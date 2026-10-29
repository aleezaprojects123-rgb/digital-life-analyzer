"""users and daily_summaries

Revision ID: 0001
Revises:
Create Date: 2026-10-29
"""
from alembic import op
import sqlalchemy as sa

revision = "0001"
down_revision = None
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "users",
        sa.Column("id", sa.Uuid(), server_default=sa.text("gen_random_uuid()"), nullable=False),
        sa.Column("email", sa.String(length=254), nullable=False),
        sa.Column("password_hash", sa.Text(), nullable=False),
        sa.Column("plan", sa.String(length=16), server_default="free", nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), server_default=sa.func.now(), nullable=False),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_users")),
        sa.UniqueConstraint("email", name=op.f("uq_users_email")),
        sa.CheckConstraint("email = lower(email)", name=op.f("ck_users_email_lowercase")),
        sa.CheckConstraint("plan IN ('free', 'pro')", name=op.f("ck_users_plan_known")),
    )
    op.create_table(
        "daily_summaries",
        sa.Column("user_id", sa.Uuid(), nullable=False),
        sa.Column("summary_date", sa.Date(), nullable=False),
        sa.Column("schema_version", sa.String(length=8), nullable=False),
        sa.Column("generated_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("received_at", sa.DateTime(timezone=True), server_default=sa.func.now(), nullable=False),
        sa.Column("payload_encrypted", sa.LargeBinary(), nullable=False),
        sa.ForeignKeyConstraint(
            ["user_id"], ["users.id"], name=op.f("fk_daily_summaries_user_id_users"), ondelete="CASCADE"
        ),
        sa.PrimaryKeyConstraint("user_id", "summary_date", name=op.f("pk_daily_summaries")),
    )


def downgrade() -> None:
    op.drop_table("daily_summaries")
    op.drop_table("users")
