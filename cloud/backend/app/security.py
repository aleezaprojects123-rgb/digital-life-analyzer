"""Password hashing (Argon2id) and access tokens (signed JWT).

Passwords are never stored, logged or returned: only the hash is kept. A token proves who the
caller is; it holds the user id and an expiry date, nothing else.
"""

import datetime as dt
import uuid

import jwt
from argon2 import PasswordHasher
from argon2.exceptions import InvalidHashError, VerificationError

MIN_SECRET_LENGTH = 32
ALGORITHM = "HS256"

_hasher = PasswordHasher()  # Argon2id with the library's recommended settings (random salt each time)
# Checked when the email is unknown, so "no such account" takes as long as "wrong password".
_DUMMY_HASH = _hasher.hash("password-that-belongs-to-nobody")


def hash_password(password: str) -> str:
    return _hasher.hash(password)


def verify_password(stored_hash: str, password: str) -> bool:
    try:
        return _hasher.verify(stored_hash, password)
    except (VerificationError, InvalidHashError):
        return False


def verify_password_of_nobody(password: str) -> None:
    """Spend the same time as a real check. The answer is always 'no'."""
    verify_password(_DUMMY_HASH, password)


class InvalidToken(Exception):
    pass


def create_access_token(user_id: uuid.UUID, secret: str, minutes: int, now: dt.datetime | None = None) -> str:
    now = now or dt.datetime.now(dt.timezone.utc)
    claims = {"sub": str(user_id), "iat": now, "exp": now + dt.timedelta(minutes=minutes)}
    return jwt.encode(claims, secret, algorithm=ALGORITHM)


def decode_access_token(token: str, secret: str) -> uuid.UUID:
    """Returns the user id, or raises InvalidToken (bad signature, expired, wrong algorithm, no subject...)."""
    try:
        claims = jwt.decode(token, secret, algorithms=[ALGORITHM], options={"require": ["exp", "sub"]})
        return uuid.UUID(claims["sub"])
    except (jwt.PyJWTError, ValueError, KeyError, TypeError):
        raise InvalidToken from None
