# Grand Audit — Phase 4 / Milestone M1 (Multiplayer Prototype)

**Дата:** 2026-10-27
**База:** `5beb1d1` (`main`), рабочее дерево чистое на входе аудита
**Проект:** GlobalFront, Unity 6000.6.2f1 / URP 17.6.0
**Режим:** независимый zero-trust состязательный аудит, с повторным прогоном тестов и временными probe-тестами
**Отчёт:** `Artifacts/Audit-Phase4-M1-5beb1d1.md`

## 1. Резолюция

# REJECT

**P0 = 0, P1 = 2, P2 = 7, P3 = 10.**

M1 **не может быть сертифицирован как «ZERO DEFECTS»** в заявленном объёме. 1v1-контур dedicated-клиента работает; 1356 тестов действительно зелёные. Однако два P1 затрагивают заявленный Definition of Done (2v2 и гарантированный auto-reconnect), а ряд P2 делает формулировки «5-Minute», «Zero-GC render cycle» и «ZERO DEFECTS» не подтверждёнными артефактами.

Условия для повторной подачи M1 перечислены в §8. После закрытия P1 и явного решения по P2 резолюция может быть изменена на CONDITIONAL APPROVAL / APPROVE.

## 2. Метод и что проверено независимо

- Git: `5beb1d1`, ветка `main`; на входе и на выходе `git status --porcelain` пуст.
- Повторно прогнаны оба suite (Unity Test Framework через Unity MCP):
  - EditMode: **1334/1334 passed**, 0 failed, 0 skipped, 28.2 s (повторный чистый прогон после удаления probe-фикстуры — 1334/1334, 26.6 s).
  - PlayMode: **22/22 passed**, 0 failed, 0 skipped, 63.2 s; synthetic playtest — 27.1 s.
  - Итого **1356/1356**, что совпадает с заявленным гейтом и `Artifacts/TestResults/step465-polish-*.xml`.
- Временно добавлена изолированная probe-фикстура (`AuditProbes`, 4 теста), прогнана, результаты зафиксированы ниже, затем фикстура удалена; финальный EditMode снова 1334/1334.
- Прочитаны и проверены: `TransportBufferPool` и все production call-site `ReturnEventBuffer`; `LiteNetLibCarrier`/`OwnDatagramCarrier`/`VirtualNetworkPipe`; `FragmentAssembler`; `VisibilityEngine`; `VisibilityHistoryRing`; `FogOfWarReplicationFilter`; `ServerReplicationEmitter`; `MatchServer.TryEnqueueAttack`; `SessionManager`; `ServerTransportHost`; `ServerHost`; `ClientReconnectCoordinator`; `RemoteSliceSession`; `ClientTransportEndpoint`; `TacticalVerticalSliceRunner`; `UnitSelectionController`/`UnitViewBinder`; asmdef-файлы; артефакты Playtest.

## 3. Положительные результаты (что действительно выполнено)

1. **1356 тестов зелёные.** Оба suite повторены независимо; записанные XML-гейты соответствуют.
2. **Use-after-free / double-return в штатном receive-path не найдены.**
   - `ServerTransportHost` и `ClientTransportEndpoint` возвращают `TransportEvent.Data` ровно один раз в `finally`.
   - `FragmentAssembler.ReleaseGroup` возвращает все fragment-слоты; собранное сообщение — отдельный rented buffer, который освобождает consumer.
   - `TransportBufferPool.Rent/Return` сериализованы общим lock; double-return в production-путях не найден.
   - `ReplicationFeedbackReceived` и `SnapshotReceived` потребляются синхронно; удержания pooled-буфера после возврата не найдено.
3. **FoW per-client фильтрация в 1v1 реализована корректно.**
   - `VisibilityEngine`: целочисленная uniform grid 8192 мм, без Unity API, без горячих аллокаций.
   - `FogOfWarReplicationFilter`: маски по игрокам + `VisibilityHistoryRing` (120 тиков).
   - `ServerReplicationEmitter`: ADD/UPDATE/REMOVE строятся по переходам `baseMask → currentMask`, скрытые сущности в дельту не попадают; keyframe фильтруется по маске; `StateChecksum` считается по тому же множеству.
4. **Anti-maphack гейт атаки стоит в правильном месте.**
   - `MatchServer.TryEnqueueAttack`: `TargetInFogOfWar` проверяется до `Schedule`/`CommitSequence`; sequence при отказе не списывается.
   - Авто-приобретение цели не обходит FoW для текущих констант: `AutoAcquireRangeMm = 18000 < DefaultSightRadiusMm = 20000`, поэтому цель в радиусе auto-acquire уже видима.
