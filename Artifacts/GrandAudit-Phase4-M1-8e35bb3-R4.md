# Adversarial Re-Audit R4 — Phase 4 / Milestone M1 (1v1 Dedicated Server Prototype)

- **Проверенный HEAD:** `8e35bb3` (main), коммит `fix(multiplayer): remediate Grand Audit M1 defects for 1v1 dedicated prototype`. Рабочее дерево **чистое** (`git status --porcelain` пуст).
- **Дата:** 2026-10-27. Метод: zero-trust — каждый пункт задания проверен по исходникам на HEAD, свидетели прочитаны, гейт прогнан заново в живом редакторе; отчёты R1–R3 использованы только как словарь дефектов, не как доказательства.
- **Независимые прогоны этого аудита:**
  - EditMode: **1351/1351 passed, 0 failed, 0 skipped**, 28.41 s (job `f397c3e4…`).
  - PlayMode: **22/22 passed, 0 failed, 0 skipped**, 61.38 s (job `8f35ad73…`).
  - Коммиченные гейт-XML совпадают: `Artifacts/TestResults/m1-remediation-editmode-postbite.xml` (`total=1351 passed=1351`), `m1-remediation-playmode.xml` (`total=22 passed=22`); существуют также `-final` варианты обоих.
- **Итого:** 1373/1373 (1351 + 22) — совпадает с заявкой.

---

## Резолюция: **CONDITIONAL APPROVAL** — Milestone M1 (1v1 Dedicated Server Prototype) сертифицируем

P0 = 0 · P1 = 0 открытых · новых P2 = 0 · открытых P2-остатков R3 = 6 (все задокументированы в `CURRENT_STATE.md:107`) · новых P3 = 5

Все пять P1 и все шесть целевых P2 предыдущих аудитов закрыты с регрессионными свидетелями против production-классов; скоуп честно сужен до 1v1 (OD-48); документация приведена в соответствие с фактическими доказательствами. Условие сертификации — формальный перенос остаточного P2-бэклога (R3 P2-02, P2-05, P2-08, P2-10, P2-11, P2-12) в Milestone M2 с обязательной починкой P2-05 (grace-часы во время паузы) и P2-12 (флейкующий GC-гейт) до любой следующей сертификации реконнекта.

---

## 1. Статус исправленных пунктов

