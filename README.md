# GlobalFront

GlobalFront — экспериментальный RTS-прототип на Unity 6 с детерминированной симуляцией и заделом под авторитетный сервер.

## Открытие проекта

Клонируйте репозиторий:

```bash
git clone https://github.com/DjMariarty/GlobalFront.git
```

В Unity Hub выберите корень клонированного репозитория `GlobalFront`: именно в нём находятся одновременно `Assets`, `Packages` и `ProjectSettings`.

Проект зафиксирован на Unity `6000.5.6f1`.

Техническая и архитектурная документация: [Documentation/README.md](Documentation/README.md).

## Структура

```text
GlobalFront/                         # корень Git-репозитория и Unity-проекта
├── Assets/
│   ├── Scenes/SampleScene.unity      # единственная добавленная в Build сцена
│   └── _GlobalFront/
│       ├── Runtime/                  # Core, Server, Client
│       ├── Tests/EditMode/            # EditMode-тесты
│       ├── Documentation/
│       ├── GameContent/
│       └── ThirdParty/
├── Artifacts/
│   ├── Logs/                          # локальные логи, не версионируются
│   ├── TestResults/                   # локальные результаты тестов, не версионируются
│   └── Releases/                      # релизные артефакты
├── Packages/
├── ProjectSettings/
├── README.md
└── README_FIRST_RUN_RU.md
```

## Текущее состояние

Реализованы детерминированный Core на 20 Hz, headless `MatchServer`, локальный RTS-прототип и EditMode-тесты. Проверка на Unity `6000.5.6f1` от 08.08.2026: **102/102 EditMode-тестов пройдены**.

Это пока локальный прототип, а не полноценная сетевая игра: нет транспорта, сериализации снапшотов и клиентской реконсиляции. Клиент создаёт `MatchServer` для проверки регистрации юнитов, но ещё не передаёт ему команды и не получает из него состояние. Подробности и технический долг — в [индексе документации](Documentation/README.md).

## Запуск и тесты

1. Откройте `Assets/Scenes/SampleScene`.
2. Нажмите Play.
3. Для EditMode-тестов откройте `Window -> General -> Test Runner`, выберите `EditMode` и нажмите `Run All`.

Подробные инструкции по первому запуску и управлению прототипом находятся в `README_FIRST_RUN_RU.md`.

## Ветки

- `main` — стабильные версии.
- `develop` — активная разработка.
