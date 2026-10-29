"""Register, login and /me, against the real app and the PostgreSQL test database.
All emails and passwords here are made up."""

import datetime as dt
import uuid

import jwt
import pytest
from sqlalchemy import text

from app import security
from tests.conftest import TEST_JWT_SECRET

PASSWORD = "test-password-1"
API = "/api/v1"


def register(client, email="ali@example.com", password=PASSWORD, **extra):
    return client.post(f"{API}/auth/register", json={"email": email, "password": password, **extra})


def login(client, email="ali@example.com", password=PASSWORD):
    return client.post(f"{API}/auth/login", json={"email": email, "password": password})


def token_for(client, email, password=PASSWORD) -> str:
    assert register(client, email, password).status_code == 201
    response = login(client, email, password)
    assert response.status_code == 200
    return response.json()["access_token"]


def bearer(token: str) -> dict:
    return {"Authorization": f"Bearer {token}"}


def rows(engine, sql, **params):
    with engine.connect() as connection:
        return connection.execute(text(sql), params).all()


# ---------------------------------------------------------------- register


def test_register_makes_an_account_on_the_free_plan(client, app_db):
    response = register(client)
    assert response.status_code == 201
    body = response.json()
    assert body["email"] == "ali@example.com"
    assert body["plan"] == "free"
    uuid.UUID(body["id"])
    assert rows(app_db, "SELECT count(*) FROM users")[0][0] == 1


def test_register_answer_has_no_password_and_no_hash(client):
    body = register(client).json()
    assert set(body) == {"id", "email", "plan", "created_at"}
    assert PASSWORD not in str(body)


def test_the_password_is_stored_only_as_an_argon2_hash(client, app_db):
    register(client)
    (stored,) = rows(app_db, "SELECT password_hash FROM users")[0]
    assert stored.startswith("$argon2id$")
    assert PASSWORD not in stored
    assert security.verify_password(stored, PASSWORD)


def test_two_users_with_the_same_password_get_different_hashes(client, app_db):
    register(client, "one@example.com")
    register(client, "two@example.com")
    hashes = {h for (h,) in rows(app_db, "SELECT password_hash FROM users")}
    assert len(hashes) == 2


def test_email_is_stored_lowercase_and_without_spaces(client, app_db):
    response = register(client, "  Ali.Khan@Example.COM ")
    assert response.status_code == 201
    assert response.json()["email"] == "ali.khan@example.com"
    assert rows(app_db, "SELECT email FROM users") == [("ali.khan@example.com",)]


def test_duplicate_email_is_refused(client, app_db):
    assert register(client).status_code == 201
    second = register(client, password="another-password-2")
    assert second.status_code == 409
    assert second.json() == {"detail": "An account with this email already exists"}
    assert rows(app_db, "SELECT count(*) FROM users")[0][0] == 1


def test_duplicate_email_is_refused_whatever_the_letter_case(client, app_db):
    assert register(client, "ali@example.com").status_code == 201
    assert register(client, "ALI@Example.com").status_code == 409
    assert rows(app_db, "SELECT count(*) FROM users")[0][0] == 1


def test_the_original_password_still_works_after_a_refused_duplicate(client):
    register(client)
    register(client, password="attacker-password-9")
    assert login(client).status_code == 200
    assert login(client, password="attacker-password-9").status_code == 401


@pytest.mark.parametrize("bad", ["not-an-email", "a@", "@example.com", "", "a b@example.com", "x" * 250 + "@example.com"])
def test_bad_emails_are_refused(client, app_db, bad):
    assert register(client, bad).status_code == 422
    assert rows(app_db, "SELECT count(*) FROM users")[0][0] == 0


def test_short_password_is_refused_and_eight_characters_are_enough(client):
    assert register(client, "short@example.com", "1234567").status_code == 422
    assert register(client, "ok@example.com", "12345678").status_code == 201


def test_very_long_password_is_refused(client):
    assert register(client, "long@example.com", "p" * 129).status_code == 422
    assert register(client, "edge@example.com", "p" * 128).status_code == 201


