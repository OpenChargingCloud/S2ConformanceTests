#!/usr/bin/env python
"""An FRBC resource manager built on s2-python's asynchronous connection, driven by the
WWCP S2 interoperability tests.

It connects to a CEM WebSocket server (plain S2-JSON-over-WebSocket mode with the Handshake
exchange, exactly like s2-python's own examples), announces the EV charger of the S2 worked
example, offers Fill Rate Based Control, sends its FRBC system description, storage status
and actuator status when the control type is activated, acknowledges instructions with an
InstructionStatusUpdate NEW, and reports everything as JSON-line events on stdout.

Commands on stdin (see s2_harness.command_loop): send, send_raw, stop.
"""

import argparse
import asyncio
import datetime
import sys
import uuid
from typing import Any, Optional

from s2python.common import (
    Commodity,
    CommodityQuantity,
    Currency,
    Duration,
    InstructionStatus,
    InstructionStatusUpdate,
    NumberRange,
    PowerRange,
    RevokeObject,
    Role,
    RoleType,
    SessionRequest,
    SessionRequestType,
)
from s2python.connection import AssetDetails
from s2python.connection.async_ import S2AsyncConnection, WebsocketClientMedium
from s2python.connection.async_.control_type.class_based import (
    FRBCControlType,
    NoControlControlType,
    ResourceManagerHandler,
)
from s2python.frbc import (
    FRBCActuatorDescription,
    FRBCActuatorStatus,
    FRBCInstruction,
    FRBCOperationMode,
    FRBCOperationModeElement,
    FRBCStorageDescription,
    FRBCStorageStatus,
    FRBCSystemDescription,
)

from s2_harness import command_loop, describe, emit, finish, instrument, send_and_report


def now() -> datetime.datetime:
    return datetime.datetime.now(tz=datetime.timezone.utc)


class HarnessFRBC(FRBCControlType):
    """The EV charger of the S2 worked example: off and charging (1.4 to 11 kW), battery 0 to 100 %."""

    def __init__(self) -> None:
        super().__init__()
        self.actuator_id = uuid.uuid4()
        self.off_id = uuid.uuid4()
        self.charging_id = uuid.uuid4()

    def system_description(self) -> FRBCSystemDescription:
        return FRBCSystemDescription(
            message_id=uuid.uuid4(),
            valid_from=now(),
            actuators=[
                FRBCActuatorDescription(
                    id=self.actuator_id,
                    diagnostic_label="EV charger actuator",
                    supported_commodities=[Commodity.ELECTRICITY],
                    operation_modes=[
                        FRBCOperationMode(
                            id=self.off_id,
                            diagnostic_label="off",
                            abnormal_condition_only=False,
                            elements=[
                                FRBCOperationModeElement(
                                    fill_level_range=NumberRange(start_of_range=0.0, end_of_range=100.0),
                                    fill_rate=NumberRange(start_of_range=0.0, end_of_range=0.0),
                                    power_ranges=[
                                        PowerRange(
                                            start_of_range=0.0,
                                            end_of_range=0.0,
                                            commodity_quantity=CommodityQuantity.ELECTRIC_POWER_3_PHASE_SYMMETRIC,
                                        )
                                    ],
                                )
                            ],
                        ),
                        FRBCOperationMode(
                            id=self.charging_id,
                            diagnostic_label="charging",
                            abnormal_condition_only=False,
                            elements=[
                                FRBCOperationModeElement(
                                    fill_level_range=NumberRange(start_of_range=0.0, end_of_range=100.0),
                                    fill_rate=NumberRange(start_of_range=0.00065, end_of_range=0.0051),
                                    power_ranges=[
                                        PowerRange(
                                            start_of_range=1400.0,
                                            end_of_range=11000.0,
                                            commodity_quantity=CommodityQuantity.ELECTRIC_POWER_3_PHASE_SYMMETRIC,
                                        )
                                    ],
                                )
                            ],
                        ),
                    ],
                    transitions=[],
                    timers=[],
                )
            ],
            storage=FRBCStorageDescription(
                provides_leakage_behaviour=False,
                provides_fill_level_target_profile=True,
                provides_usage_forecast=False,
                fill_level_range=NumberRange(start_of_range=0.0, end_of_range=100.0),
                diagnostic_label="Battery SoC",
                fill_level_label="EV Battery SoC",
            ),
        )

    async def handle_instruction(self, connection: S2AsyncConnection, msg: Any, send_okay: Any) -> None:
        assert send_okay is not None
        if not isinstance(msg, FRBCInstruction):
            raise RuntimeError(f"Expected an FRBCInstruction but received {type(msg)}.")
        await send_okay()
        emit(
            "instruction",
            instruction_id=str(msg.id),
            actuator_id=str(msg.actuator_id),
            operation_mode=str(msg.operation_mode),
            operation_mode_factor=msg.operation_mode_factor,
            execution_time=msg.execution_time.isoformat(),
        )
        await send_and_report(
            connection,
            InstructionStatusUpdate(
                message_id=uuid.uuid4(),
                instruction_id=msg.id,
                status_type=InstructionStatus.NEW,
                timestamp=now(),
            ),
        )

    async def activate(self, connection: S2AsyncConnection) -> None:
        emit(
            "activated",
            control_type="FILL_RATE_BASED_CONTROL",
            actuator_id=str(self.actuator_id),
            operation_mode_ids=[str(self.off_id), str(self.charging_id)],
        )
        await send_and_report(connection, self.system_description())
        await send_and_report(connection, FRBCStorageStatus(message_id=uuid.uuid4(), present_fill_level=42.0))
        await send_and_report(
            connection,
            FRBCActuatorStatus(
                message_id=uuid.uuid4(),
                actuator_id=self.actuator_id,
                active_operation_mode_id=self.off_id,
                operation_mode_factor=0.0,
            ),
        )

    async def deactivate(self, connection: S2AsyncConnection) -> None:
        emit("deactivated", control_type="FILL_RATE_BASED_CONTROL")


