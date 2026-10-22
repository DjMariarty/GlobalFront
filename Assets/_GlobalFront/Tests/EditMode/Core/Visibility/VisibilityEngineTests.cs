using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Core.Visibility;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace GlobalFront.Tests.EditMode.Core.Visibility
{
    /// <summary>
    /// Coverage of the server visibility engine (ADR-014, Step 4.3.1):
    /// own/allied unconditional visibility, the 20 m enemy radius including
    /// its exact boundary, team mapping, grid results against a brute-force
    /// reference, the 1024-unit performance ceiling and the zero-GC contract.
    /// </summary>
    [TestFixture]
    public sealed class VisibilityEngineTests
    {
        private static VisibilityUnitSnapshot Unit(ulong entity, byte owner, int x, int z) =>
            new VisibilityUnitSnapshot(new EntityId(entity), new PlayerId(owner), new WorldPointMm(x, z));

        private static BitMask1024[] NewOutput() =>
            new BitMask1024[SimulationConstants.MaxPlayers + 1];

        private static BitMask1024[] Calculate(
            VisibilityEngine engine,
            VisibilityUnitSnapshot[] units,
            int sightRadiusMm = SimulationConstants.DefaultSightRadiusMm)
        {
            var output = NewOutput();
            engine.CalculateVisibility(units, units.Length, output, sightRadiusMm);
            return output;
        }

        private static bool IsVisible(BitMask1024[] output, byte playerValue, ulong entityValue) =>
            output[playerValue].IsSet((int)entityValue - 1);

        [Test]
        public void EnemyAtNineteenPointNineMetres_IsVisible()
        {
            var engine = new VisibilityEngine();
            var units = new[]
            {
                Unit(1, 1, 0, 0),
                Unit(2, 2, SimulationConstants.DefaultSightRadiusMm - 100, 0),
            };

            var output = Calculate(engine, units);

            Assert.That(IsVisible(output, 1, 2), Is.True,
                "19.9 m is inside the 20 m sight radius");
            Assert.That(IsVisible(output, 2, 1), Is.True,
                "sight is symmetric at the same distance");
        }

        [Test]
        public void EnemyAtTwentyPointOneMetres_IsNotVisible()
        {
            var engine = new VisibilityEngine();
            var units = new[]
            {
                Unit(1, 1, 0, 0),
                Unit(2, 2, SimulationConstants.DefaultSightRadiusMm + 100, 0),
            };

            var output = Calculate(engine, units);

            Assert.That(IsVisible(output, 1, 2), Is.False,
                "20.1 m is outside the 20 m sight radius");
            Assert.That(IsVisible(output, 2, 1), Is.False);
        }

        [Test]
        public void EnemyExactlyAtSightRadius_IsVisible()
        {
            var engine = new VisibilityEngine();
            var units = new[]
            {
                Unit(1, 1, 0, 0),
                Unit(2, 2, SimulationConstants.DefaultSightRadiusMm, 0),
            };

            var output = Calculate(engine, units);

            Assert.That(IsVisible(output, 1, 2), Is.True,
                "the radius comparison is inclusive (<=)");
        }

        [Test]
        public void OwnUnits_AreVisibleAtAnyDistance()
        {
            var engine = new VisibilityEngine();
            var units = new[]
            {
                Unit(1, 1, 0, 0),
                Unit(2, 1, 5_000, 0),
                Unit(3, 1, 500_000, 0),
            };

            var output = Calculate(engine, units);

            Assert.That(IsVisible(output, 1, 1), Is.True, "the near own unit");
            Assert.That(IsVisible(output, 1, 2), Is.True, "a second own unit inside the sight radius");
            Assert.That(IsVisible(output, 1, 3), Is.True,
                "an own unit 500 m away stays visible: own visibility is unconditional");
        }

        [Test]
        public void AlliedUnits_AreVisibleAtAnyDistance_WithTeamMapping()
        {
            var engine = new VisibilityEngine();
            engine.SetPlayerTeam(new PlayerId(1), new TeamId(0));
            engine.SetPlayerTeam(new PlayerId(2), new TeamId(0));

            var units = new[]
            {
                Unit(1, 1, 0, 0),
                Unit(2, 2, 500_000, 0),
            };

            var output = Calculate(engine, units);

            Assert.That(IsVisible(output, 1, 2), Is.True,
                "an ally 500 m away stays visible");
            Assert.That(IsVisible(output, 2, 1), Is.True,
                "ally visibility is symmetric");
        }

        [Test]
        public void SameDistanceWithoutTeamMapping_IsAnEnemyAndStaysHidden()
        {
            var engine = new VisibilityEngine();
            var units = new[]
            {
                Unit(1, 1, 0, 0),
                Unit(2, 2, 500_000, 0),
            };

            var output = Calculate(engine, units);

            Assert.That(IsVisible(output, 1, 2), Is.False,
                "without a mapping other players are enemies and 500 m exceeds the radius");
        }

        [Test]
        public void AlliedSight_RevealsEnemiesInsideTheAllysRadius()
        {
            var engine = new VisibilityEngine();
            engine.SetPlayerTeam(new PlayerId(1), new TeamId(0));
            engine.SetPlayerTeam(new PlayerId(2), new TeamId(0));
            engine.SetPlayerTeam(new PlayerId(3), new TeamId(1));

            var units = new[]
            {
                Unit(1, 1, 0, 0),
                Unit(2, 2, 500_000, 0),
                Unit(3, 3, 505_000, 0),
            };

            var output = Calculate(engine, units);

            Assert.That(IsVisible(output, 2, 3), Is.True, "the ally sees its 5 m neighbour");
            Assert.That(IsVisible(output, 1, 3), Is.True,
                "the enemy is 505 m from player 1 but inside the ally's radius");
            Assert.That(IsVisible(output, 1, 2), Is.True, "the ally itself is always visible");
            Assert.That(IsVisible(output, 3, 1), Is.False,
                "the enemy does not gain sight of the distant player 1");
        }

        [Test]
        public void OneFriendlyUnit_RevealsEveryEnemyInsideItsRadius()
        {
            var engine = new VisibilityEngine();
            var units = new[]
            {
                Unit(1, 1, 0, 0),
                Unit(2, 2, 10_000, 0),
                Unit(3, 2, 0, 10_000),
                Unit(4, 2, -15_000, 5_000),
                Unit(5, 3, 30_000, 0),
            };

            var output = Calculate(engine, units);

            Assert.That(IsVisible(output, 1, 2), Is.True, "east 10 m");
            Assert.That(IsVisible(output, 1, 3), Is.True, "north 10 m");
            Assert.That(IsVisible(output, 1, 4), Is.True, "south-west 15.8 m");
            Assert.That(IsVisible(output, 1, 5), Is.False, "30 m exceeds the radius");
        }

        [Test]
        public void NegativeCoordinates_UseFlooredCells()
        {
            var engine = new VisibilityEngine();
            var units = new[]
            {
                Unit(1, 1, -1, -1),
                Unit(2, 2, -19_900, -1),
                Unit(3, 2, -1, -19_900),
            };

            var output = Calculate(engine, units);

            Assert.That(IsVisible(output, 1, 2), Is.True,
                "both units straddle the origin cell boundary");
            Assert.That(IsVisible(output, 1, 3), Is.True);
        }

        [Test]
        public void ExplicitSightRadius_OverridesTheDefault()
        {
            var engine = new VisibilityEngine();
            var units = new[]
            {
                Unit(1, 1, 0, 0),
                Unit(2, 2, 40_000, 0),
            };

            var defaultOutput = Calculate(engine, units);
            Assert.That(IsVisible(defaultOutput, 1, 2), Is.False,
                "40 m is outside the 20 m default");

            var extendedOutput = Calculate(engine, units, 50_000);
            Assert.That(IsVisible(extendedOutput, 1, 2), Is.True,
                "an explicit 50 m radius reaches 40 m");
        }

        [Test]
        public void EmptyRoster_ClearsEveryOutputMask()
        {
            var engine = new VisibilityEngine();
            var output = NewOutput();
            output[0].Set(10);
            output[1].Set(20);

            engine.CalculateVisibility(Array.Empty<VisibilityUnitSnapshot>(), 0, output);

            for (var playerValue = 0; playerValue <= SimulationConstants.MaxPlayers; playerValue++)
            {
                Assert.That(output[playerValue].IsEmpty, Is.True,
                    $"player slot {playerValue} must be cleared on every call");
            }
        }

        [Test]
        public void GridResults_MatchBruteForceReference()
        {
            var engine = new VisibilityEngine();
            engine.SetPlayerTeam(new PlayerId(1), new TeamId(0));
            engine.SetPlayerTeam(new PlayerId(2), new TeamId(0));
            engine.SetPlayerTeam(new PlayerId(3), new TeamId(1));
            engine.SetPlayerTeam(new PlayerId(4), new TeamId(1));

            const int unitCount = 200;
            var units = new VisibilityUnitSnapshot[unitCount];
            var random = new Random(431);
            for (var index = 0; index < unitCount; index++)
            {
                units[index] = Unit(
                    (ulong)(index + 1),
                    (byte)((index % SimulationConstants.MaxPlayers) + 1),
                    random.Next(-120_000, 120_001),
                    random.Next(-120_000, 120_001));
            }

            var output = Calculate(engine, units);
            var radius = (long)SimulationConstants.DefaultSightRadiusMm;
            var radiusSquared = radius * radius;

            for (var playerValue = 1; playerValue <= SimulationConstants.MaxPlayers; playerValue++)
            {
                var viewer = new PlayerId((byte)playerValue);
                for (var target = 0; target < unitCount; target++)
                {
                    var expected = IsExpectedVisible(units, target, viewer, radiusSquared);
                    Assert.That(
                        IsVisible(output, (byte)playerValue, (ulong)(target + 1)),
                        Is.EqualTo(expected),
                        $"player {playerValue}, entity {target + 1} at {units[target].Position}");
                }
            }
        }

        [Test]
        public void SparseWorld_FallsBackToTheHashGridAndStillResolvesSight()
        {
            var engine = new VisibilityEngine();
            var units = new[]
            {
                Unit(1, 1, 0, 0),
                Unit(2, 2, 10_000, 0),
                Unit(3, 3, 600_000_000, 0),
                Unit(4, 4, -600_000_000, 0),
            };

            var output = Calculate(engine, units);

            Assert.That(IsVisible(output, 1, 2), Is.True,
                "the hash fallback must still resolve a unit 10 m away");
            Assert.That(IsVisible(output, 1, 3), Is.False, "600 km is far outside the radius");
            Assert.That(IsVisible(output, 1, 4), Is.False);
            Assert.That(IsVisible(output, 3, 4), Is.False);
        }

        [Test]
        public void SparseCluster_ExercisesTheHashedNeighbourhood()
        {
            var engine = new VisibilityEngine();
            var units = new VisibilityUnitSnapshot[61];
            for (var index = 0; index < 60; index++)
            {
                units[index] = Unit(
                    (ulong)(index + 1),
                    (byte)((index % 2) + 1),
                    100 + (index * 8_192),
                    0);
            }

            // One distant unit stretches the bounding rectangle past the dense
            // limit while the cluster keeps 60 occupied cells, so the engine
            // must take the hashed neighbour path rather than the fallback.
            units[60] = Unit(61, 3, 600_000_000, 0);

            var output = Calculate(engine, units);

            Assert.That(IsVisible(output, 1, 2), Is.True, "the next cell is 8.192 m away");
            Assert.That(IsVisible(output, 1, 4), Is.True,
                "a hashed neighbour lookup two cells away");
            Assert.That(IsVisible(output, 1, 61), Is.False, "600 km is far outside the radius");
            Assert.That(IsVisible(output, 2, 1), Is.True);
            Assert.That(IsVisible(output, 2, 61), Is.False);
            Assert.That(IsVisible(output, 3, 61), Is.True, "the distant unit sees itself");
            Assert.That(IsVisible(output, 3, 1), Is.False);
        }

        [Test]
        public void ThousandTwentyFourUnits_CalculateAverageUnderOneMillisecond()
        {
            var units = new VisibilityUnitSnapshot[SimulationConstants.MaxUnitsPerMatch];
            for (var index = 0; index < units.Length; index++)
            {
                units[index] = Unit(
                    (ulong)(index + 1),
                    (byte)((index % SimulationConstants.MaxPlayers) + 1),
                    ((index % 32) * 6_000) - 96_000,
                    ((index / 32) * 6_000) - 96_000);
            }

            var engine = new VisibilityEngine();
            var output = NewOutput();

            for (var warmup = 0; warmup < 20; warmup++)
            {
                engine.CalculateVisibility(units, units.Length, output);
            }

            // Background editor work can preempt one window; the median of
            // independent windows is the metric, not a single reading.
            const int samples = 5;
            const int iterations = 50;
            var sampleMs = new double[samples];
            for (var sample = 0; sample < samples; sample++)
            {
                GC.Collect();
                var stopwatch = Stopwatch.StartNew();
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    engine.CalculateVisibility(units, units.Length, output);
                }

                stopwatch.Stop();
                sampleMs[sample] = stopwatch.Elapsed.TotalMilliseconds / iterations;
            }

            var sorted = (double[])sampleMs.Clone();
            Array.Sort(sorted);
            var medianMs = sorted[samples / 2];
            TestContext.WriteLine(
                $"{SimulationConstants.MaxUnitsPerMatch} units: median {medianMs:F3} ms per calculation " +
                $"over {samples} x {iterations} iterations (samples: {string.Join(", ", sampleMs)}; " +
                "guard 1.000 ms)");

            Assert.That(output[1].PopCount(), Is.GreaterThan(0),
                "the measured calls must actually resolve visibility");
            Assert.That(medianMs, Is.LessThan(1.0),
                $"{SimulationConstants.MaxUnitsPerMatch} units took {medianMs:F3} ms median " +
                $"(samples: {string.Join(", ", sampleMs)}; budget 1.000 ms)");
        }

        [Test]
        public void CalculateVisibility_DoesNotAllocate()
        {
            var engine = new VisibilityEngine();
            var units = new VisibilityUnitSnapshot[256];
            for (var index = 0; index < units.Length; index++)
            {
                units[index] = Unit(
                    (ulong)(index + 1),
                    (byte)((index % SimulationConstants.MaxPlayers) + 1),
                    ((index % 16) * 8_000) - 64_000,
                    ((index / 16) * 8_000) - 64_000);
            }

            var output = NewOutput();

            for (var warmup = 0; warmup < 100; warmup++)
            {
                engine.CalculateVisibility(units, units.Length, output);
            }

            Assert.That(
                () =>
                {
                    for (var iteration = 0; iteration < 20; iteration++)
                    {
                        engine.CalculateVisibility(units, units.Length, output);
                    }
                },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "visibility calculation must be allocation-free");
        }

        [Test]
        public void GcAllocationRecorder_ObservesKnownAllocation_Control()
        {
            byte[] ballast = null;

            Assert.That(
                () => { ballast = new byte[4096]; },
                UnityEngine.TestTools.Constraints.Is.AllocatingGCMemory(),
                "the GC.Alloc recorder must observe a known allocation");

            Assert.That(ballast, Is.Not.Null);
        }

        [Test]
        public void InvalidArguments_AreRejected()
        {
            var engine = new VisibilityEngine();
            var units = new[] { Unit(1, 1, 0, 0) };
            var output = NewOutput();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => engine.CalculateVisibility(units, 2, output));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => engine.CalculateVisibility(units, -1, output));
            Assert.Throws<ArgumentException>(
                () => engine.CalculateVisibility(
                    units, 1, new BitMask1024[SimulationConstants.MaxPlayers]));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => engine.CalculateVisibility(units, 1, output, -1));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => engine.CalculateVisibility(new[] { Unit(0, 1, 0, 0) }, 1, output));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => engine.CalculateVisibility(new[] { Unit(1025, 1, 0, 0) }, 1, output));
        }

        private static bool IsExpectedVisible(
            VisibilityUnitSnapshot[] units, int target, PlayerId viewer, long radiusSquared)
        {
            if (units[target].Owner == viewer)
            {
                return true;
            }

            for (var friendly = 0; friendly < units.Length; friendly++)
            {
                if (!IsFriendlyReference(units[friendly].Owner, viewer))
                {
                    continue;
                }

                var dx = (long)units[target].Position.X - units[friendly].Position.X;
                var dz = (long)units[target].Position.Z - units[friendly].Position.Z;
                if ((dx * dx) + (dz * dz) <= radiusSquared)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsFriendlyReference(PlayerId owner, PlayerId viewer)
        {
            if (owner == viewer)
            {
                return true;
            }

            var ownerTeam = ReferenceTeam(owner.Value);
            return ownerTeam >= 0 && ownerTeam == ReferenceTeam(viewer.Value);
        }

        // Mirrors the mapping configured in GridResults_MatchBruteForceReference.
        private static int ReferenceTeam(byte playerValue)
        {
            switch (playerValue)
            {
                case 1:
                case 2:
                    return 0;
                case 3:
                case 4:
                    return 1;
                default:
                    return -1;
            }
        }
    }
}
