# GlobalFront Roadmap

> Утверждённая последовательность Product Definition → GlobalFront 1.0

## Current Position

- Phase 1 — **[DONE/PASSED: ZERO DEFECTS]** (hardening-спринты 1.1–1.5.1 закрыли concurrency, reentrancy, friendly fire и snapshot allocs)
- Phase 2 (2.1–2.8 + Hardening 2.1–2.5) — **[DONE/PASSED: P0/P1 = 0 CERTIFIED; все P2/P3 закрыты]** (Grand Re-Audit на `4cb8ffa`: строгий состязательный ре-аудит Claude, сертифицирован **P0/P1 = 0**, отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`; оставшиеся P2/P3 закрыты в Hardening Phase 3.7.1)
- Phase 3 (3.1–3.7) — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (RTS Camera, Controls, Visuals, 400-Unit Vertical Slice, OD-28 benchmark: 0.623 ms/frame — CPU-пайплайн симуляции и презентации, headless, 0 B GC.Alloc)
- Phase 4 (4.1–4.6.5 + M1.1/M1.2) — **[DONE/PASSED: ZERO DEFECTS CERTIFIED]** (Milestone M1.2 1v1 Dedicated Server Prototype сертифицирован Kimi k 3.1 Pro: P0=0, P1=0, P2=0, P3=0 на `66419a8`, OD-48..OD-50; Strict Zero-GC до 895 фрагментов / 1 MiB; live UDP MatchFinished)
- **Текущий бейзлайн:** **1370/1370 EditMode** + **22/22 PlayMode** = **1392 теста** (100% green, 0 compiler warnings, 0 compiler errors на `66419a8` / `6d8a20c`)
- **Следующая фаза:** **Phase 5: Full Five Factions (Faction Asymmetry & Strategic Combat) / Milestone M2** `[READY / NEXT]`

## Phase Roadmap

| Phase | Результат | Milestone | Статус |
|---:|---|---|---|
| 0 | Product Definition: vision, GDD, 1.0 scope и границы post-1.0 | — | master scope approved; detailed TBD remain |
| 1 | Simulation Foundation (Core, deterministic simulation, hardening 1.1–1.5.1) | M0 Technical Foundation | **[DONE/PASSED: ZERO DEFECTS]** |
| 2 | Multiplayer Foundation (2.1–2.8, transport, replication, reconnect, hardening 2.1–2.5 + 3.7.1) | prerequisite для M1 | **[DONE/PASSED: P0/P1 = 0; P2/P3 закрыты]** (`4cb8ffa`..`3a112ad`) |
| 3 | RTS Core + Vertical Slice пяти фракций (RTS Camera, Controls, Visuals, 400-unit slice) | M2 Playable RTS Core | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** |
| 4 | Multiplayer Gameplay Integration (1v1 Dedicated Server Prototype) | M1 Multiplayer Prototype | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** (`66419a8`, OD-48..OD-50, 1392 green) |
| 5 | Full Five Factions (Faction Asymmetry & Strategic Combat, 2v2) | M2 (2v2) / M3 Five Factions | **[NEXT]** |
| 6 | AI | M4 RTS AI | planned |
| 7 | Content + Maps + Presentation | — | planned |
| 8 | Scale / Performance / Reliability | M5 Scale Prototype | planned |
| 9 | Alpha | M6 Alpha | planned |
| 10 | Beta | M7 Beta | planned |
| 11 | Release Candidate | release gate | planned |
| 12 | GlobalFront 1.0 | M8 Release 1.0 | planned |

## Phase 2 Sequence

| Step | Scope | Status |
|---:|---|---|
| 2.1 | Server-Owned Match State | **[DONE/PASSED]** — `8178110` |
| 2.2 | Command Channel | **[DONE/PASSED]** — `313e9ef` |
| 2.3 | Server Tick Driver | **[DONE/PASSED]** — `ea0435d` (ADR-007) |
| 2.4 | Session / Player Identity | **[DONE/PASSED]** — `73d1276` (ADR-008) |
| 2.5 | Network Transport | **[DONE/PASSED]** — ADR-009 (LiteNetLib 1.3.5 за `INetworkCarrier`) |
| 2.6 | Snapshot Networking | **[DONE/PASSED]** — ADR-010 (дельта-сжатие, history ring, keyframe slicing) |
| 2.7 | Reconnect / Resync | **[DONE/PASSED]** — ADR-011 (OD-18..OD-22, тактическая пауза, resync) |
| 2.8 | Network Prototype Playtest (2v2) | **[DONE/PASSED]** — 2v2 topology, abandonment unpause, HUD overlay |
| — | Hardening Sprints 2.1–2.5 | **[DONE/PASSED: P0/P1 = 0]** — `4cb8ffa`: socket hijack guard, Zero-GC send scratch, canonical PartIndex, thread-safe tick driver, bounded fragment maps, exclusive session-socket binding (N-01 eradicated). Сертификация ограничена P0/P1 = 0; P2/P3 закрыты в Hardening Phase 3.7.1. |

## Phase 3 Sequence

| Step | Scope | Status |
|---:|---|---|
| 3.1 | RTS Camera & Input | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `59ef38a` → `8144541`, `2b71414` |
| 3.2 | UnitViewTickBuffer & Interpolation (OD-23) | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `6ab12ad` → `eac0e1d` |
| — | OD-29 UnitKind Replication & UnitCatalog | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `be03002` → `27a10f6` |
| 3.3 | UnitViewBinder & Object Pooling (OD-26) | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `bcb1e78` → `0243971`, `e69bed1` |
| 3.4 | Instanced Selection Rings & HP Bars (OD-25) | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `27a10f6` → `b897eea` |
| 3.5 | Selection, Screen-Space Drag-Box & Command Issuing (OD-24) | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `cfa24c2` |
| 3.6 | Minimap & Tactical HUD (OD-24, OD-27, OD-28) | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `2a754db` (1043/1043 EditMode, 6/6 PlayMode) |
| 3.7 | Vertical Slice Integration & 400-Unit Benchmark (OD-23..OD-29) | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `3414ba6`, `cb01ea2` (1086/1086 EditMode, 13/13 PlayMode; OD-28: 0.623 ms/frame avg over 120 frames, 0 B GC) |
| — | Hardening Phase 3.7.1 (Grand Audit: F-1..F-11, F-13..F-15, N-02..N-04; ID F-12 отсутствует) | **[DONE/PASSED]** — blocks 1–4 `934840d`..`3a112ad` + Block 5 docs; **1179/1179 EditMode + 16/16 PlayMode** |

Hardening Phase 3.7.1 закрывает все замечания Grand Audit (`Artifacts/GrandAudit-Phase3-efbf547-CLAUDE.md`) и сертифицирует Phase 2 + Phase 3 к переходу в Phase 4. Phase 3 закрывает M2 Playable RTS Core и **завершена полностью**: 3.1–3.6 — презентационный стек целиком, 3.7 — его интеграция и профильный замер OD-28 (**400 активных юнитов: 0.623 ms/frame среднее за 120 презентационных кадров с включённым авторитетным тиком симуляции, 0 B GC.Alloc** — далеко ниже 8.0 ms EditMode-гарда и потолка 16.67 ms для 60 FPS), плюс 12 инвариантов архитектуры в `ArchitectureInvariantsTests`.

## Phase 4 Sequence

| Step | Scope | Status |
|---:|---|---|
| 4.1 | Dedicated Server Core Relocation & Headless Runner | **[DONE/PASSED]** — `5f2f29e` (ADR-014, OD-35..OD-37) |
| 4.2 | Client Remote Connection & Handshake | **[DONE/PASSED]** — `1760f17` (OD-38) |
| 4.2.1 | Client Bootstrap CLI `-connect` | **[DONE/PASSED]** — `34e994e` |
| 4.3.1 | Core Visibility Engine & Visibility History Ring | **[DONE/PASSED]** — `e5d2ec0` (OD-39) |
| 4.3.2 | Per-Client Replication Filtering & StateChecksum (OD-39/OD-40) | **[DONE/PASSED]** — `3666834` |
| 4.4.1 | Server FoW Target Validation & Command Rejection Gates (OD-41) | **[DONE/PASSED]** — `4e830c9` |
| 4.4.2 | Client Lead Calculation, Multi-Batch Dispatch & E2E Network Ingress (OD-41) | **[DONE/PASSED]** — `5bdab1e` |
| 4.5.1 | Reconnect Signal Wirings & C0 Transport Integration (OD-18/OD-20) | **[DONE/PASSED]** — `1b9b288` |
| 4.5.2 | View Batch Recycling & Presentation Resync (OD-42) | **[DONE/PASSED]** — `364646b` |
| 4.5.3 | Live Combat Reconnect & Tactical Pause Governance (OD-42 final) | **[DONE/PASSED]** — `57a8cef` |
| 4.5.4 | Runtime Loop Closure & Production Runner Wiring | **[DONE/PASSED]** — `7be1648` |
| 4.6.1 | Reconnect Retry Policy & Socket-Connect Timeout | **[DONE/PASSED]** — `eb679e0` |
| 4.6.2 | Impairment Harness & Presets (Wi-Fi 1% / WAN 5% + Burst) | **[DONE/PASSED]** — `1b38f05` |
| 4.6.3 | Parity under Loss/Latency on 400 Units (OD-40 / OD-43) | **[DONE/PASSED]** — `4d15ace` |
| 4.6.4 | Live UDP PlayMode Harness & Reconnect Path B | **[DONE/PASSED]** — `1eeb3d2`, `491811c`, `3eca5de` |
| 4.6.5 | Synthetic 400-Unit Playtest, Visual Evidence & Zero-GC Transport | **[DONE/PASSED]** — `666f89f`, `828c55d`, `ff344a9`, `366e444`, `1bac832` |
| M1.1 | M1.1 Forensic Seal: устранение 10 дефектов R4/R5 (FoW fail-closed, лобби, ready deadline, grace clock) | **[DONE/PASSED]** — `645a532` (OD-49, 1390 тестов) |
| M1.2 | M1.2 Seal: Strict Zero-GC на 65+ фрагментах до 1 MiB, терминация MatchFinished на real UDP, LiteNetLib race guard | **[DONE/PASSED / AUDITED: ZERO DEFECTS]** — `66419a8` (OD-50, 1370 EditMode + 22 PlayMode = 1392 теста 100% green; сертификат APPROVE Kimi k 3.1 Pro `6d8a20c`) |

Phase 4 соединила сетевой фундамент Phase 2 с принятым RTS-геймплеем Phase 3 в authoritative 1v1 dedicated-серверном прототипе на 400 юнитов. Milestone M1 официально закрыт и сертифицирован независимым состязательным аудитом (`Artifacts/GrandAudit-Phase4-M1-66419a8-KIMI.md`). Следующая фаза — **Phase 5: Full Five Factions (Faction Asymmetry & Strategic Combat)**, включающая Milestone M2 (расширение сетевой топологии до 2v2).

## Cross-Phase Rules

- Performance benchmarks начинаются в Phase 3; Phase 8 выполняет глубокую optimization/reliability работу.
- Все пять основных фракций получают representative vertical slice в Phase 3 и развиваются параллельно до полного roster в Phase 5.
- Phase 4 соединяет сетевой фундамент Phase 2 с реальным RTS gameplay Phase 3.
- Фазы могут пересекаться, но завершение фиксируется только по Definition of Done соответствующего Phase-файла.

## Product 1.0 Boundary

1.0 включает пять основных фракций, 1v1–5v5, FFA, до 10 игроков, AI, reconnect/resync, replay, desync detection, большие армии, 3000+ target, многочасовые матчи, production-quality presentation и стабильный server.

Подфракции, новые фракции/режимы, advanced modding, ranked, spectator expansion и неутверждённая campaign относятся к post-1.0.

## Связанные документы

- [Master Game Plan](MASTER_GAME_PLAN.md)
- [Phases](Phases/README.md)
- [Current State](CURRENT_STATE.md)
- [Architecture](ARCHITECTURE.md)
