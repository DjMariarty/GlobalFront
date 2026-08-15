# Phase 00 — Product Definition

> Status: master-level scope approved; detailed GDD decisions remain TBD

## Goal

Зафиксировать Vision, GDD, Product 1.0 scope, пять основных фракций, modes, multiplayer goals, scale requirements и границу 1.0/post-1.0.

## Dependencies

- owner approval;
- подтверждённый технический baseline как ограничение осуществимости;
- R&D для деталей, которых нет в Master Development Plan.

## Scope

- современная реализация формулы Generals/Zero Hour;
- пять основных фракций в 1.0;
- 1v1–5v5, FFA, до 10 игроков;
- AI, dedicated server, reconnect/resync, replay, desync detection;
- large-army и long-match цели;
- подфракции и другие расширения как post-1.0.

## Key Systems

- [Master Game Plan](../MASTER_GAME_PLAN.md);
- [GDD](../GDD.md);
- [Roadmap](../ROADMAP.md);
- owner decision и ADR process.

## Milestones

- Vision и high-level 1.0 boundary — approved;
- Phase Plan 0–12 — approved;
- detailed gameplay/content decisions — TBD.

## Acceptance Criteria

- Product 1.0 и post-1.0 явно разделены;
- пять основных фракций и multiplayer modes зафиксированы;
- неизвестные детали отмечены `TBD / Owner Decision Required`;
- GDD не смешан с технической архитектурой.

## Tests

- document consistency и Markdown link checks;
- review Product Scope против owner-approved plan;
- code tests к этой фазе не применяются.

## Risks

- конкретные roster/balance/content решения могут расширить scope;
- преждевременная детализация может быть ошибочно принята за owner approval.

## Out of Scope

- implementation;
- конкретные units, abilities, stats, balance, story и generals;
- campaign без отдельного approval.

## Definition of Done

Phase 00 завершена полностью, когда все обязательные Product/GDD решения для планирования 1.0 утверждены владельцем, TBD имеют owners, а Master Game Plan, GDD, Roadmap и phase boundaries согласованы. Сейчас master-level boundary готова, но detailed TBD остаются.