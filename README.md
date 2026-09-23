# GlobalFront

GlobalFront — современная реализация фундаментальной RTS-формулы *Generals / Zero Hour* на Unity 6. Проект сохраняет основу: экономика, строительство, производство, пехота, техника, авиация, артиллерия, ПВО, генералы, способности, супероружие, гарнизоны, захват, ветеранство, фракционная асимметрия и multiplayer experience.

Техническая цель — перенести эту формулу на современную архитектуру: authoritative server, deterministic simulation, улучшенные pathfinding, multiplayer, производительность, стабильность, UX, reconnect/resync, replay и диагностика desync.

> Unity: `6000.6.2f1` • рендер: URP • текущий статус: Фазы 1.0–2.8 полностью завершены и приняты (Grand Audit [APPROVED: ZERO DEFECTS]); в активной разработке Фаза 3 (завершены шаги 3.1, 3.2, OD-29, 3.3 — 711 тестов green)

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

Фазы 1.0–2.8 полностью завершены и приняты: **661/661 EditMode** и **6/6 PlayMode** тестов пройдены (Grand Adversarial Audit **[APPROVED: ZERO DEFECTS]** на `a37f53d`, отчёт: `Artifacts/GrandAudit-Certification-a37f53d.md`). В Фазе 3 (RTS Camera, Controls, Visuals) завершены Шаги 3.1, 3.2, OD-29 и 3.3 (**711/711 EditMode** и **6/6 PlayMode** тестов пройдены).

Краткая сводка для человека или AI-агента: [CURRENT_STATE.md](Documentation/CURRENT_STATE.md).

## Быстрый запуск

1. Откройте в Unity Hub каталог `GlobalFront`, содержащий `Assets`, `Packages` и `ProjectSettings`.
2. Используйте Unity `6000.6.2f1` и дождитесь импорта.
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