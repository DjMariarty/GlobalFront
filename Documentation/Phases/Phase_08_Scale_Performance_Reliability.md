# Phase 08 — Scale / Performance / Reliability

> Status: planned • Milestone M5 Scale Prototype

## Goal

Выполнить глубокую optimization/reliability работу для больших армий, 5v5 и многочасовых матчей.

## Dependencies

- benchmarks существуют с Phase 3;
- networked RTS gameplay и representative production content;
- AI и 5v5 scenarios;
- approved hardware/network profiles и pass thresholds — TBD.

## Scope

- 3000+ entity target;
- 5000+ stress;
- 5v5;
- long matches;
- CPU, memory и GC;
- pathfinding;
- network load;
- reconnect;
- desync detection/diagnostics.

## Key Systems

- profiling/benchmark harness;
- simulation and pathfinding performance;
- memory/GC management;
- snapshot/transport load;
- long-run server reliability;
- reconnect/resync и desync tooling.

## Milestones

- 3000+ target demonstrated;
- 5000+ stress characterized;
- stable 5v5/long-match prototype;
- M5 Scale Prototype.

## Acceptance Criteria

- approved 3000+ scenario проходит target budgets;
- 5000+ stress results задокументированы;
- 5v5 и многочасовые runs стабильны по утверждённым criteria;
- CPU/memory/GC/pathfinding/network bottlenecks измерены и обработаны;
- reconnect/desync diagnostics работают под нагрузкой.

## Tests

- automated 3000+ benchmark;
- 5000+ stress suite;
- long-duration soak tests;
- 5v5 network and AI load tests;
- reconnect/resync and induced-desync tests;
- concrete hardware, duration и thresholds: TBD / Owner Decision Required.

## Risks

- late architecture bottlenecks;
- nondeterminism under concurrency/load;
- memory growth в многочасовых матчах;
- pathfinding/network saturation;
- benchmark scenarios, не отражающие реальный gameplay.

## Out of Scope

- первый performance test: он обязан существовать с Phase 3;
- новые gameplay mechanics;
- final balance/polish beyond performance impact;
- снижение утверждённого scale target без owner decision.

## Definition of Done

Утверждённые 3000+ target, 5000+ stress, 5v5 и long-match scenarios измерены; reliability, reconnect и desync diagnostics соответствуют approved thresholds; M5 принят.