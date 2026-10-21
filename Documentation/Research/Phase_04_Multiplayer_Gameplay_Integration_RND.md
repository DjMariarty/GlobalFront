# Phase 4 R&D — Multiplayer Gameplay Integration (GlobalFront)

> Статус: **R&D DRAFT** (вход для ADR-014) • Дата: 2026-10-21 • Метод: анализ кодовой базы на `3a112ad`-бейзлайне (1195 тестов) + официальная документация Unity 6 (6000.6) + индустриальные практики (SC2 / AoE / SupCom / PA / Spring-BAR / Quake3 / Source).
>
> Правило проекта: этот документ — этап **R&D**; решения фиксируются в `DECISIONS.md` как **ADR-014** только после ревью. Здесь формулировки «предлагаю» равнозначны «кандидат в ADR».

---

## 0. Верификация кодовой базы: подтверждённые факты и расхождения с брифом

Прежде чем проектировать — что реально есть в коде. Всё ниже проверено чтением файлов, не по памяти.

### 0.1 Что уже существует и переиспользуется (сильный фундамент)

| Компонент | Файл | Факт |
|---|---|---|
| Тикер | `Runtime\Server\TickDriver.cs` | `TickDriverMode {Manual, RealTime}`, `AdvanceRealTime(double)`, bounded catch-up `MaxCatchUpTicksPerFrame = 4`, `Pause()/Resume()`, event `TickDue(ulong)`, backlog не теряется |
| Серверная симуляция | `Runtime\Server\MatchServer.cs` | `TryEnqueueMove/Attack/Stop`, окно `AcceptedPastTicks = 20`, `AcceptedFutureTicks = 60`, два-фазовый тик через хост, `CopySnapshots(Span<ServerUnitSnapshot>)` zero-alloc |
| Сессии | `Runtime\Server\Sessions\SessionManager.cs` | `SessionSecret32` (32 байта, `RandomNumberGenerator`), `ValidateCommand` (гейт: UnknownSession → SessionClosed → NoBoundMatch → SessionNotConnected → PlayerMismatch → MatchNotRunning), `TryRebindSession` (fixed-time сравнение секрета), grace `DefaultDisconnectGraceTicks = 4000` (200 с), PlayerId монотонный и не переиспользуется |
| Транспортный хост | `Runtime\Server\Transport\ServerTransportHost.cs` | `Pump(long nowMs)` и `Pump(long nowMs, ulong drainTick)`; полный хэндшейк уже реализован (`ConnectRequest` 21 байт → `ConnectAccept` 41 байт → `SessionSecretMessage` 33 байта, opcode 9), гейт команд (`SessionManager.ValidateCommand` → dispatch), CommandAck 7 байт, реконнект opcode 12 (64 байта) → `ReconnectResponse` 92 байта, анти-хайджек (1 сокет = 1 сессия), rate limiter |
| Носители | `OwnDatagramCarrier`, `LiteNetLibCarrier` | Единый контракт `INetworkCarrier`; C0+C1 → ReliableOrdered, C2 → Sequenced (у LNL — приложенческая фрагментация снапшотов с 8-байтным префиксом); у LNL свой worker-thread `PollEvents` + `Sleep(1)`, `Pump(nowMs)` — no-op |
| Дельта-протокол | `Core\Snapshot\DeltaSnapshotProtocol.cs` | v2, header 36 B, `MaxPayloadBytes = 8192`, `MaxPacketBytes = 8228`, ADD-запись 40 B; `DeltaFlags.HasChecksum`, **`HasTombstoneEcho` зарезервирован** |
| Ключевые кадры | `Core\Snapshot\KeyframeSliceCodec.cs` | v2, header 24 B, слайс ≤ 16384 B, `KeyframeSeq` монотонный сквозь реконнекты |
| Эмиттер | `Runtime\Server\Replication\ServerReplicationEmitter.cs` | Per-client `LastAckedTick`/`LastSentTick`, pacing-токены (refill 2048 B/тик, burst 16 KB), per-client keyframe staging (4096×40 B, переживает detach), `OnTickCompleted`, `PumpPausedSlices()` для тактической паузы, FNV-1a `StateChecksum` **по всему миру** на 1 Гц |
| История | `ReplicationHistoryRing.cs` | 120 тиков, `TryMergeRange` (базовый тик выселен → false → кейфрейм) |
| Клиент-приём | `Runtime\Client\Replication\ClientReplicationReceiver.cs` + FSM | Окно 120 тиков, guard `Tick > LastAppliedTick && BaseTick <= LastAppliedTick && KeyframeRef == CurrentKeyframeSeq`, 2 подряд несовпадения чексуммы → авто re-baseline, feedback 10 Гц (34 B ack, мин. интервал 100 мс) |
| Клиентский мир | `ClientReplicationWorld.cs` | 4096 слотов, open-addressing, `ComputeStateChecksum()` FNV-1a по возрастанию EntityId, **уже считает и применяет `DeltaRemoveCause.FogOfWarHidden`** |
| Презентация | `Presentation\UnitViewTickBuffer.cs`, `UnitViewBinder.cs`, `UnitViewPool.cs` | SoA 32 тика, адаптивная задержка `delay = 2·interval + 0.5·jitter` clamp [3, 12] тиков, `Flush()/Resync(tick)`, пул `AllowCombatGrowth = false` + `Warmup`, binder: recycling по EntityId, `HandleResync(snapshotTick) = ReleaseAllViews + buffer.Resync` |
| Шов F-11 | `Presentation\TacticalVerticalSliceRunner.cs` | `Initialize(..., IReplicationTransport transport, IReplicationUplink uplink)` — all-or-nothing (`ArgumentException` на половину), тест-пиннед в `TacticalVerticalSliceBenchmarkTests` |
| Тест-транспорт | `Tests\EditMode\TransportTestSupport.cs`, `VirtualNetworkPipe.cs` | `ImpairmentProfile { LossProbability, DuplicationProbability, CorruptionProbability, LatencyMs, ReorderJitterMs, Partitioned, Seed }` — **эмуляция задержки/дропа уже есть**, детерминированная, только через `Advance(ms)`; мульти-клиент = повторные `CreateClient(rig)` (паттерн 2v2 уже есть в `Match2v2PlaytestScenarioTests`) |

### 0.2 Расхождения брифа с реальностью (важно!)

