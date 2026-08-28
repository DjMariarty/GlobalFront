# ADR-009: Network Transport Architecture (Phase 2.5) — DRAFT

> **Статус: Accepted, 2026-08-22** (OD-1 = LiteNetLib одобрен владельцем: preferred initial transport carrier behind `INetworkCarrier`, subject to implementation and validation).
> Финальная версия записана в [DECISIONS.md](../DECISIONS.md); этот файл сохраняется как approved research-источник (rev. 3).
> Основание: [Phase 2.5 Network Transport — R&D Report](Phase_02_05_Network_Transport_RND.md), контрольная точка `73d1276`.

## Context

После Phase 2.1–2.4 существуют authoritative deterministic simulation, command channel, server tick lifecycle (ADR-007) и server-authoritative session/player identity (ADR-008), но весь трафик остаётся in-process (`LocalCommandChannel`). [ARCHITECTURE.md](../ARCHITECTURE.md) фиксирует transport technology как `TBD / Architecture Decision Required`; Phase 2.5 должен определить transport contract, на который затем опираются Snapshot Networking (2.6) и Reconnect/Resync (2.7).

Действующие ограничения, которые draft не нарушает:

- `CommandHeader` не изменяется без отдельного ADR;
- Snapshot Protocol v1 не изменяется без compatibility review;
- deterministic simulation, TickDriver ownership (ADR-007) и SessionManager authority model (ADR-008) остаются неприкосновенными;
- Core и Server не используют Unity API.

## Problem

Нужен транспортный слой, который одновременно:

1. доставляет команды (нельзя терять), снапшоты (нужен latest-wins, устаревшие бесполезны) и control-сообщения (handshake/disconnect/ack) с **разными delivery semantics**;
2. атрибутирует каждый входящий пакет существующей модели identity (`ConnectionHandle → SessionId`) и проходит неизменённый session gate ADR-008;
3. не влияет на server tick lifecycle: команды дренируются в существующей фазе `TickStarting`, контракт `RequestedTick` сохранён;
4. работает engine-independent: один и тот же транспортный core для local prototype и будущего dedicated host без Unity;
5. переносит Snapshot Protocol v1 как payload без модификаций и оставляет пространство для delta/cadence в 2.6;
6. детектируемо переживает loss/duplication/reorder/corruption/timeout и совместим с grace-окном Phase 2.4 (reconnect-семантика 2.7 не реализуется, но не ломается).

## Alternatives

1. **TCP-only.** Отклонено: head-of-line blocking недопустим для latest-wins snapshot стрима; reliability TCP не контролируется проектом.
2. **QUIC.** Отложен для текущей фазы: избыточная коннект-модель и молодой game-dev ecosystem; остаётся кандидатом на замену носителя под тем же контрактом.
3. **Unity Transport.** Отклонён для общего core: Unity-bound пакет (Unity.Collections/Burst) не может жить в `GlobalFront.Server` без Unity API; расщепляет стек.
4. **LiteNetLib / ENet.** Не отклоняются: pure-.NET LiteNetLib и native ENet engine-independent (ошибочное утверждение rev. 1 об их несовместимости с «Server без Unity» снято review). Полное сравнение build-vs-adopt — в OD-1; recommendation — LiteNetLib, решение не принято владельцем.
5. **Расширить `CommandHeader` транспортными полями.** Отклонено: запрещено без отдельного ADR; attribution — свойство ingress path (ADR-008).
6. **Встроить транспорт в `MatchServer`/`SessionManager`.** Отклонено: нарушает authority-модель и детерминизм-файрвол.
7. **Минимальный UDP-транспорт с тонким reliability-слоем в `GlobalFront.Server.Transport`** (несущий слой — собственный или принятый по OD-1). Предлагается.

## Proposed Decision

