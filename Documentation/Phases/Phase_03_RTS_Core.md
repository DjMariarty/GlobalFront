# Phase 03 — RTS Core + Vertical Slice

> Status: planned • Milestone M2 Playable RTS Core

## Goal

Создать playable RTS core и минимальный representative vertical slice каждой из пяти основных фракций.

## Dependencies

- Phase 01 complete;
- approved Product/GDD boundaries;
- стабильные применимые interfaces Phase 2;
- faction-specific details, необходимые для slice: Owner Decision Required.

## Scope

- economy;
- base building;
- production;
- movement и combat;
- Fog of War;
- capture, garrison и veterancy;
- general system и abilities;
- basic AI;
- map framework;
- vertical slice NATO, Russia, China, GLA и USA;
- performance benchmarks с этой фазы.

## Key Systems

- economy/build/production loops;
- authoritative gameplay state и commands;
- visibility/Fog of War;
- capture/garrison/veterancy;
- general/ability foundation;
- basic AI и map framework;
- benchmark harness.

## Milestones

- M2 Playable RTS Core;
- representative slice всех пяти основных фракций;
- первый gameplay performance baseline.

## Acceptance Criteria

- утверждённый core gameplay образует цельный playable loop;
- каждая из пяти фракций представлена минимальным различимым slice;
- core systems работают через authoritative simulation boundary;
- benchmarks существуют и воспроизводимы;
- ограничения и TBD задокументированы.

## Tests

- deterministic unit/integration tests core gameplay systems;
- gameplay flow tests economy → build → production → combat;
- tests Fog of War, capture, garrison, veterancy, generals и abilities по утверждённым rules;
- basic AI и map-framework tests;
- benchmark scenarios и thresholds: TBD / Owner Decision Required.

## Risks

- чрезмерное расширение vertical slice;
- ранняя faction asymmetry без утверждённых rules;
- gameplay systems могут потребовать изменения network/snapshot contracts;
- performance regressions без ранних benchmarks.

## Out of Scope

- полный roster каждой фракции;
- полный multiplayer gameplay milestone — Phase 4;
- production-quality maps/presentation — Phase 7;
- deep optimization — Phase 8;
- подфракции.

## Definition of Done

Утверждённый RTS core playable end-to-end, все пять фракций имеют representative vertical slice, basic AI и map framework работают, benchmarks зафиксированы, а M2 принят.