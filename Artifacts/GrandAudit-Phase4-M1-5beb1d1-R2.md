# Adversarial Grand Audit — Phase 4 / Milestone M1 (Multiplayer Prototype), round 2

- **HEAD:** `5beb1d1` (main), working tree: `ProjectSettings/ProjectSettings.asset` modified (LF/CRLF), `Artifacts/Audit-Phase4-M1-5beb1d1.md` untracked. Всё остальное чисто.
- **Дата аудита:** 2026-10-27
- **Метод:** zero-trust: каждый тезис документов сверен с кодом построчно; тестовые гейты прогнаны заново в живом редакторе; артефакты плейтеста прочитаны как первоисточник; скриншоты просмотрены.
- **Независимые прогоны тестов (этот аудит):**
  - EditMode: **1334/1334 passed, 0 failed, 0 skipped**, 31.15 s (job `d83c444a…`).
  - PlayMode: **22/22 passed, 0 failed, 0 skipped**, 60.94 s (job `e044ec62…`).
  - Коммиченные гейт-XML совпадают: `Artifacts/TestResults/step465-polish-editmode.xml` (`total=1334 passed=1334`), `step465-polish-playmode.xml` (`total=22 passed=22`).
- **Связь с раундом 1:** `Artifacts/Audit-Phase4-M1-5beb1d1.md`. Настоящий отчёт независим: все находки перепроверены по коду, часть классификаций изменена (см. §7).

---

## Резолюция: **REJECT** (закрытие Milestone M1 не разрешено)

P0 = 0 · P1 = 2 · P2 = 5 · P3 = 9

Причина: два P1 ломают заявленный scope и надёжность реконнекта; шесть P2 включают четыре расхождения «документация/артефакты против кода», то есть пакет доказательств M1 не подтверждает собственные заявления. Тесты зелёные, контракт владения буферами и per-client фильтрация корректны — но «1v1/2v2 prototype» и «reconnect в бою» в продакшн-композиции не существуют в заявленном виде.

---

## 1. Контракт владения буферами и пулинг (P0/P1-направление)

**Вердикт направления: корректно, P0/P1 нет.** Контракт «событие владеет массивом до возврата» соблюдён:

- Единственные точки возврата — `finally` в `ServerTransportHost.Pump` (`Runtime/Server/Transport/ServerTransportHost.cs:130-133`) и `ClientTransportEndpoint.Pump` (`Runtime/Server/Transport/ClientTransportEndpoint.cs:186-189`). Двойного возврата нет ни на одном пути: обработчики не ставят события в очередь повторно.
- Use-after-free нет: снапшот-диспетч копирует payload во вращающееся кольцо из 4 преаллоцированных буферов до возврата (`ClientTransportEndpoint.cs:501-505`); client-feedback декодируется синхронно из `ReadOnlySpan` (`ServerReplicationEmitter.cs:1154,1158`, `ServerReplicationContracts.cs:124-125`); фрагменты ассемблера возвращаются в пул при освобождении группы с обнулением слотов (`FragmentAssembler.cs:358-369`).
- Thread-safety пула корректна: `Rent`/`Return` под одним `Gate`-локом, бакеты создаются в статическом инициализаторе (`TransportBufferPool.cs:26-98`).

**Найдено (P2-1): пул теряет буферы на трёх путях LiteNetLibCarrier** — пул опустошается ровно в тех сценариях, против которых он построен:

1. `LiteNetLibCarrier.cs:660-673` — control-путь: `TransportBufferPool.Rent(controlLength)` выполняется **до** `limiter.TryConsume(...)`; при rate-limit буфер бросается без `Return`.
2. `LiteNetLibCarrier.cs:916-936` — `EnqueueEvent` при переполнении очереди (`DroppedQueueOverflow`) роняет Message-событие вместе с пуловым буфером.
3. `LiteNetLibCarrier.cs:459-462` — `Shutdown` очищает `_events`, не возвращая буферы queued-событий.

Последствия: бакет = 8 буферов; при флуде/медленном хосте пул осушается и каждый следующий пакет аллоцирует — zero-GC деградирует именно под adversarial-нагрузкой. Память не растёт неограниченно (осиротевшие массивы — мусор), поэтому P2, не P1.

