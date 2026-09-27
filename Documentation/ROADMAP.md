# GlobalFront Roadmap

> Утверждённая последовательность Product Definition → GlobalFront 1.0

## Current Position

- Phase 1 — [DONE/PASSED]
- Phase 2 (2.1–2.8) — [DONE/PASSED] (Grand Audit [APPROVED: ZERO DEFECTS] на `a37f53d`; 661/661 EditMode, 6/6 PlayMode)
- Phase 3 — [IN PROGRESS] (RTS Camera, Controls, Visuals): Шаги 3.1–3.6 **[DONE/PASSED / AUDITED: ZERO DEFECTS]**, Шаг 3.7 **[READY / NEXT]**; baseline 1043/1043 EditMode, 6/6 PlayMode (`2a754db`)

## Phase Roadmap

| Phase | Результат | Milestone | Статус |
|---:|---|---|---|
| 0 | Product Definition: vision, GDD, 1.0 scope и границы post-1.0 | — | master scope approved; detailed TBD remain |
| 1.0–2.8 | Simulation + Multiplayer Foundation (2.1–2.8, Grand Audit remediation F-01/F-02) | M0 Technical Foundation + prerequisite для M1 | **[DONE/PASSED]** |
| 2 | Multiplayer Foundation | prerequisite для M1 | **[DONE/PASSED]** |
| 3 | RTS Core + Vertical Slice пяти фракций (RTS Camera, Controls, Visuals) | M2 Playable RTS Core | **[IN PROGRESS]** — 3.1–3.6 certified, 3.7 next |
| 4 | Multiplayer Gameplay Integration | M1 Multiplayer Prototype | planned |
| 5 | Full Five Factions | M3 Five Factions | planned |
| 6 | AI | M4 RTS AI | planned |
| 7 | Content + Maps + Presentation | — | planned |
| 8 | Scale / Performance / Reliability | M5 Scale Prototype | planned |
| 9 | Alpha | M6 Alpha | planned |
| 10 | Beta | M7 Beta | planned |
| 11 | Release Candidate | release gate | planned |
| 12 | GlobalFront 1.0 | M8 Release 1.0 | planned |

## Phase 2 Sequence

| Step | Scope | Status |
|---:|---|---|
| 2.1 | Server-Owned Match State | **[DONE/PASSED]** — `8178110` |
| 2.2 | Command Channel | **[DONE/PASSED]** — `313e9ef` |
| 2.3 | Server Tick Driver | **[DONE/PASSED]** |
| 2.4 | Session / Player Identity | **[DONE/PASSED]** |
| 2.5 | Network Transport | **[DONE/PASSED]** |
| 2.6 | Snapshot Networking | **[DONE/PASSED]** |
| 2.7 | Reconnect / Resync | **[DONE/PASSED]** |
| 2.8 | Network Prototype Playtest (2v2) | **[DONE/PASSED]** |

## Phase 3 Sequence

| Step | Scope | Status |
|---:|---|---|
| 3.1 | RTS Camera & Input | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `59ef38a` → `8144541`, `2b71414` |
| 3.2 | UnitViewTickBuffer & Interpolation (OD-23) | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `6ab12ad` → `eac0e1d` |
| — | OD-29 UnitKind Replication & UnitCatalog | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `be03002` → `27a10f6` |
| 3.3 | UnitViewBinder & Object Pooling (OD-26) | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `bcb1e78` → `0243971`, `e69bed1` |
| 3.4 | Instanced Selection Rings & HP Bars (OD-25) | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `27a10f6` → `b897eea` |
| 3.5 | Selection, Screen-Space Drag-Box & Command Issuing (OD-24) | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `cfa24c2` |
| 3.6 | Minimap & Tactical HUD (OD-24, OD-27, OD-28) | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `2a754db` (1043/1043 EditMode, 6/6 PlayMode) |
| 3.7 | Vertical Slice Integration & 400-Unit Benchmark (OD-23..OD-29) | **[READY / NEXT]** |

Phase 3 закрывает M2 Playable RTS Core: 3.1–3.6 — презентационный стек целиком, 3.7 — его интеграция и профильный замер OD-28 (400 активных юнитов при стабильных 60+ FPS на средних ПК).

## Cross-Phase Rules

- Performance benchmarks начинаются в Phase 3; Phase 8 выполняет глубокую optimization/reliability работу.
- Все пять основных фракций получают representative vertical slice в Phase 3 и развиваются параллельно до полного roster в Phase 5.
- Phase 4 соединяет сетевой фундамент Phase 2 с реальным RTS gameplay Phase 3.
- Фазы могут пересекаться, но завершение фиксируется только по Definition of Done соответствующего Phase-файла.

## Product 1.0 Boundary

1.0 включает пять основных фракций, 1v1–5v5, FFA, до 10 игроков, AI, reconnect/resync, replay, desync detection, большие армии, 3000+ target, многочасовые матчи, production-quality presentation и стабильный server.

Подфракции, новые фракции/режимы, advanced modding, ranked, spectator expansion и неутверждённая campaign относятся к post-1.0.

## Связанные документы

- [Master Game Plan](MASTER_GAME_PLAN.md)
- [Phases](Phases/README.md)
- [Current State](CURRENT_STATE.md)