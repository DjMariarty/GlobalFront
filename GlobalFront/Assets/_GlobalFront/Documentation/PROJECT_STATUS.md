# GlobalFront — Status Report (Stage 01 Audit)

**Date:** 2026-08-03
**Unity:** 6000.5.6f1 (URP)
**Project root:** `Assets/_GlobalFront/`

---

## 1. Executive Summary

Deterministic 20 Hz simulation core and headless authoritative server logic are implemented and fully covered by tests (89/89 passing). Client-side unit selection (9 tests) and command queue pipeline (18 tests) are now tested. The client is a playable local prototype. **The critical gap: client and server are not connected** — the client applies commands locally without using `MatchServer`, and there is no transport, snapshot serialization, or client prediction layer. ECS (DOTS) is not used anywhere despite being mentioned in roadmap comments.

---

## 2. Assembly Structure

| Assembly | References | Engine refs | Purpose |
|---|---|---|---|
| `GlobalFront.Core` | — | **none** | Pure deterministic simulation (int math, commands, combat) |
| `GlobalFront.Server` | Core | **none** | Headless authoritative `MatchServer` |
| `GlobalFront.Client` | Core, Unity.InputSystem | yes, `!UNITY_SERVER` | Prototype presentation + local command application |
| `GlobalFront.Tests.EditMode` | Core, Server, Client | Editor only | 89 unit tests |

Dependency graph is clean: Core has zero Unity references; Server is headless-compatible. Tests now reference Client and cover `UnitSelection` and `PrototypeUnit` behaviour.

---

## 3. Module Inventory

### 3.1 Core (`Runtime/Core/`, ~1050 LOC, 10 files)

| File | Role |
|---|---|
| `WorldPointMm.cs` | Integer millimetre coordinates, bounds check |
| `PlanarMovement.cs` | Integer-step movement with exact sqrt (no floats) |
| `CombatSimulation.cs` | `CombatStats`, `CombatantState`, `CombatTickResolver` (canonical EntityId ordering, simultaneous damage resolution), `CombatMath` (squared distance, overflow-safe) |
| `CommandHeader.cs` | `CommandHeader` validation: tick window, sequence, type; `GameCommandType` enum |
| `MoveCommand.cs` / `AttackCommand.cs` | Immutable validated command payloads |
| `FormationLayout.cs` | Deterministic formation slot assignment |
| `FixedStepClock.cs` | 20 Hz fixed-step clock with backlog tracking and interpolation alpha |
| `SimulationConstants.cs` | Max players, world bounds |
| `Identifiers.cs` | `EntityId` (ulong), `PlayerId` (byte, 1..MaxPlayers) |

**Key invariants verified:** all math is integer; no `float` in simulation path; combat resolved in canonical EntityId order; damage applied simultaneously after all shots committed.

### 3.2 Server (`Runtime/Server/`, 480 LOC)

`MatchServer`:
- Validates command headers (tick window: −20..+60 ticks, sequence dedup per player)
- Validates entity ownership and liveness
- Schedules commands per tick, executes in deterministic (PlayerId, Sequence) order
- Movement + combat via Core systems
- `ServerUnitSnapshot` immutable view — **prepared for serialization, but no serializer exists**
- Terminal state: rejects commands when `Outcome.IsTerminal`

### 3.3 Client (`Runtime/Client/`, ~2000 LOC, 9 files)

| File | Role |
|---|---|
| `FixedSimulationRunner.cs` | Drives `FixedStepClock` in `Update()`, fires `TickExecuted` event |
| `PrototypeRtsController.cs` | **Thinned controller (~650 LOC).** Input routing + HUD; delegates command pipeline to `PrototypeCommandQueue` |
| `PrototypeCommandQueue.cs` | **Client command pipeline (~360 LOC).** Builds/validates `MoveCommand`/`AttackCommand`, deterministic (tick, player, sequence) ordering, local application with ownership/liveness checks. Seam for future network interception |
| `UnitRegistry.cs` | Per-frame registry of visible `PrototypeUnit` instances, filtered by local player |
| `UnitSelection.cs` | Screen-rect selection, additive mode, dead/enemy filtering, deterministic EntityId sorting |
| `PrototypeUnit.cs` | Per-unit presentation + local movement/combat state |
| `PrototypeWorldBootstrap.cs` | Procedural scene generation (ground, armies, obstacles) |
| `GlobalFrontRuntimeBootstrap.cs` | `RuntimeInitializeOnLoadMethod` — auto-creates all components on any scene load |
| `RtsCameraController.cs` / `PrototypeHud.cs` | Camera pan/zoom, debug HUD |