5. **Архитектурные границы сборок.**
   - `GlobalFront.Core`: `noEngineReferences: true`, без ссылок на Server/Client.
   - `GlobalFront.Server`: `noEngineReferences: true`, только Core + `LiteNetLib.dll`.
   - `GlobalFront.ServerHost`: Core + Server, `DedicatedServerRunner` и `DedicatedServerBootstrap` находятся здесь; Server не ссылается на Client.
6. **Reconnect happy path.**
   - Opcode 9/13 распознаются, секрет сравнивается FixedTimeEquals, токен из `ReconnectResponse` усыновляется; opcode 14 доходит до `ServerHost.OnClientReadyToResume`.
   - Keyframe/`ResyncSelectionViews` для стабильного slot-маппинга работает; скриншот `05_reconnected_and_resumed.png` показывает кольца и HP 50%.
7. **Визуальные артефакты.**
   - Все 6 PNG — 1920×1080; `03_clash_combat.png` — 74% HP, `05_reconnected_and_resumed.png` — 50% HP; `04_network_loss_pause.png` — overlay 200 s / 5 s; HUD `ValueColumnX = 124f`; кольца видны (в ракурсе — преимущественно передний ряд, тест проверяет 100 `IsSelected` views).
   - Scoped GC-окно действительно дало 0 blocks / 0 bytes; полный кадр — 14.6–17.6 MB за 3 s (URP/editor, помечено informational).

## 4. P1 — Critical (блокируют сертификацию M1)

### P1-01. Production 2v2 не собран: dedicated-сервер всегда FFA, FoW не знает союзников

**Где:**
- `Assets/_GlobalFront/Runtime/ServerHost/DedicatedServerRunner.cs:136` — `InitializeCore` создаёт `new ServerHost(tickRate)`; API/CLI выбора топологии нет.
- `Assets/_GlobalFront/Runtime/Server/ServerHost.cs:103-113` — default-конструкторы создают `new MatchServer()`.
- `Assets/_GlobalFront/Runtime/Server/MatchServer.cs:193-196` — default relation `SkirmishCombatRelation.Instance`, где `AreEnemies(left, right) => left != right` (`Assets/_GlobalFront/Runtime/Core/CombatRelation.cs:55`).
- `Assets/_GlobalFront/Runtime/ServerHost/DedicatedServerRunner.cs:148-151` — создаётся `new FogOfWarReplicationFilter()`, но `SetPlayerTeam` не вызывается нигде в Runtime.
- `Assets/_GlobalFront/Tests/EditMode/Integration/Replication/ReplicationIntegrationSupport.cs:85` → `TransportTestSupport.CreateRig` → `new LocalMatchHost(...)` (`Assets/_GlobalFront/Tests/EditMode/TransportTestSupport.cs:66`), то есть «2v2» сценарии `Match2v2PlaytestScenarioTests` идут на default Skirmish relation.
- `Team2v2CombatRelation` используется только в изолированных `Team2v2HostilityTests`, не в composition root.

**Факт:** четырёх-игровой dedicated-матч — FFA: союзники являются врагами, могут авто-атаковать и наносить урон друг другу; FoW скрывает союзников за пределами 20 м, потому что team-маппинг не задан. HUD при этом использует `MatchTopology2v2` и показывает команды.

**Почему это P1:** Phase 04 Scope прямо заявляет «1v1 и 2v2 сценарии с реальным MatchServer — COMPLETE»; Definition of Done — «authoritative 1v1/2v2 RTS prototype». Заявленный 2v2 не подтверждён production-сборкой. Если owner официально выводит 2v2 из M1, P1-01 понижается до P3 (документация), но тогда это должно быть исправлено во всех Phase/status/DoD-документах и тестах.

**Требуется:** параметр топологии/mode в `ServerHost`/`DedicatedServerRunner`; создание `MatchServer(Team2v2CombatRelation.Instance)`; вызовы `filter.SetPlayerTeam` для всех четырёх игроков; dedicated 2v2 E2E-тест, проверяющий отсутствие friendly fire и общую видимость союзников. Либо формальный descope 2v2.

### P1-02. Auto-reconnect может навсегда завершиться, если клиент ретраится раньше, чем сервер заметил потерю

