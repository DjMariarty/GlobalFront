# GlobalFront Master Game Plan

> Статус: утверждён владельцем • Product 1.0 scope и Phase Plan • 2026-08-15

## Vision

GlobalFront — современная реализация формулы *Generals / Zero Hour*. Проект сохраняет фундаментальный gameplay оригинальной формулы и улучшает техническую архитектуру, графику, производительность, pathfinding, multiplayer, стабильность, масштаб, UX, поддержку долгих матчей, reconnect, replay и диагностику desync.

Новые механики допускаются только тогда, когда они усиливают эту формулу и не усложняют gameplay без необходимости.

## Product 1.0 Scope

В 1.0 входят:

- полный RTS-фундамент: экономика, строительство, производство, пехота, техника, авиация, артиллерия, ПВО, генералы, способности, супероружие, гарнизоны, захват, ветеранство и фракционная асимметрия;
- пять основных фракций: NATO, Russia, China, GLA и USA;
- multiplayer 1v1, 2v2, 3v3, 4v4, 5v5 и FFA, до 10 игроков;
- dedicated server, reconnect/resync, replay и desync detection;
- AI для всех пяти фракций и командных матчей;
- большие армии, цель 3000+ entities и многочасовые матчи;
- production-quality graphics, UI и audio.

Подфракции не входят в 1.0. Детали конкретных юнитов, способностей, генералов, характеристик, баланса и сюжета остаются `TBD / Owner Decision Required`.

## Delivery Model

Работа проходит через:

```text
R&D → Architecture Decision → Implementation → Tests → Review → Documentation → Commit
```

Это не буквальный Waterfall. Фазы могут пересекаться, когда это необходимо, но Definition of Done каждой поставки и фактический статус должны оставаться проверяемыми.

## Phase Plan

| Phase | Название | Статус |
|---:|---|---|
| 0 | Product Definition | scope утверждён на master-plan уровне; подробности GDD имеют TBD |
| 1 | Simulation Foundation | **COMPLETE** |
| 2 | Multiplayer Foundation | **CURRENT**; 2.1–2.5 complete, 2.6 next |
| 3 | RTS Core + Vertical Slice | planned |
| 4 | Multiplayer Gameplay Integration | planned |
| 5 | Full Five Factions | planned |
| 6 | AI | planned |
| 7 | Content + Maps + Presentation | planned |
| 8 | Scale / Performance / Reliability | planned |
| 9 | Alpha | planned |
| 10 | Beta | planned |
| 11 | Release Candidate | planned |
| 12 | GlobalFront 1.0 | planned |

Детали: [ROADMAP.md](ROADMAP.md) и [Phases/README.md](Phases/README.md).

## Milestones

| ID | Milestone | Условие |
|---|---|---|
| M0 | Technical Foundation | **COMPLETE**, Phase 1 |
| M1 | Multiplayer Prototype | после Phase 4 |
| M2 | Playable RTS Core | Phase 3 |
| M3 | Five Factions | Phase 5 |
| M4 | RTS AI | Phase 6 |
| M5 | Scale Prototype | Phase 8 |
| M6 | Alpha | Phase 9 |
| M7 | Beta | Phase 10 |
| M8 | Release 1.0 | Phase 12 |

Идентификаторы milestones являются утверждёнными метками; порядок исполнения определяется Phase Plan.

## Product 1.0 Definition of Done

- пять полноценных, уникальных и сбалансированных основных фракций;
- полный утверждённый фундамент Generals/Zero Hour;
- 1v1–5v5, FFA и до 10 игроков;
- AI, reconnect/resync, replay и desync detection;
- большие армии, 3000+ target и стабильные многочасовые матчи;
- production-quality graphics/UI/audio;
- стабильный authoritative dedicated server.

## Post-1.0

- система 25 подфракций;
- новые фракции и режимы;
- advanced modding, ranked и spectator expansion;
- campaign — только если будет отдельно утверждена.

## Связанные документы

- [GDD](GDD.md)
- [Roadmap](ROADMAP.md)
- [Current State](CURRENT_STATE.md)
- [Architecture](ARCHITECTURE.md)