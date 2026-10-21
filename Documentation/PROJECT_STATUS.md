# Статус проекта GlobalFront

> Фактический технический статус • обновлено 2026-10-21

## Summary

GlobalFront: Phases 1.0 — 2.8 + Phase 3.0 (Шаги 3.1–3.7) + Hardening Phase 3.7.1 — **[100% COMPLETED / AUDITED]**.
- **Phase 1 Hardening:** Спринты 1.1–1.5.1 (`5b16a05`..`349a074`) закрыли race conditions, reentrancy, friendly fire, unit limits и alloc-free snapshots.
- **Phase 2 Hardening & Grand Re-Audit:** Спринты 2.1–2.5 (`5c94e07`..`4cb8ffa`) устранили socket hijack, rate limit leaks, fragment map overflow, concurrency в TickDriver и cross-session reconnect hijack (N-01 / D-10 / A-01).
- **Phase 2.0 Grand Re-Audit (`4cb8ffa`):** сертификация подтвердила **P0/P1 = 0**. Строгий состязательный ре-аудит выполнен Claude на основе коммита `4cb8ffa` (отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`); отчёт ограничивает область сертификации P0/P1 и фиксирует открытый на тот момент P2-бэклог (N-02, N-03, N-04) и P3-пакет. Сторонние модели участвовали только в промежуточных этапах ревью. Все замечания закрыты в Hardening Phase 3.7.1.
- **Hardening Phase 3.7.1 (Grand Audit remediation):** Блоки 1–4 (`934840d`..`3a112ad`) закрыли F-1..F-11, F-13..F-15 и N-02..N-04 на уровне кода; блок 5 закрыл документальные F-6 и F-10. 17 новых EditMode-тестов, bite-прогоны по каждому наблюдаемому дефекту.
- **Phase 3 (Visual Presentation & RTS Controls):** Шаги 3.1–3.7 (ADR-012, OD-23..OD-29) полностью сертифицированы `[APPROVED: ZERO DEFECTS]` (`3414ba6` / `cb01ea2`). Замер OD-28 на 400 юнитах: **0.623 ms/frame** (CPU-пайплайн симуляции и презентации, headless) и **0 B GC.Alloc** за 60 steady-state кадров.

Проверка текущего состояния: **1179/1179 EditMode passed (100%)** и **16/16 PlayMode passed (100%)** (1195 автоматизированных тестов суммарно, 0 failed, 0 skipped; 0 compiler warnings, 0 compiler errors; gates: `Artifacts/TestResults/block5-editmode-final.xml` / `Artifacts/TestResults/block5-playmode-final.xml`, 2026-10-21). Текущая задача / Next Phase — **Phase 4: Multiplayer Gameplay Integration** [READY / NEXT].
 
## Confirmed Baseline
 
| Область | Фактическое состояние |
|---|---|
| Unity | `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0` |
| Core | детерминированный, без Unity API, 20 Hz; opaque `SessionId`/`MatchId`; `DeltaSnapshotWireCodec`, `SnapshotAckCodec` (34 B), `KeyframeSliceCodec`, `ReplicationRequestCodec`, `ReconnectWireCodec` (64 B request / 24 B response); Zero-GC formation slots, hardening sprints 1.1–1.5.1 |
| Server | `MatchServer`, `TickDriver` (thread-safe step capture под lock, ADR-007), `SessionManager` (re-bind token, 32 B cryptographically secure secret, O(k) grace sweep); `Server.Transport` по ADR-009; session-aware reconnect guard (`ServerTransportHost.cs:333-356`), exclusive socket binding (`TransportSessionBinder.cs:51-60`), `ServerReplicationEmitter` интегрирован с тиками, history ring (120 тиков), pacing-слайсингом keyframe и тактической паузой |
| Transport | `INetworkCarrier` + `OwnDatagramCarrier` (детерминированные тесты через `VirtualNetworkPipe`) и `LiteNetLibCarrier` (LiteNetLib 1.3.5, real UDP); `NetworkCommandChannel` реализует `ICommandChannel` с асинхронным авторитетным `CommandAckPayload`; каналы C0/C1/C2; bounded fragment reassembly maps (`MaxFragmentGroups = 4`); Zero-GC send scratch buffer и receive stale-scan pooling; hardening sprints 2.1–2.5; headless-цикл `ServerTransportHost.Pump` покрыт `LiteNetLibCarrierIntegrationTests`, продакшн call-site — Phase 4, Шаг 4.1 (F-10) |
| Client replication | `ClientReplicationReceiver`, `ClientReplicationWorld`, `ReplicationReceiverFSM`, `ClientTransportReplicationBridge`, `ClientReconnectCoordinator`; накат дельт, сборка keyframe-слайсов (canonical PartIndex), 10 Hz `SnapshotAck` по C0, Zero-GC верификация StateChecksum, авто-восстановление и координация реконнекта |
| Local integration | `LocalMatchHost` (ServerHost) внутри Client process: владеет `MatchServer`, `TickDriver` и `SessionManager`; тактическая пауза (OD-18..OD-22), окно ожидания 200с / 4000 тиков (OD-18) и 5-секундный countdown таймер (OD-20); сквозная интеграция верифицирована в EditMode |
| Commands | Move, Attack и Stop; `ICommandChannel` + session-attributed `LocalCommandChannel`; ingress проходит session gate; сохранение команд во время реконнекта |
| Snapshots | Delta Snapshot Core/Server/Client слои Шагов 2.6.1–2.6.4 и Reconnect 2.7.1–2.7.4 завершены, rate-pacing/keyframe slicing/end-to-end integration и loss stress (1–5% loss) верифицированы; независимый аудит Phase 2.6 = APPROVE (zero P0/P1 blockers); hardening sprints 2.1–2.5 и Hardening Phase 3.7.1 (F-3 sink parity, F-14 checksum fields, F-15 order agreement, F-9 pacing) закрыли репликационные дефекты |
| Presentation | RTS camera (`RtsCameraController`) и input layer (`RtsInputManager` с `IsShiftPressed` / `StopRequested` на `X`/`H`) — Шаг 3.1; `UnitViewTickBuffer` (SoA, 37 B/slot, safe alpha, adaptive 10/5Hz delay) — Шаг 3.2 (ADR-012, OD-23); `UnitView`, `UnitViewPool`, `UnitViewBinder` (O(1)-биндинг слотов репликации к представлениям, Zero-GC, без `Instantiate`/`Destroy` в бою) — Шаг 3.3 (OD-26); `UnitOverlayBatcher` + `UnitOverlayGeometry` + `UnitOverlayRenderPass`/`UnitOverlayRendererFeature` + `UnitOverlay.shader` (инстансированные кольца выделения и HP-бары, 0 B GC, до 250 инстансов на draw) — Шаг 3.4 (OD-25); `UnitPickMath` (zero-physics ray-to-ground по $Y=0$, сквозной `double`-конверт метры → `WorldPointMm`), `UnitSelectionController` (каноническое восходящее множество `EntityId`, одиночный клик, Shift-toggle, двойной клик по `UnitKind`, screen-space маркер, гейт `IsStillSelectable`, `PruneStaleSelection`), `UnitCommandIssuing` (`IssuedCommand`, `IUnitCommandSink`, `UnitCommandChannelSink` → `ICommandChannel`), `UnitSelectionDriver` (покадровая обвязка) и `SelectionMarqueePresenter` (transient-канвас, `SortingOrder = 1000`, 0 B на кадр) — Шаг 3.5 (OD-24); миникарта и тактический HUD в `GlobalFront.Client.UI` (`MinimapProjection`, `MinimapRadarModel`, `MinimapRadarGraphic` в 1 батченый draw call, `MinimapInteractionController`, `TacticalHudPointerBlocker`, `SelectionHudState`, `PermanentHudPresenter`, `SelectionHudPresenter`) — Шаг 3.6 (OD-24, OD-27, OD-28); сквозной вертикальный срез 400 юнитов в `GlobalFront.Client.Presentation` (`TacticalVerticalSliceRunner`, `LocalReplicationLoopback`, `CorpseReapSnapshotSource`) — Шаг 3.7 (OD-23..OD-29) |
| Scene | одна включённая `Assets/Scenes/SampleScene.unity` |
| Tests | **1179/1179 EditMode passed (100%)**, 0 failed, 0 skipped; **16/16 PlayMode passed (100%)**, 0 failed, 0 skipped; **1195 автоматизированных тестов суммарно** (0 warnings, 0 errors; gates: `Artifacts/TestResults/block5-editmode-final.xml`, `Artifacts/TestResults/block5-playmode-final.xml`, 2026-10-21) |

## Phase 1.0 – 2.8 & Remediation Status — [100% COMPLETED / AUDITED]

Технический фундамент Фаз 1.0–2.8 принят и сертифицирован: **0 P0, 0 P1**. Все замечания P2/P3, открытые на момент сертификации `4cb8ffa`, закрыты в Hardening Phase 3.7.1.

- **Phase 1 (RTS Foundation / Simulation):** [100% COMPLETED / AUDITED: ZERO DEFECTS]
  - Спринт 1.1 (`5b16a05`): pause drain, packet overflow, zero-GC sort, selection cap.
  - Спринт 1.2 (`93b465e`): zero-GC formation slots, unified spawn validation, archetype forward.
  - Спринт 1.3 (`2b275a5`): 2v2 team hostility, friendly fire suppression, team victory outcome.
  - Спринт 1.4 (`e4c73bb`): de-globalize CombatTickResolver instance scratch, thread-safety, Zero-GC.
  - Спринт 1.5 (`3e413a7`): final hardening, reentrancy guard, friendly target rejection, zero-alloc snapshots, physical unit cap.
  - Спринт 1.5.1 (`349a074`): atomic concurrency guard, capacity validation hoisting, instant victory command flush.

- **Phase 2 (Multiplayer Foundation):** [100% COMPLETED / P0/P1 = 0 CERTIFIED; все P2/P3 закрыты]
  - 2.1 Server-Owned Match State — [100% COMPLETED / AUDITED] (`8178110`)
  - 2.2 Command Channel — [100% COMPLETED / AUDITED] (`313e9ef`)
  - 2.3 Server Tick Driver — [100% COMPLETED / AUDITED] (`ea0435d`, ADR-007)
  - 2.4 Session / Player Identity — [100% COMPLETED / AUDITED] (`73d1276`, ADR-008)
  - 2.5 Network Transport — [100% COMPLETED / AUDITED] (ADR-009, LiteNetLib 1.3.5 за `INetworkCarrier`)
  - 2.6 Snapshot Networking — [100% COMPLETED / AUDITED] (ADR-010; Шаги 2.6.1–2.6.4 complete; 599 EditMode, 6 PlayMode)
  - 2.7 Reconnect / Resync — [100% COMPLETED / AUDITED] (ADR-011; Steps 2.7.1–2.7.4 complete; 641 EditMode, 6 PlayMode; Zero-GC, tactical pause OD-18..OD-22, 5s countdown OD-20)
  - 2.8 Network Prototype Playtest (2v2) — [100% COMPLETED / AUDITED] (Steps 2.8.1–2.8.3 complete; 661 EditMode, 6 PlayMode)
  - **Phase 2.0 Hardening Sprints 2.1–2.5 (`5c94e07`..`4cb8ffa`):**
    - Спринт 2.1 (`5c94e07`): socket hijack guard, O(k) grace sweep, rate limiter and pending ack lifecycle cleanup.
    - Спринт 2.2 (`ceae9a8`): Zero-GC send scratch buffer and receive stale-scan pooling in LiteNetLibCarrier.
    - Спринт 2.3 (`750218c`): canonical PartIndex keyframe assembly and slice timeout aging.
    - Спринт 2.4 (`e6d91ea`): thread-safe tick driver step capture (`baseTick = _tick - (ulong)scheduled` под `_gate`, лок на `Tick => _tick`) и bounded fragment reassembly maps (`SeqByMessageId` и `SnapshotSeqByMessageId` ограничены до `MaxFragmentGroups` (4) с eviction по старому sequence).
    - Спринт 2.5 (`4cb8ffa`): exclusive session-socket binding and cross-session reconnect hijack guard (N-01 / D-10 / A-01). Session-aware guard в `ServerTransportHost.cs:333-356` (`bound.Session != request.SessionId -> Denied`), эксклюзивность в `TransportSessionBinder.Bind`, токен-идентичность в `TransportSessionBinder.Release`. 3 новых регрессионных теста в `TransportFlowAndSecurityTests.cs`.
  - **Phase 2.0 Grand Re-Audit (`4cb8ffa`):** сертификация подтвердила **P0/P1 = 0**. Строгий состязательный ре-аудит выполнен Claude на основе коммита `4cb8ffa` (отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`); отчёт ограничивает область сертификации P0/P1 и фиксирует открытый на тот момент P2-бэклог (N-02, N-03, N-04) и P3-пакет. Сторонние модели участвовали только в промежуточных этапах ревью.