| Пункт | Статус | Доказательство закрытия (HEAD `8e35bb3`) |
|---|---|---|
| **P1-03** трупы открывают туман | **Закрыт** | `FogOfWarReplicationFilter.cs:110-117` — записи с `CurrentHealth <= 0` исключаются из vision-мира до `CalculateVisibility`, включая правило «свои видны всегда». Свидетель: `FogOfWarAntiCheatTests.Corpse_RealCombatDeath_StopsOpeningFogForItsOwner` — смерть через боевое разрешение (one-shot урон), `corpse.CurrentHealth == 0`, матч не терминален, `IsVisible == false` на тике смерти и ещё 5 тиков подряд. |
| **P1-04** утечка координат через `MoveTarget` преследователя | **Закрыт** | `MatchServer.cs:1011-1021` + `IsVisibleToOwner` (`:1056-1071`): при цели, скрытой по маске последнего завершённого тика, преследование не пишет новые координаты — freeze на last-known, без `MoveTarget` юнит держит позицию. Свидетели: `Pursuit_HiddenTarget_FreezesMoveTargetAtLastKnownPosition` (freeze 4 тика, `MoveTarget.X < live X`) и контрольный `VisiblePursuit_StillTracksTheTarget` (видимая цель продолжает отслеживаться). Анализ порядка тиков: координата, записанная на первом скрытом тике, равна стартовой позиции тика = последней видимой клиентом позиции, т.е. новых координат не раскрывается и в этом крае. |
| **P2-09** status-oracle в отказах атаки | **Закрыт** | `MatchServer.cs:600-610` — гейт `TargetInFogOfWar` стоит **первым** осмотром цели, до `TryGetValue`/`IsAlive`: отсутствующая, мёртвая и живая скрытые цели дают один и тот же отказ. Свидетель: `AttackRejection_HiddenMissingAndDeadTargets_AreIndistinguishable` (три цели → идентичный `TargetInFogOfWar`, sequence не списан) + `Passthrough_WithoutFilter_KeepsTheDetailedRejections` (исторический контракт без фильтра не сломан). |
| **P1-01** dedicated не запускает матч | **Закрыт** | `DedicatedServerRunner.cs:262-306` — `OnSessionAttached` ведёт лобби, `TryStartMatchWhenReady` по второму рукопожатию строит серверный ростер (`BuildDefaultMatchConfig`, `:313-352`) и вызывает `TryStartSessionMatch`; дедлайн 30 s от первого join как страховка. Свидетель: `DedicatedServerRunnerTests.TwoClients_AutoStartOneVersusOne_WithSpawnedArmies` — production runner + настоящие `RemoteSliceSession`/endpoint: 1 клиент → `IsMatchActive == false`, `UnitCount == 0`; 2-й → матч активен, 12 юнитов, два владельца. |
| **OD-48 / P1-02** фиксация скоупа 1v1 | **Закрыт (дескоуп)** | `DECISIONS.md` OD-48: M1 = 1v1 Dedicated Server Prototype, 2v2 официально в Milestone M2; `Phase_04` — «2v2 — [DEFERRED → Milestone M2]», harness `Match2v2PlaytestScenarioTests` объявлен заделом M2 и выведен из приёмки. Решение владельца задокументировано, не маскируется. |
| **P2-03 / P1-2** терминальный `InvalidState` | **Закрыт** | `ClientReconnectCoordinator.cs:182-186` — `InvalidState` не меняет состояние (остаётся `Reconnecting`, ретрай жив); терминальны только `InvalidSecret`/`SessionNotFound`/`GraceExpired`/`MatchFinished`. Свидетели: `ClientReconnectTests.Coordinator_OnReconnectResponse_InvalidState_StaysRetryable`; `NetworkImpairmentPlayModeTests` (real UDP) **удалил** ожидание серверного `Disconnected` перед ретраем и теперь реально проходит асимметричный обрыв. |
| **P1-05** вечная пауза после принятого реконнекта | **Закрыт** | `ServerHost.cs:48-56,333-364` — `_awaitingResume` стал `Dictionary<SessionId, ulong>` с меткой paused-тика; `ExpireOverdueResumeWaiters` на каждом `AdvancePauseTicks` принудительно закрывает молчащие сессии после 400 paused-тиков (20 s) через `SessionManager.CloseSession`. В production часы идут: `AdvanceRealTime` в паузе дренирует `AdvancePauseTicks(1)` на тик (`ServerHost.cs:274-300`), а `DedicatedServerRunner.Update` вызывает `AdvanceRealTime` каждый кадр. Свидетель: `ClientReadyToResumeTests.Host_ReattachedSessionWithoutReady_IsClosedAfterDeadline` — граница проверена потиково (399 → пауза держится, 400 → сессия `Closed`, пауза снята). |
| **P2-06** преждевременный resume | **Закрыт** | `ServerHost.cs:467-478` — `CheckAutoResumeAfterSessionChange` теперь требует `_awaitingResume.Count == 0` вдобавок к отсутствию сессий в grace; предикат совпадает с `OnClientReadyToResume`. Свидетель: `ClientReadyToResumeTests.Host_ClosingAnotherSession_DoesNotResumeWhileOneStillAwaitsReady`. |
| **P2-04** события чужого поколения соединений | **Закрыт** | `ClientTransportEndpoint.cs:333-343` — события с `ConnectionId != _connectionId` (при ненулевом текущем) отбрасываются до обработки; возврат буфера в `finally` пампа сохранён. Свидетель: `ReconnectRetryPolicyTests.Session_LateDisconnectFromPreviousSocket_DoesNotResetCurrentAttempt` (+ шов `EnqueueStaleDisconnected`). |
| **P2-01** потеря пуловых буферов | **Закрыт** | `LiteNetLibCarrier.cs`: rate-limit ветвь возвращает буфер (`:700-705`, копирование вынесено в `HandleControlReceive` с `finally reader.Recycle()`); queue-overflow возвращает (`:980-984`); `Shutdown` дренирует очередь через `Return` и сбрасывает ассемблеры (`:467-484`); `RemovePeerState` сбрасывает частичную группу фрагментов (`:443-450`). Учёт: `TransportBufferPool.RentCount/ReturnCount/OutstandingCount` (`:29-44`), инкременты сбалансированы по всем ветвям (не-пуловые пути не считаются с обеих сторон). Свидетели: `LiteNetLibBufferAccountingTests` — 4 теста, включая `Shutdown_ReturnsEveryQueuedBuffer` (8 арендованных → Outstanding +8 → возврат в базис) и `PeerRemoval_ReleasesPartialFragmentGroup`. |
| **P2-07** selection не переживает resync | **Закрыт** | `UnitSelectionController.cs`: `PruneStaleSelection` держит идентичность через пустое зеркало (`:316-334`, по `world.ResetCount`) и освежает слот через `_world.TryGetSlot` (`:349-365`); `ResyncSelectionViews` больше не доверяет кэшированному слоту — разрешение по EntityId с починкой кэша (`:406-425`); идентичность защищена `IsStillSelectable` (`:1130-1144`: view и world state сверяют entity, здоровье, владельца). Новый шов: `ClientReplicationWorld.TryGetSlot` (`:397-403`). Свидетели: `ResyncSelectionViews_FollowsTheEntityWhenKeyframeCompactsSlots`, `Prune_HoldsSelectionAcrossTheEmptyMirrorBeforeTheKeyframe`, `Prune_RefreshesASlotThatCompactedWithoutAResync`. |
| **Блок 6** документация | **Закрыт** | OD-48 фиксирует 1v1, честные ≈26.1–26.3 s и границы Zero-GC (scoped pump без презентации, 3 s; sentinel-поле `gcAllocBytes`); `Phase_04` и `PROJECT_STATUS` сняли «ZERO DEFECTS / M1 ACHIEVED» («REMEDIATION DELIVERED, PENDING REVIEW»); открытые остатки перечислены в `CURRENT_STATE.md:107`; `playtest_metrics.json` перегенерирован (26.177 s, scoped 0 блоков). |

