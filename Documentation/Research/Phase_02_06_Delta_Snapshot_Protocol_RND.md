# Phase 2.6 — Delta Snapshot Protocol: R&D Report

> Статус: R&D Report — Revision 2 (Rev. 2, P0/P1 Closed, Ready for ADR) • Owner Decisions Required
> Базис: ADR-009 (Accepted, 2026-08-22, OD-1 = LiteNetLib 1.3.5, Phase 2.5 COMPLETE), Snapshot Protocol v1 (`SnapshotProtocol.Version = 1`, 16 B header + 39 B/entity), `SimulationConstants` (20 Hz, MaxPlayers = 10)
> Документ не является принятым ADR; реализация Phase 2.6 требует отдельного ADR на основе этого отчёта. Все рекомендации ниже — технические предложения для owner review.

---

## 0. Executive Summary

Snapshot Protocol v1 рассылает полный снапшот на каждый тик репликации. При N = 3000 юнитов полный кадр = 16 + 39·3000 = **117 016 байт (~117 КБ)**; при cadence 20 Hz это **~2.23 МБ/с ≈ 18.7 Мбит/с на клиента**, ×10 клиентов = **~187 Мбит/с исходящего трафика сервера** — на два порядка выше целевых 50–100 КБ/с на клиента.

Цель Phase 2.6 — **гибридная модель репликации**:

- **Keyframe baseline** (полный снапшот в формате, совместимом с v1-record) — периодически и по запросу;
- **Delta stream** (unreliable sequenced на C2, latest-wins) — только изменившиеся сущности/поля;
- **Клиентский feedback** (`SnapshotAck`) — источник `BaseTick` для дельт;
- **Окно истории дельт на сервере** + кумулятивный пакет догана внутри окна;
- **Rate-paced emission** всех C2-сообщений для защиты C1 от bufferbloat.

Ключевые рекомендации (детали и обоснование в соответствующих разделах):

| # | Решение | Рекомендация |
|---|---|---|
| 1 | HOL Blocking для Keyframes | Keyframe **не** идёт в reliable-пространство C0/C1. Слайсы keyframe передаются по C2 (unreliable) с клиентским NACK-repair (`SnapshotRequest`); rate-pacing — на всём C2-эмиттере. Отдельный reliable sub-channel — отложенная альтернатива (§3) |
| 2 | Client State Feedback | `SnapshotAck { LastAppliedTick, BaseKeyframeTick, MissingBase, MissingBitmap }` на C0 после каждого применённого такта репликации: 10 Hz, 340 B/s + немедленно при невозможности применения (§4) |
| 3 | DeltaResume vs SnapshotRequest | DeltaResume — гэп внутри окна истории (кумулятивный дельта-пакет); SnapshotRequest — выпадение за окно или нет базы (полный keyframe). Защита: token bucket на запросы (§5) |
| 4 | Wire format | Byte-aligned little-endian; per-unit `dirtyMask u8` (8 полей), entity-id через zigzag-varint delta; ADD = 39-байтная v1-record; UPDATE/REMOVE = компактные записи (§6) |
| 5 | Tombstone Horizon | Tombstones удерживаются и переотправляются в течение всего окна истории (рекомендация 120 тиков = 6 с) + 1 Hz integrity-fingerprint живого множества сущностей (§7) |
| 6 | Версионирование | Четыре независимых пространства: `CarrierVersion` / `MessageVersion` / `SnapshotProtocol.Version` / `DeltaSnapshotProtocol.Version` + capability-битмап в handshake (§8) |
| 7 | Бюджеты | Ingress 256 КБ/с (OD-8) — это **анти-DoS входа сервера**, не бюджет репликации. Egress-цель: ≤ 100 КБ/с на клиента, ≤ ~1 МБ/с суммарно на 10 клиентов (§9) |
| 8 | Окно истории | `WindowEst = RetainedDeltaHistoryWindow = 120 тиков (6 с при 20 Hz)`; кумулятивный пакет = слияние change-set'ов с last-wins по полю; > 32 КБ → форсировать keyframe (§10) |
| 9 | Бенчмарки слайсинга | Гипотеза: слайс 8–16 KB оптимален; ожидаемый объём ретрансмиссии растёт линейно с размером слайса (§11) |
| 10 | Cadence | При 3000 сущностей **20 Hz укладывается в бюджет только при dirty ≤ ~9%**; **10 Hz** — при dirty ≤ ~23% (с амортизацией keyframe). Базовая рекомендация: replication cadence 10 Hz, decoupled от 20 Hz симуляции, с адаптивной деградацией (§12) |
| 11 | FoW boundary | `IReplicationFilter` на границе ReplicationPipeline; per-client view → per-client dirty tracking; v1-реализация = passthrough `AllVisibleFilter` (§13) |

---

## 1. Верифицированный технический базис

Факты проверены по репозиторию (код и документация, сентябрь 2026):

- **Snapshot Protocol v1** (`Runtime/Server/Snapshot/ProtocolVersion.cs`): little-endian; header 16 B (`uint32 ProtocolVersion`, `uint64 Tick`, `uint32 UnitCount`) + 39 B/entity в canonical entity-id order (`SnapshotSerializer` валидирует порядок). Поля entity-record: `Entity.Value u64`, `Owner u8`, `PosX i32`, `PosZ i32`, `Health i32`, `HasMoveTarget u8`, `MoveTargetX i32`, `MoveTargetZ i32`, `AttackTarget u64`, `AutoAcquire u8`. `SnapshotDeserializer` отвергает version mismatch/truncation/malformed.
- **Транспорт (ADR-009, rev.3)**: три канала — C0 Control (reliable ordered), C1 Command (reliable ordered), C2 Snapshot (unreliable sequenced, latest-wins **по `SnapshotTick` конверта C2**). C0+C1 делят **единое uint16 reliable sequence-пространство на направление** с cumulative ACK + ack-bitmap (serial arithmetic RFC 1982, W = 1024, R = 64). Payload C2 для транспорта opaque.
- **Конверт собственного носителя** (`EnvelopeCodec`): magic, `CarrierVersion u16`, flags (channel/fragment), `SessionToken u64`, `Sequence u16`, `AckNumber u16`, `AckBitmap u32`, `PayloadLength u16`, опциональный fragment header (`MessageId/FragmentIndex/FragmentCount u16`). Целевой датаграмм ≤ 1200 B (OD-4); max message 1 MB; reassembly: lifetime 2000 ms, ≤ 4 групп на peer, ≤ 2048 фрагментов, ≤ 2 MB budget.
- **Rate limiting (OD-8)**: ориентир **200 pkt/s + 256 КБ/с на endpoint** (ingress-анти-DoS); ping 1 c, idle 5 c, retransmits 10; RTO = EWMA(RTT)×2, clamps 50–1000 ms, Karn's rule.
- **Симуляция**: `ServerTickRate = 20`, детерминированная, integer state; `MaxPlayers = 10`; команды валидируются в окне `AcceptedPastTicks = 20 / AcceptedFutureTicks = 60`.
- **Процесс**: Core/Server без Unity API; каждое сетевое изменение формата проходит R&D → ADR; protocol-significant constants — только через compatibility review; `CommandHeader` и Snapshot Protocol v1 не изменяются без отдельного ADR.
- **Ограничения ADR-009, релевантные 2.6**: snapshot cadence policy, delta snapshots и FoW-фильтрация явно out of scope 2.5; reconnect/resync — 2.7; `MatchId` в снапшотах — reopening в 2.6 (OD-5 ADR-008).

---

## 2. Математика проблемы (постановка)

Обозначения: `N` — число сущностей, `F` — cadence репликации (Hz), `K` — размер keyframe, `d` — доля dirty-юнитов за тик, `Ū` — средний размер update-записи, `H` — overhead дельта-пакета, `I` — интервал keyframe (сек), `R` — retry-фактор keyframe (≥ 1, учёт потери слайсов).

```
Keyframe:        K = 16 + 39·N                    (v1-совместимая раскладка)
Delta:           D = F · (d·N·Ū + H)
Keyframe amort.: A = K / I · R
Итог на клиента:  B = D + A
Итог на сервер:  B_total = 10 · B                 (10 клиентов)
```