## Hardening Phase 3.7.1 (Grand Audit remediation) — [100% COMPLETED]

Закрытие всех **15** замечаний Grand Audit (`Artifacts/GrandAudit-Phase3-efbf547-CLAUDE.md`): **F-1..F-11, F-13..F-15** (14 findings; ID `F-12` в отчёте отсутствует), **N-02** и алиасы **N-03 = F-3**, **N-04 = F-2**. N-01 / D-10 / A-01 закрыт ранее в спринте 2.5 (`4cb8ffa`).

- **Block 1 (`934840d`):** F-2 (N-04: control-события `Connected`/`Disconnected` не отбрасываются при переполнении очереди; `Shutdown` очищает rate limiters и pending acks), F-4 (телеметрия cross-session denial), F-5 (изоляция исключений на цепочке `Pump → HandleEvent → HandleMessage`), F-13 (порядок eviction при отклонении фрагмента).
- **Block 2 (`a08beaf`):** F-3 (N-03 sink parity 512 для add и remove), F-14 (`Owner` и `AutoAcquireEnemies` включены в `StateChecksum`), F-15 (согласование серверного и клиентского порядка сущностей), F-9 (pacing-комментарии приведены к константам).
- **Block 3 (`a944623`):** F-1 (zero-alloc обновление `ClientSession` и живой GC-гейт), N-02 (`ArrayPool` и group stack pool в `FragmentAssembler`), мёртвые allocation-метры заменены.
- **Block 4 (`3a112ad`):** F-11 (инъекция transport/uplink seam в раннер), F-7 (регистрация URP RenderGraph overlay feature и handoff), F-8 (дисклеймер для OD-28 0.623 ms/frame как CPU-замера презентации).
- **Block 5:** F-6 (атрибуция аудита) и F-10 (статус headless transport pump) — документация синхронизирована.
- **Верификация:** 17 новых EditMode-тестов (1162 → 1179), bite-прогоны по каждому наблюдаемому дефекту; **1179/1179 EditMode + 16/16 PlayMode** (100% green).

