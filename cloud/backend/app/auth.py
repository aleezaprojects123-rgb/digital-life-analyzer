"""Finding out who is calling: reads the login token and loads that user."""

from dataclasses import dataclass

from fastapi import Depends, HTTPException
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy.orm import Session

from .config import Settings, get_settings
from .db import get_session
from .models import RevokedToken, User
from .security import MIN_SECRET_LENGTH, InvalidToken, TokenClaims, decode_access_token

_bearer = HTTPBearer(auto_error=False)


def require_auth_configured(settings: Settings) -> None:
    """Without a long secret, tokens would be easy to forge, so login and token checks refuse to run."""
    if len(settings.jwt_secret) < MIN_SECRET_LENGTH:
        raise HTTPException(status_code=503, detail="authentication is not configured")


def not_authenticated() -> HTTPException:
    return HTTPException(
        status_code=401, detail="Not authenticated", headers={"WWW-Authenticate": "Bearer"}
    )


@dataclass(frozen=True)
class Auth:
    user: User
    token: TokenClaims


def current_auth(
    credentials: HTTPAuthorizationCredentials | None = Depends(_bearer),
    settings: Settings = Depends(get_settings),
    session: Session = Depends(get_session),
) -> Auth:
    """The logged-in user and the token she used. A token is refused when it is changed, expired, logged out,
    or made before her last password reset."""
    require_auth_configured(settings)
    if credentials is None:
        raise not_authenticated()
    try:
        claims = decode_access_token(credentials.credentials, settings.jwt_secret)
    except InvalidToken:
        raise not_authenticated() from None
    if session.get(RevokedToken, claims.jti) is not None:  # logged out
        raise not_authenticated()
    user = session.get(User, claims.user_id)
    if user is None:  # for example, the account was deleted after the token was made
        raise not_authenticated()
    if user.token_version != claims.version:  # her password was reset after this token was made
        raise not_authenticated()
    return Auth(user=user, token=claims)


def current_user(auth: Auth = Depends(current_auth)) -> User:
    """The logged-in user. Every route that shows or changes account data depends on this, and then
    asks only for rows that belong to the returned user."""
    return auth.user
