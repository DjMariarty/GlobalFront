# Phase 2.5 — Network Transport: R&D Report

> Статус: **R&D Report — основание принятого ADR-009 (Accepted, 2026-08-22; OD-1 = LiteNetLib)** • rev. 3 по итогам independent review rev.2 (P0=0/P1=0) • контрольная точка `73d1276` (Phase 2.4) • реализация верифицирована: Phase 2.5 **COMPLETE** (2026-08-29), финальный независимый review = APPROVE (P0=0/P1=0/P2=0), LiteNetLib 1.3.5 validated real-UDP loopback, large C2 fragmentation validated на обоих носителях, retransmission amplification исправлена, connection/admission hardening завершён; оставшиеся заметки документа — только informational/future (2.6/2.7)
> Документ не является принятым ADR; финальное решение — ADR-009 в [DECISIONS.md](../DECISIONS.md).

## 1. Проверенный текущий technical baseline

Факты проверены по репозиторию на контрольной точке `73d1276` (Phase 2.4 Session / Player Identity); конфликтов между документацией и кодом не обнаружено. Предыдущие контрольные точки: `8178110` (Server-Owned Match State), `313e9ef` (Command Channel), `ea0435d` (Server Tick Driver).

- Deterministic simulation: 20 Hz; целочисленный state; канонический порядок команд `RequestedTick → PlayerId → Sequence`; tick-окна валидации `AcceptedPastTicks = 20`, `AcceptedFutureTicks = 60`; per-player sequence watermark (`DuplicateSequence`) в `MatchServer`.
- Tick-контракт хоста (ADR-007): `TickStarting(N)` (команды для тика N подаются, пока сервер на N−1) → `MatchServer.TickOnce` → `TickCompleted(N)`. `TickDriver` — engine-independent (manual/real-time, bounded catch-up).
- Session/identity (ADR-008): `SessionManager` — единственный источник `PlayerId` (монотонное назначение по порядку join, без повторного использования); session gate с порядком отказов `UnknownSession → SessionClosed → NoBoundMatch → SessionNotConnected → PlayerMismatch → MatchNotRunning`; `ConnectionHandle` — opaque ulong; grace window считается в server ticks; lifecycle матча завершается на `Finished` (`Closed` зарезервирован для будущего teardown).
- `ICommandChannel` (Phase 2.2) возвращает `MatchCommandRejection` **синхронно**; в документации Phase 2.2 явно предусмотрен будущий `NetworkCommandChannel`.
- Snapshot Protocol v1: little-endian; 16-byte header (`uint32 ProtocolVersion`, `uint64 Tick`, `uint32 UnitCount`) + 39 bytes/entity в canonical entity-id order; `SnapshotDeserializer` отвергает version mismatch, truncation и malformed data через `SnapshotSerializationException`; существуют golden-bytes тесты.
- Верификация на момент R&D: 223/223 EditMode, 5/5 PlayMode; Unity `6000.5.6f1`.
- Process rules: Core/Server без Unity API; каждая новая network boundary проходит R&D → ADR; protocol-significant constants требуют compatibility review.

## 2. Recommended architecture

Минимальный **UDP-датаграммный транспорт с тонким reliability-слоем**: единое reliable sequence-пространство на направление (C0+C1) с cumulative ACK + ack-bitmap + retransmission и отдельное sequenced/latest-wins пространство для C2 без ACK; полностью engine-independent, размещённый как `GlobalFront.Server.Transport`; клиентский адаптер в `GlobalFront.Client`. Три виртуальных канала поверх одного UDP-потока. Несущий слой (delivery/фрагментация/сокет) предоставляется либо собственным минимальным носителем, либо зрелой библиотекой за тем же контрактом носителя `INetworkCarrier` — preferred candidate OD-1 по итогам review: **LiteNetLib**; final decision — Owner Decision Required, зависимость до approval не вводится.

```text
Client process                                    ServerHost (local now / dedicated later)
──────────────────────────────────────────────────────────────────────────────────────────
PrototypeCommandQueue                             MatchServer  (unchanged)
        │ ICommandChannel (unchanged)                   ▲
        ▼                                               │ session gate (unchanged, ADR-008)
NetworkCommandChannel (NEW)                     ServerTransportHost (NEW)
   serialize CommandWireCodec (Core, NEW)          ▲ attribution: token → ConnectionHandle
        │ C1 reliable-ordered                       │        → SessionId (SessionManager)
        ▼                                           │
ClientTransportEndpoint (NEW)   ── UDP ──▶  UdpServerEndpoint (NEW)
   C2 snapshots in  ◀── UDP ─────────────────  snapshot out (cadence TBD, 2.6)
   C0 control     ◀──────────── handshake / ack / ping ──────────────▶
```

