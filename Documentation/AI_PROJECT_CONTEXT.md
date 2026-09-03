# GlobalFront — AI Project Context

**Version:** 1.0  
**Purpose:** operational project context for the AI coordinator and implementation/review agents.  
**Important:** this document is a handoff baseline, not a substitute for the repository. The incoming coordinator must verify all current-state claims against Git and the actual project before acting on them.

---

## 1. Project Identity

**Project:** GlobalFront  
**Engine:** Unity 6000.5.6f1  
**Repository:** `https://github.com/DjMariarty/GlobalFront.git`  
**Repository / Unity root:** `C:\UnityProjects\GlobalFront`

The project is a modern, higher-quality implementation of the Generals / Zero Hour formula with a stronger technical foundation, improved stability/optimization, modern presentation, and expanded multiplayer architecture.

### Product scope anchors

- 5 main factions are targeted for 1.0: NATO, Russia, China, GLA, USA.
- Subfactions are post-1.0 and are not a 1.0 blocker.
- Multiplayer target: up to 10 players, including 5v5.
- Long multi-hour matches are a design target.
- Reconnect/resync, replay, and desync diagnostics are 1.0 goals.
- The project values deterministic simulation and server authority.

Do not invent faction rosters, balance values, ability numbers, or unapproved game systems in technical tasks.

---

## 2. Source of Truth

Use this order:

1. repository/Git;
2. accepted ADRs and project docs;
3. actual tests/benchmarks;
4. independent review evidence;
5. AI handoff/report documents;
6. chat history.

This file must be treated as operational context, not as proof that the repository currently matches every statement below.

---

## 3. Known Documentation Structure

The repository documentation is centered on:

```text
Documentation/
├── README.md
├── ARCHITECTURE.md
├── PROJECT_STATUS.md
├── DECISIONS.md
├── CHANGELOG.md
├── ThirdPartyAssetsRegistry.md
└── ...
```

Additional handoff material may exist at repository root, including `AI_HANDOFF.md`.

The project already has an `AI_HANDOFF.md`. It is useful as a state snapshot, but it does **not** replace the coordinator role described by `AI_COORDINATOR.md`.

---

## 4. Architecture Baseline

The approved technical direction is centered on:

```text
Unity Client
    │
    │ Commands
    ▼
LocalMatchHost / Server Host
    │
    ▼
MatchServer
    │
    │ authoritative state / snapshots
    ▼
Client Presentation
```

### Core

`GlobalFront.Core`

- Unity-independent deterministic simulation;
- integer-oriented simulation math;
- deterministic command processing;
- fixed-step simulation;
- canonical ordering where required.

### Server

`GlobalFront.Server`

`MatchServer` is the authoritative simulation authority.

Responsibilities include:

- authoritative state;
- command validation;
- command scheduling;
- deterministic execution;
- movement;
- combat;
- terminal outcome.

### Client

`GlobalFront.Client`

The earlier prototype contained local gameplay simulation. The target architecture is to make the client consume authoritative server state and use the server as the sole source of gameplay state.

The coordinator must verify the **current** client implementation before planning any further migration work because the project has progressed materially beyond the original prototype state.

---

## 5. Deterministic Simulation Contract

Known approved baseline:

- simulation rate: **20 Hz**;
- `TickDriver` owns simulation time;
- `MatchServer` does not own the wall clock;
- command timing uses requested tick semantics;
- deterministic ordering is required;
- terminal match state does not stop the simulation tick counter in the current server-tick design, while terminal snapshots remain stable.

Do not reintroduce an independent simulation clock into `MatchServer` without an explicit architecture decision.

---

## 6. Completed Technical Milestones

The latest confirmed milestone history available to this handoff is:

### Phase 2.1 — Server-Owned Match State

Commit:

```text
8178110 — feat: implement server-owned match initialization via MatchConfig
```

### Phase 2.2 — Command Channel Abstraction

Commit:

```text
313e9ef — feat: implement Command Channel Abstraction (Phase 2.2)
```

### Phase 2.3 — Server Tick Driver

Commit:

```text
ea0435d — feat: implement server tick driver (Phase 2.3)
```

Known architecture:

```text
TickDriver
  ↓
LocalMatchHost / ServerHost
  ↓
MatchServer
```

The tick driver is engine-independent and uses a 20 Hz simulation rate with bounded catch-up.

### Phase 2.4 — Session / Player Identity

Commit:

```text
73d1276 — feat: implement session and player identity (Phase 2.4)
```

ADR-008 was accepted for this phase.

