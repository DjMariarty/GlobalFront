# Phase 06 — AI

> Status: planned • Milestone M4 RTS AI

## Goal

Создать честный RTS AI для всех пяти фракций, командных матчей и больших карт.

## Dependencies

- полный утверждённый gameplay foundation;
- M3 Five Factions;
- authoritative information/visibility rules;
- map framework и performance benchmarks.

## Scope

- all five factions;
- difficulty levels;
- scouting;
- economy, build и production;
- attack и defense;
- adaptation;
- team AI и 5v5 AI;
- использование авиации, abilities, generals и superweapons;
- работа на больших картах.

## Key Systems

- perception на доступной игроку информации;
- strategic/economic decision making;
- build/production planning;
- combat, defense и scouting control;
- faction-specific behavior;
- team coordination и difficulty configuration.

## Milestones

- functional AI для каждой основной фракции;
- team and 5v5 AI;
- M4 RTS AI.

## Acceptance Criteria

- AI использует все утверждённые основные gameplay systems;
- AI играет каждой из пяти фракций;
- AI поддерживает team matches и большие карты;
- AI не получает скрытую информацию, недоступную игроку;
- difficulty/adaptation behavior соответствует утверждённым criteria.

## Tests

- behavior/integration tests economy, build, scouting, attack и defense;
- faction coverage tests;
- fair-information tests;
- team/5v5 and large-map scenarios;
- long-run stability/performance tests;
- exact difficulty metrics: TBD / Owner Decision Required.

## Risks

- скрытое cheating через server state;
- чрезмерная стоимость decision making;
- несовместимость с faction asymmetry;
- нестабильность командной координации;
- brittle scripted behavior.

## Out of Scope

- ML/нейросетевой AI как обязательное требование;
- campaign scripting;
- подфракции;
- финальный presentation polish.

## Definition of Done

Честный AI играет всеми пятью фракциями, использует утверждённые core mechanics, поддерживает difficulty levels, team matches и 5v5 на больших картах; M4 принят.