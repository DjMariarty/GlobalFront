# Независимый Re-Audit Phase 4 / Milestone M1 — `8e35bb3`

**Дата:** 2026-10-27. **Тип:** adversarial, zero-trust re-audit после Grand Audit R3 (`5beb1d1`).

**Проверяемый коммит:** `8e35bb3` — `fix(multiplayer): remediate Grand Audit M1 defects for 1v1 dedicated prototype`.

**Фактический HEAD на момент проверки:** `76836e9` (`docs(certification): record Re-Audit R4 certification and synchronize Milestone M1 completion`). Кодовое дерево `Assets/` в `76836e9` идентично `8e35bb3`; поверх добавлены только `Documentation/CURRENT_STATE.md`, `Artifacts/GrandAudit-Phase4-M1-8e35bb3-R4.md` и изменённые playtest PNG/metrics. Код проверялся по `8e35bb3`; документация — по обоим срезам.

**Резолюция:**

# CONDITIONAL APPROVAL — M1 (1v1 Dedicated Server Prototype) с обязательными условиями

| Категория | Итог |
|---|---:|
| Новые P0 Blocker | 0 |
| Новые P1 Critical | 0 |
| Новые P2 Major | **3** (P2-13, P2-14, P2-15) |
| Остаточные P2 R3 | 6 + P2-01 residual для `OwnDatagramCarrier` |
| P3 | документация/гигиена гейта + P3-01..P3-04 |

APPROVE не обоснован: три новых P2 и остаточные P2-02/P2-05/P2-08/P2-10/P2-11/P2-12 остаются, а HEAD-документация уже заявляет `P0=0, P1=0, новые P2=0`. REJECT не обоснован для заявленного happy path: все перечисленные в задании исправления P1/P2 закрыты, а 1v1-автостарт двух клиентов подтверждён по реальному UDP.

**Граница эскалации:** если Координатор считает выход клиента из лобби до старта (P2-14) или terminal-состояние клиента после неудачного resync (P2-15) частью приёмки M1, а не M2, эти пункты становятся P1 Critical, и корректная резолюция — **REJECT**. В текущей формулировке M1 как 1v1 happy-path прототипа они классифицированы как P2 Major.

## 1. Исходная точка и сохранность

- До начала проверки рабочее дерево было чистым; `HEAD=76836e9`, `8e35bb3` — родитель.
- Код `Assets/` в `76836e9` не изменён относительно `8e35bb3`; различия только в документации и playtest-артефактах.
- Production-код, настройки, тестовые сцены и существующие отчёты не исправлялись. Коммиты и push не выполнялись.
- Все шесть playtest PNG, `playtest_metrics.json` и `gc_alloc_callstacks.txt` сохранены до прогонов и восстановлены побайтно после тестов; SHA256 до/после совпадают.
- Прогон PlayMode добавил в `ProjectSettings/ProjectSettings.asset` define `SENTIS_ANALYTICS_ENABLED`; это побочный эффект Editor/package, а не исправление. Файл восстановлен к HEAD; итоговое дерево снова чистое.
- Отчёт R4 и прежние audit-отчёты **не использовались как доказательства**. Все выводы ниже основаны на исходниках, независимом запуске Unity-тестов и отдельных пробах.

Ограничения: не выполнялись сборка/запуск отдельного `StandaloneBuildSubtarget.Server` player, полноценный two-process WAN-сеанс и five-minute soak. Live-пробы ниже — реальные LiteNetLib UDP-сокеты внутри Unity Editor; это не заменяет отдельный server build.

## 2. Тестовый гейт: подтверждён

| Suite | Файл | Уникальных test-case | Результат |
|---|---|---:|---|
| EditMode | [reaum1-8e35bb3-editmode-1351.xml](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Artifacts/TestResults/reaum1-8e35bb3-editmode-1351.xml) | 1351 | 1351 Passed, 0 failed/skipped |
| PlayMode, прогон A | [reaum1-8e35bb3-playmode-a-22.xml](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Artifacts/TestResults/reaum1-8e35bb3-playmode-a-22.xml) | 22 | 22 Passed, 0 failed/skipped |
| PlayMode, прогон B | [reaum1-8e35bb3-playmode-b-22.xml](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Artifacts/TestResults/reaum1-8e35bb3-playmode-b-22.xml) | 22 | 22 Passed, 0 failed/skipped |

