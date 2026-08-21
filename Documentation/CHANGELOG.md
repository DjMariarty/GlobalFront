# Журнал изменений GlobalFront

> Исторический журнал подтверждённых изменений; будущий roadmap сюда не переносится как реализованный.

## Unreleased

### Documentation

- Документация синхронизирована с утверждённым Master Development Plan.
- Добавлены `MASTER_GAME_PLAN.md`, product `GDD.md` и короткий `CURRENT_STATE.md`.
- Roadmap заменён утверждённым Phase Plan 0–12 и milestones M0–M8.
- Фазовые документы приведены к единой структуре Goal → Definition of Done.
- Зафиксированы пять основных фракций 1.0 и перенос подфракций в post-1.0.
- Current baseline отделён от approved target architecture.

### Technical Milestones

- Phase 2.1 Server-Owned Match State — `8178110`.
- Phase 2.2 Command Channel — `313e9ef`.
- `ICommandChannel` и `LocalCommandChannel` отделяют client command queue от конкретного local host.
- `StopCommand` включён в command channel baseline.
- Phase 2.3 Server Tick Driver — реализован и принят (ADR-007), commit `ea0435d`: engine-independent `TickDriver` в `GlobalFront.Server` (20 Hz, bounded catch-up, manual/real-time); `LocalMatchHost` (ServerHost) владеет `MatchServer` и `TickDriver`; `FixedSimulationRunner` удалён; server tick lifecycle отделён от Unity client lifecycle.
- Phase 2.4 Session / Player Identity — реализована и принята (ADR-008; commit pending): opaque `SessionId`/`MatchId` в Core; `SessionManager` в `GlobalFront.Server` — единственный источник `PlayerId` (монотонное назначение по порядку join, без повторного использования); session gate перед неизменным `MatchServer`; `LocalCommandChannel` session-attributed; клиент получает `PlayerId` от сервера (`ClientSession`); match lifecycle завершается на `MatchPhase.Finished` — `Closed` зарезервирован для будущего server lifecycle/teardown.

### Verified

- Unity `6000.5.6f1`.
- **223/223 EditMode** passed по `Artifacts/TestResults/editmode-phase24.xml`, 2026-08-21 (184 baseline + 39 Phase 2.4).
- **5/5 PlayMode** passed по `Artifacts/TestResults/playmode-phase24.xml`, 2026-08-21.

## Confirmed Foundation History

| Commit | Milestone |
|---|---|
| `313e9ef` | Phase 2.2 Command Channel Abstraction |
| `8178110` | Phase 2.1 Server-Owned Match State через `MatchConfig` |
| `b50f474` | Snapshot Serialization Protocol v1 |
| `f4a496f` | PlayMode validation local authoritative pipeline |
| `8e687e3` | Local authoritative `LocalMatchHost` integration |
| `66bc76b` | Unity repository restructure |

## Historical Git Tags

| Tag | Commit | Context |
|---|---|---|
| `v0.2-matchserver` | `90a4bad` | MatchServer milestone |
| `v0.2.1-project-cleanup` | `4a9ff3a` | project cleanup |
| `v0.2.2-pre-restructure-backup` | `4a9ff3a` | pre-restructure backup |
| `v0.3.0-restructure` | `66bc76b` | repository restructure |

Незавершённые Phase 2.3+ системы добавляются сюда только после implementation, tests, review и принятого commit.

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Project Status](PROJECT_STATUS.md)
- [Roadmap](ROADMAP.md)