**P3-1:** `TransportBufferPool.Warm()` пишет `_warmed` вне лока и пушит без проверки `BuffersPerBucket` (`TransportBufferPool.cs:30-49`) — гонка двойного прогрева даёт до 8 лишних буферов на бакет. Ограничено, безвредно, но data race на non-volatile флаге.

**P3-2:** `ServerTransportHost.cs:293-294` — диспетч opcode 14 читает `transportEvent.Data[0]` **без проверки длины** (в отличие от opcode 12 на строке 284). Нулевой по длине пакет (`Rent(0)` → `Array.Empty`) бросает `IndexOutOfRangeException`, пойманный в `Pump` (`ServerTransportHost.cs:124-129`): исключение на хост-потоке на каждый такой пакет, загрязнение счётчика `DroppedMalformed`.

---

## 2. Архитектурные границы и сборки (OD-35, OD-36)

**Вердикт направления: буква OD-35/OD-36 соблюдена, критерий «клиент не имеет прямого доступа к серверному состоянию» — нет (P3-3).**

Подтверждено:
- `GlobalFront.Server.asmdef`: `noEngineReferences: true`, references = `["GlobalFront.Core"]`, `overrideReferences: true`, `precompiledReferences: ["LiteNetLib.dll"]`. `GlobalFront.Core.asmdef`: `noEngineReferences: true`, references = `[]`.
- В деревьях `Runtime/Server/**` и `Runtime/Core/**` ноль вхождений `UnityEngine`, `MonoBehaviour`, `GameObject`, `Debug.Log`, `GlobalFront.Client`.
- `DedicatedServerRunner`/`DedicatedServerBootstrap` живут в `GlobalFront.ServerHost` (`Runtime/ServerHost/DedicatedServerRunner.cs:24`); из Core/Server на них ссылок нет.
- `UNITY_SERVER`: Client-asmdef имеет `defineConstraints: ["!UNITY_SERVER"]`.

Нарушения критерия изоляции:
- `GlobalFront.Client.asmdef` ссылается на `GlobalFront.Server` **и** `GlobalFront.ServerHost`.
- `LocalMatchHost : ServerHost` в клиентской сборке (`Runtime/Client/LocalMatchHost.cs:17`); OD-36 обещал инверсию связки через абстракцию — вместо этого клиент наследует авторитетный хост.
- `TacticalVerticalSliceRunner` держит хост публично (`Runtime/Client/Presentation/TacticalVerticalSliceRunner.cs:549`) и читает авторитетный мир напрямую (`:1560` — `new MatchServerSnapshotSource(_host.Server)`), а `PrototypeRtsController.cs:439` снимает `CopySnapshots` с `MatchServer` в обход репликации.
- `InteractivePlaytestBootstrap.cs:147` (Client) создаёт `DedicatedServerRunner` в процессе клиента: shipped-клиент содержит полный авторитетный сервер.

В режиме `DedicatedClient` in-process хоста нет, поэтому практический вектор maphack ограничен локальными/демо-путями — отсюда P3, а не P1/P2. Но пункт брифа «отсутствие прямого доступа клиента к серверному состоянию» не выполнен.

---

## 3. Fog of War и anti-maphack (OD-39, OD-40, OD-41)

**Вердикт направления: механика корректна и fail-closed; P1-1 (командный режим) и P3-4 (потолок EntityId).**

Подтверждено:
- `VisibilityEngine`: целочисленная сетка 8192 мм (`SimulationConstants.cs:41`), `FloorDiv` корректен для отрицательных координат (`VisibilityEngine.cs:626-635`), расстояния в `long` без переполнения (`:463-478`), dense-прямоугольник ≤ 16384 ячеек и hash-fallback с линейным зондированием (занятых ячеек ≤ 1024 < 4096 бакетов — зависание недостижимо), все буферы преаллоцированы в конструкторе.
- Пер-client дельты: `TryBuildFilteredDelta` (`ServerReplicationEmitter.cs:744-864`) реализует ровно 0→1 ADD / 1→0 REMOVE(FogOfWarHidden|Destroyed) / 1→1 merged UPDATE / 0→0 пропуск; при недоступной маске — fail-closed keyframe; `_engine.Buffer` переиспользуется как scratch и кодируется до следующего клиента.
- Кейфрейм фильтруется по маске (`StartKeyframe`, `:1016-1041`), при недоступной маске базис пустой (fail-closed).
- Per-client StateChecksum: сервер (`:908-942`) и клиент (`ClientReplicationWorld.cs:458-502`) мешают FNV-1a идентичный набор полей в идентичном порядке, включая гейт координат cleared move-target; детект десинка с re-baseline после 2 подряд несовпадений (`ClientReplicationReceiver.cs:412-433`).
- OD-41: гейт `TargetInFogOfWar` стоит **до** `Schedule` и `CommitSequence` (`MatchServer.cs:624-632`) — sequence на отказ не списывается. Секрет сессии сравнивается fixed-time (`SessionManager.cs:548-556`); opcode 14 привязан к сессии сокета (`ServerTransportHost.cs:449-454`).

