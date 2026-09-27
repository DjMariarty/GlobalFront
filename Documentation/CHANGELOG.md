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

### Technical Milestones

- Phase 2.1 Server-Owned Match State — `8178110`.
- Phase 2.2 Command Channel — `313e9ef`.
- `ICommandChannel` и `LocalCommandChannel` отделяют client command queue от конкретного local host.
- `StopCommand` включён в command channel baseline.
- Phase 2.3 Server Tick Driver — реализован и принят (ADR-007), commit `ea0435d`: engine-independent `TickDriver` в `GlobalFront.Server` (20 Hz, bounded catch-up, manual/real-time); `LocalMatchHost` (ServerHost) владеет `MatchServer` и `TickDriver`; `FixedSimulationRunner` удалён; server tick lifecycle отделён от Unity client lifecycle.
- Phase 2.4 Session / Player Identity — реализована и принята (ADR-008), commit `73d1276`: opaque `SessionId`/`MatchId` в Core; `SessionManager` в `GlobalFront.Server` — единственный источник `PlayerId` (монотонное назначение по порядку join, без повторного использования); session gate перед неизменным `MatchServer`; `LocalCommandChannel` session-attributed; клиент получает `PlayerId` от сервера (`ClientSession`); match lifecycle завершается на `MatchPhase.Finished` — `Closed` зарезервирован для будущего server lifecycle/teardown.
- Phase 2.5 Network Transport — COMPLETE, `feat: implement network transport (Phase 2.5)`, по ADR-009 (OD-1 = LiteNetLib); финальный независимый review = **APPROVE** (P0=0/P1=0/P2=0): контракт `INetworkCarrier`; собственный детерминированный `OwnDatagramCarrier` (конверт `CarrierVersion`, единое reliable-пространство C0/C1, separate sequenced C2 без ACK, serial arithmetic, receive/reorder windows, cumulative+selective ACK, EWMA RTO, keepalive/idle на `ITransportClock`, per-peer rate limiting, bounded reassembly: ≤2 MB/peer, ≤4 группы, ≤2048 фрагментов, lifetime 2000 ms) поверх `VirtualNetworkPipe` (seeded loss/dup/reorder/latency/corruption/partition); `LiteNetLibCarrier` — LiteNetLib 1.3.5 (precompiled netstandard2.0 DLL в `Assets/_GlobalFront/ThirdParty/LiteNetLib`, интеграция через `precompiledReferences`) с worker-потоком, bounded event queue, app-level C2 fragmentation по MTU пира и carrier-level admission control (bounded `MaxConnections`, per-peer admission reservations); `ServerTransportHost`/`ClientTransportEndpoint` — host-thread orchestration и атрибуция токен → `ConnectionHandle` → `SessionId` → session gate; `CommandWireCodec` (versioned, little-endian, additive, `CommandHeader` не изменён); `NetworkCommandChannel` реализует `ICommandChannel`: `TrySubmit*` — только local pre-flight, авторитетный результат асинхронно через `CommandResultReceived` (`CommandAckPayload` с `SessionRejection` и `MatchCommandRejection`); Snapshot Protocol v1 передаётся как opaque C2 payload (latest-wins по `SnapshotTick`); retransmission semantics: per-packet `MaxRetransmits`, per-item экспоненциальный backoff, Karn RTT sampling, reorder-span throttle против амплификации; полный жизненный цикл соединения, обработка malformed-пакетов, протокольные версии разделены (Carrier/Message/SnapshotProtocol); large C2 (~117 КБ, 3000+ сущностей) доставляется byte-identical через оба носителя.

