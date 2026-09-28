# Архитектура GlobalFront

> Живой нормативный документ • current baseline + approved target • обновлено 2026-09-27

## Architectural Principle

GlobalFront строится вокруг authoritative server и deterministic simulation. Product goal — сохранить multiplayer experience Generals/Zero Hour при современной реализации command channel, snapshots, resync, dedicated server и desync diagnostics.

Будущая целевая система не считается реализованной до кода, тестов и обновления фактического статуса.

## Current Runtime Boundaries

| Assembly | Current responsibility | Dependencies |
|---|---|---|
| `GlobalFront.Core` | детерминированные identifiers (`EntityId`, `PlayerId`, opaque `SessionId`/`MatchId`), commands, coordinates, movement, combat, formation, `MatchConfig`, constants | none; Unity API запрещён |
| `GlobalFront.Server` | authoritative `MatchServer`, state, validation, tick phases, `TickDriver` (server tick scheduling), snapshots и Snapshot Protocol v1, session/player identity (`SessionManager`, Phase 2.4) | `GlobalFront.Core`; Unity API запрещён |
| `GlobalFront.Client` | Unity input/presentation, selection (`UnitSelectionController`, `UnitSelectionDriver`, `UnitPickMath`, `UnitCommandIssuing`, `SelectionMarqueePresenter`), minimap & tactical HUD (`MinimapProjection`, `MinimapRadarModel`, `MinimapRadarGraphic`, `MinimapInteractionController`, `TacticalHudPointerBlocker`, `SelectionHudState`, `PermanentHudPresenter`, `SelectionHudPresenter`), vertical slice (`TacticalVerticalSliceRunner`, `LocalReplicationLoopback`, `CorpseReapSnapshotSource`), `RtsCameraController`, `RtsInputManager`, `UnitViewTickBuffer`, `UnitView`, `UnitViewPool`, `UnitViewBinder`, `UnitCatalog`, `UnitOverlayBatcher`, `UnitOverlayGeometry`, `UnitOverlayRenderPass`, `UnitOverlayRendererFeature`, bootstrap, `LocalMatchHost` (ServerHost: владеет `MatchServer`, `TickDriver` и `SessionManager`), command channel adapter, `ClientSession` | Core, Server, Unity/Input System, URP (`Unity.RenderPipelines.Core.Runtime`, `Unity.RenderPipelines.Universal.Runtime`) |

```text
GlobalFront.Core  ←  GlobalFront.Server
       ↑                    ↑
       └── GlobalFront.Client
```

Client → Server является текущей временной связью local prototype, а не конечной dedicated-server topology.

## Confirmed Simulation Contract

- fixed simulation rate: 20 Hz;
- целочисленные `WorldPointMm`;
- канонический порядок команд: `RequestedTick → PlayerId → Sequence`;
- server-owned state и entity assignment через `MatchConfig`;
- команды Move, Attack и Stop валидируются authoritative server;
- presentation получает `ServerUnitSnapshot` после server tick;
- Snapshot Protocol v1: little-endian, 16-byte header, 39 bytes/entity, entity-id order.

## Command Channel — Phase 2.2 Complete

`ICommandChannel` отделяет `PrototypeCommandQueue` от конкретного authoritative backend. `LocalCommandChannel` адаптирует текущий `LocalMatchHost`. Phase 2.2 подтверждена commit `313e9ef` и входит в baseline.

Это abstraction boundary, а не реализованный network transport.

## Current Tick Flow

```text
TickDriver (GlobalFront.Server, 20 Hz, bounded catch-up)
       ↓ TickDue (per tick)
LocalMatchHost  (ServerHost: владеет MatchServer и TickDriver)
       │ TickStarting(N): PrototypeCommandQueue → ICommandChannel → LocalCommandChannel
       ↓ MatchServer.TickOnce (N)
       │ TickCompleted(N): ServerUnitSnapshot → presentation
```

**Phase 2.3 Server Tick Driver — complete, commit `ea0435d`** (ADR-007). Server tick lifecycle больше не зависит от Unity client lifecycle: `TickDriver` в `GlobalFront.Server` расписывает тики (manual mode для тестов, real-time 20 Hz с bounded catch-up через `FixedStepClock`); `LocalMatchHost` выполняет роль ServerHost; `PrototypeRtsController` pump’ит driver и потребляет snapshots; `FixedSimulationRunner` удалён. Local и future dedicated server используют общий simulation core (`MatchServer` + `TickDriver`). Транспорт, session/identity, reconnect/resync — out of scope (Phase 2.4–2.7).