def test_missing_fields_are_refused(client):
    assert client.post(f"{API}/auth/register", json={}).status_code == 422
    assert client.post(f"{API}/auth/register", json={"email": "a@example.com"}).status_code == 422
    assert client.post(f"{API}/auth/register", json={"password": PASSWORD}).status_code == 422


def test_nobody_can_choose_the_pro_plan_when_registering(client, app_db):
    response = register(client, plan="pro")
    assert response.status_code == 422
    assert rows(app_db, "SELECT count(*) FROM users")[0][0] == 0


def test_a_refused_request_does_not_send_the_password_back(client):
    response = register(client, "ali@example.com", "short-7")  # 7 characters: one too few
    assert response.status_code == 422
    assert "short-7" not in response.text
    response = register(client, "not-an-email", "private-pass-77")
    assert response.status_code == 422
    assert "private-pass-77" not in response.text


# ------------------------------------------------------------------ login


def test_login_with_the_right_password_gives_a_token_for_that_user(client):
    user = register(client).json()
    response = login(client)
    assert response.status_code == 200
    body = response.json()
    assert body["token_type"] == "bearer"
    assert body["expires_in"] == 480 * 60
    claims = jwt.decode(body["access_token"], TEST_JWT_SECRET, algorithms=["HS256"])
    assert claims["sub"] == user["id"]
    remaining = claims["exp"] - dt.datetime.now(dt.timezone.utc).timestamp()
    assert 479 * 60 < remaining <= 480 * 60


@pytest.mark.parametrize(
    "attempt", ["wrong-password-0", "TEST-PASSWORD-1", "Test-password-1", "test-password-1 ", " test-password-1", "test-password-"]
)
def test_wrong_password_is_refused(client, attempt):
    register(client)
    response = login(client, password=attempt)
    assert response.status_code == 401
    assert "access_token" not in response.text
    assert response.headers["www-authenticate"] == "Bearer"


def test_unknown_email_gets_exactly_the_same_answer_as_a_wrong_password(client):
    register(client)
    wrong_password = login(client, password="wrong-password-0")
    unknown_email = login(client, email="nobody@example.com")
    assert wrong_password.status_code == unknown_email.status_code == 401
    assert wrong_password.json() == unknown_email.json() == {"detail": "Incorrect email or password"}


def test_unknown_email_still_costs_a_password_check(client, monkeypatch):
    calls = []
    monkeypatch.setattr("app.routes.accounts.verify_password_of_nobody", lambda password: calls.append(password))
    login(client, email="nobody@example.com")
    assert calls == [PASSWORD]


def test_login_does_not_care_about_email_letter_case_or_spaces(client):
    register(client, "ali@example.com")
    assert login(client, "ALI@Example.com").status_code == 200
    assert login(client, "  ali@example.com ").status_code == 200


def test_login_without_a_password_is_refused(client):
    register(client)
    assert client.post(f"{API}/auth/login", json={"email": "ali@example.com"}).status_code == 422
    assert login(client, password="").status_code == 422


def test_login_one_users_password_does_not_open_another_account(client):
    register(client, "ali@example.com", "ali-password-1")
    register(client, "sara@example.com", "sara-password-2")
    assert login(client, "ali@example.com", "sara-password-2").status_code == 401
    assert login(client, "sara@example.com", "ali-password-1").status_code == 401


def test_login_is_refused_when_the_server_has_no_secret(client_without_secret):
    register(client_without_secret)
    response = login(client_without_secret)
    assert response.status_code == 503
    assert "access_token" not in response.text


def test_a_short_secret_is_not_accepted(app_db):
    from tests.conftest import build_client

    weak = build_client(app_db, "too-short")
    register(weak)
    assert login(weak).status_code == 503


# --------------------------------------------------------------------- /me


