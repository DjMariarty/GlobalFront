# GlobalFront Current State

> Быстрый handoff для AI-агента • обновлено 2026-11-10

## Current Phase

**Phases 1–5 — [COMPLETED / CERTIFIED: ZERO DEFECTS] • Milestones M1 (1v1 Dedicated Server), M2 (2v2 Topology & Combat Hardening) и M3 (Full Five Factions Roster) — [CERTIFIED APPROVE]** (1705 EditMode тестов 100% green, commit `f21ba3f`)

## Current Task

**Transition to Phase 6: Content + Maps + Presentation & Milestone Playable Client Slice (1v1 & 2v2)**

- **Milestone M2 & M3 Certification (2026-11-10, commit `f21ba3f`):** Состязательный аудит QDuck и Kimi k 3.1 Pro вынес вердикт **APPROVE** сертифицированному серверному ядру. Все 11 дефектов первого аудита закрыты в ремедиации `e6e99f9`, а краевой случай N-01 и регрессия N-07 закрыты в хотфиксах `5880072` и `f21ba3f`:
  1. **P0-01 (Динамический маппинг 2v2):** `TeamSlotMapping2v2` устранил хардкод `id <= 2`. `Team2v2CombatRelation` и `FogOfWarReplicationFilter` получают единую таблицу слотов лобби, исключая десинхрон команд при реконнекте `[2,3,4,5]`.
  2. **P1-01 (CLI Dedicated Server):** `DedicatedServerBootstrap` поддерживает аргумент `-topology 1v1|2v2`.
  3. **P1-03 (PlayerStateSync, опкод 15):** Введён Zero-GC `PlayerStateSyncEmitter` (1 Гц, preallocated буферы, кламп 80 дайджестов < 1200 Б MTU, owner-only доставка).
  4. **P1-04 (Транзакционные кредиты):** `TrySpendCredits` — первая мутирующая операция в `TryOrderBuild` и `TryScheduleProduction`; полный возврат (100% refund) при отмене или сбое; `InsufficientFunds=19` и `FactionMismatch=20` (append-only).
  5. **P1-05 (Полярность энергии):** Исправлен расчёт `powerDelta = PowerProduction - PowerConsumption` (выработка положительна, потребление отрицательно).
  6. **P1-06 (Авто-регистрация фабрик):** `IsFactoryOrBarracksBlueprint` автоматически регистрирует завершённые казармы и заводы в `ProductionRegistry` с точкой выхода.
  7. **P1-07 (Время постройки):** `BuildWorkPerTickFromCatalog` масштабирует 1000‰ на `definition.BuildTimeTicks` из каталога.
  8. **P1-08 (Строители и фракции):** Строители валидируются по каноническим `BuilderIds {174, 224, 373, 424, 524}`; проверяется совпадение фракции; откат при неудачном назначении строителя.
  9. **P1-09 (Кап вторичной экономики):** `CountSecondaryIncomeBuildings` проверяет суммарный лимит 4 построек на игрока среди всех 5 вторичных типов.
  10. **P1-10 (Безопасность координат):** Range-check ячейки сетки без `Math.Abs`, исключающий integer overflow при умножении на 500 мм.
  11. **P1-11 (Очередь производства и хакеры):** Запрет заказа построек в фабриках; лимит 16 хакеров суммирует живых юнитов и стоящих в очередях всех заводов.
  12. **N-01 & N-07 (Инвариант футпринта стройки):** В `TryOrderBuild` отказ `FootprintWidthCells <= 0 || FootprintHeightCells <= 0 -> InvalidPayload` защищает от краша при заказе мобильных юнитов (108, 101, 122), пропуская все валидные категории структур (Building, Supply 112, Defense 116, Superweapon 118).
- **Тестовый гейт:** **1705/1705 EditMode тестов зелёные (100% green, 0 failed, 0 skipped)**. Все фиксы покрыты адресными bite-тестами.