## Session / Player Identity — Phase 2.4 Complete (`73d1276`)

Server-authoritative identity layer (ADR-008): opaque `SessionId`/`MatchId` в Core, `SessionManager` в `GlobalFront.Server` — единственный источник `PlayerId` (монотонное назначение по порядку join, без повторного использования). Command ingress проходит session gate перед неизменным `MatchServer`; `CommandHeader`, canonical command order и Snapshot Protocol v1 не изменяются. Match lifecycle в 2.4 завершается на `Finished`; `MatchPhase.Closed` (teardown/registry disposal) — reserved future state для dedicated/server lifecycle. Transport attribution реализован в Phase 2.5 (ADR-009); реализация reconnect, конкретная grace duration — Phase 2.7 / TBD.

## Network Transport — Phase 2.5 Complete (`feat: implement network transport (Phase 2.5)`)

Транспортный слой по ADR-009 (OD-1 = LiteNetLib 1.3.5): контракт `INetworkCarrier` с двумя носителями — детерминированный `OwnDatagramCarrier` поверх `VirtualNetworkPipe` и `LiteNetLibCarrier` (real UDP). C0 Control и C1 Command — reliable ordered (единое seq-пространство на направление), C2 Snapshot — unreliable sequenced latest-wins по `SnapshotTick`; Snapshot Protocol v1 переносится как opaque payload. Атрибуция: токен → `ConnectionHandle` → `SessionId` → session gate ADR-008; `NetworkCommandChannel` реализует `ICommandChannel` (local pre-flight отдельно от асинхронного авторитетного `CommandAckPayload`). Транспорт не владеет simulation: `MatchServer`/`TickDriver`/`SessionManager`/`CommandHeader`/Snapshot Protocol v1 не изменены. Финальный независимый review = APPROVE (P0=0/P1=0/P2=0). Delta snapshots / cadence / Fog of War replication — Phase 2.6; reconnect/resync — Phase 2.7.

## Approved Target Architecture

Product 1.0 требует:

- authoritative dedicated server;
- sessions и player identity до 10 игроков;
- network transport для commands и snapshots;
- reconnect/resync;
- replay;
- desync detection и нормальную диагностику;
- стабильные длительные матчи и большие армии;
- 3000+ entity target;
- pathfinding, performance и reliability, пригодные для 5v5.

Transport technology определена ADR-009 (Accepted; OD-1 = LiteNetLib 1.3.5 — preferred initial carrier за контрактом `INetworkCarrier`) и реализована в Phase 2.5. Protocol cadence, resync algorithm, replay format, desync hashing/telemetry, deployment topology и server infrastructure: **TBD / Architecture Decision Required**. Session model baseline зафиксирован ADR-008 (Phase 2.4); его dedicated-server teardown — отдельное будущее решение.

## Gameplay Architecture Boundary

Economy, building, production, Fog of War, capture, garrison, veterancy, generals, abilities, superweapons, AI, factions и maps входят в approved Product Scope, но ещё не входят в confirmed runtime baseline. Их архитектура определяется по production rule по мере приближения соответствующих фаз.

## Scale and Performance

Benchmarks должны появиться в Phase 3 и сопровождать развитие gameplay. Phase 8 выполняет глубокую optimization/reliability работу для 3000+ target, 5000+ stress, 5v5, долгих матчей, CPU, memory, GC, pathfinding, network load, reconnect и desync.

Hardware profiles, budgets, benchmark scenes/workloads и pass thresholds, кроме утверждённых entity targets: **TBD / Owner Decision Required**.

## Architecture Governance

Для каждой крупной системы:

```text
R&D → Architecture Decision → Implementation → Tests → Review → Documentation → Commit
```

Изменение deterministic contract, snapshot layout или protocol-significant constants требует compatibility review и соответствующего ADR.

## Presentation Architecture — Phase 3 (ADR-012, OD-23..OD-29)

