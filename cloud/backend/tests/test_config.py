from app.config import Settings


def make(**values) -> Settings:
    # _env_file=None: ignore any real .env on this PC, so the test gives the same answer everywhere
    return Settings(_env_file=None, **values)


def test_plain_postgres_urls_get_the_psycopg_driver():
    assert make(database_url="postgresql://u:p@localhost:5432/dla").sqlalchemy_url == "postgresql+psycopg://u:p@localhost:5432/dla"
    assert make(database_url="postgres://u:p@localhost/dla").sqlalchemy_url == "postgresql+psycopg://u:p@localhost/dla"


def test_a_url_that_already_names_a_driver_is_kept():
    url = "postgresql+psycopg://u:p@localhost/dla"
    assert make(database_url=url).sqlalchemy_url == url


def test_no_database_url_stays_empty():
    assert make().sqlalchemy_url == ""


def test_secrets_have_no_default_value():
    settings = make()
    assert settings.jwt_secret == ""
    assert settings.encryption_key == ""


def test_the_reset_link_lasts_30_minutes_unless_set_otherwise():
    assert make().reset_token_minutes == 30
    assert make(reset_token_minutes=10).reset_token_minutes == 10


def test_the_dashboard_address_defaults_to_the_local_dev_server_and_loses_a_trailing_slash():
    assert make().dashboard_url == "http://localhost:5173"
    assert make(app_base_url="https://dla.example.test/").dashboard_url == "https://dla.example.test"


def test_cors_origins_are_split_on_commas():
    assert make(cors_origins="http://a.test, http://b.test,").cors_origin_list == ["http://a.test", "http://b.test"]
