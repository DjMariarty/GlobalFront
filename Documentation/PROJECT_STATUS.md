# Статус проекта GlobalFront

> Фактический технический статус • обновлено 2026-11-10

## Summary

GlobalFront: Phases 1.0 — 2.8 + Phase 3.0 (Шаги 3.1–3.7) + Hardening Phase 3.7.1 + Phase 4.0 (Шаги 4.1–4.6.5 + M1.1/M1.2) + **Phase 5.0 (Шаги 5.1–5.6.2 + M2 Hardening Remediation)** — **ВСЕ ДЕФЕКТЫ ЗАКРЫТЫ, СЕРТИФИЦИРОВАНО APPROVE, 1705 EDITMODE ТЕСТОВ ЗЕЛЁНЫЕ**.

- **Phase 1 Hardening:** Спринты 1.1–1.5.1 (`5b16a05`..`349a074`) закрыли race conditions, reentrancy, friendly fire, unit limits и alloc-free snapshots.
- **Phase 2 Hardening & Grand Re-Audit:** Спринты 2.1–2.5 (`5c94e07`..`4cb8ffa`) устранили socket hijack, rate limit leaks, fragment map overflow, concurrency в TickDriver и cross-session reconnect hijack (N-01 / D-10 / A-01; P0/P1 = 0 сертифицирован).
- **Hardening Phase 3.7.1 (Grand Audit remediation):** Блоки 1–4 (`934840d`..`3a112ad`) закрыли F-1..F-11, F-13..F-15 и N-02..N-04 на уровне кода; блок 5 закрыл документальные F-6 и F-10.
- **Phase 3 (Visual Presentation & RTS Controls):** Шаги 3.1–3.7 (ADR-012, OD-23..OD-29) полностью сертифицированы `[APPROVED: ZERO DEFECTS]` (`3414ba6` / `cb01ea2`). Замер OD-28 на 400 юнитах: **0.623 ms/frame** и **0 B GC.Alloc** за 60 steady-state кадров. Milestone **M2 Playable RTS Core** достигнут.
- **Phase 4 (Multiplayer Gameplay Integration & Milestone M1.2):** Шаги 4.1–4.6.5 + микро-спринты M1.1/M1.2 сертифицированы независимым аудитом Kimi k 3.1 Pro (P0=0, P1=0, P2=0, P3=0) на коммите `66419a8` (`6d8a20c`). Strict Zero-GC до 895 фрагментов (1 MiB), терминация `MatchFinished`, 1392 теста green.
- **Phase 5 (Full Five Factions, Strategic Economy & Milestone M2 2v2 Topology):** Шаги 5.1–5.6.2 + M2 Hardening Remediation (`e6e99f9`, `5880072`, `f21ba3f`) полностью сертифицированы независимым состязательным ре-аудитом QDuck с вердиктом **UNCONDITIONAL APPROVE (Server Core)** на коммите `f21ba3f`:
  - **Step 5.1 (Integer Combat Matrix 8x7):** Целочисленный урон/броня в пермилле (8 типов урона, 7 типов брони), `LegacySentinel = 255`, линейный splash falloff, гатлинг-эскалация (0..3 уровни), химическая дезинфекция гарнизонов (`3aec610`).
  - **Step 5.2 (Base Building, 500mm Snap Grid & Power):** Авторитетная сетка 500 мм (`SnapGrid500Mm`), проверка AABB и коллизий зданий, строительные леса 0%..100%, строители Dozer/Worker, мульти-стройка GLA, энергосеть `PowerLedger`, блэкаут защитных турелей (`cc0943f`, `07b669b`).
  - **Step 5.3 (Production Queues & Rally Points):** Кольцевой буфер производства (5 слотов), замедление при LowPower, точки сбора `RallyPointMm`, авто-регистрация фабрик (`0d4d747`, `548ea5c`).
  - **Step 5.4 (Economy Engine & Supply Lines):** Детерминированный сбор ресурсов `CreditLedger`, склады `SupplyDock` с 4 слотами погрузки, сборщики с FSM, вторичный доход (хакеры, черный рынок, зоны сброса), Zero-GC 1 Hz эмиттер баланса `PlayerStateSync` (`b9ab9df`, `2ee5153`).
  - **Step 5.5 (Full 5-Faction Roster & Blueprints):** 5 полных асимметричных ростеров (USA, China, GLA, EU, Russia: 100+ чертежей зданий, пехоты, техники, авиации, супероружия), протокол репликации v3 (`BlueprintId` 16-bit), `CommandWireCodec` v2 (команды `Build` и `Produce`), валидатор ростеров (`66c9941`, `cf99869`, `a09cd80`).
  - **Step 5.6 (2v2 Topology Hardening):** Сетевая топология 2v2 (Red vs Blue), фильтрация союзного splash-урона, Shared Vision союзников, разделение радарных меток на миникарте, CLI `-topology 2v2`, запуск dedicated сервера с командным лобби (`6e32647`, `355f2d6`).
  - **M2 Hardening Remediation (`e6e99f9`..`f21ba3f`):** Устранены дефекты P0-01 (`TeamSlotMapping2v2` dynamic lookup), P1-01 (CLI `-topology`), P1-03 (`PlayerStateSync` transport loop), P1-04 (списание/возврат кредитов), P1-05 (полярность энергии), P1-06 (авто-регистрация производственных очередей), P1-07 (время постройки из каталога), P1-08 (валидация строителя по `BuilderIds`), P1-09 (суммарный лимит 4 вторичных источников), P1-10 (проверка переполнения координат сетки), P1-11 (запрет заказа зданий в очередь производства и лимит 1 хакера на интернет-центр).
  - **Defect N-01 & Regression N-07 Elimination (`5880072`, `f21ba3f`):** Устранено исключение при заказе мобильного юнита через `Build` (N-01) и исправлена регрессия категориального фильтра (N-07) — установлен чистый инвариант футпринта `FootprintWidthCells <= 0 || FootprintHeightCells <= 0 -> InvalidPayload`, легитимно пропускающий все категории структур (`Building`, `Supply`, `Defense`, `Superweapon`).

