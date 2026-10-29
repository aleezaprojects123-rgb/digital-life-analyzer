import pytest
from alembic import command
from alembic.config import Config
from fastapi.testclient import TestClient
from sqlalchemy import create_engine, text
from sqlalchemy.engine import make_url
from sqlalchemy.orm import sessionmaker

from app.config import BACKEND_DIR, Settings, get_settings
from app.db import get_session
from app.main import create_app

# A made-up secret for the tests only. It is long enough to be accepted (32 characters or more).
TEST_JWT_SECRET = "test-only-secret-" + "x" * 40


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


@pytest.fixture
def app_db(test_db_url):
    """The test database at the latest migration with no accounts in it (quicker than wiping it)."""
    command.upgrade(alembic_config(test_db_url), "head")
    engine = create_engine(test_db_url)
    with engine.begin() as connection:
        connection.execute(text("TRUNCATE users CASCADE"))
    yield engine
    engine.dispose()


def build_client(engine, jwt_secret: str) -> TestClient:
    """The real app, talking to the test database, with the given login secret."""
    app = create_app()
    make_session = sessionmaker(bind=engine, expire_on_commit=False)

    def test_session():
        with make_session() as session:
            yield session

    app.dependency_overrides[get_session] = test_session
    app.dependency_overrides[get_settings] = lambda: Settings(_env_file=None, jwt_secret=jwt_secret)
    return TestClient(app)


@pytest.fixture
def client(app_db) -> TestClient:
    return build_client(app_db, TEST_JWT_SECRET)


@pytest.fixture
def client_without_secret(app_db) -> TestClient:
    """The login secret is not set, as on a PC that has not finished setup."""
    return build_client(app_db, "")
