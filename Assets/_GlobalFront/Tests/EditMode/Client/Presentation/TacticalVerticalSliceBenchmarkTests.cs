using System;
using System.Collections.Generic;
using System.Reflection;
using GlobalFront.Client;
using GlobalFront.Client.Catalog;
using GlobalFront.Client.Presentation;
using GlobalFront.Client.Replication;
using GlobalFront.Client.UI;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools.Constraints;
using EntityId = GlobalFront.Core.Model.EntityId;
using GcAssert = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace GlobalFront.Tests.EditMode.Client.Presentation
{
    /// <summary>
    /// Phase 3, step 3.7 (part 1): the 400-unit tactical vertical slice, wired
    /// end to end and measured (<see cref="TacticalVerticalSliceRunner"/>,
    /// <see cref="LocalReplicationLoopback"/>,
    /// <see cref="CorpseReapSnapshotSource"/>, ADR-012 / OD-23..OD-29).
    ///
    /// Steps 3.1 to 3.6 certified each layer on its own. These tests exist for
    /// what only a composition can show.
    ///
    /// <b>The loop has to be closed.</b> A host that simulates and a binder that
    /// presents are not a slice; the claim is that a move order issued from a
    /// click arrives, through the real wire codecs, as a position the pooled view
    /// interpolates toward and a blip the radar moves. The command tests walk that
    /// whole circle, and the loopback's own counters are asserted so a silently
    /// dropped packet cannot be mistaken for a steady state.
    ///
    /// <b>One frame, stepped once.</b> A driver, a camera rig and two HUD
    /// presenters each polling the same clock and the same mouse would double-step
    /// the slice and make an EditMode proof describe a frame nobody renders. The
    /// slice is stepped through <c>StepSimulationTick</c> and
    /// <c>StepPresentationFrame</c>, the components that would drive themselves
    /// are proven disabled, and a tick requested while the host paces its own
    /// driver refuses rather than silently measuring nothing.
    ///
    /// <b>Zero-GC at the composition, not the component.</b> Each layer was proved
    /// alone; an allocation is not always local, so the whole 400-unit presentation
    /// frame and the whole replication tick are measured together under Unity's
    /// GC.Alloc recorder, after a control case proves the recorder observes an
    /// allocation at all.
    ///
    /// <b>The budget is OD-28's.</b> 400 units at 60 FPS, measured rather than
    /// asserted, with the number in the failure message.
    /// </summary>
    [TestFixture]
    public sealed class TacticalVerticalSliceBenchmarkTests
    {
        private const int FullRoster = TacticalVerticalSliceRunner.DefaultUnitCount;
        private const int SmallRoster = 24;

        /// <summary>
        /// 250 is 125 units an army, which is a batch of
        /// <see cref="SimulationConstants.MaxSelectedEntities"/> and a remainder: the only
        /// roster size that shows how the order batching handles a tail.
        /// </summary>
        private const int OversizedRoster = 250;

        /// <summary>A point both quadrants are far from, so a march ordered to it never arrives.</summary>
        private static readonly WorldPointMm ReissueDestination = new WorldPointMm(20_000, 20_000);

        /// <summary>The formation the slice itself marches in, for orders this fixture issues by hand.</summary>
        private static readonly FormationSpec MarchFormation =
            new FormationSpec(10, 3200, CardinalFacing.North);

        private const string GroundMeshName = "GlobalFront Tactical Ground 400x400";
        private const string GroundMaterialName = "Tactical Ground";

        private static readonly string[] ContentMeshNames =
        {
            GroundMeshName,
            "GlobalFront Hull Scout",
            "GlobalFront Hull Tank",
            "GlobalFront Hull Base structure",
            "GlobalFront Turret Scout",
            "GlobalFront Turret Tank",
        };

        /// <summary>
        /// Every material the visual content path builds, named as
        /// <see cref="TacticalVerticalSliceRunner"/> names it.
        /// </summary>
        private static readonly string[] ContentMaterialNames =
        {
            GroundMaterialName,
            "Unit Scout",
            "Unit Tank",
            "Unit Base structure",
        };

        /// <summary>
        /// Above any sequence the slice's own batching commits for these rosters, so an
        /// order this fixture issues by hand cannot be read as a replay.
        /// </summary>
        private const uint ManualSequenceBase = 100;
        private const int MeasuredFrames = 60;
        private const int BudgetFrames = 120;
        private const int WarmupFrames = 30;
        private const float FrameSeconds = 1f / 60f;

        /// <summary>OD-28's 60 FPS ceiling, with the tight EditMode guard the brief asks for.</summary>
        private const double FrameBudgetMilliseconds = 8.0;

        /// <summary>A screen point with no unit within the pick radius: the map centre.</summary>
        private static readonly Vector3 OpenGroundMetres = new Vector3(10f, 0f, 10f);

        /// <summary>How long the contact test gives both armies to close on each other.</summary>
        private const int ContactTickLimit = 700;

        /// <summary>How long the checksum test gives the march to complete for some units.</summary>
        private const int ArrivalTickLimit = 520;

        private readonly List<TacticalVerticalSliceRunner> _runners =
            new List<TacticalVerticalSliceRunner>();

        private ServerUnitSnapshot[] _snapshots;

        private uint _manualSequence;

        private readonly List<GameObject> _created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (var index = 0; index < _runners.Count; index++)
            {
                var runner = _runners[index];
                if (runner == null)
                {
                    continue;
                }

                runner.Shutdown();
                if (runner.gameObject != null)
                {
                    Object.DestroyImmediate(runner.gameObject);
                }
            }

            _runners.Clear();

            for (var index = 0; index < _created.Count; index++)
            {
                if (_created[index] != null)
                {
                    Object.DestroyImmediate(_created[index]);
                }
            }

            _created.Clear();
        }

        // ---------------------------------------------------------------- bootstrap

        [Test]
        public void Initialize_FourHundredUnits_PopulatesEveryLayerOfTheSlice()
        {
            var runner = BuildSlice(FullRoster);

            Assert.That(runner.IsInitialized, Is.True);
            Assert.That(runner.UnitCount, Is.EqualTo(FullRoster));
            Assert.That(runner.World.LiveCount, Is.EqualTo(FullRoster),
                "the replicated world must hold the whole roster before the first frame");
            Assert.That(runner.Binder.BoundViewCount, Is.EqualTo(FullRoster),
                "every replicated unit must have a pooled view");
            Assert.That(runner.Binder.DestroyedViewCount, Is.EqualTo(0),
                "no view may be destroyed behind the pool's back");
            Assert.That(runner.FriendlyAliveCount, Is.EqualTo(FullRoster / 2));
            Assert.That(runner.EnemyAliveCount, Is.EqualTo(FullRoster / 2));
            Assert.That(runner.RadarModel.Rebuild(), Is.EqualTo(FullRoster),
                "the radar must blip the whole roster");
            Assert.That(runner.Receiver.State, Is.EqualTo(ReplicationReceiverState.Streaming));
            Assert.That(runner.Receiver.FailureReason, Is.EqualTo(ReplicationFailureReason.None));
            Assert.That(runner.Receiver.WorldPartiallyApplied, Is.False);

            // A warmed pool plus a full roster is the OD-26 claim: the first
            // Acquire and the four hundredth must cost the same.
            Assert.That(runner.ViewPool.GrowCount, Is.EqualTo(0),
                "steady-state ticks must never instantiate a view");
            Assert.That(runner.ViewPool.RejectedCount, Is.EqualTo(0));
            Assert.That(runner.ViewPool.AllowCombatGrowth, Is.False);
            Assert.That(runner.TickBuffer.CapturedTick, Is.EqualTo(runner.Receiver.LastAppliedTick));
            Assert.That(runner.TickBuffer.StalePacketCount, Is.EqualTo(0),
                "the slice must capture each applied packet exactly once");
            Assert.That(runner.Loopback.RejectedMessageCount, Is.EqualTo(0));
            Assert.That(runner.Emitter.SnapshotShortfallCount, Is.EqualTo(0));
            Assert.That(runner.LocalPlayer.IsValid, Is.True);
            Assert.That(runner.LocalSession.Player, Is.EqualTo(runner.LocalPlayer));
            Assert.That(runner.CommandChannel, Is.Not.Null);
            Assert.That(runner.CommandSink, Is.Not.Null);
        }

        [Test]
        public void Initialize_FourHundredUnits_RepresentsAllThreeCatalogArchetypes()
        {
            var runner = BuildSlice(FullRoster);
            var tally = KindTally(runner.World);

            // The third archetype is UnitKinds.BaseStructure: there is no
            // Artillery kind in the protocol (the kind byte is the 40th byte of a
            // delta add record), and these are the three kinds the catalog
            // resolves and the selection HUD draws rows for.
            Assert.That(tally[UnitKinds.Scout], Is.EqualTo(134));
            Assert.That(tally[UnitKinds.Tank], Is.EqualTo(134));
            Assert.That(tally[UnitKinds.BaseStructure], Is.EqualTo(132));
            Assert.That(CountOf(tally, UnitKinds.Unknown), Is.EqualTo(0),
                "every unit in the slice must resolve to an archetype");

            for (var kind = 1; kind < UnitKinds.Count; kind++)
            {
                Assert.That(runner.Catalog.TryGet((byte)kind, out var definition), Is.True,
                    $"archetype {kind} must resolve in the slice catalog");
                Assert.That(definition.MaximumHealth, Is.EqualTo(UnitCatalog.PrototypeMaximumHealth));
                Assert.That(runner.ViewPool.OwnedCount((byte)kind), Is.EqualTo(tally[(byte)kind]),
                    "the pool must have created exactly the views this archetype needs");
            }

            // Both armies field all three, so the radar palette, the pool's
            // per-kind free lists and the HUD's archetype rows are exercised on
            // both sides rather than only on the player's.
            KindTallyByOwner(runner, out var friendly, out var enemy);
            Assert.That(friendly.Count, Is.EqualTo(3));
            Assert.That(enemy.Count, Is.EqualTo(3));
        }

        [Test]
        public void Initialize_PlacesEachArmyInItsOwnQuadrantOnTheGroundPlane()
        {
            var runner = BuildSlice(FullRoster);
            var friendly = 0;
            var enemy = 0;

            for (var slot = 0; slot < runner.World.Capacity; slot++)
            {
                if (!runner.World.TryGetSlotState(slot, out var state) || state.Health <= 0)
                {
                    continue;
                }

                if (state.Owner == runner.LocalPlayer)
                {
                    Assert.That(state.PosX, Is.InRange(
                        TacticalVerticalSliceRunner.FriendlyQuadrantMinMillimetres,
                        TacticalVerticalSliceRunner.FriendlyQuadrantMaxMillimetres));
                    Assert.That(state.PosZ, Is.InRange(
                        TacticalVerticalSliceRunner.FriendlyQuadrantMinMillimetres,
                        TacticalVerticalSliceRunner.FriendlyQuadrantMaxMillimetres));
                    friendly++;
                }
                else
                {
                    Assert.That(state.PosX, Is.InRange(
                        TacticalVerticalSliceRunner.EnemyQuadrantMinMillimetres,
                        TacticalVerticalSliceRunner.EnemyQuadrantMaxMillimetres));
                    Assert.That(state.PosZ, Is.InRange(
                        TacticalVerticalSliceRunner.EnemyQuadrantMinMillimetres,
                        TacticalVerticalSliceRunner.EnemyQuadrantMaxMillimetres));
                    enemy++;
                }

                Assert.That(state.Position.IsWithinSimulationBounds, Is.True);

                // OD-27: the walkable surface is Y = 0 for everything, and a
                // bound view that disagrees has escaped the binder's pin.
                if (runner.Binder.TryGetView(slot, out var view) && view.IsBound)
                {
                    Assert.That(view.Hull.position.y, Is.EqualTo(UnitPickMath.GroundHeightMetres));
                }
            }

            Assert.That(friendly, Is.EqualTo(FullRoster / 2));
            Assert.That(enemy, Is.EqualTo(FullRoster / 2));
        }

        [Test]
        public void Loopback_CarriesKeyframeFeedbackAndDeltasInBothDirections()
        {
            var runner = BuildSlice(FullRoster);

            // The baseline is request-driven, so a slice that never asked would
            // never have been served: both halves of the uplink have to have run.
            Assert.That(runner.Loopback.RoutedRequestCount, Is.GreaterThan(0),
                "the receiver must have requested its baseline through the loopback");
            Assert.That(runner.Loopback.ForwardedSliceCount, Is.GreaterThan(0),
                "the emitter must have served keyframe slices through the loopback");

            // Acknowledgements follow applied *changes*. A roster that is standing
            // still produces no records, the emitter holds its deltas back to the
            // idle keep-alive, and the client has nothing new to acknowledge — so
            // this half of the claim needs the armies actually marching.
            Assert.That(runner.IssueConvergenceMarch(), Is.GreaterThan(0));
            StepTicks(runner, 30);

            Assert.That(runner.Loopback.ForwardedDeltaCount, Is.GreaterThan(0),
                "the steady state must be deltas, not a stream of keyframes");
            Assert.That(runner.Loopback.RoutedAckCount, Is.GreaterThan(0),
                "the client must have acknowledged applied packets");
            Assert.That(runner.Emitter.AckCount, Is.GreaterThan(0),
                "the emitter must have consumed the acknowledgements the loopback routed back");
            Assert.That(runner.Emitter.RequestCount, Is.GreaterThan(0),
                "the emitter must have consumed the baseline requests the loopback routed back");
            Assert.That(runner.Bridge.SentAckCount, Is.GreaterThan(0));
            Assert.That(runner.Bridge.AssembledKeyframeCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(runner.Loopback.ForwardedPayloadBytes, Is.GreaterThan(0));
            Assert.That(runner.Loopback.RejectedMessageCount, Is.EqualTo(0));
            Assert.That(runner.Emitter.SendFailureCount, Is.EqualTo(0));
            Assert.That(runner.Emitter.KeyframeFallbackCount, Is.EqualTo(0));
        }

        // ------------------------------------------------------ selection and commands

        [Test]
        public void SelectingLocalUnitsAcrossArchetypes_UpdatesHudRadarRingsAndBars()
        {
            var runner = BuildSlice(SmallRoster);
            var selected = 0;
            foreach (var kind in new[] { UnitKinds.Scout, UnitKinds.Tank, UnitKinds.BaseStructure })
            {
                selected += AddUnitsOfKind(runner, kind, 3);
            }

            Assert.That(selected, Is.EqualTo(9),
                "the small roster must field three of each archetype on the local side");
            Assert.That(runner.Selection.SelectedCount, Is.EqualTo(9));

            runner.StepPresentationFrame(FrameSeconds, 1.0);

            Assert.That(runner.SelectionHudModel.SelectedCount, Is.EqualTo(9));
            Assert.That(runner.SelectionHudModel.GetKindCount(UnitKinds.Scout), Is.EqualTo(3));
            Assert.That(runner.SelectionHudModel.GetKindCount(UnitKinds.Tank), Is.EqualTo(3));
            Assert.That(runner.SelectionHudModel.GetKindCount(UnitKinds.BaseStructure), Is.EqualTo(3));
            Assert.That(runner.SelectionHudModel.HealthPercent, Is.EqualTo(100),
                "untouched units read as full health");

            Assert.That(runner.SelectionHud.DisplayedCount, Is.EqualTo(HudStringTable.Count(9)));
            Assert.That(runner.SelectionHud.DisplayedKindCount(UnitKinds.Scout),
                Is.EqualTo(HudStringTable.Count(3)));
            Assert.That(runner.SelectionHud.DisplayedHealth, Is.EqualTo(HudStringTable.Health(100)));

            // The permanent canvas header carries the local player id; the alive
            // and selected counters are the dynamic panel's, not this one's.
            Assert.That(runner.PermanentHud.HeaderValueText.text,
                Is.EqualTo(HudStringTable.Count(runner.LocalPlayer.Value)));

            Assert.That(runner.OverlayBatcher.SelectionRingCount, Is.EqualTo(9));
            Assert.That(runner.OverlayBatcher.HealthBarCount, Is.EqualTo(9),
                "a selected unit always carries a bar, damaged or not");
            Assert.That(runner.OverlayBatcher.ClippedInstanceCount, Is.EqualTo(0));

            Assert.That(KindCount(runner.RadarModel, MinimapBlipKind.Selected), Is.EqualTo(9),
                "the radar must mark the same nine units");
            Assert.That(
                KindCount(runner.RadarModel, MinimapBlipKind.Friendly) +
                KindCount(runner.RadarModel, MinimapBlipKind.Selected),
                Is.EqualTo(runner.FriendlyAliveCount));
            Assert.That(KindCount(runner.RadarModel, MinimapBlipKind.Enemy), Is.EqualTo(runner.EnemyAliveCount));
        }

        [Test]
        public void DriverRightClickOnGround_SendsMoveToHostAndMovesUnitsAndBlips()
        {
            var runner = BuildSlice(SmallRoster);
            runner.SelectionDriver.PointerProjector = new SliceProjector();
            var movers = SelectLocalUnits(runner, 4);
            var mover = movers[0];

            var submittedBefore = runner.CommandSink.SubmittedCount;
            var attemptedBefore = runner.CommandSink.AttemptedCount;
            var before = ViewPosition(runner, mover);
            var blipBefore = BlipFor(runner, mover);

            runner.SelectionDriver.RequestedTick = runner.CurrentTick + 1ul;
            RightClick(runner, OpenGroundMetres);

            Assert.That(runner.CommandSink.AttemptedCount, Is.EqualTo(attemptedBefore + 1),
                "one right click must be one order, not two");
            Assert.That(runner.CommandSink.SubmittedCount, Is.EqualTo(submittedBefore + 1),
                "the host must have accepted the order");
            Assert.That(runner.CommandSink.LastKind, Is.EqualTo(GameCommandType.Move));
            Assert.That(runner.CommandSink.LastRejection, Is.EqualTo(MatchCommandRejection.None));

            // The order has to cross the loopback and land on the authoritative
            // roster, not merely sit in the sink.
            StepTicks(runner, 4);
            Assert.That(WorldState(runner, mover).HasMoveTarget, Is.True,
                "the accepted move order must reach the authoritative server");
            AssertMoveTargetNear(runner, mover, new WorldPointMm(10_000, 10_000));

            var after = StepFrames(runner, 60);
            Assert.That(Vector3.Distance(after, OpenGroundMetres),
                Is.LessThan(Vector3.Distance(before, OpenGroundMetres)),
                "a bound view must interpolate toward the destination it was ordered to");
            Assert.That(Vector3.Distance(after, before), Is.GreaterThan(0.4f),
                "sixty frames of a marched unit must actually move it");

            var blipAfter = BlipFor(runner, mover);
            Assert.That(blipAfter.Uv, Is.Not.EqualTo(blipBefore.Uv),
                "the radar must follow the unit it is blipping");
            var authority = WorldState(runner, mover);
            Assert.That(blipAfter.Uv, Is.EqualTo(MinimapProjection.MillimetresToUv(
                    MinimapBounds.Default, authority.PosX, authority.PosZ)),
                "the blip is the authoritative position, never a view guess");
        }

        [Test]
        public void MinimapRightClick_OrdersTheSelectedArmyThroughTheSameSink()
        {
            var runner = BuildSlice(SmallRoster);
            var movers = SelectLocalUnits(runner, 3);

            var radarRect = new Rect(0f, 0f, 200f, 200f);
            runner.MinimapInteraction.SetMinimapScreenRect(radarRect);
            var pixel = RadarPixelForMillimetres(radarRect, -60_000, 91_000);
            var submittedBefore = runner.CommandSink.SubmittedCount;
            var attemptedBefore = runner.CommandSink.AttemptedCount;
            runner.MinimapInteraction.RequestedTick = runner.CurrentTick + 1ul;

            Assert.That(runner.MinimapInteraction.HandleRightPointerUp(pixel), Is.True);
            Assert.That(runner.CommandSink.SubmittedCount, Is.EqualTo(submittedBefore + 1));
            Assert.That(runner.CommandSink.AttemptedCount, Is.EqualTo(attemptedBefore + 1),
                "a radar order must not also be issued as a battlefield order");
            Assert.That(runner.CommandSink.LastKind, Is.EqualTo(GameCommandType.Move));
            Assert.That(runner.MinimapInteraction.LastMoveDestinationMm,
                Is.EqualTo(new WorldPointMm(-60_000, 91_000)));

            StepTicks(runner, 6);
            Assert.That(runner.CommandSink.LastRejection, Is.EqualTo(MatchCommandRejection.None));
            Assert.That(WorldState(runner, movers[0]).HasMoveTarget, Is.True,
                "a radar order must move units, exactly as a battlefield order does");
            AssertMoveTargetNear(runner, movers[0], new WorldPointMm(-60_000, 91_000));

            var before = ViewPosition(runner, movers[0]);
            var after = StepFrames(runner, 40);
            Assert.That(Vector3.Distance(after, before), Is.GreaterThan(0.4f));
        }

        [Test]
        public void DriverRightClickOnEnemyUnit_IssuesAttackAndPursuesThroughTheHost()
        {
            var runner = BuildSlice(SmallRoster);
            runner.SelectionDriver.PointerProjector = new SliceProjector();
            var movers = SelectLocalUnits(runner, 3);
            var attacker = movers[0];
            var target = FirstEnemyEntity(runner);
            var targetMetres = WorldMetresOf(runner, target);

            var submittedBefore = runner.CommandSink.SubmittedCount;
            runner.SelectionDriver.RequestedTick = runner.CurrentTick + 1ul;
            RightClick(runner, targetMetres);

            Assert.That(runner.CommandSink.SubmittedCount, Is.EqualTo(submittedBefore + 1));
            Assert.That(runner.CommandSink.LastKind, Is.EqualTo(GameCommandType.Attack),
                "a right click on an enemy is an attack order, not a move");

            StepTicks(runner, 8);
            Assert.That(WorldState(runner, attacker).AttackTarget, Is.EqualTo(target),
                "the attack order must reach the authoritative roster");

            var before = Vector3.SqrMagnitude(ViewPosition(runner, attacker) - targetMetres);
            var afterPosition = StepFrames(runner, 60);
            var after = Vector3.SqrMagnitude(afterPosition - targetMetres);
            Assert.That(after, Is.LessThan(before),
                "a unit ordered to attack an out-of-range target closes on it");
        }

        // ---------------------------------------------------------------- attrition

        [Test]
        public void ConvergenceMarchAndEngagement_ArmiesMeetAcquireAndDealDamage()
        {
            var runner = BuildSlice(SmallRoster);

            // Read the authoritative side first: whether the enemy army was armed
            // at spawn is a fact the client cannot report.
            Assert.That(CountServerAutoAcquiring(runner), Is.EqualTo(SmallRoster / 2),
                "the enemy army must spawn autonomous and the local army player-driven");

            Assert.That(runner.IssueConvergenceMarch(), Is.EqualTo(2));
            Assert.That(runner.IssueEngagementOrders(), Is.EqualTo(2),
                "both armies must accept an order to fight each other");

            var closest = long.MaxValue;
            var acquired = 0;
            var damaged = 0;
            var replicated = 0;
            for (var tick = 0; tick < ContactTickLimit; tick++)
            {
                runner.StepSimulationTick();
                var pair = MeasureAuthoritativeContact(runner, out var targets, out var hurt);
                closest = Math.Min(closest, pair);
                acquired = Math.Max(acquired, targets);
                damaged = Math.Max(damaged, hurt);

                // Peak rather than final: an attack target is cleared the moment
                // its unit dies, so the last tick of a battle is the worst moment
                // to ask whether the fight ever reached the client.
                replicated = Math.Max(replicated, CountClientAttackTargets(runner));
            }

            // Four separate stages, so a failure says which one broke: the armies
            // met, the server armed them, the meeting produced damage, and the
            // damage reached the client.
            var acquireRange = (long)SimulationConstants.AutoAcquireRangeMm *
                               SimulationConstants.AutoAcquireRangeMm;
            Assert.That(closest, Is.LessThanOrEqualTo(acquireRange),
                "both armies must converge inside the auto-acquire range");
            Assert.That(acquired, Is.GreaterThan(0),
                "an army ordered to engage must hold an attack target; a bare move order " +
                "clears auto-acquisition, which is why the slice issues both");
            Assert.That(damaged, Is.GreaterThan(0),
                "acquired contact must produce damage, or no unit can ever die");
            Assert.That(replicated, Is.GreaterThan(0),
                "an acquired attack target must reach ClientReplicationWorld through the loopback");
        }

        [Test]
        public void CombatAttrition_ReapedCorpsesLeaveTheWorldViewsAndSelection()
        {
            var runner = BuildSlice(SmallRoster);
            var selected = SelectLocalUnits(runner, SimulationConstants.MaxSelectedEntities).Length;
            var aliveBefore = runner.FriendlyAliveCount + runner.EnemyAliveCount;
            Assert.That(runner.Receiver.AppliedRemoveCount, Is.EqualTo(0),
                "no unit has died yet, so the control is that removes start at zero");
            Assert.That(selected, Is.EqualTo(SmallRoster / 2));

            Assert.That(runner.IssueConvergenceMarch(), Is.EqualTo(2),
                "both armies must have their march accepted");
            Assert.That(runner.IssueEngagementOrders(), Is.EqualTo(2),
                "both armies must be ordered to fight, or nothing dies");

            var peakBars = 0;
            var peakRings = 0;
            var ticks = 0;
            const int TickLimit = 1400;
            while (ticks < TickLimit && runner.ReapedCorpseCount < 1)
            {
                runner.StepSimulationTick();
                ticks++;
                if (ticks % 5 != 0)
                {
                    continue;
                }

                runner.StepPresentationFrame(FrameSeconds, ticks * (double)FrameSeconds);
                peakBars = Math.Max(peakBars, runner.OverlayBatcher.HealthBarCount);
                peakRings = Math.Max(peakRings, runner.OverlayBatcher.SelectionRingCount);
            }

            MeasureAuthoritativeContact(runner, out var stillArmed, out var stillWounded);
            Assert.That(runner.ReapedCorpseCount, Is.GreaterThan(0),
                $"the converged armies must fight to a kill inside {TickLimit} ticks " +
                $"(world holds {runner.World.LiveCount}, wounded {stillWounded}, targeting {stillArmed}, " +
                $"removes applied {runner.Receiver.AppliedRemoveCount})");
            Assert.That(peakRings, Is.EqualTo(selected),
                "the living selection keeps its rings");
            Assert.That(peakBars, Is.GreaterThan(0),
                "a wounded unit must produce a health bar before it dies");

            Assert.That(runner.Receiver.AppliedRemoveCount, Is.GreaterThan(0),
                "the reap must reach the client as a DeltaRemoveRecord, not as a client-side guess");
            Assert.That(runner.World.LiveCount, Is.LessThan(SmallRoster));
            Assert.That(runner.FriendlyAliveCount + runner.EnemyAliveCount, Is.LessThan(aliveBefore));

            // Present once more so the binder has seen every removal the world has.
            runner.StepPresentationFrame(FrameSeconds, 200.0);

            Assert.That(runner.Binder.BoundViewCount, Is.EqualTo(runner.World.LiveCount),
                "every removed unit must have returned its view to the pool");
            Assert.That(runner.ViewPool.InUseCount, Is.EqualTo(runner.World.LiveCount));
            Assert.That(runner.Binder.DestroyedViewCount, Is.EqualTo(0),
                "returning a view to the pool must never mean destroying it");
            Assert.That(runner.ViewPool.GrowCount, Is.EqualTo(0),
                "attrition must not force the pool to instantiate");

            var blips = runner.RadarModel.Rebuild();
            Assert.That(blips, Is.EqualTo(runner.FriendlyAliveCount + runner.EnemyAliveCount),
                "the radar must drop the units the world dropped");

            // Whatever the melee took, the panel may not be holding a unit the
            // world no longer describes.
            AssertSelectionHoldsOnlyLivingOwnedUnits(runner);
        }

        [Test]
        public void CombatAttrition_KilledLocalUnitsLeaveTheSelectionAndTheRings()
        {
            var runner = BuildSlice(SmallRoster);
            var selected = SelectLocalUnits(runner, SimulationConstants.MaxSelectedEntities).Length;
            Assert.That(selected, Is.EqualTo(SmallRoster / 2));

            // One-sided engagement, so a local death is a certainty rather than a
            // coin flip: a mirrored melee can end with the player's army intact,
            // and then nothing in this test would have had anything to prune.
            // The local army is marched into contact but never told to fire.
            Assert.That(runner.IssueConvergenceMarch(), Is.EqualTo(2));
            Assert.That(runner.IssueEngagementOrders(forLocalArmy: false), Is.EqualTo(1));

            var friendlyBefore = runner.FriendlyAliveCount;
            var ticks = 0;
            const int TickLimit = 1400;
            while (ticks < TickLimit && runner.FriendlyAliveCount >= friendlyBefore)
            {
                runner.StepSimulationTick();
                ticks++;
                if (ticks % 5 != 0)
                {
                    continue;
                }

                runner.StepPresentationFrame(FrameSeconds, ticks * (double)FrameSeconds);
            }

            Assert.That(runner.FriendlyAliveCount, Is.LessThan(friendlyBefore),
                $"the enemy army must kill a local unit inside {TickLimit} ticks " +
                $"(world holds {runner.World.LiveCount}, local alive {runner.FriendlyAliveCount})");

            // The loop exits on the tick the death landed, and pruning is a frame
            // job: present once before asking what the panel holds.
            runner.StepPresentationFrame(FrameSeconds, 300.0);

            Assert.That(runner.Selection.SelectedCount, Is.LessThan(selected),
                "a dead unit must leave the selection without anyone asking it to");
            Assert.That(runner.Selection.PruneStaleSelection(), Is.EqualTo(0),
                "the frame loop has already dropped everything it could not keep");
            Assert.That(runner.OverlayBatcher.SelectionRingCount, Is.EqualTo(runner.Selection.SelectedCount),
                "a pruned unit loses its ring with the frame, not with the panel");
            AssertSelectionHoldsOnlyLivingOwnedUnits(runner);
        }

        // ------------------------------------------------------------------ zero-GC

        [Test]
        public void GcAllocationRecorder_ObservesKnownAllocation_Control()
        {
            // The recorder must be seen to work in this fixture before its
            // silence means anything.
            byte[] ballast = null;
            Assert.That(
                () => { ballast = new byte[4096]; },
                GcAssert.AllocatingGCMemory(),
                "the GC.Alloc recorder must observe a known allocation");
            Assert.That(ballast, Is.Not.Null);
        }

        [Test]
        public void StepPresentationFrame_FourHundredMovingAndSelectedUnits_DoesNotAllocate()
        {
            var runner = BuildSlice(FullRoster);
            SelectLocalUnits(runner, 100);
            // Two hundred units per army is two orders per army, at the server's
            // ceiling of SimulationConstants.MaxSelectedEntities per command.
            Assert.That(runner.IssueConvergenceMarch(), Is.EqualTo(4));

            // The roster has to be marching and the selection live before the
            // window opens, or the frame being measured is the empty one.
            StepTicks(runner, 12);
            Assert.That(WorldState(runner, FirstEnemyEntity(runner)).HasMoveTarget, Is.True,
                "the measured frame must present units that are moving");

            var time = Warmup(runner, WarmupFrames);
            Assert.That(runner.OverlayBatcher.SelectionRingCount, Is.GreaterThan(0));
            Assert.That(runner.OverlayBatcher.HealthBarCount, Is.GreaterThan(0));
            Assert.That(runner.World.LiveCount, Is.EqualTo(FullRoster));
            Assert.That(runner.RadarModel.HasCameraView, Is.True,
                "the viewport indicator must be part of the measured frame");

            var measured = time;
            Assert.That(
                () =>
                {
                    for (var frame = 0; frame < MeasuredFrames; frame++)
                    {
                        measured += FrameSeconds;
                        runner.StepPresentationFrame(FrameSeconds, measured);
                    }
                },
                GcAssert.Not.AllocatingGCMemory(),
                "a 400-unit presentation frame must allocate no managed memory");

            Assert.That(runner.RadarModel.RefreshCount, Is.GreaterThan(0),
                "the 30 Hz radar must have rebuilt inside the measured window");
            Assert.That(runner.Receiver.FailureReason, Is.EqualTo(ReplicationFailureReason.None));
        }

        [Test]
        public void StepSimulationTick_WhileTheRosterIsSettled_CapturesNoDuplicatePacket()
        {
            var runner = BuildSlice(SmallRoster);
            var capturedBefore = runner.TickBuffer.CapturedTick;
            Assert.That(capturedBefore, Is.EqualTo(runner.Receiver.LastAppliedTick));

            // Nobody has been ordered anywhere, and the emitter's idle keep-alive
            // is twenty ticks out, so these ticks apply nothing at all. The tick
            // buffer counts a repeat as a stale packet, which is exactly what the
            // capture gate exists to prevent — and a cadence estimate built from
            // re-captured ticks would tell the presenter the server is faster than
            // it is, which is the failure that gate retires.
            StepTicks(runner, 12);

            Assert.That(runner.TickBuffer.StalePacketCount, Is.EqualTo(0),
                "a tick that applied no packet must not re-capture the last one");
            Assert.That(runner.TickBuffer.CapturedTick, Is.EqualTo(capturedBefore));
        }

        [Test]
        public void StepSimulationTick_FourHundredUnitsOverTheLoopback_DoesNotAllocate()
        {
            var runner = BuildSlice(FullRoster);
            // Two hundred units per army is two orders per army, at the server's
            // ceiling of SimulationConstants.MaxSelectedEntities per command.
            Assert.That(runner.IssueConvergenceMarch(), Is.EqualTo(4));
            StepTicks(runner, WarmupFrames);

            Assert.That(runner.World.LiveCount, Is.EqualTo(FullRoster));
            var appliedBefore = runner.Receiver.AppliedUpdateCount + runner.Receiver.AppliedDeltaCount;

            Assert.That(
                () =>
                {
                    for (var tick = 0; tick < MeasuredFrames; tick++)
                    {
                        runner.StepSimulationTick();
                    }
                },
                GcAssert.Not.AllocatingGCMemory(),
                "an authoritative tick whose deltas cross the loopback must allocate nothing");

            var appliedAfter = runner.Receiver.AppliedUpdateCount + runner.Receiver.AppliedDeltaCount;
            Assert.That(appliedAfter, Is.GreaterThan(appliedBefore),
                "the measured window must actually have applied replicated records");
            Assert.That(runner.Loopback.RejectedMessageCount, Is.EqualTo(0));
            Assert.That(runner.Receiver.InconsistentApplyCount, Is.EqualTo(0));
            Assert.That(runner.Receiver.ChecksumMismatchCount, Is.EqualTo(0),
                "a checksum mismatch would re-baseline and mask the steady state being measured");
        }

        // ------------------------------------------------------------------- budget

        /// <summary>
        /// OD-28's 400-unit profile, measured on the CPU side of the presentation
        /// loop: one authoritative simulation tick plus one presentation frame
        /// (tick-buffer interpolation, views, overlays, radar and both HUD canvases)
        /// per iteration, with no camera and no GPU submit because the slice runs
        /// headless (<c>createVisuals: false</c>) in EditMode.
        ///
        /// The reference number this fixture reported is <b>0.623 ms/frame</b> on the
        /// test machine, against the 8.0 ms guard asserted below — better than 2×
        /// headroom to the 16.67 ms 60 FPS ceiling before any rendering cost. It is
        /// not a full-frame measurement: the draw calls the real game issues are
        /// outside this window, so the guard describes the simulation and
        /// presentation budget the slice owns, not the frame rate of a built game.
        /// </summary>
        [Test]
        public void StepPresentationFrame_OneHundredTwentyFrames_StaysInsideTheSixtyFpsBudget()
        {
            var runner = BuildSlice(FullRoster);
            SelectLocalUnits(runner, 100);
            // Two hundred units per army is two orders per army, at the server's
            // ceiling of SimulationConstants.MaxSelectedEntities per command.
            Assert.That(runner.IssueConvergenceMarch(), Is.EqualTo(4));

            var time = Warmup(runner, WarmupFrames);
            var stopwatch = Stopwatch.StartNew();
            for (var frame = 0; frame < BudgetFrames; frame++)
            {
                runner.StepSimulationTick();
                time += FrameSeconds;
                runner.StepPresentationFrame(FrameSeconds, time);
            }

            stopwatch.Stop();
            var averageMs = stopwatch.Elapsed.TotalMilliseconds / BudgetFrames;

            // Recorded as well as asserted: a passing budget claim that never
            // prints its number cannot be re-checked against a later run.
            TestContext.WriteLine(
                $"OD-28 profile: {FullRoster} units, {BudgetFrames} presentation frames, " +
                $"{averageMs:F3} ms per frame average (simulation tick included), " +
                $"guard {FrameBudgetMilliseconds:F2} ms, 60 FPS ceiling 16.67 ms.");

            Assert.That(averageMs, Is.GreaterThan(0.0),
                "a measured window that costs nothing measured nothing");
            Assert.That(averageMs, Is.LessThan(FrameBudgetMilliseconds),
                $"{FullRoster} units: {averageMs:F3} ms per frame over {BudgetFrames} frames " +
                $"(guard {FrameBudgetMilliseconds} ms, inside the 16.67 ms 60 FPS ceiling with room " +
                $"for the rendering the slice does not own)");
        }

        // ------------------------------------------------------- validation, teardown

        [Test]
        public void Initialize_RejectsRostersOutsideTheLegalRangeAndStaysUninitialized()
        {
            var runner = BuildRunner();

            Assert.That(
                () => runner.Initialize(0, createVisuals: false, autoStartRealTime: false),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => runner.Initialize(1, createVisuals: false, autoStartRealTime: false),
                Throws.InstanceOf<ArgumentOutOfRangeException>(),
                "one army of one unit leaves the other side empty, so two is the floor");
            Assert.That(
                () => runner.Initialize(ClientReplicationWorld.DefaultCapacity + 1,
                    createVisuals: false, autoStartRealTime: false),
                Throws.InstanceOf<ArgumentOutOfRangeException>());

            Assert.That(runner.IsInitialized, Is.False);
            Assert.That(runner.Host, Is.Null);
            Assert.That(runner.UnitCount, Is.EqualTo(0));

            // A rejected request must not leave the slice unusable.
            runner.Initialize(SmallRoster, createVisuals: false, autoStartRealTime: false);
            Assert.That(runner.IsInitialized, Is.True);
            Assert.That(runner.World.LiveCount, Is.EqualTo(SmallRoster));

            Assert.That(
                () => runner.Initialize(SmallRoster, createVisuals: false, autoStartRealTime: false),
                Throws.InstanceOf<InvalidOperationException>(),
                "initializing twice would leave one runner holding two matches");
        }

        /// <summary>
        /// Phase 4's transport seam (F-11): the slice has to run on the interfaces the
        /// emitter and the bridge already speak, not on the in-process loopback.
        ///
        /// The fake pair is a real relay, not a recorder: the downlink hands the
        /// emitter's payloads to the bridge's uplink, and the uplink hands the
        /// bridge's feedback back to the emitter's transport. That is the whole
        /// loop, minus the socket, which is what makes the baseline converge here
        /// for the same request/keyframe/pacing reasons it converges over the
        /// loopback. A seam that was merely stored would leave
        /// <see cref="TacticalVerticalSliceRunner.World"/> empty and fail below.
        ///
        /// The session is the seam's own: a network transport learns its connection
        /// identity from its handshake, so this one raises the attachment on the
        /// first control record it is asked to send rather than being handed one by
        /// the runner. Supplying a seam is all-or-nothing; half a loop is an
        /// exception, not a quietly degraded slice.
        /// </summary>
        [Test]
        public void Initialize_WithCustomTransportAndUplink_UsesInjectedSeamsWithoutCreatingLoopback()
        {
            var downlink = new RelayTransport();
            var uplink = new RelayUplink();
            var attached = false;
            downlink.PayloadSink = uplink.Deliver;
            uplink.FeedbackSink = (kind, payload, length) =>
            {
                if (!attached)
                {
                    // The handshake a socket would have completed before the first
                    // request; the emitter only tracks attached sessions.
                    attached = true;
                    downlink.AttachOneClient();
                }

                var type = kind == ReplicationFeedbackKind.Ack
                    ? TransportMessageType.SnapshotAck
                    : TransportMessageType.ReplicationRequest;
                downlink.DeliverFeedback(
                    new ReplicationFeedbackMessage(RelayedSession, type, payload, 0, length));
            };

            var runner = BuildRunner();
            runner.Initialize(
                SmallRoster,
                createVisuals: false,
                autoStartRealTime: false,
                transport: downlink,
                uplink: uplink);

            Assert.That(runner.Transport, Is.SameAs(downlink),
                "the injected downlink has to be the one the emitter sends through");
            Assert.That(runner.Uplink, Is.SameAs(uplink),
                "the injected uplink has to be the one the bridge sends feedback through");
            Assert.That(runner.Loopback, Is.Null,
                "an injected seam must not also build the in-process loopback behind it");
            Assert.That(runner.World.LiveCount, Is.EqualTo(SmallRoster),
                "the injected pair has to carry the baseline keyframe; a seam that is only " +
                "stored is not a seam the slice runs on");
            Assert.That(downlink.PayloadCount, Is.GreaterThan(0),
                "the emitter must have put its slices on the injected downlink");
            Assert.That(uplink.FeedbackCount, Is.GreaterThan(0),
                "and the bridge must have put its acks and requests on the injected uplink");

            Assert.That(
                () => runner.StepPresentationFrame(FrameSeconds, 1.0),
                Throws.Nothing,
                "a presentation frame has to run with no loopback behind the injected seam");

            runner.Shutdown();
            Assert.That(runner.Transport, Is.Null, "shutdown releases the injected downlink");
            Assert.That(runner.Uplink, Is.Null, "and the injected uplink");
            Assert.That(runner.Loopback, Is.Null);
        }

        [Test]
        public void Initialize_WithOnlyOneInjectedSeam_RefusesHalfAWiredLoop()
        {
            var downlink = new RelayTransport();
            var uplink = new RelayUplink();
            var runner = BuildRunner();

            Assert.That(
                () => runner.Initialize(
                    SmallRoster,
                    createVisuals: false,
                    autoStartRealTime: false,
                    transport: downlink),
                Throws.ArgumentException,
                "a transport without an uplink leaves the bridge with nowhere to send feedback");
            Assert.That(
                () => runner.Initialize(
                    SmallRoster,
                    createVisuals: false,
                    autoStartRealTime: false,
                    uplink: uplink),
                Throws.ArgumentException,
                "an uplink without a transport leaves the emitter with nowhere to send slices");
            Assert.That(runner.IsInitialized, Is.False);
            Assert.That(runner.Transport, Is.Null);
            Assert.That(runner.Uplink, Is.Null);
        }

        [Test]
        public void StepSimulationTick_WhileTheHostPacesItsOwnTicks_RefusesInsteadOfNoOp()
        {
            var runner = BuildRunner();
            runner.Initialize(SmallRoster, createVisuals: false, autoStartRealTime: true);
            Assert.That(runner.Host.TickDriverMode, Is.EqualTo(TickDriverMode.RealTime));

            // A silent refusal here is what would let a benchmark report a steady
            // state while measuring an empty loop.
            Assert.That(
                () => runner.StepSimulationTick(),
                Throws.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public void Shutdown_ReleasesViewsAndContentAndIsSafeToCallTwice()
        {
            var runner = BuildSlice(FullRoster);
            var pool = runner.ViewPool;
            Assert.That(runner.Binder.BoundViewCount, Is.EqualTo(FullRoster));
            Assert.That(runner.GetComponentsInChildren<UnitView>(true).Length, Is.EqualTo(FullRoster));

            runner.Shutdown();

            Assert.That(runner.IsInitialized, Is.False);
            Assert.That(runner.UnitCount, Is.EqualTo(0));
            Assert.That(runner.Host, Is.Null);
            Assert.That(runner.World, Is.Null);
            Assert.That(runner.Binder, Is.Null);
            Assert.That(pool.IsDisposed, Is.True);
            Assert.That(runner.GetComponentsInChildren<UnitView>(true).Length, Is.EqualTo(0),
                "teardown must destroy the views it instantiated");

            Assert.That(() => runner.Shutdown(), Throws.Nothing,
                "teardown must be idempotent, because OnDestroy follows a manual Shutdown");
        }

        [Test]
        public void Slice_OwnsTheFrameLoopSoNoWiredComponentStepsItself()
        {
            var runner = BuildSlice(SmallRoster);

            Assert.That(runner.SelectionDriver.enabled, Is.False,
                "the driver would step the same mouse a second time");
            Assert.That(runner.MinimapInteraction.enabled, Is.False);
            Assert.That(runner.PermanentHud.enabled, Is.False);
            Assert.That(runner.SelectionHud.enabled, Is.False);
            Assert.That(runner.CameraController.enabled, Is.False,
                "the rig would advance its own smoothing clock as well");
            Assert.That(runner.PermanentHud.IsBuilt, Is.True);
            Assert.That(runner.SelectionHud.IsBuilt, Is.True);
            Assert.That(runner.MarqueePresenter.IsBuilt, Is.True);
            Assert.That(runner.PointerBlocker.UsedCount, Is.EqualTo(3),
                "minimap, command bar and selection panel must all block the pointer");
            Assert.That(runner.InputManager, Is.Not.Null);
            Assert.That(runner.InputManager.IsMockMode, Is.True,
                "a headless slice is stepped by its test, not by wherever the editor's mouse is");
            Assert.That(runner.ViewportCamera, Is.Not.Null);
            Assert.That(runner.CameraController.MapBounds, Is.EqualTo(new Rect(-200f, -200f, 400f, 400f)),
                "the camera's world must be the same 400 m map the radar projects");

            // The frame loop has to tell the pointer path which tick its orders
            // should land on; a driver left at zero sends every click to a tick the
            // server has already passed, and the order is refused without anyone
            // in the slice having decided that.
            runner.StepPresentationFrame(FrameSeconds, 1.0);
            Assert.That(runner.SelectionDriver.RequestedTick, Is.EqualTo(runner.CurrentTick + 1ul),
                "commands issued this frame must be dated for the next authoritative tick");
            Assert.That(runner.MinimapInteraction.RequestedTick, Is.EqualTo(runner.CurrentTick + 1ul));
        }

        [Test]
        public void ActivateTacticalVerticalSlice_FromLegacyPrototype_SwitchesBattlefields()
        {
            var runtimeRoot = new GameObject("[GlobalFront Runtime]");
            _created.Add(runtimeRoot);
            runtimeRoot.AddComponent<PrototypeRtsController>();
            runtimeRoot.AddComponent<PrototypeHud>();
            var prototypeWorld = new GameObject("[GlobalFront Prototype World]");
            _created.Add(prototypeWorld);

            var runner = GlobalFrontRuntimeBootstrap.ActivateTacticalVerticalSlice(
                SmallRoster,
                issueInitialConvergenceMarch: false,
                createVisuals: false);
            _runners.Add(runner);

            Assert.That(runner, Is.Not.Null);
            Assert.That(runner.IsInitialized, Is.True);
            Assert.That(runner.World.LiveCount, Is.EqualTo(SmallRoster));
            Assert.That(GameObject.Find(GlobalFrontRuntimeBootstrap.TacticalSliceRootName), Is.Not.Null);
            Assert.That(runner.Host.TickDriverMode, Is.EqualTo(TickDriverMode.RealTime),
                "a launched slice runs on its host's real-time driver");

            // The 40-unit prototype is what the PlayMode suite still asserts on
            // scene load; switching takes it down without waiting for a frame end.
            Assert.That(GameObject.Find("[GlobalFront Prototype World]"), Is.Null);
            Assert.That(Object.FindAnyObjectByType<PrototypeRtsController>(), Is.Null);
            Assert.That(Object.FindAnyObjectByType<PrototypeHud>(), Is.Null);

            // Unity's == operator, not NUnit's Is.Null: the boxed reference of a
            // destroyed GameObject is not null, so Is.Null would stay silent about
            // exactly the leak this asserts against.
            var firstSliceRoot = runner.gameObject;

            var replaced = GlobalFrontRuntimeBootstrap.ActivateTacticalVerticalSlice(
                SmallRoster,
                issueInitialConvergenceMarch: false,
                createVisuals: false);
            _runners.Add(replaced);
            Assert.That(replaced, Is.Not.Null);
            Assert.That(firstSliceRoot == null, Is.True,
                "re-arming destroys the previous slice root instead of stacking a second host behind the same camera");
            Assert.That(
                GameObject.Find(GlobalFrontRuntimeBootstrap.TacticalSliceRootName) == replaced.gameObject,
                Is.True,
                "the surviving slice root is the one the switcher just created");
        }

        /// <summary>
        /// Playing-capture defect: launching the slice dropped the player into a battle
        /// that was already decided. Both armies were marched onto the same 14 m patch —
        /// the convergence destinations are (+5, +5) m and (-5, -5) m — and then ordered
        /// into each other, well inside the 18 m at which the server lets a unit acquire
        /// targets on its own, so four hundred units destroyed one another in about thirty
        /// seconds and no frame ever arrived in which the player was commanding anything.
        ///
        /// <b>Why the assertions are made this way.</b> The orders are enqueued for the
        /// next tick, so reading the roster the instant the switch returns proves nothing —
        /// a client that has not yet been replicated to looks identical either way. The
        /// host's own snapshots are read instead, after the ticks that would have applied
        /// them, which is also what makes the witness bite: a reverted default lands move
        /// targets on the whole roster within two ticks. The separation half is the reason
        /// standing still is genuinely quiet rather than merely un-ordered: the quadrants
        /// put the closest pair 113 m apart, six times the acquisition range.
        /// </summary>
        [Test]
        public void ActivateTacticalVerticalSlice_WithNoArguments_HandsThePlayerQuietArmies()
        {
            // No march argument at all: this is the call the F9 key and the prototype HUD's
            // button make, so the default is the gameplay the player is handed.
            var runner = GlobalFrontRuntimeBootstrap.ActivateTacticalVerticalSlice(
                SmallRoster,
                createVisuals: false);
            _runners.Add(runner);

            var before = runner.Host.GetAllSnapshots();
            Assert.That(before.Length, Is.EqualTo(SmallRoster));

            // The launched slice paces itself; two seconds of deterministic ticks needs the
            // driver back on manual, which is what the switch leaves behind it.
            runner.Host.StopRealTime();
            for (var tick = 0; tick < 40; tick++)
            {
                Assert.That(runner.Host.TickOnce(), Is.True, "the manual driver must advance on request");
            }

            var after = runner.Host.GetAllSnapshots();
            var commanded = 0;
            var moved = 0;
            var wounded = 0;
            var closestSquaredMillimetres = long.MaxValue;

            for (var index = 0; index < after.Length; index++)
            {
                var unit = after[index];
                var spawned = before[index];
                Assert.That(
                    unit.Entity,
                    Is.EqualTo(spawned.Entity),
                    "the snapshot tables are compared positionally here, so an unstable order would " +
                    "measure two different units and answer nothing");

                if (unit.HasMoveTarget || unit.AttackTarget.IsValid)
                {
                    commanded++;
                }

                if (unit.CurrentHealth < spawned.CurrentHealth)
                {
                    wounded++;
                }

                if (unit.Position.X != spawned.Position.X || unit.Position.Z != spawned.Position.Z)
                {
                    moved++;
                }

                var friendly = unit.Owner == runner.LocalPlayer;
                for (var other = 0; other < after.Length; other++)
                {
                    var rival = after[other];
                    if ((rival.Owner == runner.LocalPlayer) == friendly)
                    {
                        continue;
                    }

                    var nearX = (long)unit.Position.X - rival.Position.X;
                    var nearZ = (long)unit.Position.Z - rival.Position.Z;
                    var squared = nearX * nearX + nearZ * nearZ;
                    if (squared < closestSquaredMillimetres)
                    {
                        closestSquaredMillimetres = squared;
                    }
                }
            }

            var closestApproachMillimetres = (long)System.Math.Sqrt((double)closestSquaredMillimetres);

            Assert.That(
                commanded,
                Is.EqualTo(0),
                $"{commanded} of {SmallRoster} units hold an order two seconds after a slice that was " +
                "launched with no arguments — the switch is starting the battle for the player, and " +
                "the first thing a right-click does then is fail to matter");
            Assert.That(
                moved,
                Is.EqualTo(0),
                $"{moved} units left the formation they were spawned in");
            Assert.That(
                wounded,
                Is.EqualTo(0),
                $"{wounded} units lost health while standing in their own quadrant");
            Assert.That(
                closestApproachMillimetres,
                Is.GreaterThan(4L * SimulationConstants.AutoAcquireRangeMm),
                $"the two armies start {closestApproachMillimetres} mm apart against an automatic " +
                $"acquisition range of {SimulationConstants.AutoAcquireRangeMm} mm; standing still is " +
                "only quiet if nobody is in range to shoot");
        }

        [Test]
        public void UnitsReachingTheirDestination_KeepTheStateChecksumInSyncAcrossTheLoopback()
        {
            var runner = BuildSlice(SmallRoster);
            Assert.That(runner.IssueConvergenceMarch(), Is.EqualTo(2));

            // Long enough for the first formation slots to be reached and for the
            // checksum cadence to sample the settled roster more than once.
            StepTicks(runner, ArrivalTickLimit);

            var staleTargets = 0;
            var arrived = 0;
            var count = CopyServerSnapshots(runner);
            for (var index = 0; index < count; index++)
            {
                if (_snapshots[index].HasMoveTarget)
                {
                    continue;
                }

                arrived++;

                // The record keeps its last destination after arriving. This is
                // the state the wire cannot describe, and therefore the state the
                // two halves of the digest have to agree to forget.
                if (_snapshots[index].MoveTarget.X != 0 || _snapshots[index].MoveTarget.Z != 0)
                {
                    staleTargets++;
                }
            }

            Assert.That(arrived, Is.GreaterThan(0), "the march must have completed for some units");
            Assert.That(staleTargets, Is.GreaterThan(0),
                "the arrived units must still carry destination coordinates on the authoritative " +
                "record, or this test does not exercise the case it exists for");
            Assert.That(runner.Receiver.ChecksumMismatchCount, Is.EqualTo(0),
                "a completed march is not a desync: the digest may not depend on coordinates the " +
                "protocol omits for a cleared move target");
            Assert.That(runner.Receiver.RebaseCount, Is.EqualTo(0));
            Assert.That(runner.Emitter.KeyframeFallbackCount, Is.EqualTo(0));
        }

        // ------------------------------------------------------- reissued move orders

        /// <summary>
        /// A stopped unit keeps its destination on the authoritative record while OD-14
        /// tells the client to forget it, so the record agreeing with itself proves
        /// nothing about the client. Ordering the same march again has to be described as
        /// the change it is, or the client walks toward the origin and the two state
        /// digests never agree again.
        /// </summary>
        [Test]
        public void MoveOrderReissuedAfterAStop_DeliversTheCoordinatesAndKeepsTheChecksumInSync()
        {
            var runner = BuildInertSlice(SmallRoster);
            var army = ArmyOf(runner, runner.LocalPlayer);
            Assert.That(army.Length, Is.EqualTo(SmallRoster / 2));

            Assert.That(
                IssueLocalOrder(runner, army, GameCommandType.Move, ReissueDestination),
                Is.EqualTo(MatchCommandRejection.None));
            StepTicks(runner, 40);

            var destinations = new Dictionary<EntityId, WorldPointMm>();
            for (var index = 0; index < army.Length; index++)
            {
                var record = ServerRecord(runner, army[index]);
                Assert.That(record.HasMoveTarget, Is.True,
                    $"entity {army[index].Value} must still be marching, or there is no " +
                    "destination left to reissue");

                destinations[army[index]] = record.MoveTarget;
                var state = WorldState(runner, army[index]);
                Assert.That(state.HasMoveTarget, Is.True);
                Assert.That(state.MoveTarget, Is.EqualTo(record.MoveTarget),
                    "the client must hold the destination the record holds");
            }

            Assert.That(
                IssueLocalOrder(runner, army, GameCommandType.Stop, default),
                Is.EqualTo(MatchCommandRejection.None));
            StepTicks(runner, 6);

            var carryingStaleCoordinates = 0;
            foreach (var pair in destinations)
            {
                var record = ServerRecord(runner, pair.Key);
                Assert.That(record.HasMoveTarget, Is.False,
                    $"entity {pair.Key.Value} must have been stopped");

                var state = WorldState(runner, pair.Key);
                Assert.That(state.HasMoveTarget, Is.False);
                Assert.That(state.MoveTargetX, Is.EqualTo(0),
                    "a cleared target is described by the flag alone, so the client zeroes " +
                    $"the coordinates it was never sent (entity {pair.Key.Value})");
                Assert.That(state.MoveTargetZ, Is.EqualTo(0));

                if (record.MoveTarget != pair.Value)
                {
                    Assert.Fail(
                        $"entity {pair.Key.Value} lost the destination the stop left behind, so " +
                        "this scenario no longer exercises the case it exists for");
                }

                if (pair.Value.X != 0 || pair.Value.Z != 0)
                {
                    carryingStaleCoordinates++;
                }
            }

            Assert.That(carryingStaleCoordinates, Is.EqualTo(army.Length),
                "the stopped records must still carry their destinations, or the reissue below " +
                "is an ordinary first order rather than a repeat of a cleared one");

            Assert.That(
                IssueLocalOrder(runner, army, GameCommandType.Move, ReissueDestination),
                Is.EqualTo(MatchCommandRejection.None));
            StepTicks(runner, 30);

            var toldAgain = 0;
            foreach (var pair in destinations)
            {
                var state = WorldState(runner, pair.Key);
                Assert.That(state.HasMoveTarget, Is.True,
                    $"entity {pair.Key.Value} was ordered back onto its march and the client " +
                    "was never told the flag came back");

                if (state.MoveTarget == pair.Value)
                {
                    toldAgain++;
                }
            }

            Assert.That(toldAgain, Is.EqualTo(army.Length),
                "a reissued destination the client had been told to forget must be sent again: " +
                "the record matching its own stale value is not agreement with the client");
            Assert.That(runner.Emitter.StateChecksumCount, Is.GreaterThan(0),
                "the checksum cadence has to have run, or the parity asserted below proves " +
                "nothing at all");
            Assert.That(runner.Receiver.ChecksumMismatchCount, Is.EqualTo(0),
                "reissuing a cleared destination is not a desync, and a digest that counts " +
                "coordinates the protocol omits would say it was");
            Assert.That(runner.Receiver.RebaseCount, Is.EqualTo(0));
            Assert.That(runner.Emitter.KeyframeFallbackCount, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- order batching

        /// <summary>
        /// An army larger than one order is batched, and a batch that carries the previous
        /// batch's entity ids as well orders those units a second time onto slots that are
        /// already taken. Two units in one slot is an army marching into itself.
        /// </summary>
        [Test]
        public void IssueConvergenceMarch_OverAnOversizedArmy_OrdersEveryUnitIntoASlotOfItsOwn()
        {
            var runner = BuildSlice(OversizedRoster);
            var friendly = ArmyOf(runner, runner.LocalPlayer);
            var enemy = ArmyOf(runner, runner.EnemyPlayer);
            Assert.That(friendly.Length + enemy.Length, Is.EqualTo(OversizedRoster),
                "an odd roster splits with the extra unit on the local side");
            Assert.That(
                friendly.Length % SimulationConstants.MaxSelectedEntities, Is.Not.EqualTo(0),
                "this test exists for the remainder batch, so the army has to be one batch plus a tail");

            Assert.That(runner.IssueConvergenceMarch(), Is.EqualTo(4),
                "125 units an army is two orders each, and every one of them has to be accepted");
            StepTicks(runner, 2);

            Assert.That(CountDistinctServerMoveTargets(runner, friendly), Is.EqualTo(friendly.Length),
                "the local army's units must each hold a formation slot to themselves");
            Assert.That(CountDistinctServerMoveTargets(runner, enemy), Is.EqualTo(enemy.Length),
                "and the same for the enemy army: the remainder batch is the one that reused " +
                "the first batch's buffer");
        }

        /// <summary>
        /// Every batch of a march is aimed at the army's own destination, so without an
        /// offset per batch the second hundred units form up on top of the first hundred.
        /// </summary>
        [Test]
        public void IssueConvergenceMarch_OverAnOversizedArmy_FormsTheTailBatchIntoItsOwnBlock()
        {
            var runner = BuildSlice(OversizedRoster);

            Assert.That(runner.IssueConvergenceMarch(), Is.EqualTo(4));
            StepTicks(runner, 2);

            AssertTailBatchFormsBehindTheHead(
                runner, ArmyOf(runner, runner.LocalPlayer), "local");
            AssertTailBatchFormsBehindTheHead(
                runner, ArmyOf(runner, runner.EnemyPlayer), "enemy");

            Assert.That(runner.IssueEngagementOrders(), Is.EqualTo(4),
                "an army too big for one order is too big for one attack order as well");
            StepTicks(runner, 2);

            MeasureAuthoritativeContact(runner, out var armed, out _);
            Assert.That(armed, Is.EqualTo(OversizedRoster),
                "every unit of an oversized army must be ordered to fight, not just the first " +
                "hundred of each side");
        }

        /// <summary>
        /// The remainder batch is a second order, not a second helping of the first one: a
        /// buffer reused across chunks still carries the head's entity ids past the point
        /// where this chunk's copy stopped, and the head's units are then ordered again onto
        /// the tail's block.
        /// </summary>
        [Test]
        public void IssueConvergenceMarch_OverAnOversizedArmy_LeavesTheHeadBatchInTheFirstBlock()
        {
            var runner = BuildSlice(OversizedRoster);
            var friendly = ArmyOf(runner, runner.LocalPlayer);

            Assert.That(runner.IssueConvergenceMarch(), Is.EqualTo(4));
            StepTicks(runner, 2);

            var head = Math.Min(SimulationConstants.MaxSelectedEntities, friendly.Length);
            var tailBack = int.MaxValue;
            for (var index = head; index < friendly.Length; index++)
            {
                tailBack = Math.Min(tailBack, ServerRecord(runner, friendly[index]).MoveTarget.Z);
            }

            var stayed = 0;
            for (var index = 0; index < head; index++)
            {
                if (ServerRecord(runner, friendly[index]).MoveTarget.Z < tailBack)
                {
                    stayed++;
                }
            }

            Assert.That(stayed, Is.EqualTo(head),
                $"{head - stayed} of the local army's first {head} units share rows with the " +
                $"remainder batch (only {stayed} stayed ahead of z {tailBack}): either they were " +
                "ordered a second time, because the order buffer kept the previous chunk's tail, " +
                "or the second order was aimed at the point the first one already occupies");
        }

        // ---------------------------------------------------------------- corpse reaping

        /// <summary>
        /// A reaped corpse stays out of the roster the source describes, so without a
        /// mark it also stays past its grace window on every later capture and the count
        /// grows once per packet instead of once per unit.
        /// </summary>
        [Test]
        public void CorpseReap_CountsEachRemovedUnitOnceAcrossIdleCaptures()
        {
            var runner = BuildSlice(SmallRoster);
            var friendlyBefore = runner.FriendlyAliveCount;
            Assert.That(friendlyBefore, Is.EqualTo(SmallRoster / 2));
            Assert.That(runner.ReapedCorpseCount, Is.EqualTo(0));

            Assert.That(runner.IssueConvergenceMarch(), Is.EqualTo(2));

            // One-sided on purpose: a mirrored melee can finish with the player's army
            // intact, and a reaper nobody feeds has nothing to count.
            Assert.That(runner.IssueEngagementOrders(forLocalArmy: false), Is.EqualTo(1));

            var ticks = 0;
            const int TickLimit = 1400;
            while (ticks < TickLimit && runner.FriendlyAliveCount > 0)
            {
                runner.StepSimulationTick();
                ticks++;
            }

            Assert.That(runner.FriendlyAliveCount, Is.EqualTo(0),
                $"the armed army must clear the unarmed one inside {TickLimit} ticks so the " +
                $"roster settles (ticks run {ticks}, reaps {runner.ReapedCorpseCount}, " +
                $"removals {runner.Receiver.AppliedRemoveCount})");

            StepTicks(runner, 40);
            Assert.That(runner.ReapedCorpseCount, Is.EqualTo(friendlyBefore),
                "every corpse the reaper omitted must be counted exactly once");
            Assert.That(runner.Receiver.AppliedRemoveCount, Is.EqualTo(runner.ReapedCorpseCount),
                "the units this source removed are the removals the client was told about");

            var reaped = runner.ReapedCorpseCount;
            StepTicks(runner, 40);
            Assert.That(runner.ReapedCorpseCount, Is.EqualTo(reaped),
                $"a settled roster of {runner.World.LiveCount} live units counted " +
                $"{runner.ReapedCorpseCount - reaped} further removals across 40 idle captures: " +
                "the counter describes units this source left out, not captures it left them out in");
            Assert.That(runner.World.LiveCount, Is.EqualTo(SmallRoster / 2),
                "the roster has to have settled to the surviving army, or the 40 captures above " +
                "were not idle ones and the count they left behind says nothing");
        }

        // ------------------------------------------------------------- content lifecycle

        /// <summary>
        /// The only fixture that takes <c>createVisuals</c> on: every other test runs the
        /// slice headless, so the battlefield mesh, the ground material and the teardown
        /// order of the whole content set were otherwise uncovered by any assertion.
        /// </summary>
        [Test]
        public void Shutdown_AfterCreatingVisuals_ReleasesTheGroundAndEveryUnitAsset()
        {
            var runner = BuildRunner();
            runner.Initialize(SmallRoster, createVisuals: true, autoStartRealTime: false);
            Assert.That(runner.CreatesVisuals, Is.True);

            var selected = SelectLocalUnits(runner, 4);
            Assert.That(selected.Length, Is.EqualTo(4));
            StepTicks(runner, 2);
            runner.StepPresentationFrame(FrameSeconds, 1.0);
            Assert.That(runner.OverlayBatcher.SelectionRingCount, Is.EqualTo(4),
                "the selection has to reach the overlay batch before the draw runs");
            Assert.That(runner.OverlayBatcher.HealthBarCount, Is.GreaterThan(0),
                "and so do the wounded");
            runner.DrawOverlaysDirect();

            Assert.That(
                runner.GetComponentsInChildren<MeshRenderer>(true).Length, Is.GreaterThan(0),
                "a slice told to build the battlefield must own renderers to draw it");
            Assert.That(CountContentObjects<Mesh>(ContentMeshNames), Is.GreaterThan(0),
                "no content mesh was created, so the release below cannot see a leak");
            var materials = CountContentObjects<Material>(ContentMaterialNames);
            Assert.That(materials, Is.GreaterThan(0),
                "no content material was created — a surface shader did not resolve in this " +
                "run, so the material half of this test has nothing to release");
            Assert.That(CountContentObjects<Material>(new[] { GroundMaterialName }), Is.EqualTo(1),
                "the ground must be built before it can be proven released");

            runner.Shutdown();

            Assert.That(CountContentObjects<Mesh>(ContentMeshNames), Is.EqualTo(0),
                "teardown must destroy every mesh it built, the battlefield's with the hulls");
            Assert.That(CountContentObjects<Material>(ContentMaterialNames), Is.EqualTo(0),
                "and every material, including the ground's, which no field used to hold");
            Assert.That(runner.GetComponentsInChildren<MeshRenderer>(true), Is.Empty,
                "the content roots hold those renderers, so they have to go too");
            Assert.That(runner.Binder, Is.Null);
            Assert.That(runner.IsInitialized, Is.False);
        }

        // ------------------------------------------------ renderer feature installation (F-7)

        /// <summary>
        /// F-7: a renderer feature that is not in the URP renderer assets is never
        /// created, so <see cref="UnitOverlayRendererFeature.Active"/> stays null and the
        /// RenderGraph pass that chunks 250 instances at a time never runs in a build.
        /// The slice falls back to its direct command buffer, which is why the defect is
        /// invisible in play and visible only where the ownership rule is written down.
        ///
        /// This is the witness the assets themselves can give without a rendered frame:
        /// both renderers carry exactly one active feature of the right type, and the
        /// reference the renderer list holds is the sub-asset the file contains.
        /// </summary>
        [Test]
        public void UrpRendererAssets_RegisterExactlyOneActiveUnitOverlayFeature()
        {
            AssertRendererAssetCarriesOverlayFeature("Assets/Settings/PC_Renderer.asset");
            AssertRendererAssetCarriesOverlayFeature("Assets/Settings/Mobile_Renderer.asset");
        }

        private static void AssertRendererAssetCarriesOverlayFeature(string assetPath)
        {
            var renderer = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(assetPath);
            Assert.That(renderer, Is.Not.Null,
                $"{assetPath} is not an importable URP ScriptableRendererData");

            var features = renderer.rendererFeatures;
            var matches = 0;
            ScriptableRendererFeature overlay = null;
            for (var index = 0; index < features.Count; index++)
            {
                if (features[index] is UnitOverlayRendererFeature)
                {
                    matches++;
                    overlay = features[index];
                }
            }

            Assert.That(matches, Is.EqualTo(1),
                $"{assetPath} must register exactly one {nameof(UnitOverlayRendererFeature)}, " +
                $"found {matches}: {DescribeRendererFeatures(features)}");
            Assert.That(overlay.isActive, Is.True,
                $"the feature in {assetPath} is disabled, so URP never enqueues its pass");
            Assert.That(overlay is UnitOverlayRendererFeature, Is.True);

            // The renderer list has to point at the sub-asset the file actually carries:
            // a reference to a deleted or re-created object loads as a missing feature.
            var subAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            var linkedToSubAsset = false;
            for (var index = 0; index < subAssets.Length; index++)
            {
                if (ReferenceEquals(subAssets[index], overlay))
                {
                    linkedToSubAsset = true;
                    break;
                }
            }

            Assert.That(linkedToSubAsset, Is.True,
                $"{assetPath}'s feature list does not point at one of the file's own sub-assets, " +
                "so the reference only works while the asset is already loaded");
        }

        private static string DescribeRendererFeatures(List<ScriptableRendererFeature> features)
        {
            if (features == null || features.Count == 0)
            {
                return "no renderer features";
            }

            var names = new List<string>();
            for (var index = 0; index < features.Count; index++)
            {
                names.Add(features[index] == null ? "<missing>" : features[index].GetType().Name);
            }

            return string.Join(", ", names);
        }

        /// <summary>
        /// The other half of F-7: registration is only an installation if the running
        /// slice then hands its batches to the installed feature. The feature is
        /// created before <see cref="TacticalVerticalSliceRunner.Initialize"/> so the
        /// runner's own adoption path (the <c>Active?.Attach(_binder)</c> call) is the
        /// one under test, and the ownership rule is read where it is enforced: the
        /// direct command buffer must stay empty while the feature is ready, and must
        /// fill once the feature is disposed. Without the second half, an empty batch
        /// would make the first assertion pass for the wrong reason.
        /// </summary>
        [Test]
        public void Initialize_WithLiveOverlayFeature_HandsDrawingToTheFeatureWithoutDoubleDraw()
        {
            var feature = ScriptableObject.CreateInstance<UnitOverlayRendererFeature>();
            var previous = UnitOverlayRendererFeature.Active;
            var disposed = false;
            try
            {
                feature.Create();
                Assert.That(UnitOverlayRendererFeature.Active, Is.SameAs(feature));

                var runner = BuildRunner();
                runner.Initialize(SmallRoster, createVisuals: true, autoStartRealTime: false);

                Assert.That(feature.IsReady, Is.True,
                    "the slice has to attach its binder to the installed feature; a ready renderer " +
                    "with no match draws nothing and this slice would then draw through the fallback");

                SelectLocalUnits(runner, 4);
                StepTicks(runner, 2);
                runner.StepPresentationFrame(FrameSeconds, 1.0);
                Assert.That(runner.OverlayBatcher.SelectionRingCount, Is.EqualTo(4),
                    "the selection has to reach the overlay batch before the ownership rule means anything");

                var direct = OverlayCommandBufferOf(runner);
                Assert.That(direct.sizeInBytes, Is.EqualTo(0), "a fresh command buffer is empty");

                runner.DrawOverlaysDirect();
                Assert.That(direct.sizeInBytes, Is.EqualTo(0),
                    "the live feature owns the batches, so the direct path must record nothing: drawing " +
                    "both would put a second set of rings on the first");

                feature.Dispose();
                disposed = true;
                Assert.That(feature.IsReady, Is.False);
                Assert.That(UnitOverlayRendererFeature.Active, Is.Null);

                runner.DrawOverlaysDirect();
                Assert.That(direct.sizeInBytes, Is.GreaterThan(0),
                    "with the feature gone the same frame has to reach the direct path, or the empty " +
                    "buffer above was an empty batch rather than a hand-off");
            }
            finally
            {
                if (!disposed)
                {
                    feature.Dispose();
                }

                // The pipeline's own feature may have been the active one before this
                // test created its stand-in; a disposed feature leaves Active null, and
                // the next renderer creation is the only other path back.
                if (previous != null && previous != feature)
                {
                    previous.Create();
                }
            }
        }

        private static CommandBuffer OverlayCommandBufferOf(TacticalVerticalSliceRunner runner)
        {
            var field = typeof(TacticalVerticalSliceRunner).GetField(
                "_overlayCommandBuffer",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null,
                "the runner no longer owns a direct overlay command buffer, so the fallback under " +
                "test is gone");
            var buffer = (CommandBuffer)field.GetValue(runner);
            Assert.That(buffer, Is.Not.Null,
                "createVisuals: true must have built the direct command buffer");
            return buffer;
        }

        // ------------------------------------------- overlay drawing hook (playing capture)

        /// <summary>
        /// Playing-capture defect: the selection rings and the health bars were built,
        /// drawn and never seen. <c>LateUpdate</c> runs before the camera renders, and the
        /// camera opens its own frame by clearing it, so a command buffer executed from
        /// there is gone a few hundred microseconds after it was submitted. The overlay
        /// belongs on the pipeline's camera-end callback — the slot URP's own
        /// <c>PixelPerfectCamera</c> uses, which fires once the camera has finished with
        /// its context and onto the target about to be shown.
        ///
        /// <b>What EditMode can and cannot observe.</b> A headless run draws nothing, so
        /// the pixels are out of reach; the wiring is not. This asserts the subscription
        /// exists, that it is released again, and — below — that the pre-camera hook is gone
        /// rather than kept alongside it.
        /// </summary>
        [Test]
        public void OverlayDrawing_IsSubscribedToThePipelineCameraEndCallbackAndReleasedAgain()
        {
            var runner = BuildRunner();
            var enable = DeclaredRunnerMethod(runner, "OnEnable");
            var disable = DeclaredRunnerMethod(runner, "OnDisable");

            Assert.That(
                enable,
                Is.Not.Null,
                "the runner has no OnEnable, so it never subscribes to the render pipeline and " +
                "the overlays it draws are cleared by the camera that renders after them");
            Assert.That(
                disable,
                Is.Not.Null,
                "the callback is a static one, so a runner with no OnDisable leaves its handler " +
                "in the pipeline's list and keeps being called after its match has shut down");

            Assert.That(CountCameraEndHandlers(runner), Is.EqualTo(0), "a fresh runner is already subscribed");
            try
            {
                enable.Invoke(runner, null);
                Assert.That(
                    CountCameraEndHandlers(runner),
                    Is.EqualTo(1),
                    "the runner declares an OnEnable that never reaches endCameraRendering");
            }
            finally
            {
                disable.Invoke(runner, null);
            }

            Assert.That(CountCameraEndHandlers(runner), Is.EqualTo(0), "the handler outlived its OnDisable");
        }

        /// <summary>
        /// The other half of the same finding: hooking the camera-end callback while
        /// <c>LateUpdate</c> still drew too would put the rings on screen twice per frame —
        /// and one of those draws is the invisible one that produced the report.
        /// </summary>
        [Test]
        public void OverlayDrawing_IsNotHookedToLateUpdate()
        {
            Assert.That(
                typeof(TacticalVerticalSliceRunner).GetMethod(
                    "LateUpdate",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
                Is.Null,
                "TacticalVerticalSliceRunner still has a LateUpdate: a command buffer executed " +
                "before the camera renders is wiped by that camera's own clear, so the overlay " +
                "draw must live on the pipeline's camera-end callback and nowhere else");
        }

        /// <summary>
        /// <see cref="TacticalVerticalSliceRunner"/>'s own declaration of a Unity message.
        /// Scoped to the type rather than inherited, because a base class hook is not this
        /// slice's hook.
        /// </summary>
        private static MethodInfo DeclaredRunnerMethod(
            TacticalVerticalSliceRunner runner,
            string name)
        {
            Assert.That(runner, Is.Not.Null, $"no runner to look for {name} on");
            return runner.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        }

        /// <summary>
        /// How many times <paramref name="runner"/> appears in the pipeline's camera-end
        /// list. The event is a private static delegate on
        /// <see cref="RenderPipelineManager"/>, so it is read by
        /// reflection — the alternative is asserting nothing about the one mechanism the
        /// fix consists of.
        /// </summary>
        private static int CountCameraEndHandlers(TacticalVerticalSliceRunner runner)
        {
            var fields = typeof(RenderPipelineManager).GetFields(
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            foreach (var field in fields)
            {
                if (!typeof(Delegate).IsAssignableFrom(field.FieldType) ||
                    field.Name.IndexOf(
                        "EndCameraRendering",
                        StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var subscribers = field.GetValue(null) as Delegate;
                if (subscribers == null)
                {
                    return 0;
                }

                var matches = 0;
                foreach (var subscriber in subscribers.GetInvocationList())
                {
                    if (ReferenceEquals(subscriber.Target, runner))
                    {
                        matches++;
                    }
                }

                return matches;
            }

            Assert.Fail(
                "RenderPipelineManager exposes no camera-end delegate field to read, so the " +
                "subscription cannot be observed here at all");
            return 0;
        }

        // ----------------------------------------------------------- borrowed scene state

        /// <summary>
        /// The slice adopts a scene camera rig rather than building a second camera, and it
        /// rewrites that rig's map bounds, height and pitch bands, its enabled flag and the
        /// shared input manager's mock mode to do it. None of that belongs to it.
        /// </summary>
        [Test]
        public void Shutdown_RestoresTheAdoptedCameraRigAndInputManager()
        {
            var rig = new GameObject("Adopted Tactical Slice Rig");
            _created.Add(rig);
            rig.AddComponent<Camera>();
            var controller = rig.AddComponent<RtsCameraController>();
            var input = rig.AddComponent<RtsInputManager>();

            // Different from the rig's defaults and from the slice's, so a restore that
            // never happened cannot be mistaken for one that did.
            controller.enabled = true;
            controller.MaxHeight = 80f;
            controller.MinHeight = 15f;
            controller.MaxPitch = 70f;
            controller.MinPitch = 40f;
            controller.MapBounds = new Rect(-123f, -123f, 246f, 246f);
            input.SetMockMode(false);

            var boundsBefore = controller.MapBounds;
            var mockModeBefore = input.IsMockMode;

            var runner = BuildSlice(SmallRoster);
            Assert.That(runner.CameraController == controller, Is.True,
                "the slice took a different rig than the one this test put in the scene, so " +
                "nothing below is measuring the adoption path");
            Assert.That(controller.enabled, Is.False, "the runner steps the rig itself");
            Assert.That(controller.MapBounds, Is.EqualTo(new Rect(-200f, -200f, 400f, 400f)),
                "the slice has to have rewritten the bounds for a restore to mean anything");
            Assert.That(controller.MaxHeight, Is.EqualTo(260f));
            Assert.That(controller.MinPitch, Is.EqualTo(55f));
            Assert.That(input.IsMockMode, Is.True,
                "a headless slice is pinned to mock inputs while it runs");
            input.SetMockMousePosition(new Vector2(640f, 320f));

            runner.Shutdown();

            Assert.That(controller.enabled, Is.True,
                "a borrowed rig left switched off is a camera the next owner cannot move");
            Assert.That(controller.MapBounds, Is.EqualTo(boundsBefore),
                "and borrowed map bounds have to come back at the size they were found");
            Assert.That(controller.MinHeight, Is.EqualTo(15f));
            Assert.That(controller.MaxHeight, Is.EqualTo(80f));
            Assert.That(controller.MinPitch, Is.EqualTo(40f));
            Assert.That(controller.MaxPitch, Is.EqualTo(70f));
            Assert.That(input.IsMockMode, Is.EqualTo(mockModeBefore),
                "the shared manager's mock mode belongs to the scene, not to one slice");

            // Read the mock state back through the mode that owns it. With mock mode off
            // the same property answers with the editor's hardware pointer, which is
            // wherever whoever ran this last left it, and the assertion would be about the
            // machine rather than about the teardown.
            input.SetMockMode(true);
            Assert.That(input.MousePosition, Is.EqualTo(Vector2.zero),
                "mock inputs left in the shared manager would steer whoever reads it next");
            input.SetMockMode(mockModeBefore);

            Assert.That(controller.InputManager, Is.SameAs(input),
                "the rig is left reading the manager the slice stepped, not whatever it held " +
                "before adoption — a slice that hands back a dead binding leaves a camera " +
                "that answers nothing");

            Assert.That(runner.CameraController, Is.Null);
            Assert.That(runner.InputManager, Is.Null);
            Assert.That(runner.ViewportCamera, Is.Null);
        }

        /// <summary>
        /// The rig reads its pointer from a manager it latched in <c>Awake</c>, which is not
        /// necessarily the manager this slice ends up holding: a manager that has since been
        /// destroyed is fake-null to the controller, and <c>ProcessInput</c> then returns
        /// before it touches pan, zoom or rotation. The slice steps the rig itself, so it has
        /// to bind the manager it steps — otherwise the player drags across a battlefield
        /// that answers nothing and no log line says why.
        /// </summary>
        [Test]
        public void ResolveCameraRig_RebindsTheAdoptedRigToTheManagerTheSliceSteps()
        {
            var staleHost = new GameObject("SliceTest_ManagerTheRigFoundFirst");
            _created.Add(staleHost);
            var stale = staleHost.AddComponent<RtsInputManager>();

            var rig = new GameObject("SliceTest_RigOnASceneCamera");
            _created.Add(rig);
            rig.AddComponent<Camera>();
            var controller = rig.AddComponent<RtsCameraController>();

            // The binding a played scene's Awake would have left. EditMode does not invoke
            // Awake, so the state adoption starts from is written here rather than waited
            // for; the runner cannot tell the two apart and must not have to.
            controller.SetInputManager(stale);
            Object.DestroyImmediate(staleHost);
            Assert.That(controller.InputManager == null, Is.True,
                "Unity's comparison: the rig now holds a manager that is gone");

            var runner = BuildSlice(SmallRoster);
            Assert.That(runner.CameraController == controller, Is.True,
                "the slice took a different rig than the one this test rigged up, so nothing below " +
                "is measuring the adoption path");
            Assert.That(runner.InputManager, Is.Not.Null);
            Assert.That(runner.InputManager.gameObject == runner.gameObject, Is.True,
                "with no live manager anywhere the slice adds one to its own root, which is the " +
                "manager the rig has to be pointed at");
            Assert.That(
                controller.InputManager,
                Is.SameAs(runner.InputManager),
                "the rig has to read the manager the slice is stepping");

            // The same property as the behaviour a player gets: mock input on the slice's
            // manager moves the borrowed rig's focus.
            runner.InputManager.SetMockMoveInput(new Vector2(0f, 1f));
            runner.StepPresentationFrame(FrameSeconds, 1.0);
            Assert.That(
                controller.TargetFocusPoint.z,
                Is.GreaterThan(0f),
                "a pan the player asked for and the rig refused to make");
        }

        /// <summary>
        /// The other half of the same lease: when the scene has no input manager the slice
        /// adds one to its own root, and that component is the slice's to destroy. A manager
        /// the next slice finds through <c>FindAnyObjectByType</c> arrives carrying whoever
        /// created it, including its mock mode.
        /// </summary>
        [Test]
        public void Shutdown_DestroysTheInputManagerTheSliceAddedItself()
        {
            var runner = BuildSlice(SmallRoster);
            var manager = runner.InputManager;
            Assert.That(manager, Is.Not.Null);
            Assert.That(manager.gameObject == runner.gameObject, Is.True,
                "with no manager in the scene the slice adds one to its own root, which is the " +
                "case this test is about");

            runner.Shutdown();

            Assert.That(manager == null, Is.True,
                "a manager the slice created has to go with it: Unity's ==, because the boxed " +
                "reference of a destroyed component is not null");
        }

        // ------------------------------------------------------------------ helpers

        private TacticalVerticalSliceRunner BuildSlice(int unitCount)
        {
            var runner = BuildRunner();
            runner.Initialize(unitCount, createVisuals: false, autoStartRealTime: false);
            return runner;
        }

        /// <summary>
        /// A slice with nothing firing: the local army is player-driven by design and the
        /// enemy's automatic acquisition is switched off too, so a march stays a march for as
        /// long as the test needs one instead of turning into a battle mid-assertion.
        /// </summary>
        private TacticalVerticalSliceRunner BuildInertSlice(int unitCount)
        {
            var runner = BuildRunner();
            runner.Initialize(
                unitCount,
                createVisuals: false,
                autoStartRealTime: false,
                enemyAutoAcquire: false);
            return runner;
        }

        private TacticalVerticalSliceRunner BuildRunner()
        {
            var go = new GameObject("TacticalVerticalSliceBenchmarkTests_Slice");
            _created.Add(go);
            var runner = go.AddComponent<TacticalVerticalSliceRunner>();
            _runners.Add(runner);
            return runner;
        }

        /// <summary>
        /// Counts the authoritative roster's autonomous units. Whether the enemy
        /// army was armed at all is a spawn-time fact the client cannot report,
        /// so this reads the host rather than the replication.
        /// </summary>
        private int CountServerAutoAcquiring(TacticalVerticalSliceRunner runner)
        {
            var count = CopyServerSnapshots(runner);
            var armed = 0;
            for (var index = 0; index < count; index++)
            {
                if (_snapshots[index].AutoAcquireEnemies)
                {
                    armed++;
                }
            }

            return armed;
        }

        /// <summary>
        /// Squared millimetres between the closest opposing pair on the
        /// authoritative roster, plus how many units are targeting something and
        /// how many have lost health. Squared, because the bound asserted is a
        /// range and a square root per pair per tick would cost more than the
        /// tick it is measuring.
        /// </summary>
        private long MeasureAuthoritativeContact(
            TacticalVerticalSliceRunner runner,
            out int acquired,
            out int damaged)
        {
            var count = CopyServerSnapshots(runner);
            acquired = 0;
            damaged = 0;
            var best = long.MaxValue;

            for (var index = 0; index < count; index++)
            {
                var left = _snapshots[index];
                if (left.AttackTarget.IsValid)
                {
                    acquired++;
                }

                if (left.CurrentHealth < UnitCatalog.PrototypeMaximumHealth)
                {
                    damaged++;
                }

                for (var other = index + 1; other < count; other++)
                {
                    var right = _snapshots[other];
                    if (right.Owner == left.Owner)
                    {
                        continue;
                    }

                    var dx = (long)left.Position.X - right.Position.X;
                    var dz = (long)left.Position.Z - right.Position.Z;
                    var squared = dx * dx + dz * dz;
                    if (squared < best)
                    {
                        best = squared;
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// The invariant the attrition tests end on, whichever side of the melee
        /// lost units: a selection may never hold something the world no longer
        /// describes, or the next order goes to a corpse.
        /// </summary>
        private static void AssertSelectionHoldsOnlyLivingOwnedUnits(TacticalVerticalSliceRunner runner)
        {
            for (var index = 0; index < runner.Selection.SelectedCount; index++)
            {
                var entity = runner.Selection.GetSelectedEntity(index);
                Assert.That(runner.World.Contains(entity), Is.True,
                    $"entity {entity.Value} is selected but no longer in the replicated world");

                var state = WorldState(runner, entity);
                Assert.That(state.Health, Is.GreaterThan(0),
                    $"entity {entity.Value} is selected but dead");
                Assert.That(state.Owner, Is.EqualTo(runner.LocalPlayer),
                    $"entity {entity.Value} is selected but not the local player's");
            }
        }

        private static int CountClientAttackTargets(TacticalVerticalSliceRunner runner)
        {
            var withTarget = 0;
            for (var slot = 0; slot < runner.World.Capacity; slot++)
            {
                if (runner.World.TryGetSlotState(slot, out var state) && state.AttackTarget.IsValid)
                {
                    withTarget++;
                }
            }

            return withTarget;
        }

        private int CopyServerSnapshots(TacticalVerticalSliceRunner runner)
        {
            _snapshots ??= new ServerUnitSnapshot[ClientReplicationWorld.DefaultCapacity];
            var count = runner.Host.Server.CopySnapshots(_snapshots);
            Assert.That(count, Is.GreaterThanOrEqualTo(0),
                "the authoritative roster must fit the read buffer");
            return count;
        }

        private static void StepTicks(TacticalVerticalSliceRunner runner, int count)
        {
            for (var tick = 0; tick < count; tick++)
            {
                runner.StepSimulationTick();
            }
        }

        /// <summary>Steps presentation frames on a monotonic clock and returns the last view position.</summary>
        private static Vector3 StepFrames(TacticalVerticalSliceRunner runner, int count)
        {
            var first = runner.Selection.SelectedCount > 0
                ? runner.Selection.GetSelectedEntity(0)
                : FirstLocalEntity(runner);
            var time = 50.0;
            Vector3 position = Vector3.zero;
            for (var frame = 0; frame < count; frame++)
            {
                runner.StepSimulationTick();
                time += FrameSeconds;
                runner.StepPresentationFrame(FrameSeconds, time);
                if (runner.World.TryGet(first, out var probe) &&
                    runner.Binder.TryGetView(SlotOf(runner, probe.Entity), out var view) &&
                    view != null &&
                    view.IsBound)
                {
                    position = view.Hull.position;
                }
            }

            return position;
        }

        private static double Warmup(TacticalVerticalSliceRunner runner, int frames)
        {
            var time = 0.0;
            for (var frame = 0; frame < frames; frame++)
            {
                time += FrameSeconds;
                runner.StepPresentationFrame(FrameSeconds, time);
            }

            return time;
        }

        /// <summary>One complete press and release of the right mouse button at a world point.</summary>
        private static void RightClick(TacticalVerticalSliceRunner runner, Vector3 worldMetres)
        {
            var projector = (SliceProjector)runner.SelectionDriver.PointerProjector;
            runner.InputManager.SetMockMousePosition(projector.ScreenForWorld(worldMetres));
            runner.InputManager.SetMockMouseButton(1, true, true, false);
            // Mock input holds whatever it was last told, unlike a hardware edge
            // that reports pressed for exactly one frame: leaving the button down
            // would re-arm the press at the same pixel.
            runner.InputManager.SetMockMouseButton(1, false, false, true);
            runner.SelectionDriver.ManualUpdate(5.0);
            runner.InputManager.ResetMockInputs();
        }

        private static EntityId[] SelectLocalUnits(TacticalVerticalSliceRunner runner, int count)
        {
            var picked = new List<EntityId>();
            for (var slot = 0;
                 slot < runner.World.Capacity && picked.Count < count;
                 slot++)
            {
                if (!runner.World.TryGetSlotState(slot, out var state) ||
                    state.Owner != runner.LocalPlayer ||
                    state.Health <= 0)
                {
                    continue;
                }

                runner.Selection.AddSelected(state.Entity, slot);
                picked.Add(state.Entity);
            }

            return picked.ToArray();
        }

        private static int AddUnitsOfKind(TacticalVerticalSliceRunner runner, byte kind, int count)
        {
            var added = 0;
            for (var slot = 0; slot < runner.World.Capacity && added < count; slot++)
            {
                if (!runner.World.TryGetSlotState(slot, out var state) ||
                    state.UnitKind != kind ||
                    state.Owner != runner.LocalPlayer ||
                    state.Health <= 0)
                {
                    continue;
                }

                runner.Selection.AddSelected(state.Entity, slot);
                added++;
            }

            return added;
        }

        /// <summary>
        /// A kind the scenario never uses has no entry at all, so reading the
        /// dictionary for it throws instead of reporting the zero the test is
        /// asking about.
        /// </summary>
        private static int CountOf(Dictionary<byte, int> tally, byte kind)
        {
            tally.TryGetValue(kind, out var count);
            return count;
        }

        private static Dictionary<byte, int> KindTally(ClientReplicationWorld world)
        {
            var tally = new Dictionary<byte, int>();
            for (var slot = 0; slot < world.Capacity; slot++)
            {
                if (!world.TryGetSlotState(slot, out var state))
                {
                    continue;
                }

                tally.TryGetValue(state.UnitKind, out var seen);
                tally[state.UnitKind] = seen + 1;
            }

            return tally;
        }

        private static void KindTallyByOwner(
            TacticalVerticalSliceRunner runner,
            out Dictionary<byte, int> friendly,
            out Dictionary<byte, int> enemy)
        {
            friendly = new Dictionary<byte, int>();
            enemy = new Dictionary<byte, int>();
            for (var slot = 0; slot < runner.World.Capacity; slot++)
            {
                if (!runner.World.TryGetSlotState(slot, out var state))
                {
                    continue;
                }

                var bucket = state.Owner == runner.LocalPlayer ? friendly : enemy;
                bucket.TryGetValue(state.UnitKind, out var seen);
                bucket[state.UnitKind] = seen + 1;
            }
        }

        private static int KindCount(MinimapRadarModel radar, MinimapBlipKind kind)
        {
            radar.Rebuild();
            var count = 0;
            for (var index = 0; index < radar.BlipCount; index++)
            {
                if (radar.GetBlip(index).Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }

        private static MinimapBlip BlipFor(TacticalVerticalSliceRunner runner, EntityId entity)
        {
            var radar = runner.RadarModel;
            radar.Rebuild();
            for (var index = 0; index < radar.BlipCount; index++)
            {
                var blip = radar.GetBlip(index);
                if (blip.Entity == entity.Value)
                {
                    return blip;
                }
            }

            Assert.Fail($"the radar holds no blip for entity {entity.Value}");
            return default;
        }

        private static int SlotOf(TacticalVerticalSliceRunner runner, EntityId entity)
        {
            for (var slot = 0; slot < runner.World.Capacity; slot++)
            {
                if (runner.World.TryGetSlotState(slot, out var state) && state.Entity == entity)
                {
                    return slot;
                }
            }

            return -1;
        }

        private static EntityId FirstLocalEntity(TacticalVerticalSliceRunner runner)
        {
            for (var slot = 0; slot < runner.World.Capacity; slot++)
            {
                if (runner.World.TryGetSlotState(slot, out var state) && state.Owner == runner.LocalPlayer)
                {
                    return state.Entity;
                }
            }

            Assert.Fail("the slice has no local unit");
            return default;
        }

        private static EntityId FirstEnemyEntity(TacticalVerticalSliceRunner runner)
        {
            for (var slot = 0; slot < runner.World.Capacity; slot++)
            {
                if (runner.World.TryGetSlotState(slot, out var state) &&
                    state.Owner != runner.LocalPlayer &&
                    state.Health > 0)
                {
                    return state.Entity;
                }
            }

            Assert.Fail("the slice has no enemy unit");
            return default;
        }

        /// <summary>
        /// A move order is applied as a formation, so the move target the server
        /// writes on a unit is the destination plus that unit's slot offset, not
        /// the destination itself. Asserting the exact point would test
        /// <c>FormationLayout</c> rather than the slice, so the claim is that the
        /// order landed inside the formation the controller asked for.
        /// </summary>
        private static void AssertMoveTargetNear(
            TacticalVerticalSliceRunner runner,
            EntityId entity,
            WorldPointMm destination)
        {
            var target = WorldState(runner, entity).MoveTarget;
            Assert.That(Math.Abs(target.X - destination.X), Is.LessThanOrEqualTo(20_000),
                $"the move target {target.X},{target.Z} is not a formation offset of {destination.X},{destination.Z}");
            Assert.That(Math.Abs(target.Z - destination.Z), Is.LessThanOrEqualTo(20_000),
                $"the move target {target.X},{target.Z} is not a formation offset of {destination.X},{destination.Z}");
        }

        /// <summary>
        /// One order through the local player's own channel. The slice's army orders go out
        /// over the host's session path with a per-player sequence counter the runner keeps to
        /// itself, so a test that has to put two orders on one army in a row cannot call it
        /// twice without replaying a sequence the host already committed.
        /// </summary>
        private MatchCommandRejection IssueLocalOrder(
            TacticalVerticalSliceRunner runner,
            EntityId[] entities,
            GameCommandType kind,
            WorldPointMm destination)
        {
            var header = new CommandHeader(
                runner.LocalPlayer,
                ManualSequenceBase + ++_manualSequence,
                runner.CurrentTick + 1ul,
                kind);

            return kind == GameCommandType.Stop
                ? runner.CommandChannel.TrySubmitStop(header, entities)
                : runner.CommandChannel.TrySubmitMove(header, entities, destination, MarchFormation);
        }

        /// <summary>
        /// An army's entities in the order the server handed them out, which is the order the
        /// slice batches its orders in and therefore the order its formation slots are laid
        /// out in.
        /// </summary>
        private static EntityId[] ArmyOf(TacticalVerticalSliceRunner runner, PlayerId owner)
        {
            var picked = new List<EntityId>();
            for (var slot = 0; slot < runner.World.Capacity; slot++)
            {
                if (!runner.World.TryGetSlotState(slot, out var state) ||
                    state.Owner != owner ||
                    state.Health <= 0)
                {
                    continue;
                }

                picked.Add(state.Entity);
            }

            picked.Sort((left, right) => left.Value.CompareTo(right.Value));
            return picked.ToArray();
        }

        private static ServerUnitSnapshot ServerRecord(TacticalVerticalSliceRunner runner, EntityId entity)
        {
            Assert.That(runner.Host.TryGetUnit(entity, out var record), Is.True,
                $"entity {entity.Value} is not on the authoritative roster");
            return record;
        }

        /// <summary>
        /// How many distinct destinations an army's units hold on the authoritative roster.
        /// Fewer than the roster means two units were sent to one formation slot.
        /// </summary>
        private int CountDistinctServerMoveTargets(
            TacticalVerticalSliceRunner runner,
            EntityId[] army)
        {
            var seen = new HashSet<WorldPointMm>();
            for (var index = 0; index < army.Length; index++)
            {
                var record = ServerRecord(runner, army[index]);
                Assert.That(record.HasMoveTarget, Is.True,
                    $"entity {army[index].Value} was never ordered to march");
                seen.Add(record.MoveTarget);
            }

            return seen.Count;
        }

        /// <summary>
        /// The head of an oversized army and the remainder behind it are two orders, so they
        /// are two blocks: the tail's nearest row has to stand ahead of the head's furthest
        /// one. Asserted off the units' own coordinates rather than off the march destination,
        /// which the scenario keeps to itself.
        /// </summary>
        private void AssertTailBatchFormsBehindTheHead(
            TacticalVerticalSliceRunner runner,
            EntityId[] army,
            string label)
        {
            var head = Math.Min(SimulationConstants.MaxSelectedEntities, army.Length);
            Assert.That(army.Length, Is.GreaterThan(head),
                "there is no remainder batch in an army this size, so this asserts nothing");

            var headFront = int.MinValue;
            for (var index = 0; index < head; index++)
            {
                headFront = Math.Max(headFront, ServerRecord(runner, army[index]).MoveTarget.Z);
            }

            var tailBack = int.MaxValue;
            for (var index = head; index < army.Length; index++)
            {
                tailBack = Math.Min(tailBack, ServerRecord(runner, army[index]).MoveTarget.Z);
            }

            Assert.That(tailBack, Is.GreaterThan(headFront),
                $"the {label} army's batches do not form two blocks: its nearest row stands at " +
                $"z {tailBack} while the first order reaches z {headFront}. Each order of a split " +
                "march has to be aimed a formation block further along, and each has to carry only " +
                "its own units, or two hundred units are sent to the same hundred slots");
        }

        /// <summary>
        /// Native objects the visual content path builds, counted by the names it gives them.
        /// A dropped reference does not free them and a destroyed one is still not
        /// <c>null</c>, so this is the question a leak test can actually ask.
        /// </summary>
        private static int CountContentObjects<T>(string[] names) where T : Object
        {
            var found = 0;
            var candidates = Resources.FindObjectsOfTypeAll<T>();
            for (var index = 0; index < candidates.Length; index++)
            {
                var candidate = candidates[index];
                if (candidate == null)
                {
                    continue;
                }

                for (var name = 0; name < names.Length; name++)
                {
                    if (candidate.name == names[name])
                    {
                        found++;
                        break;
                    }
                }
            }

            return found;
        }

        private static ClientUnitState WorldState(TacticalVerticalSliceRunner runner, EntityId entity)
        {
            Assert.That(runner.World.TryGet(entity, out var state), Is.True,
                $"entity {entity.Value} is not in the replicated world");
            return state;
        }

        private static Vector3 WorldMetresOf(TacticalVerticalSliceRunner runner, EntityId entity)
        {
            var state = WorldState(runner, entity);
            return new Vector3(
                state.PosX * UnitView.MillimetresToMetres,
                UnitPickMath.GroundHeightMetres,
                state.PosZ * UnitView.MillimetresToMetres);
        }

        private static Vector3 ViewPosition(TacticalVerticalSliceRunner runner, EntityId entity)
        {
            var slot = SlotOf(runner, entity);
            Assert.That(slot, Is.GreaterThanOrEqualTo(0), $"entity {entity.Value} has no replication slot");
            Assert.That(runner.Binder.TryGetView(slot, out var view) && view.IsBound, Is.True,
                $"entity {entity.Value} has no bound view");
            return view.Hull.position;
        }

        private static Vector2 RadarPixelForMillimetres(Rect rect, int xMillimetres, int zMillimetres)
        {
            var uv = MinimapProjection.MillimetresToUv(MinimapBounds.Default, xMillimetres, zMillimetres);
            return new Vector2(
                rect.xMin + uv.x * rect.width,
                rect.yMin + uv.y * rect.height);
        }

        /// <summary>
        /// The identity a socket would own: the fake transport attaches this session
        /// and the fake uplink stamps its feedback with it, because in a real seam the
        /// connection — not the runner — is what knows both ends of the wire.
        /// </summary>
        private static readonly SessionId RelayedSession = new SessionId(new Guid(
            0x0B100004, 0x0000, 0x0000, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11));

        /// <summary>
        /// An injected downlink that does what a transport does: hands every payload
        /// it is given to whichever sink the test wired to it. The sink is a delegate
        /// rather than a reference to the uplink so the two fakes stay independent
        /// objects, which is the arrangement F-11 exists to prove the runner accepts.
        /// </summary>
        private sealed class RelayTransport : IReplicationTransport
        {
            public Action<ulong, byte[], int> PayloadSink { get; set; }

            /// <summary>Payloads the emitter put on this downlink.</summary>
            public int PayloadCount { get; private set; }

            public event Action<SessionId, MatchId, PlayerId> SessionAttached;

            public event Action<SessionId, MatchId, PlayerId> SessionReattached;

            public event Action<SessionId> PostSessionReattached;

            public event Action<SessionId, TransportDisconnectReason> SessionDetached;

            public event Action<ReplicationFeedbackMessage> FeedbackReceived;

            public bool SendToSession(
                SessionId session,
                ulong envelopeTick,
                byte[] buffer,
                int length)
            {
                if (buffer == null || length < 1 || length > buffer.Length)
                {
                    return false;
                }

                PayloadCount++;
                PayloadSink?.Invoke(envelopeTick, buffer, length);
                return true;
            }

            public void AttachOneClient() =>
                SessionAttached?.Invoke(RelayedSession, default, new PlayerId(1));

            public void ReattachOneClient()
            {
                SessionReattached?.Invoke(RelayedSession, default, new PlayerId(1));
                PostSessionReattached?.Invoke(RelayedSession);
            }

            public void DetachOneClient() =>
                SessionDetached?.Invoke(RelayedSession, TransportDisconnectReason.ClientRequested);

            public void DeliverFeedback(ReplicationFeedbackMessage message) =>
                FeedbackReceived?.Invoke(message);
        }

        /// <summary>
        /// An injected uplink that records the bridge's control records and forwards
        /// them to the sink the test wired. It exposes the snapshot event the bridge
        /// subscribes to, so payloads handed in by the downlink arrive as if a socket
        /// had surfaced them.
        /// </summary>
        private sealed class RelayUplink : IReplicationUplink
        {
            public Action<ReplicationFeedbackKind, byte[], int> FeedbackSink { get; set; }

            /// <summary>Control records the bridge put on this uplink.</summary>
            public int FeedbackCount { get; private set; }

            public event Action<ulong, byte[], int> SnapshotPayloadReceived;

            public bool TrySendFeedback(ReplicationFeedbackKind kind, byte[] payload, int length)
            {
                if (payload == null || length < 1 || length > payload.Length)
                {
                    return false;
                }

                FeedbackCount++;
                FeedbackSink?.Invoke(kind, payload, length);
                return true;
            }

            public void Deliver(ulong envelopeTick, byte[] buffer, int length) =>
                SnapshotPayloadReceived?.Invoke(envelopeTick, buffer, length);
        }

        /// <summary>
        /// A deterministic stand-in for the camera projector: the screen mapping is
        /// a metre-to-pixel offset rather than a lens, so a click lands where the
        /// test says it does. What these tests prove is the composition — driver to
        /// controller to sink to host and back through replication — which is the
        /// same pointer path step 3.5 certified against a real camera.
        /// </summary>
        private sealed class SliceProjector : IUnitPointerProjector
        {
            private const float CentrePixels = 400f;

            public Vector2 ScreenSizePixels => new Vector2(800f, 800f);

            public bool TryGetGroundPoint(Vector2 screenPixels, out Vector3 groundWorld)
            {
                groundWorld = new Vector3(
                    screenPixels.x - CentrePixels,
                    UnitPickMath.GroundHeightMetres,
                    screenPixels.y - CentrePixels);
                return true;
            }

            public Vector3 ProjectToScreen(Vector3 world) =>
                new Vector3(world.x + CentrePixels, 0f, world.z + CentrePixels);

            public Vector2 ScreenForWorld(Vector3 world) =>
                new Vector2(world.x + CentrePixels, world.z + CentrePixels);
        }
    }
}