Презентационный слой полностью изолирован от серверной детерминированной симуляции:
- **`UnitViewTickBuffer` (OD-23):** кольцевой буфер (32 слота) с плавающим окном интерполяции тиков, демпфированием микро-джиттера поворота (`HeadingDeadzoneDegrees`), клампированной экстраполяцией к `MoveTarget` и защитой от деления на ноль.
- **`UnitCatalog` (OD-29):** клиентский справочник архетипов юнитов (`UnitDefinition`), сопоставляющий `UnitKind` с физическими характеристиками, мешами и базовыми параметрами.
- **`UnitViewPool` & `UnitViewBinder` (OD-26):** преаллоцированный пул физических представлений (`UnitView`) без вызовов `Instantiate`/`Destroy` во время боя. Корпус юнита представляет собой статичный меш (движение гусениц через UV-скролл в шейдере, без Mecanim `Animator`), поворот башни управляется дочерним узлом (`TurretYawDegrees`). Привязка слотов `ClientReplicationWorld` к представлениям выполняется за $O(1)$ через плоский массив без хеширования и без аллокаций памяти в кадре.
- **Инстансированные оверлеи `UnitOverlayBatcher` / `UnitOverlayGeometry` / `UnitOverlayRenderPass` / `UnitOverlayRendererFeature` / `UnitOverlay.shader` (OD-25):** кольца выделения и полоски здоровья — единственные «объёмные» элементы презентации вне корпуса юнита, поэтому они рисуются батчами, а не объектами. `UnitOverlayBatcher` переводит представления в два плоских массива матриц за $O(n)$ с **0 B GC Alloc** в превыделенных буферах (`DefaultCapacity = 512`, `MaxCapacity = 4096`); `UnitView.IsSelected` / `MaximumHealth` / `RadiusMillimetres` — единственный источник данных для оверлея. `UnitOverlayGeometry` генерирует процедурные quad-меши без запечённых ассетов (ground-quad в XZ для колец, billboard-quad в XY для HP-баров) и instancing-совместимый материал. `UnitOverlayRenderPass` встраивается в URP 17.6 RenderGraph через `RecordRenderGraph` и `RasterCommandBuffer.DrawMeshInstanced` на `RenderPassEvent.AfterRenderingOpaques`, чанкуя батчи по 250 инстансов (лимит константного буфера `UNITY_INSTANCED_ARRAY_SIZE`) и используя отдельные `MaterialPropertyBlock` на вид оверлея. Двухпроходный шейдер `GlobalFront/Unit Overlay` (`UnitOverlayRing` с `fwidth`-антиалиасингом, `UnitOverlayHealthBar` с рамкой и заливкой по доле здоровья) не требует per-unit `Canvas` и не трогает детерминированный Core. Жизненный цикл ресурсов и изоляция оверлея закреплены ремедиацией аудита: каждый чанк батча получает собственные `OverlayChunkBuffers` (матрицы, векторы свойств и `MaterialPropertyBlock`), потому что командный буфер разрешает block в момент playback, а не записи; пас исполняется только для `CameraType.Game` (в `AddRenderPasses` и в `RecordRenderGraph`), так что scene view, material preview и reflection probe не перестраивают общий батчер и не кладут HP-бары в reflection cubemap; `ringCount`/`barCount` снапшотятся в `PassData` до воспроизведения; меши и процедурно созданный материал (флаг `_ownsMaterial`) освобождаются через `CoreUtils.Destroy` при пересборке и в `Dispose(bool)`.

