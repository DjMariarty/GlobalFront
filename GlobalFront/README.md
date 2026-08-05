# GlobalFront

GlobalFront — экспериментальная RTS на Unity 6 с детерминированной симуляцией и авторитетным сервером.

## Открытие проекта

Клонируйте репозиторий:

```bash
git clone https://github.com/DjMariarty/GlobalFront.git
```

В Unity Hub выбирайте папку `GlobalFront/GlobalFront` внутри клонированного репозитория. Это единственная папка, содержащая одновременно `Assets`, `Packages` и `ProjectSettings`.

Проект зафиксирован на Unity `6000.5.6f1`.

## Структура

```text
GlobalFront/                         # корень Git-репозитория
├── GlobalFront/                     # корень Unity-проекта — выбрать в Unity Hub
│   ├── Assets/
│   │   └── _GlobalFront/
│   │       ├── Runtime/              # Core, Server, Client
│   │       ├── Tests/EditMode/
│   │       ├── Documentation/
│   │       ├── GameContent/
│   │       └── ThirdParty/
│   ├── Artifacts/
│   │   ├── Logs/                     # локальные логи, не версионируются
│   │   ├── TestResults/              # локальные результаты тестов, не версионируются
│   │   └── Releases/                 # релизные артефакты, версионируются
│   ├── Packages/
│   ├── ProjectSettings/
│   ├── README.md
│   └── README_FIRST_RUN_RU.md
└── .gitignore
```

## Текущее состояние

Реализованы детерминированная симуляция, фиксированный игровой тик, RTS-прототип, основа MatchServer и EditMode-тесты.

## Запуск и тесты

1. Откройте `Assets/Scenes/SampleScene`.
2. Нажмите Play.
3. Для EditMode-тестов откройте `Window -> General -> Test Runner`, выберите `EditMode` и нажмите `Run All`.

Подробные инструкции по первому запуску и управлению прототипом находятся в `README_FIRST_RUN_RU.md`.

## Ветки

- `main` — стабильные версии.
- `develop` — активная разработка.
