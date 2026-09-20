# Phase 2.8 (Network Prototype Playtest 2v2) — Completion Summary

> Статус: **[COMPLETED]** — 2026-09-20
> База: Phase 2.7 [APPROVED] (`Phase27-ReviewGate-c7d68c2.md`); продолжает ADR-011 (OD-18…OD-22)

## 1. Метрики приёмки (верифицированы прогоном 2026-09-20)

| Сьют | Результат | Duration |
|---|---|---|
| EditMode | **655/655 Passed (100%)**, failed=0, skipped=0 | 8.44s |
| PlayMode | **6/6 Passed (100%)**, failed=0, skipped=0 | 14.47s |
| Консоль Unity | **0 ошибок, 0 предупреждений** | — |

- Состав EditMode: 641 baseline (Review Gate 2.7) + 14 новых тестов Phase 2.8 (5 × `Match2v2SessionTests`, 6 × `NetworkHudPresenterTests`, 3 × `Match2v2PlaytestScenarioTests`).

## 2. Шаг 2.8.1 — Сессии 2v2, Command Ownership, abandonment unpause

- **2v2 Topology:** `Runtime/Core/MatchTopology2v2.cs` — 4 слота, Team 0 Red (Players 1–2) / Team 1 Blue (Players 3–4); `AreAllies` / `AreEnemies`; новый `TeamId` (`Runtime/Core/Identifiers.cs`).
- **Command Ownership:** `Runtime/Client/PrototypeCommandQueue.cs` — Move/Attack/Stop отвергают приказы на юнитов с `unit.Owner != _localPlayer` (`"Move/Attack/Stop rejected: not entity owner"`).
- **P2-1 (abandonment unpause):** `SessionManager.SessionGraceExpired` (событие при переходе сессии в Abandoned/Closed) + `HasDisconnectedSessionsInGrace` (Zero-GC); `LocalMatchHost.OnSessionGraceExpired` вызывает `Resume()`, когда в grace-окне не остаётся отключённых сессий; `AdvancePauseTicks` прокачивает grace-экспирацию во время паузы.
- **P2-2 (единый источник паузы):** `SessionManager.SessionDisconnected` — каноничный источник паузы; `LocalMatchHost.OnPlayerDisconnected` помечен `[Obsolete]`.
- Тесты: `Tests/EditMode/Network/Match2v2SessionTests.cs` (5) — авто-resume при экспирации последней grace, пауза при живой grace, ownership-фильтр, топология слотов, накопление grace-тиков в паузе.

## 3. Шаг 2.8.2 — Сетевой HUD и оверлей тактической паузы

- `Runtime/Client/UI/NetworkMatchHudState.cs` — ViewModel: 4 слота (`NetworkPlayerSlotState`: PlayerId/TeamId/`SlotStatus`/PingMs), grace-таймер 200с (`DefaultGraceSeconds`), countdown 5..1; Zero-GC steady state.
- `Runtime/Client/UI/NetworkHudPresenter.cs` — event-driven презентер (host + `SessionManager` + `ClientReconnectCoordinator` + overlay); `IDisposable`.
- `Runtime/Client/UI/TacticalPauseOverlay.cs` — `MonoBehaviour`: оверлей паузы (сообщение, таймер 200с, баннер отсчёта), headless/null-safe, кэшированные строки (Zero-GC).
- `LocalMatchHost` — события `Paused` / `Resumed` / `RealTimeAdvanced` / `PauseTicksAdvanced` для привязки HUD.
- Тесты: `Tests/EditMode/Network/NetworkHudPresenterTests.cs` (6) — активация оверлея и pausing player, точность grace-таймера, countdown 5..1 + очистка, статус Abandoned, headless null-safety, маппинг слотов.
- Сборка: ссылки `UnityEngine.UI` в `GlobalFront.Client.asmdef` и `GlobalFront.Tests.EditMode.asmdef`.

## 4. Шаг 2.8.3 — Сквозной плейтест 2v2 (`Match2v2Fixture`)

`Tests/EditMode/Network/Match2v2PlaytestScenarioTests.cs` (3 сценария):

1. `SimultaneousCombat_MaintainsIdenticalChecksums...` — одновременный бой 2v2, побитовый детерминизм чексумм состояния.
2. `AllyDisconnectDuringCombat_PausesWorld_ResyncsAndRe...` — дисконнект союзника в бою: пауза мира, ресинк, реконнект, продолжение боя.
3. `PlayerAbandonsMatch_GraceExpires_GameResumesInTwoVs...` — окончательный выход игрока: экспирация grace, авто-снятие паузы, продолжение игры 2v1.

## 5. Бэклог

- P2-1, P2-2 — **закрыты** (§2). Остаток: **P2-3** (C0 readiness handshake для dedicated-сервера) → следующие фазы.
- Из backlog Phase 2.7: шифрование / proof-of-possession (OD-15) по-прежнему отложены.

## 6. Решение

Phase 2.8 (Network Prototype Playtest 2v2) — **[COMPLETED]**: 655/655 EditMode, 6/6 PlayMode, чистая консоль. Разрешено: коммит финализации, выбор следующей фазы.
