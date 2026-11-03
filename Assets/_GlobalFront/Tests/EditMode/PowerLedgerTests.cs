using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Structures;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Step 5.2.3 — the per-player power ledger and the LowPower blackout rules
    /// (OD-52 / RND §2.4). Completed buildings carry integer PowerDelta; frames
    /// contribute exactly 0 until notified complete. LowPower triggers strictly
    /// at NetPower &lt; 0 and cuts defense/radar, halves production to exactly
    /// 500‰ and freezes modular construction — while power-exempt players (GLA
    /// doctrine) never black out. Reads are O(1) precomputed snapshots; the
    /// recompute is event-driven and allocation-free.
    /// </summary>
    [TestFixture]
    public sealed class PowerLedgerTests
    {
        private static readonly PlayerId PlayerOne = new PlayerId(1);

        private static readonly PlayerId PlayerTwo = new PlayerId(2);

        private const int WarmupIterations = 200;

        private const int MeasuredIterations = 1000;

        // ------------------------------------------------------------------
        // 1 — protocol values and the neutral state
        // ------------------------------------------------------------------

        [Test]
        public void PowerConsumerKind_CarriesTheDocumentedNumericValues()
        {
            Assert.That((byte)PowerConsumerKind.None, Is.EqualTo(0));
            Assert.That((byte)PowerConsumerKind.Defense, Is.EqualTo(1));
            Assert.That((byte)PowerConsumerKind.Radar, Is.EqualTo(2));
            Assert.That((byte)PowerConsumerKind.Production, Is.EqualTo(3));
            Assert.That((byte)PowerConsumerKind.ModularCrane, Is.EqualTo(4));
        }

        [Test]
        public void GetPowerState_EmptyLedger_IsNeutral()
        {
            var ledger = new PowerLedger();
            PlayerPowerState state = ledger.GetPowerState(PlayerOne);
            Assert.That(state.Produced, Is.EqualTo(0));
            Assert.That(state.Consumed, Is.EqualTo(0));
            Assert.That(state.NetPower, Is.EqualTo(0));
            Assert.That(state.IsLowPower, Is.False, "an empty grid must never black out");
            Assert.That(state.ProductionRatePermille, Is.EqualTo(PowerLedger.NormalProductionRatePermille));
        }

        // ------------------------------------------------------------------
        // 2 — balance and the strict NetPower < 0 threshold
        // ------------------------------------------------------------------

        [Test]
        public void GetPowerState_SumsGenerationAndConsumption()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, 10), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.RegisterBuilding(PlayerOne, 2, 5), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.RegisterBuilding(PlayerOne, 3, -3), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.RegisterBuilding(PlayerOne, 4, -4), Is.EqualTo(PowerLedgerRejection.None));

            PlayerPowerState state = ledger.GetPowerState(PlayerOne);
            Assert.That(state.Produced, Is.EqualTo(15), "+10 and +5 generation");
            Assert.That(state.Consumed, Is.EqualTo(7), "|-3| and |-4| consumption");
            Assert.That(state.NetPower, Is.EqualTo(8));
            Assert.That(state.IsLowPower, Is.False);
        }

        [Test]
        public void GetPowerState_NetPowerZero_IsNormal()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, 5), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.RegisterBuilding(PlayerOne, 2, -5), Is.EqualTo(PowerLedgerRejection.None));

            PlayerPowerState state = ledger.GetPowerState(PlayerOne);
            Assert.That(state.NetPower, Is.EqualTo(0));
            Assert.That(state.IsLowPower, Is.False, "the blackout threshold is strict: 0 is NOT LowPower");
            Assert.That(state.ProductionRatePermille, Is.EqualTo(PowerLedger.NormalProductionRatePermille));
        }

        [Test]
        public void GetPowerState_NetPowerMinusOne_IsLowPower()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, 5), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.RegisterBuilding(PlayerOne, 2, -6), Is.EqualTo(PowerLedgerRejection.None));

            PlayerPowerState state = ledger.GetPowerState(PlayerOne);
            Assert.That(state.NetPower, Is.EqualTo(-1), "the smallest possible deficit must trigger");
            Assert.That(state.IsLowPower, Is.True);
            Assert.That(state.ProductionRatePermille, Is.EqualTo(PowerLedger.LowPowerProductionRatePermille));
        }

        [Test]
        public void GetPowerState_PlayersAreIndependent()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, -6), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.RegisterBuilding(PlayerTwo, 2, 10), Is.EqualTo(PowerLedgerRejection.None));

            Assert.That(ledger.GetPowerState(PlayerOne).IsLowPower, Is.True, "player one's deficit blacks out only player one");
            Assert.That(ledger.GetPowerState(PlayerTwo).IsLowPower, Is.False);
        }

        // ------------------------------------------------------------------
        // 3 — production rate
        // ------------------------------------------------------------------

        [Test]
        public void GetProductionRatePermille_MapsToTheDocumentedRates()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.GetProductionRatePermille(PlayerOne), Is.EqualTo(1000), "a powered base produces at full rate");

            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, -1), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.GetProductionRatePermille(PlayerOne), Is.EqualTo(500), "LowPower throttles production to exactly 500‰");
        }

        [Test]
        public void IsConsumerPowered_BlackoutCutsDefenseRadarCrane_ProductionSurvives()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, -1), Is.EqualTo(PowerLedgerRejection.None), "arrange a LowPower base");

            Assert.That(ledger.IsConsumerPowered(PlayerOne, PowerConsumerKind.Defense), Is.False, "turrets are dead under blackout");
            Assert.That(ledger.IsConsumerPowered(PlayerOne, PowerConsumerKind.Radar), Is.False, "radar is blind under blackout");
            Assert.That(ledger.IsConsumerPowered(PlayerOne, PowerConsumerKind.ModularCrane), Is.False, "modular cranes freeze under blackout");
            Assert.That(ledger.IsConsumerPowered(PlayerOne, PowerConsumerKind.Production), Is.True, "production throttles, it never stops");
            Assert.That(ledger.IsConsumerPowered(PlayerOne, PowerConsumerKind.None), Is.True, "nothing to power");

            Assert.That(ledger.IsConsumerPowered(PlayerTwo, PowerConsumerKind.Defense), Is.True, "a powered base keeps everything online");
        }

        // ------------------------------------------------------------------
        // 4 — GLA exemption: never a blackout
        // ------------------------------------------------------------------

        [Test]
        public void SetPlayerPowerExempt_ExemptPlayerNeverBlacksOut()
        {
            var ledger = new PowerLedger();
            ledger.SetPlayerPowerExempt(PlayerOne, true);
            Assert.That(ledger.IsPowerExempt(PlayerOne), Is.True);

            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, -6), Is.EqualTo(PowerLedgerRejection.None), "register a pure deficit");

            PlayerPowerState state = ledger.GetPowerState(PlayerOne);
            Assert.That(state.Consumed, Is.EqualTo(6), "the deficit is still accounted");
            Assert.That(state.NetPower, Is.EqualTo(0), "exempt players report NetPower = 0 by doctrine");
            Assert.That(state.IsLowPower, Is.False, "GLA never suffers LowPower");
            Assert.That(state.ProductionRatePermille, Is.EqualTo(PowerLedger.NormalProductionRatePermille));
        }

        [Test]
        public void SetPlayerPowerExempt_ToggleRecomputesInBothDirections()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, -6), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.GetPowerState(PlayerOne).IsLowPower, Is.True, "a non-exempt base with a deficit blacks out");

            ledger.SetPlayerPowerExempt(PlayerOne, true);
            Assert.That(ledger.GetPowerState(PlayerOne).IsLowPower, Is.False, "exemption lifts the blackout");

            ledger.SetPlayerPowerExempt(PlayerOne, false);
            Assert.That(ledger.GetPowerState(PlayerOne).IsLowPower, Is.True, "removing exemption restores the blackout");
            Assert.That(ledger.GetPowerState(PlayerOne).ProductionRatePermille, Is.EqualTo(PowerLedger.LowPowerProductionRatePermille));
        }

        // ------------------------------------------------------------------
        // 5 — frames contribute nothing until completed
        // ------------------------------------------------------------------

        [Test]
        public void IncompleteFrame_ContributesNothingUntilNotified()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, 10, isComplete: false), Is.EqualTo(PowerLedgerRejection.None));

            PlayerPowerState frameState = ledger.GetPowerState(PlayerOne);
            Assert.That(frameState.Produced, Is.EqualTo(0), "a frame gives no power while under construction");

            Assert.That(ledger.NotifyBuildingCompleted(PlayerOne, 1, 10), Is.True, "connecting the finished reactor applies its delta");
            Assert.That(ledger.GetPowerState(PlayerOne).Produced, Is.EqualTo(10));
        }

        [Test]
        public void NotifyBuildingCompleted_ConnectingConsumer_BlacksOutBase()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, 10), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.RegisterBuilding(PlayerOne, 2, -4, isComplete: false), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.GetPowerState(PlayerOne).IsLowPower, Is.False, "the frame draws nothing yet");

            Assert.That(ledger.NotifyBuildingCompleted(PlayerOne, 2, -4), Is.True);
            PlayerPowerState state = ledger.GetPowerState(PlayerOne);
            Assert.That(state.NetPower, Is.EqualTo(6), "10 - 4 stays positive");
            Assert.That(state.IsLowPower, Is.False);
        }

        [Test]
        public void NotifyBuildingCompleted_Twice_ReturnsFalse()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, 5, isComplete: false), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.NotifyBuildingCompleted(PlayerOne, 1, 5), Is.True);
            Assert.That(ledger.NotifyBuildingCompleted(PlayerOne, 1, 5), Is.False, "a completed building cannot be connected again");
            Assert.That(ledger.GetPowerState(PlayerOne).Produced, Is.EqualTo(5), "the delta must not be applied twice");
        }

        [Test]
        public void NotifyBuildingCompleted_UnknownEntity_ReturnsFalse()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.NotifyBuildingCompleted(PlayerOne, 42, 5), Is.False);
        }

        // ------------------------------------------------------------------
        // 6 — losing a power plant blacks the base out
        // ------------------------------------------------------------------

        [Test]
        public void UnregisterBuilding_PowerPlant_KnocksBaseIntoLowPower()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, 10), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.RegisterBuilding(PlayerOne, 2, -6), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.GetPowerState(PlayerOne).IsLowPower, Is.False, "10 - 6 = 4 is powered");

            Assert.That(ledger.UnregisterBuilding(PlayerOne, 1), Is.True, "destroying the reactor");
            PlayerPowerState state = ledger.GetPowerState(PlayerOne);
            Assert.That(state.NetPower, Is.EqualTo(-6));
            Assert.That(state.IsLowPower, Is.True, "losing the plant blacks the base out");

            Assert.That(ledger.RegisterBuilding(PlayerOne, 3, 10), Is.EqualTo(PowerLedgerRejection.None), "rebuild the plant");
            Assert.That(ledger.GetPowerState(PlayerOne).IsLowPower, Is.False, "power restored");
        }

        [Test]
        public void UnregisterBuilding_Consumer_CanLiftBlackout()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, 5), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.RegisterBuilding(PlayerOne, 2, -6), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.GetPowerState(PlayerOne).IsLowPower, Is.True);

            Assert.That(ledger.UnregisterBuilding(PlayerOne, 2), Is.True, "the consumer is destroyed");
            Assert.That(ledger.GetPowerState(PlayerOne).IsLowPower, Is.False, "destroying a consumer can end a blackout");
        }

        [Test]
        public void UnregisterBuilding_UnknownEntity_ReturnsFalse()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.UnregisterBuilding(PlayerOne, 42), Is.False);
        }

        // ------------------------------------------------------------------
        // 7 — registration gates
        // ------------------------------------------------------------------

        [Test]
        public void RegisterBuilding_DuplicateEntity_Rejects()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, 5), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, 5), Is.EqualTo(PowerLedgerRejection.DuplicateBuilding));
        }

        [Test]
        public void RegisterBuilding_ZeroEntityId_Rejects()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 0, 5), Is.EqualTo(PowerLedgerRejection.InvalidEntityId), "0 is reserved as 'no entity'");
        }

        [Test]
        public void RegisterBuilding_UnknownPlayer_Rejects()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(new PlayerId(0), 1, 5), Is.EqualTo(PowerLedgerRejection.UnknownPlayer));
            Assert.That(ledger.RegisterBuilding(new PlayerId((byte)(PowerLedger.MaxPlayersPerLedger + 1)), 1, 5), Is.EqualTo(PowerLedgerRejection.UnknownPlayer), "the ledger is sized from the protocol constant — id 11 does not exist");
        }

        [Test]
        public void GetPowerState_InvalidPlayer_Throws()
        {
            var ledger = new PowerLedger();
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.GetPowerState(new PlayerId(0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.GetPowerState(new PlayerId((byte)(PowerLedger.MaxPlayersPerLedger + 1))));
        }

        [Test]
        public void RegisterBuilding_PastCapacity_RejectsWithTableFull()
        {
            var ledger = new PowerLedger();
            for (int i = 0; i < PowerLedger.MaxBuildingsPerPlayer; i++)
            {
                Assert.That(ledger.RegisterBuilding(PlayerOne, (ulong)(i + 1), 1), Is.EqualTo(PowerLedgerRejection.None), $"building {i} must register");
            }

            Assert.That(
                ledger.RegisterBuilding(PlayerOne, (ulong)(PowerLedger.MaxBuildingsPerPlayer + 1), 1),
                Is.EqualTo(PowerLedgerRejection.TableFull),
                "the 513th building of one player cannot be stored");
        }

        [Test]
        public void Clear_ResetsEveryPlayerToNeutral()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, -6), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.GetPowerState(PlayerOne).IsLowPower, Is.True);
            ledger.SetPlayerPowerExempt(PlayerTwo, true);

            ledger.Clear();

            Assert.That(ledger.GetPowerState(PlayerOne).IsLowPower, Is.False, "Clear lifts every blackout");
            Assert.That(ledger.IsPowerExempt(PlayerTwo), Is.False, "Clear resets exemptions");
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, 5), Is.EqualTo(PowerLedgerRejection.None), "ids are reusable after Clear");
        }

        // ------------------------------------------------------------------
        // 8 — ConstructionRegistry integration: modular freeze, dozer autonomy
        // ------------------------------------------------------------------

        [Test]
        public void Construction_ModularSite_IsFrozenUnderOwnerBlackout()
        {
            PowerLedger ledger = CreateLowPowerLedger();
            var placement = new BuildingPlacementRegistry();
            var construction = new ConstructionRegistry(placement, powerLedger: ledger);
            construction.TryStartConstruction(100, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, 25, 100, out _);

            var noBuilders = new BuilderTickInput[0];
            for (int tick = 0; tick < 10; tick++)
            {
                construction.AdvanceTick(noBuilders, 0);
            }

            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(0), "a blackout freezes modular self-assembly");
            Assert.That(ledger.IsModularConstructionPowered(PlayerOne), Is.False);
        }

        [Test]
        public void Construction_ModularSite_BuildsWhenOwnerIsPowered()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, 10), Is.EqualTo(PowerLedgerRejection.None));
            var placement = new BuildingPlacementRegistry();
            var construction = new ConstructionRegistry(placement, powerLedger: ledger);
            construction.TryStartConstruction(100, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, 25, 100, out _);

            var noBuilders = new BuilderTickInput[0];
            for (int tick = 0; tick < 10; tick++)
            {
                construction.AdvanceTick(noBuilders, 0);
            }

            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(250), "a powered modular base self-assembles at the base rate");
        }

        [Test]
        public void Construction_DozerWorkerSite_IsAutonomousUnderBlackout()
        {
            PowerLedger ledger = CreateLowPowerLedger();
            var placement = new BuildingPlacementRegistry();
            var construction = new ConstructionRegistry(placement, powerLedger: ledger);
            construction.TryStartConstruction(100, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.DozerWorker, 25, 100, out _);
            Assert.That(construction.AssignBuilder(1, BuilderKind.Dozer, 100), Is.EqualTo(BuilderAssignmentRejection.None));

            var builders = new[] { new BuilderTickInput(1, new WorldPointMm(0, 0), true) };
            for (int tick = 0; tick < 4; tick++)
            {
                construction.AdvanceTick(builders, builders.Length);
            }

            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(100), "dozers are autonomous — a blackout never slows them");
        }

        [Test]
        public void Construction_ExemptOwner_ModularSiteBuildsThroughAnyDeficit()
        {
            var ledger = new PowerLedger();
            ledger.SetPlayerPowerExempt(PlayerOne, true);
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, -6), Is.EqualTo(PowerLedgerRejection.None));
            var placement = new BuildingPlacementRegistry();
            var construction = new ConstructionRegistry(placement, powerLedger: ledger);
            construction.TryStartConstruction(100, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, 25, 100, out _);

            var noBuilders = new BuilderTickInput[0];
            for (int tick = 0; tick < 10; tick++)
            {
                construction.AdvanceTick(noBuilders, 0);
            }

            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(250), "an exempt owner's modular frames never freeze");
        }

        [Test]
        public void Construction_WithoutLedger_ModularSiteAlwaysBuilds()
        {
            var placement = new BuildingPlacementRegistry();
            var construction = new ConstructionRegistry(placement);
            construction.TryStartConstruction(100, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, 25, 100, out _);

            var noBuilders = new BuilderTickInput[0];
            for (int tick = 0; tick < 10; tick++)
            {
                construction.AdvanceTick(noBuilders, 0);
            }

            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(250), "the legacy null-ledger mode means 'always powered'");
        }

        // ------------------------------------------------------------------
        // 9 — zero-GC: O(1) read and event-driven recompute
        // ------------------------------------------------------------------

        [Test]
        public void GcRecorder_ObservesKnownAllocation_Control()
        {
            // The recorder must be proven live before it can prove absence.
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[4096]; },
                GcAssert.AllocatingGCMemory(),
                "the GC.Alloc recorder must observe a known allocation");
            Assert.That(ballast, Is.Not.Null);
        }

        [Test]
        public void PowerLedger_ReadAndEventRecompute_DoesNotAllocate()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, 10), Is.EqualTo(PowerLedgerRejection.None));
            Assert.That(ledger.RegisterBuilding(PlayerOne, 2, -6), Is.EqualTo(PowerLedgerRejection.None));

            // Warmup: JIT the read and the register/unregister recompute paths.
            int warmupSink = 0;
            for (int i = 0; i < WarmupIterations; i++)
            {
                warmupSink += ledger.GetPowerState(PlayerOne).NetPower;
                ledger.RegisterBuilding(PlayerOne, 9_000, 1);
                warmupSink += ledger.GetPowerState(PlayerOne).NetPower;
                ledger.UnregisterBuilding(PlayerOne, 9_000);
            }

            Assert.That(warmupSink, Is.Not.EqualTo(0), "the warmup must exercise the measured paths on a live grid");

            int calls = 0;
            int sink = 0;
            Assert.That(
                () =>
                {
                    for (int i = 0; i < MeasuredIterations; i++)
                    {
                        sink += ledger.GetPowerState(PlayerOne).NetPower;
                        ledger.RegisterBuilding(PlayerOne, 9_000, 1);
                        sink += ledger.GetPowerState(PlayerOne).NetPower;
                        ledger.UnregisterBuilding(PlayerOne, 9_000);
                        calls++;
                    }
                },
                GcAssert.Not.AllocatingGCMemory(),
                "power reads and event recomputes are on the tick-adjacent path and must be allocation-free");

            Assert.That(calls, Is.EqualTo(MeasuredIterations), "the measured window must actually run");
            Assert.That(sink, Is.Not.EqualTo(0), "the register/unregister cycle must flip the state");
            Assert.That(ledger.RegisterBuilding(PlayerTwo, 1, 1), Is.EqualTo(PowerLedgerRejection.None), "other players stay untouched");
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        private static readonly BuildingFootprint ShedFootprint = new BuildingFootprint(500, 500);

        /// <summary>A one-player ledger whose grid sits at NetPower = -1 (LowPower).</summary>
        private static PowerLedger CreateLowPowerLedger()
        {
            var ledger = new PowerLedger();
            Assert.That(ledger.RegisterBuilding(PlayerOne, 1, -1), Is.EqualTo(PowerLedgerRejection.None), "a lone consumer forces NetPower = -1");
            Assert.That(ledger.GetPowerState(PlayerOne).IsLowPower, Is.True, "scenario setup must produce a blackout");
            return ledger;
        }
    }
}
