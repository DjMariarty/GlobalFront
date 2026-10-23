# GlobalFront Current State

> Быстрый handoff для AI-агента • обновлено 2026-10-23

## Current Phase

**Phase 1 и Phase 3 — [100% COMPLETED / AUDITED: ZERO DEFECTS] • Phase 2 — [100% COMPLETED / P0/P1 = 0 CERTIFIED; все P2/P3 закрыты]**

## Current Task

**Phase 1, 2 и 3 ЗАВЕРШЕНЫ полностью • Phase 4 (Multiplayer Gameplay Integration) — [ACTIVE / IN PROGRESS]**

- **Phase 1 (Simulation Foundation):** [100% COMPLETED / AUDITED: ZERO DEFECTS]. Спринты hardening 1.1–1.5.1 закрыли concurrency, reentrancy, friendly-fire и snapshot allocations (`5b16a05`..`349a074`).
- **Phase 2 (Multiplayer Foundation):** [100% COMPLETED / P0/P1 = 0 CERTIFIED]. Шаги 2.1–2.8 + спринты hardening 2.1–2.5 (`5c94e07`..`4cb8ffa`). Сертификация Phase 2.0 на `4cb8ffa` подтвердила **P0/P1 = 0**: это строгий состязательный ре-аудит, выполненный Claude на основе коммита `4cb8ffa` (отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`); сам отчёт ограничивает область сертификации P0/P1 и фиксирует открытый на тот момент P2-бэклог (N-02, N-03, N-04) и P3-пакет. Сторонние модели привлекались только на промежуточных этапах ревью и авторами сертификата не являются. Уязвимость N-01 / D-10 / A-01 полностью ликвидирована на 3 независимых слоях. Все оставшиеся замечания (F-1..F-11, F-13..F-15, N-02..N-04) закрыты в Hardening Phase 3.7.1 — см. блоки ниже.
- **Phase 3 (Visual Presentation & RTS Controls):** [100% COMPLETED / AUDITED: ZERO DEFECTS]. Шаги 3.1–3.7 (ADR-012, OD-23..OD-29). Сквозной вертикальный срез на 400 юнитах (`3414ba6` / `cb01ea2`). Замер OD-28: **0.623 ms/frame** — это CPU-пайплайн симуляции и презентации (headless EditMode, 400 юнитов, гард 8.0 ms), а не полный кадр с физическим GPU draw-call; **0 B GC.Alloc** за 60 steady-state кадров.
- **Hardening Phase 3.7.1 (закрытие Grand Audit):** [100% COMPLETED]. Блоки 1–4 (`934840d`..`3a112ad`) закрыли code-level замечания F-1..F-11, F-13..F-15 и N-02..N-04; блок 5 закрыл F-6 и F-10. 17 новых EditMode-тестов (1162 → 1179), bite-прогоны по каждому наблюдаемому дефекту.
- **Phase 4 (Multiplayer Gameplay Integration):** [ACTIVE / IN PROGRESS]. ADR-014 принят (OD-35..OD-43, `DECISIONS.md`).
- **Шаг 4.1 (Dedicated Server Core Relocation & Headless Runner):** [COMPLETE / COMMITTED `5f2f29e`]. `ServerHost` вынесен в `GlobalFront.Server` (engine-free), `LocalMatchHost` остался тонкой клиентской обёрткой с `BindReconnectCoordinator`; добавлена сборка `GlobalFront.ServerHost` с `DedicatedServerRunner` (цикл `Pump` → `AdvanceRealTime`, 120 FPS) и `DedicatedServerBootstrap` (`-port`, `-tickrate`, `-dedicatedServer` / `UNITY_SERVER`); в `FixedStepClock` добавлен кламп бэклога `MaxBacklogSeconds = 0.25` (OD-45).
- **Шаг 4.2 (Client Remote Connection & Handshake):** [COMPLETE / COMMITTED `1760f17`]. `ClientTransportEndpoint` отправляет `Ping` и публикует `PongReceived`; `RemoteSliceSession` компонует endpoint, uplink, receiver/bridge, C1 command channel и RTT-контур (каденс 1 Гц, EWMA α = 0.125 по клиентским часам); `TacticalVerticalSliceRunner.InitializeDedicatedClient` работает без локального `_host` (тик — из `ClientReplicationReceiver`, команды — через `NetworkCommandChannel`, пул прогревается по полученному ростеру), RTT и сетевой статус выводятся в `NetworkHudPresenter`.
- **Шаг 4.2.1 (Client Bootstrap CLI `-connect`):** [COMPLETE / COMMITTED `34e994e`]. `GlobalFrontRuntimeBootstrap` разбирает `-connect <host>[:<port>]` и `-connect=<host>[:<port>]` (порт по умолчанию 7777, диапазон 1..65535; отсутствие флага/пустой хост/невалидный порт → `false`). При наличии флага `Initialize()` не строит легаси-прототип, а вызывает `ActivateDedicatedClient`: снимает легаси-прототип и предыдущий срез, создаёт `RemoteSliceSession`, подключается и инициализирует раннер в `DedicatedClient`; сессией владеет бутстрап и освобождает её при замене среза. Гейт: EditMode **1219/1219**, PlayMode **17/17**; live PlayMode-проверка с реальным UDP-сервером: `Established`, RTT 28–29 ms, HUD-слот `Connected`.
- **Шаг 4.3.1 (Core Visibility Engine & Visibility History Ring):** [COMPLETE / COMMITTED `e5d2ec0`]. `GlobalFront.Core.Visibility`: `BitMask1024` (ровно 128 B, 16 `ulong`, bounds-checked set/Clear/IsSet, `Or`/`And`/`AndNot`, SWAR popcount), `VisibilityEngine` (uniform grid 8192 мм: плотный прямоугольник для компактных сцен + hash fallback для разреженных, мемоизация полностью видимых ячеек, свои и союзники видны безусловно, враги — по радиусу 20 м, команды через `SetPlayerTeam`, zero-GC) и `GlobalFront.Server.Replication.VisibilityHistoryRing` (120 тиков × 10 игроков, forward-only, вытеснение, бинарный поиск, zero-GC). Вход движка — engine-free `VisibilityUnitSnapshot`: `ServerUnitSnapshot` живёт в `GlobalFront.Server`, на который Core ссылаться не может. 39 новых EditMode-тестов. Гейт: EditMode **1258/1258**, PlayMode **17/17**; 1024 юнита — **0.694 ms** median за расчёт в финальном гейте (0.709 ms focused; минимум по прогонам 0.398 ms), гард 1.0 ms, 0 B GC.Alloc. Bite-прогоны: 9 / 4 / 1 падений. `ServerReplicationEmitter`, `MatchServer`, `ClientReplicationWorld` не изменялись.
- **Шаг 4.3.2 (Per-Client Replication Filtering & StateChecksum, OD-39/OD-40):** [COMPLETE / COMMITTED `3666834`]. `IReplicationFilter` + `FogOfWarReplicationFilter` (VisibilityEngine + VisibilityHistoryRing, один преаллоцированный `VisibilityUnitSnapshot[1024]`, сущности вне 1..1024 игнорируются и считаются в `IgnoredUnitCount`, zero-GC). `ServerReplicationEmitter`: `PlayerId` в `ClientState`, фильтрация кейфреймов по `V_tick`, per-client дельты по переходам `V_base → V_cur` (`0→1` ADD из текущего снимка, `1→0` REMOVE `FogOfWarHidden`/`Destroyed`, `1→1` слитые UPDATE, `0→0` полный пропуск), fallback на кейфрейм при `encodedSize > MaxPayloadBytes (8192)` или вытесненной маске, фильтрованный `DeltaResume`, per-client FNV-1a чексумма по возрастанию `EntityId`. `ServerHost.AttachReplication` принимает `IReplicationFilter` (3- и 4-аргументные overload'ы). 12 новых EditMode-тестов. Гейт: EditMode **1270/1270**, PlayMode **17/17**, 0 warnings / 0 errors. Bite-прогоны: anti-maphack (`Expected: 0 But was: 45`), remove cause (`Expected: FogOfWarHidden But was: Destroyed`), per-client checksum (`Expected: 0 But was: 2`).
- **Шаг 4.4.1 (Server FoW Target Validation & Command Rejection Gates, OD-41):** [IMPLEMENTED / UNCOMMITTED on `3666834`]. `MatchCommandRejection.TargetInFogOfWar = 12`; `MatchServer` получил `IReplicationFilter _visibilityFilter` (+ `VisibilityFilter`, `SetVisibilityFilter`, ctor-overload) и anti-maphack гейт в `TryEnqueueAttack` сразу после `FriendlyTarget`: цель, невидимая для `header.Player` на `CurrentTick`, отклоняется до `Schedule`/`CommitSequence`. Null или `IsEnabled == false` — passthrough, поэтому все прежние вызовы не меняют поведение. `ServerHost.AttachReplication` устанавливает тот же фильтр в `_server`, наружу добавлен `ServerHost.VisibilityFilter`. 9 новых EditMode-тестов. Гейт: EditMode **1279/1279**, PlayMode **17/17**, 0 errors. Bite-прогоны: `step441-bite1-fow-target.xml` (`Expected: TargetInFogOfWar But was: None`), `step441-bite2-ownership.xml` (`Expected: NotEntityOwner But was: None`).
- **Следующий шаг:** верификация diff Шага 4.4.1 координатором; далее — следующие шаги Phase 4 по Rev.2.

## Last Commit

`3666834` (`feat(replication): implement per-client FoW filtering, delta transitions, and StateChecksum (Step 4.3.2)`) + uncommitted Step 4.4.1 diff

## Tests

- EditMode: **1279/1279 passed** (100% green, 0 failed, 0 skipped, 2026-10-23; gate: `Artifacts/TestResults/step441-editmode-final.xml`)
- PlayMode: **17/17 passed** (100% green, 0 failed, 0 skipped, 2026-10-23; единый прогон полного suite; gate: `Artifacts/TestResults/step441-playmode-final.xml`)
- Всего: **1296 автоматизированных тестов** (100% green; +9 EditMode к базовым 1270)
- Step 4.4.1 focused (`MatchServerCommandValidationTests`): **9/9** (fog-отказ без постановки в расписание и без списания sequence, visible-приём с постановкой/списанием, disabled passthrough, null passthrough, чужой атакующий, мёртвый атакующий, мёртвая цель, friendly target, проброс фильтра через `ServerHost`); артефакт `Artifacts/TestResults/step441-focused-editmode.xml`
- Step 4.3.2 focused (`FogOfWarReplicationTests`): **12/12** (Reveal, Hide, Destroyed, anti-maphack, packet-loss resume, per-client checksum, zero-GC 60 тиков, payload fallback, disabled filter, range safety, ServerHost overload); zero-GC: 60 тиков фильтрованного пайплайна — 0 B GC.Alloc
- Bite-прогоны 4.4.1: `step441-bite1-fow-target.xml` (гейт закомментирован → `Expected: TargetInFogOfWar But was: None`), `step441-bite2-ownership.xml` (проверка владельца закомментирована → `Expected: NotEntityOwner But was: None`)
- Bite-прогоны 4.3.2: `step432-bite1-antimaphack.xml` (дельты без фильтра → `Expected: 0 But was: 45`), `step432-bite2-remove-cause.xml` (причина удаления всегда `Destroyed` → `Expected: FogOfWarHidden But was: Destroyed`), `step432-bite3-per-client-checksum.xml` (глобальная чексумма → `Expected: 0 But was: 2`)
- Bite-прогоны 4.2.1: `step421-bite1-connect.xml` (forced `false` → 4 падения, `Expected: True But was: False`), `step421-bite2-endpoint-split.xml` (сломанный split → 5 падений), `step421-bite3-activation.xml` (`LocalLoopback` вместо `DedicatedClient`)
- Компиляция: 0 warnings, 0 errors
- OD-28 (400 юнитов): **0.623 ms/frame** — среднее CPU-время тика симуляции и презентационного кадра за 120 итераций в headless EditMode (`createVisuals: false`), гард 8.0 ms, потолок 60 FPS 16.67 ms; это не замер полного кадра с GPU draw-call; **0 B GC.Alloc** за 60 steady-state кадров и 60 loopback-тиков
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
  - Step 2.6.1 Core Delta Wire Codec — COMPLETE
  - Step 2.6.2 Server Replication Engine & History Ring — COMPLETE
  - Step 2.6.3 Client Replication Receiver & FSM — COMPLETE (`c49eeda`)
  - Step 2.6.4 Full Integration & Impairment Tests — COMPLETE
- **Phase 2.7 Reconnect & Resync — COMPLETE (ADR-008..ADR-010; 641/641 EditMode, 6/6 PlayMode)**
  - Step 2.7.1 Core Wire Codecs & Model — COMPLETE
  - Step 2.7.2 Server Session Re-attachment & Defect Fixes D1-D6 — COMPLETE
  - Step 2.7.3 Client Reconnect Coordinator & FSM — COMPLETE
  - Step 2.7.4 End-to-End Integration, Tactical Pause & Stress Tests — COMPLETE
- **Phase 2.8 Network Prototype Playtest (2v2) — COMPLETE (Steps 2.8.1–2.8.3)**
- **Phase 2.0 Hardening Sprints 2.1–2.5 & Certification — COMPLETE:**
  - Спринт 2.1 (`5c94e07`): socket hijack guard, O(k) grace sweep, rate limiter and pending ack lifecycle cleanup.
  - Спринт 2.2 (`ceae9a8`): Zero-GC send scratch buffer and receive stale-scan pooling in LiteNetLibCarrier.
  - Спринт 2.3 (`750218c`): canonical PartIndex keyframe assembly and slice timeout aging.
  - Спринт 2.4 (`e6d91ea`): thread-safe tick driver step capture (`baseTick = _tick - (ulong)scheduled` под `_gate`, лок на `Tick => _tick`) и bounded fragment reassembly maps (`SeqByMessageId` и `SnapshotSeqByMessageId` ограничены до `MaxFragmentGroups` (4) с eviction по старому sequence).
  - Спринт 2.5 (`4cb8ffa`): exclusive session-socket binding and cross-session reconnect hijack guard (N-01 / D-10 / A-01). Session-aware guard в `ServerTransportHost.cs:333-356` (`bound.Session != request.SessionId -> Denied`), эксклюзивность в `TransportSessionBinder.Bind`, токен-идентичность в `TransportSessionBinder.Release`. 3 новых регрессионных теста в `TransportFlowAndSecurityTests.cs`.
  - **Phase 2.0 Grand Re-Audit (`4cb8ffa`):** сертификация подтвердила **P0/P1 = 0**. Строгий состязательный ре-аудит выполнен Claude на основе коммита `4cb8ffa` (отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`); отчёт ограничивает область сертификации P0/P1 и фиксирует открытый на тот момент P2-бэклог (N-02, N-03, N-04) и P3-пакет. Сторонние модели участвовали только в промежуточных этапах ревью.
