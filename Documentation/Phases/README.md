# GlobalFront Development Phases

> Утверждённый Phase Plan 0–12. Фазы могут пересекаться, но их Definition of Done проверяется отдельно.

| Phase | Document | Status | Milestone |
|---:|---|---|---|
| 0 | [Product Definition](Phase_00_Product_Definition.md) | master scope approved; detailed TBD | — |
| 1 | [Simulation Foundation](Phase_01_Foundation.md) | **COMPLETE** | M0 |
| 2 | [Multiplayer Foundation](Phase_02_Multiplayer.md) | **CURRENT**, 2.3 next | prerequisite M1 |
| 3 | [RTS Core + Vertical Slice](Phase_03_RTS_Core.md) | planned | M2 |
| 4 | [Multiplayer Gameplay Integration](Phase_04_Multiplayer_Integration.md) | planned | M1 |
| 5 | [Full Five Factions](Phase_05_Five_Factions.md) | planned | M3 |
| 6 | [AI](Phase_06_AI.md) | planned | M4 |
| 7 | [Content + Maps + Presentation](Phase_07_Content_Maps_Presentation.md) | planned | — |
| 8 | [Scale / Performance / Reliability](Phase_08_Scale_Performance_Reliability.md) | planned | M5 |
| 9 | [Alpha](Phase_09_Alpha.md) | planned | M6 |
| 10 | [Beta](Phase_10_Beta.md) | planned | M7 |
| 11 | [Release Candidate](Phase_11_Release_Candidate.md) | planned | release gate |
| 12 | [GlobalFront 1.0](Phase_12_Release_1_0.md) | planned | M8 |

## Shared Structure

Каждый Phase-файл содержит: Goal, Dependencies, Scope, Key Systems, Milestones, Acceptance Criteria, Tests, Risks, Out of Scope и Definition of Done.

## Shared Delivery Rule

```text
R&D → Architecture Decision → Implementation → Tests → Review → Documentation → Commit
```

Навигация: [Master Game Plan](../MASTER_GAME_PLAN.md) • [Roadmap](../ROADMAP.md) • [Current State](../CURRENT_STATE.md)