# Документация GlobalFront

> Центральная точка навигации для владельца проекта, разработчиков и AI-агентов. Синхронизировано с утверждённым Master Development Plan 2026-08-15.

## Быстрый вход

1. [CURRENT_STATE.md](CURRENT_STATE.md) — короткая фактическая сводка и следующий шаг.
2. [MASTER_GAME_PLAN.md](MASTER_GAME_PLAN.md) — верхнеуровневый план продукта, фаз и milestones.
3. [GDD.md](GDD.md) — утверждённый gameplay intent и границы Product 1.0.
4. [ROADMAP.md](ROADMAP.md) — последовательность Phase 0–12.
5. [Phases/README.md](Phases/README.md) — детальные документы фаз.
6. [AI_CONTRACT.md](AI_CONTRACT.md) — обязательные правила AI-driven разработки и escalation.

## Карта документов

| Документ | Роль | Тип | Состояние |
|---|---|---|---|
| [MASTER_GAME_PLAN.md](MASTER_GAME_PLAN.md) | Product Scope, roadmap и milestones | живой, нормативный | готов на уровне утверждённого плана |
| [GDD.md](GDD.md) | gameplay intent и продуктовые границы | живой, нормативный | утверждённый scope; детали roster/balance остаются TBD |
| [CURRENT_STATE.md](CURRENT_STATE.md) | быстрый handoff состояния | живой, оперативный | актуален для Phase 2.3 |
| [ROADMAP.md](ROADMAP.md) | Phase 0–12 и порядок поставки | живой, нормативный | готов |
| [PROJECT_STATUS.md](PROJECT_STATUS.md) | реализовано, не реализовано, риски | живой, фактический | готов для текущего baseline |
| [ARCHITECTURE.md](ARCHITECTURE.md) | текущие и целевые технические границы | живой, нормативный | готов; будущие детали через ADR |
| [DECISIONS.md](DECISIONS.md) | решения владельца и ADR | живой, нормативный/исторический | готов; пополняется |
| [CHANGELOG.md](CHANGELOG.md) | подтверждённые изменения и вехи | живой, исторический | готов; пополняется |
| [DEVELOPMENT_STANDARD.md](DEVELOPMENT_STANDARD.md) | production workflow и правила доказательств | живой, нормативный | готов |
| [AI_CONTRACT.md](AI_CONTRACT.md) | AI statuses, review gates и escalation | живой, нормативный | готов |
| [RISK_REGISTER.md](RISK_REGISTER.md) | Product 1.0 risks и mitigations | живой, процессный | открыт; owners требуют назначения |
| [IntegrationContracts/README.md](IntegrationContracts/README.md) | Контракты Phase 2↔3, 3↔4 и 4↔5 | живой, нормативный | готов |
| [GLOBALFRONT_RESEARCH_CONTEXT.md](GLOBALFRONT_RESEARCH_CONTEXT.md) | утверждённый research context и TBD | живой, справочный | готов |
| [ThirdPartyAssetsRegistry.md](ThirdPartyAssetsRegistry.md) | источники и лицензии внешних ассетов | живой, нормативный | готов; реестр пока пуст |

## Иерархия истины

1. Утверждённый владельцем Master Development Plan определяет Product 1.0 и Phase Plan.
2. Реализация, Git commits и результаты тестов определяют фактический технический baseline.
3. Незавершённые системы в roadmap не считаются реализованными.
4. Детали, отсутствующие в плане, маркируются `TBD / Owner Decision Required`.

## Локальные документы

- [GameContent README](../Assets/_GlobalFront/GameContent/README.md)
- [ThirdParty README](../Assets/_GlobalFront/ThirdParty/README.md)
