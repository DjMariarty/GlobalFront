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
| Engine | Unity `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0` |
| Simulation | deterministic Core, 20 Hz |
| Server | `MatchServer`, server-owned state, `MatchConfig`; `TickDriver` (ADR-007); `SessionManager` — session/player identity (ADR-008) |
| Local host | `LocalMatchHost` in Client process (owns `MatchServer`, `TickDriver`, `SessionManager`) |
| Protocol | Snapshot Protocol v1 |
| Commands | `ICommandChannel` + session-attributed `LocalCommandChannel`, Phase 2.2 complete; ingress через session gate (Phase 2.4) |
| Verification | 763 EditMode + 6 PlayMode passed 2026-09-26 (baseline 223 + 5 on 2026-08-21) |

## Current Research Priority

Phase 2.5 Network Transport:

- transport technology selection;
- delivery для commands и snapshots;
- attribution boundary с session layer (`SessionManager`, ADR-008);
- cadence и protocol compatibility с Snapshot Protocol v1;
- test strategy.

Все конкретные ответы являются **Architecture Decision Required**; этот документ их не предрешает.

## Later Research Questions

- transport-adjacent вопросы: snapshot cadence, bandwidth, reconnect/resync algorithm;
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