- **M2 Playable RTS Core — COMPLETE (Phase 3.0)**
  - Phase 3.1 RTS Camera & Input — COMPLETE (`59ef38a` → `8144541`, `2b71414`) — [APPROVED: ZERO DEFECTS]
  - Phase 3.2 UnitViewTickBuffer & Interpolation — COMPLETE (`6ab12ad` → `eac0e1d`) — [APPROVED: ZERO DEFECTS]
  - OD-29 UnitKind Replication & UnitCatalog — COMPLETE (`be03002`, `212740a`, `27a10f6`) — [APPROVED: ZERO DEFECTS]
  - Phase 3.3 UnitViewBinder & Object Pooling — COMPLETE (`bcb1e78` → `0243971`, `e69bed1`) — [APPROVED: ZERO DEFECTS]
  - Phase 3.4 Instanced Selection Rings & HP Bars — COMPLETE (`27a10f6` → `b897eea`) — [APPROVED: ZERO DEFECTS]
  - Phase 3.5 Selection System, Screen-Space Drag-Box & RTS Command Issuing — COMPLETE (`cfa24c2`) — [APPROVED: ZERO DEFECTS]
  - Phase 3.6 Minimap & Tactical HUD — COMPLETE (`2a754db`) — [APPROVED: ZERO DEFECTS]
  - Phase 3.7 Vertical Slice Integration, 400-Unit Benchmark, Multi-Frame PlayMode Suite & Architecture Invariants Gate — COMPLETE (`3414ba6`, `cb01ea2`) — [APPROVED: ZERO DEFECTS]