- Phase 2.6 Snapshot Networking — COMPLETE (ADR-010; delta compression, history ring, keyframe slicing).
- Phase 2.7 Reconnect & Resync — COMPLETE (session reattachment, full state resync, tactical pause sync).
- Phase 2.8 Network Prototype Playtest (2v2) — COMPLETE (2v2 topology, abandonment unpause, HUD overlay).
- Grand Adversarial Audit (Phases 1.0 — 2.8) — [APPROVED: ZERO DEFECTS] на `a37f53d` (661/661 EditMode, 6/6 PlayMode).
- Phase 3.1 RTS Camera & Input — COMPLETE (`59ef38a`, 665 тестов). Ретроспективный adversarial-аудит (DeepSeek v4.1 Flash + GLM 5.3 Flash) → **[APPROVED: ZERO DEFECTS]**; закрыты P1-1 и P2-1..P2-5 (NaN-safe сериализованные диапазоны через `FiniteOr`, защита от невалидных значений камеры) в `8144541` (**723 теста**), затем остатки R-1..R-4 (кламп mock zoom input, защита от деления на ноль в unproject, Zero-GC active input) в `2b71414` (**725 тестов**).
- Phase 3.2 UnitViewTickBuffer & Interpolation — COMPLETE (`6ab12ad`, 673 теста). Ретроспективный adversarial-аудит (DeepSeek v4.1 Flash + GLM 5.3 Flash) → **[APPROVED: ZERO DEFECTS]**; двухэтапная ремедиация в `eac0e1d` (**735 тестов**): P1-1 — slew clock freeze и `MaxAdvanceTicksPerCall` clamp; P1-2/F-1 — снятие drift-snap; P2-1/F-2 — `IsFinite`-guards в `SlewDegrees`/`ShortestArcDegrees`; P2-2 — `DriftCorrectionPerSecond = 0.25`; P2-3/F-5 — `_slotHealthOverridden`; N-1/F-1 — сброс override при смерти юнита и сохранение при тактической паузе через `Flush`; F-4 — `MaxIntervalSampleTicks = 8.0`; F-2 — `DriftSnapTicks = MaxRenderDelayTicks + MaxExtrapolationTicks + 2.0`.
- OD-29 UnitKind Replication & UnitCatalog — COMPLETE (`be03002`, `212740a`, 687 тестов). Ретроспективный аудит (DeepSeek v4.1 Flash / Gemini 3.8 Flash + GLM 5.3 Flash) → **[APPROVE: ZERO DEFECTS]** (40-байтовый `DeltaAddRecord` v2, паритет `KeyframeSliceCodec` v2 и FNV-1a `StateChecksum`, O(1) Zero-GC `UnitCatalog`). Две P3-заметки закрыты в `27a10f6`: F-1 — защита от переполнения `targetDelta > long.MaxValue - id` в `DeltaSnapshotWireCodec.ReadUpdates`; F-2 — валидация `IsResolved` и `DisplayName != null` в конструкторе `UnitCatalog`.
- Phase 3.3 UnitViewBinder & Object Pooling — COMPLETE (`bcb1e78`, 711 тестов): `UnitView`, `UnitViewPool` (преаллокация, без Instantiate/Destroy в бою, OD-26), `UnitViewBinder` (Zero-GC связывание слотов репликации с представлениями). Ретроспективный аудит (DeepSeek v4.1 Flash + Qwen 3.8 Max) → **[APPROVED: ZERO DEFECTS]**; ремедиация P1-1..P1-3 и P2-1..P2-7 в `0243971` (**719 тестов**), телеметрия `DestroyedViewCount` и лог-бюджет — в `e69bed1`.
- Phase 3.4 Instanced Selection Rings & HP Bars — **[APPROVED: ZERO DEFECTS]** (`27a10f6`, **763/763 EditMode passed**, 0 failed, 0 skipped; 6/6 PlayMode passed; ADR-012, OD-25). В `GlobalFront.Client.Presentation`:
  - `UnitOverlayBatcher` — предвыделенные плоские массивы (`DefaultCapacity = 512`, `MaxCapacity = 4096`, `MaxInstancesPerDraw = 250` под лимит константного буфера `UNITY_INSTANCED_ARRAY_SIZE`), детерминированный `BuildBatches(UnitViewBinder, Quaternion)` с **0 B GC Alloc**, правила видимости (кольца для выделенных живых юнитов; HP-бары для выделенных, раненых или при `AlwaysShowHealthBars = true`), защита от `NaN`/`Infinity` и вырожденных кватернионов.
  - `UnitOverlayGeometry` — генерация процедурных quad-мешей (`CreateGroundQuad` в XZ для колец, `CreateBillboardQuad` в XY для HP-баров) и материала с `enableInstancing = true`.
  - `UnitOverlayRenderPass` и `UnitOverlayRendererFeature` — интеграция в Unity 6 URP 17.6 RenderGraph (`RecordRenderGraph` + `RasterCommandBuffer.DrawMeshInstanced` на `RenderPassEvent.AfterRenderingOpaques`) с чанкингом по 250 инстансов и раздельными `MaterialPropertyBlock`.
  - `UnitOverlay.shader` (`GlobalFront/Unit Overlay`) — двухпроходный instanced HLSL-шейдер (`UnitOverlayRing` с антиалиасингом через `fwidth`, `UnitOverlayHealthBar` с рамкой и заливкой по доле здоровья).
  - Расширены `UnitView` (`IsSelected`, `SetSelected`, `MaximumHealth`, `RadiusMillimetres` со строгим сбросом при `Bind`/`Release`) и `UnitViewBinder` (`SlotCount`, кэширование `UnitDefinition` по `UnitKind`, гарантированный сброс `SetSelected(false)` в `ReleaseSlot`); `GlobalFront.Client` и EditMode-тестовый asmdef подключили URP Runtime-сборки.
  - Тесты: `UnitOverlayTests.cs` (25 кейсов) плюс покрытие F-1/F-2 (+28 тестов суммарно) → 763/763 EditMode.
  - Двойной независимый adversarial-аудит и bite-верифицированная ремедиация в `b897eea` → **[APPROVED: ZERO DEFECTS]**, 770/770 EditMode:
    - **P0-1** (`UnitOverlayRendererFeature`) — отдельный guard `_resourcesBuilt` и публичный `IsReady`; ранее проверка `_pass != null` отправляла в ранний выход первый `Create`, который обязан построить меши, и оставляла пас без данных до конца сессии.
    - **P1-1** (`UnitOverlayRenderPass`) — изолированные per-chunk `OverlayChunkBuffers[] RingChunks` / `BarChunks` с `PrepareChunks` и `MaxChunksPerBatch = 17`; командный буфер разрешает `MaterialPropertyBlock` в момент playback, поэтому один общий набор данных делал первый draw «одетым» в instance data второго чанка. Ёмкость считается от `Capacity` батчера, а не от худшего случая в 4096.
    - **P1-2** (`UnitOverlayRendererFeature`, `UnitOverlayRenderPass`, `UnitOverlayGeometry`) — `ReleaseResources()` через `CoreUtils.Destroy` с флагом `_ownsMaterial`: нативные меши и только процедурно созданный материал освобождаются на пересборке и в `Dispose(bool)`; `out bool ownsMaterial` из `TryCreateResources` и хвост `CoreUtils.Destroy` на провале поиска пассов исключают «зависший» материал.
    - **P2-1** — фильтр `CameraType.Game` в `AddRenderPasses` и `RecordRenderGraph`; без него scene view, material preview и reflection probe перестраивали батчи, а последний записавший определял отрисовку игровой камерой, и health bars попадали в reflection cubemap.
    - **P2-4** — снапшот `ringCount` / `barCount` в `PassData` вместо повторного чтения в момент playback, когда общий батчер уже мог быть перестроен другой камерой.
    - **P2-5** (`UnitOverlayBatcher`) — `SanitizeColor` с фолбэками на `Default*Color` и клампом `[0..1]`: один `NaN`-канал из options screen не мог попасть в instance data инстансированного draw.
    - **P3-1** — `SanitizeRotation` нормализует неединичный кватернион перед `Matrix4x4.TRS` (который не нормализует сам): один sqrt на кадр вместо неверного quad на юнита.
    - **P3-2** — `healthFraction <= 0f` канонизирует `-0.0f` в `+0.0f`; голая проверка нижней границы пропускала `0x80000000` в instance buffer.
    - **P3-3** (`UnitViewBinder`) — guard `UnitKinds.IsDefined((byte)kind)` в конструкторе.
    - Новый `Runtime/Client/AssemblyInfo.cs` (+ `.meta`) — `InternalsVisibleTo("GlobalFront.Tests.EditMode")`, чтобы EditMode-набор читал намеренно `internal` буферы чанков и флаг владения материалом.
    - `UnitOverlayTests` расширен с 25 до 32 кейсов (+7 bite-верифицированных регрессионных тестов на P0-1, P1-1, P1-2, P2-1, P2-4, P2-5, P3-1, P3-2, P3-3) → 770/770 EditMode passed; gate: `Artifacts/TestResults/EditMode-step34-remediation.xml`.
