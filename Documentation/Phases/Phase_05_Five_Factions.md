# Phase 05 — Full Five Factions (Faction Asymmetry, Strategic Economy & 2v2 Topology)

> Status: **[COMPLETED / AUDITED: ZERO DEFECTS CERTIFIED]** • Milestones M2 (2v2 Topology) & M3 (Five Factions) • Commit `f21ba3f` • 1705 EditMode тестов 100% green

## Goal

Реализовать полный стратегический фундамент RTS формулы *Generals / Zero Hour* для пяти основных фракций (USA, China, GLA, EU, Russia), целочисленную экономику, строительство баз на дискретной сетке, очереди производства, матрицу брони/урона и авторитетную 2v2 топологию на выделенном сервере.

## Dependencies

- M0 Simulation Foundation (Phase 1);
- M1 1v1 Dedicated Server Prototype (Phase 4, commit `66419a8` / `6d8a20c`);
- M2 Playable RTS Core (Phase 3);
- ADR-015 Integer Armor & Damage Matrix (8x7);
- Snapshot Protocol v3 & Command Codec v2.

## Delivered Scope

### 1. Step 5.1: Integer Armor & Damage Matrix (8x7) — [COMPLETE] (`bf4bafd`..`3aec610`)
- **8 типов урона** (`SmallArms`, `HeavyMachineGun`, `AntiTankCannon`, `GuidedMissile`, `ArtilleryExplosive`, `ThermobaricNapalm`, `ToxinChemical`, `HighTechLaser`).
- **7 типов брони** (`Infantry`, `HeavyInfantry`, `LightVehicle`, `HeavyVehicle`, `Aircraft`, `Structure`, `Fortified`).
- **Целочисленный расчет в пермилле** (1000 = ×1.0) с детерминированным округлением «половина вверх»: `(scaled + 500) / 1000`. Zero-GC hot path.
- `LegacySentinel = 255`: 100% обратная совместимость с легаси-юнитами.
- Линейный целочисленный splash falloff, гатлинг-эскалация (0..3 уровни), химическая дезинфекция гарнизонов токсинами.

### 2. Step 5.2: Base Building, 500mm Snap Grid & Power Grid — [COMPLETE] (`e6a250f`..`07b669b`)
- Дискретная координатная сетка 500 мм (`SnapGrid500Mm`).
- Осесимметричные AABB футпринты зданий, проверка пересечений и преаллоцированный реестр размещения на 512 слотов (`PlacementRegistry`).
- Строительные леса 0%..100%, строители Dozer / Worker, детерминированный строительный FSM и мульти-строительство рабочих ГЛА.
- Энергетический баланс `PowerLedger`: расчет генерации и потребления, режим LowPower, деактивация защитных турелей при блэкауте.
- Интеграция строительного цикла и ремонта в авторитетный тик `MatchServer`.

### 3. Step 5.3: Production Queues (5-slot Ring Buffer) & Rally Points — [COMPLETE] (`0d4d747`..`548ea5c`)
- Кольцевой буфер очереди производства на 5 слотов на каждую фабрику (`ProducerLedger`).
- Проверка требований техдрева по битовой маске `TechMask`.
- Точки сбора `RallyPointMm` с автоматическим приказом на марш вышедшему юниту.
- 50% замедление производства при дефиците энергии (`LowPower`).
- Автоматическая регистрация завершенных строительством фабрик в реестр производителей.

### 4. Step 5.4: Economy Engine & Supply Lines — [COMPLETE] (`b9ab9df`..`2ee5153`)
- Централизованный учет ресурсов `CreditLedger` (списание при заказе, возврат при отмене).
- Склады ресурсов `SupplyDock` с конечным запасом и 4 физическими слотами одновременной погрузки.
- Детерминированный сборщик с тиковым FSM (движение к складу -> ожидание слота -> погрузка 40 тиков -> движение к центру снабжения -> мгновенная разгрузка).
- Вторичный доход: хакеры Китая, черный рынок ГЛА, зоны сброса США (лимит 4 источника на игрока, 1 хакер на интернет-центр).
- `PlayerStateSync`: Zero-GC 1 Hz эмиттер баланса кредитов и энергии клиентам.

