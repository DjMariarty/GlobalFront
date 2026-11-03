# Статус проекта GlobalFront

> Фактический технический статус • обновлено 2026-11-03

## Summary

GlobalFront: Phases 1.0 — 2.8 + Phase 3.0 (Шаги 3.1–3.7) + Hardening Phase 3.7.1 + Phase 4.0 (Шаги 4.1–4.6.5) + **Микро-спринты M1.1/M1.2** — **ВСЕ ДЕФЕКТЫ ЗАКРЫТЫ, 1392 ТЕСТА ЗЕЛЁНЫЕ**.
- **Phase 1 Hardening:** Спринты 1.1–1.5.1 (`5b16a05`..`349a074`) закрыли race conditions, reentrancy, friendly fire, unit limits и alloc-free snapshots.
- **Phase 2 Hardening & Grand Re-Audit:** Спринты 2.1–2.5 (`5c94e07`..`4cb8ffa`) устранили socket hijack, rate limit leaks, fragment map overflow, concurrency в TickDriver и cross-session reconnect hijack (N-01 / D-10 / A-01; P0/P1 = 0 сертифицирован).
- **Hardening Phase 3.7.1 (Grand Audit remediation):** Блоки 1–4 (`934840d`..`3a112ad`) закрыли F-1..F-11, F-13..F-15 и N-02..N-04 на уровне кода; блок 5 закрыл документальные F-6 и F-10.
- **Phase 3 (Visual Presentation & RTS Controls):** Шаги 3.1–3.7 (ADR-012, OD-23..OD-29) полностью сертифицированы `[APPROVED: ZERO DEFECTS]` (`3414ba6` / `cb01ea2`). Замер OD-28 на 400 юнитах: **0.623 ms/frame** и **0 B GC.Alloc** за 60 steady-state кадров. Milestone **M2 Playable RTS Core** достигнут.
- **Phase 4 (Multiplayer Gameplay Integration & Milestone M1.2):** Шаги 4.1–4.6.5 + микро-спринты M1.1/M1.2 реализованы. Milestone **M1 = 1v1 Dedicated Server Prototype**; 2v2 официально перенесён в Milestone M2 (Phase 5), см. OD-48.
  - Устранён Command ACK оракул вражеского ростера (`ValidateControllable` fail-closed, P1-1).
  - Пул `TransportBufferPool` масштабирован под `MaxFragmentsPerGroup = 2048`, аллокации на 65+ фрагментах полностью устранены (0 B GC, P1-01 / OD-50).
  - Ранний терминальный ответ `MatchFinished` в `TryRebindSession` предотвращает зацикливание клиентов в `InvalidState` (P1-02 / OD-50).
  - Защита от гонки потоков при приёме снапшотов после закрытия пира в `LiteNetLibCarrier` (P2-01 / OD-50).
  - Очистка лобби при выходе до старта (`ReleaseFormingSlot`, F2) и удаление соло-старта (F1).
  - Отвязка сокета по ready-дедлайну (`DetachSession`, F3).
  - Непрерывные часы Grace-периода (`SessionGraceClock`, 200с при обрыве во время паузы, F4 / P2-05).
  - Терминация клиентов при победе (`MatchFinished`, F5) и штатном выключении сервера (`ServerShutdown`, F6).
  - Сброс ассемблера в `OwnDatagramCarrier` (P2-5).
  - Разведение enum `InvalidState=5`, `Denied=6` (N-2) и причина смерти `Destroyed` (N-3).

Проверка текущего состояния: **1370/1370 EditMode passed (100%)** и **22/22 PlayMode passed (100%)** (**1392 автоматизированных теста суммарно**, 0 failed, 0 skipped). Все фиксы подтверждены bite-прогонами revert-to-fail.

## Confirmed Baseline