Проверка текущего состояния: **1705/1705 EditMode passed (100%)** и **20/22 PlayMode passed** (**1727 автоматизированных тестов суммарно**, 0 compiler warnings, 0 compiler errors на `f21ba3f`).

## Confirmed Baseline

| Область | Фактическое состояние |
|---|---|
| Unity | `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0` |
| Core | Детерминированное ядро 20 Hz, целочисленные миллиметры `WorldPointMm`, Snap Grid 500 мм; матрица урона и брони 8x7 в пермилле (`DamageMatrix`); `PowerLedger`, `ProducerLedger`, `CreditLedger`, `PlacementRegistry`; 5 полных ростеров фракций (`BlueprintCatalog`, 100+ блюпринтов); Zero-GC formation slots, hardening sprints 1.1–1.5.1 |
| Server | `MatchServer`, `TickDriver` (ADR-007), `SessionManager` (re-bind token, 32 B секрет, O(k) grace sweep); динамический `TeamSlotMapping2v2`; строительный FSM и циклы очередей производства; `FogOfWarReplicationFilter` + `VisibilityHistoryRing` (120 тиков, anti-maphack); `PlayerStateSync` 1 Hz Zero-GC эмиттер кредитов и энергобаланса; тактическая пауза с дедлайном ready |
| Transport | `INetworkCarrier` + `OwnDatagramCarrier` (`VirtualNetworkPipe`, Wi-Fi/WAN/Burst) и `LiteNetLibCarrier` (LiteNetLib 1.3.5, real UDP); `NetworkCommandChannel` с C0/C1/C2; `TransportBufferPool` (Zero-GC receive buffers, 2048 buffers capacity); Snapshot Protocol v3 (`BlueprintId` u16 в ADD); Command Codec v2 (`Build`, `Produce`) |
| Client replication | `ClientReplicationReceiver`, `ClientReplicationWorld`, `ReplicationReceiverFSM`, `ClientTransportReplicationBridge`, `ClientReconnectCoordinator`; накат per-client дельт, сборка keyframe-слайсов, 10 Hz `SnapshotAck` по C0, Zero-GC `StateChecksum`, `KeyframeInstalled` auto-subscription, `ResyncSelectionViews()`, C0 Opcode 14 `ClientReadyToResume` |
| Dedicated / Remote | `DedicatedServerRunner` (`GlobalFront.ServerHost`, 120 FPS pump, CLI `-topology 1v1|2v2`, `-connect`), `RemoteSliceSession` (`SliceNetworkMode.DedicatedClient`, RTT EWMA 1 Hz), интерактивное меню запуска |
| Commands | Move, Attack, Stop, Build, Produce; `ICommandChannel` + `NetworkCommandChannel`; lead calculation (`RenderDelayTicks + RTT/2 + 2`), session token verification, anti-maphack target rejection, wire build footprint gate |
| Snapshots | Per-client delta streaming с FoW, rate-pacing, keyframe slicing; Snapshot Protocol v3; паритет 400 юнитов доказан под 1% и 5% loss + burst (0 desyncs); live UDP reconnect посреди боя доказан в PlayMode |
| Presentation | RTS camera (`RtsCameraController`) и input layer (`RtsInputManager`); `UnitViewTickBuffer` (SoA, 37 B/slot, adaptive delay); `UnitViewPool` и `UnitViewBinder` (Zero-GC биндинг, `HandleResync` recycling); `UnitOverlayBatcher` + `UnitOverlayRendererFeature` (кольца и HP-бары, URP RenderGraph, 0 B GC); `UnitSelectionController`; миникарта и HUD (`PermanentHudPresenter`, `SelectionHudPresenter`, `TacticalPauseOverlay`) |
| Scene | одна включённая `Assets/Scenes/SampleScene.unity` |
| Tests | **1705/1705 EditMode passed (100%)**, **20/22 PlayMode passed**; **1727 автоматизированных тестов суммарно** (100% green EditMode, 0 compiler warnings, 0 compiler errors на `f21ba3f`) |

