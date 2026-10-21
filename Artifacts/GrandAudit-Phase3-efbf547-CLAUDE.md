# ГЕНЕРАЛЬНЫЙ АУДИТ КАЧЕСТВА И АРХИТЕКТУРНОЙ ЦЕЛОСТНОСТИ GLOBALFRONT

**Роль:** Lead Adversarial Quality Auditor
**Целевой коммит:** `efbf547` (`main`) — подтверждён `git rev-parse --short HEAD`
**Дата аудита:** 2026-10-20
**Окружение:** Unity `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0`, C# 9 / .NET Standard 2.1 (`apiCompatibilityLevel: 6`), `useDeterministicCompilation: 1`, `activeInputHandler: 1`
**Метод:** живое исполнение через Unity MCP (`run_tests`, `read_console`, `manage_scene`, `execute_code`, `refresh_unity`) + построчный статический анализ 166 `.cs` файлов + независимая офф-лайн компиляция bundled SDK

> **Раскрытие:** аудитор не модифицировал ни одного исходника. Единственное изменение рабочего дерева — `ProjectSettings/ProjectSettings.asset`, внесённое самим редактором во время моих прогонов (см. §1.4). Все числовые утверждения ниже либо исполнены вживую, либо прочитаны из кода по указанной строке.

---

## 1. UNITY MCP LIVE EXECUTION SUMMARY

### 1.1 `run_tests` — EditMode

| Показатель | Значение |
|---|---|
| MCP job_id | `b8e063fa45d5430685dc1b229c05dc9f` |
| MCP summary | `total: 1162, passed: 1162, failed: 0, skipped: 0, resultState: Passed` |
| Длительность | 25.30 с |

**Авторитетное доказательство** (не job_id, а NUnit XML — `Artifacts/TestResults/grandaudit-editmode-efbf547.xml`, скопирован из `%USERPROFILE%/AppData/LocalLow/DefaultCompany/GlobalFront/TestResults.xml` немедленно после прогона):

```xml
<test-run id="2" testcasecount="1162" result="Passed" total="1162" passed="1162"
          failed="0" inconclusive="0" skipped="0" asserts="0"
          engine-version="3.5.0.0" clr-version="4.0.30319.42000"
          start-time="2026-10-20 17:39:29Z" end-time="2026-10-20 17:39:54Z"
          duration="25,3006882">
```

`grep -c 'result="Failed"'` → **0**; `grep -c 'result="Skipped"'` → **0**.
`start-time` 17:39:29Z **позже** до-прогоночной отметки (предыдущий XML был PlayMode-прогоном от 16:27:21Z), то есть это свежий прогон, а не закэшированный.

**Совпадение с бейзлайном: ТОЧНОЕ. 1162/1162, 0 failed, 0 skipped.**

### 1.2 `run_tests` — PlayMode

| Показатель | Значение |
|---|---|
| MCP job_id | `f7683a949615415290dfe626afab2d2a` |
| MCP summary | `total: 16, passed: 16, failed: 0, skipped: 0, resultState: Passed` |
| Длительность | 18.08 с |

```xml
<test-run id="2" testcasecount="16" result="Passed" total="16" passed="16"
          failed="0" inconclusive="0" skipped="0" asserts="0"
          start-time="2026-10-20 17:40:22Z" end-time="2026-10-20 17:40:40Z"
          duration="18,0793141">
```

Архив: `Artifacts/TestResults/grandaudit-playmode-efbf547.xml`. `Failed` = 0, `Skipped` = 0.

**Совпадение с бейзлайном: ТОЧНОЕ. 16/16.** Объявленный в `RISK_REGISTER.md:28-29` flake `Coordinator_TryBuildReconnectRequest_AllocatesZeroGC` в этом прогоне **не сработал** (`result="Passed"` в XML).

**Итого: 1178/1178 автоматизированных тестов, 0 падений, 0 расхождений с бейзлайном. P0-блокеров по тестам нет.**

### 1.3 `read_console` — НЕ ноль, но и не проект

Консоль **не пуста**. `read_console(types=[error,warning])` стабильно возвращает 5 записей типа `Exception`:

```
Error reason is 'NoSubscription' and no additional error information was provided
(https://generators.ai.unity.com).
  ./Library/PackageCache/com.unity.ai.assistant@1b11b9bfd84a/Modules/Unity.AI.ModelSelector/
  Services/Stores/Actions/ModelSelectorSuperProxyActions.cs:495   (× 5)
```

**Все 5 — из стороннего пакета `com.unity.ai.assistant`, ни одной из кода GlobalFront.** Источник — отсутствие подписки на Unity AI Generators, к проекту отношения не имеет. Записи переживают вход/выход из Play Mode и принудительную перекомпиляцию.

**Важная методологическая поправка:** утверждение брифа «в консоли 0 Errors и 0 Warnings» **буквально не выполняется**. Корректная формулировка: *0 ошибок и 0 предупреждений, порождённых кодом GlobalFront; 5 исключений стороннего AI-пакета.*

### 1.4 Скрытые предупреждения компилятора C#

Проверено двумя независимыми способами.

**(a) Авторитетная компиляция Unity.** `execute_code` → `CompilationPipeline.RequestScriptCompilation()`, затем `refresh_unity(wait_for_ready=true)`. `Logs/Editor.log` фиксирует две настоящие перекомпиляции:

```
5062: [ScriptCompilation] Requested script compilation because: Requested through public api
5069: Reloading assemblies after finishing script compilation.
5070: Reloading assemblies after forced synchronous recompile.
5691: [ScriptCompilation] Requested script compilation because: Requested through public api
5698: Reloading assemblies after forced synchronous recompile.
```

По всему логу (721 КБ, покрывающий обе перекомпиляции):
- `grep -c "warning CS"` → **0**
- `grep -c "error CS"` → **0**
- `grep -ic "rendergraph"` → **0** (предупреждений URP RenderGraph нет)

**(b) Независимая офф-лайн компиляция** (`Data/DotNetSdk/dotnet.exe build Library/Scratch38/typecheck.csproj --no-incremental`, net8.0, весь Runtime + EditMode + PlayMode): **0 `error CS`**, но **8 уникальных `warning CS0649`**:

| Файл:строка | Поле |
|---|---|
| `Runtime/Client/Presentation/UnitOverlayRendererFeature.cs:27` | `overlayMaterial` |
| `Runtime/Client/UI/MinimapRadarGraphic.cs:252` | `blipSprite` |
| `Runtime/Client/UI/SelectionHudPresenter.cs:63` | `panelSprite` |
| `Runtime/Client/UI/SelectionHudPresenter.cs:64` | `font` |
| `Runtime/Client/UI/TacticalPauseOverlay.cs:18` | `_overlayRoot` |
| `Runtime/Client/UI/TacticalPauseOverlay.cs:19` | `_messageText` |
| `Runtime/Client/UI/TacticalPauseOverlay.cs:20` | `_timerText` |
| `Runtime/Client/UI/TacticalPauseOverlay.cs:21` | `_countdownBannerText` |

**Вердикт: это НЕ дефект.** Все 8 полей проверены построчно и объявлены как `[SerializeField] private …` — их заполняет сериализатор Unity/Инспектор, а не код. Roslyn в голом csproj о сериализации Unity не знает, поэтому CS0649 здесь — ложное срабатывание офф-лайн сборки. Компилятор Unity (знающий о `[SerializeField]`) этих предупреждений не выдаёт, что и отражено в (a). **Ни одного CS0168 / CS0219 / CS0414 в проекте нет.**

### 1.5 Состояние сцены и URP (живой запрос через MCP)

`manage_scene(get_active)` → редактор открыт на **безымянной пустой сцене** (`name: ""`, `rootCount: 2`: `Main Camera` + `Directional Light`).

Попытка `manage_scene(load, "Assets/Scenes/SampleScene.unity")` **отклонена классификатором** как действие, не запрошенное пользователем явно. Поэтому сцена и URP-ассеты проинспектированы (i) сериализованно с диска и (ii) живым рефлексивным запросом к работающему редактору — это дало тот же результат без изменения состояния:

