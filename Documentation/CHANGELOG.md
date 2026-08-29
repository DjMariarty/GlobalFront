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
- ADR-009 Network Transport Architecture (Phase 2.5) — Accepted (OD-1 = LiteNetLib как preferred initial carrier за контрактом `INetworkCarrier`); R&D-документы в `Documentation/Research/`.

### Technical Milestones

- Phase 2.1 Server-Owned Match State — `8178110`.
- Phase 2.2 Command Channel — `313e9ef`.
- `ICommandChannel` и `LocalCommandChannel` отделяют client command queue от конкретного local host.
- `StopCommand` включён в command channel baseline.
- Phase 2.3 Server Tick Driver — реализован и принят (ADR-007), commit `ea0435d`: engine-independent `TickDriver` в `GlobalFront.Server` (20 Hz, bounded catch-up, manual/real-time); `LocalMatchHost` (ServerHost) владеет `MatchServer` и `TickDriver`; `FixedSimulationRunner` удалён; server tick lifecycle отделён от Unity client lifecycle.
- Phase 2.4 Session / Player Identity — реализована и принята (ADR-008), commit `73d1276`: opaque `SessionId`/`MatchId` в Core; `SessionManager` в `GlobalFront.Server` — единственный источник `PlayerId` (монотонное назначение по порядку join, без повторного использования); session gate перед неизменным `MatchServer`; `LocalCommandChannel` session-attributed; клиент получает `PlayerId` от сервера (`ClientSession`); match lifecycle завершается на `MatchPhase.Finished` — `Closed` зарезервирован для будущего server lifecycle/teardown.
- Phase 2.5 Network Transport — COMPLETE, `feat: implement network transport (Phase 2.5)`, по ADR-009 (OD-1 = LiteNetLib); финальный независимый review = **APPROVE** (P0=0/P1=0/P2=0): контракт `INetworkCarrier`; собственный детерминированный `OwnDatagramCarrier` (конверт `CarrierVersion`, единое reliable-пространство C0/C1, separate sequenced C2 без ACK, serial arithmetic, receive/reorder windows, cumulative+selective ACK, EWMA RTO, keepalive/idle на `ITransportClock`, per-peer rate limiting, bounded reassembly: ≤2 MB/peer, ≤4 группы, ≤2048 фрагментов, lifetime 2000 ms) поверх `VirtualNetworkPipe` (seeded loss/dup/reorder/latency/corruption/partition); `LiteNetLibCarrier` — LiteNetLib 1.3.5 (precompiled netstandard2.0 DLL в `Assets/_GlobalFront/ThirdParty/LiteNetLib`, интеграция через `precompiledReferences`) с worker-потоком, bounded event queue, app-level C2 fragmentation по MTU пира и carrier-level admission control (bounded `MaxConnections`, per-peer admission reservations); `ServerTransportHost`/`ClientTransportEndpoint` — host-thread orchestration и атрибуция токен → `ConnectionHandle` → `SessionId` → session gate; `CommandWireCodec` (versioned, little-endian, additive, `CommandHeader` не изменён); `NetworkCommandChannel` реализует `ICommandChannel`: `TrySubmit*` — только local pre-flight, авторитетный результат асинхронно через `CommandResultReceived` (`CommandAckPayload` с `SessionRejection` и `MatchCommandRejection`); Snapshot Protocol v1 передаётся как opaque C2 payload (latest-wins по `SnapshotTick`); retransmission semantics: per-packet `MaxRetransmits`, per-item экспоненциальный backoff, Karn RTT sampling, reorder-span throttle против амплификации; полный жизненный цикл соединения, обработка malformed-пакетов, протокольные версии разделены (Carrier/Message/SnapshotProtocol); large C2 (~117 КБ, 3000+ сущностей) доставляется byte-identical через оба носителя.

### Verified

- Unity `6000.5.6f1`.
- **301/301 EditMode** passed по `Artifacts/TestResults/editmode-phase25-r4.xml`, 2026-08-29 (223 baseline + 78 Phase 2.5: codec/reliability/clock/fragmentation/security/integration/performance baseline + real-UDP LiteNetLib loopback + oversized snapshot + retransmission bound + lifecycle/cleanup/spoof soak + admission churn).
- **6/6 PlayMode** passed по `Artifacts/TestResults/playmode-phase25-r4.xml`, 2026-08-29.

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