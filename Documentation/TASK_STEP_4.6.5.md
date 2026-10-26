# ТЗ на реализацию: Шаг 4.6.5 — Автономный синтетический плейтест и стенд визуальной верификации (Synthetic Playtest Runner, OD-43 / Milestone M1)

## 1. Инвариант и контекст шага
- **Базовый коммит:** `828c55d` (Шаг 2 оверлея закоммичен, 1353/1353 тестов зелёные: 1332 EditMode + 21 PlayMode, рабочее дерево чистое).
- **Среда выполнения:** Unity Editor через **Unity MCP** (PlayMode runner) / C# 9.0 / Unity `6000.6.2f1` / URP `17.6.0`.
- **Цель шага:**
  1. Обеспечить разработчику возможность в один запуск через `Unity MCP` (PlayMode suite) полностью автономно провести сквозной 400-юнитный плейтест в реальном времени с ботом, сетевыми ухудшениями, разрывом сокета, тактической паузой, авто-реконнектом и авто-возобновлением боя.
  2. Зафиксировать 5 сквозных визуальных доказательств (PNG 1080p) в `Artifacts/Playtest/`:
     - `01_spawn_and_fog.png` — спавн 400 юнитов, активный Fog of War скрывает базу врага на 80м.
     - `02_box_selection.png` — drag-box выделение 100 юнитов игрока, зелёные маркеры и статус в HUD.
     - `03_clash_combat.png` — столкновение армий в центре карты, убывание HP-баров, стрельба.
     - `04_network_loss_pause.png` — обрыв сети на 20-й с, тактическая пауза, оверлей с обратным отсчётом ("Тактическая пауза: Игрок 1 переподключается...").
     - `05_reconnected_and_resumed.png` — авто-реконнект, keyframe resync, авто-возобновление боя по C0 opcode 14.
  3. Сохранить UX-телеметрию производительности в `Artifacts/Playtest/playtest_metrics.json` (FPS, 1% low, GC.Alloc, RTT, сетевые пакеты).
  4. Предоставить точку входа для интерактивного плейтеста пользователем с живой клавиатуры и мыши (в `SampleScene` или через Editor-меню).

- **Архитектурные инварианты и границы («Что НЕ делать»):**
  1. *Zero-GC в steady state:* Сбор телеметрии кадров и сетевой цикл не должны порождать аллокаций в куче в устойчивом режиме боя.
  2. *Не ломать существующий PlayMode suite:* Все 20 существующих PlayMode-тестов и 1332 EditMode-тестов должны оставаться строго зелёными.
  3. *Реальный физический сокет:* Плейтест выполняется поверх `DedicatedServerRunner` и `LiteNetLibCarrier` на локальном UDP loopback (`127.0.0.1`).
  4. *Автономность:* Тест не должен требовать ручного вмешательства человека во время выполнения (все действия эмулируются бот-агентом).

---

## 2. Существующие файлы и точки интеграции

1. `Assets/_GlobalFront/Runtime/Client/Presentation/RemoteSliceSession.cs`
   - Перенос тикера в `Pump(nowMs)` (Single Owner): сессия сама отсчитывает countdown по `nowMs`, не требуя ручных внешних вызовов из runner или тестов.
2. `Assets/_GlobalFront/Runtime/Client/Presentation/TacticalVerticalSliceRunner.cs`
   - В `InitializeDedicatedClient` подключить `_networkHud.BindCoordinator(_remoteSession.ReconnectCoordinator)` и привязать `TacticalPauseOverlay`.
   - В `StepPresentationFrame` вызывать `_networkHud.AdvanceRealTime(deltaTimeSeconds)`.
3. `Assets/_GlobalFront/Runtime/Client/UI/TacticalPauseOverlay.cs`
   - Уже содержит визуализацию паузы и обратного отсчета через `OnGUI` и uGUI Text.
4. `Assets/_GlobalFront/Runtime/Client/GlobalFrontRuntimeBootstrap.cs`
   - Точка входа для запуска `ActivateDedicatedClient(host, port)`.

---

## 3. Спецификация изменений

### A. Runtime: Перенос тикера Countdown в `RemoteSliceSession.Pump` (Single Owner)
Перенести продвижение `ReconnectCoordinator.AdvanceCountdown` внутрь `RemoteSliceSession.Pump(long nowMs)`:
```csharp
if (ReconnectCoordinator.State == ClientReconnectState.Countdown)
{
    if (_lastCountdownPumpMs == 0)
    {
        _lastCountdownPumpMs = nowMs;
    }
    var elapsedSec = (float)(nowMs - _lastCountdownPumpMs) / 1000f;
    _lastCountdownPumpMs = nowMs;
    if (elapsedSec > 0f)
    {
        AdvanceReconnectCountdown(elapsedSec);
    }
}
else
{
    _lastCountdownPumpMs = 0;
}
```
*Эффект:* Сессия становится 100% автономной. Вызов из `TacticalVerticalSliceRunner.Update()` можно снять или оставить как passthrough/no-op, чтобы не было двойного тика. Любой headless harness или runner получает рабочий countdown из коробки.

