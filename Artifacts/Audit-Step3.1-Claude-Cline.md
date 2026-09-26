# Независимый враждебный аудит: Phase 3, Step 3.1 (RTS Camera Controller & Input Layer)

> **Auditor:** Claude (Cline), независимый adversarial re-audit
> **Target Scope:** Phase 3, Step 3.1 — `RtsCameraController.cs`, `RtsInputManager.cs`, `RtsCameraTests.cs`
> **Baseline:** 719/719 EditMode passed (Unity 6000.6.2f1, batchmode), рабочее дерево чистое
> **Normative Standards:** ADR-012, OD-27, OD-28 (`Documentation/DECISIONS.md`, строки 386–418), `DEVELOPMENT_STANDARD.md`
> **Method:** построчное чтение → 13 временных adversarial-проб (EditMode) → batchmode-прогон → удаление проб → повторная верификация базы
> **Verification:** пробный прогон 732 теста (719 + 13 проб): 728 passed / 4 failed (3 ожидаемых падения = подтверждённые дефекты, 1 — ошибка дизайна самой пробы, отозвана); финальный прогон после удаления проб: **719/719 passed, 0 failed**

---

## 1. Верификация архитектурных инвариантов

### Инвариант 1: Граница Presentation / Simulation — PASS
Ни `RtsCameraController`, ни `RtsInputManager` не содержат ни одной ссылки на `GlobalFront.Core`, `GlobalFront.Server`, `ClientReplicationWorld`, `ICommandChannel`, `CommandHeader` (полный grep по `Runtime/Client`, 2026-09-24). Импорты обоих файлов: только `System` (не используется), `UnityEngine`, `UnityEngine.InputSystem`. Камера — чистый односторонний потребитель `RtsInputManager`, мутирует только собственный `transform`. Двойная защита от серверной сборки: `#if !UNITY_SERVER` в `Update()` (оба файла) + `defineConstraints: !UNITY_SERVER` в `GlobalFront.Client.asmdef`.

### Инвариант 2: Zero-GC в горячем цикле (OD-28) — PASS (доказано пробами)
- **Probe01:** `RtsCameraController.ManualUpdate` × 2000 итераций с одновременно активными pan + zoom + rotation + edge pan → **0 байт** (`GC.GetAllocatedBytesForCurrentThread`).
- **Probe02:** `TryGetMouseWorldPosition` × 2000 → **0 байт**.
- **Probe03:** `RtsInputManager.ManualUpdate` (hardware path, не-mock) × 2000 → **0 байт**.
- Статический скан подтверждает: нет `new` (кроме struct), замыканий, boxing, LINQ, `foreach`, строк в горячих путях. `Plane`, `Ray`, `Quaternion`, `Vector2/3` — структуры.

### Инвариант 3: Map clamp ±200 м (OD-27) и математическая безопасность — ЧАСТИЧНЫЙ ПРОВАЛ
- Clamp ±200: **Probe11 PASS** — точное зажатие `(1e6, 50, -1e6)` → `(200, 0, -200)`, Y принудительно в 0 (строка 280), 300 итераций диагонального пана не выходят за границы.
- Raycast: **Probe06/07 PASS** — `Plane.Raycast` сам защищает от параллельного луча (`d≈0` → `false`) и луча «от плоскости» (`enter<0` → `false`). Ручной `Approximately` не требуется — выбор API корректен.
- `Mathf.InverseLerp(min,max)` при `minHeight==maxHeight` — внутренний guard Unity возвращает 0 → безопасно.
- **P1 (строки 70–71 + 227):** деление на `Mathf.Tan(pitch)` без защиты — см. §2.
- **P2 (строки 240–269):** NaN принимается публичными сеттерами — см. §2.

