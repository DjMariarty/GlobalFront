# Статус проекта GlobalFront

> Фактический технический статус • обновлено 2026-10-26

## Summary

GlobalFront: Phases 1.0 — 2.8 + Phase 3.0 (Шаги 3.1–3.7) + Hardening Phase 3.7.1 + Phase 4.0 (Шаги 4.1–4.6.5) — **[100% COMPLETED / AUDITED: ZERO DEFECTS]**.
- **Phase 1 Hardening:** Спринты 1.1–1.5.1 (`5b16a05`..`349a074`) закрыли race conditions, reentrancy, friendly fire, unit limits и alloc-free snapshots.
- **Phase 2 Hardening & Grand Re-Audit:** Спринты 2.1–2.5 (`5c94e07`..`4cb8ffa`) устранили socket hijack, rate limit leaks, fragment map overflow, concurrency в TickDriver и cross-session reconnect hijack (N-01 / D-10 / A-01; P0/P1 = 0 сертифицирован).
- **Hardening Phase 3.7.1 (Grand Audit remediation):** Блоки 1–4 (`934840d`..`3a112ad`) закрыли F-1..F-11, F-13..F-15 и N-02..N-04 на уровне кода; блок 5 закрыл документальные F-6 и F-10.
- **Phase 3 (Visual Presentation & RTS Controls):** Шаги 3.1–3.7 (ADR-012, OD-23..OD-29) полностью сертифицированы `[APPROVED: ZERO DEFECTS]` (`3414ba6` / `cb01ea2`). Замер OD-28 на 400 юнитах: **0.623 ms/frame** и **0 B GC.Alloc** за 60 steady-state кадров. Milestone **M2 Playable RTS Core** достигнут.
- **Phase 4 (Multiplayer Gameplay Integration):** Шаги 4.1–4.6.5 полностью реализованы и сертифицированы `[APPROVED: ZERO DEFECTS]`. Milestone **M1 Multiplayer Prototype** достигнут.
  - Dedicated Server (`GlobalFront.ServerHost`, `DedicatedServerRunner`, `DedicatedServerBootstrap`).
  - Remote Dedicated Client (`RemoteSliceSession`, `ClientTransportEndpoint`, CLI `-connect`, меню `GlobalFront/Playtest/Launch 400-Unit Dedicated Match`).
  - Server-Side Fog of War (`BitMask1024`, `VisibilityEngine`, `VisibilityHistoryRing`, `FogOfWarReplicationFilter`, per-client delta stream & `StateChecksum`, anti-maphack combat validation).
  - Authoritative Command Ingress (`CalculateLeadTicks`, multi-batch dispatch, sequence token security).
  - Live Combat Reconnect & Tactical Pause (C0 opcode 9/13/14, 4000-тик grace, 5s countdown, C0 auto-resume, `ResyncSelectionViews()`).
  - 400-Unit Parity under Impairment (Wi-Fi 1%, WAN 5% + Burst, 0 desyncs, `ChecksumMismatchCount == 0`).
  - Live UDP PlayMode Harness (`NetworkImpairmentPlayModeTests`, LiteNetLib `SIMULATE_NETWORK`).
  - Synthetic 400-Unit Playtest (`SyntheticPlaytestPlayModeTests`), 1080p capture harness, honest combat damage (74% clash, 50% resume), Zero-GC transport receive buffer pool (`TransportBufferPool.cs`).

Проверка текущего состояния: **1334/1334 EditMode passed (100%)** и **22/22 PlayMode passed (100%)** (**1356 автоматизированных тестов суммарно**, 0 failed, 0 skipped; 0 compiler warnings, 0 compiler errors; gates: `Artifacts/TestResults/step465-polish-editmode.xml` / `Artifacts/TestResults/step465-polish-playmode.xml`, 2026-10-26). Текущая задача / Next Phase — **Phase 5: Faction Asymmetry & Strategic Combat** [READY / NEXT].

## Confirmed Baseline

