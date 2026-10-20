# Adversarial Architectural & Code Audit — Phase 2.0 (Multiplayer & Network Stack)

**Target repository:** `GlobalFront` (Commercial Lockstep/Client-Server RTS, Unity 6000.6.2f1, C# 9.0, URP 17.6.0)
**Target commit:** `e6d91eafe65c1fdd65122f21e3e8c49f233b9293` (HEAD of `main`)
**Baseline verified independently:** EditMode **1159/1159 passed**, PlayMode **16/16 passed**, 0 failed, 0 skipped (`Artifacts/TestResults/sprint24-editmode-final.xml`, `…-playmode-final.xml`)
**Scope audited:** `Runtime/Server/Sessions/`, `Runtime/Server/Transport/`, `Runtime/Server/TickDriver.cs`, `Runtime/Client/Replication/`, `Runtime/Client/Transport/` (~15,500 LOC C#), plus the supporting codecs in `Runtime/Core/Snapshot/`

---

## 1. Executive Verdict

# `[REJECT: DEFECTS DETECTED]`

**2 × P0-class blockers are absent; 2 × P1 (Critical), 4 × P2 (Medium), 11 × P3 (Minor) confirmed.**

| Severity | Count | IDs |
|---|---|---|
| P0 Blocker | 0 | — |
| P1 Critical | 2 | S-01, C-01 |
| P2 Medium | 4 | S-02, S-03, C-02, C-03 |
| P3 Minor | 11 | S-04 … S-09, C-04 … C-08 |

**Headline:** the D-1…D-9 remediations are **substantively sound** — every one of the nine prior defects is closed at the site named in the remediation record, and the fixes were not merely cosmetic. However, the remediation campaign optimised the *named* defect sites and left three structural gaps that the previous pass did not enumerate:

1. the D-1 anti-hijack guard was applied to the **connect** path only and is absent from the **reconnect** path, which can therefore put two live sessions on one socket and silently unbind the D-1 guard itself (S-01);
2. the D-3/D-7 keyframe assembly validates each slice *in isolation* but never validates that the slices of one generation **agree with each other**, so a baseline can be installed with units missing (C-01);
3. the D-4 zero-GC remediation was applied to `LiteNetLibCarrier` only; the project's own reference carrier still allocates per fragment and per datagram on its C0/C1 paths, and no gate measures it (S-03).

Two further defects (C-02, C-03) are latent interoperability/correctness bugs in the client replication pipeline that the wire-level unit tests do not reach, because the client and server defaults are mutually inconsistent and the checksum is verified after the fact.

---

## 2. Defect Matrix

### S-01 — P1 (Critical) — One connection can own two live sessions; the D-1 anti-hijack guard can be unbound by the victim itself

| | |
|---|---|
| **File** | `Assets/_GlobalFront/Runtime/Server/Transport/TransportSessionBinder.cs` |
| **Lines** | 76–78 (`Bind`), 100–116 (`Release`) |
| **Related** | `Assets/_GlobalFront/Runtime/Server/Transport/ServerTransportHost.cs` lines 322–369 (`HandleReconnectRequest`), 408–412 (the D-1 guard in `HandleConnectRequest`), 414–418 (`MaxPlayers` gate) |
| **Symbol** | `TransportSessionBinder.Bind` / `TransportSessionBinder.Release` / `ServerTransportHost.HandleReconnectRequest` |

**Defect.** `Bind` writes `_tokenByConnection[connectionId] = token` unconditionally (line 78). When a connection that already owns a binding binds a second session, the previous binding is silently orphaned inside `_byToken` / `_tokenBySession`, and the connection→token map is overwritten. `Release(session)` then removes the connection mapping *by connection id* (line 111) without checking that the mapped token is the one being released. `ServerTransportHost.HandleReconnectRequest` (322–369) performs **no** `_binder.TryGetByConnection` guard — the D-1 guard exists only in `HandleConnectRequest` (line 408).

**Failure sequence.**

1. Socket **X** completes the handshake → session **A**, token **TA**. Binder state: `_byToken{TA→A}`, `_tokenBySession{A→TA}`, `_tokenByConnection{X→TA}`.
2. Socket **X** sends a `ReconnectRequest` for session **B**, which is in `SessionState.Disconnected` (the normal state after a network blip) and for which the client legitimately still holds the 32-byte secret. `SessionManager.TryRebindSession` (line 519) accepts it: the secret matches (fixed-time compare, line 544) and the state is `Disconnected` (line 554).
3. `HandleReconnectRequest` line 339–341: `_binder.Release(B)` (no-op — B was already unbound by `HandleConnectionLost`) then `_binder.Bind(B, …, X)` → issues token **TB** → `_tokenByConnection[X] = TB`, **overwriting TA**. `_byToken` now holds **{TA→A, TB→B}**: one socket, two sessions, two `PlayerId`s.
4. **Consequence A — roster corruption.** Both A and B are `SessionState.Connected` with distinct `PlayerId`s in the same match. One client now commands two players. `BoundCount` (=2) is what the `MaxPlayers` gate at line 414 reads, so a single socket consumes two player slots and the server rejects legitimate clients with `ConnectDenyReason.ServerFull` while the roster is *not* actually full.
5. **Consequence B — the D-1 guard unbinds itself.** The client now closes A gracefully: `HandleGracefulDisconnect` → `_binder.Release(A)`. `Release` (line 100) resolves A→TA, removes `_byToken[TA]`, and then calls `_tokenByConnection.Remove(X)` — but `_tokenByConnection[X]` now holds **TB**, so B's connection mapping is destroyed while B's binding survives in `_byToken`/`_tokenBySession`.
6. `TryGetByConnection(X)` now returns **false** → the D-1 guard at `ServerTransportHost.cs:408` **no longer fires** for socket X → X can send a fresh `ConnectRequest` and be granted a *third* session. The invariant D-1 was created to enforce ("one connection may own exactly one session") is now violable by the legitimate client itself.

**Proof.** `Bind` and `Release` are not inverses. Let `B = Bind(c, s)` and `B' = Bind(c, s')` with `c` fixed. After `B'`: `_tokenByConnection[c] = token(s')`, `_byToken ⊇ {token(s), token(s')}`, `_tokenBySession ⊇ {s→token(s), s'→token(s')}`. `Release(s)` executes `_tokenByConnection.Remove(c)`, which removes `token(s')` — a token that `Release(s)` does not own. Therefore `Release` destroys a binding it does not own, and the map `connection → token` becomes non-injective only in the *destructive* direction: `token → connection` is still injective, `connection → token` is not.

**Remediation (minimal).**
1. In `TransportSessionBinder.Bind`, before writing, release any existing binding that owns `connectionId` — or reject the bind and return `false`, making the "one connection, one session" rule explicit and enforced at the only place that can enforce it.
2. In `TransportSessionBinder.Release`, remove the connection mapping only when it still points at the released token:
   `if (_tokenByConnection.TryGetValue(binding.ConnectionId, out var mapped) && mapped == token) _tokenByConnection.Remove(binding.ConnectionId);`
3. In `ServerTransportHost.HandleReconnectRequest`, apply the same guard as `HandleConnectRequest` (line 408) — deny a `ReconnectRequest` from a connection that already owns a binding — and re-check `_binder.BoundCount >= SimulationConstants.MaxPlayers` before the rebind.

---

### C-01 — P1 (Critical) — Keyframe slice assembly never validates that the slices of one generation agree; a truncated baseline is installed as complete

| | |
|---|---|
| **File** | `Assets/_GlobalFront/Runtime/Client/Replication/ClientTransportReplicationBridge.cs` |
| **Lines** | 447–464 (generation admission), 466–473 (slice admission), 501–520 (completeness check) |
| **Symbol** | `ClientTransportReplicationBridge.HandleKeyframeSlice` |
| **Invariant violated** | *"Keyframe slices arriving out-of-order over UDP must be staged and assembled in strict canonical ascending `PartIndex` order (0..PartCount-1) **without unit desynchronization**."* |

**Defect.** The assembly validates `PartCount` and `TotalUnitCount` **only on the first slice** (lines 449–450, inside the `if (!_assemblyActive)` block). For every subsequent slice the admission test (466–468) compares `header.PartIndex` against the **assembly's** `_assemblyPartCount` and never against the slice's own `header.PartCount`; and the completeness test (line 513) compares the assembled record count against the **last-arriving slice's** `header.TotalUnitCount`, not against the total declared by the generation. The assembly therefore enforces *no* cross-slice agreement at all. The generation's own `PartCount` / `TotalUnitCount` are never stored.

**Failure sequence.**

1. The server emits keyframe generation **7** with `TotalUnitCount = 1024`, `PartCount = 3`: part0 = units 1–409, part1 = 410–818, part2 = 819–1024.
2. A slice is injected or corrupted so that it carries `KeyframeSeq = 7`, `Tick = T` (identical to the genuine slices), `PartIndex = 2`, but `PartCount = 2` (a lie) and `TotalUnitCount = 615`, with 206 records.
3. Line 434: `header.KeyframeSeq == _assemblySeq` → the generation check passes. Lines 449–450 are **skipped** because `_assemblyActive` is already true → the `PartCount`/`TotalUnitCount` bounds are never re-applied.
4. Line 466–468: `header.Tick == _assemblyTick` ✔; `header.PartIndex (2) >= _assemblyPartCount (3)` → false ✔ (the bound is the assembly's count, never the slice's own); staging bound ✔ → the slice is **accepted**.
5. `_assemblyReceivedCount` reaches 3 == `_assemblyPartCount` → the assembly completes. Replay (502–511) yields `orderedCount = 409 + 206 + 0 = 615`.
6. Line 513: `orderedCount (615) != (int)header.TotalUnitCount (615)` → **false** → the "corrupt assembly" guard does not fire.
7. Line 531: `_receiver.ReceiveKeyframe(nowMs, 7, T, _keyframeUnits.AsSpan(0, 615))` → the client installs a baseline of **615 of 1024 units**. `HasBaseline` becomes true, so no re-baseline is ever requested; the server's subsequent deltas are computed against the server's own 1024-unit base and never re-ADD the 409 missing entities. The client is **permanently desynchronised**, detectable only by the 1 Hz state checksum — and even that path tolerates the first mismatch (see C-03).

**Proof.** The only completeness relation the implementation enforces is `Σ(records of parts 0..PartCount-1) == header.TotalUnitCount(last slice)`. The sender of the last slice chooses `TotalUnitCount` freely, so the relation is satisfiable for *any* truncated unit set. The bounds `PartCount ≤ MaxParts` and `TotalUnitCount ≤ MaxKeyframeUnits` (lines 449–450) run only on slice 0 and are therefore properties of the generation, not of the assembled content.

**Exploit precondition.** The transport carries no authentication (ADR-009 explicitly defers it: *"the token is NOT authentication … authentication remains deferred"*), so a party able to deliver a UDP datagram to the client — an on-path/LAN injector, or a malicious or buggy server — can forge the slice. Random bit corruption alone does **not** trigger it: a corrupted `PartCount` alone leaves the assembly's `_assemblyPartCount` intact and the completeness relation still holds.

**Remediation (minimal).** Persist the generation's framing in dedicated fields (`_assemblyDeclaredPartCount`, `_assemblyDeclaredTotalUnitCount`) set at creation, and on **every** slice reject (count malformed + `ResetAssembly()`) any slice whose `header.PartCount != _assemblyDeclaredPartCount` or `header.TotalUnitCount != _assemblyDeclaredTotalUnitCount`. Compare `orderedCount` against `_assemblyDeclaredTotalUnitCount`, not against `header.TotalUnitCount`.

---

### S-02 — P2 (Medium) — `_pendingAcks` is the only remote-influenced structure in the transport without an explicit bound

| | |
|---|---|
| **File** | `Assets/_GlobalFront/Runtime/Server/Transport/ServerTransportHost.cs` |
| **Lines** | 35 (declaration), 117–127 (`DrainPendingAcks`), 210–216 (purge on disconnect), 595–626 (`SendCommandAck`) |
| **Symbol** | `ServerTransportHost.SendCommandAck` / `ServerTransportHost.DrainPendingAcks` |

**Defect.** When `_carrier.Send` fails, `SendCommandAck` allocates a fresh `byte[]` copy of the ack and appends it to `_pendingAcks` (lines 617–624). `DrainPendingAcks` retries every pump and **never drops** an entry that keeps failing (117–127). There is no cap.

**Failure sequence.** `OwnDatagramCarrier.Send` returns `false` when `connection.Unacked.Count >= TransportProtocol.ReceiveWindow` (line 225) or when the outstanding span reaches `ReorderWindow` (line 235). A peer that keeps sending commands while never acknowledging the server's reliable traffic keeps the server's outbound window permanently full; every accepted command then produces one more queued ack, each a separate heap allocation. Every other remote-influenced structure in the codebase is explicitly capped — `MaxQueuedReceiveEvents` (4096), `MaxFragmentGroups` (4), `MaxConnections` (64), `MaxTrackedPreHandshakePeers` (256), `ReassemblyBudgetBytes` (2 MB) — which makes this omission an inconsistency rather than a deliberate design choice.

**Bound.** The leak is indirectly capped: the ARQ path closes the connection after `MaxRetransmits` (10) with exponential backoff (`200→400→800→1000…`, ≈8.6 s), and `HandleConnectionLost` purges the connection's acks (lines 210–216). The practical ceiling is therefore `rate-limit (200 pkt/s) × ≈8.6 s ≈ 1,700 acks` per connection, ≈50 KB — with 64 connections ≈3.2 MB. Not a memory-exhaustion DoS today, but the invariant is enforced by accident rather than by design.

**Remediation.** Add `MaxPendingCommandAcks` (e.g. 256). On overflow, drop the **oldest** entry, count it in `_carrier.Metrics.DroppedQueueOverflow`, and surface it as a diagnostic. Purge the acks of a connection on the carrier's connection-count sweep rather than only on the `Disconnected` event (see S-05).

---

### S-03 — P2 (Medium) — The D-4 zero-GC remediation was applied to one carrier only; the reference carrier allocates per fragment and per datagram

| | |
|---|---|
| **File** | `Assets/_GlobalFront/Runtime/Server/Transport/OwnDatagramCarrier.cs` |
| **Lines** | 405 (`var fragment = new byte[fragmentLength];`), 411 (`var item = new PendingItem`), 423 (`connection.Unacked.Add(item)`), 816 (`var payload = new byte[envelope.PayloadLength];`), 872 (`var payload = new byte[envelope.PayloadLength];`) |
| **Symbol** | `OwnDatagramCarrier.SendReliableMessage` / `HandleReliableDatagram` / `DeliverPayload` |

**Defect.** The C0/C1 reliable send path allocates **two objects per fragment** (a `byte[]` copy of the fragment and a `PendingItem` class instance) and the receive path allocates **one `byte[]` per received datagram**, on top of the `ReceivedDatagram` class instance at line 819. The D-4 fix introduced a reusable `_sendScratch` in `LiteNetLibCarrier` (lines 49–51) and a pooled `StaleIdsScratch`, and the sprint-2.2 test asserts 0 `GC.Alloc` samples for the LiteNetLib **C2 send** path only (`Assets/_GlobalFront/Tests/EditMode/LiteNetLibCarrierIntegrationTests.cs:526–639`).

**Why this is a gap, not a nit.** The project's own zero-GC gate measures one carrier and one channel. `Assets/_GlobalFront/Tests/EditMode/TransportPerformanceBaselineTests.cs:170` *prints* `managed allocations: … KB` for a run that exercises exactly the OwnDatagramCarrier C0/C1 path, but asserts no bound on it. The invariant is therefore unenforced for the carrier that the deterministic test-suite — and every reconnect, flow-control, security and lifecycle test — actually runs on.

**Severity calibration.** `OwnDatagramCarrier` is instantiated only from test code (`grep OwnDatagramCarrier.Create` → 22 hits, all under `Assets/_GlobalFront/Tests/`); the production carrier is `LiteNetLibCarrier` (ADR-009 / OD-1). So this is not a shipping P1. It is, however, the reference implementation of the carrier contract, and it fails the same gate the adopted carrier passes.

**Remediation.** Reuse a per-connection fragment buffer pool and keep the unacked ring as a preallocated struct array (the `PendingItem` fields are all value types — it does not need to be a class). Extend the `LiteNetLibCarrierIntegrationTests` zero-GC gate to the own carrier's C0/C1 send and receive paths so the invariant becomes carrier-wide and regression-proof.

---

### C-02 — P2 (Medium) — The client's default REMOVE sink is smaller than the server's REMOVE budget: a legal delta is dropped as "malformed" and never repaired

| | |
|---|---|
| **File** | `Assets/_GlobalFront/Runtime/Client/Replication/ClientTransportReplicationBridge.cs` |
| **Lines** | 82 (`DefaultRemoveSink = 256`), 68–73 (the config's own contract), 252 (sink allocation), 381–393 (`HandleDelta`) |
| **Related** | `Assets/_GlobalFront/Runtime/Server/Replication/ServerReplicationEmitter.cs` line 19 (`DefaultRemoveCapacity = 512`); `Assets/_GlobalFront/Runtime/Server/Replication/ReplicationChangeSetBuilder.cs` lines 54–62, 261–269; `Assets/_GlobalFront/Runtime/Core/Snapshot/DeltaSnapshotWireCodec.cs` line 353 |
| **Symbol** | `ClientTransportReplicationBridgeConfig.DefaultRemoveSink` / `ClientTransportReplicationBridge.HandleDelta` |

**Defect.** The bridge's own documentation states the contract it then violates: *"Decode sinks must never be smaller than the sender's budgets"* (lines 68–73). The server sizes its change-set builder with `DefaultRemoveCapacity = 512` (`ServerReplicationEmitter.cs:19`) and the merge succeeds up to exactly 512 removes (`ReplicationChangeSetBuilder.cs:262–268` returns `CapacityExceeded` only *beyond* that). The client's default sink is **256** (`ClientTransportReplicationBridge.cs:82`).

**Failure sequence.**

1. 512 units die inside one 20 Hz tick interval; the merged change-set holds 512 REMOVE records.
2. Wire size: 512 × `RemoveRecordMinSizeBytes` (2, `DeltaSnapshotProtocol.cs:71`) = 1024 B + 36 B header ≈ 1060 B ≤ `MaxPacketBytes` (8228, `DeltaSnapshotProtocol.cs:55`). The emitter's own guard (`ServerReplicationEmitter.cs:600–611`) passes and the delta is sent.
3. Client: `DeltaSnapshotWireCodec.TryDecode` (line 353) evaluates `removes.Length (256) < header.RemoveCount (512)` → returns `DeltaCodecResult.BufferTooSmall`.
4. `HandleDelta` lines 389–392: `result != Ok` → `_malformedPayloadCount++` → the entire delta is **discarded**. No repair is queued, `LastAppliedTick` does not advance, and the failure is recorded as *malformed*, not as a gap — so nothing in the health metrics distinguishes it from bit rot.
5. The next delta's `BaseTick` now exceeds the client's `LastAppliedTick` → the FSM enters `CatchingUp` → a `DeltaResume` is requested → the cumulative answer is *still* larger than the sink → dropped again → the episode escalates to a full re-baseline (a multi-slice 16 KB keyframe) instead of a 1 KB delta.

**Proof.** Max legal REMOVE count in one delta = min(server change-set capacity 512, payload bound ⌊8192/2⌋ = 4096) = **512** > `DefaultRemoveSink` = **256**. The ADD sink is safe (`AddRecordSizeBytes = 40` ⇒ ⌊8192/40⌋ = 204 ≤ 256) and the UPDATE sink is safe (`UpdateRecordMinSizeBytes = 2` ⇒ ⌊8192/2⌋ = 4096 ≤ 4096). Only the REMOVE sink is inconsistent with the sender. (`DeltaSnapshotProtocol.cs:52, 65, 68, 71`.)

**Remediation.** Raise `DefaultRemoveSink` to the sender's budget (`public const int DefaultRemoveSink = 512;`). More importantly, distinguish *sink too small* from *corrupt*: on `DeltaCodecResult.BufferTooSmall` queue a repair (`_receiver.Fsm.ReportBaselineLoss(nowMs)` / a `DeltaResume`) instead of counting the packet as malformed — a sink that is too small is a local configuration fault, not evidence of a hostile peer.

---

### C-03 — P2 (Medium) — The state checksum is verified *after* the delta is applied and *after* the tick is acknowledged as clean progress

| | |
|---|---|
| **File** | `Assets/_GlobalFront/Runtime/Client/Replication/ClientReplicationReceiver.cs` |
| **Lines** | 356–395 (record application), 397–400 (`NoteApplied` on the FSM **and** the feedback generator), 402–423 (the checksum block), 413 (`>= 2` escalation threshold) |
| **Symbol** | `ClientReplicationReceiver.ReceiveDelta` |

**Defect.** The verify step is ordered *after* the mutation it is supposed to police, and the acknowledgement is emitted *before* the verification. A checksum mismatch is proof that the client mirror is wrong, yet the first mismatch is tolerated and the tick is acknowledged as a healthy, gap-free advance.

**Failure sequence.**

1. The server sends the delta for tick **500** with `Flags.HasChecksum`; one 40-byte ADD record is corrupted in flight (a position off by 1 mm). `header.StateChecksum` is the server's correct fingerprint.
2. Lines 356–395 apply the records to `_world` — the corrupt position is now in the mirror.
3. Line 398: `_fsm.NoteApplied(500)` → `LastAppliedTick = 500`.
4. Line 399: `_feedback.NoteApplied(500, base)` → the pending C0 ack is a **clean progress ack for tick 500** (no gap, no `BaseStale` flag). The server sets `client.LastAckedTick = 500` — the shared delta base advances to a state the client does not hold.
5. Lines 404–412: the checksum mismatches → `_consecutiveChecksumMismatches = 1` → `ChecksumMismatchCount++` → the `>= 2` test at line 413 is **false** → nothing further happens; the function returns `Applied` (line 425).
6. The client keeps the corrupt position and keeps acking healthy progress. If the next checksummed delta (every 20 ticks) is also lost or corrupt, the corrupt mirror persists indefinitely — the server will never re-send the corrected ADD, because the server believes the client already holds tick 500.

**Proof.** Ordering in the source: apply (356–395) precedes verify (402–423); ack (399) precedes verify. The escalation threshold `>= 2` means exactly one corrupt checksummed delta is always applied **and** acknowledged as healthy.

**Remediation.** Verify **before** acknowledging: move `_feedback.NoteApplied(header.Tick, …)` (line 399) to *after* the checksum block, and either drop the `>= 2` threshold or, on the first mismatch, call `_fsm.ReportBaselineLoss(nowMs)` and return `RebaseRequested` without acknowledging the tick. Ideally, decode into the world only after the checksum is known to agree — e.g. compute the expected checksum over a scratch mirror and commit on match.

---

### P3 (Minor) findings

| ID | File / Lines | Defect | Remediation |
|---|---|---|---|
| **S-04** | `Runtime/Server/Sessions/SessionManager.cs` — `CloseSession` 618–642, `OnTickCompleted` 695–732, `TryRebindSession` 563–575 | Closed sessions are marked `SessionState.Closed` but **never removed** from `_sessions`; `CreateSession` (185–204) always adds. The dictionary grows monotonically for the server's lifetime. Not on a hot path (`HasConnectedSessions`, the only O(N) scan, has no runtime caller), but it is an unbounded server-side structure. | Remove the entry from `_sessions` when it closes (keep a tombstone only if a closed session must remain resolvable). |
| **S-05** | `Runtime/Server/Transport/ServerTransportHost.cs` 206, 245–249; `LiteNetLibCarrier.cs` 843–856; `OwnDatagramCarrier.cs` 923–936 | The rate limiter is removed **only** when a `Disconnected` event is delivered, but both carriers **drop** events when the bounded queue is full (`DroppedQueueOverflow`). A dropped `Disconnected` leaks the rate limiter *and* the connection's pending acks permanently. | Guarantee delivery of the disconnect event (a separate non-droppable control queue), or sweep per-connection state against the carrier's live connection count each pump. |
| **S-06** | `Runtime/Server/Transport/MessageCodec.cs` 181–182 | `header.PayloadLength = length - headerSize` is computed relative to `length`, not to `offset + headerSize`. Correct only while `offset == 0` (the sole current call site); with `offset != 0` the payload length is overstated by `offset` bytes and consumers read past the declared payload. Latent. | `header.PayloadLength = length - (offset + headerSize);` |
| **S-07** | `Runtime/Server/Transport/RateLimiter.cs` 49–60 | `if (_lastRefillMs == 0) { _lastRefillMs = nowMs; }` uses `0` as the "never refilled" sentinel. A transport clock that starts at `0` keeps `_lastRefillMs == 0`, so the first real refill is deferred by one call and the token buckets behave as a one-shot burst. | Use a separate `bool _hasRefilled` flag (or `long.MinValue` as the sentinel). |
| **S-08** | `Runtime/Server/TickDriver.cs` 65–74, 192–218 | `Tick` reports the **last scheduled** tick of the current step, not the tick being simulated: `_tick = _clock.Tick` is assigned before the `TickDue` loop, so while the host simulates tick `baseTick+1`, `Tick` already returns `baseTick+scheduled` — a *future* tick. D-8's stated goal (remove the data race) **is** achieved: the read is under `_gate` and `baseTick` is captured under the same lock. No current caller misreads it (`LocalMatchHost.OnDriverTickDue`, line 608, uses the event parameter). Latent footgun. | Either assign `_tick` to the tick being dispatched (updating it after the handler returns), or rename to `LastScheduledTick` and add `CurrentTick` with dispatch-time semantics. |
| **S-09** | `Runtime/Server/Transport/ServerTransportHost.cs` 537–551 | `HandlePing` decodes the pong with `transportEvent.Length` (the whole datagram) instead of `header.PayloadOffset + header.PayloadLength`. `TryDecodePingPong` only checks `length - offset >= 8`, so trailing bytes beyond the declared payload are accepted — a frame the rest of the codec would reject. | Pass `header.PayloadOffset + header.PayloadLength` as the length. |
| **C-04** | `LiteNetLibCarrier.cs` 680, 748–755; `OwnDatagramCarrier.cs` 405, 816, 872; `ClientTransportEndpoint.cs` 316–320 | The carrier allocates one exact-size array per **delivered message** and the endpoint then copies the same bytes again into its pooled ring — a double copy whose first copy is pure waste. Note the project's own zero-GC contract explicitly excludes the carrier ("only the transport carrier itself (outside this layer) allocates per datagram", bridge lines 178–180), so this is an efficiency observation, **not** an invariant violation. | Let the endpoint own the carrier's array and copy *from* it, or have the assembler write into a caller-supplied pooled buffer. |
| **C-05** | `ClientTransportReplicationBridge.cs` 184–192, 562–571; `ServerReplicationEmitter.cs` 22, 25, 28, 354–358, 433–434, 797 | `AssemblyTimeoutMs = 1500` is calibrated in its own comment against "a full 1024-unit keyframe", but the bridge is configured for `MaxKeyframeUnits = ClientReplicationWorld.DefaultCapacity = 4096`. The server paces one 16,384-byte slice per `⌈16384/2048⌉ = 8 ticks = 400 ms`; a 4096-unit keyframe is 10 slices ≈ 4,000 ms. The client ages the assembly out after ~4 slices (~1,600 ms), so **a 4096-unit keyframe can never be assembled** — the FSM re-requests, the server starts a new generation, and the episode ends in the terminal `ConnectionFailed` state. At the production cap (`MaxUnitsPerMatch = 1024`, 3 slices ≈ 1,200 ms) the margin is only 300 ms (25 %). | Derive the window from the sender's pacing and the configured capacity, or carry it in the slice header / negotiate it. Raise it above the 4096-unit worst case (≈4 s) and add a test that assembles a 4096-unit keyframe at the sender's real pacing. |
| **C-06** | `ClientReplicationReceiver.cs` 413–421 | The checksum-mismatch path builds an interpolated `string` and calls `UnityEngine.Debug.LogWarning` — an allocation plus one of the most expensive Unity calls, on a path a corrupt stream can reach on **every** checksummed packet (every 20 ticks), flooding the log. | Log at most once per episode (gate on a counter reset in `Reset()`), or move the diagnostic to the existing `ChecksumMismatchCount` counter and let the HUD read it. |
| **C-07** | `ClientReplicationReceiver.cs` 234–242 | `_world.Reset()` runs **before** the capacity check, so a keyframe that does not fit the table destroys the client's existing (valid) mirror — although the enum documentation claims the error is "detected before any record is applied". | Evaluate `HasCapacityFor(units.Length)` before `_world.Reset()`. |
| **C-08** | `Runtime/Server/Transport/ClientTransportEndpoint.cs` 31–33, 42, 45–47, 57–60 | The client's snapshot ring is sized from the transport's absolute ceiling: `SnapshotBufferBytes = MaxMessageBytes − 13 − 8 = 1,048,555`, × 4 slots = **4.19 MB per endpoint**, plus a 1 MB `_messageBuffer` (≈5.2 MB per client), for payloads that can never exceed 16,420 B (a keyframe slice is `24 + 409×40 = 16,384` B). | Size the ring from the snapshot protocol bound (`KeyframeSliceCodec.GetSliceSize(...)` vs `DeltaSnapshotProtocol.MaxPacketBytes`) — ≈16.5 KB per slot. |

---

## 3. Verification of Remediations D-1 … D-9

Every prior defect was re-checked at the site named in the remediation record, **and** at every other site that could re-introduce it.

| ID | Sev | Claimed fix | Verdict | Evidence |
|---|---|---|---|---|
| **D-1** | P0 | Full-roster socket hijack — `_binder.TryGetByConnection` duplicate-connection rejection | **CLOSED at the connect path — INCOMPLETE overall** | The guard is present and correctly ordered: `ServerTransportHost.cs:408` runs it *after* the version check (contract) and *before* any `SessionId`/`PlayerId`/roster slot is consumed (lines 414–436). `SessionManager.TryRebindSession` compares the secret in fixed time (`SessionManager.cs:539–547`, `ReconnectWireCodec.FixedTimeEquals`) and requires `SessionState.Disconnected` (line 554), so an unauthenticated peer cannot rebind a live session. **But** the guard is absent from the **reconnect** path (`ServerTransportHost.cs:322–369`) and the binder's `Bind`/`Release` are not inverses → **S-01**. Remediation: **incomplete**. |
| **D-2** | P1 | Unbounded session registry O(N) tick scan → dedicated `_disconnectedSessions` index (O(k) grace sweep) | **CLOSED** | `SessionManager.cs:82` (`HashSet<SessionId> _disconnectedSessions`), added on `NotifyConnectionLost` (418), removed on `CloseSession` (628), `TryReconnect` (499), `TryRebindSession` (566/589) and the expiry sweep (729). `OnTickCompleted` (695–732) iterates **only** the index, collects into `_expiredSessionBuffer` and mutates the set **after** the iteration — no mutation-during-enumeration. `LastTickScannedSessions` (147, 698–705) makes the bound observable, and `SessionManagerTests.cs:294–308` asserts it. ✅ |
| **D-3** | P1 | Out-of-order slice corruption → staging buffer with canonical `PartIndex` copy | **CLOSED for ordering — INCOMPLETE for framing** | `ClientTransportReplicationBridge.cs:475–479` rejects a duplicate `PartIndex`; line 467 bounds `PartIndex < _assemblyPartCount`; `_assemblyReceivedCount` counts **distinct** indices, so `_assemblyReceivedCount == _assemblyPartCount` implies every index in `0..PartCount-1` was staged exactly once; the replay loop (502–511) emits the parts in strict ascending `PartIndex` order and its `Array.Copy` (507) cannot overflow because `orderedCount ≤ _assemblyUnitCount ≤ _assemblyUnits.Length`. **But** the slices of one generation are never cross-checked → **C-01**. Remediation: **incomplete**. |
| **D-4** | P1 | Zero-GC transport path violations → reusable locked `_sendScratch`, `StaleIdsScratch` pooling, in-place datagram reading | **CLOSED for `LiteNetLibCarrier` — NOT enforced carrier-wide** | `LiteNetLibCarrier.cs:49–51` (reusable `_sendScratch`), 294–340 (the send is fully covered by `_sendGate`, write + `Send` inside the lock, so concurrent messages cannot interleave), 73/724–741 (per-peer reused `StaleIdsScratch`, collect-then-mutate), 576 (`GetRemainingBytesSegment`, in-place, no copy). The gate is proven by `LiteNetLibCarrierIntegrationTests.cs:526–639` (100 fragmented snapshots ⇒ **0** `GC.Alloc` samples, with a control proving the recorder observes a known allocation). **But** the own carrier still allocates per fragment / per datagram and no gate measures it → **S-03**. Remediation: **partial**. |
| **D-5** | P2 | Rate-limiter leak on connection drop | **CLOSED on the normal path** | `ServerTransportHost.cs:206` removes the limiter in `HandleConnectionLost`, before the early return at line 220 (so the limiter is freed even when the socket never bound a session — the exact case the comment describes). Carrier-side state is freed too: `LiteNetLibCarrier.RemovePeerState` (386–392) clears the mapping, the limiter, the snapshot state; `OwnDatagramCarrier.SweepPendingRemovals` (320–333) clears the connection, the endpoint map and the per-connection limiter/assembler. **Caveat:** the cleanup is event-driven and the carriers drop events under queue pressure → **S-05**. |
| **D-6** | P2 | Pending-ACK leak on connection drop | **CLOSED on the normal path, structure still unbounded** | `ServerTransportHost.cs:210–216` purges every ack whose `ConnectionId` matches, before the binder lookup. **But** `_pendingAcks` itself has no cap and `DrainPendingAcks` never drops a persistently failing entry → **S-02**. Remediation: **partial**. |
| **D-7** | P2 | Hung assembly → `AssemblyTimeoutMs` aging | **CLOSED** | `ClientTransportReplicationBridge.cs:192` (`AssemblyTimeoutMs = 1500`), 562–571 (`AgeAssembly`), invoked from `Pump` (311) **and** before each slice is admitted (432) — so a late slice cannot revive a closed generation; it starts a fresh one. The clock is the same injected monotonic `nowMs` used by `Pump` and by the slice handler, so a stalled clock stalls the receiver uniformly (no partial-state hazard). `ResetAssembly` (545–554) clears every per-generation field. **Caveat:** the constant is not derived from the sender's pacing and is invalid for the configured capacity → **C-05**. |
| **D-8** | P2 | Unlocked `Tick` read and racy `_tick` read during tick invocation → locked property + step `baseTick` capture under `_gate` | **CLOSED (race removed)** | `TickDriver.cs:65–74` — `Tick` now takes `lock (_gate)`. `AdvanceRealTime` (192–218) captures `baseTick = _tick - (ulong)scheduled` **inside** the same lock as `_clock.Advance` and `_tick = _clock.Tick`, then raises `TickDue` **outside** the lock. `FixedStepClock.Advance` (`FixedStepClock.cs:46–77`) advances `Tick` by exactly `executedTicks` and returns that count, so `_clock.Tick − scheduled` is the tick preceding the step and **cannot underflow** (`_clock.Tick ≥ scheduled` always). The reentrant-`Reset` case is pinned by `TickDriverTests.cs:131` ("D-8: Reset from inside the first TickDue handler must not renumber the step"). **Caveat:** `Tick` still reports the last *scheduled* tick rather than the tick being simulated → **S-08** (latent). |
| **D-9** | P2 | Unbounded `SeqByMessageId` fragment tracking map → `MaxFragmentGroups` cap + oldest-sequence eviction | **CLOSED** | `LiteNetLibCarrier.cs:689–693` — a **new** `messageId` is admitted only after `SeqByMessageId.Count >= MaxFragmentGroups` triggers `EvictOldestFragmentGroup` (764–787), which discards the partial assembly **and** its map entry together. `OwnDatagramCarrier.cs:676–680` + `EvictOldestSnapshotGroup` (752–775) is the mirror implementation. `FragmentAssembler` adds a second, independent bound: `MaxFragmentGroups` (line 114), `MaxFragmentsPerGroup` (line 103), `MaxMessageBytes` (line 155) and `ReassemblyBudgetBytes` (line 126, with `EvictUntilBudget`). The eviction order uses `SerialArithmetic.Diff` (RFC 1982 wrap-around, `SerialArithmetic.cs:16–20`), so it stays correct across a `ushort` sequence wrap. Verified by the sprint-2.4 tests (`ReliableCarrierTests.cs`, `LiteNetLibCarrierIntegrationTests.cs`). ✅ |

**Summary:** D-2, D-5, D-7, D-8, D-9 are **fully closed**. D-1, D-3, D-4 and D-6 are **closed at the named site but incomplete in principle** — each one has a sibling path or a carrier that was not covered by the same fix, and those gaps are exactly S-01, C-01, S-03 and S-02.

---

## 4. Zero-GC & Concurrency Assessment

### 4.1 Zero-GC on hot paths

**What is provably clean (verified by reading the code and by the project's own `GC.Alloc` gate):**

- **`LiteNetLibCarrier` C2 send path** — fully allocation-free in steady state: one reusable `_sendScratch` (49–51) refilled per fragment inside `_sendGate` (294–340), `WriteUInt16` writing the prefix in place, no per-fragment array. Proven by `LiteNetLibCarrierIntegrationTests.cs:526–639` (0 `GC.Alloc` samples over 100 fragmented snapshots, with a positive control at line 520).
- **`LiteNetLibCarrier` C2 receive bookkeeping** — the stale-group scan reuses a per-peer `StaleIdsScratch` (73), collects before mutating (724–741), and the eviction path reuses the same list.
- **`ClientTransportReplicationBridge`** — no `new`, no LINQ, no closures, no boxing, no `params`, no `ToArray()`, no string interpolation in `HandleSnapshotPayload` / `HandleDelta` / `HandleKeyframeSlice` (350–543). All decode sinks, the assembly staging area and the part bookkeeping are preallocated fixed arrays (250–259). `out`/`in` parameters are structs, so nothing is boxed.
- **`ClientReplicationReceiver` / `ClientReplicationWorld`** — the checksum uses a preallocated `_checksumSlots` scratch with an in-place heapsort (`ClientReplicationWorld.cs:457–549`), producing canonical ascending entity-id order with no LINQ and no interface-enumerator boxing.
- **`ReplicationReceiverFSM` / `ReplicationFeedbackGenerator`** — a fixed-size request ring with latest-wins overflow (`ReplicationReceiverFSM.cs:377, 885–907`); no allocations.
- **`TickDriver`** — the `TickDue` invocation is outside the lock and the delegate is read once; no per-tick allocation.

**Where the invariant is not met:**

| Path | Allocation | Note |
|---|---|---|
| `OwnDatagramCarrier.SendReliableMessage` | `new byte[fragmentLength]` **per fragment** + `new PendingItem` **per fragment** | **S-03** — the D-4 fix was not applied here. Not the production carrier, but the reference one, and the gate does not measure it. |
| `OwnDatagramCarrier` C0/C1 receive | `new byte[envelope.PayloadLength]` per datagram + `new ReceivedDatagram` | **S-03** — same gap. |
| `LiteNetLibCarrier` / `OwnDatagramCarrier` message delivery | one exact-size array per delivered message | **C-04** — outside the project's declared zero-GC boundary (the carrier is explicitly excluded by the bridge's contract); a double copy, not a contract breach. |
| `ServerTransportHost.SendCommandAck` | `new byte[offset]` per ack whose send failed | Cold path; bounded in practice by the ARQ timer → **S-02**. |
| `TransportSessionBinder.Bind` | `new byte[8]` per bind | Cold path (handshake/rebind); no impact. |
| `SessionManager.CreateSession` | `Guid.NewGuid()` + a `SessionRecordInternal` | Cold path; intentional (cryptographic identity). |
| `ClientReplicationReceiver.ReceiveDelta` | interpolated string + `Debug.LogWarning` | **C-06** — remotely reachable on every corrupt checksummed packet. |

**Conclusion.** The **production** hot path (LiteNetLib C2 send + the whole client replication layer) does satisfy "0 B/tick, 0 B/packet" for the paths the project has chosen to measure. The invariant is **not** enforced at the carrier-contract level: the own carrier — the one every deterministic, reconnect, security and lifecycle test runs on — allocates per fragment and per datagram on its C0/C1 paths, and `TransportPerformanceBaselineTests` prints the allocation total without asserting a bound. The invariant is currently a property of *one carrier and one channel*, not of the carrier contract.

### 4.2 Concurrency & thread safety

**Sound:**

- **`TickDriver`** — every mutable field (`_tick`, `_mode`, `_isPaused`) is read and written under `_gate`; `TickDue` is raised outside the lock, and the step's index base is captured under the same lock that scheduled it, so a reentrant `Reset` from a handler cannot renumber the step in flight (`TickDriverTests.cs:131`).
- **`LiteNetLibCarrier`** — the worker thread only *enqueues*; all authoritative processing happens on the host thread. `_peerGate` protects `_connectionToPeer`, `_peerToConnection`, `_peerLimiters`, `_snapshotStates` and `_pendingAdmissions`; `_eventGate` protects the queue; `_sendGate` serialises the shared send scratch. Lock order is `_peerGate → _sendGate`, never the reverse. `RemovePeerState` (386–392) removes the mapping, the limiter and the snapshot state together, so no map can outlive its peer. `CloseConnection` removes the state *before* `peer.Disconnect()`, so the worker's `OnPeerDisconnected` sees an unknown peer and does not double-report.
- **`OwnDatagramCarrier`** — single-threaded by construction (host thread only). Close paths triggered from inside `Pump` defer their map removal to `_pendingRemovals` and sweep at the end of `Pump` (320–333), so no close path mutates the collection being iterated. `ProcessAcks` and `ProcessTimers` iterate `connection.Unacked` by index and remove from the tail.
- **Client replication layer** — no cross-thread mutable state at all: the carrier's worker enqueues, the endpoint dequeues and dispatches on the pumping thread, and the bridge, receiver, FSM and feedback generator all run on that one thread. No locks are needed and none are taken.

**Residual concerns (all P3 or already captured above):**

1. **`TransportSessionBinder` is not thread-safe** and is documented as host-thread-only — correct as written, but `Bind`/`Release` are *not* logically consistent even single-threaded (**S-01**).
2. **`LiteNetLibCarrier.SendSnapshotFragmented`** reads `peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced)` outside `_peerGate` while LiteNetLib's MTU-discovery worker may resize the peer's MTU concurrently. The value is only used to size a chunk, and the clamp to `FragmentPayloadBytes` bounds it, so the effect is a suboptimal chunk size at worst — not a correctness failure.
3. **`LiteNetLibCarrier.EnqueueEvent` drops events under queue pressure**, including `Disconnected` (**S-05**), which is the mechanism by which the transport's per-connection cleanup can be skipped.
4. **`TickDriver.Tick` semantics** are a lookahead rather than a "current tick" (**S-08**) — safe today because every caller uses the `TickDue` parameter.

---

## 5. Recommended Remediation Order

| Sprint | Scope | Defects |
|---|---|---|
| **2.5** | Binder correctness + reconnect guard | **S-01** (P1) — make `Bind`/`Release` inverses; add the one-connection-one-session guard to `HandleReconnectRequest`; re-check `MaxPlayers`. |
| **2.6** | Keyframe framing agreement | **C-01** (P1) — store and enforce the generation's `PartCount`/`TotalUnitCount` on every slice; compare against the stored values. |
| **2.7** | Client/server sink alignment + checksum ordering | **C-02**, **C-03** (P2) — `DefaultRemoveSink = 512`; distinguish `BufferTooSmall` from corrupt; verify before acknowledging; drop the `>= 2` tolerance. |
| **2.8** | Bound + zero-GC closure | **S-02**, **S-03** (P2) — cap `_pendingAcks`; extend the zero-GC gate to `OwnDatagramCarrier` C0/C1 and remove its per-fragment allocations. |
| **2.9** | Hygiene | **S-04 … S-09**, **C-04 … C-08** (P3). |

**Exit criteria for the next certification:** 2 × P1 = 0, 2 × P2 = 0, all new findings covered by regression tests, and the EditMode/PlayMode baseline re-verified at ≥ 1159 / 16.

---

*Audit performed against `e6d91eafe65c1fdd65122f21e3e8c49f233b9293`. Every line reference in this report was read from the source at that commit. No file was modified.*
