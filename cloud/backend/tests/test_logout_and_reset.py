"""Logout and password reset (F9), against the real app and the PostgreSQL test database.
All emails and passwords here are made up. Reset links go to a recording mailer, never to a real inbox."""

import datetime as dt
import logging
import uuid

import pytest
from sqlalchemy import text

from app import security
from app.mailer import get_mailer
from app.security import hash_reset_token
from tests.conftest import TEST_JWT_SECRET
from tests.test_accounts import API, PASSWORD, bearer, login, register, rows, token_for

NEW_PASSWORD = "brand-new-password-5"
FORGOT = f"{API}/auth/forgot-password"
RESET = f"{API}/auth/reset-password"
LOGOUT = f"{API}/auth/logout"


class RecordingMailer:
    """Takes the place of the mock mailer in the tests and keeps what it was asked to send."""

    def __init__(self):
        self.sent = []

    def send_password_reset(self, to, link, valid_minutes):
        self.sent.append({"to": to, "link": link, "valid_minutes": valid_minutes})

    def last_token(self) -> str:
        link = self.sent[-1]["link"]
        assert "#token=" in link
        return link.split("#token=", 1)[1]


@pytest.fixture
def mailbox(client) -> RecordingMailer:
    mailer = RecordingMailer()
    client.app.dependency_overrides[get_mailer] = lambda: mailer
    return mailer


def forgot(client, email="ali@example.com"):
    return client.post(FORGOT, json={"email": email})


def reset(client, token, new_password=NEW_PASSWORD):
    return client.post(RESET, json={"token": token, "new_password": new_password})


def request_link(client, mailbox, email="ali@example.com") -> str:
    assert forgot(client, email).status_code == 202
    return mailbox.last_token()


def age_all_reset_links(engine, minutes):
    """Pretend the links were made `minutes` ago, by moving their expiry back."""
    with engine.begin() as connection:
        connection.execute(
            text("UPDATE password_reset_tokens SET expires_at = expires_at - make_interval(mins => :m)"), {"m": minutes}
        )


# ------------------------------------------------------------------ logout


def test_logout_ends_this_login(client):
    token = token_for(client, "ali@example.com")
    assert client.get(f"{API}/me", headers=bearer(token)).status_code == 200
    response = client.post(LOGOUT, headers=bearer(token))
    assert response.status_code == 204
    assert response.content == b""
    assert client.get(f"{API}/me", headers=bearer(token)).status_code == 401


def test_logout_stores_only_the_token_id_and_its_expiry(client, app_db):
    token = token_for(client, "ali@example.com")
    client.post(LOGOUT, headers=bearer(token))
    claims = security.decode_access_token(token, TEST_JWT_SECRET)
    assert rows(app_db, "SELECT jti, expires_at FROM revoked_tokens") == [(claims.jti, claims.expires_at)]


def test_logout_leaves_her_other_logins_working(client):
    token_for(client, "ali@example.com")
    laptop = login(client).json()["access_token"]
    phone = login(client).json()["access_token"]
    assert laptop != phone
    assert client.post(LOGOUT, headers=bearer(laptop)).status_code == 204
    assert client.get(f"{API}/me", headers=bearer(laptop)).status_code == 401
    assert client.get(f"{API}/me", headers=bearer(phone)).status_code == 200


def test_logout_does_not_touch_another_users_login(client):
    ali = token_for(client, "ali@example.com", "ali-password-1")
    sara = token_for(client, "sara@example.com", "sara-password-2")
    client.post(LOGOUT, headers=bearer(ali))
    assert client.get(f"{API}/me", headers=bearer(sara)).status_code == 200


def test_after_logout_a_new_login_works(client):
    token = token_for(client, "ali@example.com")
    client.post(LOGOUT, headers=bearer(token))
    fresh = login(client)
    assert fresh.status_code == 200
    assert client.get(f"{API}/me", headers=bearer(fresh.json()["access_token"])).status_code == 200


