# Журнал изменений GlobalFront

> Исторический журнал подтверждённых изменений; будущий roadmap сюда не переносится как реализованный.

## Unreleased

### Documentation

- Документация синхронизирована с утверждённым Master Development Plan.
- Добавлены `MASTER_GAME_PLAN.md`, product `GDD.md` и короткий `CURRENT_STATE.md`.
- Roadmap заменён утверждённым Phase Plan 0–12 и milestones M0–M8.
- Фазовые документы приведены к единой структуре Goal → Definition of Done.
- Зафиксированы пять основных фракций 1.0 и перенос подфракций в post-1.0.
- Current baseline отделён от approved target architecture.
- ADR-009 Network Transport Architecture (Phase 2.5) — Accepted (OD-1 = LiteNetLib как preferred initial carrier за контрактом `INetworkCarrier`); R&D-документы в `Documentation/Research/`.
- Hardening Phase 3.7.1, Block 5: исправлена атрибуция аудита Phase 2.0 (F-6) — сертификат на `4cb8ffa` является строгим состязательным ре-аудитом Claude с областью P0/P1 = 0, а не «сертификацией трёх независимых аудиторов»; задокументирован статус headless-цикла `ServerTransportHost.Pump` (F-10); статус проекта синхронизирован на **1179 EditMode + 16 PlayMode**.

### Technical Milestones

- Phase 2.1 Server-Owned Match State — `8178110`.
- Phase 2.2 Command Channel — `313e9ef`.
- `ICommandChannel` и `LocalCommandChannel` отделяют client command queue от конкретного local host.
- `StopCommand` включён в command channel baseline.
- Phase 2.3 Server Tick Driver — реализован и принят (ADR-007), commit `ea0435d`: engine-independent `TickDriver` в `GlobalFront.Server` (20 Hz, bounded catch-up, manual/real-time); `LocalMatchHost` (ServerHost) владеет `MatchServer` и `TickDriver`; `FixedSimulationRunner` удалён; server tick lifecycle отделён от Unity client lifecycle.
- Phase 2.4 Session / Player Identity — реализована и принята (ADR-008), commit `73d1276`: opaque `SessionId`/`MatchId` в Core; `SessionManager` в `GlobalFront.Server` — единственный источник `PlayerId` (монотонное назначение по порядку join, без повторного использования); session gate перед неизменным `MatchServer`; `LocalCommandChannel` session-attributed; клиент получает `PlayerId` от сервера (`ClientSession`); match lifecycle завершается на `MatchPhase.Finished` — `Closed` зарезервирован для будущего server lifecycle/teardown.
- Phase 2.5 Network Transport — COMPLETE, `feat: implement network transport (Phase 2.5)`, по ADR-009 (OD-1 = LiteNetLib); финальный независимый review = **APPROVE** (P0=0/P1=0/P2=0): контракт `INetworkCarrier`; собственный детерминированный `OwnDatagramCarrier` поверх `VirtualNetworkPipe`; `LiteNetLibCarrier` (LiteNetLib 1.3.5, real UDP); `ServerTransportHost`/`ClientTransportEndpoint` — host-thread orchestration и атрибуция токен → `ConnectionHandle` → `SessionId` → session gate; `NetworkCommandChannel` реализует `ICommandChannel`.
- Phase 2.6 Snapshot Networking — COMPLETE (ADR-010; delta compression, history ring, keyframe slicing; 599/599 EditMode, 6/6 PlayMode).
- Phase 2.7 Reconnect & Resync — COMPLETE (session reattachment, full state resync, tactical pause sync, 5s countdown; 641/641 EditMode, 6/6 PlayMode).
- Phase 2.8 Network Prototype Playtest (2v2) — COMPLETE (2v2 topology, abandonment unpause, HUD overlay; 661/661 EditMode, 6/6 PlayMode).
- Phase 3.1 RTS Camera & Input — COMPLETE (`59ef38a`, 665 тестов; `8144541`, 723 теста; `2b71414`, 725 тестов; [APPROVED: ZERO DEFECTS]).
- Phase 3.2 UnitViewTickBuffer & Interpolation — COMPLETE (`6ab12ad` → `eac0e1d`, 735 тестов; safe alpha, adaptive delay, [APPROVED: ZERO DEFECTS]).
- OD-29 UnitKind Replication & UnitCatalog — COMPLETE (`be03002`, `212740a`, `27a10f6`, 687 тестов; protocol v2, 40 B `DeltaAddRecord`, [APPROVED: ZERO DEFECTS]).
- Phase 3.3 UnitViewBinder & Object Pooling — COMPLETE (`bcb1e78` → `0243971`, `e69bed1`, 719 тестов; Zero-GC O(1) биндинг, [APPROVED: ZERO DEFECTS]).
- Phase 3.4 Instanced Selection Rings & HP Bars — COMPLETE (`27a10f6` → `b897eea`, 770 тестов; URP 17.6 RenderGraph pass, [APPROVED: ZERO DEFECTS]).
- Phase 3.5 Selection System, Screen-Space Drag-Box & RTS Command Issuing — COMPLETE (`cfa24c2`, 867 EditMode + 6 PlayMode; zero-physics picking, каноническое множество, [APPROVED: ZERO DEFECTS]).
- Phase 3.6 Minimap & Tactical HUD — COMPLETE (`2a754db`, 1043 EditMode + 6 PlayMode; ADR-012, OD-24/OD-27/OD-28; `MinimapProjection`, `MinimapRadarModel`, батченый `MinimapRadarGraphic` в 1 UI draw call, `MinimapInteractionController`, `TacticalHudPointerBlocker`, `PermanentHudPresenter`, `SelectionHudPresenter`; 0 B GC steady state, [APPROVED: ZERO DEFECTS]).
- Phase 3.7 Vertical Slice Integration, 400-Unit Benchmark, Multi-Frame PlayMode Suite & Architecture Invariants Gate — COMPLETE (`3414ba6`, `cb01ea2`; ADR-012, OD-23..OD-29; `TacticalVerticalSliceRunner`, `LocalReplicationLoopback`, `CorpseReapSnapshotSource`; 400 юнитов end-to-end; фиксы `ComputeStateChecksum` и `ComputeDirtyMask`; 30 тестов бенчмарка, 7 мультикадровых PlayMode, 12 архитектурных гейтов; профиль OD-28: 0.623 ms/frame, 0 B GC; 1086 EditMode + 13 PlayMode; [APPROVED: ZERO DEFECTS]).
- Phase 1.0 Hardening Sprints 1..5.1 — COMPLETE (`5b16a05`..`349a074`):
  - Спринт 1.1 (`5b16a05`): pause drain, packet overflow, zero-GC sort, selection cap.
  - Спринт 1.2 (`93b465e`): zero-GC formation slots, unified spawn validation, archetype forward.
  - Спринт 1.3 (`2b275a5`): 2v2 team hostility, friendly fire suppression, team victory outcome.
  - Спринт 1.4 (`e4c73bb`): de-globalize CombatTickResolver instance scratch, thread-safety, Zero-GC.
  - Спринт 1.5 (`3e413a7`): final hardening, reentrancy guard, friendly target rejection, zero-alloc snapshots, physical unit cap.
  - Спринт 1.5.1 (`349a074`): atomic concurrency guard, capacity validation hoisting, instant victory command flush.