## Phase 3 (Visual Presentation & RTS Controls) — [100% COMPLETED / AUDITED: ZERO DEFECTS]

- 3.1 RTS Camera & Input — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`59ef38a` → `8144541`, `2b71414`).
- 3.2 UnitViewTickBuffer & Interpolation — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (ADR-012, OD-23; `6ab12ad` → `eac0e1d`).
- OD-29 UnitKind Replication & UnitCatalog — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`be03002`, `212740a`, `27a10f6`).
- 3.3 UnitViewBinder & Object Pooling — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (ADR-012, OD-26; `bcb1e78` → `0243971`, `e69bed1`).
- 3.4 Instanced Selection Rings & HP Bars — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (ADR-012, OD-25; `27a10f6` → `b897eea`).
- 3.5 Selection System, Screen-Space Drag-Box & RTS Command Issuing — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (ADR-012, OD-24; `cfa24c2`).
- 3.6 Minimap & Tactical HUD — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (ADR-012, OD-24, OD-27, OD-28; `2a754db`).
- 3.7 Vertical Slice Integration, 400-Unit Benchmark, Multi-Frame PlayMode Suite & Architecture Invariants Gate — **[100% COMPLETED / AUDITED: ZERO DEFECTS]** (`3414ba6`, `cb01ea2`; OD-28: 0.623 ms/frame за 120 кадров, 0 B GC.Alloc).

