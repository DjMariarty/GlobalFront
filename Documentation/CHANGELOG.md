# Журнал изменений GlobalFront

> Исторический журнал подтверждённых изменений; будущий roadmap сюда не переносится как реализованный.

## Unreleased

### Documentation

- Документация синхронизирована с утверждённым Master Development Plan.
- Добавлены `MASTER_GAME_PLAN.md`, product `GDD.md` и короткий `CURRENT_STATE.md`.
- Roadmap заменён утверждённым Phase Plan 0–12 и milestones M0–M8.
- Фазовые документы приведены к единой структуре Goal → Definition of Done.
- Зафиксированы пять основных фракций 1.0 и перенос подфракций в post-1.0.
- Current baseline отделён от approved target architecture.
- ADR-009 Network Transport Architecture (Phase 2.5) — Accepted (OD-1 = LiteNetLib как preferred initial carrier за контрактом `INetworkCarrier`); R&D-документы в `Documentation/Research/`.
- ADR-014 Phase 4 Multiplayer Gameplay Integration & Architecture — Accepted & Closed (OD-35..OD-43).
- Milestone M1 (Multiplayer Prototype) полностью закрыт и зафиксирован.

### Technical Milestones

- Phase 2.1 Server-Owned Match State — `8178110`.
- Phase 2.2 Command Channel — `313e9ef`.
- `ICommandChannel` и `LocalCommandChannel` отделяют client command queue от конкретного local host.
- `StopCommand` включён в command channel baseline.
- Phase 2.3 Server Tick Driver — реализован и принят (ADR-007), commit `ea0435d`: engine-independent `TickDriver` в `GlobalFront.Server` (20 Hz, bounded catch-up, manual/real-time); `LocalMatchHost` (ServerHost) владеет `MatchServer` и `TickDriver`; `FixedSimulationRunner` удалён; server tick lifecycle отделён от Unity client lifecycle.
- Phase 2.4 Session / Player Identity — реализована и принята (ADR-008), commit `73d1276`: opaque `SessionId`/`MatchId` в Core; `SessionManager` в `GlobalFront.Server` — единственный источник `PlayerId` (монотонное назначение по порядку join, без повторного использования); session gate перед неизменным `MatchServer`; `LocalCommandChannel` session-attributed; клиент получает `PlayerId` от сервера (`ClientSession`); match lifecycle завершается на `MatchPhase.Finished` — `Closed` зарезервирован для будущего server lifecycle/teardown.
- Phase 2.5 Network Transport — COMPLETE, `feat: implement network transport (Phase 2.5)`, по ADR-009 (OD-1 = LiteNetLib); финальный независимый review = **APPROVE** (P0=0/P1=0/P2=0): контракт `INetworkCarrier`; собственный детерминированный `OwnDatagramCarrier` поверх `VirtualNetworkPipe`; `LiteNetLibCarrier` (LiteNetLib 1.3.5, real UDP); `ServerTransportHost`/`ClientTransportEndpoint` — host-thread orchestration и атрибуция токен → `ConnectionHandle` → `SessionId` → session gate; `NetworkCommandChannel` реализует `ICommandChannel`.
- Phase 2.6 Snapshot Networking — COMPLETE (ADR-010; delta compression, history ring, keyframe slicing; 599/599 EditMode, 6/6 PlayMode).
- Phase 2.7 Reconnect & Resync — COMPLETE (session reattachment, full state resync, tactical pause sync, 5s countdown; 641/641 EditMode, 6/6 PlayMode).
- Phase 2.8 Network Prototype Playtest (2v2) — COMPLETE (2v2 topology, abandonment unpause, HUD overlay; 661/661 EditMode, 6/6 PlayMode).
- Phase 3.1 RTS Camera & Input — COMPLETE (`59ef38a`, 665 тестов; `8144541`, 723 теста; `2b71414`, 725 тестов; [APPROVED: ZERO DEFECTS]).
- Phase 3.2 UnitViewTickBuffer & Interpolation — COMPLETE (`6ab12ad` → `eac0e1d`, 735 тестов; safe alpha, adaptive delay, [APPROVED: ZERO DEFECTS]).
- OD-29 UnitKind Replication & UnitCatalog — COMPLETE (`be03002`, `212740a`, `27a10f6`, 687 тестов; protocol v2, 40 B `DeltaAddRecord`, [APPROVED: ZERO DEFECTS]).
- Phase 3.3 UnitViewBinder & Object Pooling — COMPLETE (`bcb1e78` → `0243971`, `e69bed1`, 719 тестов; Zero-GC O(1) биндинг, [APPROVED: ZERO DEFECTS]).
- Phase 3.4 Instanced Selection Rings & HP Bars — COMPLETE (`27a10f6` → `b897eea`, 770 тестов; URP 17.6 RenderGraph pass, [APPROVED: ZERO DEFECTS]).
- Phase 3.5 Selection System, Screen-Space Drag-Box & RTS Command Issuing — COMPLETE (`cfa24c2`, 867 EditMode + 6 PlayMode; zero-physics picking, каноническое множество, [APPROVED: ZERO DEFECTS]).
- Phase 3.6 Minimap & Tactical HUD — COMPLETE (`2a754db`, 1043 EditMode + 6 PlayMode; ADR-012, OD-24/OD-27/OD-28; `MinimapProjection`, `MinimapRadarModel`, батченый `MinimapRadarGraphic` в 1 UI draw call, `MinimapInteractionController`, `TacticalHudPointerBlocker`, `PermanentHudPresenter`, `SelectionHudPresenter`; 0 B GC steady state, [APPROVED: ZERO DEFECTS]).
- Phase 3.7 Vertical Slice Integration, 400-Unit Benchmark, Multi-Frame PlayMode Suite & Architecture Invariants Gate — COMPLETE (`3414ba6`, `cb01ea2`; ADR-012, OD-23..OD-29; `TacticalVerticalSliceRunner`, `LocalReplicationLoopback`, `CorpseReapSnapshotSource`; 400 юнитов end-to-end; фиксы `ComputeStateChecksum` и `ComputeDirtyMask`; 30 тестов бенчмарка, 7 мультикадровых PlayMode, 12 архитектурных гейтов; профиль OD-28: 0.623 ms/frame, 0 B GC; 1086 EditMode + 13 PlayMode; [APPROVED: ZERO DEFECTS]).
- Phase 4.1 Dedicated Server Core Relocation & Headless Runner — COMPLETE (`5f2f29e`, ADR-014, OD-35..OD-37).
- Phase 4.2 Client Remote Connection & Handshake — COMPLETE (`1760f17`, OD-38).
- Phase 4.2.1 Client Bootstrap CLI `-connect` — COMPLETE (`34e994e`).
- Phase 4.3.1 Core Visibility Engine & Visibility History Ring — COMPLETE (`e5d2ec0`, OD-39).
- Phase 4.3.2 Per-Client Replication Filtering & StateChecksum — COMPLETE (`3666834`, OD-39, OD-40).
- Phase 4.4.1 Server FoW Target Validation & Command Rejection Gates — COMPLETE (`4e830c9`, OD-41).
- Phase 4.4.2 Client Lead Calculation, Multi-Batch Dispatch & E2E Ingress — COMPLETE (`5bdab1e`, OD-41).
- Phase 4.5.1 Reconnect Signal Wirings & C0 Transport Integration — COMPLETE (`1b9b288`, OD-18, OD-20).
- Phase 4.5.2 View Batch Recycling & Presentation Resync — COMPLETE (`364646b`, OD-42).
- Phase 4.5.3 Live Combat Reconnect & Tactical Pause Governance — COMPLETE (`57a8cef`, OD-42).
- Phase 4.5.4 Runtime Loop Closure & Production Runner Wiring — COMPLETE (`7be1648`).
- Phase 4.6.1 Reconnect Retry Policy & Socket-Connect Timeout — COMPLETE (`eb679e0`).
- Phase 4.6.2 Impairment Harness & Presets (Wi-Fi/WAN/Burst) — COMPLETE (`1b38f05`).
- Phase 4.6.3 Parity under Loss/Latency on 400 Units — COMPLETE (`4d15ace`, OD-40, OD-43).
- Phase 4.6.4 Live UDP PlayMode Harness & Reconnect Path B — COMPLETE (`1eeb3d2`, `491811c`, `3eca5de`).
- Phase 4.6.5 5-Minute 400-Unit Playtest, Full HD Visual Evidence, Polish & Zero-GC Transport — COMPLETE (`666f89f`, `828c55d`, `ff344a9`, `366e444`, `1bac832`).

