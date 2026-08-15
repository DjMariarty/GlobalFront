# Phase 05 — Full Five Factions

> Status: planned • Milestone M3 Five Factions

## Goal

Довести NATO, Russia, China, GLA и USA до полного утверждённого roster и уникального игрового характера для Product 1.0.

## Dependencies

- M2 RTS core и representative slices;
- M1 multiplayer prototype;
- утверждённые faction rosters, mechanics и balance intent;
- content/data pipeline, достаточный для разработки roster.

## Scope

Для всех пяти основных фракций параллельно:

- buildings;
- infantry, vehicles, aircraft, artillery и AA;
- technology;
- generals, abilities и superweapons;
- unique mechanics;
- balance.

## Key Systems

- faction data и content definitions;
- production/technology trees;
- unit/building gameplay contracts;
- general/ability/superweapon systems;
- cross-faction balance framework.

## Milestones

- полный roster NATO;
- полный roster Russia;
- полный roster China;
- полный roster GLA;
- полный roster USA;
- M3 Five Factions.

## Acceptance Criteria

- каждая фракция имеет полный утверждённый 1.0 roster;
- все имеют перечисленные категории и собственный gameplay character;
- ни одна фракция не оставлена как временный slice;
- multiplayer compatibility и balance baseline подтверждены;
- все assets имеют допустимое происхождение.

## Tests

- faction roster/data validation;
- unit/building/technology/ability integration tests;
- multiplayer parity и deterministic regression;
- matchup/balance tests; конкретные balance targets — TBD / Owner Decision Required.

## Risks

- content volume и несинхронное развитие фракций;
- размывание faction identity;
- balance debt;
- рост simulation/network cost.

## Out of Scope

- подфракции и их 25-вариантная система;
- новые основные фракции;
- campaign;
- окончательный production polish Phase 7.

## Definition of Done

Все пять основных фракций имеют полный утверждённый roster, уникальный character и multiplayer-ready balance baseline; M3 принят.