- **Выбор, рамка выделения и выдача RTS-команд (OD-24, `UnitPickMath` / `UnitSelectionController` / `UnitCommandIssuing` / `UnitSelectionDriver` / `SelectionMarqueePresenter`):** picking — это арифметика, а не физика. У представления юнита нет коллайдера (корпус рисуется из преаллоцированного пула, OD-26), а кольцо выделения — инстансированный оверлей из таблицы слотов (OD-25), поэтому `Physics.Raycast` по этой иерархии ответил бы «ничего» для каждого юнита на экране; кроме того physics-hit зависит от порядка вставки коллайдеров и момента шага физики, то есть два клиента, кликнувшие один пиксель, выбрали бы разных юнитов. `UnitPickMath` поэтому пересекает луч с плоскостью $Y=0$ (OD-27) как статическая чистая функция, отказывая для нефинитного луча, луча, параллельного плоскости или направленного от неё, и для луча, стартующего на неправильной стороне; `ParallelAxisEpsilon` ловит и «почти горизонтальный» луч, потому что `ScreenPointToRay` на камере, упертой в кламп питча, даёт крошечный ненулевой `direction.y` и дистанцию входа в плоскость порядка $10^8$ м — правдоподобную миллиметровую координату далеко за картой. Граница метры → миллиметры решается сквозным `double` с `MidpointRounding.AwayFromZero` (промежуточный `float` возвращал бы 24-битную квантизацию ровно у верхней границы координат, а round-half-to-even увёл бы ровную половину миллиметра к чётному соседу). `UnitSelectionController` хранит каноническое восходящее множество `EntityId` и классифицирует жест как `SelectionGesture` (`None` / `Click` / `Marquee` / `DoubleClickKind`); порог драга выводится на отпускании, а не из промежуточных сэмплов, потому что драйвер может увидеть нажатие и отпускание в одном кадре. Авторитет и живость (`IsStillSelectable`) решаются на **каждой** точке входа, включая `AddSelected`, `SelectSingle` и саму эмиссию команды: чужой юнит или труп не попадают в набор и не зажигают кольцо, а переиспользованный слот не читает позицию чужого юнита в центроид, что разворачивало бы строй на 180°. Команды уходят через `IUnitCommandSink` (`UnitCommandChannelSink` → существующий `ICommandChannel`), а не напрямую в канал: слой выбора не знает ни тикового окна, ни атрибуции сессии, ни того, где хостится матч, — `RequestedTick` назначает владелец цикла. `UnitSelectionDriver` — единственное место, где встречается покадровый цикл Unity; контроллер принимает позицию, проекцию и явные часы, поэтому тест воспроизводит двойной клик на 0.29 с и драг в 400 юнитов, не ожидая ни того, ни другого. `SelectionMarqueePresenter` реализует transient-канвас OD-24 изолированно (`SortingOrder = 1000`, один `Image` типа Sliced, **0 B** аллокаций на кадр) и прячет рамку вместо записи в `RectTransform` нефинитного прямоугольника: `RectTransform` принимает `NaN` без возражений и затем сообщает сломанный layout каждый кадр, включая кадры следующего корректного драга. Ни один из этих классов не трогает детерминированный Core.

