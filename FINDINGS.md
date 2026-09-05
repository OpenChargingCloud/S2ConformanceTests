# Findings

What the tests of WWCP S2 against s2-python, s2-rust and s2auth, against the S2 documentation
and — on Linux — against its own foundation Hermod found, with the test that reproduces each
finding, the place upstream and the consequence for WWCP S2. Every finding of the "Known
issues" kind is marked in its test with `Interop.KnownIssue(...)`: the test ends with a warning
(the test adapter shows it as skipped) while the issue exists and fails once the upstream
behaviour changes, so that the marker gets removed and the fix recorded here.

State on 2026-09-06: WWCP S2 `cc6f59b`, s2-python v0.10.0 (`ea46bde`), s2-rust `afedaa4`
(2026-09, after 0.3.0), s2auth v0.1.0, s2-json v1.0.0 (`d58b2f0`), s2-connect v1.0
(`d434762`), s2-documentation `0ba4f5c`; run on Windows 11 with the S2 Connect drivers of
s2-rust inside WSL. 128 tests: 108 passed, 20 known issues, 0 failed. On Linux the four
tests of the "WWCP RM → s2-python CEM" fixture are known issues as well (H1, H2).

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
* S2 Connect 1.0.0, session initiation (the same document, and the OpenAPI files pinned in
  `libs/s2-connect/openapi`): the connection details of the pairing carry

  > `initiateSessionUrl` | The base URL for the connection process (does not include the version number)

  and session initiation is `POST /[version]/initiateSession` followed by
  `POST /[version]/confirmAccessToken`, both with the access token as bearer authorization
  (`securitySchemes.accessToken: http, bearer`); `confirmAccessToken` is answered with 200 and
  the `WebSocketCommunicationDetails` (`communicationProtocol`, `websocketToken`,
  `websocketUrl`), `unpair` with 204.
* RFC 7230 §3: an HTTP request line ends with CRLF (a recipient MAY accept a bare LF; a
  sender must not rely on it).

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

## s2auth 0.1.0

The Python implementation of S2 Connect pairing and session initiation (a client library and
a FastAPI reference server; no S2 communication server). Tested through the drivers of
`tools/s2auth-harness`: as an RM pairing client against the WWCP S2 pairing server, and with
the WWCP S2 clients against its reference server.

### Known issues

| # | Finding | Test | Where in s2auth |
|---|---|---|---|
| A1 | The client posts `initiateSession` (and `unpair`) to its *pairing* URL instead of the `initiateSessionUrl` it received in the connection details. Against a WWCP S2 CEM, whose session initiation API is served under `/connection/`, it gets 404 from the pairing API; and since Hermod does not mount two APIs under one path, an s2auth client cannot initiate a session with WWCP S2 at all. | `S2AuthSessionInitiationTests.S2AuthClient_InitiatesASessionWithTheWWCPServer` | `src/s2auth/client/pairing_core.py` (`connect`, `unpair`: `f'{pairing_uri}/initiateSession'` with `pairing_uri = details["pairing_server_url"]`) |
| A2 | The client uses the pairing *target* as the server node identification and never stores the `serverNodeDescription.id` it received. After pairing by alias, `connect` fails with `ValueError` (`UUID("CEM1")`) before any request is made. | `S2AuthSessionInitiationTests.S2AuthClient_PairedByAlias_CannotInitiateASession` | `pairing_core.py` (`s2_node_id = pairing_s2_node_id …`; `connect`: `NodeId(UUID(pairing_s2_node_id))`) |
| A3 | The connection initiation API of the reference server, announced as `initiateSessionUrl` (`…/connection/`), has no `initiateSession` operation: the operation is called `initiateConnection` and reads the access token from a header named `accessToken` instead of the bearer authorization. The WWCP S2 `SessionInitiationClient` gets 404 at the announced URL. | `S2AuthSessionInitiationTests.WWCPClient_InitiatesASessionWithTheS2AuthServer` | `src/s2auth/reference/server/connection.py` (`/{version}/initiateConnection`, `Header(alias="accessToken")`) |
| A4 | The pairing API of the reference server does serve `initiateSession` with bearer authorization (and rotates the token), but its `confirmAccessToken` is a no-op: 200 without the `WebSocketCommunicationDetails`, and the pending token is never activated. Its `unpair` and `postConnectionDetails` are no-ops as well. | same test | `src/s2auth/reference/server/pairing.py` (`confirm_access_token`, `unpair`, `post_connection_details`: `pass`) |

### Behaviour WWCP S2 has to account for (no defect, no test marker)

* The reference server validates its own node identification as UUID **version 4**
  (`Settings.server_s2_node_id: UUID4`); WWCP S2 generates version 7 identifications
  (`Node_Id.NewRandom`). Client node identifications are plain UUIDs, so WWCP S2's ids are
  accepted in `requestPairing`; the tests give the s2auth server a version 4 id.
* The pairing token of the reference server is one-time (`DEFAULT_PAIRING_TOKEN`, consumed by
  the first unknown client, TTL 300 s); the driver has a `set_token` command for further
  attempts. The alias of its pairing node must have 8 to 12 characters, and the target a
  client names (`nodeId`/`nodeIdAlias`) is not checked against it.
