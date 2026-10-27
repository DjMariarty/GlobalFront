# GlobalFront Current State

> Быстрый handoff для AI-агента • обновлено 2026-10-27

## Current Phase

**Phases 1–3 — [COMPLETED / CERTIFIED] • Phase 4 / Milestone M1 (1v1 Dedicated Server Prototype) — [REMEDIATION DELIVERED, PENDING COORDINATOR REVIEW]** (изменения не закоммичены поверх `5beb1d1`)

## Current Task

**M1 remediation (Blocks 1–6) complete • 1351 EditMode + 22 PlayMode green • next: independent review & commit; then Milestone M2 (2v2 + Phase 5)**

- **M1 remediation 2026-10-27 (на базе `5beb1d1`, без коммита):** Grand Audit M1 (`Artifacts/Audit-Phase4-M1-5beb1d1.md`, `Artifacts/GrandAudit-Phase4-M1-5beb1d1-R2.md`, `-R3.md`) вынес REJECT. Закрыты: corpse vision (`FogOfWarReplicationFilter` исключает `CurrentHealth <= 0`), утечка координат через `MoveTarget` преследователя (заморозка на last-known), status-oracle в отказах атаки (`TargetInFogOfWar` до проверок существования/жизни), автоматический старт 1v1 (`DedicatedServerRunner`: lobby → ростер → `TryStartSessionMatch`), повторяемый `InvalidState`, дедлайн `_awaitingResume` (400 paused ticks), запрет преждевременного resume, фильтрация поколений `ConnectionId`, возврат пуловых буферов на всех путях сброса (+ Rent/Return accounting), resync колец по `EntityId` и удержание selection через пустое зеркало. 17 новых EditMode-тестов, 15 bite-прогонов.
- **Scope M1 зафиксирован как 1v1** (OD-48 в `DECISIONS.md`); 2v2 официально перенесён в Milestone M2 / Phase 5.