def test_logging_out_twice_is_refused_the_second_time(client):
    token = token_for(client, "ali@example.com")
    assert client.post(LOGOUT, headers=bearer(token)).status_code == 204
    assert client.post(LOGOUT, headers=bearer(token)).status_code == 401


def test_logout_needs_a_login(client):
    assert client.post(LOGOUT).status_code == 401
    assert client.post(LOGOUT, headers=bearer("not.a.token")).status_code == 401


def test_logout_is_refused_when_the_server_has_no_secret(client_without_secret):
    assert client_without_secret.post(LOGOUT, headers=bearer("anything")).status_code == 503


def test_logout_clears_blacklist_rows_of_tokens_that_have_expired_anyway(client, app_db):
    with app_db.begin() as connection:
        connection.execute(
            text("INSERT INTO revoked_tokens (jti, expires_at) VALUES (:j, now() - interval '1 day')"),
            {"j": uuid.uuid4()},
        )
    token = token_for(client, "ali@example.com")
    client.post(LOGOUT, headers=bearer(token))
    remaining = rows(app_db, "SELECT count(*) FROM revoked_tokens")[0][0]
    assert remaining == 1  # only the token that was just logged out


# --------------------------------------------------- asking for a reset link


def test_forgot_password_sends_one_link_to_that_account(client, mailbox):
    register(client)
    response = forgot(client)
    assert response.status_code == 202
    assert len(mailbox.sent) == 1
    assert mailbox.sent[0]["to"] == "ali@example.com"
    assert mailbox.sent[0]["valid_minutes"] == 30


def test_the_link_points_at_the_dashboard_and_keeps_the_token_after_the_hash_sign(client, mailbox):
    register(client)
    forgot(client)
    link = mailbox.sent[0]["link"]
    assert link.startswith("http://localhost:5173/reset-password#token=")
    assert "?" not in link  # nothing a server would receive or log


def test_a_known_and_an_unknown_email_get_exactly_the_same_answer(client, mailbox):
    register(client)
    known = forgot(client, "ali@example.com")
    unknown = forgot(client, "nobody@example.com")
    assert known.status_code == unknown.status_code == 202
    assert known.json() == unknown.json()
    assert len(mailbox.sent) == 1  # nothing was sent for the unknown email


def test_an_unknown_email_leaves_nothing_in_the_database(client, app_db, mailbox):
    forgot(client, "nobody@example.com")
    assert rows(app_db, "SELECT count(*) FROM password_reset_tokens")[0][0] == 0


def test_forgot_password_does_not_care_about_letter_case_or_spaces(client, mailbox):
    register(client, "ali@example.com")
    assert forgot(client, "  ALI@Example.com ").status_code == 202
    assert len(mailbox.sent) == 1


@pytest.mark.parametrize("bad", ["", "not-an-email", "a@"])
def test_forgot_password_refuses_a_bad_email(client, mailbox, bad):
    assert forgot(client, bad).status_code == 422
    assert mailbox.sent == []


def test_the_database_holds_only_a_hash_of_the_token(client, app_db, mailbox):
    register(client)
    token = request_link(client, mailbox)
    stored = rows(app_db, "SELECT token_hash FROM password_reset_tokens")
    assert stored == [(hash_reset_token(token),)]
    dump = str(rows(app_db, "SELECT * FROM password_reset_tokens"))
    assert token not in dump


def test_the_link_expires_after_the_set_number_of_minutes(client, app_db, mailbox):
    register(client)
    request_link(client, mailbox)
    (expires, created) = rows(app_db, "SELECT expires_at, created_at FROM password_reset_tokens")[0]
    assert dt.timedelta(minutes=29) < expires - created <= dt.timedelta(minutes=30, seconds=5)


def test_two_requests_make_two_different_tokens(client, mailbox):
    register(client)
    first = request_link(client, mailbox)
    second = request_link(client, mailbox)
    assert first != second