Ключевые принципы:

1. Simulation core ничего не знает о транспорте: `MatchServer`, `TickDriver`, `SessionManager` не изменяются; транспорт — слой *перед* session gate и *вокруг* snapshot delivery.
2. Транспорт-агностичный контракт: транспорт работает через `INetworkCarrier`/`INetworkEndpoint`; реальная реализация — собственный UDP-носитель или принятая библиотека (OD-1), тестовая — `VirtualNetworkPipe` с управляемыми impairments. Замена носителя (в т.ч. QUIC) возможна без смены протокола.
3. Attribution: после handshake пакет несёт 64-битный server-issued **session token**; маппинг `token → ConnectionHandle → SessionId` живёт в `TransportSessionBinder` и использует существующие API Phase 2.4. `SessionId` (Guid) по проводу после handshake не летает (только в handshake; в 2.7 — для resume).

## 3. Почему выбран этот подход

1. **Разнородные delivery-требования RTS**: снапшотам нужен unreliable latest-wins (устаревший снапшот бесполезен), командам — reliable (потеря команды = потеря ввода), контролю — reliable ordered. Один поток или TCP-only не дают этого без head-of-line blocking снапшотов.
2. **Engine-independence (паттерн ADR-007/008)**: тот же транспортный core должен работать в local prototype и в будущем dedicated host без Unity. Engine independence обеспечивается *чистотой* несущего слоя: и `System.Net.Sockets`, и pure-.NET библиотеки (LiteNetLib) ей удовлетворяют; Unity-bound стеки (Unity Transport) исключены из общего core, поскольку Unity API запрещён в Core/Server.
3. **Минимальная поверхность**: RTS-трафик мал по сообщению (команды — десятки байт, снапшоты — один производитель), поэтому нужен не универсальный стек, а ~3 канала с чётким delivery-контрактом; реализация delivery — собственная или принятая по OD-1. Это делает слой полностью детерминированно тестируемым in-memory.
4. **Контроль над protocol constants** (проект уже ведёт их как protocol-significant: `SimulationConstants`, `SnapshotProtocol`).
5. **Совместимость без изменений**: Snapshot Protocol v1 входит как payload без модификаций (OD-5 ADR-008 соблюдается); `CommandHeader` не расширяется — на проводе его поля кодирует новый additive `CommandWireCodec`.

## 4. Rejected alternatives

| Альтернатива | Почему отклонена |
|---|---|
| TCP-only | Head-of-line blocking: задержка одного reliable сообщения стопорит снапшот-стрим; reliability TCP не контролируется проектом. |
| QUIC | Избыточная коннект-модель и молодой game-dev ecosystem для текущей фазы; остаётся кандидатом на замену носителя под тем же контрактом. |
| Unity Transport | Отклонён для общего core: Unity-bound пакет (Unity.Collections/Burst) не может жить в `GlobalFront.Server` без Unity API; расщепляет стек (клиент Unity / сервер нет). |
| LiteNetLib / ENet как категорию | **Не отклоняются**: pure-.NET LiteNetLib и native ENet engine-independent. Ошибочное утверждение rev. 1 об их несовместимости с «Server без Unity» снято review. Полное сравнение build-vs-adopt — в OD-1 (§16); recommendation — LiteNetLib за контрактом носителя, решение не принято владельцем. |
| Расширить `CommandHeader` полем SessionId/transport-полями | Запрещено без отдельного ADR; attribution — свойство ingress path (зафиксировано ADR-008). |
| Встроить транспорт в `MatchServer`/`SessionManager` | Нарушает authority-модель и детерминизм-файрвол; транспорт — инфраструктурный слой, не авторитетный. |
| WebSocket | TCP-основа → те же HOL-проблемы; нужен для browser-таргета, которого нет в scope. |

## 5. Delivery semantics