### 5. Step 5.5: Full 5-Faction Roster Database & Protocol v3 — [COMPLETE] (`66c9941`..`a09cd80`)
- **100+ утвержденных чертежей** для пяти фракций:
  - **USA:** Dozer, Ranger, Missile Defender, Sniper, Crusader, Humvee, Paladin, Tomahawk, Comanche, Raptor, Aurora, Chinook, Command Center, Cold Fusion, Barracks, War Factory, Airfield, Supply Center, Patriot, Particle Cannon.
  - **China:** Dozer, Red Guard, Tank Hunter, Hacker, Black Lotus, Battlemaster, Dragon Tank, Troop Crawler, Overlord, Gatling Tank, MiG, Supply Truck, Command Center, Nuclear Reactor, Barracks, War Factory, Airfield, Supply Center, Gatling Cannon, Nuclear Missile.
  - **GLA:** Worker, Rebel, RPG Trooper, Terrorist, Hijacker, Jarmen Kell, Scorpion, Technical, Marauder, Quad Cannon, Rocket Buggy, Bomb Truck, Supply Worker, Command Center, Barracks, Arms Dealer, Supply Stash, Tunnel Network, Stinger Site, Black Market, SCUD Storm.
  - **EU:** CEV, European Peacekeeper, Javelin Anti-Tank, Leopard Tank, IFV, Panzerhaubitze, Eurofighter Typhoon, Heavy Transport, Command Center, Solar Array, Barracks, Factory, Airbase, Supply Depot, Anti-Air Battery, Orbital Cannon.
  - **Russia:** IMR, Conscript, Shock Trooper, Boris, T-90 Tank, BTR-80, BMPT Terminator, Msta-S Artillery, Ka-52 Alligator, Su-34 Fullback, Transport Truck, Command Center, Tesla Reactor, Barracks, Heavy Factory, Airfield, Logistics Post, Arena Active Defense, Topol-M ICBM.
- **Wire Delta Protocol v3:** Включение 16-битного `BlueprintId` в `DeltaAddRecord`.
- **Command Wire Codec v2:** Авторитетная поддержка сетевых команд `Build` и `Produce`.
- Автоматический валидатор целостности каталога блюпринтов `BlueprintCatalogValidator`.

### 6. Step 5.6: 2v2 Topology Hardening & Shared Vision — [COMPLETE] (`6e32647`..`355f2d6`)
- Сетевая топология 2v2 (Red: игроки 1-2, Blue: игроки 3-4).
- Командное лобби dedicated-сервера, CLI аргумент `-topology 1v1|2v2`.
- Командная геометрия спавна (базы союзников сгруппированы на своей стороне карты).
- Shared Vision: видимость союзников и совместное раскрытие вражеского тумана войны.
- Фильтрация дружественного splash-урона (снаряды и взрывы союзников не наносят урон сокомандникам).
- Разделение радарных меток на миникарте (зеленый = союзник, красный = враг, синий = игрок).

### 7. M2 Hardening Remediation & Adversarial Certification — [COMPLETE] (`e6e99f9`..`f21ba3f`)
- **P0-01:** Динамический поиск слотов через `TeamSlotMapping2v2`.
- **P1-01:** Поддержка CLI флага `-topology` в `DedicatedServerRunner`.
- **P1-03:** Интеграция `PlayerStateSync` в сетевой цикл без циклических аллокаций памяти.
- **P1-04:** Гарантированное списание кредитов перед заказом и возврат при отмене/уничтожении.
- **P1-05:** Согласование знака энергобаланса (`NetPower = Produced - Consumed`).
- **P1-06:** Автоматическая регистрация фабрик в `ProducerLedger` после завершения стройки.
- **P1-07:** Инициализация времени постройки строго из каталога блюпринтов.
- **P1-08:** Валидация строителя по списку `BuilderIds` чертежа.
- **P1-09:** Общий лимит 4 вторичных источников дохода на игрока.
- **P1-10:** Проверка координат сетки на предотвращение целочисленного переполнения.
- **P1-11:** Запрет постановки зданий в очередь производства фабрик; лимит 1 хакера на интернет-центр.
- **N-01 & N-07 Hotfixes:** Устранение исключения мобильных юнитов в гейте `Build` (N-01) и установка чистого инварианта валидного футпринта `FootprintWidthCells <= 0 || FootprintHeightCells <= 0 -> InvalidPayload` (N-07), разрешающего строительство всех типов структур (`Building`, `Supply`, `Defense`, `Superweapon`).

## Milestones Achieved

- **Milestone M2 (2v2 Topology & Combat Hardening) — [CERTIFIED APPROVE]** (`f21ba3f`)
- **Milestone M3 (Full Five Factions Roster Database) — [CERTIFIED APPROVE]** (`f21ba3f`)

## Verification & Tests

- **EditMode:** 1705 / 1705 passed (100% green, 0 failed, 0 skipped, 2026-11-10).
- **PlayMode:** 20 / 22 passed (2 предсуществующие заглушки для легаси-юнитов).
- **Всего:** 1727 автоматизированных тестов.
- **Zero-GC:** 0 B GC.Alloc на всех тиковых путях симуляции, расчетах боя, движении, строительстве, экономике и сетевом транспорте.
- **Состязательный аудит:** Независимый ре-аудит QDuck вынес вердикт **UNCONDITIONAL APPROVE (Server Core)** на коммите `f21ba3f`.

## Definition of Done

Все пять основных фракций имеют полный утверждённый ростер, уникальную механику, целочисленную экономику, строительство баз, очереди производства, матрицу брони/урона и проверенную 2v2 топологию на выделенном сервере. Фаза 5 полностью закрыта и сертифицирована.
