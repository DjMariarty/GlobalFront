# GlobalFront Current State

> Быстрый handoff для AI-агента • обновлено 2026-10-21

## Current Phase

**Phase 1 и Phase 3 — [100% COMPLETED / AUDITED: ZERO DEFECTS] • Phase 2 — [100% COMPLETED / P0/P1 = 0 CERTIFIED; все P2/P3 закрыты]**

## Current Task

**Phase 1, 2 и 3 ЗАВЕРШЕНЫ полностью • Следующая фаза: Phase 4 (Multiplayer Gameplay Integration) — [READY / NEXT]**

- **Phase 1 (Simulation Foundation):** [100% COMPLETED / AUDITED: ZERO DEFECTS]. Спринты hardening 1.1–1.5.1 закрыли concurrency, reentrancy, friendly-fire и snapshot allocations (`5b16a05`..`349a074`).
- **Phase 2 (Multiplayer Foundation):** [100% COMPLETED / P0/P1 = 0 CERTIFIED]. Шаги 2.1–2.8 + спринты hardening 2.1–2.5 (`5c94e07`..`4cb8ffa`). Сертификация Phase 2.0 на `4cb8ffa` подтвердила **P0/P1 = 0**: это строгий состязательный ре-аудит, выполненный Claude на основе коммита `4cb8ffa` (отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`); сам отчёт ограничивает область сертификации P0/P1 и фиксирует открытый на тот момент P2-бэклог (N-02, N-03, N-04) и P3-пакет. Сторонние модели привлекались только на промежуточных этапах ревью и авторами сертификата не являются. Уязвимость N-01 / D-10 / A-01 полностью ликвидирована на 3 независимых слоях. Все оставшиеся замечания (F-1..F-11, F-13..F-15, N-02..N-04) закрыты в Hardening Phase 3.7.1 — см. блоки ниже.
- **Phase 3 (Visual Presentation & RTS Controls):** [100% COMPLETED / AUDITED: ZERO DEFECTS]. Шаги 3.1–3.7 (ADR-012, OD-23..OD-29). Сквозной вертикальный срез на 400 юнитах (`3414ba6` / `cb01ea2`). Замер OD-28: **0.623 ms/frame** — это CPU-пайплайн симуляции и презентации (headless EditMode, 400 юнитов, гард 8.0 ms), а не полный кадр с физическим GPU draw-call; **0 B GC.Alloc** за 60 steady-state кадров.
- **Hardening Phase 3.7.1 (закрытие Grand Audit):** [100% COMPLETED]. Блоки 1–4 (`934840d`..`3a112ad`) закрыли code-level замечания F-1..F-11, F-13..F-15 и N-02..N-04; блок 5 закрыл F-6 и F-10. 17 новых EditMode-тестов (1162 → 1179), bite-прогоны по каждому наблюдаемому дефекту.
- **Следующий шаг:** переход к **Phase 4 (Multiplayer Gameplay Integration)** — соединение проверенного сетевого фундамента Phase 2 с принятым RTS-геймплеем Phase 3.

## Last Commit

`3a112ad` (`fix(presentation): resolve F-11, F-7, and F-8 (Block 4 hardening)`)

## Tests

- EditMode: **1179/1179 passed** (100% green, 0 failed, 0 skipped, 2026-10-21; gate: `Artifacts/TestResults/block5-editmode-final.xml`)
- PlayMode: **16/16 passed** (100% green, 0 failed, 0 skipped, 2026-10-21; gate: `Artifacts/TestResults/block5-playmode-final.xml`)
- Всего: **1195 автоматизированных тестов** (100% green)
- Компиляция: 0 warnings, 0 errors
- OD-28 (400 юнитов): **0.623 ms/frame** — среднее CPU-время тика симуляции и презентационного кадра за 120 итераций в headless EditMode (`createVisuals: false`), гард 8.0 ms, потолок 60 FPS 16.67 ms; это не замер полного кадра с GPU draw-call; **0 B GC.Alloc** за 60 steady-state кадров и 60 loopback-тиков
- Unity: `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0`

## Completed Milestones

- **M0 Technical Foundation — COMPLETE**
- **Phase 1 Simulation Foundation — COMPLETE**
  - Спринт 1.1 (`5b16a05`): pause drain, packet overflow, zero-GC sort, selection cap.
  - Спринт 1.2 (`93b465e`): zero-GC formation slots, unified spawn validation, archetype forward.
  - Спринт 1.3 (`2b275a5`): 2v2 team hostility, friendly fire suppression, team victory outcome.
  - Спринт 1.4 (`e4c73bb`): de-globalize CombatTickResolver instance scratch, thread-safety, Zero-GC.
  - Спринт 1.5 (`3e413a7`): final hardening, reentrancy guard, friendly target rejection, zero-alloc snapshots, physical unit cap.
  - Спринт 1.5.1 (`349a074`): atomic concurrency guard, capacity validation hoisting, instant victory command flush.
- **Phase 2.1 Server-Owned Match State — COMPLETE (`8178110`)**
- **Phase 2.2 Command Channel — COMPLETE (`313e9ef`)**
- **Phase 2.3 Server Tick Driver — COMPLETE (`ea0435d`, ADR-007)**
- **Phase 2.4 Session / Player Identity — COMPLETE (`73d1276`, ADR-008)**
- **Phase 2.5 Network Transport — COMPLETE (ADR-009; LiteNetLib 1.3.5 за контрактом `INetworkCarrier`)**
- **Phase 2.6 Snapshot Networking — COMPLETE (ADR-010; 599/599 EditMode, 6/6 PlayMode)**
  - Step 2.6.1 Core Delta Wire Codec — COMPLETE
  - Step 2.6.2 Server Replication Engine & History Ring — COMPLETE
  - Step 2.6.3 Client Replication Receiver & FSM — COMPLETE (`c49eeda`)
  - Step 2.6.4 Full Integration & Impairment Tests — COMPLETE
- **Phase 2.7 Reconnect & Resync — COMPLETE (ADR-008..ADR-010; 641/641 EditMode, 6/6 PlayMode)**
  - Step 2.7.1 Core Wire Codecs & Model — COMPLETE
  - Step 2.7.2 Server Session Re-attachment & Defect Fixes D1-D6 — COMPLETE
  - Step 2.7.3 Client Reconnect Coordinator & FSM — COMPLETE
  - Step 2.7.4 End-to-End Integration, Tactical Pause & Stress Tests — COMPLETE
- **Phase 2.8 Network Prototype Playtest (2v2) — COMPLETE (Steps 2.8.1–2.8.3)**
- **Phase 2.0 Hardening Sprints 2.1–2.5 & Certification — COMPLETE:**
  - Спринт 2.1 (`5c94e07`): socket hijack guard, O(k) grace sweep, rate limiter and pending ack lifecycle cleanup.
  - Спринт 2.2 (`ceae9a8`): Zero-GC send scratch buffer and receive stale-scan pooling in LiteNetLibCarrier.
  - Спринт 2.3 (`750218c`): canonical PartIndex keyframe assembly and slice timeout aging.
  - Спринт 2.4 (`e6d91ea`): thread-safe tick driver step capture (`baseTick = _tick - (ulong)scheduled` под `_gate`, лок на `Tick => _tick`) и bounded fragment reassembly maps (`SeqByMessageId` и `SnapshotSeqByMessageId` ограничены до `MaxFragmentGroups` (4) с eviction по старому sequence).
  - Спринт 2.5 (`4cb8ffa`): exclusive session-socket binding and cross-session reconnect hijack guard (N-01 / D-10 / A-01). Session-aware guard в `ServerTransportHost.cs:333-356` (`bound.Session != request.SessionId -> Denied`), эксклюзивность в `TransportSessionBinder.Bind`, токен-идентичность в `TransportSessionBinder.Release`. 3 новых регрессионных теста в `TransportFlowAndSecurityTests.cs`.
  - **Phase 2.0 Grand Re-Audit (`4cb8ffa`):** сертификация подтвердила **P0/P1 = 0**. Строгий состязательный ре-аудит выполнен Claude на основе коммита `4cb8ffa` (отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`); отчёт ограничивает область сертификации P0/P1 и фиксирует открытый на тот момент P2-бэклог (N-02, N-03, N-04) и P3-пакет. Сторонние модели участвовали только в промежуточных этапах ревью.
