# Phase 01 — Simulation Foundation

> Status: **COMPLETE** • M0 Technical Foundation complete

## Goal

Создать детерминированный технический фундамент, на котором могут строиться authoritative multiplayer и RTS gameplay.

## Dependencies

- Unity `6000.5.6f1` project;
- high-level product direction;
- deterministic architecture boundaries.

## Scope

- deterministic Core;
- fixed simulation 20 Hz;
- `MatchServer`;
- `LocalMatchHost`;
- server-owned state;
- Snapshot Protocol v1;
- command foundation;
- tests.

## Key Systems

- `GlobalFront.Core` без Unity API;
- `GlobalFront.Server` и `MatchServer`;
- `MatchConfig` и server-assigned entity ids;
- `LocalMatchHost` authoritative pipeline;
- command validation/ordering;
- snapshot serialization/deserialization.

## Milestones

- M0 Technical Foundation — **COMPLETE**;
- local authoritative pipeline — complete;
- Snapshot Protocol v1 — complete.

## Acceptance Criteria

- deterministic 20 Hz simulation существует;
- server владеет gameplay state;
- Client presentation синхронизируется из snapshots;
- Core/Server boundaries не используют Unity API;
- regression tests подтверждают foundation.

## Tests

- EditMode tests deterministic Core, commands, movement, combat, match config/server и snapshots;
- PlayMode tests local authoritative pipeline;
- current post-Phase-2.2 regression baseline: 161/161 EditMode и 5/5 PlayMode passed.

## Risks

- нарушение deterministic order или protocol compatibility;
- расширение временной Client → Server зависимости;
- смешивание presentation и authoritative state.

## Out of Scope

- network transport и sessions;
- dedicated server process;
- reconnect/resync и replay;
- production RTS content.

## Definition of Done

Все перечисленные foundation systems реализованы, проверены и документированы. **Status: satisfied / COMPLETE.**