| Канал | Семантика | Сообщения | Направление |
|---|---|---|---|
| **C0 Control** | reliable, ordered | `ConnectRequest`, `ConnectAccept`, `ConnectDenied`, `Disconnect`, `Ping/Pong`, `CommandAck` | двунаправленно |
| **C1 Command** | reliable, ordered | `CommandMessage` (wire-кодирование `CommandHeader` + payload Move/Attack/Stop) | client → server |
| **C2 Snapshot** | unreliable, sequenced, latest-wins | `SnapshotMessage` = байты Snapshot Protocol v1 в envelope | server → client |
| ACK | piggyback в исходящих пакетах; cumulative ack + 32-битная bitmap; ссылается **только на единое reliable-пространство C0+C1 данного направления**; пространство C2 не ACKается | — | двунаправленно |

Обоснование: команды нельзя терять (потеря ввода), но их авторитетный порядок всё равно определяется сервером (`RequestedTick → PlayerId → Sequence`), поэтому transport-ordering — courtesy, упрощающий клиент; снапшоты теряются без последствий (следующий покрывает); `CommandAck` идёт reliable, чтобы клиент узнал авторитетный отказ.

## 6. Packet framing

Framing определяется на двух уровнях: carrier-уровень гарантирует delivery (собственный носитель или принятый по OD-1 — семантика ниже обязательна в обоих случаях), message-уровень принадлежит проекту и сохраняется при любом носителе.

### 6.1 Delivery contract и sequence spaces (P1-1, rev.3)

Контракт разделён на два уровня: **delivery semantics, обязательные для любого носителя** (собственного или принятого), и **invariants собственного носителя** (детали реализации, которые проект фиксирует при OD-1 = custom). При принятом носителе (например, LiteNetLib) требуется эквивалентность только delivery semantics; внутренние ARQ/sequence-механизмы носителя могут не совпадать с проектными и не нормируются.

**Delivery semantics, обязательные для любого носителя:**

- C0+C1: reliable ordered доставка на направление;
- C2: unreliable sequenced доставка, latest-wins по `SnapshotTick` конверта C2 (§6.3), толерантная к потерям;
- фрагментация сообщений до max message size (OD-4) с bounded reassembly (бюджеты ниже);
- delivery не влияет на авторитетный порядок команд (`RequestedTick → PlayerId → Sequence`).

**Invariants собственного носителя (OD-1 = custom):**

- **Reliable space (C0+C1)**: единый uint16-счётчик на направление, покрывающий ВСЕ reliable-сообщения (control и команды вперемешку). Это единственное пространство, на которое ссылаются `AckNumber`/`AckBitmap`. Противоречие rev. 1 (per-channel sequence при едином ACK) устранено.
- **Snapshot space (C2)**: отдельный uint16-счётчик для снапшот-сообщений и fragment-групп; **не ACKается** и не влияет на reliability-механику; упорядочивание — sequenced/latest-wins.
- **Wrap-around / сравнение**: serial-number arithmetic (семантика RFC 1982): `d = (a − b) mod 65536`; `a` новее `b` ⟺ `0 < d < 32768`; `d = 0` — дубликат; `d ≥ 32768` — старее. Все сравнения sequence/ack — только через неё.
- **Receive window**: `W = 1024` — reliable-сообщение принимается, если оно новее `AckBase` (наибольший доставленный in-order) и не старше `AckBase + W`; старее → drop (duplicate/replay), совпадение с уже буферизованным → drop.
- **Reorder window**: `R = 64` — out-of-order сообщения в пределах `AckBase + R` буферизуются до in-order доставки; за пределами `AckBase + R` → drop (потеря компенсируется retransmission).
- **Фрагментация**: `MessageId(uint16) + FragmentIndex(uint16) + FragmentCount(uint16)`; фрагменты reliable-сообщения живут в reliable space, фрагменты снапшот-группы — в snapshot space.
- **Reassembly budget per peer (rev.3)**: ≤ **2 MB** (конфигурируемо); верхние границы: ≤ 4 одновременных групп, ≤ 2048 фрагментов на группу, ≤ 1 MB на сообщение, lifetime 2000 мс по transport clock; admission control: превышение бюджета/границ → drop старейшей группы + счётчик (P2-6).

### 6.2 Конверт (случай собственного носителя; little-endian; датаграмм ≤ 1200 байт, OD-4)