class HarnessNoControl(NoControlControlType):
    async def activate(self, connection: S2AsyncConnection) -> None:
        emit("activated", control_type="NOT_CONTROLABLE")

    async def deactivate(self, connection: S2AsyncConnection) -> None:
        emit("deactivated", control_type="NOT_CONTROLABLE")


async def on_session_request(connection: S2AsyncConnection, msg: Any, send_okay: Any) -> None:
    assert send_okay is not None
    await send_okay()
    emit("session_request", request=msg.request.value, diagnostic_label=msg.diagnostic_label)
    if msg.request == SessionRequestType.TERMINATE:
        await connection.stop()


async def on_revoke_object(connection: S2AsyncConnection, msg: Any, send_okay: Any) -> None:
    assert send_okay is not None
    await send_okay()
    emit("revoke_object", object_type=msg.object_type.value, object_id=str(msg.object_id))


async def run(args: argparse.Namespace) -> int:
    frbc = HarnessFRBC()

    asset_details = AssetDetails(
        resource_id=uuid.UUID(args.resource_id) if args.resource_id else uuid.uuid4(),
        name="Interop EV charger",
        manufacturer="ACME",
        model="WallBox-b100",
        serial_number="123",
        firmware_version="v1.0",
        instruction_processing_delay=Duration.from_milliseconds(3000),
        roles=[Role(role=RoleType.ENERGY_CONSUMER, commodity=Commodity.ELECTRICITY)],
        currency=Currency.EUR,
        provides_forecast=False,
        provides_power_measurements=[CommodityQuantity.ELECTRIC_POWER_3_PHASE_SYMMETRIC],
    )

    rm_handler = ResourceManagerHandler(asset_details=asset_details, control_types=[frbc, HarnessNoControl()])

    medium = WebsocketClientMedium(url=args.url, verify_certificate=False, bearer_token=args.token)

    try:
        await medium.connect()
    except Exception as error:  # pylint: disable=broad-exception-caught
        emit("connect_failed", url=args.url, error=describe(error))
        return 2

    emit("connected", url=args.url, resource_id=str(asset_details.resource_id))

    connection = S2AsyncConnection(medium=medium)
    instrument(connection)
    rm_handler.register_handlers(connection)
    connection.register_handler(SessionRequest, on_session_request)
    connection.register_handler(RevokeObject, on_revoke_object)

    async def get_connection() -> S2AsyncConnection:
        return connection

    commands = asyncio.create_task(command_loop(get_connection))

    try:
        await connection.run()
    finally:
        commands.cancel()
        emit("stopping")
        try:
            await asyncio.wait_for(medium.disconnect(), timeout=5.0)
        except Exception:  # pylint: disable=broad-exception-caught
            pass

    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="An s2-python FRBC resource manager for the WWCP S2 interoperability tests.")
    parser.add_argument("--url", required=True, help="The WebSocket URL of the CEM, e.g. ws://127.0.0.1:8003/")
    parser.add_argument("--token", default=None, help="The bearer token to present in the upgrade request")
    parser.add_argument("--resource-id", default=None, help="The resource identification (UUID)")
    args = parser.parse_args()
    code = asyncio.run(run(args))
    finish(code)
    return code


if __name__ == "__main__":
    sys.exit(main())