| Область | Фактическое состояние |
|---|---|
| Unity | `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0` |
| Core | детерминированный, без Unity API, 20 Hz; opaque `SessionId`/`MatchId`; `DeltaSnapshotWireCodec`, `SnapshotAckCodec` (34 B), `KeyframeSliceCodec`, `ReplicationRequestCodec`, `ReconnectWireCodec`; `BitMask1024` и `VisibilityEngine` (uniform grid 8192 мм); Zero-GC formation slots, hardening sprints 1.1–1.5.1 |
| Server | `MatchServer`, `TickDriver` (ADR-007), `SessionManager` (re-bind token, 32 B секрет, O(k) grace sweep); `ServerHost` (`GlobalFront.Server`, engine-free); `FogOfWarReplicationFilter` + `VisibilityHistoryRing` (120 тиков, anti-maphack); `ServerReplicationEmitter` с per-client delta streaming и per-client FNV-1a `StateChecksum`; тактическая пауза (OD-18..OD-22) |
| Transport | `INetworkCarrier` + `OwnDatagramCarrier` (`VirtualNetworkPipe`, Wi-Fi/WAN/Burst) и `LiteNetLibCarrier` (LiteNetLib 1.3.5, real UDP, `SIMULATE_NETWORK`); `NetworkCommandChannel` с C0/C1/C2; `TransportBufferPool` (Zero-GC receive buffers); hardening sprints 2.1–2.5; headless-цикл `ServerTransportHost.Pump` в `DedicatedServerRunner` |
| Client replication | `ClientReplicationReceiver`, `ClientReplicationWorld`, `ReplicationReceiverFSM`, `ClientTransportReplicationBridge`, `ClientReconnectCoordinator`; накат per-client дельт, сборка keyframe-слайсов, 10 Hz `SnapshotAck` по C0, Zero-GC `StateChecksum`, `KeyframeInstalled` auto-subscription, `ResyncSelectionViews()`, C0 Opcode 14 `ClientReadyToResume` |
| Dedicated / Remote | `DedicatedServerRunner` (`GlobalFront.ServerHost`, 120 FPS pump), `RemoteSliceSession` (`SliceNetworkMode.DedicatedClient`, RTT EWMA 1 Hz), CLI `-connect`, меню `GlobalFront/Playtest/Launch 400-Unit Dedicated Match` (`InteractivePlaytestBootstrap`) |
| Commands | Move, Attack и Stop; `ICommandChannel` + `NetworkCommandChannel`; lead calculation (`RenderDelayTicks + RTT/2 + 2`), session token verification, anti-maphack target rejection (`TargetInFogOfWar`), multi-batch dispatch |
| Snapshots | Per-client delta streaming с FoW, rate-pacing, keyframe slicing; паритет 400 юнитов доказан под 1% и 5% loss + burst (0 desyncs); live UDP reconnect посреди боя доказан в PlayMode |
| Presentation | RTS camera (`RtsCameraController`) и input layer (`RtsInputManager`); `UnitViewTickBuffer` (SoA, 37 B/slot, adaptive delay); `UnitViewPool` и `UnitViewBinder` (Zero-GC биндинг, `HandleResync` recycling); `UnitOverlayBatcher` + `UnitOverlayRendererFeature` (кольца и HP-бары, URP RenderGraph, 0 B GC); `UnitSelectionController` (каноническое множество `EntityId`, box drag, `ResyncSelectionViews()`); миникарта и HUD (`PermanentHudPresenter`, `SelectionHudPresenter` с `ValueColumnX = 124f`, `TacticalPauseOverlay`); сквозной 400-юнитный срез (`TacticalVerticalSliceRunner`) |
| Scene | одна включённая `Assets/Scenes/SampleScene.unity` |
| Tests | **1334/1334 EditMode passed (100%)**, **22/22 PlayMode passed (100%)**; **1356 автоматизированных тестов суммарно** (0 warnings, 0 errors; gates: `Artifacts/TestResults/step465-polish-editmode.xml`, `Artifacts/TestResults/step465-polish-playmode.xml`, 2026-10-26) |

## Phase 4.0 Status — [100% COMPLETED / AUDITED: ZERO DEFECTS]

- 4.1 Dedicated Server Core Relocation & Headless Runner — [COMPLETE / AUDITED] (`5f2f29e`)
- 4.2 Client Remote Connection & Handshake — [COMPLETE / AUDITED] (`1760f17`)
- 4.2.1 Client Bootstrap CLI `-connect` — [COMPLETE / AUDITED] (`34e994e`)
- 4.3.1 Core Visibility Engine & Visibility History Ring — [COMPLETE / AUDITED] (`e5d2ec0`)
- 4.3.2 Per-Client Replication Filtering & StateChecksum (OD-39/OD-40) — [COMPLETE / AUDITED] (`3666834`)
- 4.4.1 Server FoW Target Validation & Command Rejection Gates (OD-41) — [COMPLETE / AUDITED] (`4e830c9`)
- 4.4.2 Client Lead Calculation, Multi-Batch Dispatch & E2E Network Ingress (OD-41) — [COMPLETE / AUDITED] (`5bdab1e`)
- 4.5.1 Reconnect Signal Wirings & C0 Transport Integration (OD-18/OD-20) — [COMPLETE / AUDITED] (`1b9b288`)
- 4.5.2 View Batch Recycling & Presentation Resync (OD-42) — [COMPLETE / AUDITED] (`364646b`)
- 4.5.3 Live Combat Reconnect & Tactical Pause Governance (OD-42 final) — [COMPLETE / AUDITED] (`57a8cef`)
- 4.5.4 Runtime Loop Closure & Production Runner Wiring — [COMPLETE / AUDITED] (`7be1648`)
- 4.6.1 Reconnect Retry Policy & Socket-Connect Timeout — [COMPLETE / AUDITED] (`eb679e0`)
- 4.6.2 Impairment Harness & Presets (Wi-Fi 1% / WAN 5% + Burst) — [COMPLETE / AUDITED] (`1b38f05`)
- 4.6.3 Parity under Loss/Latency on 400 Units (OD-40 / OD-43) — [COMPLETE / AUDITED] (`4d15ace`)
- 4.6.4 Live UDP PlayMode Harness & Reconnect Path B — [COMPLETE / AUDITED] (`1eeb3d2`, `491811c`, `3eca5de`)
- 4.6.5 5-Minute 400-Unit Playtest, Full HD Visual Evidence, Polish & Zero-GC Transport — [COMPLETE / AUDITED] (`666f89f`, `828c55d`, `ff344a9`, `366e444`)

**Итог: Milestone M1 (Multiplayer Prototype) ПОЛНОСТЬЮ ДОСТИГНУТ**.

## Next Phase

**Phase 5: Faction Asymmetry & Strategic Combat** (Roster, asymmetric faction mechanics, armor/damage matrix, production buildings, resource flow).

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Architecture](ARCHITECTURE.md)
- [Roadmap](ROADMAP.md)
- [Phase 04](Phases/Phase_04_Multiplayer_Integration.md)
- [Phase 05](Phases/Phase_05_Faction_Asymmetry.md)
