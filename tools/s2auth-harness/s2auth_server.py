"""s2auth reference server driver: the FastAPI pairing and connection initiation server of
s2auth (s2auth.reference.server) behind uvicorn with TLS, on a port of the test's choosing
and without the interactive parts of the reference `server` entry point (fixed port 8000,
auto-reload, the keyboard watcher that would read our stdin).

Events (stdout, one JSON object per line):
  {"event":"listening", "port":…, "pairing_url":…, "connection_url":…}
  {"event":"http", "method":…, "path":…, "status":…}
  {"event":"pairing_requested", "client_node_id":…, "pairing_token":…}
  {"event":"token_set", "token":…}
  {"event":"error", "operation":…, "type":…, "message":…}
  {"event":"stopped"}

Commands (stdin, one JSON object per line):
  {"command":"set_token", "token":…}   the one-time pairing token for the next unknown client
  {"command":"stop"}
"""

import argparse
import asyncio
import json
import logging
import os
import sys

from harness import describe, emit, finish, parse_command, stdin_lines


def configure_environment(args: argparse.Namespace) -> None:
    """s2auth's server reads its settings from the environment (pydantic-settings); set them
    before anything of s2auth is imported."""
    env = {
        "SSL_CERTFILE": args.cert,
        "SSL_KEYFILE": args.key,
        "DOMAIN_NAME": args.domain,
        "PAIRING_NODE_ID": args.pairing_node_id,
        "SERVER_S2_NODE_ID": args.server_node_id,
        "DEFAULT_PAIRING_TOKEN": args.token or "",
        "PAIRING_TOKEN_TTL_SECONDS": str(args.token_ttl),
        "SUPPORTED_S2_VERSIONS": json.dumps(args.s2_versions),
        "SUPPORTED_S2_CONNECT_VERSIONS": json.dumps(["v1"]),
        "SUPPORTED_COMMUNICATION_PROTOCOLS": json.dumps(["WebSocket"]),
        "CEM_S2_NODE_ID": args.server_node_id,
        "CEM_TYPE": args.node_type,
        "CEM_MODEL_NAME": args.model_name,
        "CEM_BRAND": args.brand,
        "CEM_DEPLOYMENT_TYPE": args.deployment,
        "SQLALCHEMY_DB_URI": "sqlite+aiosqlite:///:memory:",
    }
    for key, value in env.items():
        os.environ[key] = value


