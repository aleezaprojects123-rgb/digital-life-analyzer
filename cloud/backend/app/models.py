"""Database tables. Only what exists today: accounts (F9) and stored daily summaries (F8).
Later features (password reset, goals, Stripe) add their own tables with their own migrations."""

import datetime as dt
import uuid

from sqlalchemy import (
    CheckConstraint,
    Date,
    DateTime,
    ForeignKey,
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