Known semantics:

- server-owned PlayerId assignment;
- monotonic join-order policy;
- no PlayerId reuse within a MatchId;
- SessionId / MatchId / ConnectionHandle abstractions;
- `SessionManager` and `ClientSession` remain outside deterministic simulation;
- reconnect semantics are prepared for the later reconnect/resync phase.

### Regression Fix Found by Runtime Smoke Test

Commit:

```text
296664c — fix: restore enemy auto-acquisition in prototype combat
```

This was important process evidence: an automated suite did not catch a runtime behavior regression that was then found by manual smoke testing. Relevant runtime changes must therefore continue to receive manual verification.

### Phase 2.5 — Network Transport

Commit:

```text
dadc593 — feat: implement network transport (Phase 2.5)
```

This is the latest confirmed technical checkpoint in the available independent-review material.

Known facts from the Phase 2.5 review:

- LiteNetLib **1.3.5** is pinned behind `INetworkCarrier`;
- a custom `OwnDatagramCarrier` also exists behind the same abstraction;
- transport remains additive and engine-independent at the contract boundary;
- C0/C1 provide reliable ordered delivery using a shared reliable sequence space per direction;
- C2 provides unreliable sequenced/latest-wins snapshot-style delivery;
- C2 carries `SnapshotTick` in its envelope;
- fragmentation/reassembly exists;
- transport uses session attribution and async command acknowledgements;
- admission control/rate limiting and relevant flood/churn tests were hardened during Phase 2.5;
- the transport is not itself the authority model and must not be leaked into deterministic simulation/core contracts.

The Phase 2.5 independent review found P0/P1 issues resolved before the accepted checkpoint; remaining notes were informational/P2-level follow-ups.

---

## 7. Current Development Position

### Phase 2.6 — Snapshot Networking

**Status: R&D only. Not approved for implementation.**

This is the current architectural focus.

The repository should be treated as being at the Phase 2.5 accepted baseline until the actual repository proves otherwise.

### Why Phase 2.6 is not yet implementation-ready

The first proposed snapshot model was rejected during independent review because:

- sending large full snapshots over unreliable C2 is statistically ineffective under packet loss;
- cumulative deltas can approach full-state bandwidth during active combat;
- the transport's 256 KB/s ingress anti-DoS reference is not a production snapshot egress budget;
- lifecycle semantics were incomplete;
- client-applied-state feedback was not defined sufficiently;
- reliable keyframe delivery can create head-of-line blocking because C0/C1 share a reliable sequence space;
- catch-up/re-baseline boundaries were underspecified.

The revised R&D direction, currently considered promising but not approved, is a hybrid model:

```text
Reliable keyframe / baseline
        +
Incremental delta stream
        +
Bounded delta history window
        +
Cumulative catch-up inside the window
        +
Reliable re-baseline when the client falls outside the window
```

A candidate 10 Hz snapshot cadence and compact keyframe fragmentation were proposed, but these remain parameters to validate by benchmark rather than fixed implementation facts.

---

## 8. Phase 2.6 Required R&D Revision

Before implementation is authorized, the coordinator must require a new R&D revision that closes the independent-review findings.

Minimum required topics:

### 8.1 Reliable keyframe scheduling

Resolve the interaction between reliable keyframes and the shared C0/C1 reliable sequence space.

Do not silently modify ADR-009 transport semantics.

Investigate scheduling/prioritization or a separately justified logical/physical lane design.

### 8.2 Client state feedback

Define exactly what the server learns about client-applied snapshot state.

At minimum, semantics for:

- last applied snapshot tick;
- keyframe identity/reference;
- gap detection;
- resume requests;
- re-baseline requests.

### 8.3 Snapshot lifecycle semantics

Define explicit:

- ADD;
- UPDATE;
- REMOVE;
- owner-change behavior;
- entity lifecycle and non-reuse rules;
- behavior when an entity disappears between baselines.

### 8.4 Protocol versioning

Define a version space for the delta protocol separately from the existing full Snapshot Protocol v1 unless an ADR explicitly chooses another compatible model.

Mixed-version behavior must be explicit.

### 8.5 Catch-up / re-baseline algorithm

Specify exact rules for:

```text
client gap
   ↓
DeltaResume / SnapshotRequest
   ↓
inside retained history ?
   ├─ yes → cumulative catch-up
   └─ no  → reliable re-baseline
```

Include precise tick/keyframe boundary semantics.

### 8.6 Keyframe fragmentation benchmark

Benchmark at least:

```text
8 KB
16 KB
32 KB
```

Measure latency, fragmentation count, HOL impact, retransmissions, memory, and interaction with command traffic.

### 8.7 Bandwidth benchmark matrix

At minimum evaluate entity counts:

```text
100 / 500 / 1000 / 3000 / 5000
```

Changed fraction:

```text
0 / 1 / 5 / 10 / 25 / 50 / 75 / 100 %
```

Cadence:

```text
5 / 10 / 20 Hz
```

Client counts:

```text
1 / 5 / 10
```

Representative workloads should include idle, movement, large formation movement, combat, and mixed combat.

### 8.8 Filtering / future FoW

The protocol must leave a clean per-client filtering boundary even if Phase 2.6 initially uses an all-visible filter.

### 8.9 Deterministic reconstruction

Define canonical ordering, exact field comparison, and golden-byte tests for deterministic reconstruction.

---

## 9. Important Numeric Facts for R&D

Known R&D estimates included:

- Snapshot Protocol v1 full snapshot size is approximately `16 + 39*N` bytes;
- 3000 entities therefore produce roughly 117 KB per full snapshot before transport overhead;
- full snapshots at 20 Hz are clearly outside the intended bandwidth envelope;
- earlier R&D estimates suggested delta traffic can remain manageable at low changed fractions but can approach hundreds of KB/s in high-activity states.

These numbers are **R&D estimates / code-derived measurements, not acceptance targets** unless explicitly approved and benchmarked.

Do not convert them into hard production budgets without owner approval and measured evidence.

---

## 10. Known Transport Boundaries That Must Be Preserved

The coordinator must preserve the accepted Phase 2.5 boundaries unless a new ADR changes them.

Important invariants:

- simulation/core must not depend directly on LiteNetLib;
- transport is behind `INetworkCarrier`;
- worker-thread transport handling should terminate at a bounded event queue before simulation/identity state is mutated;
- C2 is latest-wins/unreliable-sequenced;
- reliable C0/C1 sequence semantics must be treated as an existing contract;
- transport versioning and message/snapshot protocol versioning are separate concerns;
- command validation and authoritative execution remain server-owned.

---

## 11. Reconnect / Resync Direction

Phase 2.7 is downstream of snapshot semantics.

Do not implement full reconnect/resync semantics before Phase 2.6 defines:

- client snapshot state tracking;
- baseline identity;
- retained delta window;
- gap handling;
- reliable re-baseline behavior;
- relevant protocol versioning.

Reconnect implementation must build on, not redefine, the snapshot model.

---

## 12. Testing Policy

Known project testing expectations:

- EditMode tests for deterministic/core/server contracts;
- PlayMode tests for runtime integration;
- transport integration/stress tests where applicable;
- golden-byte tests for protocol determinism;
- runtime smoke tests for relevant player-visible behavior;
- performance benchmarks for scalability claims.

Never use a stale XML result or historical test count as the current baseline.

The coordinator must obtain the actual current test result from the repository/Unity Test Runner before reporting a milestone as verified.

---

## 13. Known Evidence / Historical Baseline

Useful historical checkpoints:

```text
8178110  Phase 2.1
313e9ef  Phase 2.2
ea0435d  Phase 2.3
73d1276  Phase 2.4
296664c  runtime regression fix
dadc593  Phase 2.5 transport
```

These hashes are historical evidence available to the handoff. The coordinator must still run Git commands to determine the actual HEAD and working-tree state before acting.

---

## 14. Known Existing Handoff Document

`AI_HANDOFF.md` already exists in the project/document history.

Its purpose is to provide a concise operational snapshot and it explicitly states that Git and code are the factual source of truth.

However, the existing handoff material predates the current Phase 2.5/2.6 state and should not be used as the sole description of current work.

The coordinator must reconcile it with the actual repository and update/supplement the relevant documentation rather than blindly following stale next-task instructions.

---

## 15. Immediate Coordinator Action

The incoming coordinator's first action is **not implementation**.

It must produce a Coordinator State Report containing:

```text
1. actual HEAD commit
2. active branch
3. git status / uncommitted changes
4. current documentation state
5. completed phases verified from Git
6. current Phase 2.6 state
7. existing ADR status
8. documentation/repository mismatches
9. open Owner Decisions
10. blocking findings
11. exact next R&D task
```

After that report, the coordinator should prepare the next Phase 2.6 R&D Revision 3 task for the implementation/research agent.

**No Phase 2.6 production implementation should start until the coordinator confirms that the required R&D gate and independent review have passed.**
