# GlobalFront Research Context

> Исследовательский контекст синхронизирован с утверждённым Product Scope; не заменяет R&D reports или ADR.

## Fixed Product Context

- GlobalFront модернизирует фундаментальную формулу Generals/Zero Hour.
- Product 1.0 включает NATO, Russia, China, GLA и USA.
- Подфракции не входят в 1.0; система 25 подфракций — post-1.0.
- 1.0 требует 1v1–5v5, FFA, до 10 игроков, authoritative dedicated server, reconnect/resync, replay и desync detection.
- 1.0 включает AI для пяти фракций, большие армии, 3000+ target и многочасовые матчи.
- Карты следуют классическому подходу без ненужных сложных mechanics; 5v5 требует справедливых стартов и распределения ресурсов.
- Новые механики допустимы только если усиливают основную формулу.

## Confirmed Technical Context

| Area | Current fact |
|---|---|
| Engine | Unity `6000.5.6f1`, URP |
| Simulation | deterministic Core, 20 Hz |
| Server | `MatchServer`, server-owned state, `MatchConfig` |
| Local host | `LocalMatchHost` in Client process |
| Protocol | Snapshot Protocol v1 |
| Commands | `ICommandChannel` + `LocalCommandChannel`, Phase 2.2 complete |
| Verification | 161 EditMode + 5 PlayMode passed 2026-08-12 |

## Current Research Priority

Phase 2.3 Server Tick Driver:

- ownership и lifecycle;
- fixed-tick scheduling вне client presentation;
- startup/shutdown и error behavior;
- границы с future session/transport layers;
- test strategy.

Все конкретные ответы являются **Architecture Decision Required**; этот документ их не предрешает.

## Later Research Questions

- session/player identity и transport technology;
- snapshot cadence, bandwidth, reconnect/resync algorithm;
- replay format и desync detection/diagnostics;
- deterministic pathfinding для больших армий;
- benchmark workloads и hardware profiles для 3000+/5000+;
- economy/build/production rules и faction data model;
- fair-information AI architecture для 5v5;
- production content pipeline, presentation direction и map validation.

## Product TBD / Owner Decision Required

- конкретные faction rosters, abilities и unique mechanics;
- stats, economy values и balance targets;
- generals, их имена и точные systems;
- конкретные maps/layouts/resources;
- art, UI, audio и destruction direction;
- campaign и любые дополнительные modes;
- release platform/deployment details, если они не определены отдельным решением.

## Research Output Rule

Каждое исследование должно фиксировать вопрос, ограничения, alternatives, evidence, recommendation и decision owner. Recommendation не становится requirement до ADR или owner approval.

## Связанные документы

- [GDD](GDD.md)
- [Architecture](ARCHITECTURE.md)
- [Decisions](DECISIONS.md)
- [Phase 02](Phases/Phase_02_Multiplayer.md)