- **M2 Playable RTS Core — COMPLETE (Phase 3.0)**
  - Phase 3.1 RTS Camera & Input — COMPLETE (`59ef38a` → `8144541`, `2b71414`) — [APPROVED: ZERO DEFECTS]
  - Phase 3.2 UnitViewTickBuffer & Interpolation — COMPLETE (`6ab12ad` → `eac0e1d`) — [APPROVED: ZERO DEFECTS]
  - OD-29 UnitKind Replication & UnitCatalog — COMPLETE (`be03002`, `212740a`, `27a10f6`) — [APPROVED: ZERO DEFECTS]
  - Phase 3.3 UnitViewBinder & Object Pooling — COMPLETE (`bcb1e78` → `0243971`, `e69bed1`) — [APPROVED: ZERO DEFECTS]
  - Phase 3.4 Instanced Selection Rings & HP Bars — COMPLETE (`27a10f6` → `b897eea`) — [APPROVED: ZERO DEFECTS]
  - Phase 3.5 Selection System, Screen-Space Drag-Box & RTS Command Issuing — COMPLETE (`cfa24c2`) — [APPROVED: ZERO DEFECTS]
  - Phase 3.6 Minimap & Tactical HUD — COMPLETE (`2a754db`) — [APPROVED: ZERO DEFECTS]
  - Phase 3.7 Vertical Slice Integration, 400-Unit Benchmark, Multi-Frame PlayMode Suite & Architecture Invariants Gate — COMPLETE (`3414ba6`, `cb01ea2`) — [APPROVED: ZERO DEFECTS]
