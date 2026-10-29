"""Finding out who is calling: reads the login token and loads that user."""

from fastapi import Depends, HTTPException
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy.orm import Session

from .config import Settings, get_settings
from .db import get_session
from .models import User
from .security import MIN_SECRET_LENGTH, InvalidToken, decode_access_token

_bearer = HTTPBearer(auto_error=False)


def require_auth_configured(settings: Settings) -> None:
    """Without a long secret, tokens would be easy to forge, so login and token checks refuse to run."""
    if len(settings.jwt_secret) < MIN_SECRET_LENGTH:
        raise HTTPException(status_code=503, detail="authentication is not configured")


def not_authenticated() -> HTTPException:
    return HTTPException(
        status_code=401, detail="Not authenticated", headers={"WWW-Authenticate": "Bearer"}
    )


def current_user(
    credentials: HTTPAuthorizationCredentials | None = Depends(_bearer),
    settings: Settings = Depends(get_settings),
    session: Session = Depends(get_session),
) -> User:
    """The logged-in user. Every route that shows or changes account data depends on this, and then
    asks only for rows that belong to the returned user."""
    require_auth_configured(settings)
    if credentials is None:
        raise not_authenticated()
    try:
        user_id = decode_access_token(credentials.credentials, settings.jwt_secret)
    except InvalidToken:
        raise not_authenticated() from None
    user = session.get(User, user_id)
    if user is None:  # for example, the account was deleted after the token was made
        raise not_authenticated()
    return user