**Итог: Phase 3 (Шаги 3.1–3.7) — [100% COMPLETED / AUDITED: ZERO DEFECTS]**, milestone **M2 Playable RTS Core** достигнут. Следующая фаза — **Phase 4: Multiplayer Gameplay Integration**.

## Product Scope vs Implementation

Утверждённый Product 1.0 включает пять основных фракций (NATO, Russia, China, GLA, USA), полный Generals-style RTS foundation, multiplayer до 10 игроков, AI, reconnect/resync, replay, desync detection, большие армии и production-quality presentation. Эти требования являются roadmap, а не текущими возможностями прототипа.

Сейчас нет production economy/build/production systems, пяти реализованных фракций, полного AI, карт 1v1–5v5, dedicated server, replay или scale proof 3000+. Reconnect/resync реализованы и приняты (Phase 2.7, OD-18…OD-22). Core/Server/Client слои delta snapshots реализованы и верифицированы (1179 EditMode, 16 PlayMode); независимые аудиты утверждены (zero P0/P1 blockers); scale proof 3000+ запланирован на последующие фазы.

## Backlog

- **[P2 Informational] (Phase 4 FoW)**: при реализации Fog of War (OD-16 / `IReplicationFilter`) в Phase 4 `StateChecksum` должен вычисляться per-client (для отфильтрованного среза видимости конкретного клиента), а не глобально по всему миру симуляции сервера.
- **[CLOSED] Backlog Phase 2.0 (N-02, N-03, N-04) и P3-пакет Grand Audit (F-1..F-11, F-13..F-15; ID F-12 в отчёте отсутствует)**: закрыты в Hardening Phase 3.7.1 (блоки 1–5). Открытых замечаний аудита нет.