- **Phase 1 (Simulation Foundation):** [100% COMPLETED / AUDITED: ZERO DEFECTS]. Спринты hardening 1.1–1.5.1 закрыли concurrency, reentrancy, friendly-fire и snapshot allocations (`5b16a05`..`349a074`).
- **Phase 2 (Multiplayer Foundation):** [100% COMPLETED / P0/P1 = 0 CERTIFIED]. Шаги 2.1–2.8 + спринты hardening 2.1–2.5 (`5c94e07`..`4cb8ffa`). Сертификация Phase 2.0 на `4cb8ffa` подтвердила **P0/P1 = 0**: это строгий состязательный ре-аудит, выполненный Claude на основе коммита `4cb8ffa` (отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`); сам отчёт ограничивает область сертификации P0/P1 и фиксирует открытый на тот момент P2-бэклог (N-02, N-03, N-04) и P3-пакет. Сторонние модели привлекались только на промежуточных этапах ревью и авторами сертификата не являются. Уязвимость N-01 / D-10 / A-01 полностью ликвидирована на 3 независимых слоях. Все оставшиеся замечания (F-1..F-11, F-13..F-15, N-02..N-04) закрыты в Hardening Phase 3.7.1 — см. блоки ниже.
- **Phase 3 (Visual Presentation & RTS Controls):** [100% COMPLETED / AUDITED: ZERO DEFECTS]. Шаги 3.1–3.7 (ADR-012, OD-23..OD-29). Сквозной вертикальный срез на 400 юнитах (`3414ba6` / `cb01ea2`). Замер OD-28: **0.623 ms/frame** — это CPU-пайплайн симуляции и презентации (headless EditMode, 400 юнитов, гард 8.0 ms), а не полный кадр с физическим GPU draw-call; **0 B GC.Alloc** за 60 steady-state кадров.
- **Hardening Phase 3.7.1 (закрытие Grand Audit):** [100% COMPLETED]. Блоки 1–4 (`934840d`..`3a112ad`) закрыли code-level замечания F-1..F-11, F-13..F-15 и N-02..N-04; блок 5 закрыл F-6 и F-10. 17 новых EditMode-тестов (1162 → 1179), bite-прогоны по каждому наблюдаемому дефекту.
- **Phase 4 (Multiplayer Gameplay Integration):** [M1 REMEDIATION DELIVERED / PENDING REVIEW]. ADR-014 принят (OD-35..OD-43); формат M1 зафиксирован как 1v1 (OD-48), 2v2 — Milestone M2. Grand Audit M1 вынес REJECT прежней сертификации; см. блок remediation в начале документа.
- **Шаг 4.1 (Dedicated Server Core Relocation & Headless Runner):** [COMPLETE / COMMITTED `5f2f29e`]. `ServerHost` вынесен в `GlobalFront.Server` (engine-free), `LocalMatchHost` остался тонкой клиентской обёрткой с `BindReconnectCoordinator`; добавлена сборка `GlobalFront.ServerHost` с `DedicatedServerRunner` (цикл `Pump` → `AdvanceRealTime`, 120 FPS) и `DedicatedServerBootstrap` (`-port`, `-tickrate`, `-dedicatedServer` / `UNITY_SERVER`); в `FixedStepClock` добавлен кламп бэклога `MaxBacklogSeconds = 0.25` (OD-45).
- **Шаг 4.2 (Client Remote Connection & Handshake):** [COMPLETE / COMMITTED `1760f17`]. `ClientTransportEndpoint` отправляет `Ping` и публикует `PongReceived`; `RemoteSliceSession` компонует endpoint, uplink, receiver/bridge, C1 command channel и RTT-контур (каденс 1 Гц, EWMA α = 0.125 по клиентским часам); `TacticalVerticalSliceRunner.InitializeDedicatedClient` работает без локального `_host` (тик — из `ClientReplicationReceiver`, команды — через `NetworkCommandChannel`, пул прогревается по полученному ростеру), RTT и сетевой статус выводятся в `NetworkHudPresenter`.
- **Шаг 4.2.1 (Client Bootstrap CLI `-connect`):** [COMPLETE / COMMITTED `34e994e`]. `GlobalFrontRuntimeBootstrap` разбирает `-connect <host>[:<port>]` и `-connect=<host>[:<port>]` (порт по умолчанию 7777, диапазон 1..65535; отсутствие флага/пустой хост/невалидный порт → `false`). При наличии флага `Initialize()` не строит легаси-прототип, а вызывает `ActivateDedicatedClient`: снимает легаси-прототип и предыдущий срез, создаёт `RemoteSliceSession`, подключается и инициализирует раннер в `DedicatedClient`; сессией владеет бутстрап и освобождает её при замене среза. Гейт: EditMode **1219/1219**, PlayMode **17/17**; live PlayMode-проверка с реальным UDP-сервером: `Established`, RTT 28–29 ms, HUD-слот `Connected`.
- **Шаг 4.3.1 (Core Visibility Engine & Visibility History Ring):** [COMPLETE / COMMITTED `e5d2ec0`]. `GlobalFront.Core.Visibility`: `BitMask1024` (ровно 128 B, 16 `ulong`, bounds-checked set/Clear/IsSet, `Or`/`And`/`AndNot`, SWAR popcount), `VisibilityEngine` (uniform grid 8192 мм: плотный прямоугольник для компактных сцен + hash fallback для разреженных, мемоизация полностью видимых ячеек, свои и союзники видны безусловно, враги — по радиусу 20 м, команды через `SetPlayerTeam`, zero-GC) и `GlobalFront.Server.Replication.VisibilityHistoryRing` (120 тиков × 10 игроков, forward-only, вытеснение, бинарный поиск, zero-GC). Вход движка — engine-free `VisibilityUnitSnapshot`: `ServerUnitSnapshot` живёт в `GlobalFront.Server`, на который Core ссылаться не может. 39 новых EditMode-тестов. Гейт: EditMode **1258/1258**, PlayMode **17/17**; 1024 юнита — **0.694 ms** median за расчёт в финальном гейте (0.709 ms focused; минимум по прогонам 0.398 ms), гард 1.0 ms, 0 B GC.Alloc.
- **Шаг 4.3.2 (Per-Client Replication Filtering & StateChecksum, OD-39/OD-40):** [COMPLETE / COMMITTED `3666834`]. `IReplicationFilter` + `FogOfWarReplicationFilter` (VisibilityEngine + VisibilityHistoryRing, один преаллоцированный `VisibilityUnitSnapshot[1024]`, сущности вне 1..1024 игнорируются и считаются в `IgnoredUnitCount`, zero-GC). `ServerReplicationEmitter`: `PlayerId` в `ClientState`, фильтрация кейфреймов по `V_tick`, per-client дельты по переходам `V_base → V_cur` (`0→1` ADD из текущего снимка, `1→0` REMOVE `FogOfWarHidden`/`Destroyed`, `1→1` слитые UPDATE, `0→0` полный пропуск), fallback на кейфрейм при `encodedSize > MaxPayloadBytes (8192)` или вытесненной маске, фильтрованный `DeltaResume`, per-client FNV-1a чексумма по возрастанию `EntityId`. 12 новых EditMode-тестов. Гейт: EditMode **1270/1270**, PlayMode **17/17**, 0 warnings / 0 errors.
- **Шаг 4.4.1 (Server FoW Target Validation & Command Rejection Gates, OD-41):** [COMPLETE / COMMITTED `4e830c9`]. `MatchCommandRejection.TargetInFogOfWar = 12`; `MatchServer` получил `IReplicationFilter _visibilityFilter` и anti-maphack гейт в `TryEnqueueAttack`: цель, невидимая для `header.Player` на `CurrentTick`, отклоняется до `Schedule`/`CommitSequence`. 9 новых EditMode-тестов. Гейт: EditMode **1279/1279**, PlayMode **17/17**, 0 errors.
- **Шаг 4.4.2 (Client Lead Calculation, Multi-Batch Dispatch & E2E Network Ingress, OD-41):** [COMPLETE / COMMITTED `5bdab1e`]. `TacticalVerticalSliceRunner.CalculateLeadTicks` складывает play-out задержку тик-буфера (`RenderDelayTicks`), половину сглаженного RTT и защитный интервал `+2`, удерживая `RequestedTick` внутри серверного окна `[-20, +60]`. `TacticalHudText.RejectionNames` получил `"Target in fog"`. 9 новых EditMode-тестов. Гейт: EditMode **1288/1288**, PlayMode **17/17**, 0 errors.
- **Шаг 4.5.1 (Reconnect Signal Wirings & C0 Transport Integration, OD-18/OD-20):** [COMPLETE / COMMITTED `1b9b288`]. `ClientReplicationReceiver.KeyframeInstalled` поднимается строго на пути `BaselineInstalled` без аллокаций. `ClientReconnectCoordinator` подписывается автоматически. `ClientTransportEndpoint` распознаёт opcodes 9 (`SessionSecretMessage`) и 13 (`ReconnectResponse`). 6 новых EditMode-тестов. Гейт: EditMode **1294/1294**, PlayMode **17/17**.
- **Шаг 4.5.2 (View Batch Recycling & Presentation Resync, OD-42):** [COMPLETE / COMMITTED `364646b`]. `TacticalVerticalSliceRunner.BuildPresentation` подписывает `KeyframeInstalled` на `UnitViewBinder.HandleResync`. 6 новых EditMode-тестов `ViewBatchRecyclingTests`: 400 представлений возвращаются в пул без `Destroy`, повторный захват 400 — 0 samples GC.Alloc. Гейт: EditMode **1300/1300**, PlayMode **17/17**.
- **Шаг 4.5.3 (Live Combat Reconnect & Tactical Pause Governance, OD-42 final):** [COMPLETE / COMMITTED `57a8cef`]. 400-юнитный паритет мира под обрывом и реконнектом, grace period 4000 тиков (200 с), 5-секундный countdown, авто-возобновление. 4 E2E-теста. Гейт: EditMode **1304/1304**, PlayMode **17/17**.
- **Шаг 4.5.4 (Runtime Loop Closure & Production Runner Wiring):** [COMPLETE / COMMITTED `7be1648`]. C0 token wire в `ReconnectResponse` (байты 21..28); `ClientTransportEndpoint.Reconnect` + `RemoteSliceSession.TryReconnect`; FoW подключён в `DedicatedServerRunner`. 4 новых EditMode-теста. Гейт: EditMode **1308/1308**, PlayMode **17/17**.
- **Шаг 4.6.1 (Reconnect Retry Policy & Socket-Connect Timeout):** [COMPLETE / COMMITTED `eb679e0`]. Регулярный авто-ретрай каждые 500 мс в `RemoteSliceSession.Pump`; таймаут зависшего сокета 2000 мс в `ClientTransportEndpoint`. 3 новых EditMode-теста. Гейт: EditMode **1311/1311**, PlayMode **17/17**.
- **Шаг 4.6.2 (Impairment Harness & Presets, Wi-Fi 1% / WAN 5% + Burst):** [COMPLETE / COMMITTED `1b38f05`]. Пресеты `Lan`/`Wifi`/`Wan`, детерминированная burst-модель потерь, телеметрия `SentCount`/`DroppedCount`/`DeliveredCount`. 5 новых EditMode-тестов. Гейт: EditMode **1316/1316**, PlayMode **17/17**.
- **Шаг 4.6.3 (Parity under Loss/Latency on 400 Units, OD-40 / OD-43):** [COMPLETE / COMMITTED `4d15ace`]. Паритет 400 юнитов под 1% и 5% потерями (120 тиков боя, zero desyncs, `ChecksumMismatchCount == 0`, `AssertWorldsMatch`), keyframe-rebase после 150-тикового обрыва. 3 новых EditMode-теста. Гейт: EditMode **1319/1319**, PlayMode **17/17**.
- **Шаг 4.6.4 (Live UDP PlayMode Harness & Reconnect Path B):** [COMPLETE / COMMITTED `1eeb3d2`, `491811c`, `3eca5de`]. Сборка LiteNetLib с `SIMULATE_NETWORK`; 2 live PlayMode-теста на реальных UDP-сокетах под 5% и 4% потерями; C0 Opcode 14 `ClientReadyToResume` авто-возобновление сервера по окончании countdown. Гейт: EditMode **1332/1332**, PlayMode **21/21**.
- **Шаг 4.6.5 (Synthetic 400-Unit Playtest, Full HD Visual Evidence, Polish & Zero-GC Transport):** [COMPLETE / COMMITTED `666f89f`, `828c55d`, `ff344a9`, `366e444`, `1bac832`, `156d898`].
  - Step 1: Pump-owned countdown ticker с epsilon threshold и single-attempt flush.
  - Step 2: `TacticalPauseOverlay` интегрирован с `NetworkHudPresenter` и presentation loop.
  - Step 3: Синтетический 400-юнитный PlayMode-тест (`SyntheticPlaytestPlayModeTests`), 1080p capture harness (`PlaytestScreenshotCapture`), прогрев пула при первом keyframe.
  - Polish Sprint:
    1. Ремонт колец выделения после реконнекта (`ResyncSelectionViews()`, отложенный запуск после `Render`).
    2. Единый столбец значений HUD (`ValueColumnX = 124f`), исключающий наложения текста.
    3. Честный HP в бою: спавн 100 HP (согласован с `UnitCatalog`), встречный бой лоб в лоб с уроном (`03_clash_combat.png` — 74% HP, `05_reconnected_and_resumed.png` — 50% HP).
    4. Интерактивная точка входа `GlobalFront/Playtest/Launch 400-Unit Dedicated Match` (`InteractivePlaytestBootstrap.cs`).
    5. Архитектурный Zero-GC фикс транспорта: `TransportBufferPool.cs`, возврат буферов после обработки событий, пул ассемблера фрагментов. M1 remediation добавил учёт `RentCount`/`ReturnCount` и закрыл потери на rate-limit, queue overflow, teardown и shutdown.
    6. Стабилизация `NetworkImpairmentPlayModeTests.cs` (`1bac832`): пошаговая откачка тиков сервера во время ожидания дельт при 5% потерь пакетов, устранившая таймауты под нагрузкой полного пакета тестов.
    7. Диагностика аллокационных стек-трейсов (`156d898`, `Artifacts/Playtest/gc_alloc_callstacks.txt`): доказано отсутствие аллокаций в `GlobalFront.*` и `LiteNetLib.*`; подтверждено, что периодические блоки вызваны внутренними механизмами `UnityEngine.TestRunner` (`LogScope`), Editor и URP/IMGUI.
  - Гейт: EditMode **1334/1334**, PlayMode **22/22** (всего **1356/1356 passed**) на момент шага 4.6.5; после M1 remediation 2026-10-27 — **1351 EditMode + 22 PlayMode**.
  - Сценарий синтетического плейтеста длится **≈26.1–26.3 s** (спавн, бой, разрыв на 20-й секунде, реконнект, возобновление), а не «5 минут»; FPS-окно — 3 s.
  - Zero-GC контракт: измеряется только клиентский транспорт и пайплайны репликации при отключённой презентации (3-секундное окно) — 0 блоков GC.Alloc. Полный кадр с URP/Editor аллоцирует мегабайты и в контракт не входит; поле `gcAllocBytes` в метриках — sentinel по числу blocks, а не байты.
  - Артефакты: 6 скриншотов 1080p (`Artifacts/Playtest/01_spawn_and_fog.png` .. `06_interactive_menu.png`), `playtest_metrics.json`, `gc_alloc_callstacks.txt`.

## Last Commit

`5beb1d1` (`docs(current_state): record allocation callstack artifact and close GC residual as test-runner LogScope artifact`). M1 remediation 2026-10-27 идёт поверх него **без коммита** — на ревью Координатору.

## Tests

- EditMode: **1351/1351 passed** (100% green, 0 failed, 0 skipped, 2026-10-27; gate: `Artifacts/TestResults/m1-remediation-editmode-postbite.xml`)
- PlayMode: **22/22 passed** (100% green, 0 failed, 0 skipped, 2026-10-27; gate: `Artifacts/TestResults/m1-remediation-playmode.xml`)
- Всего: **1373 автоматизированных теста** (1351 EditMode + 22 PlayMode)
- Zero-GC (scoped): 0 блоков GC.Alloc в клиентском транспорте и пайплайнах репликации при отключённой презентации; полный кадр не входит в контракт
- 6 визуальных артефактов Full HD 1080p в `Artifacts/Playtest/`
- Unity: `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0`

## Completed Milestones

- **M0 Technical Foundation — COMPLETE**
- **Phase 1 Simulation Foundation — COMPLETE**
  - Спринт 1.1 (`5b16a05`): pause drain, packet overflow, zero-GC sort, selection cap.
  - Спринт 1.2 (`93b465e`): zero-GC formation slots, unified spawn validation, archetype forward.
  - Спринт 1.3 (`2b275a5`): 2v2 team hostility, friendly fire suppression, team victory outcome.
  - Спринт 1.4 (`e4c73bb`): de-globalize CombatTickResolver instance scratch, thread-safety, Zero-GC.
  - Спринт 1.5 (`3e413a7`): final hardening, reentrancy guard, friendly target rejection, zero-alloc snapshots, physical unit cap.
  - Спринт 1.5.1 (`349a074`): atomic concurrency guard, capacity validation hoisting, instant victory command flush.
- **Phase 2.1 Server-Owned Match State — COMPLETE (`8178110`)**
- **Phase 2.2 Command Channel — COMPLETE (`313e9ef`)**
- **Phase 2.3 Server Tick Driver — COMPLETE (`ea0435d`, ADR-007)**
- **Phase 2.4 Session / Player Identity — COMPLETE (`73d1276`, ADR-008)**
- **Phase 2.5 Network Transport — COMPLETE (ADR-009; LiteNetLib 1.3.5 за контрактом `INetworkCarrier`)**
- **Phase 2.6 Snapshot Networking — COMPLETE (ADR-010; 599/599 EditMode, 6/6 PlayMode)**
- **Phase 2.7 Reconnect & Resync — COMPLETE (ADR-008..ADR-010; 641/641 EditMode, 6/6 PlayMode)**
- **Phase 2.8 Network Prototype Playtest (2v2) — COMPLETE (Steps 2.8.1–2.8.3)**
- **Phase 2.0 Hardening Sprints 2.1–2.5 & Certification — COMPLETE (`4cb8ffa`, P0/P1 = 0)**
- **M2 Playable RTS Core — COMPLETE (Phase 3.0)**
  - Phase 3.1 RTS Camera & Input — COMPLETE (`59ef38a` → `8144541`, `2b71414`)
  - Phase 3.2 UnitViewTickBuffer & Interpolation — COMPLETE (`6ab12ad` → `eac0e1d`)
  - OD-29 UnitKind Replication & UnitCatalog — COMPLETE (`be03002`, `212740a`, `27a10f6`)
  - Phase 3.3 UnitViewBinder & Object Pooling — COMPLETE (`bcb1e78` → `0243971`, `e69bed1`)
  - Phase 3.4 Instanced Selection Rings & HP Bars — COMPLETE (`27a10f6` → `b897eea`)
  - Phase 3.5 Selection System, Screen-Space Drag-Box & RTS Command Issuing — COMPLETE (`cfa24c2`)
  - Phase 3.6 Minimap & Tactical HUD — COMPLETE (`2a754db`)
  - Phase 3.7 Vertical Slice Integration, 400-Unit Benchmark & Multi-Frame Suite — COMPLETE (`3414ba6`, `cb01ea2`)
- **Hardening Phase 3.7.1 — Grand Audit remediation — COMPLETE (`934840d`..`3a112ad`)**
- **Milestone M1 Multiplayer Prototype — [1v1 SCOPE, REMEDIATION DELIVERED / PENDING REVIEW] (Phase 4.0, ADR-014, OD-35..OD-43, OD-48)**
  - Шаги 4.1–4.6.5 реализованы; M1 remediation 2026-10-27 закрыл блоки 1–5 задания и обновил документацию (блок 6).
  - Сквозной dedicated-матч на 402 юнита, FoW, реальные UDP-сокеты, живой реконнект в разгаре боя, авто-возобновление C0 Opcode 14, 6 скриншотов Full HD 1080p.
  - Заявляемый формат M1 — **1v1 Dedicated Server Prototype**; 2v2 — Milestone M2.

## Next Step

**Независимое ревью и коммит M1 remediation Координатором.** После приёмки — Milestone M2: 2v2 composition (ex-P1-02) и Phase 5 Faction Asymmetry & Strategic Combat.

## Backlog

- **[OPEN — M2] 2v2 composition:** `DedicatedServerRunner` создаёт `ServerHost(tickRate)` с `SkirmishCombatRelation`; `Team2v2CombatRelation` и `SetPlayerTeam` не подключены runtime-композицией. Официально вынесено в Milestone M2 (OD-48).
- **[OPEN — audit residuals, не входили в M1 remediation]:** P2-02 (receive может воскресить state закрытого connection), P2-05 (новый disconnect во время паузы наследует остаток старого grace), P2-08 (countdown не реплицируется всем клиентам), P2-10 (bootstrap-owned `RemoteSliceSession` не закрывается при уничтожении root), P2-11 (приёмочный E2E-сценарий не покрывает real-entry-point до outcome), P2-12 (GC-gate нестабилен и уже заявленного охвата), P3-01..P3-04.
- **[OPEN] (Phase 4.4.2 lead ceiling residual)**: `CalculateLeadTicks` реализован точно по формуле брифа и не клампится. При RTT выше ~4.5 с (`1 + 12 render + 2 + ceil(RTT/100ms) > 60`) `RequestedTick` выйдет за `AcceptedFutureTicks` и сервер ответит `HeaderRejected`; для LAN/обычного интернета запас велик (RTT 100 мс → lead ≈ 8 тиков).
- **[CLOSED] (Phase 4 FoW)**: per-client `StateChecksum` для отфильтрованного среза мира реализован в Шаге 4.3.2 (OD-40); глобальная чексумма остаётся только для режима без фильтра.
- **[CLOSED] Backlog Phase 2.0 (N-02, N-03, N-04) и P3-пакет Grand Audit (F-1..F-11, F-13..F-15; ID F-12 в отчёте отсутствует)**: закрыты в Hardening Phase 3.7.1 (блоки 1–5). Это не отменяет OPEN-замечаний Grand Audit M1, перечисленных выше.
- **[CLOSED] (Phase 4.4.1 HUD residual)**: `TacticalHudText` теперь содержит `"Target in fog"` для `MatchCommandRejection.TargetInFogOfWar`; закрыто в Шаге 4.4.2.
- **[CLOSED for dedicated / PARTIAL for slice] (Phase 4.4.1 runner residual)**: `DedicatedServerRunner` теперь устанавливает включённый `FogOfWarReplicationFilter` (`step454`); `TacticalVerticalSliceRunner` подключает фильтр, но держит его выключенным в локальном loopback: армии демо спавнятся в 80 м при радиусе обзора 20 м.
- **[CLOSED] (Phase 4.5.1 reconnect loop residual)**: `ClientTransportEndpoint.Reconnect` + `RemoteSliceSession.TryReconnect` создают заменяющий сокет и отправляют C0-запрос с кэшированным секретом; токен приходит в `ReconnectResponse` и усыновляется endpoint'ом. Авто-ретрай каждые 500 мс в `Pump`, таймаут 2000 мс.
- **[CLOSED] (Phase 4.5.3 reconnect token residual)**: `ReconnectResponse.Token` (байты 21..28) доносит токен ребинда до `ClientTransportEndpoint`, который назначает его carrier'у.
- **[CLOSED] (Phase 4.6.4 / 4.6.5 polish)**: Polish Sprint закрыл resync колец выделения (`ResyncSelectionViews()`), единый 124px столбец значений HUD, боевую убыль HP (74% и 50%), интерактивную точку входа `GlobalFront/Playtest/Launch 400-Unit Dedicated Match`, Zero-GC буферный пул транспорта (`TransportBufferPool.cs`) и стабилизацию таймаутов `NetworkImpairmentPlayModeTests.cs`.
- **[CLOSED / PROVEN HARNESS RESIDUAL] (Full-Suite LogScope GC residual)**: при прогоне полного пакета из 22 PlayMode-тестов в редакторе периодически фиксировались 1–8 блоков GC.Alloc в Recorder; прицельная диагностика аллокационных стек-трейсов (`Profiler.enableAllocationCallstacks`, артефакт `Artifacts/Playtest/gc_alloc_callstacks.txt`) доказала, что ни одна аллокация не принадлежит коду `GlobalFront.*` или `LiteNetLib.*`: все разрешённые стеки принадлежат `UnityEngine.TestRunner` (`LogScope.ProcessExpectedLogs`/`EvaluateLogScope`), Editor (`PlayModeView.RenderView`) и URP/IMGUI. Пул буферов транспорта полностью чист (`poolMisses = 0`). Это артефакт измерительной обвязки редактора/харнесса, а не игровой симуляции или транспорта. **Граница заявления:** scoped-окно измеряет только клиентский транспорт и пайплайны репликации при отключённой презентации; полный кадр с URP/Editor в контракт не входит.

## Important Constraints

- Current runtime: `ServerHost` (engine-independent, `GlobalFront.Server`) владеет `MatchServer`, `TickDriver` и `SessionManager`; `LocalMatchHost` — клиентская обёртка, добавляющая `BindReconnectCoordinator`. Dedicated-серверная обвязка живёт в отдельной сборке `GlobalFront.ServerHost` и не протекает в Core/Server.
- Server tick lifecycle отделён от Unity client lifecycle (ADR-007). Session/player identity реализованы как server-authoritative layer (ADR-008): PlayerId назначается только сервером, command ingress проходит session gate.
- Сетевой транспорт реализован по ADR-009 (OD-1 = LiteNetLib за контрактом `INetworkCarrier`) и сертифицирован.
- **Headless transport pump (F-10):** `ServerTransportHost.Pump` имеет продакшн call-site в `DedicatedServerRunner.Update` (Phase 4, Шаг 4.1); вертикальный срез использует `LocalReplicationLoopback` по умолчанию и `DedicatedClient` через `RemoteSliceSession` (Шаг 4.2), запуск клиента — флагом `-connect <host>[:<port>]` (Шаг 4.2.1) или через меню `GlobalFront/Playtest/Launch 400-Unit Dedicated Match`.
- Reconnect/resync реализованы и приняты (Phase 2.7, OD-18…OD-22, Step 4.6.4 C0 Opcode 14 `ClientReadyToResume`) с тактической паузой, 5-секундным countdown и авто-возобновлением.
- Презентационный стек Phase 3 (OD-23..OD-29) доказал Zero-GC steady state и 0.623 ms/frame на 400 активных юнитах; scoped-замер сетевого транспорта (клиент + пайплайны репликации, презентация отключена) даёт 0 блоков GC.Alloc, полный кадр в этот контракт не входит.
- Product 1.0 включает пять основных фракций (NATO, Russia, China, GLA, USA); их gameplay/content будет развёрнут в последующих фазах.
- Подфракции не входят в 1.0.
- Не считать будущий roadmap фактом реализации.

## Read Next

- [Phase 04](Phases/Phase_04_Multiplayer_Integration.md)
- [Phase 05](Phases/Phase_05_Faction_Asymmetry.md)
- [Architecture](ARCHITECTURE.md)
- [Project Status](PROJECT_STATUS.md)
