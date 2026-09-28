using System.Collections;
using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Presentation;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Tests.PlayMode
{
    /// <summary>
    /// Step 3.7 (part 2): the 400-unit tactical vertical slice driven through real Unity
    /// <c>Update</c>/<c>LateUpdate</c> frames, so the composition part 1 wired is proven
    /// against the frame loop a player gets rather than against
    /// <see cref="TacticalVerticalSliceRunner.StepPresentationFrame"/>, which is what the
    /// EditMode fixture measures.
    ///
    /// Two properties of the runner decide how these tests are written, and both are part
    /// of its design (part 1, "one frame loop"):
    ///
    /// 1. With <c>autoStartRealTime: true</c> the runner steps the host, the replication
    ///    pump, the camera rig, the selection driver, both HUD presenters and the overlay
    ///    batch itself from <c>Update</c>, and leaves the components it drives disabled so
    ///    they cannot step the same mouse and the same clock twice. A test in that mode
    ///    only has to <c>yield return null</c>; calling
    ///    <see cref="TacticalVerticalSliceRunner.StepPresentationFrame"/> as well would
    ///    present the frame a second time.
    /// 2. The host paces itself at <see cref="SimulationConstants.ServerTickRate"/> ticks
    ///    per second of wall clock, and two armies need a few hundred ticks to reach
    ///    contact from the slice's start quadrants, so a real-time fight to first death
    ///    would cost this suite tens of seconds. Only the combat test takes the manual tick
    ///    driver (<c>autoStartRealTime: false</c>, which makes <c>Update</c> inert by
    ///    design) and interleaves its own steps with real frames; every other test here is
    ///    frame-driven.
    ///
    /// Isolation from <see cref="LocalMatchHostPipelinePlayModeTests"/>: every test owns one
    /// <c>GameObject</c> named <c>SlicePlayModeTest</c> and teardown destroys that and
    /// nothing else. The switcher test is the one place the two fixtures genuinely share
    /// state, because <see cref="GlobalFrontRuntimeBootstrap.ActivateTacticalVerticalSlice"/>
    /// takes the legacy prototype down and the runtime bootstrap runs once per Play Mode
    /// session rather than once per scene load — so teardown rebuilds it. Which fixture
    /// Unity runs first is not specified, so the scene is left as it was found either way.
    /// </summary>
    [TestFixture]
    public sealed class TacticalVerticalSlicePlayModeTests
    {
        private const string RuntimeRootName = "[GlobalFront Runtime]";
        private const string PrototypeWorldName = "[GlobalFront Prototype World]";
        private const string SliceTestRootName = "SlicePlayModeTest";

        private const int FullRoster = TacticalVerticalSliceRunner.DefaultUnitCount;
        private const int HalfFullRoster = FullRoster / 2;
        private const int CompactRoster = 24;

        /// <summary>One second of authoritative simulation: 20 ticks, ~7 m of visible travel.</summary>
        private const ulong TickAdvance = (ulong)SimulationConstants.ServerTickRate;

        /// <summary>
        /// Wall-clock ceiling for a wait the host paces itself. <see cref="TickAdvance"/>
        /// ticks is one second of simulation, so this only fires if the frame loop stopped
        /// feeding the tick driver at all.
        /// </summary>
        private const float RealTimeTimeoutSeconds = 20f;

        /// <summary>
        /// Bound on the manual fight. The EditMode attrition fixture measures contact at
        /// well under 700 ticks for the same quadrant layout, so this is that plus the reap
        /// grace and a margin; reaching it is a failure, not a quiet exit.
        /// </summary>
        private const int CombatTickCap = 900;

        /// <summary>Manual ticks between two real frames, so the fight still spans frames.</summary>
        private const int TicksPerFrame = 8;

        /// <summary>Tolerance for a camera focus the minimap set from a projected world point.</summary>
        private const float CameraToleranceMetres = 0.01f;

        private const float TickSeconds = (float)SimulationConstants.ServerTickDurationSeconds;

        private readonly List<TacticalVerticalSliceRunner> _runners =
            new List<TacticalVerticalSliceRunner>();

        private readonly List<GameObject> _sliceRoots = new List<GameObject>();

        /// <summary>Set by the switcher test, which hands the scene's prototype to the slice.</summary>
        private bool _legacyPrototypeTakenDown;

        [TearDown]
        public void TearDown()
        {
            for (var index = 0; index < _runners.Count; index++)
            {
                var runner = _runners[index];
                if (runner != null)
                {
                    runner.Shutdown();
                }
            }

            _runners.Clear();

            for (var index = 0; index < _sliceRoots.Count; index++)
            {
                var root = _sliceRoots[index];
                if (root != null)
                {
                    Object.Destroy(root);
                }
            }

            _sliceRoots.Clear();

            // The slice adopts an existing rig rather than building a second camera and
            // then disables it, because it steps the rig itself. A rig left switched off is
            // a camera the next fixture cannot move, so hand it back running.
            var rig = Object.FindAnyObjectByType<RtsCameraController>();
            if (rig != null && !rig.enabled)
            {
                rig.enabled = true;
            }

            // With createVisuals off the runner pins the shared input manager to mock mode
            // so a measured frame is not steered by the editor's mouse. Hand the mouse back.
            var input = Object.FindAnyObjectByType<RtsInputManager>();
            if (input != null && input.IsMockMode)
            {
                input.ResetMockInputs();
                input.SetMockMode(false);
            }

            if (_legacyPrototypeTakenDown)
            {
                _legacyPrototypeTakenDown = false;
                RestoreLegacyPrototype();
            }
        }

        // ----------------------------------------------------------------
        // 1 — boot and full-roster replication
        // ----------------------------------------------------------------

        [UnityTest]
        [Order(1)]
        public IEnumerator VerticalSlice_400Units_BootsAndReplicatesFullRosterAcrossRealFrames()
        {
            var runner = BuildSlice(FullRoster, autoStartRealTime: true);

            Assert.That(runner.IsInitialized, Is.True);
            Assert.That(runner.World.LiveCount, Is.EqualTo(FullRoster),
                "the client world must hold the whole authoritative roster");
            Assert.That(runner.Binder.BoundViewCount, Is.EqualTo(FullRoster),
                "every replicated unit must own a pooled view before the first frame");
            Assert.That(runner.Binder.DestroyedViewCount, Is.EqualTo(0L),
                "views come from UnitViewPool, so booting 400 of them must destroy none");
            Assert.That(runner.FriendlyAliveCount, Is.EqualTo(HalfFullRoster));
            Assert.That(runner.EnemyAliveCount, Is.EqualTo(HalfFullRoster));

            var bootTick = runner.CurrentTick;
            var bootFrame = Time.frameCount;

            yield return null;
            yield return null;
            yield return null;

            Assert.That(Time.frameCount, Is.GreaterThan(bootFrame),
                "the roster must have been carried across real Unity frames, not a manual loop");
            Assert.That(runner.CurrentTick, Is.GreaterThan(bootTick),
                "the host paces its own ticks from the frame loop in real-time mode");
            Assert.That(runner.RenderTick, Is.GreaterThan(0.0),
                "the interpolation ring must advance with the frames");
            Assert.That(runner.World.LiveCount, Is.EqualTo(FullRoster),
                "and nobody may fall out of the world while the frame loop runs");

            // The radar is a 30 Hz model and the frame loop is the only thing that feeds it
            // in this mode, so this is an assertion a manual-step fixture cannot make.
            Assert.That(runner.RadarModel.RefreshCount, Is.GreaterThan(0),
                "the permanent HUD must have advanced the radar from the frame loop");
            Assert.That(runner.RadarModel.BlipCount, Is.EqualTo(FullRoster),
                "the radar must describe the whole roster");
            Assert.That(runner.RadarModel.SkippedBlipCount, Is.EqualTo(0),
                "a radar sized to the world table has no reason to drop a blip");

            var kinds = CountLiveKinds(runner);
            Assert.That(kinds[UnitKinds.Scout], Is.GreaterThan(0), "the roster must contain scouts");
            Assert.That(kinds[UnitKinds.Tank], Is.GreaterThan(0), "the roster must contain tanks");
            Assert.That(kinds[UnitKinds.BaseStructure], Is.GreaterThan(0),
                "the roster's third archetype is BaseStructure; there is no Artillery row");
            Assert.That(
                kinds[UnitKinds.Scout] + kinds[UnitKinds.Tank] + kinds[UnitKinds.BaseStructure],
                Is.EqualTo(FullRoster),
                "every live unit resolves to one of the three archetypes the client catalogue knows");
        }

        // ----------------------------------------------------------------
        // 2 — real-time ticks, interpolated views, overlay batches
        // ----------------------------------------------------------------

        [UnityTest]
        [Order(2)]
        public IEnumerator VerticalSlice_RealTimeFrameLoop_AdvancesTicksInterpolatesViewsAndBuildsOverlayBatches()
        {
            var runner = BuildSlice(CompactRoster, autoStartRealTime: true);
            var selectedCount = SelectOneLocalUnitOfEachKind(runner);
            Assert.That(runner.Selection.SelectedCount, Is.EqualTo(selectedCount));

            var entity = runner.Selection.GetSelectedEntity(0);
            var slot = runner.Selection.GetSelectedSlot(0);
            Assert.That(runner.Binder.TryGetView(slot, out var view), Is.True,
                "the selected unit's slot must carry a bound view");
            Assert.That(view.IsBound, Is.True);
            Assert.That(view.EntityValue, Is.EqualTo(entity.Value));
            var startPosition = view.Hull.position;

            Assert.That(runner.IssueConvergenceMarch(), Is.GreaterThan(0),
                "both armies must take the march order");

            var startTick = runner.CurrentTick;
            yield return WaitForRealTime(runner, startTick + TickAdvance);

            Assert.That(runner.CurrentTick, Is.GreaterThanOrEqualTo(startTick + TickAdvance));
            Assert.That(runner.RenderTick, Is.GreaterThan(0.0),
                "the render clock must have left its prime, or no pose could be sampled");
            Assert.That(runner.World.TryGet(entity, out var marched), Is.True);
            Assert.That(marched.HasMoveTarget, Is.True,
                "the march must reach the client world, not only the host");

            var destination = new WorldPointMm(marched.MoveTargetX, marched.MoveTargetZ);
            var movedPosition = view.Hull.position;
            Assert.That(movedPosition, Is.Not.EqualTo(startPosition),
                "an interpolated view must follow the replication stream across frames");
            Assert.That(movedPosition.y, Is.EqualTo(0f),
                "the playfield is flat by contract (OD-27), so a pose can never carry height");
            Assert.That(
                SquaredPlanarDistance(movedPosition, destination),
                Is.LessThan(SquaredPlanarDistance(startPosition, destination)),
                "and it must travel toward the destination the server was told to march to");

            Assert.That(runner.OverlayBatcher.SelectionRingCount, Is.EqualTo(selectedCount),
                "one ground ring per selected unit, rebuilt by the frame loop");
            Assert.That(runner.OverlayBatcher.HealthBarCount, Is.EqualTo(selectedCount),
                "selected units carry a bar, and 20 ticks is nowhere near contact, so " +
                "the only bars in the batch belong to the selection");
        }

        // ----------------------------------------------------------------
        // 3 — selection HUD and canvases across frames
        // ----------------------------------------------------------------

        [UnityTest]
        [Order(3)]
        public IEnumerator VerticalSlice_SelectionAndHudCanvases_UpdateAcrossRealFrames()
        {
            var runner = BuildSlice(CompactRoster, autoStartRealTime: true);

            Assert.That(runner.PermanentHud.IsBuilt, Is.True);
            Assert.That(runner.SelectionHud.IsBuilt, Is.True);
            Assert.That(runner.MarqueePresenter.IsBuilt, Is.True);
            Assert.That(runner.PointerBlocker.UsedCount, Is.GreaterThan(0),
                "the minimap, command bar and selection panel must all block the pointer");

            var selectedCount = SelectOneLocalUnitOfEachKind(runner);
            yield return null;
            yield return null;

            var model = runner.SelectionHudModel;
            Assert.That(model.SelectedCount, Is.EqualTo(selectedCount),
                "the panel model is refreshed by the frame loop, not by the caller");
            Assert.That(model.GetKindCount(UnitKinds.Scout), Is.EqualTo(1));
            Assert.That(model.GetKindCount(UnitKinds.Tank), Is.EqualTo(1));
            Assert.That(model.GetKindCount(UnitKinds.BaseStructure), Is.EqualTo(1));
            Assert.That(model.HealthPercent, Is.EqualTo(100),
                "nobody has been in range yet, so the mean health is full");

            Assert.That(runner.SelectionHud.CountValue.text, Is.EqualTo("3"));
            Assert.That(runner.SelectionHud.DisplayedCount, Is.EqualTo(model.CountText));
            Assert.That(runner.SelectionHud.HealthValue.text, Is.EqualTo("100%"));
            Assert.That(runner.SelectionHud.DisplayedHealth, Is.EqualTo(model.HealthText));
            Assert.That(runner.SelectionHud.GetKindValue(UnitKinds.Tank).text, Is.EqualTo("1"));
            Assert.That(
                runner.SelectionHud.DisplayedKindCount(UnitKinds.BaseStructure), Is.EqualTo("1"));
            Assert.That(runner.PermanentHud.HeaderValueText.text, Is.Not.Empty,
                "the permanent header is a live Text on a live canvas, not a plain field");

            // A steady frame must not rewrite the canvas: the panel is dirty-checked, and
            // the frames above are the only writes there are to count.
            var mutations = runner.SelectionHud.TextMutationCount;
            yield return null;
            yield return null;
            Assert.That(runner.SelectionHud.TextMutationCount, Is.EqualTo(mutations),
                "an unchanged selection must not touch a single Text");
        }

        // ----------------------------------------------------------------
        // 4 — minimap: the left button looks, the right button sends
        // ----------------------------------------------------------------

        [UnityTest]
        [Order(4)]
        public IEnumerator VerticalSlice_MinimapInteraction_LeftClickPansCamera_RightClickIssuesMoveOrderAcrossFrames()
        {
            var runner = BuildSlice(CompactRoster, autoStartRealTime: true);
            var minimap = runner.MinimapInteraction;
            var input = runner.InputManager;
            Assert.That(input.IsMockMode, Is.True, "createVisuals off pins the input to mock mode");

            yield return null;

            var rect = minimap.MinimapScreenRect;
            if (rect.width <= 0f || rect.height <= 0f)
            {
                // The rectangle normally arrives from the laid-out minimap RectTransform,
                // handed over by the permanent HUD on each presentation frame. A batchmode
                // -nographics player has no screen for a canvas to lay out, so the frame
                // loop has no rectangle to offer; declaring the widget's own rectangle
                // leaves the mapping under test the one a real HUD produces.
                minimap.SetMinimapScreenRect(new Rect(24f, 24f, 176f, 176f));
                rect = minimap.MinimapScreenRect;
            }

            Assert.That(rect.width, Is.GreaterThan(0f));
            Assert.That(rect.height, Is.GreaterThan(0f));

            // The radar is reached through the HUD gate, which normally reads the same panel
            // rectangle. Registering it as a blocking rect is what a laid-out canvas does,
            // and it is also what keeps the battlefield marquee from eating the click.
            runner.PointerBlocker.AddScreenRect(rect);

            var lookPixel = new Vector2(
                rect.xMin + rect.width * 0.25f,
                rect.yMin + rect.height * 0.75f);
            Assert.That(
                minimap.TryMapScreenToWorldMetres(lookPixel, out var expectedFocus), Is.True,
                "the pixel the test clicks must be inside the radar");
            Assert.That(
                runner.PointerBlocker.IsPointerOverHud(lookPixel), Is.True,
                "the radar must be the half of the screen the battlefield is not");

            var focusBefore = runner.CameraController.FocusPoint;
            input.SetMockMousePosition(lookPixel);
            input.SetMockMouseButton(0, true, true, false);
            yield return null;
            input.SetMockMouseButton(0, false, false, true);
            yield return null;
            input.ResetMockInputs();

            Assert.That(minimap.IsDraggingCameraOnMinimap, Is.False,
                "the release must end the radar drag it started");
            Assert.That(minimap.LastCameraTargetMetres, Is.EqualTo(expectedFocus),
                "the radar must record the point the left button looked at");
            Assert.That(
                PlanarDistance(runner.CameraController.FocusPoint, expectedFocus),
                Is.LessThan(CameraToleranceMetres),
                "the rig must move to the radar point through the driver and the minimap, " +
                "not to somewhere the test guessed");
            Assert.That(
                PlanarDistance(focusBefore, expectedFocus),
                Is.GreaterThan(1f),
                "the click must be far enough from the opening view to be a real move");

            var entity = SelectLocalUnitOfKind(runner, UnitKinds.Scout);
            Assert.That(runner.World.TryGet(entity, out var selected), Is.True);
            Assert.That(selected.Owner, Is.EqualTo(runner.LocalPlayer),
                "only the local player's units may be ordered");

            var sendPixel = new Vector2(
                rect.xMin + rect.width * 0.7f,
                rect.yMin + rect.height * 0.3f);
            Assert.That(
                minimap.TryMapScreenToMillimetres(sendPixel, out var expectedDestination), Is.True);
            Assert.That(runner.PointerBlocker.IsPointerOverHud(sendPixel), Is.True,
                "the right button only reaches the radar through the panel gate");

            var submittedBefore = runner.CommandSink.SubmittedCount;
            input.SetMockMousePosition(sendPixel);
            input.SetMockMouseButton(1, false, false, true);
            yield return null;
            input.ResetMockInputs();

            Assert.That(runner.CommandSink.SubmittedCount, Is.EqualTo(submittedBefore + 1),
                "the right button on the radar must hand exactly one order to the channel");
            Assert.That(runner.CommandSink.LastKind, Is.EqualTo(GameCommandType.Move),
                "and it must be a move");
            Assert.That(minimap.LastMoveDestinationMm, Is.EqualTo(expectedDestination),
                "to the map coordinate the clicked pixel stands for");

            // Across the following frames the order has to cross the loopback and change the
            // authoritative roster, which is what an EditMode fixture cannot show.
            var startTick = runner.CurrentTick;
            yield return WaitForRealTime(runner, startTick + TickAdvance);

            Assert.That(runner.Host.TryGetUnit(entity, out var authoritative), Is.True);
            Assert.That(
                authoritative.HasMoveTarget || authoritative.Position != selected.Position,
                Is.True,
                "the authoritative server must have taken the radar order: it either still " +
                "holds the destination or has already started marching to it");
        }

        // ----------------------------------------------------------------
        // 5 — combat, corpses and the reaper, across frames
        // ----------------------------------------------------------------

        [UnityTest]
        [Order(5)]
        public IEnumerator VerticalSlice_CombatAndCorpseReaper_DamagesAndReapsDeadUnitsAcrossRealFrames()
        {
            // Manual ticks, real frames: see the class summary for why the fight alone
            // cannot be afforded at 20 ticks per second of wall clock.
            var runner = BuildSlice(CompactRoster, autoStartRealTime: false);
            var initialBoundViews = runner.Binder.BoundViewCount;
            Assert.That(initialBoundViews, Is.EqualTo(CompactRoster));
            var healthAtStart = SnapshotHealth(runner);

            Assert.That(runner.IssueConvergenceMarch(), Is.GreaterThan(0),
                "the armies have to meet before anything can shoot");

            // Deliberately one-sided: a move order clears automatic acquisition, so if both
            // armies were told to fire the local squad could survive the whole fight and
            // "a corpse leaves the world" would pass without ever having a death.
            Assert.That(
                runner.IssueEngagementOrders(forLocalArmy: false, forEnemyArmy: true),
                Is.GreaterThan(0),
                "a marched army only starts shooting once it is given an attack order");

            var ticks = 0;
            var peakHealthBars = 0;
            var peakWounded = 0;
            var presentationClock = 5.0;
            var frameCount = Time.frameCount;

            while (ticks < CombatTickCap &&
                   (runner.ReapedCorpseCount == 0 ||
                    runner.Binder.BoundViewCount >= initialBoundViews))
            {
                runner.StepSimulationTick();
                ticks++;

                presentationClock += (double)SimulationConstants.ServerTickDurationSeconds;
                runner.StepPresentationFrame(TickSeconds, presentationClock);

                peakHealthBars = Mathf.Max(peakHealthBars, runner.OverlayBatcher.HealthBarCount);
                peakWounded = Mathf.Max(peakWounded, CountWoundedUnits(runner, healthAtStart));

                if (ticks % TicksPerFrame == 0)
                {
                    yield return null;
                }
            }

            Assert.That(runner.ReapedCorpseCount, Is.GreaterThan(0),
                $"the fight must kill somebody inside {CombatTickCap} ticks " +
                $"(ticks run: {ticks}, removals applied: {runner.World.DestroyedRemoveCount})");
            Assert.That(peakHealthBars, Is.GreaterThan(0),
                "a wounded unit must draw a health bar while it is still in the world");
            Assert.That(peakWounded, Is.GreaterThan(0),
                "and the authoritative damage must reach the replicated world, not only the bars");
            Assert.That(Time.frameCount, Is.GreaterThan(frameCount),
                "the fight must have spanned real Unity frames, not only a tick loop");
            Assert.That(runner.World.DestroyedRemoveCount, Is.GreaterThan(0),
                "a reaped corpse must leave the replicated world as a removal");
            Assert.That(runner.Binder.BoundViewCount, Is.LessThan(initialBoundViews),
                "so the binder holds fewer views than the roster it started with");
            Assert.That(runner.Binder.DestroyedViewCount, Is.EqualTo(0L),
                "and every one of them went back to UnitViewPool rather than being destroyed");
            Assert.That(runner.Binder.BoundViewCount, Is.EqualTo(runner.World.LiveCount),
                "one bound view per live unit: no orphan left on the pool root, no gap");
            Assert.That(
                runner.FriendlyAliveCount + runner.EnemyAliveCount,
                Is.LessThan(CompactRoster),
                "attrition has to show in the counts the permanent HUD reads");

            // The reap grace is the reason a corpse is still in the world on the tick it
            // died; run a couple more captures per grace so the removal is not being
            // asserted the instant it became possible.
            var settleTicks = CorpseReapSnapshotSource.DefaultGraceCaptures * 2;
            for (var settle = 0; settle < settleTicks; settle++)
            {
                runner.StepSimulationTick();
                presentationClock += (double)SimulationConstants.ServerTickDurationSeconds;
                runner.StepPresentationFrame(TickSeconds, presentationClock);
                yield return null;
            }

            Assert.That(runner.ReapedCorpseCount, Is.GreaterThan(0));
            Assert.That(runner.World.DestroyedRemoveCount, Is.GreaterThan(0));
        }

        // ----------------------------------------------------------------
        // 6 — the switcher, last because it edits the shared scene
        // ----------------------------------------------------------------

        [UnityTest]
        [Order(100)]
        public IEnumerator ActivateTacticalVerticalSlice_ReplacesLegacyPrototypeAndRunsRealTimeFrames()
        {
            Assert.That(
                Object.FindAnyObjectByType<PrototypeRtsController>() != null, Is.True,
                "the scene must still carry the legacy prototype this call replaces");

            // Set before the call: the switcher takes the prototype down on its first line,
            // so a failure anywhere below still leaves the scene with a teardown that knows
            // what to put back.
            _legacyPrototypeTakenDown = true;

            var runner = GlobalFrontRuntimeBootstrap.ActivateTacticalVerticalSlice(
                FullRoster,
                issueInitialConvergenceMarch: true,
                createVisuals: false);

            Assert.That(runner, Is.Not.Null);
            _runners.Add(runner);
            _sliceRoots.Add(runner.gameObject);
            Assert.That(runner.IsInitialized, Is.True);
            Assert.That(runner.World.LiveCount, Is.EqualTo(FullRoster));
            Assert.That(runner.Binder.BoundViewCount, Is.EqualTo(FullRoster));
            Assert.That(runner.Binder.DestroyedViewCount, Is.EqualTo(0L));

            var bootFrame = Time.frameCount;
            yield return null;
            yield return null;
            yield return null;

            Assert.That(GameObject.Find(GlobalFrontRuntimeBootstrap.TacticalSliceRootName) != null,
                Is.True, "the slice must be a live scene root after the switch");
            Assert.That(
                GameObject.Find(PrototypeWorldName) == null, Is.True,
                "Object.Destroy runs at the end of a frame, so three real frames later the " +
                "40-unit prototype world must be gone");
            Assert.That(
                Object.FindAnyObjectByType<PrototypeRtsController>() == null, Is.True,
                "and the legacy controller with it: two hosts pacing two matches into one " +
                "camera is not a switch");
            Assert.That(Object.FindAnyObjectByType<PrototypeHud>() == null, Is.True);
            Assert.That(Object.FindAnyObjectByType<PrototypeWorldBootstrap>() == null, Is.True);

            Assert.That(Time.frameCount, Is.GreaterThan(bootFrame));
            Assert.That(runner.CurrentTick, Is.GreaterThan(0ul),
                "the slice the switcher returns drives itself");

            // Read the orders off the authoritative roster rather than off
            // CommandSink: the slice's own army orders go through the host's session
            // command path, and the sink counts what the player's pointer submits.
            var roster = runner.Host.GetAllSnapshots();
            Assert.That(roster.Length, Is.EqualTo(FullRoster));

            var marching = 0;
            var engaged = 0;
            for (var index = 0; index < roster.Length; index++)
            {
                if (roster[index].HasMoveTarget)
                {
                    marching++;
                }

                if (roster[index].AttackTarget.IsValid)
                {
                    engaged++;
                }
            }

            Assert.That(marching, Is.GreaterThan(0),
                "issueInitialConvergenceMarch must have ordered the armies together: not one " +
                "unit holds a move destination");
            Assert.That(engaged, Is.GreaterThan(0),
                "and engagement must follow the march. A move order clears automatic " +
                "acquisition, so a slice that only marched would park two silent lines facing " +
                "each other fourteen metres apart");
            Assert.That(runner.FriendlyAliveCount, Is.GreaterThan(0));
            Assert.That(runner.EnemyAliveCount, Is.GreaterThan(0));
        }

        // ----------------------------------------------------------------
        // 7 — re-arming twice inside one frame
        // ----------------------------------------------------------------

        /// <summary>
        /// What only a real frame loop can show: <c>Object.Destroy</c> is deferred to the end
        /// of a Play Mode frame, so a switch that only destroys leaves the slice it replaced
        /// enabled for the rest of this one and its root findable by the name the next switch
        /// looks up.
        /// </summary>
        [UnityTest]
        [Order(101)]
        public IEnumerator ActivateTacticalVerticalSlice_TwiceInOneFrame_TakesThePreviousSliceDownAtOnce()
        {
            Assert.That(
                Object.FindAnyObjectByType<PrototypeRtsController>() != null, Is.True,
                "the scene must still carry the legacy prototype this call replaces");

            // Set before the call, as in the switcher test: a failure below must still
            // leave the scene with a teardown that knows what to put back.
            _legacyPrototypeTakenDown = true;

            var first = GlobalFrontRuntimeBootstrap.ActivateTacticalVerticalSlice(
                CompactRoster,
                issueInitialConvergenceMarch: false,
                createVisuals: false);
            var firstRoot = first.gameObject;
            _runners.Add(first);
            _sliceRoots.Add(firstRoot);

            var second = GlobalFrontRuntimeBootstrap.ActivateTacticalVerticalSlice(
                CompactRoster,
                issueInitialConvergenceMarch: false,
                createVisuals: false);
            _runners.Add(second);
            _sliceRoots.Add(second.gameObject);

            Assert.That(first.IsInitialized, Is.False,
                "the replaced slice must be shut down on the way out, not merely scheduled " +
                "for a destroy that runs at the end of the frame");
            Assert.That(first.Host, Is.Null);
            Assert.That(first.enabled, Is.False,
                "and switched off, because its Update still runs until it is destroyed");
            Assert.That(
                GameObject.Find(GlobalFrontRuntimeBootstrap.TacticalSliceRootName) ==
                second.gameObject,
                Is.True,
                "a dying root that still answers to the slice's name is a root the next switch " +
                "would find and replace a second time");
            Assert.That(firstRoot.name, Does.Contain("[Released]"));
            Assert.That(second.IsInitialized, Is.True);

            var legacyController = Object.FindAnyObjectByType<PrototypeRtsController>();
            Assert.That(legacyController, Is.Not.Null,
                "the legacy prototype is still in this frame; the assertions below are about " +
                "what the switch has already done to it");
            Assert.That(legacyController.enabled, Is.False,
                "a prototype HUD controller left enabled spends the rest of the frame " +
                "registering units into a world the slice just replaced");

            yield return null;
            yield return null;

            Assert.That(firstRoot == null, Is.True,
                "two frames later the replaced root must really be gone");
            Assert.That(GameObject.Find(PrototypeWorldName) == null, Is.True);
            Assert.That(Object.FindAnyObjectByType<PrototypeRtsController>() == null, Is.True);
            Assert.That(Object.FindAnyObjectByType<PrototypeHud>() == null, Is.True);
            Assert.That(second.IsInitialized, Is.True, "and the slice that survived keeps running");
        }

        // ----------------------------------------------------------------
        // helpers
        // ----------------------------------------------------------------

        private TacticalVerticalSliceRunner BuildSlice(int unitCount, bool autoStartRealTime)
        {
            var root = new GameObject(SliceTestRootName);
            _sliceRoots.Add(root);

            var runner = root.AddComponent<TacticalVerticalSliceRunner>();
            _runners.Add(runner);
            runner.Initialize(
                unitCount,
                createVisuals: false,
                autoStartRealTime: autoStartRealTime,
                enemyAutoAcquire: true);
            return runner;
        }

        /// <summary>
        /// Waits for the host to reach <paramref name="targetTick"/> on its own, then one
        /// further frame, so the presentation for the last replicated packet has run.
        /// </summary>
        private IEnumerator WaitForRealTime(TacticalVerticalSliceRunner runner, ulong targetTick)
        {
            var elapsed = 0f;
            while (runner.CurrentTick < targetTick && elapsed < RealTimeTimeoutSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            Assert.That(runner.CurrentTick, Is.GreaterThanOrEqualTo(targetTick),
                $"the frame loop did not carry the host to tick {targetTick} within " +
                $"{RealTimeTimeoutSeconds} seconds; it reached {runner.CurrentTick}");

            yield return null;
        }

        /// <summary>
        /// Selects one living local unit of each Phase 3 archetype: the first through
        /// <see cref="UnitSelectionController.SelectSingle"/>, the rest through
        /// <see cref="UnitSelectionController.AddSelected"/>.
        /// </summary>
        /// <returns>How many units the selection now holds.</returns>
        private static int SelectOneLocalUnitOfEachKind(TacticalVerticalSliceRunner runner)
        {
            var selected = 0;
            for (var kind = UnitKinds.Scout; kind < UnitKinds.Count; kind++)
            {
                var entity = FindLocalUnitOfKind(runner, (byte)kind, out var slot);
                if (selected == 0)
                {
                    runner.Selection.SelectSingle(entity, slot);
                }
                else
                {
                    runner.Selection.AddSelected(entity, slot);
                }

                selected++;
            }

            Assert.That(runner.Selection.SelectedCount, Is.EqualTo(selected),
                "every archetype must be selectable while it is alive");
            return selected;
        }

        /// <summary>Selects exactly one living local unit of one archetype.</summary>
        private static CoreEntityId SelectLocalUnitOfKind(
            TacticalVerticalSliceRunner runner,
            byte kind)
        {
            var entity = FindLocalUnitOfKind(runner, kind, out var slot);
            runner.Selection.SelectSingle(entity, slot);
            return entity;
        }

        /// <summary>
        /// The first living unit of <paramref name="kind"/> owned by the local player, with
        /// the world slot it sits in — the pair the selection API asks for, because the
        /// binder's table is keyed by slot rather than by entity.
        /// </summary>
        private static CoreEntityId FindLocalUnitOfKind(
            TacticalVerticalSliceRunner runner,
            byte kind,
            out int slot)
        {
            var world = runner.World;
            for (var candidate = 0; candidate < world.Capacity; candidate++)
            {
                if (!world.TryGetSlotState(candidate, out var state) ||
                    !state.IsLive ||
                    state.UnitKind != kind ||
                    state.Owner != runner.LocalPlayer)
                {
                    continue;
                }

                slot = candidate;
                return state.Entity;
            }

            slot = -1;
            Assert.Fail($"the slice has no living local unit of archetype {kind}");
            return default;
        }

        /// <summary>The world-slot health of every live unit, taken before a fight starts.</summary>
        private static int[] SnapshotHealth(TacticalVerticalSliceRunner runner)
        {
            var world = runner.World;
            var health = new int[world.Capacity];
            for (var slot = 0; slot < world.Capacity; slot++)
            {
                if (world.TryGetSlotState(slot, out var state) && state.IsLive)
                {
                    health[slot] = state.Health;
                }
            }

            return health;
        }

        /// <summary>Live units the fight has cost hit points, read against <see cref="SnapshotHealth"/>.</summary>
        private static int CountWoundedUnits(TacticalVerticalSliceRunner runner, int[] healthAtStart)
        {
            var world = runner.World;
            var wounded = 0;
            for (var slot = 0; slot < world.Capacity; slot++)
            {
                if (healthAtStart[slot] > 0 &&
                    world.TryGetSlotState(slot, out var state) &&
                    state.IsLive &&
                    state.Health < healthAtStart[slot])
                {
                    wounded++;
                }
            }

            return wounded;
        }

        private static int[] CountLiveKinds(TacticalVerticalSliceRunner runner)
        {
            var kinds = new int[UnitKinds.Count];
            var world = runner.World;
            for (var slot = 0; slot < world.Capacity; slot++)
            {
                if (world.TryGetSlotState(slot, out var state) && state.IsLive)
                {
                    kinds[state.UnitKind]++;
                }
            }

            return kinds;
        }

        private static float PlanarDistance(Vector3 left, Vector3 right)
        {
            var dx = left.x - right.x;
            var dz = left.z - right.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float SquaredPlanarDistance(Vector3 from, WorldPointMm target)
        {
            var dx = from.x - target.X * UnitView.MillimetresToMetres;
            var dz = from.z - target.Z * UnitView.MillimetresToMetres;
            return dx * dx + dz * dz;
        }

        /// <summary>
        /// <see cref="GlobalFrontRuntimeBootstrap"/> runs once per Play Mode session rather
        /// than once per scene load, so nothing else in the run would put the prototype back
        /// after the switcher took it down. Rebuild the trio the scene boot built, in the
        /// order it built them, on the root that survived the switch — the switcher keeps
        /// <c>[GlobalFront Runtime]</c> because the <see cref="RtsInputManager"/> it carries
        /// is the one the slice's rig and selection driver read.
        /// </summary>
        private static void RestoreLegacyPrototype()
        {
            if (Object.FindAnyObjectByType<PrototypeRtsController>() != null)
            {
                return;
            }

            var runtime = GameObject.Find(RuntimeRootName);
            if (runtime == null)
            {
                return;
            }

            runtime.AddComponent<PrototypeWorldBootstrap>();
            runtime.AddComponent<PrototypeRtsController>();
            runtime.AddComponent<PrototypeHud>();
        }
    }
}