```
defaultRenderPipeline = UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset / PC_RPAsset
  renderer[0] type = UnityEngine.Rendering.Universal.UniversalRenderer
    rendererFeatures count = 1
      [0] UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion
LIVE UnitOverlayRendererFeature.Active = null
```

Факты о контенте:
- Единственная сцена проекта — `Assets/Scenes/SampleScene.unity` (build index 0). **Сцены `GlobalFront_Root` не существует** (бриф ошибается). В сцене 3 объекта: `Main Camera`, `Directional Light`, `Global Volume` — **ни одного компонента GlobalFront**.
- **Префабов в проекте ноль** (`find Assets -name '*.prefab'` → пусто). Вся презентация 400 юнитов строится процедурно в рантайме.
- Точка входа — `GlobalFrontRuntimeBootstrap.Initialize()` c `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` (`GlobalFrontRuntimeBootstrap.cs:14`).

### 1.6 Побочный эффект аудита (требует решения владельца)

Рабочее дерево было **чистым** на момент начала (`git status --short` → пусто, проверено первым же вызовом). После моих прогонов:

```
 M ProjectSettings/ProjectSettings.asset
```

`git diff` — ровно одна строка, внесённая редактором, не мной:

```diff
   scriptingDefineSymbols:
-    Standalone: APP_UI_EDITOR_ONLY
+    Standalone: APP_UI_EDITOR_ONLY;SENTIS_ANALYTICS_ENABLED
```

Это инъекция define пакетом Unity AI / Inference Engine (тот же, что даёт исключения в §1.3). К аудируемым исходникам отношения не имеет. **Откатывать не стал — это tracked-файл, решение за владельцем** (`git checkout -- ProjectSettings/ProjectSettings.asset`). Мои XML-артефакты лежат в gitignored `Artifacts/TestResults/` (`.gitignore:70`) и дерево не пачкают.

---

## 2. EXECUTIVE VERDICT

# `[CONDITIONAL APPROVAL]`

**P0 (Blocker) = 0. P1 (Critical) = 0. P2 (Major) = 4. P3 (Minor / Code Smell) = 8.**

Обоснование вердикта по рубрике брифа:
- **Не `[REJECTED]`**, потому что ни одного воспроизводимого дефекта P0/P1 не найдено: 1178/1178 тестов зелёные и точно соответствуют бейзлайну; детерминизм симуляции, изоляция сборок, Zero-GC в steady state, защита от hijack и каноническая чексумма **подтверждены построчно и частично исполнены вживую**.
- **Не `[APPROVED: ZERO DEFECTS]`**, потому что найдены 4 воспроизводимых P2. Два из них — не «косметика»: **(F-1)** zero-GC гейт на пути реконнекта физически неспособен упасть (доказано вживую тремя независимыми измерениями), и **(F-3)** ёмкости клиентских sink'ов по умолчанию вдвое меньше серверных, из-за чего легальная дельта молча выбрасывается и учитывается как повреждение от пиринга. Оба бьют ровно туда, куда пойдёт Phase 4.
- Отдельно: **(F-6)** — заявленная в 9 документах «единогласная сертификация тремя независимыми аудиторами» не имеет **ни одного** подтверждающего артефакта, а единственный процитированный отчёт является раскрытой самопроверкой автора кода. Это дефект целостности доказательной базы, на которой держится gate-решение о переходе к Phase 4.

**Проект не «монолитен», но и не заблокирован.** Переход к Phase 4 допустим при закрытии F-1 и F-3 (или явном принятии их владельцем с записью в `DECISIONS.md`).

---

## 3. SCORECARD ПО 6 ПИЛОНАМ

| # | Пилон | Оценка | Инженерное резюме |
|---|---|---|---|
| **I** | Детерминизм и изоляция сборок | **9 / 10** | Эталон. `GlobalFront.Core` и `GlobalFront.Server` имеют `"noEngineReferences": true`; **0** обращений к `UnityEngine`, **0** `System.Linq`, **0** `async`/`await`/`Task`/`Parallel.` в обеих сборках. Всё авторитетное состояние — `readonly struct WorldPointMm { int X; int Z; }` (`WorldPointMm.cs:10-20`), тик `ServerTickRate = 20` (`SimulationConstants.cs:9`). `float`/`decimal` в Core+Server — **0**; все 17 `double` — только пейсинг wall-clock, транспортный token-bucket и тестовая труба. 12 `[Test]`-гейтов с настоящими anti-vacuity контролями (`scanned > 60`, `decodedBodies > 400`, `resolvedMembers > 500`) и IL-декодированием. −1: брифовская предпосылка о «детерминированном засеянном генераторе» ложна — RNG в симуляции **нет вообще**, детерминизм структурный; и порядок обхода для чексуммы поддерживается двумя независимыми механизмами без связывающей их проверки. |
| **II** | Zero-GC и утечки в Hot Path | **7 / 10** | Продуктовый код — образцовый: все 6 перечисленных в брифе hot-path'ов (тик, эмиттер, приёмник, клиентский мир, тик-буфер/байндер, батчер оверлеев, миникарта) проверены построчно и **не содержат ни одной аллокации в steady state**; буферы предаллоцированы в конструкторах, возвращаемые значения — `readonly struct`. Пулинг честный: единственный `Object.Instantiate` в Runtime — `UnitViewPool.cs:490`, закрыт флагом `AllowCombatGrowth` (**false** по умолчанию, `:142`, `:427-441`); все 17 `Destroy`-вызовов — teardown/editor/bootstrap. Бенчмарк OD-28 измеряет GC **настоящим** рекордером Unity с положительным контролем. −3: **(F-1)** один zero-GC гейт на пути реконнекта мёртв и не может упасть; исключительные пути всё же аллоцируют (интерполированные `Debug.LogWarning` в `ClientReplicationReceiver.cs:415`, `UnitViewBinder.cs:221`). |
| **III** | Сетевая безопасность и Reconnect FSM | **8 / 10** | Hijack-защита подтверждена: session-aware guard ровно на `ServerTransportHost.cs:333-356` (бриф точен), второй независимый guard на `:429-438`, эксклюзивность `Bind` (`TransportSessionBinder.cs:51-60`), токен-идентичность `Release` (`:124-128`). Ровно **2** call site'а `Bind` в Runtime, оба защищены. Фрагменты ограничены на трёх слоях: 4 группы / 2048 фрагментов / 2 МБ / 2000 мс на пира, `AddFragment` никогда не бросает и не растёт. `TickDriver` — потокобезопасен: все **7** обращений к `_tick` под `_gate` (11 `lock`-блоков), `baseTick = _tick - (ulong)scheduled` на `:209` внутри `lock` (`:196-210`), `Tick`-геттер залочен (`:64-74`). −2: **(F-2/N-04)** потерянное событие `Disconnected` перманентно утекает и через 10 утечек даёт вечный `ServerFull`; **(F-4)** denial при hijack'е не инкрементирует ни одного счётчика; **(F-5)** на всей цепочке `Pump → HandleEvent → HandleMessage` нет ни одного `try`/`catch`. |
| **IV** | Репликация, MTU-фрагментация, Checksum | **7 / 10** | FNV-1a канонический: basis `2166136261u`, prime `16777619u` — стандартные 32-битные константы, две независимые копии (сервер `ServerReplicationEmitter.cs:679/714`, клиент `ClientReplicationWorld.cs:475/558`) с **байт-в-байт идентичной** последовательностью смешивания. Маскирование `MoveTarget → (0,0)` при `!HasMoveTarget` присутствует **с обеих сторон** (`:696-697` / `:490-491`) и мотивировано правилом провода OD-14. Порядок обхода строго по возрастанию `EntityId` с обеих сторон. Сборка `PartIndex` all-or-nothing: дубликат → `_droppedDuplicateSliceCount++`, out-of-order → переупорядочивание по возрастанию `PartIndex` в момент завершения (`:502-511`), расхождение с `TotalUnitCount` → `Malformed` + reset, старение 1500 мс (`AssemblyTimeoutMs`, `:192`). Частично применённого снапшота не существует в природе. −3: **(F-3)** `DefaultRemoveSink 256` < `DefaultRemoveCapacity 512` (и то же на add-канале) → легальная дельта молча отбрасывается и помечается как malformed; **(F-14)** `Owner` и `AutoAcquireEnemies` реплицируются и мутируют, но **исключены из чексуммы**; комментарии о пейсинге расходятся с собственными константами (F-9). |
| **V** | Презентация, URP 17.6 RenderGraph, RTS Controls | **7 / 10** | Инженерно сильно. `RecordRenderGraph` — единственный корректный хук URP 17.6 (проверено по исходникам пакета: `Execute`/`OnCameraSetup`/`Configure` в `ScriptableRenderPass.cs` отсутствуют); `AddRasterRenderPass<PassData>` + `UniversalResourceData`, `AllowPassCulling(false)`, `RenderPassEvent.AfterRenderingOpaques`. Чанки по `MaxInstancesPerDraw = 250` с **отдельным** `MaterialPropertyBlock` на каждый (`OverlayChunkBuffers`, `UnitOverlayRenderPass.cs:145-151`) — подозреваемого бага с общим MPB **нет**. Фильтр `CameraType.Game` — в **двух** местах (`UnitOverlayRendererFeature.cs:122`, `UnitOverlayRenderPass.cs:185`). `_ownsMaterial` реален, `ReleaseResources()` (`:123-138`) корректно уничтожает оба меша безусловно и материал только если владеет. Пикинг без физики: `ParallelAxisEpsilon = 1e-6f`, отказ по нефинитным входу **и** результату, `Physics.Raycast` в пути среза отсутствует. Миникарта: сатурирующий кламп написан явной ветвью с `NaN → 0f` (`MinimapProjection.cs:232-240`) — ловушка `Mathf.Clamp01(NaN)` осознанно обойдена; `AddUIVertexQuad` (4 вершины), а не `GetUIVertexStream` (6) — вторая ловушка обойдена; второй URP-камеры нет. `TacticalVerticalSliceRunner` — единственный владелец покадрового цикла (все 5 заимствованных компонентов выключаются и шагаются вручную, корутин в Runtime **нет**); `Shutdown()` → `RestoreBorrowedRig()` (`:1003-1032`) корректно **восстанавливает** заимствованную камеру и ввод и **уничтожает** только своё. −3: **(F-7)** renderer feature не установлен ни в один ассет (подтверждено живым запросом: `Active = null`), поэтому весь RenderGraph-путь оверлеев **не исполняется в собранном билде**; **(F-8)** 0.623 ms/frame — печатная диагностика, а не утверждаемая граница, и замерена headless с `createVisuals: false`. |
| **VI** | Готовность к Phase 4 | **6 / 10** | Шов существует, но не там, где предполагает бриф. `LocalReplicationLoopback` реализует `IReplicationTransport` (downlink) и `IReplicationUplink` (uplink), и **со-трудники** принимают интерфейсы: `_host.AttachReplication(_loopback, …)` и `new ClientTransportReplicationBridge(_loopback, _receiver, …)`, плюс есть готовый продакшен-`ClientTransportUplink` и явный `BindUplink(IReplicationUplink)` «например после реконнекта носителя». **Но сам раннер жёстко зашит на конкретику**: `new LocalReplicationLoopback(...)` внутри `AttachReplication()` (`:1168`), поле `private LocalReplicationLoopback _loopback` (`:446`), публичное свойство того же типа (`:527`), прямые обращения в `Shutdown()` (`:935`) и в тексте ошибки (`:1535`). Замена на `LiteNetLibCarrier` требует правки раннера, а не подстановки. Дополнительно: `ServerTransportHost.Pump` **не имеет ни одного продуктового call site'а** — серверный транспорт Phase 2 физически не подключён к циклу. `UnitViewTickBuffer` при фрагментированных C2-снапшотах ведёт себя корректно (см. §5, F-11): он стоит **ниже** сборки фрагментов и никогда не видит частичный снапшот. −4: F-3, F-11, F-15 и незакрытый N-04 — это ровно те места, где Phase 4 споткнётся первым. |

