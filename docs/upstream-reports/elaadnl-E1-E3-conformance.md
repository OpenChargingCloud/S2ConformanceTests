# Draft — public issue for ElaadNL/resflex-open-s2-hems-connector

Draft of a conformance issue for the ElaadNL S2 CEM library, to be filed at
<https://github.com/ElaadNL/resflex-open-s2-hems-connector/issues>. This file is a draft kept
in the S2ConformanceTests repository; it has **not** been submitted. E4 is tracked separately
(`elaadnl-E4-security.md`) and must go through private disclosure, not a public issue.

All three findings were reproduced against `s2_cem_lib` at `v1.0.0` (`c12c857`) by the WWCP S2
conformance suite, acting as an S2 Connect pairing client (Resource Manager, LAN). Line numbers
refer to `s2_cem_lib/pairing/views.py` at that tag.

---

**Title:** Pairing server deviates from S2 Connect 1.0.0 on failed and out-of-order pairing attempts

**Body:**

Hi, and thanks for publishing the library.

While testing an independent S2 Connect Resource Manager against `s2_cem_lib` (v1.0.0), we found
three deviations from the S2 Connect 1.0.0 pairing process, all in `s2_cem_lib/pairing/views.py`.
They share a theme: the pairing server commits and persists state before the pairing attempt has
actually succeeded. None of them require TLS or a specific transport to reproduce; the sequence of
pairing HTTP calls is enough.

### 1. A failed pairing attempt leaves the Resource Manager paired

`RequestPairingView.post` stores the client as a paired node and persists it **before** the
HMAC challenge-response is checked — that check only happens later, in
`RequestConnectionDetailsView`:

```python
# views.py, RequestPairingView.post
server.paired_nodes[client_node_id] = node          # line 255
await save_paired_nodes(ctx.persistence, server)     # line 256
```

`FinalizePairingView.post` then only deletes the pairing attempt; it never reads the `success`
field and never removes the paired node:

```python
# views.py, FinalizePairingView.post
if attempt_id in server.pairing_attempts:
    del server.pairing_attempts[attempt_id]          # line 408
return web.Response(status=204)                      # line 410
```

**Steps to reproduce.** Pair with a *wrong* pairing token. The client detects at
`requestConnectionDetails` that the server's HMAC response does not verify, stops, and reports the
failure with `finalizePairing` (`success: false`), as S2 Connect requires. The node nevertheless
remains in `server.paired_nodes` and in persistence.

**Expected.** S2 Connect 1.0.0, *Interruption of the process*: "any pairing attempt that is not
completed [...] (with success or not) is considered a failed attempt." A failed attempt must not
leave an established pairing behind.

### 2. The rotated access token from `requestConnectionDetails` is never persisted

`RequestConnectionDetailsView.post` issues the access token the Resource Manager will actually use
and replaces it on the in-memory node, but does not persist it:

```python
# views.py, RequestConnectionDetailsView.post
if node is not None:
    node.access_token = access_token                 # line 332  (no save_paired_nodes afterwards)
```

Only the earlier token from `requestPairing` (line 256) is on disk.

**Steps to reproduce.** Pair successfully, then restart the CEM before the first S2 session. The
Resource Manager presents the token it received in the connection details; the restarted CEM has
only the older token and answers `initiateSession` with 401. The pairing is effectively lost and
the Resource Manager is told to re-pair. (After a first successful session the rotated session
token *is* persisted, so the window is pairing → restart → first session.)

**Expected.** A completed pairing survives a restart; the token the client was handed in the
connection details is the one the server honours.

### 3. `finalizePairing` is accepted in the wrong order

`FinalizePairingView.post` parses the body but neither inspects `success` nor checks which requests
preceded it; it ends any attempt with 204.

**Steps to reproduce.** After `requestPairing`, call `finalizePairing` with `success: true`
*without* having called `requestConnectionDetails`. The server responds 204.

**Expected.** S2 Connect 1.0.0, *Invalid interactions*: "when it receives the requests in the wrong
order (e.g. `/finalizePairing` with `success = true` before calling `/requestConnectionDetails`) it
**must** respond with a status 400 and consider the pairing attempt as failed. The only exception
is receiving the same request twice." This is the exact example the specification gives.

---

Happy to provide the full request/response traces or a minimal reproduction if that helps. Finding
3 in particular is a small, well-specified fix; findings 1 and 2 are about committing/persisting
pairing state only once an attempt has genuinely completed.
