# GlobalFront Current State

> Быстрый handoff для AI-агента • обновлено 2026-09-28

## Current Phase

**Phase 3.0 (Visual Presentation & RTS Controls) — [100% COMPLETED / AUDITED: ZERO DEFECTS]**

## Current Task

**Phase 3 — ЗАВЕРШЕНА полностью (Шаги 3.1–3.7) • Следующая фаза: Phase 4 (Multiplayer Gameplay Integration) — [READY / NEXT]**

Phases 1.0–2.8 — **[100% COMPLETED / AUDITED]**. Grand Adversarial Audit (Phases 1.0 — 2.8) — **[APPROVED: ZERO DEFECTS]** на `a37f53d` (отчёт: `Artifacts/GrandAudit-Certification-a37f53d.md`). Phase 3.1 (RTS Camera & Input) — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`59ef38a` → `8144541`, `2b71414`). Phase 3.2 (UnitViewTickBuffer & Interpolation) — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`6ab12ad` → `eac0e1d`). OD-29 (UnitKind Replication, Protocol v2 & UnitCatalog) — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`be03002`, P3 F-1/F-2 закрыты в `27a10f6`). Phase 3.3 (UnitViewBinder & Object Pooling) — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`bcb1e78` → `0243971`, `e69bed1`). Phase 3.4 (Instanced Selection Rings & HP Bars) — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`27a10f6` → `b897eea`, двойной независимый adversarial-аудит, P0-1/P1-1/P1-2/P2-1/P2-4/P2-5/P3-1/P3-2/P3-3 закрыты). Phase 3.5 (Selection System, Screen-Space Drag-Box & RTS Command Issuing — ADR-012, OD-24) — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`cfa24c2`: `UnitPickMath`, `UnitSelectionController`, `UnitCommandIssuing`, `UnitSelectionDriver`, `SelectionMarqueePresenter`, `UnitSelectionAndCommandTests` — 59 стартовых кейсов подняли EditMode-базу до 829; двойной независимый adversarial-аудит; 34-failure bite-пасс и bite-верифицированная ремедиация P1-1..P1-3, P2-4..P2-8, P3-9..P3-16 довели `UnitSelectionAndCommandTests` до 97 кейсов). Phase 3.6 (Minimap & Tactical HUD — ADR-012, OD-24 / OD-27 / OD-28) — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`2a754db`: `MinimapProjection`, `MinimapRadarModel`, `MinimapRadarGraphic`, `MinimapInteractionController`, `TacticalHudPointerBlocker`, `SelectionHudState`, `PermanentHudPresenter`, `SelectionHudPresenter`; 1010 стартовых EditMode-тестов; тройной независимый adversarial-аудит (Space Bunny + GLM 5.3 Flash + Pixel Canary); 31-failure bite-пасс и ремедиация P1-1..P1-3, P2-1..P2-6 довели `MinimapAndTacticalHudTests` до 176 кейсов, включая доказательства Zero-GC steady state). 1043/1043 EditMode тестов green, 6/6 PlayMode тестов green (1049 автоматизированных тестов). Технический фундамент Фаз 1.0–2.8 ПРИНЯТ: 0 P0, 0 P1. Остаточный бэклог P2 (dedicated server handshake) зафиксирован для будущих фаз. Phase 3.7 (Vertical Slice Integration, 400-Unit Benchmark, Multi-Frame PlayMode Suite & Architecture Invariants Gate — ADR-012, OD-23..OD-29) — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`3414ba6`: `TacticalVerticalSliceRunner`, `LocalReplicationLoopback`, `CorpseReapSnapshotSource` (GraceCaptures = 2, `ReapedSentinel` single-count) в `GlobalFront.Client.Presentation`; сквозная обвязка 400 юнитов `LocalMatchHost` (20 Гц) → `ServerReplicationEmitter` / `ClientTransportReplicationBridge` / `ClientReplicationReceiver` / `ClientReplicationWorld` → `UnitViewTickBuffer` → `UnitViewPool` / `UnitViewBinder` → `UnitOverlayBatcher` + `DrawOverlaysDirect` → `UnitSelectionController` / `UnitSelectionDriver` / `SelectionMarqueePresenter` → `MinimapRadarModel` / `MinimapInteractionController` / `PermanentHudPresenter` / `SelectionHudPresenter`, с заимствованием и восстановлением камеры/ввода в `Shutdown()` и поклеточным шагом строевых блоков (`BatchDestination`); `GlobalFrontRuntimeBootstrap.ActivateTacticalVerticalSlice` + `PrototypeHud` (F9 и GUI-кнопка) для живого переключения 40-юнитного прототипа на 400-юнитный срез; перегрузка `LocalMatchHost.AttachReplication` под пользовательский `IServerSnapshotSource`; протокольные фиксы `ComputeStateChecksum` в эмиттере и клиентском мире (маскирование очищенных координат `MoveTarget` → `(0, 0)`) и `ServerSnapshotDiffEngine.ComputeDirtyMask` (эмит `UnitDirtyMask.MoveTarget`, когда `current.HasMoveTarget && (!previous.HasMoveTarget || previous.MoveTarget != current.MoveTarget)`); 30 кейсов `TacticalVerticalSliceBenchmarkTests`, 7 мультикадровых `[UnityTest]` в `TacticalVerticalSlicePlayModeTests` и 12 гейтов `ArchitectureInvariantsTests`; тройной независимый adversarial-аудит (Space Bunny + GLM 5.3 Flash + Pixel Canary) и верификация ремедиации → 1086/1086 EditMode тестов green, 13/13 PlayMode тестов green (1099 автоматизированных тестов). **ИТОГ: Phase 3 (Шаги 3.1–3.7) — [100% COMPLETED / AUDITED: ZERO DEFECTS]**. Профильный замер OD-28 на `3414ba6`: **0.623 ms/frame** среднее за 120 презентационных кадров с включённым авторитетным тиком симуляции (далеко ниже 8.0 ms EditMode-гарда и потолка 16.67 ms для 60 FPS) и **0 B GC.Alloc** за 60 steady-state презентационных кадров и 60 тиков репликационного loopback. Следующая фаза — **Phase 4 (Multiplayer Gameplay Integration)**, соединяющая сетевой фундамент Phase 2 с принятым RTS-геймплеем Phase 3.

