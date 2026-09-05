# S2 Conformance & Interoperability Tests

[![CI](https://github.com/OpenChargingCloud/S2ConformanceTests/actions/workflows/ci.yml/badge.svg)](https://github.com/OpenChargingCloud/S2ConformanceTests/actions/workflows/ci.yml)
[![Nightly](https://github.com/OpenChargingCloud/S2ConformanceTests/actions/workflows/nightly.yml/badge.svg)](https://github.com/OpenChargingCloud/S2ConformanceTests/actions/workflows/nightly.yml)
[![Upstream drift](https://github.com/OpenChargingCloud/S2ConformanceTests/actions/workflows/upstream-drift.yml/badge.svg)](https://github.com/OpenChargingCloud/S2ConformanceTests/actions/workflows/upstream-drift.yml)

Conformance and interoperability test suite for the **S2 standard** (EN 50491-12-2: S2 JSON
v1.0.0 and S2 Connect 1.0.0), the protocol between customer energy managers (CEM) and resource
managers (RM) maintained by the [FlexiblePower Alliance Network](https://github.com/flexiblepower).

The suite tests [WWCP_S2](https://github.com/OpenChargingCloud/WWCP_S2) — the S2 stack in
C# / .NET developed alongside it — against the reference implementations
[s2-python](https://github.com/flexiblepower/s2-python),
[s2-rust](https://github.com/flexiblepower/s2-rust) and
[s2auth](https://github.com/flexiblepower/s2auth) in both roles and both directions, and
holds its embedded copies of the normative material (the S2 JSON schemas, the S2 Connect
OpenAPI files, the structured documentation of the data model) to the pinned upstream
sources. It is Phase 11b of the WWCP S2 development plan, moved out of the library
repository so that "does our stack interoperate" is a question this repository answers and
the library does not have to carry.

## What is in here

| Directory | Content |
|-----------|---------|
| `S2InteropTests/` | The NUnit test project. `Python/`, `Rust/` and `S2Auth/` hold the interoperability tests (category `Interop`, plus `s2-python`, `s2-rust` or `s2auth` per peer): message round trips, rejection of malformed messages, plain-mode WebSocket sessions, S2 Connect pairing and session initiation. `Specification/` holds the drift tests (category `Specification`): the schemas and OpenAPI files WWCP S2 embeds against the pinned upstream files, and the structured documentation against the schemas and WWCP S2's types. `Harness/` holds the driver control, the schema-validating traffic recorder and the known-issue marker. |
| `tools/s2-python-harness/` | The s2-python drivers: `s2_echo.py` (message round trips), `s2_rm.py` (an FRBC resource manager), `s2_cem.py` (a CEM WebSocket server). |
| `tools/s2-rust-harness/` | The s2-rust drivers, a cargo crate: `s2-json-echo`, `s2-hmac`, `s2-pairing-server`, `s2-pairing-client`, `s2-comm-server`, `s2-comm-client`. |
| `tools/s2auth-harness/` | The s2auth drivers: `s2auth_client.py` (the pairing client, reporting every HTTP exchange) and `s2auth_server.py` (the reference server behind uvicorn, on a port of the test's choosing). |
| [`FINDINGS.md`](FINDINGS.md) | What the tests found in s2-python, s2-rust, s2auth, the S2 documentation and Hermod: the reproducing test, the place upstream and the consequence for WWCP S2. |
| `libs/` | Git submodules: the stack under test, its foundations, the reference implementations and the normative material. |

The drivers are started as child processes and talk JSON lines on stdin/stdout, so the C#
tests can script the reference implementation and observe every message it sends and
receives. Every message that crosses the wire on the WWCP S2 side is validated against the
embedded s2-json v1.0.0 schemas (`TrafficRecorder`), and the tests check that each message
gets exactly one `ReceptionStatus`.

## Submodules

| Submodule | Purpose |
|-----------|---------|
| [`libs/WWCP_S2`](https://github.com/OpenChargingCloud/WWCP_S2) | **The stack under test**: S2 JSON messages, S2 Connect pairing, session initiation and the WebSocket transport in C#. Its `WWCP_S2Tests` project provides the schema validator and the `[S2C]` traceability attribute, and runs in the CI here as well. |
| [`libs/Styx`](https://github.com/Vanaheimr/Styx), [`libs/Hermod`](https://github.com/Vanaheimr/Hermod) | Foundations of the stack: helpers, networking (HTTP, WebSockets, TLS, PKI). WWCP S2 references them as sibling directories, which is why they have to be checked out. |
| [`libs/s2-python`](https://github.com/flexiblepower/s2-python) | Reference implementation #1 (v0.10.0): pydantic models and `S2AsyncConnection` over WebSockets in plain mode with the `Handshake` exchange. Installed into a virtual environment by the tests. |
| [`libs/s2-rust`](https://github.com/flexiblepower/s2-rust) | Reference implementation #2: the message layer (`s2energy-messaging`, generated from its own JSON schema) and S2 Connect pairing, session initiation and communication (`s2energy-connection`). Wrapped by the driver binaries. |
| [`libs/s2auth`](https://github.com/flexiblepower/s2auth) | Reference implementation #3 (v0.1.0): S2 Connect pairing and session initiation in Python (client library, FastAPI reference server), the HTTP part only. Installed into its own virtual environment by the tests. |
| [`libs/s2-json`](https://github.com/flexiblepower/s2-json) | **The normative S2 JSON schemas** (tag v1.0.0): 36 message and 41 type schemas. WWCP S2 embeds a copy; the drift tests compare the two. |
| [`libs/s2-connect`](https://github.com/flexiblepower/s2-connect) | **The normative S2 Connect OpenAPI files** (tag v1.0): pairing, session initiation, common types, WAN endpoint registry. WWCP S2 embeds a copy; the drift tests compare the two. |
| [`libs/s2-documentation`](https://github.com/flexiblepower/s2-documentation) | The S2 documentation website and its `structured-documentation/` (one TOML file per message, object and enumeration, the source of the data model reference), held against the schemas and WWCP S2's types. |

```bash
git clone --recurse-submodules https://github.com/OpenChargingCloud/S2ConformanceTests.git
```

## What is tested

| Area | s2-python | s2-rust | s2auth |
|---|---|---|---|
| All 36 S2 JSON messages: serialise with WWCP S2, parse and re-serialise with the reference implementation, parse back strictly, compare | yes | yes | – |
| Rejection of malformed messages (non-UUID identifiers, naive timestamps, unknown types, missing and additional properties, unknown enumeration values, semantic rules) | yes | yes | – |
| Plain-mode WebSocket session with the `Handshake` exchange (`0.0.2-beta` / `v1.0.0` selection, no common version), `ResourceManagerDetails`, `SelectControlType`, the FRBC system description and statuses, an instruction with its `InstructionStatusUpdate`, `SessionRequest TERMINATE` | WWCP CEM ↔ s2-python RM and WWCP RM ↔ s2-python CEM | – (s2-rust has no plain WebSocket transport any more) | – |
| `ReceptionStatus` rules: `INVALID_DATA` for non-JSON, `INVALID_MESSAGE` for unknown types, `INVALID_CONTENT` for out-of-state messages, bearer token rejection with 401 | yes | – | – |
| S2 Connect challenge-response function (LAN and WAN formulas, token encoding) | – | yes | – |
| S2 Connect pairing over TLS (both directions, wrong-token rejection) | – | yes (LAN) | yes (LAN and WAN) |
| S2 Connect session initiation with access token rotation, the WebSocket session and S2 messages on it, unpairing (both directions) | – | yes | HTTP part only (s2auth has no communication server); the known issues in FINDINGS.md block the rest |

And, without an external implementation (category `Specification`): the 77 S2 JSON schema
files and the 4 S2 Connect OpenAPI files WWCP S2 embeds are the pinned upstream files and the
upstream commits named in its third-party notices are the pinned ones; the sample messages
cover every message schema; the structured documentation of the data model names the same
messages, fields and enumeration values as the schemas; and every documented enumeration
value is accepted by the WWCP S2 type of that name.

## Findings

[FINDINGS.md](FINDINGS.md) lists what the tests found in s2-python 0.10.0, s2-rust, s2auth
0.1.0, the S2 documentation and Hermod, with the test that reproduces each finding, the place
upstream and the consequence for WWCP S2. State on 2026-09-06 (Windows): 128 tests, 108
passed, 20 known issues, 0 failed. Known issues are pinned in their tests with
`Interop.KnownIssue(...)`: they end with a warning while the issue exists and fail once the
upstream behaviour changes, so that the marker gets removed and the fix recorded.

## Building and testing

```bash
dotnet build S2InteropTests/S2InteropTests.csproj
```

```bash
dotnet test S2InteropTests/S2InteropTests.csproj --filter "TestCategory=Interop|TestCategory=Specification"
```

The interoperability tests start the reference implementations as external peers and
therefore need, besides the .NET 10 SDK, a Python 3.9–3.13 interpreter (s2-python, s2auth)
and a Rust toolchain (`cargo`, s2-rust). The first run creates `.venv-s2python/` from
`libs/s2-python` and `.venv-s2auth/` from `libs/s2auth`, and builds `tools/s2-rust-harness`.
Fixtures whose toolchain is missing are skipped with a message; set `S2_INTEROP_REQUIRE=1`
(as the CI does) to turn that into a failure. The specification tests need nothing but the
checked-out submodules.

### On Windows

The S2 Connect crate of s2-rust (`s2energy-connection`) only builds on Unix, so on Windows the
S2 Connect drivers are built and run inside a WSL distribution with Rust installed
(`curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal`).
For the "s2-rust client → WWCP server" tests WSL reaches the test host through the WSL
virtual network (the Windows firewall must admit the test host there) and the harness maps
the host name `wwcp-cem.local` to the Windows address in the distribution's `/etc/hosts` (as
root, which WSL grants without a password; WSL rewrites the file on restart). Both steps are
checked before each such test, which is skipped with a message when they are not possible.

### Environment variables

| Variable | Meaning |
|---|---|
| `S2_INTEROP_PYTHON` | a Python interpreter with `s2-python[ws]` installed (skips the venv) |
| `S2_INTEROP_BASE_PYTHON` | the interpreter the venvs are created from |
| `S2_INTEROP_VENV` | the s2-python venv directory (default `.venv-s2python`) |
| `S2_INTEROP_S2AUTH_PYTHON` | a Python interpreter with `s2auth[client,server]` installed (skips the venv) |
| `S2_INTEROP_S2AUTH_VENV` | the s2auth venv directory (default `.venv-s2auth`) |
| `S2_INTEROP_LOG_LEVEL` | the log level of WWCP S2 and Hermod in the test output (default `Warning`) |
| `S2_INTEROP_CARGO` | the cargo executable |
| `S2_INTEROP_WSL_DISTRO` | the WSL distribution for the S2 Connect drivers (default: the default distribution) |
| `S2_INTEROP_SKIP_CARGO_BUILD` | `1` uses the previously built drivers as they are |
| `S2_INTEROP_RUST_LOG` | the `RUST_LOG` filter of the drivers (default `info`) |
| `S2_INTEROP_REQUIRE` | `1` fails instead of skipping when a toolchain is missing |

### Continuous integration

`ci.yml` gates every push and pull request on two legs: Debian 13 (in a container, the
platform the stack runs on in production, and the only leg that can build
`s2energy-connection`, so it runs everything and treats a missing toolchain as a failure)
and Windows (where the S2 Connect fixtures skip for want of WSL; the s2-python tests and the
s2-rust message layer are the gate there). Both legs also run the WWCP S2 stack's own tests
against the submodule pins. `nightly.yml` repeats the pinned suite every night, for the
timing races two implementations over loopback can produce and for the toolchains that move
without a push (Rust stable, Python and .NET SDK patches). `upstream-drift.yml` moves the
submodules to their upstream default branches first — WWCP S2, Hermod and Styx in one job,
s2-python, s2-rust and s2auth in a second, the normative material (s2-json, s2-connect,
s2-documentation) in a third — so that a change upstream, including a known issue that stops
reproducing or a schema that moved, is reported the night it lands rather than at the next
pin bump. All three upload the `.trx` results, which keep the skip reasons and the known-issue
warnings the console log does not show.

## License

GNU Affero General Public License 3.0 or later, see
[WWCP_S2](https://github.com/OpenChargingCloud/WWCP_S2/blob/master/LICENSE). s2-python,
s2-rust, s2auth, s2-json, s2-connect and s2-documentation are Apache-2.0 licensed by the
FlexiblePower Alliance Network; see the submodules for their licenses.