Проверка текущего состояния (v1, full, 20 Hz, N = 3000):
- `K = 16 + 39·3000 = 117 016 B`; на клиента `B = 20 · 117 016 ≈ 2.23 МБ/с ≈ 18.7 Мбит/с`; на сервер `≈ 23.4 МБ/с ≈ 187 Мбит/с`. Подтверждает постановку задачи.
- Целевой коридор 50–100 КБ/с на клиента ⇒ суммарно 0.5–1.0 МБ/с ≈ **4–8 Мбит/с** на uplink сервера. Запас к текущему — 23–47×.

Дополнительный фактор: **чувствительность unreliable-канала к потере фрагментов**. Пакет 117 КБ через MTU 1200 B — это ~98 датаграмм; при independent loss 1% вероятность целостной доставки `0.99^98 ≈ 0.43`, т.е. более половины «полных» кадров требует ремонта. Отсюда требования §6: **дельта-пакет ограничен сверху** (≤ 8 KB payload, разбиение по диапазонам entity-id), а keyframe — слайсами (§3).

---

## 3. Вопрос 1 — HOL Blocking для Keyframes (C0/C1 vs C2)

### 3.1. Количественная постановка

Keyframe 117 КБ через надёжный канал = 98 датаграмм в одном окне фрагментации. Потеря **любого** фрагмента ретранслируется точечно (selective ack bitmap есть), но — критично — **в едином reliable-пространстве C0/C1 все последующие сообщения (команды C1, CommandAck, ping/pong C0) встают в очередь за ретрансмиссией** до её успеха или исчерпания `max retransmits = 10`.

Вероятность хотя бы одной потери на keyframe: `P = 1 − (1−p)^98`: p=0.5% → 38.8%; p=1% → 62.3%; p=2% → 86%.

При срабатывании: C1 простаивает ≥ RTO (50–100 ms при нормальном RTT), при серии потерь — до сотен ms. Для RTS это прямая деградация input latency и риск переполнения `AcceptedFutureTicks`. **Ставка keyframe на reliable-канал недопустима при текущем бюджете потерь.**

### 3.2. Вариант (а): отдельный logical sub-channel / sequence space

Выделить C3 (или sub-space) — reliable ordered с собственной последовательностью.

- **Плюсы**: полностью устраняет HOL между keyframe и C1/C0 (независимая ARQ-очередь); повторная отправка дёшева; не нужен клиентский repair-протокол; простая семантика.
- **Минусы**: **изменение контракта ADR-009** (delivery semantics зафиксированы: ровно C0/C1/C2 и единое reliable-пространство) → rev. ADR; при adopt-носителе реализуемо (LiteNetLib — до 64 каналов), но собственный `OwnDatagramCarrier` требует второго reliable-пространства и отдельного ack-буккипинга; ретрансмиссии 117 КБ всё равно потребляют полосу и при отсутствии pacing вызывают bufferbloat сокета, задерживая C1; reliable доставка «устаревшего» keyframe бессмысленна — если keyframe не успел, дешевле запросить свежий.

- **Риски**: расширение верифицированного транспортного контракта; двойной ACK-механизм; тестовая матрица ×2.

### 3.3. Вариант (б): слайсинг + rate-pacing внутри reliable C0/C1

- **Плюсы**: без изменения протокола; pacing устраняет burst/bufferbloat.
- **Минусы**: pacing **не лечит потерю**. При p=1% и 98 датаграммах 62% keyframes блокируют C1/CommandAck минимум на один RTO. Слайсинг лишь размазывает потери, увеличивая частоту «застреваний» на длинных keyframe'ах. Кроме того, 98 фрагментов одного сообщения съедают значительную часть in-flight окна W = 1024 и усугубляют reorder-чувствительность.
- **Вердикт**: отклонить как основной механизм; pacing оставить, но перенести на C2 (см. (в)).

### 3.4. Вариант (в): keyframe по C2 (unreliable) + клиентский NACK-repair — рекомендуется

Механика: keyframe режется на слайсы фиксированного размера (параметр бенчмарков, §11); каждый слайс — самостоятельное C2-сообщение (`KeyframeSlice`) с общим `KeyframeSeq` и уникальным `SliceIndex`. Маркер конверта `SnapshotTick = (Tick << 8) | SliceIndex` используется для маршрутизации и диагностики, но не как глобальный порог отбрасывания: reorder слайсов одного keyframe допустим, сборщик дедуплицирует их по `(KeyframeSeq, SliceIndex)`. Клиент собирает слайсы; недостающие запрашивает `SnapshotRequest{KeyframeTick, MissingSliceMask}` по C0 (reliable, rate-limited). Сервер повторяет **только** недостающие слайсы.

**Latest-Wins на уровне replication consumer:** отбрасываются только сообщения, чей декодированный мировой `Tick <= LastAppliedTick`. Любой пакет с `Tick > LastAppliedTick`, включая пришедший после reorder, сохраняет право на сборку и применение по Apply-Guard §5.1; максимум ранее *полученного* C2-маркера не является drop-порогом.

- **Плюсы**: C1/C0 гарантированно не блокируются — по построению (разные очереди delivery); потеря слайса = потеря 8–16 КБ, а не остановка канала; повторение точечное и дешёвое; C2 уже валидирован в 2.5; не требуется менять reliable-семантику ADR-009.
- **Минусы**: repair-loop добавляет `RTT + half-RTO` к времени ребейзлинга; нужна анти-спам защита запросов; клиентский буфер сборки ≤ `2·K`.
- **Tail-latency guard**: допускается не более 2 NACK-циклов для текущего keyframe. После второй неудачи клиент **немедленно** запрашивает свежий keyframe через `SnapshotRequest` в рамках token bucket и не ждёт плановые 15–20 с.
- **Риски**: при устойчивой потере > 10% исчерпание запроса переводит управление в `ConnectionFailed / ResyncRequired` (контур Phase 2.7); безусловное дублирование всех слайсов запрещено, опционально дублируется только header-слайс.

### 3.5. Сравнительная таблица и рекомендация

| Критерий | (а) reliable sub-channel | (б) слайсинг в reliable | (в) keyframe на C2 + NACK |
|---|---|---|---|
| HOL для C1/CommandAck | устранён | **не устранён** (RTO-stall при p ≥ 0.5%) | устранён |
| Изменение контракта ADR-009 | да (rev. ADR) | нет | additive (интерпретация SnapshotTick) |
| Стоимость потери слайса | точечная ретрансмиссия | RTO-stall C1 | запрос 1–2 слайсов |
| Полоса при p = 1% | ~K | K + простой C1 | K·(1 + ~8% ожид. ремонт) |
| Сложность клиента | низкая | низкая | средняя (сборка + NACK) |
| Сложность сервера | средняя (второй ARQ) | низкая | низкая/средняя |
| Тестовая матрица | ×2 (второй reliable) | — | + repair-сценарии |

**Рекомендация**: **(в)** как основной механизм + **rate-pacing C2-эмиттера** (все C2-сообщения — дельты и слайсы — отпускаются через token-bucket с burst-лимитом ≤ 16 KB на тик репликации). Вариант (а) зафиксировать как fallback/future ADR, если бенчмарки §11 покажут недопустимый repair-overhead. Вариант (б) отклонить.

### 3.6. Сравнение архитектурных моделей репликации

| Критерий | A: Reliable full + cumulative delta | B: Reliable full + инкрементальная цепь | C: Sliced keyframe + delta stream | D (Рекомендовано): Hybrid (нарезанная база + инкрементальное окно + кумулятивный догон) |
|---|---|---|---|---|
| **Потеря пакета (Loss)** | Отлично (дельта самодостаточна к базе) | Плохо (потеря 1 дельты рвёт всю цепочку) | Как A | Отлично (потерянный тик пропускается следующей Establishing Delta; dependency gap закрывается догоном) |
| **Reorder / Duplicate** | Отлично (по тикам, идемпотентно) | Цепь чувствительна к разрывам | Как A | Дубликаты идемпотентны; reorder → догон |
| **Всплеск потерь (Burst loss)** | Без последствий для следующих дельт | Цепь рвётся → запрос полной базы | Как A | Следующая Establishing Delta либо один кумулятивный догон на весь всплеск |
| **Рост размера дельт** | Плато дрейфа: в бою дельта быстро разрастается до размера полного кадра | Минимальный (только изменения за 1 тик) | Как A | Минимальный в норме; при потерях ограничен окном догона |
| **Полоса (Bandwidth)** | Высокая в активном бою (эффект плато) | Минимальная в норме, но частые ре-базовые | Высокая в бою | Минимальная в норме + редкие компактные догоны |
| **CPU сервера** | $O(N)$ сравнение с базой на клиента | Сравнение с предыдущей эмиссией | Как A | Сравнение с предыдущей эмиссией + кольцевой буфер |
| **Память сервера** | Базовая линия на каждого клиента | Состояние предыдущей эмиссии | Как A | 2 полных emission-среза + общее окно 120 compact change-set'ов + буферы клиентов |
| **Масштаб 3000 / 5000 юнитов** | Плато полосы перегружает канал | Частые запросы полных баз роняют сеть | Как A | Уверенно укладывается в целевой бюджет |
| **Сложность** | Низкая | Низкая-средняя | Средняя | Высокая (FSM догона, кольцо состояний, NACK) |

