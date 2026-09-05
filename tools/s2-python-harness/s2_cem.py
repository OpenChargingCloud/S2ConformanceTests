#!/usr/bin/env python
"""A minimal customer energy manager built on s2-python's asynchronous connection, driven by
the WWCP S2 interoperability tests.

s2-python only ships a WebSocket *client* medium, so this driver wraps a `websockets` server
connection in the ``S2AsyncMediumConnection`` interface and runs an ``S2AsyncConnection``
on top of it, with all of s2-python's message validation and ReceptionStatus handling.

Plain S2-JSON-over-WebSocket mode: the CEM sends its Handshake when the connection starts,
answers the RM's Handshake with a HandshakeResponse (selecting the first offered version it
supports), selects a control type once the ResourceManagerDetails arrive, remembers the
actuator and operation modes of an FRBC system description, and acknowledges everything
else. It reports everything as JSON-line events on stdout.

Commands on stdin (see s2_harness.command_loop): send, send_raw, stop, and
  {"cmd": "instruction", "factor": 1.0, "operation_mode": "<uuid>"}   an FRBC.Instruction
  {"cmd": "terminate", "label": "..."}                                a SessionRequest TERMINATE
"""

import argparse
import asyncio
import datetime
import http
import sys
import uuid
from typing import Any, AsyncGenerator, List, Optional

import websockets
from websockets.asyncio.server import serve

from s2python.common import (
    ControlType,
    EnergyManagementRole,
    Handshake,
    HandshakeResponse,
    InstructionStatusUpdate,
    PowerForecast,
    PowerMeasurement,
    ResourceManagerDetails,
    RevokeObject,
    SelectControlType,
    SessionRequest,
    SessionRequestType,
)
from s2python.connection.async_ import S2AsyncConnection
from s2python.connection.async_.medium.s2_medium import MediumClosedConnectionError, S2AsyncMediumConnection
from s2python.connection.connection_events import ConnectionStarted
from s2python.frbc import (
    FRBCActuatorStatus,
    FRBCFillLevelTargetProfile,
    FRBCInstruction,
    FRBCLeakageBehaviour,
    FRBCStorageStatus,
    FRBCSystemDescription,
    FRBCTimerStatus,
    FRBCUsageForecast,
)
from s2python.version import S2_VERSION

from s2_harness import command_loop, emit, finish, instrument, send_and_report


def now() -> datetime.datetime:
    return datetime.datetime.now(tz=datetime.timezone.utc)


class WebsocketServerMedium(S2AsyncMediumConnection):
    """An accepted `websockets` server connection as an s2-python medium."""

    def __init__(self, connection: Any) -> None:
        self._connection = connection
        self._closed = False

    async def is_connected(self) -> bool:
        return not self._closed

    async def messages(self) -> AsyncGenerator[Any, None]:  # pylint: disable=invalid-overridden-method
        try:
            async for message in self._connection:
                yield message
        except websockets.WebSocketException as error:
            self._closed = True
            raise MediumClosedConnectionError("the WebSocket connection failed") from error
        self._closed = True
        raise MediumClosedConnectionError("the WebSocket connection was closed by the peer")

    async def send(self, message: str) -> None:
        try:
            await self._connection.send(message)
        except websockets.WebSocketException as error:
            self._closed = True
            raise MediumClosedConnectionError("the WebSocket connection is closed") from error