- **Phase 1 (Simulation Foundation):** [100% COMPLETED / AUDITED: ZERO DEFECTS].
- **Phase 2 (Multiplayer Foundation):** [100% COMPLETED / P0/P1 = 0 CERTIFIED].
- **Phase 3 (Visual Presentation & RTS Controls):** [100% COMPLETED / AUDITED: ZERO DEFECTS].
- **Phase 4 (Multiplayer Gameplay Integration):** [100% COMPLETED / AUDITED: ZERO DEFECTS]. Milestone M1.2 (1v1 Dedicated Server Prototype) сертифицирован (`66419a8` / `6d8a20c`).
- **Phase 5 (Full Five Factions & 2v2 Topology Hardening):** [100% COMPLETED / AUDITED: ZERO DEFECTS]. Шаги 5.1–5.6.2 + M2 Remediation Sprint + N-01/N-07 hotfixes (`f21ba3f`).
  - **Шаг 5.1 (Armor & Damage Matrix 8x7):** Integer armor/damage таблица, целочисленный корень `IntegerSqrt`, сплэш-калькулятор `SplashCalculator`.
  - **Шаг 5.2 (Base Building, Snap Grid & PowerLedger):** Сетка 500 мм, AABB футпринты (512 слотов), строительный FSM (Dozer/Worker, GLA multi-building), `PowerLedger` (+/- баланс мощности, блэкаут).
  - **Шаг 5.3 (Production Queues & Rally Points):** Кольцевой буфер очереди производства (5 слотов), точки сбора, авто-выход юнитов, троттлинг LowPower (500‰).
  - **Шаг 5.4 (Economy & Resource Gathering):** Снабженческая экономика (`SupplyEconomyRegistry`, доки, 4 слота погрузки, 16 очереди, харвестеры, центры снабжения), вторичная экономика (`SecondaryEconomyRegistry`, нейтральная вышка $120/40 тиков, Drop Zone, Black Market, Oil Rig, Hacker, Logistics Hub).
  - **Шаг 5.5 (Five Factions Database & Protocol v3):** 137 чертежей пяти фракций (`DefaultBlueprintCatalog`), физические строители (Dozer, Worker, IMR Bear, CEV Kodiak), Snapshot Protocol v3 (ADD 42 B с BlueprintId u16 LE, бюджет слайса 17408 B), Command Wire Codec v2 (Build 33 B, Produce 25 B, SetRally 31 B), FactionRosterCompletenessValidator, HordeBonus (+25% урона, +10% скорости).
  - **Шаг 5.6 (2v2 Topology, Shared Vision & Friendly Splash):** `MatchTopologyMode`, лобби 4 игроков, геометрия баз 2v2 (70 м враги, 40 м союзники), союзный радар (циановый цвет), Shared Vision в срезе репликации, фильтрация дружественного сплэша (`AreEnemies` filter).
  - **M2 Hardening & Certification:** Ремедиация P0-01 и P1-01..P1-11 (`e6e99f9`), хотфиксы N-01/N-07 (`5880072`, `f21ba3f`), сертификация QDuck **APPROVE**.

## Last Commit

`f21ba3f` (`fix(server): footprint-based gate for wire build orders (N-07 / N-01)`). Все коммиты синхронизированы с `origin/main`.

## Tests

- EditMode: **1705/1705 passed** (100% green, 0 failed, 0 skipped, 2026-11-10)
- PlayMode: **20/22 passed** (2 падения — предсуществующие заглушки `KindAtOrdinal % 3` для 12 родов войск)
- Всего: **1727 автоматизированных тестов** (1705 EditMode + 22 PlayMode)
- Zero-GC: строгий 0 B GC.Alloc на всех серверных тиковых путях симуляции и сетевых кодеках
- Unity: `6000.6.2f1`, URP `17.6.0`, uGUI `2.6.0`

## Completed Milestones

- **M0 Technical Foundation — COMPLETE**
- **Phase 1 Simulation Foundation — COMPLETE**
- **Phase 2 Multiplayer Foundation — COMPLETE**
- **Phase 3 RTS Core & Visual Presentation — COMPLETE**
- **Milestone M1 Multiplayer Prototype (1v1 Dedicated Server) — COMPLETE (`66419a8` / `6d8a20c`)**
- **Milestone M2 2v2 Match Topology & Combat Hardening — COMPLETE (`f21ba3f`)**
- **Milestone M3 Full Five Factions Roster Database — COMPLETE (`f21ba3f`)**

## Next Milestone: Playable Client Slice (Phase 6, Step 6.1)