**Вывод обоснования:** модели A, B и C отвергнуты: A не решает проблему раздувания дельт в бою; B страдает от обрывов цепочки при 1% потерь; C решает только нарезку базы, но сохраняет недостатки кумулятивных дельт. **Модель D — единственная жизнеспособная.**

---

## 4. Вопрос 2 — модель обратной связи клиента (Client State Feedback)

### 4.1. Что сервер должен знать

Чтобы кодировать дельту, серверу на каждый cadence-тик нужно для каждого клиента: `BaseTick` — тик состояния, которым клиент фактически владеет. Источник истины — **клиент**, потому что доставка C2 unreliable и сервер не знает, что дошло. Подпись «клиент применил тик T» означает: все части Establishing Delta тика T получены, прошли Apply-Guard и применены к текущей keyframe-базе; промежуточные мировые тики могут быть безопасно пропущены.

### 4.2. Структура SnapshotAck (C0, reliable ordered, client → server)

```text
SnapshotAck (34 байта, little-endian):
  0   u8   MessageType       = SnapshotAck
  1   u8   HealthFlags       = { GapPresent:1, BitmapOverflow:1, BaseStale:1, Reserved:5 }
  2   u64  LastAppliedTick   — последний тик, полностью применённый клиентом
  10  u64  BaseKeyframeTick  — тик keyframe-базы клиента (0 = базы нет)
  18  u64  MissingBase       — первый тик непрерывного пропуска (0 = нет гэпа)
  26  u64  MissingBitmap     — бит i = тик (MissingBase + i) не получен;
                                 все 64 бита = «гэп длиннее 64 тиков» (overflow)
  34  —    конец фиксированной записи; continuation-блоки в v1 отсутствуют
```

Нормативный размер `SnapshotAck` составляет ровно **34 байта**: `1 + 1 + 8 + 8 + 8 + 8 = 34`, что соответствует смещениям `0/1/2/10/18/26`. Число 33 в исходной редакции было арифметической опечаткой. При 10 Hz uplink равен `34 B × 10 Hz = 340 B/s` на клиента.

Семантика битовой маски: пропуски почти всегда короткие (1–5 тиков, §10), поэтому 64-битного окна достаточно; overflow-флаг честно сигнализирует «я далеко позади», и сервер не обязан вычислять точную маску — он ответит кумулятивным пакетом или keyframe.

### 4.3. Частота и правила отправки

- **Регулярно**: после **каждого применённого такта репликации** при базовой cadence 10 Hz. `34 B × 10 Hz = 340 B/s` uplink на клиента — пренебрежимо мало, зато сервер получает актуальный `BaseTick` с задержкой не более одного RTT.
- **Немедленно, если новый тик не был применён** (rate-limited ≥ 100 ms между сигналами): при неполной multipart-сборке, `BitmapOverflow`, `BaseTick > LastAppliedTick`, несовпадении `KeyframeRef` или нормативном разрыве `BaseTick − LastAppliedTick > WindowEst`.
- **Не подавлять ACK прогресса**: подтверждение каждого успешно применённого `Tick` обязательно. Дубликат того же ACK сервер может идемпотентно проигнорировать, но клиент не пропускает ACK ради экономии нескольких десятков байт.
- Канал — C0 (reliable ordered): потеря ACK недопустима, а ordered-доставка гарантирует монотонность наблюдаемого сервером `LastAppliedTick`.

`MissingBitmap` остаётся диагностическим/NACK-сигналом для неполных multipart-пакетов и запроса догана, но не заставляет клиента ждать отсутствующие промежуточные мировые тики, если уже доступна применимая более новая Establishing Delta.

### 4.4. Использование на сервере

- Подтверждённый `LastAppliedTick` становится `BaseTick` следующей Establishing Delta. Сервер сворачивает change-set'ы интервала `(BaseTick, Tick]`, поэтому ACK-задержка в один RTT не создаёт цепочной зависимости.
- Если ACK не приходит дольше `max(0.5 с, 3·(1/F) + RTT_max)`, feedback-канал считается деградировавшим: cadence снижается до 5 Hz и инициируется свежий keyframe в рамках token bucket; дальнейшая эскалация — `ConnectionFailed / ResyncRequired` Phase 2.7.

## 5. Вопрос 3 — SnapshotRequest / DeltaResume: конечные автоматы

### 5.1. FSM клиента (ReplicationReceiver)

`WindowEst = RetainedDeltaHistoryWindow = 120 тиков (6 секунд при 20 Hz)` — единая нормативная константа Revision 2.

```text
join / resync ──▶ UNBASED ──(полный keyframe)──────────────────────────────▶ STREAMING
                    │ 3 неудачных SnapshotRequest: 0.5 / 1.0 / 2.0 с           │
                    └──────────────────────────────▶ ConnectionFailed / ResyncRequired

STREAMING ──(Establishing Delta прошла Apply-Guard)────────────────────────▶ STREAMING
    │ BaseTick > LastAppliedTick, зависимость внутри 120 тиков
    ├────────────────────────▶ CATCHING_UP ──(CumulativeDelta)──────────────▶ STREAMING
    │ KeyframeRef mismatch / BaseTick − LastAppliedTick > 120 / 2 NACK-цикла исчерпаны
    └────────────────────────▶ REBASING ──(свежий keyframe)─────────────────▶ STREAMING
```

Состояния и переходы:

1. **UNBASED**: база отсутствует (join / потеря базы). Клиент отправляет не более 3 `SnapshotRequest{KeyframeTick=0}` с экспоненциальным backoff `0.5 с → 1.0 с → 2.0 с`. После таймаута третьей попытки — `ConnectionFailed / ResyncRequired` (контур Phase 2.7), а не вечное ожидание. Полностью собранный keyframe устанавливает `CurrentKeyframeSeq`, `LastAppliedTick` и переводит FSM в STREAMING.
2. **STREAMING — Apply-Guard Establishing Delta**:
   - пакет отбрасывается только если `Tick <= LastAppliedTick`;
   - дельта принимается и применяется, если `Tick > LastAppliedTick` **и** `BaseTick <= LastAppliedTick` **и** `KeyframeRef == CurrentKeyframeSeq`;
   - все `PartCount` частей данного `Tick` должны быть собраны; затем абсолютные ADD/UPDATE/REMOVE устанавливают состояние мира на `Tick`, после чего `LastAppliedTick = Tick` и немедленно отправляется `SnapshotAck`;
   - пакет с `Tick > LastAppliedTick` не отбрасывается из-за reorder. Ожидание отсутствующих промежуточных тиков и локальный ring последовательных тиков не используются: более новая полная Establishing Delta безопасно пропускает их;
   - `BaseTick > LastAppliedTick` запускает CATCHING_UP, если `BaseTick − LastAppliedTick <= 120`; нормативный разрыв `BaseTick − LastAppliedTick > 120` или `KeyframeRef != CurrentKeyframeSeq` запускает REBASING. Возраст keyframe-базы (`Now − BaseKeyframeTick`) сам по себе не является триггером: плановые keyframe с интервалом 15–20 с не должны вызывать ложный ребейз через 6 с.
3. **CATCHING_UP**: используется только когда входящая дельта зависит от ещё не установленного `BaseTick`, но нужная история остаётся внутри 120 тиков. Клиент отправляет `DeltaResume{LastAppliedTick, BaseKeyframeTick}`; применимый кумулятивный пакет возвращает FSM в STREAMING.
4. **REBASING**: при `BaseTick − LastAppliedTick > 120`, несовпадении `KeyframeRef` либо после 2 неудачных NACK-циклов клиент немедленно отправляет `SnapshotRequest` свежего keyframe в рамках token bucket, не ожидая плановый интервал 15–20 с. Успех → STREAMING; полная потеря базы использует тот же лимит трёх попыток, что UNBASED.
5. **ConnectionFailed / ResyncRequired**: терминальная локальная эскалация Phase 2.6; восстановление соединения и полного состояния выполняет контур Phase 2.7.