## Important Constraints

- Client пока напрямую ссылается на Server из-за local host.
- Server tick lifecycle engine-independent (ADR-007); тот же `TickDriver`/`MatchServer` core будет использован future dedicated host.
- Session/player identity реализованы server-authoritative (ADR-008): PlayerId назначается только сервером. Транспорт реализован по ADR-009 (OD-1 = LiteNetLib 1.3.5 за контрактом `INetworkCarrier`).
- **Headless transport pump (F-10):** `ServerTransportHost.Pump` реализован и покрыт `LiteNetLibCarrierIntegrationTests`, но в Phase 3 не имеет продакшн call-site — срез использует `LocalReplicationLoopback`. Подключение к dedicated server loop — Phase 4, Шаг 4.1.
- Snapshot Protocol v1 сохраняется как legacy/full payload. Delta Snapshot Core/Server/Client слои Шагов 2.6.1–2.6.4 реализованы и утверждены: сквозная связка с transport/tick-loop, rate-pacing, keyframe slicing и loss stress верифицированы.
- Pathfinding и детерминированное разрешение препятствий отсутствуют.
- Детали roster, abilities, stats, balance, generals и map layouts остаются TBD.

## Связанные документы

- [Current State](CURRENT_STATE.md)
- [Architecture](ARCHITECTURE.md)
- [Roadmap](ROADMAP.md)
- [Phase 02](Phases/Phase_02_Multiplayer.md)
- [Phase 03](Phases/Phase_03_RTS_Core.md)
- [Phase 04](Phases/Phase_04_Multiplayer_Integration.md)
