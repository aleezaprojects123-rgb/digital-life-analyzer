"""Uploading and reading daily summaries (Contract 2), against the real app and the PostgreSQL test database.
All data here is made up: the contract's own sample, the mock file and small changes of them."""

import copy
import datetime as dt
import json
import uuid

import pytest
from sqlalchemy import text

from app import crypto, security
from app.config import REPO_ROOT
from app.routes.summaries import MAX_PROBLEMS_SHOWN, MAX_SUMMARY_BYTES
from tests.conftest import TEST_ENCRYPTION_KEY, TEST_JWT_SECRET, build_client
from tests.test_accounts import API, bearer, login, rows, token_for

SAMPLES = REPO_ROOT / "contracts" / "samples"
BASE = json.loads((SAMPLES / "summary-valid.json").read_text(encoding="utf-8"))
MOCK = json.loads((REPO_ROOT / "cloud" / "mock" / "summary-2026-10-28.json").read_text(encoding="utf-8"))
DAY = BASE["date"]  # 2026-10-27
KEY = crypto.load_key(TEST_ENCRYPTION_KEY)


def make(generated_at=None, app_name=None, day=None) -> dict:
    """A valid summary: the contract's sample, with small changes that keep every number consistent."""
    summary = copy.deepcopy(BASE)
    if generated_at:
        summary["generated_at"] = generated_at
    if app_name:
        summary["top_apps"][-1]["name"] = app_name  # a name, so no number changes
    if day:
        summary["date"] = day
    return summary


def put(client, token, day, body):
    return client.put(f"{API}/summaries/{day}", json=body, headers=bearer(token))


def get(client, token, day):
    return client.get(f"{API}/summaries/{day}", headers=bearer(token))


def count(engine) -> int:
    return rows(engine, "SELECT count(*) FROM daily_summaries")[0][0]


def blob_of(engine, email="ali@example.com"):
    sql = "SELECT s.payload_encrypted FROM daily_summaries s JOIN users u ON u.id = s.user_id WHERE u.email = :e"
    return bytes(rows(engine, sql, e=email)[0][0])


@pytest.fixture
def ali(client) -> str:
    return token_for(client, "ali@example.com", "ali-password-1")


@pytest.fixture
def sara(client) -> str:
    return token_for(client, "sara@example.com", "sara-password-2")


# ------------------------------------------------------------ storing a summary


def test_a_first_upload_is_created(client, ali, app_db):
    response = put(client, ali, DAY, make())
    assert response.status_code == 200
    assert response.json() == {"date": DAY, "result": "created", "generated_at": BASE["generated_at"]}
    assert count(app_db) == 1


def test_the_contracts_own_sample_is_accepted(client, ali):
    assert put(client, ali, BASE["date"], BASE).json()["result"] == "created"


def test_the_mock_summary_is_accepted(client, ali):
    assert put(client, ali, MOCK["date"], MOCK).json()["result"] == "created"


def test_the_stored_row_has_the_user_the_date_and_the_version(client, ali, app_db):
    put(client, ali, DAY, make())
    (row,) = rows(app_db, "SELECT u.email, s.summary_date, s.schema_version FROM daily_summaries s JOIN users u ON u.id = s.user_id")
    assert row == ("ali@example.com", dt.date.fromisoformat(DAY), "1")


def test_reading_it_back_gives_exactly_what_was_sent(client, ali):
    sent = make()
    put(client, ali, DAY, sent)
    response = get(client, ali, DAY)
    assert response.status_code == 200
    assert response.json() == sent


def test_reading_a_day_with_no_summary_is_a_404(client, ali):
    response = get(client, ali, "2026-10-01")
    assert response.status_code == 404
    assert response.json() == {"detail": "No summary is stored for this date"}


def test_one_user_can_keep_several_days(client, ali, app_db):
    put(client, ali, "2026-10-27", make(day="2026-10-27"))
    put(client, ali, "2026-10-26", make(day="2026-10-26"))
    assert count(app_db) == 2
    assert get(client, ali, "2026-10-26").json()["date"] == "2026-10-26"


# ------------------------------------- the same user and date: replace, not add


def test_sending_the_same_summary_twice_changes_nothing(client, ali, app_db):
    first = put(client, ali, DAY, make())
    stored = blob_of(app_db)
    second = put(client, ali, DAY, make())
    assert first.json()["result"] == "created"
    assert second.status_code == 200
    assert second.json()["result"] == "ignored"
    assert count(app_db) == 1
    assert blob_of(app_db) == stored  # not even re-encrypted
    assert get(client, ali, DAY).json() == make()