### 5.2. FSM сервера (ReplicationSender, per-client)

```text
AWAITING_BASE ──(SnapshotAck c BaseKeyframeTick≠0)──▶ STREAMING
AWAITING_BASE: по SnapshotRequest → свежий keyframe; исчерпание клиента ограничено §5.1
STREAMING:
  cadence tick → EstablishingDelta(BaseTick = LastAppliedAck, KeyframeRef = active keyframe)
  BaseTick..Tick → свернуть compact change-set'ы по last-wins per field
  DeltaResume(LastAppliedTick) внутри 120 тиков → CumulativeDelta (§10.2)
  разрыв BaseTick − LastAppliedTick > 120 → свежий keyframe (rate-limited)
  egress-бюджет превышен → применить Pre-emption Rule и понизить cadence (§9.2)
REBASE_SERVING: SnapshotRequest → свежие слайсы; ≤ 2 NACK-цикла на конкретный keyframe
```

Инварианты: `BaseTick` обычной Establishing Delta равен последнему подтверждённому `LastAppliedTick`; пакет покрывает все изменения `(BaseTick, Tick]` абсолютными значениями. Сервер хранит ровно `RetainedDeltaHistoryWindow = 120` compact change-set'ов; клиент с разрывом `BaseTick − LastAppliedTick > 120` получает keyframe вместо расширения истории. Возраст установленного keyframe не используется как самостоятельный критерий.

### 5.3. DeltaResume vs SnapshotRequest — критерий выбора

`WindowEst = RetainedDeltaHistoryWindow = 120 тиков (6 секунд при 20 Hz)`; отдельного оценочного или клиентского окна нет.

| Ситуация | Условие | Механизм |
|---|---|---|
| Пропущены промежуточные тики, но новая дельта устанавливающая | `Tick > LastAppliedTick`, `BaseTick <= LastAppliedTick`, `KeyframeRef` совпадает | Применить сразу по Apply-Guard; запрос не нужен |
| Не хватает заявленной базы внутри окна | `BaseTick > LastAppliedTick` и зависимость не старше 120 тиков | `DeltaResume` → кумулятивный пакет от `LastAppliedTick` |
| Заявленная зависимость за пределами окна | `BaseTick − LastAppliedTick > 120` | Немедленный переход в REBASING и `SnapshotRequest` свежего keyframe; возраст keyframe сам по себе не учитывается |
| Базы нет (join/reconnect) | `BaseKeyframeTick = 0` | UNBASED и не более 3 `SnapshotRequest` с backoff 0.5/1.0/2.0 с |
| Гэп > 64 тиков (`BitmapOverflow`) | advisory bitmap переполнен | Сервер проверяет 120-тактовое окно: CumulativeDelta при покрытии, иначе свежий keyframe |

### 5.4. Защита от DoS/спама запросами

1. **Token bucket на SnapshotRequest/DeltaResume**: capacity 3, refill 1/500 ms (устойчиво ≤ 2 req/s на клиента). Превышение → тихий drop + счётчик `RequestFlood`.
2. **Валидация тиков**: `DeltaResume` с `LastAppliedTick` вне `[Now − HistoryWindow − Slack, Now]` → drop + счётчик; невалидный `SnapshotRequest` → drop.
3. **Ограничение repair-циклов**: ≤ 2 на конкретный keyframe; повторный запрос того же слайса чаще 250 ms — drop. После второй неудачи клиент немедленно запрашивает **свежий** keyframe через `SnapshotRequest` в рамках token bucket, а не ждёт периодические 15–20 с.
4. **Рамки ADR-009**: общий ingress-лимит 200 pkt/s + 256 КБ/с на endpoint действует и на запросы; повторные нарушения → уведомление через C0 + escalation к disconnect-пути (grace Phase 2.4). Счётчики — в `TransportMetrics`.

## 6. Вопрос 4 — Wire Format дельта-снапшота и семантика ADD / UPDATE / REMOVE

### 6.1. Принципы кодирования

- **Little-endian, byte-aligned** — как весь протокол проекта; нулевые аллокации на сериализаторе (pooled buffers), O(1) десериализация.
- **Establishing Delta**: wire-пакет содержит свёртку всех change-set'ов интервала `(BaseTick, Tick]`, необходимую для установки состояния на `Tick`. Внутренние серверные change-set'ы остаются инкрементальными, но наружу кодируется устанавливающая дельта от последнего подтверждённого ACK.
- Значения полей в UPDATE — **абсолютные**, а не residual-смещения. Для каждого поля применяется последнее значение интервала (`last-wins per field`), поэтому пакет с `BaseTick <= LastAppliedTick` безопасно переводит совместимую keyframe-базу вперёд, даже если промежуточные тики потеряны.
- Порядок записей — **canonical entity-id ascending** (инвариант v1 сохраняется; внутри пакета id кодируются дельтой от предыдущего).
- Ограничение payload дельты: **≤ 8 KB**; большее изменение тика режется на несколько пакетов одного `Tick` (`PartIndex/PartCount`). Сборка атомарна на уровне `Tick`: записи частей самодостаточны, но `LastAppliedTick` меняется только после получения всех частей.

### 6.2. Заголовок DeltaSnapshotPacket (payload C2)

```text
DeltaSnapshotPacket (заголовок 36 байт, little-endian):
  0   u8   MessageType          = 0x03 Delta    (0x01 Full v1 legacy, 0x02 KeyframeSlice,
                                                0x03 Delta, 0x04 CumulativeDelta)
  1   u32  DeltaProtocolVersion = DeltaSnapshotProtocol.Version (§8)
  5   u64  Tick              — тик состояния после применения (результат)
  13  u64  BaseTick          — начало покрываемого интервала change-set'ов
  21  u8   PartIndex
  22  u8   PartCount         — разбиение большого тика
  23  u8   Flags             = { HasChecksum:1, HasTombstoneEcho:1, Reserved:6 }
  24  u32  StateChecksum     (optional, xxHash32 fingerprint состояния тика)
  28  u16  AddCount
  30  u16  UpdateCount
  32  u16  RemoveCount
  34  u16  KeyframeRef       — sequence активной keyframe-базы клиента
  36  ...  секции ADD | UPDATE | REMOVE
```

`KeyframeRef` переиспользует ранее зарезервированное поле `Reserved0`, поэтому размер заголовка остаётся 36 B. Пакет применим только при `KeyframeRef == CurrentKeyframeSeq` (§5.1).

Маркер конверта C2 `SnapshotTick = (Tick << 8) | PartIndex` служит для демультиплексирования частей, но не задаёт порог latest-wins. Replication consumer отбрасывает только пакет с декодированным `Tick <= LastAppliedTick`. Пакет с `Tick > LastAppliedTick` не отбрасывается из-за reorder: его части собираются, и после полного `PartCount` пакет проверяется Apply-Guard. Если более новый полный тик применён раньше, незавершённые буферы старых тиков освобождаются как obsolete.

### 6.3. Секции и per-unit записи

**Секция ADD** — появление юнита; запись полностью повторяет 39-байтную v1-record (`Entity u64, Owner u8, PosX i32, PosZ i32, Health i32, HasMoveTarget u8, MoveTargetX i32, MoveTargetZ i32, AttackTarget u64, AutoAcquire u8`). Совместимость с v1-record упрощает ребейз и переиспользует `SnapshotSerializer`-логику.

**Секция UPDATE** — per-entry:

```text
UpdateEntry:
  varint entityIdDelta   — LEB128 zigzag-дельта от id предыдущей записи (первая — от 0)
  u8     dirtyMask       — UnitDirtyMask
  [поля в фиксированном порядке по установленным битам:
   u8 Owner, i32 PosX, i32 PosZ, i32 Health, u8 HasMoveTarget,
   i32 MoveTargetX, i32 MoveTargetZ, varint AttackTargetDelta, u8 AutoAcquire]
```

