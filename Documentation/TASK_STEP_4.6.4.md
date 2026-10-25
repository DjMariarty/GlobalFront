# ТЗ на реализацию: Шаг 4.6.4 — Live UDP PlayMode Harness with LiteNetLib Simulation (OD-43)

## 1. Инвариант и контекст шага
- **Базовый коммит:** `4d15ace` (Шаг 4.6.3 закоммичен, дерево чистое).
- **Среда выполнения:** Unity Editor через **Unity MCP** (PlayMode runner).
- **Цель шага:** Доказать жизнеспособность (liveness) клиент-серверного сетевого контура поверх реальных UDP-сокетов операционной системы через `LiteNetLibCarrier` под аппаратной симуляцией сетевых потерь (5%) и задержек (60–80 мс), а также успешный live-реконнект после принудительного разрыва сокета.
- **Архитектурные инварианты и границы («Что НЕ делать»):**
  1. *Liveness over timing:* В PlayMode под реальными сокетами и асинхронными потоками LiteNetLib недопустимы хрупкие потиковые микро-дедлайны. Критерии приёмки — исключительно liveness (успешный handshake, назначение PlayerId, подтверждение команды сервером, дельта-движение и восстановление сессии при реконнекте) с общими таймаутами (15–25 с).
  2. *Не требовать детерминированного чексумма:* Не требовать `ChecksumMismatchCount == 0` на живом сокете (математический паритет без десинков уже исчерпывающе доказан в Шаге 4.6.3 на детерминированном стенде).
  3. *Не менять FoW:* Спецификацию тумана войны не трогать (остаётся включённой на dedicated и подключённой/выключенной на slice).
  4. *Witness реальных потерь:* Обязательно подтвердить, что симуляция потерь физически активна на сокете во время теста.
  5. *Защита от флаков (3x Clean Run):* Live PlayMode-тест под потерями должен быть запущен и пройти 3 раза подряд для подтверждения устойчивости к случайным таймингам ОС.

---

## 2. Существующие файлы и точки интеграции
- `Assets/_GlobalFront/Runtime/Server/Transport/LiteNetLibCarrier.cs`
  - Инкапсулирует LiteNetLib `NetManager _manager`.
  - В LiteNetLib 1.3.5 доступны поля: `SimulatePacketLoss` (bool), `SimulationPacketLossChance` (int 1..100), `SimulateLatency` (bool), `SimulationMinLatency` (int ms), `SimulationMaxLatency` (int ms).
- `Assets/_GlobalFront/Runtime/Client/Presentation/RemoteSliceSession.cs`
  - Предоставляет свойство `Carrier` (`INetworkCarrier`, по умолчанию экземпляр `LiteNetLibCarrier`).
  - Содержит `AutoReconnect = true`, `ReconnectRetryIntervalMs = 500`, методы `TryReconnect(nowMs)` и координатор `ReconnectCoordinator`.
- `Assets/_GlobalFront/Tests/PlayMode/NetworkedClientPlayModeTests.cs`
  - Базовый PlayMode-тест клиент-серверного взаимодействия через реальный loopback UDP (`127.0.0.1`).

---

## 3. Спецификация изменений

### A. Runtime: `Assets/_GlobalFront/Runtime/Server/Transport/LiteNetLibCarrier.cs`
Добавить публичный API настройки симуляции ухудшений (по умолчанию выключен, продакшн не затрагивает):

```csharp
/// <summary>
/// Configures hardware-level packet loss and latency simulation on the underlying LiteNetLib manager.
/// </summary>
public void ConfigureSimulation(
    bool packetLoss,
    int lossChancePercent,
    bool latency,
    int minLatencyMs,
    int maxLatencyMs)
{
    if (_manager == null) return;
    _manager.SimulatePacketLoss = packetLoss;
    _manager.SimulationPacketLossChance = Math.Max(0, Math.Min(100, lossChancePercent));
    _manager.SimulateLatency = latency;
    _manager.SimulationMinLatency = minLatencyMs;
    _manager.SimulationMaxLatency = maxLatencyMs;
}

/// <summary>Whether packet loss simulation is currently active on this carrier.</summary>
public bool IsSimulatingPacketLoss => _manager?.SimulatePacketLoss ?? false;
```

---

### B. Tests: `Assets/_GlobalFront/Tests/PlayMode/NetworkImpairmentPlayModeTests.cs` (Новый файл)
Создать тестовую фикстуру `NetworkImpairmentPlayModeTests` в пространстве имен `GlobalFront.Tests.PlayMode`:

1. **`LiveUDP_UnderPacketLossAndLatency_HandshakeAndCommandSucceed`** (`[UnityTest]`):
   - Инициализировать `DedicatedServerRunner` на свободном порту (`Initialize(0)`).
   - Создать два клиентских `RemoteSliceSession` поверх `LiteNetLibCarrier`.
   - Включить симуляцию ухудшений на клиентах и сервере:
     `carrier.ConfigureSimulation(packetLoss: true, lossChancePercent: 5, latency: true, minLatencyMs: 20, maxLatencyMs: 60);`
   - `Assert.That(carrier.IsSimulatingPacketLoss, Is.True);` (witness активности симулятора).
   - Подключить клиентов к серверу `127.0.0.1:port`.
   - Дождаться успешного handshake (`sessionA.IsConnected && sessionB.IsConnected`) в пределах таймаута 25 с.
   - Запустить матч на 2 игрока (`TryStartSessionMatch`).
   - Дождаться установки baseline keyframe на обоих клиентах (`World.LiveCount == 2` и `State == Streaming`).
   - Отправить команду перемещения от клиента A.
   - Дождаться подтверждения команды от сервера (`acknowledgements.Count > 0` и `IsAccepted == true`).
   - Сделать несколько ручных тиков хоста (`_server.Host.TickOnce()`) и убедиться, что дельта пришла и координаты юнита обновились.
   - Ни одного перехода в `ConnectionFailed` за время сценария.

2. **`LiveUDP_UnderLoss_SocketDisconnectAndReconnect_RecoversSession`** (`[UnityTest]`):
   - Установить живую UDP-сессию между выделенным сервером и клиентом под симуляцией 3–5% потерь.
   - Дождаться baseline keyframe и зафиксировать `entityId` юнита.
   - Сымитировать обрыв: разорвать соединение со стороны сокета (например, `session.Carrier.CloseConnection(...)`).
   - Дождаться, пока клиентский `ReconnectCoordinator` перейдёт в состояние `Reconnecting`.
   - Проверить срабатывание автоматической политики ретраев (`session.AutoReconnect` / `session.Pump(nowMs)`) с открытием нового заменяющего сокета.
   - Дождаться успешного ответа сервера (`ReconnectResponse`), установки ключевого кадра, перехода в `Streaming` / `Countdown` / `Established`.
   - Убедиться, что мир на клиенте содержит актуальные сущности, а сессия полностью восстановила управляемость.

3. **`TearDown` Очистка:**
   - Гарантированный `Dispose()` всех сессий и `Shutdown()` сервера с уничтожением созданных `GameObject` во избежание утечек портов в Unity Editor.

---

## 4. Обязательный состязательный Bite-прогон (Defect Injection)
1. **Мутация:** в `LiteNetLibCarrier.ConfigureSimulation` временно сделать метод no-op (закомментировать присвоение полей `_manager`).
2. **Ожидаемый отказ:** тест падает на проверке свидетеля симуляции:
   `Assert.That(carrier.IsSimulatingPacketLoss, Is.True)` → `Expected: True But was: False`.
3. Сохранить артефакт падения: `Artifacts/TestResults/step464-bite1-live-simulation.xml`.
4. Восстановить рабочий код и прогнать чистые гейты.

---

## 5. Критерии приёмки и гейты (через Unity MCP)
| Гейт | Требование | Артефакт |
|---|---|---|
| **Focused PlayMode** | 2/2 passed | `Artifacts/TestResults/step464-focused-playmode.xml` |
| **Stability Gate** | 3x повторный запуск focused PlayMode без сбоев | 3/3 зелёных прогона |
| **Bite Defect** | Authentic failure свидетеля симуляции | `Artifacts/TestResults/step464-bite1-live-simulation.xml` |
| **Full PlayMode** | 19/19 passed (17 baseline + 2 new) | `Artifacts/TestResults/step464-playmode-final.xml` |
| **Full EditMode** | 1319/1319 passed (без регрессий) | `Artifacts/TestResults/step464-editmode-final.xml` |
| **Zero Errors** | 0 проектных ошибок/предупреждений | Unity Console |
| **Документация** | Обновление `CURRENT_STATE.md` (секция 4.6.4) | `git diff Documentation/CURRENT_STATE.md` |
