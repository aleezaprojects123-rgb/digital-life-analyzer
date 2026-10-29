import pytest
from fastapi.testclient import TestClient
from sqlalchemy import create_engine

from app.main import create_app


@pytest.fixture
def client() -> TestClient:
    return TestClient(create_app())


def test_health_says_ok(client):
    response = client.get("/api/v1/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


def test_health_db_says_unavailable_without_a_database(client, monkeypatch):
    def no_database():
        raise RuntimeError("DATABASE_URL is not set")

    monkeypatch.setattr("app.routes.health.get_engine", no_database)
    response = client.get("/api/v1/health/db")
    assert response.status_code == 503
    assert response.json() == {"detail": "database unavailable"}


def test_health_db_does_not_leak_connection_details(client, monkeypatch):
    # Nothing listens on port 1, so connecting fails. The answer must not name the host or the password.
    engine = create_engine("postgresql+psycopg://someone:secret-pw@127.0.0.1:1/nodb", connect_args={"connect_timeout": 1})
    monkeypatch.setattr("app.routes.health.get_engine", lambda: engine)
    response = client.get("/api/v1/health/db")
    assert response.status_code == 503
    assert "secret-pw" not in response.text and "127.0.0.1" not in response.text


def test_health_db_says_ok_with_the_test_database(client, monkeypatch, migrated_db):
    monkeypatch.setattr("app.routes.health.get_engine", lambda: migrated_db)
    response = client.get("/api/v1/health/db")
    assert response.status_code == 200
    assert response.json() == {"database": "ok"}


def test_dashboard_origin_is_allowed_by_cors(client):
    response = client.options(
        "/api/v1/health",
        headers={"Origin": "http://localhost:5173", "Access-Control-Request-Method": "GET"},
    )
    assert response.status_code == 200
    assert response.headers["access-control-allow-origin"] == "http://localhost:5173"


def test_other_origins_are_not_allowed_by_cors(client):
    response = client.options(
        "/api/v1/health",
        headers={"Origin": "https://evil.example", "Access-Control-Request-Method": "GET"},
    )
    assert "access-control-allow-origin" not in response.headers
    plain = client.get("/api/v1/health", headers={"Origin": "https://evil.example"})
    assert "access-control-allow-origin" not in plain.headers