### Инвариант 4: Screen-edge pan (фокус / курсор вне экрана) — PASS
- **Probe08 PASS:** в batchmode `Application.isFocused=False` → edge pan подавлен (guard строки 138 работает: потеря фокуса отключает пан).
- **Probe09 PASS:** курсор `(-500,-500)` и `(Screen.width+500, Screen.height+500)` → пан отсутствует (граничные проверки строк 141–145 корректны).
- Edge pan читает `inputManager.MousePosition` (строка 140) → мокабельность через `SetMockMousePosition` сохранена, абстракция не пробита.

### Инвариант 5: `SetMockMode(true)` — изоляция тестов — PASS
- **Probe13 (мок-часть) PASS:** mock-состояние переживает `ManualUpdate` (строки 112–115: ранний выход в mock-режиме), `ResetMockInputs()` обнуляет все 12 полей.
- Синглтон-ассерты Probe13 **отозваны**: в EditMode `Awake()` у MonoBehaviour без `[ExecuteAlways]` не вызывается — `Instance` остаётся `null`; это свойство тестового окружения, а не дефект продукта (именно поэтому `InitializeState()`/`ManualUpdate()`/`SetInputManager()` — публичные; дизайн корректен).

---

## 2. Реестр дефектов

### P1-1: Деление на ноль в `ApplyTransform` — `tan(pitch)` без защиты (доказано пробой)
**Файл:** `RtsCameraController.cs:227` (`groundDist = _currentHeight / Mathf.Tan(pitchRad)`), корень — сеттеры `MinPitch`/`MaxPitch` (строки 70–71) без валидации.
**Механизм:** `[Range(30,60)]`/`[Range(60,85)]` (строки 34–35) действуют только в инспекторе. Публичные сеттеры принимают любое значение: `MinPitch = 0` → `pitch = 0` → `tan(0) = 0` → `groundDist = h/0 = -Infinity` → `transform.position = {NaN, NaN, -Infinity}`.
**Доказательство (Probe04 FAILED):** Unity залогировал ошибку: `transform.position assign attempt for 'AuditProbe_Camera' is not valid. Input position is { NaN, NaN, -Infinity }.` — т.е. ещё и нарушение гейта «чистая консоль».
**Исправление:** валидировать сеттеры (`Mathf.Clamp(value, 1f, 89f)`) и/или защитить деление: `var tan = Mathf.Tan(pitchRad); groundDist = _currentHeight / Mathf.Max(0.0001f, Mathf.Abs(tan));`.

### P2-1: NaN отравление состояния через публичные API (доказано пробами)
**Файл:** `RtsCameraController.cs:240–243` (`SetFocusPoint`), `251–253` (`SetHeight`), `261–263` (`SetYaw`).
**Механизм:** `Mathf.Clamp(NaN, min, max)` возвращает NaN (оба сравнения ложны) → `FocusPoint`/`CurrentHeight`/`TargetYaw` становятся NaN навсегда; `Vector3.Lerp(x, NaN, t)` не восстанавливается. Сеттер `transform.position` Unity отклоняет NaN, поэтому камера молча «замораживается» на последней валидной позе — не восстанавливается без `InitializeState()`.
**Доказательство:** Probe05 FAILED (`FocusPoint=(NaN, 0, NaN)`), Probe12 FAILED (`CurrentHeight=NaN`).
**Исправление:** ранняя отбраковка: `if (!float.IsFinite(point.x) || !float.IsFinite(point.z)) return;` (аналогично height/yaw).

### P2-2: Расхождение модели зума mock vs hardware; потеря «схлопнутых» щелчков колеса
**Файлы:** `RtsInputManager.cs:146–147` (`Sign(scroll)` → ±1), `RtsInputManager.cs:247–250` (mock хранит сырую величину), `RtsCameraController.cs:162` (шаг зума не масштабирован на `deltaTime`).
**Механизм:** hardware-путь обрезает зум до ±1 на кадр (несколько щелчков колеса за один кадр теряются), mock-путь пропускает величину как есть (`SetMockZoomInput(3)` → −24 м за один кадр). Доказательство: Probe10 PASS (задокументировано расхождение). Пан при этом масштабируется на `deltaTime` (строка 154), зум — нет: модель времени несогласованна (для дискретного колеса допустимо, но mock/hardware семантика обязана совпадать).
**Исправление:** нормализовать mock к ±1 (как hardware) либо пропускать накопленный `scroll.y` без `Sign` в обоих путях.

