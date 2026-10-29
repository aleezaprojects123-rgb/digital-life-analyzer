"""Settings, read from environment variables or from a local .env file.

The names are the ones in the repository's .env.example. The real values live only in your own
.env (ignored by git). Nothing secret has a default here.
"""

from functools import lru_cache
from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict

# cloud/backend/app/config.py -> parents[3] is the repository root
REPO_ROOT = Path(__file__).resolve().parents[3]
BACKEND_DIR = Path(__file__).resolve().parents[1]


def _psycopg_url(url: str) -> str:
    """Accept postgresql:// or postgres:// and make SQLAlchemy use the psycopg 3 driver."""
    for prefix in ("postgresql://", "postgres://"):
        if url.startswith(prefix):
            return "postgresql+psycopg://" + url[len(prefix):]
    return url


class Settings(BaseSettings):
    # Later files win: cloud/backend/.env overrides the repository-root .env.
    model_config = SettingsConfigDict(
        env_file=(REPO_ROOT / ".env", BACKEND_DIR / ".env"),
        env_file_encoding="utf-8",
        extra="ignore",
    )

    database_url: str = ""
    # A separate database for the tests. Its name must end with "_test": the tests wipe it.
    test_database_url: str = ""
    # Signs login tokens. At least 32 characters; login refuses to work without it.
    jwt_secret: str = ""
    # How long a login stays valid (minutes). 480 = 8 hours.
    access_token_minutes: int = 480
    encryption_key: str = ""
    app_base_url: str = ""
    # Web addresses allowed to call the API from a browser, separated by commas.
    cors_origins: str = "http://localhost:5173,http://127.0.0.1:5173"

    @property
    def sqlalchemy_url(self) -> str:
        return _psycopg_url(self.database_url)

    @property
    def sqlalchemy_test_url(self) -> str:
        return _psycopg_url(self.test_database_url)

    @property
    def cors_origin_list(self) -> list[str]:
        return [o.strip() for o in self.cors_origins.split(",") if o.strip()]


@lru_cache
def get_settings() -> Settings:
    return Settings()
