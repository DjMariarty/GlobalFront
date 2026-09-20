# Phase 2.7 (Reconnect & Resync) — Review Gate Report

> Статус: **[APPROVED]**
> Дата приёмки: 2026-09-20
> Спецификация: ADR-011 (`Documentation/DECISIONS.md`), OD-18…OD-22; R&D: `Documentation/Research/Phase_02_07_Reconnect_Resync_RND.md`
> Итоговый коммит приёмки: `c7d68c2` (`fix(reconnect): resolve review gate defects P0-1..P0-3, P1-1, D4, D6`)
> Аудиторы: Musa Spark 1.3, Kimi k3 — вердикт **[APPROVE]** (оба)

## 1. Метрики тестов (фактические, из XML)

| Сьют | Файл | Результат | Duration |
|---|---|---|---|
| EditMode | `Artifacts/TestResults/EditMode-reviewgate.xml` | **641/641 Passed (100%)**, failed=0, skipped=0 | 3.37s (`duration="3,3744696"`, start 2026-09-20 01:23:08Z) |
| PlayMode | `Artifacts/TestResults/PlayMode-reviewgate.xml` | **6/6 Passed (100%)**, failed=0, skipped=0 | 13.99s (`duration="13,9991155"`, start 2026-09-20 01:23:34Z) |

- Состав EditMode: 599 baseline + 42 новых теста Phase 2.7 (шаги 2.7.1–2.7.4).
- Zero-GC: 0 аллокаций в куче на горячих путях reconnect/resync (wire-кодеки, rebind, эмиттер/мост/клиент).
- 5% packet loss stress: Passed (шаг 2.7.4, End-to-End Integration + Tactical Pause).

## 2. Закрытые дефекты Review Gate

- **P0-1 & P0-2:** `LocalMatchHost.DefaultDisconnectGraceTicks = 4000` (200с @ 20 Гц), `AutoPauseOnDisconnect = true` (default), автопауза по `SessionDisconnected` (`Runtime/Client/LocalMatchHost.cs`).
- **P0-3:** `PumpPausedSlices()` при паузе (`LocalMatchHost` → `ServerReplicationEmitter.PumpPausedSlices()`); порядок ReconnectResponse (C0) до нарезки C2; удалены ложные early-returns.
- **P1-1:** авто-снятие с паузы (`Resume()`) по окончании 5с отсчёта через `ClientReconnectCoordinator.ReadyToResume` (`DefaultCountdownSeconds = 5.0`); подписка `coordinator.ReadyToResume += Resume`.
- **D4:** `ConnectDenyReason.SessionResumeRejected = 5` (`Runtime/Server/Transport/TransportProtocol.cs`); устаревшие resume-запросы отклоняются через `SendConnectDenied(..., SessionResumeRejected)` (`ServerTransportHost.cs`).
- **D6:** буфер `KeyframeStaging` предвыделен: `ClientState.DefaultKeyframeStagingBytes = 159744` байта (4096 × 39, `ServerReplicationEmitter.cs`); буфер удерживается между detachments (no LOH GC-spike в тик-цикле).

## 3. Соответствие ADR-011 (OD-18…OD-22)

- OD-18 Tactical Pause 200с — реализована (`TickDriver` pause/resume, `IsPaused`, синхронный таймер).
- OD-19 сохранение очереди команд — fence по `ReconnectResponse.CurrentTick` / `LastAcceptedCommandSequence`, подъём клиентского `_nextSequence` только вверх, без переотправки неподтверждённого.
- OD-20 resync + 5с countdown — keyframe застывшего мира, `Resyncing → Countdown → Connected`.
- OD-21 anti-hijacking — 32-байтный `SessionSecret` (C0 opcode 9, 33 B), `ReconnectRequest` (C0 opcode 12, 64 B), `ReconnectResponse` (C0 opcode 13, 92 B), валидация `FixedTimeEquals`, ротация + retention.
- OD-22 закрытие D1–D6 baseline — выполнено (см. §2 + шаги 2.7.1–2.7.4).

## 4. Бэклог P2 → Phase 2.8

- **P2-1:** авто-снятие паузы при экспирации брошенной сессии (abandonment).
- **P2-2:** единый каноничный вызов паузы через `SessionDisconnected`.
- **P2-3:** C0 readiness handshake для dedicated-сервера.

## 5. Решение

Phase 2.7 (Reconnect & Resync) — **[APPROVED]**. ADR-011 — [ACCEPTED / APPROVED]. Разрешено: финализация документации, коммит приёмки, переход к Phase 2.8 (Network Prototype Playtest 2v2).
