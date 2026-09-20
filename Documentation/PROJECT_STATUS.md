# Статус проекта GlobalFront

> Фактический технический статус • обновлено 2026-09-20

## Summary

GlobalFront: Phases 1.0 — 2.8 — **[100% COMPLETED / AUDITED]** (Grand Adversarial Audit **[APPROVED: ZERO DEFECTS]** на `a37f53d`; отчёт: `Artifacts/GrandAudit-Certification-a37f53d.md`). Проверка: **661/661 EditMode passed (100%)** и **6/6 PlayMode passed (100%)**, консоль Unity: 0 ошибок, 0 предупреждений. Текущая задача / Next Phase — **Phase 3 (Visual Presentation & RTS Controls) — [CURRENT / IN PROGRESS]**.
 
## Confirmed Baseline
 
| Область | Фактическое состояние |
|---|---|
| Unity | `6000.5.6f1`, URP |
| Core | детерминированный, без Unity API, 20 Hz; opaque `SessionId`/`MatchId`; `DeltaSnapshotWireCodec`, `SnapshotAckCodec` (34 B), `KeyframeSliceCodec`, `ReplicationRequestCodec`, `ReconnectWireCodec` (64 B request / 24 B response) |
| Server | `MatchServer`, `TickDriver`, `SessionManager` (re-bind token, 32 B cryptographically secure secret, grace timeout); `Server.Transport` по ADR-009; `ServerReplicationEmitter` интегрирован с тиками, history ring (120 тиков), pacing-слайсингом keyframe и тактической паузой |
| Transport | `INetworkCarrier` + `OwnDatagramCarrier` (детерминированные тесты через `VirtualNetworkPipe`) и `LiteNetLibCarrier` (LiteNetLib 1.3.5, real UDP); `NetworkCommandChannel` реализует `ICommandChannel` с асинхронным авторитетным `CommandAckPayload`; каналы C0/C1/C2 |
| Client replication | `ClientReplicationReceiver`, `ClientReplicationWorld`, `ReplicationReceiverFSM`, `ClientTransportReplicationBridge`, `ClientReconnectCoordinator`; накат дельт, сборка keyframe-слайсов, 10 Hz `SnapshotAck` по C0, Zero-GC верификация StateChecksum, авто-восстановление и координация реконнекта |
| Local integration | `LocalMatchHost` (ServerHost) внутри Client process: владеет `MatchServer`, `TickDriver` и `SessionManager`; тактическая пауза (OD-18..OD-22), окно ожидания 200с / 4000 тиков (OD-18) и 5-секундный countdown таймер (OD-20); сквозная интеграция верифицирована в EditMode |
| Commands | Move, Attack и Stop; `ICommandChannel` + session-attributed `LocalCommandChannel`; ingress проходит session gate; сохранение команд во время реконнекта |
| Snapshots | Delta Snapshot Core/Server/Client слои Шагов 2.6.1–2.6.4 и Reconnect 2.7.1–2.7.4 завершены, rate-pacing/keyframe slicing/end-to-end integration и loss stress (1–5% loss) верифицированы; независимый аудит Phase 2.6 = APPROVE (zero P0/P1 blockers) |
| Presentation | выбор, движение, атака, камера, HUD, runtime prototype world |
| Scene | одна включённая `Assets/Scenes/SampleScene.unity` |
| Tests | **661/661 EditMode passed (100%)**; **6/6 PlayMode passed (100%)** (Grand Audit baseline `a37f53d`; prior gates: `Artifacts/TestResults/EditMode-reviewgate.xml`, `Artifacts/TestResults/PlayMode-reviewgate.xml`, 2026-09-20) |

## Phase 1.0 – 2.8 Status — [100% COMPLETED / AUDITED]

- Phase 1.x (RTS Foundation / Prototype) — **[100% COMPLETED / AUDITED]** (включена в Grand Audit `a37f53d`, [APPROVED: ZERO DEFECTS]).

## Phase 2 Status — [100% COMPLETED / AUDITED]
 
