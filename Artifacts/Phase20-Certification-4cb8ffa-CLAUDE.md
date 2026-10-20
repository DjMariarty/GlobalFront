# Final Certification Re-Audit — Phase 2.0 Sprint 2.5 (N-01 / D-10 / A-01)

**Target commit:** `4cb8ffa2462f5b7ef17daf522e82743c1582ad6c` (verified: `git rev-parse HEAD`)
**Working tree:** clean except the known EOL-only `ProjectSettings/ProjectSettings.asset` marker and three untracked prior audit reports — i.e. the sources are byte-identical to the commit.
**Change under review:** `git diff e6d91ea..4cb8ffa` = **+197 / −1 across exactly 3 files** (`ServerTransportHost.cs` +26, `TransportSessionBinder.cs` +19/−1, `TransportFlowAndSecurityTests.cs` +153). No other file touched.
**Baseline (authoritative):** `Artifacts/TestResults/sprint25-editmode-final.xml` → `test-run testcasecount="1162" result="Passed" total="1162" passed="1162" failed="0" skipped="0"`, `start-time="2026-10-20 16:08:16Z"`; `…sprint25-playmode-final.xml` → `testcasecount="16" … passed="16" failed="0"`, `start-time="2026-10-20 16:08:55Z"`. `grep -c 'result="Failed"'` = **0**, `'result="Skipped"'` = **0** across the whole EditMode run. 1159 → 1162 is exactly the three new tests.
**Disclosure:** sprint 2.5 was implemented in an earlier session of mine. This certification therefore re-derived every claim from the committed diff, an exhaustive call-site enumeration and the archived XMLs — not from my own notes.

---

## 1. Verdict

# `[APPROVED: ZERO DEFECTS]`

**N-01 / D-10 / A-01 is completely eradicated.** No P0 or P1 defect remains in the audited stack, and the remediation introduces no new blocker.

**Scope of the approval, stated precisely so it is not over-read:** this certifies *P0/P1 = 0* per the rubric in §2 of the request. It is **not** a clean bill of health for the whole network stack. Three **P2** findings from `Artifacts/Phase20-Adversarial-Audit-e6d91ea-CLAUDE.md` remain open and were not in sprint 2.5's scope — **N-02** (production C2 receive/reassembly allocates `2+N` objects per fragmented snapshot), **N-03** (`DefaultRemoveSink = 256` < server `DefaultRemoveCapacity = 512`), **N-04** (a droppable `Disconnected` event leaks a rate limiter and a binding permanently) — plus the P3 batch. **N-04 in particular still reaches the same terminal outcome N-01 did** (permanent `BoundCount` inflation → `ServerFull` for everyone), by a different mechanism. Eradicating N-01 does not make that outcome impossible; see §5.

---

## 2. Verification of the Three Fix Layers

### 2.1 Handler guard — `ServerTransportHost.cs:333-356`

```csharp
if (_binder.TryGetByConnection(transportEvent.ConnectionId, out var bound) &&
    bound.Session != request.SessionId)
{
    var denied = new ReconnectResponse(ReconnectResult.Denied, default(PlayerId),
        default(MatchId), currentTick, 0);
    if (ReconnectWireCodec.TryEncodeResponse(denied, _messageBuffer, out var deniedWritten))
        _carrier.Send(transportEvent.ConnectionId, TransportChannel.Control, _messageBuffer, 0, deniedWritten);
    return;
}
```