| Область | Фактическое состояние |
|---|---|
| Unity | `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0` |
| Core | детерминированный, без Unity API, 20 Hz; opaque `SessionId`/`MatchId`; `DeltaSnapshotWireCodec`, `SnapshotAckCodec` (34 B), `KeyframeSliceCodec`, `ReplicationRequestCodec`, `ReconnectWireCodec`; `BitMask1024` и `VisibilityEngine` (uniform grid 8192 мм); Zero-GC formation slots, hardening sprints 1.1–1.5.1 |
| Server | `MatchServer`, `TickDriver` (ADR-007), `SessionManager` (re-bind token, 32 B секрет, O(k) grace sweep); `ServerHost` (`GlobalFront.Server`, engine-free); `FogOfWarReplicationFilter` + `VisibilityHistoryRing` (120 тиков, anti-maphack); `ServerReplicationEmitter` с per-client delta streaming и per-client FNV-1a `StateChecksum`; тактическая пауза (OD-18..OD-22) с дедлайном ready |
| Transport | `INetworkCarrier` + `OwnDatagramCarrier` (`VirtualNetworkPipe`, Wi-Fi/WAN/Burst) и `LiteNetLibCarrier` (LiteNetLib 1.3.5, real UDP, `SIMULATE_NETWORK`); `NetworkCommandChannel` с C0/C1/C2; `TransportBufferPool` (Zero-GC receive buffers, Rent/Return accounting); hardening sprints 2.1–2.5; headless-цикл `ServerTransportHost.Pump` в `DedicatedServerRunner` |
| Client replication | `ClientReplicationReceiver`, `ClientReplicationWorld`, `ReplicationReceiverFSM`, `ClientTransportReplicationBridge`, `ClientReconnectCoordinator`; накат per-client дельт, сборка keyframe-слайсов, 10 Hz `SnapshotAck` по C0, Zero-GC `StateChecksum`, `KeyframeInstalled` auto-subscription, `ResyncSelectionViews()` (по `EntityId`), C0 Opcode 14 `ClientReadyToResume` |
| Dedicated / Remote | `DedicatedServerRunner` (`GlobalFront.ServerHost`, 120 FPS pump, production auto-start 1v1, `AutoStartMatch` seam), `RemoteSliceSession` (`SliceNetworkMode.DedicatedClient`, RTT EWMA 1 Hz), CLI `-connect`, меню `GlobalFront/Playtest/Launch 400-Unit Dedicated Match` (`InteractivePlaytestBootstrap`) |
| Commands | Move, Attack и Stop; `ICommandChannel` + `NetworkCommandChannel`; lead calculation (`RenderDelayTicks + RTT/2 + 2`), session token verification, anti-maphack target rejection (`TargetInFogOfWar` до раскрытия статуса цели), multi-batch dispatch |
| Snapshots | Per-client delta streaming с FoW, rate-pacing, keyframe slicing; паритет 400 юнитов доказан под 1% и 5% loss + burst (0 desyncs); live UDP reconnect посреди боя доказан в PlayMode |
| Presentation | RTS camera (`RtsCameraController`) и input layer (`RtsInputManager`); `UnitViewTickBuffer` (SoA, 37 B/slot, adaptive delay); `UnitViewPool` и `UnitViewBinder` (Zero-GC биндинг, `HandleResync` recycling); `UnitOverlayBatcher` + `UnitOverlayRendererFeature` (кольца и HP-бары, URP RenderGraph, 0 B GC); `UnitSelectionController` (каноническое множество `EntityId`, box drag, resync по `EntityId`, удержание selection через пустое зеркало); миникарта и HUD (`PermanentHudPresenter`, `SelectionHudPresenter` с `ValueColumnX = 124f`, `TacticalPauseOverlay`); сквозной 400-юнитный срез (`TacticalVerticalSliceRunner`) |
| Scene | одна включённая `Assets/Scenes/SampleScene.unity` |
| Tests | **1370/1370 EditMode passed (100%)**, **22/22 PlayMode passed (100%)**; **1392 автоматизированных теста суммарно** (100% green, 0 failed, 0 skipped на `66419a8` / `6d8a20c`) |

## Phase 4.0 Status — [100% COMPLETED / AUDITED: ZERO DEFECTS]

