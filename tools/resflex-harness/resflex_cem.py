"""resflex CEM driver: the S2 CEM engine of the ElaadNL Residential Flexibility initiative
(s2_cem_lib.S2CemApp, libs/resflex-open-s2-hems-connector) behind aiohttp with TLS, wired the
way its standalone engine (cem_engine/run.py) wires it, but on a port of the test's choosing,
with in-memory persistence (or a state file of the test's choosing, so that a restart can be
tested), without DNS-SD and without the engine's debug API.

The library itself is used unchanged. The driver only observes it: an aiohttp middleware
reports every HTTP request, the ports report what the engine persists and publishes, and the
WebSocket view and WebSocketResponse.send_str are wrapped to report every frame in both
directions.

Events (stdout, one JSON object per line):
  {"event":"listening", "port":…, "pairing_url":…, "connection_url":…, "node_id":…, "fingerprint":…}
  {"event":"http", "method":…, "path":…, "status":…}
  {"event":"persisted", "nodes":{node_id: access_token, …}}
  {"event":"bus", "signal":…, "node_id":…}
  {"event":"notify", "id":…, "title":…}
  {"event":"ws_open", "node_id":…}
  {"event":"received", "node_id":…, "message":{…}}       a frame from the RM
  {"event":"sent", "message":{…}}                         a frame to the RM
  {"event":"ws_closed", "node_id":…}
  {"event":"instruction_sent", "node_id":…, "instruction_id":…, "power_constraints_id":…}
  {"event":"error", "operation":…, "type":…, "message":…}
  {"event":"stopped"}

Commands (stdin, one JSON object per line):
  {"command":"send_instruction", "node_id":…, "upper_limit":…, "lower_limit":…,
   "commodity_quantity":…, "duration_ms":…}   a PEBC.Instruction within the stored constraints
  {"command":"stop"}
"""

import argparse
import asyncio
import hashlib
import json
import logging
import os
import ssl
import sys
import uuid
from typing import Any

from harness import describe, emit, finish, parse_command, stdin_lines


def library_path(args: argparse.Namespace) -> str:
    if args.library:
        return os.path.abspath(args.library)
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.abspath(os.path.join(here, "..", "..", "libs", "resflex-open-s2-hems-connector"))


def leaf_fingerprint(cert_file: str) -> bytes:
    """SHA-256 of the DER encoding of the first certificate in the PEM file, which is what
    the engine's run.py computes and what the LAN challenge-response is bound to."""
    with open(cert_file, "r", encoding="ascii") as pem:
        text = pem.read()
    end = "-----END CERTIFICATE-----"
    first = text[:text.index(end) + len(end)]
    return hashlib.sha256(ssl.PEM_cert_to_DER_cert(first)).digest()


