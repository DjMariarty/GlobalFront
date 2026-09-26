# Статус проекта GlobalFront

> Фактический технический статус • обновлено 2026-09-26

## Summary

GlobalFront: Phases 1.0 — 2.8 — **[100% COMPLETED / AUDITED]** (Grand Adversarial Audit **[APPROVED: ZERO DEFECTS]** на `a37f53d`; отчёт: `Artifacts/GrandAudit-Certification-a37f53d.md`). Проверка текущего состояния: **763/763 EditMode passed (100%)** и **6/6 PlayMode passed (100%)** (0 failed, 0 skipped; gate: `Artifacts/TestResults/EditMode-step34.xml`, 2026-09-26). Текущая задача / Next Phase — **Phase 3 (Visual Presentation & RTS Controls) — [CURRENT / IN PROGRESS]**: Шаг 3.1 RTS Camera & Input — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`59ef38a` → `8144541`, `2b71414`); Шаг 3.2 UnitViewTickBuffer & Interpolation — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`6ab12ad` → `eac0e1d`); OD-29 UnitKind Replication & UnitCatalog — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`be03002`, P3 F-1/F-2 закрыты в `27a10f6`); Шаг 3.3 UnitViewBinder & Object Pooling — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`bcb1e78` → `0243971`, `e69bed1`); Шаг 3.4 Instanced Selection Rings & HP Bars — **[IMPLEMENTED / 763 TESTS GREEN]** (`27a10f6`, ADR-012/OD-25); следующий — **Шаг 3.5: Selection System, Screen-Space Drag-Box & RTS Command Issuing** (ADR-012, OD-24).
 
## Confirmed Baseline
 
| Область | Фактическое состояние |
|---|---|
| Unity | `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0` |
| Core | детерминированный, без Unity API, 20 Hz; opaque `SessionId`/`MatchId`; `DeltaSnapshotWireCodec`, `SnapshotAckCodec` (34 B), `KeyframeSliceCodec`, `ReplicationRequestCodec`, `ReconnectWireCodec` (64 B request / 24 B response) |
| Server | `MatchServer`, `TickDriver`, `SessionManager` (re-bind token, 32 B cryptographically secure secret, grace timeout); `Server.Transport` по ADR-009; `ServerReplicationEmitter` интегрирован с тиками, history ring (120 тиков), pacing-слайсингом keyframe и тактической паузой |
| Transport | `INetworkCarrier` + `OwnDatagramCarrier` (детерминированные тесты через `VirtualNetworkPipe`) и `LiteNetLibCarrier` (LiteNetLib 1.3.5, real UDP); `NetworkCommandChannel` реализует `ICommandChannel` с асинхронным авторитетным `CommandAckPayload`; каналы C0/C1/C2 |
| Client replication | `ClientReplicationReceiver`, `ClientReplicationWorld`, `ReplicationReceiverFSM`, `ClientTransportReplicationBridge`, `ClientReconnectCoordinator`; накат дельт, сборка keyframe-слайсов, 10 Hz `SnapshotAck` по C0, Zero-GC верификация StateChecksum, авто-восстановление и координация реконнекта |
| Local integration | `LocalMatchHost` (ServerHost) внутри Client process: владеет `MatchServer`, `TickDriver` и `SessionManager`; тактическая пауза (OD-18..OD-22), окно ожидания 200с / 4000 тиков (OD-18) и 5-секундный countdown таймер (OD-20); сквозная интеграция верифицирована в EditMode |
| Commands | Move, Attack и Stop; `ICommandChannel` + session-attributed `LocalCommandChannel`; ingress проходит session gate; сохранение команд во время реконнекта |
| Snapshots | Delta Snapshot Core/Server/Client слои Шагов 2.6.1–2.6.4 и Reconnect 2.7.1–2.7.4 завершены, rate-pacing/keyframe slicing/end-to-end integration и loss stress (1–5% loss) верифицированы; независимый аудит Phase 2.6 = APPROVE (zero P0/P1 blockers) |
| Presentation | RTS camera (`RtsCameraController`) и input layer (`RtsInputManager`) — Шаг 3.1; `UnitViewTickBuffer` (SoA, 37 B/slot, safe alpha, adaptive 10/5Hz delay) — Шаг 3.2 (ADR-012, OD-23); `UnitView`, `UnitViewPool`, `UnitViewBinder` (O(1)-биндинг слотов репликации к представлениям, Zero-GC, без `Instantiate`/`Destroy` в бою) — Шаг 3.3 (OD-26); `UnitOverlayBatcher` + `UnitOverlayGeometry` + `UnitOverlayRenderPass`/`UnitOverlayRendererFeature` + `UnitOverlay.shader` (инстансированные кольца выделения и HP-бары, 0 B GC, до 250 инстансов на draw) — Шаг 3.4 (OD-25); выбор, движение, атака, HUD, runtime prototype world |
| Scene | одна включённая `Assets/Scenes/SampleScene.unity` |
| Tests | **763/763 EditMode passed (100%)**, 0 failed, 0 skipped (661 Grand Audit baseline `a37f53d` + 4 `RtsCameraTests` + 8 `UnitViewTickBufferTests` + 14 OD-29/`UnitCatalogTests` + Step 3.3 `UnitViewBinderTests` + 25 `UnitOverlayTests` Step 3.4 + OD-29 P3 F-1/F-2; gate: `Artifacts/TestResults/EditMode-step34.xml`, 2026-09-26); **6/6 PlayMode passed (100%)**, 0 failed, 0 skipped (2026-09-26; prior gates: `Artifacts/TestResults/EditMode-reviewgate.xml`, `Artifacts/TestResults/PlayMode-reviewgate.xml`, 2026-09-20) |

