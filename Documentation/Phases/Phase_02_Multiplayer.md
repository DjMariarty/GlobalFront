# Phase 02 — Multiplayer Foundation

> Status: **COMPLETE [P0/P1 = 0 CERTIFIED; все P2/P3 закрыты]** • сертификация на commit `4cb8ffa`: строгий состязательный ре-аудит Claude, область сертификации ограничена **P0/P1 = 0** (отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`); открытые на тот момент P2/P3 закрыты в Hardening Phase 3.7.1 (блоки 1–5)

## Goal

Создать современный authoritative multiplayer foundation до интеграции полного RTS gameplay.

## Dependencies

- Phase 01 complete;
- deterministic command/snapshot contracts;
- R&D и ADR для каждого нового network boundary (ADR-007..ADR-011).

## Scope

1. **2.1 Server-Owned Match State — COMPLETE**, `8178110`.
2. **2.2 Command Channel — COMPLETE**, `313e9ef`.
3. **2.3 Server Tick Driver — COMPLETE**, `ea0435d` (ADR-007).
4. **2.4 Session / Player Identity — COMPLETE**, `73d1276` (ADR-008). Lifecycle завершается на `Finished`; `MatchPhase.Closed` — reserved future state для dedicated/server lifecycle.
5. **2.5 Network Transport — COMPLETE**, `feat: implement network transport (Phase 2.5)` (ADR-009, OD-1 = LiteNetLib 1.3.5): контракт `INetworkCarrier` + собственный детерминированный `OwnDatagramCarrier` (`VirtualNetworkPipe`) и `LiteNetLibCarrier` (real UDP); C0/C1 reliable ordered, C2 unreliable sequenced latest-wins, async CommandAck, attribution token → session gate, security/rate-limit/fragmentation baseline; финальный независимый review = **APPROVE** (P0=0/P1=0/P2=0).
6. **2.6 Snapshot Networking — COMPLETE**, ADR-010: Core `DeltaSnapshotWireCodec`, History Ring (120 тиков), Server Replication Engine, Client Replication Receiver (`ClientReplicationReceiver`, `ClientReplicationWorld`, `ReplicationReceiverFSM`), keyframe slicing, pacing (10/5 Hz), 10 Hz `SnapshotAck` (34 B), Zero-GC верификация StateChecksum (32-bit FNV-1a). Шаги 2.6.1–2.6.4 приняты (599/599 EditMode, 6/6 PlayMode).
7. **2.7 Reconnect & Resync — COMPLETE**, ADR-011 (OD-18..OD-22): Core `ReconnectWireCodec` (64 B request / 24 B response), `SessionManager` re-attachment (32 B cryptographically secure secret), тактическая пауза, окно ожидания 200 с / 4000 тиков, 5-секундный countdown, full state resync, `ClientReconnectCoordinator`. Шаги 2.7.1–2.7.4 приняты (641/641 EditMode, 6/6 PlayMode).
8. **2.8 Network Prototype Playtest (2v2) — COMPLETE**: топология 2v2, атрибуция владения командами, HUD + оверлей тактической паузы, abandonment unpause (661/661 EditMode, 6/6 PlayMode).
9. **Hardening Sprints 2.1–2.5 & Certification — COMPLETE** (`5c94e07`..`4cb8ffa`):
   - Sprint 2.1 (`5c94e07`): socket hijack guard, O(k) grace sweep, rate limiter & pending ack lifecycle cleanup.
   - Sprint 2.2 (`ceae9a8`): Zero-GC send scratch buffer и receive stale-scan pooling в `LiteNetLibCarrier`.
   - Sprint 2.3 (`750218c`): canonical PartIndex keyframe assembly и slice timeout aging.
   - Sprint 2.4 (`e6d91ea`): thread-safe tick driver step capture (`baseTick = _tick - (ulong)scheduled` под `_gate`, лок на `Tick => _tick`) и bounded fragment reassembly maps (`MaxFragmentGroups = 4`).
   - Sprint 2.5 (`4cb8ffa`): exclusive session-socket binding и cross-session reconnect hijack guard (N-01 / D-10 / A-01). Session-aware guard в `ServerTransportHost.cs:333-356` (`bound.Session != request.SessionId -> Denied`), эксклюзивность в `TransportSessionBinder.Bind`, токен-идентичность в `TransportSessionBinder.Release`.
   - **Grand Re-Audit Certification (`4cb8ffa`):** сертификация подтвердила **P0/P1 = 0**. Строгий состязательный ре-аудит выполнен Claude на основе коммита `4cb8ffa` (отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`); отчёт ограничивает область сертификации P0/P1 и фиксирует открытый на тот момент P2-бэклог (N-02, N-03, N-04) и P3-пакет. Сторонние модели участвовали только в промежуточных этапах ревью.

## Key Systems