- **Миникарта и тактический HUD (OD-24 / OD-27 / OD-28, `MinimapProjection` / `MinimapRadarModel` / `MinimapRadarGraphic` / `MinimapInteractionController` / `TacticalHudPointerBlocker` / `SelectionHudState` / `PermanentHudPresenter` / `SelectionHudPresenter`):** слой `GlobalFront.Client.UI`. Миникарта — **арифметика, а не вторая камера**: OD-24 запрещает постоянную камеру URP поверх запечённого террейна, поэтому кадр камеры на миникарту не рендерится вовсе, а `MinimapProjection` переводит координаты напрямую — нормализованный UV `[0..1]` ⇄ пиксели виджета ⇄ координаты мира в миллиметрах `[-200_000..+200_000]` по каждой оси, то есть ровно домен карты $400 \times 400$ м с центром в $(0, 0)$ (OD-27). `MetresToMillimetres` клампит сатурирующе в `[int.MinValue..int.MaxValue]` и отдаёт `0` на `NaN`: промежуточный `float` вернул бы 24-битную квантизацию ровно у границы домена, а переполнение `int` выдало бы точку по другую сторону карты. Рамка камеры на миникарте строится не по экранному прямоугольнику камеры, а `TryCameraGroundRect` пересекает луч камеры с плоскостью $Y=0$ — той же, что использует `UnitPickMath` для picking, поэтому рамка миникарты и клик по земле физически не могут разойтись на разных плоскостях. Статический террейн запекается ровно один раз при загрузке карты (`BakeDefaultTerrainTexture`, процедурная RGBA32 64 × 64, без запечённых ассетов), а в рантайме `MinimapRadarModel.RefreshIfDue` снимает rate-gated 30-Гц снапшот из `ClientReplicationWorld` в переиспользуемый преаллоцированный буфер `MinimapBlip[]` (слоты с `Health <= 0` пропускаются, принадлежность local/enemy/neutral выводится из владельца). `MinimapRadarGraphic` — `MaskableGraphic`, который рисует **все** точки радара и 4-реберную рамку вьюпорта в одном батченом UI draw call через `OnPopulateMesh(VertexHelper)` с преаллоцированным `UIVertex[4]`: цена миникарты не растёт с числом юнитов, что и требует профиль OD-28. `MinimapBlipGeometry.BuildQuads` санитизирует нефинитный `blipSizePixels` и клампит половину размера в `min(width, height) * 0.5f`, чтобы вырожденный или схлопнувшийся виджет не построил перевёрнутые квады, а `SetModel(null)` обнуляет `_quadCount` и помечает вершины грязными вместо того, чтобы оставить протухшую геометрию на экране. Три канваса OD-24 изолированы по `SortingOrder`: постоянный HUD (`PermanentHudPresenter`, 100), динамическая выборка (`SelectionHudPresenter`, 200) и transient-рамка выделения (`SelectionMarqueePresenter`, 1000). Поскольку HUD перекрывает игровое поле, обычный экранный picking выбора не должен «проваливаться» сквозь панель: `TacticalHudPointerBlocker` держит преаллоцированную таблицу из 8 экранных прямоугольников и сворачивает 4 угла `GetWorldCorners`, а `HasArea` валидирует `x`/`y`/`width`/`height`/`xMax`/`yMax` на конечность в духе Unity 6 — `RectTransform` принимает `NaN` без возражений, как и `SelectionMarqueePresenter` выше. `SelectionHudState` держит модель выборки и обратной связи по командам как чистую C#-логику над `UnitSelectionController`, `ClientReplicationWorld`, `UnitCatalog` и `IUnitCommandSink` (идентичность слота проверяется как `state.Entity == expectedEntity && state.Health > 0 && state.Owner == localPlayer`, доля здоровья клампится в `[0..1]`), а `PermanentHudPresenter`/`SelectionHudPresenter` только отображают её и обновляют текст через препрогретые числовые строковые таблицы с гейтом по `ReferenceEquals`. Ввод по миникарте идёт через `MinimapInteractionController`: ЛКМ панорамирует камеру через `RtsCameraController.SnapToWorldXZ`, ПКМ по земле выдаёт приказ движения через тот же `IUnitCommandSink` и `UnitSelectionController`, что и рамка выделения, так что оба способа приказа физически не могут разойтись; `UnitSelectionDriver` синхронизирует `RequestedTick` миникарты со своим, маршрутизирует ЛКМ down/drag/up и ПКМ up над миникартой и гасит `CancelDrag()` / `ClearHover()` при входе указателя в HUD. Ни один из этих классов не трогает детерминированный Core.

