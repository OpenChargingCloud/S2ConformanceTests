# S2 Conformance Tests

Interoperability tests of [WWCP S2](https://github.com/OpenChargingCloud/WWCP_S2) (the C# / .NET
implementation of the **S2 standard** EN 50491-12-2 with S2 JSON v1.0.0 and S2 Connect 1.0.0)
against the two reference implementations of the FlexiblePower Alliance Network:

* [s2-python](https://github.com/flexiblepower/s2-python) – the S2 JSON message layer (pydantic
  models, `S2AsyncConnection` over WebSockets in plain mode with the `Handshake` exchange).
* [s2-rust](https://github.com/flexiblepower/s2-rust) – the S2 JSON message layer
  (`s2energy-messaging`, generated from its own JSON schema) and S2 Connect pairing, session
  initiation and WebSocket communication (`s2energy-connection`).

This repository is Phase 11b of the WWCP S2 development plan, moved out of the library
repository. Everything is consumed as git submodules:

| Path | Content |
|---|---|
| `libs/WWCP_S2` | the library under test (its `WWCP_S2Tests` project provides the schema validator and the `[S2C]` traceability attribute) |
| `libs/Styx`, `libs/Hermod` | the Vanaheimr libraries WWCP S2 is built on (referenced as sibling directories) |
| `libs/s2-python` | s2-python, installed into a virtual environment by the tests |
| `libs/s2-rust` | s2-rust, wrapped by small driver binaries |
| `S2InteropTests/` | the NUnit test project (category `Interop`, plus `s2-python` / `s2-rust`) |
| `tools/s2-python-harness/` | the s2-python drivers: `s2_echo.py` (message round trips), `s2_rm.py` (an FRBC resource manager), `s2_cem.py` (a CEM WebSocket server) |
| `tools/s2-rust-harness/` | the s2-rust drivers: `s2-json-echo`, `s2-hmac`, `s2-pairing-server`, `s2-pairing-client`, `s2-comm-server`, `s2-comm-client` |

The drivers are started as child processes and talk JSON lines on stdin/stdout, so the C# tests
can script the reference implementation and observe every message it sends and receives. Every
message that crosses the wire on the WWCP S2 side is validated against the embedded s2-json
v1.0.0 schemas (`TrafficRecorder`), and the tests check that each message gets exactly one
`ReceptionStatus`.

## What is tested

| Area | s2-python | s2-rust |
|---|---|---|
| All 36 S2 JSON messages: serialise with WWCP S2, parse and re-serialise with the reference implementation, parse back strictly, compare | yes | yes |
| Rejection of malformed messages (non-UUID identifiers, naive timestamps, unknown types, missing and additional properties, unknown enumeration values, semantic rules) | yes | yes |
| Plain-mode WebSocket session with the `Handshake` exchange (`0.0.2-beta` / `v1.0.0` selection, no common version), `ResourceManagerDetails`, `SelectControlType`, the FRBC system description and statuses, an instruction with its `InstructionStatusUpdate`, `SessionRequest TERMINATE` | WWCP CEM ↔ s2-python RM and WWCP RM ↔ s2-python CEM | – (s2-rust has no plain WebSocket transport any more) |
| `ReceptionStatus` rules: `INVALID_DATA` for non-JSON, `INVALID_MESSAGE` for unknown types, `INVALID_CONTENT` for out-of-state messages, bearer token rejection with 401 | yes | – |
| S2 Connect challenge-response function (LAN and WAN formulas, token encoding) | – | yes |
| S2 Connect pairing over TLS (LAN, both directions) | – | yes |
| S2 Connect session initiation with access token rotation, the WebSocket session and S2 messages on it, unpairing (both directions) | – | yes |

## Findings

[FINDINGS.md](FINDINGS.md) lists what the tests found in s2-python 0.10.0 and s2-rust, with the
test that reproduces each finding, the place in the reference implementation and the consequence
for WWCP S2. State on 2026-09-05: 110 tests, 95 passed, 15 known issues, 0 failed. Known issues
are marked in their tests with `Interop.KnownIssue(...)`: they end with a warning while the issue
exists and fail once the upstream behaviour changes, so that the marker gets removed and the fix
recorded.

## Running the tests

Prerequisites: .NET 10 SDK, a Python 3.9–3.13 interpreter, and Rust (`cargo`). On Windows the
S2 Connect drivers of s2-rust additionally need a WSL distribution with Rust installed
(`curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal`).
For the "s2-rust client → WWCP server" tests WSL reaches the test host through the WSL virtual
network (the Windows firewall must admit the test host there) and the harness maps the host
name `wwcp-cem.local` to the Windows address in the distribution's `/etc/hosts` (as root, which
WSL grants without a password; WSL rewrites the file on restart). Both steps are checked before
each such test, which is skipped with a message when they are not possible.

```bash
git clone --recurse-submodules https://github.com/OpenChargingCloud/S2ConformanceTests.git
cd S2ConformanceTests
dotnet build S2InteropTests/S2InteropTests.csproj
dotnet test  S2InteropTests/S2InteropTests.csproj --filter "TestCategory=Interop"
```

The first run creates `.venv-s2python/` from `libs/s2-python` and builds
`tools/s2-rust-harness` (natively, and inside WSL on Windows). Fixtures whose toolchain is
missing are skipped with a message; set `S2_INTEROP_REQUIRE=1` (as the CI does) to turn that
into a failure.

| Variable | Meaning |
|---|---|
| `S2_INTEROP_PYTHON` | a Python interpreter with `s2-python[ws]` installed (skips the venv) |
| `S2_INTEROP_BASE_PYTHON` | the interpreter the venv is created from |
| `S2_INTEROP_VENV` | the venv directory (default `.venv-s2python`) |
| `S2_INTEROP_CARGO` | the cargo executable |
| `S2_INTEROP_WSL_DISTRO` | the WSL distribution for the S2 Connect drivers (default: the default distribution) |
| `S2_INTEROP_SKIP_CARGO_BUILD` | `1` uses the previously built drivers as they are |
| `S2_INTEROP_RUST_LOG` | the `RUST_LOG` filter of the drivers (default `info`) |
| `S2_INTEROP_REQUIRE` | `1` fails instead of skipping when a toolchain is missing |

`.github/workflows/interop.yml` runs the suite on Linux (everything) and Windows (without the
WSL-only drivers) on every push and nightly.

## Licenses

The tests and drivers are released under the GNU Affero General Public License 3.0 or later, like
WWCP S2. s2-python and s2-rust are Apache-2.0 licensed by the FlexiblePower Alliance Network; see
the submodules for their licenses.