**Итого: 1373** автоматизированных теста; уникальность `fullname` пересчитана по XML, а не по счётчику runner. EditMode — 32.85 s; PlayMode — 61.17 s и 61.12 s.

SHA256 независимых файлов:

- EditMode: `74eb93fa146e998f6c817c8d39f43393f3e26b1351325c751de65e60a1fc1870`.
- PlayMode A: `fb534a7fca0f7e4fb5f14702d84854f311bd6b304c7387ad56e23263903a29b9`.
- PlayMode B: `110ff4815d9c81a204722def0e167f159c3d1c6c67de7ccc8cb1046422ac74ed`.

Заявленные в документации `m1-remediation-editmode-postbite.xml` и `m1-remediation-playmode.xml` существуют локально и также содержат 1351/22 Passed, но **не отслеживаются git** (`.gitignore` исключает `/Artifacts/TestResults/*`). На чистом клоне этих доказательств не будет; ссылки на них в `CURRENT_STATE.md`/`PROJECT_STATUS.md` не воспроизводимы без артефактов.

**Стабильность:** два последовательных PlayMode-прогона зелёные. Это сильнее, чем один прогон R3, но не доказывает детерминизм GC-гейта: в R3 один полный прогон уже падал на `SyntheticPlaytestPlayModeTests` с `2` GC-блоками, а remediation не менял измерительный контур. Остаточный **P2-12** не закрыт; два зелёных прогона — наблюдение, не сертификат стабильности.

