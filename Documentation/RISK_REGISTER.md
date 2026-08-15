# GlobalFront Risk Register

> Живой процессный реестр. Severity отражает исходный риск для Product 1.0; конкретные ответственные ещё требуют назначения владельцем.

## Status Vocabulary

- `OPEN` — риск существует, mitigation не подтверждён полностью.
- `MONITORING` — mitigation действует и собирает evidence.
- `MITIGATED` — agreed acceptance evidence получено.
- `ACCEPTED` — остаточный риск явно принят владельцем.

## Register

| Risk | Severity | Mitigation | Owner | Status |
|---|---|---|---|---|
| Пять полноценных фракций создают большой параллельный content, systems и balance scope. | High | Representative slice всех пяти в Phase 3; параллельное развитие; полный roster gate в Phase 5; data/content и regression validation. | TBD — Owner Decision Required | OPEN |
| Multiplayer до 10 игроков повышает сложность authority, sessions, bandwidth, Fog of War и failure handling. | Critical | Phase 2 networking foundation; Phase 4 real RTS integration; обязательные end-to-end, load и ownership tests. | TBD — Owner Decision Required | OPEN — Phase 2 active |
| Цель 3000+ entities может превысить CPU, memory, GC и network budgets. | Critical | Benchmarks с Phase 3; automated 3000+ suite; 5000+ stress и deep optimization в Phase 8; evidence до claims. | TBD — Owner Decision Required | OPEN |
| Pathfinding больших армий может нарушить determinism, responsiveness и long-match performance. | Critical | Отдельные R&D/ADR; deterministic test corpus; representative benchmarks с Phase 3; scale/stress validation в Phase 8. | TBD — Owner Decision Required | OPEN |
| AI всех пяти фракций и 5v5 может быть дорогим, нестабильным или использовать недоступную игроку информацию. | High | Fair-information contract; staged basic AI → full AI; faction/team/large-map tests; long-run performance validation. | TBD — Owner Decision Required | OPEN |
| Баланс пяти асимметричных фракций может задержать Alpha/Beta и создавать постоянный churn. | High | Утверждённые faction intents; balance baseline в Phase 5/9; matchup evidence; Beta balance gate; изменения только через tracked decisions. | TBD — Owner Decision Required | OPEN |
| Dedicated server operations могут иметь deployment, observability, compatibility и recovery failures. | Critical | R&D/ADR topology; diagnostics; deployment/rollback validation; server infrastructure gate в Phase 11; soak/reliability evidence. | TBD — Owner Decision Required | OPEN |
| Reconnect/resync может восстановить неполное или несовместимое authoritative state и вызвать desync. | Critical | Versioned state contracts; Phase 2.7 tests; Phase 4 gameplay integration tests; induced-disconnect/desync scenarios; replay/diagnostic evidence. | TBD — Owner Decision Required | OPEN |
| Production presentation может снизить gameplay readability или нарушить performance targets. | High | Performance budgets и profiling; UI/readability review; content/VFX regression; representative presentation в benchmarks; quality gates Phase 7–10. | TBD — Owner Decision Required | OPEN |

## Review Rules

- Реестр проверяется на Scope, Architecture, Integration, Alpha, Beta и Release Candidate gates.
- Critical/High risk без owner или actionable mitigation подлежит escalation по [AI Contract](AI_CONTRACT.md).
- Severity, mitigation или status меняются только с evidence; закрытие phase не закрывает риск автоматически.
- Новые риски добавляются без удаления исторического контекста; accepted risk требует явного owner decision.

## Related Documents

- [AI Contract](AI_CONTRACT.md)
- [Master Game Plan](MASTER_GAME_PLAN.md)
- [Roadmap](ROADMAP.md)
- [Phase 08](Phases/Phase_08_Scale_Performance_Reliability.md)