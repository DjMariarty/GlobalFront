# GlobalFront Roadmap

> Утверждённая последовательность Product Definition → GlobalFront 1.0

## Current Position

- Phase 1 — COMPLETE
- Phase 2 — CURRENT
- Phase 2.1 — COMPLETE (`8178110`)
- Phase 2.2 — COMPLETE (`313e9ef`)
- Phase 2.3 — **NEXT**

## Phase Roadmap

| Phase | Результат | Milestone | Статус |
|---:|---|---|---|
| 0 | Product Definition: vision, GDD, 1.0 scope и границы post-1.0 | — | master scope approved; detailed TBD remain |
| 1 | Simulation Foundation | M0 Technical Foundation | **COMPLETE** |
| 2 | Multiplayer Foundation | prerequisite для M1 | **CURRENT** |
| 3 | RTS Core + Vertical Slice пяти фракций | M2 Playable RTS Core | planned |
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
| 2.1 | Server-Owned Match State | **COMPLETE** — `8178110` |
| 2.2 | Command Channel | **COMPLETE** — `313e9ef` |
| 2.3 | Server Tick Driver | **NEXT** |
| 2.4 | Session / Player Identity | planned |
| 2.5 | Network Transport | planned |
| 2.6 | Snapshot Networking | planned |
| 2.7 | Reconnect / Resync | planned |

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