- **Phase 4.2 Client Remote Connection & Handshake — COMPLETE (`1760f17`):**
  - `ClientTransportEndpoint.TrySendPing` / `PongReceived` и ветка `Pong` в `HandleMessage` (C0, `long` timestamp клиента).
  - `SliceNetworkMode` (`LocalLoopback` = 0 по умолчанию, `DedicatedClient` = 2) и `RemoteSliceSession` — единый владелец endpoint/uplink/receiver/bridge/command channel, пинг раз в 1000 мс, `sampleRtt = clientNow - timestamp`, EWMA 0.875/0.125.
  - `TacticalVerticalSliceRunner.InitializeDedicatedClient`: `Host == null`, `CurrentTick` из `ClientReplicationReceiver.LastAppliedTick`, команды через `NetworkCommandChannel`, прогрев пула по полученному baseline-ростеру, защита локальных путей (`StepSimulationTick`, `IssueConvergenceMarch`/`IssueEngagementOrders`) от dedicated-режима.
  - RTT и `SlotStatus` уходят в `NetworkHudPresenter`; `CurrentTick + 1 + ceil(RTT/2 / 50 ms)` как запрошенный тик команды.
  - Bite-прогоны: `step42-bite1-pong.xml` (RTT sample 0 вместо 40), `step42-bite2-isolation.xml` (`Host` не null), `step42-bite3-command.xml` (сервер получил 0 команд).
