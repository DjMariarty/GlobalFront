# GlobalFront Current State

> Быстрый handoff для AI-агента • обновлено 2026-08-21

## Current Phase

**Phase 2 — Multiplayer Foundation**

## Current Task

**Phase 2.5 — Network Transport: IN_PROGRESS (design accepted, implementation not started)**

ADR-009 принят владельцем: OD-1 = LiteNetLib как preferred initial transport carrier за контрактом `INetworkCarrier`, subject to implementation and validation. R&D: `Documentation/Research/Phase_02_05_Network_Transport_RND.md`. LiteNetLib-зависимость не добавлена, implementation не начат. Snapshot cadence/delta — 2.6; reconnect/resync implementation — 2.7.

## Last Commit

`73d1276` — `feat: implement session and player identity (Phase 2.4)`

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
- Phase 2.4 Session / Player Identity — COMPLETE (`73d1276`, ADR-008)
- Phase 2.5 Network Transport — IN_PROGRESS (ADR-009 Accepted, OD-1 = LiteNetLib; implementation не начат)

## Next Step

Implementation Phase 2.5 по ADR-009: ввод LiteNetLib-зависимости (pinned version + ThirdPartyAssetsRegistry) и транспортный контракт — только в рамках implementation; timing constants (OD-8) согласуются при implementation.

## Important Constraints

- Current runtime — local prototype; `LocalMatchHost` (ServerHost) работает в клиентском процессе и владеет `MatchServer`, `TickDriver` (engine-independent, в `GlobalFront.Server`) и `SessionManager` (Phase 2.4, ADR-008).
- Server tick lifecycle отделён от Unity client lifecycle (ADR-007). Session/player identity реализованы как server-authoritative layer (ADR-008): PlayerId назначается только сервером, command ingress проходит session gate; конкретная grace duration — TBD. Транспортная архитектура определена ADR-009 (OD-1 = LiteNetLib за контрактом `INetworkCarrier`); transport implementation не начат. Отдельного dedicated server process, snapshot networking и reconnect/resync пока нет; тот же driver и simulation core будут использованы future dedicated host.
- Snapshot Protocol v1 реализован, но ещё не передаётся по сети.
- Product 1.0 включает пять основных фракций; их gameplay/content ещё не реализован.
- Подфракции не входят в 1.0.
- Не считать будущий roadmap фактом реализации.

## Read Next

- [Phase 02](Phases/Phase_02_Multiplayer.md)
- [Architecture](ARCHITECTURE.md)
- [Project Status](PROJECT_STATUS.md)