---

## 4. РЕЕСТР ВЫЯВЛЕННЫХ НАХОДОК

> Стандарт доказательства соблюдён: каждая находка P2 снабжена **исполненным** PoC (живой `execute_code` в редакторе) либо полным пошаговым трейсом с цитатами строк. Находки, для которых исполненный PoC отсутствует, явно помечены **«analysis-only»** и в P2 не повышались.

### F-1 · P2 · Zero-GC гейт пути реконнекта физически неспособен упасть

**Локализация:** `Assets/_GlobalFront/Tests/EditMode/Server/ServerReconnectionTests.cs:183-211`, тест `TryRebindSession_ZeroGcAllocations`

**Механика сбоя (A → B → C).** Дефект трёхслойный; каждый слой доказан отдельным живым измерением в редакторе.

**Слой A — метр мёртв.** Тест измеряет аллокации так (`:197`, `:206-210`):

```csharp
long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 100; i++) { … }
long allocatedAfter  = GC.GetAllocatedBytesForCurrentThread();
long totalAllocated  = allocatedAfter - allocatedBefore;
Assert.That(totalAllocated / 100, Is.LessThanOrEqualTo(64), …);
```

`GC.GetAllocatedBytesForCurrentThread()` под Mono-рантаймом редактора возвращает **абсолютный 0**, а не дельту. Живое доказательство (`execute_code`, окно из 200 заведомо аллоцирующих итераций — `new List<int>(64)` + конкатенация строк):

```
GC.GetAllocatedBytesForCurrentThread delta = 0   [before=0, after=0]
Unity GC.Alloc recorder sampleBlockCount, ТО ЖЕ ОКНО = 500
```

То есть метр не «ошибается», он постоянно равен нулю, тогда как рекордер Unity в том же окне видит 500 блоков. Проект **знает** об этом и в 12 других файлах использует рекордер, оставляя поясняющие комментарии («The editor's Mono runtime reports 0 from `GC.GetAllocatedBytesForCurrentThread()`, so zero-GC evidence uses Unity's GC.Alloc recorder», `ClientReplicationReceiverTests.cs:601-603`). Данный тест — один из двух, где мёртвый метр остался в **исполняемом** коде, а не в комментарии.

**Слой B — сценарий вообще не доходит до аллоцирующего пути.** Setup теста (`:185-190`) делает `CreateSession` → `NotifyConnectionLost`, **пропуская** `CreateMatch` + `TryJoinMatch`. Но `NotifyConnectionLost` требует состояния `Connected` (`SessionManager.cs:410-414`):

```csharp
if (!_sessions.TryGetValue(session, out var record) ||
    record.State != SessionState.Connected)
{
    return false;
}
```

После `CreateSession` сессия в состоянии `Created`, поэтому вызов возвращает `false`, сессия не становится `Disconnected`, и `TryRebindSession` выходит на `:554-557` (`record.State != SessionState.Disconnected → InvalidState`), **не дойдя до `new ClientSession(...)` на `:602`**. Живое воспроизведение точного кода теста:

```
lost1=False  warm=InvalidState/null  lost2=False
|| loop results: InvalidState/null x100;
```

Все 100 итераций — `InvalidState`, ноль аллокаций. Комментарий самого теста («Session rebind hot path creates ClientSession object; check that per-rebind allocation is minimal (< 64 bytes)», `:208`) описывает путь, который тест **никогда не исполняет**.

**Слой C — даже починенный тест упал бы: реальный бюджет превышен.** С корректным setup'ом (взят из соседнего `TryRebindSession_WithinGrace_ReturnsAccepted_AndRestoresAttachedState`, `:131-135`: `CreateMatch(2)` + `TryJoinMatch`) живое измерение:

```
join=Assigned  accepted=100/100
  GC.Alloc recorder sampleBlockCount, окно ре-байнда = 107   (контроль в той же сессии = 103 → рекордер жив)
  ClientSession runtime type = GlobalFront.Server.Sessions.ClientSession
```

и точный размер объекта (20 000 удержанных экземпляров, `GC.GetTotalMemory(true)`):

```
GC.GetTotalMemory(true) delta = 1 769 472 bytes
measured size per ClientSession = 88 bytes
бюджет теста = 64 bytes  →  превышение на 24 bytes
```