def test_a_newer_link_replaces_the_older_one(client, mailbox):
    register(client)
    old = request_link(client, mailbox)
    new = request_link(client, mailbox)
    assert reset(client, old).status_code == 400
    assert reset(client, new).status_code == 204


# ----------------------------------------------------- using a reset link


def test_reset_sets_the_new_password_and_the_old_one_stops_working(client, mailbox):
    register(client)
    token = request_link(client, mailbox)
    response = reset(client, token)
    assert response.status_code == 204
    assert response.content == b""
    assert login(client, password=PASSWORD).status_code == 401
    assert login(client, password=NEW_PASSWORD).status_code == 200


def test_the_new_password_is_stored_only_as_an_argon2_hash(client, app_db, mailbox):
    register(client)
    reset(client, request_link(client, mailbox))
    (stored,) = rows(app_db, "SELECT password_hash FROM users")[0]
    assert stored.startswith("$argon2id$")
    assert NEW_PASSWORD not in stored
    assert security.verify_password(stored, NEW_PASSWORD)


def test_reset_ends_every_login_made_before_it(client, mailbox):
    register(client)
    before = login(client).json()["access_token"]
    assert client.get(f"{API}/me", headers=bearer(before)).status_code == 200
    reset(client, request_link(client, mailbox))
    assert client.get(f"{API}/me", headers=bearer(before)).status_code == 401
    after = login(client, password=NEW_PASSWORD).json()["access_token"]
    assert client.get(f"{API}/me", headers=bearer(after)).status_code == 200


def test_an_expired_link_is_refused_and_the_password_is_unchanged(client, app_db, mailbox):
    register(client)
    token = request_link(client, mailbox)
    age_all_reset_links(app_db, minutes=31)
    response = reset(client, token)
    assert response.status_code == 400
    assert "expired" in response.json()["detail"]
    assert login(client, password=PASSWORD).status_code == 200
    assert login(client, password=NEW_PASSWORD).status_code == 401


def test_a_link_is_still_good_just_before_it_expires(client, app_db, mailbox):
    register(client)
    token = request_link(client, mailbox)
    age_all_reset_links(app_db, minutes=29)
    assert reset(client, token).status_code == 204


def test_a_used_link_can_not_be_used_again(client, mailbox):
    register(client)
    token = request_link(client, mailbox)
    assert reset(client, token).status_code == 204
    again = reset(client, token, "attacker-password-9")
    assert again.status_code == 400
    assert login(client, password=NEW_PASSWORD).status_code == 200
    assert login(client, password="attacker-password-9").status_code == 401


def test_a_used_link_is_marked_used_in_the_database(client, app_db, mailbox):
    register(client)
    reset(client, request_link(client, mailbox))
    (used_at,) = rows(app_db, "SELECT used_at FROM password_reset_tokens")[0]
    assert used_at is not None


def test_a_refused_reset_changes_nothing(client, app_db, mailbox):
    register(client)
    request_link(client, mailbox)
    before = rows(app_db, "SELECT password_hash, token_version FROM users")
    assert reset(client, "x" * 43).status_code == 400
    assert rows(app_db, "SELECT password_hash, token_version FROM users") == before
    assert rows(app_db, "SELECT count(*) FROM password_reset_tokens WHERE used_at IS NOT NULL")[0][0] == 0


@pytest.mark.parametrize("junk", ["a", "not-a-real-token", "x" * 200, "' OR '1'='1", " "])
def test_a_made_up_token_is_refused(client, mailbox, junk):
    register(client)
    request_link(client, mailbox)
    assert reset(client, junk).status_code == 400


def test_a_token_that_is_too_long_or_missing_is_a_validation_error(client):
    assert reset(client, "x" * 201).status_code == 422
    assert reset(client, "").status_code == 422
    assert client.post(RESET, json={"new_password": NEW_PASSWORD}).status_code == 422
    assert client.post(RESET, json={"token": "abc"}).status_code == 422


