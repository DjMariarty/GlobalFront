# Статус проекта GlobalFront

> Фактический технический статус • обновлено 2026-09-04

## Summary

GlobalFront находится в **Phase 2 — Multiplayer Foundation**. Phase 1 и Phase 2.1–2.5 завершены. Phase 2.6 Snapshot Networking — **IMPLEMENTATION COMPLETE** по ADR-010: Шаги 2.6.1 (Core Codec), 2.6.2 (Server Diff & Ring), 2.6.3 (Client Receiver & FSM) и 2.6.4 (Full Integration & Impairment Tests) — **COMPLETE**. Проверка: **597/597 EditMode passed** (`Artifacts/TestResults/editmode-phase26-step4.xml`, 2026-09-04) и **6/6 PlayMode passed** (`Artifacts/TestResults/playmode-phase26-step4.xml`, 2026-09-04). Текущая задача — независимый аудит Phase 2.6 (DeepSeek V4 Pro) перед переходом к Phase 2.7.

## Confirmed Baseline

| Область | Фактическое состояние |
|---|---|
| Unity | `6000.5.6f1`, URP |
| Core | детерминированный, без Unity API, 20 Hz; opaque `SessionId`/`MatchId`; `DeltaSnapshotWireCodec`, `SnapshotAckCodec` (34 B), `KeyframeSliceCodec`, `ReplicationRequestCodec` |
| Server | `MatchServer`, `TickDriver`, `SessionManager`; `Server.Transport` по ADR-009; `ServerReplicationEmitter` интегрирован с тиками, history ring (120 тиков) и pacing-слайсингом keyframe |
| Transport | `INetworkCarrier` + `OwnDatagramCarrier` (детерминированные тесты через `VirtualNetworkPipe`) и `LiteNetLibCarrier` (LiteNetLib 1.3.5, real UDP); `NetworkCommandChannel` реализует `ICommandChannel` с асинхронным авторитетным `CommandAckPayload`; каналы C0/C1/C2 |
| Client replication | `ClientReplicationReceiver`, `ClientReplicationWorld`, `ReplicationReceiverFSM`, `ClientTransportReplicationBridge`; накат дельт, сборка keyframe-слайсов, 10 Hz `SnapshotAck` по C0 |
| Local integration | `LocalMatchHost` (ServerHost) внутри Client process: владеет `MatchServer`, `TickDriver` и `SessionManager`; сквозная интеграция репликации верифицирована в EditMode |
| Commands | Move, Attack и Stop; `ICommandChannel` + session-attributed `LocalCommandChannel`; ingress проходит session gate |
| Snapshots | Delta Snapshot Core/Server/Client слои Шагов 2.6.1–2.6.4 завершены, rate-pacing/keyframe slicing/end-to-end integration и loss stress (1–5% loss) верифицированы |
| Presentation | выбор, движение, атака, камера, HUD, runtime prototype world |
| Scene | одна включённая `Assets/Scenes/SampleScene.unity` |
| Tests | **599/599 EditMode passed** (`EditModeTestResults.xml`, 2026-09-07); **6/6 PlayMode passed** (`Artifacts/TestResults/playmode-phase26-step4.xml`, 2026-09-04) |

## Phase 2 Status

- 2.1 Server-Owned Match State — COMPLETE
- 2.2 Command Channel — COMPLETE
- 2.3 Server Tick Driver — COMPLETE, `ea0435d` (ADR-007)
- 2.4 Session / Player Identity — COMPLETE, `73d1276` (ADR-008); lifecycle завершается на `MatchPhase.Finished`, `Closed` зарезервирован для будущего server lifecycle/teardown
- 2.5 Network Transport — COMPLETE, `feat: implement network transport (Phase 2.5)` (ADR-009, OD-1 = LiteNetLib 1.3.5 за контрактом `INetworkCarrier`; финальный независимый review = APPROVE, P0=0/P1=0/P2=0)
- 2.6 Snapshot Networking — COMPLETE (Шаги 2.6.1, 2.6.2, 2.6.3, 2.6.4 complete; pending independent audit)
- 2.7 Reconnect / Resync — not implemented

## Product Scope vs Implementation

Утверждённый Product 1.0 включает пять основных фракций, полный Generals-style RTS foundation, multiplayer до 10 игроков, AI, reconnect/resync, replay, desync detection, большие армии и production-quality presentation. Эти требования являются roadmap, а не текущими возможностями прототипа.

Сейчас нет production economy/build/production systems, пяти реализованных фракций, полного AI, карт 1v1–5v5, dedicated server, reconnect, replay или scale proof 3000+. Core/Server/Client слои delta snapshots реализованы в Шагах 2.6.1–2.6.3; сквозная интеграция, rate-pacing, keyframe slicing и loss stress выполняются в Шаге 2.6.4, scale proof ещё не получен.

## Important Constraints

- Client пока напрямую ссылается на Server из-за local host.
- Server tick lifecycle engine-independent (ADR-007); тот же `TickDriver`/`MatchServer` core будет использован future dedicated host.
- Session/player identity реализованы server-authoritative (ADR-008): PlayerId назначается только сервером. Транспорт реализован по ADR-009 (OD-1 = LiteNetLib 1.3.5 за контрактом `INetworkCarrier`, precompiled DLL в `Assets/_GlobalFront/ThirdParty/LiteNetLib`): транспорт не владеет simulation и не меняет `MatchServer`/`TickDriver`/`SessionManager`/`CommandHeader`/Snapshot Protocol v1; timing constants (OD-8) применены как reference values (`TransportProtocol`).
- Snapshot Protocol v1 сохраняется как legacy/full payload. Delta Snapshot Core/Server/Client слои Шагов 2.6.1–2.6.3 реализованы; их сквозная связка с transport/tick-loop, rate-pacing, keyframe slicing и loss stress — текущий Шаг 2.6.4.
- Pathfinding и детерминированное разрешение препятствий отсутствуют.
- Детали roster, abilities, stats, balance, generals и map layouts остаются TBD.

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Architecture](ARCHITECTURE.md)
- [Roadmap](ROADMAP.md)
- [Phase 02](Phases/Phase_02_Multiplayer.md)
