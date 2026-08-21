# GlobalFront Current State

> Быстрый handoff для AI-агента • обновлено 2026-08-21

## Current Phase

**Phase 2 — Multiplayer Foundation**

## Current Task

**Phase 2.4 — Session / Player Identity: implementation complete, commit pending**

Design зафиксирован в ADR-008 (`DECISIONS.md`). Реализация и тесты завершены (см. Tests); commit выполняется ответственным участником после review. Match lifecycle в 2.4 завершается на `Finished`; `MatchPhase.Closed` (teardown/registry disposal) — reserved future state для dedicated/server lifecycle. Следующая задача после commit — Phase 2.5 Network Transport.

## Last Commit

`ea0435d` — `feat: implement server tick driver (Phase 2.3)`

## Tests

- EditMode: **223/223 passed** (`Artifacts/TestResults/editmode-phase24.xml`, 2026-08-21; 184 baseline + 39 Phase 2.4)
- PlayMode: **5/5 passed** (`Artifacts/TestResults/playmode-phase24.xml`, 2026-08-21)
- Unity: `6000.5.6f1`

## Completed Milestones

- M0 Technical Foundation — COMPLETE
- Phase 1 Simulation Foundation — COMPLETE
- Phase 2.1 Server-Owned Match State — COMPLETE (`8178110`)
- Phase 2.2 Command Channel — COMPLETE (`313e9ef`)
- Phase 2.3 Server Tick Driver — COMPLETE (`ea0435d`, ADR-007)
- Phase 2.4 Session / Player Identity — implemented и проверен; commit pending (ADR-008)

## Next Step

Review и commit Phase 2.4 (implementation и tests готовы, ADR-008 зафиксирован), затем Phase 2.5 Network Transport.

## Important Constraints

- Current runtime — local prototype; `LocalMatchHost` (ServerHost) работает в клиентском процессе и владеет `MatchServer`, `TickDriver` (engine-independent, в `GlobalFront.Server`) и `SessionManager` (Phase 2.4, ADR-008).
- Server tick lifecycle отделён от Unity client lifecycle (ADR-007). Session/player identity реализованы как server-authoritative layer (ADR-008): PlayerId назначается только сервером, command ingress проходит session gate; конкретная grace duration — TBD. Отдельного dedicated server process, transport, snapshot networking и reconnect/resync пока нет; тот же driver и simulation core будут использованы future dedicated host.
- Snapshot Protocol v1 реализован, но ещё не передаётся по сети.
- Product 1.0 включает пять основных фракций; их gameplay/content ещё не реализован.
- Подфракции не входят в 1.0.
- Не считать будущий roadmap фактом реализации.

## Read Next

- [Phase 02](Phases/Phase_02_Multiplayer.md)
- [Architecture](ARCHITECTURE.md)
- [Project Status](PROJECT_STATUS.md)