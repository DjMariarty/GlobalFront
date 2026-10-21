# Phase 4 R&D Rev. 2 — Multiplayer Gameplay Integration (GlobalFront)

> Статус: **R&D Rev. 2** (вход для ADR-014) • Дата: 2026-10-21 • Бейзлайн: `bbfd19d`, 1195 тестов (1179 EditMode + 16 PlayMode).
>
> Этот документ **дополняет и исправляет** [Rev. 1](Phase_04_Multiplayer_Gameplay_Integration_RND.md). Rev. 1 остаётся в силе там, где здесь не оспорен; все расхождения перечислены в §0.3 и помечены в тексте как **[R2]**.
>
> Метод: сплошное чтение кода на `bbfd19d` (не по памяти и не по Rev. 1), плюс сверка с принятыми OD-10…OD-22 (ADR-010/011) и OD-23…OD-34 (ADR-012/013). Каждое утверждение ниже имеет `file:line`. Оценки, которые не измерены, явно помечены как оценки.

---

## 0. Верификация: что реально есть в коде

### 0.1 Подтверждённый фундамент (переиспользуется без изменений)

| Механизм | Файл:строка | Подтверждённый факт |
|---|---|---|
| Тик-контракт | `Runtime\Core\SimulationConstants.cs:9,11,12,13,21` | `ServerTickRate = 20`, `MaxCatchUpTicksPerFrame = 4`, `MaxPlayers = 10`, `MaxSelectedEntities = 100`, `MaxUnitsPerMatch = 1024` |
| Тикер | `Runtime\Server\TickDriver.cs:192,226` | `AdvanceRealTime(double)` / `AdvanceManualTick()`; `Pause()` блокирует **оба** режима; все члены под `lock (_gate)` |
| Монотонные часы | `Runtime\Server\Transport\ITransportClock.cs:21-27` | `SystemTransportClock` = `Stopwatch.StartNew()`. **Не** `DateTime.UtcNow` — гранулярность ~100 нс, проблемы resolution 15.6 мс нет |
| Протокол дельт | `Runtime\Core\Snapshot\DeltaSnapshotProtocol.cs:31,52,55,65,78` | `Version = 2` (OD-29 добавил `UnitKind u8`), header 36 B, `MaxPayloadBytes = 8192`, `MaxPacketBytes = 8228`, ADD = 40 B, UPDATE 2…44 B |
| Флаги | `DeltaSnapshotProtocol.cs:105,113` | `HasChecksum = 1<<0`, **`HasTombstoneEcho = 1<<1` зарезервирован и никем не выставляется** |
| Эмиттер | `Runtime\Server\Replication\ServerReplicationEmitter.cs:22,24,26,48` | pacing refill 2048 B/тик (40 КБ/с), burst 16384 B, слайс ≤ 16384 B, чексумма раз в 20 тиков (1 Гц) |
| Per-client состояние | `ServerReplicationEmitter.cs:291-318` | `LastAckedTick`, `LastSentTick`, `PacingTokens`, `KeyframeSeq`, `KeyframeStaging` = 163 840 B на клиента |
| Кольцо истории | `Runtime\Server\Replication\ReplicationHistoryRing.cs:32` | `Capacity = 120` тиков; eviction → `TryMergeRange` false → keyframe |
| Клиентский приём | `Runtime\Client\Replication\ClientReplicationReceiver.cs:355-420` | Порядок: ADD → UPDATE → REMOVE → `NoteApplied` → **только потом** сверка чексуммы; 2 промаха подряд → `ReportBaselineLoss` |
| Клиентский мир | `Runtime\Client\Replication\ClientReplicationWorld.cs:284,372-374,458-502,504-552` | `ApplyAdd`, `FogOfWarRemoveCount`, `ComputeStateChecksum()` (FNV-1a), heapsort по возрастанию `EntityId` |
| Интерполяция | `Runtime\Client\Presentation\UnitViewTickBuffer.cs:162,195,198,201,208,214,182` | 32 тика × 37 B/слот; `delay = 2·interval + 0.5·jitter`, EMA α = 0.25, clamp **[3, 12] тиков**; экстраполяция ≤ 2 тиков по ≤ 350 мм/тик, затем `Held` |
| Пул представлений | `Runtime\Client\Presentation\UnitViewPool.cs:41,35,142,199-240` | потолок 4096, блок роста 32, `AllowCombatGrowth = false`, `Warmup(kind, count)` |
| Шов F-11 | `Runtime\Client\Presentation\TacticalVerticalSliceRunner.cs:657-663,686-691` | `Initialize(..., IReplicationTransport transport = null, IReplicationUplink uplink = null)`; половина шва → `ArgumentException` |
| Транспортный адаптер | `Runtime\Server\Replication\ServerReplicationContracts.cs:79-126` | `TransportReplicationAdapter : IReplicationTransport` — **полностью готов и ни разу не сконструирован в production** |
| Эмуляция сети | `Runtime\Server\Transport\VirtualNetworkPipe.cs:85-94,177-211` | `ImpairmentProfile { LossProbability, DuplicationProbability, CorruptionProbability, LatencyMs, ReorderJitterMs, Partitioned, Seed }`; время только через `Advance(ms)`; endpoint'ов может быть N |
| Мульти-клиент стенд | `Tests\EditMode\Network\Match2v2PlaytestScenarioTests.cs:94-98,162-254` | реальные 4 лега, бит-равенство чексумм всех четырёх, дисконнект → пауза → реконнект → 5 с → resume |

### 0.2 Расхождения брифа с реальностью

