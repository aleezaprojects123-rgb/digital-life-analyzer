"""Daily summaries (Contract 2): upload one, and read back your own.

Every query here asks for `user.id` taken from the verified login, never from the request, so one user can
not read or replace another user's summary. The summary is stored encrypted (app/crypto.py).
"""

import datetime as dt
import json
import logging
from typing import Literal

from fastapi import APIRouter, Depends, HTTPException, Path, Request
from pydantic import BaseModel
from sqlalchemy import func, literal_column
from sqlalchemy.dialects.postgresql import insert as pg_insert
from sqlalchemy.orm import Session

from ..auth import current_user
from ..config import Settings, get_settings
from ..crypto import DecryptionError, EncryptionNotConfigured, decrypt_summary, encrypt_summary, load_key
from ..db import get_session
from ..models import DailySummary, User
from ..summary_contract import validate_summary

log = logging.getLogger("dla.summaries")
router = APIRouter(tags=["summaries"])

MAX_SUMMARY_BYTES = 100_000  # a real summary is a few thousand bytes; this only stops huge uploads
MAX_PROBLEMS_SHOWN = 20
DAY_PATTERN = r"^\d{4}-\d{2}-\d{2}$"


class SummaryReceipt(BaseModel):
    date: str
    # created: first summary for this day. replaced: it was newer than the stored one.
    # ignored: the stored one is as new or newer, and was kept (an upload can be repeated safely).
    result: Literal["created", "replaced", "ignored"]
    generated_at: str  # of the summary that is stored now


def encryption_key(settings: Settings) -> bytes:
    try:
        return load_key(settings.encryption_key)
    except EncryptionNotConfigured:
        raise HTTPException(status_code=503, detail="storage encryption is not configured") from None


def parse_day(day: str = Path(pattern=DAY_PATTERN, description="YYYY-MM-DD")) -> dt.date:
    try:
        return dt.date.fromisoformat(day)
    except ValueError:
        raise HTTPException(status_code=422, detail=f"{day} is not a real calendar date") from None


async def summary_body(
    request: Request, user: User = Depends(current_user), settings: Settings = Depends(get_settings)
) -> object:
    """The uploaded JSON. Runs after the login check, so only a logged-in user makes the server read a body."""
    encryption_key(settings)  # refuse before reading anything if nothing could be stored
    declared = request.headers.get("content-length", "")
    if declared.isdigit() and int(declared) > MAX_SUMMARY_BYTES:
        raise HTTPException(status_code=413, detail=f"The summary is larger than {MAX_SUMMARY_BYTES} bytes")
    chunks, size = [], 0
    async for chunk in request.stream():
        size += len(chunk)
        if size > MAX_SUMMARY_BYTES:
            raise HTTPException(status_code=413, detail=f"The summary is larger than {MAX_SUMMARY_BYTES} bytes")
        chunks.append(chunk)
    try:
        return json.loads(b"".join(chunks))
    except ValueError:  # includes bad UTF-8
        raise HTTPException(status_code=400, detail="The request body is not valid JSON") from None


def parse_utc(value: str) -> dt.datetime:
    return dt.datetime.fromisoformat(value).astimezone(dt.timezone.utc)


def address_problems(summary: dict, day: dt.date, now: dt.datetime) -> list[str]:
    """Checks that tie the summary to the address and the clock. (The first one is in the contract; the two
    time checks are extra sanity checks of this server.)"""
    problems = []
    if summary["date"] != day.isoformat():
        problems.append(f"$.date: the body says {summary['date']} but the address says {day.isoformat()}")
    if day > now.date() + dt.timedelta(days=1):
        problems.append("$.date: is in the future")
    try:
        generated_at = parse_utc(summary["generated_at"])
    except ValueError:  # the schema checks the look of the text, not whether the time exists (for example 31 Feb)
        problems.append("$.generated_at: is not a real time")
    else:
        # A far-future time would make this summary impossible to replace by a later, correct one.
        if generated_at > now + dt.timedelta(days=1):
            problems.append("$.generated_at: is in the future")
    return problems


@router.put("/summaries/{day}", response_model=SummaryReceipt)
def put_summary(
    day: dt.date = Depends(parse_day),
    body: object = Depends(summary_body),
    user: User = Depends(current_user),
    settings: Settings = Depends(get_settings),
    session: Session = Depends(get_session),
) -> SummaryReceipt:
    """Stores the caller's summary for `day`. A newer upload for the same day replaces the old one; an upload
    that is not newer is ignored, so sending the same summary twice changes nothing."""
    key = encryption_key(settings)
    problems = validate_summary(body)
    if not problems:
        problems = address_problems(body, day, dt.datetime.now(dt.timezone.utc))
    if problems:
        # Nothing is stored. The messages name fields; they come from the summary the caller sent.
        raise HTTPException(
            status_code=422,
            detail={
                "message": "The summary does not follow Contract 2 and was not stored",
                "problems": problems[:MAX_PROBLEMS_SHOWN],
                "problems_not_shown": max(0, len(problems) - MAX_PROBLEMS_SHOWN),
            },
        )

    generated_at = parse_utc(body["generated_at"])
    stmt = pg_insert(DailySummary).values(
        user_id=user.id,
        summary_date=day,
        schema_version=body["schema_version"],
        generated_at=generated_at,
        payload_encrypted=encrypt_summary(key, user.id, day, body),
    )
    stmt = stmt.on_conflict_do_update(
        index_elements=[DailySummary.user_id, DailySummary.summary_date],
        set_={
            "schema_version": stmt.excluded.schema_version,
            "generated_at": stmt.excluded.generated_at,
            "received_at": func.now(),
            "payload_encrypted": stmt.excluded.payload_encrypted,
        },
        where=DailySummary.generated_at < stmt.excluded.generated_at,  # one statement, so two uploads can not race
    ).returning(literal_column("(xmax = 0)").label("inserted"))  # xmax is 0 for a new row, not for an updated one
    row = session.execute(stmt).first()
    session.commit()

    if row is None:
        kept = session.get(DailySummary, (user.id, day))
        return SummaryReceipt(date=day.isoformat(), result="ignored", generated_at=_z(kept.generated_at))
    return SummaryReceipt(
        date=day.isoformat(), result="created" if row.inserted else "replaced", generated_at=_z(generated_at)
    )


@router.get("/summaries/{day}")
def get_summary(
    day: dt.date = Depends(parse_day),
    user: User = Depends(current_user),
    settings: Settings = Depends(get_settings),
    session: Session = Depends(get_session),
) -> dict:
    """The caller's own summary for `day`, decrypted. 404 if she has none for that day."""
    key = encryption_key(settings)
    row = session.get(DailySummary, (user.id, day))
    if row is None:
        raise HTTPException(status_code=404, detail="No summary is stored for this date")
    try:
        return decrypt_summary(key, user.id, day, row.payload_encrypted)
    except DecryptionError:
        log.error("A stored summary could not be decrypted (user %s, date %s)", user.id, day)
        raise HTTPException(status_code=500, detail="The stored summary could not be read") from None


def _z(moment: dt.datetime) -> str:
    return moment.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
