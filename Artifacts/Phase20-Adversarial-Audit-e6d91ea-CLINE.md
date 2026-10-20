# Adversarial Architectural & Code Audit — Phase 2.0 (Multiplayer & Network Stack)

## 2. Defect Matrix

### A-01 — P1 (Critical) — Reconnect path bypasses the D-1 one-connection-one-session guard

| | |
|---|---|
| **File** | `Assets/_GlobalFront/Runtime/Server/Transport/ServerTransportHost.cs` |
| **Lines** | `HandleReconnectRequest` 322–369 (binder `Release`/`Bind` at 339–340); contrast `HandleConnectRequest` guard at 408; `TransportSessionBinder.Bind` unconditional `_tokenByConnection[connectionId] = token` at `TransportSessionBinder.cs:78` |
| **Symbol** | `ServerTransportHost.HandleReconnectRequest`, `TransportSessionBinder.Bind` |

**Defect.** `HandleConnectRequest` enforces the D-1 invariant — one connection may own exactly one session — via `_binder.TryGetByConnection(connectionId, out _)` at line 408, denying a repeated handshake before any session/roster slot is consumed. `HandleReconnectRequest` performs **no equivalent guard**. It calls `_binder.Release(session.Id)` then `_binder.Bind(session.Id, ..., connectionId)` unconditionally. `Bind` overwrites `_tokenByConnection[connectionId]` without checking whether that connection already owns a *different* live binding, orphaning the previous binding inside `_byToken`/`_tokenBySession`.

**Failure sequence (session orphaning / roster corruption):**
1. Socket **X** completes handshake → session **A** (token `TA`), player P1, match M. Binder: `_byToken{TA→A}`, `_tokenBySession{A→TA}`, `_tokenByConnection{X→TA}`. Session A is `Connected`; P1's roster slot is consumed.
2. Client A drops abruptly (no graceful disconnect). Session A enters the Disconnected grace window but remains bound to `TA`/`X`.
3. The same client (or an attacker who knows a valid secret) sends `ReconnectRequest` for a **different** session **B** (player P2, previously connected, valid secret, in grace window) **on connection X**.
4. `TryRebindSession(B)` → `Accepted`. `_binder.Release(B)` is a no-op (B not bound). `_binder.Bind(B, ..., X)` overwrites `_tokenByConnection[X] = TB`.
5. Result: connection X now attributes to session B, but session A is still `Connected` in `_byToken`/`_tokenBySession` with player P1's roster slot consumed and unreachable via X. The roster holds two "live" sessions on one socket; P1 is stranded, P2 hijacked onto X's transport state. Subsequent traffic from X (token `TB`) is attributed to B; session A can no longer be driven by its socket.

**Exploitability:** Requires a valid reconnect secret for session B (replay/credential scenario) or a client that legitimately holds multiple sessions. Does not require breaking cryptography — it is a logic bypass of the D-1 guard. Impact: session displacement, roster corruption, stranded player slots, potential denial of service for the orphaned player.

**Minimal remediation:** Add the one-connection-one-session guard to `HandleReconnectRequest` before rebinding:
```csharp
if (_binder.TryGetByConnection(transportEvent.ConnectionId, out var existing) &&
    existing.Session != session.Id)
{
    _carrier.Metrics.DroppedHijackAttempts++;
    return; // deny rebind on a socket that already owns a different live session
}
```
Also make `TransportSessionBinder.Bind` consistent: reject/return failure when `connectionId` is already mapped to a *different* token, and make `Release` only clear `_tokenByConnection[connectionId]` when it maps to the token being released (defense in depth).