- **Placement is correct.** It sits after `TryDecodeRequest` (so a malformed request never reaches it) and **before** `CreateConnectionHandle()` / `TryRebindSession` / `Bind` / `AssignSessionToken` — so a denial consumes no handle, mutates no session state, issues no token and writes no map entry. Verified against the full method body, not just the hunk.
- **Session-aware form is required, not a compromise.** An unconditional `TryGetByConnection` guard regresses a legitimate flow: `ServerReconnectionTests.MonotonicKeyframeSeq_AcrossReattachments_NeverResetsToZero` re-attaches the *same* session on the *same* socket after a direct `NotifyConnectionLost`. That test is `result="Passed"` in the committed XML (test-case id 1496), and the sprint-2.5 record shows it failing `Expected: greater than 1 / But was: 1` under the literal guard. The brief's description (`bound.Session != request.SessionId`) matches the implementation exactly — **no deviation to report**.
- **`ReconnectResult.Denied` exists** (`Core/Reconnect/ReconnectModel.cs:18`) — the brief is accurate here, unlike several earlier rounds.
- **Zero-GC preserved.** `ReconnectResponse` is a `readonly struct` (`ReconnectModel.cs:186`), the buffer is the preallocated `_messageBuffer`, and the guard itself is two dictionary lookups. The denial path allocates nothing.
- **No amplification.** The 64-byte request elicits a smaller response, and `HandleMessage` runs the per-connection `RateLimiter` (200 pkt/s) before dispatch, so the denial path cannot be used as a reflector or a GC-pressure vector.

### 2.2 `Bind` exclusivity — `TransportSessionBinder.cs:51-60`

Throws `InvalidOperationException` when `connectionId` **or** `session` is already present. I treated a new `throw` on a packet-reachable path as a candidate P1 (remote DoS) and tried to reach it. **It is unreachable**, by exhaustive enumeration:

`grep` over `Assets/_GlobalFront/Runtime` finds exactly **two** `Bind` call sites.

| Call site | Connection-occupied check | Session-occupied check |
|---|---|---|
| `ServerTransportHost.cs:462` (connect) | D-1 guard at **:434** denies any connection that already owns a binding | session is a fresh `Guid.NewGuid()` from `CreateSession` (**:447**) ⇒ cannot be in `_tokenBySession` |
| `ServerTransportHost.cs:366` (reconnect) | §2.1 guard denies a *different* session; for the *same* session, `_binder.Release(session.Id)` at **:365** runs immediately before `Bind`, and §2.3 now guarantees that Release clears `_tokenByConnection[X]` when the mapping is X's own | the same `Release(session.Id)` removes `_tokenBySession[session]` unconditionally when present (**:120**) |

I also probed the one divergence source outside the transport: `LocalMatchHost.NotifyConnectionLost` (**:506-507**) is a public pass-through that puts a session into `Disconnected` **without** releasing the binder. Even then the throw is not reached — a same-session reconnect on that socket still executes `Release(A)` first, and Part C's identity check passes because the mapping *does* point at A's token, so both maps are clean before `Bind`. A cross-session reconnect is denied by §2.1; a fresh `ConnectRequest` is denied by :434.

**Residual observation (P3, not a blocker).** `Pump` → `HandleEvent` → `HandleMessage` → `HandleReconnectRequest` contains **no `try`/`catch`** (grep confirms only a comment matches), so were the invariant ever violated the exception would unwind into the host tick loop and kill the server — the opposite of the project's own stated principle at `LiteNetLibCarrier.cs:236-238` ("a send failure must surface as a drop counter, never as an exception in the host/simulation tick loop"). Fail-loud is defensible for a *defensive* invariant and the bite record shows it doing its job, but it should be a recorded decision, and Phase 4 must not add a third `Bind` call site without a guard. Consider a comment on `Bind` stating the contract, or a `bool TryBind` overload for callers that must not throw.

### 2.3 `Release` token identity — `TransportSessionBinder.cs:124-128`

```csharp
if (_tokenByConnection.TryGetValue(binding.ConnectionId, out var mappedToken) &&
    mappedToken == token)
{
    _tokenByConnection.Remove(binding.ConnectionId);
}
```

This closes N-01's consequence D (the guard unbinding itself): releasing a displaced session can no longer delete the mapping that now belongs to a different session. `_byToken.Remove(token)` and `_tokenBySession.Remove(session)` still execute unconditionally, so the released session is fully retired and `_cachedSnapshotTargets` is still cleaned (**:131**) — no new leak is introduced by skipping the connection removal.

