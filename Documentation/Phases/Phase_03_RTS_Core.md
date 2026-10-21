# Phase 03 — RTS Core + Vertical Slice

> Status: **COMPLETE [APPROVED: ZERO DEFECTS]** • Milestone M2 Playable RTS Core • Шаги 3.1–3.7 полностью завершены и сертифицированы независимым аудитом (отчёты: `3414ba6`, `cb01ea2`)

## Goal

Создать playable RTS core и high-performance representative vertical slice (400 активных юнитов, Zero-GC steady state, 60+ FPS).

## Dependencies

- Phase 01 complete (детерминированное ядро симуляции);
- Phase 02 complete (сетевой фундамент, репликация дельт, реконнект);
- ADR-012 (Client Presentation Stack & Tick Interpolation Architecture, OD-23..OD-29).

## Scope & Completed Steps

1. **3.1 RTS Camera & Input — COMPLETE [APPROVED: ZERO DEFECTS]** (`59ef38a` → `8144541`, `2b71414`): `RtsCameraController` (pan/orbit/zoom, клампы, NaN-safety через `FiniteOr`), `RtsInputManager` (`IsShiftPressed`, `StopRequested` на `X`/`H`, изоляция от `WASD`).
2. **3.2 UnitViewTickBuffer & Interpolation — COMPLETE [APPROVED: ZERO DEFECTS]** (`6ab12ad` → `eac0e1d`): кольцевой буфер 32 слота (SoA, 37 B/slot), adaptive render delay (10/5 Hz), safe alpha (zero division protection), clamped-экстраполяция к `MoveTarget`, `Flush()`/`Resync()` для тактической паузы (OD-23).
3. **OD-29 UnitKind Replication & UnitCatalog — COMPLETE [APPROVED: ZERO DEFECTS]** (`be03002`, `212740a`, `27a10f6`): protocol v2, 40-байтовый `DeltaAddRecord`, паритет `KeyframeSliceCodec` v2 и `StateChecksum`, O(1) Zero-GC справочник `UnitCatalog` (Scout, Tank, BaseStructure).
4. **3.3 UnitViewBinder & Object Pooling — COMPLETE [APPROVED: ZERO DEFECTS]** (`bcb1e78` → `0243971`, `e69bed1`): преаллоцированный `UnitViewPool` (без `Instantiate`/`Destroy` в бою, OD-26), `UnitViewBinder` (O(1) биндинг слотов репликации к представлениям, Zero-GC), статический меш + поворотная башня (`TurretYawDegrees`).
5. **3.4 Instanced Selection Rings & HP Bars — COMPLETE [APPROVED: ZERO DEFECTS]** (`27a10f6` → `b897eea`): процедурные quad-меши `UnitOverlayGeometry`, `UnitOverlayBatcher` (0 B GC), Unity 6 URP 17.6 RenderGraph pass (`UnitOverlayRenderPass` + `UnitOverlayRendererFeature`, чанкинг по 250 инстансов, изолированные `MaterialPropertyBlock`), двухпроходный instanced шейдер `GlobalFront/Unit Overlay` (OD-25).
6. **3.5 Selection System, Screen-Space Drag-Box & RTS Command Issuing — COMPLETE [APPROVED: ZERO DEFECTS]** (`cfa24c2`): zero-physics ray-to-ground по $Y=0$ (`UnitPickMath`), сквозной `double`-конверт в `WorldPointMm`, `UnitSelectionController` (каноническое восходящее множество `EntityId`, `IsStillSelectable`-гейты), `SelectionMarqueePresenter` (transient-канвас, 0 B на кадр), выдача команд через `IUnitCommandSink` → `ICommandChannel` (OD-24).
7. **3.6 Minimap & Tactical HUD — COMPLETE [APPROVED: ZERO DEFECTS]** (`2a754db`): `MinimapProjection` (чистая математика, запечённая RGBA32 64x64 текстура террейна без второй URP камеры), rate-gated 30-Гц `MinimapRadarModel`, батченый `MinimapRadarGraphic` (все точки + рамка камеры за 1 draw call), `MinimapInteractionController`, `TacticalHudPointerBlocker`, `PermanentHudPresenter` (канвас 1), `SelectionHudPresenter` (канвас 2) (OD-24, OD-27, OD-28).
8. **3.7 Vertical Slice Integration, 400-Unit Benchmark, Multi-Frame PlayMode Suite & Architecture Invariants Gate — COMPLETE [APPROVED: ZERO DEFECTS]** (`3414ba6`, `cb01ea2`):
   - `TacticalVerticalSliceRunner` — единственный владелец кадра;
   - `LocalReplicationLoopback` — замыкание дельт и фидбэка в процессе;
   - `CorpseReapSnapshotSource` — grace-окно трупов (`GraceCaptures = 2`);
   - Сквозная обвязка 400 юнитов всех трёх архетипов;
   - Протокольные фиксы маскирования `MoveTarget` в `ComputeStateChecksum` и `ComputeDirtyMask`;
   - 30 кейсов бенчмарка `TacticalVerticalSliceBenchmarkTests`, 7 мультикадровых `[UnityTest]`, 12 гейтов `ArchitectureInvariantsTests`;
   - **Профильный замер OD-28:** **0.623 ms/frame** среднее CPU-время симуляции и презентационного кадра за 120 кадров в headless EditMode (гард 8.0 ms, потолок 60 FPS 16.67 ms; не включает GPU draw-call, F-8) и **0 B GC.Alloc** за 60 steady-state кадров.
9. **Hardening Phase 3.7.1 — COMPLETE** (`934840d`..`80226f5`): закрыты замечания аудита F-1..F-15 и N-02..N-04:
   - инъекция transport/uplink швов в `TacticalVerticalSliceRunner` (F-11);
   - регистрация `UnitOverlayRendererFeature` в URP ассетах и передача отрисовки RenderGraph с защитой от double-draw (F-7);
   - паритет `StateChecksum` (F-14) и порядка сущностей (F-15);
   - Zero-GC пулинг в `FragmentAssembler` (N-02);
   - бейзлайн увеличен до **1179 EditMode + 16 PlayMode тестов** (100% green).

## Milestones

- M2 Playable RTS Core — **COMPLETE**;
- Профильный бенчмарк OD-28 зафиксирован и сдан.

## Acceptance Criteria

- утверждённый presentation & RTS control стек образует цельный loop (выполнено);
- core systems работают через authoritative simulation boundary (выполнено);
- benchmarks существуют и воспроизводимы на 400 юнитах (выполнено);
- Zero-GC steady state доказан (выполнено).

## Definition of Done

**ВЫПОЛНЕН ПОЛНОСТЬЮ.** Шаги 3.1–3.7 реализованы, протестированы и сертифицированы с вердиктом **`[APPROVED: ZERO DEFECTS]`**. Milestone **M2 Playable RTS Core** достигнут. Следующая фаза — **Phase 4: Multiplayer Gameplay Integration**.
