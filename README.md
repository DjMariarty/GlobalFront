# GlobalFront

GlobalFront — современная реализация фундаментальной RTS-формулы *Generals / Zero Hour* на Unity 6. Проект сохраняет основу: экономика, строительство, производство, пехота, техника, авиация, артиллерия, ПВО, генералы, способности, супероружие, гарнизоны, захват, ветеранство, фракционная асимметрия и multiplayer experience.

Техническая цель — перенести эту формулу на современную архитектуру: authoritative server, deterministic simulation, улучшенные pathfinding, multiplayer, производительность, стабильность, UX, reconnect/resync, replay и диагностика desync.

> **Стек:** Unity `6000.6.2f1` • URP `17.6.0` • uGUI `2.6.0` • C# 9.0 (Zero-GC hot path)  
> **Статус проекта:** Фазы 1, 2 и 3 полностью завершены и сертифицированы `[APPROVED: ZERO DEFECTS]`  
> **Текущий бейзлайн:** **1162/1162 EditMode** + **16/16 PlayMode** = **1178 тестов passed** (100% green, 0 warnings, 0 errors на `4cb8ffa`)  
> **Следующая фаза:** **Phase 4: Multiplayer Gameplay Integration** `[READY / NEXT]`

---

## Product 1.0

В GlobalFront 1.0 входят пять полноценных основных фракций:

1. NATO
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

2. **Phase 2: Multiplayer Foundation — [100% COMPLETED / AUDITED: ZERO DEFECTS]**
   - Authoritative dedicated server stack (`GlobalFront.Server`), `MatchServer`, `TickDriver` (ADR-007).
   - Session & Player Identity (`SessionManager`, ADR-008), 32-байтный криптографический секрет, O(k) grace sweep.
   - Сетевой транспорт (ADR-009, OD-1 = LiteNetLib 1.3.5 за `INetworkCarrier` + собственный `OwnDatagramCarrier`).
   - Дельта-компрессия снапшотов (ADR-010, `DeltaSnapshotWireCodec`, History Ring 120 тиков, keyframe pacing/slicing).
   - Reconnect & Resync (ADR-011, OD-18..OD-22, тактическая пауза, 5-секундный countdown, full state catch-up).
   - Спринты hardening 2.1–2.5 устранили все уязвимости hijack и переполнений (commit `4cb8ffa`).
   - **Grand Re-Audit Phase 2.0:** единогласно сертифицирован тремя независимыми аудиторами (Qwen 3.8 Max, NVIDIA Nemotron 3.5 Super VL, MiniMax M3.1 Flash) с вердиктом **`[APPROVED: ZERO DEFECTS]`** (отчёт: `Artifacts/Phase20-Certification-4cb8ffa-CLAUDE.md`).

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
│   ├── Tests/                # EditMode (1162) и PlayMode (16) тесты
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
