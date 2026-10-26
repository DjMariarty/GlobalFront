# Phase 04 — Multiplayer Gameplay Integration

> Status: **100% COMPLETED / AUDITED: ZERO DEFECTS** • Milestone M1 (Multiplayer Prototype) ACHIEVED (Commit `366e444`, 1356 tests passed)

## Goal

Интегрировать сетевой фундамент Phase 2 (`ICommandChannel`, delta snapshots, reconnect/resync, `SessionManager`) с RTS gameplay и презентационным стеком Phase 3, получив первый настоящий multiplayer RTS prototype (1v1, 2v2).

## Dependencies

- Phase 01 complete (детерминированная авторитетная симуляция);
- Phase 02 complete (P0/P1 = 0 сертифицирован на `4cb8ffa`, все открытые замечания закрыты в Hardening 3.7.1: `INetworkCarrier`, delta snapshots, session attribution, reconnect/resync);
- Phase 03 complete и сертифицирована `[APPROVED: ZERO DEFECTS]` (RTS controls, camera, SoA tick buffer, instanced overlays, minimap, tactical HUD, 400-unit slice, M2 achieved; Hardening 3.7.1 подготовил seam-инъекцию F-11 и URP handoff F-7);
- утверждённые authoritative validation rules.

## Scope

- соединение `TacticalVerticalSliceRunner` с реальным сетевым транспортом (`INetworkCarrier` / LiteNetLib 1.3.5) вместо `LocalReplicationLoopback` — [COMPLETE, Step 4.2];
- сетевая передача команд выделения и перемещения через `NetworkCommandChannel` — [COMPLETE, Step 4.2 / 4.4.2];
- сетевая репликация дельт и ключевых кадров 400 юнитов по реальной сети (UDP loopback и LAN/remote) — [COMPLETE, Step 4.3.2 / 4.6.3 / 4.6.4];
- реконнект и ресинк клиента прямо посреди активного RTS-боя с тактической паузой и 5-секундным countdown — [COMPLETE, Step 4.5.1–4.5.3 / 4.6.4 / 4.6.5];
- networked Fog of War (OD-16 / OD-39) с расчетом `StateChecksum` per-client — [COMPLETE, Step 4.3.1 / 4.3.2];
- authoritative validation команд и ownership — [COMPLETE, Step 4.4.1];
- 1v1 и 2v2 сценарии с реальным `MatchServer` — [COMPLETE, Step 4.6.4 / 4.6.5].

## Key Systems

- gameplay command transport (`NetworkCommandChannel` → session gate → `MatchServer`);
- gameplay snapshot replication (`ServerReplicationEmitter` → `LiteNetLibCarrier` → `ClientReplicationReceiver` → `UnitViewTickBuffer`);
- reconnect/resync с активным gameplay state (`ClientReconnectCoordinator`, `SessionManager`, tactical pause, `TacticalPauseOverlay`, `ResyncSelectionViews()`);
- Fog of War visibility boundary (`BitMask1024`, `VisibilityEngine`, `VisibilityHistoryRing`, `FogOfWarReplicationFilter`);
- multiplayer diagnostics и desync detection (`TransportBufferPool`, per-client FNV-1a checksums, 0 B GC.Alloc).

## Milestones

- первый end-to-end multiplayer RTS prototype на 400 юнитов — [COMPLETE];
- Milestone M1 Multiplayer Prototype после принятия Phase 4 — [ACHIEVED].

## Acceptance Criteria

- [x] два клиента могут завершить утверждённый gameplay loop через real server (Dedicated / ServerHost);
- [x] команды выбора, движения, атаки и остановки валидируются сервером;
- [x] дельта-снапшоты и ключевые кадры доставляются по реальной сети с контролем пропускной способности;
- [x] отключение и повторное подключение (reconnect/resync) клиента восстанавливает идентичное состояние боя без десинхронизации;
- [x] нулевые дефекты P0/P1 по итогам независимого аудита.

## Tests

- end-to-end 1v1 / 2v2 сетевые тесты (real UDP LiteNetLib);
- authoritative validation и ownership tests;
- gameplay snapshot/reconnect tests под эмулированными сетевыми потерями (1–5% packet loss);
- latency/jitter/disconnect сценарии;
- deterministic and desync regression tests;
- Zero-GC валидация сетевого цикла (**0 блоков / 0 байт GC.Alloc** под боем 402 юнитов);
- Итог: **1356 автоматизированных тестов (1334 EditMode + 22 PlayMode), 100% green**.

## Definition of Done

Настоящий authoritative 1v1/2v2 RTS prototype работает end-to-end по сети с реальными юнитами, combat, snapshots, reconnect и presentation; тесты green (EditMode + PlayMode); Milestone M1 принят.