## Last Commit

`3414ba6` (`feat(client): implement Step 3.7 tactical vertical slice, 400-unit benchmark and architecture gates (1086 EditMode + 13 PlayMode green)`)

## Tests

- EditMode: **1086/1086 passed** (100% green, 0 failed, 0 skipped, 8.76 s, 2026-09-28; gate: `Artifacts/TestResults/EditMode-step37.xml`; 1099 автоматизированных тестов вместе с PlayMode)
- PlayMode: **13/13 passed** (100% green, 0 failed, 0 skipped, 17.08 s, 2026-09-28; gate: `Artifacts/TestResults/PlayMode-step37.xml`)
- Компиляция: 0 warnings, 0 errors
- OD-28 (400 юнитов): **0.623 ms/frame** среднее за 120 презентационных кадров (гард 8.0 ms, потолок 60 FPS 16.67 ms); **0 B GC.Alloc** за 60 steady-state кадров и 60 loopback-тиков
- Unity: `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0`

## Completed Milestones
- M0 Technical Foundation — COMPLETE
- Phase 1 Simulation Foundation — COMPLETE
- Phase 2.1 Server-Owned Match State — COMPLETE (`8178110`)
- Phase 2.2 Command Channel — COMPLETE (`313e9ef`)
- Phase 2.3 Server Tick Driver — COMPLETE (`ea0435d`, ADR-007)
- Phase 2.4 Session / Player Identity — COMPLETE (`73d1276`, ADR-008)
- Phase 2.5 Network Transport — COMPLETE (ADR-009; LiteNetLib 1.3.5 за контрактом `INetworkCarrier`; финальный независимый review = APPROVE, P0=0/P1=0/P2=0)
- Phase 2.6 Snapshot Networking — APPROVED (Independent adversarial audit passed with zero P0/P1 blockers; ADR-010; 599/599 EditMode passed, 6/6 PlayMode passed; Zero-GC hot path эмиттера, моста, клиента и транспорта валидирован)
  - Step 2.6.1 Core Delta Wire Codec — COMPLETE (355/355 EditMode passed; Zero-GC hot path валидирован)
  - Step 2.6.2 Server Replication Engine & History Ring — COMPLETE (429/429 EditMode passed; 74 новых теста репликации; Zero-GC hot path валидирован)
  - Step 2.6.3 Client Replication Receiver & FSM — COMPLETE (`c49eeda`; 558/558 EditMode passed; 129 новых тестов; Zero-GC hot path клиента валидирован)
  - Step 2.6.4 Full Integration & Impairment Tests — COMPLETE (599/599 EditMode passed; 41 интеграционный тест; Zero-GC hot path эмиттера и моста валидирован)