### P2-3: `MouseWorldPosition` возвращает `Vector3.zero` при промахе; двойной поиск камеры
**Файл:** `RtsInputManager.cs:75–82`, `192–215`, `223–228`.
**Механизм:** (а) промах неотличим от валидного попадания в точку (0,0,0); вызывающий обязан использовать `TryGetMouseWorldPosition`, но публичный геттер это скрывает. (б) при `cam == null` `GetActiveCamera()` вызывается дважды (строки 79 и 197). (в) fallback `FindAnyObjectByType<Camera>()` (строка 227) — скан сцены на каждый вызов, если камера не сконфигурирована.
**Исправление:** документировать семантику промаха; кэшировать результат `GetActiveCamera()` в локальную переменную.

### P2-4: `isMockMode` сериализован в сцену — риск «мёртвого» ввода в билде
**Файл:** `RtsInputManager.cs:17–18` (`[SerializeField] private bool isMockMode`).
**Механизм:** если поле случайно включено и сохранено в сцене/префабе, весь hardware-ввод молча игнорируется (`ManualUpdate` выходит на строке 114). Без предупреждения в рантайме.
**Исправление:** убрать `[SerializeField]` (оставить только `SetMockMode`) либо логировать предупреждение при `isMockMode && Application.isPlaying` вне тестов.

### P2-5: Alt без кнопки мыши считается вращением
**Файл:** `RtsInputManager.cs:150–153` (`_isRotating = mmbPressed || altPressed`).
**Механизм:** документация класса обещает «MMB or Alt + mouse **drag**», но реализация включает вращение при одном зажатом Alt без какой-либо кнопки — любое горизонтальное движение мыши с Alt крутит камеру (в редакторе Alt+движение — ещё и конфликт с навигацией).
**Исправление:** `_isRotating = mmbPressed || (altPressed && mouse.leftButton.isPressed)` (или иное явное сочетание).

