# Журнал изменений

> Формат: Keep a Changelog.
> Последнее обновление: 2026-08-12

## Unreleased

### Added

- `LocalMatchHost` в `GlobalFront.Client`: локальный авторитетный host, владеющий `MatchServer` внутри клиентского процесса.
- `MatchServer.GetAllSnapshots()` — детерминированный набор `ServerUnitSnapshot` всех юнитов.
- `CombatantState.SynchronizeFromAuthoritative` — синхронизация presentation-состояния с авторитетным снапшотом.
- `PrototypeUnit.ApplyServerSnapshot` — применение авторитетного снапшота к presentation-юнитам.
- EditMode-тесты: `LocalMatchHostTests` (9) и `LocalMatchHostIntegrationTests` (8).
- Snapshot Serialization Protocol v1: детерминированный бинарный формат для сериализации `ServerUnitSnapshot`.
  - Фиксированный размер: 16 байт header + 39 байт на unit (little-endian)
  - Валидация: protocol version, packet size, boolean-значения
  - Canonical entity-id order для детерминизма
- 13 EditMode-тестов для snapshot-сериализации (round-trip, validation, golden bytes, determinism)
- 5 PlayMode-тестов для полного pipeline (bootstrap, server ticks, move, attack, terminal outcome)

### Changed

- Клиент больше не выполняет локальную симуляцию: `PrototypeRtsController` передаёт команды в `LocalMatchHost`, тикает авторитетный сервер и применяет снапшоты к presentation.
- `PrototypeCommandQueue` получил `ForwardPendingToHost` для передачи команд в авторитетный host.
- Удалены `ShadowServerIntegrationTests` и shadow-регистрация без тиков: её заменил авторитетный host.
- Техническая документация перемещена из `Assets/_GlobalFront/Documentation/` в корневую `Documentation/`.
- Добавлены индекс документации, ADR и перекрёстные ссылки.
- Обновлены статус и архитектурное описание в соответствии с текущим прототипом.

### Verified

- На Unity `6000.5.6f1` 12.08.2026 пройдены **113/113 EditMode-теста**.

## Исторические Git tags

| Tag | Дата commit | Commit | Зафиксированный контекст |
|---|---:|---|---|
| `v0.2-matchserver` | 2026-08-05 | `90a4bad` | Интеграция MatchServer по имени тега и истории commit. |
| `v0.2.1-project-cleanup` | 2026-08-06 | `4a9ff3a` | Очистка/организация проекта по имени тега и истории commit. |
| `v0.2.2-pre-restructure-backup` | 2026-08-06 | `4a9ff3a` | Резервная точка перед реструктуризацией по имени тега. |

Этот файл не восстанавливает детали прошлых релизов, если они отсутствуют в Git history. При создании следующего тега добавьте датированную секцию с подтверждёнными пользовательскими изменениями.

## Связанные документы

- [Индекс документации](README.md)
- [Статус проекта](PROJECT_STATUS.md)
- [Архитектурные решения](DECISIONS.md)