---

## 3. Witness Verification (executable proof, per §2 of the request)

All three tests are present in the committed XML with `result="Passed"` individually (test-case ids 1365, 1366, 1367), and the pre-existing D-1 connect-path witness (`ConnectRequest_FromAlreadyBoundConnection_IsDeniedAndRosterPreserved`, id 1364) still passes.

| Test | What it actually proves | Bite evidence (mutation → observed failure) |
|---|---|---|
| `DoubleBindOnSameConnection_IsDenied` (`:423-499`) | **The full N-01 exploit, end-to-end over the real carrier.** Two sessions established; B's socket closed; waits until B is `Disconnected`; then sends `ReconnectRequest(sessionB, secretB)` **on A's socket** (`carrier.Send(1, …)`) using B's *valid* secret — so the denial cannot be an artifact of secret validation. Asserts: a response arrives (`"the hijacking socket must receive an explicit denial"`), `Result == Denied`, `BoundCount` stays **1**, `TryGetByConnection(connectionA)` still resolves to **session A with token A unchanged**, and session B remains `Disconnected` (unaltered). | Handler guard removed **and** binder socket guard removed → `the hijacking socket must receive an explicit denial / Expected: True But was: False` (the server accepted and `AssignSessionToken` rewrote the socket token). Handler guard removed alone → `System.InvalidOperationException : Connection 1 already owns a session binding.` — proving §2.2 fires as defence in depth. |
| `ReleaseOfDisplacedSession_PreservesActiveConnectionMapping` (`:501-543`) | Part C in isolation. Stages the displaced index state **by reflection** — necessary, because §2.2 now forbids creating it through the public API — then asserts `Release(A)` preserves B's connection mapping (`"Release(A) must not delete a socket mapping owned by B"`), retires A's token and session entries, and leaves `BoundCount == 1` with `SnapshotTargets == [B]`. | Token-identity check removed → `Release(A) must not delete a socket mapping owned by B / Expected: True But was: False`. Separately, `_byToken.Remove` removed → `bound sessions must not leak / Expected: 0 But was: 1`. |
| `BoundCount_ReturnsToBaseline_OnDisconnect` (`:545-568`) | The leak half (consequence C) on the delivered-event path: graceful disconnect drives `BoundCount` and `AttachedCount` 1 → 0 and empties all four structures (`_tokenBySession`, `_byToken`, `_tokenByConnection`, `SnapshotTargets`). | See the `_byToken.Remove` mutation above. |

Every changed line has a witness that fails for its own reason, and no `BITE-` mutation marker survives anywhere in `Assets/_GlobalFront` (grep count 0) — the bite runs were fully restored before commit.

**Coverage note, stated plainly:** `ReleaseOfDisplacedSession_…` proves Part C against a *staged* state that the public API can no longer produce. That is the correct treatment for defence in depth, but it means Part C has no end-to-end witness — its only reachable trigger was removed by §2.1/§2.2. Do not read its green as proof that the displaced state is reachable.

---

## 4. Attempted Refutation — routes to the N-01 corruption that survive

I re-derived each of N-01's four consequences against the committed code:

