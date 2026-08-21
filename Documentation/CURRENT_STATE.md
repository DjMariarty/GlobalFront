# GlobalFront Current State

> Быстрый handoff для AI-агента • обновлено 2026-08-21

## Current Phase

**Phase 2 — Multiplayer Foundation**

## Current Task

**Phase 2.3 — Server Tick Driver: implementation complete, commit pending**

Design зафиксирован в ADR-007 (`DECISIONS.md`). Реализация и тесты завершены (см. Tests); commit выполняется ответственным участником после review. Следующая задача после commit — Phase 2.4 Session / Player Identity.

## Last Commit

`313e9ef` — `feat: implement Command Channel Abstraction (Phase 2.2)`

## Tests

- EditMode: **184/184 passed** (`EditModeTestResults.xml`, 2026-08-21; 161 baseline + 23 Phase 2.3)
- PlayMode: **5/5 passed** (`PlayModeTestResults.xml`, 2026-08-21)
- Unity: `6000.5.6f1`

## Completed Milestones

- M0 Technical Foundation — COMPLETE
- Phase 1 Simulation Foundation — COMPLETE
- Phase 2.1 Server-Owned Match State — COMPLETE (`8178110`)
- Phase 2.2 Command Channel — COMPLETE (`313e9ef`)
- Phase 2.3 Server Tick Driver — implemented и проверен; commit pending (ADR-007)

## Next Step

Review и commit Phase 2.3 (implementation и tests готовы, ADR-007 зафиксирован), затем Phase 2.4 Session / Player Identity.

## Important Constraints

- Current runtime — local prototype; `LocalMatchHost` (ServerHost) работает в клиентском процессе и владеет `MatchServer` и `TickDriver` (engine-independent, в `GlobalFront.Server`).
- Server tick lifecycle отделён от Unity client lifecycle (ADR-007): `TickDriver` — manual/real-time 20 Hz с bounded catch-up; `FixedSimulationRunner` удалён. Отдельного dedicated server process, sessions/player identity, transport, snapshot networking и reconnect/resync пока нет; тот же driver и simulation core будут использованы future dedicated host.
- Snapshot Protocol v1 реализован, но ещё не передаётся по сети.
- Product 1.0 включает пять основных фракций; их gameplay/content ещё не реализован.
- Подфракции не входят в 1.0.
- Не считать будущий roadmap фактом реализации.

## Read Next

- [Phase 02](Phases/Phase_02_Multiplayer.md)
- [Architecture](ARCHITECTURE.md)
- [Project Status](PROJECT_STATUS.md)