**Итоговая механика:** `0 / 100 = 0 ≤ 64` → `Assert` зелёный всегда, независимо от кода. Тест, носящий имя `ZeroGcAllocations`, (i) измеряет мёртвым прибором, (ii) не доходит до измеряемого пути, (iii) если бы доходил и прибор работал — **упал бы**, потому что продукция реально аллоцирует 88 Б на ре-байнд.

**Почему это P2, а не P3:** путь реконнекта — именно то, что Phase 4 нагрузит первым (массовые реконнекты при лаге). Единственный гейт, который должен был ловить деградацию его аллокаций, не ловит ничего и при этом своим именем и комментарием утверждает, что ловит. Следующий инженер, добавивший аллокацию в `TryRebindSession`, получит зелёный прогон.

**Минимальный валидный фикс** (3 правки, все внутри одного теста):
1. Довести сценарий до `Accepted` — добавить `var matchId = _sessionManager.CreateMatch(2);` и `_sessionManager.TryJoinMatch(sessionId, matchId, out var player);` перед `NotifyConnectionLost`, и **внутри цикла** asserting `RebindResult.Accepted`, чтобы деградация сценария больше не могла пройти молча.
2. Заменить метр на принятый в проекте: `GcAssert.Not.AllocatingGCMemory()` либо прямой `Recorder.Get("GC.Alloc").sampleBlockCount` с требованием двух последовательных чистых окон (паттерн уже отработан в `TacticalVerticalSliceBenchmarkTests.cs:645-656` вместе с положительным контролем).
3. Либо поднять бюджет до измеренных 88 Б и честно переименовать тест, либо убрать аллокацию в продукции — кэшировать/переиспользовать `ClientSession` в `SessionManager.TryRebindSession` (`:602`), как это уже сделано для `_cachedSnapshotTargets` в `TransportSessionBinder`.

**Побочно (тот же класс дефекта, P3):** `TransportPerformanceBaselineTests.cs:87/136/170` использует тот же мёртвый метр, но не в ассерте, а в диагностике — `TestContext.Out.WriteLine($"managed allocations: {(allocAfter - allocBefore) / 1024} KB")`. Строка всегда печатает `0 KB`. Утверждение на ней не построено, поэтому гейт не мёртв, но цитировать эту цифру как свидетельство zero-GC нельзя.

---

### F-2 · P2 · Потерянное событие `Disconnected` перманентно утекает и завершается вечным `ServerFull` (= backlog N-04, подтверждено)

**Локализация:** `LiteNetLibCarrier.cs:843-856`, `OwnDatagramCarrier.cs:923-936`, `ServerTransportHost.cs:206`, `:245-249`, `:440-444`, `:168-178`

**Механика сбоя (A → B → C → D).**

**A. Событие отвязки — обычная запись в ограниченной FIFO.** Оба носителя кладут `Disconnected` в ту же очередь, что и данные, с молчаливым выбросом при переполнении (`LiteNetLibCarrier.cs:843-856`):

```csharp
if (_events.Count >= TransportProtocol.MaxQueuedReceiveEvents)
{
    _metrics.DroppedQueueOverflow++;
    return;
}
```

`MaxQueuedReceiveEvents = 4096` (`TransportProtocol.cs:202`). Приоритета, резерва или отдельной очереди управления **нет** — для `EnqueueEvent` событие отвязки неотличимо от датаграммы.

**B. Единственная точка очистки — обработчик этого события.** `_rateLimiters.Remove(...)` на `ServerTransportHost.cs:206` — первый оператор `HandleConnectionLost`, который вызывается **только** из `case TransportEventType.Disconnected:` (`:192-194`). Других удалений нет: `Shutdown()` (`:168-178`) вызывает `_carrier.Shutdown(...)` и `_binder.ReleaseAll()`, но **`_rateLimiters` и `_pendingAcks` не трогает вообще**.

**C. Ключи не переиспользуются → утечка перманентна.** `_nextConnectionId` монотонен и никогда не сбрасывается (`LiteNetLibCarrier.cs:84`/`:496`, `OwnDatagramCarrier.cs:124`/`:577`, `SessionManager.cs:212`), поэтому утёкший ключ не может быть перезаписан позднейшим соединением.

**D. Терминальный исход — отказ всем.** `ServerTransportHost.cs:440-444`:

```csharp
if (_binder.BoundCount >= SimulationConstants.MaxPlayers)
{
    SendConnectDenied(transportEvent.ConnectionId, ConnectDenyReason.ServerFull);
    return;
}
```

`BoundCount => _byToken.Count` (`TransportSessionBinder.cs:38`), `MaxPlayers = 10` (`SimulationConstants.cs:12`). **Десять утёкших привязок → каждый последующий connect отклоняется `ServerFull` навсегда**, и `SendConnectDenied` (`:612-619`) дополнительно закрывает соединение.

**Уточнение, которого не было в прежних отчётах:** порог утечки лимитера и порог `ServerFull` **разные**. Carrier допускает `MaxConnections = 64` (`TransportProtocol.cs:211`), а байндер saturated уже на 10. Поэтому утёкшие лимитеры (память, до 64 записей) и отказ в подключении (байндинги, 10) — два независимых последствия одного пропущенного события; лимитер сам по себе `ServerFull` не вызывает.

**PoC:** исполненного PoC нет — воспроизведение требует ≥320 мс остановки host-насоса при 64 пирах × 200 pkt/s (12 800 событий/с против ёмкости 4096). **Analysis-only**; трейс полный и цитируемый. Существующий тест `BoundCount_ReturnsToBaseline_OnDisconnect` покрывает **только** путь доставленного события, то есть свидетеля у дефекта нет — это ровно тот случай, который прежняя сертификация честно пометила как «unmitigated and unwitnessed».

**Рекомендуемый фикс** (на выбор, первый предпочтительнее):
1. Сделать управляющие события неудаляемыми: отдельная очередь для `Connected`/`Disconnected` либо резерв головы в `_events` под них.
2. Каждый `Pump` сверять состояние хоста с живым набором соединений носителя (sweep `_rateLimiters` и `_binder` по `_carrier`-известным id) — закрывает и другие пути потери, не только переполнение.
3. Минимум: очистить `_rateLimiters` и `_pendingAcks` в `Shutdown()` и добавить регрессионный тест на путь **недоставленного** события.

---

### F-3 · P2 · Клиентские sink'и вдвое меньше серверных ёмкостей → легальная дельта молча отбрасывается и помечается как malformed (= backlog N-03, но шире)

**Локализация:** `ClientTransportReplicationBridge.cs:80/82` vs `ServerReplicationEmitter.cs:17/19`; свёртка ошибки `:381-393`; `DeltaSnapshotWireCodec.cs:349-354`

**Расхождение констант.** Бриф называет одну пару; на деле их **три**, и одна — в противоположную сторону от заявленной:

| Константа | Значение | Файл:строка |
|---|---|---|
| `ClientTransportReplicationBridgeConfig.DefaultRemoveSink` | **256** | `ClientTransportReplicationBridge.cs:82` |
| `ServerReplicationEmitterConfig.DefaultRemoveCapacity` | **512** | `ServerReplicationEmitter.cs:19` |
| `ServerSnapshotDiffEngine.DefaultRemoveCapacity` | **64** | `ServerSnapshotDiffEngine.cs:62` |
| `ClientTransportReplicationBridgeConfig.DefaultAddSink` | **256** | `ClientTransportReplicationBridge.cs:80` |
| `ServerReplicationEmitterConfig.DefaultAddCapacity` | **512** | `ServerReplicationEmitter.cs:17` |

**Новое, ранее не задокументированное:** (i) **add-канал рассогласован точно так же** (256 vs 512) — N-03 говорит только про remove; (ii) `ReplicationHistoryRing.cs:38` алиасит `DefaultMaxRemovesPerTick = ServerSnapshotDiffEngine.DefaultRemoveCapacity` (=64), поэтому отдельно созданное кольцо получает 64 removes/tick, а эмиттер с дефолтным конфигом — 512: **расхождение в 8 раз** между двумя серверными default'ами. `UpdateSink 4096` ↔ `DefaultUpdateCapacity 4096` — единственная согласованная пара.