```text
Offset  Size  Field
0       2     Magic — фильтр мусора и чужих протоколов
2       2     CarrierVersion (uint16 — версия layout конверта собственного носителя; независима от MessageVersion и SnapshotProtocol.Version)
4       1     Flags: [Channel:2][HasFragmentInfo:1][Reserved:5]
5       8     SessionToken (uint64, server-issued; в handshake — зарезервированное значение 0)
13      2     Sequence (reliable space для C0/C1; snapshot space для C2)
15      2     AckNumber (uint16; ссылается ТОЛЬКО на reliable space приёма данного направления; валиден в любом исходящем пакете)
17      4     AckBitmap (32 бита, selective ack вперёд от AckNumber)
21      2     PayloadLength (uint16)
[если фрагмент: +6  MessageId(uint16) + FragmentIndex(uint16) + FragmentCount(uint16)]
23..    N     Payload
```

При принятом носителе (OD-1) этот конверт отсутствует: носитель предоставляет delivery с эквивалентными semantics (§6.1), а переносится message-конверт §6.3.

### 6.3 Message-уровень (project-owned, при любом носителе)

Каждый message payload начинается проектным заголовком: `Magic(uint16) + MessageVersion(uint16) + MessageType(uint8) + SessionToken(uint64)`; конверт C2 SnapshotMessage дополнительно несёт `SnapshotTick(uint64)`.

**Разделение version spaces (rev.3):**

- `CarrierVersion` — версия layout конверта *собственного носителя* (§6.2); существует только при OD-1 = custom. У принятого носителя собственная внутренняя версия протокола; проект не требует её совпадения ни с чем.
- `MessageVersion` — версия проектной message-модели (layout заголовков, типы сообщений, формат `CommandAck`); несётся в каждом сообщении и проверяется в handshake при любом носителе.
- `SnapshotProtocol.Version` — версия snapshot payload; транспорт payload не интерпретирует (v1 не изменяется).

**C2 carrier envelope (rev.3)**: `SnapshotTick` размещается в конверте C2, и транспорт выполняет latest-wins/discard устаревших групп по нему; payload (байты Snapshot Protocol v1) остаётся для транспорта opaque. Скрытая зависимость транспорта от v1 layout устранена; сам Snapshot Protocol v1 не меняется.

При принятом внешнем носителе (OD-1) carrier-поля §6.2 (sequence/ack/retransmission/фрагментация) предоставляются носителем с delivery semantics, эквивалентными §6.1, а контракт проекта — доставка message-конверта и payload.

Правила валидации (malformed handling): неверный magic / версия / длина / неизвестный канал / payload вне границ → **тихий drop + счётчик**, без исключений в tick-цикл; до handshake принимаются только C0-пакеты с token = 0.

Wire-кодирование команд — новый additive `CommandWireCodec` в Core: `[CommandHeader fields: PlayerId, Sequence, RequestedTick, Type][payload по типу]`, little-endian, versioned. Сам тип `CommandHeader` не меняется.

## 7. Ordering / reliability

- **Единое reliable-пространство** (invariant собственного носителя; для принятого — эквивалентные delivery semantics): одна последовательность/ACK-пространство для C0+C1 на направление (§6.1); C2 в ACK не участвует.
- **Reliable delivery**: send-buffer с retransmit; ACK piggyback + таймаут; RTO = smoothed RTT (EWMA) × 2, clamps [50 мс, 1000 мс]; Karn's rule (retransmitted-пакеты не участвуют в RTT-выборке); in-order доставка через reorder-buffer (R = 64); max retransmits (OD-8) → соединение считается потерянным.
- **Время (P2-2)**: все транспортные таймеры (RTO, keepalive, idle, fragment lifetime) идут на инжектируемых монотонных часах `ITransportClock` (мс); в тестах — виртуальные детерминированные часы. Транспортные таймеры не зависят ни от wall clock, ни от simulation ticks; grace по-прежнему считается в server ticks (2.4, без изменений): транспорт детектирует потерю по своим часам и сообщает хосту, а хост вызывает `NotifyConnectionLost` с текущим server tick.
- **Unreliable-sequenced (снапшоты)**: снапшот-группа принимается, если её `SnapshotTick` в конверте C2 новее последнего применённого (latest-wins по конверту, payload opaque — rev.3; транспорт не разбирает v1); устаревшая группа или потерянный фрагмент → discard группы (придёт следующий снапшот).
- **Дубликаты**: транспортная дедупликация по seq-окну + существующая серверная защита (`DuplicateSequence`, per-player watermark) — defense in depth.
- **Keepalive**: `Ping/Pong` с периодом (OD-8) по transport clock; idle timeout эндпоинта → `NotifyConnectionLost` → grace в `SessionManager` (механика Phase 2.4, без изменений).

## 8. Connection lifecycle

