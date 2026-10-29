"""The migrations against a real PostgreSQL test database (TEST_DATABASE_URL). All data here is made up."""

import datetime as dt
import uuid

import pytest
from alembic import command
from alembic.autogenerate import compare_metadata
from alembic.migration import MigrationContext
from sqlalchemy import inspect, text
from sqlalchemy.exc import IntegrityError

from app.db import Base
from tests.conftest import alembic_config


def add_user(connection, email="test@example.com", plan=None) -> uuid.UUID:
    # password_hash is a placeholder, not a real hash
    if plan is None:
        row = connection.execute(
            text("INSERT INTO users (email, password_hash) VALUES (:e, 'placeholder') RETURNING id"), {"e": email}
        )
    else:
        row = connection.execute(
            text("INSERT INTO users (email, password_hash, plan) VALUES (:e, 'placeholder', :p) RETURNING id"),
            {"e": email, "p": plan},
        )
    return row.scalar_one()


def add_summary(connection, user_id, day="2026-10-27"):
    connection.execute(
        text(
            "INSERT INTO daily_summaries (user_id, summary_date, schema_version, generated_at, payload_encrypted) "
            "VALUES (:u, :d, '1', :g, :p)"
        ),
        {"u": user_id, "d": dt.date.fromisoformat(day), "g": dt.datetime(2026, 10, 27, 17, 30, tzinfo=dt.timezone.utc), "p": b"not-real-ciphertext"},
    )


def test_upgrade_creates_the_tables(migrated_db):
    assert set(inspect(migrated_db).get_table_names()) == {"users", "daily_summaries", "alembic_version"}


def test_users_columns(migrated_db):
    columns = {c["name"] for c in inspect(migrated_db).get_columns("users")}
    assert columns == {"id", "email", "password_hash", "plan", "created_at"}


def test_daily_summaries_hold_no_activity_columns(migrated_db):
    columns = {c["name"] for c in inspect(migrated_db).get_columns("daily_summaries")}
    assert columns == {"user_id", "summary_date", "schema_version", "generated_at", "received_at", "payload_encrypted"}


def test_new_user_gets_an_id_and_the_free_plan(migrated_db):
    with migrated_db.begin() as connection:
        user_id = add_user(connection)
        row = connection.execute(text("SELECT plan, created_at FROM users WHERE id = :i"), {"i": user_id}).one()
    assert row.plan == "free"
    assert row.created_at is not None


def test_email_must_be_lowercase(migrated_db):
    with pytest.raises(IntegrityError):
        with migrated_db.begin() as connection:
            add_user(connection, email="Mixed@Example.com")


def test_email_must_be_unique(migrated_db):
    with pytest.raises(IntegrityError):
        with migrated_db.begin() as connection:
            add_user(connection)
            add_user(connection)


def test_plan_must_be_free_or_pro(migrated_db):
    with migrated_db.begin() as connection:
        add_user(connection, email="pro@example.com", plan="pro")
    with pytest.raises(IntegrityError):
        with migrated_db.begin() as connection:
            add_user(connection, email="gold@example.com", plan="gold")


def test_one_summary_per_user_per_day(migrated_db):
    with migrated_db.begin() as connection:
        user_id = add_user(connection)
        add_summary(connection, user_id)
        add_summary(connection, user_id, day="2026-10-28")  # another day is fine
    with pytest.raises(IntegrityError):
        with migrated_db.begin() as connection:
            add_summary(connection, user_id)


def test_two_users_can_have_a_summary_for_the_same_day(migrated_db):
    with migrated_db.begin() as connection:
        add_summary(connection, add_user(connection, email="one@example.com"))
        add_summary(connection, add_user(connection, email="two@example.com"))
        count = connection.execute(text("SELECT count(*) FROM daily_summaries")).scalar_one()
    assert count == 2


def test_a_summary_needs_an_existing_user(migrated_db):
    with pytest.raises(IntegrityError):
        with migrated_db.begin() as connection:
            add_summary(connection, uuid.uuid4())


def test_deleting_a_user_deletes_her_summaries(migrated_db):
    with migrated_db.begin() as connection:
        user_id = add_user(connection)
        add_summary(connection, user_id)
        connection.execute(text("DELETE FROM users WHERE id = :i"), {"i": user_id})
        count = connection.execute(text("SELECT count(*) FROM daily_summaries")).scalar_one()
    assert count == 0


def test_migration_and_models_agree(migrated_db):
    """If someone changes models.py without a migration (or the reverse), this fails."""
    with migrated_db.connect() as connection:
        differences = compare_metadata(MigrationContext.configure(connection), Base.metadata)
    assert differences == []


def test_downgrade_removes_the_tables(migrated_db, test_db_url):
    command.downgrade(alembic_config(test_db_url), "base")
    assert set(inspect(migrated_db).get_table_names()) == {"alembic_version"}


def test_upgrade_can_run_twice(migrated_db, test_db_url):
    command.upgrade(alembic_config(test_db_url), "head")  # already at head: changes nothing, no error
    assert "users" in inspect(migrated_db).get_table_names()
