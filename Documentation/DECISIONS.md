# Архитектурные и проектные решения (ADR)

> Живой нормативный и исторический журнал • обновлено 2026-08-22

Каждое долговременное решение содержит Context, Alternatives, Decision и Consequences. Детали, которых нет в утверждённом плане, не считаются решёнными.

## ADR-001: Общепроектная документация хранится вне Assets

**Статус:** Accepted, 2026-08-08

### Context

Общепроектные Markdown-файлы не являются Unity assets, но ранее находились внутри `Assets`.

### Alternatives

1. Хранить всё в `Assets`.
2. Вынести все Markdown-файлы без исключений.
3. Хранить центральные документы в `Documentation/`, а локальные README рядом с контентом.

### Decision

Выбран третий вариант.

### Consequences

Центральная документация не импортируется Unity; локальные README `GameContent` и `ThirdParty` сохраняют контекст каталогов.

## ADR-002: Baseline отделяется от рабочей копии и планов

**Статус:** Accepted, 2026-08-12; контекст продуктовой неопределённости superseded утверждённым Master Development Plan 2026-08-15

### Context

Документация должна отличать проверенную реализацию от незавершённой работы и будущего scope.

### Alternatives

1. Описывать roadmap как готовую систему.
2. Игнорировать будущий scope.
3. Явно маркировать current baseline, current work и approved target.

### Decision

Выбран третий вариант. Новый Master Development Plan заменяет прежнее утверждение об отсутствии продуктового scope, но не отменяет разделение plan/implementation.

### Consequences

Будущие системы не объявляются реализованными; `CURRENT_STATE.md` содержит только фактический handoff.

## ADR-003: Product сохраняет фундамент Generals / Zero Hour

**Статус:** Owner Approved, 2026-08-15

### Context

Нужно зафиксировать устойчивую продуктовую формулу GlobalFront.

### Alternatives

1. Создать новую RTS без обязательной связи с исходной формулой.
2. Сохранить фундамент Generals/Zero Hour и модернизировать реализацию.

### Decision

Выбран второй вариант. Новые механики добавляются только если усиливают формулу и не создают ненужной сложности.

### Consequences

GDD и фазовые решения должны поддерживать экономику, base building, production, виды войск, генералов, способности, супероружие, гарнизоны, захват, ветеранство, асимметрию и multiplayer experience.

## ADR-004: Пять основных фракций входят в 1.0; подфракции не входят

**Статус:** Owner Approved, 2026-08-15

### Context

Нужна ясная content boundary для 1.0.

### Alternatives

1. Выпустить часть основных фракций.
2. Включить основные и подфракции одновременно.
3. Включить NATO, Russia, China, GLA и USA; перенести подфракции в post-1.0.

### Decision

Выбран третий вариант.

### Consequences

Все пять основных фракций получают representative slice в Phase 3 и полный roster в Phase 5. Система 25 подфракций относится к post-1.0.

## ADR-005: Multiplayer строится вокруг authoritative dedicated server

**Статус:** Owner Approved, 2026-08-15

### Context

Product 1.0 требует 1v1–5v5, FFA, reconnect/resync, replay, desync detection, большие армии и долгие матчи.

### Alternatives

1. Оставить local-only host.
2. Использовать authoritative deterministic dedicated server с command/snapshot границами.

### Decision

Выбран второй вариант.

### Consequences

Phase 2 создаёт networking foundation; Phase 4 интегрирует его с реальным RTS gameplay. Конкретные transport/session/resync решения требуют отдельных ADR.

## ADR-006: Production flow и Phase 0–12

**Статус:** Owner Approved, 2026-08-15

### Context

Нужно связать исследования, решения, реализацию и проверку без буквального Waterfall.

### Alternatives

1. Неформальная последовательность без gates.
2. Жёсткий Waterfall.
3. R&D → ADR → Implementation → Tests → Review → Documentation → Commit с допустимым пересечением фаз.

### Decision

Выбран третий вариант и утверждён Phase Plan 0–12.

### Consequences

Каждая фаза имеет единый Definition of Done. Performance benchmarks начинаются в Phase 3, а Phase 8 остаётся глубокой optimization/reliability фазой.

## ADR-007: Server Tick Driver — engine-independent tick scheduling (Phase 2.3)

**Статус:** Accepted, 2026-08-21 (commit `ea0435d`)

### Context

После Phase 2.1/2.2 authoritative tick lifecycle всё ещё владели client-side `FixedSimulationRunner` (MonoBehaviour в `GlobalFront.Client`): тики authoritative server запускались Unity lifecycle клиентского процесса. Задача Phase 2.3 — отделить authoritative server tick lifecycle от Unity client lifecycle при сохранении общего simulation core для local и будущего dedicated server. Транспорт, session/identity, reconnect/resync, prediction, lobby, matchmaking и dedicated deployment — out of scope.