- `MatchServer`, `MatchConfig`, `LocalMatchHost`;
- `ICommandChannel` и `LocalCommandChannel`;
- authoritative server tick ownership (`TickDriver`);
- session/player identity (`SessionManager`);
- command transport и snapshot delivery (`INetworkCarrier`, `ServerTransportHost`, `ClientTransportEndpoint`);
- delta snapshot replication (`ServerReplicationEmitter`, `ClientReplicationReceiver`, `ClientReplicationWorld`);
- reconnect/resync foundation (`ClientReconnectCoordinator`, tactical pause, countdown).

## Milestones

- 2.1 — complete (`8178110`);
- 2.2 — complete (`313e9ef`);
- 2.3 — complete (`ea0435d`);
- 2.4 — complete (`73d1276`, ADR-008);
- 2.5 — complete (ADR-009);
- 2.6 — complete (ADR-010);
- 2.7 — complete (ADR-011);
- 2.8 — complete;
- Hardening Sprints 2.1–2.5 & Grand Re-Audit Certification — complete (`4cb8ffa`, P0/P1 = 0).
- Hardening Phase 3.7.1 remediation (F-1..F-11, F-13..F-15, N-02..N-04) — complete (`934840d`..`3a112ad` + Block 5 docs).

## Acceptance Criteria

- server tick lifecycle не зависит от Client presentation (выполнено);
- identities и sessions имеют authoritative model (выполнено);
- commands и snapshots проходят через реальный transport (выполнено);
- reconnect/resync восстанавливает согласованное authoritative state (выполнено);
- решения задокументированы ADR (ADR-007..ADR-011) и проверены тестами (выполнено).

## Tests

- unit tests tick/session/transport/protocol boundaries (выполнено);
- integration tests command → server tick → snapshot (выполнено);
- reconnect/resync and failure-path tests (выполнено);
- deterministic regression and compatibility tests (выполнено);
- real-UDP loopback stress/loss tests (выполнено);
- bite-тесты всех ремедиаций (выполнено);
- **1179 EditMode + 16 PlayMode тестов — 100% green** (2026-10-21; gates: `Artifacts/TestResults/block5-editmode-final.xml`, `block5-playmode-final.xml`).

## Transport Runtime Status — F-10

- `ServerTransportHost.Pump(long nowMs)` / `Pump(long nowMs, ulong drainTick)` — цикл откачки пакетов выделенного headless-сервера (`ServerTransportHost.cs:100`, `:106`): таймеры носителя, дренаж receive-событий с явным drain-tick, pending acknowledgements.
- В Phase 2 верифицирован интеграционными тестами `LiteNetLibCarrierIntegrationTests` на реальных сокетах LiteNetLib (`transportHost.Pump(clock.NowMs)` на каждом шаге).
- В Phase 3 вертикальный срез изолировал презентацию от сети через `LocalReplicationLoopback`; продакшн call-site `ServerTransportHost.Pump` в рантайме Phase 3 отсутствует. «Phase 2 COMPLETE» означает «реализовано и протестировано», а не «подключено к билду».
- В Phase 4 (Шаг 4.1 Dedicated Server Loop) `ServerTransportHost.Pump` будет напрямую подключён к независимому серверному тикеру реального времени.

## Risks & Residual Backlog

- **[CLOSED] (N-02)**: `LiteNetLibCarrier` C2 reassembly аллоцировал `3+N` объектов на фрагментированный снапшот; закрыто в Block 3 (`a944623`) через `ArrayPool` и group stack pool в `FragmentAssembler`.
- **[CLOSED] (N-03)**: рассогласование `DefaultRemoveSink`/`DefaultAddSink` с серверными ёмкостями; закрыто в Block 2 (`a08beaf`) — sink parity 512 для add и remove.
- **[CLOSED] (N-04)**: утечка rate limiter и binding при отброшенном событии `Disconnected`; закрыто в Block 1 (`934840d`) — control-события `Connected`/`Disconnected` не отбрасываются при переполнении очереди, а `Shutdown` очищает rate limiters и pending acks.
- **[P2 Informational] (OD-16)**: Fog of War `StateChecksum` per-client — для реализации в Phase 4.

## Out of Scope

- полный RTS gameplay integration — Phase 4;
- полный faction roster — Phase 5;
- production maps/presentation — Phase 7;
- deep scale optimization — Phase 8.

## Definition of Done

**ВЫПОЛНЕН ПОЛНОСТЬЮ.** Шаги 2.1–2.8 и спринты hardening 2.1–2.5 реализованы, протестированы и сертифицированы строгим состязательным ре-аудитом с вердиктом **P0/P1 = 0** (отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`). Оставшиеся на момент `4cb8ffa` замечания P2/P3 (N-02, N-03, N-04, F-1..F-11, F-13..F-15) закрыты в Hardening Phase 3.7.1 (блоки 1–5); финальный baseline — **1179 EditMode + 16 PlayMode**, 100% green. Authoritative dedicated-server foundation поддерживает sessions, command/snapshot networking, reconnect/resync и tactical pause; подключение `ServerTransportHost.Pump` к реальному серверному тикеру — Phase 4, Шаг 4.1 (F-10).
