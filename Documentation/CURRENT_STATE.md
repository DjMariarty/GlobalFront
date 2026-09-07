# GlobalFront Current State

> Быстрый handoff для AI-агента • обновлено 2026-09-03

## Current Phase

**Phase 2 — Multiplayer Foundation**

## Current Task

**Phase 2.6 — Snapshot Networking: COMPLETE (P1-1..P1-5 закрыты). Текущая задача — Independent Review Gate Re-check (DeepSeek).**

Шаг 2.6.4 End-to-End Integration & Impairment Tests — **COMPLETE**: 599/599 EditMode тестов green (558 baseline + 41 интеграционный тест с P1-1..P1-5). Замечания аудита P1-1 (KeyframeRef на проводе), P1-2 (StateChecksum 1 Hz и Zero-GC клиентская верификация с авто-восстановлением через SnapshotRequest), P1-3 (Zero-GC в ClientTransportEndpoint через кольцевой пул), P1-4 (Idle keep-alive дельта каждые 20 тиков для предотвращения ложного ребейза), P1-5 (проводка `LocalMatchHost.AttachReplication`) полностью устранены и верифицированы.

## Last Commit

`09366d5` — `feat(replication): complete end-to-end integration and impairment tests (Phase 2.6 step 4)`

## Tests

- EditMode: **599/599 passed** (`EditModeTestResults.xml`, 2026-09-07; 558 baseline + 41 интеграционный тест с P1-1..P1-5; Zero-GC hot path эмиттера, моста, клиента и транспорта валидирован)
- PlayMode: **6/6 passed** (`Artifacts/TestResults/playmode-phase26-step4.xml`, 2026-09-04)
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
- Phase 2.6 Step 2.6.3 Client Replication Receiver & FSM — COMPLETE (`c49eeda`; 558/558 EditMode passed; 129 новых тестов; Zero-GC hot path клиента валидирован)
- Phase 2.6 Step 2.6.4 Full Integration & Impairment Tests — COMPLETE (597/597 EditMode passed; 39 новых интеграционных тестов; Zero-GC hot path эмиттера и моста валидирован)

## Next Step

Независимый аудит реализации Phase 2.6 аудитором DeepSeek V4 Pro. После утверждения — переход к Phase 2.7 (Reconnect / Resync) и Phase 2.8 (Network Prototype Playtest 2v2).

## Important Constraints

- Current runtime — local prototype; `LocalMatchHost` (ServerHost) работает в клиентском процессе и владеет `MatchServer`, `TickDriver` (engine-independent, в `GlobalFront.Server`) и `SessionManager` (Phase 2.4, ADR-008).
- Server tick lifecycle отделён от Unity client lifecycle (ADR-007). Session/player identity реализованы как server-authoritative layer (ADR-008): PlayerId назначается только сервером, command ingress проходит session gate; конкретная grace duration — TBD. Транспортная архитектура определена ADR-009 (OD-1 = LiteNetLib за контрактом `INetworkCarrier`); транспорт реализован и проверен (детерминированный `VirtualNetworkPipe` + real-UDP LiteNetLib loopback) и не владеет simulation: `MatchServer`/`TickDriver`/`SessionManager`/`CommandHeader`/Snapshot Protocol v1 не изменены. Отдельного dedicated server process и reconnect/resync пока нет; тот же driver и simulation core будут использованы future dedicated host.
- Snapshot Protocol v1 реализован и передаётся по сети как opaque payload (C2, unreliable sequenced, latest-wins по `SnapshotTick`). Шаги 2.6.1 и 2.6.2 реализовали и верифицировали Core `DeltaSnapshotWireCodec`, Server Replication Engine и History Ring; Шаг 2.6.3 добавил клиентский приёмник (`ClientReplicationReceiver`, `ClientReplicationWorld`, `ReplicationReceiverFSM`, `ReplicationFeedbackGenerator` в `GlobalFront.Client.Replication`) и wire-модель `SnapshotAck` в `GlobalFront.Core.Snapshot`. Приёмник пока изолирован: в `LocalMatchHost`, клиентский рендер и транспортный pump он не подключён (Шаг 2.6.4).
- Зафиксированные отклонения/уточнения Шага 2.6.3 (архитектуру не меняют):
  - `SnapshotAck` = **34 байта**: offsets 0/1/2/10/18/26 в сумме дают 34; uplink-бюджет — 340 Б/с при 10 Hz. Арифметическая опечатка исправлена в R&D §4.2–4.3.
  - `KeyframeRef` в delta wire format v1 по-прежнему пишется как `Reserved0` (байты 34..35 заголовка) и не декодируется: Apply-Guard принимает `keyframeRef` явным параметром API. Вынос поля на wire — отдельное решение владельца (Шаги 2.6.1/2.6.4).
  - Возраст keyframe-базы (`Now − BaseKeyframeTick > 120`) не используется как триггер REBASING: при плановом интервале keyframe 15–20 с (OD-11) это давало бы ложный ребейз каждые 6 с. Нормативный триггер — `BaseTick − LastAppliedTick > 120` или несовпадение `KeyframeRef`; правило синхронизировано в R&D §5.1 и связанных сводках.
  - Лимит повторов `DeltaResume` в ADR-010 не зафиксирован (TBD): реализован defensive liveness guard — тот же бюджет 3 попыток, после чего CATCHING_UP эскалирует в REBASING.
  - Multipart-сборка тика (`PartCount > 1`) в Шаге 2.6.3 не применяется и сигнализируется как gap; сборка — Шаг 2.6.4.
- Product 1.0 включает пять основных фракций; их gameplay/content ещё не реализован.
- Подфракции не входят в 1.0.
- Не считать будущий roadmap фактом реализации.

## Read Next

- [Phase 02](Phases/Phase_02_Multiplayer.md)
- [Architecture](ARCHITECTURE.md)
- [Project Status](PROJECT_STATUS.md)