**Role:** Senior Staff Adversarial Systems Architect, Network Security Specialist & Real-Time Engine Auditor.
**Target Repository:** `GlobalFront` (Commercial Lockstep/Client-Server RTS, Unity 6000.6.2f1, C# 9.0, URP 17.6.0).
**Target Commit:** `e6d91eafe65c1fdd65122f21e3e8c49f233b9293` (HEAD of `main`) — working tree clean except an unrelated `ProjectSettings.asset` modification.
**Current Test Baseline:** 1159 EditMode + 16 PlayMode tests — **100% GREEN (0 errors, 0 warnings)**. Baseline is trusted but NOT re-executed here (no Unity Editor available); several findings are latent and are NOT reached by the existing test suite.

## 1. Executive Verdict

# `[REJECT: DEFECTS DETECTED]`

| Severity | Count | IDs |
|---|---|---|
| P0 Blocker | 0 | — |
| P1 Critical | 1 | A-01 |
| P2 Medium | 5 | A-02, A-03, A-04, A-05, A-06 |
| P3 Minor | 0 | — |

**Headline:** The D-1…D-9 remediation campaign (Sprints 2.1–2.4) is **substantively sound at the named sites** — every one of the nine prior defects has a real fix in place, and the fixes are not cosmetic. However, the campaign optimized the *named* sites and left **structural gaps** the previous pass did not enumerate:

1. The D-1 anti-hijack guard exists on the **connect** path only (`ServerTransportHost.cs:408`) and is **absent from the reconnect path** (`HandleReconnectRequest`, `ServerTransportHost.cs:322-369`), allowing one connection to orphan/displace a live session and corrupt the roster (A-01, P1).
2. The D-4 Zero-GC remediation was applied to `LiteNetLibCarrier` only; the project's own reference carrier (`OwnDatagramCarrier`) still allocates per fragment and per datagram on its C0/C1/C2 paths, and `FragmentAssembler` allocates per fragment on the keyframe slice-reassembly path (A-02/A-03/A-04, P2). No test gate measures the own carrier.
3. `_pendingAcks` remains an unbounded, remote-influenced list whose persistently-failing entries are never dropped (A-05, P2) — D-6 cleanup exists but the structure itself is uncapped.
4. `TickDriver.AdvanceRealTime` captures the step base under `_gate` but re-emits stale indices if a `TickDue` handler re-entrantly calls `Reset()`, violating the strict-monotonicity invariant under reentrant `Reset()` (A-06, P2) — D-8 removed the read race but did not close the reentrancy case.

**Net:** because a P1 (A-01) and multiple P2s are confirmed, the commit cannot be approved for the stated invariants.

### A-02 — P2 (Medium) — OwnDatagramCarrier allocates per fragment on the C0/C1 send path (Zero-GC violation)

| | |
|---|---|
| **File** | `Assets/_GlobalFront/Runtime/Server/Transport/OwnDatagramCarrier.cs` |
| **Lines** | `SendReliableMessage` — `var fragment = new byte[fragmentLength];` at line **405** |
| **Symbol** | `OwnDatagramCarrier.SendReliableMessage` |

**Defect.** Every reliable fragment sent by the project's own carrier allocates a new `byte[]` on the steady-state send path. This directly violates the invariant **$0\text{ B/packet}$** on the send path. D-4 (reusable locked `_sendScratch`) was applied to `LiteNetLibCarrier` only (`LiteNetLibCarrier.cs:49-51`); the own carrier was not remediated and no Zero-GC gate measures it.

**Failure sequence:** Under steady-state C0/C1 traffic (handshake, commands, acks, disconnects that exceed one datagram), each fragmented reliable message performs a heap allocation per fragment. Garbage accumulates proportionally to message count and fragment count — predictable GC pressure on the host/simulation thread, jitter risk at 20 Hz, and a divergence from the project's own Zero-GC contract.

**Minimal remediation:** Introduce a reusable, gate-serialized send scratch buffer for the own carrier (mirroring `LiteNetLibCarrier._sendScratch`/`_sendGate`) and fragment in-place into it; or return fragments through a pooled/preallocated buffer contract. Measure with the same `GC.Alloc` gate used for `LiteNetLibCarrier` (100 fragmented snapshots ⇒ 0 samples).

### A-03 — P2 (Medium) — OwnDatagramCarrier allocates per datagram on the receive/reorder path (Zero-GC violation)

| | |
|---|---|
| **File** | `Assets/_GlobalFront/Runtime/Server/Transport/OwnDatagramCarrier.cs` |
| **Lines** | `HandleReliableDatagram` — `var payload = new byte[envelope.PayloadLength];` at lines **816** and **872** |
| **Symbol** | `OwnDatagramCarrier.HandleReliableDatagram` |

**Defect.** Incoming reliable datagrams that land in the receive/reorder window are copied into a freshly allocated `byte[]` per datagram (`new byte[envelope.PayloadLength]` at lines 816 and 872). This violates **$0\text{ B/packet}$** on the receive path. `LiteNetLibCarrier` reads datagrams in-place (no copy) per D-4; the own carrier does not.

**Failure sequence:** Every reordered/out-of-order reliable datagram (expected under loss/reorder on UDP) triggers a heap allocation. Under lossy or congested links the reorder window sees sustained traffic, so allocation rate scales with received packet rate — GC pressure directly correlated with network conditions, on the host thread.

**Minimal remediation:** Decode/inspect in-place from `transportEvent.Data` (or a reusable scratch slice) instead of copying into `new byte[]`; retain a per-connection reusable reorder slot bounded by `ReorderWindow` (64), copied only once into the assembler when a group completes.
### A-04 — P2 (Medium) — FragmentAssembler allocates per fragment on the keyframe slice-reassembly path (Zero-GC violation)

| | |
|---|---|
| **File** | `Assets/_GlobalFront/Runtime/Server/Transport/FragmentAssembler.cs` |
| **Lines** | `AddFragment`: `Fragments = new byte[fragmentCount][]` line **136**; `var slice = new byte[length];` line **148**; final `new byte[group.TotalBytes]` line **167** |
| **Symbol** | `FragmentAssembler.AddFragment` |

**Defect.** The invariant explicitly lists **keyframe slice reassembly** as a $0\text{ B/packet}$ path, yet `AddFragment` allocates: (a) the fragment array per new group, (b) a `byte[]` per received fragment slice, and (c) the reassembled message once complete. C2 snapshots (fragmented every snapshot because they exceed one datagram) route through `connection.Assembler.AddFragment` (`OwnDatagramCarrier.cs:683, 855, 881`), so this is a per-snapshot receive-path allocation.

**Failure sequence:** Each fragmented keyframe/snapshot allocates `fragmentCount` slice arrays plus the final message array. Under steady-state 20 Hz snapshot traffic this is recurring Gen-0 pressure on the host thread; under fragment floods it also contributes to reassembly memory churn.

**Note (scope of D-9):** The *group-count* cap (`MaxFragmentGroups = 4`, line 114) and budget eviction (`EvictUntilBudget`, line 125) are correctly enforced — D-9's memory-bound concern is closed for `FragmentAssembler`. The defect here is purely the **allocation-per-fragment**, not the bound.

**Minimal remediation:** Preallocate/reuse slice storage from a bounded per-peer scratch pool (or write fragments directly into a single preallocated group buffer indexed by `FragmentIndex`), and assemble into a reused output buffer. Keep the group/fragment/budget caps intact.

### A-05 — P2 (Medium) — `_pendingAcks` is unbounded and never drops persistently-failing entries (remote-influenced memory DoS)

| | |
|---|---|
| **File** | `Assets/_GlobalFront/Runtime/Server/Transport/ServerTransportHost.cs` |
| **Lines** | `_pendingAcks` declaration line **35**; `SendCommandAck` allocation+enqueue lines **617-624**; connection-drop purge lines **210-216** |
| **Symbol** | `ServerTransportHost.SendCommandAck`, `ServerTransportHost.DrainPendingAcks`, `ServerTransportHost.HandleConnectionLost` |

**Defect.** When `_carrier.Send` fails, `SendCommandAck` allocates a fresh `byte[]` copy and appends it to `_pendingAcks` — a `List<PendingCommandAck>` with **no cap** (line 35). `DrainPendingAcks` retries every pump and never drops an entry that keeps failing. Every remote-influenced structure in the codebase is explicitly capped (`MaxQueuedReceiveEvents` 4096, `MaxFragmentGroups` 4, `MaxConnections` 64, `MaxTrackedPreHandshakePeers` 256, `ReassemblyBudgetBytes` 2 MB) — this is the one exception.

`HandleConnectionLost` (lines 210-216) does purge matching acks on a clean disconnect (D-6 closed on the normal path), but a peer that never disconnects gracefully — it just keeps the server's outbound reliable window full (e.g. `OwnDatagramCarrier.Send` returns `false` while `Unacked.Count >= ReceiveWindow` or the outstanding span reaches `ReorderWindow`) — can force the server to append one queued ack per accepted command, forever.

**Failure sequence:** Attacker / lagging peer sustains command traffic while never acknowledging server reliable traffic. Server's outbound window stays full; every accepted command causes `SendCommandAck` → Send fails → `new byte[offset]` + `_pendingAcks.Add`. List grows without bound; each entry is a separate heap allocation plus retained buffer. Memory exhaustion DoS; no backpressure, no eviction, no cap.

**Minimal remediation:** Cap `_pendingAcks` (e.g. `MaxPendingCommandAcks`, proportional to `MaxConnections * ReceiveWindow`); when full, drop the oldest failing entry and increment a `DroppedPendingAcks` metric; optionally bound per-connection pending acks.

### A-06 — P2 (Medium) — `TickDriver.AdvanceRealTime` emits non-monotonic tick indices under reentrant `Reset()` (D-8 incomplete)

| | |
|---|---|
| **File** | `Assets/_GlobalFront/Runtime/Server/TickDriver.cs` |
| **Lines** | `AdvanceRealTime` lines **192-218**; `baseTick` capture at 209; emission loop 212-215 |
| **Symbol** | `TickDriver.AdvanceRealTime`, `TickDriver.Reset` |

**Defect.** The invariant requires: *"TickDriver step indices emitted to `TickDue` must remain strictly monotonic and race-free under reentrant `Reset()` or concurrent calls. `Tick => _tick` must be protected under `_gate`."* The `_tick` read race is closed (`Tick` getter locks `_gate`, line 65-74; `baseTick` captured under `_gate` at line 209), but **monotonicity under reentrant `Reset()` is not**. `AdvanceRealTime` captures `baseTick` once under the gate, releases the gate, then emits `baseTick + index` for `index = 1..scheduled`. If a `TickDue` handler re-entrantly calls `Reset(newTick)` where `newTick > baseTick + scheduled`, the driver's `_tick` jumps to `newTick`, but the loop continues emitting `baseTick + (k+1)...baseTick + scheduled` — all **strictly less than** the now-current `_tick`. Indices are emitted out of order / non-monotonically relative to the driver state after the reset.

**Concrete trace:** `_tick = 100`; `AdvanceRealTime` schedules 5 → `baseTick = 100`, emits 101. Handler for 101 calls `Reset(200)` → `_tick = 200`. Loop continues emitting 102, 103, 104, 105 — four ticks numerically *behind* the current `_tick (200)`, violating strict monotonicity and potentially causing the simulation to step backward relative to the host's notion of "current tick."

**Reachability:** Not exercised by `TickDriverTests.cs` (no reentrant-Reset-during-emission test exists). Latent in production only if a `TickDue` handler calls `Reset`, but the invariant explicitly demands the property, so the gap is a real defect.

**Minimal remediation:** Re-validate monotonicity inside the emission loop — e.g., re-read `_tick` under `_gate` before each `TickDue` invocation and skip/clamp any index that is not strictly greater than the last emitted index, or capture the step and assert `baseTick + scheduled <= _tick` after the gate is released (fail closed). Alternatively, document that `TickDue` handlers must not call `Reset` and enforce via a reentrancy guard.

## 3. Verification of Prior Remediations (D-1 through D-9)

Each prior defect was re-verified against the source at `e6d91ea`. Status key: **CLOSED** = fix present and robust; **PARTIAL** = fix present at the named site but a structural gap remains (see Defect Matrix); **NOT CLOSED** = absent.

| ID | Severity | Prior summary | Site | Status | Evidence at e6d91ea |
|---|---|---|---|---|---|
| **D-1** | P0 | Full-roster socket hijack via duplicate-connection rejection | `ServerTransportHost.cs` connect path | **PARTIAL** | `_binder.TryGetByConnection` guard present at line 408 in `HandleConnectRequest` ✓. **ABSENT** from `HandleReconnectRequest` (322-369) → A-01. |
| **D-2** | P1 | Unbounded session-registry $O(N)$ tick scan → `$O(k)$` grace sweep | `SessionManager.cs:73, 82, 236-248` | **CLOSED** | `_disconnectedSessions: HashSet<SessionId>` (82); `SweepDisconnectedSessions` iterates only that set; `HasDisconnectedSessionsInGrace` (819) scans only `_disconnectedSessions`. |
| **D-3** | P1 | Out-of-order slice corruption → staged canonical `PartIndex` copy | `ClientTransportReplicationBridge.cs:466-511` | **CLOSED** | Staging buffer `_assemblyUnits`/`_partOffsets`/`_partLengths`; slices rejected if `partIndex >= _assemblyPartCount` (set from first slice at 479); replay in strict ascending `PartIndex` (498-511); `_assemblySeq` staleness check (522). |
| **D-4** | P1 | Zero-GC transport path → reusable locked `_sendScratch`, stale-id pooling, in-place reads | `LiteNetLibCarrier.cs:49-51, 294-340, 724-741, 576` | **PARTIAL** | `LiteNetLibCarrier` send path is Zero-GC (reusable `_sendScratch` under `_sendGate`, in-place reads) ✓. **NOT** extended to `OwnDatagramCarrier` (A-02, A-03) or `FragmentAssembler` (A-04). No carrier-wide gate. |
| **D-5** | P2 | Rate-limiter leak on connection drop | `ServerTransportHost.cs:202-241` | **CLOSED** | `HandleConnectionLost` removes `_rateLimiters[ConnectionId]` (206) before early returns; LiteNetLib `RemovePeerState` (386-392) clears mapping+limiter+snapshot state; OwnDatagram per-connection `ReliableConnection.Limiter` is reclaimed with the connection (`SweepPendingRemovals`, 320-332). |
| **D-6** | P2 | Pending-ACK leak on connection drop | `ServerTransportHost.cs:210-216` | **PARTIAL** | Clean-disconnect purge present ✓ (`_pendingAcks.RemoveAll` by ConnectionId). Structure itself uncapped; `DrainPendingAcks` never drops persistently-failing entries → A-05. |
| **D-7** | P2 | Hung assembly → `AssemblyTimeoutMs` aging | `ClientTransportReplicationBridge.cs:192, 432, 562-571` | **CLOSED** | `AssemblyTimeoutMs = 1500`; `AgeAssembly` called from `Pump` (311) and before each slice admission (432); `ResetAssembly` clears all per-generation fields (545-554); late slices start fresh generations. |
| **D-8** | P2 | Unlocked `Tick` read / racy `_tick` during invocation | `TickDriver.cs:65-74, 192-218` | **PARTIAL** | `Tick` getter locked ✓; `baseTick` captured under `_gate` ✓; `TickDue` raised outside lock ✓. **Reentrant-`Reset` monotonicity not guaranteed** → A-06. |
| **D-9** | P2 | Unbounded `SeqByMessageId` fragment-tracking map | `LiteNetLibCarrier.cs:764-786`; `OwnDatagramCarrier.cs:676-677, 764-786`; `FragmentAssembler.cs:114-129` | **CLOSED** | Both carriers cap in-progress groups at `TransportProtocol.MaxFragmentGroups` (4) with oldest-sequence eviction; `FragmentAssembler` caps `_groups` at `MaxFragmentGroups` + `ReassemblyBudgetBytes`. |

**Conclusion on D-1…D-9:** All nine named sites are fixed and none is reverted. Four are only **partially** closed because the remediation did not extend to a symmetric code path or structure (D-1 reconnect, D-4 carrier breadth, D-6 cap, D-8 reentrancy), which is exactly where A-01, A-02/A-03/A-04, A-05, and A-06 originate.

## 4. Zero-GC & Concurrency Assessment

### 4.1 Zero-GC on hot paths ($0\text{ B/tick}$, $0\text{ B/packet}$)

| Path | Carrier / module | Status | Notes |
|---|---|---|---|
| Server C0/C1 send | `LiteNetLibCarrier` | **CLEAN** | Reusable locked `_sendScratch` (`_sendGate` spans write+Send); no per-fragment alloc. Verified by integration test (100 fragmented snapshots ⇒ 0 `GC.Alloc` samples). |
| Server C0/C1 send | `OwnDatagramCarrier` | **VIOLATION** | `new byte[fragmentLength]` per fragment (line 405). |
| Server reliable receive/reorder | `OwnDatagramCarrier` | **VIOLATION** | `new byte[envelope.PayloadLength]` per datagram (lines 816, 872). |
| Keyframe slice reassembly | `FragmentAssembler` | **VIOLATION** | `new byte[fragmentCount][]` (136), `new byte[length]` per slice (148), `new byte[TotalBytes]` (167). |
| Server → client snapshot dispatch | `ClientTransportEndpoint` | **CLEAN** | Rotating ring of 4 preallocated buffers; `Array.Copy` into slot, no per-packet alloc (316-320). |
| Client → server feedback/command send | `ClientTransportEndpoint` | **CLEAN** | Shared `_messageBuffer`, `Array.Copy`, no per-call alloc (123-160). |
| Delta snapshot encode/decode | `DeltaSnapshotWireCodec` (Core/Snapshot) | **CLEAN** | Span-based, allocation-free both directions. |
| Client keyframe assembly | `ClientTransportReplicationBridge` | **CLEAN** | Preallocated `_assemblyUnits`/`_partOffsets`/`_partLengths`; no per-slice alloc. |
| Session/tick bookkeeping | `SessionManager`, `TickDriver` | **CLEAN** | No per-tick allocations. |

**Overall:** Zero-GC is **not** carrier-wide. The own reference carrier and the shared `FragmentAssembler` breach the $0\text{ B/packet}$ invariant on the receive and reassembly paths. Because the existing Zero-GC integration test covers only `LiteNetLibCarrier`, these own-carrier violations are **latent and unreached by tests**.

### 4.2 Concurrency & thread safety

- **`TickDriver`:** All mutable fields (`_tick`, `_mode`, `_isPaused`) read/written under `_gate`; `TickDue` raised outside the lock; `baseTick` captured under `_gate`. Read race (D-8) closed. **Residual gap:** reentrant `Reset()` during the emission loop breaks strict monotonicity (A-06). Lock order is trivial (single gate); no lock-order inversion risk.
- **`LiteNetLibCarrier`:** Worker thread only enqueues; authoritative processing on host thread. `_peerGate` protects peer/limiter/snapshot-state maps; `_eventGate` protects the event queue; `_sendGate` serializes the send scratch. Lock order `_peerGate → _sendGate`, never reversed. `RemovePeerState` (386-392) removes mapping+limiter+snapshot state together; `CloseConnection` removes state before `peer.Disconnect()` to avoid double-reporting. Sound.
- **`OwnDatagramCarrier`:** Single-threaded by construction (host thread only). Close paths triggered inside `Pump` defer map removal to `_pendingRemovals` and sweep at the end of `Pump` (320-332) — no collection mutation during iteration. `ProcessAcks`/`ProcessTimers` iterate `Unacked` by index and remove from the tail. Sound.
- **Session / binder layer:** `SessionManager`, `TransportSessionBinder`, `ServerTransportHost` are documented and implemented as **host/simulation-thread-only** (no locks). This is consistent with the pump architecture but means they are **not thread-safe** if a future transport adapter pumps from another thread — a documented assumption, not a defect at this commit. The one logical inconsistency (bind/release not being inverses across `connectionId`) is captured in A-01.
- **Client replication layer:** No cross-thread mutable state; carrier worker enqueues, endpoint dequeues and dispatches on the single pumping thread. Sound.

### 4.3 Session & anti-hijack invariants

- Connect path: unauthenticated UDP cannot hijack — D-1 guard at line 408, `HandshakeToken` validation, `MaxPlayers` gate at 414. **Sound.**
- Reconnect path: **not** guarded against a connection that already owns a different live session → **A-01 (P1)**. This is the single highest-severity finding.
- Disconnect cleanup: `$O(1)$`/`$O(k)$`, rate limiters and pending acks purged, binder released — D-5/D-6 sound on the normal path (A-05 concerns the uncapped structure, not the cleanup logic).
- Fragment tracking maps strictly bounded at `MaxFragmentGroups` (4) in both carriers and `FragmentAssembler` — D-9 sound.

## 5. Recommended Remediation Order

| Sprint | Scope | Defects |
|---|---|---|
| **2.5** | Binder correctness + reconnect guard | **A-01 (P1)** — one-connection-one-session guard on `HandleReconnectRequest`; `Bind`/`Release` inverse consistency. |
| **2.6** | Zero-GC closure (carrier-wide) | **A-02, A-03, A-04 (P2)** — reusable send scratch + in-place receive reads in `OwnDatagramCarrier`; pooled/preallocated slice storage in `FragmentAssembler`; extend the `GC.Alloc` test gate to the own carrier and reassembly path. |
| **2.7** | Bounded unreliable-influenced structures | **A-05 (P2)** — cap `_pendingAcks`; drop oldest failing entry + metric. |
| **2.8** | TickDriver reentrancy | **A-06 (P2)** — enforce strict monotonicity of `TickDue` indices under reentrant `Reset()` (or forbid `Reset` from handlers with a guard). |
| **2.9** | Regression | Re-verify EditMode/PlayMode baseline (≥ 1159/16) and add targeted tests for A-01..A-06. |

**Exit criteria:** P1 = 0, P2 = 0, all new findings covered by regression tests, Zero-GC gate green on **both** carriers and the reassembly path, baseline re-verified.

## 6. Methodology & Scope Notes

- Audited files (~15,500 LOC C#): `Runtime/Server/Sessions/` (`SessionManager.cs`, `ClientSession.cs`, `ConnectionHandle.cs`; `SessionRegistry.cs`/`PlayerSession.cs` do not exist as separate files — session state lives in `SessionManager`), `Runtime/Server/Transport/` (`ServerTransportHost.cs`, `LiteNetLibCarrier.cs`, `OwnDatagramCarrier.cs`, `FragmentAssembler.cs`, `TransportSessionBinder.cs`, `RateLimiter.cs`, `TransportProtocol.cs`, `SerialArithmetic.cs`, `EnvelopeCodec.cs`, `MessageCodec.cs`, `ClientTransportEndpoint.cs`), `Runtime/Server/TickDriver.cs`, `Runtime/Client/Replication/` (`ClientTransportReplicationBridge.cs`, `ClientReplicationReceiver.cs`, `DeltaSnapshotWireCodec.cs` lives in `Runtime/Core/Snapshot/`). There is **no** `Runtime/Client/Transport/` folder or `ClientTransportHost.cs` in this commit; the client-side transport attachment is `ClientTransportEndpoint`.
- `SessionRegistry.cs` and `PlayerSession.cs` referenced in the scope do not exist at this commit; their responsibilities are folded into `SessionManager`/`ClientSession`.
- Tests were **not executed** (no Unity Editor in this session). The 1159/1159 + 16/16 baseline is taken as given. Findings A-01, A-02, A-03, A-04, A-05, A-06 are either code-proven (A-01–A-05) or derived from the stated invariant plus untested reentrancy (A-06); several are confirmed **not reached** by the existing Zero-GC integration test, which covers `LiteNetLibCarrier` only.
- No files were modified during this audit.

---
*Audit performed against `e6d91eafe65c1fdd65122f21e3e8c49f233b9293`. Every line reference was read from the source at that commit. Independent re-audit; the prior `Artifacts/Phase20-Adversarial-Audit-e6d91ea.md` (S-01…S-09, C-01…C-08) was used only as context and is superseded by this report where they overlap.*