- Phase 2.7 Reconnect & Resync — COMPLETE (ADR-008..ADR-010; 641/641 EditMode passed, 6/6 PlayMode passed)
  - Step 2.7.1 Core Wire Codecs & Model — COMPLETE (605/605 EditMode passed)
  - Step 2.7.2 Server Session Re-attachment & Defect Fixes D1-D6 — COMPLETE (621/621 EditMode passed)
  - Step 2.7.3 Client Reconnect Coordinator & FSM — COMPLETE (637/637 EditMode passed)
  - Step 2.7.4 End-to-End Integration, Tactical Pause & Stress Tests — COMPLETE (641/641 EditMode passed, 6/6 PlayMode passed)
- Phase 2.8 Network Prototype Playtest (2v2) — COMPLETE (Steps 2.8.1–2.8.3; 2v2 topology, command ownership, HUD + tactical pause overlay, abandonment unpause P2-1/P2-2)
- Grand Adversarial Audit Remediation — COMPLETE (`c3cce4d`: P0 pause deadlock, Zero-GC tick 11.5 МБ/с, RateLimiter, anti-hijack, D8 full checksum; `a37f53d`: F-01 zero-gc snapshot targets, F-02 reentrancy safety)
- Grand Adversarial Audit (Phases 1.0 — 2.8) — [APPROVED: ZERO DEFECTS] (`872d0b2`; 661/661 EditMode, 6/6 PlayMode; `Artifacts/GrandAudit-Certification-a37f53d.md`)
- Phase 3.1 RTS Camera & Input — COMPLETE (`59ef38a`, 665 тестов); ремедиация аудита (`8144541`, 723 теста; `2b71414`, 725 тестов) — [APPROVED: ZERO DEFECTS]
- Phase 3.2 UnitViewTickBuffer & Interpolation — COMPLETE (`6ab12ad`, 673 теста); ремедиация аудита (`eac0e1d`, 735 тестов) — [APPROVED: ZERO DEFECTS]
- OD-29 UnitKind Replication & UnitCatalog — COMPLETE (`be03002`, `212740a`, 687 тестов); P3 F-1/F-2 закрыты в `27a10f6` — [APPROVED: ZERO DEFECTS]
- Phase 3.3 UnitViewBinder & Object Pooling — COMPLETE (`bcb1e78`, 711 тестов green, Zero-GC доказан, ADR-012/OD-26); ремедиация аудита (`0243971`, 719 тестов; `e69bed1` — телеметрия `DestroyedViewCount` и лог-бюджет) — [APPROVED: ZERO DEFECTS]
- Phase 3.4 Instanced Selection Rings & HP Bars — COMPLETE (`27a10f6`, 763 теста green, 0 B GC, ADR-012/OD-25); ремедиация двойного независимого adversarial-аудита (`b897eea`, 770 тестов; P0-1, P1-1, P1-2, P2-1, P2-4, P2-5, P3-1, P3-2, P3-3) — **[APPROVED: ZERO DEFECTS]**
- Phase 3.5 Selection System, Screen-Space Drag-Box & RTS Command Issuing — COMPLETE (`cfa24c2`, ADR-012/OD-24: `UnitPickMath` — zero-physics ray-to-ground и сквозной `double`-конверт метры → `WorldPointMm`; `UnitSelectionController` — каноническое восходящее множество `EntityId`, одиночный клик, Shift-toggle, двойной клик по `UnitKind` на экране, screen-space маркер, гейт `IsStillSelectable` на всех входах; `UnitCommandIssuing` — `IUnitCommandSink` → `ICommandChannel`; `UnitSelectionDriver` — покадровая обвязка; `SelectionMarqueePresenter` — изолированный transient-канвас, 0 B на кадр); стартовая реализация 829 тестов, двойной независимый adversarial-аудит, 34-failure bite-пасс и ремедиация P1-1..P1-3 / P2-4..P2-8 / P3-9..P3-16 подняли `UnitSelectionAndCommandTests` до 97 кейсов → **867/867 EditMode + 6/6 PlayMode** (873 всего)) — **[APPROVED: ZERO DEFECTS]**
- Phase 3.6 Minimap & Tactical HUD — COMPLETE (`2a754db`, ADR-012/OD-24/OD-27/OD-28: `MinimapProjection` — чистая статическая математика миникарты, мир в миллиметрах `[-200_000..+200_000]` ⇄ нормализованный UV `[0..1]` ⇄ пиксели виджета, сатурирующий кламп `MetresToMillimetres` в `[int.MinValue..int.MaxValue]` с `NaN -> 0`, `TryCameraGroundRect` как footprint луча камеры на $Y=0$ (OD-27), процедурно запечённая RGBA32-текстура террейна 64 × 64; `MinimapRadarModel` — 30-Гц rate-gated снапшот радара поверх `ClientReplicationWorld` с преаллоцированным буфером `MinimapBlip[]`; `MinimapRadarGraphic` — один батченый UI draw call на все точки + рамку камеры; `MinimapInteractionController` — ЛКМ-панорама камеры через `RtsCameraController.SnapToWorldXZ` и ПКМ-приказ движения через `IUnitCommandSink`; `TacticalHudPointerBlocker` — таблица из 8 экранных прямоугольников, перекрывающих raycast выбора; `SelectionHudState`; `PermanentHudPresenter` (канвас 1, `SortingOrder = 100`) и `SelectionHudPresenter` (канвас 2, `SortingOrder = 200`); `UnitSelectionDriver` синхронизирован с миникартой и гасит drag/hover при входе указателя в HUD); стартовая реализация 1010 тестов, тройной независимый adversarial-аудит, 31-failure bite-пасс и ремедиация P1-1..P1-3 / P2-1..P2-6 подняли `MinimapAndTacticalHudTests` до 176 кейсов → **1043/1043 EditMode + 6/6 PlayMode** (1049 всего) — **[APPROVED: ZERO DEFECTS]**
- **Phase 3 (Visual Presentation & RTS Controls) — [100% COMPLETED / AUDITED: ZERO DEFECTS]** (Шаги 3.1–3.7)
- Phase 3.7 Vertical Slice Integration, 400-Unit Benchmark, Multi-Frame PlayMode Suite & Architecture Invariants Gate — COMPLETE (`3414ba6`, ADR-012/OD-23..OD-29: `TacticalVerticalSliceRunner` — единственный владелец кадра, шагающий тик симуляции и презентацию в фиксированном порядке; `LocalReplicationLoopback` замыкает репликацию в процессе без сетевых syscalls, поэтому замер OD-28 отражает стоимость пайплайна, а не UDP; `CorpseReapSnapshotSource` с `GraceCaptures = 2` и `ReapedSentinel` снимает труп из мира ровно один раз; `GlobalFrontRuntimeBootstrap.ActivateTacticalVerticalSlice` + `PrototypeHud` (F9) переключают живой сценарий с 40-юнитного прототипа на 400-юнитный срез; перегрузка `LocalMatchHost.AttachReplication` под пользовательский `IServerSnapshotSource`; протокольные фиксы маскирования очищенного `MoveTarget` в `ComputeStateChecksum` и эмита `UnitDirtyMask.MoveTarget` в `ComputeDirtyMask`); 30 кейсов `TacticalVerticalSliceBenchmarkTests` (bootstrap на 400 юнитов, замкнутые циклы выборки/движения/атаки/миникарты, батчинг 250-юнитной сетки, attrition и сбор трупов, lifecycle материалов при `createVisuals: true`, восстановление заимствованной камеры/ввода, Zero-GC-рекордеры, тайминг OD-28 на 120 кадрах), 7 мультикадровых `[UnityTest]` в `TacticalVerticalSlicePlayModeTests`, 12 гейтов `ArchitectureInvariantsTests`; тройной независимый adversarial-аудит (Space Bunny + GLM 5.3 Flash + Pixel Canary) и верификация ремедиации → **1086/1086 EditMode + 13/13 PlayMode** (1099 всего, 0 warnings, 0 errors) — **[APPROVED: ZERO DEFECTS]**