1. **`IReplicationFilter` в коде нет** (grep по `Assets\` — 0 совпадений). Но он **уже нормативно специфицирован** принятым OD-16 в `Documentation\Research\Phase_02_06_Delta_Snapshot_Protocol_RND.md:575-590`, причём с другой сигнатурой, чем предлагает Rev. 1 (см. §0.3.1).
2. **`JoinMatchRequest` не существует** как wire-сообщение. В `TransportProtocol.cs:26-52` одиннадцать типов: `ConnectRequest=1 … ReplicationRequest=11` (+ C0 opcode 12/13 реконнекта). Присоединение к матчу — публичное поле `ServerTransportHost.AutoJoinMatch` (`:43`), применяемое внутри `HandleConnectRequest` (`:463-474`). Никакого отдельного шага «join» на проводе нет.
3. **«32-байтный криптографический SessionToken» — ошибка смешения двух разных секретов.** Токен сообщения — **64-битный** crypto-random из `TransportSessionBinder.Bind` (ADR-009, `DECISIONS.md:221`), живёт только внутри handshake-пары «сокет ↔ сессия». **32 байта** — это `SessionSecret32` (`SessionManager.cs:193-195`, OD-21), отдельная долгосрочная идентичность для реконнекта, доставляется один раз сообщением opcode 9 (33 B). Проектировать «один 32-байтный токен на всё» — значит сломать уже принятый OD-21.
4. **«5-секундный обратный отсчёт» на сервере — нет.** Серверный grace = `DefaultDisconnectGraceTicks = 4000` тиков = **200.0 с** (`SessionManager.cs:99`, OD-18). 5 с — это **клиентский** `DefaultCountdownSeconds = 5.0f` (`ClientReconnectCoordinator.cs:41`, OD-20). Два разных таймера, их нельзя путать в DoD.
5. **`T_exec = T_server + Buffer` на сервере не существует.** `Schedule` исполняет `executeTick = requestedTick <= CurrentTick ? CurrentTick + 1 : requestedTick` (`MatchServer.cs:820`), окно приёма −20/+60 тиков (`:127,130`). Буфер — это **клиентское** решение о том, какой `RequestedTick` положить в пакет, а не серверная очередь.
6. **«адаптивная задержка 10/5 Hz» — смешение слоёв.** 10 Гц / 5 Гц — каденс **репликации** (OD-12). Задержка `UnitViewTickBuffer` измеряется в **тиках** и лежит в [3, 12] (`:201,208`). Это разные величины; в WAN-сценарии они связаны (§4.3), но не тождественны.
7. **«0.623 ms/frame» — не ассерт.** Единственная проверка — `averageMs < 8.0` (`TacticalVerticalSliceBenchmarkTests.cs:119,798-803`); 0.623 — `TestContext.WriteLine` (наследие F-8). Zero-GC при этом **ассертится** (`Is.Not.AllocatingGCMemory()`, `:685-695,734-743`) с позитивным контролем рекордера (`:650-660`).
8. **F-11 сильнее, чем описан в брифе.** Подмена loopback на реальный carrier требует **ноль правок** в `TacticalVerticalSliceRunner` — шов параметризован и запиннен тестом `Initialize_WithCustomTransportAndUplink_UsesInjectedSeamsWithoutCreatingLoopback` (`:858-913`) и тестом отказа половины шва (`:915-941`). Замечание F-11 про «5 правок» закрыто в `3a112ad`.
9. **Бейзлайн 1195 подтверждён двумя независимыми способами**: `[Test]` = 1068 + `[TestCase]` = 111 = 1179 EditMode, `[UnityTest]` = 16 PlayMode.

### 0.3 Расхождения Rev. 1 с реальностью — **[R2]**

#### 0.3.1 Rev. 1 молча переопределяет принятый OD-16

OD-16 (Accepted 2026-09-02, `DECISIONS.md:262`) фиксирует:

```csharp
namespace GlobalFront.Server.Snapshot {
    public interface IReplicationFilter {
        void BuildView(in ReplicationContext context, ReplicationViewBuilder builder);
        VisibilityDecision DecideVisibility(in ReplicationContext context, EntityId entity);
    }
    public enum VisibilityDecision : byte { FullVisible = 0, GhostVisible = 1, Hidden = 2 }
}
```

и прямо запрещает per-entity вызов в горячем цикле (RND 2.6 §13.2: «Вызов `DecideVisibility` для каждой пары entity/client в горячем цикле запрещён»). Rev. 1 предлагает вместо этого `Classify(in ServerUnitSnapshot, PlayerId)` → `ReplicationFilterVerdict { Keep, Reveal, Hide, Suppress }` — per-record виртуальный вызов, то есть ровно то, что OD-16 запретил, и без `GhostVisible`.

**[R2] Решение:** сохранить **batch-контракт OD-16** (`BuildView` один раз на `(client, tick)`), а четвёрку Rev. 1 трактовать не как вердикт интерфейса, а как **внутреннее правило кодировщика**, выводимое из view (§3.3). `GhostVisible` явно отложить в Phase 7 и записать это в ADR-014, иначе значение enum останется неопределённым. Это не косметика: batch-контракт — единственная форма, при которой 400 юнитов × 10 клиентов × 10 Гц не превращаются в 40 000 виртуальных вызовов в секунду.

#### 0.3.2 Per-client `KnownSet` из Rev. 1 небезопасен при потере пакета

Rev.1 §3.2/§3.3 хранит на клиенте-слоте bitmap `KnownSet` и мутирует его при кодировании. Проверка клиентской стороны показывает, что это приводит к **шторму re-baseline**:

- `ApplyAdd` для уже известного entity возвращает `DuplicateEntity` и инкрементирует `DuplicateAddCount` (`ClientReplicationWorld.cs:294-295`);
- приемник на любой не-`Ok` результат вызывает `Fail(...)` (`ClientReplicationReceiver.cs:360-364`);
- `Fail` = `WorldPartiallyApplied = true` + `InconsistentApplyCount++` + **`_fsm.ReportBaselineLoss`** (`:493-503`).

То есть повторный ADD для сущности, которую клиент уже держит, — это не «идемпотентный ремонт», а немедленный полный 16-килобайтный keyframe. Любая схема «откатить `KnownSet` к подтверждённому и переиздать ADD» (Rev.1 риск 7.5) попадает ровно в этот случай: окно между отправкой и ack — 2 тика (feedback 10 Гц), значит при любой потере C2 откат переиздаёт ADD'ы уже применённых сущностей.

**[R2] Решение (§3.4):** не хранить пер-клиентское *множество* вообще. Видимость — детерминированная функция тика, поэтому набор записей для клиента выводится из пары тиков `(baseTick, curTick]` по **кольцу истории видимости**. Ремонт становится идемпотентным по построению: повторное слияние того же диапазона даёт байт-в-байт те же ADD/REMOVE, потому что у сервера нет_MUTABLE состояния, которое могло бы разъехаться.

#### 0.3.3 OD-36 (перенос хоста в `GlobalFront.Server`) заблокирован одной конкретной зависимостью

`LocalMatchHost.cs:1-9` не содержит ни одного `using UnityEngine` — класс переносим. Но у него есть ровно одна клиентская зависимость:

```csharp
public void BindReconnectCoordinator(ClientReconnectCoordinator coordinator)  // LocalMatchHost.cs:316
    => coordinator.ReadyToResume += Resume;
```

`ClientReconnectCoordinator` живёт в `namespace GlobalFront.Client` и сам зависит от `GlobalFront.Client.Replication`, поэтому в Server не переезжает. Инвариант `ArchitectureInvariantsTests.ServerAssembly_HasZeroReferencesToUnityOrClient` (`:186-204`) после наивного переноса **упадёт**.

**[R2] Фикс:** метод удалить, подписку инвертировать на стороне вызывающего — `coordinator.ReadyToResume += host.Resume` (`Resume` публичен). Затрагивает ровно 2 места, оба в тестах (`ReconnectionIntegrationTests.cs:36`, `Match2v2PlaytestScenarioTests.cs:111`). Rev. 1 эту блокировку не упоминает.

#### 0.3.4 `FixedStepClock` копит backlog **неограниченно**

`FixedStepClock.Advance`: `_accumulatorSeconds += elapsedSeconds`, затем `executedTicks = Math.Min(pendingTicks, _maxCatchUpTicks)` и `_accumulatorSeconds -= executedTicks * _tickDurationSeconds`. Клампа накопителя нет. Следствие: одна sustained просадка кадра > 200 мс оставляет излишек в аккумуляторе **навсегда** — сервер хронически отстаёт от wall-clock, `BacklogSeconds` растёт без предела, а catch-up 4 тика не догоняет, потому что каждый кадр добавляет столько же, сколько съедают.

Rev.1 риск 7.9 описывает симптом («отставание симуляции»), но не предлагает решения и не замечает неограниченности.

**[R2] Хорошая новость:** тактическая пауза backlog **не** копит — `AdvanceRealTime` выходит по `_isPaused` до `_clock.Advance(...)` (`TickDriver.cs:196-200`). Значит 200 с паузы безопасны.

**[R2] Решение (§2.4):** явный кламп `MaxBacklogSeconds` (предложение: 0.25 с = 5 тиков) **на уровне хоста**, а не `FixedStepClock` (чтобы не менять Core-контракт и не ломать существующие тесты тикера), плюс счётчик отброшенного времени в телеметрию. Это сознательная дилатация времени сервера: матч играет по серверным тикам, клиенты их интерполируют, поэтому «сервер чуть медленнее стены» невидимо, а неограниченный backlog — это неограниченный рост задержки.

#### 0.3.5 RTT-измерения не существует

Rev.1 §4.3: «RTT измеряем существующим Ping/Pong на 1 Гц». Реально: `Ping`/`Pong` **только отвечают** — `ServerTransportHost.HandlePing` (`:577-589`) и `ClientTransportEndpoint` (`:323-333`) эхом возвращают timestamp. Инициатора нет ни у кого, RTT не вычисляется нигде, `NetworkHudPresenter.UpdatePing` (`:175`) имеет **ноль** вызывающих.

**[R2]** Это работа шага 4.2, а не «проводка». Три требования, которые Rev. 1 не учитывает:
- инициатор — клиент, каденс 1 Гц, канал C0 (надёжный);
- timestamp в `EncodePingPong` — это `ITransportClock.NowMs` **сервера**; клиент обязан считать RTT по **своим** часам как `nowMs − sentAtMs` по локальному эху и никогда не вычитать серверный timestamp (разные часовые пояса часов → отрицательный RTT);
- хранить EWMA, а не сырое значение, иначе `MaxRenderDelayTicks`-логика буфера (§4.3) будет дёргаться.

#### 0.3.6 Риск Rev.1 7.1 (per-client scratch-буферы) закрывается существующим кодом

Один общий `_deltaBuffer[8228]` (`ServerReplicationEmitter.cs:354`) безопасен: `SendToSession` синхронный и оба carrier'а копируют в собственный буфер до возврата — `LiteNetLibCarrier` пишет в общий `_sendScratch` под `_sendGate` (`:42-51,294-340`), `OwnDatagramCarrier` — в `_sendBuffer` (`:107-108`). `TransportReplicationAdapter.SendToSession` (`ServerReplicationContracts.cs:93-94`) ссылку не удерживает.

**[R2]** Понижаю P1 → P3: вместо 10 × 16 КБ буферов достаточно одного регрессионного теста «два клиента, разные срезы, оба payload'а дошли различимыми».

#### 0.3.7 Пропущенный Rev. 1 факт: `ClientState` не хранит `PlayerId`

`SessionAttached(SessionId, MatchId, PlayerId)` передаёт `PlayerId` (`ServerReplicationContracts.cs:55`), но `ClientState` (`ServerReplicationEmitter.cs:291-318`) хранит только `Session` — игрок **отбрасывается**. Без этого поля невозможна ни фильтрация, ни пер-клиентская чексумма. Это prerequisite шага 4.3, и он отсутствует в плане Rev. 1.

---

## 1. Executive Summary & Architectural Decisions (вход для ADR-014)

### 1.1 Главный вывод

Фундамент Phase 2/3 подходит под интеграцию лучше, чем предполагает бриф: шов F-11 запиннен тестами, `TransportReplicationAdapter` готов, `ImpairmentProfile` уже эмулирует задержку/дроп/дубликаты/порчу/партицию, 2v2-сценарий с четырьмя легами и сверкой чексумм уже написан, `FogOfWarHidden` уже прошит по проводу и на клиенте. Phase 4 — это **четыре новых механизма** (серверный runner, удалённый клиентский флоу, модель видимости, пер-клиентская фильтрация) **плюс шесть конкретных проводок**, а не «соединение двух стеков».

Одновременно найдено одно **количественное** ограничение, которое меняет приоритеты: при 400 движущихся юнитах дельта занимает 54…98 % потолка `MaxPayloadBytes = 8192`, а multipart-дельты в коде **зарезервированы и не используются** (`ServerReplicationEmitter.cs:606-608`, `ClientTransportReplicationBridge.cs:418-423`). Переполнение → `_keyframeFallbackCount++` → 16-килобайтный keyframe вместо дельты. **Fog of War в этой архитектуре — не только анти-мапхак, но и единственный доступный в Phase 4 механизм удержания полосы** (§3.1). Это стоит зафиксировать в ADR-014 как вторую мотивацию OD-39.

### 1.2 Решения (дополнение и правка таблицы Rev. 1)

Rev.1 OD-35…OD-44 подтверждаются, кроме оговорённого ниже. Новые и исправленные:

| # | Решение | Основание |
|---|---|---|
| **OD-36 (уточнение)** | Перенос `LocalMatchHost` → `GlobalFront.Server\ServerHost.cs` выполняется **только после** удаления `BindReconnectCoordinator` и инверсии подписки в 2 тестах. Иначе падает `ServerAssembly_HasZeroReferencesToUnityOrClient`. | §0.3.3 |
| **OD-39 (уточнение)** | Фильтр — batch-контракт OD-16 (`BuildView` один раз на `(client, tick)`), а не per-record `Classify`. Видимость хранится в **кольце истории видимости** (per player, 120 тиков, 128 B/тик), а не в пер-клиентском `KnownSet`. | §0.3.1, §0.3.2, §3.4 |
| **OD-40 (уточнение)** | Пер-клиентская чексумма считается по **пост-дельта** множеству `Visible(curTick)` в порядке возрастания `EntityId` итерацией по уже захваченному буферу. Без сортировки и без аллокаций: серверный захват уже отсортирован вставкой. | `MatchServer.cs:432-433`, §3.6 |
| **OD-45 (новое)** | **Кламп backlog серверного тика.** Хост ограничивает накопитель `FixedStepClock` величиной `MaxBacklogSeconds = 0.25` и считает отброшенное время. `FixedStepClock` не менять. | §0.3.4, §2.4 |
| **OD-46 (новое)** | **Каденс репликации — функция pacing-токенов, и при 400 юнитах он уже деградировал до 5 Гц.** Refill 2048 B/тик при дельте ~4.4…8.0 КБ даёт одну отправку раз в 3…4 тика. Для возврата к OD-12 (10 Гц) нужен refill 4096 B/тик = 80 КБ/с — внутри цели OD-13 (50–100 КБ/с) и под soft cap 128 КБ/с. Решение принимать **по замеру**, не априори. | §2.5, §4.3 |
| **OD-47 (новое)** | **RTT-контур строится заново**: клиентский инициатор Ping 1 Гц на C0, локальный расчёт `nowMs − sentAtMs`, EWMA, и только потом `NetworkHudPresenter.UpdatePing`. Серверный timestamp из эха не вычитать. | §0.3.5 |
| **OD-48 (новое)** | **Мультипарт-дельты остаются зарезервированными.** Документация `MaxPayloadBytes` («split into several packets») противоречит коду и должна быть исправлена вместе с введением фильтра: после FoW размер дельты определяется размером *видимого* среза, и потолок 8192 B становится достижимым только за пределами ~740 одновременно движущихся юнитов у одного зрителя. | §0.3.6, §2.5 |

### 1.3 Что в Phase 4 делать **не** следует

- Не вводить client-side prediction движения (§4.4) — клиент не владеет симуляцией, а `DuplicateEntity`/`WorldInconsistent` превращают любое расхождение в re-baseline.
- Не делать per-client кольцо истории дельт (Rev.1 альтернатива OD-39): 120 тиков × 10 клиентов × `ReplicationChangeSetBuffer` — память и поломка ack-семантики. Кольцо остаётся общим и нефильтрованным.
- Не реализовывать `GhostVisible`/«память разведки» в Phase 4: клиентская сторона уже корректно освобождает слот по `FogOfWarHidden` (`ClientReplicationWorld.cs:372-374`), а ghost-слой попал бы в чексумму и сломал OD-40.
- Не строить консольный .NET-хост (подтверждаю OD-35 Rev. 1): `LiteNetLib.dll` — vendored precompiled под Unity (`Assets\_GlobalFront\ThirdParty\LiteNetLib\`), `GlobalFront.Server.asmdef` ссылается на него через `overrideReferences`/`precompiledReferences`, а второй build pipeline — это вторая поверхность дефектов при нулевой выгоде для M1.

---

## 2. Dedicated Server & Pump Engine (Шаг 4.1)

### 2.1 Форма сервера

Unity 6000.6.2f1 (`ProjectSettings\ProjectVersion.txt`), build subtarget `Server` → scripting define `UNITY_SERVER`. Проект уже наполовину готов:

- `GlobalFront.Client.asmdef:19` — `defineConstraints: ["!UNITY_SERVER"]`: вся клиентская сборка (включая `LocalMatchHost` и весь презентационный стек) в серверной сборке **не компилируется**;
- шесть файлов уже имеют `#if !UNITY_SERVER`-_guard'ы (`RtsCameraController.cs:217`, `RtsInputManager.cs:151`, `TacticalVerticalSliceRunner.cs:1081`, `PrototypeHud.cs:24,38`, `PrototypeRtsController.cs:187,207,216,229`);
- `GlobalFront.Server.asmdef` — `noEngineReferences: true`, без constraints → присутствует всегда.

**Критическая дыра, которую Rev. 1 назвал, но не раскрыл:** единственный production entry point — `GlobalFrontRuntimeBootstrap` с `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]` (`:14-15`), и он в **Client**-сборке. В серверной сборке не запускается ничего: `EditorBuildSettings.asset` содержит один `SampleScene`, в котором ноль GlobalFront-компонентов. Server-сборка сегодня стартовала бы пустой.

При этом `[RuntimeInitializeOnLoadMethod]` нельзя положить в `GlobalFront.Server` — там `noEngineReferences: true`, и это ассертится (`ArchitectureInvariantsTests.ServerAssembly_HasZeroReferencesToUnityOrClient:186-204`).

**Вывод: третья сборка обязательна**, как и написано в Rev.1 §2.2:

```
GlobalFront.Core         без изменений (noEngineReferences: true, references: [])
GlobalFront.Server       + ServerHost.cs (переезд ядра LocalMatchHost, §0.3.3)
GlobalFront.ServerHost   НОВЫЙ asmdef: references Core+Server, noEngineReferences: false,
                         defineConstraints: ["UNITY_SERVER"]
   ├─ DedicatedServerBootstrap.cs   [RuntimeInitializeOnLoadMethod(AfterSceneLoad)]
   └─ DedicatedServerRunner.cs      MonoBehaviour: цикл Pump + AdvanceRealTime
GlobalFront.Client       − LocalMatchHost.cs, + [Obsolete]-обёртка
```

`defineConstraints: ["UNITY_SERVER"]` (а не его отсутствие) — обязательно: без него runner попадёт и в клиентскую сборку, где `Application.targetFrameRate = 120` сломает рендер.

### 2.2 Цикл: порядок операций и почему он именно такой

```
Update():
  nowMs = _clock.NowMs                                  // Stopwatch, монотонный
  elapsed = (nowMs - _lastPumpMs) / 1000.0; _lastPumpMs = nowMs

  1. _transportHost.Pump(nowMs, _host.Server.CurrentTick)   // ДРЕНАЖ СОКЕТОВ ДО ТИКОВ
  2. _host.AdvanceRealTime(clamp(elapsed))                  // тики → TickDue → TickOnce
                                                              // → emitter.OnTickCompleted
                                                              // → sessions.OnTickCompleted
  3. если пауза: _host.PumpPausedSlices()                   // слайсы реконнектящимся
```

Обоснование порядка, по коду:

- **Пункт 1 до пункта 2.** Команды, принятые в этом кадре, должны попасть в `CurrentTick`, иначе они получают лишний тик задержки. `Schedule` всё равно клампит `requestedTick <= CurrentTick → CurrentTick + 1` (`MatchServer.cs:820`), так что дренаж до тика даёт исполнение на ближайшем тике, а после — на следующем.
- **`Pump(nowMs, drainTick)` с явным тиком**, а не `Pump(nowMs)`: одноаргументная версия берёт `_server.CurrentTick` в момент вызова (`ServerTransportHost.cs:100`), что корректно, но явная передача устраняет неоднозначность при вызове из обработчика `TickDue`.
- **`_currentDrainTick` используется при потере соединения**: `HandleConnectionLost` берёт `atTick = _currentDrainTick > 0 ? _currentDrainTick : _server.CurrentTick` (`:248-249`) — от него отсчитывается grace. Если `Pump` не вызывать во время паузы, grace будет считаться от устаревшего тика. Отсюда требование DoD: **сокеты дренажатся и во время тактической паузы** (Rev.1 риск 7.7 — подтверждаю, это не теория).

### 2.3 Джиттер и CPU

| Механизм | Значение | Эффект |
|---|---|---|
| `Application.targetFrameRate = 120` | кадр ~8.3 мс | опоздание тика после дедлайна ≤ 8.3 мс = 1/6 тика |
| Sleep-based pacing Unity | — | CPU холостого хода доли процента; **никакого spin-wait не нужно** |
| Номера тиков на проводе | `DeltaSnapshotHeader.Tick` | детерминизм и интерполяция вообще не зависят от wall-time |
| LNL worker thread | `PollEvents()` + `Thread.Sleep(1)` (`LiteNetLibCarrier.cs:445-474`) | приём не привязан к кадрам; события копятся в bounded-очереди 4096 (`TransportProtocol.cs:202`) |
| `LiteNetLibCarrier.Pump(nowMs)` | **no-op** (`:422-425`) | для LNL `Pump` нужен только чтобы drainage событий и `_currentDrainTick`; RTO/keepalive живёт внутри LNL |

Важное различие, которое Rev. 1 не проговаривает: у `OwnDatagramCarrier.Pump(nowMs)` (`:289-302`) семантика другая — он **сам** двигает reliable-таймеры (`ProcessAcks`, `ProcessTimers`, `:954-1061`) и забирает датаграммы из пайпа. Пропустить `Pump` на виртуальном пайпе = остановить сеть целиком. На LNL пропуск `Pump` = переполнение очереди событий. DoD-тест должен покрывать оба carrier'а.

Точный pacing-поток (`CreateWaitableTimerEx` с `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, Windows 10 1803+, не требует `timeBeginPeriod`) — подтверждаю вывод Rev. 1: в Phase 4 не делать. Но зафиксировать в ADR как точку расширения: он потребовал бы нарушения главного контрактa «эмиттер и `MatchServer` однопоточны» (`ServerTransportHost.cs:17-19`, `TransportSessionBinder.cs:14-15` — ни одного `lock`), а это дороже, чем выигрыш в 8 мс джиттера, который клиент и так гасит задержкой 150–600 мс.

### 2.4 Бюджет тика < 5.0 мс — где реальная стоимость

Структурно (всё проверено по коду):

| Стадия | Кратность | Комментарий |
|---|---|---|
| `TickOnce` (команды → clear targets → auto-acquire → move → combat) | 1 × на тик | `MatchServer.cs:635,661,831-846` |
| `CopySnapshots(Span<ServerUnitSnapshot>)` | 1 × на тик | zero-alloc, `_orderedUnits` уже отсортирован (`:739-752`) |
| `CaptureAndRecord` + `_engine.Diff` + `_ring.RecordTick` | **1 × на тик, общий для всех клиентов** | `ServerReplicationEmitter.cs:488-529` |
| `TryMergeRange` + классификация view + кодирование | **N × на тик** (N = клиенты) | `:565-571`, общий `_builder` → строго последовательно |
| Пер-клиентская чексумма | N × раз в 20 тиков | §3.6 |
| Grace sweep | O(дисконнектов в grace) | `SessionManager.cs:703-740` |

**Единственный член, растущий с числом игроков, — merge+encode.** При 400 юнитах и 10 клиентах это 10 проходов по общему `_builder`. Оценка Rev.1 «1.5–2.5 мс» **не измерена**; я её не подтверждаю и не опровергаю — вместо этого требую замер как DoD:

> **DoD 4.1 (бенчмарк-гейт):** EditMode-тест на виртуальном пайпе: 1 сервер + 10 клиентов, 400 юнитов, все движутся, 200 тиков. Утверждения: (а) медиана `Pump + AdvanceRealTime` < 5.0 мс; (б) p99 < 12 мс; (в) `_keyframeFallbackCount == 0`; (г) `Is.Not.AllocatingGCMemory()` на всём окне; (д) `BacklogSeconds` не растёт монотонно.

Пункт (в) — самый важный: он ловит клифф §2.5 до того, как он уедет в production.

Техники, которые уже в проекте и которые нельзя ослаблять: предаллоцированные scratch (`_captures[2][4096]`, `_addScratch[4096]`, `_deltaBuffer[8228]`, `_sliceBuffer`, per-client `KeyframeStaging` 163 840 B), запрет LINQ IL-тестом (`ArchitectureInvariantsTests:299`), запрет float-полей в авторитетных типах (`:227`), запрет `System.Random`/`DateTime.Now` (`:501`) и `Guid`/crypto-RNG/`Stopwatch` в авторитетных неймспейсах (`:558`).

**[R2] Замечание к последнему:** `VisibilityEngine` обязан попасть под действие теста `:558`, если его положить в авторитетный неймспейс. Значит — никаких `Stopwatch` внутри, только целочисленная математика над `WorldPointMm`. Это согласуется с OD-39 Rev. 1, но Rev. 1 не связывает это с конкретным IL-тестом.

### 2.5 **[R2]** Полоса пропускания: количественный клифф, который определяет дизайн

Факты:
- `MaxPayloadBytes = 8192`, `MaxPacketBytes = 8228` (`DeltaSnapshotProtocol.cs:52,55`);
- при `GetEncodedSize(...) != Ok || encodedSize > MaxPacketBytes` → `_keyframeFallbackCount++` + `QueueKeyframe(client)` (`ServerReplicationEmitter.cs:601-612`);
- комментарий там же: *«multipart deltas are reserved, not used»* (`:606-608`); клиент на `PartCount > 1` только инкрементирует `_multipartDeltaCount` (`ClientTransportReplicationBridge.cs:418-423`);
- UPDATE-запись: 2 B минимум, 44 B максимум = 10 (id varint) + 1 (mask) + 1 (owner) + 8 (position) + 4 (health) + 1 (hasMoveTarget) + 8 (moveTarget) + 10 (attackTarget) + 1 (autoAcquire) (`DeltaSnapshotProtocol.cs:74-78`);
- pacing refill 2048 B/тик (`ServerReplicationEmitter.cs:22`).

Оценка размера дельты (формула, не замер):

```
encodedSize ≈ 36 + Σ per changed unit (2..44)
400 юнитов, dirty = Position only       : 400 × 11 ≈ 4 436 B  → 54 % потолка
400 юнитов, dirty = Position+MoveTarget : 400 × 20 ≈ 8 036 B  → 98 % потолка
≈410 юнитов с MoveTarget-churn          : > 8 228 B           → keyframe fallback КАЖДЫЙ тик
≈740 юнитов position-only               : > 8 228 B           → keyframe fallback КАЖДЫЙ тик
1024 юнита (MaxUnitsPerMatch)           : 11 264…20 480 B     → дельта невозможна в принципе
```

Стоимость fallback'а: keyframe на 400 юнитов = 400 × 40 B = 16 000 B → `_recordsPerSlice = (16384−24)/40 = 409` → 1 слайс, но pacing 2048 B/тик → **8 тиков = 400 мс** на доставку, и всё это время клиент не получает дельт.

**Следствие для каденса.** Даже без переполнения pacing ограничивает частоту: 4 436 B при refill 2048 B/тик = одна отправка раз в ~2.2 тика ≈ 9 Гц; 8 036 B = раз в ~4 тика = **5 Гц**. То есть OD-12 («базовая частота 10 Гц, при всплесках потерь 5 Гц») на 400 юнитах деградирует до 5 Гц **в норме**, без единой потери. Это напрямую бьёт по §4.3: интервал прибытия 4 тика → `delay = 2·4 + 0.5·jitter` ≥ 8 тиков при потолке 12.

**Два вывода для ADR-014:**
1. **FoW-фильтрация — это и механизм полосы.** Клиент получает только видимый срез: в типовом 1v1/2v2 столкновении это 100–250 юнитов из 400, то есть дельта 1.1–5.0 КБ вместо 4.4–8.0 КБ, и каденс возвращается к 10 Гц без изменения pacing. Это сильнее аргумента «анти-мапхак», потому что анти-мапхак можно отложить, а клифф — нет.
2. **Нужен измеренный выбор refill (OD-46).** 4096 B/тик = 80 КБ/с на клиента — внутри цели OD-13 (50–100 КБ/с) и под soft cap 128 КБ/с. Но принимать его следует только по замеру §2.4(в), иначе мы поднимаем полосу, не зная, упираемся ли мы в неё.

---

## 3. Fog of War & Per-Client Replication Design (Шаг 4.3)

### 3.1 Принцип

Мапхак-устойчивость даёт **только** фильтрация на отправке. Lockstep (SC2, AoE2) принципиально уязвим: у клиента есть полная симуляция, чит читает её память. Проект по ADR-005 выбрал authoritative snapshot server — правильную сторону; тот же путь у Spring/BAR (серверная LOS-фильтрация), SupCom/PA (interest management), Quake 3 / Source (relevancy + delta против последнего подтверждённого кадра).

Нормативное следствие: **сервер не отправляет скрытого туманом врага ни в дельтах, ни в keyframe'ах, ни в чексуммах.** Скрытие на рендере — презентационная надстройка Phase 7, не мера защиты.

И, как показано в §2.5, та же фильтрация — единственный доступный в Phase 4 способ удержать дельту под 8192 B на 400+ юнитах.

### 3.2 Текущая готовность протокола (важно: почти всё уже есть)

| Элемент | Состояние | Файл:строка |
|---|---|---|
| `DeltaRemoveCause.FogOfWarHidden = 1` | **определён** | `DeltaSnapshotModel.cs:12-23` |
| Валидация cause ≤ FogOfWarHidden в кодеке | **есть** (encode и decode) | `DeltaSnapshotWireCodec.cs:683,1010` |
| Клиентский учёт | **есть**: `FogOfWarRemoveCount` | `ClientReplicationWorld.cs:273-274,372-374` |
| Серверная генерация | **нет и явно отказана**: «fog-of-war hiding is a filtering concern of a later step and is not produced here»; все removal'ы — `Destroyed` | `ServerSnapshotDiffEngine.cs:41-42,138` |
| Модель видимости в Core | **отсутствует полностью**. Нет `SightRange`/`VisionRange`/LOS ни в одном файле Runtime | grep по `Runtime\Core`, `Runtime\Server` |
| Единственный радиус | `AutoAcquireRangeMm = 18000` (18 м) — это радиус автозахвата цели, **не** обзор | `SimulationConstants.cs:33` |
| `HasTombstoneEcho` | зарезервирован, не выставляется | `DeltaSnapshotProtocol.cs:113` |

**Вывод:** Phase 4 не меняет wire-формат вообще. Меняются (а) Core — новая детерминированная модель видимости, (б) Server — новая стадия фильтрации между `TryMergeRange` и `TryEncode`, (в) пер-клиентская чексумма. `DeltaSnapshotProtocol.Version` остаётся 2.

### 3.3 Контракты (сохраняем OD-16, добавляем источник видимости)

```csharp
// GlobalFront.Server.Snapshot — batch-контракт OD-16 БЕЗ ИЗМЕНЕНИЙ (RND 2.6 §13.2).
public interface IReplicationFilter {
    void BuildView(in ReplicationContext context, ReplicationViewBuilder builder);
    VisibilityDecision DecideVisibility(in ReplicationContext context, EntityId entity); // slow-path policy hook
}
public enum VisibilityDecision : byte { FullVisible = 0, GhostVisible = 1, Hidden = 2 }
// GhostVisible в Phase 4 не реализуется; фиксируется в ADR-014 как отложенный (Phase 7).

// НОВОЕ. Источник видимости, отделённый от снимка мира.
public interface IVisibilitySource {
    // Заполняет view зрителя на данный тик одним проходом. Вызывается РОВНО один раз
    // на (viewer, tick) — это и есть batch hot path OD-16.
    void BuildVisibleView(PlayerId viewer, ulong tick, ReplicationViewBuilder builder);
}
```

`ReplicationViewBuilder` — переиспользуемый, предаллоцированный при attach: bitmap `ulong[16]` = 1024 бита = `MaxUnitsPerMatch` (`SimulationConstants.cs:21`), **128 B на клиента**. Никаких `HashSet`, никакого boxing.

Правила кодировщика (внутренние, не интерфейс) выводятся из двух view — на `baseTick` и на `curTick`:

| Состояние | Видим на `curTick` | Видим на `baseTick` | Действие |
|---|---|---|---|
| `Reveal` | да | нет | синтезировать `DeltaAddRecord` (40 B) из **текущего** захвата |
| `Keep` | да | да | пропустить merged-записи как есть |
| `Hide` | нет | да | синтезировать `DeltaRemoveRecord { Cause = FogOfWarHidden }` |
| `Suppress` | нет | нет | merged-записи выкинуть |

Это ровно четвёрка Rev. 1, но **выводимая из истории видимости**, а не из мутируемого `KnownSet` (§3.4).

Интеграция в эмиттер: `ServerReplicationEmitterConfig` дополняется `IVisibilitySource VisibilitySource = null` (аддитивно; `null` = сегодняшнее поведение «весь мир», все 1195 тестов не регрессируют) — и `ClientState` дополняется полем `PlayerId` (§0.3.7).

### 3.4 **[R2]** Кольцо истории видимости вместо `KnownSet`

Причина (§0.3.2): дублирующийся ADD фатален — `DuplicateEntity` → `Fail` → `ReportBaselineLoss` → keyframe. Любая схема с мутируемым пер-клиентским множеством при потере C2 даёт либо пропущенный ADD (→ UPDATE для неизвестной сущности → тоже `Fail`, `ClientReplicationReceiver.cs:368-378`), либо повторный ADD (→ `Fail`). Оба пути — 16 КБ re-baseline.

Решение: видимость — детерминированная функция тика, поэтому множество записей выводится из `(baseTick, curTick]`:

```csharp
// НОВОЕ. GlobalFront.Server.Replication
// Зеркалит глубину ReplicationHistoryRing.Capacity = 120, чтобы оба кольца
// выселялись синхронно: если base tick выпал из истории дельт, он выпал и здесь,
// и путь один — keyframe (существующий).
public sealed class VisibilityHistoryRing {
    public const int Capacity = ReplicationHistoryRing.Capacity;      // 120
    private const int WordsPerPlayer = (SimulationConstants.MaxUnitsPerMatch + 63) / 64; // 16
    private readonly ulong[] _slots = new ulong[Capacity * MaxPlayers * WordsPerPlayer];
    // 120 × 10 × 16 × 8 B = 153 600 B, выделено один раз
    public void Record(ulong tick, PlayerId viewer, ReadOnlySpan<ulong> mask);
    public bool TryGet(ulong tick, PlayerId viewer, Span<ulong> destination);
}
```

Тогда per-client проход в `StreamDeltas` (`ServerReplicationEmitter.cs:565-612`):

```
baseTick = Max(client.LastAckedTick, client.LastSentTick)      // уже есть, :565-567
merged   = _ring.TryMergeRange(baseTick, tick, _builder)        // уже есть, :568-571
если VisibilitySource != null:
    VisibilitySource.BuildVisibleView(client.Player, tick,     _viewCur)   // 1 вызов
    _visibilityHistory.TryGet(baseTick, client.Player,         _viewBase)  // 1 чтение
    // Reveal = viewCur \ viewBase ; Hide = viewBase \ viewCur ; Suppress = ¬viewCur
    // ADD синтезируются из _captures[_captureIndex] (текущий захват, :684-685)
```

Свойства, которые это даёт бесплатно:
- **Идемпотентный ремонт.** Повторное слияние того же `(base, cur]` даёт те же записи: нет mutable-состояния, которое могло бы разъехаться. Rev.1 риск 7.5 («вечный re-baseline цикл») снимается по построению, а не тестом.
- **Ремонт без keyframe.** Потерянная дельта восстанавливается существующим `DeltaResume` (C0, reliable) — сервер пере-сливает диапазон и пере-выводит view. `HasTombstoneEcho` остаётся зарезервированным.
- **Стоимость.** `Record` = 16 `ulong`-записей на игрока на тик (160 записей/тик на 10 игроков) — пренебрежимо. Память 150 КБ — пренебрежимо против 10 × 163 840 B `KeyframeStaging` = 1.6 МБ, которые уже выделены.

Ограничение, которое надо принять честно: `VisibilityHistoryRing` хранит 120 тиков, а `ReplicationHistoryRing` — тоже 120. Если клиент отстал дальше, оба кольца дают отказ и путь один — keyframe. Это существующее поведение, фильтрация его не ухудшает.

### 3.5 Переходы Enter/Exit Fog

**Enter (юнит впервые входит в зону видимости).** Юнит мог существовать тиками раньше — в merged change-set для него может быть только UPDATE или вообще ничего. Классификация `Reveal` → эмиттер строит полный `DeltaAddRecord` (40 B) из **текущего захвата**, а не из исторической записи. Клиент: `ApplyAdd` берёт свободный слот (штатный путь ADD после baseline), binder на следующем `Render` выдаёт view из пула.

**Exit (юнит уходит из видимости).** Юнит жив — это не смерть. Классификация `Hide` → `DeltaRemoveRecord { Cause = FogOfWarHidden }`. Клиент уже готов: `ClientReplicationWorld.cs:372-374` считает именно этот cause отдельно, слот освобождается, view возвращается в пул. «Память разведки» — Phase 7 (§1.3).

**Смерть невидимого юнита.** `REMOVE(Destroyed)` для сущности из `viewBase \ viewCur` не возникает: она классифицируется как `Suppress`. Никаких следов — это корректно и это же требование анти-мапхака.

**Потеря пакета с REMOVE(FogOfWarHidden).** C2 unreliable latest-wins. Клиент обнаруживает разрыв → `DeltaResume` на C0 → сервер пере-сливает `(lastApplied, cur]` и пере-выводит view → REMOVE переиздаётся **идемпотентно** (§3.4). Отдельный tombstone echo не нужен.

**[R2] Кейфрейм при фильтрации.** Per-client keyframe staging уже существует (`KeyframeStaging`, 163 840 B на клиента, переживает detach). Фильтр применяется **на этапе стейджинга**, а не при отправке слайсов — иначе слайсы разных клиентов делят один буфер (Rev.1 риск 7.12 — подтверждаю как P1). В keyframe попадают только `viewCur`; `TotalUnitCount` в `KeyframeSliceHeader` становится per-client. Механика слайсинга (409 записей на слайс), `KeyframeSeq` и его монотонность сквозь detach не меняются.

**[R2] AttackTarget, указывающий на невидимую сущность.** UPDATE-запись класса `Keep` может нести `AttackTarget`, которого клиент не знает (автозахват 18 м срабатывает на сервере по своим правилам). Проверено: клиент хранит `AttackTarget` как число без валидации, и `Fail` на этом не возникает. Требуется явный тест, что презентационный слой **не разыменовывает** `AttackTarget` в entity — иначе `KeyNotFoundException`/промах в `UnitViewBinder`.

**Гистерезис.** Юнит на границе радиуса обзора даёт ADD/REMOVE-шторм. Конфиг-крючок `FogExitConfirmTicks` (1 = мгновенно, 2 = держим тик после выхода из LOS). Рекомендация: v1 без гистерезиса (подтверждаю Rev.1), но с **лимитером переходов** — не более K Reveal/Hide на клиента на тик, сверх которого клиенту уходит keyframe вместо шквала записей. K = 64 как стартовое значение (оценка, не замер).

### 3.6 Пер-клиентская `StateChecksum`

Текущее состояние: `ComputeStateChecksum()` **без параметров** (`ServerReplicationEmitter.cs:679-711`) вызывается внутри пер-клиентского цикла (`:622-629`), но итерирует общий захват `_captures[_captureIndex]` → **все клиенты получают одно и то же число**. При FoW это гарантированные ложные re-baseline.

Пер-клиентская версия:

```csharp
private uint ComputeStateChecksum(PlayerId viewer, ReadOnlySpan<ulong> viewCur)
{
    uint hash = 2166136261u;                       // тот же FNV-1a offset basis
    for (var i = 0; i < _captureCounts[_captureIndex]; i++)
    {
        ref readonly var unit = ref _captures[_captureIndex][i];
        if (!IsSet(viewCur, unit.Entity)) continue; // O(1), bitmap
        hash = MixChecksumByte(hash, unit.Entity.Value);
        hash = MixChecksumByte(hash, (ulong)unit.Owner.Value);
        hash = MixChecksumByte(hash, (ulong)unit.Position.X);
        hash = MixChecksumByte(hash, (ulong)unit.Position.Z);
        hash = MixChecksumByte(hash, (ulong)unit.CurrentHealth);
        hash = MixChecksumByte(hash, unit.HasMoveTarget ? 1UL : 0UL);
        hash = MixChecksumByte(hash, unit.HasMoveTarget ? (ulong)unit.MoveTarget.X : 0UL);
        hash = MixChecksumByte(hash, unit.HasMoveTarget ? (ulong)unit.MoveTarget.Z : 0UL);
        hash = MixChecksumByte(hash, unit.AttackTarget.Value);
        hash = MixChecksumByte(hash, unit.AutoAcquireEnemies ? 1UL : 0UL);
    }
    return hash;
}
```

Четыре инварианта, каждый со своим основанием в коде:

1. **Поля и порядок — байт-в-байт как на клиенте.** Клиентская версия `ClientReplicationWorld.cs:458-502` смешивает ровно этот список в этом порядке, с обнулением `MoveTarget` при `!HasMoveTarget` (OD-14, `:492-495`). Сервер делает то же (`:701-702`). Любое расхождение = вечная ложная рассинхронизация. `UnitKind` **не** смешивается ни там, ни там — согласованно.
2. **Порядок обхода — возрастание `EntityId`, без сортировки.** Серверный захват уже отсортирован: `_orderedUnits.Add(record); _orderedUnits.Sort(...)` при каждой вставке (`MatchServer.cs:432-433`), и `CopySnapshots` копирует в этом порядке (`:739-752`). Фильтрация bitmap'ом порядок сохраняет. Клиент при этом **сортирует явно** — heapsort по возрастанию (`ClientReplicationWorld.cs:504-552`). Результаты совпадают, и это надо зафиксировать тестом (существующий `ReplicationRebaseIntegrationTests.StateChecksum_ServerAndClient_EntityOrderAgreement` расширяется на случай включённого фильтра).
3. **Множество = пост-дельта.** Приёмник применяет ADD/UPDATE/REMOVE, затем `NoteApplied`, и **только потом** сверяет чексумму (`ClientReplicationReceiver.cs:355-404`). Значит сервер обязан считать по `Visible(curTick)`, а не по `Visible(baseTick)`.
4. **Zero-GC.** Итерация по уже захваченному буферу + побитовая проверка — ноль аллокаций. `MixChecksumByte` (`:714-725`) — чистая арифметика.

Стоимость: 400 итераций × 10 клиентов × 1 Гц = 4 000 итераций/с. Пренебрежимо (подтверждаю оценку Rev.1).

Протокол не меняется: то же поле `DeltaSnapshotHeader.StateChecksum`, тот же флаг `HasChecksum`. Клиент **не знает**, что его чексумма «осечённая» — он считает её по своему миру, как и раньше.

---

## 4. Command Pipeline & Latency Management (Шаг 4.4)

### 4.1 Жизненный цикл команды (проверено по коду, а не по схеме Rev. 1)

```
[Клиент, кадр t0]
  RtsInputManager → UnitSelectionDriver.ManualUpdate → UnitSelectionController.TryIssueCommandAtPointer
  → Emit(...) — блоки ≤ MaxSelectedEntities = 100 (SimulationConstants.cs:13), свой Sequence на блок
  → ICommandChannel (сегодня в production: LocalCommandChannel, TacticalVerticalSliceRunner.cs:1204)
  → NetworkCommandChannel.TrySubmitMove/Attack/Stop (:44,68,90)
      • гейт: _endpoint.State != Established → SessionRejected (:50-53)
      • проверка числа сущностей (:55-59)
      • CommandWireCodec.Encode* в предаллоцированный буфер (Version=1, HeaderSizeBytes=15)
  → ClientTransportEndpoint.TrySendCommand (:123-135) — заголовок Command + Token, канал C1

[Сервер]
  LNL worker thread принимает → bounded очередь событий (4096)
  → ServerTransportHost.Pump(nowMs, drainTick) (:106)
      • RateLimiter.TryConsume (:258-269) — 200 pkt/s, 256 КБ/с (TransportProtocol.cs:197-198)
      • HandleCommand (:503): binder по токену И connectionId (:505-509) → DroppedBadToken
      • CommandWireCodec.TryDecode — структурная валидация (:512)
      • SessionManager.ValidateCommand (:524 → SessionManager.cs:659-695):
          UnknownSession → SessionClosed → NoBoundMatch → SessionNotConnected
          → PlayerMismatch (:681) → MatchNotRunning
      • MatchServer.TryEnqueue* (:539-549):
          ValidateCommonHeader (:766-793): MatchOver → окно −20/+60 (:127,130,775)
                                          → UnsupportedCommandType → DuplicateSequence (:786-790)
          ValidateControllable (:795-813): UnknownEntity → NotEntityOwner (:802) → EntityNotAlive (:807)
          Attack: цель существует/жива (:562-570), FriendlyTarget через _combatRelation.AreEnemies (:577-580)
      • Schedule (:818-829): executeTick = requestedTick <= CurrentTick ? CurrentTick + 1 : requestedTick
      • CommandAck (7 B, MessageCodec.cs:347) на C0 немедленно (:555-560)

[Тик T_exec]
  TickOnce → ExecuteScheduledCommands(CurrentTick) (:661,831-846)
  каноническая сортировка PlayerId → Sequence (:838,1099-1107)

[Клиент]
  CommandAckReceived → поверхность отказа в HUD (пламбинг LastRejection существует)
  результат движения/боя — дельтами C2 → UnitViewTickBuffer → UnitViewBinder
```

Отличие от схемы Rev. 1: `Schedule` не создаёт «буфер» — он **минимизирует** задержку (`CurrentTick + 1`). Весь буфер — это клиентский lead в `RequestedTick` (§4.3).

### 4.2 Мгновенная реакция без prediction

Что уже работает локально и останется локальным: выделение применяется сразу; маркер приказа в точке клика; звук. Что приходит с сервера: факт движения и боя.

**[R2] Бюджет задержки отклика (это надо записать в ADR, чтобы не спорить потом).** Воспринимаемая задержка приказа:

```
RTT/2  (C1 вверх)  +  ≤50 мс (ожидание ближайшего тика)  +  renderDelay × 50 мс (буфер вида)
= 25…75 мс         +  ≤50 мс                            +  150…600 мс
= 225…725 мс
```

Доминирующий член — `renderDelay`, а не сеть. При каденсе 5 Гц (§2.5) `delay = 2·4 + 0.5·jitter` ≥ 8 тиков = 400 мс, и это **норма сегодняшнего кода**, а не дефект Phase 4. Отсюда два следствия:

1. Мгновенный UI-фидбек обязателен, потому что 400 мс без него ощущаются как сломанное управление.
2. Поднятие каденса до 10 Гц (OD-46) снижает воспринимаемую задержку сильнее, чем любая работа над RTT: 8 тиков → 4 тика = **−200 мс**. Это главный аргумент в пользу refill 4096 B/тик.

### 4.3 Почему нет client-side prediction (подтверждаю OD-41, с новыми основаниями)

1. **Клиенту нечего предсказывать.** Дельта-модель реплицирует состояние, не ввод. На клиенте нет ни симуляции, ни пассфайндинга, ни боевой логики (`ClientReplicationWorld` — хранилище слотов). Prediction потребовал бы второй копии симуляции в Client-сборке — прямой конфликт с ADR-005.
2. **[R2] Приемник структурно не терпит расхождений.** Любое не-`Ok` применение → `Fail` → `ReportBaselineLoss` → keyframe (`ClientReplicationReceiver.cs:360-364,493-503`); два промаха чексуммы подряд → то же (`:402-423`). Локально предсказанное состояние, не совпавшее с авторитетным, — это не «резиновый отскок», это 16 КБ re-baseline на каждое расхождение.
3. **Резиновые отскоки поверх интерполяции.** Юниты и так рисуются на 3–12 тиков позади авторитета.
4. **Индустрия.** SC2 и AoE исполняют команды с задержкой и движение юнитов не предсказывают; мгновенность достигается звуком, маркером и анимацией клика.

### 4.4 Lead-расчёт `RequestedTick`

```
requestedTick = lastAppliedTick          // последний применённый серверный тик (FSM)
              + renderDelayTicks         // насколько буфер вида отстаёт (3…12)
              + ceil(halfRttEwma / 50)   // путь команды вверх; halfRtt из OD-47
              + 2                        // запас на кадр ввода и один тик рейса
```

Сервер клампит в `max(requested, CurrentTick + 1)`, окно −20/+60 поглощает огрубление. Пределы: при `renderDelay = 12`, RTT 150 мс → `requested = lastApplied + 12 + 2 + 2 = lastApplied + 16`. `lastApplied` отстаёт от `CurrentTick` примерно на `renderDelay + RTT/2` = 12 + 1.5 ≈ 13 тиков, значит `requested − CurrentTick ≈ +3` — глубоко внутри окна `AcceptedFutureTicks = 60`. **Запас есть, динамическая подстройка lead в Phase 4 не нужна.**

UX-свойство формулы: член `renderDelayTicks` гарантирует, что приказ исполнится не раньше, чем игрок увидел сцену, по которой кликнул (vision-sync). Строгая гарантия (`T_exec` = тик, который игрок реально видит) — опция Phase 7; маппинг render-tick → server-tick уже есть (`UnitViewTickBuffer.RenderTick`).

**[R2] Требование к DoD 4.4, которого нет в Rev. 1:** тест должен утверждать не «команда принята», а `CurrentTick − AcceptedPastTicks ≤ requestedTick ≤ CurrentTick + AcceptedFutureTicks` **на всём прогоне**, при профиле `Wan` (100 мс, 5 % потерь). Иначе lead-формула непроверяема, а окно −20/+60 молча поглощает ошибки.

---

## 5. Live Combat Reconnect Protocol (Шаг 4.5)

### 5.1 Протокол с точными таймерами

```
[Сервер] LNL idle timeout / graceful disconnect
  → ServerTransportHost.HandleConnectionLost (:215-254)
      • освобождает limiter и pending acks (:219-229)
      • ClientRequested → _sessions.CloseSession (:240)
      • иначе → _sessions.NotifyConnectionLost(session, atTick), atTick = _currentDrainTick (:248-249)
      • _binder.Release + SessionDetached (:252-253)
  → SessionManager.NotifyConnectionLost (:417-443): Connected → Disconnected,
      DisconnectedAtTick записан, SessionDisconnected (:441)
  → хост (AutoPauseOnDisconnect, default true): TickDriver.Pause()
      • тики стоят; AdvanceRealTime выходит ДО _clock.Advance → backlog НЕ копится (§0.3.4)
  → grace: 4000 тиков = 200.0 с (SessionManager.cs:99, OD-18)
      • истечение: OnTickCompleted, tick > DisconnectedAtTick + grace (:717) → SessionGraceExpired (:738)
  → эмиттер: KeyframeStaging сохранён, NextKeyframeSeq монотонно растёт сквозь detach
  → во время паузы PumpPausedSlices() качает слайсы (ServerReplicationEmitter.cs:1122)
  → все клиенты: TacticalPauseOverlay

[Клиент] endpoint.Lost(reason)
  → ClientReconnectCoordinator.OnTransportDisconnected (:98-107) → Reconnecting
  → TryBuildReconnectRequest (:113-124): 64 B, opcode 12, C0, zero-GC

[Сервер] ReconnectRequest → HandleReconnectRequest (:271-277,335-409)
  → SessionManager.TryRebindSession (:528-620)
      • FixedTimeEquals по SessionSecret32 (:547-555) — анти-timing
      • внутри grace → State = Connected, новый handle, слот Connected (:595-609)
      • после grace → GraceExpired → Abandoned; PlayerId НЕ переиспользуется
  → binder rebinding (:379-381), SessionReattached (:383)
  → ReconnectResponse (92 B, opcode 13): currentTick + activeKeyframeSeq (:385-402)
  → PostSessionReattached (:407)

[Клиент] coordinator.OnReconnectResponse (:129-154)
  → _replicationReceiver.PrepareForResync(response.ActiveKeyframeSeq) (:146)
  → ResyncStarted (:147) → Resyncing
  → bridge.BindUplink(newUplink) — перепривязка к новому сокету

[Сервер] emitter.OnSessionReattached → форс-keyframe (per-client, FoW-фильтрованный §3.5)
  → слайсы по C2; в паузе — через PumpPausedSlices

[Клиент] сборка слайсов (assembly timeout 1500 мс, ClientTransportReplicationBridge.cs:199,585-594;
         дубли отбрасываются :498-502, устаревший seq :457-468, порядок по возрастанию PartIndex :521-534)
  → receiver.ReceiveKeyframe → World reset + ADD всех → FSM baseline
  → ★ ПРОВОДКА 1: OnKeyframeInstalled() → Resyncing → Countdown
  → ★ ПРОВОДКА 2: binder.HandleResync(keyframeTick)
        ReleaseAllViews() — 400 view В ПУЛ, без Destroy (UnitViewBinder.cs:293-306)
        + buffer.Resync(keyframeTick) — Flush + re-anchor (:864-882)
  → следующий Render: 400 × (Pool.Pop + SetActive) — ноль Instantiate, ноль аллокаций

[Countdown] coordinator.AdvanceCountdown (:173-194) → 5.0 с (:41, OD-20) → ReadyToResume (:70)
  → host.Resume() → TickDriver.Resume → симуляция пошла
```

### 5.2 Три отсутствующие проводки (проверено grep'ом)

| Проводка | Статус | Доказательство |
|---|---|---|
| baseline-событие приёмника → `coordinator.OnKeyframeInstalled()` | **ноль production-вызовов**; вызывается только из 3 тестовых файлов | grep: `ClientReconnectCoordinator.cs:160` + `ClientReconnectTests.cs:241,262,316,399,433`, `ReconnectionIntegrationTests.cs:149,476`, `Match2v2PlaytestScenarioTests.cs:384` |
| `UnitViewBinder.HandleResync(snapshotTick)` | **ноль production-вызовов**; только 2 теста | grep: `UnitViewBinder.cs:282` + `UnitOverlayTests.cs:418`, `UnitViewBinderTests.cs:727` |
| `NetworkHudPresenter.UpdatePing(player, pingMs)` | **ноль вызовов вообще**, источника RTT нет (§0.3.5) | grep: единственное вхождение — само объявление `:175` |

Механизм события для проводки 1: `ClientReconnectCoordinator` уже публикует `ResyncStarted` (`:67`) и `ReadyToResume` (`:70`); не хватает обратного — сигнала «baseline установлен». Минимальное изменение контракта: опрос `Fsm.HasBaseline` при переходе из `Unbased/Rebasing`, либо новое событие в `ClientReplicationReceiver`. Первое дешевле и не расширяет поверхность.

`ReadyToResume → host.Resume` уже связано через `LocalMatchHost.BindReconnectCoordinator` (`:316-324`) — но этот метод подлежит удалению (§0.3.3), значит подписка переезжает в композиционный слой клиента. **Это надо сделать в одном шаге, иначе связь потеряется молча.**

### 5.3 Презентационный бёрст 400 юнитов за один кадр

Почему это уже спроектировано, а не надежда:
- `UnitViewPool.AllowCombatGrowth = false` (`:142`) → в steady state `TryAcquire` (`:260-301`) только снимает с per-kind `Stack<UnitView>`; `Instantiate` невозможен;
- `Warmup(kind, count)` (`:199-240`) вызывается по одному разу на архетип при инициализации среза (`TacticalVerticalSliceRunner.cs:1552-1566`) → пул уже прогрет до 400;
- `ReleaseAllViews` возвращает view в пул, а не уничтожает (`UnitViewBinder.cs:293-306`) → ресинк = 400 pop'ов из тёплого стека;
- оверлеи перестраиваются батчами по 250 инстансов на следующем кадре (`UnitOverlayBatcher`), 0 B GC.

**[R2] Единственный реальный риск бёрста — не пул, а `UnitViewTickBuffer.Resync`.** После `Flush()` буфер пуст, и первый `TrySample` (`:674-838`) попадает в ветвь «единственный образец» → `Snapped` (`:733-742`): все 400 юнитов телепортируются на авторитетные позиции. Это корректно и ожидаемо после ресинка (мир застыл на паузе, интерполировать нечего), но это надо **утвердить как поведение тестом**, а не обнаружить в плейтесте. Второе: `Reprime()` при разрыве ≥ 32 тиков (`:458-461,1133-1143`) — после 200 с паузы разрыв именно такой, так что `Resync(snapshotTick)` обязателен, иначе репрайм сработает на каждом юните.

### 5.4 Grace 200 с — решение, которое надо принять в ADR-014

`DefaultDisconnectGraceTicks = 4000` = 200 с (OD-18, принято владельцем 2026-09-19). Для 1v1/2v2 с работающим реконнектом это замораживает матч на 3.3 минуты. Rev.1 (вопрос 4 приложения B) предлагает 30 с — поддерживаю, но с двумя оговорками:

- менять надо **конфиг матча**, а не константу: `LocalMatchHost.DefaultDisconnectGraceTicks = 4000` (`:46`) и `SessionManager.DefaultDisconnectGraceTicks = 4000` (`:99`) — две копии, и тесты опираются на обе. Одна константа меняется в одном месте, иначе они разъедутся (это уже случалось: F-3 — три разных ёмкости в трёх местах).
- тесты обязаны использовать dramatically уменьшенный grace (10–20 тиков), иначе каждый тест реконнекта ждёт 200 с виртуального времени.

---

## 6. План шагов 4.1 – 4.7

Общие DoD для всех шагов: EditMode + PlayMode green, 0 `warning CS` / 0 `error CS` после принудительной перекомпиляции, ноль аллокаций в измеренных циклах **с позитивным контролем рекордера**, архитектурные инварианты не ослаблены, evidence-файл в `Artifacts/TestResults/*.xml` (авторитет — строка `<test-run total= passed= failed=>`, не код возврата).

Порядок: **4.1 → 4.5(wiring) → 4.2 → 4.3 → 4.4 → 4.6 → 4.7**. Обоснование изменения против Rev.1 (§6 «4.1, 4.2, 4.5, 4.3, 4.4»): проводки 4.5 стоят ~50 строк, разблокируют полноценные 2v2-тесты и снижают риск того, что 4.2 придётся переделывать; 4.3 — самый большой и должен идти после того, как удалённый клиент уже работает.

### 4.1 Dedicated Server Runner & Pump

**Файлы:** NEW `GlobalFront.ServerHost` asmdef (`defineConstraints: ["UNITY_SERVER"]`), `DedicatedServerBootstrap.cs`, `DedicatedServerRunner.cs`; MOVE `Client\LocalMatchHost.cs` → `Server\ServerHost.cs` **после** удаления `BindReconnectCoordinator` (§0.3.3); NEW Build Profile «Windows Server»; кламп backlog (OD-45); teardown по `MatchPhase.Closed`.

**Контракты:** `ServerHost` (новое имя), `GlobalFront.Server.asmdef` (состав файлов). Core и wire-протоколы не трогаются.

**DoD:**
1. `ServerTransportHost.Pump` имеет ≥ 1 production call-site (закрывает F-10, задокументированный в Block 5 как «scheduled for Phase 4 Step 4.1»).
2. Server-сборка стартует headless, обслуживает 2 клиентов, завершает матч, уходит в `Closed`; клиент получает `Lost(ServerShutdown)`, а не `Timeout`.
3. EditMode-тесты runner'а на виртуальном пайпе + `VirtualTransportClock` — **без wall-clock**.
4. Бенчмарк-гейт §2.4: 400 юнитов × 10 клиентов, медиана < 5.0 мс, p99 < 12 мс, `_keyframeFallbackCount == 0`, 0 B GC, `BacklogSeconds` не растёт монотонно.
5. Тест «пауза → коннекты и команды продолжают обрабатываться» (закрывает риск 7.7).
6. `ArchitectureInvariantsTests` — все 12 green без ослаблений, в частности `ServerAssembly_HasZeroReferencesToUnityOrClient`.

### 4.2 Client Remote Connection & Handshake

**Файлы:** `TacticalVerticalSliceRunner` (+ `SliceNetworkMode { LocalLoopback, ListenServer, DedicatedClient }`), NEW `RemoteSliceSession` (композиция `ClientTransportEndpoint` + `ClientTransportUplink` + `ClientTransportReplicationBridge` + `ClientReconnectCoordinator`), `NetworkHudPresenter` (connect-минимум: IP/порт/статус), RTT-контур (OD-47).

**Контракты:** `IReplicationTransport` / `IReplicationUplink` **не меняются** — шов F-11 уже достаточен, правок в runner'е не требуется (§0.2.8).

**DoD:**
1. Два клиента на реальном UDP loopback (`LiteNetLibCarrier`) играют утверждённый loop 1v1: select → move → attack → stop; ack'и видны.
2. RTT-контур: инициатор на клиенте, EWMA, `UpdatePing` вызывается (закрывает ноль-вызовов из §0.3.5), значение правдоподобное на loopback (< 5 мс).
3. `RequestedTick` в окне −20/+60 **на всём прогоне** под профилем `Wan`.
4. PlayMode end-to-end localhost. **Требуется правка `Tests\PlayMode\GlobalFront.Tests.PlayMode.asmdef`**: сегодня он не ссылается на `LiteNetLib.dll` (в отличие от EditMode), поэтому real-UDP PlayMode-тест сейчас не компилируется.
5. `LocalReplicationLoopback` остаётся путем по умолчанию: все существующие 1195 тестов не регрессируют.

### 4.3 Fog of War / Per-Client Filtering

**Файлы:** Core NEW `VisibilityEngine` (+ `SightRangeMm` в юнит-статах, `FowBucketMm`); Server NEW `IReplicationFilter`/`IVisibilitySource`/`ReplicationContext`/`ReplicationViewBuilder` (batch-контракт OD-16), `VisibilityHistoryRing` (§3.4), интеграция в `ServerReplicationEmitter` (+ `ClientState.PlayerId`, §0.3.7; + конфиг `VisibilitySource`, null = passthrough).

**Контракты:** `ServerReplicationEmitterConfig` аддитивно; `ClientState` +1 поле; новые Core-типы. **Wire-формат не меняется** — `Version` остаётся 2, `FogOfWarHidden` уже в протоколе.

**[R2] Ограничение, которое надо принять до начала:** `SightRangeMm` — новое поле в юнит-статах. `SimulationConstants` документирован как «часть сетевого протокола, изменения только через compatibility review». Если радиус обзора кладётся в статы, он влияет на симуляцию → на `AutoAcquireEnemies`-поведение → потенциально на детерминизм. Рекомендация: обзор **не** входит в `ServerUnitSnapshot` и **не** смешивается в чексумму (как `UnitKind`), видимость — отдельный поток данных (§3.3), протокол не меняется.

**DoD:**
1. **Мапхак-тест:** вражеский юнит вне LOS ни разу не появляется в `ClientReplicationWorld` за N тиков — включая keyframe'ы после re-baseline и включая `DeltaResume`-ремонт.
2. Enter → ADD (синтезированный из текущего захвата), Exit → `REMOVE(FogOfWarHidden)`; потеря C2 самовосстанавливается через `DeltaResume` **без keyframe** (это прямая проверка §3.4; при `KnownSet`-дизайне Rev.1 тест провалится).
3. Per-client чексуммы сервера и клиента бит-равны при включённом фильтре; passthrough-режим не регрессировал.
4. Анти-вакуумность: тест, что фильтр **действительно** что-то скрывает (счётчик скрытых записей > 0), иначе «мапхак-тест» проходит и на passthrough.
5. Замер FoW: `VisibilityEngine` O(N·k) на grid'е; утверждение «400 юнитов, rebuild < 0.5 мс». Наивный O(N²) на 1024 юнитах = 1 M проверок/тик — недопустим, grid с самого начала.
6. `[R2]` Полоса: тот же прогон, что DoD 4.1(4), но с фильтром — утверждение `_keyframeFallbackCount == 0` при 400 юнитах и замер фактического размера дельты (§2.5). Это доказательство того, что FoW снимает клифф.
7. `AttackTarget`, указывающий на невидимую сущность, не роняет презентацию (§3.5).

### 4.4 Authoritative Command Ingress

**Файлы:** production-проводка `NetworkCommandChannel` в режиме `DedicatedClient`, lead-расчёт (§4.4), HUD-поверхность отказов.

**DoD:**
1. E2E через **реальный** транспорт: чужой юнит → `NotEntityOwner`; мёртвый → `EntityNotAlive`; спуф-токен → `DroppedBadToken`; дубликат sequence → `DuplicateSequence`. Сегодня это покрыто только через виртуальный пайп.
2. Ack round-trip < RTT + 2 тика.
3. Ни одна команда не исполняется до `max(requested, CurrentTick + 1)` — утверждение по `executeTick`, а не по факту принятия.
4. Блок > 100 сущностей (`MaxSelectedEntities`) разбивается на блоки с собственным `Sequence`, все блоки доходят (паттерн, запинненный в sprint 3.7 part 7).

### 4.5 Live Combat Reconnect

**Файлы:** три проводки §5.2; конфиг grace (§5.4).

**DoD:**
1. Интеграционный тест: **400 юнитов в бою** → disconnect → пауза → reconnect → keyframe → 5 с countdown → resume → мир поле-в-поле равен доаварийному (в FoW-срезе этого зрителя).
2. Презентационный бёрст: 400 view восстановлены за один `Render`, `Is.Not.AllocatingGCMemory()`; поведение `Snapped` после `Flush` утверждено явно (§5.3).
3. Двойной реконнект подряд: `KeyframeSeq` монотонен, старое поколение дельт отброшено (`PrepareForResync`).
4. `[R2]` Реконнект **после** истечения grace → `GraceExpired` → `Abandoned`, `PlayerId` не переиспользован.
5. `[R2]` Дисконнект во время keyframe-in-flight: `KeyframeStaging` сохранён, `PendingNextPart` не даёт рваный слайс.

### 4.6 Test Pyramid & Networked Harness

Существующее (переиспользовать, не переписывать):
- `VirtualNetworkPipe` + `ImpairmentProfile` — задержка, джиттер, дроп, дубликаты, порча, партиция, seed (`:85-94`);
- `VirtualTransportClock : ITransportClock` (`TransportTestSupport.cs:15-20`) — ручное время;
- `Rig` / `CreateRig` (`TransportTestSupport.cs:35-83`) — 1 сервер + N клиентов через один пайп, реальные `MatchServer` + `SessionManager` + `ServerTransportHost` + `OwnDatagramCarrier`;
- `ReplicationIntegrationWorld.Create(profile, emitterConfig, clientCount, disconnectGraceTicks)` (`ReplicationIntegrationSupport.cs:77-107`) + `AddClient` (`:109-125`);
- `PumpTick` / `PumpUntilEstablished` / `PumpUntilCaughtUp` (`:156-208`, `TransportTestSupport.cs:117-137`);
- `Match2v2PlaytestScenarioTests` — 4 лега, бит-равенство чексумм, полный цикл реконнекта (`:94-98,162-254,256-447,449-527`).

**Главный пробел:** 2v2-сценарий создаётся с `new ImpairmentProfile()`, то есть **нулевая задержка и нулевые потери**, и всего на 4 юнитах. Шаг 4.6 — это в первую очередь **параметризация существующей фикстуры**, а не написание новой.

**Новое:**
- пресеты профилей: `LanReliable { Latency 5, Loss 0 }`, `Wifi { Latency 40, ReorderJitter 20, Loss 0.01 }`, `Wan { Latency 100, ReorderJitter 50, Loss 0.05 }`;
- `NetworkedGameplayIntegrationTests` (1 сервер + 2 клиента, полный loop, **400 юнитов**);
- `FogOfWarReplicationTests`, `DedicatedRunnerTests`, `CommandIngressNetworkTests`, `MidCombatReconnectTests`;
- control-тесты (anti-vacuity) на каждое «отсутствие» — обязательный паттерн проекта.

**Инварианты в каждый сетевой тест:** `ChecksumMismatchCount == 0`; `InconsistentApplyCount == 0`; `DuplicateAddCount == 0` (**[R2]** новый — именно он ловит дефект §0.3.2); `_keyframeFallbackCount == 0` в сценариях без потерь; `RejectedMessageCount == 0` в чистых сценариях; `Is.Not.AllocatingGCMemory()` на pump-цикле клиента и на `Pump + AdvanceRealTime` сервера; детерминизм — два прогона с одним `Seed` дают бит-равные миры; мапхак-инвариант.

**Zero-GC дисциплина (проверено):** канонический инструмент — Unity `GC.Alloc` recorder в двух формах: constraint `Is.Not.AllocatingGCMemory()` и счётчик `CountCurrentThreadGcAllocations` через `Recorder.Get("GC.Alloc")` (`LiteNetLibCarrierIntegrationTests.cs:900-922`). Слепые зоны, которые надо учитывать: `GC.GetAllocatedBytesForCurrentThread()` возвращает абсолютный 0 в editor Mono (`:513-515`, `TransportPerformanceBaselineTests.cs:168-171`) — **не использовать**; `FilterToCurrentThread()` не видит аллокации LNL worker-потока. Каждый «нулевой» тест — с позитивным контролем.

**DoD:** рост 1195 → ≥ 1260; PlayMode: real-UDP loopback 1v1 (после правки asmdef) + 2v2 virtual; ни один новый тест не использует wall-clock, кроме явно помеченных LNL-тестов.

**[R2] Замечание о flaky-риске:** единственные wall-clock тесты сегодня — `LiteNetLibCarrierIntegrationTests` (`Thread.Sleep(5)` poll-циклы с дедлайнами 10–40 с, `:81-86,120-126,218-224,306,415`). Расширять real-UDP набор надо осознанно: каждый такой тест — потенциальный flake на нагруженной машине. Основной объём 4.6 должен жить на виртуальном пайпе.

### 4.7 ADR-014, документация, аудит

**Файлы:** `DECISIONS.md` (+ ADR-014: OD-35…OD-48), `ARCHITECTURE.md` (сборка `ServerHost`, граф зависимостей), `CURRENT_STATE.md`, `CHANGELOG.md`, `Phases\Phase_04_Multiplayer_Integration.md`, evidence в `Artifacts\`, независимый adversarial audit по образцу Grand Audit.

**[R2] Обязательная часть шага — закрытие doc-drift, найденного в этом исследовании:**
1. `MaxPayloadBytes` doc-comment обещает «split into several packets sharing the same Tick» — кода нет (`ServerReplicationEmitter.cs:606-608`). Исправить на «multipart зарезервирован, переполнение → keyframe» (OD-48).
2. `Phases\Phase_04_Multiplayer_Integration.md:3` — «Пререквизиты Phase 2 и Phase 3 полностью завершены и сертифицированы `[APPROVED: ZERO DEFECTS]`». Это формулировка, которую Block 5 (F-6) уже устранял в 8 других документах; канон — `[P0/P1 = 0 CERTIFIED]` с указанием, что сертификат `4cb8ffa` — строгий adversarial re-audit с scope P0/P1 = 0.
3. Out-of-jail residuals из Block 5 всё ещё открыты: `AI_PROJECT_CONTEXT.md:510,520`, `MASTER_GAME_PLAN.md:41`, `Phases\README.md:9`, `Documentation\README.md:20,22`. Phase 4 — естественный момент их закрыть.
4. `Phase_04_Multiplayer_Integration.md:31` перечисляет `IReplicationFilter` как «Key Systems» без пометки, что контракт отсутствует в коде и специфицирован только в OD-16.

**DoD:** P0/P1 = 0 по итогам независимого аудита, Milestone M1 принят.

---

## 7. Риски и Zero-GC ограничения

| # | Риск | Приоритет | Превенция (с основанием в коде) |
|---|---|---|---|
| 7.1 | **[R2] Дублирующийся ADD → keyframe-шторм.** `DuplicateEntity` фатален | **P1** | Кольцо истории видимости вместо `KnownSet` (§3.4); инвариант `DuplicateAddCount == 0` в каждом сетевом тесте |
| 7.2 | **[R2] Дельта > 8228 B → keyframe fallback каждый тик** на 400+ юнитах | **P1** | FoW-фильтрация (§2.5, §3.1); гейт `_keyframeFallbackCount == 0` (§2.4, DoD 4.3.6); решение по refill 4096 B/тик — только по замеру (OD-46) |
| 7.3 | **[R2] Неограниченный backlog `FixedStepClock`** → хроническое отставание сервера | **P1** | Кламп `MaxBacklogSeconds = 0.25` на уровне хоста + счётчик (OD-45); пауза backlog не копится (проверено, `TickDriver.cs:196-200`) |
| 7.4 | **[R2] Инверсия `BindReconnectCoordinator` молча теряет `ReadyToResume → Resume`** | **P1** | Перенос хоста и инверсия подписки — в одном коммите; тест «countdown finished → тики пошли» обязан остаться green после переноса |
| 7.5 | Пер-клиентский keyframe-стейджинг: фильтр должен применяться при стейджинге, не при отправке слайсов | **P1** | Per-client буферы уже изолированы (163 840 B каждый); тест «2 клиента с разными срезами → разные keyframe'ы, одновременный приём» |
| 7.6 | `VisibilityEngine` в Core — риск детерминизма | **P1** | Целочисленная математика, фиксированные ячейки grid, стабильный порядок итерации; попадает под `ArchitectureInvariantsTests:227,501,558`; тест «identical setups → identical visibility» |
| 7.7 | LNL worker thread vs главный поток | **P1** | Контракт уже такой: bounded очередь 4096, дренаж только в `Pump` на главном потоке; `ServerTransportHost`/`MatchServer`/`SessionManager` не имеют ни одного `lock` и однопоточны по документации (`:17-19`). Тест «пауза → сокеты продолжают дренажиться» |
| 7.8 | **[R2] RTT из серверного timestamp** → отрицательный/бессмысленный ping | **P2** | OD-47: считать только `nowMs − sentAtMs` по локальным часам клиента; тест «loopback RTT ∈ [0, 5] мс» |
| 7.9 | Re-baseline шторм при одновременном отставании 10 клиентов | **P2** | Pacing-токены уже дозируют (refill 2048, burst 16384); раздельный бюджет токенов для keyframe и дельт; мониторинг `_keyframeFallbackCount`/`QueueDepth` |
| 7.10 | FoW-фликер на границе LOS → ADD/REMOVE-шторм | **P2** | Лимитер K переходов на клиента на тик (K = 64, оценка) → сверх него keyframe; телеметрия переходов; гистерезис-конфиг `FogExitConfirmTicks` |
| 7.11 | Управляемый стриппинг в Server-сборке | **P2** | Smoke-тест build profile в DoD 4.1; LiteNetLib рефлексии не использует |
| 7.12 | Отсутствие async-инварианта | **P2** | IL-тест по образцу LINQ-скана (`:299`): запрет `System.Threading.Tasks` в Core/Server, явное исключение для LNL worker-потока |
| 7.13 | **[R2] Две копии `DefaultDisconnectGraceTicks = 4000`** (`SessionManager.cs:99`, `LocalMatchHost.cs:46`) разъезжаются при переходе на конфиг | **P2** | Одна константа-источник; тест, что обе стороны согласованы (прецедент F-3: три ёмкости в трёх местах) |
| 7.14 | Пауза 200 с замораживает матч для всех | **P3** | OD-18 принят владельцем; конфиг матча (§5.4); не в M1 |
| 7.15 | `UnitViewPool.AllowCombatGrowth = false` + незнакомый `UnitKind` → юниты не презентируются | **P3** | Fallback `UnitKinds.Unknown` уже есть; `UnitKind` не в чексумме — согласованно |
| 7.16 | **[R2] Per-client scratch-буферы** (было P1 в Rev.1) | **P3** | Понижено по доказательству: `SendToSession` синхронный, оба carrier'а копируют до возврата (`LiteNetLibCarrier.cs:42-51,294-340`, `OwnDatagramCarrier.cs:107-108`), адаптер ссылку не удерживает. Достаточно одного регрессионного теста |

### 7.17 Zero-GC чек-лист для Phase 4

**Сервер, горячий путь** (захват → diff → merge → view → encode → send): аллокации допустимы только при attach/detach. Известные остатки, которые Phase 4 не должен увеличить:
- `new ClientState()` при attach и в `FindFreeSlot` (`ServerReplicationEmitter.cs:361-364,1185`) — one-time, допустимо;
- рост `KeyframeStaging` при мире > 163 840 B (`:752-755`) — off-hot-path по собственному комментарию;
- `new List<ScheduledCommand>()` на каждый новый ключ тика в `_scheduledCommands` (`MatchServer.cs:824`) — **симуляция, не эмиттер**; при 20 Гц это 20 аллокаций/с. Не блокирует Phase 4, но должно попасть в бэклог: пул списков закроет это за ~20 строк;
- транспорт: `ServerTransportHost.cs:657` (`new byte[offset]` в fallback отправки ack), `LiteNetLibCarrier.cs:680` (одна копия на доставленное сообщение), `OwnDatagramCarrier.cs:405,816,872` (на фрагмент, удерживается для ретрансмита), `FragmentAssembler.cs:179`.
  **[R2] Практическое следствие:** гейт «0 B GC на сервере» надо ставить на **эмиттер + симуляцию**, а не на весь транспортный цикл, иначе он будет красным по причинам, которые Phase 4 не создавал. Это ровно та ошибка, из-за которой N-02 в Grand Audit оказался недооценён (аллокации на worker-потоке рекордер не видит).

**Клиент, горячий путь** (bridge decode → receiver apply → CaptureTick → Render → Sample): уже ассертится; расширяется на сетевой цикл. Проверить отдельно, что 4-slot rotating buffer приёма и фрагмент-риассембле C2 не аллоцируют на каждый пакет.

**Новое в Phase 4:** `VisibilityEngine.Rebuild` (один раз на тик, только целые массивы), `VisibilityHistoryRing.Record` (16 `ulong`-записей на игрока), `BuildVisibleView` (заполнение предаллоцированного bitmap). Все три — ноль аллокаций по построению; утвердить тестом `Emitter_HotPath_PerClient_WithFilter_DoesNotAllocate` (10 клиентов × 20 тиков, с позитивным контролем).

---

## Приложение A. Источники

**Проверено чтением кода на `bbfd19d`** (пути от `Assets\_GlobalFront\`): `Runtime\Core\SimulationConstants.cs`, `Core\FixedStepClock.cs`, `Core\Snapshot\{DeltaSnapshotProtocol,DeltaSnapshotModel,DeltaSnapshotWireCodec,KeyframeSliceCodec}.cs`, `Core\CommandWireCodec.cs`, `Core\Reconnect\*`; `Runtime\Server\{TickDriver,MatchServer}.cs`, `Server\Sessions\SessionManager.cs`, `Server\Transport\{ServerTransportHost,ClientTransportEndpoint,TransportProtocol,MessageCodec,VirtualNetworkPipe,OwnDatagramCarrier,LiteNetLibCarrier,ITransportClock,TransportSessionBinder}.cs`, `Server\Replication\{ServerReplicationEmitter,ServerReplicationContracts,ServerSnapshotDiffEngine,ReplicationHistoryRing,ReplicationChangeSetBuilder}.cs`; `Runtime\Client\{LocalMatchHost,NetworkCommandChannel,ClientReconnectCoordinator,GlobalFrontRuntimeBootstrap}.cs`, `Client\Replication\{ClientTransportReplicationBridge,ClientReplicationReceiver,ClientReplicationWorld,ReplicationReceiverFSM}.cs`, `Client\Presentation\{TacticalVerticalSliceRunner,UnitViewTickBuffer,UnitViewBinder,UnitViewPool}.cs`, `Client\UI\NetworkHudPresenter.cs`; `Tests\EditMode\{Architecture\ArchitectureInvariantsTests,LiteNetLibCarrierIntegrationTests,TransportTestSupport,Network\Match2v2PlaytestScenarioTests,Integration\Replication\ReplicationIntegrationSupport}.cs`; все пять `.asmdef`; `ProjectSettings\{ProjectVersion,EditorBuildSettings}.asset`.

**Документация:** `DECISIONS.md` (ADR-001…013, OD-10…OD-34), `Research\Phase_02_06_Delta_Snapshot_Protocol_RND.md` §13 (OD-16), `Research\Phase_04_Multiplayer_Gameplay_Integration_RND.md` (Rev. 1), `Phases\Phase_04_Multiplayer_Integration.md`.

**Индустриальные практики** (экспертная база, без свежих ссылок — при подготовке ADR-014 подтвердить точечно): SC2 / AoE2 — deterministic lockstep и его принципиальная мапхак-уязвимость, исполнение команд с задержкой, отсутствие prediction движения; Spring RTS / Beyond All Reason — серверная LOS-фильтрация юнитов (референс OD-39); SupCom / Planetary Annihilation — server-authoritative interest management; Quake 3 / Source — delta compression против последнего подтверждённого кадра и relevancy-фильтрация (референс уже реализованного ADR-010); Glenn Fiedler, «Snapshot Compression» / «Networked Physics» — relevancy и priority.

## Приложение B. Открытые вопросы для ревью ADR-014

1. **Контракт фильтра:** подтвердить batch-форму OD-16 (`BuildView`) и отказ от per-record `Classify` из Rev. 1 (§0.3.1). Это изменение уже принятого решения, нужен явный апрув.
2. **`GhostVisible`:** отложить в Phase 7 и записать в ADR, или удалить из enum? Rev. 1 молча его не реализует.
3. **Кольцо истории видимости против `KnownSet`** (§3.4): 150 КБ памяти и требование синхронной глубины с `ReplicationHistoryRing` против идемпотентного ремонта. Рекомендация — кольцо.
4. **Pacing refill** 2048 → 4096 B/тик (OD-46): принимать по замеру DoD 4.1(4) или оставить 40 КБ/с и жить с 5 Гц на 400 юнитах?
5. **Grace 200 с → 30 с** (§5.4) и перенос в конфиг матча: требуется решение владельца, OD-18 принимался явно.
6. **Перенос `LocalMatchHost` → `ServerHost`:** rename с `[Obsolete]`-обёрткой против сохранения имени. ~30 тестовых файлов используют `LocalMatchHost`; обёртка дешевле, но `[Obsolete]`-обёртка в Client-сборке не компилируется в Server-сборке, где она и не нужна.
7. **`MaxRenderDelayTicks = 12`** (600 мс) — поднимать ли потолок для WAN (RTT 150+)? Конфиг матча против константы.
8. **Консольный dotnet-хост** — подтвердить отказ в Phase 4 (OD-35), отложить на Phase 8.