- **Phase 4.2.1 Client Bootstrap CLI `-connect` — COMPLETE (`34e994e`):**
  - `GlobalFrontRuntimeBootstrap.TryParseConnectEndpoint`: форматы `-connect host[:port]` и `-connect=host[:port]` (case-insensitive), порт по умолчанию 7777, валидация 1..65535, `LastIndexOf(':')` сохраняет IPv6-литерал.
  - `GlobalFrontRuntimeBootstrap.ActivateDedicatedClient`: снимает легаси-прототип и предыдущий срез, создаёт `RemoteSliceSession`, вызывает `Connect`, затем `TacticalVerticalSliceRunner.InitializeDedicatedClient(session, createVisuals, autoStartRealTime: true)`; предыдущая bootstrap-сессия освобождается при замене среза.
  - `Initialize()` при наличии `-connect` уходит в dedicated-ветку и не создаёт одиночный легаси-прототип.
  - 9 новых EditMode-тестов (разбор аргументов, dedicated-режим, снятие прототипа, замена среза); live PlayMode-проверка с реальным UDP-сервером на 7777: `NetworkMode=DedicatedClient`, `Host=null`, `Established`, игрок назначен, HUD-слот `Connected`, RTT EWMA 28–29 ms, тики идут с 20 Гц.
  - Bite-прогоны: `step421-bite1-connect.xml`, `step421-bite2-endpoint-split.xml`, `step421-bite3-activation.xml`.