**P1-1 (совместно с §4): 2v2 не подключён ни в одной продакшн- или тестовой композиции.**
- `DedicatedServerRunner.InitializeCore`: `_host = new ServerHost(tickRate)` (`Runtime/ServerHost/DedicatedServerRunner.cs:136`) → `new MatchServer()` → `SkirmishCombatRelation.Instance` (`MatchServer.cs:195`). `Team2v2CombatRelation` (`Runtime/Core/CombatRelation.cs:100`) не имеет ни одного вызова в Runtime — только `Tests/EditMode/Team2v2HostilityTests.cs`.
- `MatchConfig` не содержит поля команды вообще (grep `Team` по `Runtime/Core/MatchConfig.cs` пуст) — выразить 2v2 через конфигурацию матча невозможно.
- `FogOfWarReplicationFilter.SetPlayerTeam` (`FogOfWarReplicationFilter.cs:72`) не имеет ни одного вызова в Runtime: в 4-игровом dedicated-матче союзники враждебны друг другу (friendly fire легален) и невидимы дальше 20 м радиуса обзора.
- «2v2»-свидетельство (`Tests/EditMode/Network/Match2v2PlaytestScenarioTests.cs:94-98`) строится через `ReplicationIntegrationSupport.Create` → `AttachReplication(adapter, emitterConfig)` без фильтра (`ReplicationIntegrationSupport.cs:92`) и через `LocalMatchHost` со Skirmish-отношением (`TransportTestSupport.cs:66`): это FFA-тест без FoW, помеченный как 2v2.
- Противоречит: `Documentation/Phases/Phase_04_Multiplayer_Integration.md:24` («1v1 и 2v2 сценарии … [COMPLETE]»), `:59` (DoD «authoritative 1v1/2v2 RTS prototype»), `Documentation/DECISIONS.md` OD-43.

**P3-4: потолок EntityId 1024.** `_nextEntityValue` монотонен и не переиспользуется (`MatchServer.cs:321,366`). После 1024 суммарных спавнов новые юниты: пропускаются фильтром (`FogOfWarReplicationFilter.cs:96-101`, `IgnoredUnitCount`) → невидимы всем; и неотбиваемы (`MatchServer.cs:624-628` → `TargetInFogOfWar` для всех). Fail-closed, но молча. В M1-демо недостижимо (респавнов нет), для Phase 5+ — жёсткий потолок.

**P3-5 (perf):** `BuildFriendlyOwnerLookup` перестраивает таблицу 11×256 bool на каждый `CalculateVisibility` (`VisibilityEngine.cs:582-600`), хотя осмысленны только 10 владельцев; одиночное поле `Stamp` на юнит ослабляет мемоизацию ячеек при командах (союзник, проштампованный более поздним циклом игрока, проваливает дистанционный тест и блокирует memo ячейки). Только производительность.

---

## 4. Реконнект и тактическая пауза в бою (OD-42, OD-20)

**Вердикт направления: протокол защищён, но две дыры в управлении паузой/ретраями (P1-2, P2-2) и отсутствие паузы на проводе (P2-3).**

Подтверждено: opcode 9 `SessionSecretMessage` выдаётся только в ответ на валидный handshake (`ServerTransportHost.cs:540-546`); opcode 13 `ReconnectResponse` несёт токен ребинда; opcode 14 `ClientReadyToResume` (25 байт) принимается только от сокета, владеющего названной сессией (`ServerTransportHost.cs:434-457`); агрегация «все сессии готовы» существует (`_awaitingResume`, `ServerHost.cs:69,542`) и учитывается в `OnClientReadyToResume` (`:341-354`).