- 4.1 Dedicated Server Core Relocation & Headless Runner — [COMPLETE] (`5f2f29e`)
- 4.2 Client Remote Connection & Handshake — [COMPLETE] (`1760f17`)
- 4.2.1 Client Bootstrap CLI `-connect` — [COMPLETE] (`34e994e`)
- 4.3.1 Core Visibility Engine & Visibility History Ring — [COMPLETE] (`e5d2ec0`)
- 4.3.2 Per-Client Replication Filtering & StateChecksum (OD-39/OD-40) — [COMPLETE] (`3666834`)
- 4.4.1 Server FoW Target Validation & Command Rejection Gates (OD-41) — [COMPLETE] (`4e830c9`)
- 4.4.2 Client Lead Calculation, Multi-Batch Dispatch & E2E Network Ingress (OD-41) — [COMPLETE] (`5bdab1e`)
- 4.5.1 Reconnect Signal Wirings & C0 Transport Integration (OD-18/OD-20) — [COMPLETE] (`1b9b288`)
- 4.5.2 View Batch Recycling & Presentation Resync (OD-42) — [COMPLETE] (`364646b`)
- 4.5.3 Live Combat Reconnect & Tactical Pause Governance (OD-42 final) — [COMPLETE] (`57a8cef`)
- 4.5.4 Runtime Loop Closure & Production Runner Wiring — [COMPLETE] (`7be1648`)
- 4.6.1 Reconnect Retry Policy & Socket-Connect Timeout — [COMPLETE] (`eb679e0`)
- 4.6.2 Impairment Harness & Presets (Wi-Fi 1% / WAN 5% + Burst) — [COMPLETE] (`1b38f05`)
- 4.6.3 Parity under Loss/Latency on 400 Units (OD-40 / OD-43) — [COMPLETE] (`4d15ace`)
- 4.6.4 Live UDP PlayMode Harness & Reconnect Path B — [COMPLETE] (`1eeb3d2`, `491811c`, `3eca5de`)
- 4.6.5 Synthetic 400-Unit Playtest (~26 s), Full HD Visual Evidence, Polish & Zero-GC Transport — [COMPLETE] (`666f89f`, `828c55d`, `ff344a9`, `366e444`, `1bac832`)
- M1 Remediation (Block 1–6) — [COMPLETE] (`8e35bb3`, OD-48, OD-49)
- M1.1 Forensic Seal (10 defect fixes across 35 files) — [COMPLETE] (`645a532`, OD-49, 1390 green)
- M1.2 Strict Zero-GC & Match Termination Seal — [COMPLETE] (`66419a8`, OD-50, 1392 green)
- Grand Re-Audit M1.2 Certification — [COMPLETE / CERTIFIED] (`6d8a20c`, Kimi k 3.1 Pro **APPROVE: P0=0, P1=0, P2=0, P3=0**, `Artifacts/GrandAudit-Phase4-M1-66419a8-KIMI.md`)

**Итог: Milestone M1.2 (1v1 Dedicated Server Prototype) полностью сертифицирован независимым состязательным ре-аудитом Kimi k 3.1 Pro (P0=0, P1=0, P2=0, P3=0) на коммите `66419a8` (отчёт `Artifacts/GrandAudit-Phase4-M1-66419a8-KIMI.md`, коммит `6d8a20c`).** 2v2 — Milestone M2 / Phase 5.

## Current Phase (In Progress)

**Milestone M2 / Phase 5: Faction Asymmetry & Strategic Combat**
- Step 5.1: Integer Armor & Damage Matrix (8x7) in `GlobalFront.Core` — [COMPLETE] (`3aec610`, 1409 green EditMode, 0 B GC.Alloc)
- Step 5.2: Base Building, 500mm Snap Grid & Power Grid (`PowerLedger`) — [IN PROGRESS]
- Steps 5.3–5.6: Queues, Economy, 5 Factions, 2v2 Topology — [QUEUED]

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Architecture](ARCHITECTURE.md)
- [Roadmap](ROADMAP.md)
- [Phase 04](Phases/Phase_04_Multiplayer_Integration.md)
- [Phase 05](Phases/Phase_05_Faction_Asymmetry.md)