- **Phase 4.3.1 Core Visibility Engine & Visibility History Ring — COMPLETE (`e5d2ec0`):**
  - `Assets/_GlobalFront/Runtime/Core/Visibility/BitMask1024.cs`: 1024-битная маска (16 `ulong`, 128 B), `Set`/`Clear`/`IsSet` с проверкой 0..1023, `ClearAll`, `IsEmpty`, `PopCount` (SWAR — Unity-профиль не отдаёт `System.Numerics.BitOperations`), `Or`/`And`/`AndNot`, value-equality.
  - `Assets/_GlobalFront/Runtime/Core/Visibility/VisibilityEngine.cs`: uniform grid 8192 мм; плотный прямоугольник (≤ 128×128 ячеек) с прямым индексом для компактных сцен и hash fallback с открытой адресацией для разреженных миров; мемоизация «ячейка полностью видима»; свои и союзники (общий `TeamId` через `SetPlayerTeam`) видны безусловно, враги — только в радиусе любого дружественного юнита; per-call штампы исключают повторные `Set`/дистанции. Вход — `VisibilityUnitSnapshot` (engine-free дубль трёх полей `ServerUnitSnapshot`, иначе Core зависел бы от Server).
  - `Assets/_GlobalFront/Runtime/Server/Replication/VisibilityHistoryRing.cs`: 120 тиков × 10 игроков (150 KB), forward-only запись, вытеснение самого старого тика, бинарный поиск, `TryGetVisibility`/`ContainsTick`/`Clear`, index 0 маск-спана игнорируется.
  - 39 новых EditMode-тестов: границы битов, set algebra, дистанция 19.9/20.0/20.1 м, свои/союзники на 500 м, отсутствие маппинга = враг, союзный обзор, 200 юнитов против brute-force, sparse hash fallback, hash-neighbourhood кластер (60 ячеек), 1024 юнита (< 1 ms, median 5×50), zero-GC движка и кольца, ротация 1..130.
  - Bite-прогоны: `step431-bite1-sight-distance.xml`, `step431-bite2-allies-visibility.xml`, `step431-bite3-ring-eviction.xml`.
