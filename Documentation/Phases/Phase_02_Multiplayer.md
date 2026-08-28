# Phase 02 — Multiplayer Foundation

> Status: **CURRENT** • 2.1–2.4 complete • 2.5 in progress

## Goal

Создать современный authoritative multiplayer foundation до интеграции полного RTS gameplay.

## Dependencies

- Phase 01 complete;
- deterministic command/snapshot contracts;
- R&D и ADR для каждого нового network boundary.

## Scope

1. 2.1 Server-Owned Match State — **COMPLETE**, `8178110`.
2. 2.2 Command Channel — **COMPLETE**, `313e9ef`.
3. 2.3 Server Tick Driver — **COMPLETE**, `ea0435d` (ADR-007).
4. 2.4 Session / Player Identity — **COMPLETE**, `73d1276` (ADR-008). Lifecycle завершается на `Finished`; `MatchPhase.Closed` (teardown/registry disposal) — reserved future state для dedicated/server lifecycle.
5. 2.5 Network Transport — **IN_PROGRESS**: ADR-009 Accepted (OD-1 = LiteNetLib как preferred initial carrier за контрактом `INetworkCarrier`); implementation не начат, зависимость не добавлена.
6. 2.6 Snapshot Networking.
7. 2.7 Reconnect / Resync.

## Key Systems

- `MatchServer`, `MatchConfig`, `LocalMatchHost`;
- `ICommandChannel` и `LocalCommandChannel`;
- authoritative server tick ownership;
- session/player identity;
- command transport и snapshot delivery;
- reconnect/resync foundation.

## Milestones

- 2.1 — complete;
- 2.2 — complete;
- 2.3 — complete (`ea0435d`);
- 2.4 — complete (`73d1276`, ADR-008);
- 2.5 — in progress (ADR-009 Accepted);
- Phase 2 complete — prerequisite для multiplayer integration и M1.

## Acceptance Criteria

- server tick lifecycle не зависит от Client presentation;
- identities и sessions имеют authoritative model;
- commands и snapshots проходят через реальный transport;
- reconnect/resync восстанавливает согласованное authoritative state;
- решения задокументированы ADR и проверены.

## Tests

- unit tests tick/session/transport/protocol boundaries;
- integration tests command → server tick → snapshot;
- reconnect/resync and failure-path tests;
- deterministic regression and compatibility tests;
- конкретные network conditions/thresholds: TBD через R&D.

## Risks

- nondeterminism и desync;
- lifecycle/race errors;
- protocol incompatibility;
- identity/ownership validation gaps;
- reconnect state divergence.

## Out of Scope

- полный RTS gameplay integration — Phase 4;
- полный faction roster — Phase 5;
- production maps/presentation — Phase 7;
- deep scale optimization — Phase 8.

## Definition of Done

Steps 2.1–2.7 реализованы, протестированы и задокументированы; authoritative dedicated-server foundation поддерживает sessions, command/snapshot networking и reconnect/resync. Сейчас Definition of Done **не выполнен**; 2.4 complete (`73d1276`), 2.5 Network Transport — in progress (ADR-009 Accepted, implementation не начат).