Артефакты прогонов сохранены отдельно: [метрики A](file:///C:/Users/Yaskiev/Desktop/UnityProjects/.scratch/re-audit-m1-8e35bb3-20261027/live/playmode-a-artifacts/playtest_metrics.json) и [метрики B](file:///C:/Users/Yaskiev/Desktop/UnityProjects/.scratch/re-audit-m1-8e35bb3-20261027/live/playmode-b-artifacts/playtest_metrics.json). Оба: scoped `gcAllocBlocksSteadyState=0`, `gcAllocBytesSteadyState=0`, длительность 26.1589 s / 26.1676 s, HP при clash 74%, countdown 5 s.

Шесть PNG в `Artifacts/Playtest` — 1920×1080. На `03_clash_combat.png` HUD показывает 74%, на `05_reconnected_and_resumed.png` — 50%; столбец значений HUD остаётся 124 px. Это соответствует текущим документам; изображение 06 по-прежнему не является снимком открытого editor menu и генерируется не synthetic-тестом (пять изображений 01–05), что уже входило в P2-11 R3.

## 3. Статус исправлений задания

| ID / пункт | Статус | Доказательство |
|---|---|---|
| **P1-03** мёртвые юниты не дают FoW | **Закрыт** | [FogOfWarReplicationFilter.cs:110](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Replication/FogOfWarReplicationFilter.cs#L110) исключает `CurrentHealth <= 0` из vision world; `FogOfWarAntiCheatTests.Corpse_RealCombatDeath_StopsOpeningFogForItsOwner` проходит в составе 1351. Тест использует реальный бой, не `Despawn`, и проверяет несколько тиков. |
| **P1-04** утечка координат через `MoveTarget` | **Закрыт** | [MatchServer.cs:1014](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs#L1014) замораживает `MoveTarget` на last-known при скрытой цели; [MatchServer.cs:1035](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs#L1035) по-прежнему пишет позицию только для видимой цели; `Pursuit_HiddenTarget_FreezesMoveTargetAtLastKnownPosition` проходит. Статическая цепочка `CopySnapshots → ToAddRecord → DeltaSnapshotWireCodec` не преобразует поле, поэтому замороженное значение уходит и в wire. |
| **P2-09** status-oracle в отказах атаки | **Закрыт** | Гейт FoW перенесён до `TryGetValue`/`IsAlive` в [MatchServer.cs:608-611](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs#L608); `AttackRejection_HiddenMissingAndDeadTargets_AreIndistinguishable` проверяет отсутствующий, мёртвый скрытый и живой скрытый target — все `TargetInFogOfWar`, sequence не списывается. Passthrough без фильтра сохраняет прежние детальные ответы. |
| **P1-01** dedicated 1v1 auto-start | **Закрыт для happy path**; новые P2-13/P2-14 | Production runner: [DedicatedServerRunner.cs:186](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/ServerHost/DedicatedServerRunner.cs#L186) подписка на `SessionAttached`, [DedicatedServerRunner.cs:267-283](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/ServerHost/DedicatedServerRunner.cs#L267) ростер, [DedicatedServerRunner.cs:291-313](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/ServerHost/DedicatedServerRunner.cs#L291) старт. **LIVE UDP:** два реальных LiteNetLib-клиента → `joined=2`, `active=true`, `units=12`, оба world `6`, `Streaming`, checksum mismatches `0` за 602 ms. |
| **2v2 scope** (OD-48) | **Закрыт** | [DECISIONS.md:543-549](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/DECISIONS.md#L543): M1 = 1v1, 2v2 → M2/Phase 5; [Phase_04:24](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/Phases/Phase_04_Multiplayer_Integration.md#L25) и `PROJECT_STATUS` повторяют. Runtime Team2v2 по-прежнему не подключён, но это теперь **явно вне M1**, а не скрытый дефект. |
| **P2-03** `InvalidState` не terminal | **Закрыт** | [ClientReconnectCoordinator.cs:182](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/ClientReconnectCoordinator.cs#L182) оставляет `Reconnecting`; новый `Coordinator_OnReconnectResponse_InvalidState_StaysRetryable` проходит; PlayMode impairment-тест, где rebind приходит до наблюдения сервером потери, зелёный в двух прогонах. |
| **P1-05** ready deadline 400 paused ticks | **Закрыт по серверной паузе**; новый P2-15 | [ServerHost.cs:55](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/ServerHost.cs#L55) `DefaultResumeReadyTimeoutTicks=400`, [ServerHost.cs:343](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/ServerHost.cs#L343) expiry; `Host_ReattachedSessionWithoutReady_IsClosedAfterDeadline` подтверждает `Closed` и снятие паузы. **LIVE COMPONENT:** после 400 paused ticks `paused=false`, `awaiting=0`, но `bound=1` — transport binding не освобождён (P2-15). |
| **P2-06** преждевременный auto-resume | **Закрыт** | [ServerHost.cs:475](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/ServerHost.cs#L475) проверяет `_awaitingResume.Count == 0`; `Host_ClosingAnotherSession_DoesNotResumeWhileOneStillAwaitsReady` проходит. |
| **P2-04** фильтрация поколений `ConnectionId` | **Закрыт** | [ClientTransportEndpoint.cs:341](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/ClientTransportEndpoint.cs#L341) отбрасывает события чужого поколения до dispatch; `Session_LateDisconnectFromPreviousSocket_DoesNotResetCurrentAttempt` проходит. |
| **P2-01** возврат pooled buffers | **Закрыт для LiteNetLib**; residual для `OwnDatagramCarrier` | [LiteNetLibCarrier.cs:700-716](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/LiteNetLibCarrier.cs#L700) возвращает buffer на rate-limit, [LiteNetLibCarrier.cs:979-985](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/LiteNetLibCarrier.cs#L979) на queue overflow, `Shutdown`/`RemovePeerState` освобождают очередь и assembler; `LiteNetLibBufferAccountingTests` (4 кейса) проходит. `OwnDatagramCarrier` не менялся: [OwnDatagramCarrier.cs:266-286](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/OwnDatagramCarrier.cs#L266) очищает connections без `Assembler.Reset`, [OwnDatagramCarrier.cs:322-340](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/OwnDatagramCarrier.cs#L322) удаляет connection без освобождения фрагментов. Для production dedicated (LiteNetLib) P2-01 закрыт; формулировка «все пути» в `Phase_04` слишком широка. |
| **P2-07** selection по `EntityId` и пустой resync-кадр | **Закрыт** | [UnitSelectionController.cs:314-333](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/Presentation/UnitSelectionController.cs#L314) удерживает selection через reset/пустое зеркало; [UnitSelectionController.cs:352](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/Presentation/UnitSelectionController.cs#L352) и [UnitSelectionController.cs:404-414](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/Presentation/UnitSelectionController.cs#L404) разрешают слот через [ClientReplicationWorld.cs:403](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/Replication/ClientReplicationWorld.cs#L403); три новых теста (`ResyncSelectionViews_Follows...`, `Prune_Holds...`, `Prune_Refreshes...`) проходят. |
| **Тестовый гейт** 1351+22 | **Подтверждён** | Три независимых XML выше; 1373 уникальных кейса; ProjectSettings после прогонов восстановлен. |
| **Документация** (Block 6) | **Не полностью** | Скоуп 1v1, ~26.2 s и scoped Zero-GC описаны правильно, но статус/commit/acceptance противоречивы — см. P3-05. |

**Регрессий среди перечисленных исправлений не найдено.** Новые P2-13/P2-14/P2-15 — дефекты новых граничных путей, а не откат уже существовавшего поведения.

## 4. Новые P2 Major

### P2-13. Таймаут автостарта создаёт матч на одного игрока и навсегда отклоняет позднего второго клиента

**LIVE UDP + STATIC.** [DedicatedServerRunner.cs:94](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/ServerHost/DedicatedServerRunner.cs#L94) задаёт `MatchStartTimeoutMs=30_000`; [DedicatedServerRunner.cs:302-313](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/ServerHost/DedicatedServerRunner.cs#L302) при отсутствии второго handshake через 30 s запускает матч с одним owner. Комментарий называет это «one-army skirmish», но OD-48 фиксирует M1 как 1v1 и говорит о старте по двум завершённым рукопожатиям; в документации fallback не описан.

**Проба:** `MatchStartTimeoutMs=100` (ускорение того же пути; production default 30 s), реальный LiteNetLib UDP, один клиент:

- через таймаут: `joined=1`, `active=true`, `units=6`; через 3 s клиент получает `world=6`, `Streaming`, checksum 0;
- второй клиент после этого: `joined=1`, `active=true`, `units=6`, `state=Disconnected`, `player=0`, `world=0`, `receiver=ConnectionFailed`.

`SessionManager.TryJoinMatch` разрешает вход только в `Forming` и возвращает `MatchNotForming` в Running-матч ([SessionManager.cs:251-253](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Sessions/SessionManager.cs#L251)). Поздний второй игрок не может ни войти, ни заменить бота; сервер остаётся в матче на одного. При задержке второго handshake >30 s (потеря/долгий коннект) 1v1 не собирается без рестарта.

**Влияние:** нарушение 1v1-контракта и недоступность матча для второго клиента в граничном, но реальном сценарии. **P2 Major**; intentional liveness fallback не отменяет отсутствие late-join/recovery и документации.

### P2-14. Disconnect клиента до старта навсегда «замораживает» лобби

**LIVE UDP + STATIC.** Runner подписан только на `SessionAttached` ([DedicatedServerRunner.cs:186](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/ServerHost/DedicatedServerRunner.cs#L186), [DedicatedServerRunner.cs:267-283](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/ServerHost/DedicatedServerRunner.cs#L267)); `_joinedPlayers` никогда не удаляет игрока при detach. `TryStartMatch` требует, чтобы **все** назначенные сессии были `Connected` ([SessionManager.cs:347](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Sessions/SessionManager.cs#L347)).

**Проба:** первый клиент подключён, затем `Dispose` (ClientRequested), после этого подключается второй:

- до ухода: `joined=1`, `active=false`, `Established`;
- после второго: `joined=2`, `active=false`, `units=0`, второй `Established`, `player=2`, `world=0`, `receiver=Streaming`.

Первый игрок навсегда остаётся в `_joinedPlayers`; `TryStartMatch` каждый кадр получает отказ из-за его Disconnected/Closed-сессии, а capacity=2 не даёт подключить нового противника. Второй клиент висит в Established без матча; `MatchStartTimeoutMs` не помогает, потому что `joined=2` и `TryStartMatch` снова падает на stale-сессии. Каждый кадр при этом заново строится `MatchConfig`/`UnitSpawnSpec[]` — дополнительная GC-нагрузка.

**Влияние:** обычный выход из лобби до старта блокирует сервер до рестарта. **P2 Major**; при трактовке lobby disconnect как части M1 — P1 Critical.

### P2-15. Ready-deadline закрывает сессию, но не транспорт: клиент остаётся ghost, binding не освобождается

**LIVE COMPONENT + STATIC.** [ServerHost.cs:343](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/ServerHost.cs#L343) вызывает `_sessions.CloseSession`; [SessionManager.cs:626-648](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Sessions/SessionManager.cs#L626) помечает сессию Closed и поднимает `SessionClosed`, но не знает о транспорте. `ServerHost.OnSessionClosed` ([ServerHost.cs:462](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/ServerHost.cs#L462)) только убирает `_awaitingResume` и проверяет auto-resume. `ServerTransportHost` освобождает binder только на detach/graceful-close ([ServerTransportHost.cs:264](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/ServerTransportHost.cs#L264), [ServerTransportHost.cs:621](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/Transport/ServerTransportHost.cs#L621)); на `SessionClosed` он не подписан.

**LIVE COMPONENT:** `ServerHost + ServerTransportHost + VirtualNetworkPipe`, сессия вручную привязана, host paused, `OnSessionReattached`, `AdvancePauseTicks(400)`:

- до: `bound=1`, `paused=true`, `awaiting=1`;
- после: `paused=false`, `awaiting=0`, `session=Closed`, **`bound=1`**, **`binderStillBound=true`**.

Клиентская сторона после Accepted остаётся в `Resyncing`; `RemoteSliceSession` автоматически переподключается только при `Reconnecting`/`Disconnected` ([RemoteSliceSession.cs:283-315](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Client/Presentation/RemoteSliceSession.cs#L283)), а terminal receiver failure (`ConnectionFailed`) не переводит coordinator в terminal и не закрывает endpoint. Поэтому клиент, не собравший baseline, после серверного дедлайна остаётся `Established/Resyncing` навсегда, сервер держит ghost-сессию и binding; освобождение слота происходит только при отдельном transport detach/выходе процесса.

**Влияние:** серверная пауза больше не бесконечна (P1-05 server-side закрыт), но поражённый клиент остаётся в вечном `Resyncing`, а binding/slot не освобождается. **P2 Major**; при требовании terminal-состояния клиента в M1 — P1 Critical.

## 5. Остаточные дефекты R3 (не новые)

Документы честно перечисляют их в [CURRENT_STATE.md:109](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/CURRENT_STATE.md#L109); remediation их не исправлял:

- **P2-02** — receive может воскресить state закрытого LiteNetLib connection; проверка liveness перед публикацией `PeerSnapshotState` не добавлена.
- **P2-05** — новый disconnect во время паузы наследует остаток старого grace; отдельная временная шкала не введена.
- **P2-08** — countdown не реплицируется всем клиентам; остающийся подключённым 1v1-игрок не получает authoritative pause/countdown.
- **P2-10** — bootstrap-owned `RemoteSliceSession` не закрывается при уничтожении client root.
- **P2-11** — приёмочный E2E-сценарий не покрывает production entry point до outcome; `Phase_04:44` всё ещё ставит `[x]` «два клиента могут завершить gameplay loop через real server», хотя реальные UDP-тесты используют ручной ростер/ручной старт. Наш live-проба подтвердила auto-start и baseline, но не полный loop до победы.
- **P2-12** — GC-гейт нестабилен и уже заявленного охвата; два зелёных прогона не отменяют прошлый flake, измерительный контур не менялся.

Дополнительно:

- **P2-01 residual** — `OwnDatagramCarrier` (тестовый/virtual carrier) всё ещё теряет незавершённые pooled-фрагменты на `Shutdown`/`SweepPendingRemovals`; для production LiteNetLib paths P2-01 закрыт.
- **P3-01..P3-04** — expiry фрагментов только при следующем fragment, несогласованная telemetry, warm race пула, `UNITY_SERVER`/asmdef-документация; без изменений.

## 6. Документация: скоуп и числа верны, статус — нет

**Правильно отражено:**

- M1 зафиксирован как **1v1 Dedicated Server Prototype**, 2v2 → M2 (OD-48): [DECISIONS.md:543](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/DECISIONS.md#L543), [Phase_04:24](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/Phases/Phase_04_Multiplayer_Integration.md#L25).
- Длительность плейтеста **≈26.1–26.3 s**, а не 5 минут: [CURRENT_STATE.md:51](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/CURRENT_STATE.md#L51), [PROJECT_STATUS.md:20](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/PROJECT_STATUS.md#L20), [DECISIONS.md:547](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/DECISIONS.md#L547). Наши метрики A/B: 26.1589/26.1676 s.
- Zero-GC ограничен клиентским транспортом/пайплайнами репликации при отключённой презентации, 3 s; полный кадр исключён; `gcAllocBytes` — sentinel: [CURRENT_STATE.md:52](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/CURRENT_STATE.md#L52), [Phase_04:63](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/Phases/Phase_04_Multiplayer_Integration.md#L63), [DECISIONS.md:548](file:///C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Documentation/DECISIONS.md#L548).
- HP 74%/50%, 1080p и 124 px HUD соответствуют текущим PNG/коду.

**Противоречия и переоценка (P3-05):**

- `CURRENT_STATE.md:7` говорит `[CERTIFIED: CONDITIONAL APPROVAL]` на `8e35bb3`, а `CURRENT_STATE.md:57` всё ещё пишет «Last Commit `5beb1d1`… remediation идёт без коммита». Файл противоречит сам себе.
- `CURRENT_STATE.md:13` утверждает «новые P2 = 0, все дефекты закрыты без регрессий», хотя `CURRENT_STATE.md:14/109` перечисляет открытые P2, а данный ре-аудит нашёл ещё три P2. Формулировка «все дефекты закрыты» неверна.
- `PROJECT_STATUS.md:23` и `:59` всё ещё говорят «изменения не закоммичены» и «uncommitted on `5beb1d1`»; `Phase_04:3` — «changes uncommitted». Это неверно для `8e35bb3`/HEAD.
- `Phase_04:44` ставит `[x]` «два клиента могут завершить gameplay loop через real server», хотя P2-11 признаёт отсутствие real-entry-point E2E до outcome; `Phase_04:57` называет end-to-end 1v1 real-UDP тестом, но новый auto-start тест — виртуальный, а реальные UDP-тесты используют ручной ростер/старт.
- Заявленные gate-XML `m1-remediation-*` не входят в git; на чистом клоне доказательства отсутствуют. `CURRENT_STATE.md:61/63`, `PROJECT_STATUS.md:23/39` ссылаются на них как на постоянные артефакты.
- `HEAD=76836e9` содержит дополнительный docs/artifacts commit и R4-отчёт; код идентичен `8e35bb3`, но `PROJECT_STATUS`/`Phase_04` не синхронизированы с новым `CURRENT_STATE`. R4 как доказательство не использовался.

Итог по документации: **скоуп 1v1, ~26.2 s и границы Zero-GC отражены правильно; статус сертификации, commit state, acceptance E2E и provenance gate-файлов — нет.** Это P3 само по себе, но оно усиливает необходимость условий ниже.

## 7. Положительные результаты и ограничения

- **Buffer ownership:** в LiteNetLib endpoint/host возвращают event buffers в `finally`; control buffer возвращается на rate-limit; очередь и partial fragments освобождаются на shutdown/peer removal; `Rent/Return` accounting сбалансирован в четырёх тестах. Достижимого production UAF/double-return в проверенных путях не найдено. `TransportBufferPool` остаётся internal; тесты используют `InternalsVisibleTo`.
- **Assembly boundaries:** `GlobalFront.Server` остаётся `noEngineReferences=true`, runner — в `GlobalFront.ServerHost`; dedicated client не создаёт authoritative host.
- **FoW:** dead-unit exclusion, hidden-pursuit freeze и единый отказ hidden/missing/dead подтверждены production-тестами; integer grid, per-client masks/checksum не изменены негативно.
- **Reconnect:** secret/opcode 9/13, bound-session ready, `InvalidState` retry, stale-generation filtering, auto-resume count и 400-tick deadline подтверждены кодом/тестами. Live UDP auto-start — 602 ms до `Streaming` у обоих клиентов.
- **Selection:** identity-based slot remap и hold через пустой mirror подтверждены тремя тестами.
- **Ограничения:** не проверялись отдельный server build, two-process WAN, полный loop до победы и длительный soak; live-пробы используют Editor + LiteNetLib на loopback и не заменяют эти проверки.

## 8. Условия CONDITIONAL APPROVAL

1. **P2-13:** убрать/переопределить one-player fallback так, чтобы M1 оставался 1v1; либо документировать его и реализовать late-join/replacement. Добавить тест: второй клиент приходит после таймаута и либо получает матч, либо получает явный terminal с понятным кодом; сейчас он получает `Disconnected` без матча.
2. **P2-14:** очищать/перестраивать `_joinedPlayers` при detach до старта; не позволять stale-сессии блокировать `TryStartMatch`; добавить regression: A уходит до B, B должен либо стартовать с новым A, либо получить terminal, но не висеть в Established без матча. Устранить per-frame `BuildDefaultMatchConfig` в отказе.
3. **P2-15:** на ready-deadline закрывать транспорт/освобождать binder и уведомлять клиента, либо на клиенте переводить `ConnectionFailed` в terminal/reconnect; добавить тест, что после forced close `AttachedCount` возвращается к baseline, а клиент не остаётся `Resyncing`. Иначе P1-05 закрыт только для серверной паузы, не для клиента.
4. **Остаточные P2:** либо закрыть P2-02/P2-05/P2-08/P2-10/P2-11/P2-12, либо явно принять их в M2 с указанием, что M1 не заявляет all-client countdown, production-entry-point full loop и детерминированный GC-гейт. P2-08/P2-11/P2-12 особенно важны для честности сертификации.
5. **Документация:** синхронизировать `CURRENT_STATE`, `PROJECT_STATUS`, `Phase_04`, `DECISIONS`; убрать «все дефекты закрыты / новые P2=0 / uncommitted», исправить `[x]` full-loop acceptance и provenance gate-XML (включить XML в репозиторий или не ссылаться на ignored-файлы). Указать фактический HEAD и различие `8e35bb3 → 76836e9`.
6. **Гейт:** сохранить 1351 EditMode + 22 PlayMode и добавить regression-тесты для P2-13/P2-14/P2-15; для P2-12 либо сделать GC-измерение детерминированным, либо явно ограничить контракт и не называть его стабильным 100% gate. Прогон не должен оставлять `ProjectSettings.asset` изменённым.

**Итоговая формулировка:** M1 1v1 happy path готов к **условной** приёмке; безусловная сертификация и формулировка «новые P2=0» не подтверждены. При включении lobby-disconnect/failed-client terminality в M1 вместо M2 пункты P2-14/P2-15 становятся P1, и резолюция должна быть **REJECT**.

**Окончательная резолюция: CONDITIONAL APPROVAL.** Production-код в рамках ре-аудита не изменялся; все исходные playtest-артефакты и настройки восстановлены.