- **Phase 4.3.2 Per-Client Replication Filtering & StateChecksum — COMPLETE (`3666834`):**
  - `Assets/_GlobalFront/Runtime/Server/Replication/IReplicationFilter.cs`: контракт `IsEnabled` / `UpdateTick(tick, ReadOnlySpan<ServerUnitSnapshot>, count)` / `TryGetVisibilityMask(tick, player, out mask)` / `IsVisible(tick, player, entity)`.
  - `Assets/_GlobalFront/Runtime/Server/Replication/FogOfWarReplicationFilter.cs`: владеет `VisibilityEngine` + `VisibilityHistoryRing`; `UpdateTick` копирует снимок в преаллоцированный `VisibilityUnitSnapshot[1024]`, считает маски в `BitMask1024[MaxPlayers + 1]` и пишет их в кольцо; сущности с id 0 или > 1024 пропускаются и считаются в `IgnoredUnitCount` (fail-closed, без исключений); `IsEnabled` можно выключать — эмиттер уходит на глобальный поток; passthrough `SetPlayerTeam`/`ClearPlayerTeams`.
  - `ServerReplicationEmitter`: `ClientState.Player` заполняется в `OnSessionAttached`/`OnSessionReattached`; `StartKeyframe` стейджит только `V_tick` (при отсутствии маски — пустой baseline, fail-closed); `StreamDeltas`/`DeltaResume` строят per-client дельту по `V_base → V_cur` (ADD/REMOVE/UPDATE/пропуск), причина `FogOfWarHidden` определяется наличием сущности в текущем capture, `Destroyed` — отсутствием; лимит `MaxPayloadBytes (8192)` и вытеснение маски дают `QueueKeyframe`; `StateChecksum` считается per-client по возрастанию `EntityId` (FNV-1a, те же термы, что у клиента).
  - `ServerHost.AttachReplication` — overload'ы `(transport, config, filter)` и `(transport, config, source, filter)`; старые вызовы сохраняют глобальный режим.
  - 12 новых EditMode-тестов в `Assets/_GlobalFront/Tests/EditMode/Server/Replication/FogOfWarReplicationTests.cs`; harness подаёт реальные C2-пакеты в `ClientReplicationReceiver` (транспорт — единственный заменённый слой). Гейт: EditMode **1270/1270**, PlayMode **17/17**, 0 warnings / 0 errors.
  - Bite-прогоны: `step432-bite1-antimaphack.xml`, `step432-bite2-remove-cause.xml`, `step432-bite3-per-client-checksum.xml`.