## Next Step

Phase 4 (Multiplayer Gameplay Integration) — **[READY / NEXT]**: соединить принятый сетевой фундамент Phase 2 (`ICommandChannel`, snapshot networking, reconnect/resync, `SessionManager`) с принятым RTS-геймплеем и презентационным стеком Phase 3, чтобы vertical slice из 400 юнитов стал сетевым игровым опытом.

## Backlog

- **[P2 Informational]**: при реализации Fog of War (OD-16 / `IReplicationFilter`) в будущих фазах `StateChecksum` должен вычисляться per-client (для отфильтрованного среза мира конкретного клиента), а не глобально по всему серверному миру.

## Important Constraints

- Current runtime — local prototype; `LocalMatchHost` (ServerHost) работает в клиентском процессе и владеет `MatchServer`, `TickDriver` (engine-independent, в `GlobalFront.Server`) и `SessionManager` (Phase 2.4, ADR-008).
- Server tick lifecycle отделён от Unity client lifecycle (ADR-007). Session/player identity реализованы как server-authoritative layer (ADR-008): PlayerId назначается только сервером, command ingress проходит session gate; конкретная grace duration — TBD. Транспортная архитектура определена ADR-009 (OD-1 = LiteNetLib за контрактом `INetworkCarrier`); транспорт реализован и проверен (детерминированный `VirtualNetworkPipe` + real-UDP LiteNetLib loopback) и не владеет simulation: `MatchServer`/`TickDriver`/`SessionManager`/`CommandHeader`/Snapshot Protocol v1 не изменены. Отдельного dedicated server process пока нет; тот же driver и simulation core будут использованы future dedicated host. Reconnect/resync реализованы и приняты (Phase 2.7, OD-18…OD-22).
- Snapshot Protocol v1 реализован и передаётся по сети как opaque payload (C2, unreliable sequenced, latest-wins по `SnapshotTick`). Шаги 2.6.1–2.6.4 реализовали и верифицировали Core `DeltaSnapshotWireCodec`, Server Replication Engine, History Ring, клиентский приёмник (`ClientReplicationReceiver`, `ClientReplicationWorld`, `ReplicationReceiverFSM`, `ReplicationFeedbackGenerator` в `GlobalFront.Client.Replication`) и сквозную связку с транспортом в `LocalMatchHost` / `ClientTransportReplicationBridge`.
- Зафиксированные отклонения/уточнения Шага 2.6.3/2.6.4 (архитектуру не меняют):
  - `SnapshotAck` = **34 байта**: offsets 0/1/2/10/18/26 в сумме дают 34; uplink-бюджет — 340 Б/с при 10 Hz. Арифметическая опечатка исправлена в R&D §4.2–4.3.
  - `KeyframeRef` передаётся на проводе (байты 34..35 заголовка) и валидируется на клиенте (P1-1).
  - Возраст keyframe-базы (`Now − BaseKeyframeTick > 120`) не используется как триггер REBASING: при плановом интервале keyframe 15–20 с (OD-11) это давало бы ложный ребейз каждые 6 с. Нормативный триггер — `BaseTick − LastAppliedTick > 120` или несовпадение `KeyframeRef`; правило синхронизировано в R&D §5.1 и связанных сводках.
  - Лимит повторов `DeltaResume` в ADR-010: реализован defensive liveness guard — бюджет 3 попыток, после чего CATCHING_UP эскалирует в REBASING.
  - Клиентская верификация `StateChecksum` (32-bit FNV-1a): при двух последовательных несовпадениях FSM переходит в REBASING и запрашивает `SnapshotRequest` (P1-2).
  - Multipart-сборка тика (`PartCount > 1`) и keyframe slicing реализованы и верифицированы в Шаге 2.6.4.
- Product 1.0 включает пять основных фракций; их gameplay/content ещё не реализован.
- Подфракции не входят в 1.0.
- Не считать будущий roadmap фактом реализации.

## Read Next

- [Phase 02](Phases/Phase_02_Multiplayer.md)
- [Architecture](ARCHITECTURE.md)
- [Project Status](PROJECT_STATUS.md)