## Phase 1.0 – 2.8 Status — [100% COMPLETED / AUDITED]

Технический фундамент Фаз 1.0–2.8 ПРИНЯТ: 0 P0, 0 P1. Остаточный бэклог P2 (dedicated server handshake) зафиксирован для будущих фаз.

- Phase 1.x (RTS Foundation / Prototype) — **[100% COMPLETED / AUDITED]** (включена в Grand Audit `a37f53d`, [APPROVED: ZERO DEFECTS]).

## Phase 2 Status — [100% COMPLETED / AUDITED]
 
- 2.1 Server-Owned Match State — [100% COMPLETED / AUDITED]
- 2.2 Command Channel — [100% COMPLETED / AUDITED]
- 2.3 Server Tick Driver — [100% COMPLETED / AUDITED], `ea0435d` (ADR-007)
- 2.4 Session / Player Identity — [100% COMPLETED / AUDITED], `73d1276` (ADR-008); lifecycle завершается на `MatchPhase.Finished`, `Closed` зарезервирован для будущего server lifecycle/teardown
- 2.5 Network Transport — [100% COMPLETED / AUDITED], `feat: implement network transport (Phase 2.5)` (ADR-009, OD-1 = LiteNetLib 1.3.5 за контрактом `INetworkCarrier`; финальный независимый review = APPROVE, P0=0/P1=0/P2=0)
- 2.6 Snapshot Networking — [100% COMPLETED / AUDITED] (Independent adversarial audit passed with zero P0/P1 blockers; ADR-010; Шаги 2.6.1–2.6.4 complete; 599/599 EditMode, 6/6 PlayMode)
- 2.7 Reconnect / Resync — [100% COMPLETED / AUDITED] (Steps 2.7.1–2.7.4 complete; 641/641 EditMode, 6/6 PlayMode; Zero-GC, tactical pause OD-18..OD-22, 5s countdown OD-20; Review Gate APPROVED)
- 2.8 Network Prototype Playtest (2v2) — [100% COMPLETED / AUDITED] (Steps 2.8.1–2.8.3 complete; 655/655 EditMode baseline → 661/661 на `a37f53d` после F-01/F-02; 6/6 PlayMode; 2v2 topology, command ownership, HUD + tactical pause overlay, abandonment unpause P2-1/P2-2)

## Phase 3 (Visual Presentation & RTS Controls) — [CURRENT / IN PROGRESS]