**P1-2: `InvalidState` терминален для координатора реконнекта.**
- `SessionManager.TryRebindSession` возвращает `RebindResult.InvalidState`, когда сервер ещё считает сессию Connected (`Runtime/Server/Sessions/SessionManager.cs:563-566`); ответ уходит на провод как есть (`ServerTransportHost.cs:413-426`).
- `ClientReconnectCoordinator.OnReconnectResponse`: любая ветка кроме `Accepted` → `SetState(Terminated)` (`Runtime/Client/ClientReconnectCoordinator.cs:170-173`). Из `Terminated` выхода нет: `TryReconnect`/`TryAutoReconnect` требуют `State == Reconnecting` (`RemoteSliceSession.cs:183-196,283-302`), `OnTransportDisconnected` рано выходит при `Terminated` (`ClientReconnectCoordinator.cs:120-123`).
- При реальном UDP-обрыве ретрай клиента (500 мс, `RemoteSliceSession.cs:141`) легко приходит раньше, чем сервер заметит потерю сокета → клиент навсегда перестаёт реконнектиться посреди матча; сервер держит паузу до истечения 200 с grace и возобновляет бой без игрока.
- Собственный PlayMode-тест документирует опасность и обходит её, дожидаясь серверного `Disconnected` перед ретраем (`Tests/PlayMode/NetworkImpairmentPlayModeTests.cs:372-380`).

**P2-2: `CheckAutoResumeAfterSessionChange` обходит агрегацию `_awaitingResume`.**
`ServerHost.cs:411-417` снимает паузу по условию «нет сессий в grace», игнорируя `_awaitingResume`. Сценарий: A ребиндится (попадает в `_awaitingResume`), у B истекает grace → `OnSessionGraceExpired` (`:399-403`) → `Resume()`, пока A ещё считает 5-секундный countdown. Тест `ClientReadyToResumeTests.Host_KeepsPauseWhileAnotherSessionIsInGrace` покрывает только обратный порядок; данный сценарий тестом не закрыт.

**P2-3: пауза и countdown не реплицируются.**
В `Core/Snapshot/*` и клиентской репликации нет поля паузы/countdown (единственное совпадение — `DeltaResume` в `ReplicationRequestCodec.cs:17`, вид repair-запроса). HUD выводит паузу из локального состояния координатора (`NetworkHudPresenter.cs:311-323`). Клиент, который не отключался, в dedicated-матче не получает никакого сигнала: дельты просто перестают приходить. OD-20 (`DECISIONS.md`) обещает «у всех игроков на экранах запускается 5-секундный отсчёт». Дополнительно: 5-секундный таймер принадлежит **клиенту** (`RemoteSliceSession.cs:330-354`, `ClientReconnectCoordinator.cs:41`); сервер возобновляется только по opcode 14 или по книжности grace — клиент, переставший качать (свёрнут/завис, но жив), держит матч в паузе до 200 с.

**P2-4: `ResyncSelectionViews` восстанавливает кольца по кэшированным слотам.**
`UnitSelectionController.cs:357-368` идёт по параллельным массивам `_selectedEntities`/`_selectedSlots` и ставит флаг только если слот всё ещё показывает тот же entity. После keyframe таблица мира сбрасывается, слоты выдёргиваются из free-стека в порядке записей кейфрейма (`ClientReplicationWorld.cs:304,389-393`), fog-hidden юниты освобождают слоты (`UnitViewBinder.cs:229-239`) — порядок слотов легитимно меняется, и identity-guard молча пропускает: HUD рапортует сквад, колец на поле нет. Оба существующих теста держат слоты фиксированными (`UnitSelectionAndCommandTests.cs:538-578`) — дефект не покрыт.

---

## 5. Zero-GC и артефакты плейтеста

**Вердикт направления: гейты зелёные, но пакет доказательств не подтверждает заявленные формулировки (P2-5, P2-6, P3-6, P3-7).**

Прочитано: `Artifacts/Playtest/playtest_metrics.json`, `gc_alloc_callstacks.txt`, просмотрены `02_box_selection.png`, `03_clash_combat.png`.

- Тесты: 1334 EditMode + 22 PlayMode = 1356 — подтверждено живыми прогонами и коммиченными XML (§0).
- Скриншоты: HUD-таблица с единым столбцом значений присутствует, `Health 74%` в `03_clash_combat.png` виден; кольца выделения присутствуют, **но только на переднем ряду** формации (≈8 колец на 100 выбранных в обоих снимках) — задние ряды перекрыты квадами юнитов. Это пре-существующая особенность презентации, одинаковая до и после реконнекта, т.е. не регрессия реконнекта; однако снимки не свидетельствуют «кольца на всём скваде» (P3-7).
- `unitsVisibleToClientA: 201` из 402 и `enemyBaseHidden: true` — FoW-фильтрация в синтетическом плейтесте работает.