- 2.1 Server-Owned Match State — [100% COMPLETED / AUDITED]
- 2.2 Command Channel — [100% COMPLETED / AUDITED]
- 2.3 Server Tick Driver — [100% COMPLETED / AUDITED], `ea0435d` (ADR-007)
- 2.4 Session / Player Identity — [100% COMPLETED / AUDITED], `73d1276` (ADR-008); lifecycle завершается на `MatchPhase.Finished`, `Closed` зарезервирован для будущего server lifecycle/teardown
- 2.5 Network Transport — [100% COMPLETED / AUDITED], `feat: implement network transport (Phase 2.5)` (ADR-009, OD-1 = LiteNetLib 1.3.5 за контрактом `INetworkCarrier`; финальный независимый review = APPROVE, P0=0/P1=0/P2=0)
- 2.6 Snapshot Networking — [100% COMPLETED / AUDITED] (Independent adversarial audit passed with zero P0/P1 blockers; ADR-010; Шаги 2.6.1–2.6.4 complete; 599/599 EditMode, 6/6 PlayMode)
- 2.7 Reconnect / Resync — [100% COMPLETED / AUDITED] (Steps 2.7.1–2.7.4 complete; 641/641 EditMode, 6/6 PlayMode; Zero-GC, tactical pause OD-18..OD-22, 5s countdown OD-20; Review Gate APPROVED)
- 2.8 Network Prototype Playtest (2v2) — [100% COMPLETED / AUDITED] (Steps 2.8.1–2.8.3 complete; 655/655 EditMode baseline → 661/661 на `a37f53d` после F-01/F-02; 6/6 PlayMode; 2v2 topology, command ownership, HUD + tactical pause overlay, abandonment unpause P2-1/P2-2)

## Phase 3 (Visual Presentation & RTS Controls) — [CURRENT / IN PROGRESS]

## Product Scope vs Implementation

Утверждённый Product 1.0 включает пять основных фракций, полный Generals-style RTS foundation, multiplayer до 10 игроков, AI, reconnect/resync, replay, desync detection, большие армии и production-quality presentation. Эти требования являются roadmap, а не текущими возможностями прототипа.

Сейчас нет production economy/build/production systems, пяти реализованных фракций, полного AI, карт 1v1–5v5, dedicated server, reconnect, replay или scale proof 3000+. Core/Server/Client слои delta snapshots реализованы и верифицированы в Шагах 2.6.1–2.6.4 (599/599 EditMode, 6/6 PlayMode); независимый аудит Phase 2.6 утверждён (zero P0/P1 blockers); scale proof 3000+ запланирован на последующие фазы.

## Backlog

- **[P2 Informational]**: при реализации Fog of War (OD-16 / `IReplicationFilter`) в будущих фазах `StateChecksum` должен вычисляться per-client (для отфильтрованного среза видимости конкретного клиента), а не глобально по всему миру симуляции сервера.

## Important Constraints

- Client пока напрямую ссылается на Server из-за local host.
- Server tick lifecycle engine-independent (ADR-007); тот же `TickDriver`/`MatchServer` core будет использован future dedicated host.
- Session/player identity реализованы server-authoritative (ADR-008): PlayerId назначается только сервером. Транспорт реализован по ADR-009 (OD-1 = LiteNetLib 1.3.5 за контрактом `INetworkCarrier`, precompiled DLL в `Assets/_GlobalFront/ThirdParty/LiteNetLib`): транспорт не владеет simulation и не меняет `MatchServer`/`TickDriver`/`SessionManager`/`CommandHeader`/Snapshot Protocol v1; timing constants (OD-8) применены как reference values (`TransportProtocol`).
- Snapshot Protocol v1 сохраняется как legacy/full payload. Delta Snapshot Core/Server/Client слои Шагов 2.6.1–2.6.4 реализованы и утверждены: сквозная связка с transport/tick-loop, rate-pacing, keyframe slicing и loss stress верифицированы.
- Pathfinding и детерминированное разрешение препятствий отсутствуют.
- Детали roster, abilities, stats, balance, generals и map layouts остаются TBD.

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Architecture](ARCHITECTURE.md)
- [Roadmap](ROADMAP.md)
- [Phase 02](Phases/Phase_02_Multiplayer.md)