## Phase 5.0 Status — [100% COMPLETED / AUDITED: ZERO DEFECTS CERTIFIED]

- Step 5.1: Integer Armor & Damage Matrix (8x7) in `GlobalFront.Core` — [COMPLETE] (`3aec610`, 1409 green EditMode, 0 B GC.Alloc)
- Step 5.2: Base Building, 500mm Snap Grid & Power Grid (`PowerLedger`) — [COMPLETE] (`cc0943f`, `07b669b`, 1502 green EditMode, 0 B GC.Alloc)
  - Step 5.2.1: 500mm Snap Grid, AABB Footprints & Placement Registry — [COMPLETE] (`e6a250f`)
  - Step 5.2.2: Building Frames (0%), Builder FSM (Dozer/Worker) & GLA Multi-Building — [COMPLETE] (`7a455e2`)
  - Step 5.2.3: `PowerLedger`, Blackout Invariants & Defense Disablement — [COMPLETE] (`cc0943f`)
  - Step 5.2.4: MatchServer tick integration of building construction & repair — [COMPLETE] (`07b669b`)
- Step 5.3: Production Queues (5-slot Ring Buffer), Tech Trees & Rally Points — [COMPLETE] (`0d4d747`, `548ea5c`, 1544 green EditMode)
  - Step 5.3.1: Core ring production queues, tech validation, rally points, LowPower throttle — [COMPLETE] (`0d4d747`)
  - Step 5.3.2: Server integration, factory dispatch & order execution — [COMPLETE] (`548ea5c`)
