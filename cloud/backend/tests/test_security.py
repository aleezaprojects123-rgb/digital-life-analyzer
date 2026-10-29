import datetime as dt
import uuid

import jwt
import pytest

from app import security

SECRET = "unit-test-secret-" + "y" * 40


def test_hash_is_argon2id_and_does_not_contain_the_password():
    hashed = security.hash_password("test-password-1")
    assert hashed.startswith("$argon2id$")
    assert "test-password-1" not in hashed


def test_same_password_hashes_differently_each_time():
    assert security.hash_password("test-password-1") != security.hash_password("test-password-1")


def test_right_password_verifies_and_wrong_ones_do_not():
    hashed = security.hash_password("test-password-1")
    assert security.verify_password(hashed, "test-password-1") is True
    assert security.verify_password(hashed, "test-password-2") is False
    assert security.verify_password(hashed, "Test-Password-1") is False
    assert security.verify_password(hashed, "test-password-1 ") is False
    assert security.verify_password(hashed, "") is False


@pytest.mark.parametrize("broken", ["", "not a hash", "$argon2id$garbage", "plain-text-password"])
def test_a_broken_stored_hash_never_verifies(broken):
    assert security.verify_password(broken, "test-password-1") is False


def test_token_round_trip_gives_back_the_user_id():
    user_id = uuid.uuid4()
    token = security.create_access_token(user_id, SECRET, minutes=5)
    assert security.decode_access_token(token, SECRET) == user_id


def test_token_holds_only_subject_and_times():
    token = security.create_access_token(uuid.uuid4(), SECRET, minutes=5)
    assert set(jwt.decode(token, SECRET, algorithms=["HS256"])) == {"sub", "iat", "exp"}


def test_token_expires():
    long_ago = dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=2)
    token = security.create_access_token(uuid.uuid4(), SECRET, minutes=5, now=long_ago)
    with pytest.raises(security.InvalidToken):
        security.decode_access_token(token, SECRET)


def test_token_with_another_secret_is_refused():
    token = security.create_access_token(uuid.uuid4(), "another-secret-" + "z" * 40, minutes=5)
    with pytest.raises(security.InvalidToken):
        security.decode_access_token(token, SECRET)


def test_a_changed_token_is_refused():
    token = security.create_access_token(uuid.uuid4(), SECRET, minutes=5)
    head, body, signature = token.split(".")
    flipped = signature[:-2] + ("AA" if signature[-2:] != "AA" else "BB")
    with pytest.raises(security.InvalidToken):
        security.decode_access_token(f"{head}.{body}.{flipped}", SECRET)


@pytest.mark.parametrize("junk", ["", "abc", "a.b.c", "....", "Bearer x"])
def test_junk_is_refused(junk):
    with pytest.raises(security.InvalidToken):
        security.decode_access_token(junk, SECRET)


def test_token_without_expiry_is_refused():
    token = jwt.encode({"sub": str(uuid.uuid4())}, SECRET, algorithm="HS256")
    with pytest.raises(security.InvalidToken):
        security.decode_access_token(token, SECRET)


def test_token_with_no_signature_algorithm_is_refused():
    claims = {"sub": str(uuid.uuid4()), "exp": dt.datetime.now(dt.timezone.utc) + dt.timedelta(minutes=5)}
    token = jwt.encode(claims, None, algorithm="none")
    with pytest.raises(security.InvalidToken):
        security.decode_access_token(token, SECRET)


def test_token_whose_subject_is_not_a_user_id_is_refused():
    claims = {"sub": "not-a-uuid", "exp": dt.datetime.now(dt.timezone.utc) + dt.timedelta(minutes=5)}
    with pytest.raises(security.InvalidToken):
        security.decode_access_token(jwt.encode(claims, SECRET, algorithm="HS256"), SECRET)
