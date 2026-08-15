# Статус проекта GlobalFront

> Фактический технический статус • обновлено 2026-08-15

## Summary

GlobalFront находится в **Phase 2 — Multiplayer Foundation**. Phase 1 завершена. Phase 2.1 Server-Owned Match State и Phase 2.2 Command Channel завершены соответствующими commits `8178110` и `313e9ef`. Следующий шаг — Phase 2.3 Server Tick Driver.

## Confirmed Baseline

| Область | Фактическое состояние |
|---|---|
| Unity | `6000.5.6f1`, URP |
| Core | детерминированный, без Unity API, 20 Hz |
| Server | `MatchServer`, server-owned state и entity assignment через `MatchConfig` |
| Local integration | `LocalMatchHost` внутри Client process |
| Commands | Move, Attack и Stop; `ICommandChannel` + `LocalCommandChannel` |
| Snapshots | Snapshot Protocol v1, little-endian, 16-byte header + 39 bytes/entity |
| Presentation | выбор, движение, атака, камера, HUD, runtime prototype world |
| Scene | одна включённая `Assets/Scenes/SampleScene.unity` |
| Tests | 161/161 EditMode и 5/5 PlayMode passed 2026-08-12 |

## Phase 2 Status

- 2.1 Server-Owned Match State — COMPLETE
- 2.2 Command Channel — COMPLETE
- 2.3 Server Tick Driver — **NEXT**
- 2.4 Session / Player Identity — not implemented
- 2.5 Network Transport — not implemented
- 2.6 Snapshot Networking — not implemented
- 2.7 Reconnect / Resync — not implemented

## Product Scope vs Implementation

Утверждённый Product 1.0 включает пять основных фракций, полный Generals-style RTS foundation, multiplayer до 10 игроков, AI, reconnect/resync, replay, desync detection, большие армии и production-quality presentation. Эти требования являются roadmap, а не текущими возможностями прототипа.

Сейчас нет production economy/build/production systems, пяти реализованных фракций, полного AI, карт 1v1–5v5, dedicated server, transport, reconnect, replay или scale proof 3000+.

## Important Constraints

- Client пока напрямую ссылается на Server из-за local host.
- Тики authoritative server пока управляются локальным Client runner; Phase 2.3 должен определить серверный tick driver.
- Player identity локально фиксирована и не подкреплена session layer.
- Snapshot serialization реализована, но network delivery отсутствует.
- Pathfinding и детерминированное разрешение препятствий отсутствуют.
- Детали roster, abilities, stats, balance, generals и map layouts остаются TBD.

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Architecture](ARCHITECTURE.md)
- [Roadmap](ROADMAP.md)
- [Phase 02](Phases/Phase_02_Multiplayer.md)