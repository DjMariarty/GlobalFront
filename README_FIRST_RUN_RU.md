# Первый запуск GlobalFront

> Unity: `6000.6.2f1` • URP: `17.6.0` • uGUI: `2.6.0` • текущая реализация: технический RTS-прототип и 400-юнитный тактический вертикальный срез

## Открытие проекта

1. В Unity Hub выберите каталог `GlobalFront`, в котором находятся `Assets`, `Packages` и `ProjectSettings`.
2. Дождитесь завершения импорта и компиляции (Unity `6000.6.2f1`).
3. Откройте сцену `Assets/Scenes/SampleScene.unity`.
4. Нажмите Play.
5. Для переключения на полномасштабный 400-юнитный тактический срез Phase 3 нажмите **F9** или кнопку переключения в HUD.

## Управление

- **ЛКМ по дружественному юниту** — выбрать юнит.
- **Рамка с зажатой ЛКМ** — прямоугольное выделение группы (transient-канвас, 0 аллокаций).
- **Shift + ЛКМ** — добавление/снятие юнита из текущей группы.
- **Двойной клик ЛКМ по юниту** — выбор всех видимых юнитов данного типа на экране.
- **ПКМ по земле** — приказ движения выбранной группе (с разворотом в строй).
- **ПКМ по живому вражескому юниту** — приказ атаки.
- **X или H** — команда Стоп (остановка отряда; клавиша `S` намеренно освобождена для панорамирования камеры).
- **WASD, стрелки или курсор у края окна** — перемещение RTS-камеры (`RtsCameraController`).
- **Колесо мыши** — плавный зум камеры.
- **Миникарта (слева внизу):**
  - **ЛКМ по миникарте** — мгновенное центрирование камеры на указанной точке мира.
  - **ПКМ по миникарте** — выдача приказа движения выбранному отряду в указанную точку карты.

## Реализованные ключевые подсистемы

- **Core & Server:** Детерминированная авторитетная симуляция 20 Hz (`GlobalFront.Core`, `GlobalFront.Server`), `MatchServer`, `TickDriver` (ADR-007, потокобезопасный шаг под lock).
- **Session & Identity:** `SessionManager` (ADR-008), 32-байтный криптографический секрет, O(k) grace sweep, эксклюзивная привязка сокета и сессии (`TransportSessionBinder`).
- **Транспорт и дельта-сжатие:** `INetworkCarrier` (ADR-009, LiteNetLib 1.3.5 + `OwnDatagramCarrier`), каналы C0/C1/C2, дельта-снапшоты (ADR-010, History Ring 120 тиков, keyframe slicing), Zero-GC буферы отправки.
- **Reconnect & Resync:** Полноценное переподключение и восстановление (ADR-011, OD-18..OD-22), тактическая пауза, 5-секундный countdown таймер, защита от cross-session hijack (`ServerTransportHost`).
- **Presentation & Controls:** SoA интерполяционный буфер `UnitViewTickBuffer` (OD-23), пул `UnitViewPool` и O(1) биндинг `UnitViewBinder` (OD-26), инстансированные кольца выделения и HP-бары `UnitOverlayBatcher` (URP 17.6 RenderGraph, OD-25), миникарта и HUD за 1 draw call (OD-24, OD-27, OD-28).
- **Сквозной вертикальный срез 400 юнитов:** `TacticalVerticalSliceRunner` с доказанным профилем OD-28: **0.623 ms/frame** и **0 B GC.Alloc** за 60 steady-state кадров.

## Ограничения прототипа

- `LocalMatchHost` работает внутри клиентского процесса (выделенный dedicated-серверный процесс запланирован в Phase 4/8).
- Pathfinding по неровной местности и детерминированный обход динамических препятствий — в процессе разработки.
- Фракции и производственный контент 1.0 (пять основных фракций: NATO, Russia, China, GLA, USA) являются утверждённым планом, а не финальным набором юнитов.

## Автоматические проверки

Откройте `Window → General → Test Runner`.
Актуальный бейзлайн (коммит `4cb8ffa`, 2026-10-20):
- **1162/1162 EditMode** тестов passed (100% green).
- **16/16 PlayMode** тестов passed (100% green).
- **1178 автоматизированных тестов суммарно** (0 warnings, 0 errors).
- Grand Re-Audit сертифицирован 3 независимыми аудиторами: **`[APPROVED: ZERO DEFECTS]`**.

## Связанные документы

- [Текущее состояние](Documentation/CURRENT_STATE.md)
- [Индекс документации](Documentation/README.md)
- [Архитектура](Documentation/ARCHITECTURE.md)
- [Roadmap](Documentation/ROADMAP.md)
- [Phase 02 (Multiplayer)](Documentation/Phases/Phase_02_Multiplayer.md)
- [Phase 03 (RTS Core)](Documentation/Phases/Phase_03_RTS_Core.md)
- [Phase 04 (Multiplayer Integration)](Documentation/Phases/Phase_04_Multiplayer_Integration.md)