### P2-6: Пробелы покрытия и гигиена `RtsCameraTests`
**Файл:** `RtsCameraTests.cs`.
- Нет тестов Zero-GC, edge pan, вращения/сброса (`Home`/`` ` ``), NaN/div-by-zero — все четыре класса проб оказались реализуемы в EditMode (см. §3).
- Строки 178 и 182: дублирующий `_createdObjects.Add(rayTestObj)`.
- `RenderTexture` (строка 181) утекает при падении ассертов до строки 213 (нет try/finally).
**Исправление:** влить пробы (очищенные) в постоянный набор; убрать дубль; обернуть RT в try/finally.

### Info (не дефекты)
- `RtsCameraController.cs:1`, `RtsInputManager.cs:1` — неиспользуемый `using System;`.
- `RtsInputManager.cs:110` — параметр `deltaTime` в `ManualUpdate` не используется (hardware-сэмплирование покадровое).
- `RtsInputManager.cs:13` — класс не `sealed` (в отличие от `RtsCameraController`) — несогласованность стиля.
- `RtsCameraController.cs:78–91` — `Awake → InitializeState` молча создаёт `RtsInputManager` через `AddComponent`, если не найден: неявный сайд-эффект; приемлемо для bootstrap.
- Edge pan активен при `mousePos.x == Screen.width` ровно (1 px за пределами окна) — косметическая граница.
- Нет явного `ScriptExecutionOrder` между `RtsInputManager.Update` и `RtsCameraController.Update`: edge-флаги (`wasPressedThisFrame`) могут потребляться с задержкой в 1 кадр — для камеры безвредно.


---

## 3. Протокол adversarial-проб (13 проб, временный файл, удалён после прогона)

Прогон с пробами: `Artifacts/TestResults/EditMode-audit31-probes.xml` (732 теста: 719 база + 13 проб → 728 passed / 4 failed; 3 падения — подтверждённые дефекты, 1 — отозванная ошибка дизайна пробы). Финальная реверификация после удаления проб: `Artifacts/TestResults/EditMode-audit31-final.xml` — **719/719 passed, 0 failed**, компиляция чистая, дерево восстановлено.

| # | Проба | Цель | Результат | Интерпретация |
|---|-------|------|-----------|----------------|
| 01 | `ManualUpdate` ×2000, все входы активны | Zero-GC камеры | PASS | 0 байт |
| 02 | `TryGetMouseWorldPosition` ×2000 | Zero-GC raycast | PASS | 0 байт |
| 03 | hardware-read ×2000 (не-mock) | Zero-GC ввода | PASS | 0 байт |
| 04 | `MinPitch=MaxPitch=0` → `ManualUpdate` | div-by-zero в pitch | **FAIL** | **P1-1**: `{NaN, NaN, -Infinity}` + error-лог |
| 05 | `SetFocusPoint(NaN)` | NaN-валидация | **FAIL** | **P2-1**: `FocusPoint=(NaN,0,NaN)` |
| 06 | луч параллелен плоскости | безопасность raycast | PASS | `false`, out конечен |
| 07 | луч «от плоскости» (pitch −45°) | безопасность raycast | PASS | `false`, out = `Vector3.zero` |
| 08 | edge pan при потере фокуса | guard `isFocused` | PASS | batchmode `isFocused=False` → пан подавлен |
| 09 | курсор вне экрана (обе стороны) | граничные проверки | PASS | пан отсутствует |
| 10 | `SetMockZoomInput(3)` | расхождение mock/hw | PASS | задокументировано: −24 м/кадр (P2-2) |
| 11 | clamp карты ±200 | OD-27 | PASS | точное зажатие, Y=0 |
| 12 | `SetHeight(NaN)` | NaN-валидация | **FAIL** | **P2-1**: `CurrentHeight=NaN` |
| 13 | mock-изоляция + синглтон | изоляция тестов | ЧАСТИЧНО | mock-часть PASS; синглтон-ассерты отозваны (EditMode не вызывает `Awake` без `[ExecuteAlways]`) |

**Положительные находки (подтверждено чтением и пробами):** строгая граница Presentation/Simulation; Zero-GC во всех горячих путях; `Time.unscaledDeltaTime` (камера живёт при тактической паузе OD-18); экспоненциальное сглаживание `1-exp(-k·dt)` (frame-rate independent); `LerpAngle` для yaw; двойной `!UNITY_SERVER` guard; clamp и target, и current; корректный выбор `Plane.Raycast` (встроенный guard от параллельных лучей); полный mock-API с `ResetMockInputs`.

---

## 4. Вердикт

**НЕ `[APPROVE: ZERO DEFECTS]`.** Итог: **0 P0, 1 P1, 6 P2, 6 Info.**

**Обязательное исправление (блокирует аппрув):**
- **P1-1:** защитить `RtsCameraController.cs:227` от `tan(pitch)→0` (валидация сеттеров `MinPitch`/`MaxPitch` строк 70–71 и/или `Mathf.Max(eps, |tan|)` в `ApplyTransform`). После исправления — повторный gate-прогон (EditMode + чистая консоль).

**Рекомендованные исправления (P2, не блокируют, фиксируются в бэклог):** P2-1 (finite-валидация `SetFocusPoint`/`SetHeight`/`SetYaw`), P2-2 (унификация zoom mock/hardware), P2-3 (семантика промаха `MouseWorldPosition`), P2-4 (несериализуемый mock-флаг), P2-5 (Alt+drag по документации), P2-6 (покрытие: Zero-GC / edge pan / rotation / NaN тесты + гигиена `RtsCameraTests`).

*Аудит выполнен 2026-09-24, Unity 6000.6.2f1, batchmode (`-quit` не использовать с `-runTests` — гонка, отменяющая запуск TestRunner).*