**Где:**
- `Assets/_GlobalFront/Runtime/Client/Presentation/RemoteSliceSession.cs:283-302` — авто-ретрай каждые 500 мс, пока coordinator `Reconnecting`.
- `Assets/_GlobalFront/Runtime/Server/Sessions/SessionManager.cs:563-566` — rebind разрешён только при `SessionState.Disconnected`; при `Connected` возвращается `InvalidState`.
- `Assets/_GlobalFront/Runtime/Client/ClientReconnectCoordinator.cs:170-173` — **любой** не-Accepted ответ переводит FSM в `Terminated`.
- `RemoteSliceSession.TryAutoReconnect` требует `Reconnecting`; после `Terminated` повторов нет; endpoint timeout также не перевзводит FSM.
- Тест сам признаёт гонку: `Assets/_GlobalFront/Tests/PlayMode/NetworkImpairmentPlayModeTests.cs:372-375` — «a rebind that arrives while the server still considers the session Connected is answered with InvalidState, which is terminal for the coordinator», после чего тест **ждёт**, пока сервер увидит потерю, прежде чем разрешить клиенту ретрай.

**Факт:** при реальной потере клиент может открыть заменяющий сокет и отправить ReconnectRequest раньше, чем сервер (LiteNetLib `DisconnectTimeout = 5000 ms`) переведёт сессию в Disconnected. Первый ответ `InvalidState` терминально убивает coordinator; автоматический реконнект больше не выполняется, требуется перезапуск клиента. Тест обходит именно этот сценарий.

**Почему это P1:** заявлены «живой реконнект в разгаре боя» и авто-ретрай; в реальном network-loss сценарии гарантия не выполняется.

**Требуется:** трактовать `InvalidState` (и переходные ответы) как retryable в ограниченном окне; либо разрешить серверу rebind с валидным секретом на новый сокет при устаревшей/неотвечающей старой привязке; добавить E2E-тест «клиент ретраится до того, как сервер заметил потерю».

## 5. P2 — Major

### P2-01. TransportBufferPool теряет буферы на трёх drop-path

**Где:**
- `Assets/_GlobalFront/Runtime/Server/Transport/LiteNetLibCarrier.cs:660` — `Rent` делается **до** проверки rate limiter; при отказе (`:669-673`) буфер не возвращается.
- `Assets/_GlobalFront/Runtime/Server/Transport/LiteNetLibCarrier.cs:916-935` — при переполнении `_events` Message-event отбрасывается, `Data` не возвращается (касается control и snapshot).
- `Assets/_GlobalFront/Runtime/Server/Transport/LiteNetLibCarrier.cs:452-462` — `Shutdown` очищает очередь, не возвращая pooled-буферы.
- Штатный `FragmentAssembler` возвращает fragment-буферы корректно (`FragmentAssembler.cs:358-370`); double-return в production не найден.

**Probe:** `Probe_EventQueueOverflowDropsPooledBufferWithoutReturningIt` — ожидалось, что после overflow буфер вернётся в bucket (+1), фактически bucket не изменился (первый прогон 4→3, второй 1→0). Это означает утечку из пула на каждом переполнении; при повторных прогонах пул истощается.

**Влияние:** rate-limit/overflow заставляют пул аллоцировать новые массивы (до 1 MiB на bucket) вместо повторного использования; под flood это GC-давление и деградация, а заявление «0 B GC» верно только для невозмущённого steady-state. Это не corruption и не double-return, но контракт владения нарушен.

**Требуется:** проверять rate limit до `Rent`; возвращать `transportEvent.Data` при drop в `EnqueueEvent`; возвращать/осушать очередь при `Shutdown`; добавить тест на все три пути.

### P2-02. Tactical pause снимается, пока другая reattached-сессия ещё ждёт ReadyToResume

**Где:**
- `Assets/_GlobalFront/Runtime/Server/ServerHost.cs:411-416` — `CheckAutoResumeAfterSessionChange` снимает паузу, если нет сессий в grace, **не проверяя `_awaitingResume`**.
- Вызывается из `OnSessionGraceExpired` (`:399-403`), `OnSessionClosed` (`:405-409`) и `SessionDetached` lambda (`:525-534`).
- Комментарий `OnClientReadyToResume` (`:335-339`) утверждает, что пауза держится, пока другая reattached-сессия не сигнализировала; код это не обеспечивает.

**Probe:** `Probe_AwaitingResume_IsLiftedByAnUnrelatedSessionClose` — B в grace, A reattach (awaiting=1), затем `CloseSession(B)`. Ожидалось `IsPaused == true`, получено `false`.

