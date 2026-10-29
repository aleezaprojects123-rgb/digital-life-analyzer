"""Alembic environment. The database address comes from DATABASE_URL (or from the caller, which is
how the tests point it at the test database)."""

from logging.config import fileConfig

from alembic import context
from sqlalchemy import create_engine

from app import models  # noqa: F401  (loads the tables into Base.metadata)
from app.config import get_settings
from app.db import Base

config = context.config
if config.config_file_name is not None:
    # disable_existing_loggers=False keeps other loggers (and the tests' log capture) working
    fileConfig(config.config_file_name, disable_existing_loggers=False)
target_metadata = Base.metadata


def _url() -> str:
    url = config.get_main_option("sqlalchemy.url") or get_settings().sqlalchemy_url
    if not url:
        raise SystemExit("DATABASE_URL is not set. Copy .env.example to .env and fill it in.")
    return url


def run_migrations_offline() -> None:
    context.configure(url=_url(), target_metadata=target_metadata, literal_binds=True)
    with context.begin_transaction():
        context.run_migrations()


def run_migrations_online() -> None:
    engine = create_engine(_url())
    with engine.connect() as connection:
        context.configure(connection=connection, target_metadata=target_metadata)
        with context.begin_transaction():
            context.run_migrations()
    engine.dispose()


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()
