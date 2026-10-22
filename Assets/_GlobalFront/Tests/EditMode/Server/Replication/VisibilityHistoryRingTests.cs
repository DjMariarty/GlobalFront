using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Simulation;
using GlobalFront.Core.Visibility;
using GlobalFront.Server.Replication;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Server.Replication
{
    /// <summary>
    /// Isolated coverage of the 120-tick visibility history (ADR-014,
    /// Step 4.3.1): per-player mask storage, exact read-back, cyclic eviction
    /// of the oldest ten ticks after 130 records, forward-only recording,
    /// diagnostics reset and the zero-GC contract.
    /// </summary>
    [TestFixture]
    public sealed class VisibilityHistoryRingTests
    {
        private static BitMask1024[] MasksForTick(ulong tick)
        {
            var masks = new BitMask1024[SimulationConstants.MaxPlayers + 1];
            for (var playerValue = 1; playerValue <= SimulationConstants.MaxPlayers; playerValue++)
            {
                masks[playerValue].Set((int)(((tick * 37UL) + (ulong)playerValue) % BitMask1024.BitCount));
            }

            return masks;
        }

        [Test]
        public void Capacity_MatchesTheReplicationWindow()
        {
            Assert.That(VisibilityHistoryRing.Capacity, Is.EqualTo(120),
                "ADR-010 fixes the retained window at 120 ticks (6 s at 20 Hz)");
            Assert.That(VisibilityHistoryRing.PlayerMaskCapacity,
                Is.EqualTo(SimulationConstants.MaxPlayers));
        }

        [Test]
        public void RecordFiftyTicks_ReadsBackEveryPlayerMask()
        {
            var ring = new VisibilityHistoryRing();
            for (var tick = 1UL; tick <= 50UL; tick++)
            {
                ring.RecordTick(tick, MasksForTick(tick));
            }

            Assert.That(ring.Count, Is.EqualTo(50));
            Assert.That(ring.OldestTick, Is.EqualTo(1UL));
            Assert.That(ring.NewestTick, Is.EqualTo(50UL));
            Assert.That(ring.HasEvicted, Is.False);

            for (var tick = 1UL; tick <= 50UL; tick++)
            {
                var expected = MasksForTick(tick);
                for (var playerValue = 1; playerValue <= SimulationConstants.MaxPlayers; playerValue++)
                {
                    Assert.That(
                        ring.TryGetVisibility(tick, new PlayerId((byte)playerValue), out var mask),
                        Is.True, $"tick {tick}, player {playerValue}");
                    Assert.That(mask, Is.EqualTo(expected[playerValue]),
                        $"tick {tick}, player {playerValue} mask");
                }
            }
        }

        [Test]
        public void RecordOneHundredThirtyTicks_EvictsExactlyTheOldestTen()
        {
            var ring = new VisibilityHistoryRing();
            for (var tick = 1UL; tick <= 130UL; tick++)
            {
                ring.RecordTick(tick, MasksForTick(tick));
            }

            Assert.That(ring.Count, Is.EqualTo(VisibilityHistoryRing.Capacity));
            Assert.That(ring.OldestTick, Is.EqualTo(11UL));
            Assert.That(ring.NewestTick, Is.EqualTo(130UL));
            Assert.That(ring.EvictedThroughTick, Is.EqualTo(10UL));
            Assert.That(ring.HasEvicted, Is.True);

            for (var tick = 1UL; tick <= 10UL; tick++)
            {
                Assert.That(
                    ring.TryGetVisibility(tick, new PlayerId(1), out _),
                    Is.False, $"tick {tick} must be evicted");
            }

            for (var tick = 11UL; tick <= 130UL; tick++)
            {
                Assert.That(
                    ring.TryGetVisibility(tick, new PlayerId(4), out var mask),
                    Is.True, $"tick {tick} must be retained");
                Assert.That(mask, Is.EqualTo(MasksForTick(tick)[4]),
                    $"tick {tick} must keep its own mask");
            }
        }

        [Test]
        public void ContainsTick_FollowsTheRetainedWindow()
        {
            var ring = new VisibilityHistoryRing();
            for (var tick = 1UL; tick <= 130UL; tick++)
            {
                ring.RecordTick(tick, MasksForTick(tick));
            }

            Assert.That(ring.ContainsTick(10), Is.False);
            Assert.That(ring.ContainsTick(11), Is.True);
            Assert.That(ring.ContainsTick(70), Is.True);
            Assert.That(ring.ContainsTick(130), Is.True);
            Assert.That(ring.ContainsTick(131), Is.False);
            Assert.That(ring.ContainsTick(0), Is.False);
        }

        [Test]
        public void NonAdvancingTicks_AreIgnored()
        {
            var ring = new VisibilityHistoryRing();
            var masks = MasksForTick(5);

            ring.RecordTick(5, masks);
            ring.RecordTick(5, masks);
            ring.RecordTick(4, masks);

            Assert.That(ring.Count, Is.EqualTo(1));
            Assert.That(ring.NewestTick, Is.EqualTo(5UL));
            Assert.That(ring.IgnoredRecordCount, Is.EqualTo(2));
        }

        [Test]
        public void TryGetVisibility_InvalidPlayer_ReturnsFalse()
        {
            var ring = new VisibilityHistoryRing();
            ring.RecordTick(1, MasksForTick(1));

            Assert.That(ring.TryGetVisibility(1, new PlayerId(0), out _), Is.False,
                "player 0 is not a valid id");
            Assert.That(ring.TryGetVisibility(1, new PlayerId(11), out _), Is.False,
                "player 11 exceeds MaxPlayers");
        }

        [Test]
        public void RecordTick_ShortMaskSpan_ThrowsAndDoesNotRecord()
        {
            var ring = new VisibilityHistoryRing();

            Assert.Throws<ArgumentException>(
                () => ring.RecordTick(1, new BitMask1024[SimulationConstants.MaxPlayers]));

            Assert.That(ring.ContainsTick(1), Is.False);
            Assert.That(ring.Count, Is.Zero);
        }

        [Test]
        public void PlayerIndexZero_IsIgnored()
        {
            var ring = new VisibilityHistoryRing();
            var masks = new BitMask1024[SimulationConstants.MaxPlayers + 1];
            for (var bit = 0; bit < BitMask1024.BitCount; bit++)
            {
                masks[0].Set(bit);
            }

            ring.RecordTick(7, masks);

            for (var playerValue = 1; playerValue <= SimulationConstants.MaxPlayers; playerValue++)
            {
                Assert.That(
                    ring.TryGetVisibility(7, new PlayerId((byte)playerValue), out var mask),
                    Is.True);
                Assert.That(mask.IsEmpty, Is.True,
                    $"player index 0 is unused and must not be copied into player {playerValue}");
            }
        }

        [Test]
        public void Clear_DropsHistoryAndDiagnostics()
        {
            var ring = new VisibilityHistoryRing();
            for (var tick = 1UL; tick <= 3UL; tick++)
            {
                ring.RecordTick(tick, MasksForTick(tick));
            }

            ring.Clear();

            Assert.That(ring.Count, Is.Zero);
            Assert.That(ring.OldestTick, Is.Zero);
            Assert.That(ring.NewestTick, Is.Zero);
            Assert.That(ring.EvictedThroughTick, Is.Zero);
            Assert.That(ring.HasEvicted, Is.False);
            Assert.That(ring.IgnoredRecordCount, Is.Zero);
            Assert.That(ring.ContainsTick(1), Is.False);

            ring.RecordTick(1, MasksForTick(1));
            Assert.That(ring.ContainsTick(1), Is.True,
                "Clear resets the forward-only cursor");
        }

        [Test]
        public void RecordAndRead_DoNotAllocate()
        {
            var ring = new VisibilityHistoryRing();
            var masks = MasksForTick(1);

            for (var warmup = 0; warmup < 200; warmup++)
            {
                ring.RecordTick((ulong)(warmup + 1), masks);
                ring.TryGetVisibility((ulong)(warmup + 1), new PlayerId(1), out _);
            }

            var tick = 201UL;
            var allFound = true;

            Assert.That(
                () =>
                {
                    for (var iteration = 0; iteration < 100; iteration++)
                    {
                        ring.RecordTick(tick, masks);
                        allFound &= ring.TryGetVisibility(tick, new PlayerId(1), out _);
                        tick++;
                    }
                },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "recording and reading visibility masks must be allocation-free");

            Assert.That(allFound, Is.True, "every measured tick must be readable");
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
    }
}
