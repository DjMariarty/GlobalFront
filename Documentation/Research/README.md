# GlobalFront Research

> Каталог R&D-документов: исследования и draft ADR, подготовленные до owner approval.

Принятые ADR живут в [Decisions](../DECISIONS.md). Документы здесь не являются утверждёнными решениями и не меняют статусы фаз.

## Phase 2.5 — Network Transport

Phase 2.5 **COMPLETE** (2026-08-29): ADR-009 реализован и прошёл финальный независимый review = APPROVE (P0=0/P1=0/P2=0); LiteNetLib 1.3.5 validated (real-UDP loopback), large C2 fragmentation validated на обоих носителях. Документы ниже сохраняются как approved research-источник; оставшиеся транспортные заметки — только informational/future (2.6 delta/cadence/FoW, 2.7 reconnect/resync).

| Документ | Статус |
|---|---|
| [Phase 2.5 Network Transport — R&D Report](Phase_02_05_Network_Transport_RND.md) | R&D Report — основание ADR-009 (Accepted, 2026-08-22); реализация верифицирована 2026-08-29 |
| [ADR-009 Network Transport Architecture](ADR-009-Network-Transport-DRAFT.md) | **Accepted, 2026-08-22** (OD-1 = LiteNetLib) — реализован и верифицирован (Phase 2.5 COMPLETE); финальная версия в [DECISIONS.md](../DECISIONS.md) |

## Phase 2.6 — Delta Snapshot Protocol

| Документ | Статус |
|---|---|
| [Phase 2.6 Delta Snapshot Protocol — R&D Report](Phase_02_06_Delta_Snapshot_Protocol_RND.md) | **R&D Report — DRAFT, Owner Decisions Required** (rev. 1); реализация требует отдельного ADR |

## Phase 2.7 — Reconnect & Resync

| Документ | Статус |
|---|---|
| [Phase 2.7 Reconnect & Resync — R&D Report](Phase_02_07_Reconnect_Resync_RND.md) | **R&D Report — Revision 2, Decisions Accepted** (OD-18…OD-22); закреплено в ADR-011 (Accepted) |

## Связанные документы

- [Decisions](../DECISIONS.md)
- [Architecture](../ARCHITECTURE.md)
- [AI Contract](../AI_CONTRACT.md)
- [Phase 02](../Phases/Phase_02_Multiplayer.md)
