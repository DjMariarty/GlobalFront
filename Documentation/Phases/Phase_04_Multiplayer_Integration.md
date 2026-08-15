# Phase 04 — Multiplayer Gameplay Integration

> Status: planned • Milestone M1 Multiplayer Prototype

## Goal

Интегрировать networking foundation Phase 2 с реальным RTS gameplay Phase 3 и получить первый настоящий multiplayer RTS prototype.

## Dependencies

- применимый Phase 2 multiplayer foundation;
- M2 Playable RTS Core;
- утверждённые authoritative validation rules;
- доступная server execution environment.

## Scope

- networked economy и production;
- networked combat и abilities;
- networked Fog of War;
- authoritative validation;
- snapshots и reconnect для реального gameplay;
- минимум 1v1 с реальной экономикой, реальными юнитами, реальным combat и реальным server.

## Key Systems

- gameplay command transport;
- authoritative RTS state;
- gameplay snapshot replication;
- reconnect/resync с gameplay state;
- Fog of War visibility boundary;
- multiplayer diagnostics.

## Milestones

- первый end-to-end multiplayer RTS prototype;
- M1 Multiplayer Prototype после принятия Phase 4.

## Acceptance Criteria

- два игрока могут завершить утверждённый 1v1 gameplay loop через real server;
- economy, production, combat, abilities и Fog of War авторитетны;
- invalid commands отклоняются server-side;
- snapshot flow и reconnect работают с реальным RTS state;
- выявленные desync/failure states диагностируемы.

## Tests

- end-to-end 1v1 tests;
- authoritative validation и ownership tests;
- gameplay snapshot/reconnect tests;
- latency/loss/disconnect scenarios: параметры TBD через R&D;
- deterministic and desync regression tests.

## Risks

- расхождение simulation и presentation;
- утечка скрытой Fog of War информации;
- bandwidth/state-size growth;
- reconnect с неполным gameplay state;
- сетевые ошибки, маскирующие gameplay defects.

## Out of Scope

- полный faction roster и финальный balance — Phase 5;
- full AI — Phase 6;
- production maps/presentation — Phase 7;
- 5v5 scale proof — Phase 8.

## Definition of Done

Минимальный настоящий authoritative 1v1 RTS prototype работает end-to-end с реальной экономикой, юнитами, combat, snapshots и reconnect; M1 принят.