### Alternatives

1. Оставить `FixedSimulationRunner` источником тиков. Отклонено: client presentation lifecycle остаётся владельцем authoritative ticks.
2. TickDriver в `GlobalFront.Client` как MonoBehaviour. Отклонено: dedicated server host не может использовать Unity client assembly; driver не стал бы общим.
3. Engine-independent `TickDriver` в `GlobalFront.Server`; `LocalMatchHost` выполняет роль ServerHost (владеет `MatchServer` и `TickDriver`); клиент только pump’ит driver. Выбран.

### Decision

- Новый `GlobalFront.Server.TickDriver` (`TickDriverMode.Manual` / `TickDriverMode.RealTime`, событие `TickDue`, `AdvanceRealTime` / `AdvanceManualTick`, `StartRealTime` / `StopRealTime`, `Reset`). Real-time pacing через существующий `Core.FixedStepClock`: 20 Hz, bounded catch-up `MaxCatchUpTicksPerFrame` (4); excess time остаётся queued и дренируется последующими вызовами. Manual mode — один tick на вызов для deterministic tests.
- `LocalMatchHost` (роль ServerHost) владеет `MatchServer` и `TickDriver`. Per-tick двухфазный контракт: `TickStarting` (команды для tick N forward’ятся, пока server на N-1) → `MatchServer.TickOnce` → `TickCompleted` (presentation потребляет snapshots). Command timing contract сохранён: `RequestedTick N` применяется во время tick N.
- `LocalMatchHost.TickOnce()` — deterministic manual advance (возвращает `bool`); real-time — `AdvanceRealTime`.
- `PrototypeRtsController` больше не владеет тиками: pump’ит driver в `Update`, подписан на `TickStarting`/`TickCompleted`; команды запрашиваются на `CurrentTick + 1`.
- `FixedSimulationRunner` удалён; клиентский interpolation alpha вычисляется из driver backlog (значения не меняются).
- `MatchServer.TickOnce` для пустого сервера (без юнитов) инкрементирует tick counter, но не симулирует и не выдаёт terminal (Draw) outcome до инициализации матча — server tick lifecycle независим от match initialization.

### Consequences

- Server tick lifecycle не зависит от Client presentation; та же пара `TickDriver`/`MatchServer` расписывает тики для future dedicated server host (out of scope, будет реализован в соответствующей фазе).
- Protocol-совместимость не нарушена: 20 Hz, tick duration, catch-up, snapshot layout и command contract не изменены; Snapshot Protocol v1 не тронут.
- `LocalMatchHost` сохраняет публичный API Phase 2.1/2.2; поведение `TickOnce()` уточнено (driver-backed, manual mode по умолчанию).
- Local и future dedicated server используют общий simulation core (`MatchServer` + `TickDriver`).

## ADR-008: Session / Player Identity — server-authoritative identity layer (Phase 2.4)

**Статус:** Accepted, 2026-08-21 (implementation и tests завершены; коммит `73d1276`)

### Context

После Phase 2.1–2.3 (commit `ea0435d`) authoritative simulation, command validation и server tick lifecycle существовали, но identity модель отсутствовала: `PlayerId` выбирался caller'ом (`new PlayerId(1)` в клиенте, owners в `MatchConfig`), сервер проверял только диапазон 1..10, а не авторизацию отправителя; SessionId/MatchId/соединения не существовали. Phase 2.4 создаёт transport-agnostic identity layer, на который затем опираются Network Transport (2.5), Snapshot Networking (2.6) и Reconnect/Resync (2.7). Транспорт, реализация reconnect, matchmaking, lobby, prediction, аутентификация и deployment — out of scope.

### Alternatives

1. Расширить `CommandHeader` полем SessionId. Отклонено: ломает Phase 2.2 contract и связывает command layer с attribution, которая является свойством ingress path (transport), а не payload.
2. Встроить session/identity логику в `MatchServer`. Отклонено: смешивает identity authority с simulation; затрудняет общий simulation core для future dedicated host (паттерн ADR-007).
3. Отдельный `SessionManager` в `GlobalFront.Server` как gate перед `MatchServer`; value types `SessionId`/`MatchId` в `GlobalFront.Core`; `CommandHeader` и `MatchServer` не изменяются. Выбран.

### Decision

