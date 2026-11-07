namespace GlobalFront.Core.Simulation
{
    /// <summary>
    /// Constants that form part of the network protocol and may not be changed
    /// without a compatibility review.
    /// </summary>
    public static class SimulationConstants
    {
        public const int ServerTickRate = 20;
        public const double ServerTickDurationSeconds = 1.0 / ServerTickRate;
        public const int MaxCatchUpTicksPerFrame = 4;
        public const int MaxPlayers = 10;
        public const int MaxSelectedEntities = 100;

        /// <summary>
        /// Hard ceiling on live units in one match. Every spawn path funnels
        /// through <see cref="GlobalFront.Server.MatchServer"/>'s single roster
        /// chokepoint, so this is what bounds the presentation snapshot buffers
        /// that size themselves from it.
        /// </summary>
        public const int MaxUnitsPerMatch = 1024;

        public const int MaxFormationSpacingMm = 100_000;
        public const int MaxWorldCoordinateMm = 1_000_000_000;

        /// <summary>
        /// Radius in millimetres within which a unit with auto-acquire enabled
        /// will seek the closest enemy when it has no explicit attack target.
        /// Authoritative target acquisition lives in
        /// <see cref="GlobalFront.Server.MatchServer"/>; the client presentation
        /// layer no longer runs this phase locally.
        /// </summary>
        public const int AutoAcquireRangeMm = 18000;

        /// <summary>
        /// Максимальная длина очереди предварительного заказа в одном
        /// производственном здании (канон C&C Generals).
        /// </summary>
        public const int MaxProductionQueueSlots = 9;

        /// <summary>
        /// Максимальное число активных производственных зданий на матч.
        /// </summary>
        public const int MaxProductionBuildings = 128;

        /// <summary>Насыщающий потолок баланса игрока; прибавление — только Math.Min(MaxCredits, current + income).</summary>
        public const int MaxCredits = 2_000_000_000;

        /// <summary>Максимум складов снабжения (Supply Dock) на матч.</summary>
        public const int MaxSupplyDocks = 64;

        /// <summary>Максимум центров снабжения (Supply Center) на матч.</summary>
        public const int MaxSupplyCenters = 64;

        /// <summary>Максимум сборщиков (Harvester) на матч.</summary>
        public const int MaxHarvesters = 128;

        /// <summary>Ровно 4 параллельных слота погрузки на склад (канон C&C Generals).</summary>
        public const int SupplyDockSlots = 4;

        /// <summary>Ёмкость очереди ожидания одного склада.</summary>
        public const int MaxWaitingHarvestersPerDock = 16;

        /// <summary>Стандартная вместимость грузовика USA/China/GLA/Russia/NATO, долларов за цикл.</summary>
        public const int DefaultHarvesterCapacityDollars = 300;

        /// <summary>Длительность погрузки, тиков (2.0 секунды при 20 Гц).</summary>
        public const int DefaultLoadingDurationTicks = 40;

        /// <summary>Радиус взаимодействия склада и радиус разгрузки центра, мм (6 метров).</summary>
        public const int DefaultEconomyInteractionRadiusMm = 6_000;

        /// <summary>Максимум нейтральных нефтяных вышек (Oil Derrick) на матч.</summary>
        public const int MaxOilDerricks = 32;

        /// <summary>Максимум доходных зданий вторичной экономики (Drop Zone + Black Market) на матч.</summary>
        public const int MaxSecondaryIncomeBuildings = 64;

        /// <summary>Максимум хакеров (Hacker) на матч.</summary>
        public const int MaxHackers = 128;

        /// <summary>Длительность оккупации нефтяной вышки пехотинцем, тиков (4 секунды при 20 Гц).</summary>
        public const int OilDerrickCaptureDurationTicks = 80;

        /// <summary>Интервал выплат нефтяной вышки владельцу, тиков (2 секунды).</summary>
        public const int OilDerrickPayoutIntervalTicks = 40;

        /// <summary>Выплата нефтяной вышки владельцу, долларов за интервал.</summary>
        public const int OilDerrickPayoutDollars = 100;

        /// <summary>Интервал выплат Drop Zone (USA), тиков (60 секунд).</summary>
        public const int DropZonePayoutIntervalTicks = 1200;

        /// <summary>Выплата Drop Zone (USA), долларов за интервал.</summary>
        public const int DropZonePayoutDollars = 1500;

        /// <summary>Интервал выплат Black Market (GLA), тиков (4 секунды).</summary>
        public const int BlackMarketPayoutIntervalTicks = 80;

        /// <summary>Выплата Black Market (GLA), долларов за интервал.</summary>
        public const int BlackMarketPayoutDollars = 50;

        /// <summary>Интервал выплат Hacker (China), тиков (2 секунды).</summary>
        public const int HackerPayoutIntervalTicks = 40;

        /// <summary>Выплата Hacker (China), долларов за интервал.</summary>
        public const int HackerPayoutDollars = 20;

        /// <summary>Интервал выплат Бурового комплекса (Russia), тиков (60 секунд).</summary>
        public const int OilRigPayoutIntervalTicks = 1200;

        /// <summary>Выплата Бурового комплекса (Russia), долларов за интервал.</summary>
        public const int OilRigPayoutDollars = 1000;

        /// <summary>Интервал выплат Coalition Logistics Hub (NATO), тиков (20 секунд).</summary>
        public const int LogisticsHubPayoutIntervalTicks = 400;

        /// <summary>Выплата Coalition Logistics Hub (NATO), долларов за интервал.</summary>
        public const int LogisticsHubPayoutDollars = 200;

        /// <summary>Потолок зданий вторичного дохода одного типа на игрока (Step 5.5.3).</summary>
        public const int MaxSecondaryBuildingsPerPlayer = 4;

        /// <summary>Суммарный потолок хакеров на игрока: 4 Интернет-центра × 4 хакера (Step 5.5.3).</summary>
        public const int MaxHackersPerPlayer = 16;

        /// <summary>
        /// Радиус орды Horde Bonus (Китай), мм (10 м, черновик 1.0): от этого
        /// числа юнитов одной категории в радиусе действует бонус.
        /// </summary>
        public const int HordeRadiusMm = 10_000;

        /// <summary>Порог орды Horde Bonus (Китай): юнитов одной категории в радиусе.</summary>
        public const int HordeThreshold = 5;

        /// <summary>Бонус урона орды, ‰ (+25%).</summary>
        public const int HordeDamagePermille = 1250;

        /// <summary>Бонус скорости орды, ‰ (+10%).</summary>
        public const int HordeSpeedPermille = 1100;

        /// <summary>
        /// Edge length in millimetres of one cell of the server-side visibility
        /// uniform grid (2^13 = 8192 mm = 8.192 m). The grid accelerates
        /// sight queries from O(N^2) to O(N * k) by scanning only cells that
        /// can hold a unit inside the sight radius.
        /// </summary>
        public const int VisibilityGridCellSizeMm = 8192;

        /// <summary>
        /// Default sight radius in millimetres (20 m) used by
        /// <see cref="GlobalFront.Core.Visibility.VisibilityEngine"/> when the
        /// caller does not pass an explicit radius.
        /// </summary>
        public const int DefaultSightRadiusMm = 20000;
    }
}