def test_a_newer_upload_replaces_the_old_one(client, ali, app_db):
    put(client, ali, DAY, make())
    newer = make(generated_at="2026-10-27T23:00:00Z", app_name="firefox.exe")
    response = put(client, ali, DAY, newer)
    assert response.json() == {"date": DAY, "result": "replaced", "generated_at": "2026-10-27T23:00:00Z"}
    assert count(app_db) == 1
    assert get(client, ali, DAY).json() == newer
    assert get(client, ali, DAY).json() != make()


def test_replacing_updates_received_at(client, ali, app_db):
    put(client, ali, DAY, make())
    (first,) = rows(app_db, "SELECT received_at FROM daily_summaries")[0]
    put(client, ali, DAY, make(generated_at="2026-10-27T23:00:00Z"))
    (second,) = rows(app_db, "SELECT received_at FROM daily_summaries")[0]
    assert second > first


def test_an_older_upload_is_ignored_and_the_newer_one_stays(client, ali, app_db):
    newer = make(generated_at="2026-10-27T23:00:00Z", app_name="firefox.exe")
    put(client, ali, DAY, newer)
    response = put(client, ali, DAY, make(generated_at="2026-10-27T10:00:00Z"))
    assert response.status_code == 200
    assert response.json() == {"date": DAY, "result": "ignored", "generated_at": "2026-10-27T23:00:00Z"}
    assert get(client, ali, DAY).json() == newer
    assert count(app_db) == 1


def test_an_upload_with_the_same_time_but_other_content_is_ignored(client, ali):
    put(client, ali, DAY, make())
    response = put(client, ali, DAY, make(app_name="firefox.exe"))
    assert response.json()["result"] == "ignored"
    assert get(client, ali, DAY).json() == make()


def test_each_replacement_is_encrypted_with_a_new_nonce(client, ali, app_db):
    put(client, ali, DAY, make())
    first = blob_of(app_db)
    put(client, ali, DAY, make(generated_at="2026-10-27T23:00:00Z"))
    second = blob_of(app_db)
    assert first[1:13] != second[1:13]


# ------------------------------------------------------------ encrypted at rest


def test_the_stored_bytes_hold_no_readable_activity(client, ali, app_db):
    put(client, ali, DAY, make())
    stored = blob_of(app_db)
    for needle in (b"youtube", b"instagram", b"Entertainment", b"top_sites", b"msedge", b"presence", b'"seconds"'):
        assert needle not in stored
    with pytest.raises(ValueError):
        json.loads(stored)


def test_no_column_of_the_row_holds_activity_as_text(client, ali, app_db):
    put(client, ali, DAY, make())
    row_text = str(rows(app_db, "SELECT * FROM daily_summaries")).lower()
    for needle in ("youtube", "instagram", "netflix", "category", "domain", "top_"):
        assert needle not in row_text


def test_the_server_key_opens_the_stored_bytes_and_another_key_does_not(client, ali, app_db):
    put(client, ali, DAY, make())
    user_id = rows(app_db, "SELECT id FROM users")[0][0]
    day = dt.date.fromisoformat(DAY)
    assert crypto.decrypt_summary(KEY, user_id, day, blob_of(app_db)) == make()
    with pytest.raises(crypto.DecryptionError):
        crypto.decrypt_summary(bytes(range(1, 33)), user_id, day, blob_of(app_db))


def test_a_summary_copied_to_another_users_row_can_not_be_read_there(client, ali, sara, app_db):
    put(client, ali, DAY, make())
    put(client, sara, DAY, make(generated_at="2026-10-27T18:00:00Z"))
    with app_db.begin() as connection:  # someone with database access copies Ali's blob over Sara's
        connection.execute(
            text(
                "UPDATE daily_summaries SET payload_encrypted = "
                "(SELECT s.payload_encrypted FROM daily_summaries s JOIN users u ON u.id = s.user_id WHERE u.email = 'ali@example.com') "
                "WHERE user_id = (SELECT id FROM users WHERE email = 'sara@example.com')"
            )
        )
    response = get(client, sara, DAY)
    assert response.status_code == 500
    assert response.json() == {"detail": "The stored summary could not be read"}
    assert "youtube" not in response.text


