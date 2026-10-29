# Phase 04 — Multiplayer Gameplay Integration

> Status: **MILESTONE M1.1 FORENSIC SEAL DELIVERED — 10/10 FIXES VERIFIED** • Scope fixed to **1v1 Dedicated Server Prototype** (OD-48, OD-49, 1368 EditMode + 22 PlayMode = 1390 green)

## Goal

Интегрировать сетевой фундамент Phase 2 (`ICommandChannel`, delta snapshots, reconnect/resync, `SessionManager`) с RTS gameplay и презентационным стеком Phase 3, получив первый настоящий multiplayer RTS prototype. Формат Milestone M1 — **1v1 Dedicated Server Prototype**; режим 2v2 официально перенесён в Milestone M2 (Phase 5), см. OD-48 в `DECISIONS.md`.

## Dependencies

- Phase 01 complete (детерминированная авторитетная симуляция);
- Phase 02 complete (P0/P1 = 0 сертифицирован на `4cb8ffa`, все открытые замечания закрыты в Hardening 3.7.1: `INetworkCarrier`, delta snapshots, session attribution, reconnect/resync);
- Phase 03 complete и сертифицирована `[APPROVED: ZERO DEFECTS]` (RTS controls, camera, SoA tick buffer, instanced overlays, minimap, tactical HUD, 400-unit slice, M2 achieved; Hardening 3.7.1 подготовил seam-инъекцию F-11 и URP handoff F-7);
- утверждённые authoritative validation rules.

## Scope

- соединение `TacticalVerticalSliceRunner` с реальным сетевым транспортом (`INetworkCarrier` / LiteNetLib 1.3.5) вместо `LocalReplicationLoopback` — [COMPLETE, Step 4.2];
- сетевая передача команд выделения и перемещения через `NetworkCommandChannel` — [COMPLETE, Step 4.2 / 4.4.2];
- сетевая репликация дельт и ключевых кадров 400 юнитов по реальной сети (UDP loopback и LAN/remote) — [COMPLETE, Step 4.3.2 / 4.6.3 / 4.6.4];
- реконнект и ресинк клиента прямо посреди активного RTS-боя с тактической паузой и 5-секундным countdown — [COMPLETE, Step 4.5.1–4.5.3 / 4.6.4 / 4.6.5, M1 remediation 3.1–3.4];
- networked Fog of War (OD-16 / OD-39) с расчетом `StateChecksum` per-client — [COMPLETE, Step 4.3.1 / 4.3.2, M1 remediation 1.1–1.3];
- authoritative validation команд и ownership — [COMPLETE, Step 4.4.1];
- автоматический запуск 1v1 dedicated-матча с серверным ростером — [COMPLETE, M1 remediation Block 2];
- 1v1 сценарий с реальным `MatchServer` — [COMPLETE]; 2v2 сценарий — [DEFERRED → Milestone M2 / Phase 5].

## Key Systems

- gameplay command transport (`NetworkCommandChannel` → session gate → `MatchServer`);
- gameplay snapshot replication (`ServerReplicationEmitter` → `LiteNetLibCarrier` → `ClientReplicationReceiver` → `UnitViewTickBuffer`);
- reconnect/resync с активным gameplay state (`ClientReconnectCoordinator`, `SessionManager`, tactical pause, `TacticalPauseOverlay`, `ResyncSelectionViews()`);
- Fog of War visibility boundary (`BitMask1024`, `VisibilityEngine`, `VisibilityHistoryRing`, `FogOfWarReplicationFilter`);
- multiplayer diagnostics и desync detection (`TransportBufferPool` accounting, per-client FNV-1a checksums);
- dedicated match composition (`DedicatedServerRunner`: lobby → server-owned 1v1 roster → `TryStartSessionMatch`).

## Milestones

- первый end-to-end multiplayer RTS prototype на 400 юнитов — [COMPLETE];
- Milestone M1 (1v1 Dedicated Server Prototype) — [REMEDIATED, PENDING REVIEW];
- Milestone M2 (2v2 + Phase 5 Faction Asymmetry) — [PLANNED].

## Acceptance Criteria

- [x] два клиента могут завершить утверждённый gameplay loop через real server (Dedicated / ServerHost);
- [x] dedicated-сервер сам формирует ростер и стартует 1v1-матч по подключению двух игроков (M1 remediation 2.1);
- [x] команды выбора, движения, атаки и остановки валидируются сервером;
- [x] отказы атаки не раскрывают статус скрытой цели (M1 remediation 1.3);
- [x] трупы не дают обзора, преследование скрытой цели не передаёт её новые координаты (M1 remediation 1.1–1.2);
- [x] дельта-снапшоты и ключевые кадры доставляются по реальной сети с контролем пропускной способности;
- [x] отключение и повторное подключение (reconnect/resync) клиента восстанавливает идентичное состояние боя без десинхронизации;
- [x] реконнект ограничен по времени: временный `InvalidState` повторяется, ожидание ready имеет дедлайн (M1 remediation 3.1–3.2);
- [x] пул транспортных буферов сбалансирован на всех путях сброса (M1 remediation Block 4);
- [ ] 2v2 (командный режим) — перенесено в Milestone M2.

## Tests

- end-to-end 1v1 сетевые тесты (real UDP LiteNetLib); 2v2 harness остаётся в репозитории, но M1 его не заявляет;
- authoritative validation и ownership tests;
- gameplay snapshot/reconnect tests под эмулированными сетевыми потерями (1–5% packet loss);
- latency/jitter/disconnect сценарии;
- deterministic and desync regression tests;
- 17 новых регрессионных тестов M1 remediation + 17 новых тестов M1.1 Forensic Seal (`M1ForensicSealTests.cs` и фикстуры Ingress, FoW, Fragmentation, Runner, Reconnect);
- Zero-GC: замер **ограничен** клиентским транспортом и пайплайнами репликации при отключённой презентации (3-секундное окно) — **0 блоков GC.Alloc**; вместимость пула расширена до 32/64 буферов под 16-32 KiB keyframes;
- Итог: **1390 автоматизированных тестов (1368 EditMode + 22 PlayMode), 100% green** на 2026-10-29.

## Definition of Done

Настоящий authoritative **1v1** RTS prototype работает end-to-end по сети с реальными юнитами, combat, snapshots, reconnect и presentation; тесты green (EditMode + PlayMode); 2v2 вынесен в Milestone M2; Milestone M1 ожидает независимого ревью.