### Verified

- Unity `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0`.
- **1334/1334 EditMode passed (100%)**, 0 failed, 0 skipped, 2026-10-26 (gate: `Artifacts/TestResults/step465-polish-editmode.xml`).
- **22/22 PlayMode passed (100%)**, 0 failed, 0 skipped, 2026-10-26 (gate: `Artifacts/TestResults/step465-polish-playmode.xml`).
- **1356 автоматизированных тестов суммарно** (0 compiler warnings, 0 compiler errors).
- Zero-GC сетевого транспорта и симуляции под непрерывным боем 402 юнитов: **0 блоков / 0 байт GC.Alloc**.
- 6 визуальных артефактов Full HD 1080p в `Artifacts/Playtest/` (`01_spawn_and_fog.png` .. `06_interactive_menu.png`).

## Confirmed Foundation History

| Commit | Milestone |
|---|---|
| `1bac832` | Phase 4.6.5 stabilization: manual server tick pump during post-command delta waits under loss |
| `366e444` | Phase 4.6.5 Polish Sprint: selection rings resync, HUD 124px layout, honest HP damage, interactive bootstrap, Zero-GC TransportBufferPool |
| `ff344a9` | Phase 4.6.5 Step 3: synthetic 400-unit PlayMode test, 1080p capture harness, remote pool warming |
| `828c55d` | Phase 4.6.5 Step 2: wire TacticalPauseOverlay to NetworkHudPresenter and frame presentation loop |
| `666f89f` | Phase 4.6.5 Step 1: Pump-owned countdown ticker with epsilon threshold |
| `3eca5de` | Phase 4.6.4 follow-up: frame-driven countdown, retry latch, strict awaitingResume authorization |
| `491811c` | Phase 4.6.4 Path B: wire C0 ClientReadyToResume auto-resume protocol and document LiteNetLib build provenance |
| `1eeb3d2` | Phase 4.6.4 Step 4.6.4 live UDP liveness & reconnect under packet loss in PlayMode |
| `4d15ace` | Phase 4.6.3 validate 400-unit bitwise parity under Wi-Fi/WAN impairment and burst rebase |
| `1b38f05` | Phase 4.6.2 deterministic network impairment harness & presets (Wi-Fi/WAN/Burst) |
| `eb679e0` | Phase 4.6.1 reconnect retry policy & socket-connect timeout |
| `7be1648` | Phase 4.5.4 runtime loop closure & production runner wiring |
| `57a8cef` | Phase 4.5.3 live combat reconnect & tactical pause governance on 400 units |
| `364646b` | Phase 4.5.2 view batch recycling & presentation resync |
| `1b9b288` | Phase 4.5.1 reconnect signal wirings & C0 transport integration |
| `5bdab1e` | Phase 4.4.2 client lead calculation, multi-batch dispatch & E2E network ingress |
| `4e830c9` | Phase 4.4.1 server FoW target validation & command rejection gates |
| `3666834` | Phase 4.3.2 per-client replication filtering & StateChecksum |
| `e5d2ec0` | Phase 4.3.1 core visibility engine & visibility history ring |
| `34e994e` | Phase 4.2.1 client bootstrap CLI `-connect` |
| `1760f17` | Phase 4.2 client remote connection & handshake |
| `5f2f29e` | Phase 4.1 dedicated server core relocation & headless runner |
| `3a112ad` | Hardening Phase 3.7.1 Block 4: transport/uplink seam, URP overlay feature, OD-28 disclaimer |
| `a944623` | Hardening Phase 3.7.1 Block 3: zero-alloc ClientSession update, ArrayPool reassembly |
| `a08beaf` | Hardening Phase 3.7.1 Block 2: sink parity, checksum fields, order agreement, pacing comments |
| `934840d` | Hardening Phase 3.7.1 Block 1: disconnect leak guard, denial telemetry, exception isolation, fragment eviction |
| `4cb8ffa` | Phase 2.0 Remediation Sprint 2.5: exclusive session-socket binding & reconnect hijack guard |
| `e6d91ea` | Phase 2.0 Remediation Sprint 2.4: thread-safe tick driver & bounded fragment reassembly maps |
| `750218c` | Phase 2.0 Remediation Sprint 2.3: canonical PartIndex keyframe assembly |
| `ceae9a8` | Phase 2.0 Remediation Sprint 2.2: Zero-GC send scratch buffer & receive pooling |
| `5c94e07` | Phase 2.0 Remediation Sprint 2.1: socket hijack guard, O(k) grace sweep |
| `349a074` | Phase 1.0 Remediation Sprint 5.1: atomic concurrency guard, capacity validation hoisting |
| `3e413a7` | Phase 1.0 Remediation Sprint 5: final hardening, reentrancy guard, unit cap |
| `cb01ea2` | Phase 3 presentation and selection batching polish |
| `3414ba6` | Phase 3.7 Tactical Vertical Slice, 400-unit benchmark, architecture gates |
| `2a754db` | Phase 3.6 Minimap & Tactical HUD |
| `cfa24c2` | Phase 3.5 Selection System & Screen-Space Drag-Box |
| `b897eea` | Phase 3.4 Instanced Selection Rings & HP Bars remediation |
| `27a10f6` | Phase 3.4 Instanced Selection Rings & HP Bars |
| `bcb1e78` | Phase 3.3 UnitViewBinder & Object Pooling |
| `be03002` | OD-29 UnitKind Replication & UnitCatalog |
| `6ab12ad` | Phase 3.2 UnitViewTickBuffer & Interpolation |
| `59ef38a` | Phase 3.1 RTS Camera & Input |
| `a37f53d` | Grand Adversarial Audit Phase 1.0–2.8 certification [APPROVED: ZERO DEFECTS] |
| `313e9ef` | Phase 2.2 Command Channel Abstraction |
| `8178110` | Phase 2.1 Server-Owned Match State через `MatchConfig` |
| `b50f474` | Snapshot Serialization Protocol v1 |
| `f4a496f` | PlayMode validation local authoritative pipeline |
| `8e687e3` | Local authoritative `LocalMatchHost` integration |
| `66bc76b` | Unity repository restructure |

## Historical Git Tags

| Tag | Commit | Context |
|---|---|---|
| `v0.2-matchserver` | `90a4bad` | MatchServer milestone |
| `v0.2.1-project-cleanup` | `4a9ff3a` | project cleanup |
| `v0.2.2-pre-restructure-backup` | `4a9ff3a` | pre-restructure backup |
| `v0.3.0-restructure` | `66bc76b` | repository restructure |

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Project Status](PROJECT_STATUS.md)
- [Roadmap](ROADMAP.md)
- [Phase 04](Phases/Phase_04_Multiplayer_Integration.md)
- [Phase 05](Phases/Phase_05_Faction_Asymmetry.md)
