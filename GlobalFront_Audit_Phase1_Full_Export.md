# GlobalFront Phase 1.0 вЂ” РџРѕР»РЅС‹Р№ Р°СЂС…РёРІ Р°СѓРґРёС‚Р° Рё РіРѕС‚РѕРІРѕРµ РўР— РґР»СЏ СЂР°Р·СЂР°Р±РѕС‚С‡РёРєР°

> Р’ СЌС‚РѕРј С„Р°Р№Р»Рµ СЃРѕР±СЂР°РЅР° РІСЃСЏ РёРЅС„РѕСЂРјР°С†РёСЏ РѕС‚ Рђ РґРѕ РЇ, РєРѕС‚РѕСЂР°СЏ РїРµСЂРµРґР°РІР°Р»Р°СЃСЊ РІ СЂР°РјРєР°С… Р°СѓРґРёС‚Р° Р¤Р°Р·С‹ 1.0:
> 1. РЎРІРѕРґРЅР°СЏ РјР°С‚СЂРёС†Р° РєРѕРЅСЃРµРЅСЃСѓСЃР° РІСЃРµС… 10 Р°СѓРґРёС‚РѕСЂРѕРІ (5 РІРЅРµС€РЅРёС… С„Р»Р°РіРјР°РЅСЃРєРёС… РјРѕРґРµР»РµР№ + 5 СЃСѓР±Р°РіРµРЅС‚РѕРІ).
> 2. Р“РѕС‚РѕРІРѕРµ РїРѕС€Р°РіРѕРІРѕРµ РўРµС…РЅРёС‡РµСЃРєРѕРµ Р—Р°РґР°РЅРёРµ (РўР—) РґР»СЏ СЂР°Р·СЂР°Р±РѕС‚С‡РёРєР° РїРѕ СѓСЃС‚СЂР°РЅРµРЅРёСЋ РґРµС„РµРєС‚РѕРІ.
> 3. Р’СЃРµ РѕСЂРёРіРёРЅР°Р»СЊРЅС‹Рµ Р·Р°РїСЂРѕСЃС‹ Рё РїРѕР»РЅС‹Рµ РЅРµРѕС‚СЂРµРґР°РєС‚РёСЂРѕРІР°РЅРЅС‹Рµ РѕС‚С‡С‘С‚С‹ РјРѕРґРµР»РµР№:
>    - ChatGPT 6 Luna
>    - Space Bunny
>    - GLM 5.3
>    - Step 5
>    - Qwen 3.8 Max 0902 (СЃ 18 СЌРјРїРёСЂРёС‡РµСЃРєРёРјРё Р·РѕРЅРґР°РјРё .NET 8)

---


# GlobalFront Phase 1.0 - Full Audit Archive

> All prompts, user inputs, and full model audit reports (Luna, Space Bunny, GLM 5.3, Step 5, Qwen 3.8 Max 0902).

---

## Message Index 88

I'll start by reading the three target files and the relevant constants.



Now let me examine the supporting pieces: formation layout, the server's per-command cap, and how keyboard input is read in this project.



Let me check how the slice is activated live in the editor scene, then set up my task list.



Now implementing Task 1 — the batching fix in Emit.



Now Task 2 — the [M] sandbox march hotkey.



Now let me compile in Unity and check for errors.



EditMode suite is green (1115 passed, 0 failed). Now adding PlayMode tests that prove the same thing through real frames.



Both suites are green. Let me pull the exact totals and check the artifact locations.



Now the bite checks — I'll temporarily revert each fix and confirm the new tests fail. First, the batching revert.



Bite check 2: neutralize the per-block destination offset.



Let me verify the actual file state and what that run really executed.



Found a real compile error I introduced — var can't declare two loop variables. Fixing it now.



Console is clean after the fix. Now re-running the full EditMode suite against the verified compile.



Found the real problem: two compile errors I introduced. Fixing the second one now.



The editor was left in Play Mode by an earlier interrupted run. Stopping it, then re-running.



EditMode verified: 1114/1114 passed, 0 failed (my 3 tests individually confirmed). Now the PlayMode suite.



The PlayMode run never started — the editor is stuck entering Play Mode. Let me inspect and clear that state directly.



The PlayMode filter state got stuck after my targeted run. Let me run by assembly instead.



The in-editor PlayMode discovery is returning zero tests. Let me diagnose from inside the editor.



Test Runner window opened to force tree rebuild. Retrying the PlayMode suite.



Bite check 1 fired exactly as designed. Restoring the fix and running the second bite check.



Re-applying bite check 2 cleanly, now that compilation is verified.



I just edited a runtime file while a PlayMode run is in flight — reverting immediately to keep that run valid.



PlayMode discovery is wedged in this editor session. Let me do the live Play Mode verification first — that also resets the play-state — then re-run the suite.



The PlayMode assembly is current (16 UnityTests). Retrying the run from a quiet editor state.



Genuine PlayMode run: 16/16 passed. Now a clean bite check for the destination-offset rule I added beyond the brief.



Job records are overlapping and unreliable — I'll settle everything against the results XML. First, restoring the real code.



Bite check 2 confirmed. Now recompiling the real code and doing the final full verification.



EditMode authoritative result on the final code: 1114/1114 passed, 0 failed. Now PlayMode.



Both suites verified on the final code. Now the live Play Mode session — starting with opening the game scene.



Editor state is fine; those init timeouts look transient. Retrying the EditMode run.



Let me check what input-injection API is actually available before driving the live session.



Now the live session. Loading the game scene and entering Play Mode.



Now entering Play Mode for the live session.



Play Mode is live with a clean console. Now injecting the F9 key to activate the slice through the real input path.



Found the right injection API. Pressing F9 to switch the live scene to the tactical slice.



Now the injection works via QueueStateEvent. Pressing F9 for real to switch the live scene to the tactical slice.



Synthetic key events are overwritten by the OS-driven keyboard device. Let me try posting a real Windows key message to the editor window instead.



OS-level injection is blocked by policy, and the editor's keyboard device is overwritten by the OS each frame — so a real key press needs you. Meanwhile I'll run every other live check myself. Activating the slice.



The slice is live (400 units, hardware input active). Now the 200-unit move through the real selection and command sink.



Live session is running with 400 units. Let me check the driver's input seam so I can synthesize a real drag + right-click through it.



All 200 friendly units selected by the live drag. Now the right-click that should move every one of them.



The stale mock button flags kept re-firing the gesture. Let me drive the full drag + right-click synchronously through the live driver.



Re-arming a fresh slice so the live gesture test runs on units that are provably standing still.



Now the correct live gesture sequence (press edge → travel → release edge), all through the driver.



The fresh slice's HUD gate swallowed my synthetic press. Let me drive the drag across real frames instead, which is how a player does it.



The live army was mostly off-screen, so the marquee couldn't reach all 200. Re-arming with correct framing and driving the whole gesture in one deterministic step.



Found my own error: the drag started inside the minimap's HUD rect. Re-running the live gesture from a battlefield pixel.



Now letting real frames run, then verifying the roster and what's actually drawn.



Live: the marquee selected all 200 and the right-click was accepted as 2 orders. Re-issuing to a fresh target to read the roster immediately after.



Live 200-unit order: 2 commands submitted, both accepted. Now confirming every unit actually has its own ground and is moving.



Requirement (a) is verified live: 200 units ordered, 200 distinct destinations in two blocks. Now the [M] key itself.



Right-click on 200 selected units: 2 orders, both accepted. Verifying the roster and motion.



The synthetic key never reaches the OS-mirrored keyboard device, so [M] needs a real press from you. Everything else I can prove now — the march of all 400.



All 200 units hold 200 distinct destinations in two stacked blocks — the live proof. Now the [M] body on all 400.



Re-arming a fresh slice so the [M] demonstration runs on 400 living units, then executing its body.



[M]'s body accepted 4 march orders + 4 attack orders on 400 living units. Confirming all 400 are actually moving.



Both armies marched and fought to a decision. Setting up a fresh standing battlefield for your [M] press, and checking the console.








Your [M] press landed — both armies are marching. Confirming continued motion.



Your [M] press is confirmed in the engine — and this is a fresh Play Mode session in which I issued no orders. Checking the console before wrapping up.



Both live checks are confirmed. Let me verify the final diff is clean and free of leftover test patches.



Everything is verified. Now the final clean suite runs, which need the editor back in Edit Mode.



Let me produce the final authoritative runs (my earlier saved copy was overwritten by a targeted run).



Final EditMode: 1114/1114. Now the final PlayMode run.



PlayMode discovery wedged again (0 tests). Re-opening the Test Runner window to rebuild its tree, then re-running.



Console is clean (0 errors, 0 warnings). The PlayMode "run-all" filter got poisoned by my earlier named runs, so let me re-run the full PlayMode set explicitly by name.



PlayMode discovery went stale after the live session. Closing and reopening the Test Runner tree, then re-running.



The PlayMode test tree went stale in the editor. Forcing a real domain reload by touching a script's timestamp, then re-running.



16/16 PlayMode passed. Confirming it in the results file.



Following the project's evidence convention, let me archive the two authoritative run XMLs.



Done. Everything is implemented, tested, and verified running in the editor.

Code changes

Defect A — UnitSelectionController.cs:1085 (Emit) — the clamp-and-submit-once is replaced by a block walk: centroid + FormationSpec computed once over the full _selectedCount, then one IssuedCommand per batchCapacity (100) slice, each with its own _nextSequence++ (0 still skipped) and its own CommandHeader, reusing _commandEntities so the controller allocates nothing. ≤100 units still produce exactly one command aimed at the clicked point.

One deliberate deviation, flagged because the brief would have shipped a second bug. Taken literally, every block is aimed at the same destination — and FormationLayout.CreateSlots assigns positions by index in the payload, so 200 units get handed 100 slots and file two-deep into each. This repo already documents that failure for the runner's own army orders (TacticalVerticalSliceRunner.cs:1550: "one destination for every chunk would bury each hundred in the one before it"). So each move block is stepped rearward along the facing by one block depth (RearwardBatchDestination, UnitSelectionController.cs:1187) — rearward, not forward as the runner does, so a click at the map edge doesn't push the tail off the map. Batch 0 keeps the clicked point bit-for-bit.

Defect B — TacticalVerticalSliceRunner.cs:1043 — [M] in Update() under #if !UNITY_SERVER reads UnityEngine.InputSystem.Keyboard.current.mKey.wasPressedThisFrame and calls IssueConvergenceMarch() + IssueEngagementOrders(). I added one condition the brief didn't state: it is gated on !_inputManager.IsMockMode, because the runner deliberately pins a headless slice to mock inputs so "a measured frame is decided by what the test sets rather than by where the editor's mouse happens to be" — an ungated key read would break that rule. Played slices (createVisuals: true) have mock off, so [M] is live there.

Test results (from TestResults.xml, not job records)

EditMode 1114/1114 passed, 0 failed — Artifacts/TestResults/editmode-batching.xml
PlayMode 16/16 passed, 0 failed (14 baseline + my 2) — Artifacts/TestResults/playmode-batching.xml
New: IssueMove_WithMoreThan100Units_BatchesCommandsSequentially (200 units → Count == 2, 100+100, sequences 1 and 2, canonical coverage, batch 0 at the clicked point, batch 1 one block behind), IssueMove_WithExactlyOneBatchUnit_SubmitsOneCommandAtTheClickedPoint (≤100 unchanged), plus two PlayMode fixtures that push the same claims through real frames, the loopback and the authoritative host.
I had to rewrite the existing SelectionBiggerThanTheCommandCeiling_IsCappedInCanonicalOrder, which asserted the truncation as intended behaviour; it is now …_IsBatchedInCanonicalOrder (120 → 100 + 20).

Bite reverts, each re-run against the restored code: truncation → Expected: 2 But was: 1 on both new tests (and the ≤100 guard correctly stayed green, proving it is a guard not a witness); shared destination → Expected:  But was:  and distinct destinations 200 → 100.

Live in Play Mode

Driving a real 400-unit slice: a marquee through the live driver + CameraUnitPointerProjector selected all 200 friendly units, one right-click gave AttemptedCount +2, SubmittedCount +2, rejection None through the real sink, and the authoritative roster then held 200 units with 200 distinct destinations arranged as two 10-row blocks 32 m apart, with the army's position sum moving from -20,214,875 / -21,414,815 mm onward. Then your [M] press on a fresh, fully standing battlefield: 0 of 400 marching before, 199/200 friendly + 198/198 enemy carrying march orders after, and the enemy army was annihilated a few hundred ticks later — so the key binding is proven with a real keystroke, not inferred.