1. **`IReplicationFilter` НЕ существует.** Grep по проекту — 0 совпадений. Это *название из плана Phase 4* (`Phase_04_Multiplayer_Integration.md:31`), а не готовый контракт. Контракт предстоит спроектировать (раздел 3.2). Бриф описывал его как «существующий интерфейс» — это неточность.
2. **Per-client `StateChecksum` НЕ существует.** Чексумма считается в `ServerReplicationEmitter.ComputeStateChecksum()` по *всему миру* и рассылается всем одинаковая. Per-client версий нет нигде.
3. **`LocalMatchHost` живёт в `GlobalFront.Client`** (`Runtime\Client\LocalMatchHost.cs`), а Client asmdef имеет `defineConstraints: ["!UNITY_SERVER"]` → **в Dedicated Server build вся сборка Client не компилируется, включая хост-ядро**. Для шага 4.1 хост-ядро необходимо перенести в `GlobalFront.Server`.
4. **`ServerTransportHost.Pump` не имеет ни одного production call-site** (наследие F-10 из аудита Phase 3: «подключение `ServerTransportHost.Pump` к реальному серверному тикеру — Phase 4, Шаг 4.1»). Подтверждено: `Main()` в проекте нет, dedicated-server entry point нет, Build Profile Dedicated Server на диске отсутствует (только 2 Windows-профиля).
5. **Незакрытые wiring-дыры реконнекта (критично для 4.5):** `ClientReconnectCoordinator.OnKeyframeInstalled()` в runtime-коде клиента не вызывается никем (только тестами); `UnitViewBinder.HandleResync` не подписан ни на что; `NetworkHudPresenter.UpdatePing(PlayerId, int)` существует, но не вызывается — RTT-трекинга на клиенте нет вовсе.
6. **Fog of War:** `DeltaRemoveCause.FogOfWarHidden = 1` поддержан end-to-end на проводе и на клиенте (декод, счётчик `FogOfWarRemoveCount`, освобождение view), но сервер **никогда** его не эмитит (`ServerSnapshotDiffEngine`: «fog-of-war hiding is a filtering concern of a later step»). Модели видимости в Core нет.
7. **UI подключения отсутствует**: нет меню/лобби/join-панели; сессии создаются только in-process. Phase 4 нужен минимальный connect-флоу (IP/порт → `ClientTransportEndpoint.Connect`).
8. **В `ArchitectureInvariantsTests` нет запрета `async`/`Task`** — только LINQ (IL-скан), float-поля в авторитетных типах, `System.Random`/`DateTime`, Guid/крипто-RNG в авторитетных неймспейсах, `[DisallowMultipleComponent]`. Асинхронность кор-кода запрещена конвенцией, но не ассертится — рекомендую добавить тест (7.3).
9. **LiteNetLib 1.3.5 — vendored precompiled DLL** (`Assets\_GlobalFront\ThirdParty\LiteNetLib\LiteNetLib.dll`), не UPM-пакет. Для консольного (не-Unity) хоста это означало бы двойную сборку — аргумент против консольного хоста в Phase 4.

---

## 1. Executive Summary & Architectural Decisions (кандидат в ADR-014)

**Главный вывод исследования:** фундамент Phase 2 + Phase 3 спроектирован под эту интеграцию на удивление точно — шов F-11, pacing-токены, per-client keyframe staging, `PumpPausedSlices`, `ImpairmentProfile` уже отвечают на половину вопросов брифа. Phase 4 — это **не** «соединение двух стеков», а четыре новых механизма (dedicated runner, удалённый клиентский флоу, per-client фильтрация видимости, wiring реконнекта) плюс дисциплина wiring.

### ADR-014 (черновик решений)

| # | Решение | Альтернатива (отклонена) |
|---|---|---|
| **OD-35** | **Dedicated Server = Unity 6 Build Target `Server`** (`-standaloneBuildSubtarget Server`, define `UNITY_SERVER`), не консольный C#-процесс. Server-сборка уже готова архитектурно: `GlobalFront.Server` — `noEngineReferences: true`, Client исключён из серверной сборки через `defineConstraints: ["!UNITY_SERVER"]`. Консольный хост (pure .NET) откладывается на Phase 8+: LiteNetLib — vendored DLL под Unity, двойная сборка, и `UNITY_SERVER`-стриппинг теряется. | Консольный `dotnet`-процесс: требует csproj-обвязку, дублирует build pipeline, не даёт стриппинга; ценен только для Linux-дистрибутива, что не в scope M1. |
| **OD-36** | **Хост-ядро (`LocalMatchHost`-композиция) переезжает в `GlobalFront.Server`** как `ServerHost` (двигатель-нейтральное: MatchServer + TickDriver + SessionManager + ServerReplicationEmitter + graceful resume). В `GlobalFront.Client` остаётся тонкая совместимая обёртка `[Obsolete] LocalMatchHost → ServerHost`. Причина: Client-сборка не компилируется в server build (см. 0.2.3), а `ServerHost` — единственное место, где живёт двухфазовый тик-контракт. | Дублировать хост-логику в новом Unity-asmdef: расхождение кодовых путей, двойная поддержка, прямой путь к десинкам паузы/grace. |
| **OD-37** | **Pacing серверного цикла: `TargetFramerate = 120` + elapsed-feeding.** `Update()` Dedicated Server feed'ит `ServerHost.AdvanceRealTime(elapsedSeconds)` (виртуальное время, catch-up ≤ 4) и один `ServerTransportHost.Pump(nowMs, CurrentTick)`. Джиттер ≤ 8.3 мс/кадр, невидим (клиент интерполирует с задержкой 3–12 тиков; тики несут номера тиков, не wall-time). CPU холостого хода ~0% (Sleep-based frame pacing). Busy-wait/spin-wait НЕ использовать в Unity-плеере; высокоточный поток-тикер (`timeBeginPeriod(1)` / high-res waitable timer) — задокументированный вариант для Phase 8. | Spin-wait в `Update`: 100% CPU; Thread-тикер: нарушение главного-потокового контракта эмиттера без нужды. |
| **OD-38** | **Два клиентских топологии, одна сборка:** `SliceNetworkMode { LocalLoopback, ListenServer, DedicatedClient }` в `TacticalVerticalSliceRunner`. `DedicatedClient` — новый режим без `LocalMatchHost`: identity из хэндшейка (`ClientTransportEndpoint` уже кэширует `Secret`), команды через `NetworkCommandChannel`, `RequestedTick` от реплицированного тика + lead. | Расширять `Initialize` host-событиями «виртуального сервера» на клиенте — путает авторитет. |
| **OD-39** | **Fog of War — фильтрация на отправке (server-side), не рендеринг-скрытие.** Видимость вычисляется в Core-симуляции (`VisibilityEngine`, целочисленный uniform-grid, детерминизм), хранится как per-tick `VisibilityMask` per player — **отдельно от `ServerUnitSnapshot`** (протокол v1/v2 и чексумма-поля не меняются). `ServerReplicationEmitter` фильтрует *merged* change-set per-client в момент отправки против per-client `KnownSet` (bitmap 128 B на клиента). История (`ReplicationHistoryRing`) остаётся общей нефильтрованной — фильтр детерминированно применяется и при `DeltaResume`. | (a) Client-side fog (рендерить, но не скрывать данные): мапхак тривиален — именно так текут мапхаки SC2/AoE (lockstep шлёт всем всё). (b) Per-client истории в ring: память ×10 клиентов, ломает ack-семантику. Server-side filtering — индустриальный стандарт (Spring/BAR LOS, SupCom, Quake relevancy). |
| **OD-40** | **Пер-клиентская контрольная сумма:** на чексумм-тиках (1 Гц) сервер вычисляет FNV-1a только по сущностям в `KnownSet(viewer)` в каноническом порядке возрастания EntityId — ровно то, что лежит в `ClientReplicationWorld` клиента. Стоимость O(N) на клиента на 1 Гц — пренебрежимо. Несовпадение ≥ 2 подряд → re-baseline (существующий механизм). | Whole-world чексумма при FoW: у клиентов гарантированно разные срезы → вечные ложные re-baseline. |
| **OD-41** | **Без client-side prediction движения юнитов.** Мгновенная реакция — только презентационная (выделение, маркер приказа на локальном слое); авторитетный результат приходит через интерполяционный буфер. Причина: клиент не имеет симуляции/пассфайнда (дельта-модель реплицирует *состояние*, не ввод), prediction 400 юнитов = гарантированная дивергенция + резиновые отскоки поверх уже-отложенного интерполяционного буфера; индустрия RTS (SC2, AoE) живёт с задержкой отклика 100–300 мс и instant-UI-feedback. | Prediction/rollback: несовместим с дельта-репликацией состояния, удвоил бы симуляцию на клиенте, ломает Zero-GC. |
| **OD-42** | **Исполнение команды — ближайший тик после прибытия на сервер** (`T_exec = max(requestedTick, serverTick+1)`, клампится уже реализованным `Schedule`). `RequestedTick` = `lastAppliedTick + renderDelayTicks + 2` — используется как подсказка порядка и запас окна, не как жёсткое расписание. | Большой fixed buffer (SC2-style «+N тиков»): добавляет ощущение лага без пользы, т.к. клиент не играет по командам локально. |
| **OD-43** | **Reconnect — оживление существующих механизмов + 3 wiring-фикса** (0.2.5): цепочка `bridge.PrepareForResync → keyframe install → coordinator.OnKeyframeInstalled → binder.HandleResync → 5 s countdown → ReadyToResume → host.Resume`. Presentation-side burst 400 юнитов = 400 `Pop + SetActive` из прогретого пула за 1 `Render` — уже спроектирован под это (`AllowCombatGrowth = false`). | Новая FSM реконнекта: всё уже есть, нужны только проводки. |