- 3.1 RTS Camera & Input — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (commit `59ef38a`: `RtsCameraController`, `RtsInputManager`, `RtsCameraTests`, wiring в `GlobalFrontRuntimeBootstrap`; **665/665 EditMode passed**). Ретроспективный adversarial-аудит (DeepSeek v4.1 Flash + GLM 5.3 Flash) и ремедиация P1-1, P2-1..P2-5 (NaN-safe сериализованные диапазоны через `FiniteOr`) → `8144541` (**723 теста**); остатки R-1..R-4 (кламп mock zoom input, защита от деления на ноль в unproject, Zero-GC active input) → `2b71414` (**725 тестов**). Итог аудита: **[APPROVED: ZERO DEFECTS]**.
- 3.2 UnitViewTickBuffer & Interpolation — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (**673 EditMode green, 0 GC allocs, SoA 37 B/slot, safe alpha, adaptive 10/5Hz delay**; ADR-012, OD-23: кольцевой буфер 32 слота, clamped-экстраполяция, snap при `newerTick == olderTick`, `Flush()`/`Resync()`; gate: `Artifacts/TestResults/EditMode-phase32-A.xml`). Ретроспективный аудит (DeepSeek v4.1 Flash + GLM 5.3 Flash) и двухэтапная ремедиация: P1-1 (slew clock freeze, `MaxAdvanceTicksPerCall` clamp), P1-2/F-1 (снятие drift-snap), P2-1/F-2 (`IsFinite`-guards в `SlewDegrees`/`ShortestArcDegrees`), P2-2 (`DriftCorrectionPerSecond = 0.25`), P2-3/F-5 (`_slotHealthOverridden`), N-1/F-1 (сброс override при смерти юнита, сохранение при `Flush` на тактической паузе), F-4 (`MaxIntervalSampleTicks = 8.0`), F-2 (`DriftSnapTicks = MaxRenderDelayTicks + MaxExtrapolationTicks + 2.0`) → `eac0e1d` (**735 тестов**). Итог аудита: **[APPROVED: ZERO DEFECTS]**.
- OD-29 UnitKind Replication & UnitCatalog — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (commit `be03002`: поле `byte UnitKind` в `DeltaAddRecord` с бампом `DeltaProtocolVersion` → protocol v2, клиентский справочник `UnitCatalog`; 40-байтовый `DeltaAddRecord` v2, паритет `KeyframeSliceCodec` v2 и FNV-1a `StateChecksum`, O(1) Zero-GC `UnitCatalog`; **687/687 EditMode green, 6/6 PlayMode**, gate: `Artifacts/TestResults/EditMode-od29-unitkind.xml`). Ретроспективный аудит (DeepSeek v4.1 Flash / Gemini 3.8 Flash + GLM 5.3 Flash) — **[APPROVE: ZERO DEFECTS]**; две P3-заметки закрыты в `27a10f6`: F-1 — защита от переполнения `targetDelta > long.MaxValue - id` в `DeltaSnapshotWireCodec.ReadUpdates`; F-2 — валидация `IsResolved` и `DisplayName != null` в конструкторе `UnitCatalog`.
- 3.3 UnitViewBinder & Object Pooling — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (commit `bcb1e78`: `UnitView`, `UnitViewPool` (преаллокация, без `Instantiate`/`Destroy` в бою, OD-26), `UnitViewBinder` (O(1)-биндинг, Zero-GC); **711/711 EditMode green**). Ретроспективный аудит (DeepSeek v4.1 Flash + Qwen 3.8 Max) и ремедиация P1-1..P1-3, P2-1..P2-7 → `0243971` (**719 тестов**); телеметрия `DestroyedViewCount` и лог-бюджет → `e69bed1`. Итог аудита: **[APPROVED: ZERO DEFECTS]**.
- 3.4 Instanced Selection Rings & HP Bars — **[IMPLEMENTED / 763 TESTS GREEN]** (commit `27a10f6`; ADR-012, OD-25: `UnitOverlayBatcher` — предвыделенные плоские массивы `DefaultCapacity = 512` / `MaxCapacity = 4096` / `MaxInstancesPerDraw = 250`, детерминированный `BuildBatches(UnitViewBinder, Quaternion)` с **0 B GC Alloc**, защита от `NaN`/`Infinity` и вырожденных кватернионов; `UnitOverlayGeometry` — процедурные quad-меши `CreateGroundQuad` (XZ, кольца) и `CreateBillboardQuad` (XY, HP-бары) плюс материал с `enableInstancing = true`; `UnitOverlayRenderPass` + `UnitOverlayRendererFeature` — Unity 6 URP 17.6 RenderGraph (`RecordRenderGraph` + `RasterCommandBuffer.DrawMeshInstanced` на `RenderPassEvent.AfterRenderingOpaques`, чанкинг по 250, раздельные `MaterialPropertyBlock`); `UnitOverlay.shader` (`GlobalFront/Unit Overlay`) — двухпроходный instanced HLSL-шейдер `UnitOverlayRing` (антиалиасинг через `fwidth`) и `UnitOverlayHealthBar` (рамка + заливка по доле здоровья); расширены `UnitView` (`IsSelected`, `SetSelected`, `MaximumHealth`, `RadiusMillimetres` со строгим сбросом при `Bind`/`Release`) и `UnitViewBinder` (`SlotCount`, кэш `UnitDefinition` по `UnitKind`, гарантированный `SetSelected(false)` в `ReleaseSlot`); тесты `UnitOverlayTests.cs` (25) + F-1/F-2 → **763/763 EditMode passed**, **6/6 PlayMode passed**; gate: `Artifacts/TestResults/EditMode-step34.xml`).
- 3.5 Selection System, Screen-Space Drag-Box & RTS Command Issuing — **[READY / NEXT]** (ADR-012, OD-24).