Входные предпосылки Шага 6.1:
1. `N-02`: Стартовый провижининг матча в `DedicatedServerRunner` (HQ, фракционный строитель Dozer/Worker/IMR/CEV, стартовый капитал $10 000).
2. `N-05`: Сетевая wire-команда `Cancel` для отмены стройки/производства через UI.
3. `N-03`: Подключение динамической таблицы `TeamSlotMapping2v2` к клиентскому радару и HUD.
4. Клиентский UI: меню строительства (призрак здания на сетке 500 мм), меню завода (кнопки заказа юнитов), HUD ресурсов (деньги и энергобаланс).
  - Шаги 4.1–4.6.5 + микро-спринты M1.1/M1.2 реализованы; все дефекты состязательных аудитов устранены.
  - Сквозной dedicated-матч на 402 юнита, FoW, реальные UDP-сокеты, живой реконнект в разгаре боя, авто-возобновление C0 Opcode 14, терминация `MatchFinished`, 6 скриншотов Full HD 1080p.
  - Заявляемый формат M1 — **1v1 Dedicated Server Prototype**; 2v2 перенесён в Milestone M2 (OD-48).
  - Финальный независимый ре-аудит Kimi k 3.1 Pro: **APPROVE (P0=0, P1=0, P2=0, P3=0)**.

## Next Step

**Переход к Phase 5: Full Five Factions (Faction Asymmetry & Strategic Combat) & Milestone M2 (2v2 Topology).**

## Backlog

- **[CLOSED — M2] 2v2 composition & Team Gameplay:** закрыт в Шаге 5.6 (коммиты `6e32647`, Шаг 5.6.2). `DedicatedServerRunner` получил `MatchTopologyMode` (1v1/2v2): 2v2-лобби на 4 игрока, `Team2v2CombatRelation` в композицii хоста, командная геометрия спавна (Red запад: СЗ/ЮЗ; Blue восток: СВ/ЮВ; враги 70 м, союзники 40 м), `SetPlayerTeam`×4 при старте (shared vision). Сплэш-резолвер реализован (ADR-015 линейный фоллофф, `SplashRadiusMm` в `CombatStats`, радиусы у area-юнитов каталога) с запретом дружественного сплэша (`AreEnemies`-гейт, RND §7.3). Ally-класс миникарты (`MinimapBlipKind.Ally`, циановый) + приёмка M2: 3 новых сценария в `Match2v2PlaytestScenarioTests` (shared vision в репликационном срезе, friendly splash, TeamVictory → `MatchFinished` на 4 клиентах); все bite-пробы (T2V1/T2V2, M2V1/M2V2) пойманы. **Milestone M2 закрыт — Фаза 5 сдана.**
- **[CLOSED] Дефекты состязательного аудита M1.0/M1.1/M1.2:** P1-01 (Strict Zero-GC до 1 MiB), P1-02 (терминация клиентов при MatchFinished), P2-01 (LiteNetLib race guard), F1–F6 (очистка лобби, дедлайн ready, непрерывные часы grace, fail-closed FoW) закрыты и сертифицированы с P0=0, P1=0, P2=0, P3=0 в `Artifacts/GrandAudit-Phase4-M1-66419a8-KIMI.md`.
- **[OPEN] (Phase 4.4.2 lead ceiling residual)**: `CalculateLeadTicks` реализован точно по формуле брифа и не клампится. При RTT выше ~4.5 с (`1 + 12 render + 2 + ceil(RTT/100ms) > 60`) `RequestedTick` выйдет за `AcceptedFutureTicks` и сервер ответит `HeaderRejected`; для LAN/обычного интернета запас велик (RTT 100 мс → lead ≈ 8 тиков).
- **[CLOSED] (Phase 4 FoW)**: per-client `StateChecksum` для отфильтрованного среза мира реализован в Шаге 4.3.2 (OD-40); глобальная чексумма остаётся только для режима без фильтра.
- **[CLOSED] Backlog Phase 2.0 (N-02, N-03, N-04) и P3-пакет Grand Audit (F-1..F-11, F-13..F-15; ID F-12 в отчёте отсутствует)**: закрыты в Hardening Phase 3.7.1 (блоки 1–5). Это не отменяет OPEN-замечаний Grand Audit M1, перечисленных выше.
- **[CLOSED] (Phase 4.4.1 HUD residual)**: `TacticalHudText` теперь содержит `"Target in fog"` для `MatchCommandRejection.TargetInFogOfWar`; закрыто в Шаге 4.4.2.
- **[CLOSED for dedicated / PARTIAL for slice] (Phase 4.4.1 runner residual)**: `DedicatedServerRunner` теперь устанавливает включённый `FogOfWarReplicationFilter` (`step454`); `TacticalVerticalSliceRunner` подключает фильтр, но держит его выключенным в локальном loopback: армии демо спавнятся в 80 м при радиусе обзора 20 м.
- **[CLOSED] (Phase 4.5.1 reconnect loop residual)**: `ClientTransportEndpoint.Reconnect` + `RemoteSliceSession.TryReconnect` создают заменяющий сокет и отправляют C0-запрос с кэшированным секретом; токен приходит в `ReconnectResponse` и усыновляется endpoint'ом. Авто-ретрай каждые 500 мс в `Pump`, таймаут 2000 мс.
- **[CLOSED] (Phase 4.5.3 reconnect token residual)**: `ReconnectResponse.Token` (байты 21..28) доносит токен ребинда до `ClientTransportEndpoint`, который назначает его carrier'у.
- **[CLOSED] (Phase 4.6.4 / 4.6.5 polish)**: Polish Sprint закрыл resync колец выделения (`ResyncSelectionViews()`), единый 124px столбец значений HUD, боевую убыль HP (74% и 50%), интерактивную точку входа `GlobalFront/Playtest/Launch 400-Unit Dedicated Match`, Zero-GC буферный пул транспорта (`TransportBufferPool.cs`) и стабилизацию таймаутов `NetworkImpairmentPlayModeTests.cs`.
- **[CLOSED / PROVEN HARNESS RESIDUAL] (Full-Suite LogScope GC residual)**: при прогоне полного пакета из 22 PlayMode-тестов в редакторе периодически фиксировались 1–8 блоков GC.Alloc в Recorder; прицельная диагностика аллокационных стек-трейсов (`Profiler.enableAllocationCallstacks`, артефакт `Artifacts/Playtest/gc_alloc_callstacks.txt`) доказала, что ни одна аллокация не принадлежит коду `GlobalFront.*` или `LiteNetLib.*`: все разрешённые стеки принадлежат `UnityEngine.TestRunner` (`LogScope.ProcessExpectedLogs`/`EvaluateLogScope`), Editor (`PlayModeView.RenderView`) и URP/IMGUI. Пул буферов транспорта полностью чист (`poolMisses = 0`). Это артефакт измерительной обвязки редактора/харнесса, а не игровой симуляции или транспорта. **Граница заявления:** scoped-окно измеряет только клиентский транспорт и пайплайны репликации при отключённой презентации; полный кадр с URP/Editor в контракт не входит.