def test_a_summary_copied_to_another_day_can_not_be_read_there(client, ali, app_db):
    put(client, ali, "2026-10-27", make(day="2026-10-27"))
    put(client, ali, "2026-10-26", make(day="2026-10-26", generated_at="2026-10-26T18:00:00Z"))
    with app_db.begin() as connection:
        connection.execute(
            text(
                "UPDATE daily_summaries SET payload_encrypted = "
                "(SELECT payload_encrypted FROM daily_summaries WHERE summary_date = '2026-10-27') "
                "WHERE summary_date = '2026-10-26'"
            )
        )
    assert get(client, ali, "2026-10-26").status_code == 500
    assert get(client, ali, "2026-10-27").status_code == 200


def test_a_damaged_stored_summary_gives_a_clear_error_and_no_data(client, ali, app_db, caplog):
    put(client, ali, DAY, make())
    with app_db.begin() as connection:
        connection.execute(text("UPDATE daily_summaries SET payload_encrypted = '\\x0102030405'::bytea"))
    with caplog.at_level("ERROR", logger="dla.summaries"):
        response = get(client, ali, DAY)
    assert response.status_code == 500
    assert response.json() == {"detail": "The stored summary could not be read"}
    assert any("could not be decrypted" in r.getMessage() for r in caplog.records)
    assert "youtube" not in caplog.text


@pytest.mark.parametrize("bad_key", ["", "not base64!", "AAAA"])
def test_without_a_good_encryption_key_nothing_is_stored_or_shown(app_db, bad_key):
    broken = build_client(app_db, TEST_JWT_SECRET, encryption_key=bad_key)
    token = token_for(broken, "ali@example.com")
    response = put(broken, token, DAY, make())
    assert response.status_code == 503
    assert response.json() == {"detail": "storage encryption is not configured"}
    assert get(broken, token, DAY).status_code == 503
    assert count(app_db) == 0


# ------------------------------------------------------- bad summaries are refused


def test_the_invalid_sample_is_refused_with_its_problems_and_nothing_is_stored(client, ali, app_db):
    bad = json.loads((SAMPLES / "summary-invalid.json").read_text(encoding="utf-8"))
    response = put(client, ali, DAY, bad)
    assert response.status_code == 422
    detail = response.json()["detail"]
    assert detail["message"] == "The summary does not follow Contract 2 and was not stored"
    assert len(detail["problems"]) == 6
    assert detail["problems_not_shown"] == 0
    assert any("window_titles" in p for p in detail["problems"])
    assert count(app_db) == 0


def test_a_summary_with_a_window_title_is_never_stored(client, ali, app_db):
    sneaky = make()
    sneaky["window_titles"] = ["Chapter 4 - Thermodynamics.pdf"]
    response = put(client, ali, DAY, sneaky)
    assert response.status_code == 422
    assert any("window_titles" in p for p in response.json()["detail"]["problems"])
    assert count(app_db) == 0


def test_numbers_that_do_not_add_up_are_refused(client, ali, app_db):
    wrong = make()
    wrong["category_seconds"]["Study"]["active"] += 1
    response = put(client, ali, DAY, wrong)
    assert response.status_code == 422
    assert any("presence_seconds.active" in p for p in response.json()["detail"]["problems"])
    assert count(app_db) == 0


def test_a_bad_upload_does_not_damage_the_stored_summary(client, ali):
    put(client, ali, DAY, make())
    wrong = make(generated_at="2026-10-27T23:00:00Z")
    wrong["presence_seconds"]["active"] = -1
    assert put(client, ali, DAY, wrong).status_code == 422
    assert get(client, ali, DAY).json() == make()


def test_the_date_in_the_body_must_equal_the_date_in_the_address(client, ali, app_db):
    response = put(client, ali, "2026-10-26", make())  # body says 2026-10-27
    assert response.status_code == 422
    assert "the body says 2026-10-27 but the address says 2026-10-26" in response.json()["detail"]["problems"][0]
    assert count(app_db) == 0


def test_a_date_in_the_future_is_refused(client, ali, app_db):
    response = put(client, ali, "2099-01-01", make(day="2099-01-01"))
    assert response.status_code == 422
    assert "$.date: is in the future" in response.json()["detail"]["problems"]
    assert count(app_db) == 0


def test_tomorrow_is_allowed_for_users_ahead_of_utc(client, ali):
    tomorrow = (dt.datetime.now(dt.timezone.utc) + dt.timedelta(days=1)).date().isoformat()
    response = put(client, ali, tomorrow, make(day=tomorrow))
    assert response.status_code == 200


def test_a_build_time_in_the_future_is_refused(client, ali, app_db):
    response = put(client, ali, DAY, make(generated_at="2099-01-01T00:00:00Z"))
    assert response.status_code == 422
    assert "$.generated_at: is in the future" in response.json()["detail"]["problems"]
    assert count(app_db) == 0


