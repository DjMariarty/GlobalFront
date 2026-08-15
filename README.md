# GlobalFront

GlobalFront — современная реализация фундаментальной RTS-формулы *Generals / Zero Hour* на Unity 6. Проект сохраняет основу: экономика, строительство, производство, пехота, техника, авиация, артиллерия, ПВО, генералы, способности, супероружие, гарнизоны, захват, ветеранство, фракционная асимметрия и multiplayer experience.

Техническая цель — перенести эту формулу на современную архитектуру: authoritative server, deterministic simulation, улучшенные pathfinding, multiplayer, производительность, стабильность, UX, reconnect/resync, replay и диагностика desync.

> Unity: `6000.5.6f1` • рендер: URP • текущая стадия: Phase 2 — Multiplayer Foundation

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

## Текущее состояние

Сейчас подтверждены детерминированный Core 20 Hz, `MatchServer`, `LocalMatchHost`, server-owned `MatchConfig`, Snapshot Protocol v1 и Command Channel. Phase 1 завершена; в Phase 2 завершены 2.1 и 2.2. Следующая задача — **Phase 2.3 Server Tick Driver**.

Сохранённые результаты проверки от 2026-08-12: **161/161 EditMode** и **5/5 PlayMode** тестов пройдены. Транспорт, сессии, snapshot networking, reconnect/resync и отдельный dedicated server ещё не реализованы.

Краткая сводка для человека или AI-агента: [CURRENT_STATE.md](Documentation/CURRENT_STATE.md).

## Быстрый запуск

1. Откройте в Unity Hub каталог `GlobalFront`, содержащий `Assets`, `Packages` и `ProjectSettings`.
2. Используйте Unity `6000.5.6f1` и дождитесь импорта.
3. Откройте `Assets/Scenes/SampleScene.unity`.
4. Нажмите Play.

Подробности управления и ограничения прототипа: [README_FIRST_RUN_RU.md](README_FIRST_RUN_RU.md).

## Структура

```text
GlobalFront/
├── Assets/_GlobalFront/
│   ├── Runtime/              # Core, Server, Client
│   ├── Tests/                # EditMode и PlayMode
│   ├── GameContent/
│   └── ThirdParty/
├── Documentation/            # Product, GDD, architecture, roadmap, phases
├── Artifacts/                # локальные результаты проверок
├── Packages/
├── ProjectSettings/
├── README.md
└── README_FIRST_RUN_RU.md
```

Центральная навигация: [Documentation/README.md](Documentation/README.md).