"""Shared pieces of the s2-python drivers: JSON-line events, connection instrumentation and
the stdin command loop.

The drivers instrument an ``S2AsyncConnection`` so that the tests can observe everything
that crosses the wire:

* every received message and connection event (``received`` / ``connection_event``),
* every sent message (``sent``), including the ReceptionStatus messages s2-python generates,
* every ReceptionStatus received for an own message (``reception_status_received``), also the
  ones nobody awaits, and a ``reception_status_duplicate`` event when s2-python's awaiter
  sees a second status for the same message.
"""

import asyncio
import json
import os
import sys
import threading
import uuid
from typing import Any, Awaitable, Callable, Optional

from s2python.common import ReceptionStatus
from s2python.connection.async_ import S2AsyncConnection
from s2python.connection.async_.message_handlers import MessageHandlers
from s2python.s2_parser import S2Parser
from s2python.s2_validation_error import S2ValidationError


def emit(event: str, **data: Any) -> None:
    """Write one JSON event line to stdout."""
    print(json.dumps({"event": event, **data}, default=str), flush=True)


def json_of(message: Any) -> Any:
    return json.loads(message.to_json())


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


class LoggingHandlers(MessageHandlers):
    """The s2-python handler registry, logging every dispatched event."""

    async def handle_event(self, connection: S2AsyncConnection, event: Any) -> None:
        if hasattr(event, "to_json"):
            emit("received", message=json_of(event))
        else:
            emit("connection_event", name=type(event).__name__)
        await super().handle_event(connection, event)


def instrument(connection: S2AsyncConnection) -> None:
    """Log everything the connection sends and every ReceptionStatus it receives."""

    connection._handlers = LoggingHandlers()  # pylint: disable=protected-access

    original_send = connection.send_and_forget

    async def logged_send(message: Any) -> None:
        emit("sent", message=json_of(message))
        await original_send(message)

    connection.send_and_forget = logged_send  # type: ignore[method-assign]

    awaiter = connection._reception_status_awaiter  # pylint: disable=protected-access
    original_receive = awaiter.receive_reception_status

    async def logged_receive(status: ReceptionStatus) -> None:
        emit(
            "reception_status_received",
            subject_message_id=str(status.subject_message_id),
            status=status.status.value,
            diagnostic_label=status.diagnostic_label,
        )
        try:
            await original_receive(status)
        except RuntimeError as error:
            emit("reception_status_duplicate", subject_message_id=str(status.subject_message_id), error=str(error))

    awaiter.receive_reception_status = logged_receive  # type: ignore[method-assign]


async def send_and_report(connection: S2AsyncConnection, message: Any, timeout: float = 5.0) -> Optional[ReceptionStatus]:
    """Send a message, await its ReceptionStatus and report the outcome as an event."""

    message_type = getattr(message, "message_type", type(message).__name__)

    if isinstance(message, ReceptionStatus):
        await connection.send_and_forget(message)
        return None

    try:
        status = await connection.send_msg_and_await_reception_status(message, timeout_reception_status=timeout, raise_on_error=False)
    except (TimeoutError, asyncio.TimeoutError):
        emit("reception_timeout", message_type=message_type, message_id=str(message.message_id))
        return None
    except Exception as error:  # pylint: disable=broad-exception-caught
        emit("send_error", message_type=message_type, message_id=str(message.message_id), error=describe(error))
        return None

    emit(
        "reception_status",
        message_type=message_type,
        subject_message_id=str(status.subject_message_id),
        status=status.status.value,
        diagnostic_label=status.diagnostic_label,
    )
    return status


CommandHandler = Callable[[S2AsyncConnection, dict], Awaitable[bool]]


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


async def command_loop(get_connection: Callable[[], Awaitable[S2AsyncConnection]], extra: Optional[CommandHandler] = None) -> None:
    """Read JSON commands from stdin and apply them to the connection.

    Built-in commands:
      {"cmd": "send", "message": {...}}   parse with s2-python, send and await the ReceptionStatus
      {"cmd": "send_raw", "text": "..."}  write the text to the medium as it is (negative tests)
      {"cmd": "stop"}                     stop the connection
    Additional commands are handled by ``extra`` (returns True when it handled the command).
    """

    lines = stdin_lines(asyncio.get_running_loop())

    while True:
        line = await lines.get()
        if line is None:
            return
        line = line.strip()
        if not line:
            continue

        try:
            command = json.loads(line)
        except json.JSONDecodeError as error:
            emit("command_error", error=f"invalid JSON: {error}")
            continue

        connection = await get_connection()
        name = command.get("cmd")

        try:
            if name == "send":
                try:
                    message = S2Parser.parse_as_any_message(command["message"])
                except (S2ValidationError, ValueError, KeyError, TypeError) as error:
                    emit("command_error", cmd=name, error=describe(error))
                    continue
                await send_and_report(connection, message, float(command.get("timeout", 5.0)))
            elif name == "send_raw":
                await connection._medium.send(command["text"])  # pylint: disable=protected-access
                emit("sent_raw", text=command["text"])
            elif name == "stop":
                await connection.stop()
                emit("stopping")
            elif extra is not None and await extra(connection, command):
                pass
            else:
                emit("command_error", cmd=name, error="unknown command")
        except Exception as error:  # pylint: disable=broad-exception-caught
            emit("command_error", cmd=name, error=describe(error))


def new_id() -> uuid.UUID:
    return uuid.uuid4()