**Влияние:** при выходе/истечении другого игрока матч возобновляется до 5-секундного countdown/готовности A; A может ещё скачивать keyframe или находиться в countdown. Нарушен OD-20 и инвариант, заявленный в коде.

**Требуется:** добавить `_awaitingResume.Count == 0` в auto-resume; тест на непрямое закрытие/expiry другого сокета.

### P2-03. Сервер не имеет 5-секундного countdown: ReadyToResume принимается сразу

**Где:** `ServerHost.OnClientReadyToResume` (`:341-353`) снимает паузу немедленно; серверного таймера/eligibility-окна нет. Клиентский countdown — `ClientReconnectCoordinator.DefaultCountdownSeconds = 5`, но сервер ему доверяет.

**Probe:** `Probe_ReadySignal_ResumesWithoutAnyServerCountdown` прошёл: после `Pause()` + reattach + немедленный `OnClientReadyToResume` хост сразу `IsPaused == false`.

**Влияние:** баг/чит-клиент может снять паузу раньше 5 s; OD-20 фактически становится клиентской рекомендацией. Связано с P2-04.

### P2-04. Остальные подключённые игроки не видят tactical pause/countdown

**Где:**
- `Assets/_GlobalFront/Runtime/Client/Presentation/TacticalVerticalSliceRunner.cs:787-792` — в `DedicatedClient` HUD связывается только с собственным `ReconnectCoordinator`.
- `Assets/_GlobalFront/Runtime/Client/UI/NetworkHudPresenter.cs:196-228` — `WireSessionManager`/`WireHost` доступны только локальному host; удалённый клиент их не имеет.
- В `TransportMessageType` (`Assets/_GlobalFront/Runtime/Server/Transport/TransportProtocol.cs:26-63`) нет pause-state сообщения; есть только C0 ClientReadyToResume (клиент→сервер).
- PlayMode-тест проверяет overlay только у victim-клиента (`NetworkImpairmentPlayModeTests.cs:589-710`); `sessionB` остаётся подключённым и никакого overlay не получает.

**Факт:** когда игрок A отключается, сервер замирает, но клиент B видит только остановившийся мир без overlay, 200 s grace и 5 s countdown. OD-18/ADR-011 требует экран ожидания у всех подключённых игроков.

**Требуется:** реплицировать pause state (отдельное C0/C2 сообщение или флаг дельты) и связать его с `TacticalPauseOverlay` у всех клиентов; тест с двумя клиентами, где B проверяет overlay.

### P2-05. ResyncSelectionViews восстанавливает кольца только при неизменном slot-маппинге

**Где:**
- `Assets/_GlobalFront/Runtime/Client/Presentation/UnitSelectionController.cs:357-367` — используются сохранённые `_selectedSlots[index]`; поиска entity→новый slot нет.
- `Assets/_GlobalFront/Runtime/Client/Presentation/UnitViewBinder.cs:282-286` — keyframe `HandleResync` освобождает все views.
- `ClientReplicationWorld.Reset` (`Assets/_GlobalFront/Runtime/Client/Replication/ClientReplicationWorld.cs:389`) и free-stack (`:571-584`) пересобирают слоты; порядок keyframe ADD — ascending EntityId, поэтому старый slot может принадлежать другому entity.
- `UnitSelectionDriver.cs:173` затем вызывает `PruneStaleSelection`, который выбросит selection при несовпадении slot/entity (`UnitSelectionController.cs:1072-1085`).

**Probe:** `Probe_SelectionResync_DoesNotFollowTheEntityWhenItsSlotChanges` — entity 3 после keyframe переехал из slot 2 в slot 1; `ResyncSelectionViews` не включил кольцо (Expected True, was False).

**Влияние:** после любого keyframe с churn слотов (смерти, FoW hide/reveal, потеря дельт) selection/ring теряются; тесты `UnitSelectionAndCommandTests` меняют только флаг, не slot, а synthetic playtest не имеет смертей, поэтому дефект не виден.

**Требуется:** искать новый slot по entity (scan binder/world) либо обновлять `_selectedSlots` при resync; тест с keyframe-перестановкой слотов.

### P2-06. Playtest evidence не подтверждает «5-Minute» и «Zero-GC render cycle»