**Механика сбоя (A → B → C).**

**A. Сервер легально выпускает дельту с 257…512 removes.** Бюджет сервера — 512; 512 × `RemoveRecordMinSizeBytes`(2) = 1024 Б, что заведомо внутри `DeltaSnapshotProtocol.MaxPayloadBytes = 8192`. Пакет корректен по всем серверным правилам.

**B. Клиент отказывает по ёмкости, но сообщает это отдельным кодом.** `DeltaSnapshotWireCodec.TryDecode` (`:349-354`):

```csharp
if (adds.Length < header.AddCount ||
    updates.Length < header.UpdateCount ||
    removes.Length < header.RemoveCount)
{
    return DeltaCodecResult.BufferTooSmall;
}
```

`BufferTooSmall = 1` — **отдельное** значение enum (`DeltaSnapshotWireCodec.cs:22`), отличное от `Malformed`.

**C. Мост сворачивает всё не-`Ok` в одну ветку и выбрасывает дельту.** `ClientTransportReplicationBridge.HandleDelta` (`:381-393`):

```csharp
var result = DeltaSnapshotWireCodec.TryDecode(…, _addSink, _updateSink, _removeSink);
if (result != DeltaCodecResult.Ok)
{
    _malformedPayloadCount++;
    return;
}
```

Последствия, все проверяемы по коду:
- **Данные молча теряются:** `_receiver.ReceiveDelta` (`:404-410`) не вызывается, `LastAppliedTick` не advances, ack не формируется.
- **Диагностика врёт:** нехватка ёмкости собственного буфера учитывается счётчиком повреждённого трафика — тем же `_malformedPayloadCount`, куда пишут genuinely corrupt payloads (`:354`, `:391`, `:426`, `:452`, `:470`, `:517`). Телеметрия укажет на пира, а не на конфигурацию.
- **Ремонт не запрашивается:** выход до обращения к приёмнику означает, что FSM (`ReplicationReceiverFSM`, backoff 500/1000/2000 мс, `AttemptLimit = 3`) о потере **не узнаёт**. Восстановление происходит лишь случайно — когда следующий применённый тик уйдёт достаточно далеко, чтобы `TryMergeRange` на сервере не сошёлся и эмиттер сам поставил keyframe (`ServerReplicationEmitter.cs:570-575`).
- Исключения нет, роста нет: `_removeSink` — фиксированный массив (`:252`).

**Кто поражён сегодня, а кто в Phase 4.** Дефолтный конфиг приходит из 2-аргументного конструктора (`:237-240`) — то есть **любой сетевой клиент Phase 4, созданный как `new ClientTransportReplicationBridge(uplink, receiver)`, получает 256**. Сертифицированный срез защищён **локальным** workaround'ом в единственном месте (`TacticalVerticalSliceRunner.cs:1180-1192`, все три sink'а выставлены в `ClientReplicationWorld.DefaultCapacity = 4096`) с честным комментарием о причине. Сегодня продуктового сетевого клиента нет (`ServerTransportHost.Pump` не подключён — см. F-10), поэтому **P2, а не P1**; но это ровно тот дефект, который станет P1 в первый день Phase 4, если воркаут не перенести в `Default`.

**PoC:** analysis-only (полный трейс выше); исполненного прогона с 257 removes не делал.

**Рекомендуемый фикс:**
1. Поднять `DefaultAddSink` и `DefaultRemoveSink` до 4096 (согласование с `ClientReplicationWorld.DefaultCapacity`) и удалить локальный воркаут из раннера, чтобы конфигурация была одна.
2. Разделить обработку `BufferTooSmall` и `Malformed` в `HandleDelta`: отдельный счётчик (например `_bufferTooSmallCount`) и явный запрос ре-базлайна вместо тихого `return`.
3. Связать константы утверждением (architecture gate или `Debug.Assert` в конструкторе конфига), чтобы серверная ёмкость не могла превысить клиентский sink — сегодня их ничто не сверяет.

---

### F-6 · P2 · Сертификация приписана трём независимым аудиторам, артефактов которых не существует

**Локализация:** 13 строк в 9 файлах документации; единственный цитируемый артефакт — `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`

**Утверждение под проверкой.** `Documentation/CURRENT_STATE.md:14`:

> Grand Re-Audit сертифицирован единогласно 3 независимыми аудиторами: Qwen 3.8 Max, NVIDIA Nemotron 3.5 Super VL, MiniMax M3.1 Flash с вердиктом `[APPROVED: ZERO DEFECTS]` (отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`).

Та же формулировка повторяется в `ARCHITECTURE.md:120`, `CHANGELOG.md:50`, `CURRENT_STATE.md:63`, `DECISIONS.md:434`, `Phases/Phase_02_Multiplayer.md:3` и `:31`, `PROJECT_STATUS.md:10` и `:58`, `ROADMAP.md:8`, `README.md:46`.

**Проверка.**

1. **Имена отсутствуют в артефактах.** `grep -rl "Qwen|Nemotron|MiniMax" Artifacts` (исключая `Logs/` и `TestResults/`) → **ноль файлов**. Все 13 вхождений — только в `Documentation/` и `README.md`. Ни одного отчёта, подписанного этими аудиторами, в репозитории нет.
2. **Процитирован один отчёт, а не три.** `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md` — единственный документ, на который ссылаются все 13 строк.
3. **Этот отчёт — раскрытая самопроверка, а не независимая.** Его собственная строка 8:
   > *Disclosure: sprint 2.5 was implemented in an earlier session of mine. This certification therefore re-derived every claim from the committed diff…*
4. **Имена этих аудиторов в отчёте не встречаются** вообще (grep по файлу).
5. **Содержание отчёта противоречит «единогласному ZERO DEFECTS».** Сам отчёт в §1 явно ограничивает вердикт: *«this certifies P0/P1 = 0 … It is **not** a clean bill of health for the whole network stack»*, и перечисляет 3 открытых P2 (N-02, N-03, N-04) плюс P3-пакет, отмечая, что *«N-04 in particular still reaches the same terminal outcome N-01 did»*. Документация же транслирует это как безусловное «Phase 2: 100% COMPLETED / AUDITED: ZERO DEFECTS».

**Механика вреда.** Это не косметика: gate-решение о переходе к Phase 4 опирается на `CURRENT_STATE.md` / `PROJECT_STATUS.md` как на источник истины. Приписывание самопроверке статуса тройного независимого консенсуса (a) создаёт ложную уверенность в закрытии N-02/N-03/N-04, которые сам отчёт считает открытыми, и (b) обесценивает весь аудит-трейл проекта: следующий аудит не может отличить реальную независимую проверку от атрибутированной.

**PoC:** приведённые выше grep'ы воспроизводятся одной командой; артефактов не существует, что и является доказательством.

**Рекомендуемый фикс.** Во всех 13 строках заменить атрибуцию на фактическую: «ре-аудит выполнил тот же агент, который реализовывал спринт 2.5 (самопроверка с раскрытием); вердикт ограничен P0/P1 = 0; 3 × P2 (N-02/N-03/N-04) и P3-пакет оставались открытыми». Либо, если независимая проверка действительно проводилась, — приложить её отчёты в `Artifacts/` и ссылаться на них. Одновременно снять пометку «AUDITED: ZERO DEFECTS» с Phase 2 в `CURRENT_STATE.md:7` и `:14`, приведя её в соответствие с §1 процитированного отчёта.

---

### Реестр P3

| ID | Локализация | Суть | Свидетель |
|---|---|---|---|
| **F-4** | `ServerTransportHost.cs:336-356` | Denial при cross-session hijack — **единственный** отказ в `HandleMessage`, не инкрементирующий счётчик. Все соседи следуют заявленному в `TransportMetrics.cs:3-8` паттерну «drop silently, count» (`DroppedBadToken`, `DroppedMalformed`, `DroppedBadVersion`). Попытка угона ростера невидима для телеметрии; единственное свидетельство — ответный пакет, который контролирует атакующий. Фикс — одна строка `_carrier.Metrics.DroppedBadToken++;` перед `return`. | analysis-only (подтверждено прежней сертификацией, §5.2) |
| **F-5** | `ServerTransportHost.cs:106-115`, `:184-200`, `:243` | На всей цепочке `Pump → HandleEvent → HandleMessage` **ноль** `try`/`catch` (в 669-строчном файле — только 3 `throw` в конструкторе). `InvalidOperationException` из `Bind` (`TransportSessionBinder.cs:53/59`) размотается в tick-цикл хоста **и прервёт `while`-дренаж на середине**, оставив остальные события в очереди носителя. Сегодня недостижимо (оба call site'а защищены), но это противоречит собственному принципу проекта в `LiteNetLibCarrier.cs:236-238` («a send failure must surface as a drop counter, never as an exception in the host/simulation tick loop»). Phase 4 не должен добавлять третий call site без guard'а. | analysis-only |
| **F-7** | `Assets/Settings/PC_Renderer.asset:36-38`, `Mobile_Renderer.asset:29-30`; `UnitOverlayRendererFeature.cs.meta:2` | `UnitOverlayRendererFeature` (GUID `995fb204d4689a9acaa8004c7782fdeb`) не упомянут **ни в одном** `.asset` — grep по GUID даёт только его собственный `.meta`. Живой запрос подтверждает: renderer[0] holds единственный feature `ScreenSpaceAmbientOcclusion`, `UnitOverlayRendererFeature.Active = null`. **Это задокументированное намерение, а не баг**: `DrawOverlaysDirect()` (`TacticalVerticalSliceRunner.cs:757-785`) существует именно для сессии «whose active URP asset has no `UnitOverlayRendererFeature`», и подписка на `RenderPipelineManager.endCameraRendering` реально стоит (`:1077`, снятие `:1085`). **Но следствие аудит-релевантно:** весь `ScriptableRenderPass`/RenderGraph-путь оверлеев (`RecordRenderGraph`, чанкирование, MPB, фильтр камеры) в собранном билде **не исполняется ни разу** и покрыт только EditMode-тестами, создающими feature через `ScriptableObject.CreateInstance`. | **исполнено вживую** (§1.5) |
| **F-8** | `TacticalVerticalSliceBenchmarkTests.cs:752-786` | `0.623 ms/frame` — значение из `TestContext.WriteLine($"OD-28 profile: … {averageMs:F3} ms …")`, **не константа и не assertion**. Единственное утверждение — `averageMs < FrameBudgetMilliseconds` (8.0). Замер: headless EditMode, `createVisuals: false` (`:1676`), 30 кадров warmup, 120 кадров замера, окно включает `StepSimulationTick() + StepPresentationFrame()` и **исключает** отправку на GPU и фактическую передачу `DrawMeshInstanced`. Документация (`CURRENT_STATE.md:15`, `:28`) подаёт это как профильный результат среза. Корректная формулировка: «CPU-сторона кадрового цикла, < 8 мс гарантировано, 0.623 мс — замер на машине аудита». | analysis-only (код прочитан) |
| **F-9** | `ServerReplicationEmitter.cs:21`; `ClientTransportReplicationBridge.cs:186-191` | Комментарии расходятся с собственными константами. (i) «~100 KB/s at the 20 Hz tick» при `DefaultPacingRefillBytesPerTick = 2048` даёт 2048 × 20 = **40 960 Б/с ≈ 40 КБ/с**. (ii) «The server paces a full 1024-unit keyframe in ~650 ms» при тех же константах: 1024 × 40 Б = 40 960 Б = 3 слайса, первый покрыт burst-seed 16384 Б, два следующих по 8 тиков (16384/2048) → **≈ 800-1000 мс**. Phase 4 будет настраивать пейсинг по этим цифрам. | analysis-only (арифметика по константам) |
| **F-10** | `ServerTransportHost.cs:106` | У `Pump(long nowMs, ulong drainTick)` **нет ни одного продуктового call site'а**: grep по `ServerTransportHost` в Runtime даёт только `ServerReplicationContracts.cs:75,81,83` (шов `TransportReplicationAdapter`) и самоссылки. Серверный транспорт Phase 2 не подключён к циклу — это работа Phase 4, но факт надо зафиксировать: «Phase 2 COMPLETE» означает «реализовано и протестировано», а не «работает в билде». | analysis-only |
| **F-11** | `TacticalVerticalSliceRunner.cs:446`, `:527`, `:1168`, `:935`, `:1535` | Раннер жёстко зашит на конкретику `LocalReplicationLoopback`: `new` внутри `AttachReplication()`, поле и публичное свойство конкретного типа, прямые вызовы в `Shutdown()` и в тексте ошибки. При этом со-трудники (`_host.AttachReplication`, `ClientTransportReplicationBridge`) принимают интерфейсы `IReplicationTransport` / `IReplicationUplink`, а продакшен-`ClientTransportUplink` и `BindUplink(IReplicationUplink)` уже существуют. Шов для Phase 4 есть, но **на уровне раннера его нет** — замена требует правки 5 мест. Фикс: типизировать поле/свойство интерфейсом и принимать uplink/transport как параметр `Initialize`. | analysis-only |
| **F-13** | `FragmentAssembler.cs:111-139`, `:128`, `:251-258` | `EvictUntilBudget` выполняется **до** проверки отказа, поэтому один враждебный фрагмент с заявленным `fragmentCount = 2048` (worst case 2048 × 1171 = 2 398 208 Б > 2 МБ) вытесняет **все** легитимные незавершённые группы пира и лишь затем отклоняется: усиление в одну датаграмму, уничтожающее до 4 in-flight сборок. Дополнительно `return null` на `:128` не инкрементирует **никакого** счётчика, а вытеснение пишется в `DroppedStaleSnapshot`, то есть причина неразличима. | analysis-only |
| **F-14** | `ServerReplicationEmitter.cs:675-705`, `ClientReplicationWorld.cs:457-499`; `DeltaSnapshotProtocol.cs:130/158`; `MatchServer.cs:888` | Из 11 полей снапшота в чексумму входят 7. Два **реплицируемых и мутирующих** поля исключены: `Owner` (`UnitDirtyMask.Owner = 1<<0`, выставляется в `ServerSnapshotDiffEngine.cs:216-219`) и `AutoAcquireEnemies` (`1<<6`, `:251-254`). `AutoAcquireEnemies` реально меняется в геймплее — `MatchServer.cs:888` (`record.AutoAcquireEnemies = false;` в `ExecuteMove`, т.е. **каждый приказ на движение**). Расхождение этого флага чексумма не видит; оно проявляется лишь косвенно, через `AttackTarget` на следующем тике. `Owner` после спавна не мутирует вовсе (проверено: `:62`/`:286`/`:427` — запись, остальные 8 вхождений — чтение), поэтому сегодня это латентный риск, а не активный desync. `UnitKind` исключён осознанно и задокументирован (`MatchServer.cs:88-94`). Третье: `MatchServer.Outcome` (`:230`) — авторитетное состояние, которое клиенты не fingerprint'ят. **Эскалация для Phase 4:** при появлении передачи владения юнитом `Owner`-расхождение станет невидимым для единственного механизма desync-контроля. Фикс: добавить оба поля в mix (они уже канонически упорядочены) — это wire-незатратно, чексумма не передаётся по проводу побайтово. | analysis-only |
| **F-15** | `MatchServer.cs:432-433`; `ClientReplicationWorld.cs:501-549` | Канонический порядок для чексуммы поддерживается **двумя независимыми механизмами**: сервер сортирует `_orderedUnits` в момент вставки (`List.Sort`, нестабильный introsort), клиент сортирует в момент вычисления (in-place heapsort). Ничто не утверждает их согласие. Сегодня расхождение невозможно: `_orderedUnits` только `.Add` + `.Sort` и никогда не теряет записей (проверено по всем 20 вхождениям), `EntityId` уникальны (`MatchServer.cs:368` отвергает дубликаты), а `CorpseReapSnapshotSource.CopySnapshots` (`TacticalVerticalSliceRunner.cs:270-297`) только **фильтрует** с сохранением порядка (`destination[written++] = snapshot` при обходе по возрастанию). Как только Phase 4 добавит настоящее удаление юнитов в `MatchServer`, инвариант порядка станет load-bearing и непроверенным. Фикс: связывающий гейт, сравнивающий порядок серверного capture и клиентской чексуммы на общем фикстуре. | analysis-only |

---

## 5. ОЦЕНКА ОТКРЫТОГО БЭКЛОГА P2

### N-02 · аллокация при C2 reassembly в `LiteNetLibCarrier` — **ПОДТВЕРЖДЕНО, но занижено на единицу**

Бэклог (`CURRENT_STATE.md:82`) и все производные документы говорят «`2+N` объектов». Реальное число — **`3+N`**. Полный перечень сайтов на пути приёма (`FragmentAssembler.cs`):

| # | Строка | Аллокация | Количество |
|---|---|---|---|
| A | `:131-137` | `new Group { … }` — `sealed class` (`:57`), heap-объект | **1** |
| B | `:136` | `Fragments = new byte[fragmentCount][]` — jagged-индекс | **1** |
| C | `:148` | `var slice = new byte[length];` — точная копия каждого фрагмента | **N** |
| D | `:167` | `var message = new byte[group.TotalBytes];` — итоговое сообщение для очереди событий | **1** |

Итого `1 + 1 + N + 1 = N + 3`. Примечательно, что прежний отчёт `Artifacts/Phase20-Adversarial-Audit-e6d91ea-CLAUDE.md:238` **сам перечисляет четыре слагаемых**, но заголовок и вся документация (`CURRENT_STATE.md:82`, `PROJECT_STATUS.md:83`, `Phases/Phase_02_Multiplayer.md:75`) говорят `2+N`.

**Что такое N.** `fragmentCount` из 8-байтного префикса (`LiteNetLibCarrier.cs:650`), ограничен `MaxFragmentsPerGroup = 2048`. На стороне отправки (`:263-273`) `maxChunk = min(GetMaxSinglePacketSize(Sequenced) - 8, 1171)`; при стартовом MTU LiteNetLib 1024 → `maxChunk ≈ 1016`, при обнаруженном 1400 → `1171`. Для полного 16384-байтного слайса **N ≈ 14…17**.

**Объём мусора:** ≈ 2× от payload транзитно. Для 16384 Б при N = 17: 17 срезов (16384 Б) + итоговые 16384 Б + `byte[17][]` + `Group` ≈ **32.8 КБ на слайс, на клиента, на worker-потоке**.

**Дополнительно, ранее не учтённое:** путь **отправки** `OwnDatagramCarrier` не zero-alloc — `:405` `var fragment = new byte[fragmentLength];` внутри цикла по фрагментам (`:390-425`), по одной аллокации на фрагмент, удерживаемой в `PendingItem.Fragment` (`:415`) для ретрансмита. Приёмная сторона own-носителя аллоцирует и на нефрагментированных путях (`:816`, `:872`). Отправка LiteNetLib, наоборот, honest zero-alloc благодаря общему scratch'у под отдельным gate (`:42-51`, `:311-329`).

**Важное ограничение доказательной базы:** существующие тесты этого пути **бюджетируют две аллокации, а не утверждают ноль** (`LiteNetLibCarrierIntegrationTests.cs:658-710`, `:747-796`, `:899-921`), а рекордер `GC.Alloc` фильтруется по текущему потоку — **аллокации worker-потока ему невидны по построению**. То есть N-02 сегодня в принципе не имеет исполняемого свидетеля.

**Оценка аудитора:** остаётся **P2**, приоритет — перед Phase 8, но **не** откладывать до Phase 8, если Phase 4 собирается гонять реальные keyframe'ы: 32.8 КБ мусора на слайс на клиента при 10 клиентах и пейсинге ~1 слайс/400 мс — это уже сотни КБ/с на worker-потоке, который рекордер не видит. Корректный фикс — пул срезов с арендой по `fragmentCount` и переиспользуемый итоговый буфер; минимум — добавить счётчик аллокаций на worker-потоке, чтобы дефект стал измеримым.

### N-03 · `DefaultRemoveSink = 256` < `DefaultRemoveCapacity = 512` — **ПОДТВЕРЖДЕНО И РАСШИРЕНО**

Значения подтверждены дословно (`ClientTransportReplicationBridge.cs:82` = 256, `ServerReplicationEmitter.cs:19` = 512). Поведение при массовом удалении трейсировано полностью — см. **F-3** в §4. Кратко:

- **Серверная сторона ведёт себя правильно.** `ServerSnapshotDiffEngine.Diff` при переполнении возвращает `CapacityExceeded` (`:132-135`), эмиттер инкрементирует `_diffFailureCount` и **не пишет тик в history ring** (`ServerReplicationEmitter.cs:515-528`); последующий merge через этот тик не сходится и клиенту ставится keyframe (`:570-575`). Ничего не теряется молча, ничего не бросается, ничего не растёт. Цена — полный ре-базлайн на клиента, а не потеря данных.
- **Клиентская сторона — вот где дефект.** Легальная дельта с 257…512 removes получает `BufferTooSmall`, сворачивается в `_malformedPayloadCount++` и выбрасывается без ack и без запроса ремонта.

**Расширения сверх бэклога:** рассогласован и **add**-канал (256 vs 512); `ServerSnapshotDiffEngine.DefaultRemoveCapacity = 64` против `ServerReplicationEmitterConfig.DefaultRemoveCapacity = 512` — два серверных default'а расходятся в 8 раз через алиас `ReplicationHistoryRing.cs:38`. Сертифицированный срез защищён локальным воркаутом (`TacticalVerticalSliceRunner.cs:1180-1192`), дефолт — нет.

**Оценка аудитора:** **P2 с явной эскалацией до P1 в первый день Phase 4.** Фикс — в F-3; ключевое — не просто поднять число, а разделить `BufferTooSmall` и `Malformed` в телеметрии, иначе Phase 4 будет отлаживать «повреждённый трафик» там, где была нехватка собственного буфера.

### N-04 · утечка rate limiter при потере события `Disconnected` — **ПОДТВЕРЖДЕНО, трейс уточнён**

См. **F-2** в §4 — полная цепочка A→B→C→D с цитатами. Подтверждаю прежний вывод: **терминальный исход N-01 (вечный `ServerFull`) по-прежнему достижим**, другим механизмом.

**Уточнение к прежнему отчёту:** прежняя формулировка («rate limiter **и binding** могут утекать → `ServerFull`») смешивает два порога. Лимитеры ограничены `MaxConnections = 64` на уровне носителя, а `ServerFull` наступает на `MaxPlayers = 10` по `BoundCount`. Утечка лимитеров сама по себе `ServerFull` не вызывает — это утечка памяти; отказ всем даёт только утечка **байндинга**. Для Phase 4 это различие существенно: чинить надо байндинги, а лимитеры чистятся заодно.

**Оценка аудитора:** **P2**, но самый рискованный из трёх — единственный, у которого есть путь к полному отказу сервиса, и единственный без свидетеля (`BoundCount_ReturnsToBaseline_OnDisconnect` покрывает только доставленное событие). Приоритет в бэклоге следует поднять **выше** N-02 и N-03 и закрыть **до** Phase 4, а не в Phase 8.

---

## 6. ФИНАЛЬНОЕ ЗАКЛЮЧЕНИЕ

### Готовность кодовой базы к Phase 4 (Multiplayer Gameplay Integration)

**Ядро симуляции и транспортная безопасность — готовы.** То, на чём Phase 4 будет стоять, проверено построчно и выдерживает adversarial-проверку:

- **Изоляция сборок абсолютная.** `GlobalFront.Core` и `GlobalFront.Server` объявлены с `"noEngineReferences": true` и содержат **0** обращений к `UnityEngine`, **0** `System.Linq`, **0** `async`/`await`/`Task`/`Parallel.`, **0** `float`/`decimal`. Единственный `Thread` — worker-поток LiteNetLib, который только кладёт события в ограниченную очередь (`LiteNetLibCarrier.cs:448`, `:477-479`). `System.Random` встречается ровно один раз — в засеянной тестовой трубе `VirtualNetworkPipe` (`:126`), которую гейт `TransportExemption_ExcusesTheSeededDrawAndNothingElse` (`ArchitectureInvariantsTests.cs:422`) держит единственной разрешённой. **Симуляция не использует случайность вообще** — детерминизм структурный (каноническая сортировка по `EntityId`, целочисленный `IntegerSquareRoot`, `checked`-арифметика, стабильное first-found-wins разрешение ничьих).
- **Авторитетное состояние строго целочисленное.** `readonly struct WorldPointMm { int X; int Z; }` в миллиметрах, тик `ServerTickRate = 20`. Все 17 `double` в Core+Server — wall-clock пейсинг (`FixedStepClock`), транспортный token-bucket (`RateLimiter`) и тестовая impairment-труба; каждый покрыт явным exemption'ом в гейте, и каждый exemption обязан «keep earning its place» (`ArchitectureInvariantsTests.cs:541-544`).
- **Гейты не вакантны.** 12 `[Test]`-методов: 6 инвариантов + 6 anti-vacuity контролей, каждая из которых доказывает, что сканер что-то видел (`scanned > 60`, `decodedBodies > 400`, `resolvedMembers > 500`, положительные свидетели `Contains.Item("RtsCameraController")` и т.д.). IL-декодирование ловит `System.Linq` и недетерминированные типы на уровне member-token'ов, а не строк.
- **Hijack закрыт на трёх слоях**, каждый с цитируемой строкой: guard `:333-356`, эксклюзивность `Bind`, токен-идентичность `Release`. Ровно два call site'а `Bind` в Runtime, оба защищены.
- **Чексумма каноническая**: стандартные константы FNV-1a, идентичная последовательность смешивания на двух сторонах, маскирование `MoveTarget` с обеих сторон, строгий порядок по возрастанию `EntityId`.
- **Сборка keyframe'ов all-or-nothing**: частичный снапшот не может дойти до приёмника ни по одному пути — дубликат, расхождение с `TotalUnitCount`, устаревшая генерация и таймаут 1500 мс обрабатываются явно, а переупорядочивание частей выполняется один раз в момент завершения.
- **Zero-GC продуктового кода реален и измерен живым прибором** с положительным контролем; единственный `Instantiate` закрыт флагом, по умолчанию запрещающим рост в бою.

**Что блокирует безусловное одобрение.** Четыре P2 — и все четыре бьют в стык Phase 2 ↔ Phase 3, то есть ровно туда, где начнётся работа:

1. **F-1** — единственный zero-GC гейт на пути реконнекта не способен упасть (доказано вживую тремя измерениями), а реальный бюджет превышен: 88 Б против заявленных 64.
2. **F-3 / N-03** — дефолтные ёмкости клиентских sink'ов вдвое меньше серверных; легальная дельта молча выбрасывается и учитывается как повреждение трафика. Сертифицированный срез спасён локальным воркаутом, дефолтный конструктор — нет.
3. **F-2 / N-04** — потерянное событие отвязки даёт перманентную утечку байндинга и через 10 утечек — вечный `ServerFull`; свидетеля у дефекта нет.
4. **F-6** — документация приписывает сертификацию трём независимым аудиторам, чьих отчётов в репозитории не существует, а процитированный отчёт является раскрытой самопроверкой и сам ограничивает свой вердикт условием P0/P1 = 0.

Плюс структурная готовность шва: **F-11** (раннер жёстко зашит на `LocalReplicationLoopback` в 5 местах) и **F-10** (`ServerTransportHost.Pump` не подключён ни к одному продуктовому циклу) означают, что «соединение Phase 2 с Phase 3» ещё предстоит построить, а не расконсервировать.

### Условия перехода

**Можно начинать Phase 4 немедленно**, если в первый же спринт включить:
1. Починить F-1 (сценарий до `Accepted` + живой рекордер + assert внутри цикла) — иначе деградация аллокаций на реконнекте пройдёт незамеченной на протяжении всей Phase 4.
2. Починить F-3 (поднять `Default*Sink` до 4096, убрать локальный воркаут, разделить `BufferTooSmall` и `Malformed`) — до написания первого сетевого клиента на 2-аргументном конструкторе.
3. Поднять приоритет N-04 выше N-02/N-03 и закрыть до выхода в сеть — это единственный дефект с путём к полному отказу сервиса.
4. Исправить атрибуцию сертификации в 13 строках 9 документов (F-6) — иначе gate-решения Phase 4 будут опираться на несуществующее независимое подтверждение.

**Не требует действий перед Phase 4, но требует записи в `DECISIONS.md`:** F-7 (renderer feature сознательно не установлен; RenderGraph-путь оверлеев мёртв в билде, работает command-buffer fallback), F-8 (0.623 ms/frame — диагностика, не граница; замер headless без GPU), F-14/F-15 (слепые зоны чексуммы и независимые механизмы порядка — латентны, пока нет передачи владения и удаления юнитов), F-9 (устаревшие комментарии о пейсинге), F-10/F-11 (шов предстоит построить).

### Отдельно: расхождения брифа с реальностью

Бриф содержит 9 фактических ошибок. Фиксирую, чтобы следующий раунд не повторял:

| Утверждение брифа | Реальность |
|---|---|
| `MinimapRadarModel.RefreshIfDue` | Метода не существует; это `Advance(double nowSeconds)` (`MinimapRadarModel.cs:262`), throttled до 30 Гц |
| `FormationMath` | Типа не существует; это `FormationLayout` (`Core/FormationLayout.cs`) |
| «симуляция использует детерминированный засеянный генератор» | **Ложно.** RNG в симуляции нет вообще (0 hits на `DeterministicRandom|Lcg|Xorshift|SplitMix|…`); детерминизм структурный |
| «12 архитектурных гейтов» | 12 `[Test]`-методов, но **6 гейтов + 6 anti-vacuity контролей** |
| «до 117 КБ на 3000 сущностей», «слайсы 8-16 КБ» | Слайс — ровно **16384 Б** (`DefaultMaxSlicePayloadBytes`); потолок юнитов **1024** (`MaxUnitsPerMatch`), поэтому 3000 сущностей недостижимы; «8-16 КБ» и «117 КБ» живут только в doc-комментариях, а 117 КБ = 3000 × **39** Б — размер записи **v1**, заменённого на 40 Б в OD-29 |
| «пейсинг 10/5 Гц» | Константы 5 Гц в Runtime **нет** (8 вхождений — только в комментариях `UnitViewTickBuffer.cs`, как деградация, которую буфер должен терпеть). Серверный пейсинг — byte-token (2048 Б/тик, burst 16384 Б), не герцовый. 10 Гц реально существует, но это каденс **клиентского ack** (`ReplicationFeedbackGenerator.DefaultCadenceHz`) |
| «`MaxFragmentGroups = 4` в `LiteNetLibCarrier.cs` и `OwnDatagramCarrier.cs`» (могут различаться) | Значение 4 верно, но это **одна общая** константа `TransportProtocol.cs:166`; ни один носитель не объявляет своей, различаться они не могут |
| «инспектируй сцену `GlobalFront_Root` / `SampleScene`» | Сцены `GlobalFront_Root` **не существует**; единственная сцена — `Assets/Scenes/SampleScene.unity`, и в ней нет ни одного компонента GlobalFront |
| N-02 «аллоцирует `2+N` объектов» | Реально **`3+N`** (`Group` + `byte[N][]` + N срезов + итоговое сообщение) |

Три утверждения брифа подтвердились **точно**: guard на `ServerTransportHost.cs:333-356`, `baseTick = _tick - (ulong)scheduled` под `_gate` (`TickDriver.cs:209`) с залоченным `Tick` (`:64-74`), и маскирование `MoveTarget → (0,0)` при `HasMoveTarget == false` с обеих сторон.

---

*Все ссылки на строки прочитаны из исходников на коммите `efbf547`. Ни один исходник не был модифицирован этим аудитом. Живые измерения выполнены через Unity MCP `execute_code` в открытом редакторе; авторитетные счетчики тестов — из NUnit XML, скопированных в `Artifacts/TestResults/grandaudit-{editmode,playmode}-efbf547.xml` немедленно после каждого прогона.*
