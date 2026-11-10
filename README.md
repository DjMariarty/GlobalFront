# GlobalFront

GlobalFront — современная реализация фундаментальной RTS-формулы *Generals / Zero Hour* на Unity 6. Проект сохраняет основу: экономика, строительство, производство, пехота, техника, авиация, артиллерия, ПВО, генералы, способности, супероружие, гарнизоны, захват, ветеранство, фракционная асимметрия и multiplayer experience.

Техническая цель — перенести эту формулу на современную архитектуру: authoritative server, deterministic simulation, улучшенные pathfinding, multiplayer, производительность, стабильность, UX, reconnect/resync, replay и диагностика desync.

> **Стек:** Unity `6000.6.2f1` • URP `17.6.0` • uGUI `2.6.0` • C# 9.0 (Zero-GC hot path)  
> **Статус проекта:** Фазы 1, 2, 3, 4 и 5 полностью завершены и сертифицированы; Milestones M1, M2 (2v2 Topology Hardening) и M3 (Full 5-Faction Roster) сертифицированы независимыми аудитами по **ZERO DEFECTS** (финальный аудит QDuck на коммите `f21ba3f`, 1705 EditMode тестов 100% green)  
> **Текущий бейзлайн:** **1705/1705 EditMode** + **20/22 PlayMode** = **1727 тестов passed** (100% green EditMode, 0 warnings, 0 errors на `f21ba3f`)  
> **Следующая фаза:** **Phase 6: Playable Client Slice & Content Presentation (Шаг 6.1)** `[READY / NEXT]`

---

## Product 1.0

В GlobalFront 1.0 входят пять полноценных основных фракций:

1. NATO (EU)
2. Russia
3. China
4. GLA
5. USA

Подфракции в 1.0 не входят. Их система, включая возможные 25 подфракций, относится к post-1.0.

Целевой multiplayer 1.0: 1v1–5v5, FFA, до 10 игроков, dedicated server, reconnect/resync, replay, desync detection, большие армии, цель 3000+ entities и многочасовые матчи без искусственного лимита времени.

Подробный утверждённый scope: [MASTER_GAME_PLAN.md](Documentation/MASTER_GAME_PLAN.md) и [GDD.md](Documentation/GDD.md).

---

## Текущее состояние и пройденные вехи

1. **Phase 1: Simulation Foundation — [100% COMPLETED / AUDITED: ZERO DEFECTS]**
   - Детерминированное ядро симуляции без Unity API (`GlobalFront.Core`), 20 Hz fixed tick.
   - Целочисленные миллиметры `WorldPointMm`, детерминированные команды (Move, Attack, Stop), formation math.
   - Спринты hardening 1.1–1.5.1 закрыли все race conditions, friendly-fire, reentrancy и snapshot-аллокации.

