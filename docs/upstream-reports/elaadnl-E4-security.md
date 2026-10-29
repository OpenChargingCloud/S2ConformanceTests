# Draft — PRIVATE security report for ElaadNL/resflex-open-s2-hems-connector

> **Do not open this as a public GitHub issue.** The library's `SECURITY.md` asks for private
> disclosure: GitHub → *Security* → *Report a vulnerability* on the
> `ElaadNL/resflex-open-s2-hems-connector` repository, or email `foss@elaad.nl`.
> This file is an unsent draft kept in the S2ConformanceTests repository.

Reproduced against `s2_cem_lib` at `v1.0.0` (`c12c857`). Line numbers refer to
`s2_cem_lib/pairing/views.py` at that tag.

---

**Summary:** An unauthenticated `requestPairing` evicts an already-established pairing. Because the
pairing server mutates and persists state before it verifies knowledge of the pairing token, a
party that knows only a Resource Manager's node id — which the CEM itself advertises — can
terminate that Resource Manager's live S2 session and lock it out, without ever proving it knows
the token. This is a remote denial of service against an established pairing.

**Severity (suggested):** Medium. Network access to the CEM's pairing API is required, no secret is
needed, and the impact is availability (loss of an established control channel), not a takeover.

### Root cause

In S2 Connect, the HMAC challenge-response is what proves the pairing client knows the token. In
`s2_cem_lib` that proof is verified only in `RequestConnectionDetailsView` (line 319). But
`RequestPairingView.post`, which runs earlier, already performs all the destructive work:

```python
# views.py, RequestPairingView.post
# Re-pairing: clean up stale connections
if client_node_id in server.paired_nodes:                        # line 241
    server.pending_tokens = [...]
    old_ws = server.active_connections.pop(client_node_id, None) # line 243
    if old_ws is not None:
        await old_ws.close()                                     # line 245  — cuts the live session

access_token = Token.access_token()
node = _PairedNode(access_token, ...)
server.paired_nodes[client_node_id] = node                       # line 255  — replaces the pairing
await save_paired_nodes(ctx.persistence, server)                 # line 256  — and persists it
```

The `client_node_id` comes from `clientNodeDescription.id` in the request body. The CEM publishes
its paired/known node descriptions over the pairing API (e.g. `GET /pairing/nodes`), so an attacker
on the network does not have to guess it.

### Steps to reproduce

1. A legitimate Resource Manager pairs with the CEM and opens an S2 session (WebSocket connected,
   access token rotated and persisted).
2. A second client sends `POST /pairing/<version>/requestPairing` with a `clientNodeDescription.id`
   equal to the legitimate RM's node id, a well-formed `clientEndpointDescription`, compatible
   versions/protocols, and an **arbitrary** `clientHmacChallenge`. It does not know the pairing
   token.
3. The CEM answers **200**. In doing so it closes the legitimate RM's WebSocket (line 245) and
   replaces and persists a new access token for that node id (lines 255–256).
4. The legitimate RM, still holding its own token, can no longer start a session: `initiateSession`
   now returns **401**.

The attacker itself cannot finish pairing (it fails the HMAC check at `requestConnectionDetails`),
so this is denial of service against the victim, not impersonation — but the victim's established
pairing and session are already gone by then.

We reproduce this in an automated conformance test (WWCP S2 against `s2_cem_lib`):
`ResflexCEMTests.ResflexCEM_UnauthenticatedRequestPairingEvictsAnEstablishedPairing`.

### Suggested direction for a fix

Do not let `requestPairing` tear down or overwrite an existing, established pairing until the new
attempt has proven token knowledge. Options, roughly in order of preference:

- Treat a `requestPairing` for a `client_node_id` that is already paired as a *new, isolated*
  attempt: build the candidate node and challenge in the pairing-attempt state only, and promote it
  to `paired_nodes` / persistence (and close the old WebSocket) **after** the HMAC response is
  verified in `requestConnectionDetails`.
- Until then, keep the existing pairing and its live connection untouched, so a failed or forged
  attempt cannot affect them.

We are happy to share full request/response traces and to re-test a candidate fix against the
conformance suite.