- **Hardening Phase 3.7.1 — Grand Audit remediation — COMPLETE:**
  - Block 1 (`934840d`): F-2 (N-04: control-события `Connected`/`Disconnected` не отбрасываются при переполнении очереди; `Shutdown` очищает rate limiters и pending acks), F-4 (телеметрия cross-session denial), F-5 (изоляция исключений на цепочке `Pump → HandleEvent → HandleMessage`), F-13 (порядок eviction при отклонении фрагмента).
  - Block 2 (`a08beaf`): F-3 (N-03 sink parity 512 для add и remove), F-14 (`Owner` и `AutoAcquireEnemies` включены в `StateChecksum`), F-15 (согласование серверного и клиентского порядка сущностей), F-9 (pacing-комментарии приведены к константам).
  - Block 3 (`a944623`): F-1 (zero-alloc обновление `ClientSession` и живой GC-гейт), N-02 (`ArrayPool` и group stack pool в `FragmentAssembler`: устранены `3+N` аллокаций), мёртвые allocation-метры заменены.
  - Block 4 (`3a112ad`): F-11 (инъекция transport/uplink seam в раннер), F-7 (регистрация URP RenderGraph overlay feature и handoff), F-8 (дисклеймер для OD-28 0.623 ms/frame как CPU-замера презентации).
  - Block 5: F-6 (атрибуция аудита) и F-10 (статус headless transport pump) — документация синхронизирована, сертификация закрытия всех замечаний.

## Next Step

**Phase 4 (Multiplayer Gameplay Integration) — [READY / NEXT]**: соединить принятый сетевой фундамент Phase 2 (`ICommandChannel`, snapshot networking, reconnect/resync, `SessionManager`) с принятым RTS-геймплеем и презентационным стеком Phase 3, чтобы vertical slice из 400 юнитов стал сетевым многопользовательским опытом.

## Backlog

- **[P2 Informational] (Phase 4 FoW)**: при реализации Fog of War (OD-16 / `IReplicationFilter`) в Phase 4 `StateChecksum` должен вычисляться per-client (для отфильтрованного среза мира конкретного клиента), а не глобально по всему серверному миру.
- **[CLOSED] Backlog Phase 2.0 (N-02, N-03, N-04) и P3-пакет Grand Audit (F-1..F-11, F-13..F-15; ID F-12 в отчёте отсутствует)**: закрыты в Hardening Phase 3.7.1 (блоки 1–5). Открытых замечаний аудита нет.

## Important Constraints

- Current runtime: `LocalMatchHost` (ServerHost) работает в клиентском процессе и владеет `MatchServer`, `TickDriver` (engine-independent, в `GlobalFront.Server`) и `SessionManager` (Phase 2.4, ADR-008).
- Server tick lifecycle отделён от Unity client lifecycle (ADR-007). Session/player identity реализованы как server-authoritative layer (ADR-008): PlayerId назначается только сервером, command ingress проходит session gate.
- Сетевой транспорт реализован по ADR-009 (OD-1 = LiteNetLib за контрактом `INetworkCarrier`) и сертифицирован.
- **Headless transport pump (F-10):** `ServerTransportHost.Pump` реализован и покрыт `LiteNetLibCarrierIntegrationTests`, но в Phase 3 не имеет продакшн call-site — вертикальный срез использует `LocalReplicationLoopback`. Подключение к dedicated server loop — Phase 4, Шаг 4.1.
- Reconnect/resync реализованы и приняты (Phase 2.7, OD-18…OD-22) с тактической паузой и 5-секундным countdown.
- Презентационный стек Phase 3 (OD-23..OD-29) доказал Zero-GC steady state и 0.623 ms/frame на 400 активных юнитах.
- Product 1.0 включает пять основных фракций (NATO, Russia, China, GLA, USA); их gameplay/content будет развёрнут в последующих фазах.
- Подфракции не входят в 1.0.
- Не считать будущий roadmap фактом реализации.

## Read Next

- [Phase 04](Phases/Phase_04_Multiplayer_Integration.md)
- [Phase 02](Phases/Phase_02_Multiplayer.md)
- [Architecture](ARCHITECTURE.md)
- [Project Status](PROJECT_STATUS.md)