- Phase 2.0 Hardening Sprints 2.1..2.5 — COMPLETE (`5c94e07`..`4cb8ffa`):
  - Спринт 2.1 (`5c94e07`): socket hijack guard, O(k) grace sweep, rate limiter and pending ack lifecycle cleanup.
  - Спринт 2.2 (`ceae9a8`): Zero-GC send scratch buffer and receive stale-scan pooling in `LiteNetLibCarrier`.
  - Спринт 2.3 (`750218c`): canonical PartIndex keyframe assembly and slice timeout aging.
  - Спринт 2.4 (`e6d91ea`): thread-safe tick driver step capture (`baseTick = _tick - (ulong)scheduled` под `_gate`, лок на `Tick => _tick`) и bounded fragment reassembly maps (`SeqByMessageId` и `SnapshotSeqByMessageId` ограничены до `MaxFragmentGroups = 4` с eviction по старому sequence).
  - Спринт 2.5 (`4cb8ffa`): exclusive session-socket binding and cross-session reconnect hijack guard (N-01 / D-10 / A-01). Session-aware guard в `ServerTransportHost.cs:333-356` (`bound.Session != request.SessionId -> Denied`), эксклюзивность в `TransportSessionBinder.Bind`, токен-идентичность в `TransportSessionBinder.Release`. 3 новых регрессионных теста в `TransportFlowAndSecurityTests.cs`.
