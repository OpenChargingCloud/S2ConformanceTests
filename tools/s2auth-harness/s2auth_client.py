"""s2auth pairing client driver: pairs with an S2 Connect pairing server, initiates a
session and unpairs on command, reporting every HTTP exchange.

Events (stdout, one JSON object per line):
  {"event":"ready", "client_node_id":…}
  {"event":"http_request",  "method":…, "url":…, "headers":{…}, "body":…}
  {"event":"http_response", "status":…, "url":…, "headers":{…}, "body":…}
  {"event":"pair_result",    "success":…, "pairing_s2_node_id":…, "client_s2_node_id":…, "connection_details":{…}}
  {"event":"connect_result", "success":…, "pairing_s2_node_id":…, "connection_details":{…}}
  {"event":"unpair_result",  "success":…, "pairing_s2_node_id":…, "previous_connection_details":{…}}
  {"event":"error", "operation":…, "type":…, "message":…}
  {"event":"stopped"}

Commands (stdin, one JSON object per line):
  {"command":"pair"}  {"command":"connect"}  {"command":"unpair"}  {"command":"stop"}
"""

import argparse
import asyncio
import copy
import dataclasses
import logging
import sys
from typing import Any, Optional

import httpx

from harness import describe, emit, finish, parse_command, redact, stdin_lines

from s2auth.client import ClientSettings, PairingClient, PairingClientHooks
from s2auth.common.model.s2_connect_common import Deployment, Role


class InMemoryStore:
    """A ConnectionStore keeping the connection details in memory, with the merge semantics
    of s2auth's SQLAlchemy Dao (store_connection_details updates the given keys)."""

    def __init__(self) -> None:
        self.details: dict[str, dict[str, Any]] = {}

    def store_connection_details(self, s2_node_id: str, details: dict[str, Any]) -> None:
        self.details.setdefault(s2_node_id, {}).update(details)

    def load_connection_details(self, s2_node_id: str) -> Optional[dict[str, Any]]:
        stored = self.details.get(s2_node_id)
        return copy.deepcopy(stored) if stored is not None else None

    def remove_connection_details(self, s2_node_id: str) -> bool:
        return self.details.pop(s2_node_id, None) is not None


def body_of(content: bytes) -> Any:
    if not content:
        return None
    try:
        return httpx.Response(200, content=content).json()
    except Exception:  # pylint: disable=broad-exception-caught
        return content.decode("utf-8", errors="replace")


async def on_request(request: httpx.Request) -> None:
    emit("http_request", method=request.method, url=str(request.url), headers=redact(request.headers), body=body_of(request.content))


async def on_response(response: httpx.Response) -> None:
    await response.aread()
    emit("http_response", status=response.status_code, url=str(response.request.url), headers=redact(response.headers), body=body_of(response.content))


def settings_from(args: argparse.Namespace) -> ClientSettings:
    return ClientSettings(
        server_url=args.server_url,
        pairing_token=args.token,
        pairing_s2_node_id=args.target,
        client_s2_node_id=args.client_node_id,
        client_role=Role(args.role),
        client_deployment=Deployment(args.deployment) if args.deployment else None,
        domain_name=args.domain,
        verify_tls=not args.insecure,
        ssl_certfile=args.ca,
        storage_db_url="sqlite://",
        supported_s2_versions=args.s2_version,
        cleint_brand=args.brand,
        client_device_type=args.device_type,
        client_model_name=args.model_name,
    )


async def run(args: argparse.Namespace) -> None:
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO, stream=sys.stderr, format="%(levelname)s %(name)s: %(message)s")

    store = InMemoryStore()
    hooks = PairingClientHooks(http_request=on_request, http_response=on_response)
    client = PairingClient.from_settings(settings_from(args), storage=store, hooks=hooks)

    emit("ready", client_node_id=args.client_node_id, server_url=args.server_url, target=args.target)

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
            if name == "pair":
                result = await client.pair()
                emit("pair_result", **dataclasses.asdict(result))
            elif name == "connect":
                result = await client.connect(command.get("pairing_s2_node_id"))
                emit("connect_result", **dataclasses.asdict(result))
            elif name == "unpair":
                result = await client.unpair(command.get("pairing_s2_node_id"))
                emit("unpair_result", **dataclasses.asdict(result))
            elif name == "stop":
                break
            else:
                emit("error", operation=name, type="UnknownCommand", message=f"unknown command '{name}'")
        except Exception as error:  # pylint: disable=broad-exception-caught
            emit("error", operation=name, type=type(error).__name__, message=describe(error))


def main() -> None:
    parser = argparse.ArgumentParser(description="s2auth pairing client driver")
    parser.add_argument("--server-url", required=True, help="the pairing URL including the API version, e.g. https://host:8443/pairing/v1")
    parser.add_argument("--token", required=True, help="the pairing token")
    parser.add_argument("--target", default=None, help="the node to pair with: a UUID (sent as nodeId) or an alias (sent as nodeIdAlias)")
    parser.add_argument("--client-node-id", required=True, help="the UUID of this client node")
    parser.add_argument("--role", default="RM", choices=["RM", "CEM"])
    parser.add_argument("--deployment", default=None, choices=["LAN", "WAN"], help="the deployment (default: s2auth's auto detection)")
    parser.add_argument("--domain", default=None, help="the domain name of the WAN challenge-response")
    parser.add_argument("--ca", default=None, help="a PEM file to verify the server certificate against")
    parser.add_argument("--insecure", action="store_true", help="do not verify the server certificate")
    parser.add_argument("--s2-version", action="append", default=None, help="a supported S2 message version (repeatable, default: s2auth's default)")
    parser.add_argument("--brand", default="GraphDefined")
    parser.add_argument("--device-type", default="EV charger")
    parser.add_argument("--model-name", default="s2auth interop client")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    if args.s2_version is None:
        args.s2_version = ClientSettings().supported_s2_versions

    try:
        asyncio.run(run(args))
    except Exception as error:  # pylint: disable=broad-exception-caught
        emit("error", operation="run", type=type(error).__name__, message=describe(error))
        finish(1)
    finish(0)


if __name__ == "__main__":
    main()