Архитектурные решения Фазы 3 (OD-23 — OD-29) зафиксированы в [ADR-012](DECISIONS.md).

## Product Scope vs Implementation

Утверждённый Product 1.0 включает пять основных фракций, полный Generals-style RTS foundation, multiplayer до 10 игроков, AI, reconnect/resync, replay, desync detection, большие армии и production-quality presentation. Эти требования являются roadmap, а не текущими возможностями прототипа.

Сейчас нет production economy/build/production systems, пяти реализованных фракций, полного AI, карт 1v1–5v5, dedicated server, replay или scale proof 3000+. Reconnect/resync реализованы и приняты (Phase 2.7, OD-18…OD-22). Core/Server/Client слои delta snapshots реализованы и верифицированы в Шагах 2.6.1–2.6.4 (599/599 EditMode, 6/6 PlayMode); независимый аудит Phase 2.6 утверждён (zero P0/P1 blockers); scale proof 3000+ запланирован на последующие фазы.

## Backlog

- **[P2 Informational]**: при реализации Fog of War (OD-16 / `IReplicationFilter`) в будущих фазах `StateChecksum` должен вычисляться per-client (для отфильтрованного среза видимости конкретного клиента), а не глобально по всему миру симуляции сервера.

## Important Constraints

- Client пока напрямую ссылается на Server из-за local host.
- Server tick lifecycle engine-independent (ADR-007); тот же `TickDriver`/`MatchServer` core будет использован future dedicated host.
- Session/player identity реализованы server-authoritative (ADR-008): PlayerId назначается только сервером. Транспорт реализован по ADR-009 (OD-1 = LiteNetLib 1.3.5 за контрактом `INetworkCarrier`, precompiled DLL в `Assets/_GlobalFront/ThirdParty/LiteNetLib`): транспорт не владеет simulation и не меняет `MatchServer`/`TickDriver`/`SessionManager`/`CommandHeader`/Snapshot Protocol v1; timing constants (OD-8) применены как reference values (`TransportProtocol`).
- Snapshot Protocol v1 сохраняется как legacy/full payload. Delta Snapshot Core/Server/Client слои Шагов 2.6.1–2.6.4 реализованы и утверждены: сквозная связка с transport/tick-loop, rate-pacing, keyframe slicing и loss stress верифицированы.
- Pathfinding и детерминированное разрешение препятствий отсутствуют.
- Детали roster, abilities, stats, balance, generals и map layouts остаются TBD.

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Architecture](ARCHITECTURE.md)
- [Roadmap](ROADMAP.md)
- [Phase 02](Phases/Phase_02_Multiplayer.md)

