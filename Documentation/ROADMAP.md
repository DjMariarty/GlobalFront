# GlobalFront Roadmap

> Утверждённая последовательность Product Definition → GlobalFront 1.0

## Current Position

- Phase 1 — **[DONE/PASSED: ZERO DEFECTS]** (hardening-спринты 1.1–1.5.1 закрыли concurrency, reentrancy, friendly fire и snapshot allocs)
- Phase 2 (2.1–2.8 + Hardening 2.1–2.5) — **[DONE/PASSED: ZERO DEFECTS]** (Grand Re-Audit [APPROVED: ZERO DEFECTS] единогласно сертифицирован 3 независимыми аудиторами: Qwen 3.8 Max, NVIDIA Nemotron 3.5 Super VL, MiniMax M3.1 Flash на commit `4cb8ffa`, отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`)
- Phase 3 (3.1–3.7) — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (RTS Camera, Controls, Visuals, 400-Unit Vertical Slice, OD-28 benchmark: 0.623 ms/frame, 0 B GC.Alloc)
- **Текущий бейзлайн:** **1162/1162 EditMode** + **16/16 PlayMode** = **1178 тестов** (100% green, 0 compiler warnings, 0 compiler errors на `4cb8ffa`)
- **Следующая фаза:** **Phase 4: Multiplayer Gameplay Integration** `[READY / NEXT]`

## Phase Roadmap

| Phase | Результат | Milestone | Статус |
|---:|---|---|---|
| 0 | Product Definition: vision, GDD, 1.0 scope и границы post-1.0 | — | master scope approved; detailed TBD remain |
| 1 | Simulation Foundation (Core, deterministic simulation, hardening 1.1–1.5.1) | M0 Technical Foundation | **[DONE/PASSED: ZERO DEFECTS]** |
| 2 | Multiplayer Foundation (2.1–2.8, transport, replication, reconnect, hardening 2.1–2.5) | prerequisite для M1 | **[DONE/PASSED: ZERO DEFECTS]** (`4cb8ffa`) |
| 3 | RTS Core + Vertical Slice пяти фракций (RTS Camera, Controls, Visuals, 400-unit slice) | M2 Playable RTS Core | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** |
| 4 | Multiplayer Gameplay Integration | M1 Multiplayer Prototype | **[NEXT]** |
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
| 2.3 | Server Tick Driver | **[DONE/PASSED]** — `ea0435d` (ADR-007) |
| 2.4 | Session / Player Identity | **[DONE/PASSED]** — `73d1276` (ADR-008) |
| 2.5 | Network Transport | **[DONE/PASSED]** — ADR-009 (LiteNetLib 1.3.5 за `INetworkCarrier`) |
| 2.6 | Snapshot Networking | **[DONE/PASSED]** — ADR-010 (дельта-сжатие, history ring, keyframe slicing) |
| 2.7 | Reconnect / Resync | **[DONE/PASSED]** — ADR-011 (OD-18..OD-22, тактическая пауза, resync) |
| 2.8 | Network Prototype Playtest (2v2) | **[DONE/PASSED]** — 2v2 topology, abandonment unpause, HUD overlay |
| — | Hardening Sprints 2.1–2.5 | **[DONE/PASSED: ZERO DEFECTS]** — `4cb8ffa`: socket hijack guard, Zero-GC send scratch, canonical PartIndex, thread-safe tick driver, bounded fragment maps, exclusive session-socket binding (N-01 eradicated). Сертификация 3/3 независимых аудиторов. |

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
| 3.7 | Vertical Slice Integration & 400-Unit Benchmark (OD-23..OD-29) | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `3414ba6`, `cb01ea2` (1086/1086 EditMode, 13/13 PlayMode; OD-28: 0.623 ms/frame avg over 120 frames, 0 B GC) |

Phase 3 закрывает M2 Playable RTS Core и **завершена полностью**: 3.1–3.6 — презентационный стек целиком, 3.7 — его интеграция и профильный замер OD-28 (**400 активных юнитов: 0.623 ms/frame среднее за 120 презентационных кадров с включённым авторитетным тиком симуляции, 0 B GC.Alloc** — далеко ниже 8.0 ms EditMode-гарда и потолка 16.67 ms для 60 FPS), плюс 12 инвариантов архитектуры в `ArchitectureInvariantsTests`. Следующая фаза — **Phase 4 (Multiplayer Gameplay Integration)**, которая соединяет сетевой фундамент Phase 2 с принятым RTS-геймплеем Phase 3.

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
- [Architecture](ARCHITECTURE.md)
