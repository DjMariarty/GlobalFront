# Архитектурные и проектные решения (ADR)

> Живой нормативный и исторический журнал • обновлено 2026-08-15

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

## Open Decision Queue

- Phase 2.3 Server Tick Driver design.
- Session/player identity и transport technology.
- Snapshot networking cadence, reconnect/resync, replay и desync diagnostics.
- Точные faction rosters, abilities, generals, stats и balance.
- Economy/build/production rules, map layouts и presentation direction.
- Hardware profiles и pass thresholds для scale benchmarks.

## Связанные документы

- [Master Game Plan](MASTER_GAME_PLAN.md)
- [Architecture](ARCHITECTURE.md)
- [Roadmap](ROADMAP.md)