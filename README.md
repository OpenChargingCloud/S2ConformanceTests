# S2 Conformance & Interoperability Tests

[![CI](https://github.com/OpenChargingCloud/S2ConformanceTests/actions/workflows/ci.yml/badge.svg)](https://github.com/OpenChargingCloud/S2ConformanceTests/actions/workflows/ci.yml)
[![Nightly](https://github.com/OpenChargingCloud/S2ConformanceTests/actions/workflows/nightly.yml/badge.svg)](https://github.com/OpenChargingCloud/S2ConformanceTests/actions/workflows/nightly.yml)
[![Upstream drift](https://github.com/OpenChargingCloud/S2ConformanceTests/actions/workflows/upstream-drift.yml/badge.svg)](https://github.com/OpenChargingCloud/S2ConformanceTests/actions/workflows/upstream-drift.yml)

Interoperability test suite for the **S2 standard** (EN 50491-12-2: S2 JSON v1.0.0 and
S2 Connect 1.0.0), the protocol between customer energy managers (CEM) and resource
managers (RM) maintained by the [FlexiblePower Alliance Network](https://github.com/flexiblepower).

The suite tests [WWCP_S2](https://github.com/OpenChargingCloud/WWCP_S2) — the S2 stack in
C# / .NET developed alongside it — against the two reference implementations,
[s2-python](https://github.com/flexiblepower/s2-python) and
[s2-rust](https://github.com/flexiblepower/s2-rust), in both roles and both directions. It
is Phase 11b of the WWCP S2 development plan, moved out of the library repository so that
"does our stack interoperate" is a question this repository answers and the library does
not have to carry.

## What is in here

| Directory | Content |
|-----------|---------|
| `S2InteropTests/` | The NUnit test project (category `Interop`, plus `s2-python` / `s2-rust` per peer): message round trips, rejection of malformed messages, plain-mode WebSocket sessions, S2 Connect pairing and session initiation. `Harness/` holds the driver control, the schema-validating traffic recorder and the known-issue marker. |
| `tools/s2-python-harness/` | The s2-python drivers: `s2_echo.py` (message round trips), `s2_rm.py` (an FRBC resource manager), `s2_cem.py` (a CEM WebSocket server). |
| `tools/s2-rust-harness/` | The s2-rust drivers, a cargo crate: `s2-json-echo`, `s2-hmac`, `s2-pairing-server`, `s2-pairing-client`, `s2-comm-server`, `s2-comm-client`. |
| [`FINDINGS.md`](FINDINGS.md) | What the tests found in s2-python and s2-rust: the reproducing test, the place in the reference implementation and the consequence for WWCP S2. |
| `libs/` | Git submodules: the stack under test, its foundations, and the reference implementations. |

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

```bash
git clone --recurse-submodules https://github.com/OpenChargingCloud/S2ConformanceTests.git
```

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

[FINDINGS.md](FINDINGS.md) lists what the tests found in s2-python 0.10.0 and s2-rust, with
the test that reproduces each finding, the place in the reference implementation and the
consequence for WWCP S2. State on 2026-09-05: 110 tests, 95 passed, 15 known issues,
0 failed. Known issues are pinned in their tests with `Interop.KnownIssue(...)`: they end
with a warning while the issue exists and fail once the upstream behaviour changes, so that
the marker gets removed and the fix recorded.

## Building and testing

```bash
dotnet build S2InteropTests/S2InteropTests.csproj
```

```bash
dotnet test S2InteropTests/S2InteropTests.csproj --filter "TestCategory=Interop"
```

Everything in this repository is an interoperability test: the suite starts the reference
implementations as external peers and therefore needs, besides the .NET 10 SDK, a Python
3.9–3.13 interpreter for s2-python and a Rust toolchain (`cargo`) for s2-rust. The first run
creates `.venv-s2python/` from `libs/s2-python` and builds `tools/s2-rust-harness`. Fixtures
whose toolchain is missing are skipped with a message; set `S2_INTEROP_REQUIRE=1` (as the CI
does) to turn that into a failure.

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
| `S2_INTEROP_BASE_PYTHON` | the interpreter the venv is created from |
| `S2_INTEROP_VENV` | the venv directory (default `.venv-s2python`) |
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
s2-python and s2-rust in another — so that a change in a reference implementation, including
a known issue that stops reproducing, is reported the night it lands rather than at the next
pin bump. All three upload the `.trx` results, which keep the skip reasons and the known-issue
warnings the console log does not show.

## License

GNU Affero General Public License 3.0 or later, see
[WWCP_S2](https://github.com/OpenChargingCloud/WWCP_S2/blob/master/LICENSE). s2-python and
s2-rust are Apache-2.0 licensed by the FlexiblePower Alliance Network; see the submodules for
their licenses.