**Где/факты:**
- `Artifacts/Playtest/playtest_metrics.json`: `durationSeconds = 26.17 s`, `disconnectAtSeconds = 20.0`.
- `Assets/_GlobalFront/Tests/PlayMode/SyntheticPlaytestPlayModeTests.cs:67` — `DisconnectAtSeconds = 20`; отдельного 5-минутного soak-теста нет.
- `CURRENT_STATE.md:33`, `CHANGELOG.md:54`, `PROJECT_STATUS.md` называют Step 4.6.5 «5-Minute 400-Unit Playtest».
- GC-окно — 3 s (`SteadyWindowStart/End = 16/19`), `presentation disabled`; `frameGcAllocBytesSteadyState` = 14.6–17.6 MB за 3 s (URP/editor, informational).
- `DECISIONS.md:545` заявляет «Полное отсутствие аллокаций … на клиентском цикле рендеринга»; scoped-тест измеряет только `sessionA.Pump/sessionB.Pump`, presentation-цикл отключён.

**Влияние:** 26-секундный тест не является soak-тестом и не может обнаружить длительные утечки (включая P2-01). Формулировки «5-Minute» и «zero-GC render cycle» не подтверждены. Это evidence/acceptance-дефект, а не ошибка симуляции.

**Требуется:** либо реальный 5-минутный soak (или переименование/descope), либо отдельный full-frame профиль с явной областью; обновить ADR/status.

### P2-07. EntityId > 1024 становится невидимым для всех, включая владельца

**Где:**
- `MatchServer` `_nextEntityValue` монотонно растёт (`MatchServer.cs:150`, `:321`, `:366`); cap `MaxUnitsPerMatch = 1024` ограничивает **живых**, не общее число спавнов.
- `FogOfWarReplicationFilter.UpdateTick` пропускает `entityValue > BitMask1024.BitCount` и считает `IgnoredUnitCount` (`FogOfWarReplicationFilter.cs:92-100`); такой unit не попадает ни в маску, ни в keyframe/delta/checksum.
- `VisibilityEngine.ResolveBitIndex` адресует только 1..1024 (`VisibilityEngine.cs:604-611`).

**Влияние:** после 1024 спавнов за матч (при живом лимите 1024) новые unit'ы, включая собственные, не реплицируются; fail-closed (утечки нет), но gameplay ломается. В текущем playtest респавнов нет, поэтому дефект не проявляется; он станет реальным в Phase 5 (reinforcements/production) или при длинном матче.

**Требуется:** индексировать маску по slot/компактному id или перерабатывать EntityId; тест на spawn после исчерпания 1024 lifetime-id.

## 6. P3 — Minor / hygiene / evidence integrity

1. **Metrics частично декларативны.** `SyntheticPlaytestPlayModeTests.BuildMetrics` (`:860-878`) записывает `enemyBaseHidden = true`, `overlayVisibleOnDisconnect = true`, `autoResumed = true`, `overlayHiddenAfterResume = true`: соответствующие факты проверяются assertions, но в JSON попадают константы, а не считанные значения. `unitsVisibleToClientA` передаётся как `CombatUnitsPerPlayer + 1` (201), а не читается из мира клиента на конец боя. `countdownSeconds` — константа 5; `countdownObservedSeconds` — одно наблюдённое остаточное значение, а не измеренная длительность 5 s.
2. **`serverMessagesReceived = 0`.** `LiteNetLibCarrier` инкрементирует `MessagesReceived` только в snapshot-path (`:820`), но не в control-path (`:675-685`). Счётчик не отражает принятые команды/ping'и.
3. **`poolMisses = 0`** в `CURRENT_STATE.md:109` не подтверждён: в `TransportBufferPool` нет счётчика miss, в `gc_alloc_callstacks.txt` его тоже нет; артефакт сам содержит caveat, что не воспроизводит исходную аллокацию (строки 50-55).
4. **Screenshot 06.** `06_interactive_menu.png` не создаётся PlayMode suite (тест пишет 01–05; `playtest_metrics.json` перечисляет 5), имеет более ранний mtime и визуально показывает матч с HUD, а не меню. Название/атрибуция вводит в заблуждение.
5. **Pool hardening.** `Shutdown` не возвращает очередь (P2-01); `TransportBufferPool` не обнаруживает double-return и `_warmed` читается без lock (benign duplicate warm, но race); нет метрики misses.
6. **Client ↔ ServerHost boundary.** `GlobalFront.Client.asmdef` ссылается на `GlobalFront.ServerHost`; `InteractivePlaytestBootstrap.cs:147` может создать `DedicatedServerRunner` в клиентском процессе. Для удалённого клиента это не даёт доступа к чужому серверному состоянию (`_host = null` в `DedicatedClient`), но граница «клиент не имеет доступа к серверу» является конвенцией, а не compile-time запретом.
7. **Initial connect без timeout.** `ClientTransportEndpoint.ConnectTimeoutMs` применяется только при `_connectingViaReconnect` (`ClientTransportEndpoint.cs:199-206`); первоначальный handshake, который принял UDP, но не ответил, оставляет `Connecting` навсегда.
8. **Empty control frame.** `ServerTransportHost.HandleMessage` проверяет `transportEvent.Data[0] == ClientReadyToResumeCodec.Opcode` без предварительной проверки длины (`:292-297`); zero-length event даёт IndexOutOfRange, который ловится в `Pump` и считается malformed. Это не падение, но исключение на каждый такой пакет.
9. **Plaintext SessionSecret.** Opcode 9 передаёт 32-байтный секрет по UDP без шифрования; ADR-011/OD-15 явно откладывает encryption/auth. Это accepted residual для LAN/prototype, но не защита от on-path hijack в интернете.
10. **Docs drift.** `CURRENT_STATE.md:50` Last Commit = `156d898` при HEAD `5beb1d1`; `Phase_04` статус = `366e444`; формулировки «100% COMPLETED / AUDITED: ZERO DEFECTS» и «1356 tests» не отражают найденные P1/P2. `ListenServer` зарезервирован и не реализован — это корректно задокументировано, не дефект.