**P2-5: «5-Minute 400-Unit Playtest» — 26.16 с.** `playtest_metrics.json: durationSeconds = 26.1586`; константы теста: `DisconnectAtSeconds = 20`, steady-окно 16–19 с (`SyntheticPlaytestPlayModeTests.cs:67-69`). Формулировки `CURRENT_STATE.md:33` и заголовок шага 4.6.5 ложны.

**P2-6: zero-GC доказательства не покрывают заявленное.**
1. Скоуп гейта — только `sessionA.Pump + sessionB.Pump` **с выключенной презентацией** (`_sliceRunner.enabled = false`, `SyntheticPlaytestPlayModeTests.cs:314-323`) и окно 3 с. ADR-014 (Verification Criteria) заявляет «полное отсутствие аллокаций … на клиентском цикле рендеринга» — артефакт сам это опровергает: `frameGcAllocBytesSteadyState = 16 783 800` байт за то же окно.
2. `gcAllocBytesSteadyState` в JSON — не измерение байт: это `scopedBlocks == 0 ? 0 : -1` (`SyntheticPlaytestPlayModeTests.cs:860`). «0 байт» в документах выведено из счётчика блоков.
3. Гейт недетерминирован: `gc_alloc_callstacks.txt` прямо говорит, что непрофилированный полный suite «known to flake with 2-8 sample blocks» и что capture «does not reproduce the original allocation itself», тогда как `CURRENT_STATE.md:109` помечает остаток «[CLOSED / PROVEN HARNESS RESIDUAL]». Атрибуция LogScope правдоподобна, но не доказана собственным артефактом.

**P3-6: транспортная телеметрия частично мертва.** `playtest_metrics.json`: `serverMessagesReceived: 0` при `serverDatagramsReceived: 300` — `MessagesReceived` инкрементируется только на снапшот-канале (`LiteNetLibCarrier.cs:820`), контрольные сообщения не считаются; `DatagramsSent` не заполняется вовсе (примечание в самом JSON). Диагностика мультиплеера, заявленная в `Phase_04:32`, вводит в заблуждение.

**P3-7:** см. кольца выше; плюс: манифест `screenshots` в JSON содержит 5 имён, шестой артефакт (`06_interactive_menu.png`, 2026-10-26 19:49) в манифест не входит и снят в другом прогоне, хотя документы говорят о едином наборе из 6.

---

## 6. Сводная таблица замечаний