async def run(args: argparse.Namespace) -> None:
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO, stream=sys.stderr, format="%(levelname)s %(name)s: %(message)s")

    # The library is a directory, not an installed distribution (its pyproject.toml would
    # install its subpackages at the top level), so it is imported the way run.py does it.
    sys.path.insert(0, library_path(args))

    # pylint: disable=import-outside-toplevel
    from aiohttp import web
    from s2python.common import CommodityQuantity

    from s2_cem_lib import S2CemApp
    from s2_cem_lib.pairing.pairing_models import CommunicationProtocol, Deployment, EndpointDescription, Role, S2NodeDescription
    from s2_cem_lib.websocket import ws_view
    from s2_cem_lib.websocket.node_state import PowerEnvelopeSpec, get_state
    from s2_cem_lib.websocket.outgoing.pebc_instruction import send_pebc_instruction
    # pylint: enable=import-outside-toplevel

    # -- the four ports of S2CemApp ------------------------------------------------------

    class ReportingPersistence:
        """In memory, and in the --state file when one is given, so that a restarted driver
        restores what the engine persisted (as run.py's file persistence does)."""

        def __init__(self, path: str | None) -> None:
            self.path = path
            self.data: dict | None = None
            if path and os.path.exists(path):
                with open(path, "r", encoding="utf-8") as state:
                    self.data = json.load(state)

        async def load(self) -> dict | None:
            return self.data

        async def save(self, data: dict) -> None:
            self.data = data
            if self.path:
                with open(self.path, "w", encoding="utf-8") as state:
                    json.dump(data, state)
            emit("persisted", nodes={node_id: entry.get("access_token_value") for node_id, entry in data.items()})

        async def remove(self) -> None:
            self.data = None
            if self.path and os.path.exists(self.path):
                os.remove(self.path)
            emit("persisted", nodes={})

    class ReportingEventBus:
        def send(self, signal: str, *signal_args: Any) -> None:
            emit("bus", signal=signal, node_id=str(signal_args[0]) if signal_args else None)

    class ReportingHostEnv:
        def notify(self, notification_id: str, title: str, message: str) -> None:
            emit("notify", id=notification_id, title=title)

        def dismiss(self, notification_id: str) -> None:
            pass

    class LoopScheduler:
        def create_task(self, coro) -> None:  # type: ignore[no-untyped-def]
            asyncio.create_task(coro)

        def call_later(self, delay_seconds: float, cb):  # type: ignore[no-untyped-def]
            handle = asyncio.get_running_loop().call_later(delay_seconds, cb)
            return handle.cancel

    # -- observing the WebSocket traffic ---------------------------------------------------

    def as_json(text: str) -> Any:
        try:
            return json.loads(text)
        except (TypeError, ValueError):
            return text

    original_send_str = web.WebSocketResponse.send_str

    async def send_str(self, data: str, compress: Any = None) -> None:  # type: ignore[no-untyped-def]
        emit("sent", message=as_json(data))
        await original_send_str(self, data, compress)

    web.WebSocketResponse.send_str = send_str  # type: ignore[method-assign]

    original_dispatch = ws_view.S2WebSocketView._dispatch  # pylint: disable=protected-access

    async def dispatch(self, ws, remote, node_id, raw):  # type: ignore[no-untyped-def]
        emit("received", node_id=str(node_id), message=as_json(raw))
        await original_dispatch(self, ws, remote, node_id, raw)

    ws_view.S2WebSocketView._dispatch = dispatch  # type: ignore[method-assign]  # pylint: disable=protected-access

    original_loop = ws_view.S2WebSocketView._run_message_loop  # pylint: disable=protected-access

    async def message_loop(self, ws, pairing_server, node_id, remote):  # type: ignore[no-untyped-def]
        emit("ws_open", node_id=str(node_id))
        try:
            await original_loop(self, ws, pairing_server, node_id, remote)
        finally:
            emit("ws_closed", node_id=str(node_id))

    ws_view.S2WebSocketView._run_message_loop = message_loop  # type: ignore[method-assign]  # pylint: disable=protected-access

    # -- the engine --------------------------------------------------------------------------

    fingerprint = leaf_fingerprint(args.cert)
    node_id = uuid.UUID(args.node_id)

    engine = S2CemApp(
        S2NodeDescription(id=node_id, brand=args.brand, type="CEM", modelName=args.model_name, role=Role.CEM),
        persistence=ReportingPersistence(args.state),
        event_bus=ReportingEventBus(),
        host_env=ReportingHostEnv(),
        scheduler=LoopScheduler(),
        endpoint_description=EndpointDescription(deployment=Deployment.LAN),
        supported_communication_protocols=[CommunicationProtocol.WEBSOCKET],
        supported_s2_message_versions=args.s2_versions,
        certificate_fingerprint=fingerprint,
        pairing_token=args.token,
    )

    @web.middleware
    async def report_requests(request: web.Request, handler):  # type: ignore[no-untyped-def]
        try:
            response = await handler(request)
        except web.HTTPException as error:
            emit("http", method=request.method, path=request.path, status=error.status)
            raise
        emit("http", method=request.method, path=request.path, status=response.status)
        return response

    app = web.Application(middlewares=[report_requests])
    await engine.start()
    engine.register_routes(app)

    tls = ssl.create_default_context(ssl.Purpose.CLIENT_AUTH)
    tls.minimum_version = ssl.TLSVersion.TLSv1_2
    tls.load_cert_chain(args.cert, args.key)

    runner = web.AppRunner(app)
    await runner.setup()
    await web.TCPSite(runner, args.host, args.port, ssl_context=tls).start()

    base = f"https://{args.public_host}:{args.port}"
    emit("listening",
         port=args.port,
         pairing_url=f"{base}/api/s2_pebc/pairing/",
         connection_url=f"{base}/api/s2_pebc/connection/",
         node_id=str(node_id),
         fingerprint=":".join(f"{b:02X}" for b in fingerprint))

    async def send_instruction(command: dict) -> None:
        target = uuid.UUID(str(command["node_id"]))
        server = engine.pairing_server
        ws = server.active_connections.get(target) if server else None
        if ws is None or ws.closed:
            raise RuntimeError(f"node {target} is not connected")
        state = get_state(target)
        if not state.power_constraints:
            raise RuntimeError(f"node {target} has not sent PEBC.PowerConstraints yet")
        constraint = list(state.power_constraints.values())[-1]
        quantity = command.get("commodity_quantity") or constraint.allowed_limit_ranges[0].commodity_quantity
        await send_pebc_instruction(ws,
                                    target,
                                    [PowerEnvelopeSpec(CommodityQuantity(quantity), float(command["upper_limit"]), float(command["lower_limit"]))],
                                    constraint,
                                    duration_ms=int(command.get("duration_ms", 900_000)))
        pending = state.pending_instructions[-1]
        emit("instruction_sent", node_id=str(target), instruction_id=str(pending.instruction_id), power_constraints_id=str(constraint.id))

    loop = asyncio.get_running_loop()
    commands = stdin_lines(loop)

    while True:
        line = await commands.get()
        if line is None:
            break
        command = parse_command(line)
        if command is None:
            continue
        name = command["command"]
        try:
            if name == "send_instruction":
                await send_instruction(command)
            elif name == "stop":
                break
            else:
                emit("error", operation=name, type="UnknownCommand", message=f"unknown command '{name}'")
        except Exception as error:  # pylint: disable=broad-exception-caught
            emit("error", operation=name, type=type(error).__name__, message=describe(error))

    await engine.stop()
    await runner.cleanup()


