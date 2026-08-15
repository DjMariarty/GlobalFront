# Стандарт разработки и документации

> Живой нормативный документ • обновлено по утверждённому production rule 2026-08-15

## Production Rule

Для каждой крупной feature/system:

```text
R&D → Architecture Decision → Implementation → Tests → Review → Documentation → Commit
```

Это не буквальный Waterfall. Фазы могут пересекаться, если зависимости и риски требуют параллельной работы. Пересечение не отменяет acceptance criteria и Definition of Done.

## Evidence Rules

| Утверждение | Источник |
|---|---|
| Product scope | утверждённый Master Development Plan / owner decision |
| Реализованное поведение | current source + review |
| Завершённая phase task | commit + проверки + документация |
| Test baseline | конкретный result file с датой и totals |
| История | Git commit/tag + changelog |

Roadmap не является доказательством реализации. Любая отсутствующая product detail маркируется `TBD / Owner Decision Required`.

## Architecture Rules

- Core и Server не используют Unity API.
- Authoritative state принадлежит Server.
- Deterministic contract использует fixed tick, integer state, canonical ordering и protocol-significant constants.
- Изменение simulation order, snapshot layout, command contract или protocol constants требует compatibility review и ADR.
- Networking, resync, replay, desync, pathfinding и scale решения проходят R&D до реализации.

## Product Rules

- Сохранять утверждённый фундамент Generals/Zero Hour.
- Не добавлять механику, если она не усиливает формулу или создаёт ненужную сложность.
- Все пять основных фракций входят в 1.0; подфракции не входят.
- Не придумывать roster, abilities, stats, balance, story, generals, campaign или map details без owner decision.

## Testing and Performance

- Проверки должны соответствовать риску изменяемой системы.
- После runtime-изменений старый result file не является новым подтверждением.
- Performance benchmarks начинаются в Phase 3 и развиваются вместе с gameplay.
- Phase 8 углубляет optimization/reliability, а не запускает первый benchmark.
- Scale targets из утверждённого плана: 3000+ entity target и 5000+ stress в Phase 8; конкретные workloads и thresholds остаются TBD.

## Documentation Rules

- `CURRENT_STATE.md` остаётся коротким и фактическим.
- `GDD.md` описывает gameplay intent, а не техническую архитектуру.
- `ARCHITECTURE.md` отделяет current baseline от approved target.
- `PROJECT_STATUS.md` не объявляет roadmap готовой системой.
- `CHANGELOG.md` содержит только подтверждённые изменения.
- После переименований проверяются все локальные Markdown-ссылки.

## Completion Checklist

1. Scope имеет утверждённый источник.
2. R&D и ADR закрывают необходимые решения.
3. Implementation соответствует architecture boundary.
4. Tests и, где нужно, benchmarks пройдены.
5. Review не оставил блокирующих замечаний.
6. Documentation и current state синхронизированы.
7. Commit выполняется только после предыдущих шагов ответственным участником; текущая документационная задача commit не делает.

## Связанные документы

- [Master Game Plan](MASTER_GAME_PLAN.md)
- [Architecture](ARCHITECTURE.md)
- [Decisions](DECISIONS.md)