```text
Client:  Idle ──ConnectRequest──▶ Connecting ──Accept──▶ Established ──(lost/timeout)──▶
         (token=0)                │ Denied{reason}            │ Disconnect(reason)
                                  ▼                           ▼
                              Closed                     Server grace window (2.4) → Closed

Server per remote endpoint:
  Unknown ──valid handshake──▶ Handshaking ──▶ Established ──▶ Lost(grace) ──▶ Closed
```

- **Create**: `ServerTransportHost.Listen(port)`; клиент создаёт endpoint + `NetworkCommandChannel`.
- **Connect**: `ConnectRequest{messageVersion, snapshotProtocolVersion, resumeSessionId(опц., хук для 2.7)}` → сервер проверяет версии → `SessionManager.CreateSession(handle)` → `ConnectAccept{sessionToken}`; далее клиент использует существующую модель join/identity Phase 2.4.
- **Version mismatch** → `ConnectDenied{VersionMismatch}` (для snapshot-версии — отдельный код, чтобы 2.6 мог различить).
- **Disconnect**: graceful `Disconnect{reason}` (best-effort) → хост вызывает `CloseSession`; abrupt → timeout → `NotifyConnectionLost` → grace.
- **Server shutdown**: best-effort broadcast `Disconnect{ServerShutdown}` + остановка listener; судьба матчей — вне 2.5 (lifecycle Finished/Closed зафиксирован ADR-008).

## 9. Security baseline

Входит в Phase 2.5:

- magic/version/length фильтры; лимит размера датаграммы;
- attribution 64-битным cryptographic-random токеном (угадывание ~2⁻⁶⁴ на сессию);
- sequence window = анти-replay внутри соединения; per-player watermark `MatchServer` = анти-replay команд на уровне игры;
- **rate limiting per endpoint**: бюджет пакетов/байт в секунду; превышение → drop + метрика (защита от flood);
- malformed-пакеты не вызывают аллокаций-штормов и исключений в host-цикле.

Явно **откладывается** (зафиксировано, не «забыто»): шифрование (DTLS/иное), настоящая аутентификация, challenge-response против amplification/spoofing в adversarial-сети, anti-cheat. Авторитетная валидация `MatchServer` ограничивает ущерб подделки чужими EntityId (`NotEntityOwner`), но DoS остаётся риском → rate limiting обязателен.

## 10. Bandwidth

- Текущий прототип (40 юнитов): снапшот = 16 + 40×39 = **1576 байт** → 2 фрагмента по 1200; при 20 Hz full-rate ≈ 31 KB/s на клиента, на 10 клиентов ≈ **0.31 MB/s** исходящих — приемлемо для фазы, но это потолок модели полного снапшота.
- 3000+ entities: полный снапшот ≈ 117 KB — недопустимо; решение — cadence reduction, delta-снапшоты и FoW-фильтрация, что является scope **Phase 2.6**. Транспорт 2.5 обязан лишь не блокировать это: конфигурируемый send cadence, фрагментация, идемпотентный latest-wins приём.
- Команды и ack — десятки байт; overhead конверта 23 байта/датаграмм (29 с fragment header); message-конверт C2 +8 байт (`SnapshotTick`) acceptable.

## 11. Core / Server / Client boundaries

| Слой | Получает | Не получает |
|---|---|---|
| `GlobalFront.Core` | `CommandWireCodec` (little-endian, versioned; additive) | ничего Unity/сокетного |
| `GlobalFront.Server` (`…Server.Transport`) | envelope/каналы/reliability, `INetworkEndpoint`, `UdpServerEndpoint`, `TransportSessionBinder`, `ServerTransportHost`, константы `TransportProtocol` | доступ к simulation state; изменение `MatchServer`/`SessionManager` |
| `GlobalFront.Client` | `NetworkCommandChannel : ICommandChannel`, `ClientTransportEndpoint` (pump в Update, как driver), поверхность для async-отказов | авторитетные решения |
| Только адаптер | конкретный сокет/носитель (UDP сейчас; QUIC/библиотека потенциально позже — за тем же контрактом) | — |

OD-2: оставить в `GlobalFront.Server` (рекомендация) vs отдельная assembly `GlobalFront.Transport` — отдельная assembly является архитектурным изменением и требует решения владельца.

**Concurrency invariant (rev.3)**: worker thread (OD-5) принимает только raw datagrams в bounded queue; парсинг, attribution и любые вызовы `SessionManager` / `MatchServer` / `TransportSessionBinder` выполняются только на host/simulation thread (в pump хоста). Никаких авторитетных вызовов из сокет-потока.