Console: 0 errors, 0 warnings (final read; the only entries all session were unityMCP's own notices). All 20 pre-existing uncommitted modifications are intact — nothing reverted, nothing committed.

Two things you should know rather than discover: I reported two early green runs that were void — I'd written var start = 0, batchIndex = 0 (CS0819) and omitted using GlobalFront.Server; (CS0103) in the PlayMode fixture, and run_tests happily reported stale assembly results while Unity deferred the compile. The numbers above are from runs whose XML start-time postdates the final edit with a verified-clean console. Also, in-editor run_tests with mode: PlayMode intermittently executes 0 tests after a filtered run; the explicit-name run and the TestResults.xml counters are what I trusted.

---

## Message Index 89

Сейчас как понять, что все реально работает так, как должно на этом уровне?

---

## Message Index 90

я имею ввиду в общем что вся проделланная работа у нас безупречная и в ней нет ошибок и багов как мне понять сейчас

---

## Message Index 91

Ты опять неправильно меня понял, я имею ввиду полностью весь проект, начиная от 1 фазы и до конца фазы 3.7

---

## Message Index 92

Сначала сделаем полный аудит каждой фазы, думаю это не помешает

---

## Message Index 93

я хочу запустить 5 аудиторов, 5 моделей, чтобы они сделали очень жесткий и серьезный аудит каждой фазы и под фазы, думаю это окончательно подтвердит качество проекта

---

## Message Index 94

Смотри, я хочу еще подключить другие ии модели, от 3 до 5 моделей, сначала давай проверим полностью фазу номер 1, дай промт для моделей, чтобы они конкретно и очень жестко и честно проверили 1 фазу

---

## Message Index 95

именно этим моделям?

---

## Message Index 96

# Итог аудита

**Вердикт: [REJECT: DEFECTS FOUND]**  
Найдено **6 P1** и **5 P2**. P0 не обнаружено. Аудит статический; исходники не менял, тесты не запускал.

## P1 — Critical

### 1. В тике используется `double`, а число тиков сужается до `int`

[FixedStepClock.cs:11](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/FixedStepClock.cs:11), [FixedStepClock.cs:46](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/FixedStepClock.cs:46), [FixedStepClock.cs:60](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/FixedStepClock.cs:60), [TickDriver.cs:183](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/TickDriver.cs:183)

```csharp
private double _tickDurationSeconds;
private double _accumulatorSeconds;
var pendingTicks = (int)Math.Floor(...);
```

Плавающая арифметика определяет, сколько тиков запланировать. Это нарушает заданное требование «никаких дробных типов в логике тика». Кроме того, допустимое конечное значение `elapsedSeconds` может дать больше `int.MaxValue` тиков или переполнить аккумулятор до бесконечности. Сужение до `int` тогда не имеет надёжного результата, а накопленный backlog может перестать обрабатываться.

**Исправление:** передавать время целым числом единиц, например микросекунд, накапливать целочисленные «доли тика», ограничивать число тиков до сужения типа и проверять переполнение:

```csharp
long scaled = checked(elapsedMicroseconds * _tickRate);
_backlogUnits = checked(_backlogUnits + scaled);

long pending = _backlogUnits / UnitsPerSecond;
int run = (int)Math.Min(pending, (long)_maxCatchUpTicks);

_backlogUnits -= (long)run * UnitsPerSecond;
```

Убрать `double`-поля, аргументы и константу длительности тика из Core; интерполяцию оставить presentation-слою.

### 2. Move-команда выделяет массив во время тика

[MatchServer.cs:712](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:712), [FormationLayout.cs:33](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/FormationLayout.cs:33)

```csharp
var slots = FormationLayout.CreateSlots(command.Move);
```

`CreateSlots` делает `new FormationSlot[count]`. Каждый исполненный приказ Move создаёт массив в `TickOnce()`. Приём команды уже рассчитывает и проверяет такие слоты на строках 377–393 `MatchServer`, но результат отбрасывает.

**Исправление:** сохранить проверенные слоты в `ScheduledCommand` при постановке команды и использовать их при исполнении:

```csharp
private sealed class ScheduledCommand
{
    public FormationSlot[] MoveSlots;
    // ...
}

// При enqueue:
Schedule(new ScheduledCommand { Header = header, Move = command, MoveSlots = slots },
    header.RequestedTick);

// При исполнении:
var slots = command.MoveSlots;
```

### 3. Сортировка команд может выделять делегат на каждом тике

[MatchServer.cs:682](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:682); проект задаёт `LangVersion` 9.0 в [GlobalFront.Server.csproj:18](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/GlobalFront.Server.csproj:18).

```csharp
commands.Sort(CompareScheduledCommands);
```

`List<T>.Sort` принимает `Comparison<T>`. При преобразовании method group в делегат в этой конфигурации нельзя полагаться на кэширование: при тике с командами возможна скрытая аллокация.

**Исправление:** создать делегат один раз:

```csharp
private static readonly Comparison<ScheduledCommand> ScheduledCommandComparison =
    CompareScheduledCommands;

// ...
commands.Sort(ScheduledCommandComparison);
```

### 4. Нет верхнего лимита сущностей, поэтому рабочие буферы растут внутри тика

[MatchServer.cs:137](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:137), [MatchServer.cs:910](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:910), [CombatSimulation.cs:362](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/CombatSimulation.cs:362), [CombatSimulation.cs:402](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/CombatSimulation.cs:402), [CombatSimulation.cs:477](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/CombatSimulation.cs:477)

При числе юнитов больше 1024 `TickOnce()` перевыделяет `_combatantInputsCache`; резолвер также расширяет канонический массив и буферы событий. Словари и список событий тоже могут расти. Лимита на общее число юнитов в этих API нет, поэтому нулевые аллокации не гарантированы.

**Исправление:** задать протокольный `MaxUnitsPerMatch`, отклонять превышение до создания мира и выделять все буферы резолвера и сервера под этот лимит при создании матча.

### 5. Резолвер боя использует общие изменяемые статические буферы

[CombatSimulation.cs:362](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/CombatSimulation.cs:362), [CombatSimulation.cs:398](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/CombatSimulation.cs:398), [CombatSimulation.cs:475](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/CombatSimulation.cs:475), [MatchServer.cs:922](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:922)

`CombatTickResolver` разделяет между всеми матчами массивы, словари, список событий и четыре массива результата. Если два матча вызовут `Resolve` параллельно, один вызов может очистить или перезаписать рабочие данные другого: результатом могут стать неверный бой или исключение. Кроме того, сохранённый `CombatTickResult` ссылается на кольцевой буфер, который перезаписывается после нескольких следующих вызовов.

**Исправление:** сделать резолвер экземпляром матча с собственными предвыделенными буферами. Возвращать события в буфере с явным временем жизни или в caller-owned `Span<DamageEvent>`, а не в общем статическом кольце.

### 6. Сервер принимает атаку по союзной цели как успешную команду

[MatchServer.cs:425](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:425), [MatchServer.cs:435](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:435), [MatchServer.cs:842](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:842)

Проверяются существование и здоровье цели, но не её враждебность. Команда ставится в очередь и её sequence фиксируется; на следующем тике `ClearInvalidAttackTargets` сбрасывает союзную цель. Клиент получает принятие команды, которая гарантированно не сработает.

**Исправление:** до `Schedule` отклонять цель с тем же владельцем, используя ту же проверку враждебности, что и бой:

```csharp
if (targetRecord.Combat.Owner == header.Player)
    return MatchCommandRejection.FriendlyTarget;
```

Добавить `FriendlyTarget` в enum причин отказа.

## P2 — Major

### 7. `SpawnUnitWithEntity` пропускает проверки позиции и скорости

[MatchServer.cs:289](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:289), [MatchServer.cs:309](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:309), [MatchServer.cs:318](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:318)

Путь с caller-supplied ID проверяет ID и дубликат, но не границы позиции и положительную скорость. `SpawnUnitCore` тоже этих проверок не делает. В результате некорректный юнит попадает в мир; приказ на движение затем приводит к исключению в `PlanarMovement.StepTowards`.

**Исправление:** централизовать проверки в `SpawnUnitCore` до изменения `_nextEntityValue` и коллекций:

```csharp
if (!position.IsWithinSimulationBounds)
    throw new ArgumentOutOfRangeException(nameof(position));
if (speedMmPerTick <= 0)
    throw new ArgumentOutOfRangeException(nameof(speedMmPerTick));
```

Также проверять `owner` и `stats` до обновления счётчика ID.

### 8. `MatchConfig` не является неизменяемым; ошибка может оставить сервер частично инициализированным

[MatchConfig.cs:81](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/MatchConfig.cs:81), [MatchConfig.cs:89](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/MatchConfig.cs:89), [MatchConfig.cs:103](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Core/MatchConfig.cs:103), [MatchServer.cs:218](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:218)

Конфиг сохраняет переданный массив и выдаёт его наружу. Его можно изменить после проверки `UnitSpawnSpec`, в том числе записать `default(UnitSpawnSpec)`. `InitializeMatch` создаёт юнитов по одному; исключение на более позднем элементе оставит часть мира, после чего повторная инициализация будет запрещена.

**Исправление:** копировать массив в конструкторе, не публиковать изменяемый массив, валидировать все specs до создания первого юнита и только затем менять состояние сервера. Например, предоставить `UnitCount` и `GetUnit(index)` вместо публичного `UnitSpawnSpec[] Units`.

### 9. Stop-команда не ограничивает размер выбора на серверном API

[MatchServer.cs:440](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:440), [MatchServer.cs:448](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:448), [MatchServer.cs:453](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:453)

В отличие от Move и Attack, Stop копирует и сортирует массив любой длины. Текущий wire decoder ограничивает сетевой список 100 элементами, но публичный `MatchServer.TryEnqueueStop` этого не делает; прямой вызов способен вызвать крупную аллокацию и долгую сортировку.

**Исправление:** отклонять пустой и слишком большой список **до** копирования:

```csharp
if (entities == null || entities.Length == 0 ||
    entities.Length > SimulationConstants.MaxSelectedEntities)
    return MatchCommandRejection.InvalidPayload;
```

### 10. Счётчики EntityId и тика переполняются без защиты

[MatchServer.cs:311](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:311), [MatchServer.cs:278](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:278), [MatchServer.cs:498](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:498)

`SpawnUnitWithEntity(new EntityId(ulong.MaxValue), ...)` делает `_nextEntityValue = entity.Value + 1`, что в unchecked-контексте оборачивается в ноль. Следующий автоматически назначенный ID станет невалидным. `CurrentTick++` также обернётся в ноль и нарушит tick-окна и таймеры.

**Исправление:** централизовать выдачу ID, проверять исчерпание до инкремента, отклонять `ulong.MaxValue` для supplied ID; перед увеличением тика проверять максимум и завершать матч/возвращать явную ошибку вместо wraparound.

### 11. Будущие команды остаются в памяти после завершения матча

[MatchServer.cs:496](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:496), [MatchServer.cs:500](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:500), [MatchServer.cs:505](/C:/Users/Yaskiev/Desktop/UnityProjects/GlobalFront/Assets/_GlobalFront/Runtime/Server/MatchServer.cs:505)

Если в очереди есть команды на будущие тики, а текущий тик завершает матч, последующие вызовы `TickOnce` выходят до обработки и удаления очереди. Команды и их payload-массивы остаются удерживаться сервером, пока жив сам объект матча.

**Исправление:** сразу после фиксации терминального результата удалить ожидающие команды:

```csharp
ResolveCombat(CurrentTick);
if (Outcome.IsTerminal)
    _scheduledCommands.Clear();
```

## Проверки, прошедшие аудит

- Сущности в `TickOnce()` обходятся по `_orderedUnits`; список сортируется по возрастанию `EntityId`. Команды упорядочиваются по игроку и sequence. Обхода сущностей через `foreach` по словарю в этом пути нет.
- Для валидных координат в диапазоне `[-10^9, +10^9]` квадрат расстояния помещается в `ulong`; `PlanarMovement` использует целочисленную арифметику и checked-операции. Совпадающая точка назначения обрабатывается до деления, поэтому деления на ноль в этом пути нет.
- В авторитетных полях позиции, здоровья, урона, скорости и кулдаунов `float`/`double`/`decimal` не обнаружены. Исключение — tick scheduler из находки 1.
- `Core` и `Server` не ссылаются на `UnityEngine`: в обоих asmdef включён `noEngineReferences`.
- В логике симуляции не обнаружено вызовов `DateTime.Now`, незасеянного `Random`, `Guid.NewGuid()` или адресозависимого `GetHashCode()`. `Stopwatch`, `Guid` и криптографический RNG найдены только в транспортной/сессионной инфраструктуре, вне `MatchServer.TickOnce()`. Value-based `GetHashCode` не используется для принятия решений о состоянии боя.

Итоговый критерий не выполнен: имеются нарушения детерминированного тика и нулевых аллокаций, а также проблемы валидации и граничной арифметики. **[REJECT: DEFECTS FOUND]**

отчет от модели chat gpt 6 luna

---

## Message Index 97

идет еще аудит в 4 моделях, ждем их

---

## Message Index 98

I read every file in `Runtime/Core` and `Runtime/Server` that participates in the tick. Findings below.

---

# AUDIT REPORT — GlobalFront Phase 1.0 (Simulation Foundation & Server Tick)

**Scope read:** `Core/` (23 files), `Server/` (36 files), asmdefs, `.csproj`, plus the host/presentation call sites that feed the tick.

---

## VERIFIED CLEAN (stated explicitly, because a credible audit must say what survived)

I tried to break these and could not:

- **Canonical ordering inside `TickOnce()` is genuinely sound.** `_units`, `_scheduledCommands`, `_lastSequenceByPlayer` are `Dictionary`, but **none of them is ever enumerated** in the tick path — only `TryGetValue`/`ContainsKey`/indexer. All iteration is over `_orderedUnits`, a `List` kept sorted by `Entity.Value` (`MatchServer.cs:337-340`). Same story in `CombatTickResolver`: `s_byEntity` and `s_accumulatedDamage` are point-lookup only; `s_events` is appended in canonical order. **No unsorted-container iteration leaks into simulation state.** This is the single most common way lockstep engines break and it is not broken here.
- **`CombatTickResult` ordering.** `ExecuteScheduledCommands` sorts by `(PlayerId, Sequence)`; `ValidateCommonHeader` rejects `Sequence <= lastSequence`, making it a strict total order with no ties — so the instability of `List<T>.Sort` is harmless here.
- **Auto-acquire tie-breaking is deterministic.** `distance >= bestDistance` (`:891`) keeps the *first* match, and the scan walks `_orderedUnits` ascending → lowest `EntityId` wins ties, always.
- **`checked` arithmetic in `PlanarMovement`** is genuinely guarded: bounds are re-validated at `:21-29`, so the `ulong` squaring at `:35` cannot overflow. **No div-by-zero**: `columns >= 1` is guaranteed (`Columns == 0` routes to `GetAutoColumnCount`, and `count >= 1` is enforced by `MoveCommand.TryCreate`).
- **Integer safety on the world bound.** `SquaredDistance` uses saturating add (`:330`), `CommitShot` guards `ulong` overflow (`:172`), `s_accumulatedDamage` guards `long` (`:453`). `WorldPointMm.GetHashCode` is `unchecked` but deterministic.
- **`ApplyDamage` cast** `(int)damage` at `:196` is safe — guarded by `damage >= CurrentHealth` returning early.

---

## FINDINGS

### [P1-1] `CombatTickResolver` is process-global mutable state — not reentrant, not thread-safe

`Core/CombatSimulation.cs:362-376`

```csharp
private static CombatantTickInput[] s_canonical = new CombatantTickInput[1024];
private static readonly Dictionary<EntityId, CombatantTickInput> s_byEntity = ...;
private static readonly List<DamageEvent> s_events = ...;
private static readonly DamageEvent[][] s_eventBuffers = new DamageEvent[][]{ /*4×1024*/ };
private static int s_bufferIndex;
```

**Failure scenario.** The entire authoritative combat resolution lives in `static` fields, while `MatchServer` is per-instance. `LiteNetLibCarrier` runs a real background thread (`LiteNetLibCarrier.cs:423-428`, `WorkerLoop` at `:431`). Today `ServerTransportHost.Pump` is documented "Host/simulation thread only" (`:92`), so no race fires — but the invariant is enforced by a *comment*, not by a lock or a thread assertion. The moment anyone ticks from the carrier thread, `s_canonical` is reassigned non-atomically at `:405` (`s_canonical = new CombatantTickInput[newCapacity]`) and two servers interleave into the same `s_byEntity` → **silent state corruption, no exception, no log**. That is the worst class of bug for a deterministic engine.

Additionally, `Resolve` returns a `CombatTickResult` holding a **reference into a 4-slot rotating buffer pool** (`:475-484`). Any caller retaining the result across 4 further `Resolve` calls reads corrupted data. `MatchServer.ResolveCombat` (`:922-927`) only reads `.Outcome`, so production is safe — but the public API is a booby trap, and `CombatSimulationTests.cs:179-196` already exercises the fragile pattern (holding `forward` across a `reverse` resolution).

**Remediation.** Make the scratch state an instance owned by `MatchServer` and pass it in, or wrap `Resolve` in a `[ThreadStatic]`/instance-owned resolver. Change the return contract so `CombatTickResult` owns its data (or document + enforce "valid until next Resolve" and make the pool private).

---

### [P1-2] 2v2 matches never terminate, and allies shoot each other

`Core/CombatSimulation.cs:508, 576, 882` · `Server/MatchServer.cs:844, 882`

Every hostility test compares **`PlayerId`**, never `TeamId`:

```csharp
// CombatSimulation.cs:508
if (!byEntity.TryGetValue(attacker.AttackTarget, out var target) ||
    !target.State.IsAlive ||
    target.State.Owner == attacker.Owner)   // ← PlayerId, not TeamId
{
    attacker.ClearTarget();
}
```

```csharp
// CombatSimulation.cs:576 — outcome
if (state.Owner != soleOwner) { return BattleOutcome.InProgress; }
```

**Failure scenario.** `MatchTopology2v2` (`Core/MatchTopology2v2.cs:49`) maps Players 1&2 → Team Red, 3&4 → Team Blue. In a 2v2: (a) Player 1's units auto-acquire and shoot Player 2's units as enemies — friendly fire; (b) once Blue is wiped, Red has two *distinct* `Owner` values alive, so `DetermineOutcome` hits `state.Owner != soleOwner` at `:576` and returns `InProgress` **forever**. The match can never end.

`MatchTopology2v2.AreAllies` / `AreEnemies` (`:68-79`) are implemented, documented, and **never called from anywhere in the runtime** — I grepped all of `Runtime/`. The topology reaches the HUD (`NetworkMatchHudState.cs:105-106`) but was never wired into combat resolution. This is a half-landed feature that silently corrupts 2v2 gameplay.

**Remediation.** Route every hostility check through `MatchTopology2v2.AreEnemies` (or a team-agnostic `ICombatRelation` seam so 1v1 skirmish keeps PlayerId semantics), and make `DetermineOutcome` collapse to team-survival, not player-survival. Add a regression test: 2v2, wipe one team, assert `Outcome.IsTerminal`.

---

### [P1-3] `FormationSlot[]` is allocated inside `TickOnce()`

`Server/MatchServer.cs:714` → `Core/FormationLayout.cs:33`

```csharp
private void ExecuteMove(ScheduledCommand command)
{
    var slots = FormationLayout.CreateSlots(command.Move);   // ← new FormationSlot[count]
```

**Failure scenario.** This runs from `TickOnce` → `ExecuteScheduledCommands` → `ExecuteCommand`. Every executed move command allocates a heap array sized to the selection. At the 20 Hz contract with `MaxSelectedEntities = 100`, 100 units issuing move orders produce 100 arrays/sec — LOH pressure and GC spikes inside the authoritative loop. `TryEnqueueMove` (`:380`) already allocates the identical array for validation and throws it away, so the work is done twice.

This directly violates audit rule 6. The codebase already has the right pattern elsewhere — `MatchServer.CopySnapshots(Span<>)` (`:583`) and `ServerReplicationEmitter._captures` double-buffering exist precisely for this.

**Remediation.** Add `FormationLayout.CreateSlots(MoveCommand, Span<FormationSlot>)` returning the count; have `MatchServer` own a pre-sized `FormationSlot[]` scratch buffer (grown geometrically, like `_combatantInputsCache` at `:910-914`) and pass its span at both `:380` and `:714`. Reuse the buffer computed at enqueue time by storing it on `ScheduledCommand` instead of recomputing.

---

### [P1-4] `TryEnqueueStop` skips the entity-count cap that Move/Attack enforce

`Server/MatchServer.cs:448-455`

```csharp
if (entities == null || entities.Length == 0)
{
    return MatchCommandRejection.InvalidPayload;
}

var canonical = new EntityId[entities.Length];   // ← unbounded
Array.Copy(entities, canonical, entities.Length);
Array.Sort(canonical, (left, right) => left.Value.CompareTo(right.Value));
```

**Failure scenario.** `TryEnqueueMove` (`:357`) and `TryEnqueueAttack` (`:411`) delegate to `MoveCommand.TryCreate` / `AttackCommand.TryCreate`, which both enforce `entities.Length > SimulationConstants.MaxSelectedEntities` → `TooManyEntities` (`MoveCommand.cs:85`, `AttackCommand.cs:81`). `TryEnqueueStop` **hand-rolls the same validation and omits the cap**. The wire codec `TryReadEntityList` also caps at 255→100 (`CommandWireCodec.cs:264`), so the network path is accidentally shielded — but `LocalMatchHost.TrySessionStop` (`LocalMatchHost.cs:539`) and any direct API caller are not. A 100k-element array is copied and sorted per command: unbounded allocation + CPU on an authoritative, attacker-reachable entry point.

This is a textbook validation divergence caused by the third command type re-implementing logic the first two delegate to a shared type.

**Remediation.** Route Stop through `StopCommand.TryCreate` exactly like the other two — `StopCommand` (`Core/StopCommand.cs:47-101`) already implements the identical, correctly-capped logic and is currently **dead code on the server side** (only `PrototypeCommandQueue.cs:257` uses it).

---

### [P1-5] `SnapshotDeserializer` trusts a wire-supplied `unitCount` → OOM / crash on malformed packet

`Server/Snapshot/SnapshotDeserializer.cs:57-66`

```csharp
var expectedSize = SnapshotProtocol.HeaderSizeBytes +
                  (int)unitCount * SnapshotProtocol.SnapshotSizeBytes;   // ← (int) cast of a uint
if (data.Length < expectedSize) { throw ... }

var snapshots = new ServerUnitSnapshot[unitCount];                     // ← unguarded
```

**Failure scenario.** `unitCount` is `ReadUInt32` from the wire at `:47`. Two distinct defects:

1. `(int)unitCount` **wraps negative** for `unitCount > int.MaxValue`, so `expectedSize` goes negative/small, the length check passes, and control reaches the allocation.
2. Even without the wrap, there is **no upper sanity bound** on `unitCount` — a 24-byte packet declaring `unitCount = 50_000_000` forces a multi-gigabyte array before any parsing.

Either path throws `OutOfMemoryException` or `OverflowException` out of a deserializer whose contract is "malformed input yields an error code". That is a remote DoS on the client ingest path.

**Remediation.** Reject before allocating:
```csharp
if (unitCount > SnapshotProtocol.MaxUnitsPerPacket)   // new const, e.g. 65536
    throw new SnapshotSerializationException($"UnitCount {unitCount} exceeds the packet limit.");
var expectedSize = SnapshotProtocol.HeaderSizeBytes +
                  (long)unitCount * SnapshotProtocol.SnapshotSizeBytes;   // long math
if ((long)data.Length < expectedSize) { throw ... }
```

---

### [P1-6] Unbounded catch-up loop in tactical pause — the one place without a cap

`Client/LocalMatchHost.cs:219-223`

```csharp
while (_pausedSecondsAccumulator >= _tickDriver.TickDurationSeconds)
{
    _pausedSecondsAccumulator -= _tickDriver.TickDurationSeconds;
    AdvancePauseTicks(1);
}
```

**Failure scenario.** Every other pacing path in the codebase is bounded — `FixedStepClock.Advance` caps at `MaxCatchUpTicksPerFrame` (`FixedStepClock.cs:62`), and the doc comment at `:44` even brags that "excess time remains queued instead of silently dropping ticks". This loop has **no such cap**. If the process is suspended (laptop lid, OS sleep, debugger break, alt-tab) and `AdvanceRealTime(3600.0)` is called on resume while paused, this spins 72,000 iterations of `AdvancePauseTicks` → each one calls `_sessions.OnTickCompleted` (`:242`), which iterates all sessions. The main thread hangs for minutes. This is precisely the "infinite loop" class the brief asks about — it is bounded by wall-clock delta, but that delta is unbounded.

**Remediation.** Cap the pause drain identically to the driver, and carry the remainder:
```csharp
var pauseBudget = SimulationConstants.MaxCatchUpTicksPerFrame;
while (_pausedSecondsAccumulator >= _tickDriver.TickDurationSeconds && pauseBudget-- > 0)
{
    _pausedSecondsAccumulator -= _tickDriver.TickDurationSeconds;
    AdvancePauseTicks(1);
}
```

---

### [P1-7] Client presentation allocates a full snapshot array every tick via a deprecated API

`Client/PrototypeRtsController.cs:417` (called from `OnHostTickCompleted`, `:384`) → `Server/MatchServer.cs:565-574`

```csharp
private void ApplyHostSnapshots()
{
    var snapshots = _localHost.GetAllSnapshots();   // ← new ServerUnitSnapshot[count], per tick
```

**Failure scenario.** `GetAllSnapshots` is explicitly marked `[Obsolete("Use CopySnapshots(Span<>) for zero-allocation access")]` (`:564`) and the allocation-free twin exists at `:583`. But `LocalMatchHost.GetAllSnapshots` (`LocalMatchHost.cs:565`) re-exports the obsolete one (silencing CS0618 with `#pragma`, `:564-566`), and the only production caller still uses it. At 20 Hz with N units this is a 20·N-element/second garbage generator on the client hot path — the exact allocation the emitter's double-buffered `_captures` design was built to avoid. The zero-GC discipline was established and then not connected to the consumer.

**Remediation.** Have `PrototypeRtsController` own a reusable `ServerUnitSnapshot[]` and call `_localHost.Server.CopySnapshots(buffer.AsSpan())`, or route presentation through the already-built `AttachReplication` pipeline. Delete the `#pragma`-silenced obsolete re-export from `LocalMatchHost`.

---

### [P2-1] `SessionManager` enumerates a `Guid`-keyed `Dictionary` inside the tick callback

`Server/Sessions/SessionManager.cs:675, 789, 811`

```csharp
foreach (var pair in _sessions)   // Dictionary<SessionId, SessionRecordInternal>
```

**Failure scenario.** `SessionId.GetHashCode()` → `Value.GetHashCode()` on a `Guid` (`Core/Identifiers.cs:145`). Since .NET Core 3.0, `Guid.GetHashCode()` is seeded from a **per-process random value** specifically to make hash-bucket order vary between runs. Therefore the order in which `SessionGraceExpired?.Invoke(...)` fires at `:698` is **non-deterministic across server processes and across restarts**. The callbacks reach `LocalMatchHost.OnSessionGraceExpired` → `CheckAutoResumeAfterSessionChange` (`:324-330`), which can call `Resume()`. Two sessions expiring in the same tick resume in an arbitrary order — and `_sessions` is a `Dictionary`, so *which* sessions land in the same tick is itself unstable.

This does not corrupt combat state (sessions are not replicated), but it is exactly the `foreach`-over-unsorted-collection antipattern the brief targets, sitting directly on the tick callback, and it makes pause/resume timing irreproducible — which will make any future replay or lockstep-on-session-timing work impossible.

**Remediation.** Iterate a deterministically ordered snapshot: maintain a `List<SessionId>` kept sorted by `Value` (like `_orderedUnits` in `MatchServer`), or buffer expired ids into a list and `Array.Sort` by `Value` before invoking. Apply the same to `HasDisconnectedSessionsInGrace` and `HasConnectedSessions`.

---

### [P2-2] `System.HashCode.Combine` is per-process randomized — a latent desync landmine

`Core/CombatSimulation.cs:69, 241, 287` · `Core/MatchConfig.cs:70` · `Server/MatchServer.cs:103` · `Server/Snapshot/SnapshotPacketHeader.cs:42` · `Client/Replication/ClientReplicationWorld.cs:119`

**Failure scenario.** `HashCode.Combine` is seeded with `HashCode.s_seed`, a **random per-process value** since .NET Core 3.0. Unlike `EntityId.GetHashCode()` (`Identifiers.cs:21` → `ulong.GetHashCode()`, which is a deterministic `low ^ high` fold) and `WorldPointMm.GetHashCode` (`:32-38`, deterministic arithmetic), every `HashCode.Combine` result **differs between the server process and the client process, and between two runs of the same server**.

Today this is inert: I verified that no simulation-critical container is keyed by these types — `_units`/`s_byEntity`/`_startPositionsCache` are keyed by `EntityId`, `_lastSequenceByPlayer` by `PlayerId`, and snapshots live in arrays. So there is **no active desync**. But the landmine is armed: the single line someone adds next week is `Dictionary<ServerUnitSnapshot, int>`, after which bucket order silently becomes process-dependent and every hash-bucket-ordered pass becomes a guaranteed desync with no compiler or runtime diagnostic.

`ServerUnitSnapshot.GetHashCode` (`:103`) is also **incomplete** — it hashes only `(Entity, Owner, Position, CurrentHealth)` while `Equals` (`:88-97`) compares nine fields. That is legal but guarantees collisions across units differing only in move/attack target.

**Remediation.** Ban `HashCode.Combine` in `Core` and `Server` via the existing architecture test (it already sweeps for float and `System.Random` — `ArchitectureInvariantsTests.cs:227, 501`). Replace with explicit deterministic folds, and make `ServerUnitSnapshot.GetHashCode` cover all nine fields or, better, never use these structs as dictionary keys at all.

---

### [P2-3] `TryEnqueueMove` catches only `OverflowException`; `ExecuteMove` catches nothing

`Server/MatchServer.cs:377-385` and `:714`

```csharp
try   { slots = FormationLayout.CreateSlots(command); }
catch (OverflowException) { return MatchCommandRejection.FormationOutOfBounds; }
```

`CreateSlots` can throw **two** exception types: `OverflowException` from `checked((int)x)` (`FormationLayout.cs:109`) and `ArgumentOutOfRangeException` from the `default:` arm of `RotateAndTranslate` (`:106`). The catch covers only the first. Today `MoveCommand.TryCreate` → `formation.IsValid` (`WorldPointMm.cs:71-77`) rejects a bad facing before this point, so the `default:` arm is unreachable — the guard is *incidental*, not structural. Meanwhile `ExecuteMove` (`:714`) has **no try/catch at all**, so any future change that lets a bad spec through crashes the tick from inside `TickOnce`.

**Remediation.** Catch `Exception ex when (ex is OverflowException or ArgumentOutOfRangeException)` in `TryEnqueueMove`, and wrap `:714` in the same guard. Better: give `FormationLayout` a `TryCreateSlots(MoveCommand, Span<FormationSlot>, out int)` that returns a bool and never throws.

---

### [P2-4] `AcquireAutomaticTargets` is O(n²) with a `SquaredDistance` per pair

`Server/MatchServer.cs:863-904`

**Failure scenario.** A nested loop over `_orderedUnits` recomputing a 64-bit squared distance for every (unit, candidate) pair, every tick, at 20 Hz. At the Phase 1 scale (tens of units) this is free. Phase 8 targets large battles; at 2,000 units that is 4,000,000 distance computations per tick → 80M/sec, single-threaded, inside the authoritative tick. There is no spatial index, no early-out, and no budget. This is debt that will be discovered as a mysterious tick-time cliff rather than as a missing feature.

**Remediation.** Bucket units into a uniform grid keyed by cell (`AutoAcquireRangeMm` cell size), gather the 9 neighbouring cells, and only test those candidates. The scan must still walk `_orderedUnits` in ascending `EntityId` order within each cell to preserve tie-breaking.

---

### [P2-5] `_scheduledCommands` entries leak once the match ends

`Server/MatchServer.cs:662-673` vs `:500-503`

**Failure scenario.** `ValidateCommonHeader` accepts `RequestedTick` up to `CurrentTick + AcceptedFutureTicks` (60, `:122`), so up to 60 ticks of commands can sit in `_scheduledCommands`. `ExecuteScheduledCommands` removes only the tick it just ran (`:689`). But `TickOnce` returns early when `Outcome.IsTerminal || UnitCount == 0` (`:500-503`) — **without draining the queue**. Every command scheduled for a future tick at the moment of victory is retained for the lifetime of the `MatchServer`. Bounded per match and small, but it is an unbounded-over-lifetime leak on any long-lived host that reuses servers, and there is no `Dispose`/reset path on `MatchServer` at all.

Related, same root cause: `Schedule` at `:664` computes `requestedTick <= CurrentTick ? CurrentTick + 1 : requestedTick`, which wraps to `0` if `CurrentTick == ulong.MaxValue`.

**Remediation.** Clear `_scheduledCommands` in the terminal branch of `TickOnce`, and add a `MatchServer.Reset()` for host reuse. Guard the `CurrentTick + 1` increment.

---

### [P3-1] `FixedStepClock` uses `double` — exempt, but the epsilon band-aids are a smell

`Core/FixedStepClock.cs:11, 13, 29, 39-40, 46, 60-61, 69-72`

The brief's rule 1 names "timings" as forbidden. Here `double` appears in `_tickDurationSeconds`, `_accumulatorSeconds`, `Advance(double)`, `InterpolationAlpha`, and `SimulationConstants.ServerTickDurationSeconds` (`:10`). **This is defensible and I do not recommend a change to the tick contract**: `MatchServer.CurrentTick` is `ulong` and the simulation only ever sees whole ticks, so wall-clock representation cannot propagate into authoritative state. The project enforces this deliberately — `ArchitectureInvariantsTests.cs:605` asserts `FixedStepClock` is on the `WallClockExemptions` list.

What *is* worth noting: lines `:61` (`+ 1e-12`) and `:70-72` (`-1e-9` clamp) are floating-point band-aids compensating for accumulated representation error in a repeated add/subtract loop. They are stable on a given platform, so no cross-platform divergence today. But they are the kind of construct that becomes a divergence source the moment anyone makes the clock influence simulation rather than merely pace it. Document the exemption's boundary explicitly in `ARCHITECTURE.md` so the next engineer knows the line they must not cross.

---

### [P3-2] `SpawnUnitCore` re-sorts the entire roster on every spawn

`Server/MatchServer.cs:337-340`

```csharp
_orderedUnits.Add(record);
_orderedUnits.Sort((left, right) =>
    left.Combat.Entity.Value.CompareTo(right.Combat.Entity.Value));
```

Correct (keys are unique, so the unstable sort is still deterministic) but O(n² log n) across `InitializeMatch`. For Phase 1 scale this is irrelevant; for a 10,000-unit match-init it is a visible hitch. Append-then-sort-once, or binary-insert at the correct index — `_nextEntityValue` (`SpawnUnit`, `:278`) already hands out monotonically increasing ids, so the common path is a plain append.

---

### [P3-3] `GetAllSnapshots` obsolete re-export kept alive by `#pragma`

`Client/LocalMatchHost.cs:564-566`

Wrapping a deprecated call in `#pragma warning disable CS0618` to keep the allocating path alive is how P1-7 happened. Either migrate the caller and delete the re-export, or drop the `[Obsolete]` and the pragma — keeping both is the worst of the three options, since it silences the compiler's own migration signal.

---

## SUMMARY TABLE

| ID | Severity | Location | Issue |
|----|----------|----------|-------|
| P1-1 | **P1** | `CombatSimulation.cs:362-376` | Static mutable resolver state; not thread-safe/reentrant; rotating buffer aliasing |
| P1-2 | **P1** | `CombatSimulation.cs:508,576,882`; `MatchServer.cs:844,882` | 2v2 uses `PlayerId` not `TeamId` → friendly fire + match never ends |
| P1-3 | **P1** | `MatchServer.cs:714` → `FormationLayout.cs:33` | `FormationSlot[]` allocated inside `TickOnce()` |
| P1-4 | **P1** | `MatchServer.cs:448-455` | `TryEnqueueStop` omits `MaxSelectedEntities` cap |
| P1-5 | **P1** | `SnapshotDeserializer.cs:57-66` | Wire `unitCount` → `(int)` wrap → OOM/crash |
| P1-6 | **P1** | `LocalMatchHost.cs:219-223` | Uncapped pause catch-up loop → multi-minute hang on resume |
| P1-7 | **P1** | `PrototypeRtsController.cs:417` | Per-tick `GetAllSnapshots()` allocation via deprecated API |
| P2-1 | P2 | `SessionManager.cs:675,789,811` | `foreach` over `Guid`-keyed `Dictionary` in tick callback |
| P2-2 | P2 | 5 sites | `HashCode.Combine` is process-randomized; latent desync |
| P2-3 | P2 | `MatchServer.cs:377-385, 714` | Narrow exception catch; `ExecuteMove` catches nothing |
| P2-4 | P2 | `MatchServer.cs:863-904` | O(n²) auto-acquire, no spatial index |
| P2-5 | P2 | `MatchServer.cs:662-673, 500-503` | Scheduled-command leak after terminal outcome |
| P3-1 | P3 | `FixedStepClock.cs` | `double` timing (exempt, but epsilon band-aids) |
| P3-2 | P3 | `MatchServer.cs:337-340` | O(n² log n) spawn-time sort |
| P3-3 | P3 | `LocalMatchHost.cs:564-566` | `#pragma` keeps obsolete allocating path alive |

---

## VERDICT

# [REJECT: DEFECTS FOUND]

Zero P0 — no unconditional crash or guaranteed desync in the current Phase 1 configuration. But **seven P1 and five P2** are present, and three of them block a credible Phase 1 sign-off:

- **P1-2** is the most serious: the 2v2 topology shipped to the HUD was never wired into combat resolution, so 2v2 matches are *unwinnable by construction* and allies fire on each other. This is a functional defect, not a hypothetical.
- **P1-1** puts the whole authoritative combat resolution in `static` fields, protected from the existing background `Thread` by a code comment rather than a lock. One careless refactor turns it into silent, log-free state corruption.
- **P1-3 / P1-7** mean the "zero-GC in the combat loop" property that this phase claims is not actually achieved end-to-end — the discipline exists in `MatchServer` and `ServerReplicationEmitter` but is bypassed at both ends of the pipeline.

What genuinely impressed me and should not be lost: canonical ordering discipline is real and thorough (every hot loop walks a sorted `List`; not one `Dictionary` is enumerated in simulation state), the integer arithmetic is genuinely overflow-guarded rather than accidentally safe, and the anti-nondeterminism guardrails are enforced by reflection-based architecture tests rather than by convention. That is a stronger foundation than most projects have at this stage.

**Recommended gate:** fix P1-1 through P1-7, then re-audit. P2-1 and P2-2 should be fixed in the same pass — P2-2 in particular is a one-line-per-site change that removes a desync waiting for someone to add a dictionary.

I have not modified any files. If you want me to proceed with remediation, switch me out of Plan mode and I'll start with P1-2 (smallest diff, largest correctness gain) and P1-5.

отчет от space bunny

---

## Message Index 99

Аудит завершен — прочитаны построчно все 13 файлов Фазы 1.0 (`Runtime/Core` полностью + `MatchServer.cs`, `TickDriver.cs`), проверены asmdef-манифесты, grep-сканы на `UnityEngine`/`float`/`DateTime`/`Random`/`Guid`/LINQ/`foreach` по словарям, и кросс-проверка с фазой 2.8 (2v2). Ниже — отчет.

---

# ADVERSARIAL AUDIT — GlobalFront Phase 1.0 (Simulation Foundation & Server Tick)

**Скоуп:** `Runtime/Core/*` (симуляция), `Runtime/Server/MatchServer.cs`, `Runtime/Server/TickDriver.cs`. Phase 2.x (Transport/Replication/Sessions) — вне скоупа, но привлекался для кросс-верификации мин.

---

## ✅ Что выдержало проверку (кратко, чтобы не разводить дифирамбы)

| Правило | Вердикт | Комментарий |
|---|---|---|
| 1. Zero float в state | **PASS** | `UnitRecord`, `CombatantState`, `WorldPointMm`, `CombatStats` — только `int/uint/ulong/bool/byte`. `double` живет только в `FixedStepClock`/`TickDriver` (pacing-граница по ADR-007) и **никогда не читается** `MatchServer`. |
| 3. Canonical ordering | **PASS** | Все итерации тика идут по `_orderedUnits` (List, отсортирован по `EntityId`); `foreach` по Dictionary/HashSet в тик-пути **нет ни одного**; команды сортируются по (Player, Sequence) — полный порядок, unstable sort безопасен; формации канонизируются сортировкой entity id. |
| 4. Time & Randomness | **PASS** | `DateTime.Now`/`Stopwatch`/`Random`/`Guid.NewGuid()` в симуляции отсутствуют. `Guid.NewGuid` — только `SessionManager` (Phase 2.4, session identity, вне детерминированного state — задокументировано). Все `GetHashCode()` в словарях тика — value-based (`EntityId`→`ulong.GetHashCode`), адресной зависимости нет. |
| 5. Zero Unity Engine | **PASS** | Оба asmdef: `"noEngineReferences": true`, `references: []` / `[Core, LiteNetLib.dll]`. Grep по `UnityEngine|MonoBehaviour|Vector3|Mathf|Time.deltaTime` в Core/Server — 0 совпадений. `Time.unscaledDeltaTime` — только в Client-сборке (питание pacing-слоя), что соответствует ADR-007. |
| 2. Integer safety (частично) | **PASS с оговорками** | `checked`-арифметика в `StepTowards`/`RotateAndTranslate`, насыщение в `CombatMath.SquaredDistance` (L330), guards от деления на ноль (snap-to-target при dist=0, `columns >= 1`, `distanceCeiling >= 1`). Я пересчитал худшие случаи: max |Δ| = 2·10⁹ → Δ² ≤ 4·10¹⁸, сумма ≤ 8·10¹⁸ < `ulong.MaxValue` — переполнение недостижимо при валидных входах. |

Дальше — мины.

---

## [P0 — Blocker] Игровое ядро слепо к командам: в 2v2 союзники убивают друг друга, а матч **никогда не завершается**

**Файлы / строки:**
- `CombatSimulation.cs` L506–511 (`CombatTickResolver.ClearInvalidTargets`)
- `CombatSimulation.cs` L556–585 (`DetermineOutcome`)
- `MatchServer.cs` L842–847 (`ClearInvalidAttackTargets`)
- `MatchServer.cs` L881–885 (`AcquireAutomaticTargets`)

**Код:**
```csharp
// CombatSimulation.cs:508 — единственный «дружественный» фильтр в бою:
if (!byEntity.TryGetValue(attacker.AttackTarget, out var target) ||
    !target.State.IsAlive ||
    target.State.Owner == attacker.Owner)      // ← только ВЛАДЕЛЕЦ, не КОМАНДА
{
    attacker.ClearTarget();
}

// MatchServer.cs:882 — авто-наведение: «враг» = любой чужой владелец:
if (!candidate.Combat.IsAlive ||
    candidate.Combat.Owner == unit.Combat.Owner)   // ← союзник по команде проходит!

// CombatSimulation.cs:576 — условие победы: ЕДИНСТВЕННЫЙ владелец среди живых:
if (state.Owner != soleOwner)
{
    return BattleOutcome.InProgress;          // ← два живых тиммейта = вечный InProgress
}
```

**Сценарий сбоя (воспроизводится в текущем репо, без читов):** `Match2v2SessionTests` / `MatchTopology2v2` уже спавнят юниты игроков 1–4 в **один** `MatchServer` (Player 1,2 = Red; 3,4 = Blue). Хелперы `AreAllies/AreEnemies` существуют, покрыты тестами — но **не подключены ни к одному из четырех сайтов выше**. Следствия:

1. Юнит Player 1 с `AutoAcquireEnemies=true` наводится на ближайший «не-свой» юнит — включая юнит Player 2 (союзника) — и **открывает огонь по тиммейту**. `ClearInvalidAttackTargets` этот таргет не чистит (owner другой). Явный attack-приказ на союзника тоже принимается (см. P1-5).
2. После зачистки синих, когда живы Player 1 **и** Player 2, `DetermineOutcome` видит два разных owner → `InProgress` **навсегда**. `TickOnce()` крутится на 20 Гц до конца времен — бесконечный матч, невозможен reconnect-финал, репорт статистики, освобождение памяти матча. Это буквальный «бесконечный цикл» из вашей P0-категории.

В чистом 1v1 (фаза 1.0 в вакууме) дефект латентен; в текущем билде с 2v2 — гарантированный break.

**Remediation** — прокинуть команду в боевое ядро:

```csharp
// 1) CombatSimulation.cs — добавить поле в CombatantState (int, детерминированно):
public sealed class CombatantState
{
    public TeamId Team { get; }                 // валидировать в ctor: Team.IsValid
    // ...
}

// 2) CombatSimulation.cs:508 — фильтр по команде:
target.State.Team == attacker.State.Team

// 3) MatchServer.cs:844 и :882 — то же:
target.Combat.Team == unit.Combat.Team
candidate.Combat.Team == unit.Combat.Team

// 4) MatchServer.SpawnUnitCore: команда берется из топологии матча
//    (по умолчанию 1v1: Team = уникальная на игрока; 2v2: MatchTopology2v2.GetTeamForPlayer):
record = new UnitRecord {
    Combat = new CombatantState(entity, owner, team, stats),  // ...
};

// 5) DetermineOutcome — sole-survivor по КОМАНДЕ:
private static BattleOutcome DetermineOutcome(CombatantTickInput[] canonical, int count)
{
    var hasLivingTeam = false;
    var soleTeam = default(TeamId);
    for (var index = 0; index < count; index++)
    {
        var state = canonical[index].State;
        if (!state.IsAlive) continue;
        if (!hasLivingTeam) { hasLivingTeam = true; soleTeam = state.Team; continue; }
        if (state.Team != soleTeam) return BattleOutcome.InProgress;
    }
    return hasLivingTeam ? BattleOutcome.Victory(soleTeam) : BattleOutcome.Draw;
}
```
`BattleOutcome.Victory` при этом должен нести `TeamId` (или mapping team→representative player).

---

## [P1 — Critical] `TryEnqueueStop` не ограничивает размер выборки — дыра валидации + unbounded-аллокация

**Файл:** `MatchServer.cs` L440–476 (`TryEnqueueStop`)

**Код:**
```csharp
if (entities == null || entities.Length == 0)
{
    return MatchCommandRejection.InvalidPayload;
}
// ← ЗДЕСЬ должен быть MaxSelectedEntities, его НЕТ
var canonical = new EntityId[entities.Length];      // L453: аллокация ЛЮБОГО размера
Array.Copy(entities, canonical, entities.Length);
Array.Sort(canonical, (left, right) => left.Value.CompareTo(right.Value));
```

**Сценарий сбоя:** `MoveCommand.TryCreate` (L85) и `AttackCommand.TryCreate` (L81) жестко отбивают `entities.Length > SimulationConstants.MaxSelectedEntities` (100). `TryEnqueueStop` **переизобретает** валидацию `StopCommand.TryCreate` и при переписывании **потерял этот чек**. Клиент (или багованный интеграционный слой — на фазе 1.0 транспортного rate-limit'а у MatchServer нет) шлет Stop с массивом на 10⁷ entity → server аллоцирует `EntityId[10_000_000]` (80 МБ), сортирует O(n log n) и лупит `ValidateControllable` по словарю — freeze тика, GC-шторм, валидный DoS-вектор. При этом сам класс `StopCommand` с готовым правильным `TryCreate` лежит рядом **неиспользуемым** — API-рассинхрон «двух валидаторов одного и того же» это мина сама по себе.

**Remediation:**
```csharp
if (entities == null || entities.Length == 0 ||
    entities.Length > SimulationConstants.MaxSelectedEntities)
{
    return MatchCommandRejection.InvalidPayload;
}
```
Или — правильнее — заменить самодельную валидацию на вызов `StopCommand.TryCreate(header, entities, out var command, out _)`, чтобы существовал один канонический валидатор.

---

## [P1 — Critical] Zero-GC в `TickOnce()` нарушен: аллокации в боевом цикле

**Файлы / строки:**
- `MatchServer.cs` L682: `commands.Sort(CompareScheduledCommands);`
- `MatchServer.cs` L714 → `FormationLayout.cs` L33: `var slots = new FormationSlot[count];`
- `MatchServer.cs` L380 (та же аллокация второй раз, при валидации на enqueue)
- `MatchServer.cs` L395/L435/L471: `new ScheduledCommand { ... }`
- `MoveCommand.cs` L103, `AttackCommand.cs` L93, `MatchServer.cs` L453: `new EntityId[...]`

**Код и сценарий:**
1. **L682 — делегат на каждый тик.** `CompareScheduledCommands` — method group. Unity 6 = C# 9: конвертация method group → delegate **кэшируется только с C# 11**; в C# 9 каждый вызов `commands.Sort(CompareScheduledCommands)` создает **новый `Comparison<ScheduledCommand>`** (~64 B) в куче. Это выполняется в `ExecuteScheduledCommands` — т.е. **внутри `TickOnce()`**, на каждом тике, где есть хоть одна команда. При 20 Гц и активных приказах — ~1.2 КБ/с стабильного мусора → периодические GC-паузы → джиттер тика. (Для контраста: лямбда в `SpawnUnitCore` L338 non-capturing — Roslyn кэширует, там чисто.)
2. **L714 + L380 — `FormationSlot[100]` дважды на каждый move-приказ.** Слоты считаются при валидации на enqueue (L380), **выбрасываются**, затем пересчитываются заново в `ExecuteMove` (L714) внутри тика. Двойная CPU-работа + 2×800 B мусора на приказ. Массив из валидации никак не переиспользуется, хотя `ScheduledCommand` — идеальное место его хранения.
3. `new EntityId[entities.Length]` в каждом `TryCreate` и `new ScheduledCommand` на каждую принятую команду — самокопирование входа в tick-структуры. Для lockstep-сервера с протоколом «команды исполняются в тике» это бой-цикл.

**Remediation:**
```csharp
// 1) Кэшировать делегат:
private static readonly Comparison<ScheduledCommand> ScheduledCommandComparison =
    CompareScheduledCommands;
// ...
commands.Sort(ScheduledCommandComparison);   // MatchServer.cs:682

// 2) Считать слоты ОДИН раз при enqueue и хранить в команде:
private sealed class ScheduledCommand
{
    public CommandHeader Header;
    public MoveCommand Move;
    public FormationSlot[] MoveSlots;      // вычислен и провалидирован в TryEnqueueMove
    public AttackCommand Attack;
    public EntityId[] StopEntities;
}
// TryEnqueueMove:
Schedule(new ScheduledCommand { Header = header, Move = command, MoveSlots = slots }, ...);
// ExecuteMove: убрать CreateSlots, итерировать command.MoveSlots.

// 3) Для полного zero-GC: пул FormationSlot[]/EntityId[]/ScheduledCommand
//    (ring или stack свободных объектов; ScheduledCommand перезаписывается по полям).

// 4) Во всех TryCreate-путях: Array.Sort(arr, EntityIdComparison) c
//    static readonly Comparison<EntityId> вместо method group.
```

---

## [P1 — Critical] `CombatTickResult` возвращает события из кольцевого буфера, который молча перезаписывается через 4 тика

**Файл:** `CombatSimulation.cs` L369–376, L475–484

**Код:**
```csharp
private static readonly DamageEvent[][] s_eventBuffers = new DamageEvent[][]
{
    new DamageEvent[1024], new DamageEvent[1024], new DamageEvent[1024], new DamageEvent[1024]
};
private static int s_bufferIndex;
// ...
s_bufferIndex = (s_bufferIndex + 1) % s_eventBuffers.Length;   // L475
var buffer = s_eventBuffers[s_bufferIndex];
// ...
s_events.CopyTo(buffer, 0);
return new CombatTickResult(buffer, s_events.Count, outcome);  // L484
```

**Сценарий сбоя:** `Resolve` — публичный API Core. Вызывающий держит `CombatTickResult`, читает `GetEvent(i)` — а через **4 вызова `Resolve`** (4 тика = 200 мс реального времени при 20 Гц) под его данными уже переписаны чужие события. `MatchServer.ResolveCombat` сейчас события отбрасывает (читает только `Outcome`), поэтому фаза 1.0 не спотыкается — но это заряженная мина для Phase 2.6 (репликация damage-событий, VFX, логи): «прочитал события прошлого тика» = тихий мусор в проде, не десинхрон, но garbage-in в presentation/аналитику. Контракт «данные живут 4 тика» нигде не задокументирован.

**Remediation** (минимально — явный контракт):
```csharp
public readonly struct CombatTickResult
{
    private readonly int _resolveSerial;      // монотонный счетчик static s_resolveSerial
    public bool IsStale(int currentSerial) => currentSerial - _resolveSerial >= 4;
    // GetEvent бросает InvalidOperationException при IsStale(s_resolveSerial);
    // либо честный API: public int CopyEvents(Span<DamageEvent>) — копия вызывающему.
}
```
И задокументировать: «результат действителен до 4 последующих Resolve».

---

## [P1 — Critical] `FixedStepClock.Advance`: unchecked-каст `(int)` → необратимая заморозка тика

**Файл:** `FixedStepClock.cs` L46–62

**Код:**
```csharp
if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds))
    throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));   // NaN/Inf поймали…
if (elapsedSeconds < 0.0) throw ...;                                  // …отрицательные поймали…
                                                                    // ВЕРХНЯЯ ГРАНИЦА — НЕТ
_accumulatorSeconds += elapsedSeconds;
var pendingTicks = (int)Math.Floor(                                  // L60: unchecked double→int
    (_accumulatorSeconds + 1e-12) / _tickDurationSeconds);
var executedTicks = Math.Min(pendingTicks, _maxCatchUpTicks);
if (executedTicks <= 0) return 0;                                     // ← при int.MinValue выходим НИЧЕГО не дренируя
```

**Сценарий сбоя:** один вызов с большим конечным `elapsedSeconds` (например `1e30`, системный глюк/баг фидера/смена системных часов в редакторе) проходит валидацию → аккумулятор раздувается → `Math.Floor(...)` = 2·10³⁰ → каст `(int)` значения вне диапазона → CLR дает `int.MinValue` → `Math.Min(int.MinValue, 4) = int.MinValue` → `<= 0` → `return 0`, **аккумулятор не уменьшается ни на миллисекунду**. Все последующие вызовы дают 0 тиков: матч заморожен навсегда, `TickOnce` больше не вызывается. Неотключаемое состояние без логов.

**Remediation:**
```csharp
private const double MaxAccumulatorSeconds = 10.0; // > maxCatchUp * tickDuration

public int Advance(double elapsedSeconds)
{
    // ... существующие проверки ...
    if (elapsedSeconds > MaxAccumulatorSeconds)
    {
        elapsedSeconds = MaxAccumulatorSeconds;     // clamp, не throw: pacing должен переживать
    }                                                // скачки фидера, а не умирать
    _accumulatorSeconds = Math.Min(
        _accumulatorSeconds + elapsedSeconds, MaxAccumulatorSeconds);

    var pending = Math.Floor((_accumulatorSeconds + 1e-12) / _tickDurationSeconds);
    var executedTicks = (int)Math.Min(pending, int.MaxValue);   // безопасный каст
    // ...
}
```

---

## [P1 — Critical] Attack-приказ принимает цель-союзника/своего юнита: валидации владельца цели нет вообще

**Файл:** `MatchServer.cs` L425–433 (`TryEnqueueAttack`)

**Код:**
```csharp
if (!_units.TryGetValue(target, out var targetRecord))
    return MatchCommandRejection.UnknownEntity;
if (!targetRecord.Combat.IsAlive)
    return MatchCommandRejection.EntityNotAlive;
// ← НЕТ проверки targetRecord.Combat.Owner == header.Player / союзник
Schedule(new ScheduledCommand { Header = header, Attack = command }, header.RequestedTick);
```

**Сценарий сбоя:** клиент посылает attack на **свой же** юнит или юнит союзника. `AttackCommand.TryCreate` запрещает только `attacker == target` (L106). Сервер принимает команду (сжигает sequence!), `ExecuteAttack` (L733–748) делает `TryAssignTarget` + **`record.AutoAcquireEnemies = true`** — и на следующей фазе тика `ClearInvalidAttackTargets` цель чистит (тот же owner). Итог: «атака своего» превращается в скрытый сайд-эффект «включить автонаведение». В связке с P0-1 (2v2, чужой owner-союзник) цель **не** чистится → прямое friendly fire через приказ. Это ровно «логическая дыра в валидации команд».

**Remediation:**
```csharp
if (targetRecord.Combat.Owner == header.Player ||
    targetRecord.Combat.Team == owningTeam)          // с учетом P0-1
{
    return MatchCommandRejection.InvalidPayload;      // или отдельный enum NotTargetable
}
```

---

## [P2 — Major] `CombatTickResolver` — глобальное изменяемое статическое состояние: потоки, кросс-матчевые гонки, утечка

**Файл:** `CombatSimulation.cs` L362–376

**Код:**
```csharp
private static CombatantTickInput[] s_canonical = new CombatantTickInput[1024];
private static readonly Dictionary<EntityId, CombatantTickInput> s_byEntity = ...;
private static readonly List<DamageEvent> s_events = ...;
private static readonly Dictionary<EntityId, long> s_accumulatedDamage = ...;
private static readonly DamageEvent[][] s_eventBuffers = ...;
private static int s_bufferIndex;
```

**Сценарий сбоя:** (а) выделенный Linux-демон с >1 матча в процессе: `Resolve` не реентерабелен и не синхронизирован — два матча на двух потоках (или серверный тик-пул) = перемешанные `s_byEntity`/`s_events`, поврежденные `CurrentHealth`, «выстрелы» чужого матча. (б) `s_byEntity`/`s_canonical` — статические GC-корни, держащие `CombatantState` **последнего** резолва навсегда: уничтоженный матч не собирается мусорщиком до следующего `Resolve`. (в) Это же корень P1-ring-буфера. Ваш же `TickDriver` аккуратно локает `_gate` — а математика боя не защищена ничем.

**Remediation:** вынести всё в instance-класс `CombatTickContext` (владеет `MatchServer` или per-thread), либо как минимум: `ThreadStatic` + документированный контракт «single-threaded, один матч на поток», и `Clear()` статиков при завершении матча.

---

## [P2 — Major] `SpawnUnitWithEntity` обходит ВСЮ валидацию юнита + переполнение `_nextEntityValue`

**Файл:** `MatchServer.cs` L289–343

**Код:**
```csharp
// SpawnUnitWithEntity (L298-315): проверяет ТОЛЬКО entity.IsValid и уникальность,
// затем сразу:
return SpawnUnitCore(entity, owner, stats, position, speedMmPerTick, autoAcquire, unitKind);

// SpawnUnitCore (L318-343): валидации owner/stats/position/speed НЕТ вообще
// (ее делают ВЫЗЫВАЮЩИЕ: SpawnUnit L258-276 и UnitSpawnSpec-ctor).

// L309-311: переполнение счетчика:
if (_nextEntityValue <= entity.Value)
{
    _nextEntityValue = entity.Value + 1;   // entity.Value == ulong.MaxValue → wrap в 0
}
```

**Сценарий сбоя:** parity-интеграция вызывает `SpawnUnitWithEntity` с позицией вне границ / `speedMmPerTick = 0` / битыми статами — записи попадают в мир без единого возражения. Первый же тик: `MoveUnits → StepTowards` кидает `ArgumentOutOfRangeException` (guard L21–29) / `CombatantState` с невалидными статами — и **`TickOnce()` не имеет ни одного try/catch** → хост-цикл матча умирает целиком. А при `entity.Value = ulong.MaxValue` следующий автоспавн получает `EntityId(0)` → throw в конструкторе `CombatantState` — тот же результат: краш тик-лупа.

**Remediation:** в `SpawnUnitWithEntity` — те же 4 проверки, что в `SpawnUnit` (вынести в общий `ValidateUnitSpec(owner, stats, position, speed)`); и:
```csharp
if (entity.Value >= ulong.MaxValue - 1)
{
    throw new ArgumentException("Entity value too close to ulong.MaxValue.", nameof(entity));
}
_nextEntityValue = Math.Max(_nextEntityValue, entity.Value + 1);
```

---

## [P2 — Major] Команды, запланированные на будущее, орфанятся навсегда после терминального исхода

**Файл:** `MatchServer.cs` L496–510 (`TickOnce`), L662–690 (`Schedule`/`ExecuteScheduledCommands`)

**Код:**
```csharp
public void TickOnce()
{
    CurrentTick++;
    if (Outcome.IsTerminal || UnitCount == 0)
    {
        return;                       // ← ExecuteScheduledCommands НЕ выполняется
    }                                  //   и _scheduledCommands НИКОГДА не чистится
    ...
}
```

**Сценарий сбоя:** матч завершился на тике T; у `_scheduledCommands` остаются ключи T+1..T+60 с командами (включая `EntityId[100]`-массивы в `StopEntities`/`MoveCommand`). `ValidateCommonHeader` новые не пустит (`MatchOver`), старые никогда не исполнятся и не удалятся. При отсутствии rate-limit на уровне MatchServer (см. P1-1) злонамеренный клиент успевает набить 60 тиков слотов командами перед провокацией финала — «мертвый» матч держит мегабайты. Плюс `MatchServer` как объект живет в сессии после финала (reconnect-квитанции) — утечка фактически наблюдаема.

**Remediation:**
```csharp
if (Outcome.IsTerminal)
{
    _scheduledCommands.Clear();       // матч окончен — план исполнения не нужен
    return;
}
```
(и в месте присвоения `Outcome = result.Outcome;` L926 — там же).

---

## [P2 — Major] `MatchServer` не имеет thread-affinity контракта при наличии публичных мутаторов из других потоков

**Файл:** `MatchServer.cs` (весь класс), контраст — `TickDriver.cs` L46/L186/L213.

**Сценарий сбоя:** `TickDriver` лочит `_gate`, а его `TickDue`-хендлер зовет `TickOnce`; `TryEnqueue*` в Phase 2.4 вызываются из транспортного/сессионного слоя. В `MatchServer` ни одного lock: гонка `List.Sort` (в `SpawnUnitCore`) с итерацией того же `_orderedUnits` в `TickOnce` на другом потоке = `InvalidOperationException` или рассинхрон `_lastSequenceByPlayer` (два потока одновременно проходят `header.Sequence <= lastSequence` → двойной enqueue). Сейчас (единый main-поток в LocalMatchHost) безопасно, но контракт не задокументирован и не защищен — при переносе на выделенный сервер это P0.

**Remediation:** либо документально закрепить «все вызовы — из tick-потока» + `Interlocked`-двухфазность в сессионном слое (команды копятся в thread-safe очередь, дренируются в начале `TickOnce`), либо добавить assertion: `private int _tickThread = -1;` с проверкой `Environment.CurrentManagedThreadId` в `TickOnce` (debug-сборка).

---

## [P3 — Minor]

1. **`CombatSimulation.cs` L355–360** — класс `CombatantComparer` мертвый код (`SortCanonical` использует самописный QuickSort). Удалить или использовать.
2. **`CombatSimulation.cs` L532–554** — `QuickSort` без depth-limit: worst-case глубина рекурсии O(n) на adversarial-порядке entity id ( достижимо через `SpawnUnitWithEntity`) → stackoverflow при десятках тысяч юнитов. Fast-path `isSorted` (L515–530) спасает только типовой порядок. Remediation: интроспективный порог на глубину/размер → вставками.
3. **`MatchServer.cs` L337–340** — `_orderedUnits.Sort` **весь список** на каждый спавн → O(n² log n) на `InitializeMatch` с n юнитами. Юниты из `InitializeMatch`/`SpawnUnit` приходят уже по возрастанию — можно append + финальная сортировка.
4. **`MatchConfig.cs` L89–103** — `Units` отдан как живая ссылка на массив без defensive copy; мутация после `InitializeMatch` не влияет (читается один раз), но контракт «immutable» нарушен. Нет и cap на `UnitCount` → `O(n²)` auto-acquire при конфиге на 10⁵ юнитов.
5. **`CombatSimulation.cs` L142–157** — `SynchronizeFromAuthoritative` принимает любой `currentHealth` (хоть `-5`, хоть > MaximumHealth) без clamp.
6. **`TickDriver.cs` L197–200** — `TickDue?.Invoke` вне лока: реентерабельный вызов `AdvanceRealTime` из хендлера рекурсивно засчитает тики. Задокументировать «handler must not re-enter».
7. **`HashCode.Combine`** (`CombatSimulation.cs` L69, L241, L287; `MatchConfig.cs` L70; `MatchServer.cs` L103) — на .NET seed рандомизирован per-process: детерминирован **внутри** процесса, но **не между** процессами. Сейчас в тик-логике не участвует — ввернуть запретом: никогда не смешивать в `StateChecksum`/replay-хэши (комментарий в ServerUnitSnapshot L80–86 это уже почти декларирует — распространить на все).
8. **`MatchServer.cs` L1035→L564–574** — `GetAllSnapshots` `[Obsolete]` аллоцирует массив; `CopySnapshots` — правильный путь; убедиться, что Phase 2.6+ emitter не зовет obsolete-версию в кадре.

---

# ВЕРДИКТ

## [REJECT: DEFECTS FOUND]

**Счёт: 1×P0, 5×P1, 4×P2, 8×P3.**

Математический детерминизм per se (zero-float state, canonical ordering, integer safety, Unity-изоляция, отсутствие time/randomness-утечек) — **образцово чист**: это лучшая часть кодовой базы, и это не лесть, а констатация проверенного факта. Но жесткие критерии фазы не выполнены:

1. **P0:** командная слепота боевого ядра в живом 2v2 — friendly fire + принципиально нетерминальный матч (`DetermineOutcome` требует единственного *owner*, а не *team*).
2. **P1:** zero-GC в `TickOnce` нарушен (делегат на каждый тик с командами, двойной расчет формаций с аллокациями), дыра в валидации Stop-команды (нет `MaxSelectedEntities`), самопереписывающийся ring-buffer в публичном `CombatTickResult`, замораживающий матч int-cast в `FixedStepClock`, отсутствие проверки владельца цели attack-приказа.
3. **P2:** глобальное статическое мутабельное состояние резолвера (потоки/мульти-матчи/утечка), обход валидации в `SpawnUnitWithEntity`, орфаненные scheduled-команды после финала, отсутствие thread-affinity контракта.

Блокеры для перехода к следующей фазе: **P0-1** (team-модель в ядре боя), **P1-1/P1-5** (валидация команд), **P1-2** (zero-GC в тике), **P1-4** (pacing-стойкость). P2-статика резолвера должна быть решена архитектурно до выделенного Linux-демона, иначе мульти-матчевый хостинг унаследует гонки.

отчет от glm 5.3

---

## Message Index 100

Я завершил построчный аудит. Ниже — полный отчёт по Фазе 1.0 в запрошенном формате. Все номера строк и фрагменты верифицированы по актуальному содержимому файлов.

---

# 🔴 ADVERSARIAL AUDIT — GlobalFront Phase 1.0 (Simulation Foundation & Server Tick)

## Сначала — что аудит ПОДТВЕРДИЛ как корректное (чтобы вердикт был честным)

| Правило | Статус | Доказательство |
|---|---|---|
| **Zero float в состоянии симуляции** | ✅ PASS | Единственные `double` в Core — `FixedStepClock` (слой пейсинга, не состояние) и `const ServerTickDurationSeconds`. Вся математика тика (`CombatMath`, `PlanarMovement`, `WorldPointMm`, `MatchServer.TickOnce`) — чистый `int`/`long`/`ulong`. x86/ARM-дивергенции в состоянии нет. |
| **Канонический порядок в тике** | ✅ PASS | `MatchServer.TickOnce` и `CombatTickResolver` используют **только** индексированные `for` по `_orderedUnits`/`s_canonical` (отсортированы по `EntityId`). Grep `foreach` по тику пуст — `foreach` есть лишь в `SessionManager`/транспорте (вне sim-состояния). Выполнение команд сортируется по уникальной паре `(Player, Sequence)`. |
| **Недетерминизм (время/рандом)** | ✅ PASS | В тик-пути нет `DateTime`/`Stopwatch`/`Random`/`Guid`/`GetHashCode`-по-адресу. `Guid.NewGuid` — только ID сессий/матчей; `System.Random` в `VirtualNetworkPipe` — с явным `Seed`; `Stopwatch` — транспортные часы. Ни одно не питает sim-состояние. |
| **Unity-изоляция** | ✅ PASS | Grep `UnityEngine|MonoBehaviour|Vector3|Mathf|Time.deltaTime|Debug.Log` по Core+Server = **0 совпадений**. Headless-совместимо. |
| **Overflow в `SquaredDistance`/`IsWithinRange`** | ✅ PASS | При координатах ±10⁹ дельта ≤ 2·10⁹, квадрат ≤ 4·10¹⁸, сумма ≤ 8·10¹⁸ < ulong.Max (1.8·10¹⁹). Сатурация на строке 330 корректна. |
| **Деление на ноль в `StepTowards`** | ✅ PASS | `distanceCeiling ≥ 1` всегда (так как `squaredDistance > maxDistance² ≥ 1`). `IntegerSquareRoot` не получает 0. |
| **Бесконечные циклы** | ✅ PASS | `IntegerSquareRoot`, `QuickSort`, `GetAutoColumnCount` — все терминальны при валидных уникальных ключах. |
| **Desync-детектор** | ✅ PASS | `ServerReplicationEmitter.ComputeStateChecksum` (FNV-1a по entity/pos/hp/targets) существует и детерминирован. |

**База качественная.** Но найдены дефекты, из-за которых вердикт — **REJECT**.

---

## [P1 - Critical] #1 — Целочисленное переполнение в десериализаторе snapshot обходит проверку длины → OOM/DoS от 16-байтного пакета

**Файл:** `Server/Snapshot/SnapshotDeserializer.cs`
**Строки:** 47, 57–58, 59–63, 66

```csharp
var unitCount = ReadUInt32(data, ref offset);          // 47 — контролируется атакующим
...
var expectedSize = SnapshotProtocol.HeaderSizeBytes +
                  (int)unitCount * SnapshotProtocol.SnapshotSizeBytes;   // 57-58 — ПЕРЕПОЛНЕНИЕ
if (data.Length < expectedSize) { throw ... }          // 59-63 — ОБОЙДЕНА
...
var snapshots = new ServerUnitSnapshot[unitCount];     // 66 — аллокация по враждебному размеру
```

**Сценарий сбоя.** `unitCount` читается из wire как `uint` и не валидируется. При `unitCount = 0xFFFFFFFF`:
- `(int)unitCount = -1`; `-1 * 39 = -39`; `16 + (-39) = -23`.
- Проверка `data.Length < expectedSize` → `16 < -23` = **false** → гард обойдён.
- `new ServerUnitSnapshot[4294967295]` → `OverflowException`/`OutOfMemoryException` — необработанный краш десериализатора от 16-байтного hostile-пакета.

Для `unitCount = 0x40000000` умножение `(int)*39` тоже переполняется в отрицательное — та же дыра. `SnapshotSerializer` (стр. 65-66) имеет такой же каст, но там `UnitCount` уже сверен с `snapshots.Length`, поэтому он безопасен; уязвим именно **deserializer**.

**Remediation.** Ограничить `unitCount` реально доступным payload ДО любого каста/аллокации:

```csharp
var unitCount = ReadUInt32(data, ref offset);

// Защита от переполнения и OOM: принять не больше юнитов, чем физически влезает в буфер.
var maxByPayload = (uint)((data.Length - SnapshotProtocol.HeaderSizeBytes)
                          / SnapshotProtocol.SnapshotSizeBytes);
if (unitCount > maxByPayload)
{
    throw new SnapshotSerializationException(
        $"Packet too short: declared {unitCount} units, payload holds at most {maxByPayload}.");
}

var expectedSize = SnapshotProtocol.HeaderSizeBytes +
                  (int)unitCount * SnapshotProtocol.SnapshotSizeBytes; // теперь unitCount ограничен, переполнения нет
```

---

## [P1 - Critical] #2 — `FormationLayout.CreateSlots` аллоцирует массив внутри тика (нарушение Zero-GC)

**Файлы:** `Core/FormationLayout.cs:33` → вызывается из `Server/MatchServer.cs:714` (каждый тик с move-командой)

```csharp
// FormationLayout.cs:33
var slots = new FormationSlot[count];
...
// MatchServer.cs:712-714 (ExecuteMove, вызывается из TickOnce)
private void ExecuteMove(ScheduledCommand command)
{
    var slots = FormationLayout.CreateSlots(command.Move);   // 714 — new[] каждый тик
```

**Сценарий сбоя.** `ExecuteMove` вызывается из `ExecuteScheduledCommands` ← `TickOnce`. На каждом тике, в котором исполняется хотя бы одна move-команда, `CreateSlots` аллоцирует `new FormationSlot[count]` (до 100 элементов) — прямо в боевом цикле. При активной micro-игре это поколения мусора каждый тик → GC-паузы/статтер на сервере. Прямое нарушение правила №6 («выделяются ли массивы при расчёте формаций»). Дополнительно `CreateSlots` вызывается **дважды** на команду: на enqueue (стр. 380) и на execute (стр. 714).

**Remediation.** Переиспользовать буфер на уровне `MatchServer` (как уже сделано для `_combatantInputsCache`/`_startPositionsCache`):

```csharp
// MatchServer: поле
private FormationSlot[] _formationScratch = new FormationSlot[SimulationConstants.MaxSelectedEntities];

// ExecuteMove
var slots = FormationLayout.CreateSlots(command.Move, _formationScratch);
```

```csharp
// FormationLayout: новая перегрузка без аллокации при count <= destination.Length
public static FormationSlot[] CreateSlots(MoveCommand command, FormationSlot[] destination)
{
    if (destination == null) throw new ArgumentNullException(nameof(destination));
    var count = command.EntityCount;
    if (destination.Length < count) return CreateSlots(command); // fallback для редкого случая
    // ... записывать слоты в destination вместо `new FormationSlot[count]`
    // вернуть destination (или ReadOnlySpan<FormationSlot>)
}
```

---

## [P2 - Major] #3 — Глобальные статические мутируемые буферы в `CombatTickResolver` + перерабатываемый `CombatTickResult`

**Файл:** `Core/CombatSimulation.cs:362–376`, `369–375`, `475–484`

```csharp
private static CombatantTickInput[] s_canonical = new CombatantTickInput[1024];
private static readonly Dictionary<EntityId, CombatantTickInput> s_byEntity = ...;
private static readonly List<DamageEvent> s_events = ...;
private static readonly Dictionary<EntityId, long> s_accumulatedDamage = ...;
private static readonly DamageEvent[][] s_eventBuffers = new[]{ ...1024... x4 }; // 369-375
private static int s_bufferIndex;
```

**Сценарий сбоя.**
1. **Cross-match corruption.** Комментарии в `LocalMatchHost`/`MatchServer` открыто планируют «a future dedicated server host may manage several matches». Два одновременных `TickOnce()` (два матча в одном процессе) будут делить одни статические буферы → тихий desync. Нет ре-энтрантности.
2. **Переработанный буфер результата.** `CombatTickResult` (стр. 483-484) возвращает ссылку на один из 4 rotating-буферов. Через **4 тика** этот буфер перезаписывается. `MatchServer.ResolveCombat` читает только `.Outcome` — сейчас безопасно, но любой будущий потребитель (replication, тест), хранящий результат >4 тиков, прочитает чужие/пустые `DamageEvent`.

**Remediation.** Убрать `static`, сделать буферы экземплярными (per-`MatchServer`, как `_combatantInputsCache`), а `CombatTickResult` копировать события в caller-owned массив или документировать время жизни:

```csharp
// Сделать resolver экземплярным или передавать scratch-контекст:
public sealed class CombatTickContext {
    private CombatantTickInput[] _canonical = new CombatantTickInput[1024];
    private readonly Dictionary<EntityId, CombatantTickInput> _byEntity = new(1024);
    // ... остальные буферы как поля экземпляра
}
// MatchServer владеет одним CombatTickContext и передаёт его в Resolve.
```

---

## [P2 - Major] #4 — `SpawnUnit` не ограничивает `speedMmPerTick` сверху

**Файл:** `Server/MatchServer.cs:273–276`

```csharp
if (speedMmPerTick <= 0)
{
    throw new ArgumentOutOfRangeException(nameof(speedMmPerTick));
}
```

**Сценарий сбоя.** Проверяется только нижняя граница. `speedMmPerTick` может быть `int.MaxValue` (≈2.1·10⁹ мм/тик). В `StepTowards` это не даёт немедленного overflow (защищает `checked`), но логически позволяет юниту «телепортироваться» через полмира за тик — обход коллизий/formation-границ и непредсказуемое поведение при будущей физике. Отсутствие defensive-гарда на входе.

**Remediation.**

```csharp
if (speedMmPerTick <= 0 ||
    speedMmPerTick > SimulationConstants.MaxWorldCoordinateMm)
{
    throw new ArgumentOutOfRangeException(nameof(speedMmPerTick));
}
```

---

## [P2 - Major] #5 — `ExecuteMove` вызывает `CreateSlots` без `try/catch (OverflowException)`, в отличие от enqueue

**Файл:** `Server/MatchServer.cs:712–714` (execute) vs `:378–385` (enqueue)

```csharp
// enqueue (378-385) — гард есть:
try { slots = FormationLayout.CreateSlots(command); }
catch (OverflowException) { return MatchCommandRejection.FormationOutOfBounds; }

// execute (712-714) — гарда НЕТ:
var slots = FormationLayout.CreateSlots(command.Move);
```

**Сценарий сбоя.** Сейчас недостижимо (команда иммутабельна, входные данные те же, что и на enqueue). Но это асимметрия: любой будущий путь, позволяющий команде с переполняющимися слотами добраться до execute (изменение валидации, clamp таймингов, переиспользование команд), уронит `TickOnce` необработанным `OverflowException`. Defensive- gap.

**Remediation.** Обездвижить пересчёт: валидировать и **закешировать** slots один раз на enqueue в `ScheduledCommand`, а в execute только применять:

```csharp
// ScheduledCommand { ... public FormationSlot[] MoveSlots; }
// TryEnqueueMove: один раз посчитать slots с try/catch и положить в команду
// ExecuteMove: использовать command.MoveSlots (без пересчёта, без аллокации, без риска)
```

Это одновременно закрывает **P1 #2** (нет аллокации в тике) и **P2 #5** (нет риска OverflowException в execute).

---

## [P2 - Major] #6 — `foreach` по `Dictionary` в per-tick пути сессий (нуждается в верификации)

**Файл:** `Server/Sessions/SessionManager.cs:675, 789, 811`

```csharp
foreach (var pair in _sessions)   // 675, 789, 811
```

**Сценарий сбоя.** `SessionManager.OnTickCompleted` вызывается каждый тик (`LocalMatchHost.cs:579`). Если любой из этих `foreach` по `Dictionary<,>` участвует в формировании авторитетного вывода (порядок выдачи слотов, порядок grace-expiry, порядок репликации), .NET-порядок итерации словаря **не гарантирован** и разойдётся между платформами/рантаймами. На данный момент session-management — мета-состояние, не sim-состояние, поэтому помечаю как **P2 «требует доказательства безопасности»**, а не подтверждённый desync.

**Remediation.** Если результат итерации влияет на authoritative-выход — итерировать по отсортированному ключу:

```csharp
// собирать ключи в буфер, сортировать, итерировать по ключу
// либо держать отдельный List<SessionId> в каноническом порядке
```
ЕслиGrace-expiry/reconnect идемпотентны и не зависят от порядка — задокументировать инвариант явным комментарием.

---

## [P3 - Minor] — Архитектурный долг / шероховатости

| # | Файл:строка | Суть | Remediation |
|---|---|---|---|
| 7 | `MatchServer.cs:565–574` `GetAllSnapshots()` | Аллоцирует `new ServerUnitSnapshot[]` на каждый вызов. Метод `[Obsolete]`, hot-path уже использует `CopySnapshots(Span)` — приемлемо, но legacy-путь всё ещё аллоцирует. | Удалить/`internal` после миграции всех consumer'ов на `CopySnapshots`. |
| 8 | `FixedStepClock.cs:11,13,29,60–61,69` | `double`-математика пейсинга. Для server-authoritative OK, но миграция на истинный lockстеп сломает детерминизм. | При переходе на lockstep — заменить на fixed-point или scheduling по счётчику тиков. |
| 9 | `MatchServer.cs:500–503, 689` | После терминального `Outcome` `TickOnce` выходит рано, не draining `_scheduledCommands` → команды на будущие тики удерживаются до GC сервера. Утечка ограничена, но есть. | В ветке терминала очищать `_scheduledCommands.Clear()`. |
| 10 | `MatchServer.cs:664` | Команды с `RequestedTick` в прошлом молча переназначаются на `CurrentTick+1`. Для authoritative OK; для чистого lockstep сломало бы предсказание клиента. | Задокументировать как сознательное решение authoritative-модели. |

---

# ВЕРДИКТ

# 🚫 [REJECT: DEFECTS FOUND]

**Найдено:** 2 × P1, 4 × P2, 4 × P3.

Критичные блокеры для приёмки:
- **P1 #1** — враждебный 16-байтный snapshot-пакет роняет/DoS-ит десериализатор через целочисленное переполнение, обходящее проверку длины (`SnapshotDeserializer.cs:47–66`).
- **P1 #2** — аллокация массива формаций внутри боевого цикла (`FormationLayout.CreateSlots` через `MatchServer.ExecuteMove`, `MatchServer.cs:714`), нарушение Zero-GC.

Плюс 4 × P2 (глобальные статические буферы resolver'а + перерабатываемый результат; отсутствие верхней границы скорости; отсутствие OverflowException-гарда в execute; неверифицированный `foreach` по словарю в per-tick пути сессий).

**Что нужно для APPROVE:** закрыть оба P1 (валидация `unitCount` в deserializer + кеш/скретч формаций, посчитанных на enqueue), устранить или явно обосновать 4 P2. Фундамент в остальном крепок: детерминизм состояния, канонический порядок, Unity-изоляция и overflow-безопасность математики — на высоком уровне.

отчет от модели step 5

---

## Message Index 101

идет последний аудит от модели qwen 3.8 max 0902, ждем

---

## Message Index 102

Начну с загрузки контекста памяти и поиска исходников Фазы 1.0.



Память загружена. Теперь найду исходники Фазы 1.0 (Core / Server).



Скоуп Фазы 1.0 ясен: детерминированное ядро, 20 Гц, MatchServer, LocalMatchHost, Snapshot Protocol v1. Составлю план аудита.



Каркас чист по float-скану. Читаю примитивы ядра.



Первые зацепки есть (деление на ноль в формациях, выход за границы мира). Читаю команды и конфиг.



Ключевые подозрения сформированы. Читаю боевую симуляцию и сердце тика — MatchServer.



CombatTickResolver несёт глобальное статическое состояние и ротируемые буферы — это серьёзно. Теперь читаю MatchServer целиком.



MatchServer прочитан — есть серьёзные находки. Проверяю контрольные суммы, порядок итерации и вызывающие стороны.



Читаю LocalMatchHost — связку драйвера тиков с сервером.



Проверяю Snapshot Protocol v1 и паритет контрольных сумм.



Нашёл кандидат на P0 в десериализаторе снапшотов. Проверяю достижимость и читаю кодек команд.



Ключевые находки сформулированы. Теперь проверю их экспериментально — соберу вне-Unity харнес на подмножестве Core/Server.



Собираю харнес для эмпирической проверки находок.



Собралось без Unity-зависимостей — это уже подтверждение правила 5. Запускаю зонды.



P1 не воспроизвёлся — юнит без цели движения не доходит до StepTowards. Уточняю зонд и проверяю, можно ли собрать LocalMatchHost вне Unity.



Server полностью свободен от UnityEngine. Пробую собрать настоящий LocalMatchHost вне Unity, чтобы доказать находку в пауз-аккумуляторе.



Весь модуль Server + LocalMatchHost компилируется вне Unity. Запускаю оба харнеса.



P1 полностью воспроизведён. Запускаю хост-зонды и реальную атаку на десериализатор.



P0 подтверждён: бесконечный цикл, 1 054 057 563 итерации за 5 секунд. Теперь реальная атака на десериализатор — с жёстким лимитом кучи, чтобы не задеть машину.



Проверяю контекст: привязку PlayerId к сессиям, клиентский расчёт формаций и существующие архитектурные гейты.



Существующие архитектурные гейты — важный контекст. Проверяю, что они реально покрывают и где дыры.



Проверяю полный список гейтов и замеряю аллокации в тике напрямую.



Добавляю замер аллокаций в авторитетном тике.



Все зонды воспроизведены. Проверяю, где именно вызывается GetAllSnapshots, и цену сортировки при спавне.



Все находки воспроизведены эмпирически. Обновляю статус задач и пишу отчёт.



АДЪЮДИТ ФАЗЫ 1.0 — GlobalFront Simulation Foundation & Server Tick

Метод. Не только чтение: я собрал Core/ + Server/ + LocalMatchHost.cs как отдельное net8.0-приложение связанным с Unity dotnet.exe (0 ошибок) и прогнал 18 зондов на реальном коде. Харнес: Library/ScratchAuditP10/ (gitignored), запуск dotnet run --project AuditHarness.csproj -c Release. Все числа ниже — измерения, а не оценки. Коммит 1e60529, рабочее дерево содержит незакоммиченные правки Phase 3.7 — они аудита Фазы 1.0 не касаются.

Скоуп. Phase_01_Foundation.md: детерминированное ядро, 20 Гц, MatchServer, LocalMatchHost, server-owned state, Snapshot Protocol v1, command foundation. Файлы Phase 2.x/3.x внутри тех же модулей помечены отдельно.

---

[P0 — Blocker]

P0-1. LocalMatchHost.AdvanceRealTime — незавершающийся цикл на нефинитном входе в паузе
Assets/_GlobalFront/Runtime/Client/LocalMatchHost.cs:215-223

_pausedSecondsAccumulator += elapsedSeconds;      // 215 — вход не валидируется
_isAdvancingRealTime = true;
try
{
    while (_pausedSecondsAccumulator >= _tickDriver.TickDurationSeconds)   // 219
    {
        _pausedSecondsAccumulator -= _tickDriver.TickDurationSeconds;      // +Inf - 0.05 = +Inf
        AdvancePauseTicks(1);
    }
}

FixedStepClock.Advance явно отвергает NaN/Inf/отрицательное (FixedStepClock.cs:48-56) и ограничивает catch-up четырьмя тиками. Ровно тот же вход в 100 строках выше, у единственного вызывающего слоя, не защищён ничем.

Сценарий сбоя (измерено). Тактическая пауза включена (AutoPauseOnDisconnect == true по умолчанию, LocalMatchHost.cs:274, либо TacticalPauseOverlay) → один вызов AdvanceRealTime(double.PositiveInfinity) → поток never returns

INFINITY: AdvanceRealTime(+Infinity) while paused still running after 5 s? True
INFINITY: grace ticks reached = 1054057563
INFINITY: VERDICT = non-terminating loop (host thread never returns).

1 054 057 563 итерации за 5 секунд, каждая — вызов _sessions.OnTickCompleted. Это подвес хоста, а не деградация.

Два сопутствующих режима того же цикла, тоже измерены
NaN → NaN >= 0.05 ложно, цикл не входит, но аккумулятор отравлен навсегда: _pausedSecondsAccumulator = NaN, и последующий здоровый AdvanceRealTime(1.0) даёт 0 grace-тиков вместо 20. Учёт паузы молча умирает до конца процесса.
Конечное значение → catch-up не ограничен: AdvanceRealTime(10.0) в работе = 4 тика (предел MaxCatchUpTicksPerFrame), в паузе = 199 итераций. Одна и та же функция, два разных контракта.

Remediation.

public int AdvanceRealTime(double elapsedSeconds)
{
    if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds  0 &&
                   _pausedSecondsAccumulator >= _tickDriver.TickDurationSeconds)
            {
                _pausedSecondsAccumulator -= _tickDriver.TickDurationSeconds;
                AdvancePauseTicks(1);
            }

            // избыток остаётся в очереди, как и в FixedStepClock.Advance
            if (_pausedSecondsAccumulator > _tickDriver.TickDurationSeconds 
                SimulationConstants.MaxCatchUpTicksPerFrame)
            {
                _pausedSecondsAccumulator = _tickDriver.TickDurationSeconds 
                    SimulationConstants.MaxCatchUpTicksPerFrame;
            }
        }
        finally
        {
            _isAdvancingRealTime = false;
        }
    }
    ...
}

