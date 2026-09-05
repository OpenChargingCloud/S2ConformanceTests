# Findings

What the interoperability tests of WWCP S2 against s2-python and s2-rust found, with the test
that reproduces each finding, the place in the reference implementation and the consequence for
WWCP S2. Every finding of the "Known issues" kind is marked in its test with
`Interop.KnownIssue(...)`: the test ends with a warning (the test adapter shows it as skipped)
while the issue exists and fails once the upstream behaviour changes, so that the marker gets
removed and the fix recorded here.

State on 2026-09-05: WWCP S2 `cc6f59b`, s2-python v0.10.0 (`ea46bde`), s2-rust `afedaa4`
(2026-09, after 0.3.0), run on Windows 11 with the S2 Connect drivers of s2-rust inside WSL.
110 tests: 95 passed, 15 known issues, 0 failed.

## The specification texts the tests hold the implementations to

* S2 JSON v1.0.0: the schemas embedded in WWCP S2 (`libs/WWCP_S2/WWCP_S2/Schemas/S2JSON/v1.0.0`)
  and the semantic rules of their descriptions (PLAN.md §3.3 of WWCP S2).
* S2 Connect 1.0.0, "Challenge response process"
  (`website/s2c_versioned_docs/version-1.0.0/discovery-pairing-authentication.md` in
  [flexiblepower/s2-documentation](https://github.com/flexiblepower/s2-documentation)):

  > When the pairing server is deployed in the LAN: `R = HMAC(C, T || F)`
  > When the pairing server is deployed in the WAN: `R = HMAC(C, T || D)`

  > SHA256 certificate fingerprints are encoded into a hexadecimal string, and must be decoded as
  > hexadecimal string before it can be used as input […]. The pairing token and domain name are
  > strings, which need to be converted into binary data using the ASCII table.

  with `T` the pairing token, `F` the SHA-256 fingerprint of the TLS server (leaf) certificate and
  `D` the domain name of the HTTPS server. Pairing tokens match `^[0-9a-zA-Z]{4,}$` (dynamic) and
  `^[0-9a-zA-Z]{6,}$` (static).

WWCP S2 follows both; the deviations below are on the side of the reference implementations,
except where marked as a consequence for WWCP S2.

## s2-python 0.10.0

### Known issues

| # | Finding | Test | Where in s2-python |
|---|---|---|---|
| P1 | `PPBCPowerSequenceContainerStatus.progress` is declared as `uuid.UUID` although the schema defines a `Duration` (milliseconds); every `PPBC.PowerProfileStatus` that carries a progress is rejected with "UUID input should be a string, bytes or UUID object". | `PythonMessageRoundTripTests.EveryMessage_SurvivesARoundTripThroughS2Python(PPBC.PowerProfileStatus)` | `src/s2python/ppbc/ppbc_power_sequence_container_status.py` |
| P2 | The DDBC models deviate from s2-json v1.0.0: `DDBCOperationMode` requires a field `id` next to the generated `Id` (the source comments "? Id vs id") and a *list* for `supply_range` instead of one `NumberRange`; `DDBCSystemDescription` requires a `present_demand_rate` field that v1.0.0 does not define. A v1.0.0 `DDBC.SystemDescription` is therefore rejected with three validation errors. | `…(DDBC.SystemDescription)` | `src/s2python/ddbc/ddbc_operation_mode.py`, `src/s2python/generated/gen_s2.py` (`DDBCSystemDescription`) |
| P3 | The message `DDBC.PresentDemandStatus` does not exist ("Unable to parse DDBC.PresentDemandStatus as an S2 message. Type unknown."), so an RM cannot report its present demand to an s2-python CEM. | `…(DDBC.PresentDemandStatus)` | `src/s2python/s2_parser.py` (`TYPE_TO_MESSAGE_CLASS`) |
| P4 | The range of `operation_mode_factor` (0 to 1, schema description) is not enforced; an `FRBC.Instruction` with factor 1.5 is accepted. WWCP S2 rejects it. | `PythonMessageRoundTripTests.OperationModeFactorAboveOne_IsRejectedByWWCP_ButAcceptedByS2Python_KnownIssue` | `src/s2python/generated/gen_s2.py` (no `ge`/`le` constraint) |

P2 and P3 look like the models were generated from an older draft in which the demand rate was
part of the system description; s2-rust shows the same shape (R6).

### Behaviour WWCP S2 has to account for (no defect, no test marker)

* A message without a registered handler gets **no** `ReceptionStatus` (logged as "Received an
  event of type … but no handler is registered. Ignoring the event."), and a sender waiting for a
  `ReceptionStatus` gives up after 5 s and stops the whole connection
  (`S2AsyncConnection.send_msg_and_await_reception_status`). WWCP S2 therefore does not close a
  session on a missing `ReceptionStatus` by default (`S2SessionOptions.CloseOnReceptionTimeout`)
  and answers every message within the pipeline. The drivers register handlers for
  `SessionRequest` and `RevokeObject`, which s2-python's `ResourceManagerHandler` does not.
* When the peer closes the socket right after a message, s2-python can lose that message: the
  receive loop ends, `run()` cancels the handler task before it processed the queued message. The
  test of the no-common-version case (`PythonRMAgainstWWCPCEMTests.NoCommonVersion_TheCEMTerminatesTheSession`)
  takes its evidence from either side because of this.
* The identifiers are `uuid.UUID` fields and timestamps are `AwareDatetime`, so non-UUID ids and
  naive timestamps are rejected although the ID schema only requires `[a-zA-Z0-9\-_:]{2,64}`.
  WWCP S2 offers the same strictness through `S2ParserOptions.Strict` (`RequireUUIDs`,
  `RejectNaiveTimestamps`); the tests confirm that both reject the same inputs.
* The announced protocol version is `0.0.2-beta` (`s2python/version.py`); WWCP S2 treats it as a
  legacy alias of the v1.0.0 message set and selects it (PLAN.md D10).
* Python 3.14 is not supported (`requires-python = ">=3.9, < 3.14"`); the harness creates its
  venv from the newest 3.9–3.13 interpreter it finds.

### Confirmed to work

* 33 of the 36 messages survive a parse/re-serialise round trip through s2-python and are equal
  to the original after WWCP S2's strict parse (all but P1–P3).
* Both implementations reject non-UUID identifiers and naive timestamps (WWCP S2 in strict mode),
  unknown message types, missing and additional properties, unknown enumeration values and
  inverted ranges.
* Plain S2-JSON-over-WebSocket sessions in both directions
  (`PythonRMAgainstWWCPCEMTests`, `PythonCEMAgainstWWCPRMTests`): the `Handshake` exchange with
  the selection of `0.0.2-beta`, of `v1.0.0` when the s2-python CEM accepts it, and the
  `SessionRequest TERMINATE` without a common version; `ResourceManagerDetails`,
  `SelectControlType`, the FRBC system description, storage and actuator status, an
  `FRBC.Instruction` with its `InstructionStatusUpdate`, `SessionRequest TERMINATE`; exactly one
  `ReceptionStatus` per message; every message on the wire schema valid.
* The `ReceptionStatus` rules of WWCP S2 seen from s2-python: `INVALID_DATA` with the null
  message id for non-JSON, `INVALID_MESSAGE` for an unknown message type, `INVALID_CONTENT` for a
  control-type message before a control type was selected (the session survives), and 401 for a
  wrong bearer token.

## s2-rust (main, 2026-09)

### Known issues

| # | Finding | Test | Where in s2-rust |
|---|---|---|---|
| R1 | The pairing token text is decoded as standard Base64 before it enters the HMAC (`PairingToken::from_str`, `Display` re-encodes), instead of being used as the ASCII bytes of the text the specification prescribes. The responses of both sides differ for every token; the WWCP S2 pairing client ends the attempt in step 4 with `ChallengeResponseMismatch`. | `RustChallengeResponseTests.LAN_S2RustDecodesTheTokenAsBase64_KnownIssue`, `RustPairingTests.WWCPPairingClient_AgainstS2RustBase64TokenDecoding_KnownIssue` | `s2energy-connection/src/pairing/server.rs` (`impl FromStr for PairingToken`), `pairing/mod.rs` (`HmacChallenge::sha256`) |
| R2 | As a consequence of R1, a token whose length is not a multiple of four (e.g. a six character dynamic token, valid per `^[0-9a-zA-Z]{4,}$`) cannot be parsed at all (Base64 padding). | `RustChallengeResponseTests.DynamicTokenOfSixCharacters_IsNotParsableByS2Rust_KnownIssue` | same |
| R3 | WAN pairing servers compute `R = HMAC(C, T)`; the domain name `D` of the formula `R = HMAC(C, T \|\| D)` is left out. | `RustChallengeResponseTests.WAN_S2RustOmitsTheDomainName_KnownIssue`, `RustPairingTests.WWCPPairingClient_AgainstS2RustWANFormula_KnownIssue` | `pairing/mod.rs` (`Network::Wan => mac.update(pairing_token)`) |
| R4 | The pairing client selects the challenge-response formula solely from the host of the pairing URL: a host ending with `.local` gets the LAN formula, anything else, IP addresses included, the WAN formula. The deployment the server announces in `serverEndpointDescription.deployment` is not used. A LAN server addressed by IP therefore fails with `InvalidToken`. | no dedicated test; the reverse-direction tests address the WWCP S2 endpoint as `wwcp-cem.local` because of it | `pairing/client.rs` (`base_url.domain() … ends_with(".local")`) |
| R5 | For `.local` pairing URLs the client verifies TLS with its own verifier that takes the *last certificate the server transmits* as the trust anchor (trust on first use) and has no anchor at all when the server sends only its leaf: the TLS handshake then fails. Hermod's TLS server sends only the leaf (the D13 spike of WWCP S2), so the LAN pairing s2-rust → WWCP S2 cannot complete today. **Consequence for WWCP S2**: to serve s2-rust LAN pairing clients, Hermod would have to transmit the self-signed certificate as its own chain element (or a real root). | `RustPairingTests.S2RustPairingClient_PairsWithTheWWCPPairingServer_LAN_KnownIssue`, `RustPairingTests.S2RustPairingClient_AgainstTheWWCPSelfSignedLANCertificate_KnownIssue` | `pairing/transport.rs` (`HashingCertificateVerifier`: `intermediates.last().unwrap_or(&fallback)`); the same pattern in `communication/transport.rs` (`HashedCertificateVerifier`) when a `certificate_hash` is pinned |
| R6 | The `Message` enum of `s2energy-messaging` has no `DDBC.PresentDemandStatus` variant, and its `DDBC.SystemDescription` requires a `present_demand_rate` field that v1.0.0 does not define (the same older shape as s2-python's P2/P3). | `RustMessageRoundTripTests.EveryMessage_SurvivesARoundTripThroughS2Rust(DDBC.SystemDescription)`, `…(DDBC.PresentDemandStatus)` | `s2energy-messaging/src/s2.schema.json` |
| R7 | Semantic rules of the schema descriptions are not enforced: `start_of_range ≤ end_of_range` (an inverted `fill_level_range` is accepted) and `operation_mode_factor` in 0..1 (typify generates no numeric bounds). WWCP S2 rejects both. | `RustMessageRoundTripTests.InvertedRange_IsRejectedByWWCP_ButAcceptedByS2Rust_KnownIssue`, `…OperationModeFactorAboveOne_IsRejectedByWWCP_ButAcceptedByS2Rust_KnownIssue` | generated types of `s2energy-messaging` |

### Build and platform constraints (harness, no test marker)

* `s2energy-connection` only builds on Unix: `pairing/server.rs` uses
  `tokio::net::unix::pipe::Receiver`. On Windows the harness builds and runs the S2 Connect drivers
  inside WSL; the message-layer driver (`s2energy-messaging`) builds natively everywhere.
* Its mDNS dependency `zeroconf-tokio` binds Avahi (Linux, needs `libavahi-client-dev`) or
  Bonjour (`bonjour-sys`, needs libclang for bindgen and the Apple Bonjour SDK on Windows). The
  harness replaces `zeroconf-tokio` with a stub (`tools/s2-rust-harness/stubs/zeroconf-tokio`)
  through `[patch.crates-io]`; discovery is not under test.
* The announced S2 version is `0.0.2-beta` (`s2energy_messaging::s2_schema_version()`), parsed
  as semver; `initialize_as_rm` checks the CEM's `selected_protocol_version` as a semver
  requirement, which a value like `v1.0.0` would not satisfy. Not exercised: the S2 Connect
  sessions of the tests carry no `Handshake`.

### Observation from reading the code (not tested)

* `S2Connection::initialize_as_rm` treats a `ReceptionStatus` that arrives before the CEM's
  `Handshake` as an unexpected message and fails the handshake, although the RM's own `Handshake`
  is normally acknowledged first. WWCP S2's plain-mode CEM sends its `Handshake` at connect time,
  which makes the usual order harmless, but the plain mode is not part of the s2-rust tests
  (s2-rust has no plain WebSocket transport any more).

### Confirmed to work

* 34 of the 36 messages survive a round trip through the serde data model of s2-rust (all but
  R6). Both implementations reject non-UUID identifiers, naive timestamps, unknown message types,
  missing and additional properties and unknown enumeration values.
* The HMAC-SHA256 core of the challenge-response function is identical once both sides use the
  ASCII bytes of the token (`RustChallengeResponseTests.LAN_WithASCIITokenBytes_TheHMACCoresAgree`).
  The drivers hand s2-rust the ASCII bytes through its `PairingToken(Box<[u8]>)` constructor.
* LAN pairing of the WWCP S2 `PairingClient` with the s2-rust pairing server over TLS
  (`RustPairingTests.WWCPPairingClient_PairsWithTheS2RustPairingServer_LAN`): both sides end with
  the same access token, the communication roles are right, the `initiateSessionUrl` is stored,
  and the `certificateFingerprint` s2-rust announces is the fingerprint of its self-signed
  certificate, which WWCP S2 stores for pinning.
* Session initiation with access token rotation, the WebSocket session and S2 messages on it
  (`ResourceManagerDetails`, `SelectControlType`, the FRBC system description, an
  `FRBC.Instruction` and its `InstructionStatusUpdate`), and unpairing in **both** directions
  (`RustSessionInitiationTests`): the WWCP S2 `SessionInitiationClient` against the s2-rust
  communication server, and the s2-rust communication client against the WWCP S2
  `SessionInitiationServerAPI` and `S2WebSocketServer`, the latter with the real self-signed LAN
  certificate WWCP S2 issues (`SelfSignedCA`), which rustls accepts as an end entity because
  Hermod sets no CA bit on server certificates. (A certificate with BasicConstraints CA:TRUE is
  rejected by rustls with `CaUsedAsEndEntity`, as an earlier harness certificate showed.)
* Every message on the wire in those sessions is schema valid and gets exactly one
  `ReceptionStatus`.

## Keeping this file current

When a submodule is bumped, run the suite: a known-issue test that turns into a failure means
the upstream behaviour changed; remove its `Interop.KnownIssue` marker, move the finding to
"Confirmed to work" here and note the version. The `upstream-drift.yml` workflow runs the suite
against the upstream default branches of s2-python and s2-rust every night, so such a change
shows up there first, before any pin is bumped.
