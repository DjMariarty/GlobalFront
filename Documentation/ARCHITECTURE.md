# Архитектура GlobalFront

> Живой нормативный документ • current baseline + approved target • обновлено 2026-09-23

## Architectural Principle

GlobalFront строится вокруг authoritative server и deterministic simulation. Product goal — сохранить multiplayer experience Generals/Zero Hour при современной реализации command channel, snapshots, resync, dedicated server и desync diagnostics.

Будущая целевая система не считается реализованной до кода, тестов и обновления фактического статуса.

## Current Runtime Boundaries

| Assembly | Current responsibility | Dependencies |
|---|---|---|
| `GlobalFront.Core` | детерминированные identifiers (`EntityId`, `PlayerId`, opaque `SessionId`/`MatchId`), commands, coordinates, movement, combat, formation, `MatchConfig`, constants | none; Unity API запрещён |
| `GlobalFront.Server` | authoritative `MatchServer`, state, validation, tick phases, `TickDriver` (server tick scheduling), snapshots и Snapshot Protocol v1, session/player identity (`SessionManager`, Phase 2.4) | `GlobalFront.Core`; Unity API запрещён |
| `GlobalFront.Client` | Unity input/presentation, selection, `RtsCameraController`, `RtsInputManager`, `UnitViewTickBuffer`, `UnitCatalog`, HUD, bootstrap, `LocalMatchHost` (ServerHost: владеет `MatchServer`, `TickDriver` и `SessionManager`), command channel adapter, `ClientSession` | Core, Server, Unity/Input System |

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

## Verification Baseline

- Unity `6000.6.2f1`, URP `17.6.0`
- 711/711 EditMode passed (100%)
- 6/6 PlayMode passed (100%)
- last committed milestone: `bcb1e78` (Phase 3.3 UnitViewBinder & Object Pooling, ADR-012/OD-26)
- Фазы 1.0–2.8 заморожены как сертифицированный бейзлайн (`a37f53d`, Grand Audit APPROVED: ZERO DEFECTS)

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Project Status](PROJECT_STATUS.md)
- [Decisions](DECISIONS.md)
- [Phase 02](Phases/Phase_02_Multiplayer.md)