---

### B. Runtime: Визуализация паузы в `TacticalVerticalSliceRunner.cs`
В методе `InitializeDedicatedClient(...)`:
1. После `_networkHud = new NetworkHudPresenter();` добавить:
```csharp
_networkHud.BindCoordinator(_remoteSession.ReconnectCoordinator);

var overlayGo = CreateWiredGameObject("Tactical Pause Overlay");
var pauseOverlay = overlayGo.AddComponent<TacticalPauseOverlay>();
_networkHud.BindView(pauseOverlay);
```
2. В методе `StepPresentationFrame(float deltaTimeSeconds, double unscaledTimeSeconds)`:
Перед или после обновления постоянного HUD добавить:
```csharp
_networkHud?.AdvanceRealTime(deltaTimeSeconds);
```

---

### C. Инфраструктура скриншотов: `PlaytestScreenshotCapture.cs`
Создать `Assets/_GlobalFront/Tests/PlayMode/PlaytestScreenshotCapture.cs` (в тестовой сборке PlayMode):
```csharp
using System.IO;
using UnityEngine;

namespace GlobalFront.Tests.PlayMode
{
    public static class PlaytestScreenshotCapture
    {
        private const string OutputDirectory = "Artifacts/Playtest";

        public static string CaptureCamera(Camera camera, string filename, int width = 1920, int height = 1080)
        {
            if (!Directory.Exists(OutputDirectory))
            {
                Directory.CreateDirectory(OutputDirectory);
            }

            var filePath = Path.Combine(OutputDirectory, filename);

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var prevTarget = camera.targetTexture;
            var prevActive = RenderTexture.active;

            try
            {
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();

                var pngBytes = texture.EncodeToPNG();
                File.WriteAllBytes(filePath, pngBytes);
                Object.Destroy(texture);
            }
            finally
            {
                camera.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                Object.Destroy(rt);
            }

            return filePath;
        }
    }
}
```

---

### D. PlayMode Тест: `SyntheticPlaytestPlayModeTests.cs`
Создать `Assets/_GlobalFront/Tests/PlayMode/SyntheticPlaytestPlayModeTests.cs`:
Тестовый метод `[UnityTest]` с таймаутом 120 с:
**`E2E_FullMatch_400Units_SyntheticPlaytest_WithVisualWitness`**:
1. **Инициализация:**
   - Поднять `DedicatedServerRunner` на свободном порту (`runner.Initialize(0)`).
   - Создать сессию игрока (Client 1) и сессию оппонента (Client 2).
   - Подключить клиентов, дождаться `Established` и старта матча на 2 игрока.
   - Инициализировать `TacticalVerticalSliceRunner` для Client 1 с `createVisuals: true` и `autoStartRealTime: true`.
   - Дождаться baseline keyframe (400 юнитов).
2. **Witness 1 (Спавн и туман):**
   - Убедиться, что камера направлена на базу Player 1.
   - Сделать скриншот: `PlaytestScreenshotCapture.CaptureCamera(runner.ViewportCamera, "01_spawn_and_fog.png")`.
   - Проверить существование файла (> 50 КБ).
3. **Witness 2 (Выделение рамкой 100 юнитов):**
   - Эмулировать выбор 100 юнитов через `runner.Selection.SelectAllOfPlayer(runner.LocalPlayerId)` или рамку `SelectBox`.
   - Зафиксировать `runner.Selection.SelectedCount == 100` (или 200).
   - Подождать 2 кадра для отрисовки зелёных колец.
   - Сделать скриншот: `PlaytestScreenshotCapture.CaptureCamera(runner.ViewportCamera, "02_box_selection.png")`.
4. **Witness 3 (Марш и бой):**
   - Отдать приказы марша и атаки к центру: `runner.IssueConvergenceMarch()` и `runner.IssueEngagementOrders()`.
   - Просимулировать 15-20 секунд реального времени (каждый кадр `yield return null`, замеряя время кадров и аллокации).
   - Проверить, что армии вступили в бой и здоровье юнитов уменьшилось (`health < maxHealth`).
   - Сделать скриншот: `PlaytestScreenshotCapture.CaptureCamera(runner.ViewportCamera, "03_clash_combat.png")`.
