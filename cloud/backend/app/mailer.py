"""Sending email. There is no real mailer yet: the MOCK mailer writes the message into the server log.

The password reset link is printed in the log where you started `uvicorn`. No email leaves this PC.
A real mailer (SMTP or a mail service) replaces `LogMailer` later; routes only call `send_password_reset`.
"""

import logging
import sys
from typing import Protocol

log = logging.getLogger("dla.mailer")


def _show_in_server_log() -> None:
    """uvicorn only shows its own loggers, so give this one a handler (once) to make the link appear."""
    if not log.handlers:
        handler = logging.StreamHandler(sys.stderr)
        handler.setFormatter(logging.Formatter("%(levelname)s:     %(name)s: %(message)s"))
        log.addHandler(handler)
    log.setLevel(logging.INFO)


class Mailer(Protocol):
    def send_password_reset(self, to: str, link: str, valid_minutes: int) -> None: ...


class LogMailer:
    """MOCK: prints the reset link instead of emailing it. For development only."""

    def send_password_reset(self, to: str, link: str, valid_minutes: int) -> None:
        _show_in_server_log()
        log.info(
            "MOCK MAILER (no email is sent). Password reset link for %s, valid %d minutes, works once: %s",
            to,
            valid_minutes,
            link,
        )


def get_mailer() -> Mailer:
    """FastAPI dependency. The tests replace it with one that records the message."""
    return LogMailer()