- `SessionId` и `MatchId` — opaque Guid-backed value types в Core (OD-1); генерация только в Server. Determinism firewall: identity значения не входят в simulation state, snapshot payload или порядок команд (`RequestedTick → PlayerId → Sequence`).
- Адресация участника: `(MatchId, PlayerId)`; `SessionId` — attachment credential сессии к слоту и, до появления аутентификации, reconnect identity handle (OD-4, зафиксированный debt).
- Server — единственный источник PlayerId: назначение монотонное по порядку join, без повторного использования в пределах матча (OD-3); capacity ≤ `MaxPlayers`; старт матча с нулём участников запрещён.
- Состояния: Session `Created → Connected ⇄ Disconnected(grace) → Closed`; Match `Forming → Running → Finished`; слот `Assigned → Connected → Disconnected → Abandoned`. Grace считается в server ticks (OD-2; конкретная длительность TBD). **Lifecycle Phase 2.4 завершается на `Finished`; `MatchPhase.Closed` (teardown/registry disposal) зафиксирован как reserved future state для dedicated/server lifecycle и требует отдельного решения — в Phase 2.4 он не назначается.**
- Session gate: команда допускается к `MatchServer` только при session == Connected, match == Running, slot == Connected и `header.Player` == связанный PlayerId (порядок: UnknownSession → SessionClosed → NoBoundMatch → SessionNotConnected → PlayerMismatch → MatchNotRunning); вся остальная валидация остаётся в `MatchServer` без изменений. Channel contract отображает отказ gate в `MatchCommandRejection.SessionRejected`.
- `MatchConfig` остаётся host-supplied template (OD-6): `TryStartMatch` детерминированно отображает template slots (distinct owners по возрастанию) на joined players в порядке join; roster обязан точно покрывать slots.
- Reconnect-ready semantics: `(SessionId, MatchId, PlayerId)` и last-accepted sequence переживают transport drop в пределах grace window; reconnect = rebind нового `ConnectionHandle` с выдачей `ReconnectReceipt` (identity semantics; реализация resync — Phase 2.7).
- Spectators out of scope (OD-7). `MatchServer` получает только additive accessor `TryGetLastAcceptedSequence` (OD-8). `MatchId` не добавляется в Snapshot Protocol v1 (OD-5; revisiting в Phase 2.6).

### Consequences

- `LocalMatchHost` (ServerHost) дополнительно владеет `SessionManager`, pump’ит grace по `TickCompleted` и переводит session match в Finished при terminal outcome; command ingress проходит session gate (`TrySession*`), raw `TryEnqueue*` пути остаются internal/test путями.
- `LocalCommandChannel` получает обязательный `SessionId`; `PrototypeCommandQueue.ForwardPendingToHost` получает session attribution; клиент получает `PlayerId` от сервера (`ClientSession`), а не выбирает его.
- `ICommandChannel` contract, Snapshot Protocol v1, command ordering и simulation semantics `MatchServer` не изменены.
- Verification: 223/223 EditMode (184 baseline + 39 Phase 2.4) и 5/5 PlayMode; Unity 6000.5.6f1.
- Зафиксированные ограничения: disconnect без явного сигнала не детектируется до transport (2.5); конкретная grace duration — TBD (OD-2); dedicated-host teardown — отдельное будущее решение.

## ADR-009: Network Transport Architecture (Phase 2.5)

**Статус:** Accepted, 2026-08-22 (OD-1 одобрен владельцем: LiteNetLib — preferred initial carrier, subject to implementation and validation). **Реализация: завершена и верифицирована — Phase 2.5 COMPLETE, `feat: implement network transport (Phase 2.5)` (2026-08-29): финальный независимый review = APPROVE, P0=0/P1=0/P2=0; LiteNetLib 1.3.5 validated real-UDP loopback; large C2 fragmentation validated на обоих носителях.**

### Context

После Phase 2.1–2.4 (commit `73d1276`) существуют authoritative simulation, command channel, server tick lifecycle (ADR-007) и session/player identity (ADR-008), но весь трафик остаётся in-process (`LocalCommandChannel`). ARCHITECTURE фиксировал transport technology как TBD. R&D (`Documentation/Research/Phase_02_05_Network_Transport_RND.md`) и draft ADR-009 прошли independent review (rev.2, rev.3) и одобрены владельцем. Phase 2.5 определяет transport contract, на который затем опираются Snapshot Networking (2.6) и Reconnect/Resync (2.7). Snapshot cadence/delta/FoW, реализация reconnect/resync, matchmaking, lobby, authentication, anti-cheat, шифрование, NAT traversal и deployment — out of scope.

### Alternatives

1. TCP-only. Отклонено: head-of-line blocking недопустим для latest-wins snapshot стрима; reliability TCP не контролируется проектом.
2. QUIC. Отложен: избыточная коннект-модель и молодой game-dev ecosystem; кандидат на замену носителя под тем же контрактом.
3. Unity Transport. Отклонён для общего core: Unity-bound пакет не может жить в `GlobalFront.Server` без Unity API; расщепляет стек.
4. Собственный минимальный UDP-слой. Fallback: полный контроль, но полная maintenance/reliability нагрузка (High risk корректности).
5. LiteNetLib как несущий слой. Выбран (OD-1): pure .NET, engine-independent, зрелая reliability, MIT; внутренние ARQ/sequence-механизмы не нормируются — требуется эквивалентность только delivery semantics за контрактом `INetworkCarrier`.