**Регрессий не найдено.** Пройдены вручную на откат: порядок гейта атаки (passthrough-тест), freeze преследования (контрольный тест видимой цели), generation-фильтр (штатные таймаут/ретрай-тесты зелёные), autostart-шов (`AutoStartMatch = false` проставлен всем трём PlayMode-харнесам, владеющим своим ростером: `NetworkedClientPlayModeTests.cs:84`, `NetworkImpairmentPlayModeTests.cs:770`, `SyntheticPlaytestPlayModeTests.cs:561`), исключение трупов (чексуммы сервер/клиент сходятся: труп выходит из маски и из слотов клиента одновременно).

---

## 2. Новые находки этого аудита (все P3)

| ID | Суть | Локация |
|---|---|---|
| N-1 | Лобби не удаляет отключившихся до старта: `_joinedPlayers` только растёт, поэтому призрак первого игрока вместе со вторым живым даёт старт 1v1 с неконтролируемой армией; после старта в матч могут зайти 3-й и далее клиенты (до `MaxPlayers`) и получить пустой враждебный слот — нет блокировки лобби | `DedicatedServerRunner.cs:262-306` |
| N-2 | `ReconnectResult.InvalidState == Denied == 5` (алиасы): «retryable»-ветка ловит и `Denied` от hijack-guard, который на этом сокете не может успеть никогда; на проводе они неразличимы, поведение ограничено grace, но классификация и комментарий неверны | `ReconnectModel.cs:15-16`, `ClientReconnectCoordinator.cs:182` |
| N-3 | Юнит, погибший видимым, уходит к клиенту как REMOVE с причиной `FogOfWarHidden`, а не `Destroyed` (`EntityExists` находит труп в capture): клиент не отличает смерть от ухода в туман — семантика причины, не утечка | `ServerReplicationEmitter.cs:854-859` |
| N-4 | Документы пишут «изменения не закоммичены поверх `5beb1d1`», тогда как ремедиация закоммичена как `8e35bb3` — устаревшая формулировка в трёх документах | `CURRENT_STATE.md:7,52`, `DECISIONS.md` (M1 Remediation Verification), `Phase_04:3` |
| N-5 | Интерактивный плейтест (`InteractivePlaytestBootstrap`, `AutoStartMatch` не выключен) через 30 s одиночного ожидания стартует матч одной армией — поведение задокументировано как страховка, но для человеческого меню это сюрприз | `DedicatedServerRunner.cs:92-96,285-291` |

---

## 3. Открытый остаточный бэклог (не входил в задание ремедиации, задокументирован владельцем)

R3 P2: **P2-02** (receive может воскресить state закрытого LiteNetLib-соединения), **P2-05** (новый disconnect во время паузы наследует остаток старого grace: метка ставится по замороженному `CurrentTick`, а проверка идёт по `CurrentTick + _pausedGraceTicks` — свидетель R3: ~5 s вместо 200 s), **P2-08** (countdown/пауза не реплицируются всем клиентам), **P2-10** (bootstrap-owned `RemoteSliceSession` не закрывается при уничтожении root), **P2-11** (приёмочный E2E стартует хост вручную и подаёт часть команд мимо клиентского ingress; до outcome не доводится), **P2-12** (scoped GC-гейт флейкует и уже заявленного охвата; измерение не изменено, изменены только формулировки). R3 P3: P3-01..P3-04. Перечислены в `CURRENT_STATE.md:107`.

Замечание по P2-12: мои два прогона зелёные, коммиченные XML зелёные, но единичный зафиксированный R3 провал (`scopedBlocks == 2`) остаётся неповторённым и неатрибутированным; гейт по-прежнему может покраснеть без изменения кода. Поэтому сертификация реконнекта/zero-GC в M2 должна начинаться с воспроизводимого measurement boundary.

---

## 4. Условия CONDITIONAL APPROVAL

1. Остаточный бэклог §3 формально принят в Milestone M2 (владелец): записи в `ROADMAP`/backlog M2 с именами владельцев; `CURRENT_STATE.md:107` уже перечисляет состав.
2. До любой следующей сертификации реконнекта или zero-GC: закрыть **P2-05** (единые монотонные grace-часы) и **P2-12** (воспроизводимая граница измерения либо раздельные server/render-гейты).
3. Закрыть или явно принять N-1..N-5 (все P3): блокировка лобби после старта и вычерк отключившихся до старта; развести `Denied`/`InvalidState` на проводе; причина REMOVE `Destroyed` для погибших видимых; освежить формулировку «не закоммичены»; решить поведение одиночного авто-старта в интерактивном меню.
4. Сохранять практику: каждый будущий прогон гейта сохраняет XML независимо от цвета (R3 §9.1 показал цену обратного).

При выполнении 1–2 резолюция повышается до APPROVE без повторного полного аудита; новые P0/P1 требуют повторного прохода.
