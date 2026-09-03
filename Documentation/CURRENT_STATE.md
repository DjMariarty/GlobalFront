# GlobalFront Current State

> Быстрый handoff для AI-агента • обновлено 2026-09-03

## Current Phase

**Phase 2 — Multiplayer Foundation**

## Current Task

**Phase 2.6 — Snapshot Networking: Implementation (Шаг 2.6.3: Client Replication Receiver & FSM).**

Шаг 2.6.2 Server Replication Engine & History Ring — **COMPLETE**: 429/429 EditMode тестов green, Zero-GC hot path валидирован, добавлены 74 новых теста репликации. Текущая задача — Phase 2.6, Шаг 2.6.3: Client Replication Receiver & FSM в `GlobalFront.Client`.

## Last Commit

`028e646` — `feat(core): implement delta snapshot wire codec (Phase 2.6 step 1)` (предыдущий: `dadc593` — `feat: implement network transport (Phase 2.5)`)

## Tests

- EditMode: **429/429 passed** (`Artifacts/TestResults/editmode-phase26-step2.xml`, 2026-09-03; включая 74 новых теста репликации и проверку Zero-GC hot path Шага 2.6.2)
- PlayMode: **6/6 passed** (`Artifacts/TestResults/playmode-phase26-step2.xml`, 2026-09-03)
- Unity: `6000.5.6f1`

## Completed Milestones

- M0 Technical Foundation — COMPLETE
- Phase 1 Simulation Foundation — COMPLETE
- Phase 2.1 Server-Owned Match State — COMPLETE (`8178110`)
- Phase 2.2 Command Channel — COMPLETE (`313e9ef`)
- Phase 2.3 Server Tick Driver — COMPLETE (`ea0435d`, ADR-007)
- Phase 2.4 Session / Player Identity — COMPLETE (`73d1276`, ADR-008)
- Phase 2.5 Network Transport — COMPLETE (ADR-009; LiteNetLib 1.3.5 за контрактом `INetworkCarrier`; финальный независимый review = APPROVE, P0=0/P1=0/P2=0)
- Phase 2.6 Snapshot Networking R&D Gate — COMPLETE (Revision 2; независимый аудит DeepSeek V4 Pro = APPROVE, P0=0/P1=0/P2=0; ADR-010 Accepted)
- Phase 2.6 Step 2.6.1 Core Delta Wire Codec — COMPLETE (355/355 EditMode passed; Zero-GC hot path валидирован)
- Phase 2.6 Step 2.6.2 Server Replication Engine & History Ring — COMPLETE (429/429 EditMode passed; 74 новых теста репликации; Zero-GC hot path валидирован)

## Next Step

Phase 2.6, шаг 2.6.3: Client Replication Receiver & FSM в `GlobalFront.Client` по принятому ADR-010. Reconnect/resync — Phase 2.7.

## Important Constraints

- Current runtime — local prototype; `LocalMatchHost` (ServerHost) работает в клиентском процессе и владеет `MatchServer`, `TickDriver` (engine-independent, в `GlobalFront.Server`) и `SessionManager` (Phase 2.4, ADR-008).
- Server tick lifecycle отделён от Unity client lifecycle (ADR-007). Session/player identity реализованы как server-authoritative layer (ADR-008): PlayerId назначается только сервером, command ingress проходит session gate; конкретная grace duration — TBD. Транспортная архитектура определена ADR-009 (OD-1 = LiteNetLib за контрактом `INetworkCarrier`); транспорт реализован и проверен (детерминированный `VirtualNetworkPipe` + real-UDP LiteNetLib loopback) и не владеет simulation: `MatchServer`/`TickDriver`/`SessionManager`/`CommandHeader`/Snapshot Protocol v1 не изменены. Отдельного dedicated server process и reconnect/resync пока нет; тот же driver и simulation core будут использованы future dedicated host.
- Snapshot Protocol v1 реализован и передаётся по сети как opaque payload (C2, unreliable sequenced, latest-wins по `SnapshotTick`). Шаги 2.6.1 и 2.6.2 реализовали и верифицировали Core `DeltaSnapshotWireCodec`, Server Replication Engine и History Ring; Client Replication Receiver & FSM — текущий Шаг 2.6.3.
- Product 1.0 включает пять основных фракций; их gameplay/content ещё не реализован.
- Подфракции не входят в 1.0.
- Не считать будущий roadmap фактом реализации.

## Read Next

- [Phase 02](Phases/Phase_02_Multiplayer.md)
- [Architecture](ARCHITECTURE.md)
- [Project Status](PROJECT_STATUS.md)
