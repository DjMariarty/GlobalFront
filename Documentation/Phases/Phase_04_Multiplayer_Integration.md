# Phase 04 — Multiplayer Gameplay Integration

> Status: **CURRENT / NEXT** • Milestone M1 Multiplayer Prototype • Пререквизиты Phase 2 и Phase 3 полностью завершены и сертифицированы `[APPROVED: ZERO DEFECTS]`

## Goal

Интегрировать сетевой фундамент Phase 2 (`ICommandChannel`, delta snapshots, reconnect/resync, `SessionManager`) с RTS gameplay и презентационным стеком Phase 3, получив первый настоящий multiplayer RTS prototype (1v1, 2v2).

## Dependencies

- Phase 01 complete (детерминированная авторитетная симуляция);
- Phase 02 complete и сертифицирована `[APPROVED: ZERO DEFECTS]` на `4cb8ffa` (`INetworkCarrier`, delta snapshots, session attribution, reconnect/resync);
- Phase 03 complete и сертифицирована `[APPROVED: ZERO DEFECTS]` (RTS controls, camera, SoA tick buffer, instanced overlays, minimap, tactical HUD, 400-unit slice, M2 achieved);
- утверждённые authoritative validation rules.

## Scope

- соединение `TacticalVerticalSliceRunner` с реальным сетевым транспортом (`INetworkCarrier` / LiteNetLib 1.3.5) вместо `LocalReplicationLoopback`;
- сетевая передача команд выделения и перемещения через `NetworkCommandChannel`;
- сетевая репликация дельт и ключевых кадров 400 юнитов по реальной сети (UDP loopback и LAN/remote);
- реконнект и ресинк клиента прямо посреди активного RTS-боя с тактической паузой и 5-секундным countdown;
- networked Fog of War (OD-16) с расчетом `StateChecksum` per-client;
- authoritative validation команд и ownership;
- 1v1 и 2v2 сценарии с реальным `MatchServer`.

## Key Systems

- gameplay command transport (`NetworkCommandChannel` → session gate → `MatchServer`);
- gameplay snapshot replication (`ServerReplicationEmitter` → `LiteNetLibCarrier` → `ClientReplicationReceiver` → `UnitViewTickBuffer`);
- reconnect/resync с активным gameplay state (`ClientReconnectCoordinator`, `SessionManager`, tactical pause);
- Fog of War visibility boundary (`IReplicationFilter`);
- multiplayer diagnostics и desync detection.

## Milestones

- первый end-to-end multiplayer RTS prototype на 400 юнитов;
- Milestone M1 Multiplayer Prototype после принятия Phase 4.

## Acceptance Criteria

- два клиента могут завершить утверждённый gameplay loop через real server (Dedicated / ServerHost);
- команды выбора, движения, атаки и остановки валидируются сервером;
- дельта-снапшоты и ключевые кадры доставляются по реальной сети с контролем пропускной способности;
- отключение и повторное подключение (reconnect/resync) клиента восстанавливает идентичное состояние боя без десинхронизации;
- нулевые дефекты P0/P1 по итогам независимого аудита.

## Tests

- end-to-end 1v1 / 2v2 сетевые тесты (real UDP LiteNetLib);
- authoritative validation и ownership tests;
- gameplay snapshot/reconnect tests под эмулированными сетевыми потерями (1–5% packet loss);
- latency/jitter/disconnect сценарии;
- deterministic and desync regression tests;
- Zero-GC валидация сетевого цикла.

## Risks

- расхождение simulation и presentation при сетевых лагах;
- утечка скрытой информации Fog of War;
- скачки пропускной способности при массовых маневрах 400 юнитов;
- reconnect во время плотного боя.

## Out of Scope

- полный faction roster и баланс — Phase 5;
- full AI — Phase 6;
- production maps/presentation — Phase 7;
- 5v5 / 3000+ scale proof — Phase 8.

## Definition of Done

Настоящий authoritative 1v1/2v2 RTS prototype работает end-to-end по сети с реальными юнитами, combat, snapshots, reconnect и presentation; тесты green (EditMode + PlayMode); Milestone M1 принят.