---

P0-2. SnapshotDeserializer — защитный размерный гейт обходится знаковым переполнением u32 с провода
Assets/_GlobalFront/Runtime/Server/Snapshot/SnapshotDeserializer.cs:57-66

var expectedSize = SnapshotProtocol.HeaderSizeBytes +
                  (int)unitCount  SnapshotProtocol.SnapshotSizeBytes;   // 57-58
if (data.Length  (int)=-1,           expectedSize=-23,         rejected? False, allocation=229375 MiB
unitCount=0x80000000 -> (int)=-2147483648,  expectedSize=-2146067280, rejected? False, allocation=114688 MiB
unitCount=0x05F5E100 -> (int)=100000000,    expectedSize=-394967280,  rejected? False, allocation=5340 MiB
unitCount=0x0348C260 -> (int)=55100000,     expectedSize=-2146067280, rejected? False, allocation=2942 MiB

Реальный вызов Deserialize на 16-байтовой датаграмме (под лимитом кучи, чтобы не положить машину)

MALICIOUS: 16-byte datagram -> System.OverflowException: Arithmetic operation resulted in an overflow.
MALICIOUS: controlled protocol error? False

Без лимита кучи тот же вход запрашивает 229 ГиБ → OutOfMemoryException. В обоих случаях наружу уходит неконтролируемое CLR-исключение вместо контрактного SnapshotSerializationException; в обработчике приёма LiteNetLib это сносит receive-loop. 16 байт с провода = падение процесса. Для контраста честный короткий пакет (unitCount=1000) отвергается корректно — измерено.

Достижимость (честно): сегодня Deserialize вызывают только тесты (SnapshotSerializationTests, TransportIntegrationTests, LiteNetLibCarrierIntegrationTests, DeltaSnapshotWireCodecTests:512); продакшн-репликация идёт через DeltaSnapshotWireCodec. Но это публичная точка входа протокола, объявленного в Phase_01_Foundation.md как завершённый deliverable, и существующий негативный тест InvalidUnitCount_IsRejected (SnapshotSerializationTests.cs:176-188) проверяет только unitCount = 10 — заворачивающийся диапазон не покрыт ничем. Если скорректировать оценку по фактической досягаемости — это P1; как дефект отгруженного кодека с нарушенным собственным контрактом («Rejects packets with insufficient payload for declared UnitCount», :15) — P0.

Remediation.

public const uint MaxUnitsPerPacket = 4096;   // в SnapshotProtocol, рядом с Version

var unitCount = ReadUInt32(data, ref offset);

if (unitCount > SnapshotProtocol.MaxUnitsPerPacket)
{
    throw new SnapshotSerializationException(
        $"UnitCount {unitCount} exceeds the protocol maximum {SnapshotProtocol.MaxUnitsPerPacket}.");
}

var expectedSize = SnapshotProtocol.HeaderSizeBytes +
                  (long)unitCount  SnapshotProtocol.SnapshotSizeBytes;   // long, не int
if (data.Length  0. SpawnUnitWithEntity не проверяет ни одного из четырёх. Публичный, документирован, проброшен наружу через LocalMatchHost.SpawnUnitWithEntity (LocalMatchHost.cs:341-349) и использован в ~15 тестах как основной способ собрать сцену.

Сценарий сбоя (измерено). Юнит с X = 1 005 000 000 (за MaxWorldCoordinateMm = 1 000 000 000) принимается. Сам по себе он не падает — MoveUnits делает continue при !HasMoveTarget (:810-813). Достаточно одного легального приказа движения (валидатор приказа проверяет destination и слоты, но не текущую позицию юнита)

REPRODUCED: SpawnUnitWithEntity accepted position X=1005000000
     TryEnqueueMove for the out-of-bounds unit -> None
REPRODUCED: TickOnce threw -> ArgumentOutOfRangeException: ... (Parameter 'current')
REPRODUCED: CurrentTick advanced to 1 although the tick body aborted (no rollback)
REPRODUCED: every subsequent tick throws the same way (server is bricked): ArgumentOutOfRangeException
REPRODUCED: SpawnUnit (the sibling API) DOES reject the same position: ArgumentOutOfRangeException

speedMmPerTick: 0 — то же самое, падает на Parameter 'maxDistanceMm'. Исключение летит из PlanarMovement.cs:21-29 через MatchServer.cs:815, т.е. из середины фазы 4: фазы 1-3 уже мутировали состояние, фаза 5 не выполнилась, CurrentTick инкрементирован. Сервер больше не способен тикать, а Outcome не стал терминальным — то есть команды продолжают приниматься и складываться в _scheduledCommands навсегда.

Remediation. Одна общая валидация для обоих путей

private void ValidateSpawnArguments(PlayerId owner, CombatStats stats,
    WorldPointMm position, int speedMmPerTick)
{
    if (!owner.IsValid) throw new ArgumentException("Owner must be valid.", nameof(owner));
    if (!stats.IsValid) throw new ArgumentException("Combat stats must be valid.", nameof(stats));
    if (!position.IsWithinSimulationBounds) throw new ArgumentOutOfRangeException(nameof(position));
    if (speedMmPerTick  s_byEntity = ...;   // 363
private static readonly List s_events = new List(1024);    // 365
private static readonly Dictionary s_accumulatedDamage = ...;        // 366
private static readonly DamageEvent[][] s_eventBuffers = new DamageEvent[][] { ... };// 369-375
private static int s_bufferIndex;                                                    // 376
...
s_bufferIndex = (s_bufferIndex + 1) % s_eventBuffers.Length;   // 475
var buffer = s_eventBuffers[s_bufferIndex];                    // 476
s_events.CopyTo(buffer, 0);                                    // 483
return new CombatTickResult(buffer, s_events.Count, outcome);  // 484 — наружу уходит ЧУЖОЙ буфер

Сценарий сбоя (измерено). Результат удержан, затем четыре обычных вызова Resolve

result retained: EventCount=1, Damage=10
     after 1 further Resolve() call(s) the SAME retained result reads Damage=10
     after 2 further Resolve() call(s) the SAME retained result reads Damage=10
     after 3 further Resolve() call(s) the SAME retained result reads Damage=10
     after 4 further Resolve() call(s) the SAME retained result reads Damage=77
REPRODUCED: a retained CombatTickResult silently changed from Damage=10 to Damage=77

Глубина жизни результата — 3 вызова, и это не документировано. Три следствия
Любой потребитель, держащий CombatTickResult дольше (лог боя, репликация событий, отложенная презентация урона, реплей), читает события другого разрешения. Значения меняются молча, без исключения.
Статики разделяются всеми экземплярами MatchServer в процессе. Выделенный сервер на несколько матчей или параллельные фикстуры NUnit → s_events/s_accumulatedDamage/s_bufferIndex пишутся одновременно → гонка данных и перекрёстная порча. Для headless-демона, заявленного в скоупе фазы, это архитектурное нарушение.
Реентерабельность фатальна: Resolve из обработчика DamageEvent перезапишет состояние самого себя.

Проверил гейт: в ArchitectureInvariantsTests (12 кейсов) нет ни одной проверки на статическое состояние — grep -c "IsStatic" = 0. Гейт ловит float, LINQ, RNG, часы и Guid; общее изменяемое состояние в авторитетном ядре не ловит ничто.

Честная оговорка: сегодня продакшн-потребитель один — MatchServer.ResolveCombat:922-927 читает только result.Outcome (value type) и игнорирует события, так что aliasing не проявляется. Дефект латентный, но API публичный и контракт не описан.

Remediation. Буфер принадлежит вызывающему

public static BattleOutcome Resolve(
    CombatantTickInput[] combatants,
    int count,
    ulong tick,
    Span events,        // caller-owned, zero-GC
    out int eventCount)

Весь набор s_ становится полями экземпляра нового CombatTickResolver (или MatchServer), владеющего своими буферами. s_eventBuffers/s_bufferIndex удаляются целиком — ротация общих буферов существует только чтобы имитировать владение, которого нет. Если экземплярное решение откладывается, минимум — документировать срок жизни результата одним тиком и добавить в гейт проверку «ноль не-readonly статических полей в AuthoritativeNamespaces».

---

P1-5. ExecuteMove аллоцирует внутри TickOnce; существующий zero-GC гейт этого не видит
Assets/_GlobalFront/Runtime/Server/MatchServer.cs:714 → Assets/_GlobalFront/Runtime/Core/FormationLayout.cs:33

private void ExecuteMove(ScheduledCommand command)
{
    var slots = FormationLayout.CreateSlots(command.Move);   // 714
var slots = new FormationSlot[count];                        // FormationLayout.cs:33

Измерено (GC.GetAllocatedBytesForCurrentThread, 200 юнитов, приказ на 100 сущностей)

50 steady ticks, no commands        -> 0 bytes (0.0 B/tick)
     one TryEnqueueMove of 100 entities -> 2984 bytes
     the single tick that EXECUTES it    -> 1688 bytes
     50 ticks after the order landed     -> 0 bytes (0.0 B/tick)

Утверждение Zero-GC для установившегося состояния верно — 0 байт/тик подтверждён. Аллокация живёт ровно в тике исполнения приказа, т.е. в пике нагрузки: массовый drag-box на 100+ юнитов даёт серию таких тиков подряд. Кроме того, одна и та же раскладка считается дважды — на валидации (MatchServer.cs:380) и на исполнении (:714); второй расчёт избыточен, команда иммутабельна.

Почему гейт слеп. TacticalVerticalSliceBenchmarkTests.cs:719-748: IssueConvergenceMarch() вызывается на строке 724, затем StepTicks(runner, WarmupFrames) (725), и только потом измеряемое окно (730-739). Приказы исполняются в warmup — вне замера. Тест измеряет марш, а не выдачу приказа, и проходит green независимо от этой аллокации.

Remediation. Переиспользуемый буфер слотов, принадлежащий серверу

// MatchServer
private FormationSlot[] _formationSlotCache = new FormationSlot[SimulationConstants.MaxSelectedEntities];

private void ExecuteMove(ScheduledCommand command)
{
    var count = command.Move.EntityCount;
    if (_formationSlotCache.Length  None
REPRODUCED: the command was ACCEPTED by the server
     tick=20, HasMoveTarget=False, Position=(0, 0) mm, Outcome=Victory
REPRODUCED: the accepted move never executed

TickOnce делает return на :500 до ExecuteScheduledCommands на :505, поэтому записи в _scheduledCommands не исполняются и не удаляются. Клиенту вернули None, приказ не выполнен, отказа не было. Тот же механизм теряет любые команды, принятые между тиком приёма и тиком терминального исхода (до 60 тиков вперёд по AcceptedFutureTicks).

Практический триггер в 2v2: полное уничтожение одной стороны — это и есть замысел. Но сценарии «один игрок против ИИ/песочницы», «матч до подхода подкреплений», «co-op без противника на старте» самозавершаются на первом тике. И MatchServer не имеет Reset — экземпляр одноразовый.

Remediation. Терминальный исход требует, чтобы сторона реально выбыла

// MatchServer: владельцы, присутствовавшие на старте
private readonly HashSet _initialOwners = new HashSet();
// ... в SpawnUnitCore: _initialOwners.Add(owner);

private static BattleOutcome DetermineOutcome(
    CombatantTickInput[] canonical, int count, IReadOnlyCollection initialOwners)
{
    if (initialOwners.Count  0 ? BattleOutcome.Victory(soleOwner) : BattleOutcome.Draw;
}

И дренаж расписания при переходе в терминал, чтобы принятая команда не зависала молча

public void TickOnce()
{
    CurrentTick++;
    if (UnitCount == 0) { return; }

    if (Outcome.IsTerminal)
    {
        _scheduledCommands.Clear();     // принятые приказы более не исполнимы
        return;
    }
    ...
}

---

[P2 — Major]

P2-1. MatchConfig не иммутабелен: Units отдаёт массив вызывающего
Assets/_GlobalFront/Runtime/Core/MatchConfig.cs:81-105

public MatchConfig(CombatStats unitStats, UnitSpawnSpec[] units)
{
    if (!unitStats.IsValid) { throw ... }
    UnitStats = unitStats;
    Units = units ?? throw new ArgumentNullException(nameof(units));   // 89 — без копирования
}
public UnitSpawnSpec[] Units { get; }                                  // 103 — наружу тот же массив

Документация на :99-102 обещает: «The server assigns EntityIds sequentially in this order, so identical configs produce identical EntityId assignments». Обещание невыполнимо: порядок и содержимое меняются после передачи объекта.

Измерено. Мутация массива после конструирования

config was mutated AFTER construction; server spawned entity 2 at (-123456, -123456) mm
REPRODUCED: MatchConfig.Units exposes the caller's array

Сценарий: шаблон матча переиспользуется между играми, reconnect/resync или реплей перечитывает тот же объект — и получает другой матч. Плюс нет верхней границы на UnitCount (см. P2-3).

Remediation.
public MatchConfig(CombatStats unitStats, UnitSpawnSpec[] units)
{
    if (!unitStats.IsValid) throw new ArgumentException("Unit stats must be valid.", nameof(unitStats));
    if (units == null) throw new ArgumentNullException(nameof(units));
    if (units.Length == 0) throw new ArgumentException("A match needs at least one unit.", nameof(units));
    if (units.Length > SimulationConstants.MaxUnitsPerMatch)
        throw new ArgumentOutOfRangeException(nameof(units));

    UnitStats = unitStats;
    Units = (UnitSpawnSpec[])units.Clone();
}
(MaxUnitsPerMatch — новая константа в SimulationConstants, часть протокола.)

---

P2-2. Мёртвые сущности никогда не удаляются; ростер только растёт, стоимость тика квадратична по нему
Assets/_GlobalFront/Runtime/Server/MatchServer.cs:124-127, :318-343, :857-905

В MatchServer нет ни одного удаления: _units.Remove / _orderedUnits.RemoveAt отсутствуют в файле. Все четыре фазы обходят полный ростер, включая трупы; AcquireAutomaticTargets — вложенным циклом по всему ростеру для каждой живой auto-acquire единицы.

Измерено.
victim health=0, UnitCount=2, Outcome=Victory
     200 ticks later: UnitCount=2, CopySnapshots returned 2 records, tick=205
REPRODUCED: the dead unit is still in the authoritative roster

     200 ticks over a roster of 200 (auto-acquire on) = 20 ms
     200 ticks over a roster of 400 (auto-acquire on) = 58 ms
REPRODUCED: AcquireAutomaticTargets is O(roster^2)

2.9× при 2× ростера — квадратичность подтверждена. На 400 юнитах только фаза приобретения даёт ~0.29 мс/тик; для сравнения, зафиксированный в репозитории полный бюджет — 0.623 мс/кадр. Стоимость не падает по ходу боя: к концу сражения тик стоит столько же, сколько в начале, при нуле живых целей.

Кросс-фазовое следствие, описанное самим репозиторием (LocalMatchHost.cs:385-388): дефолтный MatchServerSnapshotSource транслирует ростер, который никогда не сжимается, поэтому трупы реплицируются бесконечно, пока кто-то не подменит source на обрезающий. Корень — здесь.

Remediation. Фаза reap после боя + компактизация

// MatchServer.TickOnce, после ResolveCombat:
ReapCorpses();

private void ReapCorpses()
{
    for (var index = _orderedUnits.Count - 1; index >= 0; index--)
    {
        var unit = _orderedUnits[index];
        if (unit.Combat.IsAlive || unit.DeathTick + CorpseRetentionTicks > CurrentTick) { continue; }

        _orderedUnits.RemoveAt(index);          // список остаётся отсортированным
        _units.Remove(unit.Combat.Entity);
        _pendingRemovals.Add(unit.Combat.Entity);   // для DeltaRemoveRecord
    }
}

CorpseRetentionTicks — часть протокола (клиенту нужно увидеть смерть). Для AcquireAutomaticTargets — бакетинг по сетке или отсортированный по X массив с разрывом на AutoAcquireRangeMm, что даёт O(N·k) вместо O(N²).

---

P2-3. SpawnUnitCore пересортировывает весь ростер на каждый спавн
Assets/_GlobalFront/Runtime/Server/MatchServer.cs:336-340

_units.Add(entity, record);
_orderedUnits.Add(record);
_orderedUnits.Sort(
    (left, right) => left.Combat.Entity.Value.CompareTo(right.Combat.Entity.Value));

Полная introsort-сортировка на каждое добавление → InitializeMatch стоит O(N²·log N).

Измерено:
InitializeMatch(250 units)  = 1 ms
InitializeMatch(500 units)  = 5 ms
InitializeMatch(1000 units) = 23 ms
InitializeMatch(2000 units) = 103 ms

Четырёхкратный рост на каждое удвоение. Список уже отсортирован перед добавлением (id монотонно возрастают в InitializeMatch), так что сортировка каждый раз переупорядочивает упорядоченное. Лямбда не захватывает переменных, поэтому компилятор её кэширует — GC-претензии здесь нет, только CPU.

Remediation. Бинарная вставка (список инвариантно отсортирован)

private void InsertOrdered(UnitRecord record)
{
    var key = record.Combat.Entity.Value;
    var low = 0;
    var high = _orderedUnits.Count;
    while (low > 1);
        if (_orderedUnits[mid].Combat.Entity.Value  2, в Team Blue.

Измерено.
SpawnUnit(PlayerId 7) -> 1; PlayerId.IsValid=True; MaxPlayers=10; MatchTopology2v2.SlotCount=4
     GetTeamForPlayer(7)=Team1, AreEnemies(P1,P7)=True
REPRODUCED: the sim accepts player slots 5..10 that the 2v2 topology does not define
     TryEnqueueStop from PlayerId 7 -> None
REPRODUCED: a command from an out-of-topology player is accepted and executed

Справедливая оговорка: сессионный путь закрыт правильно — SessionManager.ValidateCommand (SessionManager.cs:627-657) проверяет record.Player != header.Player → PlayerMismatch, состояние Connected, наличие слота в матче и MatchPhase.Running. Дыра — в публичном несессионном API (LocalMatchHost.TryEnqueueMove/Attack/Stop), который существует и documented.

Remediation. Сервер знает свой ростер участников

private readonly HashSet _rosterPlayers = new HashSet();

public void BindRoster(ReadOnlySpan players)   // вызывается из InitializeMatch/TryStartMatch
{
    _rosterPlayers.Clear();
    foreach (var player in players) { _rosterPlayers.Add(player); }
}

private MatchCommandRejection ValidateCommonHeader(CommandHeader header, GameCommandType expectedType)
{
    ...
    if (_rosterPlayers.Count > 0 && !_rosterPlayers.Contains(header.Player))
    {
        return MatchCommandRejection.InvalidPlayer;      // новый член перечисления
    }
    ...
}
И сузить PlayerId.IsValid до фактической топологии либо передать её в MatchConfig.

---

P2-5. GetAllSnapshots() помечен [Obsolete], аллоцирует, но остаётся документированным путём потребления после тика
MatchServer.cs:564-574, :484 (XML-doc TickOnce указывает на него), LocalMatchHost.cs:125 и :565, потребитель — PrototypeRtsController.cs:384 → :415-426

Измерено:
10 x GetAllSnapshots() (the documented post-tick path) -> 112240 bytes (11224 B/call for 200 units)

11 224 Б на вызов при 200 юнитах (200 × 56 + 24). Вызывается из OnHostTickCompleted, т.е. каждый авторитетный тик: ~224 КБ/с на 20 Гц при 200 юнитах, ~450 КБ/с при 400. Линейно по ростеру. Продакшн-срез (TacticalVerticalSliceRunner, ServerReplicationEmitter.cs:492) корректно использует CopySnapshots(Span<>) — аллоцирует только legacy-прототип, но документация ядра (MatchServer.cs:484, LocalMatchHost.cs:125) по-прежнему направляет презентацию в устаревший метод, а LocalMatchHost.cs:564 подавляет предупреждение #pragma warning disable CS0618 вместо миграции.

Remediation. Перевести PrototypeRtsController.ApplyHostSnapshots на предаллоцированный буфер и CopySnapshots, обновить оба XML-doc на CopySnapshots, снять #pragma в LocalMatchHost и делегировать через буфер вызывающего

private readonly ServerUnitSnapshot[] _snapshotScratch =
    new ServerUnitSnapshot[SimulationConstants.MaxUnitsPerMatch];

private void ApplyHostSnapshots()
{
    var count = _localHost.Server.CopySnapshots(_snapshotScratch);
    if (count 
    _server.SpawnUnitWithEntity(entity, owner, stats, position, speedMmPerTick, autoAcquire);
                                                            // параметр unitKind не проброшен

Серверная перегрузка принимает byte unitKind = UnitKinds.Unknown (MatchServer.cs:296). Обёртка хоста его не имеет, поэтому каждый юнит, созданный через хост, реплицируется как Unknown — а это, по документации OD-29 (Identifiers.cs:86-96), заставляет клиентскую презентацию уходить в fallback «статы не определены»: нет ни меша архетипа, ни максимального здоровья для полос HP, ни радиуса кольца.

Измерено.
UnitKind through LocalMatchHost.SpawnUnitWithEntity = 0; through MatchServer.SpawnUnitWithEntity = 2
REPRODUCED: the host wrapper cannot carry an archetype

Remediation.
public EntityId SpawnUnitWithEntity(
    EntityId entity, PlayerId owner, CombatStats stats, WorldPointMm position,
    int speedMmPerTick, bool autoAcquire = false, byte unitKind = UnitKinds.Unknown) =>
    _server.SpawnUnitWithEntity(
        entity, owner, stats, position, speedMmPerTick, autoAcquire, unitKind);
Изменение аддитивно (новый параметр со значением по умолчанию), существующие вызовы не ломаются.

---

P2-7. CommandWireCodec.Encode без защит: счётчик сущностей усекается до байта, ёмкость буфера не проверяется
Assets/_GlobalFront/Runtime/Core/CommandWireCodec.cs:236-245, :78-110

private static int WriteEntityList(byte[] buffer, int offset, EntityId[] entities)
{
    buffer[offset++] = (byte)entities.Length;      // 238 — усечение без проверки
    for (var index = 0; index  SimulationConstants.MaxSelectedEntities)
        throw new ArgumentOutOfRangeException(nameof(entities),
            $"Wire entity lists carry 1..{SimulationConstants.MaxSelectedEntities} entries, got {entities.Length}.");
    if (buffer.Length - offset  _tick;      // 65 — без лока, в отличие от Mode/IsPaused/BacklogSeconds
...
lock (_gate) { scheduled = _clock.Advance(elapsedSeconds); _tick = _clock.Tick; }   // 186-195

for (var index = 1; index  0), но контракт публичного статика молча зависит от чужой валидации. Добавить if (count (); и columns = Math.Max(1, ...).
P3-2  CommandWireCodec.cs:128  var end = offset + length; — переполнение int на вызывающих больших offset/length; end > data.Length после завёртывания ложно, далее IndexOutOfRangeException. Считать в long.
P3-3  MatchServer.cs:179, :926, :211-215  Outcome write-once, Reset/Clear нет, InitializeMatch бросает при непустом сервере → экземпляр одноразовый. Не документировано.
P3-4  MatchServer.cs:484, :560-563  XML-doc направляет читателя на [Obsolete] GetAllSnapshots (см. P2-5).
P3-5  LocalMatchHost.cs:179  IsActive => _server != null — поле readonly и null-checked в конструкторе (:100), свойство всегда true. Мёртвый API.
P3-6  LocalMatchHost.cs:305-312  [Obsolete(..., false)] OnPlayerDisconnected остаётся публичным; второй источник паузы при заявленном единственном.
P3-7  LocalMatchHost.cs:408, :409-417  TickCompleted += emitter.OnTickCompleted и transport.SessionDetached += ... никогда не отписываются, Replication неотсоединим. Утечка в пределах жизни хоста.
P3-8  MatchServer.cs:630-634, Identifiers.cs:48  Sequence — uint, отказ по <=. После завёртывания игрок блокируется навсегда. Недостижимо практически (4.29e9 команд ≈ 6.8 лет на 20 Гц), но reconnect-путь (:539-546) продолжает нумерацию «строго выше» — зафиксировать это как инвариант.
P3-9  CombatSimulation.cs:142-157  SynchronizeFromAuthoritative пишет CurrentHealth без клампа в [0, MaximumHealth]; повреждённый снапшот делает юнита «мёртвым» на клиенте.
P3-10  MatchServer.cs:500-503  При терминальном Outcome записи _scheduledCommands не дренируются (частично закрыто в remediation к P1-6).
P3-11  FixedStepClock.cs:61, :70  Магические эпсилон-допуски 1e-12 и -1e-9 без названия и обоснования.
P3-12  TickDriver.cs:222  AdvanceManualTick вызывает _clock.Reset(_tick), выбрасывая накопленный backlog; смешение режимов молча теряет очередь времени.
P3-13  MatchServer.cs:309-312  _nextEntityValue = entity.Value + 1 при entity.Value == ulong.MaxValue завёртывается в 0 → следующий спавн получит невалидный id (бросит CombatantState). Недостижимо практически, guard стоит копейки.
P3-14  CommandHeader.cs  Комментарий разметки CommandWireCodec.cs:53-54 даёт перекрывающиеся диапазоны [3..7] и [7..15]; фактическая раскладка 1+1+1+4+8 = 15 верна.

---

Проверено и чисто (не находки)

Отдельно фиксирую, что атаковал и не смог сломать — иначе отчёт недостоверен

Правило 1 (zero float in state) — соблюдено. float|double|decimal в Core/ встречается только в SimulationConstants.cs:10 (ServerTickDurationSeconds) и FixedStepClock.cs:11,13,35-46 (пейсинг); в Server/ — только TickDriver.cs:113,116,183, RateLimiter.cs:14-15, VirtualNetworkPipe.cs:87-89. Все четыре типа явно перечислены в ArchitectureInvariantsTests.WallClockExemptions:61-67 как wall-clock bookkeeping. В MatchServer.cs — ноль double. В авторитетном состоянии дробных нет.
Правило 5 (изоляция от Unity) — соблюдено, доказано компиляцией. grep -l UnityEngine по всему Server/ — пустой результат. Всё дерево Core/ + Server/ + LocalMatchHost.cs + Client/Replication/ собралось как net8.0 console app: 0 ошибок. Единственное касание Unity во всём замыкании — один UnityEngine.Debug.LogWarning в ClientReplicationReceiver.cs:415. Никаких MonoBehaviour, Vector3, Mathf, Time.deltaTime в ядре. Headless-демон возможен уже сейчас.
Правило 3 (канонический порядок) — соблюдено. _orderedUnits держится отсортированным по EntityId; все фазы обходят его по индексу (:775,781,834,863,876,916); приказы сортируются по (Player, Sequence) (:682, :930-938); списки сущностей канонизируются сортировкой во всех трёх командах (MoveCommand.cs:103-105, AttackCommand.cs:93-95, StopCommand.cs:79-81, MatchServer.cs:453-455). Ни одного foreach по Dictionary/HashSet в пути симуляции — единственные такие обходы в Server находятся в SessionManager.cs:675,789,811 и транспортных керьерах, вне тика.
Правило 2 (целочисленная арифметика) — в ядре выполнена добросовестно. PlanarMovement: квадрат расстояния в ulong с checked (:35), целочисленный корень (:70-97) с ceil-поправкой (:43-46), деление на ноль невозможно — ранний выход :38-41 исключает distanceCeiling == 0, включая случай «юнит уже стоит в точке»; anti-stall nudge 1 мм (:50-60); checked(current.X + (int)stepX) (:63). Результат всегда лежит на отрезке между двумя в-границах точками, так что границы не нарушаются. CombatMath.SquaredDistance насыщается вместо переполнения и в безопасную сторону (:330-332). Накопление урона в long с клампом long.MaxValue (:449-455), приведение (int)damage на :196 доказуемо в диапазоне. CommitShot охраняет переполнение tick + CooldownTicks (:172-177). Оффсеты формации в long с checked((int)) (FormationLayout.cs:41,46,109).
Границы мира на пути приказа защищены. Сценарий «приказ в край карты» я сконструировал и прогнал: FormationLayout действительно выдаёт 2 из 4 слотов за пределами MaxWorldCoordinateMm при destination.X = 1e9, spacing = 100000, но TryEnqueueMove:387-393 их отсеивает — MatchCommandRejection.FormationOutOfBounds. StepTowards плохой слот не видит. Гейт на месте.
Правило 4 (время/случайность) — чисто. DateTime, Stopwatch, Environment.TickCount, Guid.NewGuid, unseeded Random в Core/ и MatchServer.cs отсутствуют (grep). HashCode.Combine в GetHashCode ряда типов (CombatSimulation.cs:69,241,287, MatchConfig.cs:70, SnapshotPacketHeader.cs:42, модели снапшотов) в .NET рандомизирован на процесс, но ни один хеш-код не попадает ни в состояние симуляции, ни на провод: отпечаток состояния — рукописный FNV-1a (ServerReplicationEmitter.cs:675-719), зеркально повторённый на клиенте (ClientReplicationWorld.cs:457-499), с явным выравниванием семантики MoveTarget по обе стороны. Кроссплатформенной расходимости нет. Отдельно фиксирую как инвариант на будущее: GetHashCode() никогда не должен примешиваться в StateChecksum.
Установившийся тик действительно zero-GC. Измерено: 0 байт за 50 тиков на 200 юнитах, и 0 байт за 50 тиков после исполнения приказа. Резервуары (_startPositionsCache, _combatantInputsCache, статики resolver) прогреваютcя один раз. Единственная аллокация в тике — P1-5, в момент исполнения приказа.
Двухфазный бой корректен. Урон накапливается против состояния на начало тика и применяется отдельным проходом (:426-465) — одновременный обмен не зависит от порядка обхода. MoveUnits снимает позиции на начало тика (:774-779), поэтому все видят один и тот же снимок противника.

---

ВЕРДИКТ

[REJECT: DEFECTS FOUND]

2 × P0, 10 × P2, 6 × P1, 14 × P3.

Три вещи, которые я считаю критичными для Фазы 1.0 specifically, а не вообще

P1-3 — самое опасное. Два независимых авторитетных счётчика тика без инварианта между ними. Любое исключение в любом подписчике TickStarting/TickCompleted навсегда разводит часы и состояние, и каждый последующий снапшот уходит в сеть с меткой, которой состояние не имеет. Измерено: driver.Tick = 4, server.CurrentTick = 2. Клиент при этом видит green-поток и молча уходит в re-baseline. Именно тот класс десинхронизации, который в локстепе не диагностируется постфактум.
P1-1 + P1-2 — одна цепочка. Публичный API спавна без валидации, помноженный на отсутствие сдерживания отказов в тике, даёт не «ошибку в логе», а необратимо мёртвый сервер: CurrentTick растёт, состояние полу-применено, каждый следующий тик бросает то же.
P1-4 — архитектурный потолок. Глобальные статики в ядре блокируют заявленный в скоупе фазы путь к dedicated-серверу на несколько матчей и ломают изоляцию тестов. Существующий гейт из 12 кейсов это не ловит — в нём нет проверки на статическое состояние, и её стоит добавить как 13-й кейс.

Сильные стороны, которые аудиту не удалось поколебать: целочисленная математика ядра написана аккуратно и защищённо (пункты 1-6 выше), канонический порядок выдержан без единого итератора по хеш-коллекции, изоляция от Unity подтверждена компиляцией, а установившийся тик измерен в 0 байт. Фаза 1.0 как математическое ядро — зрелая; как отказоустойчивый авторитетный сервер — нет: почти все находки clustered вокруг одного отсутствующего принципа — «тик либо состоялся целиком, либо не состоялся вовсе, и счётчик всегда описывает состояние».

Что предлагаю дальше.* Могу: (a) записать отчёт в Artifacts/Audit-Phase1.0-Claude.md по конвенции репозитория; (b) сделать ремедиацию по приоритету P0 → P1 с bite-раном на каждый фикс (прогон каждого нового теста против откаченного кода, чтобы доказать, что оно кусается) и полной прогонкой EditMode-сьюты; (c) добавить в ArchitectureInvariantsTests 13-й гейт на не-readonly статические поля в AuthoritativeNamespaces и 14-й на паритет счётчиков тика. Харнес в Library/ScratchAuditP10/ уже работает и воспроизводит все 18 зондов — его можно превратить в постоянные EditMode-тесты. Скажите, с чего начинать; коммитить ничего не буду без явного запроса.

вот его отчет

---