| Бит | Маска | `UnitDirtyMask` | Поля UPDATE |
|---:|---:|---|---|
| 0 | `0x01` | `Owner` | `Owner` (`u8`) |
| 1 | `0x02` | `Position` | `PosX`, `PosZ` (`2 × i32`) |
| 2 | `0x04` | `Health` | `Health` (`i32`) |
| 3 | `0x08` | `HasMoveTarget` | `HasMoveTarget` (`u8`, 0/1); OD-14: если бит установлен и цель сброшена (`0`), координаты цели опускаются |
| 4 | `0x10` | `MoveTarget` | `MoveTargetX`, `MoveTargetZ` (`2 × i32`) |
| 5 | `0x20` | `AttackTarget` | zigzag-varint дельта `AttackTarget` от `EntityId` текущего юнита |
| 6 | `0x40` | `AutoAcquire` | `AutoAcquire` (`u8`, 0/1) |
| 7 | `0x80` | `ReservedExtension` | зарезервировано; в v1 пакет отклоняется |

- **Dirty Bitmask**: маска занимает ровно 1 байт. `0x80` не является sentinel продолжения: это `ReservedExtension`, который encoder не выставляет, а decoder v1 отвергает. Расширение маски требует новой версии протокола.
- **Семантика OD-14**: если установлен `HasMoveTarget` и передано значение `0`, цель очищается, а `MoveTargetX/MoveTargetZ` не сериализуются даже при установленном бите `MoveTarget`. В остальных случаях координаты передаются только при установленном `MoveTarget`.
- **AttackTarget → varint**: в UPDATE id цели кодируется zigzag-varint дельтой от `EntityId` текущего юнита (обычно 1–3 B вместо 8).

**Секция REMOVE (tombstone)**: `varint entityIdDelta` + `u8 Cause` (0=Destroyed, 1=FoW-hidden, reserved). Remove применяется идемпотентно: REMOVE несуществующего id — no-op (не ошибка).

### 6.4. Ожидаемые размеры

| Сценарий | Средний размер записи |
|---|---|
| UPDATE: только позиция (движущийся юнит) | ~1.5 (id) + 1 (mask) + 8 (2×i32) = **10.5 B** |
| UPDATE: позиция + health | ~12.5 B |
| UPDATE: всё (pos+hp+move+attack) | ~25 B |
| ADD | **ровно 39 B**: фиксированный `EntityId u64` и полный byte-identical layout записи Snapshot Protocol v1 |
| REMOVE | ~1.5 B |

Экономия против v1 (39 B/unit): **3–4× на типичном движении**, до 26× на tombstone. Для матрицы трафика (§12) принято консервативно `Ū = 14 B` (позиция + здоровье, 95-й процентиль), `H = 36 B`.

## 7. Вопрос 5 — жизненный цикл сущностей и Tombstone Horizon

### 7.1. Инвариант EntityId

`EntityId` строго **не переиспользуется** в рамках матча (монотонный аллокатор, соответствующий принципу ADR-008 для `PlayerId`). Следствия:
- клиентский словарь `EntityId → представление` не требует смены поколений ключей; кэш валиден всю партию;
- REMOVE + последующий ADD того же логического id невозможны;
- повторный ADD уже существующего `EntityId` из Establishing Delta обрабатывается идемпотентно как абсолютное подтверждение сущности, если identity/owner совместимы; конфликтующая identity трактуется как ошибка протокола и вызывает full-rebase;
- tombstone можно хранить как голый id без метаданных.

### 7.2. Горизонт удержания tombstones

«Юнит-призрак» возникает, если клиент пропустил REMOVE и больше не получает покрывающего его состояния. Revision 2 фиксирует точное равенство:

> **`WindowEst = RetainedDeltaHistoryWindow = Tombstone Horizon = 120 тиков (6 секунд при 20 Hz)`.**

Сервер удерживает tombstone тика `T` как минимум до `T + 120` и включает его в: (1) Establishing/Cumulative Delta, покрывающую `(BaseTick, Tick]`; (2) `TombstoneEcho`-хвост §7.3. Если заявленная зависимость выходит за окно (`BaseTick − LastAppliedTick > 120`), клиент гарантированно переходит в REBASING и принимает свежий keyframe, который не содержит удалённую сущность. Возраст keyframe сам по себе не форсирует ребейз; «юниты-призраки» исключаются при соблюдении FSM §5.

### 7.3. TombstoneEcho-хвост

Чтобы REMOVE не требовал запроса при потере 1–2 пакетов, сервер в течение `EchoTicks = 20 тиков (1 с)` после удаления включает tombstone в каждую дельту как **TombstoneEcho** (тот же формат REMOVE, но флагом `HasTombstoneEcho` в заголовке). Стоимость: `1.5 B × removals × 20` — при 50 смертях/с это ~1.5 КБ/с; приемлемо. Клиент применяет echo идемпотентно.

### 7.4. Периодический integrity-fingerprint (гарантированное устранение призраков)

Какие-либо рассинхроны (баг, потеря «последнего» REMOVE при переполнении буферов) дешевле обнаруживать, чем доказывать их невозможность. Рекомендация: раз в секунду (1 Hz) в заголовке дельты передавать `StateChecksum` — xxHash32 над конкатенацией отсортированных живых `EntityId` (счёт уже есть у canonical order). Клиент считает такой же хеш по своему состоянию; несовпадение 2 раза подряд → немедленный `SnapshotRequest`. Стоимость: 4 B/с и один проход по id на сервере (идёт при сериализации и так).

### 7.5. Выбор горизонта (числа)

- `WindowEst = RetainedDeltaHistoryWindow = Tombstone Horizon = 120 тиков = 6 с` — фиксированная константа Revision 2.
- `EchoTicks = 20 тиков (1 с)` — повторная отправка внутри 120-тактового горизонта, а не дополнительное окно поверх него.
- Память tombstones: ~2 B на удаление; даже 500 удалений/с × 6 с = ~6 КБ на сервер суммарно (история общая, фильтрация выполняется per-client при выкладке).

---

## 8. Вопрос 6 — версионирование протокола

### 8.1. Четыре независимых пространства версий

| Версия | Тип/разрядность | Где живёт | Что означает | Правило bump |
|---|---|---|---|---|
| `CarrierVersion` | u16 | только конверт собственного носителя (`OwnDatagramCarrier`) | layout конверта (magic/headers) | любое изменение полей конверта |
| `MessageVersion` | u16 | каждое сообщение, проверяется в handshake | проектная message-модель (типы C0/C1, CommandAck, запросы) | изменение набора/формата сообщений C0/C1 |
| `SnapshotProtocol.Version` | u32 | header полного снапшота (v1 = 1) | payload полного снапшота / keyframe ADD-record | изменение 39-байтной record или header v1 |
| `DeltaSnapshotProtocol.Version` (**новое**) | u32 | заголовок дельта-пакета (§6.2) | layout дельт/слайсов/кумулятива | любое изменение §6; **не** трогает v1 |

Разделение принципиально: keyframe-записи переиспользуют v1-record, поэтому Delta-протокол **ссылается** на `SnapshotProtocol.Version`, но эволюционирует независимо. Транспорт (`CarrierVersion`/`MessageVersion`) не интерпретирует ни одну из snapshot-версий.

### 8.2. Переговоры возможностей (capability negotiation)

`ConnectRequest` расширяется additive полем `SupportedSnapshotCaps: uint32`-битмап: `FullV1 = 0` (обязательная база), `DeltaV1 = 1`, `FoWFilterV1 = 2` (зарезервировано, §13), `CumulativeV1 = 3`. `ConnectAck` возвращает выбранные. Незнакомый бит у сервера → бит игнорируется; клиент, требующий неизвестную версию, получает существующий `ConnectDenied{VersionMismatch}`. Поле `MatchId` (OD-5 ADR-008) — в тот же handshake-эдж, отдельным вопросом.

### 8.3. Матрица совместимости (пример политики)

| Сервер \ Клиент | DeltaV1 | только FullV1 |
|---|---|---|
| сервер умеет DeltaV1 | гибридный режим (2.6) | v1 full-stream (back-compat сохраняется) |
| сервер только FullV1 | деградация в full-stream | v1 full-stream |

Back-compat: клиент 2.5-поколения (только FullV1) продолжает работать — гибрид включается только при согласованном `DeltaV1` в обе стороны.

---

| REMOVE | ~1.5 B |

## 9. Вопрос 7 — бюджеты пропускной способности (Bandwidth vs Ingress Anti-DoS)

### 9.1. Разграничение

