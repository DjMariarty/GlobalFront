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
    /// Step 5.2.2 — building frames, the Dozer/Worker FSM and GLA multi-build.
    /// A frame owns its final archetype from tick 0 but stays inert until 1000‰;
    /// builder death or recall freezes progress (never resets it); GLA stacks up
    /// to 3 workers with the documented 1000/1600/2000‰ ladder; cancellation
    /// refunds 100% of the paid cost and un-places the frame. Integer-only math;
    /// the per-tick path must stay allocation-free (0 B GC.Alloc).
    /// </summary>
    [TestFixture]
    public sealed class ConstructionTests
    {
        private static readonly PlayerId PlayerOne = new PlayerId(1);

        private static readonly BuildingFootprint ShedFootprint = new BuildingFootprint(500, 500);

        private static readonly BuildingFootprint HallFootprint = new BuildingFootprint(10_000, 10_000);

        private const int WarmupIterations = 200;

        private const int MeasuredIterations = 1000;

        // ------------------------------------------------------------------
        // 1 — enums are protocol-significant, pin the numeric values
        // ------------------------------------------------------------------

        [Test]
        public void ConstructionEnums_CarryTheDocumentedNumericValues()
        {
            Assert.That((byte)ConstructionMethod.DozerWorker, Is.EqualTo(0), "USA/China/GLA builder method must stay 0");
            Assert.That((byte)ConstructionMethod.Modular, Is.EqualTo(1), "Russia/NATO self-assembly must stay 1");
            Assert.That((byte)BuilderKind.Dozer, Is.EqualTo(0));
            Assert.That((byte)BuilderKind.Worker, Is.EqualTo(1));
            Assert.That((byte)BuilderActivity.Idle, Is.EqualTo(0), "Idle must stay 0 — absence from the FSM table reads as Idle");
            Assert.That((byte)BuilderActivity.MovingToSite, Is.EqualTo(1));
            Assert.That((byte)BuilderActivity.Building, Is.EqualTo(2));
            Assert.That((byte)ConstructionStartRejection.None, Is.EqualTo(0));
            Assert.That((byte)ConstructionStartRejection.InvalidEntityId, Is.EqualTo(1));
            Assert.That((byte)ConstructionStartRejection.DuplicateSiteEntity, Is.EqualTo(2));
            Assert.That((byte)ConstructionStartRejection.InvalidWorkRate, Is.EqualTo(3), "reserved: bad work rates throw instead");
            Assert.That((byte)ConstructionStartRejection.MapBounds, Is.EqualTo(4));
            Assert.That((byte)ConstructionStartRejection.OverlapsExistingBuilding, Is.EqualTo(5));
            Assert.That((byte)ConstructionStartRejection.PlacementRegistryFull, Is.EqualTo(6));
            Assert.That((byte)ConstructionStartRejection.ConstructionRegistryFull, Is.EqualTo(7));
            Assert.That((byte)BuilderAssignmentRejection.None, Is.EqualTo(0));
            Assert.That((byte)BuilderAssignmentRejection.BuilderUnknown, Is.EqualTo(1));
            Assert.That((byte)BuilderAssignmentRejection.SiteUnknown, Is.EqualTo(2));
            Assert.That((byte)BuilderAssignmentRejection.AlreadyAssigned, Is.EqualTo(3));
            Assert.That((byte)BuilderAssignmentRejection.SiteBuilderLimitReached, Is.EqualTo(4));
            Assert.That((byte)BuilderAssignmentRejection.BuilderKindMismatch, Is.EqualTo(5));
            Assert.That((byte)BuilderAssignmentRejection.BuilderTableFull, Is.EqualTo(6));
        }

        // ------------------------------------------------------------------
        // 2 — GLA presence ladder (diminishing returns)
        // ------------------------------------------------------------------

        [Test]
        public void WorkMultiplierPermille_MapsPresenceToTheDocumentedLadder()
        {
            Assert.That(ConstructionRegistry.WorkMultiplierPermille(0), Is.EqualTo(0), "zero presence is the DozerWorker freeze");
            Assert.That(ConstructionRegistry.WorkMultiplierPermille(1), Is.EqualTo(1000), "one builder works at the base rate");
            Assert.That(ConstructionRegistry.WorkMultiplierPermille(2), Is.EqualTo(1600), "two workers: 1.6x diminishing return");
            Assert.That(ConstructionRegistry.WorkMultiplierPermille(3), Is.EqualTo(2000), "three workers: 2.0x cap");
            Assert.That(ConstructionRegistry.WorkMultiplierPermille(4), Is.EqualTo(2000), "a 4th worker cannot exceed the cap");
            Assert.That(ConstructionRegistry.WorkMultiplierPermille(-1), Is.EqualTo(0), "defensive: negative presence freezes");
        }

        // ------------------------------------------------------------------
        // 3 — full cycle and completion gates
        // ------------------------------------------------------------------

        [Test]
        public void AdvanceTick_ModularSite_CompletesInExactlyFortyTicks()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = CreateRegistry(placement);
            Assert.That(
                construction.TryStartConstruction(100, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, 25, 1000, out ConstructionStartRejection rejection),
                Is.True, $"a valid modular start must succeed, got {rejection}");

            var noBuilders = new BuilderTickInput[0];
            for (int tick = 0; tick < 39; tick++)
            {
                construction.AdvanceTick(noBuilders, 0);
            }

            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(975), "39 ticks at the default 25‰ per tick");
            Assert.That(site.IsCompleted, Is.False, "975‰ is not 1000‰");
            Assert.That(site.IsFrozen, Is.False, "Modular sites never freeze on presence");

            ConstructionTickResult result = construction.AdvanceTick(noBuilders, 0);
            Assert.That(result.CompletedCount, Is.EqualTo(1), "the 40th tick crosses 1000‰ and completes the site");
            Assert.That(construction.GetCompletedBuildingEntityId(0), Is.EqualTo(100ul));
            Assert.Throws<ArgumentOutOfRangeException>(() => construction.GetCompletedBuildingEntityId(1), "only one completion was reported");
            Assert.That(construction.TryGet(100, out _), Is.False, "a completed site leaves the construction registry");
            Assert.That(construction.Count, Is.EqualTo(0));
            Assert.That(placement.TryGet(100, out _), Is.True, "the finished building stays registered in the placement registry");
        }

        [Test]
        public void AdvanceTick_ModularSite_DoesNotCompleteBeforeHundredPercent()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = CreateRegistry(placement);
            Assert.That(
                construction.TryStartConstruction(100, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, 999, 1000, out _),
                Is.True);

            var noBuilders = new BuilderTickInput[0];
            ConstructionTickResult first = construction.AdvanceTick(noBuilders, 0);
            Assert.That(first.CompletedCount, Is.EqualTo(0), "999‰ must not complete");
            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(999));

            ConstructionTickResult second = construction.AdvanceTick(noBuilders, 0);
            Assert.That(second.CompletedCount, Is.EqualTo(1), "the next tick crosses the line exactly once");
        }

        // ------------------------------------------------------------------
        // 4 — modular self-assembly vs DozerWorker freeze
        // ------------------------------------------------------------------

        [Test]
        public void ModularSite_BuildsWithoutBuilders()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.Modular);

            var noBuilders = new BuilderTickInput[0];
            for (int tick = 0; tick < 10; tick++)
            {
                construction.AdvanceTick(noBuilders, 0);
            }

            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(250), "Russia/NATO frames self-assemble at the base rate");
            Assert.That(site.PresentBuilders, Is.EqualTo(0));
        }

        [Test]
        public void DozerWorkerSite_IsFrozenWithoutBuilder()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.DozerWorker);

            var noBuilders = new BuilderTickInput[0];
            for (int tick = 0; tick < 10; tick++)
            {
                construction.AdvanceTick(noBuilders, 0);
            }

            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(0), "no living builder in radius — no progress, Generals rule");
            Assert.That(site.IsFrozen, Is.True);
        }

        // ------------------------------------------------------------------
        // 5 — work radius gate (from the snapped centre, inclusive)
        // ------------------------------------------------------------------

        [Test]
        public void DozerWorkerSite_BuilderOutsideRadius_KeepsFrozen()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.DozerWorker);
            Assert.That(construction.AssignBuilder(1, BuilderKind.Dozer, 100), Is.EqualTo(BuilderAssignmentRejection.None));

            // 15 001 mm from the centre: 1 mm past the default 15 000 mm radius.
            var builders = new[] { new BuilderTickInput(1, new WorldPointMm(15_001, 0), true) };
            for (int tick = 0; tick < 10; tick++)
            {
                construction.AdvanceTick(builders, builders.Length);
            }

            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(0), "an approaching builder does not yet contribute");
            Assert.That(site.IsFrozen, Is.True);
            Assert.That(construction.TryGetBuilderActivity(1, out BuilderActivity activity), Is.True);
            Assert.That(activity, Is.EqualTo(BuilderActivity.MovingToSite));
        }

        [Test]
        public void DozerWorkerSite_BuilderAtRadiusBoundary_Works()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.DozerWorker);
            Assert.That(construction.AssignBuilder(1, BuilderKind.Dozer, 100), Is.EqualTo(BuilderAssignmentRejection.None));

            // Exactly 15 000 mm: the radius gate is inclusive.
            var builders = new[] { new BuilderTickInput(1, new WorldPointMm(15_000, 0), true) };
            construction.AdvanceTick(builders, builders.Length);

            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(25));
            Assert.That(site.PresentBuilders, Is.EqualTo(1));
            Assert.That(site.IsFrozen, Is.False);
            Assert.That(construction.TryGetBuilderActivity(1, out BuilderActivity activity), Is.True);
            Assert.That(activity, Is.EqualTo(BuilderActivity.Building));
        }

        // ------------------------------------------------------------------
        // 6 — builder death and recall freeze, never reset
        // ------------------------------------------------------------------

        [Test]
        public void BuilderDeath_FreezesProgressWithoutReset()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.DozerWorker);
            Assert.That(construction.AssignBuilder(1, BuilderKind.Dozer, 100), Is.EqualTo(BuilderAssignmentRejection.None));

            var builders = new[] { new BuilderTickInput(1, new WorldPointMm(0, 0), true) };
            for (int tick = 0; tick < 4; tick++)
            {
                construction.AdvanceTick(builders, builders.Length);
            }

            Assert.That(construction.TryGet(100, out ConstructionSiteState alive), Is.True);
            Assert.That(alive.BuildProgressMilli, Is.EqualTo(100), "4 ticks at 25‰");

            var deadBuilder = new[] { new BuilderTickInput(1, new WorldPointMm(0, 0), false) };
            for (int tick = 0; tick < 10; tick++)
            {
                construction.AdvanceTick(deadBuilder, deadBuilder.Length);
            }

            Assert.That(construction.TryGet(100, out ConstructionSiteState frozen), Is.True);
            Assert.That(frozen.BuildProgressMilli, Is.EqualTo(100), "death freezes — the Generals progress never resets to 0");
            Assert.That(frozen.PresentBuilders, Is.EqualTo(0));
            Assert.That(frozen.IsFrozen, Is.True);
            Assert.That(construction.TryGetBuilderActivity(1, out _), Is.False, "a dead builder leaves the FSM table (Idle)");

            // A fresh dozer continues from the frozen point.
            Assert.That(construction.AssignBuilder(2, BuilderKind.Dozer, 100), Is.EqualTo(BuilderAssignmentRejection.None));
            var replacement = new[] { new BuilderTickInput(2, new WorldPointMm(0, 0), true) };
            construction.AdvanceTick(replacement, replacement.Length);
            Assert.That(construction.TryGet(100, out ConstructionSiteState resumed), Is.True);
            Assert.That(resumed.BuildProgressMilli, Is.EqualTo(125), "work resumes from the frozen 100‰, never from scratch");
        }

        [Test]
        public void UnassignBuilder_FreezesSiteUntilReassigned()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.DozerWorker);
            Assert.That(construction.AssignBuilder(1, BuilderKind.Dozer, 100), Is.EqualTo(BuilderAssignmentRejection.None));

            var builders = new[] { new BuilderTickInput(1, new WorldPointMm(0, 0), true) };
            construction.AdvanceTick(builders, builders.Length);
            construction.AdvanceTick(builders, builders.Length);
            Assert.That(construction.TryGet(100, out ConstructionSiteState working), Is.True);
            Assert.That(working.BuildProgressMilli, Is.EqualTo(50));

            Assert.That(construction.UnassignBuilder(1), Is.True, "a recall must release the builder");
            Assert.That(construction.UnassignBuilder(1), Is.False, "a second release reports absence");

            var noBuilders = new BuilderTickInput[0];
            for (int tick = 0; tick < 5; tick++)
            {
                construction.AdvanceTick(noBuilders, 0);
            }

            Assert.That(construction.TryGet(100, out ConstructionSiteState frozen), Is.True);
            Assert.That(frozen.BuildProgressMilli, Is.EqualTo(50), "recall freezes the site at its current progress");

            Assert.That(construction.AssignBuilder(1, BuilderKind.Dozer, 100), Is.EqualTo(BuilderAssignmentRejection.None));
            construction.AdvanceTick(builders, builders.Length);
            Assert.That(construction.TryGet(100, out ConstructionSiteState resumed), Is.True);
            Assert.That(resumed.BuildProgressMilli, Is.EqualTo(75));
        }

        // ------------------------------------------------------------------
        // 7 — GLA multi-build ladder (exact 1000/1600/2000‰)
        // ------------------------------------------------------------------

        [Test]
        public void OneWorker_BuildsAtTheBaseRate()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.DozerWorker);
            Assert.That(construction.AssignBuilder(1, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));

            var builders = new[] { new BuilderTickInput(1, new WorldPointMm(0, 0), true) };
            construction.AdvanceTick(builders, builders.Length);

            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(25), "1 worker = 1000‰ of the 25‰ base rate");
            Assert.That(site.PresentBuilders, Is.EqualTo(1));
        }

        [Test]
        public void TwoWorkers_BuildAt1600Permille()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.DozerWorker);
            Assert.That(construction.AssignBuilder(1, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));
            Assert.That(construction.AssignBuilder(2, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));

            var builders = new[]
            {
                new BuilderTickInput(1, new WorldPointMm(0, 0), true),
                new BuilderTickInput(2, new WorldPointMm(1_000, 0), true),
            };
            construction.AdvanceTick(builders, builders.Length);

            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(40), "(25 × 1600 + 500) / 1000 = 40‰ per tick");
            Assert.That(site.PresentBuilders, Is.EqualTo(2));
        }

        [Test]
        public void ThreeWorkers_BuildAt2000Permille()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.DozerWorker);
            Assert.That(construction.AssignBuilder(1, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));
            Assert.That(construction.AssignBuilder(2, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));
            Assert.That(construction.AssignBuilder(3, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));

            var builders = new[]
            {
                new BuilderTickInput(1, new WorldPointMm(0, 0), true),
                new BuilderTickInput(2, new WorldPointMm(1_000, 0), true),
                new BuilderTickInput(3, new WorldPointMm(2_000, 0), true),
            };
            construction.AdvanceTick(builders, builders.Length);

            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.BuildProgressMilli, Is.EqualTo(50), "(25 × 2000 + 500) / 1000 = 50‰ per tick");
            Assert.That(site.PresentBuilders, Is.EqualTo(3));
        }

        [Test]
        public void FourthWorker_IsRejectedWithSiteBuilderLimitReached()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.DozerWorker);
            Assert.That(construction.AssignBuilder(1, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));
            Assert.That(construction.AssignBuilder(2, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));
            Assert.That(construction.AssignBuilder(3, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));

            Assert.That(
                construction.AssignBuilder(4, BuilderKind.Worker, 100),
                Is.EqualTo(BuilderAssignmentRejection.SiteBuilderLimitReached),
                "a 4th GLA worker is rejected, not queued — the cap is 3");
            Assert.That(construction.AssignedBuilderCount(100), Is.EqualTo(3));
        }

        [Test]
        public void WorkerDeath_AmongThree_RecalculatesTheMultiplierDeterministically()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.DozerWorker);
            Assert.That(construction.AssignBuilder(1, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));
            Assert.That(construction.AssignBuilder(2, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));
            Assert.That(construction.AssignBuilder(3, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));

            var builders = new[]
            {
                new BuilderTickInput(1, new WorldPointMm(0, 0), true),
                new BuilderTickInput(2, new WorldPointMm(1_000, 0), true),
                new BuilderTickInput(3, new WorldPointMm(2_000, 0), true),
            };
            construction.AdvanceTick(builders, builders.Length);
            Assert.That(construction.TryGet(100, out ConstructionSiteState full), Is.True);
            Assert.That(full.BuildProgressMilli, Is.EqualTo(50), "3 workers: 2000‰");

            // Worker 2 dies; workers 1 and 3 keep working — the ladder re-evaluates to 1600‰.
            builders[1] = new BuilderTickInput(2, new WorldPointMm(1_000, 0), false);
            construction.AdvanceTick(builders, builders.Length);

            Assert.That(construction.TryGet(100, out ConstructionSiteState reduced), Is.True);
            Assert.That(reduced.BuildProgressMilli, Is.EqualTo(90), "50 + (25 × 1600 + 500) / 1000 — no reset on death");
            Assert.That(reduced.PresentBuilders, Is.EqualTo(2));
        }

        // ------------------------------------------------------------------
        // 8 — dozer cap and kind mismatch
        // ------------------------------------------------------------------

        [Test]
        public void SecondDozer_IsRejectedWithSiteBuilderLimitReached()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.DozerWorker);
            Assert.That(construction.AssignBuilder(1, BuilderKind.Dozer, 100), Is.EqualTo(BuilderAssignmentRejection.None));

            Assert.That(
                construction.AssignBuilder(2, BuilderKind.Dozer, 100),
                Is.EqualTo(BuilderAssignmentRejection.SiteBuilderLimitReached),
                "USA/China: exactly one dozer per building, Generals rule");
            Assert.That(construction.AssignedBuilderCount(100), Is.EqualTo(1));
        }

        [Test]
        public void MixedBuilderKinds_AreRejectedWithBuilderKindMismatch()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.DozerWorker);
            Assert.That(construction.AssignBuilder(1, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));
            Assert.That(
                construction.AssignBuilder(2, BuilderKind.Dozer, 100),
                Is.EqualTo(BuilderAssignmentRejection.BuilderKindMismatch),
                "a dozer cannot join a worker crew");

            BuildingPlacementRegistry otherPlacement = new BuildingPlacementRegistry();
            ConstructionRegistry otherConstruction = StartSite(otherPlacement, 100, ConstructionMethod.DozerWorker);
            Assert.That(otherConstruction.AssignBuilder(1, BuilderKind.Dozer, 100), Is.EqualTo(BuilderAssignmentRejection.None));
            Assert.That(
                otherConstruction.AssignBuilder(2, BuilderKind.Worker, 100),
                Is.EqualTo(BuilderAssignmentRejection.BuilderKindMismatch),
                "a worker cannot join a dozer site");
        }

        // ------------------------------------------------------------------
        // 9 — cancellation: 100% refund, un-place, builders to Idle
        // ------------------------------------------------------------------

        [Test]
        public void Cancel_RefundsFullCost_FreesSpot_AndIdlesBuilders()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.DozerWorker, paidCost: 777);
            Assert.That(construction.AssignBuilder(1, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));
            Assert.That(construction.AssignBuilder(2, BuilderKind.Worker, 100), Is.EqualTo(BuilderAssignmentRejection.None));

            var builders = new[]
            {
                new BuilderTickInput(1, new WorldPointMm(0, 0), true),
                new BuilderTickInput(2, new WorldPointMm(1_000, 0), true),
            };
            construction.AdvanceTick(builders, builders.Length);
            Assert.That(construction.TryGet(100, out ConstructionSiteState partial), Is.True);
            Assert.That(partial.BuildProgressMilli, Is.GreaterThan(0), "the scenario needs partial progress before cancel");

            Assert.That(construction.TryCancel(100, out int refundCost), Is.True);
            Assert.That(refundCost, Is.EqualTo(777), "cancellation refunds 100% of the paid cost, even mid-build");
            Assert.That(construction.TryGet(100, out _), Is.False, "the cancelled site leaves the registry");
            Assert.That(
                placement.ValidatePlacement(new WorldPointMm(0, 0), ShedFootprint),
                Is.EqualTo(PlacementRejection.None),
                "the frame is un-placed — the spot is rebuildable");
            Assert.That(construction.TryGetBuilderActivity(1, out _), Is.False, "builder 1 is Idle after cancel");
            Assert.That(construction.TryGetBuilderActivity(2, out _), Is.False, "builder 2 is Idle after cancel");
        }

        [Test]
        public void Cancel_BeforeAnyProgress_AlsoRefundsFully()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.Modular, paidCost: 500);

            Assert.That(construction.TryCancel(100, out int refundCost), Is.True);
            Assert.That(refundCost, Is.EqualTo(500), "refund is the paid cost, not the progress-adjusted remainder");
            Assert.That(construction.Count, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------
        // 10 — start gates
        // ------------------------------------------------------------------

        [Test]
        public void TryStartConstruction_DuplicateEntityId_Rejects()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.Modular);

            Assert.That(
                construction.TryStartConstruction(100, PlayerOne, new WorldPointMm(50_000, 0), ShedFootprint, ConstructionMethod.Modular, 25, 100, out ConstructionStartRejection rejection),
                Is.False,
                "the same entity cannot open two construction sites");
            Assert.That(rejection, Is.EqualTo(ConstructionStartRejection.DuplicateSiteEntity));
        }

        [Test]
        public void TryStartConstruction_ZeroEntityId_Rejects()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = CreateRegistry(placement);

            Assert.That(
                construction.TryStartConstruction(0, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, 25, 100, out ConstructionStartRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ConstructionStartRejection.InvalidEntityId), "0 is reserved as 'no entity' across the simulation");
        }

        [Test]
        public void TryStartConstruction_OutOfRangeWorkRate_Throws()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = CreateRegistry(placement);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => construction.TryStartConstruction(100, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, 0, 100, out _),
                "0‰ per tick would never finish");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => construction.TryStartConstruction(100, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, 1001, 100, out _),
                "a blueprint cannot exceed 1000‰ per tick");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => construction.TryStartConstruction(100, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, 25, -1, out _),
                "paid cost cannot be negative");
        }

        [Test]
        public void TryStartConstruction_OutOfBoundsCenter_RejectsWithMapBounds()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = CreateRegistry(placement);

            Assert.That(
                construction.TryStartConstruction(100, PlayerOne, new WorldPointMm(196_000, 0), HallFootprint, ConstructionMethod.Modular, 25, 100, out ConstructionStartRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ConstructionStartRejection.MapBounds), "a 10 000 mm hall at 196 000 crosses the +200 000 border");
        }

        [Test]
        public void TryStartConstruction_OverlappingExistingBuilding_Rejects()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            Assert.That(placement.TryRegister(900, new WorldPointMm(0, 0), HallFootprint, out _), Is.True);
            ConstructionRegistry construction = CreateRegistry(placement);

            Assert.That(
                construction.TryStartConstruction(100, PlayerOne, new WorldPointMm(0, 0), HallFootprint, ConstructionMethod.Modular, 25, 100, out ConstructionStartRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ConstructionStartRejection.OverlapsExistingBuilding));
            Assert.That(construction.Count, Is.EqualTo(0), "a rejected start must not open a site");
        }

        [Test]
        public void TryStartConstruction_PastCapacity_RejectsWithConstructionRegistryFull()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            var construction = new ConstructionRegistry(placement, maxActiveConstructions: 2);
            Assert.That(
                construction.TryStartConstruction(1, PlayerOne, new WorldPointMm(-10_000, 0), ShedFootprint, ConstructionMethod.Modular, 25, 100, out _), Is.True);
            Assert.That(
                construction.TryStartConstruction(2, PlayerOne, new WorldPointMm(10_000, 0), ShedFootprint, ConstructionMethod.Modular, 25, 100, out _), Is.True);

            Assert.That(
                construction.TryStartConstruction(3, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, 25, 100, out ConstructionStartRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(ConstructionStartRejection.ConstructionRegistryFull));
        }

        [Test]
        public void TryStartConstruction_SnapsTheCentre()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = CreateRegistry(placement);

            Assert.That(
                construction.TryStartConstruction(100, PlayerOne, new WorldPointMm(123, 456), ShedFootprint, ConstructionMethod.Modular, 25, 100, out _),
                Is.True);
            Assert.That(construction.TryGet(100, out ConstructionSiteState site), Is.True);
            Assert.That(site.Center, Is.EqualTo(new WorldPointMm(0, 500)), "the frame lands on the 500 mm lattice, never on raw input");
            Assert.That(placement.TryGet(100, out var placed), Is.True);
            Assert.That(placed.OriginCenter, Is.EqualTo(new WorldPointMm(0, 500)), "the placement record and the site share the snapped centre");
        }

        // ------------------------------------------------------------------
        // 11 — registry lifecycle (swap-and-pop) and FSM views
        // ------------------------------------------------------------------

        [Test]
        public void Cancel_MiddleSite_KeepsSurvivorsReachable()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = CreateRegistry(placement);
            Assert.That(construction.TryStartConstruction(1, PlayerOne, new WorldPointMm(-10_000, 0), HallFootprint, ConstructionMethod.Modular, 25, 100, out _), Is.True);
            Assert.That(construction.TryStartConstruction(2, PlayerOne, new WorldPointMm(0, 0), HallFootprint, ConstructionMethod.Modular, 25, 100, out _), Is.True);
            Assert.That(construction.TryStartConstruction(3, PlayerOne, new WorldPointMm(10_000, 0), HallFootprint, ConstructionMethod.Modular, 25, 100, out _), Is.True);

            Assert.That(construction.TryCancel(2, out int refundCost), Is.True);
            Assert.That(refundCost, Is.EqualTo(100));

            Assert.That(construction.Count, Is.EqualTo(2));
            Assert.That(construction.TryGet(1, out var first), Is.True);
            Assert.That(first.Center, Is.EqualTo(new WorldPointMm(-10_000, 0)));
            Assert.That(construction.TryGet(3, out var third), Is.True);
            Assert.That(third.Center, Is.EqualTo(new WorldPointMm(10_000, 0)));
            Assert.That(construction.TryGet(2, out _), Is.False);
            Assert.That(
                placement.ValidatePlacement(new WorldPointMm(0, 0), HallFootprint),
                Is.EqualTo(PlacementRejection.None),
                "the cancelled middle spot is free while the neighbours still block theirs");
        }

        [Test]
        public void BuilderActivity_FlipsBetweenMovingToSiteAndBuilding()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = StartSite(placement, 100, ConstructionMethod.DozerWorker);
            Assert.That(construction.AssignBuilder(1, BuilderKind.Dozer, 100), Is.EqualTo(BuilderAssignmentRejection.None));
            Assert.That(construction.TryGetBuilderActivity(1, out BuilderActivity initial), Is.True);
            Assert.That(initial, Is.EqualTo(BuilderActivity.MovingToSite), "a fresh assignment starts approaching");

            var builders = new[] { new BuilderTickInput(1, new WorldPointMm(0, 0), true) };
            construction.AdvanceTick(builders, builders.Length);
            Assert.That(construction.TryGetBuilderActivity(1, out BuilderActivity arrived), Is.True);
            Assert.That(arrived, Is.EqualTo(BuilderActivity.Building));

            var walkedAway = new[] { new BuilderTickInput(1, new WorldPointMm(100_000, 0), true) };
            construction.AdvanceTick(walkedAway, walkedAway.Length);
            Assert.That(construction.TryGetBuilderActivity(1, out BuilderActivity away), Is.True);
            Assert.That(away, Is.EqualTo(BuilderActivity.MovingToSite), "leaving the radius demotes the builder back to approach");
        }

        [Test]
        public void GetAt_IndexOutsideRegisteredRange_Throws()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            ConstructionRegistry construction = CreateRegistry(placement);
            Assert.That(construction.TryStartConstruction(1, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, ConstructionMethod.Modular, 25, 100, out _), Is.True);

            Assert.Throws<ArgumentOutOfRangeException>(() => construction.GetAt(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => construction.GetAt(1), "Count is the exclusive upper bound");
        }

        [Test]
        public void ConstructionSiteState_BoundaryFlags_BehaveAsDocumented()
        {
            var almost = new ConstructionSiteState(1, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, 999, 25, 100, ConstructionMethod.DozerWorker, 0);
            Assert.That(almost.IsCompleted, Is.False);
            Assert.That(almost.IsFrozen, Is.True, "dozer frame with no present builders is frozen");

            var done = new ConstructionSiteState(1, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, 1000, 25, 100, ConstructionMethod.DozerWorker, 1);
            Assert.That(done.IsCompleted, Is.True);
            Assert.That(done.IsFrozen, Is.False, "a completed frame is a building, not a frozen site");
        }

        // ------------------------------------------------------------------
        // 12 — zero-GC hot path
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
        public void AdvanceTick_With50SitesAnd20Builders_DoesNotAllocate()
        {
            BuildingPlacementRegistry placement = new BuildingPlacementRegistry();
            var construction = new ConstructionRegistry(placement);

            // 10 dozer-worker sheds + 40 modular sheds at 1‰ per tick — each
            // completes exactly 1000 ticks after its start.
            for (int i = 0; i < 50; i++)
            {
                ConstructionMethod method = i < 10 ? ConstructionMethod.DozerWorker : ConstructionMethod.Modular;
                Assert.That(
                    construction.TryStartConstruction((ulong)(1000 + i), PlayerOne, SiteCenter(i), ShedFootprint, method, 1, 100, out ConstructionStartRejection rejection),
                    Is.True, $"site {i} must start: {rejection}");
            }

            var builders = new BuilderTickInput[20];
            for (int b = 0; b < 20; b++)
            {
                builders[b] = b < 10
                    ? new BuilderTickInput((ulong)(b + 1), SiteCenter(b), true)
                    : new BuilderTickInput((ulong)(b + 1), new WorldPointMm(0, 199_000), true);
            }

            for (int b = 0; b < 10; b++)
            {
                Assert.That(
                    construction.AssignBuilder((ulong)(b + 1), BuilderKind.Dozer, (ulong)(1000 + b)),
                    Is.EqualTo(BuilderAssignmentRejection.None));
            }

            // Warmup: JIT the whole pipeline; nothing completes at 1‰ per tick yet.
            int warmupSink = 0;
            for (int i = 0; i < WarmupIterations; i++)
            {
                warmupSink += construction.AdvanceTick(builders, builders.Length).CompletedCount;
            }

            Assert.That(warmupSink, Is.EqualTo(0), "the warmup must not complete any site");
            Assert.That(construction.Count, Is.EqualTo(50));

            int calls = 0;
            int sink = 0;
            Assert.That(
                () =>
                {
                    for (int i = 0; i < MeasuredIterations; i++)
                    {
                        sink += construction.AdvanceTick(builders, builders.Length).CompletedCount;
                        calls++;
                    }
                },
                GcAssert.Not.AllocatingGCMemory(),
                "the construction tick is a per-tick hot path and must be allocation-free");

            Assert.That(calls, Is.EqualTo(MeasuredIterations), "the measured window must actually run");
            Assert.That(sink, Is.EqualTo(50), "every site completes exactly once inside the window");
            Assert.That(construction.Count, Is.EqualTo(0), "no site may linger after completion");
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        private static ConstructionRegistry CreateRegistry(BuildingPlacementRegistry placement)
        {
            return new ConstructionRegistry(placement);
        }

        private static ConstructionRegistry StartSite(
            BuildingPlacementRegistry placement,
            ulong entityId,
            ConstructionMethod method,
            int paidCost = 1000)
        {
            var construction = new ConstructionRegistry(placement);
            Assert.That(
                construction.TryStartConstruction(entityId, PlayerOne, new WorldPointMm(0, 0), ShedFootprint, method, 25, paidCost, out ConstructionStartRejection rejection),
                Is.True,
                $"site setup failed: {rejection}");
            return construction;
        }

        private static WorldPointMm SiteCenter(int index)
        {
            return new WorldPointMm(-199_000 + index * 2_000, -199_000);
        }
    }
}