* `supported_s2_versions` defaults to `["v1"]` on both sides; the tests pass WWCP S2's
  `v1.0.0`, otherwise `initiateSession` ends with `IncompatibleS2MessageVersions`.
* The client persists its pairing state in SQLite through SQLAlchemy by default; the driver
  substitutes an in-memory `ConnectionStore`. The reference `server` entry point binds port
  8000 with auto-reload and reads its console for a key press; the driver assembles the
  same FastAPI application itself.

### Confirmed to work

* Pairing in both directions and both deployments (`S2AuthPairingTests`): the s2auth RM
  client against the WWCP S2 pairing server (by alias and by node id) and the WWCP S2
  `PairingClient` against the s2auth reference server. The LAN formula with the SHA-256
  fingerprint of the leaf certificate (s2auth takes it from the TLS connection, WWCP S2 from
  its endpoint) and the WAN formula with the domain name agree, both sides end with the same
  access token, the communication roles are right, and s2auth receives WWCP S2's
  `initiateSessionUrl`.
* Wrong pairing tokens are detected on both sides at the first challenge response: s2auth
  stops after `requestPairing` with `VerificationError`; the WWCP S2 client never requests
  the connection details and reports the failure with `finalizePairing`.
* `initiateSession` at the pairing API of the reference server accepts WWCP S2's bearer
  token and answers with a rotated token and the negotiated version (the step before A4).

## The S2 documentation (structured-documentation, main)

`libs/s2-documentation/structured-documentation/*.toml` documents the data model, one file
per message, object and enumeration, and is the source the data model reference of the
website (and s2-rust) is generated from. Held against the S2 JSON v1.0.0 schemas and WWCP
S2's types (`S2DocumentationTests`):

| # | Finding | Test |
|---|---|---|
| D1 | There is no entry for the v1.0.0 message `DDBC.PresentDemandStatus`. | `EveryDocumentedMessage_HasASchemaAndASample` |
| D2 | `DDBC.SystemDescription` is documented with a `present_demand_rate` field that v1.0.0 does not have — the older shape s2-python (P2) and s2-rust (R6) implement. | `DocumentedFields_AreTheSchemaProperties` |
| D3 | `RevokableObjects` lists `DDBC.AverageDemandRateForecast`, `FRBC.FillLevelTargetProfile`, `FRBC.LeakageBehaviour` and `FRBC.UsageForecast`, which the v1.0.0 enumeration does not have. | same |
| D4 | `DDBC.ActuatorDescription`, an object inside `DDBC.SystemDescription`, carries a `sent_by` entry as if it were a message. | `EveryDocumentedMessage_HasASchemaAndASample` |

Everything else agrees: the 36 messages, the fields and their optionality of every documented
object, the values of every other enumeration; and WWCP S2 accepts every documented
enumeration value the schemas have.

## Hermod (the WebSocket client on Linux)

Found by running the suite on Linux (the Debian leg of the CI, reproduced in WSL); Hermod is a
submodule here and is fixed in its own repository. Until then the affected tests carry a
marker that is evaluated on Linux only.

| # | Finding | Test | Where in Hermod |
|---|---|---|---|
| H1 | `WebSocketClient.Connect` waits for the whole request timeout (10 minutes by default) when its networking task ends without a response: after an exception it closes the connection, leaves the loop (no reconnect policy) and never assigns `waitingForHTTPResponse`, so the caller polls until the timeout and gets a synthetic 400 "Timeout of … seconds reached!!!". Every failed upgrade costs the full timeout; the fixtures pass a 10 s timeout for that reason. | `PythonCEMAgainstWWCPRMTests` (all tests, Linux) | `HTTP1/WebSocket/Client/WebSocketClient.cs` (`Connect`: the `while (waitingForHTTPResponse is null && ts + RequestTimeout > Timestamp.Now)` loop after the `Task.Run`) |
| H2 | `HTTPRequest.Builder` joins the request line and the header fields with `Environment.NewLine`, so on Linux the upgrade request starts with `GET / HTTP/1.1\n` — a bare line feed where RFC 7230 requires CRLF (the header fields keep their CRLF). s2-python's `websockets` server rejects it ("line without CRLF") and closes the connection; with H1 the client then waits for its timeout. **Consequence for WWCP S2**: `S2WebSocketClient` (and every Hermod client request built through the builder) cannot connect to a strict server from Linux; the WWCP S2 → s2-python sessions of the suite are known issues on Linux until Hermod is fixed. | `PythonCEMAgainstWWCPRMTests` (all tests, Linux) | `HTTP1/Request/HTTPRequestBuilder.cs` (`EntireRequestHeader => $"{HTTPRequestLine}{Environment.NewLine}{ConstructedHTTPHeader}"`) |

## Keeping this file current

When a submodule is bumped, run the suite: a known-issue test that turns into a failure means
the upstream behaviour changed; remove its `Interop.KnownIssue` marker, move the finding to
"Confirmed to work" here and note the version. The `upstream-drift.yml` workflow runs the suite
against the upstream default branches of s2-python, s2-rust and s2auth, and the specification
tests against those of s2-json, s2-connect and s2-documentation, every night, so such a change
shows up there first, before any pin is bumped. H1 and H2 are evaluated on Linux only: the
markers fail there once Hermod is fixed and its pin bumped.