| Лимит | Направление | Назначение | Значение (OD-8 ориентир) |
|---|---|---|---|
| **Ingress anti-DoS** | клиент → сервер | защита симуляции от спама (команды, запросы) | **256 КБ/с + 200 pkt/s на endpoint** |
| **Egress replication budget** | сервер → клиент | целевой трафик репликации на клиента | цель **50–100 КБ/с**, hard cap **128 КБ/с** |
| **Egress total** | сервер → все | uplink dedicated-сервера | 10 × per-client + control ≈ **≤ ~1.1 МБ/с ≈ 8–9 Мбит/с** |

Ingress-лимит и egress-бюджет — **разные вещи**: реальный клиентский трафик вверх (команды + ack + запросы) — сотни байт/с, т.е. 256 КБ/с — чистый анти-DoS запас ×1000, к репликации отношения не имеет. Клиентский **вход** = серверному egress; клиент обязан принимать до 128 КБ/с burst — лимитировать на клиенте нечего, но BandwidthGovernor не должен превышать cap даже в пиках (§12).

### 9.2. Governance (BandwidthGovernor)

1. Токен-бакет общего egress per-client: `rate = 100 КБ/с`, `burst = 16 КБ`, hard cap `128 КБ/с`.
2. **Pre-emption Rule** при дефиците токенов фиксирован и не может инвертироваться:
   1. `C0 Control / CommandAck` — наивысший приоритет, защита командного контура;
   2. `KeyframeSlice` свежей базы — высокий приоритет, инициализация/восстановление базы;
   3. `Delta Stream` — номинальный поток;
   4. `Cumulative Catch-up / Repair` — фоновый приоритет.
3. Низкоприоритетный пакет откладывается целиком; контент и wire-message не обрезаются. При устойчивом дефиците cadence снижается `20 → 10 → 5 Hz` на интервале 2 с с гистерезисом ±20%.
4. Если бюджет превышен и при 5 Hz (`dirty%` экстремальный), инициируется fresh-keyframe путь и `HealthFlags.BaseStale`; ожидаемая частота этого аварийного пути ≈ 0.
5. Отдельные счётчики/резервы для request/repair допустимы, но все классы подчиняются общему hard cap и указанному порядку pre-emption.

### 9.3. Сводка по 10 игрокам (N = 3000, каденция 10 Hz, dirty 10%, keyframe/20 с)

- Дельта: `10 · (300·14 + 36) ≈ 42.4 КБ/с`; keyframe: `117 КБ / 20 с · 1.5 ≈ 8.8 КБ/с`; server control ≈ 0.1 КБ/с. Клиентский `SnapshotAck` = 340 B/s uplink и в server egress не входит.
- Итого ≈ **51 КБ/с на клиента ≈ 0.51 МБ/с ≈ 4.1 Мбит/с** на 10 клиентов — внутри целевого коридора.

---

## 10. Вопрос 8 — Catch-Up и Re-Baseline

### 10.1. Размер окна истории (Retained Delta History Window)

Критерии: окно ≥ feedback period + RTT max + глубина burst loss + запас на пониженный cadence.

```text
HistoryWindow ≥ FeedbackPeriod (0.1 с) + RTT_max (0.3 с) + BurstLoss (1.5 с) + CadenceSlack (1.0 с) ≈ 2.9 с
```

Revision 2 фиксирует `WindowEst = RetainedDeltaHistoryWindow = 120 тиков (6 с при 20 Hz)` — более чем двукратный запас к расчёту. Сервер хранит 120 **компактных change-set'ов**, а не 120 полных снапшотов. При N=3000 и dirty=50% верхняя грубая оценка `120 × 1500 × 14 B ≈ 2.52 МБ` общей памяти; при dirty=10% — ~0.50 МБ. Tombstones учитываются отдельно по §7.5.

### 10.2. Кумулятивный пакет догана (потеря 1–5 тиков)

Формирование на сервере по `DeltaResume{LastAppliedTick}`:

1. Собрать change-set'ы тиков `T0+1 .. Now` (T0 = LastAppliedTick клиента).
2. **Слить по правилу last-wins per field**: для каждого entity — маска = OR масок, каждое поле берётся из последнего тика, где оно было dirty (значения абсолютны — это эквивалентно последовательному применению; поэтому residual-кодирование и не принято).
3. REMOVE'ы — объединение (idempotent), включая tombstones из горизонта (§7.2). Если сущность была ADD и REMOVE внутри `(BaseTick, Now]`, результат сворачивается в **REMOVE**, а не в «ничего»: клиент с `LastAppliedTick > BaseTick` мог уже видеть сущность, тогда как для клиента без неё REMOVE остаётся безопасным no-op.
4. MessageType = 0x04 CumulativeDelta, `BaseTick = T0`, `Tick = Now`. Лимит **32 КБ**: превышение → не слать кумулятив, а отправить keyframe (дешевле и надёжнее, чем гигантский пакет через unreliable).

Оценка размера (3000 units, dirty 10%, потеря 5 тиков): 300 × 14 B × 5 ≈ 21 КБ < 32 КБ ✓. Потеря 5 тиков при 50% dirty ≈ 105 КБ > лимита → keyframe (что и корректно по времени доставки).

### 10.3. Re-Baseline (полный keyframe)

Триггеры: нормативный разрыв `BaseTick − LastAppliedTick > 120`; Overflow вне окна; JOIN; mismatch `KeyframeRef`; mismatch StateChecksum (§7.4); 2 исчерпанных NACK-цикла. Возраст keyframe не является триггером. После исчерпания NACK клиент немедленно запрашивает **свежий** keyframe и не ждёт плановый интервал. Keyframe передаётся слайсами (§3.4) по C2 с `Tick = Now`, v1-record-совместимым телом; периодический интервал — Owner Decision, рекомендация **15–20 с**, плюс «микро-ребейз» при изменении числа живых юнитов > 25%.

### 10.4. Локальный догон на клиенте

Локальный ring последовательных мировых тиков отсутствует в модели Revision 2. Клиент хранит только ограниченные буферы сборки `PartCount` и keyframe slices. Любая полностью собранная Establishing Delta с `Tick > LastAppliedTick`, `BaseTick <= LastAppliedTick` и совпадающим `KeyframeRef` применяется немедленно; промежуточные тики не ожидаются. После применения более нового тика незавершённые более старые сборки освобождаются. `DeltaResume` нужен только при `BaseTick > LastAppliedTick` внутри 120-тактового окна; за окном выполняется REBASING.

---


## 11. Вопрос 9 — программа бенчмарков слайсинга Keyframe

### 11.1. Гипотеза

Ожидаемый объём повторной передачи растёт **линейно с размером слайса** (потеря любого фрагмента роняет весь слайс), а число repair-запросов — обратно. Ожидание потерь на слайс (p = 1% на датаграмму, MTU 1200 B):

| Слайс | Датаграмм | P(потери слайса) | Слайсов на 117 КБ | E[ретрансмиссия] |
|---|---|---|---|---|
| 8 KB | 7 | 6.8% | 15 | 15 · 0.068 · 8 КБ ≈ **8.2 КБ** |
| 16 KB | 14 | 13.1% | 8 | 8 · 0.131 · 16 КБ ≈ **16.8 КБ** |
| 32 KB | 27 | 23.6% | 4 | 4 · 0.236 · 32 КБ ≈ **30.2 КБ** |
| 117 KB (без слайсинга) | 98 | 62.3% | 1 | ≈ 72.9 КБ |

⇒ рабочая гипотеза: оптимум **8–16 KB**; 32 KB проигрывает по repair-трафику, но выигрывает по числу запросов — подтвердить измерением.

### 11.2. Параметрическая сетка

| Параметр | Значения |
|---|---|
| ChunkSize | **8 / 16 / 32 KB** + baseline «единый 117 КБ message» (для сравнения с 2.5-подходом) |
| Размер keyframe | N = 500 (~20 КБ), 1000 (~39 КБ), 3000 (~117 КБ) |
| Loss (iid) | 0 / 0.5 / 1 / 2 / 5% |
| RTT | 20 / 80 / 150 ms (+jitter ±30%) |
| Reordering | 0 / 2% |
| Pacing C2 | burst (без pacing) / 1 МБ/с / 4 МБ/с |
| Носитель | `VirtualNetworkPipe` (детерминированные профили) + real-UDP loopback (LiteNetLib) для sanity |
| Фон | одновременный C1-трафик 10 команд/с от каждого клиента |

### 11.3. Метрики (обязательный набор)