| N-01 consequence | Status at `4cb8ffa` |
|---|---|
| **A** — two bindings sharing one `ConnectionId` ⇒ dual `PlayerId` command authority (`HandleCommand` accepts both tokens because both bindings carry `ConnectionId == X`) | **Impossible.** Both `Bind` call sites are guarded (§2.2 table); `_tokenByConnection` is injective by construction and `Bind` refuses to overwrite it. |
| **B** — duplicate snapshot streams to one socket (the emitter keeps the orphan's `ClientState` active because no `SessionDetached` fires) | **Impossible** — same prerequisite as A. |
| **C** — permanent `BoundCount` inflation ⇒ terminal `ServerFull`, plus permanent emitter `ClientState` slot exhaustion | **Closed via the double-bind route** (witnessed by `BoundCount_ReturnsToBaseline_OnDisconnect`). **Still open via N-04** — see §5. |
| **D** — `Release(A)` destroying B's mapping, which turns the D-1 connect guard off for that socket | **Impossible.** §2.3 makes `Release` remove only the mapping it owns. |

Legitimate flows were checked for regression, not just attacks: same-session re-attach on the same socket (allowed by §2.1, and `TryRebindSession` then returns `InvalidState` because the session is already `Connected`, so no re-bind occurs); re-attach on a **new** socket after a real loss (`TryGetByConnection(Z)` is false ⇒ guard passes ⇒ accepted); duplicate/retransmitted `ReconnectRequest` (idempotent, denied with `InvalidState`); and a reconnect attempted while the old socket is still live (denied with `InvalidState`, no corruption). `MonotonicKeyframeSeq_AcrossReattachments_NeverResetsToZero` passing in the committed XML is the executed evidence for the first of these.

---

## 5. Residuals Carried Forward (non-blocking under this rubric)

1. **N-04 (P2) — the terminal outcome is still reachable.** Both carriers funnel `Disconnected` through the same bounded FIFO as bulk data and silently drop on overflow (`LiteNetLibCarrier.cs:843-856`, `OwnDatagramCarrier.cs:923-936`). A dropped disconnect leaks `ServerTransportHost._rateLimiters[connectionId]` **and** the binder binding for a connection id that is monotonic and never reused — so `BoundCount` never returns to baseline and the server ends in the same permanent `ServerFull` that N-01 produced. Requires a ≥320 ms host pump stall at 64 peers × 200 pkt/s (12,800 events/s against a 4096 cap at 20 Hz). `BoundCount_ReturnsToBaseline_OnDisconnect` witnesses only the *delivered*-event path, so this is unmitigated and unwitnessed. Fix: make control events undroppable (separate queue or reserved headroom), or sweep host state against the carrier's live connection set each pump.
2. **The denial path increments no metric.** `TransportMetrics.cs:3-8` states the project's own pattern — "Malformed/rate-limited traffic is dropped silently and only surfaces through these counters" — and every other rejection in `HandleMessage` follows it (`DroppedBadToken`, `DroppedMalformed`, `DroppedBadVersion`). The new hijack denial is the one security event that produces **no** counter, so an attempted roster hijack is invisible to telemetry; the only evidence is a response packet the attacker controls. One line: `_carrier.Metrics.DroppedBadToken++;` before the `return` (or a dedicated `DroppedHijackAttempts` field). **P3.**
3. **`ReconnectResult.Denied = 5` is an alias of `InvalidState = 5`** (`ReconnectModel.cs:17-18`), so the client cannot distinguish "you tried to hijack a socket" from "your session is not disconnected". Pre-existing enum, wire-compatible, diagnostic only. **P3, informational.**
4. **N-02 and N-03 (P2)** are unchanged by this sprint and are the next two items in the recommended order (carrier-wide zero-GC on the receive/reassembly path; `DefaultRemoveSink = 512` plus an honest `BufferTooSmall` error class).

---

## 6. Certification

Sprint 2.5 closes **N-01 / D-10 / A-01** completely: the exploit is denied at the handler, made structurally impossible by the binder, and its self-unbinding escalation is closed by the token-identity check — three independent layers, each with an executed witness that has been *seen to fail* against its own reverted line. The change is minimal (+197/−1, three files), allocation-free on the new paths, and regresses no legitimate reconnect flow.

`[APPROVED: ZERO DEFECTS]` at `4cb8ffa2462f5b7ef17daf522e82743c1582ad6c`, meaning **P0 = 0, P1 = 0**, EditMode 1162/1162 and PlayMode 16/16 verified from the authoritative XMLs. 3 × P2 and the P3 batch remain open and are enumerated in §5 and in `Artifacts/Phase20-Adversarial-Audit-e6d91ea-CLAUDE.md`; they do not block under the stated rubric but should not be read as accepted.

*Every line reference read from the source at `4cb8ffa`. No file was modified by this re-audit.*
