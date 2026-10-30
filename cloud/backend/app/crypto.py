"""Encryption of stored summaries (AES-256-GCM, key from ENCRYPTION_KEY).

The key is a server secret read from the environment (.env). It is never written in code or git.
Make one with:
    python -c "import secrets, base64; print(base64.urlsafe_b64encode(secrets.token_bytes(32)).decode())"

Each summary is encrypted with a fresh random nonce. The user id and the date are mixed in as "associated
data", so an encrypted blob copied to another user's row, or to another day, fails to decrypt instead of
showing up there. There is no key rotation yet: if the key is lost, the stored summaries can not be read.
"""

import base64
import binascii
import datetime as dt
import json
import os
import uuid

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

KEY_BYTES = 32
FORMAT_VERSION = b"\x01"
NONCE_BYTES = 12


class EncryptionNotConfigured(Exception):
    pass


class DecryptionError(Exception):
    pass


def load_key(value: str) -> bytes:
    """The 32 key bytes from the text in ENCRYPTION_KEY (URL-safe base64), or EncryptionNotConfigured."""
    try:
        key = base64.urlsafe_b64decode(value.strip().encode("ascii"))
    except (binascii.Error, UnicodeEncodeError, ValueError):
        raise EncryptionNotConfigured from None
    if len(key) != KEY_BYTES:
        raise EncryptionNotConfigured
    return key


def _associated_data(user_id: uuid.UUID, day: dt.date) -> bytes:
    return f"dla-summary-v1|{user_id}|{day.isoformat()}".encode("ascii")


def encrypt_summary(key: bytes, user_id: uuid.UUID, day: dt.date, summary: dict) -> bytes:
    plaintext = json.dumps(summary, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    nonce = os.urandom(NONCE_BYTES)
    return FORMAT_VERSION + nonce + AESGCM(key).encrypt(nonce, plaintext, _associated_data(user_id, day))


def decrypt_summary(key: bytes, user_id: uuid.UUID, day: dt.date, blob: bytes) -> dict:
    """The summary, or DecryptionError (wrong key, changed bytes, or the blob belongs to another user or day)."""
    blob = bytes(blob)
    if blob[:1] != FORMAT_VERSION or len(blob) < 1 + NONCE_BYTES + 16:
        raise DecryptionError
    nonce, ciphertext = blob[1 : 1 + NONCE_BYTES], blob[1 + NONCE_BYTES :]
    try:
        plaintext = AESGCM(key).decrypt(nonce, ciphertext, _associated_data(user_id, day))
        return json.loads(plaintext)
    except (InvalidTag, ValueError):
        raise DecryptionError from None