class HarnessCEM:
    """The CEM behaviour on one connection."""

    def __init__(self, supported_versions: List[str], select: Optional[str]) -> None:
        self.supported_versions = supported_versions
        self.select = ControlType(select) if select else None
        self.actuator_id: Optional[uuid.UUID] = None
        self.operation_mode_ids: List[uuid.UUID] = []

    def register(self, connection: S2AsyncConnection) -> None:
        connection.register_handler(ConnectionStarted, self.on_started)
        connection.register_handler(Handshake, self.on_handshake)
        connection.register_handler(ResourceManagerDetails, self.on_resource_manager_details)
        connection.register_handler(FRBCSystemDescription, self.on_system_description)
        for message_type in (
            HandshakeResponse,
            FRBCStorageStatus,
            FRBCActuatorStatus,
            FRBCFillLevelTargetProfile,
            FRBCLeakageBehaviour,
            FRBCUsageForecast,
            FRBCTimerStatus,
            PowerMeasurement,
            PowerForecast,
            InstructionStatusUpdate,
            SessionRequest,
            RevokeObject,
        ):
            connection.register_handler(message_type, self.on_acknowledged)

    async def on_started(self, connection: S2AsyncConnection, event: Any, send_okay: Any) -> None:
        await send_and_report(
            connection,
            Handshake(message_id=uuid.uuid4(), role=EnergyManagementRole.CEM, supported_protocol_versions=self.supported_versions),
        )

    async def on_handshake(self, connection: S2AsyncConnection, msg: Any, send_okay: Any) -> None:
        assert send_okay is not None
        await send_okay()
        offered = list(msg.supported_protocol_versions or [])
        selected = next((version for version in offered if version in self.supported_versions), None)
        emit("handshake", role=msg.role.value, offered=offered, selected=selected)
        if selected is None:
            await send_and_report(
                connection,
                SessionRequest(message_id=uuid.uuid4(), request=SessionRequestType.TERMINATE, diagnostic_label="no common S2 protocol version"),
            )
            await connection.stop()
            return
        await send_and_report(connection, HandshakeResponse(message_id=uuid.uuid4(), selected_protocol_version=selected))

    async def on_resource_manager_details(self, connection: S2AsyncConnection, msg: Any, send_okay: Any) -> None:
        assert send_okay is not None
        await send_okay()
        available = list(msg.available_control_types)
        chosen = self.select if self.select in available else (ControlType.NOT_CONTROLABLE if ControlType.NOT_CONTROLABLE in available else ControlType.NO_SELECTION)
        emit(
            "resource_manager_details",
            resource_id=str(msg.resource_id),
            name=msg.name,
            available_control_types=[control_type.value for control_type in available],
            selected=chosen.value,
        )
        await send_and_report(connection, SelectControlType(message_id=uuid.uuid4(), control_type=chosen))

    async def on_system_description(self, connection: S2AsyncConnection, msg: Any, send_okay: Any) -> None:
        assert send_okay is not None
        await send_okay()
        actuator = msg.actuators[0]
        self.actuator_id = actuator.id
        self.operation_mode_ids = [operation_mode.id for operation_mode in actuator.operation_modes]
        emit(
            "system_description",
            actuator_id=str(self.actuator_id),
            operation_mode_ids=[str(operation_mode_id) for operation_mode_id in self.operation_mode_ids],
            fill_level_label=msg.storage.fill_level_label,
        )

    async def on_acknowledged(self, connection: S2AsyncConnection, msg: Any, send_okay: Any) -> None:
        assert send_okay is not None
        await send_okay()

    async def handle_command(self, connection: S2AsyncConnection, command: dict) -> bool:
        name = command.get("cmd")

        if name == "instruction":
            if self.actuator_id is None or not self.operation_mode_ids:
                emit("command_error", cmd=name, error="no FRBC system description received yet")
                return True
            operation_mode = uuid.UUID(command["operation_mode"]) if command.get("operation_mode") else self.operation_mode_ids[-1]
            instruction = FRBCInstruction(
                message_id=uuid.uuid4(),
                id=uuid.uuid4(),
                actuator_id=self.actuator_id,
                operation_mode=operation_mode,
                operation_mode_factor=float(command.get("factor", 1.0)),
                execution_time=now(),
                abnormal_condition=bool(command.get("abnormal_condition", False)),
            )
            emit("instruction_sent", instruction_id=str(instruction.id), operation_mode=str(operation_mode))
            await send_and_report(connection, instruction)
            return True

        if name == "terminate":
            await send_and_report(
                connection,
                SessionRequest(message_id=uuid.uuid4(), request=SessionRequestType.TERMINATE, diagnostic_label=command.get("label", "terminated by the test")),
            )
            await connection.stop()
            return True

        return False


async def run(args: argparse.Namespace) -> int:
    supported_versions = [S2_VERSION] + [version for version in args.accept_versions.split(",") if version]
    cem = HarnessCEM(supported_versions, args.select)

    connection_ready: asyncio.Future = asyncio.get_running_loop().create_future()
    finished = asyncio.Event()

    async def handler(websocket: Any) -> None:
        emit("client_connected", path=websocket.request.path if websocket.request else None)
        medium = WebsocketServerMedium(websocket)
        connection = S2AsyncConnection(medium=medium)
        instrument(connection)
        cem.register(connection)
        if not connection_ready.done():
            connection_ready.set_result(connection)
        try:
            await connection.run()
        finally:
            try:
                await websocket.close()
            except Exception:  # pylint: disable=broad-exception-caught
                pass
            emit("client_disconnected")
            finished.set()

    def process_request(connection: Any, request: Any) -> Any:
        if args.expect_token:
            authorization = request.headers.get("Authorization", "")
            if authorization != f"Bearer {args.expect_token}":
                emit("rejected", authorization=authorization)
                return connection.respond(http.HTTPStatus.UNAUTHORIZED, "invalid token\n")
        return None

    async def get_connection() -> S2AsyncConnection:
        return await connection_ready

    commands = asyncio.create_task(command_loop(get_connection, cem.handle_command))

    async with serve(handler, "127.0.0.1", args.port, process_request=process_request):
        emit("listening", port=args.port, supported_versions=supported_versions)
        await finished.wait()

    commands.cancel()
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="An s2-python CEM WebSocket server for the WWCP S2 interoperability tests.")
    parser.add_argument("--port", type=int, required=True, help="The TCP port to listen on (loopback only)")
    parser.add_argument("--expect-token", default=None, help="Reject upgrade requests without this bearer token")
    parser.add_argument("--select", default="FILL_RATE_BASED_CONTROL", help="The control type to select when the RM offers it")
    parser.add_argument("--accept-versions", default="", help="Additional S2 protocol versions to accept in the Handshake (comma separated)")
    args = parser.parse_args()
    code = asyncio.run(run(args))
    finish(code)
    return code


if __name__ == "__main__":
    sys.exit(main())