- Grand Re-Audit Certification of Phase 2.0 (`4cb8ffa`): сертификация подтвердила **P0/P1 = 0**. Строгий состязательный ре-аудит выполнен Claude на основе коммита `4cb8ffa` (отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`); отчёт ограничивает область сертификации P0/P1 и фиксирует открытый на тот момент P2-бэклог (N-02, N-03, N-04) и P3-пакет. Сторонние модели участвовали только в промежуточных этапах ревью.
- Hardening Phase 3.7.1 — Grand Audit remediation — COMPLETE (`934840d`..`3a112ad` + Block 5 docs):
  - Block 1 (`934840d`): F-2 (N-04: control-события `Connected`/`Disconnected` не отбрасываются при переполнении очереди; `Shutdown` очищает rate limiters и pending acks), F-4 (телеметрия cross-session denial), F-5 (изоляция исключений на цепочке `Pump → HandleEvent → HandleMessage`), F-13 (порядок eviction при отклонении фрагмента).
  - Block 2 (`a08beaf`): F-3 (N-03 sink parity 512 для add и remove), F-14 (`Owner` и `AutoAcquireEnemies` включены в `StateChecksum`), F-15 (согласование серверного и клиентского порядка сущностей), F-9 (pacing-комментарии приведены к константам).
  - Block 3 (`a944623`): F-1 (zero-alloc обновление `ClientSession` и живой GC-гейт), N-02 (`ArrayPool` и group stack pool в `FragmentAssembler`), мёртвые allocation-метры заменены.
  - Block 4 (`3a112ad`): F-11 (инъекция transport/uplink seam в раннер), F-7 (регистрация URP RenderGraph overlay feature и handoff), F-8 (дисклеймер для OD-28 0.623 ms/frame как CPU-замера презентации).
  - Block 5: F-6 (атрибуция аудита), F-10 (статус `ServerTransportHost.Pump`) — документация синхронизирована.
  - Верификация: 17 новых EditMode-тестов (1162 → 1179), bite-прогоны по каждому наблюдаемому дефекту.

### Verified

- Unity `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0`.
- **1179/1179 EditMode passed (100%)**, 0 failed, 0 skipped, 2026-10-21 (gate: `Artifacts/TestResults/block5-editmode-final.xml`).
- **16/16 PlayMode passed (100%)**, 0 failed, 0 skipped, 2026-10-21 (gate: `Artifacts/TestResults/block5-playmode-final.xml`).
- **1195 автоматизированных тестов суммарно** (0 compiler warnings, 0 compiler errors).

## Confirmed Foundation History

| Commit | Milestone |
|---|---|
| `3a112ad` | Hardening Phase 3.7.1 Block 4: transport/uplink seam, URP overlay feature, OD-28 disclaimer |
| `a944623` | Hardening Phase 3.7.1 Block 3: zero-alloc ClientSession update, ArrayPool reassembly |
| `a08beaf` | Hardening Phase 3.7.1 Block 2: sink parity, checksum fields, order agreement, pacing comments |
| `934840d` | Hardening Phase 3.7.1 Block 1: disconnect leak guard, denial telemetry, exception isolation, fragment eviction |
| `4cb8ffa` | Phase 2.0 Remediation Sprint 2.5: exclusive session-socket binding & reconnect hijack guard |
| `e6d91ea` | Phase 2.0 Remediation Sprint 2.4: thread-safe tick driver & bounded fragment reassembly maps |
| `750218c` | Phase 2.0 Remediation Sprint 2.3: canonical PartIndex keyframe assembly |
| `ceae9a8` | Phase 2.0 Remediation Sprint 2.2: Zero-GC send scratch buffer & receive pooling |
| `5c94e07` | Phase 2.0 Remediation Sprint 2.1: socket hijack guard, O(k) grace sweep |
| `349a074` | Phase 1.0 Remediation Sprint 5.1: atomic concurrency guard, capacity validation hoisting |
| `3e413a7` | Phase 1.0 Remediation Sprint 5: final hardening, reentrancy guard, unit cap |
| `cb01ea2` | Phase 3 presentation and selection batching polish |
| `3414ba6` | Phase 3.7 Tactical Vertical Slice, 400-unit benchmark, architecture gates |
| `2a754db` | Phase 3.6 Minimap & Tactical HUD |
| `cfa24c2` | Phase 3.5 Selection System & Screen-Space Drag-Box |
| `b897eea` | Phase 3.4 Instanced Selection Rings & HP Bars remediation |
| `27a10f6` | Phase 3.4 Instanced Selection Rings & HP Bars |
| `bcb1e78` | Phase 3.3 UnitViewBinder & Object Pooling |
| `be03002` | OD-29 UnitKind Replication & UnitCatalog |
| `6ab12ad` | Phase 3.2 UnitViewTickBuffer & Interpolation |
| `59ef38a` | Phase 3.1 RTS Camera & Input |
| `a37f53d` | Grand Adversarial Audit Phase 1.0–2.8 certification [APPROVED: ZERO DEFECTS] |
| `313e9ef` | Phase 2.2 Command Channel Abstraction |
| `8178110` | Phase 2.1 Server-Owned Match State через `MatchConfig` |
| `b50f474` | Snapshot Serialization Protocol v1 |
| `f4a496f` | PlayMode validation local authoritative pipeline |
| `8e687e3` | Local authoritative `LocalMatchHost` integration |
| `66bc76b` | Unity repository restructure |

## Historical Git Tags

| Tag | Commit | Context |
|---|---|---|
| `v0.2-matchserver` | `90a4bad` | MatchServer milestone |
| `v0.2.1-project-cleanup` | `4a9ff3a` | project cleanup |
| `v0.2.2-pre-restructure-backup` | `4a9ff3a` | pre-restructure backup |
| `v0.3.0-restructure` | `66bc76b` | repository restructure |

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Project Status](PROJECT_STATUS.md)
- [Roadmap](ROADMAP.md)