## 7. Результаты probe-прогона

Временная фикстура (4 теста) была добавлена в `Assets/_GlobalFront/Tests/EditMode/AuditProbes/`, прогнана, затем удалена.

| Probe | Результат | Смысл |
|---|---|---|
| `Probe_AwaitingResume_IsLiftedByAnUnrelatedSessionClose` | **FAIL** — Expected `IsPaused=True`, was `False` | Закрытие другой сессии снимает паузу, пока A ждёт ready |
| `Probe_EventQueueOverflowDropsPooledBufferWithoutReturningIt` | **FAIL** — Expected bucket +1, was +0 (4→3; повтор 1→0) | Pooled-буфер теряется при overflow |
| `Probe_SelectionResync_DoesNotFollowTheEntityWhenItsSlotChanges` | **FAIL** — Expected ring `True`, was `False` | Resync не находит entity в новом slot |
| `Probe_ReadySignal_ResumesWithoutAnyServerCountdown` | PASS | Сервер принимает ReadyToResume без 5 s gate |

Полный прогон с probe: 1338 tests, 3 failed, 1 passed. После удаления фикстуры: EditMode 1334/1334, PlayMode 22/22. Probe-файлы в репозиторий не попали; рабочее дерево после удаления чистое.

## 8. Условия повторной подачи M1

1. **P1-01:** либо production-обвязка 2v2 (`Team2v2CombatRelation` + `SetPlayerTeam` + dedicated E2E), либо официальный descope 2v2 во всех Phase/status/DoD-документах и тестах.
2. **P1-02:** retryable `InvalidState`/bounded retry или серверный stale-session rebind; E2E-тест гонки «клиент ретраится до серверного timeout».
3. **P2-01:** вернуть буферы на rate-limit/overflow/shutdown; тесты на все три пути; при необходимости — pool-miss метрика.
4. **P2-02/P2-03/P2-04:** учесть `_awaitingResume` в auto-resume; определить и проверить серверную политику 5 s; реплицировать pause state всем клиентам.
5. **P2-05:** entity→slot lookup при resync; тест с перестановкой слотов.
6. **P2-06:** либо реальный 5-минутный soak, либо переименование/descope; отдельно измерить full-frame GC и обновить ADR-014.
7. **P2-07:** закрыть до Phase 5 (spawn после 1024 lifetime ID).
8. **P3:** исправить метрики/документацию или явно пометить как known limitations; `06` переименовать/переснять.

После закрытия P1 и явного решения по P2 (fix или waiver с записью в backlog) M1 можно повторно предъявить; при P0=0/P1=0 и принятых P2 резолюция может быть CONDITIONAL APPROVAL / APPROVE.

## 9. Заключение

1v1 dedicated-контур (handshake, FoW-фильтрация, per-client checksum, attack gate, keyframe reconnect, HUD/визуальные артефакты) выглядит рабочим и подтверждён 1356 зелёными тестами. Однако **заявленный M1 в полном объёме не подтверждён**: production 2v2 отсутствует, auto-reconnect имеет терминальную гонку, а несколько P2 нарушают контракт владения буферами, pause-governance и selection resync. Формальная резолюция на базе `5beb1d1` — **REJECT** для сертификации «ZERO DEFECTS».