async def run(args: argparse.Namespace) -> None:
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO, stream=sys.stderr, format="%(levelname)s %(name)s: %(message)s")
    configure_environment(args)

    # Imported only now, after the environment is set (pydantic-settings reads it at import time
    # in places), and inside the event loop, because the FastAPI app is bound to it.
    import uvicorn  # pylint: disable=import-outside-toplevel
    from fastapi import FastAPI, Request  # pylint: disable=import-outside-toplevel
    from wepositive_di import inject  # pylint: disable=import-outside-toplevel

    import s2auth  # pylint: disable=import-outside-toplevel
    from s2auth.reference.server.connection import router as connection_router  # pylint: disable=import-outside-toplevel
    from s2auth.reference.server.pairing import router as pairing_router  # pylint: disable=import-outside-toplevel
    from s2auth.server import setup as setup_s2auth_server  # pylint: disable=import-outside-toplevel
    from s2auth.server.context import ReadOnlyAuthenticationContext, ReadOnlyPairingAttemptContext  # pylint: disable=import-outside-toplevel
    from s2auth.server.hooks import pairing_attempt_request, register_hook  # pylint: disable=import-outside-toplevel
    from s2auth.server.settings import settings as get_settings  # pylint: disable=import-outside-toplevel
    from s2auth.server.token_manager import prime_default_pairing_token, set_pending_pairing_token  # pylint: disable=import-outside-toplevel

    # Report every pairing attempt the server sees (and allow it, as the default hook does).
    @register_hook(pairing_attempt_request)
    @inject
    async def report_pairing_attempt(authentication_context: ReadOnlyAuthenticationContext,
                                     pairing_context: ReadOnlyPairingAttemptContext) -> bool:
        emit("pairing_requested",
             client_node_id=str(authentication_context.client_node_id),
             pairing_token=pairing_context.pairing_token,
             pairing_attempt_id=str(pairing_context.pairing_attempt_id))
        return True

    setup_s2auth_server(additional_hook_modules=["s2auth.reference.server.hooks"])
    server_settings = get_settings()
    prime_default_pairing_token(server_settings)

    app = FastAPI(version=getattr(s2auth, "__version__", "unknown"), title="s2auth interop server")

    @app.middleware("http")
    async def report_requests(request: Request, call_next):  # type: ignore[no-untyped-def]
        response = await call_next(request)
        emit("http", method=request.method, path=request.url.path, status=response.status_code)
        return response

    app.include_router(pairing_router, prefix="/pairing")
    app.include_router(connection_router, prefix="/connection")

    config = uvicorn.Config(app,
                            host=args.host,
                            port=args.port,
                            ssl_certfile=args.cert,
                            ssl_keyfile=args.key,
                            log_level="debug" if args.verbose else "info",
                            lifespan="off")
    server = uvicorn.Server(config)
    serving = asyncio.create_task(server.serve())

    while not server.started:
        if serving.done():
            serving.result()
            raise RuntimeError("uvicorn stopped before it started listening")
        await asyncio.sleep(0.05)

    base = f"https://{args.public_host}:{args.port}"
    emit("listening", port=args.port, pairing_url=f"{base}/pairing/", connection_url=f"{base}/connection/", pairing_node_id=args.pairing_node_id)

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
            if name == "set_token":
                token = str(command["token"])
                set_pending_pairing_token(token, ttl_seconds=int(command.get("ttl", args.token_ttl)))
                emit("token_set", token=token)
            elif name == "stop":
                break
            else:
                emit("error", operation=name, type="UnknownCommand", message=f"unknown command '{name}'")
        except Exception as error:  # pylint: disable=broad-exception-caught
            emit("error", operation=name, type=type(error).__name__, message=describe(error))

    server.should_exit = True
    try:
        await asyncio.wait_for(serving, timeout=5)
    except Exception:  # pylint: disable=broad-exception-caught
        pass


def main() -> None:
    parser = argparse.ArgumentParser(description="s2auth reference server driver")
    parser.add_argument("--host", default="127.0.0.1", help="the address to bind")
    parser.add_argument("--public-host", default="localhost", help="the host name clients use (reported in the listening event)")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--cert", required=True, help="the PEM certificate (chain) file")
    parser.add_argument("--key", required=True, help="the PEM private key file")
    parser.add_argument("--domain", default="localhost", help="the domain name of the WAN challenge-response")
    parser.add_argument("--pairing-node-id", default="S2AUTHCEM1", help="the alias of the pairing node (8 to 12 characters)")
    parser.add_argument("--server-node-id", required=True, help="the UUID of the server node (the CEM)")
    parser.add_argument("--token", default=None, help="the one-time pairing token for the first unknown client")
    parser.add_argument("--token-ttl", type=int, default=300)
    parser.add_argument("--deployment", default="LAN", choices=["LAN", "WAN"], help="the deployment assumed when the client sends none")
    parser.add_argument("--s2-version", dest="s2_versions", action="append", default=None, help="a supported S2 message version (repeatable, default: v1)")
    parser.add_argument("--brand", default="GraphDefined")
    parser.add_argument("--node-type", default="EMS")
    parser.add_argument("--model-name", default="s2auth interop server")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    if args.s2_versions is None:
        args.s2_versions = ["v1"]

    try:
        asyncio.run(run(args))
    except Exception as error:  # pylint: disable=broad-exception-caught
        emit("error", operation="run", type=type(error).__name__, message=describe(error))
        finish(1)
    finish(0)


if __name__ == "__main__":
    main()