- Три виртуальных канала поверх UDP: **C0 Control** (reliable ordered), **C1 Command** (reliable ordered, client→server), **C2 Snapshot** (unreliable sequenced, latest-wins по `SnapshotTick` конверта C2, payload opaque).
- **Sequence/ACK space (P1-1, rev.3)**: контракт разделён на delivery semantics, обязательные для любого носителя (C0+C1 reliable ordered; C2 unreliable sequenced latest-wins; фрагментация с bounded reassembly; delivery не влияет на авторитетный порядок команд), и invariants собственного носителя (OD-1 = custom): единое uint16 reliable-пространство на направление для C0+C1, отдельное snapshot-пространство без ACK, serial-number arithmetic (RFC 1982), receive window W = 1024, reorder window R = 64. От принятого носителя (например, LiteNetLib) требуется эквивалентность только delivery semantics; его внутренние ARQ/sequence-механизмы не обязаны совпадать с проектными.
- Несущий слой (delivery/фрагментация/сокет) — собственный минимальный носитель либо принятая библиотека за контрактом `INetworkCarrier` с эквивалентными delivery semantics; preferred candidate OD-1 — LiteNetLib; final decision — Owner Decision Required, зависимость до approval не вводится.
- Version spaces (rev.3): `CarrierVersion` (uint16) — версия layout конверта собственного носителя, существует только при OD-1 = custom; `MessageVersion` (uint16) — версия проектной message-модели, несётся в каждом сообщении и проверяется в handshake при любом носителе; `SnapshotProtocol.Version` — версия snapshot payload, транспорт его не интерпретирует.
- Конверт собственного носителя: magic, `CarrierVersion`, канал/флаги, 64-битный server-issued session token, `Sequence` (интерпретируется по каналу), `AckNumber`/`AckBitmap` (только reliable-пространство), payloadLength, опциональная фрагментация (`MessageId + FragmentIndex + FragmentCount`, все uint16); целевой датаграмм ≤ 1200 байт (OD-4); malformed → тихий drop + счётчик, без исключений в tick-цикл. Message-конверт (magic/MessageVersion/type/token; для C2 + `SnapshotTick(uint64)`) project-owned при любом носителе.
- **C2 carrier envelope (rev.3)**: latest-wins выполняется по `SnapshotTick` конверта C2; payload (байты Snapshot Protocol v1) остаётся для транспорта opaque — скрытая зависимость транспорта от v1 layout устранена, Snapshot Protocol v1 не изменяется.
- Команды на проводе кодирует новый additive `CommandWireCodec` в Core (little-endian, versioned); `CommandHeader` как тип и его серверная валидация не изменяются. Snapshot Protocol v1 переносится как payload без изменений.
- Attribution: после handshake `token → ConnectionHandle → SessionId` (`TransportSessionBinder`); все команды проходят неизменённый session gate ADR-008; `SessionManager` остаётся единственным источником PlayerId. `SessionId` (Guid) по проводу после handshake не летает.
- Reliability: retransmission с RTO по smoothed RTT (EWMA × 2, clamps 50–1000 мс, Karn's rule), max retransmits → потеря соединения → существующая grace-механика Phase 2.4; keepalive ping/pong; rate limiting per endpoint. Все транспортные таймеры (RTO, keepalive, idle, fragment lifetime) идут на инжектируемых монотонных часах `ITransportClock`; в тестах — виртуальные детерминированные часы (P2-2). Grace Phase 2.4 остаётся в server ticks.
- Транспорт не владеет тиками: входящие команды дренируются в существующей фазе `TickStarting`; контракт `RequestedTick` и TickDriver ownership не изменяются.
- Авторитетные отказы командам доставляются асинхронным `CommandAck` (C0, reliable ordered): wire-формат `{ PlayerId (uint8), CommandSequence (uint32), SessionRejection (uint8), MatchCommandRejection (uint8) }` (P2-3); `NetworkCommandChannel.TrySubmit*` возвращает только локальный pre-flight/queued результат и поднимает `CommandResultReceived` по получении ack (P2-4); `ICommandChannel` не изменяется (OD-7).
- **Message-size / fragmentation bounds (P2-5, P2-6, rev.3)**: max message size конфигурируем, стартовое значение 1 MB — транспорт обязан поддержать будущий full-state resync (2.7) > 64 KB по reliable-пространству; reassembly lifetime 2000 мс, верхние границы: ≤ 4 одновременных групп на peer, ≤ 2048 фрагментов на группу, ≤ 1 MB на сообщение; per-peer reassembly budget ≤ 2 MB (конфигурируемо); admission control: превышение бюджета/границ → drop старейшей группы + счётчик. Snapshot Networking здесь не решается.
- **Concurrency invariant (rev.3)**: worker thread (OD-5) принимает только raw datagrams в bounded queue; парсинг, attribution и любые вызовы `SessionManager` / `MatchServer` / `TransportSessionBinder` выполняются только на host/simulation thread (в pump хоста). Никаких авторитетных вызовов из сокет-потока.
- Lifecycle: handshake с проверкой `MessageVersion` и `SnapshotProtocol.Version` (`ConnectDenied{VersionMismatch}`), graceful/abrupt disconnect, server shutdown broadcast; reconnect resume — хук `resumeSessionId` в `ConnectRequest`, семантика — Phase 2.7.
- Security baseline: magic/version/length фильтры, crypto-random токены, sequence window против replay внутри соединения, rate limiting. Шифрование и настоящая аутентификация явно отложены.

## Consequences

- `GlobalFront.Server` получает `…Server.Transport` (message-модель, каналы, `INetworkCarrier`/`INetworkEndpoint`, `TransportSessionBinder`, `ServerTransportHost`, `ITransportClock`); `GlobalFront.Core` — `CommandWireCodec`; `GlobalFront.Client` — `NetworkCommandChannel` и клиентский endpoint pump. `MatchServer`, `TickDriver`, `SessionManager`, `CommandHeader`, Snapshot Protocol v1 не изменяются.
- При OD-1 = adopt (preferred candidate: LiteNetLib) появляется внешняя MIT-зависимость несущего слоя: pinned version + ThirdPartyAssetsRegistry; контракт носителя допускает замену вплоть до собственного; внутренние ARQ/sequence-механизмы носителя не нормируются — только delivery semantics. При OD-1 = custom проект принимает на себя полные maintenance/reliability издержки. До approval OD-1 зависимость не вводится и транспортный код не пишется.
- Тестовый фундамент: детерминированный `VirtualNetworkPipe` с loss/duplication/reorder/corruption/latency/timeout профилями; codec golden-bytes и fuzz; end-to-end multi-client сценарии поверх pipe.
- `LocalCommandChannel` и in-process pipeline сохраняются как local prototype path; транспорт не заменяет их в этой фазе.
- Зафиксированные ограничения: транспорт не решает bandwidth 3000+ units (delta/cadence — Phase 2.6), не реализует reconnect/resync (2.7), не вводит шифрование/аутентификацию; конкретные keepalive/timeout/rate-limit значения — OD-8, тюнинг TBD.

## Risks

| Риск | Уровень | Митигация |
|---|---|---|
| Корректность собственного reliability-слоя | High при OD-1 = custom; снимается при OD-1 = adopt | при custom: `VirtualNetworkPipe` + импэйрмент-тесты до real-socket кода; при adopt: зрелый носитель |
| Внешняя зависимость носителя (при adopt) | Low–Medium | pinned version, ThirdPartyAssetsRegistry, контракт допускает замену |
| Bandwidth cliff при 3000+ units | High (потенциальный) | контракт не блокирует delta/cadence 2.6 |
| Нет шифрования: подделка/чтение трафика | Medium | authoritative validation + rate limiting; шифрование — deferred |
| NAT traversal / UDP availability | Medium | deployment R&D позже; контракт носителя допускает замену |
| Threading: сокет vs host-цикл | Medium | bounded receive-queue, drain в pump-точках (OD-5) |
| Токен угадываем/утёк | Low–Medium | crypto-RNG; ротация при reconnect |
| Async-отказы vs синхронный `ICommandChannel` | Low | additive `CommandAck` с явным wire-форматом; интерфейс не меняется (OD-7) |

## Open Owner Decisions

Ни одна из рекомендаций ниже НЕ является решением владельца.

| ID | Вопрос | Рекомендация |
|---|---|---|
| OD-1 | Build vs adopt несущего слоя (полная матрица: R&D §16 — собственный UDP / LiteNetLib / ENet / Unity Transport / QUIC по maintenance, maturity, engine independence, testability, licensing, control, performance, integration) | **Preferred candidate — LiteNetLib** за контрактом `INetworkCarrier` (требуется эквивалентность только delivery semantics); final decision — Owner Decision Required; зависимость до approval не вводится, код не пишется |
| OD-2 | Namespace `GlobalFront.Server.Transport` vs отдельная assembly | namespace в Server |
| OD-3 | Формат session token: crypto-random uint64 vs truncated HMAC | crypto-random uint64 |
| OD-4 | MTU-цель датаграммы и max message size | 1200 байт; max message 1 MB (resync-readiness), фрагменты uint16 |
| OD-5 | Threading: worker thread + bounded queue vs poll на pump | worker thread + bounded queue |
| OD-6 | C1 ordering: reliable-ordered vs только серверный порядок | reliable-ordered |
| OD-7 | Async `CommandAck` как контракт отказов (ICommandChannel не меняется) | подтвердить |
| OD-8 | Keepalive/timeout/retransmit/rate-limit константы | **TBD**; retransmission timeout и idle timeout должны быть явно согласованы во время implementation (с keepalive, grace-ожиданиями 2.4 и impairment test matrix); ориентир: ping 1 c, idle 5 c, retransmits 10, 200 pkt/s + 256 KB/s |
| OD-9 | IPv4-only vs dual-stack | IPv4-only для прототипа |

Обязательные P2 pre-implementation decisions (wrap-around arithmetic, `ITransportClock`, wire-формат `CommandAck`, семантика `NetworkCommandChannel`, message-size ceiling 1 MB, fragmentation bounds) зафиксированы в R&D-отчёте §15 и подтверждаются при implementation gate.