## 12. Integration с Phase 2.3 и 2.4

### Phase 2.4 (identity)

- `ConnectionHandle` создаётся транспортом на accepted handshake (`SessionManager.CreateConnectionHandle()` уже существует); токен маппится на handle в `TransportSessionBinder`.
- Создание/закрытие/потеря соединения → существующие `CreateSession` / `CloseSession` / `NotifyConnectionLost` / `TryReconnect`. Resume-семантика чекаута сохраняется для 2.7; 2.5 только не ломает её: токен перестаёт валидироваться при Disconnected, reconnect выдаёт новую пару token/handle.
- Команды проходят **неизменённый** gate: `ValidateCommand(session, header)` → `TryEnqueue*`. Транспорт не подменяет и не обходит gate.

### Phase 2.3 (tick)

- Прибывшие команды складываются в per-session receive-буфер и дренируются хостом **внутри существующей фазы `TickStarting(N)`** — контракт «RequestedTick N применяется в тике N» сохранён.
- Транспорт **не владеет тиками**: никакого tick-влияния, catch-up или пауз; `TickDriver` неизменен.
- `RequestedTick` остаётся клиентским решением; транспорт лишь доставляет. В 2.6 клиент будет вычислять его из последнего полученного snapshot tick; контракт приёма снапшотов (payload + receive timestamp) определяется в 2.5.

### Command feedback: wire format и семантика канала (P2-3, P2-4)

`ICommandChannel` возвращает `MatchCommandRejection` синхронно; по сети авторитетный отказ асинхронен по определению. Решение в рамках ограничений: интерфейс **не меняется**. `NetworkCommandChannel.TrySubmit*` возвращает только **локальный pre-flight/queued результат** (невалидное session view, ошибка codec, переполнение send-очереди); авторитетный результат доставляется `CommandAck` (C0, reliable ordered, server→client) → событие `CommandResultReceived`. Wire-формат: `CommandAck { PlayerId (uint8), CommandSequence (uint32), SessionRejection (uint8), MatchCommandRejection (uint8) }` — несут оба rejection-enum'а Phase 2.4; `None + None` = accepted. Callers не должны трактовать синхронный `None` как авторитетное принятие. Это новая additive поверхность, а не изменение утверждённого контракта — подтверждается OD-7.

## 13. Test strategy

- **`VirtualNetworkPipe`** — детерминированный in-memory носитель с seeded-импэйрментами: loss %, duplication, reorder depth, corruption (битовые инъекции), latency profile, partition.
- **Codec**: round-trip всех типов сообщений; golden-bytes (по образцу `SnapshotSerializationTests`); fuzz malformed (укороченные, завышенная длина, неверный magic/версия/канал).
- **Reliability**: доставка при 5/10/20% loss; дедупликация; reorder-окно; RTO/retransmit counts; max-retransmits → timeout → `NotifyConnectionLost` → grace (интеграция с 2.4).
- **Sequence/wrap-around**: unit-тесты serial arithmetic на границах (0/65535/32768); окна W/R при скачках seq; единое reliable-пространство C0+C1 и изолированность C2 от ACK.
- **Виртуальные часы**: детерминизм RTO/keepalive/idle/fragment lifetime на инжектируемом `ITransportClock`.
- **Фрагментация**: сборка групп при потерях; lifetime expiry; memory bounds (≤ 4 группы / ≤ 2048 фрагментов / reassembly budget ≤ 2 MB на peer); reliable-сообщение > 64 KB (resync-readiness, P2-5).
- **Version spaces**: handshake с несовпадающим `MessageVersion` → `ConnectDenied`; `CarrierVersion` проверяется только для собственного носителя; C2 latest-wins по `SnapshotTick` конверта при opaque payload.
- **Lifecycle**: handshake успех / `VersionMismatch` / deny; disconnect graceful/abrupt; server shutdown broadcast.
- **Attribution/security**: пакет с чужим/нулевым токеном после handshake → drop; flood → rate-limit drop; replayed seq → drop; команда с `header.Player ≠ bound` → `PlayerMismatch` через реальный wire-путь.
- **End-to-end**: in-process «2 клиента + 1 host» поверх pipe: команды доходят и применяются детерминированно; снапшоты принимаются latest-wins при потерях; идентичный seed импэйрментов → идентичный результат.
- **Reconnect compatibility**: drop транспорта во время grace → сессия 2.4 сохраняет PlayerId/sequence (реализация resync — 2.7, но транспорт обязан не разрушать окно).

