import pytest
from alembic import command
from alembic.config import Config
from sqlalchemy import create_engine, text
from sqlalchemy.engine import make_url

from app.config import BACKEND_DIR, get_settings


def alembic_config(url: str) -> Config:
    config = Config(str(BACKEND_DIR / "alembic.ini"))
    # configparser treats % as special, and a password may contain one
    config.set_main_option("sqlalchemy.url", url.replace("%", "%%"))
    return config


@pytest.fixture(scope="session")
def test_db_url() -> str:
    """The test database. Skipped if TEST_DATABASE_URL is not set. Refuses any database whose name
    does not end with _test, because the tests wipe it."""
    url = get_settings().sqlalchemy_test_url
    if not url:
        pytest.skip("TEST_DATABASE_URL is not set (see cloud/backend/README.md)")
    name = make_url(url).database or ""
    if not name.endswith("_test"):
        pytest.exit(f"TEST_DATABASE_URL points at '{name}'. The name must end with _test: the tests wipe it.", 2)
    return url


@pytest.fixture
def empty_db(test_db_url):
    """The test database with nothing in it."""
    engine = create_engine(test_db_url)
    with engine.begin() as connection:
        connection.execute(text("DROP SCHEMA public CASCADE"))
        connection.execute(text("CREATE SCHEMA public"))
    yield engine
    engine.dispose()


@pytest.fixture
def migrated_db(empty_db, test_db_url):
    """The test database after `alembic upgrade head`."""
    command.upgrade(alembic_config(test_db_url), "head")
    return empty_db