---

## 2. Dedicated Server & Pump Engine (Шаг 4.1)

### 2.1 Форма выделенного сервера

**Выбор: Unity 6 Dedicated Server build target.** Официальная механика (проверено по докам Unity 6000.6):

- Build Profile → **Windows Server** (subtarget), либо скриптом: `buildPlayerOptions.subtarget = (int)StandaloneBuildSubtarget.Server`, либо CLI: `-buildTarget StandaloneWindows64 -standaloneBuildSubtarget Server`.
- Unity определяет скриптинг-символ **`UNITY_SERVER`** — проект уже готов: `GlobalFront.Client.asmdef` скомпилируется только если `!UNITY_SERVER`; `GlobalFront.Server` не имеет constraints и присутствует всегда.
- Авто-оптимизации таргета: аудио-подсистема off, lighting-треды off, рендер/UI/аудио/input PlayerLoop-коллбеки off, GPU-only ассеты вырезаются (текстуры/меши без CPU Read/Write). Дополнительно Player Settings «Enable Dedicated Server Optimizations» режет шейдеры и шрифты — **включить на Phase 4 осторожно**: сервер не грузит префабы юнитов (это Client-сборка), но пул Presentation в server-сборке не создаётся, так что шрифты uGUI-оверлеев не попадают в серверную сцену.
- Консоль/CLI: `DedicatedServer.Arguments` (`Port`, `LogLevel`, `LogPath`, `TargetFramerate`, `QueryPort`/`QueryType`) — читать порт сервера из CLI, а не из сценовых объектов.

**Почему не `-batchmode -nographics` на обычном Windows-билде** (desktop headless mode): приемлемо для CI-тестов, но не даёт стриппинга и оптимизаций. Использовать как fallback для PlayMode-тестов, а как продукт — Dedicated Server target.

**Почему не консольный C#-процесс:** сборки `GlobalFront.Core`/`Server` действительно engine-free, и dotnet-хост технически возможен, но: (а) LiteNetLib vendored под Unity; (б) теряем `UNITY_SERVER`-стриппинг и `DedicatedServer.Arguments`; (в) вторая конфигурация сборки = вторая поверхность дефектов. Отложить до Phase 8 (Linux-деплой), архитектура это позволяет без единого изменения в Core/Server — в этом и ценность `noEngineReferences`.

### 2.2 Структура сборок после OD-36

```
GlobalFront.Core        (нет изменений)
GlobalFront.Server      (+ ServerHost.cs [переезд ядра LocalMatchHost],
                          + ServerReplicationHost.cs — композиция transport host)
GlobalFront.ServerHost  (NEW asmdef, Unity-сборка: references Core+Server, Engine=true)
  └─ DedicatedServerBootstrap.cs   [RuntimeInitializeOnLoadMethod(BeforeSceneLoad? AfterSceneLoad)]
  └─ DedicatedServerRunner.cs      (MonoBehaviour, только UNITY_SERVER-ветка)
GlobalFront.Client      (− LocalMatchHost.cs [переезд], + тонкая обёртка-совместимость)
```

