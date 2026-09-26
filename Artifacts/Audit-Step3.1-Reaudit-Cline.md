# Re-Audit (враждебный): Phase 3, Step 3.1 — ремедиация P1-1 / P2-1..P2-6

> **Auditor:** Claude (Cline), ведущий системный аудитор GlobalFront
> **Target:** `RtsCameraController.cs`, `RtsInputManager.cs`, `RtsCameraTests.cs` (working tree, ремедиация не закоммичена)
> **Baseline (исходный аудит):** 0 P0, 1 P1, 6 P2, 6 Info — `Artifacts/Audit-Step3.1-Claude-Cline.md`
> **Baseline (код):** 719/719 EditMode (коммит `0243971`, Unity 6000.6.2f1)
> **Unity:** `6000.6.2f1`, batchmode, `-runTests -testPlatform EditMode`
> **Дата:** 2026-09-24

## 1. Метод верификации

1. Построчное чтение диффа всех трёх файлов (`git diff`) → подтверждение, что ни один существующий
   ассерт не ослаблен и ни одно продуктовое поведение не удалено (`--numstat`: +152/−14, +83/−10, +162/−20).
2. **Независимый повторный прогон** полного набора: `Artifacts/TestResults/EditMode-reaudit31-verify.xml`
   → **723/723 passed, 0 failed, 0 skipped, 0 inconclusive, 5.37 с** (артефакт разработчика заявлен как 5.81 с — расхождение только по времени).
   Все 8 тестов `RtsCameraTests` (4 базовых + 4 новых) — `Passed`.
3. **8 собственных adversarial-проб** (временный файл, удалён после прогона):
   `Artifacts/TestResults/EditMode-reaudit31-probes.xml` → 731 тест = 723 база + 8 проб → **730 passed / 1 failed**.

## 2. Таблица закрытия дефектов