def test_a_too_short_new_password_is_refused_and_does_not_use_up_the_link(client, mailbox):
    register(client)
    token = request_link(client, mailbox)
    response = reset(client, token, "short-7")
    assert response.status_code == 422
    assert "short-7" not in response.text
    assert reset(client, token).status_code == 204  # the link still works with a good password


def test_a_very_long_new_password_is_refused(client, mailbox):
    register(client)
    token = request_link(client, mailbox)
    assert reset(client, token, "p" * 129).status_code == 422
    assert reset(client, token, "p" * 128).status_code == 204


def test_reset_refuses_extra_fields(client, mailbox):
    register(client)
    token = request_link(client, mailbox)
    response = client.post(RESET, json={"token": token, "new_password": NEW_PASSWORD, "email": "sara@example.com"})
    assert response.status_code == 422


def test_all_bad_links_get_the_same_answer(client, app_db, mailbox):
    """Expired, used and invented tokens can not be told apart from outside."""
    register(client)
    expired = request_link(client, mailbox)
    age_all_reset_links(app_db, minutes=31)
    used = request_link(client, mailbox)
    reset(client, used)
    answers = {reset(client, t).json()["detail"] for t in (expired, used, "x" * 43)}
    assert len(answers) == 1


def test_a_reset_for_one_user_never_changes_another_users_password(client, mailbox):
    register(client, "ali@example.com", "ali-password-1")
    register(client, "sara@example.com", "sara-password-2")
    token = request_link(client, mailbox, "ali@example.com")
    assert reset(client, token).status_code == 204
    assert login(client, "sara@example.com", "sara-password-2").status_code == 200
    assert login(client, "ali@example.com", NEW_PASSWORD).status_code == 200


def test_a_reset_for_one_user_does_not_end_another_users_login(client, mailbox):
    register(client, "ali@example.com", "ali-password-1")
    sara = token_for(client, "sara@example.com", "sara-password-2")
    reset(client, request_link(client, mailbox, "ali@example.com"))
    assert client.get(f"{API}/me", headers=bearer(sara)).status_code == 200


def test_resetting_one_user_leaves_the_other_users_links_alone(client, mailbox):
    register(client, "ali@example.com", "ali-password-1")
    register(client, "sara@example.com", "sara-password-2")
    ali_link = request_link(client, mailbox, "ali@example.com")
    sara_link = request_link(client, mailbox, "sara@example.com")
    assert reset(client, ali_link).status_code == 204
    assert reset(client, sara_link).status_code == 204


def test_a_reset_after_a_reset_works_with_a_fresh_link(client, mailbox):
    register(client)
    reset(client, request_link(client, mailbox))
    assert reset(client, request_link(client, mailbox), "third-password-77").status_code == 204
    assert login(client, password="third-password-77").status_code == 200


# --------------------------------------- the mock mailer, as the server runs it


def test_the_mock_mailer_prints_a_link_that_really_resets_the_password(client, caplog):
    """No mailbox override here: this is what you see in the uvicorn window."""
    register(client)
    with caplog.at_level(logging.INFO, logger="dla.mailer"):
        assert forgot(client).status_code == 202
    messages = [r.getMessage() for r in caplog.records if r.name == "dla.mailer"]
    assert len(messages) == 1
    assert "MOCK MAILER" in messages[0] and "no email is sent" in messages[0]
    assert "ali@example.com" in messages[0]
    token = messages[0].split("#token=", 1)[1].split()[0]
    assert reset(client, token).status_code == 204
    assert login(client, password=NEW_PASSWORD).status_code == 200


def test_the_mock_mailer_logs_nothing_for_an_unknown_email(client, caplog):
    with caplog.at_level(logging.INFO, logger="dla.mailer"):
        forgot(client, "nobody@example.com")
    assert [r for r in caplog.records if r.name == "dla.mailer"] == []


def test_a_failed_reset_does_not_log_the_password(client, caplog):
    register(client)
    with caplog.at_level(logging.DEBUG):
        reset(client, "x" * 43, "secret-new-pass-11")
    assert "secret-new-pass-11" not in caplog.text
