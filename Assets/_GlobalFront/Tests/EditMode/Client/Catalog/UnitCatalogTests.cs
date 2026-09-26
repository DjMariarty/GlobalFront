using System;
using GlobalFront.Client.Catalog;
using GlobalFront.Core.Model;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Client.Catalog
{
    /// <summary>
    /// Phase 3 (OD-29): the client's archetype reference, the table that turns a
    /// replicated <see cref="UnitKinds"/> byte into the mesh, selection ring radius
    /// and health denominator a unit needs to be drawn at all.
    ///
    /// Two properties matter more than the numbers themselves. First, an unresolved
    /// archetype must never be guessed: <see cref="UnitKinds.Unknown"/> and any kind
    /// without a row have to come back as a miss, because a wrong denominator draws
    /// a full-health unit at a third of its bar and a wrong radius picks the wrong
    /// silhouette — both read as bugs while the numbers stay plausible. Second, the
    /// lookup runs per unit whenever a presentation slot initialises, so it is O(1)
    /// by kind and allocation-free.
    /// </summary>
    [TestFixture]
    public sealed class UnitCatalogTests
    {
        private const int MeasuredIterations = 2000;

        [Test]
        public void Default_ResolvesTheThreePrototypeArchetypes()
        {
            var kinds = new[] { UnitKinds.Scout, UnitKinds.Tank, UnitKinds.BaseStructure };

            for (var index = 0; index < kinds.Length; index++)
            {
                Assert.That(UnitCatalog.Default.TryGet(kinds[index], out var definition), Is.True);
                Assert.That(definition.Kind, Is.EqualTo(kinds[index]), "row indexed by its own kind");
                Assert.That(string.IsNullOrEmpty(definition.DisplayName), Is.False);
                Assert.That(definition.MaximumHealth, Is.GreaterThan(0));
                Assert.That(definition.RadiusMillimetres, Is.GreaterThan(0));
                Assert.That(definition.IsResolved, Is.True);
            }
        }

        [Test]
        public void Default_HealthDenominatorMirrorsTheAuthoritativeStats()
        {
            // MatchConfig still applies one shared CombatStats to every unit, so a
            // per-archetype maximum here would draw a full-health unit at a
            // fraction of its bar. Per-kind stats are an owner decision (roster and
            // balance), and until then the catalog must state the shared value.
            for (var kind = UnitKinds.Scout; kind <= UnitKinds.BaseStructure; kind++)
            {
                Assert.That(UnitCatalog.Default.TryGet(kind, out var definition), Is.True);
                Assert.That(
                    definition.MaximumHealth,
                    Is.EqualTo(UnitCatalog.PrototypeMaximumHealth),
                    $"kind {kind} must agree with the single authoritative maximum");
                Assert.That(
                    definition.InvMaxHealth,
                    Is.EqualTo(1f / definition.MaximumHealth).Within(1e-7f));
            }
        }

        [Test]
        public void TryGet_UnknownAndOutOfRangeKindsAreMissesNotGuesses()
        {
            Assert.That(UnitCatalog.Default.TryGet(UnitKinds.Unknown, out var unknown), Is.False);
            Assert.That(unknown.MaximumHealth, Is.EqualTo(0));
            Assert.That(unknown.IsResolved, Is.False);

            Assert.That(UnitCatalog.Default.TryGet(UnitKinds.Count, out var pastEnd), Is.False);
            Assert.That(UnitCatalog.Default.TryGet(byte.MaxValue, out var max), Is.False);
            Assert.That(pastEnd.IsResolved, Is.False);
            Assert.That(max.IsResolved, Is.False);
        }

        [Test]
        public void UnitDefinition_ZeroMaximumHealth_YieldsZeroInverseNotNaN()
        {
            // The exact shape of the PrototypeUnit.MaximumHealth ?? 0 defect: 0/0
            // would travel into a scaled quad and vanish the bar on some GPUs.
            var unresolved = new UnitDefinition(UnitKinds.Scout, "Unresolved", 0, 500);

            Assert.That(unresolved.InvMaxHealth, Is.EqualTo(0f));
            Assert.That(float.IsNaN(unresolved.InvMaxHealth), Is.False);
            Assert.That(unresolved.IsResolved, Is.False);

            var negative = new UnitDefinition(UnitKinds.Tank, "Negative", -100, 500);
            Assert.That(negative.InvMaxHealth, Is.EqualTo(0f));
            Assert.That(negative.IsResolved, Is.False);

            // An unresolved row never reaches a lookup at all: the catalog refuses it
            // at construction (see Constructor_RejectsUnusableRows), so no caller can
            // read a zero denominator back as "this unit has zero health".
            Assert.That(
                () => new UnitCatalog(new[] { unresolved }),
                Throws.ArgumentException);
        }

        /// <summary>
        /// Step 3.4 pre-flight (OD-29 note F-2): a row the presentation layer cannot
        /// draw with is refused where the roster is loaded, not where the bar is
        /// drawn. A stored zero maximum would come back through <c>TryGet</c> as a
        /// successful lookup and surface as an empty health bar on a healthy unit,
        /// and a zero radius as a ring scaled to nothing.
        /// </summary>
        [Test]
        public void Constructor_RejectsUnusableRows()
        {
            Assert.That(
                () => new UnitCatalog(new[] { new UnitDefinition(UnitKinds.Scout, "No health", 0, 500) }),
                Throws.ArgumentException,
                "a zero maximum health is an unresolved row, not a dead archetype");

            Assert.That(
                () => new UnitCatalog(new[] { new UnitDefinition(UnitKinds.Scout, "Negative health", -100, 500) }),
                Throws.ArgumentException);

            Assert.That(
                () => new UnitCatalog(new[] { new UnitDefinition(UnitKinds.Tank, "No radius", 100, 0) }),
                Throws.ArgumentException,
                "a zero radius cannot size a selection ring");

            Assert.That(
                () => new UnitCatalog(new[] { new UnitDefinition(UnitKinds.Tank, "Negative radius", 100, -1) }),
                Throws.ArgumentException);

            Assert.That(
                () => new UnitCatalog(new[] { new UnitDefinition(UnitKinds.Tank, null, 100, 500) }),
                Throws.ArgumentException,
                "a row without a name cannot drive the inspector or the selection HUD");

            // An empty name is still a name the caller chose; only the absence of a
            // row is refused, so a data-driven roster may stage a kind before its
            // localization key arrives.
            Assert.That(
                () => new UnitCatalog(new[] { new UnitDefinition(UnitKinds.Tank, string.Empty, 100, 500) }),
                Throws.Nothing);
        }

        /// <summary>
        /// The guard must not become a reason to drop a good row that happens to sit
        /// next to a bad one in the same array: the rejection is per row, and the
        /// rows that pass are exactly the ones the catalog serves.
        /// </summary>
        [Test]
        public void Constructor_RejectsTheWholeTable_AndServesNothingFromABadRow()
        {
            Assert.That(
                () => new UnitCatalog(new[]
                {
                    new UnitDefinition(UnitKinds.Scout, "Scout", 100, 500),
                    new UnitDefinition(UnitKinds.Tank, "Tank", 0, 900),
                }),
                Throws.ArgumentException);

            var partial = new UnitCatalog(new[]
            {
                new UnitDefinition(UnitKinds.Scout, "Scout", 100, 500),
            });

            Assert.That(partial.TryGet(UnitKinds.Scout, out var scout), Is.True);
            Assert.That(scout.IsResolved, Is.True);
            Assert.That(partial.TryGet(UnitKinds.Tank, out _), Is.False,
                "a kind the roster left out stays a miss rather than a placeholder");
        }

        [Test]
        public void Constructor_RejectsAmbiguousTables()
        {
            Assert.That(
                () => new UnitCatalog(new[]
                {
                    new UnitDefinition(UnitKinds.Unknown, "Unknown row", 100, 500),
                }),
                Throws.ArgumentException,
                "Unknown is a lookup miss, never a row");

            Assert.That(
                () => new UnitCatalog(new[]
                {
                    new UnitDefinition(UnitKinds.Scout, "First", 100, 500),
                    new UnitDefinition(UnitKinds.Scout, "Second", 300, 900),
                }),
                Throws.ArgumentException,
                "a duplicate kind would silently shadow one row with the other");

            Assert.That(
                () => new UnitCatalog(new[]
                {
                    new UnitDefinition(200, "Beyond the protocol range", 100, 500),
                }),
                Throws.ArgumentException);
        }

        [Test]
        public void CustomTable_ResolvesOnlyItsOwnRows()
        {
            var catalog = new UnitCatalog(new[]
            {
                new UnitDefinition(UnitKinds.Tank, "Tank", 300, 1200),
            });

            Assert.That(catalog.TryGet(UnitKinds.Tank, out var tank), Is.True);
            Assert.That(tank.MaximumHealth, Is.EqualTo(300));
            Assert.That(tank.RadiusMillimetres, Is.EqualTo(1200));
            Assert.That(tank.InvMaxHealth, Is.EqualTo(1f / 300f).Within(1e-9f));

            Assert.That(catalog.TryGet(UnitKinds.Scout, out _), Is.False,
                "a roster that has not shipped a kind must not borrow another row");
        }

        [Test]
        public void TryGet_DoesNotAllocate()
        {
            var catalog = UnitCatalog.Default;
            UnitDefinition sink = default;

            // Control first: the recorder must be able to see an allocation before
            // its silence means anything (the editor's Mono counter reads 0).
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[4096]; },
                UnityEngine.TestTools.Constraints.Is.AllocatingGCMemory());
            Assert.That(ballast, Is.Not.Null);

            void Drive(int iterations)
            {
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    catalog.TryGet((byte)(1 + (iteration & 3)), out sink);
                }
            }

            Drive(200);
            Assert.That(
                () => Drive(MeasuredIterations),
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "the presentation layer resolves archetypes per unit, so the lookup must stay free");
            Assert.That(sink.IsResolved || sink.MaximumHealth == 0, Is.True);
        }
    }
}