- Phase 3.5 Selection System, Screen-Space Drag-Box & RTS Command Issuing — **[APPROVED: ZERO DEFECTS]** (`cfa24c2`, стартовая реализация **829/829 EditMode passed**, 0 failed, 0 skipped; 6/6 PlayMode passed; ADR-012, OD-24). В `GlobalFront.Client.Presentation` / `GlobalFront.Client.UI`:
  - `UnitPickMath` — picking-арифметика без `UnityEngine.Physics`: пересечение луча с плоскостью $Y=0$ по контракту OD-27, сквозной `double`-конверт метры → `WorldPointMm` с `MidpointRounding.AwayFromZero`, `IsInsideScreenRect`, `IUnitPointerProjector` и `CameraUnitPointerProjector`. У представления юнита нет коллайдера (OD-26/OD-25), поэтому `Physics.Raycast` отвечал бы «ничего» для каждого юнита на экране; кроме того physics-hit зависит от порядка вставки коллайдеров и момента шага физики, то есть два клиента по одному пикселю выбирали бы разных юнитов. Дистанция по связанной таблице слотов — детерминирована.
  - `UnitSelectionController` — `SelectionGesture` (`None` / `Click` / `Marquee` / `DoubleClickKind`), каноническое восходящее множество `EntityId`, одиночный клик, Shift-toggle, двойной клик по тому же `UnitKind` на экране, screen-space маркер, `IsStillSelectable` (владелец + живость) на всех точках входа включая `AddSelected` / `SelectSingle` и эмиссию команд, `PruneStaleSelection`, `TryIssueCommandAtPointer` / `IssueMove` / `IssueStop`, санитизированные и валидируемые tuning-свойства.
  - `UnitCommandIssuing` — `IssuedCommand`, `IUnitCommandSink` и `UnitCommandChannelSink`, переадресующий в существующий `ICommandChannel`; детерминированный Core не изменён.
  - `UnitSelectionDriver` — покадровая обвязка `RtsInputManager`, `UnitSelectionController`, `IUnitPointerProjector`, `IUnitCommandSink` и `SelectionMarqueePresenter`; `RequestedTick` назначает владелец цикла, поэтому слой выбора не знает о тиках, сессии и хостинге матча.
  - `SelectionMarqueePresenter` — изолированный transient-канвас OD-24 (`SortingOrder = 1000`), один `Image` типа Sliced, **0 B** аллокаций на кадр, guard `IsDrawable` на нефинитный/отрицательный прямоугольник.
  - `RtsInputManager` расширен: `IsShiftPressed` (любая из двух клавиш Shift) и edge-триггерный `StopRequested`, привязанный к `X`/`H`, — `WASD` остаётся исключительно за панорамированием камеры; плюс mock-сеттеры `SetMockShiftPressed` / `SetMockStopRequested` для EditMode-набора.
  - Тесты: `UnitSelectionAndCommandTests.cs` — 59 кейсов на старте, затем 38 bite-верифицированных ремедиационных → 97 кейсов.
  - Двойной независимый adversarial-аудит, 34-failure bite-пасс и bite-верифицированная ремедиация **P1-1..P1-3, P2-4..P2-8, P3-9..P3-16** → **[APPROVED: ZERO DEFECTS]**, 867/867 EditMode:
    - **P1-1** (жест) — порог драга выводится заново на отпускании, а не только из промежуточных сэмплов: драйвер, увидевший нажатие и отпускание в одном кадре, иначе классифицировал бы драг в 600 px как клик по конечному пикселю и стирал отряд. Нефинитный сэмпл не армит драг, не отменяет живой и не резолвит релиз (`SelectionGesture.None`), а посреди драга оставляет прямоугольник и выделение нетронутыми.
    - **P1-2** (жест) — валидация прямоугольника маркера и вьюпорта проектора выполняется **до** очистки выделения: нефинитный `Rect` в `SelectInsideScreenRect` и проектор с нулевым `ScreenSize` в `SelectKindOnScreen` больше не превращают выбор «ни во что».
    - **P1-3** (владение) — `AddSelected` и `SelectSingle` решают владельца и живость сами, а не доверяют вызывающему: чужой юнит или труп не попадают в набор и не зажигают кольцо; смена `LocalPlayerId` не оставляет в команде юниты прежнего игрока.
    - **P2-4** (округление) — конвертация метры → миллиметры целиком в `double` с `MidpointRounding.AwayFromZero`: промежуточный `float` возвращал 24-битную квантизацию ровно там, где значение максимально, и round-half-to-even уводил ровную половину миллиметра к чётному соседу. Пересечение с плоскостью не пишет позицию при промахе (переполненный луч), кладёт результат ровно на $Y=0$ без float-остатка и отказывает лучу, стартующему на плоскости и уходящему от неё (`enter` — знаковый ноль, который не ловит сравнение).
    - **P2-5** (команды) — эмиссия сама прунит устаревшее выделение: павший юнит в payload — это юнит, которого сервер не знает, а переиспользованный слот читает позицию чужого юнита в центроид, что разворачивает строй на 180°.
    - **P2-6** (настройка) — `FormationSpacingMillimetres` вне серверного диапазона бросает `ArgumentOutOfRangeException` на присваивании, а не превращает каждый ход матча в `InvalidFormation`; отказ оставляет рабочее значение прежним.
    - **P2-8** (обвязка) — `SelectionMarqueePresenter` прячет рамку и никогда не пишет в `RectTransform` нефинитный или отрицательный прямоугольник (`RectTransform` принимает `NaN` и потом сообщает сломанный layout каждый кадр, включая кадры следующего корректного драга).
    - **P3-10** — только Move разворачивается в строй: Attack и Stop игнорируют раскладку, поэтому несут `default(FormationSpec)` без лишнего прохода по центроиду.
    - **P3-14** (`UnitSelectionDriver`) — `SetCamera` перенацеливает уже принадлежащий драйверу `CameraUnitPointerProjector`, а не создаёт новый объект проекции на каждую смену камеры.
    - **P3-16** — нефинитные или отрицательные tuning-значения порога драга, минимального радиуса pick и окна двойного клика откатываются к дефолтам (как `UnitOverlayBatcher` санитизирует цвета), при этом ноль у порога драга остаётся валидной настройкой.
    - Gate: `Artifacts/TestResults/EditMode-step35-remediation.xml`, `Artifacts/TestResults/PlayMode-step35-remediation.xml` → 867/867 EditMode + 6/6 PlayMode (873 автоматизированных теста).

### Verified

- Unity `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0`.
- **867/867 EditMode** passed (100%), 0 failed, 0 skipped, 2026-09-27 (5.60 s; gate: `Artifacts/TestResults/EditMode-step35-remediation.xml`; 873 автоматизированных тестов вместе с PlayMode).
- **6/6 PlayMode** passed (100%), 0 failed, 0 skipped, 2026-09-27 (gate: `Artifacts/TestResults/PlayMode-step35-remediation.xml`).

## Confirmed Foundation History

| Commit | Milestone |
|---|---|
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

Незавершённые Phase 2.3+ системы добавляются сюда только после implementation, tests, review и принятого commit.

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Project Status](PROJECT_STATUS.md)
- [Roadmap](ROADMAP.md)