def main() -> None:
    parser = argparse.ArgumentParser(description="resflex CEM driver")
    parser.add_argument("--host", default="127.0.0.1", help="the address to bind")
    parser.add_argument("--public-host", default="localhost", help="the host name clients use (reported in the listening event)")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--cert", required=True, help="the PEM certificate (chain) file")
    parser.add_argument("--key", required=True, help="the PEM private key file")
    parser.add_argument("--node-id", required=True, help="the UUID of the CEM node")
    parser.add_argument("--token", default=None, help="a fixed pairing token (the engine's --pairing-token)")
    parser.add_argument("--s2-version", dest="s2_versions", action="append", default=None, help="a supported S2 message version (repeatable, default: 0.0.2-beta, as in run.py)")
    parser.add_argument("--brand", default="S2 CEM Engine")
    parser.add_argument("--model-name", default="standalone")
    parser.add_argument("--state", default=None, help="a JSON file the engine's persistence is kept in across restarts (default: memory only)")
    parser.add_argument("--library", default=None, help="the checkout of resflex-open-s2-hems-connector (default: libs/ of this repository)")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    if args.s2_versions is None:
        args.s2_versions = ["0.0.2-beta"]

    try:
        asyncio.run(run(args))
    except Exception as error:  # pylint: disable=broad-exception-caught
        emit("error", operation="run", type=type(error).__name__, message=describe(error))
        finish(1)
    finish(0)


if __name__ == "__main__":
    main()
