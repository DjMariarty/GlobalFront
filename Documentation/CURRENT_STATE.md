# GlobalFront Current State

> Быстрый handoff для AI-агента • обновлено 2026-08-15

## Current Phase

**Phase 2 — Multiplayer Foundation**

## Current Task

**Phase 2.3 — Server Tick Driver: NEXT**

Детальная архитектура 2.3 ещё не утверждена и должна пройти R&D → Architecture Decision до реализации.

## Last Commit

`313e9ef` — `feat: implement Command Channel Abstraction (Phase 2.2)`

## Tests

- EditMode: **161/161 passed** (`EditModeTestResults.xml`, 2026-08-12)
- PlayMode: **5/5 passed** (`PlayModeTestResults.xml`, 2026-08-12)
- Unity: `6000.5.6f1`

## Completed Milestones

- M0 Technical Foundation — COMPLETE
- Phase 1 Simulation Foundation — COMPLETE
- Phase 2.1 Server-Owned Match State — COMPLETE (`8178110`)
- Phase 2.2 Command Channel — COMPLETE (`313e9ef`)

## Next Step

Провести R&D и зафиксировать ADR для Phase 2.3 Server Tick Driver, затем переходить к implementation/tests/review/documentation/commit.

## Important Constraints

- Current runtime — local prototype; `LocalMatchHost` работает в клиентском процессе.
- Отдельного dedicated server loop, sessions/player identity, transport, snapshot networking и reconnect/resync пока нет.
- Snapshot Protocol v1 реализован, но ещё не передаётся по сети.
- Product 1.0 включает пять основных фракций; их gameplay/content ещё не реализован.
- Подфракции не входят в 1.0.
- Не считать будущий roadmap фактом реализации.

## Read Next

- [Phase 02](Phases/Phase_02_Multiplayer.md)
- [Architecture](ARCHITECTURE.md)
- [Project Status](PROJECT_STATUS.md)