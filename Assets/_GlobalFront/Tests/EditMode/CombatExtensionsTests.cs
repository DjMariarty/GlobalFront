using GlobalFront.Core.Combat;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Step 5.1.3 — the two auxiliary combat calculators of ADR-015: linear splash falloff and
    /// the Gatling continuous-fire ladder. Both are whole-millimetre / whole-tick arithmetic with
    /// no heap traffic, because the combat tick runs them per shot.
    /// </summary>
    [TestFixture]
    public sealed class CombatExtensionsTests
    {
        private const int MeasuredReads = 1000;

        // ------------------------------------------------------------------
        // 1 — splash falloff
        // ------------------------------------------------------------------

        [Test]
        public void CalculateLinearFalloff_Center_ReturnsFullDamage()
        {
            Assert.That(SplashCalculator.CalculateLinearFalloff(20, 10000, 0), Is.EqualTo(20));
            Assert.That(SplashCalculator.CalculateLinearFalloff(1, 10000, 0), Is.EqualTo(1));
            Assert.That(SplashCalculator.CalculateLinearFalloff(999, 1, 0), Is.EqualTo(999));
        }

        [Test]
        public void CalculateLinearFalloff_Midpoint_ReturnsHalfDamage()
        {
            Assert.That(SplashCalculator.CalculateLinearFalloff(20, 10000, 5000), Is.EqualTo(10));
            Assert.That(SplashCalculator.CalculateLinearFalloff(40, 10000, 5000), Is.EqualTo(20));
            // Quarter and three-quarter points on the same line.
            Assert.That(SplashCalculator.CalculateLinearFalloff(40, 10000, 2500), Is.EqualTo(30));
            Assert.That(SplashCalculator.CalculateLinearFalloff(40, 10000, 7500), Is.EqualTo(10));
        }

        [Test]
        public void CalculateLinearFalloff_TruncatesRatherThanRounds()
        {
            // 21 x 0.5 = 10.5: R&D §4.3 and ADR-015 both truncate. If the splash rule ever
            // becomes round-half-up like DamageCalculator, this is the test that has to change
            // on purpose rather than by accident.
            Assert.That(SplashCalculator.CalculateLinearFalloff(21, 10000, 5000), Is.EqualTo(10));
            Assert.That(SplashCalculator.CalculateLinearFalloff(10, 4, 1), Is.EqualTo(7));
        }

        [Test]
        public void CalculateLinearFalloff_ExactEdge_ReturnsZero()
        {
            Assert.That(SplashCalculator.CalculateLinearFalloff(20, 10000, 10000), Is.EqualTo(0));
            Assert.That(SplashCalculator.CalculateLinearFalloff(20, 1, 1), Is.EqualTo(0));
        }

        [Test]
        public void CalculateLinearFalloff_BeyondEdge_ReturnsZero()
        {
            Assert.That(SplashCalculator.CalculateLinearFalloff(20, 10000, 10001), Is.EqualTo(0));
            Assert.That(SplashCalculator.CalculateLinearFalloff(20, 10000, int.MaxValue), Is.EqualTo(0));
        }

        [Test]
        public void CalculateLinearFalloff_InvalidParams_ReturnsZero()
        {
            Assert.That(SplashCalculator.CalculateLinearFalloff(0, 10000, 0), Is.EqualTo(0));
            Assert.That(SplashCalculator.CalculateLinearFalloff(-25, 10000, 0), Is.EqualTo(0));
            // A non-positive radius would otherwise divide by zero.
            Assert.That(SplashCalculator.CalculateLinearFalloff(20, 0, 0), Is.EqualTo(0));
            Assert.That(SplashCalculator.CalculateLinearFalloff(20, -1000, 0), Is.EqualTo(0));
        }

        [Test]
        public void CalculateLinearFalloff_NegativeDistance_TreatedAsCenter()
        {
            Assert.That(SplashCalculator.CalculateLinearFalloff(20, 10000, -5000), Is.EqualTo(20));
            Assert.That(SplashCalculator.CalculateLinearFalloff(20, 10000, int.MinValue), Is.EqualTo(20));
        }

        [Test]
        public void CalculateLinearFalloff_RimOfASmallBase_TruncatesToZero()
        {
            // Consequence of truncation, not a guard: one millimetre inside a 10 m blast is a
            // tenth of a percent of the base, and a base of 20 has no whole hit point left there.
            // Pinned so the 5.4.2 resolver cannot silently gain a min-1 clamp that DamageCalculator
            // applies to direct hits.
            Assert.That(SplashCalculator.CalculateLinearFalloff(20, 10000, 9999), Is.EqualTo(0));
            Assert.That(SplashCalculator.CalculateLinearFalloff(20, 10000, 9500), Is.EqualTo(1));
        }

        [Test]
        public void CalculateLinearFalloff_WorstCaseProduct_StaysInsideLongAndInt()
        {
            // The widest legal pair: CombatStats.MaxDamage over the whole world extent. The
            // product rides a long, so the narrowing back to int cannot wrap.
            Assert.That(
                SplashCalculator.CalculateLinearFalloff(CombatStats.MaxDamage, 1_000_000_000, 0),
                Is.EqualTo(800_000_000));
            Assert.That(
                SplashCalculator.CalculateLinearFalloff(CombatStats.MaxDamage, 1_000_000_000, 500_000_000),
                Is.EqualTo(400_000_000));
            Assert.That(
                SplashCalculator.CalculateLinearFalloff(int.MaxValue, 1_000_000_000, 0),
                Is.EqualTo(int.MaxValue));
        }

        // ------------------------------------------------------------------
        // 2 — gatling escalation
        // ------------------------------------------------------------------

        [Test]
        public void GatlingEscalation_TierBoundariesMatchADR015()
        {
            // Pinned against literals, not against the constants that produced them.
            Assert.That(GatlingEscalation.Tier0MultiplierPermille, Is.EqualTo((short)1000));
            Assert.That(GatlingEscalation.Tier1MultiplierPermille, Is.EqualTo((short)1400));
            Assert.That(GatlingEscalation.Tier2MultiplierPermille, Is.EqualTo((short)1800));
            Assert.That(GatlingEscalation.Tier3MultiplierPermille, Is.EqualTo((short)2200));

            Assert.That(GatlingEscalation.GetMultiplierPermille(-1), Is.EqualTo((short)1000));
            Assert.That(GatlingEscalation.GetMultiplierPermille(0), Is.EqualTo((short)1000));
            Assert.That(GatlingEscalation.GetMultiplierPermille(19), Is.EqualTo((short)1000));
            Assert.That(GatlingEscalation.GetMultiplierPermille(20), Is.EqualTo((short)1400));
            Assert.That(GatlingEscalation.GetMultiplierPermille(59), Is.EqualTo((short)1400));
            Assert.That(GatlingEscalation.GetMultiplierPermille(60), Is.EqualTo((short)1800));
            Assert.That(GatlingEscalation.GetMultiplierPermille(119), Is.EqualTo((short)1800));
            Assert.That(GatlingEscalation.GetMultiplierPermille(120), Is.EqualTo((short)2200));
            Assert.That(GatlingEscalation.GetMultiplierPermille(500), Is.EqualTo((short)2200));
            Assert.That(
                GatlingEscalation.GetMultiplierPermille(int.MaxValue),
                Is.EqualTo((short)2200),
                "a tick count at the top of int must not wrap into a lower tier");
        }

        [Test]
        public void GatlingEscalation_ShouldReset_OnlyAfterFortySilentTicks()
        {
            Assert.That(GatlingEscalation.ResetCooldownTicks, Is.EqualTo(40));

            Assert.That(GatlingEscalation.ShouldReset(-1), Is.False);
            Assert.That(GatlingEscalation.ShouldReset(0), Is.False);
            Assert.That(GatlingEscalation.ShouldReset(39), Is.False);
            Assert.That(GatlingEscalation.ShouldReset(40), Is.True);
            Assert.That(GatlingEscalation.ShouldReset(50), Is.True);
            Assert.That(GatlingEscalation.ShouldReset(int.MaxValue), Is.True);
        }

        // ------------------------------------------------------------------
        // 3 — zero-GC hot path
        // ------------------------------------------------------------------

        [Test]
        public void CalculateLinearFalloff_ThousandReads_AllocatesZeroBytes()
        {
            var warmup = 0;
            for (var i = 0; i < MeasuredReads; i++)
            {
                warmup += ResolveSplashSweep(i);
            }

            Assert.That(warmup, Is.GreaterThan(0), "the warmup must resolve real splash hits");

            var measured = 0;
            Assert.That(
                () =>
                {
                    for (var i = 0; i < MeasuredReads; i++)
                    {
                        measured += ResolveSplashSweep(i);
                    }
                },
                GcAssert.Not.AllocatingGCMemory(),
                "splash falloff runs per shot in the combat tick and must stay at 0 B GC.Alloc");

            Assert.That(measured, Is.EqualTo(warmup),
                "the measured window must have resolved the same hits, or the probe proves " +
                "nothing about work the JIT could have skipped");
        }

        [Test]
        public void GatlingEscalation_ThousandReads_AllocatesZeroBytes()
        {
            var warmup = 0;
            for (var i = 0; i < MeasuredReads; i++)
            {
                warmup += ResolveGatlingSweep(i);
            }

            Assert.That(warmup, Is.GreaterThan(0), "the warmup must read real ladder tiers");

            var measured = 0;
            Assert.That(
                () =>
                {
                    for (var i = 0; i < MeasuredReads; i++)
                    {
                        measured += ResolveGatlingSweep(i);
                    }
                },
                GcAssert.Not.AllocatingGCMemory(),
                "the gatling ladder is read per shot and must stay at 0 B GC.Alloc");

            Assert.That(measured, Is.EqualTo(warmup),
                "the measured window must have read the same ladder");
        }

        [Test]
        public void GcAllocationProbe_ObservesKnownAllocation_Control()
        {
            // The negated assertions above are only evidence once a positive one is proven live.
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[4096]; },
                GcAssert.AllocatingGCMemory(),
                "the GC.Alloc probe must observe a known allocation");
            Assert.That(ballast, Is.Not.Null);
        }

        /// <summary>
        /// Sweeps the linear range, the rim and the early-return guards. Assertion-free on
        /// purpose: an NUnit constraint inside the measured window allocates by itself.
        /// </summary>
        private static int ResolveSplashSweep(int index)
        {
            var baseDamage = 1 + (index % 512);
            var radiusMm = 1000 + (index % 7) * 1000;
            var distanceMm = (index % 10) == 0 ? radiusMm : index % 999;

            return SplashCalculator.CalculateLinearFalloff(baseDamage, radiusMm, distanceMm);
        }

        /// <summary>
        /// Reads every tier and both sides of the reset boundary.
        /// </summary>
        private static int ResolveGatlingSweep(int index)
        {
            var multiplier = GatlingEscalation.GetMultiplierPermille(index % 240);
            var reset = GatlingEscalation.ShouldReset(index % 80) ? 1 : 0;

            return multiplier + reset;
        }
    }
}