### Decision

- Три канала поверх UDP: **C0 Control** (reliable ordered), **C1 Command** (reliable ordered, client→server), **C2 Snapshot** (unreliable sequenced, latest-wins по `SnapshotTick` конверта C2, payload opaque — Snapshot Protocol v1 не изменяется и транспортом не интерпретируется).
- Разделение version spaces: `CarrierVersion` (конверт собственного носителя, существует только при custom), `MessageVersion` (версия проектной message-модели, проверяется в handshake при любом носителе), `SnapshotProtocol.Version` (payload).
- Delivery semantics, обязательные для любого носителя: reliable ordered C0+C1; unreliable sequenced C2 latest-wins; фрагментация с bounded reassembly (lifetime 2000 мс по инжектируемым часам `ITransportClock`; ≤ 4 групп на peer; ≤ 2048 фрагментов на группу; ≤ 1 MB на сообщение; per-peer budget ≤ 2 MB; admission control → drop старейшей группы); delivery не влияет на авторитетный порядок команд (`RequestedTick → PlayerId → Sequence`). Invariants собственного носителя (при OD-1 = custom): единое uint16 reliable-пространство на направление для C0+C1, отдельное snapshot-пространство без ACK, serial-number arithmetic (RFC 1982), receive window W = 1024, reorder window R = 64.
- Attribution: после handshake `token → ConnectionHandle → SessionId` (64-битный crypto-random токен); команды проходят неизменённый session gate ADR-008; `SessionManager` остаётся единственным источником PlayerId; `SessionId` (Guid) по проводу после handshake не летает.
- Команды: additive `CommandWireCodec` в Core; `CommandHeader` не изменяется. Авторитетные отказы — асинхронный `CommandAck { PlayerId (uint8), CommandSequence (uint32), SessionRejection (uint8), MatchCommandRejection (uint8) }` (C0); `NetworkCommandChannel` возвращает только локальный pre-flight/queued результат; `ICommandChannel` не изменяется.
- Транспорт не владеет тиками: команды дренируются в существующей фазе `TickStarting` (ADR-007); все транспортные таймеры — на инжектируемом монотонном `ITransportClock`; grace Phase 2.4 остаётся в server ticks.
- Concurrency invariant: worker thread принимает только raw datagrams в bounded queue; парсинг, attribution и любые вызовы `SessionManager`/`MatchServer`/binder — только на host/simulation thread.
- Message-size ceiling 1 MB (конфигурируемо) — готовность к full-state resync (2.7) > 64 KB по reliable-пространству; Snapshot Networking здесь не решается.
- Security baseline: magic/version/length фильтры, crypto-random токены, sequence window против replay, rate limiting per endpoint; шифрование и настоящая аутентификация явно отложены.
- OD-1 = **LiteNetLib** как preferred initial transport carrier за контрактом `INetworkCarrier`/`INetworkEndpoint` — subject to implementation and validation; message-модель, attribution, версии, `CommandAck` и lifecycle остаются project-owned; зависимость вводится только в рамках implementation Phase 2.5 (pinned version + ThirdPartyAssetsRegistry), контракт допускает замену вплоть до собственного носителя.

### Consequences

- `GlobalFront.Server` получит `…Server.Transport`, `GlobalFront.Core` — `CommandWireCodec`, `GlobalFront.Client` — `NetworkCommandChannel` и клиентский endpoint pump. `MatchServer`, `TickDriver`, `SessionManager`, `CommandHeader`, Snapshot Protocol v1 не изменяются.
- Тестовый фундамент: детерминированный `VirtualNetworkPipe` с loss/duplication/reorder/corruption/latency/timeout профилями и виртуальными часами; version-space, wrap-around и reassembly-budget проверки; end-to-end multi-client сценарии.
- Timing constants (OD-8) — TBD; retransmission timeout и idle timeout должны быть явно согласованы во время implementation (с keepalive, grace-ожиданиями 2.4 и impairment test matrix).
- Остальные OD (размещение, формат токена, MTU/ceiling, threading, ordering, CommandAck, IPv4) зафиксированы в принятом R&D; их конкретные значения уточняются при implementation без смены архитектуры.

## Open Decision Queue

- Snapshot networking cadence, reconnect/resync, replay и desync diagnostics.
- Точные faction rosters, abilities, generals, stats и balance.
- Economy/build/production rules, map layouts и presentation direction.
- Hardware profiles и pass thresholds для scale benchmarks.

## Связанные документы

- [Master Game Plan](MASTER_GAME_PLAN.md)
- [Architecture](ARCHITECTURE.md)
- [Roadmap](ROADMAP.md)