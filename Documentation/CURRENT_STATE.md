# GlobalFront Current State

> Быстрый handoff для AI-агента • обновлено 2026-08-29

## Current Phase

**Phase 2 — Multiplayer Foundation**

## Current Task

**Phase 2.6 — Snapshot Networking: подготовка (следующая фаза)**

Phase 2.5 Network Transport — **COMPLETE**: реализована по ADR-009, финальный независимый review = **APPROVE** (P0 = 0, P1 = 0, P2 = 0). LiteNetLib 1.3.5 validated (real-UDP loopback integration); large C2 fragmentation (~117 КБ, 3000+ сущностей) validated на обоих носителях; retransmission amplification исправлена (252 ретрансмиссии на 2000 команд при 5% loss вместо десятков тысяч); connection/admission hardening завершён (bounded state, per-peer admission reservations, cleanup/soak-тесты). Оставшиеся транспортные заметки — только informational/future: delta snapshots / cadence / Fog of War replication и 3000+ bandwidth optimization — Phase 2.6; reconnect/resync — Phase 2.7.

## Last Commit

`feat: implement network transport (Phase 2.5)` (предыдущий: `838c984` — `docs: accept ADR-009 and start Phase 2.5`)

## Tests

- EditMode: **301/301 passed** (`Artifacts/TestResults/editmode-phase25-r4.xml`, 2026-08-29; 223 baseline + 78 Phase 2.5, включая real-UDP LiteNetLib integration, oversized snapshot, retransmission bound, lifecycle/cleanup/spoof soak, admission churn)
- PlayMode: **6/6 passed** (`Artifacts/TestResults/playmode-phase25-r4.xml`, 2026-08-29)
- Unity: `6000.5.6f1`

## Completed Milestones

- M0 Technical Foundation — COMPLETE
- Phase 1 Simulation Foundation — COMPLETE
- Phase 2.1 Server-Owned Match State — COMPLETE (`8178110`)
- Phase 2.2 Command Channel — COMPLETE (`313e9ef`)
- Phase 2.3 Server Tick Driver — COMPLETE (`ea0435d`, ADR-007)
- Phase 2.4 Session / Player Identity — COMPLETE (`73d1276`, ADR-008)
- Phase 2.5 Network Transport — COMPLETE (ADR-009; LiteNetLib 1.3.5 за контрактом `INetworkCarrier`; финальный независимый review = APPROVE, P0=0/P1=0/P2=0)

## Next Step

Phase 2.6 Snapshot Networking: delta snapshots, snapshot cadence policy, Fog of War replication и bandwidth optimization для 3000+ сущностей поверх готового транспорта (C2 opaque payload, latest-wins по `SnapshotTick`). Reconnect/resync — Phase 2.7.

## Important Constraints

- Current runtime — local prototype; `LocalMatchHost` (ServerHost) работает в клиентском процессе и владеет `MatchServer`, `TickDriver` (engine-independent, в `GlobalFront.Server`) и `SessionManager` (Phase 2.4, ADR-008).
- Server tick lifecycle отделён от Unity client lifecycle (ADR-007). Session/player identity реализованы как server-authoritative layer (ADR-008): PlayerId назначается только сервером, command ingress проходит session gate; конкретная grace duration — TBD. Транспортная архитектура определена ADR-009 (OD-1 = LiteNetLib за контрактом `INetworkCarrier`); транспорт реализован и проверен (детерминированный `VirtualNetworkPipe` + real-UDP LiteNetLib loopback) и не владеет simulation: `MatchServer`/`TickDriver`/`SessionManager`/`CommandHeader`/Snapshot Protocol v1 не изменены. Отдельного dedicated server process и reconnect/resync пока нет; тот же driver и simulation core будут использованы future dedicated host.
- Snapshot Protocol v1 реализован и передаётся по сети как opaque payload (C2, unreliable sequenced, latest-wins по `SnapshotTick`); cadence/delta — Phase 2.6.
- Product 1.0 включает пять основных фракций; их gameplay/content ещё не реализован.
- Подфракции не входят в 1.0.
- Не считать будущий roadmap фактом реализации.

## Read Next

- [Phase 02](Phases/Phase_02_Multiplayer.md)
- [Architecture](ARCHITECTURE.md)
- [Project Status](PROJECT_STATUS.md)