5. **Witness 4 (Обрыв сети и тактическая пауза):**
   - Спровоцировать обрыв соединения со стороны сервера: разорвать сокет Client 1 (`serverTransport.CloseClient(connectionId)`).
   - Дождаться перехода координатора Client 1 в `Reconnecting`.
   - Убедиться, что сервер встал на тактическую паузу (`host.IsPaused == true`).
   - Подождать 2 кадра для отрисовки оверлея паузы.
   - Сделать скриншот: `PlaytestScreenshotCapture.CaptureCamera(runner.ViewportCamera, "04_network_loss_pause.png")`.
6. **Witness 5 (Авто-реконнект, тикер Countdown и возобновление):**
   - `AutoReconnect` клиента переподключает сокет, сервер принимает `ReconnectRequest`, отдает keyframe.
   - Клиент переходит в `Countdown`.
   - **ВНИМАНИЕ:** Дождаться, пока рантайм-тикер в `Pump` естественным образом отсчитает 5 секунд и отправит `ClientReadyToResume` (НЕ вызывать `AdvanceCountdown` вручную в тесте!).
   - Убедиться, что сервер снял паузу (`host.IsPaused == false`).
   - Убедиться, что тики возобновились (`CurrentTick` растет).
   - Сделать скриншот: `PlaytestScreenshotCapture.CaptureCamera(runner.ViewportCamera, "05_reconnected_and_resumed.png")`.
7. **Телеметрия:**
   - Рассчитать средний FPS, 1% low, steady-state GC.Alloc за кадр.
   - Записать JSON в `Artifacts/Playtest/playtest_metrics.json`:
     ```json
     {
       "timestamp": "...",
       "totalDurationSeconds": ...,
       "unitCount": 400,
       "averageFps": ...,
       "onePercentLowFps": ...,
       "steadyStateGcAllocBytesPerFrame": 0,
       "averageRttMs": ...,
       "reconnectDurationSeconds": ...,
       "verifiedScreenshots": [
         "01_spawn_and_fog.png",
         "02_box_selection.png",
         "03_clash_combat.png",
         "04_network_loss_pause.png",
         "05_reconnected_and_resumed.png"
       ]
     }
     ```

---

### E. Интерактивная точка входа для человека в Editor
Добавить в `Assets/_GlobalFront/Runtime/Client/InteractivePlaytestBootstrap.cs` компонент (или команду в меню Editor `[MenuItem("GlobalFront/Playtest/Launch Local Dedicated Match")]`):
- Запускает локальный `DedicatedServerRunner` в фоне на порту 7777.
- Запускает фонового бота для Player 2.
- Вызывает `GlobalFrontRuntimeBootstrap.ActivateDedicatedClient("127.0.0.1", 7777)`.
- По нажатию клавиши `[F9]` или `[P]` вызывает `_dedicatedSession.Carrier.CloseConnection(...)` (или дроп сокета), чтобы человек мог лично своими глазами увидеть паузу, оверлей и авто-реконнект прямо во время игры!

---

## 4. Обязательный состязательный Bite-прогон (Defect Injection)
1. **Мутация:** В `RemoteSliceSession.Pump(long nowMs)` закомментировать тикер обратного отсчета.
2. **Ожидаемый отказ:** Тест `E2E_FullMatch_400Units_SyntheticPlaytest_WithVisualWitness` падает по таймауту на этапе ожидания авто-возобновления боя:
   `The server must automatically unpause after 5s countdown: Expected: False But was: True` (сервер остаётся на паузе навсегда).
3. Сохранить артефакт падения: `Artifacts/TestResults/step465-bite1-countdown-ticker.xml`.
4. Восстановить рабочий тикер и прогнать чистый гейт.

---

## 5. Критерии приёмки и гейты (через Unity MCP)
| Гейт | Требование | Артефакт |
| --- | --- | --- |
| **Bite Defect Gate** | Закомментированный тикер валит авто-резюм по таймауту | `Artifacts/TestResults/step465-bite1-countdown-ticker.xml` |
| **Focused PlayMode** | `SyntheticPlaytestPlayModeTests` проходит 1/1 чисто | `Artifacts/TestResults/step465-focused-playmode.xml` |
| **Full PlayMode Suite** | Все 21 PlayMode тестов зеленые (100%) | `Artifacts/TestResults/step465-playmode-final.xml` |
| **Full EditMode Suite** | Все 1332 EditMode тестов зеленые (100%) | `Artifacts/TestResults/step465-editmode-final.xml` |
| **Visual Artifacts** | Ровно 5 PNG-файлов > 50 КБ в папке | `Artifacts/Playtest/01..05_*.png` |
| **UX Telemetry** | Метрики с 0 B GC.Alloc и валидным FPS | `Artifacts/Playtest/playtest_metrics.json` |
| **Git Working Tree** | Только изменения Шага 4.6.5, без утечек `SENTIS_ANALYTICS_ENABLED` | `git status` clean |
