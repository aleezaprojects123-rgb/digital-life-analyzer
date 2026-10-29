from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy import select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from ..auth import current_user, require_auth_configured
from ..config import Settings, get_settings
from ..db import get_session
from ..models import User
from ..schemas import LoginIn, RegisterIn, TokenOut, UserOut
from ..security import create_access_token, hash_password, verify_password, verify_password_of_nobody

router = APIRouter(tags=["accounts"])


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
    token = create_access_token(user.id, settings.jwt_secret, settings.access_token_minutes)
    return TokenOut(access_token=token, expires_in=settings.access_token_minutes * 60)


@router.get("/me", response_model=UserOut)
def me(user: User = Depends(current_user)) -> User:
    """The caller's own account, and only hers."""
    return user
