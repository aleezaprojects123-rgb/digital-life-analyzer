"""The encryption of stored summaries, on its own (no database). All keys here are made up."""

import base64
import datetime as dt
import uuid

import pytest

from app import crypto
from tests.conftest import TEST_ENCRYPTION_KEY

KEY = crypto.load_key(TEST_ENCRYPTION_KEY)
OTHER_KEY = bytes(range(1, 33))
USER = uuid.UUID("11111111-1111-1111-1111-111111111111")
OTHER_USER = uuid.UUID("22222222-2222-2222-2222-222222222222")
DAY = dt.date(2026, 10, 28)
SUMMARY = {"date": "2026-10-28", "top_sites": [{"domain": "youtube.com", "seconds": 90}], "note": "naïve ✓"}


def b64(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).decode()


def test_round_trip_gives_back_the_same_summary_including_non_ascii_text():
    blob = crypto.encrypt_summary(KEY, USER, DAY, SUMMARY)
    assert crypto.decrypt_summary(KEY, USER, DAY, blob) == SUMMARY


def test_the_stored_bytes_do_not_contain_the_text():
    blob = crypto.encrypt_summary(KEY, USER, DAY, SUMMARY)
    for needle in (b"youtube", b"top_sites", b"2026-10-28", b"seconds"):
        assert needle not in blob


def test_the_same_summary_encrypts_differently_each_time():
    assert crypto.encrypt_summary(KEY, USER, DAY, SUMMARY) != crypto.encrypt_summary(KEY, USER, DAY, SUMMARY)


def test_another_key_can_not_read_it():
    blob = crypto.encrypt_summary(KEY, USER, DAY, SUMMARY)
    with pytest.raises(crypto.DecryptionError):
        crypto.decrypt_summary(OTHER_KEY, USER, DAY, blob)


def test_a_blob_moved_to_another_user_can_not_be_read():
    blob = crypto.encrypt_summary(KEY, USER, DAY, SUMMARY)
    with pytest.raises(crypto.DecryptionError):
        crypto.decrypt_summary(KEY, OTHER_USER, DAY, blob)


def test_a_blob_moved_to_another_day_can_not_be_read():
    blob = crypto.encrypt_summary(KEY, USER, DAY, SUMMARY)
    with pytest.raises(crypto.DecryptionError):
        crypto.decrypt_summary(KEY, USER, DAY + dt.timedelta(days=1), blob)


@pytest.mark.parametrize("position", [0, 1, 12, 13, -1])
def test_a_changed_byte_is_noticed(position):
    blob = bytearray(crypto.encrypt_summary(KEY, USER, DAY, SUMMARY))
    blob[position] ^= 0x01
    with pytest.raises(crypto.DecryptionError):
        crypto.decrypt_summary(KEY, USER, DAY, bytes(blob))


@pytest.mark.parametrize("junk", [b"", b"\x01", b"\x01" + b"a" * 20, b"\x02" + b"a" * 60, b"plain text, not a blob" * 3])
def test_junk_is_not_a_blob(junk):
    with pytest.raises(crypto.DecryptionError):
        crypto.decrypt_summary(KEY, USER, DAY, junk)


def test_a_truncated_blob_is_refused():
    blob = crypto.encrypt_summary(KEY, USER, DAY, SUMMARY)
    with pytest.raises(crypto.DecryptionError):
        crypto.decrypt_summary(KEY, USER, DAY, blob[:-1])


def test_a_database_driver_may_hand_back_memoryview():
    blob = crypto.encrypt_summary(KEY, USER, DAY, SUMMARY)
    assert crypto.decrypt_summary(KEY, USER, DAY, memoryview(blob)) == SUMMARY


# ------------------------------------------------------------------- the key


def test_a_good_key_is_32_bytes_of_urlsafe_base64():
    assert crypto.load_key(b64(bytes(32))) == bytes(32)


def test_spaces_around_the_key_are_ignored():
    assert crypto.load_key("  " + b64(bytes(32)) + "\n") == bytes(32)


@pytest.mark.parametrize(
    "bad",
    ["", "   ", "not base64 at all!", b64(bytes(16)), b64(bytes(24)), b64(bytes(33)), b64(bytes(64)), "ü" * 44, "A"],
)
def test_a_missing_or_wrong_key_is_refused(bad):
    with pytest.raises(crypto.EncryptionNotConfigured):
        crypto.load_key(bad)
