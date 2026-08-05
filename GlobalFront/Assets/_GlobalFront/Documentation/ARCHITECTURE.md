# ARCHITECTURE & TECHNICAL DEBT

> Last updated: 2026-08-04

## 1. Overview

GlobalFront is a deterministic real-time strategy prototype built in Unity.
The project is split into three assemblies with strict dependency direction:

```
GlobalFront.Core  ◀──  GlobalFront.Server
                  ◀──  GlobalFront.Client  (depends on Unity Engine)
```

| Assembly | Namespace | Responsibility |
|---|---|---|
| Core | `GlobalFront.Core.*` | Pure C# game logic: simulation, combat, movement, commands. No Unity dependency. |
| Server | `GlobalFront.Server` | Headless authoritative match simulation. References Core only. |
| Client | `GlobalFront.Client` | Unity-bound presentation, input handling, local prediction. References Core + Unity. |

## 2. Determinism Contract

Both client (`PrototypeRtsController`) and server (`MatchServer`) run the **same
per-tick pipeline** so that an identical setup produces identical results:

1. Apply scheduled commands (player-ordered).
2. Clear invalid attack targets (dead or friendly).
3. Auto-acquire closest enemy for units with the flag set.
4. Move units, with attack-target pursuit overriding move orders.
5. Resolve combat via `CombatTickResolver`.

Any divergence between client and server pipelines causes desync. Constants
that affect simulation **must** live in `SimulationConstants` (Core) so both
sides read the same value.

## 3. Key Constants

All protocol- and simulation-critical constants are centralised in
`GlobalFront.Core.Simulation.SimulationConstants`:

| Constant | Value | Notes |
|---|---|---|
| `ServerTickRate` | 20 | Ticks per second. |
| `ServerTickDurationSeconds` | 0.05 | Derived from `ServerTickRate`. |
| `MaxCatchUpTicksPerFrame` | 4 | Prevents spiral-of-death on slow frames. |
| `MaxPlayers` | 10 | Protocol maximum. |
| `MaxSelectedEntities` | 100 | UI/network cap. |
| `MaxFormationSpacingMm` | 100 000 | Validation bound for formation layout. |
| `MaxWorldCoordinateMm` | 1 000 000 000 | World bounds (±1000 km). |
| `AutoAcquireRangeMm` | 18 000 | Radius for automatic enemy acquisition. |

> **Rule:** never duplicate a simulation constant in client or server code.
> Add it to `SimulationConstants` and reference it from there.

## 4. Technical Debt Register

### 4.1 Resolved

| ID | Severity | Description | Resolution |
|---|---|---|---|
| DEBT-001 | Critical | `AutoAcquireRangeMm` duplicated in `MatchServer` (public const) and `PrototypeRtsController` (private const). Risk of desync if one side changes value. | Moved to `SimulationConstants.AutoAcquireRangeMm`. Both sides now reference the Core constant. (2026-08-04) |

### 4.2 Open

| ID | Severity | Description | Proposed Fix |
|---|---|---|---|
| DEBT-002 | High | `PrototypeRtsController` carries local constants (`MillimetresPerMetre`, `FormationSpacingMm`) that may need sharing with server-side formation validation. | Evaluate whether `FormationSpacingMm` should be protocol-level; if so, move to `SimulationConstants`. |
| DEBT-003 | Medium | Client pipeline (`PrototypeRtsController.OnTickExecuted`) and server pipeline (`MatchServer.TickOnce`) are structurally identical but implemented independently. Any change to one must be manually mirrored in the other. | Extract a shared `SimulationPipeline` in Core that both client and server invoke. |
| DEBT-004 | Medium | `PrototypeRtsController` depends on `UnityEngine.GUI` for health bars and selection box. Not testable in EditMode and not ECS-compatible. | Migrate to UI Toolkit or uGUI once the prototype stabilises. |
| DEBT-005 | Low | Tests use hardcoded positions (e.g. `17000` mm for auto-acquire boundary tests) instead of referencing `SimulationConstants.AutoAcquireRangeMm`. If the constant changes, tests won't automatically adjust. | Replace magic numbers in `MatchServerTests` with `SimulationConstants.AutoAcquireRangeMm` (with small deltas for boundary cases). |
| DEBT-006 | Low | No integration test that runs client `PrototypeRtsController` and server `MatchServer` on the same setup and asserts identical tick-by-tick state. | Add a deterministic parity test once the shared pipeline (DEBT-003) is in place. |

## 5. Assembly Dependency Rules

```
GlobalFront.Core
  └─ No dependencies (pure C#).

GlobalFront.Server
  └─ References: GlobalFront.Core
  └─ MUST NOT reference: GlobalFront.Client, UnityEngine

GlobalFront.Client
  └─ References: GlobalFront.Core
  └─ MAY reference: UnityEngine (presentation + input)
  └─ MUST NOT reference: GlobalFront.Server
```

Violations of these rules will be caught by the assembly definition files
(`.asmdef`) at compile time.

## 6. Adding New Simulation Constants

1. Add the constant to `SimulationConstants` in Core.
2. Document its purpose and whether it is protocol-affecting.
3. Reference it from client and/or server code — **never** copy the value.
4. If the constant affects test expectations, update tests to reference it.
5. If the constant is protocol-affecting, bump the protocol version and
   document the change in this file under "Resolved".