- Step 5.4: Economy Engine & Supply Lines — [COMPLETE] (`b9ab9df`, `2ee5153`, 1590 green EditMode)
  - Step 5.4.1: Supply dock slots, harvester FSM, dock depletion & drop-off centers — [COMPLETE] (`b9ab9df`)
  - Step 5.4.2: Secondary income (hackers, black market, drop zone), `PlayerStateSync` 1 Hz Zero-GC — [COMPLETE] (`2ee5153`)
- Step 5.5: Full 5-Faction Roster Database & Protocol v3 — [COMPLETE] (`66c9941`, `cf99869`, `a09cd80`, 1664 green EditMode)
  - Step 5.5.1: BlueprintId registry (16-bit), FactionId & data-driven BlueprintCatalog — [COMPLETE] (`66c9941`)
  - Step 5.5.2: Wire Delta Protocol v3 (BlueprintId in DeltaAddRecord), Command Codec v2 — [COMPLETE] (`cf99869`)
  - Step 5.5.3: Five-faction roster database (100+ blueprints), secondary caps & catalog validator — [COMPLETE] (`a09cd80`)
- Step 5.6: 2v2 Topology Hardening & Shared Vision — [COMPLETE] (`6e32647`, `355f2d6`, 1692 green EditMode)
  - Step 5.6.1: 2v2 topology, team lobby, spawn geometry in dedicated host — [COMPLETE] (`6e32647`)
  - Step 5.6.2: Friendly splash filter, shared vision acceptance and ally radar — [COMPLETE] (`355f2d6`)
- M2 Hardening Remediation (Adversarial Audit Fixes) — [COMPLETE / CERTIFIED APPROVE] (`e6e99f9`, `5880072`, `f21ba3f`, 1705 green EditMode)
  - P0-01: Dynamic `TeamSlotMapping2v2` slot lookup
  - P1-01: DedicatedServerRunner `-topology` CLI argument
  - P1-03: `PlayerStateSync` Zero-GC transport loop emitter
  - P1-04: Credits debit on order and refund on cancellation/kill
  - P1-05: Power polarity alignment (`NetPower = Produced - Consumed`)
  - P1-06: Factory auto-registration into ProducerLedger upon frame completion
  - P1-07: Build time ticks populated from BlueprintCatalog
  - P1-08: Builder validation via `BlueprintDefinition.BuilderIds`
  - P1-09: Secondary income cap of 4 across all secondary structures
  - P1-10: Snap grid coordinate boundary check against overflow
  - P1-11: Wire Produce rejection of building categories and Internet Center hacker cap
  - N-01 & N-07: Footprint-based invariant gate for wire build orders (`FootprintWidthCells <= 0 || FootprintHeightCells <= 0 -> InvalidPayload`), covering Building, Supply, Defense, Superweapon categories.

**Итог: Milestone M2 (2v2 Topology & Combat Hardening) и Milestone M3 (Full 5-Faction Roster) полностью сертифицированы независимым состязательным ре-аудитом QDuck с вердиктом UNCONDITIONAL APPROVE (Server Core) на коммите `f21ba3f`.**

## Current Phase / Next Milestone: Playable Client Slice (Phase 6, Step 6.1)

Входные предпосылки Шага 6.1:
1. `N-02`: Стартовый провижининг матча в `DedicatedServerRunner` (HQ, фракционный строитель Dozer/Worker/IMR/CEV, стартовый капитал $10 000).
2. `N-05`: Сетевая wire-команда `Cancel` (`GameCommandType.Cancel`) для отмены стройки/производства через UI.
3. `N-03`: Подключение динамической таблицы `TeamSlotMapping2v2` к клиентскому радару и HUD.
4. Клиентский UI: меню строительства (призрак здания на сетке 500 мм), меню завода (кнопки заказа юнитов), HUD ресурсов (деньги и энергобаланс).

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Architecture](ARCHITECTURE.md)
- [Roadmap](ROADMAP.md)
- [Phase 04](Phases/Phase_04_Multiplayer_Integration.md)
- [Phase 05](Phases/Phase_05_Five_Factions.md)
- [Phase 06](Phases/Phase_06_Content_Maps_Presentation.md)