- **Phase 4.4.1 Server FoW Target Validation & Command Rejection Gates (OD-41) — IMPLEMENTED (uncommitted on `3666834`):**
  - `Assets/_GlobalFront/Runtime/Server/MatchServer.cs`: `MatchCommandRejection.TargetInFogOfWar = 12`; `IReplicationFilter _visibilityFilter`, `VisibilityFilter`, `SetVisibilityFilter(IReplicationFilter)` и ctor-overload `MatchServer(ICombatRelation, IReplicationFilter)`; в `TryEnqueueAttack` после `FriendlyTarget` — гейт `IsEnabled && !IsVisible(CurrentTick, header.Player, target)` → `TargetInFogOfWar` до `Schedule` и `CommitSequence`. Null/disabled фильтр — исторический passthrough.
  - `Assets/_GlobalFront/Runtime/Server/ServerHost.cs`: `AttachReplication` устанавливает переданный фильтр в `_server.SetVisibilityFilter(filter)` (только после успешной проверки transport/повторного attach); добавлено свойство `ServerHost.VisibilityFilter`.
  - `Assets/_GlobalFront/Tests/EditMode/Server/MatchServerCommandValidationTests.cs`: 9 тестов — fog-отказ (+доказательства, что команда не попала в расписание и не списала sequence), visible-приём (+постановка/списание), disabled и null passthrough, `NotEntityOwner`, мёртвый атакующий, мёртвая цель, `FriendlyTarget`, проброс фильтра через `ServerHost`. Гейт: EditMode **1279/1279**, PlayMode **17/17**, 0 errors.
  - Bite-прогоны: `step441-bite1-fow-target.xml` (`Expected: TargetInFogOfWar But was: None`), `step441-bite2-ownership.xml` (`Expected: NotEntityOwner But was: None`).
- **Phase 4.1 Dedicated Server Core Relocation & Headless Runner — COMPLETE (`5f2f29e`):**
  - `Assets/_GlobalFront/Runtime/Server/ServerHost.cs` — двигатель-нейтральное ядро хоста (MatchServer + TickDriver + SessionManager + репликация + пауза), без ссылок на `GlobalFront.Client` и `UnityEngine`.
  - `Assets/_GlobalFront/Runtime/Client/LocalMatchHost.cs` — тонкая обёртка над `ServerHost`; сохранены конструкторы, API и клиентский `BindReconnectCoordinator`.
  - `Assets/_GlobalFront/Runtime/ServerHost/` — сборка `GlobalFront.ServerHost` с `DedicatedServerRunner` (120 FPS, `Pump` до тиков) и `DedicatedServerBootstrap`.
  - `FixedStepClock.MaxBacklogSeconds` (0.25 с) — после спайка очередь ограничена, симуляция не отстаёт от wall-clock перманентно; 4 новых EditMode-теста.
  - Bite-прогоны: backlog (2 падения, 4.8 с вместо 0.25 с), runner pump (2 падения, 0 вместо 1), изоляция Server-сборки (CS0246/CS0234 на `UnityEngine`/`GlobalFront.Client`).