### 3.4 Tests (`Tests/EditMode/`, ~1500 LOC, 10 files, 89 tests)

All green. Coverage: movement, combat, commands, formations, clock, MatchServer integration (command validation, tick execution, battle outcome), client-side unit selection (screen-rect, additive, dead/enemy filtering, behind-camera culling, deterministic EntityId sorting), **`PrototypeCommandQueue`** (move/attack enqueue, deterministic ordering, due/future application, friendly-target rejection, facing derivation, sequence increment, clear, override).

---

## 4. Critical Findings

### 4.1 Client–Server Disconnection (Severity: High)

`PrototypeRtsController` **does not reference `GlobalFront.Server` at all** (verified: zero imports of `MatchServer` or `GlobalFront.Server` in Client assembly). The client independently:
- Applies commands with its own validation window (`ValidateAtTick(tick, 2, 0)` vs server's `−20/+60`)
- Runs enemy AI (`AcquireAutomaticTargets`) that does not exist on the server
- Moves units toward attack targets (`AdvanceTowardsAttackTarget`) — server only moves toward formation slots

**Consequence:** when a network layer is added, client and server states will diverge immediately. The prototype must be rewired to consume `ServerUnitSnapshot` from `MatchServer` rather than simulating locally.

### 4.2 Missing Systems for Stage 01 Completion

| System | Status |
|---|---|
| Deterministic 20 Hz core | ✅ Done, tested |
| Authoritative server logic | ✅ Done, tested |
| Server hosting / tick loop | ❌ Not started |
| Network transport | ❌ Not started |
| Snapshot serialization | ❌ Not started (`ServerUnitSnapshot` exists, no codec) |
| Client prediction / reconciliation | ❌ Not started |
| ECS (DOTS) server world | ❌ Not started (only mentioned in `FixedSimulationRunner` doc comment) |
| Stop command on client | ❌ Server supports it; client never sends it |
| GameContent / assets | ❌ Empty folders |

### 4.3 Unimplemented Command Types

`GameCommandType` declares: `AttackMove`, `Guard`, `Build`, `Produce`, `UseAbility`. None have payloads, server handlers, or client support. Only `Move`, `Attack`, `Stop` are functional.

### 4.4 Client Hardcoded Values

- `FormationSpacingMm = 3200` (3.2 m) — not in `SimulationConstants`
- `AutoAcquireRangeMm = 18000` (18 m) — client-only AI parameter
- Local player is always `PlayerId(1)`; no player identity negotiation

### 4.5 No Scene Artifacts

`SampleScene.unity` contains **zero** GlobalFront components. All setup is code-driven via `RuntimeInitializeOnLoadMethod`. No prefabs, no ScriptableObjects, no game data assets.

---

## 5. Code Quality

- **Naming/style:** consistent, no magic numbers outside documented constants
- **Immutability:** commands and snapshots are readonly structs
- **Overflow safety:** explicit checks in `CombatMath.SquaredDistance`, `CommitShot`, damage accumulation
- **Determinism:** canonical sorting everywhere (EntityId for combat, PlayerId+Sequence for commands)
- **Progress:** `PrototypeRtsController.cs` further split — command pipeline extracted into `PrototypeCommandQueue` (~360 LOC, 18 tests); controller now ~650 LOC focusing on input routing + HUD
- **Concern:** controller still owns auto-acquire AI, movement and combat presentation; splitting these before network integration remains recommended

---

## 6. Recommended Next Steps

1. **Continue splitting `PrototypeRtsController`** — extract auto-acquire AI, movement/combat presentation into dedicated components (command pipeline already extracted to `PrototypeCommandQueue`)
2. **Create `LocalMatchHost`** that runs `MatchServer` in-process and exposes `ServerUnitSnapshot[]` per tick — first integration step without transport
3. **Add snapshot serializer** (fixed-size binary, int/ulong only) for `ServerUnitSnapshot`
4. **Rewire client** to consume server snapshots; keep local input queue as prediction only
5. **Move AI and auto-acquire** to server-side (or define client-only "cosmetic" scope explicitly)
6. **Add PlayMode tests** for the client boot path and tick event flow
7. **Populate `SimulationConstants`** with formation spacing and auto-acquire range