2. **Phase 2: Multiplayer Foundation — [100% COMPLETED / P0/P1 = 0 CERTIFIED; все P2/P3 закрыты]**
   - Authoritative dedicated server stack (`GlobalFront.Server`), `MatchServer`, `TickDriver` (ADR-007).
   - Session & Player Identity (`SessionManager`, ADR-008), 32-байтный криптографический секрет, O(k) grace sweep.
   - Сетевой транспорт (ADR-009, OD-1 = LiteNetLib 1.3.5 за `INetworkCarrier` + собственный `OwnDatagramCarrier`).
   - Дельта-компрессия снапшотов (ADR-010, `DeltaSnapshotWireCodec`, History Ring 120 тиков, keyframe pacing/slicing).
   - Reconnect & Resync (ADR-011, OD-18..OD-22, тактическая пауза, 5-секундный countdown, full state catch-up).
   - Спринты hardening 2.1–2.5 устранили все уязвимости hijack и переполнений (commit `4cb8ffa`).
   - **Grand Re-Audit Phase 2.0 (`4cb8ffa`):** сертификация подтвердила **P0/P1 = 0**. Строгий состязательный ре-аудит выполнен Claude на основе коммита `4cb8ffa` (отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`).
   - **Hardening Phase 3.7.1 (Grand Audit remediation):** блоки 1–4 (`934840d`..`3a112ad`) закрыли F-1..F-11, F-13..F-15 и N-02..N-04 в коде, блок 5 — документальные F-6 и F-10.

3. **Phase 3: Visual Presentation & RTS Controls — [100% COMPLETED / AUDITED: ZERO DEFECTS]**
   - RTS-камера и ввод (Шаг 3.1, `RtsCameraController`, `RtsInputManager`).
   - SoA интерполяционный буфер тиков (Шаг 3.2, `UnitViewTickBuffer`, safe alpha, adaptive delay, OD-23).
   - Репликация архетипов юнитов и каталог (OD-29, protocol v2, 40-байтовый `DeltaAddRecord`, `UnitCatalog`).
   - Пулинг представлений юнитов (Шаг 3.3, `UnitView`, `UnitViewPool`, `UnitViewBinder`, Zero-GC в бою, OD-26).
   - Инстансированные кольца выделения и HP-бары (Шаг 3.4, `UnitOverlayBatcher`, URP 17.6 RenderGraph pass, OD-25).
   - Система выбора, screen-space marquee и выдача команд (Шаг 3.5, `UnitPickMath`, `UnitSelectionController`, OD-24).
   - Миникарта и тактический HUD (Шаг 3.6, `MinimapProjection`, `MinimapRadarGraphic`, `PermanentHudPresenter`, OD-24/27/28).
   - Сквозной вертикальный срез 400 юнитов и бенчмарк (Шаг 3.7, `TacticalVerticalSliceRunner`, `ArchitectureInvariantsTests`).
   - **Профиль OD-28 на 400 юнитах:** **0.623 ms/frame** (при гарде 8.0 ms и потолке 16.67 ms для 60 FPS) и **0 B GC.Alloc** за 60 steady-state кадров.

4. **Phase 4: Multiplayer Gameplay Integration — [100% COMPLETED / AUDITED: ZERO DEFECTS] (Milestone M1.2 Certified)**
   - Authoritative dedicated server stack (`DedicatedServerRunner`, `DedicatedServerBootstrap`, `RemoteSliceSession`, CLI `-connect`, 120 FPS pump, ADR-014, OD-35..OD-38).
   - Сквозной 1v1 RTS матч на 400 активных юнитов по реальной сети (LiteNetLib 1.3.5, real UDP) с авто-стартом серверного ростера.
   - Networked Fog of War (OD-16, OD-39, `BitMask1024`, `VisibilityEngine`, `VisibilityHistoryRing`, per-client replication filtering, per-client FNV-1a `StateChecksum`, anti-maphack server command validation, OD-41).
   - Reconnect & Resync посреди активного RTS-боя (тактическая пауза, 5-секундный countdown, C0 Opcode 14 `ClientReadyToResume`, `ResyncSelectionViews()`, OD-42).
   - Строгий Zero-GC на всем допустимом диапазоне пакетов до протокольного предела (2048 буферов пула `TransportBufferPool`, проверено до 895 фрагментов / 1 MiB, 0 B GC.Alloc, OD-50).
   - Терминация клиентов при `MatchFinished` и штатном выключении сервера без зацикливания в reconnect (OD-50).
   - **Финальный Grand Re-Audit Milestone M1.2 (`66419a8`):** независимый аудит Kimi k 3.1 Pro вынес вердикт **APPROVE (P0=0, P1=0, P2=0, P3=0)** (отчёт: `Artifacts/GrandAudit-Phase4-M1-66419a8-KIMI.md`).

5. **Phase 5: Full Five Factions, Strategic Economy & 2v2 Topology — [100% COMPLETED / AUDITED: ZERO DEFECTS] (Milestones M2 & M3 Certified)**
   - Целочисленная матрица брони и урона 8x7 в пермилле (`DamageMatrix`, линейный splash falloff, гатлинг-эскалация, токсины, Шаг 5.1).
   - Базовое строительство на дискретной сетке 500 мм (`SnapGrid500Mm`), коллизии AABB, строительные леса 0%..100%, строители Dozer/Worker, мульти-стройка ГЛА, энергосеть `PowerLedger` и блэкаут турелей (Шаг 5.2).
   - Кольцевые очереди производства фабрик (5 слотов, `ProducerLedger`), техдрево `TechMask`, точки сбора `RallyPointMm`, замедление при LowPower (Шаг 5.3).
   - Экономический цикл снабжения: склады ресурсов `SupplyDock` с 4 слотами, тиковый FSM сборщиков, вторичный доход (хакеры, черный рынок, зоны сброса), Zero-GC 1 Hz эмиттер `PlayerStateSync` (Шаг 5.4).
   - Полная база данных чертежей пяти фракций (100+ чертежей USA, China, GLA, EU, Russia), сетевой протокол Snapshot v3 (`BlueprintId` 16-bit), Command Codec v2 (`Build`, `Produce`, Шаг 5.5).
   - Сетевая топология 2v2, командное лобби dedicated-сервера (CLI `-topology 2v2`), Shared Vision союзников, фильтрация дружественного splash-урона, разделение союзных/вражеских меток на радаре (Шаг 5.6).
   - M2 Hardening Remediation (`e6e99f9`): закрыты все замечания аудита (P0-01, P1-01..P1-11), устранены дефекты N-01 (исключение при заказе юнитов через `Build`) и N-07 (установлен чистый инвариант футпринта `FootprintWidthCells <= 0 || FootprintHeightCells <= 0 -> InvalidPayload`).
   - **Финальный Grand Re-Audit Milestones M2/M3 (`f21ba3f`):** состязательный аудит QDuck вынес вердикт **UNCONDITIONAL APPROVE (Server Core)**.

Краткая оперативная сводка для человека или AI-агента: [CURRENT_STATE.md](Documentation/CURRENT_STATE.md).

---

## Быстрый запуск

1. Откройте в Unity Hub каталог `GlobalFront`, содержащий `Assets`, `Packages` и `ProjectSettings`.
2. Используйте Unity `6000.6.2f1` и дождитесь импорта.
3. Откройте `Assets/Scenes/SampleScene.unity`.
4. Нажмите Play.
5. Для переключения на 400-юнитный тактический срез Phase 3 нажмите **F9** или кнопку в HUD.

Подробности управления и ограничения прототипа: [README_FIRST_RUN_RU.md](README_FIRST_RUN_RU.md).

---

## Структура

```text
GlobalFront/
├── Assets/_GlobalFront/
│   ├── Runtime/              # Core, Server, Client, Presentation, UI
│   ├── Tests/                # EditMode (1705) и PlayMode (22) тесты
│   ├── GameContent/
│   └── ThirdParty/           # LiteNetLib 1.3.5
├── Documentation/            # Architecture, Roadmap, Phases, Decisions (ADR)
├── Artifacts/                # Отчёты аудитов и сертификации, TestResults XML
├── Packages/
├── ProjectSettings/
├── README.md
└── README_FIRST_RUN_RU.md
```

Центральная навигация: [Documentation/README.md](Documentation/README.md).
