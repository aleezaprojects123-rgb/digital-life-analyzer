"""Password hashing (Argon2id) and access tokens (signed JWT).

Passwords are never stored, logged or returned: only the hash is kept. A login token proves who the
caller is; it holds the user id, its own id, the user's token version and its dates, nothing else.
"""

import datetime as dt
import hashlib
import secrets
import uuid
from dataclasses import dataclass

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


@dataclass(frozen=True)
class TokenClaims:
    user_id: uuid.UUID
    jti: uuid.UUID  # this one token's own id: logging out blacklists it
    version: int  # the user's token_version when it was made: a password reset raises it and ends every login
    expires_at: dt.datetime


def create_access_token(
    user_id: uuid.UUID,
    secret: str,
    minutes: int,
    now: dt.datetime | None = None,
    version: int = 0,
    jti: uuid.UUID | None = None,
) -> str:
    now = now or dt.datetime.now(dt.timezone.utc)
    claims = {
        "sub": str(user_id),
        "iat": now,
        "exp": now + dt.timedelta(minutes=minutes),
        "jti": str(jti or uuid.uuid4()),
        "ver": version,
    }
    return jwt.encode(claims, secret, algorithm=ALGORITHM)


def decode_access_token(token: str, secret: str) -> TokenClaims:
    """Returns the claims, or raises InvalidToken (bad signature, expired, wrong algorithm, no subject...)."""
    try:
        claims = jwt.decode(token, secret, algorithms=[ALGORITHM], options={"require": ["exp", "sub", "jti", "ver"]})
        version = claims["ver"]
        if isinstance(version, bool) or not isinstance(version, int):
            raise ValueError("version is not a number")
        return TokenClaims(
            user_id=uuid.UUID(claims["sub"]),
            jti=uuid.UUID(claims["jti"]),
            version=version,
            expires_at=dt.datetime.fromtimestamp(claims["exp"], dt.timezone.utc),
        )
    except (jwt.PyJWTError, ValueError, KeyError, TypeError):
        raise InvalidToken from None


# ---- password reset links ------------------------------------------------------------------------------------------
# The link holds a random token. Only its SHA-256 is stored, so a copy of the database holds no working links.
# (A fast hash is right here: the token is 256 random bits, so it can not be guessed the way a password can.)


def new_reset_token() -> tuple[str, str]:
    """(the token to put in the link, the hash to store)."""
    token = secrets.token_urlsafe(32)
    return token, hash_reset_token(token)


def hash_reset_token(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()