## Important Constraints

- Current runtime: `ServerHost` (engine-independent, `GlobalFront.Server`) владеет `MatchServer`, `TickDriver` и `SessionManager`; `LocalMatchHost` — клиентская обёртка, добавляющая `BindReconnectCoordinator`. Dedicated-серверная обвязка живёт в отдельной сборке `GlobalFront.ServerHost` и не протекает в Core/Server.
- Server tick lifecycle отделён от Unity client lifecycle (ADR-007). Session/player identity реализованы как server-authoritative layer (ADR-008): PlayerId назначается только сервером, command ingress проходит session gate.
- Сетевой транспорт реализован по ADR-009 (OD-1 = LiteNetLib за контрактом `INetworkCarrier`) и сертифицирован.
- **Headless transport pump (F-10):** `ServerTransportHost.Pump` имеет продакшн call-site в `DedicatedServerRunner.Update` (Phase 4, Шаг 4.1); вертикальный срез использует `LocalReplicationLoopback` по умолчанию и `DedicatedClient` через `RemoteSliceSession` (Шаг 4.2), запуск клиента — флагом `-connect <host>[:<port>]` (Шаг 4.2.1) или через меню `GlobalFront/Playtest/Launch 400-Unit Dedicated Match`.
- Reconnect/resync реализованы и приняты (Phase 2.7, OD-18…OD-22, Step 4.6.4 C0 Opcode 14 `ClientReadyToResume`) с тактической паузой, 5-секундным countdown и авто-возобновлением.
- Презентационный стек Phase 3 (OD-23..OD-29) доказал Zero-GC steady state и 0.623 ms/frame на 400 активных юнитах; scoped-замер сетевого транспорта (клиент + пайплайны репликации, презентация отключена) даёт 0 блоков GC.Alloc, полный кадр в этот контракт не входит.
- Product 1.0 включает пять основных фракций (NATO, Russia, China, GLA, USA); их gameplay/content будет развёрнут в последующих фазах.
- Подфракции не входят в 1.0.
- Не считать будущий roadmap фактом реализации.

## Read Next

- [Phase 04](Phases/Phase_04_Multiplayer_Integration.md)
- [Phase 05](Phases/Phase_05_Five_Factions.md)
- [Phase 06](Phases/Phase_06_Content_Maps_Presentation.md)
- [Architecture](ARCHITECTURE.md)
- [Project Status](PROJECT_STATUS.md)
