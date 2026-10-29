from fastapi import APIRouter, HTTPException
from sqlalchemy import text
from sqlalchemy.exc import SQLAlchemyError

from ..db import get_engine

router = APIRouter(tags=["health"])


@router.get("/health")
def health() -> dict[str, str]:
    """The API is running. Does not touch the database."""
    return {"status": "ok"}


@router.get("/health/db")
def health_db() -> dict[str, str]:
    """The database answers. The error is kept short on purpose: no addresses, no passwords."""
    try:
        with get_engine().connect() as connection:
            connection.execute(text("select 1"))
    except (SQLAlchemyError, RuntimeError):
        raise HTTPException(status_code=503, detail="database unavailable") from None
    return {"database": "ok"}
