# Архитектурные и проектные решения (ADR)

> Живой нормативный и исторический журнал • обновлено 2026-08-21

Каждое долговременное решение содержит Context, Alternatives, Decision и Consequences. Детали, которых нет в утверждённом плане, не считаются решёнными.

## ADR-001: Общепроектная документация хранится вне Assets

**Статус:** Accepted, 2026-08-08

### Context

Общепроектные Markdown-файлы не являются Unity assets, но ранее находились внутри `Assets`.

### Alternatives

1. Хранить всё в `Assets`.
2. Вынести все Markdown-файлы без исключений.
3. Хранить центральные документы в `Documentation/`, а локальные README рядом с контентом.

### Decision

Выбран третий вариант.

### Consequences

Центральная документация не импортируется Unity; локальные README `GameContent` и `ThirdParty` сохраняют контекст каталогов.

## ADR-002: Baseline отделяется от рабочей копии и планов

**Статус:** Accepted, 2026-08-12; контекст продуктовой неопределённости superseded утверждённым Master Development Plan 2026-08-15

### Context

Документация должна отличать проверенную реализацию от незавершённой работы и будущего scope.

### Alternatives

1. Описывать roadmap как готовую систему.
2. Игнорировать будущий scope.
3. Явно маркировать current baseline, current work и approved target.

### Decision

Выбран третий вариант. Новый Master Development Plan заменяет прежнее утверждение об отсутствии продуктового scope, но не отменяет разделение plan/implementation.

### Consequences

Будущие системы не объявляются реализованными; `CURRENT_STATE.md` содержит только фактический handoff.

## ADR-003: Product сохраняет фундамент Generals / Zero Hour

**Статус:** Owner Approved, 2026-08-15

### Context

Нужно зафиксировать устойчивую продуктовую формулу GlobalFront.

### Alternatives

1. Создать новую RTS без обязательной связи с исходной формулой.
2. Сохранить фундамент Generals/Zero Hour и модернизировать реализацию.

### Decision

Выбран второй вариант. Новые механики добавляются только если усиливают формулу и не создают ненужной сложности.

### Consequences

GDD и фазовые решения должны поддерживать экономику, base building, production, виды войск, генералов, способности, супероружие, гарнизоны, захват, ветеранство, асимметрию и multiplayer experience.

## ADR-004: Пять основных фракций входят в 1.0; подфракции не входят

**Статус:** Owner Approved, 2026-08-15

### Context

Нужна ясная content boundary для 1.0.

### Alternatives

1. Выпустить часть основных фракций.
2. Включить основные и подфракции одновременно.
3. Включить NATO, Russia, China, GLA и USA; перенести подфракции в post-1.0.

### Decision

Выбран третий вариант.

### Consequences

Все пять основных фракций получают representative slice в Phase 3 и полный roster в Phase 5. Система 25 подфракций относится к post-1.0.

## ADR-005: Multiplayer строится вокруг authoritative dedicated server

**Статус:** Owner Approved, 2026-08-15

### Context

Product 1.0 требует 1v1–5v5, FFA, reconnect/resync, replay, desync detection, большие армии и долгие матчи.

### Alternatives

1. Оставить local-only host.
2. Использовать authoritative deterministic dedicated server с command/snapshot границами.

### Decision

Выбран второй вариант.

### Consequences

Phase 2 создаёт networking foundation; Phase 4 интегрирует его с реальным RTS gameplay. Конкретные transport/session/resync решения требуют отдельных ADR.

## ADR-006: Production flow и Phase 0–12

**Статус:** Owner Approved, 2026-08-15

### Context

Нужно связать исследования, решения, реализацию и проверку без буквального Waterfall.

### Alternatives

1. Неформальная последовательность без gates.
2. Жёсткий Waterfall.
3. R&D → ADR → Implementation → Tests → Review → Documentation → Commit с допустимым пересечением фаз.

### Decision

Выбран третий вариант и утверждён Phase Plan 0–12.

### Consequences

Каждая фаза имеет единый Definition of Done. Performance benchmarks начинаются в Phase 3, а Phase 8 остаётся глубокой optimization/reliability фазой.