def test_a_build_time_that_does_not_exist_is_refused_not_a_server_error(client, ali, app_db):
    response = put(client, ali, DAY, make(generated_at="2026-02-31T10:00:00Z"))
    assert response.status_code == 422
    assert "$.generated_at: is not a real time" in response.json()["detail"]["problems"]
    assert count(app_db) == 0


@pytest.mark.parametrize(
    "content, status",
    [(b"", 400), (b"{not json", 400), (b"\xff\xfe\x00bad utf8", 400), (b"[]", 422), (b"null", 422), (b'"text"', 422), (b"42", 422), (b"{}", 422)],
)
def test_bodies_that_are_not_a_summary_get_a_clear_refusal(client, ali, app_db, content, status):
    response = client.put(
        f"{API}/summaries/{DAY}", content=content, headers={**bearer(ali), "Content-Type": "application/json"}
    )
    assert response.status_code == status
    assert "detail" in response.json()
    assert count(app_db) == 0


def test_the_not_json_answer_says_so(client, ali):
    response = client.put(f"{API}/summaries/{DAY}", content=b"{oops", headers=bearer(ali))
    assert response.json() == {"detail": "The request body is not valid JSON"}


def test_at_most_twenty_problems_are_listed_and_the_rest_are_counted(client, ali):
    broken = make()
    for hour in broken["hourly"]:
        hour["hour"] = "x"  # 24 mistakes
    detail = put(client, ali, DAY, broken).json()["detail"]
    assert len(detail["problems"]) == MAX_PROBLEMS_SHOWN
    assert detail["problems_not_shown"] >= 4


def test_a_body_that_is_too_large_is_refused_by_its_declared_size(client, ali, app_db):
    big = {"padding": "a" * (MAX_SUMMARY_BYTES + 10)}
    response = put(client, ali, DAY, big)
    assert response.status_code == 413
    assert count(app_db) == 0


def test_a_body_that_is_too_large_is_refused_while_streaming_too(client, ali, app_db):
    def chunks():
        for _ in range(60):
            yield b" " * 2000  # no Content-Length is sent, so the size is only known as it arrives

    response = client.put(f"{API}/summaries/{DAY}", content=chunks(), headers=bearer(ali))
    assert response.status_code == 413
    assert count(app_db) == 0


@pytest.mark.parametrize("day", ["2026-13-01", "2026-02-30", "2026-00-10"])
def test_a_date_that_does_not_exist_is_refused(client, ali, day):
    assert put(client, ali, day, make()).status_code == 422
    assert get(client, ali, day).status_code == 422


@pytest.mark.parametrize("day", ["20261027", "27-10-2026", "abc", "2026-1-5", "1700000000", "2026-10-27T00:00"])
def test_a_date_in_another_format_is_refused(client, ali, day):
    assert put(client, ali, day, make()).status_code == 422
    assert get(client, ali, day).status_code == 422


# ---------------------------------------------------------------- logged in only


def test_uploading_needs_a_login(client, app_db):
    assert client.put(f"{API}/summaries/{DAY}", json=make()).status_code == 401
    assert client.put(f"{API}/summaries/{DAY}", json=make(), headers=bearer("not.a.token")).status_code == 401
    assert count(app_db) == 0


def test_reading_needs_a_login(client, ali):
    put(client, ali, DAY, make())
    assert client.get(f"{API}/summaries/{DAY}").status_code == 401
    assert client.get(f"{API}/summaries/{DAY}", headers=bearer("not.a.token")).status_code == 401


def test_a_logged_out_token_can_not_upload_or_read(client, ali, app_db):
    client.post(f"{API}/auth/logout", headers=bearer(ali))
    assert put(client, ali, DAY, make()).status_code == 401
    assert get(client, ali, DAY).status_code == 401
    assert count(app_db) == 0


def test_a_token_from_before_a_password_reset_can_not_upload(client, app_db):
    token = token_for(client, "ali@example.com")
    with app_db.begin() as connection:
        connection.execute(text("UPDATE users SET token_version = token_version + 1"))
    assert put(client, token, DAY, make()).status_code == 401


def test_the_login_check_comes_before_the_size_check(client):
    big = {"padding": "a" * (MAX_SUMMARY_BYTES + 10)}
    assert client.put(f"{API}/summaries/{DAY}", json=big).status_code == 401


# ------------------------------------------------------- one user and another


def test_user_b_can_not_read_user_as_summary(client, ali, sara):
    put(client, ali, DAY, make())
    response = get(client, sara, DAY)
    assert response.status_code == 404
    for secret in ("youtube", "Entertainment", "msedge"):
        assert secret not in response.text