## 14. Risks

| Риск | Уровень | Митигация в проекте |
|---|---|---|
| Корректность собственного reliability-слоя | High при OD-1 = custom; снимается при OD-1 = adopt | при custom: детерминированный `VirtualNetworkPipe` + импэйрмент-тесты до любого real-socket кода; при adopt: зрелый носитель (LiteNetLib) |
| Внешняя зависимость носителя (при OD-1 = adopt) | Low–Medium | pinned version, ThirdPartyAssetsRegistry, контракт `INetworkCarrier` допускает замену вплоть до собственного носителя |
| Bandwidth cliff при 3000+ units | High (потенциальный) | 2.5 только несёт контракт; delta/cadence гарантированно в 2.6 |
| Нет шифрования: подделка/чтение трафика | Medium | authoritative validation ограничивает игровой ущерб; rate limiting против DoS; шифрование — явный deferred item |
| NAT traversal / доступность UDP в deployment-средах | Medium | отложено до dedicated/deployment R&D; контракт носителя позволяет замену |
| Threading: сокет vs host-цикл | Medium | bounded receive-queue, drain только в pump-точках хоста; concurrency policy = OD-5 |
| Токен угадываем/утёк | Low–Medium | crypto-RNG генерация; токен живёт только внутри установленного соединения; ротация при reconnect |
| Фрагментация усложняет unreliable-канал | Low | discard-группа + latest-wins: фрагментация не создаёт новых состояний консистентности |
| Async-отказы меняют UX-ожидание от `ICommandChannel` | Low | зафиксировано в ADR-009 + OD-7; HUD-поверх в 2.5 минимальна |

## 15. Pre-implementation decisions (обязательные P2 по итогам review)

Зафиксированы на уровне proposal; подтверждаются владельцем при implementation gate. Новые архитектурные решения не вводятся — это конкретизация уже предложенного контракта.

1. **uint16 wrap-around** — serial-number arithmetic по §6.1 (семантика RFC 1982); все сравнения sequence/ack только через неё.
2. **Virtual clock** — инжектируемые монотонные часы `ITransportClock` (мс); RTO/keepalive/idle/fragment lifetime идут только на них; в тестах — детерминированные виртуальные часы; от wall clock и simulation ticks транспортные таймеры не зависят.
3. **CommandAck wire format** — `CommandAck { PlayerId (uint8), CommandSequence (uint32), SessionRejection (uint8), MatchCommandRejection (uint8) }` (C0, reliable ordered, server→client); несут оба rejection-enum'а Phase 2.4; `None + None` = accepted.
4. **NetworkCommandChannel semantics** — `TrySubmit*` возвращает только локальный pre-flight/queued результат; авторитетный результат — асинхронный `CommandAck` → `CommandResultReceived`; синхронный `None` не является авторитетным принятием.
5. **Message-size ceiling / resync-readiness** — транспорт обязан поддерживать будущий full-state resync (2.7) размером > 64 KB: max message size конфигурируем, стартовое значение 1 MB, фрагментация по §6.1; resync-передача пойдёт по reliable-пространству. Snapshot Networking (cadence/delta) здесь НЕ решается.
6. **Fragmentation bounds / reassembly budget** — reassembly lifetime 2000 мс по transport clock; верхние границы: ≤ 4 одновременных групп на peer, ≤ 2048 фрагментов на группу, ≤ 1 MB на сообщение; per-peer reassembly budget ≤ 2 MB (конфигурируемо); admission control: превышение бюджета/границ → drop старейшей группы + счётчик.

## 16. Open Owner Decisions

Ни одна из рекомендаций ниже НЕ является решением владельца.

