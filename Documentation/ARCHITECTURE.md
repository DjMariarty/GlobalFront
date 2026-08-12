# Архитектура GlobalFront

> Последнее обновление: 2026-08-12
> Статус: ранний прототип / pre-alpha

## Назначение

Этот документ описывает границы модулей, детерминированный контракт симуляции и известный технический долг. Текущее продуктовое состояние приведено в [PROJECT_STATUS.md](PROJECT_STATUS.md), а принятые архитектурные решения — в [DECISIONS.md](DECISIONS.md).

## Модули и зависимости

| Сборка | Назначение | Допустимые зависимости |
|---|---|---|
| `GlobalFront.Core` | Чистая детерминированная симуляция: идентификаторы, команды, перемещение, бой, константы. | Нет; Unity API запрещён. |
| `GlobalFront.Server` | Headless `MatchServer`: хранение состояния, валидация и исполнение команд по тикам; сериализация snapshot-пакетов для будущего транспорта. | Только `GlobalFront.Core`; Unity API и Client запрещены. |
| `GlobalFront.Client` | Unity-представление, ввод и локальный RTS-прототип. | `GlobalFront.Core`, `UnityEngine`, `Unity.InputSystem`; временно — `GlobalFront.Server`. |
| `GlobalFront.Tests.EditMode` | Редакторские тесты всех трёх runtime-сборок. | Core, Server, Client и Unity Test Framework. |

```text
GlobalFront.Core <- GlobalFront.Server
       ^
       +----------- GlobalFront.Client
GlobalFront.Server <- GlobalFront.Client (локальный авторитетный host)
```

Ссылка Client -> Server остаётся временной: `PrototypeRtsController` создаёт `LocalMatchHost`, который владеет `MatchServer` внутри клиентского процесса, передаёт команды клиента и выполняет авторитетные тики. После введения транспорта `MatchServer` будет вынесен в отдельный процесс, а зависимость Client -> Server удалена.

## Детерминированный контракт

Авторитетный сервер и будущий клиентский prediction должны применять идентичную последовательность на каждом тике:

1. Исполнить запланированные команды в каноническом порядке игрока и sequence.
2. Очистить недопустимые цели атаки.
3. Назначить автоматические цели.
4. Переместить юниты из общего снимка начальных позиций тика.
5. Разрешить бой с одновременным применением урона.

Параметры, влияющие на этот контракт, должны располагаться в `GlobalFront.Core.Simulation.SimulationConstants`. Их нельзя копировать в Client или Server.

## Текущее состояние интеграции

`LocalMatchHost` реализован и является единственным источником состояния симуляции:

- владеет `MatchServer` внутри клиентского процесса;
- передаёт в него команды клиента (`Move`, `Attack`, `Stop`);
- выполняет один авторитетный тик на каждый 20 Hz `FixedSimulationRunner.TickExecuted`;
- отдаёт клиенту `ServerUnitSnapshot`, которые применяются к presentation-слою.

Клиент не применяет команды, не двигает юниты и не рассчитывает бой локально: presentation только потребляет снапшоты. Сериализация snapshot-пакетов реализована в `GlobalFront.Server.Snapshot` (детерминированный бинарный формат, версия протокола, little-endian). Транспорт, prediction и reconciliation пока отсутствуют — host и клиент работают в одном процессе.

## Snapshot Serialization Protocol

Реализован детерминированный бинарный формат для сериализации `ServerUnitSnapshot`:

- **Protocol Version**: `1` (uint32)
- **Byte Order**: little-endian
- **Header Size**: 16 bytes
- **Snapshot Size**: 39 bytes

### Packet Structure

**Header (16 bytes):**
- Offset 0: ProtocolVersion (uint32, 4 bytes)
- Offset 4: Tick (uint64, 8 bytes)
- Offset 12: UnitCount (uint32, 4 bytes)

**Snapshot Payload (39 bytes per unit):**
- Entity.Value (uint64, 8 bytes)
- Owner.Value (uint8, 1 byte)
- Position.X (int32, 4 bytes)
- Position.Z (int32, 4 bytes)
- CurrentHealth (int32, 4 bytes)
- HasMoveTarget (uint8, 1 byte: 0 or 1)
- MoveTarget.X (int32, 4 bytes)
- MoveTarget.Z (int32, 4 bytes)
- AttackTarget.Value (uint64, 8 bytes)
- AutoAcquireEnemies (uint8, 1 byte: 0 or 1)

### Validation

Десериализатор отклоняет:
- Неподдерживаемую версию протокола
- Усечённые пакеты (недостаточная длина header или payload)
- Некорректные boolean-значения (не 0 и не 1)

Сериализатор требует canonical entity-id order (ascending) для детерминизма.

## Технический долг

| ID | Приоритет | Проблема | Направление решения |
|---|---|---|---|
| DEBT-001 | Высокий | Скорость юнита и spacing построения дублируются в клиентском прототипе и shadow-настройке. | Вынести протокольно значимые значения в Core или общий набор данных. |
| DEBT-008 | Высокий | Нет транспорта, prediction, reconciliation и серверного host loop. | Внедрять поверх существующего snapshot-протокола. |
| DEBT-004 | Средний | Интеграционные тесты host всё ещё обращаются к private lifecycle-состоянию контроллера через reflection. | Расширить диагностическую поверхность контроллера либо перейти к PlayMode-тестам. |
| DEBT-007 | Средний | Локальный путь применения команд `PrototypeCommandQueue.ApplyPending` и локальные методы движения `PrototypeUnit` сохранены, но не используются авторитетным конвейером. | Удалить после стабилизации авторитетного host. |
| DEBT-005 | Средний | UI и временный мир создаются через IMGUI и runtime bootstrap. | После стабилизации прототипа перейти к UI Toolkit/uGUI и prefab/data assets. |
| DEBT-006 | Низкий | Часть тестов использует литеральные значения вместо общих констант. | Использовать `SimulationConstants` в boundary-тестах. |

## Базовая верификация

На Unity `6000.5.6f1` 12.08.2026 пройдены:
- **EditMode**: 126/126 тестов (113 существующих + 13 snapshot-сериализация)
- **PlayMode**: 5/5 тестов (авторитетный pipeline, move, attack, terminal outcome)

## Связанные документы

- [Индекс документации](README.md)
- [Статус проекта](PROJECT_STATUS.md)
- [Архитектурные решения](DECISIONS.md)
- [Журнал изменений](CHANGELOG.md)