- **Вертикальный срез 400 юнитов и гейт инвариантов архитектуры (OD-23..OD-29, `TacticalVerticalSliceRunner` / `LocalReplicationLoopback` / `CorpseReapSnapshotSource` / `ArchitectureInvariantsTests`):** слой `GlobalFront.Client.Presentation`. Срез существует, чтобы **доказать профиль OD-28 на живом стеке, а не суммировать бюджеты слоёв по отдельности**: `TacticalVerticalSliceRunner` — единственный владелец покадрового цикла, который сам шагает симуляцию и презентацию в правильном порядке, поэтому ни один подключённый компонент не «шагает себя» (`Slice_OwnsTheFrameLoopSoNoWiredComponentStepsItself`) и порядок «тик → репликация → буфер → пул → оверлеи → HUD» не может разойтись между редактором и player-сборкой. Проводка идёт от `LocalMatchHost` (авторитетный сервер 20 Гц) через `ServerReplicationEmitter` / `ClientTransportReplicationBridge` / `ClientReplicationReceiver` / `ClientReplicationWorld` в `UnitViewTickBuffer`, затем в `UnitViewPool` / `UnitViewBinder` и дальше в `UnitOverlayBatcher` + `DrawOverlaysDirect`, `UnitSelectionController` / `UnitSelectionDriver` / `SelectionMarqueePresenter` и `MinimapRadarModel` / `MinimapInteractionController` / `PermanentHudPresenter` / `SelectionHudPresenter`. Транспорт замыкается **внутри процесса** через `LocalReplicationLoopback` (`IReplicationTransport` + `IReplicationUplink`): он несёт дельты вниз и keyframe-фидбэк вверх без сетевых syscalls, поэтому замер OD-28 отражает стоимость пайплайна репликации, а не стоимость UDP. `LocalMatchHost.AttachReplication` получил перегрузку, принимающую пользовательский `IServerSnapshotSource`, — иначе источник снапшотов был бы захардкожен и подменять его в тесте без правки продакшн-кода было бы нельзя. Удалённые юниты не исчезают из мира мгновенно: `CorpseReapSnapshotSource` ведёт счётчик захватов (`GraceCaptures = 2`) и помечает убранный слот `ReapedSentinel` (`uint.MaxValue`), чтобы труп успевал исчезнуть из представлений и выборки ровно один раз, а повторные проходы диффа на пустых слотах не порождали повторных `Remove`-записей. Срез **заимствует** камеру и input-состояние и **возвращает их в `Shutdown()`**, включая случай, когда `RtsInputManager` создал сам — иначе переключение обратно на прототип оставляло бы сцену с двумя источниками ввода. Батчинг 250-юнитной сетки не кратен размеру батча, поэтому `BatchDestination` распределяет хвостовой батч в собственный блок, а не обрезает строй. Протокольная часть потребовала двух точечных фиксов: `ServerReplicationEmitter.ComputeStateChecksum` и `ClientReplicationWorld.ComputeStateChecksum` маскируют очищенные координаты `MoveTarget` (при `HasMoveTarget == false` хешируется `(0, 0)`), а `ServerSnapshotDiffEngine.ComputeDirtyMask` эмитит `UnitDirtyMask.MoveTarget`, когда `current.HasMoveTarget && (!previous.HasMoveTarget || previous.MoveTarget != current.MoveTarget)` — остановленный юнит, которому повторно назначили ту же координату, обязан реплицировать цель чисто, иначе клиент и сервер разошлись бы по контрольной сумме. Наконец, `ArchitectureInvariantsTests` переводит часть OD-23..OD-29 из ADR в **исполняемые** правила: границы сборок через reflection/IL-токены, ноль `float`/`double`/`decimal` в авторитетном состоянии, ноль `System.Linq` в рантайме, ноль незасеянного RNG / стенного часа / `Guid.NewGuid` в авторитетной симуляции (с покрытием `TargetTypeOf` + `ResolveType` и двухбайтовой опкодой `0xFE`, иначе long-form инструкции обходили бы гейт) и `[DisallowMultipleComponent]` на всех клиентских `MonoBehaviour` Phase 3. Ни один из этих классов не трогает детерминированный Core.

## Verification Baseline

- Unity `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0`
- 1086/1086 EditMode passed (100%), 0 failed, 0 skipped, 8.76 s
- 13/13 PlayMode passed (100%), 0 failed, 0 skipped, 17.08 s
- last committed milestone: `3414ba6` (Phase 3.7 Vertical Slice Integration, 400-Unit Benchmark, Multi-Frame PlayMode Suite & Architecture Invariants Gate, ADR-012/OD-23..OD-29; `TacticalVerticalSliceRunner` + `LocalReplicationLoopback` + `CorpseReapSnapshotSource` end-to-end 400-unit slice, 30-case `TacticalVerticalSliceBenchmarkTests`, 7-case multi-frame `TacticalVerticalSlicePlayModeTests`, 12-case `ArchitectureInvariantsTests`, plus the `ComputeStateChecksum` / `ComputeDirtyMask` MoveTarget protocol fix; triple independent adversarial audit (Space Bunny + GLM 5.3 Flash + Pixel Canary) and remediation verification [APPROVED: ZERO DEFECTS]; 1086/1086 EditMode + 13/13 PlayMode)
- OD-28 measured profile at `3414ba6`: **0.623 ms/frame** average over 120 presentation frames with the authoritative simulation tick included, and **0 B GC.Alloc** across 60 steady-state presentation frames and 60 replication loopback ticks — far below the 8.0 ms EditMode guard and the 16.67 ms 60 FPS ceiling
- Фазы 1.0–2.8 заморожены как сертифицированный бейзлайн (`a37f53d`, Grand Audit APPROVED: ZERO DEFECTS)
- Фазы 3.1–3.7 сертифицированы независимым adversarial-аудитом ([APPROVED: ZERO DEFECTS]) — **Phase 3 [100% COMPLETED]**
- next: Phase 4 Multiplayer Gameplay Integration (joins the Phase 2 network foundation to the accepted Phase 3 RTS gameplay)

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Project Status](PROJECT_STATUS.md)
- [Decisions](DECISIONS.md)
- [Phase 02](Phases/Phase_02_Multiplayer.md)