1. **Keyframe delivery time**: первый слайс → полная сборка (p50/p95/p99), отдельно — с repair-циклом.
2. **Jitter/latency C1**: command RTT p50/p95/p99 во время эмиссии keyframe vs baseline без keyframe. **Критерий приёмки: p95 delta ≤ +10 ms** (доказательство отсутствия HOL).
3. **Retransmissions**: число повторов слайсов и repair-циклов на keyframe; критерий: ≥ 90% keyframes собираются с ≤ 1 repair-циклом при 1% loss.
4. **Память**: peak reassembly/сборки на клиенте и сервере (критерий ≤ 2·ChunkSize × параллельных keyframe'ов, в рамках reassembly budget 2 MB).
5. **CPU**: время сериализации/склейки слайсов (критерий < 1 мс на keyframe 117 КБ, Release).
6. **Побочный эффект pacing**: максимальный burst датаграмм/мс на сокете.

### 11.4. Регресс-тесты (входят в DoD 2.6)

- Golden-bytes: закодированный кумулятив и дельта-пакет (фиксированные состояния) — побайтовый эталон.
- Fuzz: truncated/corrupted дельты и слайсы → тихий drop + счётчик, без исключений в tick-цикл (паттерн ADR-009).
- Soak: 60 с при 5% loss, 10 клиентов — ноль призраков (проверка fingerprint), ноль застрявших FSM, командный RTT в SLO.

---

## 12. Вопрос 10 — матрица нагрузочного моделирования (Bandwidth Matrix)

Параметры формулы (консервативные, §6.4): `Ū = 14 B` (позиция+здоровье), `H = 36 B` на пакет, keyframe-амортизация `A = K/I·R` при `I = 10 с`, `R = 1.5` показана отдельной строкой (для N = 3000: 17.6 КБ/с; при I = 20 с — 8.8 КБ/с).

**Только дельта-поток, КБ/с на клиента** (`D = F·(d·N·Ū + H)`):

| Entities \ Dirty | 1% | 10% | 50% | 100% |
|---|---|---|---|---|
| **Cadence 5 Hz** | | | | |
| 100 | 0.25 | 0.9 | 3.7 | 7.2 |
| 500 | 0.5 | 3.7 | 17.7 | 35.2 |
| 1000 | 0.9 | 7.2 | 35.2 | 70.2 |
| 3000 | 2.3 | 21.2 | 105.2 | 210.2 |
| 5000 | 3.7 | 35.2 | 175.2 | 350.2 |
| **Cadence 10 Hz** | | | | |
| 100 | 0.5 | 1.8 | 7.4 | 14.4 |
| 500 | 1.1 | 7.4 | 35.4 | 70.4 |
| 1000 | 1.8 | 14.4 | 70.4 | 140.4 |
| 3000 | 4.6 | 42.4 | 210.4 | 420.4 |
| 5000 | 7.4 | 70.4 | 350.4 | 700.4 |
| **Cadence 20 Hz** | | | | |
| 100 | 1.0 | 3.5 | 14.7 | 28.7 |
| 500 | 2.1 | 14.7 | 70.7 | 140.7 |
| 1000 | 3.6 | 28.7 | 140.7 | 280.7 |
| 3000 | 9.1 | 84.7 | 420.7 | 840.7 |
| 5000 | 14.7 | 140.7 | 700.7 | 1400.7 |

**С амортизацией keyframe (I = 10 с, R = 1.5), КБ/с на клиента** (прибавка): N=100 → +0.6; 500 → +2.9; 1000 → +5.9; 3000 → +17.6; 5000 → +29.3.

### 12.1. Выводы для N = 3000 («нормальный интернет-канал» = ≤ 100 КБ/с на клиента)

| Cadence | dirty ≤ 1% | dirty 10% | dirty 50% | dirty 100% |
|---|---|---|---|---|
| 20 Hz | 26.7 ✓ | **102.3 — на грани ✗** | 438 ✗ | 858 ✗ |
| 10 Hz | 22.2 ✓ | **60.0 ✓** | 228 ✗ | 438 ✗ |

- **20 Hz** укладывается в бюджет только при dirty ≤ ~9% (включая keyframe 17.6 КБ/с). Тихая фаза боя — да, массовое сражение — нет.
- **10 Hz** покрывает dirty ≤ ~23% и целевой сценарий «3000 юнитов, 10% dirty»: **~60 КБ/с на клиента ≈ 4.8 Мбит/с на 10 игроков** — уверенно внутри коридора 50–100 КБ/с.
- **Рекомендация**: replication cadence **10 Hz** как базовая (decoupled от 20 Hz симуляции), с адаптивной деградацией 10→5 Hz по BandwidthGovernor (§9.2) и опциональным апгрейдом до 20 Hz в тихих фазах. Дополнительные резервы при dirty > 20% (в порядке приоритета): FoW-фильтрация (§13, обычно −30–60% трафика на клиента), квантование позиции (i16 mm-дельта против базы, −50% на позицию), кластеризация движущихся строем юнитов.
- Доля keyframe в бюджете при I = 10 с существенна (17.6 из 60 КБ/с = 29% при N=3000) ⇒ Owner Decision по интервалу: **15–20 с** предпочтительно (8.8 КБ/с), если StateChecksum-контур (§7.4) надёжен.

**Поправка на 10% сетевых потерь (активный клиент):** NACK-запросы, повторные слайсы и Cumulative Delta добавляют по консервативной оценке $\sim 30{-}50\text{ КБ/с}$ server egress. Для рекомендованного сценария 10 Hz / 3000 entities / dirty 10%:

$$B_{10\%\ loss} \approx 60 + (30\ldots50) = 90\ldots110\text{ КБ/с} < 128\text{ КБ/с}$$

Даже этот loss-сценарий укладывается в hard cap 128 КБ/с; конкретная доля NACK/repair/catch-up должна быть подтверждена benchmark-профилем §11.

### 12.2. Анализ аллокаций памяти и риски GC Spikes в кодовой базе

1. **Проблема `MatchServer.GetAllSnapshots()`**: в текущем коде `GetAllSnapshots()` при каждом вызове аллоцирует новый массив `ServerUnitSnapshot[]` в управляемой куче. При 3000 сущностях и 10–20 Hz это создаёт гигантское давление на Garbage Collector (GC) и риск фризов симуляции.

   **Требование Phase 2.6:** дельта-генератор и сериализатор обязаны работать строго через предвыделенные переиспользуемые пулы буферов (Zero-GC hot path).

2. **Проблема `SnapshotTargets`**: getter копирует массив при каждом обращении — результат должен кэшироваться в планировщике репликации на период эмиссии.

3. **Разделение серверных колец и срезов**:
   - хранятся ровно **2 полных среза** для вычисления дельты — текущая и предыдущая эмиссии;
   - отдельно хранятся **120 компактных change-set'ов** в `RetainedDeltaHistoryWindow` для построения Establishing/Cumulative Delta;
   - полные срезы и change-set'ы общие для всех 10 клиентов; per-client остаются только ACK/base cursors, фильтрованное view и bounded assembly/send buffers.

Для 5000 сущностей два полных среза занимают:

$$2 \times (16 + 39 \times 5000) = 390\,032\text{ байт} \approx 0.39\text{ МБ}$$

Окно change-set'ов зависит от dirty-rate: при 10% и 14 B/entry грубая верхняя оценка `120 × 500 × 14 B ≈ 0.84 МБ`; при 50% — ~4.2 МБ. Оно не дублируется на клиента и должно измеряться вместе с Zero-GC benchmark.

## 13. Вопрос 11 — граница фильтрации тумана войны (Interest Management / FoW)

### 13.1. Принцип: фильтр — это view, а не изменение формата

Ключевое архитектурное требование: **формат дельт не знает о FoW**. Фильтрация — ступень ReplicationPipeline между захватом состояния `MatchServer` и кодированием дельты/базы. Всё, что меняется от фильтра, — состав записей и состав dirty-набора, а ADD/UPDATE/REMOVE-семантика остаётся прежней.

### 13.2. Интерфейс (v1-спецификация, чистый C#, нулевые аллокации в горячем пути)

```csharp
namespace GlobalFront.Server.Snapshot
{
    /// <summary>Per-player per-tick представление видимости сущностей.</summary>
    public interface IReplicationFilter
    {
        /// <summary>Вызывается один раз на (client, tick) перед кодированием дельты/keyframe.</summary>
        void BuildView(in ReplicationContext context, ReplicationViewBuilder builder);

        /// <summary>Зарезервировано: политика для частично видимых сущностей (FoW-«призраки»).</summary>
        VisibilityDecision DecideVisibility(in ReplicationContext context, EntityId entity);
    }

    public enum VisibilityDecision : byte
    {
        FullVisible = 0,   // реплицировать все поля
        GhostVisible = 1,  // реплицировать «стелс-подмножество» (см. 13.4)
        Hidden = 2         // не реплицировать вообще
    }
}
```

`BuildView(in ReplicationContext, ReplicationViewBuilder)` является нормативным **batch hot path**: один вызов на `(client, tick)` заполняет переиспользуемый `ReplicationViewBuilder` (`Add(EntityId)`, `SetGhost(EntityId)`) и возвращает отсортированное view. Вызов `DecideVisibility` для каждой пары entity/client в горячем цикле запрещён: при 3000 сущностях, 10 клиентах и 10 Hz это создало бы `3000 × 10 × 10 = 300 000` виртуальных вызовов в секунду. `DecideVisibility` остаётся только optional slow-path/policy hook вне массового цикла. Дефолтная реализация 2.6 — `AllVisibleFilter` (passthrough).

### 13.3. Per-client dirty tracking

При активном фильтре dirty-набор **должен считаться per-client**:

```
clientDirty(e) = (globalDirty(e) AND visible(client, e))
                 OR visibilityChanged(client, e)        // hidden→visible = ADD, visible→hidden = REMOVE(ghost)
```

- Hidden→Visible повторно шлёт ADD с тем же `EntityId` — переиспользование id не требуется (инвариант §7.1 соблюдается).
- Visible→Hidden шлётся как REMOVE с `Cause = 1 (FoW-hidden)`: клиент удаляет представление; tombstone-горизонт для FoW-remove может быть короче (сервер знает, что сущность жива; повторный ADD при возврате видимости покрывает рассинхрон).
- Память: per-client bitset видимости (3000 bits = 375 B на клиента) + per-client dirty-маски — сотни КБ суммарно, приемлемо.

### 13.4. GhostVisible и анти-чит

Полная серверная фильтрация даёт anti-maphack бонус (позиции вне обзора не утекают в трафик). Альтернатива из GDD-класса C&C — детерминированный клиентский FoW: тогда сервер шлёт всё, а фильтр в 2.6 остаётся passthrough. Дизайн-граница совместима с обоими вариантами:

- `GhostVisible`: юнит «последний раз виденный» реплицируется подмножеством полей (позиция + владелец), без health/целей — подмножество задаётся той же dirtyMask-машиной (маска фильтруется AND-ом), т.е. **формат дельт не расширяется**.
- Решение «сервер-FoW vs клиент-FoW» — GDD/Owner Decision, к формату отношения не имеет; интерфейс допускает оба.

### 13.5. Что это фиксирует уже сейчас (обязательства Phase 2.6)

1. Дельта-кодер получает на вход **уже отфильтрованное** view — никаких «сервер шлёт всё, клиент фильтрует».
2. Keyframe per-client (база клиента содержит только его view) — иначе при включении FoW базы окажутся несовместимы с дельтами.
3. `StateChecksum` (§7.4) считается **по view клиента**, не по глобальному состоянию (иначе ложные ребейзы после включения FoW).
4. В статистике egress отдельно считаются filtered-out записи — метрика для будущего тюнинга FoW.


## 14. Сводка рисков

| Риск | Уровень | Анализ и митигация |
|---|---|---|
| **Граничный таймаут `FragmentLifetimeMs = 2000 мс`** | Medium | В ADR-009 время жизни собираемого фрагментированного сообщения ограничено 2 секундами. При потере 10% пакетов доставка единого 117 КБ сообщения через reliable-ретрансмиссии приближается к ~2.0 с. Слайсы 8–16 КБ на C2 собираются за 50–150 мс и не приближаются к лимиту. |
| **P0-1: непрерывный chain guard создаёт ложный base desync и REBASING-livelock** | Closed in Rev. 2 | Establishing Delta + Apply-Guard `Tick > LastAppliedTick && BaseTick <= LastAppliedTick && KeyframeRef == CurrentKeyframeSeq`; промежуточные тики безопасно пропускаются. |
| **P1-1: latest-wins отбрасывает полезный reordered tick** | Closed in Rev. 2 | Drop-порог — только `Tick <= LastAppliedTick`; максимум ранее полученного C2-маркера не используется. Multipart/slices дедуплицируются по собственным ключам. |
| Tail-latency после исчерпания repair keyframe | Low после P1-4 | После 2 NACK-циклов немедленный `SnapshotRequest` свежего keyframe; ожидание плановых 15–20 с запрещено. |
| Вечное ожидание в UNBASED | Low после P1-3 | Не более 3 запросов с backoff 0.5/1.0/2.0 с, затем `ConnectionFailed / ResyncRequired` Phase 2.7. |
| Призраки юнитов | Low при соблюдении §7 | `WindowEst = HistoryWindow = Tombstone Horizon = 120`; база старше окна всегда REBASING; TombstoneEcho + xxHash32 fingerprint. |
| Egress-превышение при dirty% и 10% loss | Medium | BandwidthGovernor + Pre-emption Rule; рекомендованный активный сценарий с loss-overhead = 90–110 КБ/с < hard cap 128 КБ/с; benchmark §11 обязателен. |
| Расширение handshake ломает 2.5-клиентов | Low | capability-битмап additive; back-compat матрица §8.3; v1 full-stream сохраняется. |
| Сложность per-client dirty при FoW | Medium | `BuildView` — один batch-вызов на client/tick; 300 000 per-entity virtual calls/s исключены; passthrough-дефолт. |
| Cumulative Delta > 32 КБ при большой потере | Low | порог → немедленный fresh keyframe вместо гигантского unreliable-пакета. |
| Детерминизм-файрвол | Low | репликация read-only над состоянием тика; фильтр/кодер не пишут в симуляцию; pump-поток как в ADR-009. |

## 15. Open Owner Decisions (Phase 2.6)

P0/P1 замечания закрыты в protocol design Revision 2 и не являются открытыми owner decisions. Для ADR остаются следующие продуктовые/пороговые решения:

| ID | Вопрос | Рекомендация |
|---|---|---|
| OD-10 | Механизм доставки и repair keyframe (§3) | C2-слайсы + NACK-repair + pacing; максимум 2 NACK-цикла на keyframe, затем немедленный `SnapshotRequest` свежего keyframe в рамках token bucket |
| OD-11 | Интервал планового keyframe | `WindowEst = RetainedDeltaHistoryWindow = 120` уже зафиксирован Revision 2; owner утверждает интервал 15–20 с по результатам benchmark |
| OD-12 | Cadence репликации | базово 10 Hz, decoupled от 20 Hz симуляции; адаптивность 20/10/5 Hz |
| OD-13 | Бюджеты egress per-client | цель 50–100 КБ/с, hard cap 128 КБ/с, burst 16 КБ; 10% loss profile обязан войти в acceptance benchmark |
| OD-14 | Семантика очистки MoveTarget в dirty-маске (§6.3) | «бит 3 ⇒ цель очищена, координаты не передаются» |
| OD-15 | Шифрование/подпись delta-пакетов | по-прежнему deferred (наследие ADR-009); revisit перед dedicated deployment |
| OD-16 | FoW: серверная фильтрация vs клиентский детерминизм | GDD-зависимо; batch `IReplicationFilter.BuildView` фиксируется в 2.6, реализация — passthrough |
| OD-17 | `MatchId` в снапшотах (reopening OD-5 ADR-008) | перенести в handshake/capability, а не в каждый пакет |

## 16. Explicit Out of Scope

Реализация reconnect/resync (2.7), prediction/reconciliation, шифрование, NAT traversal, matchmaking; серверная реализация FoW (в 2.6 — только граница интерфейса); изменения `CommandHeader`, `MatchServer`, `TickDriver`, `SessionManager`; сжатие (LZ4/словарное) — отдельное решение после бенчмарков, формат допускает `Flags.Compressed` без смены версии.

## Связанные документы

- [ADR-009 Network Transport (Accepted)](ADR-009-Network-Transport-DRAFT.md)
- [Phase 2.5 Network Transport — R&D Report](Phase_02_05_Network_Transport_RND.md)
- [Phase 02 Multiplayer](../Phases/Phase_02_Multiplayer.md)
- [Decisions](../DECISIONS.md) • [Architecture](../ARCHITECTURE.md) • [AI Contract](../AI_CONTRACT.md)

