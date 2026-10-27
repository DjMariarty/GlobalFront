# Независимый adversarial Grand Audit — Phase 4 / Milestone M1

**Дата:** 2026-10-27. **Ревизия отчёта:** R3, независимая проверка исходников и исполнения; предыдущие отчёты не использованы как доказательства.

**Проверенный HEAD:** `5beb1d107bc0a30fc70b951eca5078c5556d9987`, ветка `main`. Unity 6000.6.2f1, URP 17.6.0.

## 1. Резолюция

# REJECT — закрытие Milestone M1 не подтверждено

| Уровень | Количество |
|---|---:|
| P0 Blocker | 0 подтверждённых |
| P1 Critical | 5 |
| P2 Major | 12 |
| P3 Minor | 4 |

P1: штатная dedicated-композиция не запускает матч; режим 2v2 не подключён; мёртвые юниты сохраняют обзор; собственный преследователь раскрывает новые координаты скрытой цели; после принятого reconnect возможна неограниченная пауза.

**Последний сохранённый независимый прогон: 1334/1334 EditMode и 21/22 PlayMode — 1355 Passed, 1 Failed.** Ошибка — 2 GC-блока при ожидаемом нуле. Ранее в этой же проверке наблюдался полный 22/22 PlayMode; результаты несовместимы с утверждением о стабильном зелёном gate, но не доказывают происхождение аллокаций из production-кода.

Для наиболее приоритетного направления **не найден достижимый production use-after-return или double-return**. Это не сертификат отсутствия ошибок: обнаружены потерянные возвраты pooled buffers и гонка teardown/receive. Нарушения confidentiality и lifecycle достаточны для REJECT независимо от GC-теста.

## 2. Объект и ограничения проверки

### 2.1. Базовые требования

Прочитаны и сопоставлены с кодом:

- [Documentation/CURRENT_STATE.md](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/CURRENT_STATE.md).
- [Documentation/PROJECT_STATUS.md](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/PROJECT_STATUS.md).
- [Documentation/DECISIONS.md:500](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/DECISIONS.md#L500), OD-35–43 и связанные OD-18/20.
- [Documentation/Phases/Phase_04_Multiplayer_Integration.md:39](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/Phases/Phase_04_Multiplayer_Integration.md#L39): завершение gameplay loop, real network, reconnect, нулевые P0/P1.

### 2.2. Рабочее дерево исходно НЕ было чистым

До любых действий аудита уже были изменены:

- [Artifacts/Playtest/01_spawn_and_fog.png](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Artifacts/Playtest/01_spawn_and_fog.png).
- [Artifacts/Playtest/05_reconnected_and_resumed.png](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Artifacts/Playtest/05_reconnected_and_resumed.png).
- [Artifacts/Playtest/playtest_metrics.json](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Artifacts/Playtest/playtest_metrics.json).
- [ProjectSettings/ProjectSettings.asset](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/ProjectSettings/ProjectSettings.asset): Standalone define содержал дополнительный `SENTIS_ANALYTICS_ENABLED`.

Также существовали два untracked audit-отчёта; их содержимое не читалось. Поэтому исходники относятся к указанному HEAD, **тесты — к текущему Editor с исходной локальной конфигурацией**, а committed и локальные playtest-артефакты оценены раздельно.

Исходные восемь playtest-файлов и настройки сохранены; их SHA256 находятся в [initial-manifest.json](file:///C:/Users/Yaskiev/Desktop/UnityProjects/.scratch/grand-audit-m1-r3-20261027/initial-manifest.json). Изменённые тестами изображения/метрики скопированы отдельно, затем исходные файлы восстановлены побайтно. Production-код, настройки и предыдущие отчёты не исправлялись; commit/push не выполнялись.

### 2.3. Уровни доказательств

- **LIVE:** выполнение production-классов внутри Unity; где использован scripted/virtual transport, это указано явно.
- **STATIC:** достижимая цепочка по исходникам; не выдаётся за воспроизведённый race или wire capture.
- **GATE:** проверка тестов, артефактов и соответствия критериям приёмки.

Выполнены четыре независимые read-only проверки подсистем с последующей проверкой ключевых находок основным аудитором. Запускались штатные Unity suites и отдельные ограниченные in-memory probes. Нагрузочные атаки, внешние адреса, эксплуатация публичных серверов и destructive testing не применялись.

**Не проверено:** сборка/запуск нового `StandaloneBuildSubtarget.Server` player, два отдельных интерактивных клиента до победы, four-client real-UDP 2v2, WAN-сеанс длительностью пять минут. Виртуальный four-client probe не заменяет эти проверки.

## 3. Матрица обязательных направлений

| Направление | Подтверждено | Ограничение / нарушение |
|---|---|---|
| Buffer ownership | Endpoint/host возвращают event buffer в `finally`; reader копируется до recycle; асинхронные потребители получают свои данные; Rent/Return защищены lock | P2-01/02; Warm не атомарен; полного accounting-теста teardown/reject нет |
| Assembly boundary | `GlobalFront.Server` engine-free; runner в отдельной сборке; DedicatedClient не создаёт authoritative host | Dedicated startup и 2v2 не собраны в игровой сценарий; Server build не запускался |
| FoW / checksum | Integer grid 8192 мм; фиксированные маски; endpoint visibility transitions; keyframes/checksum per-client | Отбор сущностей не очищает скрытые сведения внутри видимых записей; dead vision; status oracle |
| Attack gate | `TargetInFogOfWar` до `Schedule` и `CommitSequence` | Ранние ответы раскрывают существование/смерть скрытой цели |
| Reconnect | Secret32, fixed-time compare, обновление token; ready проверяется по привязанной сессии; штатный countdown/повтор send работают | Неограниченное ожидание, transient denial terminal, stale events, часы grace, premature resume, несинхронные клиенты |
| Selection | Возвращает кольца при сохранении тех же entity/slot и отсутствии промежуточного prune | Пустой промежуточный mirror и смена slot теряют selection |
| Артефакты | Шесть committed PNG действительно 1920×1080; кольца и HP 74%/50% видимы; HUD value column 124 px | Пять файлов генерируются тестом; шестой не снимок меню; сценарий ~26 с; scoped GC не покрывает весь цикл |
| Тесты | 1334 EditMode; полный PlayMode действительно запускается после reload | Последний PlayMode 21/22; пустые `Passed` с 0 tests не засчитаны |

## 4. P1 Critical

### P1-01. Dedicated entry point принимает клиентов, но не запускает playable match

**LIVE + STATIC.** [DedicatedServerBootstrap.cs:31](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/ServerHost/DedicatedServerBootstrap.cs#L31) вызывает только Initialize. В [DedicatedServerRunner.cs:134](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/ServerHost/DedicatedServerRunner.cs#L134) создаются host, Forming match, auto-join и транспорт, но отсутствуют roster/config и вызов запуска матча. Handshake присоединяет сессии, не закрывая этот разрыв.

**Witness:** production runner + OwnDatagramCarrier/VirtualNetworkPipe, четыре настоящих ClientTransportEndpoint, 600 шагов по 10 ms: все `Established`, игроки 1–4, `phase=Forming`, `unitCount=0`. Только явный внешний `TryStartSessionMatch` переводит сценарий к симуляции.

**Влияние:** работоспособность transport harness ошибочно экстраполирована на штатный dedicated multiplayer prototype. Тесты маскируют отсутствие composition wiring ручным стартом host.

**Критерий закрытия:** запуск документированным entry point → присоединение клиентов → авторитетный старт утверждённого roster без доступа теста/клиента к ServerHost → завершённый матч.

### P1-02. Dedicated «2v2» фактически остаётся free-for-all

**LIVE + STATIC.** [DedicatedServerRunner.cs:136](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/ServerHost/DedicatedServerRunner.cs#L136) создаёт `ServerHost(tickRate)`, который создаёт default MatchServer. В [MatchServer.cs:193](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs#L193) по умолчанию `SkirmishCombatRelation`. Реализация [CombatRelation.cs:89](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/CombatRelation.cs#L89) задаёт союз 1+2 против 3+4, но `Team2v2CombatRelation.Instance` не подключён runtime-композицией; team visibility также не настраивается runtime-вызовами `SetPlayerTeam`.

**Witness:** после ручного старта four-owner матча в том же runner фактическая relation — `SkirmishCombatRelation`; атака игрока 1 на игрока 2 принята (`None`).

**Влияние:** неверны friendly-fire/auto-acquire и team-victory semantics; четыре сокета не доказывают 2v2.

**Критерий закрытия:** явная серверная конфигурация mode/team relation и visibility teams; four-client сценарий с отклонением allied attack и правильной командной победой.

### P1-03. Погибший юнит остаётся источником FoW-обзора

**LIVE + STATIC.** [MatchServer.cs:787](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs#L787) сохраняет погибшие записи в snapshots. [FogOfWarReplicationFilter.cs:91](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Replication/FogOfWarReplicationFilter.cs#L91) отбрасывает некорректные ID, но не dead units; преобразование в VisibilityUnitSnapshot теряет health. [VisibilityEngine.cs:253](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/Visibility/VisibilityEngine.cs#L253) использует эти записи как источники обзора. Default dedicated snapshot source не удаляет трупы.

**Witness:** разведчик P1 в (0,0), живой резерв P1 в (500000,0), противник P2 в (5000,0). После реального смертельного удара `deadHealth=0`; на tick 21, через 20 дальнейших тиков, `enemyVisible=true`, roster содержит 3 записи. Живого наблюдателя P1 около противника нет; матч не terminal.

**Влияние:** постоянная разведка местности и движений противника ценой погибшего юнита, нарушение OD-39.

**Критерий закрытия:** реальная смерть через combat должна прекращать предоставление обзора; отдельно определить возможность показа трупа, не смешивая её с правом давать vision. Тест через `Despawn` не заменяет этот witness.

### P1-04. Видимый преследователь раскрывает новые координаты уже скрытой цели

**LIVE для состояния + STATIC для wire propagation.** [MatchServer.cs:996](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs#L996) продолжает преследование живой AttackTarget и записывает её текущую позицию в MoveTarget без FoW-проверки. [ServerReplicationEmitter.cs:830](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Replication/ServerReplicationEmitter.cs#L830) переносит update видимого юнита без очистки этих полей; [ServerSnapshotDiffEngine.cs:265](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Replication/ServerSnapshotDiffEngine.cs#L265) делает то же для ADD. Персональный checksum хэширует эти поля, поэтому совпадение checksum не устраняет утечку.

**Witness:** A/P1 x=0, speed=500; B/P2 x=19900, speed=1000; A атакует B, B уходит к x=100000. После потери видимости:

| Tick | A.x | B.x | B виден P1 | A.MoveTarget.x |
|---:|---:|---:|---|---:|
| 1 | 500 | 20900 | false | 19900 |
| 2 | 1000 | 21900 | false | 20900 |
| 3 | 1500 | 22900 | false | 21900 |

Ticks 2–3 содержат **новые невиденные координаты**, а не допустимую last-seen позицию. Packet capture этой пробы не выполнялся; путь неизменённых полей до wire установлен исходниками.

**Критерий закрытия:** определить политику chase/last-seen и персональную проекцию связанных полей; одинаково применить её к ADD, UPDATE, keyframe и checksum. Тест должен декодировать полученный клиентом payload после hide цели.

### P1-05. Принятый reconnect может оставить матч на паузе без предельного срока

**LIVE component probes + STATIC end-to-end chain.** [SessionManager.cs:595](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Sessions/SessionManager.cs#L595) переводит rebind в Connected и удаляет grace tracking. Host добавляет обязанность ready, но [ServerHost.cs:341](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/ServerHost.cs#L341) не имеет deadline ожидания. После baseline retry budget [ReplicationReceiverFSM.cs:842](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/Replication/ReplicationReceiverFSM.cs#L842) переводит receiver в ConnectionFailed; coordinator/endpoint на это не реагируют reconnect-переходом.

**Witness A:** scripted carrier, Accepted response, без baseline; спустя 20 s: `endpoint=Established`, `coordinator=Resyncing`, `receiver=ConnectionFailed`, `reconnectAttempts=1`.

**Witness B:** настоящий ServerHost с test transport, событие reattach, без ready; после 12000 paused ticks (600 s при 20 Hz): `paused=true`, `awaiting=1`.

**Влияние:** при живом C0 и затянувшемся/провалившемся C2 resync клиент уже вне 200-секундного grace, но никогда не отправляет opcode 14. Автоматической ограниченной recovery/abandonment ветки нет. Бесконечность следует из отсутствия deadline, а не только из 600-секундного наблюдения.

**Критерий закрытия:** ограниченный серверный ready/resync deadline и реакция client session на terminal receiver; тест C0 alive + C2 baseline failure должен завершаться recovery либо явным terminal outcome, не вечной паузой.

## 5. P2 Major

### P2-01. Pooled buffers теряются на reject/overflow/teardown

**STATIC.** [LiteNetLibCarrier.cs:658](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/LiteNetLibCarrier.cs#L658): control buffer уже Rent, но rate-limit return на 672 не возвращает его. [EnqueueEvent:916](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/LiteNetLibCarrier.cs#L916) отбрасывает Message при overflow без Return. [Shutdown:452](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/LiteNetLibCarrier.cs#L452) очищает event queue и snapshot states без release; RemovePeerState:438 не вызывает Reset assembler. Аналогичный сброс незавершённых pooled fragments есть в [OwnDatagramCarrier.cs:283](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/OwnDatagramCarrier.cs#L283).

Это **не доказанная вечная managed heap leak**: потерянные массивы становятся GC-кандидатами. Но нарушается циркуляция пула, возникает повторная аллокация на реальных reject/disconnect путях. Закрытие: точный один Return/Reset для всех терминальных веток; accounting tests для rate limit, overflow, partial assembly disconnect и shutdown с непустой очередью.

### P2-02. Receive может воскресить state уже закрытого LiteNetLib connection

**STATIC race; принудительно не воспроизводился.** [LiteNetLibCarrier.cs:612](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/LiteNetLibCarrier.cs#L612) получает connectionId под lock, затем отпускает lock. Другой поток удаляет mapping/state в CloseConnection. Worker доходит до [HandleSnapshotReceive:699](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/LiteNetLibCarrier.cs#L699), создаёт новый PeerSnapshotState для старого ID без проверки liveness и сохраняет fragment. Поздний OnPeerDisconnected уже не найдёт mapping.

Orphan state может удерживаться до Shutdown и накапливаться по новым connection IDs. Lock защищает словарь, но не всю lifecycle-транзакцию. Закрытие: общая liveness/generation проверка перед публикацией state и deterministic barrier-тест receive-versus-close. Исправление только Return на teardown недостаточно.

### P2-03. Временный InvalidState окончательно выключает reconnect

**LIVE component + STATIC server ordering.** [SessionManager.cs:563](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Sessions/SessionManager.cs#L563) корректно отказывает, пока старая сессия Connected. При асимметричном разрыве новый peer может прийти раньше server-side уведомления о старом. Но [ClientReconnectCoordinator.cs:149](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/ClientReconnectCoordinator.cs#L149) делает любой non-Accepted terminal.

**Witness:** Reconnecting → InvalidState → повторный TransportLost оставляет `Terminated`. Закрытие: согласованная классификация retryable/terminal ответов и тест асимметричного disconnect. Не удалять защиту rebind активной сессии ради обхода ошибки.

### P2-04. Событие старой попытки подключения сбивает новую

**LIVE scripted carrier + STATIC real-carrier path.** Timeout в [ClientTransportEndpoint.cs:199](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/ClientTransportEndpoint.cs#L199) закрывает connection 2 и ставит Disconnected. В том же RemoteSliceSession.Pump уже открывается connection 3; на следующем pump старое Disconnected(2) обрабатывается [HandleEvent:348](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/ClientTransportEndpoint.cs#L348) без сравнения ConnectionId и запускает connection 4, не закрыв 3. Проверки поколения отсутствуют также у Connected и message dispatch.

**Witness:** `ConnectionsOpened=3` на 2000 ms и `4` на 2001 ms. Existing timeout test отдельно вызывает Endpoint.Pump перед Session.Pump, скрывая штатный порядок; его scripted responses всегда используют ID 1. У реального LiteNetLib mapping создаётся при ConnectToServer, а CloseConnection синхронно enqueue-ит disconnect, поэтому предпосылка применима не только к fake.

Закрытие: отбрасывать события чужого connection/attempt до обработки, сохраняя возврат buffer; regression должен идти только через Session.Pump с меняющимися IDs и поздними control messages.

### P2-05. Новый disconnect во время паузы получает остаток старого grace

**LIVE + STATIC.** [DedicatedServerRunner.cs:183](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/ServerHost/DedicatedServerRunner.cs#L183) передаёт frozen simulation tick в transport. [ServerHost.cs:302](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/ServerHost.cs#L302) проверяет истечение через `CurrentTick + _pausedGraceTicks`. Новый disconnected timestamp не включает уже прошедшее paused time.

**Witness:** спустя 3900 ticks паузы происходит новый disconnect; ещё через 101 tick новая сессия уже `Closed` — примерно 5.05 s вместо 200 s. Применимо и к повторному обрыву возвращающегося клиента. Закрытие: единая монотонная временная шкала для старта/проверки каждого grace; staggered и repeat-disconnect tests.

### P2-06. Закрытие/истечение другой сессии снимает паузу до ready

**LIVE + STATIC.** [ServerHost.cs:399](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/ServerHost.cs#L399): OnSessionClosed/GraceExpired вызывают CheckAutoResumeAfterSessionChange, который проверяет grace, но не `_awaitingResume.Count`. Resume очищает оставшиеся obligations.

**Witness:** A reattached, `awaiting=1`; закрываем B без ready от A → `paused=false`, `awaiting=0`. Закрытие: единый корректный predicate auto-resume для ready/close/expire/detach; тест A resync/countdown + B exit/expiry.

### P2-07. Selection не переживает пустой mirror или изменение slot при resync

**STATIC; observed happy path не покрывает эти случаи.** [ClientReplicationReceiver.cs:489](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/Replication/ClientReplicationReceiver.cs#L489) очищает мир сразу по Accepted response. До C2 keyframe может пройти кадр. [UnitSelectionController.cs:305](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/Presentation/UnitSelectionController.cs#L305) удаляет записи по entity+старому slot, а [ResyncSelectionViews:357](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/Presentation/UnitSelectionController.cs#L357) не ищет entity в новом slot. [TacticalVerticalSliceRunner.cs:905](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/Presentation/TacticalVerticalSliceRunner.cs#L905) выполняет Render/resync и затем обычный selection update/prune.

Две независимые предпосылки: C0 Accepted и C2 keyframe приходят в разные кадры; keyframe уплотняет slots после прежних REMOVE. Живой selected entity теряет запись/кольцо. Закрытие: сохранять selection identity на время resync и разрешать её по новому mapping после baseline; проверить оба случая и их сочетание.

### P2-08. Countdown не является серверным синхронным состоянием всех клиентов

**STATIC.** [ServerHost.cs:341](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/ServerHost.cs#L341) сразу снимает паузу после последнего ready; сервер не проверяет собственный пятисекундный срок. Принадлежность opcode 14 сессии проверяется, но добросовестность отсчёта этой сессии принимается на веру.

Кроме того, остающийся подключённым dedicated-клиент не получает общего authoritative pause/countdown состояния: его reconnect coordinator остаётся Connected. Требование OD-18/20 «все клиенты» не выполнено одним local overlay у возвращающегося клиента. Это не foreign-session spoofing: ранний ready возможен от законного владельца новой привязки.

Закрытие: авторитетные pause epoch/countdown deadline и доставка согласованного состояния всем участникам; two-remote-client overlay test и проверка раннего корректно аутентифицированного ready.

### P2-09. Отказы attack раскрывают статус скрытого entity

**LIVE core + STATIC response path.** [MatchServer.cs:600](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs#L600) выдаёт UnknownEntity/EntityNotAlive до visibility gate. Причина rejection передаётся клиенту транспортным ответом.

**Witness:** один собственный attacker, неизменённый sequence=1, три заранее известных тесту target ID: отсутствующий → `UnknownEntity`; мёртвый скрытый → `EntityNotAlive`; живой скрытый → `TargetInFogOfWar`. Обе существующие цели невидимы, sequence не consumed.

Закрытие: одинаковый внешний ответ для недоступных игроку целей либо эквивалентный information-hiding контракт; тест должен сравнивать декодированный ответ, а не только факт отказа. Gate до enqueue/sequence уже корректен и должен сохраниться.

### P2-10. Bootstrap-owned RemoteSliceSession не закрывается при уничтожении client root

**STATIC.** [GlobalFrontRuntimeBootstrap.cs:154](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/GlobalFrontRuntimeBootstrap.cs#L154) хранит созданную сессию в static field. [DisposeDedicatedSession:275](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/GlobalFrontRuntimeBootstrap.cs#L275) вызывается при замене режима, но не привязан к уничтожению root/quit. Runner в [Shutdown:1108](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/Presentation/TacticalVerticalSliceRunner.cs#L1108) намеренно не dispose-ит заимствованную сессию.

После scene unload/root destruction carrier/session могут оставаться живыми без pump; на quit отсутствует graceful disconnect. Это **не утверждение**, что socket живёт после завершения процесса. Закрытие: явный lifecycle owner созданной bootstrap-сессии; тест root destruction/scene unload и quit path без двойного Dispose чужой сессии.

### P2-11. Приёмочный E2E сценарий не доказывает заявленное завершение M1

**GATE, не дополнительная runtime-ошибка.** [SyntheticPlaytestPlayModeTests.cs:209](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Tests/PlayMode/SyntheticPlaytestPlayModeTests.cs#L209) вручную стартует host; один visual runner и второй remote session не являются двумя полными клиентскими представлениями. Сценарий напрямую управляет selection, а [StageDistributedEngagement:717](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Tests/PlayMode/SyntheticPlaytestPlayModeTests.cs#L717) подаёт attack через host, обходя клиентский command ingress. Нет assertion о победителе/завершении матча и нет проверки ChecksumMismatchCount в этом synthetic test.

Committed «5-minute» evidence длится **26.137 s**, disconnect на 20.006 s, FPS и GC по 3 s. Four-player EditMode harness использует virtual transport и не доказывает real-UDP team combat. Отдельные parity/transport tests полезны, но их свойства нельзя автоматически приписать combined FoW/combat/reconnect сценарию.

Закрытие: трассируемый real-entry-point сценарий до outcome для 1v1 и 2v2, все пользовательские команды через transport, per-client parity при FoW, reconnect обоих участников и соответствующая реальная длительность; исправить документы по фактически доказанному объёму.

### P2-12. Zero-GC gate нестабилен и существенно уже заявленного охвата

**GATE + свежий FAIL.** [SyntheticPlaytestPlayModeTests.cs:308](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Tests/PlayMode/SyntheticPlaytestPlayModeTests.cs#L308) отключает presentation runner и измеряет только session A/B pumps. Серверный tick и client rendering не входят в scoped окно, хотя OD verification заявляет их. [BuildMetrics:860](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Tests/PlayMode/SyntheticPlaytestPlayModeTests.cs#L860) записывает bytes как `0` или `-1` по числу blocks — это не независимое измерение bytes.

Последний full PlayMode run упал на [строке 541](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Tests/PlayMode/SyntheticPlaytestPlayModeTests.cs#L541): `Expected: 0; But was: 2`. Старый [gc_alloc_callstacks.txt:50](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Artifacts/Playtest/gc_alloc_callstacks.txt#L50) прямо говорит, что profiler capture **не воспроизводит исходную аллокацию** и меняет pacing. Найденные Editor/TestRunner stacks не доказывают происхождение невоспроизведённых samples.

**Не утверждается**, что эти 2 blocks созданы runtime GlobalFront или что full-frame URP allocations нарушают scoped transport budget. Утверждается: стабильный нулевой gate и весь заявленный scope не доказаны. Закрытие: воспроизводимый measurement boundary с привязкой samples/stacks к измеряемому коду, отдельные server/render gates либо суженный честный контракт; не заменять расследование повторными запусками до зелёного.

## 6. P3 Minor

### P3-01. Fragment expiry зависит от следующего входящего fragment

**STATIC.** [FragmentAssembler.cs:159](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/FragmentAssembler.cs#L159) вызывает Expire только из приёма; [LiteNetLibCarrier.Pump:474](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/LiteNetLibCarrier.cs#L474) не продвигает assembler timeout. Без следующего fragment незавершённая группа живёт дольше номинальных 2 s. Для живого peer удержание ограничено лимитами групп, поэтому не повышено до P1/P2 само по себе. Нужен timer-driven expiry и тест тишины после partial group.

### P3-02. Transport telemetry не является согласованным снимком

**STATIC + artifact observation.** [TransportMetrics.cs:19](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/TransportMetrics.cs#L19) содержит обычные mutable long. Например, DroppedRateLimited увеличивается worker в LiteNetLibCarrier:671 и consumer в [ServerTransportHost.cs:279](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/ServerTransportHost.cs#L279) без общего lock/Interlocked; read-modify-write может терять инкременты даже на x64. Кроме того, C0 accounting неполон: metrics показывают `serverMessagesReceived=0` при сотнях входящих datagrams. Не использовать эти поля как строгий conservation witness. Нужны точная семантика и согласованный accounting/snapshot.

### P3-03. Warm() пула публикует completion раньше завершения и допускает повторный warm

**STATIC; конкурентный production startup не установлен.** [TransportBufferPool.cs:30](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/TransportBufferPool.cs#L30): `_warmed` проверяется/устанавливается вне Gate, до наполнения buckets. Конкурентный caller может вернуться раньше окончания либо оба caller могут войти в warm. Это не повреждение Stack: Push/Pop защищены. Сделать check/set и completion publication частью одной синхронизированной операции; проверить concurrent warm/rent. Не выдавать теоретический warm race за найденный double-return.

### P3-04. Указанная в OD-36 compile-time граница UNITY_SERVER не совпадает с asmdef

**STATIC/documentation.** [GlobalFront.ServerHost.asmdef:14](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/ServerHost/GlobalFront.ServerHost.asmdef#L14) имеет пустые defineConstraints, тогда как DECISIONS:507 маркирует сборку UNITY_SERVER. Bootstrap намеренно допускает обычный player с `-dedicatedServer`; это объясняет решение и не доказывает UnityEngine-зависимость server core. Нужно согласовать ADR с фактической границей/поддержанными build modes, а не механически ставить constraint и ломать harness/listen scenarios.

## 7. Не включённые в основной счёт условные риски

### C-01. Diff failure не гарантирует провал merge через пропущенный tick

[ServerReplicationEmitter.cs:557](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Replication/ServerReplicationEmitter.cs#L557) при Diff failure не записывает tick, предполагая, что последующий merge откажет. Но [ReplicationHistoryRing.cs:193](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Replication/ReplicationHistoryRing.cs#L193) не проверяет непрерывность и может вернуть успешный пустой merge.

Возможный witness: допустимая уменьшенная update capacity=1, два moving units → overflow; следующий idle tick → потерянный update до дальнейшего checksum repair. **Для default capacity=4096 при текущем roster cap ≤1024 такая причина overflow не установлена**, поэтому риск конфигурационный, не дополнительный дефект default M1. Нужен gap marker/контракт непрерывности либо запрет несовместимых capacities.

### Не повышенные до находок предположения

- Двойной ручной Return одного массива позволяет получить alias, но production caller, делающий это, не найден. Отсутствие defensive provenance guard само по себе не double-return bug.
- Игнорирование результата `worker.Join(1000)` требует отдельного forced-delay teardown witness; здесь не объявлено доказанным default hang/UAF.
- Шифрование/PoP bearer secret явно отложены архитектурой; не добавлены как новый дефект в обход согласованного scope.

## 8. Положительные результаты обязательной проверки

### 8.1. Владение буферами

- [ClientTransportEndpoint.cs:180](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/ClientTransportEndpoint.cs#L180) и [ServerTransportHost.cs:118](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/ServerTransportHost.cs#L118) возвращают event Data после обработки в `finally`.
- [VirtualNetworkPipe.cs:76](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/VirtualNetworkPipe.cs#L76) задаёт default no-op ReturnEventBuffer для carrier без данного пула; это не второй владелец возврата.
- Reader storage копируется до Recycle; endpoint snapshot history имеет собственные buffers; текущий replication bridge потребляет callbacks синхронно. Server feedback также обрабатывается синхронно до возврата event buffer. Commands/secret не сохраняют borrowed array как отложенный payload.
- Fragment assembly формирует выход до возврата slices и обнуляет released references. Проверены send-copy paths; у фактической LiteNetLib 1.3.5 DLL копирование происходит до постановки в send queue.
- `Rent/Return` используют общий Gate; 8-thread isolated stress точного pool source, 80000 циклов, не обнаружил alias/errors. Это ограниченный stress, не доказательство всех interleavings.

### 8.2. Архитектура

[GlobalFront.Server.asmdef:17](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/GlobalFront.Server.asmdef#L17): `noEngineReferences=true`, references Core и LiteNetLib. Runtime reflection фактической сборки вернул только `netstandard`, `GlobalFront.Core`, `LiteNetLib`; DedicatedServerRunner находится в `GlobalFront.ServerHost`.

DedicatedClient использует remote world/identity/commands, не создавая локальный ServerHost. Прямые host access в local sandbox/test helpers не объявлены утечкой authority в DedicatedClient. Однако их нельзя использовать как доказательство готовности remote entry point — см. P1-01 и P2-11.

### 8.3. FoW и команды

Visibility engine использует integer grid с ячейкой 8192 мм, предвыделенные структуры и BitMask1024. Проверены отрицательные координаты и long-арифметика дистанций; подтверждённого overflow в допустимом диапазоне не найдено. История масок рассчитана на 120 ticks; отсутствие нужных endpoints приводит к консервативному fallback.

Emitter вычисляет переходы per-client: invisible→visible ADD, visible→invisible REMOVE, visible→visible UPDATE, invisible→invisible omit. Keyframe фильтруется до staging/counts; checksum строится над персональным current-visible subset, не всем world. Это корректность **набора entity**, а не доказательство безопасности всех полей внутри записи — P1-03/04 остаются.

[MatchServer.cs:624](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs#L624): FoW rejection действительно до Schedule:630 и CommitSequence:631. Невалидная атака не списывает sequence. P2-09 касается раскрываемой причины отказа, а не обхода самого enqueue gate.

### 8.4. Reconnect security и штатный happy path

SessionSecret32 генерируется криптографически; rebind сравнивает полный secret fixed-time. Opcode 9 и 13 обрабатываются как собственные wire records до обычного framed header; принятый response обновляет token/player/match. Opcode 14 проверяется против текущей server-side session binding; ready от чужой/старой привязки и unsolicited session не снимает паузу.

KeyframeInstalled подписан на coordinator; штатный countdown использует elapsed session time и epsilon; failed local ready send повторяется, после успеха не дублируется. Paused slices продолжают отправляться. Эти свойства подтверждены тестами и успешным synthetic reconnect, но не отменяют multi-session/order/liveness дефекты.

## 9. Тесты и воспроизводимость результатов

### 9.1. Исторические и свежие результаты

| Источник | EditMode | PlayMode | Статус доказательства |
|---|---:|---:|---|
| Сохранённые step465-polish XML, 2026-10-26 | 1334 Passed | 22 Passed | Историческое свидетельство; не текущая гарантия |
| Первичный MCP EditMode, текущий аудит | 1334 Passed | — | Job summary; затем подтверждено отдельным XML |
| Две попытки MCP PlayMode | — | 0 tests | Не засчитаны, несмотря на Passed |
| Native PlayMode после domain reload, 17:32:51–17:33:53Z | — | 22 Passed | XML был прочитан и пересчитан тогда; позднее ошибочно перезаписан EditMode callback, оригинал не сохранён |
| Native EditMode, 17:34:59–17:35:25Z | 1334 Passed | — | Уникальные test-case fullnames пересчитаны, XML сохранён |
| Финальный Native PlayMode, 17:45:08–17:46:11Z | — | **21 Passed / 1 Failed** | Уникальные test-case fullnames пересчитаны, XML сохранён |

Свежие authoritative файлы:

- [Artifacts/TestResults/grandaudit-m1-r3-editmode.xml](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Artifacts/TestResults/grandaudit-m1-r3-editmode.xml), 1334 unique cases, duration 26.0715853 s; SHA256 `77e4870cb2cba4f3b7eb3676f28a78db1445f43009b6ba9badd6813232fc0b5f`.
- [Artifacts/TestResults/grandaudit-m1-r3-playmode.xml](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Artifacts/TestResults/grandaudit-m1-r3-playmode.xml), 22 unique cases, duration 62.201868 s; SHA256 `24d8551de0ff78817931edae4ac0312725766a3f2aa98fd12b54bf458038911f`.

Failed test: `GlobalFront.Tests.PlayMode.SyntheticPlaytestPlayModeTests.SyntheticPlaytest_400Units_FogOfWar_ReconnectResume_ProducesFiveScreenshots`, assertion `the network/replication/telemetry pump must not allocate GC blocks`, expected 0, actual 2. Повторный прогон не скрыт и не заменён более ранним зелёным.

Ошибка сохранения принадлежала audit harness: оставшийся ResultsSavingCallbacks писал результат каждого suite в прежний PlayMode path. После обнаружения Edit XML закреплён отдельно, PlayMode повторён с отдельным destination, результаты скопированы после проверки содержимого, transient callbacks сброшены domain reload. Это не defect production GlobalFront и не повод засчитывать неправильный XML.

### 9.2. Дополнительные probes

| Probe | Среда | Наблюдение |
|---|---|---|
| Dedicated startup + 2v2 composition | Unity, реальный runner и endpoints, virtual carrier | 4 Established, Forming, 0 units; после manual start Skirmish и allied attack принят |
| Corpse vision | Unity, MatchServer + FoW filter | deadHealth=0, tick=21, enemyVisible=true |
| Hidden pursuit | Unity, MatchServer + FoW filter | новые скрытые координаты в own MoveTarget, таблица P1-04 |
| Hidden status oracle | Unity, core validation | UnknownEntity / EntityNotAlive / TargetInFogOfWar, sequence не consumed |
| Stale timeout event | Unity, existing ScriptedCarrier, только Session.Pump | 3 → 4 открытых подключения за 1 ms |
| Accepted без baseline | Unity, existing ScriptedCarrier | Established + Resyncing + ConnectionFailed; retry не идёт |
| Await-ready deadline | Unity, production host + existing ResumeTransport event seam | paused=true, awaiting=1 после 600 s virtual paused time |
| Close другой сессии | Та же component seam | awaiting 1 → 0 и paused=false без ready |
| Поздний disconnect | Та же component seam | новая сессия Closed через 101 tick |
| InvalidState | Unity, production coordinator | Terminated сохраняется после нового TransportLost |

Компонентные probes не представлены как десять новых NUnit tests или реальные сетевые сеансы. Production источники не модифицировались; mutation/bite tests исправлений не выполнялись, поскольку исправлений не было.

## 10. Шесть PNG, HUD, HP и GC evidence

Просмотрены все шесть **committed HEAD** PNG отдельно от изменённых локальных копий:

| HEAD-копия | Размер | Наблюдение |
|---|---|---|
| [01_spawn_and_fog.png](file:///C:/Users/Yaskiev/Desktop/UnityProjects/.scratch/grand-audit-m1-r3-20261027/head/01_spawn_and_fog.png) | 1920×1080 | Свои юниты/база, 0 selected; enemy base не видна |
| [02_box_selection.png](file:///C:/Users/Yaskiev/Desktop/UnityProjects/.scratch/grand-audit-m1-r3-20261027/head/02_box_selection.png) | 1920×1080 | 100 selected, Scout 50 / Tank 50, кольца, HP 100% |
| [03_clash_combat.png](file:///C:/Users/Yaskiev/Desktop/UnityProjects/.scratch/grand-audit-m1-r3-20261027/head/03_clash_combat.png) | 1920×1080 | HP 74%, кольца и health bars |
| [04_network_loss_pause.png](file:///C:/Users/Yaskiev/Desktop/UnityProjects/.scratch/grand-audit-m1-r3-20261027/head/04_network_loss_pause.png) | 1920×1080 | Pause overlay, grace 200 s / countdown 5, HP 54% |
| [05_reconnected_and_resumed.png](file:///C:/Users/Yaskiev/Desktop/UnityProjects/.scratch/grand-audit-m1-r3-20261027/head/05_reconnected_and_resumed.png) | 1920×1080 | Overlay скрыт, 100 selected и кольца, HP 50% |
| [06_interactive_menu.png](file:///C:/Users/Yaskiev/Desktop/UnityProjects/.scratch/grand-audit-m1-r3-20261027/head/06_interactive_menu.png) | 1920×1080 | Gameplay после запуска; это не изображение открытого editor menu |

[SelectionHudPresenter.cs:50](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/UI/SelectionHudPresenter.cs#L50) действительно задаёт ValueColumnX=124f; строки используют эту константу. На PNG значения расположены примерно на global x=436 при panel x=312, то есть local x=124; заметного label/value overlap нет.

**HP 74% и 50% в HEAD реальны, не объявляются подделкой.** Свежий успешный run дал на reconnect-кадре 52%; код проверяет факт боевого повреждения, а не точные 74/50. Значение после reconnect зависит от pacing. Наличие колец на выбранном кадре доказывает happy path, а не P2-07 edge cases.

### Метрики разных прогонов

| Метрика | Committed HEAD | Свежий успешный run | Финальный run с FAIL |
|---|---:|---:|---:|
| Длительность сценария, s | 26.1373 | 26.1658 | 26.1649 |
| FPS average, окно 3 s | 119.924 | 119.967 | 119.980 |
| FPS 1% low | 88.059 | 94.462 | 96.571 |
| Scoped GC blocks | 0 | 0 | **2** |
| Scoped bytes field | 0 | 0 | **-1 (sentinel, не bytes)** |
| Full-frame GC bytes, informational window | 17588400 | 9836000 | 17368100 |
| Spawned / visible A | 402 / 201 | 402 / 201 | 402 / 201 |
| RTT, ms | 29 | 28 | 29 |
| Countdown observed, s | 5 | 5 | 5 |
| HP при clash, % | 74 | 74 | 74 |

Исходный [committed metrics JSON](file:///C:/Users/Yaskiev/Desktop/UnityProjects/.scratch/grand-audit-m1-r3-20261027/head/playtest_metrics.json), [свежий успешный JSON](file:///C:/Users/Yaskiev/Desktop/UnityProjects/.scratch/grand-audit-m1-r3-20261027/live/playtest_metrics.json) и [финальный JSON с двумя blocks](file:///C:/Users/Yaskiev/Desktop/UnityProjects/.scratch/grand-audit-m1-r3-20261027/live/final-playtest_metrics.json) сохранены отдельно. Full-frame значения включают URP/Editor и не являются байтами на один frame или доказанной transport allocation. Короткое трёхсекундное FPS-окно не является длительным performance soak.

Тест записывает только PNG 01–05; в JSON перечислено пять screenshots. PNG 06 существует независимо, но его происхождение не подтверждено этим автоматическим run. Сценарий также может после 8 s ожидания явно вызвать DropConnection, поэтому alone не доказывает исключительно естественное обнаружение сетевого разрыва.

## 11. Условия повторной приёмки M1

1. Закрыть все пять P1 с regression witnesses, использующими фактический dedicated entry point и production death/visibility/reconnect пути.
2. Исправить buffer lifecycle и lifecycle race раздельно; проверить accounting и ordered close/receive interleavings, не только steady-state pool reuse.
3. Проверить multi-session pause/reconnect matrix: transient InvalidState, late old-connection events, новый disconnect во время паузы, failed baseline при живом C0, peer exit/expiry во время countdown, ранний ready.
4. Проверить selection через разнесённые C0/C2 кадры и изменившиеся slots; подтвердить общий countdown/overlay на обоих удалённых клиентах.
5. Провести real-entry-point 1v1 и настоящий team 2v2 до outcome; attack/stop/move только через client transport, FoW-aware parity до/после reconnect, заранее оговорённая длительность.
6. Установить происхождение GC failure и согласовать измеряемый scope с документацией; сохранить XML всех повторов, не только зелёных. Затем получить устойчивый полный test gate.
7. Обновить CURRENT_STATE / PROJECT_STATUS / Phase 04 / verification statements так, чтобы каждое утверждение имело соответствующий артефакт. Нынешние «ZERO DEFECTS / M1 ACHIEVED / 100% green» не выдержали независимую проверку.

**Окончательная резолюция: REJECT.** Базовая архитектура и значительная часть transport/replication happy path работоспособны, но confidentiality, dedicated composition и bounded reconnect остаются нарушенными; последнее полное PlayMode evidence дополнительно содержит зафиксированный провал GC gate. Исправления в рамках этого аудита не выполнялись.