def test_user_b_uploading_the_same_date_does_not_touch_user_as_summary(client, ali, sara, app_db):
    ali_summary = make()
    put(client, ali, DAY, ali_summary)
    sara_summary = make(generated_at="2026-10-27T23:00:00Z", app_name="firefox.exe")
    assert put(client, sara, DAY, sara_summary).json()["result"] == "created"  # created for Sara, not "replaced"
    assert count(app_db) == 2
    assert get(client, ali, DAY).json() == ali_summary
    assert get(client, sara, DAY).json() == sara_summary


def test_a_newer_upload_by_user_b_does_not_replace_user_as_summary(client, ali, sara):
    put(client, ali, DAY, make())
    put(client, sara, DAY, make(generated_at="2026-10-27T23:59:00Z", app_name="firefox.exe"))
    assert get(client, ali, DAY).json() == make()
    assert get(client, ali, DAY).json()["generated_at"] == BASE["generated_at"]


def test_user_b_can_not_overwrite_user_a_by_putting_an_older_or_newer_summary_with_the_same_date(client, ali, sara, app_db):
    put(client, ali, DAY, make())
    before = rows(app_db, "SELECT user_id, generated_at, received_at, payload_encrypted FROM daily_summaries")
    put(client, sara, DAY, make(generated_at="2026-10-27T01:00:00Z"))
    put(client, sara, DAY, make(generated_at="2026-10-28T01:00:00Z"))
    ali_id = rows(app_db, "SELECT id FROM users WHERE email = 'ali@example.com'")[0][0]
    after = rows(
        app_db,
        "SELECT user_id, generated_at, received_at, payload_encrypted FROM daily_summaries WHERE user_id = :u",
        u=ali_id,
    )
    assert after == before


def test_user_b_sees_only_his_own_days(client, ali, sara):
    put(client, ali, "2026-10-27", make())
    put(client, sara, "2026-10-26", make(day="2026-10-26"))
    assert get(client, sara, "2026-10-27").status_code == 404
    assert get(client, ali, "2026-10-26").status_code == 404
    assert get(client, sara, "2026-10-26").status_code == 200


def test_a_summary_can_not_name_another_user(client, ali, sara, app_db):
    sneaky = make()
    sneaky["user_id"] = str(uuid.uuid4())
    response = put(client, ali, DAY, sneaky)
    assert response.status_code == 422
    assert count(app_db) == 0


def test_there_is_no_address_that_names_a_user(client, ali, sara, app_db):
    put(client, ali, DAY, make())
    sara_id = rows(app_db, "SELECT id FROM users WHERE email = 'sara@example.com'")[0][0]
    ali_id = rows(app_db, "SELECT id FROM users WHERE email = 'ali@example.com'")[0][0]
    for path in (f"/users/{ali_id}/summaries/{DAY}", f"/summaries/{ali_id}/{DAY}", f"/summaries/{DAY}?user_id={ali_id}"):
        response = client.get(f"{API}{path}", headers=bearer(sara))
        assert response.status_code in (404, 405) or response.json() == {"detail": "No summary is stored for this date"}
        assert "youtube" not in response.text
    assert sara_id != ali_id


def test_a_token_forged_for_user_a_can_not_read_or_write(client, ali, app_db):
    put(client, ali, DAY, make())
    ali_id = rows(app_db, "SELECT id FROM users WHERE email = 'ali@example.com'")[0][0]
    forged = security.create_access_token(ali_id, "attacker-secret-" + "q" * 40, minutes=60)
    assert get(client, forged, DAY).status_code == 401
    assert put(client, forged, DAY, make(generated_at="2026-10-27T23:00:00Z")).status_code == 401
    assert get(client, ali, DAY).json() == make()


def test_deleting_one_user_deletes_only_her_summaries(client, ali, sara, app_db):
    put(client, ali, DAY, make())
    put(client, sara, DAY, make())
    with app_db.begin() as connection:
        connection.execute(text("DELETE FROM users WHERE email = 'ali@example.com'"))
    assert count(app_db) == 1
    assert get(client, sara, DAY).json() == make()


def test_logging_in_again_does_not_change_which_summary_a_user_sees(client, ali, sara):
    put(client, ali, DAY, make())
    fresh_sara = login(client, "sara@example.com", "sara-password-2").json()["access_token"]
    assert get(client, fresh_sara, DAY).status_code == 404
    assert get(client, ali, DAY).status_code == 200