- **Hardening Phase 3.7.1 — Grand Audit remediation — COMPLETE:**
  - Block 1 (`934840d`): F-2 (N-04: control-события `Connected`/`Disconnected` не отбрасываются при переполнении очереди; `Shutdown` очищает rate limiters и pending acks), F-4 (телеметрия cross-session denial), F-5 (изоляция исключений на цепочке `Pump → HandleEvent → HandleMessage`), F-13 (порядок eviction при отклонении фрагмента).
  - Block 2 (`a08beaf`): F-3 (N-03 sink parity 512 для add и remove), F-14 (`Owner` и `AutoAcquireEnemies` включены в `StateChecksum`), F-15 (согласование серверного и клиентского порядка сущностей), F-9 (pacing-комментарии приведены к константам).
  - Block 3 (`a944623`): F-1 (zero-alloc обновление `ClientSession` и живой GC-гейт), N-02 (`ArrayPool` и group stack pool в `FragmentAssembler`: устранены `3+N` аллокаций), мёртвые allocation-метры заменены.
  - Block 4 (`3a112ad`): F-11 (инъекция transport/uplink seam в раннер), F-7 (регистрация URP RenderGraph overlay feature и handoff), F-8 (дисклеймер для OD-28 0.623 ms/frame как CPU-замера презентации).
  - Block 5: F-6 (атрибуция аудита) и F-10 (статус headless transport pump) — документация синхронизирована, сертификация закрытия всех замечаний.

## Next Step

**Phase 4 (Multiplayer Gameplay Integration) — [ACTIVE]**: Шаг 4.4.1 реализован (серверный anti-maphack гейт `TargetInFogOfWar` для атакующих приказов, проброс фильтра через `ServerHost`) и ожидает верификации координатором; следующие шаги Phase 4 — по Rev.2.

## Backlog

- **[CLOSED] (Phase 4 FoW)**: per-client `StateChecksum` для отфильтрованного среза мира реализован в Шаге 4.3.2 (OD-40); глобальная чексумма остаётся только для режима без фильтра.
- **[CLOSED] Backlog Phase 2.0 (N-02, N-03, N-04) и P3-пакет Grand Audit (F-1..F-11, F-13..F-15; ID F-12 в отчёте отсутствует)**: закрыты в Hardening Phase 3.7.1 (блоки 1–5). Открытых замечаний аудита нет.
- **[OPEN] (Phase 4.4.1 residual)**: продакшн-раннеры (`DedicatedServerRunner`, `TacticalVerticalSliceRunner`) пока не устанавливают `FogOfWarReplicationFilter`, поэтому OD-41-гейт латентен в рантайме; `TacticalHudText` не содержит строки для `TargetInFogOfWar` (HUD покажет "Unrecognised").

## Important Constraints

- Current runtime: `ServerHost` (engine-independent, `GlobalFront.Server`) владеет `MatchServer`, `TickDriver` и `SessionManager`; `LocalMatchHost` — клиентская обёртка, добавляющая `BindReconnectCoordinator`. Dedicated-серверная обвязка живёт в отдельной сборке `GlobalFront.ServerHost` и не протекает в Core/Server.
- Server tick lifecycle отделён от Unity client lifecycle (ADR-007). Session/player identity реализованы как server-authoritative layer (ADR-008): PlayerId назначается только сервером, command ingress проходит session gate.
- Сетевой транспорт реализован по ADR-009 (OD-1 = LiteNetLib за контрактом `INetworkCarrier`) и сертифицирован.
- **Headless transport pump (F-10):** `ServerTransportHost.Pump` имеет продакшн call-site в `DedicatedServerRunner.Update` (Phase 4, Шаг 4.1); вертикальный срез использует `LocalReplicationLoopback` по умолчанию и `DedicatedClient` через `RemoteSliceSession` (Шаг 4.2), запуск клиента — флагом `-connect <host>[:<port>]` (Шаг 4.2.1).
- Reconnect/resync реализованы и приняты (Phase 2.7, OD-18…OD-22) с тактической паузой и 5-секундным countdown.
- Презентационный стек Phase 3 (OD-23..OD-29) доказал Zero-GC steady state и 0.623 ms/frame на 400 активных юнитах.
- Product 1.0 включает пять основных фракций (NATO, Russia, China, GLA, USA); их gameplay/content будет развёрнут в последующих фазах.
- Подфракции не входят в 1.0.
- Не считать будущий roadmap фактом реализации.

## Read Next

- [Phase 04](Phases/Phase_04_Multiplayer_Integration.md)
- [Phase 02](Phases/Phase_02_Multiplayer.md)
- [Architecture](ARCHITECTURE.md)
- [Project Status](PROJECT_STATUS.md)