def test_me_shows_the_account_of_the_token(client):
    token = token_for(client, "ali@example.com")
    response = client.get(f"{API}/me", headers=bearer(token))
    assert response.status_code == 200
    body = response.json()
    assert body["email"] == "ali@example.com"
    assert body["plan"] == "free"
    assert set(body) == {"id", "email", "plan", "created_at"}


def test_me_needs_a_token(client):
    response = client.get(f"{API}/me")
    assert response.status_code == 401
    assert response.headers["www-authenticate"] == "Bearer"


@pytest.mark.parametrize("header", ["Bearer", "Bearer ", "Bearer not.a.token", "Basic YWxpOnBhc3M=", "token-without-scheme"])
def test_me_refuses_a_bad_authorization_header(client, header):
    register(client)
    assert client.get(f"{API}/me", headers={"Authorization": header}).status_code == 401


def test_me_refuses_a_token_signed_with_another_secret(client):
    user = register(client).json()
    forged = security.create_access_token(uuid.UUID(user["id"]), "attacker-secret-" + "q" * 40, minutes=60)
    assert client.get(f"{API}/me", headers=bearer(forged)).status_code == 401


def test_me_refuses_an_expired_token(client):
    user = register(client).json()
    long_ago = dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=3)
    expired = security.create_access_token(uuid.UUID(user["id"]), TEST_JWT_SECRET, minutes=60, now=long_ago)
    assert client.get(f"{API}/me", headers=bearer(expired)).status_code == 401


def test_me_refuses_a_token_that_says_it_needs_no_signature(client):
    user = register(client).json()
    claims = {"sub": user["id"], "exp": dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=1)}
    unsigned = jwt.encode(claims, None, algorithm="none")
    assert client.get(f"{API}/me", headers=bearer(unsigned)).status_code == 401


def test_me_refuses_a_token_of_a_deleted_account(client, app_db):
    token = token_for(client, "ali@example.com")
    assert client.get(f"{API}/me", headers=bearer(token)).status_code == 200
    with app_db.begin() as connection:
        connection.execute(text("DELETE FROM users"))
    assert client.get(f"{API}/me", headers=bearer(token)).status_code == 401


def test_me_is_refused_when_the_server_has_no_secret(client_without_secret):
    register(client_without_secret)
    assert client_without_secret.get(f"{API}/me", headers=bearer("anything")).status_code == 503


# --------------------------------------------------- one user and another


def test_each_user_sees_only_her_own_account(client):
    ali_token = token_for(client, "ali@example.com", "ali-password-1")
    sara_token = token_for(client, "sara@example.com", "sara-password-2")

    ali = client.get(f"{API}/me", headers=bearer(ali_token))
    sara = client.get(f"{API}/me", headers=bearer(sara_token))

    assert ali.json()["email"] == "ali@example.com"
    assert sara.json()["email"] == "sara@example.com"
    assert ali.json()["id"] != sara.json()["id"]
    assert "sara" not in ali.text and "ali@" not in sara.text


def test_a_token_made_for_one_user_never_shows_the_other(client):
    ali_token = token_for(client, "ali@example.com", "ali-password-1")
    sara = register(client, "sara@example.com", "sara-password-2").json()
    shown = client.get(f"{API}/me", headers=bearer(ali_token)).json()
    assert shown["id"] != sara["id"]
    assert shown["email"] != sara["email"]


def test_a_token_forged_for_another_users_id_does_not_work(client):
    token_for(client, "ali@example.com", "ali-password-1")
    sara = register(client, "sara@example.com", "sara-password-2").json()
    forged = security.create_access_token(uuid.UUID(sara["id"]), "attacker-secret-" + "q" * 40, minutes=60)
    assert client.get(f"{API}/me", headers=bearer(forged)).status_code == 401


def test_logging_in_as_one_user_does_not_change_what_another_token_shows(client):
    ali_token = token_for(client, "ali@example.com", "ali-password-1")
    token_for(client, "sara@example.com", "sara-password-2")
    login(client, "sara@example.com", "sara-password-2")
    assert client.get(f"{API}/me", headers=bearer(ali_token)).json()["email"] == "ali@example.com"
