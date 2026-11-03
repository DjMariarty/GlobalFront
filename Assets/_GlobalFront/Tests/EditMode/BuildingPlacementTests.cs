using System;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Structures;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Step 5.2.1 — free-form building placement foundations: the 500 mm snap
    /// lattice, integer AABB footprints, the map-bounds and overlap gates and
    /// the pre-allocated placement registry. Placement freedom is a design
    /// constant (Generals / Contra X): the grid is a deterministic network-desync
    /// guard, never a zone restriction. Integer-only math; the registry hot path
    /// must stay allocation-free (0 B GC.Alloc).
    /// </summary>
    [TestFixture]
    public sealed class BuildingPlacementTests
    {
        private static readonly BuildingFootprint HallFootprint = new BuildingFootprint(10_000, 10_000);

        private static readonly BuildingFootprint ShedFootprint = new BuildingFootprint(500, 500);

        private const int WarmupIterations = 200;

        private const int MeasuredIterations = 1000;

        private const int ZeroGcPopulation = 50;

        // ------------------------------------------------------------------
        // 1 — rejection codes are wire-relevant, pin the numeric values
        // ------------------------------------------------------------------

        [Test]
        public void PlacementRejection_CarriesTheDocumentedNumericValues()
        {
            Assert.That((byte)PlacementRejection.None, Is.EqualTo(0), "None must stay 0 — rejection fields are zero-initialised");
            Assert.That((byte)PlacementRejection.OutOfBounds, Is.EqualTo(1));
            Assert.That((byte)PlacementRejection.TerrainBlocked, Is.EqualTo(2), "reserved for the Step 5.2.2 terrain gates");
            Assert.That((byte)PlacementRejection.OverlapsExistingBuilding, Is.EqualTo(3));
            Assert.That((byte)PlacementRejection.RegistryFull, Is.EqualTo(4));
            Assert.That((byte)PlacementRejection.InvalidEntityId, Is.EqualTo(5));
        }

        // ------------------------------------------------------------------
        // 2 — the 500 mm snap lattice
        // ------------------------------------------------------------------

        [Test]
        public void SnapCoordinate_PositiveValues_RoundHalfUp()
        {
            Assert.That(SnapGrid.SnapCoordinate(0), Is.EqualTo(0), "0 already sits on the lattice");
            Assert.That(SnapGrid.SnapCoordinate(249), Is.EqualTo(0), "249 is nearer 0 than 500");
            Assert.That(SnapGrid.SnapCoordinate(250), Is.EqualTo(500), "the exact half rounds away from zero");
            Assert.That(SnapGrid.SnapCoordinate(499), Is.EqualTo(500));
            Assert.That(SnapGrid.SnapCoordinate(500), Is.EqualTo(500));
            Assert.That(SnapGrid.SnapCoordinate(750), Is.EqualTo(1000), "750 is the half between 500 and 1000");
        }

        [Test]
        public void SnapCoordinate_NegativeValues_RoundAwayFromZeroSymmetrically()
        {
            Assert.That(SnapGrid.SnapCoordinate(-249), Is.EqualTo(0), "-249 is nearer 0 than -500");
            Assert.That(SnapGrid.SnapCoordinate(-250), Is.EqualTo(-500), "the exact half rounds away from zero — mirror of +250");
            Assert.That(SnapGrid.SnapCoordinate(-499), Is.EqualTo(-500));
            Assert.That(SnapGrid.SnapCoordinate(-500), Is.EqualTo(-500));
        }

        [Test]
        public void SnapCoordinate_CustomStep_UsesProvidedLattice()
        {
            Assert.That(SnapGrid.SnapCoordinate(124, 250), Is.EqualTo(0));
            Assert.That(SnapGrid.SnapCoordinate(125, 250), Is.EqualTo(250));
            Assert.That(SnapGrid.SnapCoordinate(-125, 250), Is.EqualTo(-250), "symmetry holds for arbitrary steps");
            Assert.That(SnapGrid.SnapCoordinate(260), Is.EqualTo(500), "an omitted step defaults to the 500 mm lattice");
        }

        [Test]
        public void SnapCoordinate_NonPositiveStep_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapGrid.SnapCoordinate(0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapGrid.SnapCoordinate(0, -500));
        }

        [Test]
        public void SnapPosition_SnapsBothAxesIndependently()
        {
            Assert.That(
                SnapGrid.SnapPosition(new WorldPointMm(240, -260)),
                Is.EqualTo(new WorldPointMm(0, -500)),
                "X and Z snap independently, negative Z mirrors +250 onto -500");
            Assert.That(
                SnapGrid.SnapPosition(new WorldPointMm(123_456, 78_901), 1_000),
                Is.EqualTo(new WorldPointMm(123_000, 79_000)),
                "a custom 1 000 mm step snaps both axes onto the metre lattice");
        }

        // ------------------------------------------------------------------
        // 3 — footprint validation
        // ------------------------------------------------------------------

        [Test]
        public void BuildingFootprint_ValidDimensions_AreExposedAsIs()
        {
            BuildingFootprint shed = new BuildingFootprint(500, 500);
            Assert.That(shed.WidthMm, Is.EqualTo(500));
            Assert.That(shed.LengthMm, Is.EqualTo(500));

            BuildingFootprint hall = new BuildingFootprint(10_000, 10_000);
            Assert.That(hall.WidthMm, Is.EqualTo(10_000));
            Assert.That(hall.LengthMm, Is.EqualTo(10_000));
        }

        [Test]
        public void BuildingFootprint_NonPositiveDimensions_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BuildingFootprint(0, 500), "a zero-width footprint has no area");
            Assert.Throws<ArgumentOutOfRangeException>(() => new BuildingFootprint(-500, 500));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BuildingFootprint(500, 0), "length is validated the same way");
        }

        [Test]
        public void BuildingFootprint_DimensionsNotMultipleOfSnapStep_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BuildingFootprint(1200, 500), "1200 mm is off the 500 mm lattice");
            Assert.Throws<ArgumentOutOfRangeException>(() => new BuildingFootprint(500, 1499), "1499 mm is off the 500 mm lattice");
        }

        [Test]
        public void BuildingFootprint_Equality_IsValueBased()
        {
            BuildingFootprint a = new BuildingFootprint(10_000, 5_000);
            BuildingFootprint b = new BuildingFootprint(10_000, 5_000);
            BuildingFootprint c = new BuildingFootprint(5_000, 10_000);

            Assert.That(a == b, Is.True);
            Assert.That(a != c, Is.True, "width and length are not interchangeable");
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.Equals(c), Is.False);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()), "equal footprints must share a hash");
        }

        // ------------------------------------------------------------------
        // 4 — map bounds (OD-27: 400 x 400 m, half extent 200 000 mm)
        // ------------------------------------------------------------------

        [Test]
        public void ValidatePlacement_CenterOfMap_IsAccepted()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(0, 0), HallFootprint),
                Is.EqualTo(PlacementRejection.None));
        }

        [Test]
        public void ValidatePlacement_ExactlyAtMapEdge_IsAccepted()
        {
            var registry = new BuildingPlacementRegistry();
            // 195 000 + 10 000 / 2 = 200 000: the hall touches the border edge-on.
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(195_000, 0), HallFootprint),
                Is.EqualTo(PlacementRejection.None),
                "touching the border exactly is legal — only crossing rejects");
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(-195_000, -195_000), HallFootprint),
                Is.EqualTo(PlacementRejection.None),
                "negative corner: the far edges land exactly on -200 000");
        }

        [Test]
        public void ValidatePlacement_BeyondMapEdge_RejectsOutOfBounds()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(196_000, 0), HallFootprint),
                Is.EqualTo(PlacementRejection.OutOfBounds),
                "196 000 + 5 000 = 201 000 crosses the +200 000 border");
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(-196_000, 0), HallFootprint),
                Is.EqualTo(PlacementRejection.OutOfBounds),
                "the negative X border is bounded the same way");
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(0, 196_000), HallFootprint),
                Is.EqualTo(PlacementRejection.OutOfBounds),
                "Z is bounded the same way as X");
        }

        [Test]
        public void ValidatePlacement_CustomMapHalfExtent_IsRespected()
        {
            var registry = new BuildingPlacementRegistry(50_000);
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(45_000, 0), HallFootprint),
                Is.EqualTo(PlacementRejection.None),
                "45 000 + 5 000 fits the custom 50 000 extent");
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(46_000, 0), HallFootprint),
                Is.EqualTo(PlacementRejection.OutOfBounds),
                "51 000 crosses the custom extent");
        }

        // ------------------------------------------------------------------
        // 5 — AABB overlap gates (edge touch legal, area intersection rejects)
        // ------------------------------------------------------------------

        [Test]
        public void ValidatePlacement_SameCenterAsRegistered_RejectsOverlap()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(registry.TryRegister(1, new WorldPointMm(0, 0), HallFootprint, out _), Is.True);
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(0, 0), HallFootprint),
                Is.EqualTo(PlacementRejection.OverlapsExistingBuilding));
        }

        [Test]
        public void ValidatePlacement_EdgeTouchingRegistered_IsAccepted()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(registry.TryRegister(1, new WorldPointMm(0, 0), HallFootprint, out _), Is.True);

            // Adjacent halls share the edge line X = 5 000 (resp. Z = 5 000):
            // the strict overlap of the two closed boxes is empty.
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(10_000, 0), HallFootprint),
                Is.EqualTo(PlacementRejection.None),
                "edge touch along X is legal");
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(0, 10_000), HallFootprint),
                Is.EqualTo(PlacementRejection.None),
                "edge touch along Z is legal");
        }

        [Test]
        public void ValidatePlacement_PartialOverlapByOneSnapStep_RejectsOverlap()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(registry.TryRegister(1, new WorldPointMm(0, 0), HallFootprint, out _), Is.True);
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(9_500, 0), HallFootprint),
                Is.EqualTo(PlacementRejection.OverlapsExistingBuilding),
                "9 500 pushes the edge 500 mm inside — the smallest lattice-expressible overlap");
        }

        [Test]
        public void ValidatePlacement_IgnoreEntityId_SkipsItsOwnBuilding()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(registry.TryRegister(7, new WorldPointMm(0, 0), HallFootprint, out _), Is.True);
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(0, 0), HallFootprint, 7),
                Is.EqualTo(PlacementRejection.None),
                "re-validating a building against its own record must ignore it (move scenario)");
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(0, 0), HallFootprint, 8),
                Is.EqualTo(PlacementRejection.OverlapsExistingBuilding),
                "another entity's building still overlaps");
        }

        // ------------------------------------------------------------------
        // 6 — registry lifecycle
        // ------------------------------------------------------------------

        [Test]
        public void TryRegister_ValidPlacement_IncreasesCountAndStoresRecord()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(registry.Count, Is.EqualTo(0));

            Assert.That(
                registry.TryRegister(1, new WorldPointMm(0, 0), HallFootprint, out PlacementRejection rejection),
                Is.True,
                "an empty registry must accept a valid placement");
            Assert.That(rejection, Is.EqualTo(PlacementRejection.None), "success must carry None, never a stale code");
            Assert.That(registry.Count, Is.EqualTo(1));

            Assert.That(registry.TryGet(1, out PlacedBuilding stored), Is.True);
            Assert.That(stored.EntityId, Is.EqualTo(1ul));
            Assert.That(stored.OriginCenter, Is.EqualTo(new WorldPointMm(0, 0)));
            Assert.That(stored.Footprint, Is.EqualTo(HallFootprint));
            Assert.That(stored.MinX, Is.EqualTo(-5_000));
            Assert.That(stored.MaxX, Is.EqualTo(5_000));
            Assert.That(stored.MinZ, Is.EqualTo(-5_000));
            Assert.That(stored.MaxZ, Is.EqualTo(5_000));
        }

        [Test]
        public void TryRegister_OverlappingSpot_RejectsAndKeepsCount()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(registry.TryRegister(1, new WorldPointMm(0, 0), HallFootprint, out _), Is.True);

            Assert.That(
                registry.TryRegister(2, new WorldPointMm(0, 0), HallFootprint, out PlacementRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(PlacementRejection.OverlapsExistingBuilding));
            Assert.That(registry.Count, Is.EqualTo(1), "a rejected registration must not grow the registry");
        }

        [Test]
        public void TryRegister_DuplicateEntityId_RejectsWithInvalidEntityId()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(registry.TryRegister(1, new WorldPointMm(0, 0), HallFootprint, out _), Is.True);

            // The second spot is free — the rejection is about the id, not the geometry.
            Assert.That(
                registry.TryRegister(1, new WorldPointMm(100_000, 100_000), HallFootprint, out PlacementRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(PlacementRejection.InvalidEntityId));
        }

        [Test]
        public void TryRegister_ZeroEntityId_RejectsWithInvalidEntityId()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(
                registry.TryRegister(0, new WorldPointMm(0, 0), HallFootprint, out PlacementRejection rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo(PlacementRejection.InvalidEntityId), "0 is reserved as 'no entity' across the simulation");
            Assert.That(registry.Count, Is.EqualTo(0));
        }

        [Test]
        public void Unregister_RemovesBuildingAndFreesItsSpot()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(registry.TryRegister(1, new WorldPointMm(-10_000, 0), HallFootprint, out _), Is.True);
            Assert.That(registry.TryRegister(2, new WorldPointMm(0, 0), HallFootprint, out _), Is.True);
            Assert.That(registry.TryRegister(3, new WorldPointMm(10_000, 0), HallFootprint, out _), Is.True);

            Assert.That(registry.Unregister(2), Is.True, "a registered id must unregister");
            Assert.That(registry.Count, Is.EqualTo(2));
            Assert.That(registry.Unregister(2), Is.False, "a second unregister of the same id must report absence");
            Assert.That(registry.TryGet(2, out _), Is.False);

            // Swap-and-pop moved entity 3 into entity 2's slot: both survivors
            // must stay reachable with their records intact.
            Assert.That(registry.TryGet(1, out PlacedBuilding first), Is.True);
            Assert.That(first.OriginCenter, Is.EqualTo(new WorldPointMm(-10_000, 0)));
            Assert.That(registry.TryGet(3, out PlacedBuilding third), Is.True);
            Assert.That(third.OriginCenter, Is.EqualTo(new WorldPointMm(10_000, 0)));

            // Entity 2's spot is free again while entity 3's spot is still taken.
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(0, 0), HallFootprint),
                Is.EqualTo(PlacementRejection.None));
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(10_000, 0), HallFootprint),
                Is.EqualTo(PlacementRejection.OverlapsExistingBuilding));
        }

        [Test]
        public void TryGet_UnknownEntityId_ReturnsFalse()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(registry.TryGet(42, out PlacedBuilding building), Is.False);
            Assert.That(building.EntityId, Is.EqualTo(0ul), "a miss must yield the default record");
        }

        [Test]
        public void GetAt_IndexInsideRange_ReturnsOrderedRecords()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(registry.TryRegister(1, new WorldPointMm(-10_000, 0), HallFootprint, out _), Is.True);
            Assert.That(registry.TryRegister(2, new WorldPointMm(10_000, 0), HallFootprint, out _), Is.True);

            Assert.That(registry.GetAt(0).EntityId, Is.EqualTo(1ul), "GetAt exposes insertion order while no removal shuffled the array");
            Assert.That(registry.GetAt(1).EntityId, Is.EqualTo(2ul));
        }

        [Test]
        public void GetAt_IndexOutsideRegisteredRange_Throws()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(registry.TryRegister(1, new WorldPointMm(0, 0), HallFootprint, out _), Is.True);

            Assert.Throws<ArgumentOutOfRangeException>(() => registry.GetAt(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => registry.GetAt(1), "Count is the exclusive upper bound — only index 0 is registered");
        }

        [Test]
        public void TryRegister_PastCapacity_RejectsWithRegistryFull()
        {
            var registry = new BuildingPlacementRegistry();

            // 20 x 26 lattice of 500 x 500 sheds on a 2 000 mm pitch fills all
            // 512 slots inside the OD-27 map without a single overlap.
            for (int i = 0; i < BuildingPlacementRegistry.MaxPlacedBuildings; i++)
            {
                int x = -199_000 + (i % 20) * 2_000;
                int z = -199_000 + (i / 20) * 2_000;
                Assert.That(
                    registry.TryRegister((ulong)(i + 1), new WorldPointMm(x, z), ShedFootprint, out _),
                    Is.True,
                    $"slot {i} must register on the free lattice");
            }

            Assert.That(registry.Count, Is.EqualTo(BuildingPlacementRegistry.MaxPlacedBuildings));

            Assert.That(
                registry.TryRegister(10_000, new WorldPointMm(0, 0), ShedFootprint, out PlacementRejection rejection),
                Is.False,
                "(0, 0) is a free, in-bounds spot — only capacity can reject it");
            Assert.That(rejection, Is.EqualTo(PlacementRejection.RegistryFull), "the 513th building cannot be stored — the registry must say so explicitly");
        }

        [Test]
        public void Clear_EmptiesRegistryAndFreesAllSpots()
        {
            var registry = new BuildingPlacementRegistry();
            Assert.That(registry.TryRegister(1, new WorldPointMm(0, 0), HallFootprint, out _), Is.True);
            Assert.That(registry.TryRegister(2, new WorldPointMm(10_000, 0), HallFootprint, out _), Is.True);

            registry.Clear();

            Assert.That(registry.Count, Is.EqualTo(0));
            Assert.That(registry.TryGet(1, out _), Is.False);
            Assert.That(
                registry.ValidatePlacement(new WorldPointMm(0, 0), HallFootprint),
                Is.EqualTo(PlacementRejection.None),
                "every spot must be rebuildable after Clear");
        }

        // ------------------------------------------------------------------
        // 7 — zero-GC hot path
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
        public void ValidatePlacement_With50RegisteredBuildings_DoesNotAllocate()
        {
            BuildingPlacementRegistry registry = CreatePopulatedRegistry(ZeroGcPopulation);
            var center = new WorldPointMm(0, 0); // free lattice point between the probe sheds

            // Warmup moves JIT and lazy statics out of the measured window.
            int warmupSink = 0;
            for (int i = 0; i < WarmupIterations; i++)
            {
                warmupSink += (int)registry.ValidatePlacement(center, ShedFootprint);
            }

            Assert.That(warmupSink, Is.EqualTo(0), "the warmup must exercise the measured path on a free spot");

            int calls = 0;
            int sink = 0;
            Assert.That(
                () =>
                {
                    for (int i = 0; i < MeasuredIterations; i++)
                    {
                        sink += (int)registry.ValidatePlacement(center, ShedFootprint);
                        calls++;
                    }
                },
                GcAssert.Not.AllocatingGCMemory(),
                "placement validation runs per build order and must be allocation-free");

            Assert.That(calls, Is.EqualTo(MeasuredIterations), "the measured window must actually run");
            Assert.That(sink, Is.EqualTo(0), "the probe centre sits on a free lattice point");
        }

        [Test]
        public void Registry_RegisterGetUnregisterCycle_DoesNotAllocate()
        {
            BuildingPlacementRegistry registry = CreatePopulatedRegistry(ZeroGcPopulation);
            var center = new WorldPointMm(0, 0); // free lattice point between the probe sheds

            // Warmup: JIT the whole cycle once so first-run effects stay out of the window.
            for (int i = 0; i < WarmupIterations; i++)
            {
                ulong warmupId = 5_000 + (ulong)i;
                registry.TryRegister(warmupId, center, ShedFootprint, out _);
                registry.Unregister(warmupId);
            }

            Assert.That(registry.Count, Is.EqualTo(ZeroGcPopulation), "the warmup must leave the population intact");

            int calls = 0;
            int sink = 0;
            Assert.That(
                () =>
                {
                    for (int i = 0; i < MeasuredIterations; i++)
                    {
                        ulong probeId = 1_000 + (ulong)i;
                        registry.TryRegister(probeId, center, ShedFootprint, out _);
                        PlacedBuilding stored = registry.GetAt(registry.Count - 1);
                        sink += stored.MinX;
                        registry.Unregister(probeId);
                        calls++;
                    }
                },
                GcAssert.Not.AllocatingGCMemory(),
                "the register/get/unregister cycle runs per build order and must be allocation-free");

            Assert.That(calls, Is.EqualTo(MeasuredIterations), "the measured window must actually run");
            Assert.That(sink, Is.EqualTo(-250 * MeasuredIterations), "the probe shed at (0, 0) spans X in [-250, 250]");
            Assert.That(registry.Count, Is.EqualTo(ZeroGcPopulation), "every probe registration must be unregistered again");
        }

        private static BuildingPlacementRegistry CreatePopulatedRegistry(int buildingCount)
        {
            var registry = new BuildingPlacementRegistry();
            for (int i = 0; i < buildingCount; i++)
            {
                int x = -199_000 + (i % 10) * 2_000;
                int z = -199_000 + (i / 10) * 2_000;
                if (!registry.TryRegister((ulong)(i + 1), new WorldPointMm(x, z), ShedFootprint, out _))
                {
                    Assert.Fail($"probe population failed at index {i}");
                }
            }

            return registry;
        }
    }
}