Причина отдельной `ServerHost`-сборки: `GlobalFront.Server` обязан остаться `noEngineReferences: true` (архитектурные тесты #2 это ассертят), а чтение `DedicatedServer.Arguments`/`RuntimeInitializeOnLoadMethod` требует UnityEngine. Сборка-хост — единственное легальное место Unity-API на серверной стороне. Ничего из симуляции/репликации в неё не утекает — только композиция и цикл.

### 2.3 Цикл тикера: 20 Гц без джиттера и без 100% CPU

Требование из брифа: 50 мс/тик, бюджет тика < 5.0 ms, не жечь CPU. Ключевой инсайт: **сервер не обязан тикать по wall-clock точно — он обязан тикать по консистентному виртуальному времени.** Дети клиента (`UnitViewTickBuffer`) выдерживают джиттер прибытия 3–12 тиками; сами снапшоты несут `tick`, а не wall-time. Поэтому джиттер срабатывания тика ±(интервал кадра) — невидим. Важно лишь: (а) не терять время (backlog уже копится в `FixedStepClock`), (б) не делать catch-up-штормов (уже bounded 4), (в) дренаж сокетов не блокировать.

**Псевдокод `DedicatedServerRunner` (Unity-сборка, компилируется только в `UNITY_SERVER`):**

```csharp
// Только для dedicated server build. В клиентском билде файл не компилируется.
public sealed class DedicatedServerRunner : MonoBehaviour
{
    private SystemTransportClock _clock;        // Stopwatch-монотонный, уже существует
    private long _lastPumpMs;
    private ServerHost _host;                   // из GlobalFront.Server (OD-36)
    private LiteNetLibCarrier _carrier;
    private ServerTransportHost _transportHost;

    public void Configure(int port, int tickRate, ...) { ... }

    private void Awake()
    {
        _host = ServerHost.CreateHeadless(tickRate: SimulationConstants.ServerTickRate);
        _carrier = LiteNetLibCarrier.CreateServer(port);   // LNL: свой PollEvents-поток
        _transportHost = new ServerTransportHost(_carrier, _host.Sessions, _host.Server);
        _host.AttachReplication(new TransportReplicationAdapter(_transportHost),
                                ServerReplicationEmitterConfig.Default);
        _transportHost.StartServer();
        _host.StartRealTime();
        _lastPumpMs = _clock.NowMs;
        Application.targetFrameRate = DedicatedServerTargetFramerate; // 120 (OD-37)
    }

    private void Update()
    {
        var nowMs = _clock.NowMs;
        var elapsedSeconds = (nowMs - _lastPumpMs) / 1000.0;
        _lastPumpMs = nowMs;

        // 1. Дренаж сокетов ДО тиков: команды, прибывшие после CurrentTick,
        //    клампятся Schedule'ом на CurrentTick + 1 (уже реализовано).
        _transportHost.Pump(nowMs, _host.Server.CurrentTick);

        // 2. Виртуальные тики по накопленному реальному времени.
        //    Внутри: TickStarting → server.TickOnce() (до 4) → TickCompleted →
        //    emitter.OnTickCompleted → sessions.OnTickCompleted (grace sweep).
        _host.AdvanceRealTime(elapsedSeconds);

        // 3. Пауза (тактическая) — тики стоят, но слайсы реконнектящимся клиентам
        //    должны продолжать качаться (уже реализовано в хосте).
        // 4. Кладбище сессий / teardown по MatchPhase.Closed (OD-44).
    }

    private void OnApplicationQuit()
    {
        _transportHost.Shutdown();
        _carrier.Shutdown(TransportDisconnectReason.ServerShutdown);
    }
}
```

**Анализ джиттера (числа):**

| Механизм | Значение | Эффект |
|---|---|---|
| `TargetFramerate = 120` | кадр каждые ~8.3 мс | максимальная задержка тика после дедлайна ≤ 8.3 мс = 1/6 тика |
| `AdvanceRealTime` backlog | копится, catch-up ≤ 4 тика | потерянное время не теряется; 4 × 50 мс = 200 мс «свободы» между кадрами |
| Тик-события | виртуальный `tick` | детерминизм не зависит от wall-time вовсе |
| LNL worker thread | `PollEvents` + `Sleep(1)` | приём не привязан к кадрам; события дренажатся в `Pump` по очереди (`MaxQueuedReceiveEvents = 4096`) |

CPU холостого хода: Unity Sleep-based frame pacing на 120 fps headless — доли процента. Никаких spin-wait циклов в Unity-плеере не требуется. Если когда-нибудь понадобится жёсткая точность (например, < 2 мс джиттера для крупного рейт-лимита или голосового канала) — отдельный pacing-thread с `CreateWaitableTimerEx(HIGH_RESOLUTION)` (Windows 10 1803+, не требует `timeBeginPeriod`) в `ServerHost`-сборке; задокументировать как Phase 8-опцию, сейчас не делать (YAGNI + главный-потоковый контракт).

**OD-44 — teardown сервера:** `MatchPhase.Closed` зарезервирован именно под это (ADR-008): по терминальному `BattleOutcome` хост ждёт `grace drain` последних сессий → переводит матч в `Closed` → `ServerTransportHost.Shutdown()` с `TransportDisconnectReason.ServerShutdown` всем → у клиента `Lost(ServerShutdown)` (не `Timeout`) → HUD «матч завершён». Признак graceful teardown, отличимый от краха — уже есть в enum'ах, нужно только проставить.

### 2.4 Бюджет серверного тика < 5.0 ms

Оценка на 400 юнитов (все пути уже Zero-GC, всё в preallocated-буферах):

| Стадия тика | Стоимость | Источник |
|---|---|---|
| Симуляция (`ExecuteScheduledCommands → ClearInvalidAttackTargets → AcquireAutomaticTargets → MoveUnits → ResolveCombat`) | < 1 мс на 400 юнитов | презентационный срез уже меряет 0.623 ms/frame на *клиенте* вместе с интерполяцией; серверная часть дешевле |
| Захват мира `CopySnapshots(Span)` | O(N) memcpy-подобный | уже реализован |
| Diff O(N+M) → ring.RecordTick | O(N+M), один раз на тик (общий для всех клиентов!) | уже реализовано, Zero-GC ассертится |
| Per-client фильтрация (4.3) | O(N) на клиента на тик | NEW: bitmap-проверки + переходы KnownSet; при 400 юнитах × 10 клиентов ≈ 4K bitmap-проверок/тик — микросекунды |
| Кодирование дельт/слайсов | ≤ 8.2 KB на пакет, pacing 2048 B/тик/клиент | уже реализовано, scratch-буферы существуют |
| Чексуммы | 1 Гц, O(N) на клиента | NEW (per-client), ≈ 400 FNV-пассов × 10 клиентов × 1 Гц — ничто |
| Grace sweep | O(disconnected-in-grace) | уже O(k), не O(n) |

Итого worst-case ≈ 1.5–2.5 ms на 400 юнитах × 10 клиентов с запасом под 5.0 ms. Предел масштаба приходит не отсюда, а от `MaxUnitsPerMatch = 1024` — и там бюджет всё ещё реалистичен, но потребует бенчмарк-гарда (см. 6/4.6).

**Техники, обязательные для бюджета (все уже в проекте, перечислить в DoD):** preallocated scratch-буферы на отправку (по одному на клиента — см. риск 7.1), запрет LINQ (IL-тест), канонический порядок EntityId (валидатор сериализатора), pacing-токены (не «отправить всё»), `RateLimiter` на вход.

---

## 3. Fog of War & Per-Client Replication Design (Шаг 4.3)

### 3.1 Принцип: «клиент не получает того, чего не видит»

Мапхак-устойчивость даёт **только** фильтрация на отправке. Lockstep-архитектуры (SC2, AoE2 — симуляция у всех игроков идентична, летают только команды) принципиально уязвимы: читер читает память симуляции. Проект по ADR-005 выбрал authoritative snapshot server — это правильная сторона; наш путь совпадает с Spring RTS / Beyond All Reason (сервер отправляет только юниты в LOS), SupCom/PA (interest filtering) и Quake3/Source (relevancy filtering).

Следствие: **сервер не отправляет врага, скрытого туманом — ни в дельтах, ни в кейфреймах, ни в чексуммах.** Скрытие на рендере (client-side fog) — презентационная надстройка Phase 7, не мера защиты.

### 3.2 Контракты (новые)

```csharp
// GlobalFront.Server.Replication (NEW) — не существует в кодовой базе (см. 0.2.1).
public interface IReplicationFilter
{
    // Возвращает вердикт для одной записи change-set относительно одного зрителя.
    // Вызывается эмиттером в порядке wire-кода; должен быть чистой функцией
    // от (unit, viewer, текущая видимость) — детерминизм обязателен.
    ReplicationFilterVerdict Classify(in ServerUnitSnapshot unit, PlayerId viewer);
}

public enum ReplicationFilterVerdict : byte
{
    Keep,      // юнит виден и известен клиенту — передать запись как есть
    Reveal,    // юнит виден, но клиент о нём не знает — синтезировать DeltaAddRecord (ADD)
    Hide,      // юнит был известен, стал невидим — синтезировать REMOVE(FogOfWarHidden)
    Suppress   // невидим и неизвестен — выкинуть запись
}

// Источник видимости. НЕ в ServerUnitSnapshot — отдельный поток данных,
// чтобы Snapshot Protocol v1/v2 и поля чексуммы не менялись (совместимость).
public interface IVisibilitySource
{
    bool IsVisible(PlayerId viewer, EntityId entity); // O(1) по bitmap
}
```

Интеграция в `ServerReplicationEmitter`: конфиг-поле `IVisibilitySource VisibilitySource` (null = текущее поведение «весь мир» — обратная совместимость для всех существующих тестов). Внутри `StreamDeltas` и keyframe-стейджинга после `TryMergeRange` применяется per-client-проход: merged-записи классифицируются против `KnownSet` зрителя (bitmap `ulong[16]` = 1024 бита = `MaxUnitsPerMatch`; 128 B/клиент, preallocated при attach).

### 3.3 Переходы видимости (Enter/Exit Fog)

Состояние на клиента: `KnownSet` (что клиент «знает» сейчас). Семантика записи в `KnownSet` — «клиент имеет живую запись об этом юните».

**Enter (юнит впервые входит в зону видимости):**

1. Shared change-set содержит для него, возможно, только `UPDATE` (он существовал тиками раньше вне видимости).
2. Фильтр видит: запись есть, юнит виден, `KnownSet` не содержит Entity → **`Reveal`**: эмиттер синтезирует полный `DeltaAddRecord` (40 B) из текущего снапшота юнита (не из исторической записи!) и добавляет Entity в `KnownSet`.
3. Клиент: `ApplyAdd` берёт свободный слот, binder на следующем `Render` выдаёт view из пула. Это уже работает — ADD mid-stream после baseline штатный путь (сформулированный guard: дельты без baseline вообще не применяются, а baseline всегда предшествует).

**Exit (юнит покидает зону видимости):**

1. Юнит остаётся жив в симуляции (это НЕ смерть!).
2. Фильтр: юнит невидим, но в `KnownSet` → **`Hide`**: синтезируется `DeltaRemoveRecord { Entity, Cause = FogOfWarHidden }`, Entity удаляется из `KnownSet`. Дальше записи юнита — `Suppress`.
3. Клиент: уже готов (`ClientReplicationWorld` считает `FogOfWarRemoveCount`, presenter уничтожает view, слот освобождается). «Память разведки» (ghost-маркер на последней позиции) — презентационная фича Phase 7; Phase 4 — строгая очистка. Важно: если юнит в этот момент имеет активный `MoveTarget`, состояние движения клиента теряется — по возвращении юнита приходит свежий ADD, это корректно.
4. Смерть невидимого юнита: `REMOVE(Destroyed)` по `KnownSet`-членству — если клиент его не знал, записи нет, `Suppress` (никаких следов).
5. **Потеря пакета с REMOVE(FogOfWarHidden)** (C2 unreliable): самовосстановление существующим feedback-механизмом — клиент «зависает» на `LastAppliedTick`, шлёт `DeltaResume` (C0, reliable), сервер отдаёт кумулятивную дельту, фильтр применяется снова — REMOVE переиздаётся. Отдельный `HasTombstoneEcho` не нужен в v1: зарезервирован и остаётся зарезервированным (опциональное усиление: периодический re-echo REMOVE, чтобы defend-in-depth против гипотетической потери ack-цепочки; осознанно не реализуем — усложнение без воспроизводимого дефекта).

**Кейфрейм (reconnect / re-baseline):** per-client keyframe staging уже существует → при его сборке записи фильтруются: в кейфрейм попадают только юниты `KnownSet ∪ CurrentlyVisible`, т.е. фактически «видимые сейчас». `TotalUnitCount` в `KeyframeSliceHeader` — per-client. Вся остальная механика (слайсинг 16 KB, `KeyframeSeq`, монотонность сквозь detach) не меняется.

### 3.4 Вычисление видимости в симуляции (Core)

Новый детерминированный компонент `VisibilityEngine` (GlobalFront.Core, целочисленная математика, без float — правило архитектурного теста #4):

```csharp
// Псевдокод;buckets пересчитываются каждый тик, детерминированно.
public sealed class VisibilityEngine
{
    // sight range в мм из UnitStats; все координаты — WorldPointMm (int).
    public void Rebuild(ReadOnlySpan<ServerUnitSnapshot> world, ...) // or Core-side roster
    public bool IsVisible(PlayerId viewer, EntityId entity);
}
```

- **Uniform grid** с ячейкой `FowBucketMm` (например, 8192 мм ≈ 8 м; параметр): юниты раскладываются по (cellX, cellZ) по Owner-командам — O(N) на тик.
- Запрос: для каждого юнита-зрителя команды V проверка врагов в 3×3 окрестности его ячейки → сложность O(N·k), k — локальная плотность (≈ 5–30 юнитов в бою), т.е. ~10–15K проверок квадратов расстояний на тик —≪ 0.1 ms. Наивный O(N²) на 1024 юнитах = 1M проверок/тик = ~20M/с — уже неприемлемо на фронте 50 мс; grid обязателен с самого начала, а не «потом оптимизируем».
- Хранение: `VisibilityMask` per unit per player — компактно `ushort` bitmask (MaxPlayers = 10 бит) в массиве эмиттера, НЕ в `ServerUnitSnapshot` (не реплицируется, не в чексумме, протокол не меняется).
- Гистерезис (анти-фликер на границе LOS): конфиг `FogExitConfirmTicks` (по умолчанию 1 = мгновенно; 2 = юнит виден ещё 1 тик после выхода из LOS). Рекомендация: v1 без гистерезиса, конфиг-крючок оставить — иначе «мигающие» юниты на границе радиуса обзора потребуют ADD/REMOVE-шторма, что дороже пары тиков слепой зоны. Вход в видимость — всегда мгновенный (анти-мапхак не страдает).
- Автоквитирование: если `AutoAcquireEnemies` стреляет в невидимого для *клиента* юнита — с клиента это выглядит как урон из ниоткуда; это корректно (враг мог видеть нас), но `AttackTarget`-поле в UPDATE записи, классифицированной `Keep`, может указывать на entity, которого клиент не знает → клиентский мир хранит AttackTarget как число без валидации (проверить: `ApplyUpdate` принимает любые id — ок). Presentation не разыменовывает AttackTarget в entity — проверить в 4.3-тестах.

### 3.5 Per-client StateChecksum (OD-40)

```csharp
// В эмиттере, на тиках кратных DefaultChecksumIntervalTicks:
private uint ComputeStateChecksum(PlayerId viewer)   // NEW per-client перегрузка
{
    // канонический захват уже лежит в _captureBuffer (ascending entity id)
    uint hash = 2166136261u;
    for (int i = 0; i < _captureCount; i++)
    {
        if (!_visibility.IsVisible(viewer, _captureBuffer[i].Entity)) continue; // O(1) bitmap
        hash = MixUnit(hash, in _captureBuffer[i]);   // ровно те же поля и порядок,
    }                                                  // что в ClientReplicationWorld.ComputeStateChecksum
    return hash;
}
```

Инварианты:
- Поля и порядок смешивания — **байт-в-байт те же**, что на клиенте (существующая функция), иначе вечная ложная рассинхронизация. Существующий тест `ReplicationRebaseIntegrationTests.StateChecksum_ServerAndClient_EntityOrderAgreement` расширяется: при включённом фильтре per-client чексумма сервера == чексумме мира клиента.
- `KnownSet` обязан быть равен множеству живых слотов клиента в момент чексуммы. Переходы Reveal/Hide применяются в том же тике, что и пакет с ними — окно между «сервер посчитал чексумму» и «клиент применил пакет» гасится двух-подряд-порогом (существующий).
- Zero-GC: итерация по уже захваченному буферу + bitmap — аллокаций нет.
- Клиент-агностика: клиент ничего не знает о том, что его чексумма «осечённая» — он считает её по своему миру как раньше. Протокол не меняется (то же поле `DeltaSnapshotHeader.StateChecksum`).

---

## 4. Command Pipeline & Latency Management (Шаг 4.4)

### 4.1 Полный жизненный цикл команды

```
[Кадр клиента, t0]  RtsInputManager.ManualUpdate → UnitSelectionDriver.ManualUpdate
                    → IsRightMouseButtonUp → UnitSelectionController.TryIssueCommandAtPointer
                    → Emit(...) → блоки ≤ MaxSelectedEntities, свой Sequence на блок
                    → UnitCommandChannelSink.Submit(IssuedCommand)
                    → NetworkCommandChannel.TrySubmitMove/Attack/Stop
                       • CommandWireCodec.Encode* в preallocated _encodeBuffer (zero-GC)
                       • endpoint.TrySendCommand → C1 (ReliableOrdered)
[Сервер]            LNL worker-thread принимает → очередь событий
                    → ServerTransportHost.Pump(nowMs, CurrentTick)
                       • binder по токену+connectionId (анти-спуф: несовпадение → DroppedBadToken)
                       • CommandWireCodec.TryDecode → SessionManager.ValidateCommand
                         [UnknownSession → SessionClosed → NoBoundMatch →
                          SessionNotConnected → PlayerMismatch → MatchNotRunning]
                       • MatchServer.TryEnqueue*: ValidateCommonHeader
                         [MatchOver → окно 20 past/60 future → DuplicateSequence]
                         → ValidateControllable [UnknownEntity → NotEntityOwner → EntityNotAlive]
                         → payload/hostility [FormationOutOfBounds, FriendlyTarget]
                       • Schedule(): T_exec = max(requestedTick, CurrentTick + 1)
                       • CommandAck (7 B) ← немедленно: (Player, Sequence, sessionRej, cmdRej)
[Тик T_exec]        ExecuteScheduledCommands (порядок: PlayerId → Sequence) → MoveUnits...
[Клиент]            CommandAckReceived → HUD-поверхность отказа (LastRejection-пломбинг есть)
                    Результат боя/движения — через дельты C2 → tick buffer → binder.
```

### 4.2 Почему нет client-side prediction (OD-41)

1. **Клиенту нечего предсказывать.** Дельта-модель реплицирует *состояние*, а не ввод: на клиенте нет пассфайнда, боевой логики, порядков юнитов. Prediction потребовал бы вторую копию симуляции (или её GLM-подмножество) в Client-сборке — прямой конфликт с авторитетной моделью ADR-005 и бюджетом Zero-GC.
2. **Резиновые отскоки поверх интерполяции.** Юниты и так рендерятся с задержкой 3–12 тиков позади авторитета (`UnitViewTickBuffer`); локальное «телепортирование» вперёд и затем откат к авторитету дали бы видимые скачки позиции — хуже, чем честная задержка 200–300 мс.
3. **Индустриальный консенсус:** SC2 и AoE исполняют команды с задержкой (SC2 — на ~2 игровых тика позже получения; AoE — по arriving-lockstep), и ни одна не предсказывает движение юнитов; мгновенная реакция достигается *звуком*, маркером приказа и анимацией клика — чисто презентационными средствами.
4. **Отклик интерфейса — локально и мгновенно** (что мы и делаем): выделение применяется сразу (уже так), маркер приказа в точке клика — локальный косметический эффект (Phase 4: минимальный, финальный полиш — Phase 7), отказ сервера всплывает через `CommandAck` в HUD (`NetworkCommandChannel.CommandResultReceived` уже связан с endpoint'ом; остаётся провести в презентацию).

### 4.3 Lead-расчёт RequestedTick (сетевой режим)

Сегодня `RequestedTick = host.CurrentTick + 1` (ADR-007, локальный хост). Для `DedicatedClient`:

```
requestedTick = lastAppliedTick            // последний применённый серверный тик
              + renderDelayTicks           // сколько тиков назад смотрит вью (3–12)
              + ceil(halfRtt / tickMs)     // путь команды вверх: RTT/2
              + 2;                         // запас на кадр ввода и один тик рейса
```

Серверный `Schedule` всё равно клампит в `max(requested, CurrentTick+1)`, а окно ±(20 past / 60 future) поглощает вариации. RTT измеряем существующим Ping/Pong (8-байтовый timestamp эхом) на 1 Гц → `NetworkHudPresenter.UpdatePing` наконец получает данные (wiring 4.2). Подстраивать lead динамически не требуется в Phase 4 — кламп и окно делают грубость нечувствительной; пересмотр — только если PlayMode-тесты покажут упор в `AcceptedFutureTicks`.

Важно для UX: чтобы приказ, отданный по тому, что игрок видит, не «телепортировал» юниты в момент, который игрок ещё не видел (vision-sync проблема), `renderDelayTicks` в формуле гарантирует, что исполнение произойдёт не раньше, чем сцена, на которую игрок кликнул, уже была показана. Строгая гарантия (`T_exec` = тик, который игрок реально видит на экране в момент клика) — опция Phase 7, требует пер-клиентского маппинга render-tick → server-tick, который у нас уже есть (`UnitViewTickBuffer.RenderTick`).

---

## 5. Live Combat Reconnect Protocol (Шаг 4.5)

### 5.1 Протокол (почти всё уже реализовано — здесь сходка существующих механизмов + 3 фикс-проводки)

```
[Сервер] LNL idle timeout 5 s / graceful disconnect
  → ServerTransportHost: SessionDetached(session, reason)
  → ServerHost (AutoPauseOnDisconnect): TickDriver.Pause() — тики стоят
  → SessionManager.NotifyConnectionLost(session, atTick) — grace 4000 тиков (200 с) пошёл
  → Emitter: staging буферы сохранены; NextKeyframeSeq продолжает монотонно (фикс D2 уже есть)
  → все клиенты: TacticalPauseOverlay «Игрок {N} переподключается…»

[Сервер, во время паузы] PumpPausedSlices() качает слайсы НОВЫМ клиентам
  (уже реализовано; reconnecting клиент получает keyframe и в паузе)

[Клиент] Отвалился сокет → endpoint.Lost(reason)
  → ClientReconnectCoordinator.OnTransportDisconnected(reason) → Reconnecting
  → [опционально auto-retry] TryBuildReconnectRequest (64 B, C0, zero-GC)

[Сервер] ReconnectRequest (opcode 12) → SessionManager.TryRebindSession(secret, newHandle)
  • fixed-time сравнение секрета (анти-timing-атака)
  • внутри grace → Accepted, слот Disconnected → Connected, PlayerId сохранён
  • после grace → GraceExpired → сессия Abandoned, PlayerId НЕ переиспользуется (OD-3)
  → ReconnectResponse (92 B): currentTick + activeKeyframeSeq

[Клиент] coordinator.OnReconnectResponse(Accepted)
  → receiver.PrepareForResync(activeKeyframeSeq)   // старое поколение дельт = stale
  → coordinator: ResyncStarted
  → uplink перебинден: bridge.BindUplink(newUplink)

[Сервер] emitter.OnSessionReattached → форс-кейфрейм (per-client, FoW-фильтрованный — 4.3)
  → слайсы по C2 (в паузе — через PumpPausedSlices)

[Клиент] ключевые слайсы собираются (1500 мс assembly timeout, дубли — re-request)
  → receiver.ReceiveKeyframe → World.Reset + ADD-все → FSM.NoteBaseline
  → ★ NEW WIRING: OnKeyframeInstalled() → coordinator: Resyncing → Countdown (5 s)
  → ★ NEW WIRING: binder.HandleResync(keyframeTick):
       ReleaseAllViews() (400 view → пул, без Instantiate/Destroy)
       + buffer.Resync(keyframeTick) (Flush + re-anchor render clock)
  → следующий binder.Render: 400 × (Pool.Pop + SetActive) — ноль аллокаций,
     один кадр; overlays перестраиваются на следующем кадре батчами по 250

[Countdown] coordinator.AdvanceCountdown(dt) → 3..2..1 → ReadyToResume
  → ★ уже связка есть: LocalMatchHost.BindReconnectCoordinator → host.Resume()
  → TacticalPauseOverlay «Бой продолжится через…» → симуляция пошла
```

### 5.2 Ответственность за проводки (что добавить)

| Проводка | Где | Примечание |
|---|---|---|
| `receiver`-событие установки baseline → `coordinator.OnKeyframeInstalled()` | клиентский композиционный слой (runner/bootstrap) | добавляется callback/событие в `ClientReplicationReceiver` (или опрос `Fsm.HasBaseline` после перехода из `Unbased/Rebasing`) — минимальное изменение контракта |
| `coordinator.OnKeyframeInstalled` → `binder.HandleResync(keyframeTick)` | там же | keyframeTick доступен из receiver |
| автопауза → overlay | `NetworkHudPresenter` уже слушает `SessionManager.SessionDisconnected/GraceExpired` и `Host.Paused/Resumed` | работает при переносе хоста в Server (события те же) |
| `UpdatePing` → источник RTT | 4.2 | Ping/Pong 1 Гц |

### 5.3 Гарантии восстановления

- **Идентичность состояния:** кейфрейм — канонический снимок мира; после install мира клиента и серверного мира совпадают с точностью до FoW-среза; per-client чексумма (4.3) фиксирует совпадение численно. Тест (4.6) сравнивает мир «до вылета» и «после ресинка» поле-в-поле.
- **Порядок команд:** `TryGetLastAcceptedSequence` в receipt — счётчик последовательностей продолжается, replay отбрасывается `DuplicateSequence` (тест уже есть).
- **Фриз боя:** `AdvancePauseTicks` ограничен (grace + catch-up), защита от «нависания» NaN/∞ уже в тестах (P0-1). 200 с грейса — много для M1; вынести в конфиг матча (`DisconnectGraceSeconds`), для тестов драматически уменьшаемый.

---

## 6. План шагов реализации 4.1–4.7

Общие DoD-правила для всех шагов: тесты green (EditMode + PlayMode), 0 warnings, 0 аллокаций в измеренных циклах, архитектурные инвариант-тесты не ослаблены, evidence-файл в `Artifacts/`.

### 4.1 Dedicated Server Runner & Pump
- **Файлы:** NEW `GlobalFront.ServerHost` asmdef; NEW `DedicatedServerBootstrap.cs`, `DedicatedServerRunner.cs`; MOVE `LocalMatchHost.cs` → `Server\ServerHost.cs` (+ `[Obsolete]` обёртка в Client); NEW Build Profile «Windows Server»; `MatchPhase.Closed` teardown.
- **Контракты:** затрагивает `ServerHost` (новое имя), ничего из `Core`/протоколов.
- **DoD:** (1) `ServerTransportHost.Pump` имеет ≥ 1 production call-site (F-10 закрыт); (2) headless-сборка стартует, обслуживает 2 клиентов, завершает матч, уходит в `Closed`; (3) EditMode-тесты раннера через виртуальный пайп + manual clock (без wall-time); (4) CPU idle < 5% профилировкой (evidence).

### 4.2 Client Remote Connection & Handshake
- **Файлы:** `TacticalVerticalSliceRunner` (+`SliceNetworkMode`), NEW `RemoteSliceSession` (композиция endpoint+uplink+bridge+coordinator), `NetworkHudPresenter` (+connect-минимум: IP/порт/статус), подключение `UpdatePing` (Ping/Pong 1 Гц).
- **Контракты:** только Client + NEW композиция; `IReplicationTransport/Uplink` не меняются.
- **DoD:** (1) два клиента на реальном UDP loopback (LiteNetLib) играют утверждённый loop 1v1: select→move→attack→stop, серверные ack'и видны; (2) `RequestedTick` в окне ±(20/60) на всём прогоне; (3) RTT отображается в HUD; (4) PlayMode-тест end-to-end localhost.

### 4.3 Fog of War / Per-Client Filtering
- **Файлы:** Core NEW `VisibilityEngine` (+ `FowBucketMm`, sight-range в юнит-статах); Server NEW `IReplicationFilter`, `IVisibilitySource`, интеграция в `ServerReplicationEmitter` (+конфиг `PerClientFiltering`, per-client `KnownSet`, per-client checksum); Client — без изменений протокола.
- **Контракты:** `ServerReplicationEmitterConfig` (аддитивно), новые Core-типы; wire-формат не меняется (`FogOfWarHidden` уже в протоколе).
- **DoD:** (1) мапхак-тест: вражеский юнит вне LOS ни разу не появляется в `ClientReplicationWorld` за N тиков (в т.ч. в кейфреймах после re-baseline); (2) Enter → ADD, Exit → REMOVE(FogOfWarHidden), потери пакета самовосстанавливаются через DeltaResume; (3) per-client чексуммы сервера и клиента бит-равны, whole-world-режим не регрессировал (все 1195 тестов); (4) FoW O(N·k) — замер в тесте.

### 4.4 Authoritative Command Ingress
- **Файлы:** `NetworkCommandChannel` (продакшн-проводка в `DedicatedClient`-режиме), lead-расчёт в `UnitSelectionDriver`/runner, HUD-поверхность отказов.
- **DoD:** (1) E2E: чужой юнит → `NotEntityOwner`; мёртвый → `EntityNotAlive`; спуф-токен → `DroppedBadToken`; дубликат sequence → `DuplicateSequence` — все через реальный транспорт (сейчас частично только через виртуальный); (2) ack round-trip < RTT+2 тика; (3) ни одна команда не исполняется до `T_exec` = max(requested, current+1).

### 4.5 Live Combat Reconnect
- **Файлы:** wiring из 5.2 (`ClientReplicationReceiver` baseline-событие, runner-композиция, binder-хэндлинг), конфиг грейса.
- **DoD:** (1) интеграционный тест: 400 юнитов в бою → disconnect → пауза → reconnect → кейфрейм → 5 s countdown → resume → мир поле-в-поле равен до-аварийному (по CopyLiveStates, FoW-срезу); (2) Presentation-бёрст: 400 view восстановлены за 1 `Render`, 0 B GC (assert `Is.Not.AllocatingGCMemory()`); (3) двойной реконнект подряд (A → отвалился → вернулся → снова отвалился) — `KeyframeSeq` монотонен, старое поколение дельт отброшено.

### 4.6 Test Pyramid & Networked Harness
- **Файлы:** расширение `TransportTestSupport`/`ReplicationIntegrationSupport` (ImpairmentProfile presets: `LanReliable` {Latency 5, Loss 0}, `Wifi` {Latency 40±jitter, Loss 1%}, `Wan` {Latency 100, Loss 5%}), NEW фикстуры:
  - `NetworkedGameplayIntegrationTests` (1 сервер + 2 клиента, полный loop, сценарии 1v1/2v2 — по образцу `Match2v2PlaytestScenarioTests`);
  - `FogOfWarReplicationTests` (мапхак/переходы/чексуммы);
  - `DedicatedRunnerTests` (цикл, пауза, Closed);
  - `CommandIngressNetworkTests`;
  - `MidCombatReconnectTests`;
  - control-тесты (anti-vacuity) для каждого «отсутствия» (паттерн проекта).
- **Инварианты, зашиваемые в каждый сетевой тест:** `ChecksumMismatchCount == 0`; `InconsistentApplyCount == 0`; `RejectedMessageCount == 0` (в чистых сценариях); `Is.Not.AllocatingGCMemory()` на pump-цикле клиента и на `Pump+AdvanceRealTime` сервера; детерминизм — два прогона с одинаковым seed `ImpairmentProfile` дают бит-равные миры; мапхак-инвариант (0.2-й пункт DoD 4.3).
- **DoD:** целевой рост тестов 1195 → ≥ 1260; PlayMode: real-UDP loopback 1v1 + 2v2 virtual.

### 4.7 ADR-014, документация, аудит
- **Файлы:** `Documentation\DECISIONS.md` (+ADR-014: OD-35…OD-43), `ARCHITECTURE.md` (новая сборка ServerHost, dependency-граф), `CURRENT_STATE.md`, `CHANGELOG.md`, evidence в `Artifacts/`, независимый adversarial audit (по образцу GrandAudit).
- **DoD:** `[APPROVED: ZERO DEFECTS]`, Milestone M1 принят, Phase 4 в `CURRENT_STATE` переведён в complete.

**Зависимости шагов:** 4.1 → (4.2, 4.3) → 4.4/4.5 параллельно → 4.6 → 4.7. Рекомендуемый порядок: 4.1, 4.2, 4.5 (wiring-фиксы дешёвые и разблокируют 2v2-тесты), 4.3 (самый большой), 4.4, 4.6, 4.7.

---

## 7. Анализ рисков и Zero-GC ограничения

| # | Риск | Серьёзность | Превенция |
|---|---|---|---|
| 7.1 | **Per-client scratch-буферы.** `ServerReplicationEmitter` сейчас кодирует в общий scratch при последовательной отправке; при per-client фильтрации с кодированием per-client в один буфер — безопасно, если кодирование и `SendToSession` не перекрываются (копия на отправке делает carrier). Требование: либо per-client буфер отправки (10 × 16 KB = 160 KB — допустимо), либо гарантия synchronous-send (у обоих карриеров `Send` — синхронный enqueue). Проверить `TransportReplicationAdapter.SendToSession` не сохраняет ссылку. | P1 | Тест: 10 клиентов, параллельная отправка, assert distinct payloads. |
| 7.2 | **Re-baseline шторм.** Если клиент отстаёт > 120 тиков (ring eviction) — keyframe 16 KB; 10 клиентов, отставших одновременно (пик боя) → 160 KB разово — внутри burst-капа, но не одновременно с дельтами. Pacing-токены уже дозируют; риск — starvation дельт. | P2 | Токен-бюджет кейфрейма отдельно от дельт; мониторинг `QueueDepth/PeakQueueDepth` (уже есть). |
| 7.3 | **Отсутствие async-инварианта.** Конвенция «no async/Task» не ассертится тестом — один неосторожный `Task.Run` в Server пройдёт CI. | P2 | Добавить IL-тест в `ArchitectureInvariantsTests` (по образцу LINQ-теста): запрет `System.Threading.Tasks`, `async`/`await` токенов в Core/Server (исключение: LiteNetLibCarrier worker-thread — домен транспорта, явно перечислить). |
| 7.4 | **FoW-фликер на границе LOS** → ADD/REMOVE-шторм на границе радиуса обзора. | P2 | Гистерезис-конфиг (3.4), лимитер: не более K FoW-переходов на клиента на тик; телеметрия переходов. |
| 7.5 | **KnownSet рассинхрон с клиентским миром** (потерянный Reveal/Hide) → вечный re-baseline цикл. | P1 | Чексумма per-client ловит мгновенно (2 подряд); тест «100% потерь C2 в течение 5 с → восстановление без постоянного re-baseline» в 4.6. |
| 7.6 | **`VisibilityEngine` — изменение Core.** Любое изменение симуляции — риск детерминизма (архитектурное правило: compatibility review). Grid-бакетинг детерминирован (фиксированные ячейки, стабильный порядок итерации). | P1 | Детерминизм-тесты: identical setups → identical visibility; `System.Random`/float-скан уже ловит проседания. |
| 7.7 | **Race: LNL worker-thread vs главный поток.** События стекаются через bounded-очередь (`MaxQueuedReceiveEvents = 4096`), дренаж только в `Pump` на главном потоке — контракт уже такой. Опасность: забыть `Pump` при паузе (PumpPausedSlices существует, но сокеты тоже). | P1 | DoD 4.1: тест «пауза → команды/коннекты продолжают обрабатываться». |
| 7.8 | **Управляемый стриппинг в Dedicated Server build** может выпилить рефлексию-зависимые куски (у нас есть рефлексия в тестах, но не в рантайме Server). LiteNetLib — без рефлексии. | P2 | Early build-profile smoke-test в 4.1 (DoD); стриппинг Level Medium+, проверка на 2v2-сценарии. |
| 7.9 | **Windows таймер-гранулярность.** Sleep-based frame pacing Unity на headless — достаточен (анализ 2.3); но на нагруженной машине (фоновые процессы) кадры могут растягиваться → catch-up 4 тика может не успевать при задержках > 200 мс → отставание симуляции. | P2 | Метрика backlog (`BacklogSeconds`) в evidence; guard-тест «симуляция не отстаёт при 25%-ных кадровых задержках» (виртуальное время). |
| 7.10 | **Пауза 200 с замораживает бой для всех** — UX-компромисс уже принят (OD-18), но для 4 игроков это боль. | P3 | Вынести grace в конфиг матча; Phase 7+ — симметричный вариант «бег-в-паузе»/bot-плейсхолдер. Не в M1. |
| 7.11 | **`UnitViewPool.AllowCombatGrowth = false`** и незнакомый `UnitKind` (новый сервер против старого клиента) → юниты не презентуются (`_unpresentedCount`). Обработка уже есть (fallback `UnitKinds.Unknown`); риск — только при рассинхроне версий. | P3 | Smoke-тест «сервер v2+1kind против клиента без kind» — уже покрыт фолбэком; чексумма не включает UnitKind (согласовано). |
| 7.12 | **Пер-клиентские ключевые кадры при FoW × реконнект:** staging per-client уже есть, но фильтр должен применяться на этапе стейджинга (не при отправке слайсов) — иначе слайсы разных клиентов будут делить один буфер. | P1 | Реализация: фильтр на `QueueKeyframe`-стейджинге (per-client буферы уже изолированы — D6-фикс); тест 2 клиентов с разными срезами видимости → разные кейфреймы, одновременный приём. |

### Чек-лист Zero-GC (в继承 существующим правилам)

- Hot-путь сервера: захват → diff → merge → фильтр → кодирование → send. Аллокации допустимы только при attach/detach (one-time) — ввести в архитектурный тест: `Emitter_HotPath_PerClient_DoesNotAllocate` (10 клиентов × 20 тиков).
- Hot-путь клиента: bridge decode → receiver apply → CaptureTick → Render → Sample. Уже ассертится; расширить на сетевой цикл (endpoint + LNL events — проверить `SnapshotReceived` 4-slot rotating buffer не аллоцирует на fragment-риассембле C2).
- Замерочная дисциплина: control-тест рекордера аллокаций (уже есть), warm-up вне окна, post-валидация содержательная (appliedAfter > appliedBefore).

---

## Приложение A. Источники

**Верифицировано в коде (пути от корня проекта):** `Runtime\Server\TickDriver.cs`; `MatchServer.cs`; `Sessions\SessionManager.cs`; `Transport\*` (ServerTransportHost, OwnDatagramCarrier, LiteNetLibCarrier, VirtualNetworkPipe, TransportSessionBinder, MessageCodec, EnvelopeCodec, FragmentAssembler, ClientTransportEndpoint); `Replication\*`; `Snapshot\*`; `Runtime\Client\*` (LocalMatchHost, ClientReconnectCoordinator, Presentation\UnitViewTickBuffer/UnitViewBinder/UnitViewPool/TacticalVerticalSliceRunner, Replication\*); `Tests\EditMode\Architecture\ArchitectureInvariantsTests.cs`; `Tests\EditMode\TransportTestSupport.cs`; `Documentation\Phases\Phase_04_Multiplayer_Integration.md`; `Documentation\DECISIONS.md` (ADR-001…013); `Documentation\ARCHITECTURE.md`; `Documentation\DEVELOPMENT_STANDARD.md`.

**Веб-источники (проверено fetch'ем, Unity 6000.6):** Unity Manual: *Dedicated Server* (dedicated-server.html), *Introduction to Dedicated Server*, *Build your application for Dedicated Server* (`StandaloneBuildSubtarget.Server`, `UNITY_SERVER`, `-standaloneBuildSubtarget Server`), *Dedicated Server optimizations*, *Desktop headless mode*, `DedicatedServer.Arguments` API (Port/TargetFramerate/LogLevel/LogPath/QueryPort).

**Индустриальные практики (экспертные знания, для контекста):** StarCraft 2 / Age of Empires — deterministic lockstep и мапхак-уязвимость lockstep-модели; Supreme Commander / Planetary Annihilation (Uber, GDC «Beyond These Virtual Times») — server-authoritative с interest management; Spring RTS / Beyond All Reason — серверная LOS-фильтрация юнитов (референс для OD-39); Quake 3 / Source Engine networking — delta compression против последнего подтверждённого кадра и relevancy-фильтрация (референс существующей архитектуры ADR-010); Gaffer On Games (Glenn Fiedler) — «Snapshot Compression», «Networked Physics» (relevancy & priority). Примечание: полнотекстовый веб-поиск в этой сессии был недоступен (инструмент вернул пустые результаты); Unity-факты верифицированы официальной документацией, индустриальные практики даны из экспертной базы без свежих ссылок — при подготовке ADR-014 можно точечно подтвердить пару источников.

## Приложение B. Открытые вопросы для ревью ADR-014

1. Перенос `LocalMatchHost` → `ServerHost` (OD-36): rename с `[Obsolete]`-обёрткой vs сохранить имя класса. Оценка: ~30 файлов тестов используют `LocalMatchHost` — обёртка дешевле; решение за ревью.
2. Гистерезис FoW: дефолт `FogExitConfirmTicks = 1` (мгновенно) или 2? Требует плейтест-решения.
3. `MaxRenderDelayTicks = 12` (0.6 с) — поднять ли потолок для WAN-матчей (RTT 150+)? Конфигурирование через матч-настройки vs жёсткая константа.
4. Грейс дисконнекта: 200 с для 1v1/2v2 с живым протоколом реконнекта — не слишком ли долго замораживать матч? Предложение: 30 с в Phase 4, обсуждение.
5. Консольный dotnet-хост — Phase 8, подтвердить отказ в Phase 4 (OD-35).
