# Первый запуск GlobalFront

> Unity: `6000.6.2f1` • URP: `17.6.0` • uGUI: `2.6.0` • текущая реализация: полномасштабный RTS-прототип с dedicated сервером, 2v2 топологией, 5 асимметричными фракциями, строительством баз и экономикой снабжения (Фазы 1–5 полностью сертифицированы)

## Открытие проекта

1. В Unity Hub выберите каталог `GlobalFront`, в котором находятся `Assets`, `Packages` и `ProjectSettings`.
2. Дождитесь завершения импорта и компиляции (Unity `6000.6.2f1`).
3. Откройте сцену `Assets/Scenes/SampleScene.unity`.
4. Нажмите Play.
5. Для переключения на полномасштабный 400-юнитный тактический срез Phase 3 нажмите **F9** или кнопку переключения в HUD.
6. Для запуска 1v1/2v2 сетевого матча на выделенном сервере используйте меню Unity: `GlobalFront → Playtest → Launch Dedicated Match` или CLI аргументы.

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

## Реализованные ключевые подсистемы (Фазы 1–5)

- **Core & Simulation:** Детерминированная авторитетная симуляция 20 Hz (`GlobalFront.Core`), целочисленная математика в миллиметрах `WorldPointMm`, матрица брони и урона 8x7 в пермилле (`DamageMatrix`, линейный splash falloff, гатлинг-эскалация, токсины).
- **Base Building & Power:** Сетка 500 мм (`SnapGrid500Mm`), проверка AABB футпринтов и коллизий зданий в `PlacementRegistry` (512 слотов), строительные леса 0%..100%, строители Dozer/Worker, мульти-стройка ГЛА, энергосеть `PowerLedger` и блэкаут защитных турелей.
- **Production Queues:** Кольцевой буфер очереди фабрик (5 слотов, `ProducerLedger`), проверка техдрева `TechMask`, точки сбора `RallyPointMm`, замедление при LowPower.
- **Supply Economy:** Склады ресурсов `SupplyDock` с 4 слотами погрузки, детерминированный FSM харвестеров, учет ресурсов `CreditLedger`, вторичный доход (хакеры, черный рынок, зоны сброса), Zero-GC 1 Hz трансляция `PlayerStateSync`.
- **Faction Database (100+ чертежей):** Полный ростер 5 фракций (USA, China, GLA, EU, Russia), сетевой Snapshot Protocol v3 (`BlueprintId` 16-bit), Command Codec v2 (`Build`, `Produce`).
- **Dedicated Server & 2v2 Topology:** `DedicatedServerRunner` (120 FPS pump, CLI `-topology 1v1|2v2`), командное лобби, Shared Vision союзников, фильтрация дружественного splash-урона, разделение меток на радаре.
- **Network Transport & Resync:** LiteNetLib 1.3.5 / `OwnDatagramCarrier`, каналы C0/C1/C2, дельта-снапшоты с FoW filtering, Zero-GC буферы пула (2048 буферов, до 1 MiB), тактическая пауза, 5-секундный countdown, авто-возобновление C0 Opcode 14.
- **Presentation & Controls:** SoA интерполяционный буфер `UnitViewTickBuffer`, пул `UnitViewPool` и O(1) биндинг `UnitViewBinder`, инстансированные кольца выделения и HP-бары `UnitOverlayBatcher` (URP 17.6 RenderGraph), миникарта и HUD за 1 draw call. Профиль на 400 юнитах: **0.623 ms/frame** и **0 B GC.Alloc**.

## Ограничения прототипа

- **Клиентский UI строительства и заказа юнитов:** Серверный бекенд (команды `Build`/`Produce`, очереди, проверка сетки, списание кредитов) полностью готов и протестирован; визуальные кнопки заказа в HUD и отображение призрака здания на сетке реализуются в Шаге 6.1 (Фаза 6).
- **Pathfinding по сложной местности:** Базовое плоское перемещение отрядов и formation slots реализованы; детальный обход динамических препятствий по трехмерному рельефу запланирован на последующие фазы.

## Автоматические проверки

Откройте `Window → General → Test Runner`.
Актуальный бейзлайн (коммит `f21ba3f` / `4d03fdf`):
- **1705/1705 EditMode** тестов passed (100% green).
- **20/22 PlayMode** тестов passed.
- **1727 автоматизированных тестов суммарно** (0 compiler warnings, 0 compiler errors).
- Сертификация: Фазы 1, 2, 3, 4 и 5 полностью закрыты независимыми состязательными аудитами с вердиктом **`[APPROVED: ZERO DEFECTS]`**.

## Связанные документы

- [Текущее состояние](Documentation/CURRENT_STATE.md)
- [Статус проекта](Documentation/PROJECT_STATUS.md)
- [Индекс документации](Documentation/README.md)
- [Архитектура](Documentation/ARCHITECTURE.md)
- [Roadmap](Documentation/ROADMAP.md)
- [Phase 04 (Multiplayer Integration)](Documentation/Phases/Phase_04_Multiplayer_Integration.md)
- [Phase 05 (Full Five Factions & 2v2)](Documentation/Phases/Phase_05_Five_Factions.md)
- [Phase 06 (Playable Client Slice)](Documentation/Phases/Phase_06_Content_Maps_Presentation.md)
