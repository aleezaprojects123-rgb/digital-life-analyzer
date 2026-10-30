import datetime as dt

from fastapi import APIRouter, Depends, HTTPException, Response
from sqlalchemy import delete, select, update
from sqlalchemy.dialects.postgresql import insert as pg_insert
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from ..auth import Auth, current_auth, current_user, require_auth_configured
from ..config import Settings, get_settings
from ..db import get_session
from ..mailer import Mailer, get_mailer
from ..models import PasswordResetToken, RevokedToken, User
from ..schemas import ForgotPasswordIn, LoginIn, MessageOut, RegisterIn, ResetPasswordIn, TokenOut, UserOut
from ..security import (
    create_access_token,
    hash_password,
    hash_reset_token,
    new_reset_token,
    verify_password,
    verify_password_of_nobody,
)

router = APIRouter(tags=["accounts"])

# One answer for a known and an unknown email, so "forgot password" can not be used to find out who has an account.
FORGOT_ANSWER = "If an account with this email exists, a password reset link has been sent."
BAD_RESET_LINK = "This password reset link is not valid. It may have expired or been used already."


@router.post("/auth/register", response_model=UserOut, status_code=201)
def register(body: RegisterIn, session: Session = Depends(get_session)) -> User:
    """Make an account on the free plan. The plan can not be chosen here."""
    user = User(email=body.email, password_hash=hash_password(body.password))
    session.add(user)
    try:
        session.commit()
    except IntegrityError:  # the email is taken (the database enforces it, even for two requests at once)
        session.rollback()
        raise HTTPException(status_code=409, detail="An account with this email already exists") from None
    session.refresh(user)
    return user


@router.post("/auth/login", response_model=TokenOut)
def login(
    body: LoginIn,
    settings: Settings = Depends(get_settings),
    session: Session = Depends(get_session),
) -> TokenOut:
    require_auth_configured(settings)
    user = session.scalar(select(User).where(User.email == body.email))
    if user is None:
        verify_password_of_nobody(body.password)  # same waiting time as a wrong password
        password_ok = False
    else:
        password_ok = verify_password(user.password_hash, body.password)
    if user is None or not password_ok:
        # One answer for "no such email" and "wrong password", so nobody can use login to find out who has an account.
        raise HTTPException(
            status_code=401, detail="Incorrect email or password", headers={"WWW-Authenticate": "Bearer"}
        )
    token = create_access_token(
        user.id, settings.jwt_secret, settings.access_token_minutes, version=user.token_version
    )
    return TokenOut(access_token=token, expires_in=settings.access_token_minutes * 60)


@router.post("/auth/logout", status_code=204)
def logout(auth: Auth = Depends(current_auth), session: Session = Depends(get_session)) -> Response:
    """Ends the login that was used for this call. Her other logins (another browser) keep working."""
    now = dt.datetime.now(dt.timezone.utc)
    session.execute(delete(RevokedToken).where(RevokedToken.expires_at < now))  # expired anyway: tidy up
    session.execute(
        pg_insert(RevokedToken)
        .values(jti=auth.token.jti, expires_at=auth.token.expires_at)
        .on_conflict_do_nothing(index_elements=["jti"])
    )
    session.commit()
    return Response(status_code=204)


@router.post("/auth/forgot-password", response_model=MessageOut, status_code=202)
def forgot_password(
    body: ForgotPasswordIn,
    settings: Settings = Depends(get_settings),
    session: Session = Depends(get_session),
    mailer: Mailer = Depends(get_mailer),
) -> MessageOut:
    """Makes a one-time link that stops working after `reset_token_minutes` and hands it to the mailer.
    The answer is the same whether or not the email has an account."""
    user = session.scalar(select(User).where(User.email == body.email))
    if user is not None:
        now = dt.datetime.now(dt.timezone.utc)
        token, token_hash = new_reset_token()
        # A newer link replaces the older ones.
        session.execute(
            update(PasswordResetToken)
            .where(PasswordResetToken.user_id == user.id, PasswordResetToken.used_at.is_(None))
            .values(used_at=now)
        )
        session.add(
            PasswordResetToken(
                user_id=user.id,
                token_hash=token_hash,
                expires_at=now + dt.timedelta(minutes=settings.reset_token_minutes),
            )
        )
        session.commit()
        # The token goes in the part after "#", which a browser never sends to any server.
        mailer.send_password_reset(
            user.email, f"{settings.dashboard_url}/reset-password#token={token}", settings.reset_token_minutes
        )
    return MessageOut(detail=FORGOT_ANSWER)


@router.post("/auth/reset-password", status_code=204)
def reset_password(body: ResetPasswordIn, session: Session = Depends(get_session)) -> Response:
    """Sets a new password with a link from `forgot-password`. The link works once, before it expires.
    Every login the user had before is ended."""
    new_hash = hash_password(body.new_password)  # first, so a wrong link costs the same time as a right one
    now = dt.datetime.now(dt.timezone.utc)
    # One statement checks and uses the link, so two requests with the same link can not both win.
    used = session.execute(
        update(PasswordResetToken)
        .where(
            PasswordResetToken.token_hash == hash_reset_token(body.token),
            PasswordResetToken.used_at.is_(None),
            PasswordResetToken.expires_at > now,
        )
        .values(used_at=now)
        .returning(PasswordResetToken.user_id)
    ).first()
    if used is None:
        session.rollback()
        raise HTTPException(status_code=400, detail=BAD_RESET_LINK)
    session.execute(
        update(User)
        .where(User.id == used.user_id)
        .values(password_hash=new_hash, token_version=User.token_version + 1)
    )
    session.execute(  # any other link she asked for stops working too
        update(PasswordResetToken)
        .where(PasswordResetToken.user_id == used.user_id, PasswordResetToken.used_at.is_(None))
        .values(used_at=now)
    )
    session.commit()
    return Response(status_code=204)


@router.get("/me", response_model=UserOut)
def me(user: User = Depends(current_user)) -> User:
    """The caller's own account, and only hers."""
    return user
