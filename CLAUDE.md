# S2ConformanceTests

Conformance and interoperability test suite for the S2 stack in the `libs/WWCP_S2` submodule
(S2 JSON v1.0.0 messages, S2 Connect pairing and session initiation), run against the
reference implementations s2-python (`libs/s2-python`), s2-rust (`libs/s2-rust`) and s2auth
(`libs/s2auth`) in both roles and both directions, plus drift tests holding WWCP_S2's embedded
copies to the pinned normative material (`libs/s2-json`, `libs/s2-connect`,
`libs/s2-documentation`). Phase 11b of WWCP_S2's PLAN.md, carried here so that the library
does not have to.

## Orientation

- **What is proven, and how:** `README.md` — the "What is tested" matrix.
- **What the reference implementations get wrong:** `FINDINGS.md`. Every known issue is
  pinned by an `Interop.KnownIssue(description, stillPresent, detail)` marker in its test:
  a warning while the issue exists, a failure the moment upstream stops reproducing it, so
  that the marker gets removed and the fix recorded there.
- **The stack under test:** `libs/WWCP_S2/` — its own README, PLAN.md and `docs/` document
  the protocol and the design. It references Styx and Hermod as sibling directories, hence
  `libs/Styx` and `libs/Hermod`.
- **The drivers:** `tools/s2-python-harness/*.py`, `tools/s2auth-harness/*.py` and the cargo
  crate `tools/s2-rust-harness/`. Child processes speaking JSON lines on stdin/stdout,
  scripted by the tests through `ExternalProcess`; every message that crosses the wire on the
  WWCP side is validated against the embedded schemas by `TrafficRecorder`.
- **The drift tests:** `S2InteropTests/Specification/` (category `Specification`, no
  toolchain needed): embedded schemas and OpenAPI files versus the pins, THIRD-PARTY-NOTICES.md
  of WWCP_S2 versus the pinned commits, the structured documentation versus the schemas and
  WWCP_S2's types.

## Build & test

```
git submodule update --init --recursive
dotnet build S2InteropTests/S2InteropTests.csproj
dotnet test  S2InteropTests/S2InteropTests.csproj --filter "TestCategory=Interop|TestCategory=Specification"
```

Expected (2026-09-06, Windows): 128 tests, 108 passed, 20 known issues (warnings), 0 failed.
Prerequisites: .NET 10 SDK, Python 3.9–3.13, Rust. On Windows the S2 Connect drivers of
s2-rust build and run inside WSL (Debian with rustup; `s2energy-connection` is Unix-only),
and the "s2-rust client → WWCP server" tests additionally need the Windows firewall to admit
the test host from the WSL network. On Linux the "WWCP RM → s2-python CEM" fixture is a
known issue of Hermod (H1/H2 in FINDINGS.md). Fixtures skip with a reason when a toolchain is
missing; `S2_INTEROP_REQUIRE=1` (what the CI sets) makes that a failure. All `S2_INTEROP_*`
variables are listed in README.md.

## Ground rules

- WWCP_S2, Hermod and Styx are submodules: change them in their own repositories and bump
  the pin here. This repository holds tests, drivers and findings.
- `S2InteropTests/Directory.Build.props` and `.editorconfig` live inside the project
  directory on purpose: a root-level copy leaks into Styx and Hermod (no props of their own)
  and turns their warnings into errors.
- A known issue is a boolean handed to `Interop.KnownIssue(...)`, never a caught assertion
  (NUnit keeps a caught assertion failure on the result). A new finding goes into
  FINDINGS.md with the test, the upstream location and the consequence for WWCP S2.
- Never change wire behaviour of WWCP S2 because a reference implementation differs: check
  the specification text first (quoted in FINDINGS.md; the OpenAPI files are in
  `libs/s2-connect/openapi`, the schemas in `libs/s2-json`). The reference implementations
  have been wrong before (P1–P4, R1–R7, A1–A4), and so has the documentation (D1–D4).
- Hermod bugs found here (H1, H2) are fixed in Hermod, not worked around in WWCP_S2 or here;
  until then their tests carry a platform-conditional `Interop.KnownIssue` marker.
- The `connect` cargo feature gates `s2energy-connection`, `stubs/zeroconf-tokio` replaces
  the Avahi/Bonjour binding. Do not patch s2-rust to make the crate build on Windows.
- CI conventions follow the other OpenChargingCloud conformance suites (EEBUS, TOTP):
  `ci.yml` gates against the pins on Debian 13 and Windows, `nightly.yml` repeats the pinned
  suite, `upstream-drift.yml` moves the submodules first. Keep the three questions apart.