| ID | Вопрос | Рекомендация |
|---|---|---|
| OD-1 | Build vs adopt несущего слоя (сравнение ниже) | **Preferred candidate — LiteNetLib** за контрактом `INetworkCarrier`; final decision — Owner Decision Required; зависимость до approval не вводится, код не пишется |
| OD-2 | Размещение: namespace `GlobalFront.Server.Transport` vs отдельная assembly | namespace в Server сейчас; выделение — отдельное решение при dedicated host |
| OD-3 | Формат session token: crypto-random uint64 vs truncated HMAC | crypto-random uint64 |
| OD-4 | MTU-цель датаграммы и max message size | 1200 байт датаграмм; max message 1 MB (§15.5), фрагменты uint16 |
| OD-5 | Threading: receive worker thread + bounded queue vs poll на pump | worker thread + bounded queue |
| OD-6 | Transport ordering для C1: держать reliable-ordered или ограничиться серверным порядком | держать ordered (дёшево, упрощает клиент, не подменяет авторитетный порядок) |
| OD-7 | Подтвердить async `CommandAck` как контракт обратной связи отказов (ICommandChannel не меняется) | подтвердить |
| OD-8 | Константы: keepalive период, idle timeout, max retransmits, rate-limit бюджеты | **TBD**; retransmission timeout и idle timeout должны быть явно согласованы во время implementation (согласование с keepalive, grace-ожиданиями 2.4 и impairment test matrix); ориентир: ping 1 c, idle 5 c, retransmits 10, 200 pkt/s + 256 KB/s на endpoint |
| OD-9 | IPv4-only vs dual-stack для прототипа | IPv4-only; dual-stack к dedicated |

### OD-1: сравнение build vs adopt (P1-2 revision)

| Критерий | Собственный UDP-слой | LiteNetLib | ENet | Unity Transport | QUIC |
|---|---|---|---|---|---|
| Reliability maturity | отсутствует — писать и доказывать проекту | зрелая: ARQ, каналы, фрагментация, MTU | зрелая (проверенная C-библиотека) | зрелая (production Unity) | зрелая (protocol level) |
| Engine independence | полная (.NET) | полная (pure C#, netstandard; работает без Unity) | native C core + C# binding: нативные бинарники на платформу | Unity-bound (Unity.Collections/Burst) — не может жить в `GlobalFront.Server` | зависит от выбранного стека (native MsQuic vs managed) |
| Deterministic testability | полный контроль | хорошая: pure .NET, loopback + `VirtualNetworkPipe`-адаптер | средняя: impairment сложнее через native-слой | ограничена вне Unity | низкая |
| Maintenance cost | полная нагрузка на проект (High risk) | низкая: активный внешний проект; остаётся dependency risk | низкая-средняя + управление нативными бинарниками | зависимость от Unity versions | высокая: молодой game-dev ecosystem |
| Licensing | собственный проект | MIT | MIT (native) | Unity Package License | varies |
| Контроль над протоколом | полный | высокий (open source), но delivery-семантика диктуется библиотекой | средний (C ABI) | низкий | средний |
| Performance | OK при корректной реализации | достаточен с запасом для ≤ 10 игроков | достаточен | достаточен | хорош; crypto overhead |
| Integration | максимальная | простая (.NET assembly, ThirdParty governance) | средняя (marshalling, бинарники) | расщепляет стек (клиент Unity / сервер нет) | сложная |

**Preferred candidate (не решение владельца; final decision — Owner Decision Required)**: **LiteNetLib** как несущий слой за проектным контрактом `INetworkCarrier`/`INetworkEndpoint`: message-модель, attribution, версии, `CommandAck` и lifecycle остаются project-owned, библиотека предоставляет только delivery (reliable/unreliable каналы, фрагментация, MTU); внутренние ARQ/sequence-механизмы библиотеки не обязаны совпадать с §6.1 — требуется эквивалентность delivery semantics. Это снимает High-риск корректности собственного reliability. До approval OD-1 зависимость не вводится и транспортный код не пишется. Fallback — собственный носитель, если владелец приоритизирует zero-dependency и полный контроль; ENet — только при потребности в native-производительности; Unity Transport отклонён для общего core; QUIC отложен.

## 17. Explicit Out of Scope

Snapshot cadence policy, delta snapshots, FoW-фильтрация репликации (2.6); реализация reconnect/resync (2.7 — в 2.5 только совместимость); matchmaking, lobby; authentication; anti-cheat; шифрование; NAT traversal / deployment infrastructure; prediction/reconciliation; добавление `MatchId` в снапшоты (OD-5 ADR-008 → revisiting в 2.6); любые изменения `CommandHeader`, Snapshot Protocol v1, детерминизма, TickDriver ownership, SessionManager authority.

## Связанные документы

- [ADR-009 Network Transport (Accepted)](ADR-009-Network-Transport-DRAFT.md)
- [Architecture](../ARCHITECTURE.md)
- [Decisions](../DECISIONS.md)
- [Phase 02](../Phases/Phase_02_Multiplayer.md)
- [AI Contract](../AI_CONTRACT.md)
