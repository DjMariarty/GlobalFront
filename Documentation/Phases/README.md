# GlobalFront Development Phases

> Утверждённый Phase Plan 0–12. Фазы могут пересекаться, но их Definition of Done проверяется отдельно.

| Phase | Document | Status | Milestone |
|---:|---|---|---|
| 0 | [Product Definition](Phase_00_Product_Definition.md) | master scope approved; detailed TBD | — |
| 1 | [Simulation Foundation](Phase_01_Foundation.md) | **COMPLETE [APPROVED: ZERO DEFECTS]** | M0 |
| 2 | [Multiplayer Foundation](Phase_02_Multiplayer.md) | **COMPLETE [P0/P1 = 0 CERTIFIED; все P2/P3 закрыты]** (`4cb8ffa` + Hardening 3.7.1) | prerequisite M1 |
| 3 | [RTS Core + Vertical Slice](Phase_03_RTS_Core.md) | **COMPLETE [APPROVED: ZERO DEFECTS]** (3.1–3.7 + Hardening 3.7.1) | M2 |
| 4 | [Multiplayer Gameplay Integration](Phase_04_Multiplayer_Integration.md) | **COMPLETE [APPROVED: ZERO DEFECTS]** (4.1–4.6.5 + M1.1/M1.2, commit `66419a8`) | M1 |
| 5 | [Full Five Factions](Phase_05_Five_Factions.md) | **COMPLETE [APPROVED: ZERO DEFECTS]** (5.1–5.6.2 + M2 Hardening, commit `f21ba3f`) | M2 (2v2) / M3 |
| 6 | [Content + Maps + Presentation](Phase_06_Content_Maps_Presentation.md) | **CURRENT / NEXT** (Playable Client Slice, Step 6.1) | — |
| 7 | [AI](Phase_07_AI.md) | planned | M4 |
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

Навигация: [Master Game Plan](../MASTER_GAME_PLAN.md) • [Roadmap](../ROADMAP.md) • [Current State](../CURRENT_STATE.md) • [Project Status](../PROJECT_STATUS.md)
