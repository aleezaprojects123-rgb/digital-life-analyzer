"""Database tables. Only what exists today: accounts (F9), logout and password reset (F9) and stored
daily summaries (F8). Later features (goals, Stripe) add their own tables with their own migrations."""

import datetime as dt
import uuid

from sqlalchemy import (
    CheckConstraint,
    Date,
    DateTime,
    ForeignKey,
    Integer,
    LargeBinary,
    String,
    Text,
    UniqueConstraint,
    Uuid,
    func,
    text,
)
from sqlalchemy.orm import Mapped, mapped_column

from .db import Base


class User(Base):
    __tablename__ = "users"
    __table_args__ = (
        UniqueConstraint("email"),
        CheckConstraint("email = lower(email)", name="email_lowercase"),
        CheckConstraint("plan IN ('free', 'pro')", name="plan_known"),
    )

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, server_default=text("gen_random_uuid()"))
    # Always stored in lowercase, so "A@x.com" and "a@x.com" are the same account.
    email: Mapped[str] = mapped_column(String(254))
    # A hash made by the password hasher. The password itself is never stored.
    password_hash: Mapped[str] = mapped_column(Text)
    plan: Mapped[str] = mapped_column(String(16), server_default="free")
    created_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    # Every login token carries the version it was made with. A password reset raises it, which ends every login.
    token_version: Mapped[int] = mapped_column(Integer, server_default="0")


class RevokedToken(Base):
    """A login token that was logged out before it expired. Holds only the token's own id, no user data.
    A row can be deleted once expires_at has passed: the token would be refused as expired anyway."""

    __tablename__ = "revoked_tokens"

    jti: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True)
    expires_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True), index=True)


class PasswordResetToken(Base):
    """One password reset link. Only the SHA-256 of the token is stored, never the token itself."""

    __tablename__ = "password_reset_tokens"
    __table_args__ = (UniqueConstraint("token_hash"),)

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, server_default=text("gen_random_uuid()"))
    user_id: Mapped[uuid.UUID] = mapped_column(Uuid, ForeignKey("users.id", ondelete="CASCADE"), index=True)
    token_hash: Mapped[str] = mapped_column(String(64))
    created_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    expires_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True))
    # Set when the link is used, or when a newer link replaces it. A link with used_at set never works again.
    used_at: Mapped[dt.datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)


class DailySummary(Base):
    """One summary per user per day (Contract 2). The summary JSON is stored encrypted with the
    server key (ENCRYPTION_KEY). The other columns hold no activity data."""

    __tablename__ = "daily_summaries"

    # Part of the key, so a user can only ever reach her own rows by asking with her own user_id.
    user_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="CASCADE"), primary_key=True
    )
    summary_date: Mapped[dt.date] = mapped_column(Date, primary_key=True)
    schema_version: Mapped[str] = mapped_column(String(8))
    # When the agent built it. A newer upload replaces an older one for the same date.
    generated_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True))
    received_at: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    payload_encrypted: Mapped[bytes] = mapped_column(LargeBinary)
