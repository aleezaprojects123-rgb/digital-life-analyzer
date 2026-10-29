"""What the API accepts and returns for accounts."""

import datetime as dt
import uuid
from typing import Annotated

from pydantic import BaseModel, BeforeValidator, ConfigDict, EmailStr, Field

MIN_PASSWORD_LENGTH = 8
MAX_PASSWORD_LENGTH = 128  # a limit stops someone sending a huge "password" just to burn CPU


def _tidy_email(value):
    """Spaces around the address are dropped and it is lowercased, so Ali@X.com and ali@x.com are one account."""
    return value.strip().lower() if isinstance(value, str) else value


Email = Annotated[EmailStr, BeforeValidator(_tidy_email), Field(max_length=254)]


class RegisterIn(BaseModel):
    # forbid: a request that sends anything else (for example "plan": "pro") is refused, not ignored
    model_config = ConfigDict(extra="forbid")

    email: Email
    password: str = Field(min_length=MIN_PASSWORD_LENGTH, max_length=MAX_PASSWORD_LENGTH)


class LoginIn(BaseModel):
    model_config = ConfigDict(extra="forbid")

    email: Annotated[str, BeforeValidator(_tidy_email), Field(min_length=1, max_length=254)]
    password: str = Field(min_length=1, max_length=MAX_PASSWORD_LENGTH)


class UserOut(BaseModel):
    """The account as the owner may see it. No password, no hash."""

    model_config = ConfigDict(from_attributes=True)

    id: uuid.UUID
    email: str
    plan: str
    created_at: dt.datetime


class TokenOut(BaseModel):
    access_token: str
    token_type: str = "bearer"
    expires_in: int  # seconds