## ADR-007: Server Tick Driver — engine-independent tick scheduling (Phase 2.3)

**Статус:** Accepted, 2026-08-21 (implementation и tests завершены; commit pending)

### Context

После Phase 2.1/2.2 authoritative tick lifecycle всё ещё владели client-side `FixedSimulationRunner` (MonoBehaviour в `GlobalFront.Client`): тики authoritative server запускались Unity lifecycle клиентского процесса. Задача Phase 2.3 — отделить authoritative server tick lifecycle от Unity client lifecycle при сохранении общего simulation core для local и будущего dedicated server. Транспорт, session/identity, reconnect/resync, prediction, lobby, matchmaking и dedicated deployment — out of scope.

### Alternatives

1. Оставить `FixedSimulationRunner` источником тиков. Отклонено: client presentation lifecycle остаётся владельцем authoritative ticks.
2. TickDriver в `GlobalFront.Client` как MonoBehaviour. Отклонено: dedicated server host не может использовать Unity client assembly; driver не стал бы общим.
3. Engine-independent `TickDriver` в `GlobalFront.Server`; `LocalMatchHost` выполняет роль ServerHost (владеет `MatchServer` и `TickDriver`); клиент только pump’ит driver. Выбран.

### Decision

- Новый `GlobalFront.Server.TickDriver` (`TickDriverMode.Manual` / `TickDriverMode.RealTime`, событие `TickDue`, `AdvanceRealTime` / `AdvanceManualTick`, `StartRealTime` / `StopRealTime`, `Reset`). Real-time pacing через существующий `Core.FixedStepClock`: 20 Hz, bounded catch-up `MaxCatchUpTicksPerFrame` (4); excess time остаётся queued и дренируется последующими вызовами. Manual mode — один tick на вызов для deterministic tests.
- `LocalMatchHost` (роль ServerHost) владеет `MatchServer` и `TickDriver`. Per-tick двухфазный контракт: `TickStarting` (команды для tick N forward’ятся, пока server на N-1) → `MatchServer.TickOnce` → `TickCompleted` (presentation потребляет snapshots). Command timing contract сохранён: `RequestedTick N` применяется во время tick N.
- `LocalMatchHost.TickOnce()` — deterministic manual advance (возвращает `bool`); real-time — `AdvanceRealTime`.
- `PrototypeRtsController` больше не владеет тиками: pump’ит driver в `Update`, подписан на `TickStarting`/`TickCompleted`; команды запрашиваются на `CurrentTick + 1`.
- `FixedSimulationRunner` удалён; клиентский interpolation alpha вычисляется из driver backlog (значения не меняются).
- `MatchServer.TickOnce` для пустого сервера (без юнитов) инкрементирует tick counter, но не симулирует и не выдаёт terminal (Draw) outcome до инициализации матча — server tick lifecycle независим от match initialization.

### Consequences

- Server tick lifecycle не зависит от Client presentation; та же пара `TickDriver`/`MatchServer` расписывает тики для future dedicated server host (out of scope, будет реализован в соответствующей фазе).
- Protocol-совместимость не нарушена: 20 Hz, tick duration, catch-up, snapshot layout и command contract не изменены; Snapshot Protocol v1 не тронут.
- `LocalMatchHost` сохраняет публичный API Phase 2.1/2.2; поведение `TickOnce()` уточнено (driver-backed, manual mode по умолчанию).
- Local и future dedicated server используют общий simulation core (`MatchServer` + `TickDriver`).

## Open Decision Queue

- Session/player identity и transport technology.
- Snapshot networking cadence, reconnect/resync, replay и desync diagnostics.
- Точные faction rosters, abilities, generals, stats и balance.
- Economy/build/production rules, map layouts и presentation direction.
- Hardware profiles и pass thresholds для scale benchmarks.

## Связанные документы

- [Master Game Plan](MASTER_GAME_PLAN.md)
- [Architecture](ARCHITECTURE.md)
- [Roadmap](ROADMAP.md)