| Defect | Статус | Доказательство |
|---|---|---|
| **P1-1** (деление на `tan(pitch)` = 0) | **ЗАКРЫТ** | Сеттеры `MinPitch`/`MaxPitch` зажаты в `[10°, 85°]`; `ApplyTransform` считает границы через локальные переменные и финально зажимает `_currentPitch`; `Mathf.Max(MinTanPitch=0.01f, tan)`; `heightFraction` защищён от 0/0. Тест `Camera_ZeroPitch_DoesNotProduceNaN` + мой прогон: NaN/Inf отсутствуют, `position.y > 0`. Мой Probe03b (конечный инвертированный диапазон 5°..7° через reflection) — PASS. Исходный репро (публичные сеттеры) больше не воспроизводится |
| **P2-1** (NaN-отравление состояния) | **ЗАКРЫТ** | `IsFinite` в `SetFocusPoint`/`SetHeight`/`SetYaw` + гарды на накопление в `ProcessInput`/`ClampToBounds`. Тест `Camera_NaNInput_IsRejected` (NaN и ±Infinity ×3 сеттера). В артефакте ровно 5 ожидаемых warning'ов «ignored a non-finite …» — совпадает с тестом |
| **P2-3(b)(c)** (`GroundPlane`, двойной поиск камеры) | **ЗАКРЫТ** | `private static readonly Plane GroundPlane`; `_cachedActiveCamera` + `_cameraSearchWarningLogged`; `SetTargetCamera` инвалидирует кэш. Probe01 (2000 кадров, pan+zoom+rotation) — 0 байт GC |
| **P2-4** (`isMockMode` сериализован) | **ЗАКРЫТ** | `[NonSerialized] private bool isMockMode`, `[Header]`/`[SerializeField]` сняты. Бонус: `using System;` в этом файле теперь реально используется |
| **P2-5** (Alt без кнопки мыши) | **ЗАКРЫТ** | `IsRotationCombination = MMB \|\| (Alt && RMB)`; hardware-путь вызывает ту же функцию; тест на 5 комбинаций жестов. Обоснование «чистой функции» корректно: EditMode-asmdef действительно не ссылается на `Unity.InputSystem` (проверено в `.asmdef`) |
| **P2-6** (гигиена тестов) | **ЗАКРЫТ** | Дубль `_createdObjects.Add(rayTestObj)` удалён; `RenderTexture` в `try/finally` с `Release()`; +4 теста (723 = 719 + 4 воспроизведено) |
| **P2-2** (расхождение модели зума mock/hardware; зум не масштабирован на `deltaTime`) | **НЕ ЗАКРЫТ** | `_mockZoomInput = zoomInput;` (без нормализации) против `Sign(scroll)` в hardware. Probe04: mock=3 → **24 м за кадр** против 8 м в hardware. Probe05: dt=1.0 и dt=0.016 дают одинаковые 8 м → зум игнорирует `deltaTime` |
| **P2-3(a)** (семантика промаха `MouseWorldPosition`) | **НЕ ЗАКРЫТ** | Геттер по-прежнему возвращает `Vector3.zero` при промахе (Probe06: `TryGet=False`, `out=(0,0,0)`, геттер `(0,0,0)`) — промах неотличим от попадания в начало координат; XML-doc свойства не дополнен |
| **P2-6** (пробелы покрытия) | **ЧАСТИЧНО** | Zero-GC-тест покрывает только **холостой** путь (в `SetUp` мок-входы не заданы) — Probe01 с активными входами подтверждает 0 байт, т.е. дефекта нет, но тест не поймает будущую аллокацию в активной ветке. Тестов edge pan и сброса yaw (`Home`/`` ` ``) нет |


---

## 3. Реестр остаточных замечаний

### R-1 (P2, не блокирует) — модель зума: расхождение mock/hardware и отсутствие `deltaTime`
Файлы: `RtsInputManager.cs:163` (`Sign(scroll)`), `RtsInputManager.cs:320–323` (`_mockZoomInput = zoomInput;`),
`RtsCameraController.cs:225` (`height -= zoom * zoomStep`).
Доказательство: Probe04 (24 м против 8 м за кадр), Probe05 (dt не влияет).
Исправление: нормализовать `SetMockZoomInput` к `[-1, 1]` **либо** отказаться от `Sign` в обоих путях;
явно зафиксировать решение по времени (дискретный шаг ±1 без `deltaTime` допустим — но одинаково в обоих путях).

### R-2 (P2, не блокирует) — семантика промаха `MouseWorldPosition` не документирована
Файл: `RtsInputManager.cs:88–98`. Промах = `Vector3.zero`. Исправление: дописать в XML-doc
«returns `Vector3.zero` when the ray misses the ground plane; use `TryGetMouseWorldPosition` to tell the two apart».

### R-3 (P2, не блокирует) — покрытие тестов неполное
(a) `Camera_ManualUpdate_DoesNotAllocate` не задаёт мок-входы → проверяется только холостая ветка
(в оригинальном Probe01 участвовали pan+zoom+rotation+edge pan). (b) нет теста edge pan —
acceptably, так как в batchmode `Application.isFocused == False` и guard в `ProcessInput` подавляет edge pan
(Probe07 подтверждает); стоит зафиксировать это комментарием в тесте или вынести в PlayMode.
(c) нет теста сброса yaw (`ResetRotationRequested → _targetYaw = 0`).

### R-4 (P3/Info, новый) — «last line of defence» в `ApplyTransform` не NaN-плотен для NaN-границ питча
Файл: `RtsCameraController.cs:293–316`. Комментарий заявляет независимость от сеттеров, но
`Mathf.Clamp`/`Mathf.Max` **пропагируют NaN из границ**: при `minPitch = maxPitch = NaN`
(достижимо только правкой YAML сцены / повреждённым ассетом — Unity сериализует `NaN` как `NaN`)
`pitchFloor → NaN → _currentPitch → NaN → tanPitch → NaN → groundDist → NaN`, и присваивание
трансформа падает с **исходной ошибкой P1-1**: `transform.position assign attempt … { NaN, NaN, NaN }`
(мой Probe02 это воспроизводит). При этом путь высот NaN-безопасен (Probe03 PASS), а конечные
инвертированные диапазоны безопасны (Probe03b PASS).
Исправление (дёшево): санитизировать читаемые поля (`IsFinite(minPitch) ? minPitch : PitchFloorDegrees`
и т.д.) или отбросить нечисловой офсет (`if (!IsFinite(offset)) return;`).
Severity: низкая — вектор входа = повреждённый контент, из публичного API больше недостижим.

### R-5 (Info) — дрейф документации и двойное связывание RMB
`RtsCameraController.cs:12` по-прежнему обещает «MMB or Alt + mouse drag», тогда как реализация —
MMB или **Alt + ПКМ**; тест `InputManager_AltWithoutMouseButton_DoesNotRotate` называет Alt+RMB
«documented fallback gesture», чего в документации нет. Дополнительно: Alt+RMB теперь вращает камеру,
оставаясь одновременно командной кнопкой (`IsRightMouseButtonDown/Pressed/Up`) — стоит зафиксировать как осознанное решение.

### Info (без изменений, не дефекты)
`using System;` в `RtsCameraController.cs:1` не используется; `deltaTime` в `RtsInputManager.ManualUpdate`
не используется; `RtsInputManager` не `sealed`; нет `ScriptExecutionOrder`; при полностью отсутствующей
в сцене камере `FindAnyObjectByType` по-прежнему вызывается на каждый запрос (кэш остаётся `null`), но
предупреждение пишется ровно один раз.

---

## 4. Вердикт

**Блокирующий гейт закрыт: P1-1 ЗАКРЫТ и подтверждён независимо.** Заявленные 723/723 воспроизведены
(723/723, 0 failed, 5.37 с) на текущем дереве; артефакт разработчика согласован с кодом.
Закрыты также P2-1, P2-3(b)(c), P2-4, P2-5 и гигиеническая часть P2-6.

**Формулировка «группа P2 закрыта полностью» — неточна:** P2-2 не начат, P2-3(a) и покрытие P2-6 — частичны.
Все остаточные замечания (R-1..R-5) классифицируются как P2/P3 и **не блокируют** аппрув по правилам
исходного аудита (блокировал только P1-1).

**Итог: `[APPROVE: P1 GATE CLOSED]` — 5 остаточных неблокирующих замечаний (R-1..R-5).**
Строгий `[APPROVE: ZERO DEFECTS]` не выставляется, пока открыты R-1 и R-2 (не начатый P2-2 и
незадокументированная семантика промаха), и пока R-4 не закрыт дешёвым санитайзом полей.

*Артефакты этого аудита: `Artifacts/TestResults/EditMode-reaudit31-verify.xml` (723/723),
`Artifacts/TestResults/EditMode-reaudit31-probes.xml` (731: 730 passed / 1 failed = подтверждённый R-4),
`Logs/reaudit31-verify.log`, `Logs/reaudit31-probes.log`. Временный файл проб удалён, дерево восстановлено
(`git status`: 3 изменённых файла ремедиации + отчёты).*