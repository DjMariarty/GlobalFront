# Статус проекта GlobalFront

> Фактический технический статус • обновлено 2026-08-22

## Summary

GlobalFront находится в **Phase 2 — Multiplayer Foundation**. Phase 1 завершена. Phase 2.1 Server-Owned Match State, Phase 2.2 Command Channel, Phase 2.3 Server Tick Driver и Phase 2.4 Session / Player Identity завершены commits `8178110`, `313e9ef`, `ea0435d` и `73d1276` (ADR-007, ADR-008). Phase 2.5 Network Transport — IN_PROGRESS: ADR-009 принят (OD-1 = LiteNetLib как preferred initial carrier за контрактом `INetworkCarrier`), implementation не начат, зависимость не добавлена.

## Confirmed Baseline

| Область | Фактическое состояние |
|---|---|
| Unity | `6000.5.6f1`, URP |
| Core | детерминированный, без Unity API, 20 Hz; opaque `SessionId`/`MatchId` value types |
| Server | `MatchServer`, server-owned state и entity assignment через `MatchConfig`; `TickDriver` — engine-independent server tick scheduling (20 Hz, bounded catch-up, manual/real-time); `SessionManager` — server-authoritative session/player identity (Phase 2.4, ADR-008) |
| Local integration | `LocalMatchHost` (ServerHost) внутри Client process: владеет `MatchServer`, `TickDriver` и `SessionManager` |
| Commands | Move, Attack и Stop; `ICommandChannel` + session-attributed `LocalCommandChannel`; ingress проходит session gate |
| Snapshots | Snapshot Protocol v1, little-endian, 16-byte header + 39 bytes/entity |
| Presentation | выбор, движение, атака, камера, HUD, runtime prototype world |
| Scene | одна включённая `Assets/Scenes/SampleScene.unity` |
| Tests | 223/223 EditMode и 5/5 PlayMode passed 2026-08-21 (`Artifacts/TestResults/editmode-phase24.xml`, `playmode-phase24.xml`) |

## Phase 2 Status

- 2.1 Server-Owned Match State — COMPLETE
- 2.2 Command Channel — COMPLETE
- 2.3 Server Tick Driver — COMPLETE, `ea0435d` (ADR-007)
- 2.4 Session / Player Identity — COMPLETE, `73d1276` (ADR-008); lifecycle завершается на `MatchPhase.Finished`, `Closed` зарезервирован для будущего server lifecycle/teardown
- 2.5 Network Transport — IN_PROGRESS (ADR-009 Accepted, OD-1 = LiteNetLib за контрактом `INetworkCarrier`; implementation не начат, зависимость не добавлена)
- 2.6 Snapshot Networking — not implemented
- 2.7 Reconnect / Resync — not implemented

## Product Scope vs Implementation

Утверждённый Product 1.0 включает пять основных фракций, полный Generals-style RTS foundation, multiplayer до 10 игроков, AI, reconnect/resync, replay, desync detection, большие армии и production-quality presentation. Эти требования являются roadmap, а не текущими возможностями прототипа.

Сейчас нет production economy/build/production systems, пяти реализованных фракций, полного AI, карт 1v1–5v5, dedicated server, transport, reconnect, replay или scale proof 3000+.

## Important Constraints

- Client пока напрямую ссылается на Server из-за local host.
- Server tick lifecycle engine-independent (ADR-007); тот же `TickDriver`/`MatchServer` core будет использован future dedicated host.
- Session/player identity реализованы server-authoritative (ADR-008): PlayerId назначается только сервером; транспортная архитектура определена ADR-009 (OD-1 = LiteNetLib за контрактом `INetworkCarrier`); transport implementation, timing constants (OD-8) и ввод зависимости — впереди, в рамках Phase 2.5.
- Snapshot serialization реализована, но network delivery отсутствует.
- Pathfinding и детерминированное разрешение препятствий отсутствуют.
- Детали roster, abilities, stats, balance, generals и map layouts остаются TBD.

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Architecture](ARCHITECTURE.md)
- [Roadmap](ROADMAP.md)
- [Phase 02](Phases/Phase_02_Multiplayer.md)