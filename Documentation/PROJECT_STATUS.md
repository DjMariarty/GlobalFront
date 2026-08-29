# Статус проекта GlobalFront

> Фактический технический статус • обновлено 2026-08-29

## Summary

GlobalFront находится в **Phase 2 — Multiplayer Foundation**. Phase 1 завершена. Phase 2.1 Server-Owned Match State, Phase 2.2 Command Channel, Phase 2.3 Server Tick Driver, Phase 2.4 Session / Player Identity и Phase 2.5 Network Transport завершены commits `8178110`, `313e9ef`, `ea0435d`, `73d1276` и `feat: implement network transport (Phase 2.5)` (ADR-007, ADR-008, ADR-009). Phase 2.5 — COMPLETE: транспортный слой по ADR-009 (OD-1 = LiteNetLib 1.3.5 как preferred initial carrier за контрактом `INetworkCarrier`) реализован, финальный независимый review = **APPROVE** (P0 = 0, P1 = 0, P2 = 0); LiteNetLib validated real-UDP loopback integration, large C2 fragmentation validated на обоих носителях, retransmission amplification исправлена, connection/admission hardening завершён; 301/301 EditMode и 6/6 PlayMode. Следующая фаза — 2.6 Snapshot Networking.

## Confirmed Baseline

| Область | Фактическое состояние |
|---|---|
| Unity | `6000.5.6f1`, URP |
| Core | детерминированный, без Unity API, 20 Hz; opaque `SessionId`/`MatchId` value types |
| Server | `MatchServer`, server-owned state и entity assignment через `MatchConfig`; `TickDriver` — engine-independent server tick scheduling (20 Hz, bounded catch-up, manual/real-time); `SessionManager` — server-authoritative session/player identity (Phase 2.4, ADR-008); `Server.Transport` — транспортный слой ADR-009: envelope/ARQ/fragmentation/rate limiting, `ServerTransportHost`, `ClientTransportEndpoint`, token-атрибуция к сессиям |
| Transport | `INetworkCarrier` + `OwnDatagramCarrier` (детерминированные тесты через `VirtualNetworkPipe`) и `LiteNetLibCarrier` (LiteNetLib 1.3.5, real UDP); `NetworkCommandChannel` реализует `ICommandChannel` с асинхронным авторитетным `CommandAckPayload` |
| Local integration | `LocalMatchHost` (ServerHost) внутри Client process: владеет `MatchServer`, `TickDriver` и `SessionManager` |
| Commands | Move, Attack и Stop; `ICommandChannel` + session-attributed `LocalCommandChannel`; ingress проходит session gate |
| Snapshots | Snapshot Protocol v1, little-endian, 16-byte header + 39 bytes/entity |
| Presentation | выбор, движение, атака, камера, HUD, runtime prototype world |
| Scene | одна включённая `Assets/Scenes/SampleScene.unity` |
| Tests | 301/301 EditMode и 6/6 PlayMode passed 2026-08-29 (`Artifacts/TestResults/editmode-phase25-r4.xml`, `playmode-phase25-r4.xml`) |

## Phase 2 Status

- 2.1 Server-Owned Match State — COMPLETE
- 2.2 Command Channel — COMPLETE
- 2.3 Server Tick Driver — COMPLETE, `ea0435d` (ADR-007)
- 2.4 Session / Player Identity — COMPLETE, `73d1276` (ADR-008); lifecycle завершается на `MatchPhase.Finished`, `Closed` зарезервирован для будущего server lifecycle/teardown
- 2.5 Network Transport — COMPLETE, `feat: implement network transport (Phase 2.5)` (ADR-009, OD-1 = LiteNetLib 1.3.5 за контрактом `INetworkCarrier`; финальный независимый review = APPROVE, P0=0/P1=0/P2=0)
- 2.6 Snapshot Networking — not implemented
- 2.7 Reconnect / Resync — not implemented

## Product Scope vs Implementation

Утверждённый Product 1.0 включает пять основных фракций, полный Generals-style RTS foundation, multiplayer до 10 игроков, AI, reconnect/resync, replay, desync detection, большие армии и production-quality presentation. Эти требования являются roadmap, а не текущими возможностями прототипа.

Сейчас нет production economy/build/production systems, пяти реализованных фракций, полного AI, карт 1v1–5v5, dedicated server, reconnect, replay или scale proof 3000+. Транспортный слой (команды и доставка снапшотов) реализован — Phase 2.5 COMPLETE; delta snapshots и 3000+ bandwidth optimization — Phase 2.6.

## Important Constraints

- Client пока напрямую ссылается на Server из-за local host.
- Server tick lifecycle engine-independent (ADR-007); тот же `TickDriver`/`MatchServer` core будет использован future dedicated host.
- Session/player identity реализованы server-authoritative (ADR-008): PlayerId назначается только сервером. Транспорт реализован по ADR-009 (OD-1 = LiteNetLib 1.3.5 за контрактом `INetworkCarrier`, precompiled DLL в `Assets/_GlobalFront/ThirdParty/LiteNetLib`): транспорт не владеет simulation и не меняет `MatchServer`/`TickDriver`/`SessionManager`/`CommandHeader`/Snapshot Protocol v1; timing constants (OD-8) применены как reference values (`TransportProtocol`).
- Snapshot serialization реализована и передаётся по сети как opaque C2 payload (latest-wins по `SnapshotTick`); cadence/delta/Fog of War replication — Phase 2.6.
- Pathfinding и детерминированное разрешение препятствий отсутствуют.
- Детали roster, abilities, stats, balance, generals и map layouts остаются TBD.

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Architecture](ARCHITECTURE.md)
- [Roadmap](ROADMAP.md)
- [Phase 02](Phases/Phase_02_Multiplayer.md)