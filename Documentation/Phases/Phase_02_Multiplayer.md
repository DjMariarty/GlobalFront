# Phase 02 — Multiplayer Foundation

> Status: **CURRENT** • 2.1 complete • 2.2 complete • 2.3 implemented (commit pending)

## Goal

Создать современный authoritative multiplayer foundation до интеграции полного RTS gameplay.

## Dependencies

- Phase 01 complete;
- deterministic command/snapshot contracts;
- R&D и ADR для каждого нового network boundary.

## Scope

1. 2.1 Server-Owned Match State — **COMPLETE**, `8178110`.
2. 2.2 Command Channel — **COMPLETE**, `313e9ef`.
3. 2.3 Server Tick Driver — implemented и проверен (ADR-007); commit pending.
4. 2.4 Session / Player Identity.
5. 2.5 Network Transport.
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
- 2.3 — implemented (commit pending);
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

Steps 2.1–2.7 реализованы, протестированы и документированы; authoritative dedicated-server foundation поддерживает sessions, command/snapshot networking и reconnect/resync. Сейчас Definition of Done **не выполнен**; 2.3 реализован и проверен (commit pending), next — 2.4 Session / Player Identity.