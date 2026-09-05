"""Shared plumbing of the s2auth drivers: JSON-lines events on stdout, JSON-lines commands
on stdin, and an exit that does not wait for the stdin reader thread.

The drivers wrap s2auth (libs/s2auth), the Python implementation of S2 Connect pairing and
session initiation, so that the C# interoperability tests can script it and observe every
HTTP request it makes.
"""

import asyncio
import json
import os
import sys
import threading
from typing import Any, Optional


def emit(event: str, **data: Any) -> None:
    """Write one JSON event line to stdout."""
    print(json.dumps({"event": event, **data}, default=str), flush=True)


def describe(error: BaseException) -> str:
    text = f"{type(error).__name__}: {error}"
    cause = error.__cause__
    while cause is not None:
        text += f" <- {type(cause).__name__}: {cause}"
        cause = cause.__cause__
    return text


def finish(code: int = 0) -> None:
    """Report the end of the driver and leave without waiting for the stdin reader thread."""
    emit("stopped")
    sys.stdout.flush()
    sys.stderr.flush()
    os._exit(code)  # pylint: disable=protected-access


def stdin_lines(loop: asyncio.AbstractEventLoop) -> "asyncio.Queue[Optional[str]]":
    """Read stdin on a daemon thread and hand every line to an asyncio queue (None at EOF)."""

    queue: "asyncio.Queue[Optional[str]]" = asyncio.Queue()

    def reader() -> None:
        try:
            for line in sys.stdin:
                loop.call_soon_threadsafe(queue.put_nowait, line)
        except Exception:  # pylint: disable=broad-exception-caught
            pass
        loop.call_soon_threadsafe(queue.put_nowait, None)

    threading.Thread(target=reader, name="stdin", daemon=True).start()
    return queue


def parse_command(line: str) -> Optional[dict]:
    """Parse one command line; malformed lines are reported and skipped."""
    line = line.strip()
    if not line:
        return None
    try:
        command = json.loads(line)
    except json.JSONDecodeError as error:
        emit("error", operation="command", message=f"not JSON: {error}", line=line)
        return None
    if not isinstance(command, dict) or "command" not in command:
        emit("error", operation="command", message="a command must be an object with a 'command' property", line=line)
        return None
    return command


def redact(headers: Any) -> dict:
    """Return the headers as a plain dictionary; nothing is redacted, the tests inspect them."""
    return {key: value for key, value in dict(headers).items()}