| ID | Sev | Суть | Локация |
|---|---|---|---|
| F-1 | P1 | 2v2 не подключён: Skirmish по умолчанию в продакшн-раннере, `Team2v2CombatRelation` и `SetPlayerTeam` без вызовов в Runtime, `MatchConfig` без команд; «2v2»-тест — FFA без фильтра | DedicatedServerRunner.cs:136; MatchServer.cs:195; CombatRelation.cs:100; FogOfWarReplicationFilter.cs:72; ReplicationIntegrationSupport.cs:92 |
| F-2 | P1 | `InvalidState`/любой отказ реконнекта терминален: клиент навсегда перестаёт ретраиться посреди матча | ClientReconnectCoordinator.cs:170-173; SessionManager.cs:563-566; RemoteSliceSession.cs:283-302 |
| F-3 | P2 | Пул буферов теряет массивы на rate-limit, queue overflow и Shutdown | LiteNetLibCarrier.cs:660-673, 916-936, 459-462 |
| F-4 | P2 | `CheckAutoResumeAfterSessionChange` снимает паузу, игнорируя `_awaitingResume` | ServerHost.cs:411-417 vs 341-354 |
| F-5 | P2 | Пауза/countdown не на проводе; 5 с — клиентский таймер; OD-20 «отсчёт у всех» не выполнен | Core/Snapshot/* (нет поля); RemoteSliceSession.cs:330-354; NetworkHudPresenter.cs:311-323 |
| F-6 | P2 | `ResyncSelectionViews` по кэшированным слотам: кольца молча теряются при смене порядка слотов | UnitSelectionController.cs:357-368; ClientReplicationWorld.cs:304,389-393 |
| F-7 | P2 | «5-Minute» плейтест = 26.16 с; zero-GC скоуп — pump без презентации, 3 с; «0 байт» не измерялось; гейт флейкует | playtest_metrics.json; SyntheticPlaytestPlayModeTests.cs:67-69,314-323,860; gc_alloc_callstacks.txt |
| F-8 | P3 | EntityId > 1024: невидим всем и неотбиваем, молча | MatchServer.cs:321,366,624-628; FogOfWarReplicationFilter.cs:96-101 |
| F-9 | P3 | Клиентская сборка наследует `ServerHost` и читает авторитетный мир; Client ссылается на Server/ServerHost | LocalMatchHost.cs:17; TacticalVerticalSliceRunner.cs:549,1560; PrototypeRtsController.cs:439; InteractivePlaytestBootstrap.cs:147 |
| F-10 | P3 | Гонка двойного прогрева пула, push вне капа бакета | TransportBufferPool.cs:30-49 |
| F-11 | P3 | Opcode-14 диспетч читает `Data[0]` без проверки длины | ServerTransportHost.cs:293-294 |
| F-12 | P3 | `MessagesReceived`/`DatagramsSent` не считаются на control-канале | LiteNetLibCarrier.cs:820; playtest_metrics.json |
| F-13 | P3 | Кольца задних рядов перекрыты квадами (8 из 100 на снимках) | 02/03_*.png |
| F-14 | P3 | Перестройка friendly-lookup 11×256 каждый тик; одиночный Stamp ослабляет memo ячеек при командах | VisibilityEngine.cs:582-600, 254-272 |
| F-15 | P3 | «AUDITED: ZERO DEFECTS» / ADR-014 «FULLY VERIFIED» противоречат артефактам; 6-й скриншот вне манифеста | CURRENT_STATE.md:7,17,109; Phase_04:3; playtest_metrics.json |
| F-16 | P3 | Рабочее дерево не чисто вопреки брифу: `ProjectSettings.asset` изменён | git status |

Итого: **P0 = 0, P1 = 2 (F-1, F-2), P2 = 5 (F-3…F-7), P3 = 9 (F-8…F-16)**.

---

## 7. Расхождения с раундом 1 (`Audit-Phase4-M1-5beb1d1.md`)

- Оба P1 раунда 1 подтверждены независимой проверкой кода (F-1, F-2).
- «Pooled buffers orphaned» подтверждён построчно, без probe-фикстуры (F-3).
- «ResyncSelectionViews stale slots» подтверждён; добавлено указание на то, что оба теста фиксируют слоты (F-6).
- Понижено: «EntityId > 1024» — с P2 до P3 (в scope M1 недостижимо: респавнов нет, fail-closed).
- Понижено: «dedicated peers never see pause» объединён с отсутствием серверного countdown в один P2 (F-5).
- Добавлено нового: F-7 (метрика «0 байт» — тавтология счётчика блоков; «5-Minute» = 26 с), F-11, F-12, F-13, F-16.
- Снято: утверждение раунда 1 о «probe confirmed pool bucket loses a buffer» как необходимость — достаточно чтения кода.

---

## 8. Условия перехода в APPROVE / CONDITIONAL APPROVAL

1. **F-1:** либо провести команды в продакшн-композицию (`DedicatedServerRunner` → `Team2v2CombatRelation` + `SetPlayerTeam` из конфигурации матча, `MatchConfig` с командами, 4-клиентский E2E с фильтром и союзной видимостью), либо формально вывести 2v2 из scope M1 с правкой Phase_04/CURRENT_STATE/ADR-014.
2. **F-2:** `InvalidState` должен быть восстанавливаемым (ретрай с backoff, пока grace не истёк), а не терминальным; bite-тест: ретрай до серверного `Disconnected` обязан завершиться реконнектом.
3. **F-3..F-7:** возврат буферов на всех путях дропа; учёт `_awaitingResume` в `CheckAutoResumeAfterSessionChange`; репликация паузы/countdown или правка OD-20; resync колец по EntityId; честные формулировки zero-GC/длительности плейтеста и детерминированный гейт.
4. Правка документов: снять «ZERO DEFECTS»/«FULLY VERIFIED» до закрытия P1.

До выполнения 1 и 2 Milestone M1 не закрывать.
