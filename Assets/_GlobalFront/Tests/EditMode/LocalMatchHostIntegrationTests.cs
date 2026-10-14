using System.Collections.Generic;
using System.Reflection;
using GlobalFront.Client;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Server;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using CoreEntityId = GlobalFront.Core.Model.EntityId;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Integration tests for the authoritative <see cref="LocalMatchHost"/>
    /// wiring inside <see cref="PrototypeRtsController"/> after Phase 2.3
    /// (ADR-007). These verify that the controller initializes the host,
    /// registers every client unit with a matching entity id, advances the
    /// host-owned <see cref="TickDriver"/> tick by tick, and synchronizes
    /// presentation units from authoritative snapshots on
    /// <see cref="LocalMatchHost.TickCompleted"/>. The controller no longer
    /// owns the authoritative tick: it is driven by the host's driver, and
    /// these tests exercise that exact path through
    /// <see cref="LocalMatchHost.TickOnce"/>.
    /// </summary>
    public sealed class LocalMatchHostIntegrationTests
    {
        private static readonly PlayerId LocalPlayer = new PlayerId(1);
        private static readonly PlayerId EnemyPlayer = new PlayerId(2);

        private readonly List<GameObject> _createdObjects = new List<GameObject>();
        private PrototypeRtsController _controller;
        private GameObject _controllerObject;

        [SetUp]
        public void SetUp()
        {
            _controllerObject = new GameObject("Test Controller");
            _createdObjects.Add(_controllerObject);
            _controller = _controllerObject.AddComponent<PrototypeRtsController>();

            // EditMode does not run the MonoBehaviour lifecycle (Awake/Start)
            // for AddComponent, so the controller's Awake must be invoked
            // explicitly to initialize _selection and _commandQueue before
            // the tick methods are exercised.
            InvokePrivateMethod(_controller, "Awake");

            // Spawn units WITHOUT EntityIds — InitializeLocalHost will
            // assign server-authoritative EntityIds via MatchConfig.
            SpawnUnit(LocalPlayer, new Vector3(0f, 1.1f, 0f));
            SpawnUnit(EnemyPlayer, new Vector3(5f, 1.1f, 0f));

            // InitializeLocalHost discovers unassigned units, builds
            // MatchConfig, calls InitializeMatch (server assigns EntityIds),
            // assigns them to presentation units, refreshes the registry and
            // switches the host tick driver to real-time pacing.
            InvokePrivateMethod(_controller, "InitializeLocalHost");
            InvokePrivateMethod(_controller, "UpdateRosterCounts");

            // The integration tests drive ticks deterministically, so return
            // the driver to manual mode.
            _controller.Host.StopRealTime();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _createdObjects)
            {
                if (obj != null)
                {
                    Object.DestroyImmediate(obj);
                }
            }

            _createdObjects.Clear();
            _controller = null;
            _controllerObject = null;
        }

        [Test]
        public void LocalMatchHost_IsActiveAfterInitialization()
        {
            Assert.That(_controller.HostActive, Is.True);
            Assert.That(_controller.Host, Is.Not.Null);
        }

        [Test]
        public void LocalMatchHost_OwnsTickDriverInRealTimeModeAfterInitialization()
        {
            // InitializeLocalHost leaves the driver in real-time mode for
            // the runtime loop; the tests opt back into manual mode above.
            Assert.That(_controller.Host.TickDriver, Is.Not.Null);
            Assert.That(_controller.Host.TickDriverMode, Is.EqualTo(TickDriverMode.Manual));
        }

        [Test]
        public void LocalMatchHost_UnitCountMatchesRegistry()
        {
            Assert.That(_controller.HostUnitCount, Is.EqualTo(2));
        }

        [Test]
        public void LocalMatchHost_EveryClientUnitRegisteredWithMatchingEntityId()
        {
            var host = _controller.Host;
            Assert.That(host.TryGetUnit(new CoreEntityId(1), out _), Is.True);
            Assert.That(host.TryGetUnit(new CoreEntityId(2), out _), Is.True);
        }

        [Test]
        public void LocalMatchHost_RegisteredOwnerMatchesClient()
        {
            var host = _controller.Host;
            Assert.That(host.TryGetUnit(new CoreEntityId(1), out var friendly), Is.True);
            Assert.That(friendly.Owner, Is.EqualTo(LocalPlayer));

            Assert.That(host.TryGetUnit(new CoreEntityId(2), out var enemy), Is.True);
            Assert.That(enemy.Owner, Is.EqualTo(EnemyPlayer));
        }

        [Test]
        public void LocalMatchHost_RegisteredPositionMatchesClient()
        {
            var host = _controller.Host;
            Assert.That(host.TryGetUnit(new CoreEntityId(1), out var friendly), Is.True);
            Assert.That(friendly.Position, Is.EqualTo(new WorldPointMm(0, 0)));

            Assert.That(host.TryGetUnit(new CoreEntityId(2), out var enemy), Is.True);
            Assert.That(enemy.Position, Is.EqualTo(new WorldPointMm(5000, 0)));
        }

        [Test]
        public void LocalMatchHost_RegisteredHealthMatchesClient()
        {
            var host = _controller.Host;
            Assert.That(host.TryGetUnit(new CoreEntityId(1), out var friendly), Is.True);
            Assert.That(friendly.CurrentHealth, Is.EqualTo(100));

            Assert.That(host.TryGetUnit(new CoreEntityId(2), out var enemy), Is.True);
            Assert.That(enemy.CurrentHealth, Is.EqualTo(100));
        }

        [Test]
        public void HostTick_AdvancesHostByExactlyOneServerTick()
        {
            Assert.That(_controller.Host.CurrentTick, Is.EqualTo(0ul));

            Assert.That(_controller.Host.TickOnce(), Is.True);
            Assert.That(_controller.Host.CurrentTick, Is.EqualTo(1ul));

            Assert.That(_controller.Host.TickOnce(), Is.True);
            Assert.That(_controller.Host.CurrentTick, Is.EqualTo(2ul));
        }

        [Test]
        public void HostTick_SynchronizesPresentationFromServerSnapshots()
        {
            var host = _controller.Host;
            Assert.That(
                host.TryEnqueueMove(
                    new CommandHeader(LocalPlayer, 1, 1, GameCommandType.Move),
                    new[] { new CoreEntityId(1) },
                    new WorldPointMm(10000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));

            var friendly = GetUnit(new CoreEntityId(1));
            Assert.That(friendly.CurrentPosition, Is.EqualTo(new WorldPointMm(0, 0)));

            // One host tick: the driver schedules it, the command requested
            // for tick 1 is forwarded, the server simulates, and the
            // controller's TickCompleted handler synchronizes presentation.
            host.TickOnce();

            Assert.That(
                host.TryGetUnit(new CoreEntityId(1), out var snapshot),
                Is.True);
            Assert.That(friendly.CurrentPosition, Is.EqualTo(snapshot.Position));
            Assert.That(friendly.CurrentPosition, Is.EqualTo(new WorldPointMm(350, 0)));
        }

        /// <summary>
        /// Witness for Phase 1.0 sprint 5: the presentation read runs once per tick for
        /// the whole roster, and <c>GetAllSnapshots</c> handed it a fresh array every
        /// time. It now copies into a buffer owned by the controller.
        /// </summary>
        [Test]
        public void ApplyHostSnapshots_ReadsTheReusableBufferWithoutAllocating()
        {
            var host = _controller.Host;
            Assert.That(
                host.TryEnqueueMove(
                    new CommandHeader(LocalPlayer, 1, 1, GameCommandType.Move),
                    new[] { new CoreEntityId(1) },
                    new WorldPointMm(10000, 0),
                    new FormationSpec(1, 2000, CardinalFacing.North)),
                Is.EqualTo(MatchCommandRejection.None));

            host.TickOnce();
            var friendly = GetUnit(new CoreEntityId(1));

            // Anti-vacuity: a silently renamed private method would be skipped by the
            // reflection helper and would also "allocate nothing", so prove first that
            // the path under test really moved the unit.
            Assert.That(
                friendly.CurrentPosition,
                Is.EqualTo(new WorldPointMm(350, 0)),
                "the measured tick did not reach presentation through the snapshot path");

            // Bind once, outside the measurement window: MethodInfo.Invoke allocates
            // per call, which would indict the code under test for the harness.
            var applyHostSnapshots = PrivateAction(_controller, "ApplyHostSnapshots");

            Assert.That(
                () => applyHostSnapshots(),
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "the per-tick presentation read allocates again instead of copying into " +
                "the controller's reusable snapshot buffer");
        }

        /// <summary>
        /// Pins the host's new zero-alloc snapshot contract, including the arm the
        /// presentation layer relies on never happening: its buffer is exactly the
        /// server's own spawn cap, so the server can never report that it did not fit.
        /// </summary>
        [Test]
        public void HostCopySnapshots_ReportsTheWholeRosterAndRefusesATooSmallBuffer()
        {
            var host = _controller.Host;
            Assert.That(host.UnitCount, Is.GreaterThan(0));

            var buffer = new ServerUnitSnapshot[SimulationConstants.MaxUnitsPerMatch];
            Assert.That(host.CopySnapshots(buffer), Is.EqualTo(host.UnitCount));
            Assert.That(host.TryGetUnit(buffer[0].Entity, out _), Is.True);

            Assert.That(
                host.CopySnapshots(new ServerUnitSnapshot[host.UnitCount - 1]),
                Is.EqualTo(-1),
                "CopySnapshots must report that it copied nothing rather than half a roster");
        }

        /// <summary>
        /// Witness for Phase 1.0 sprint 5: a team victory deliberately carries no
        /// winning player, so comparing <c>Winner</c> against the local player reported
        /// DEFEAT to the side that actually won.
        /// </summary>
        [Test]
        public void BattleStatusMessage_TeamVictory_UsesTheWinningTeam()
        {
            SetPrivateField(_controller, "_localPlayer", LocalPlayer);

            SetPrivateField(
                _controller,
                "_outcome",
                BattleOutcome.TeamVictory(MatchTopology2v2.TeamRed));
            Assert.That(
                _controller.BattleStatusMessage,
                Is.EqualTo("VICTORY"),
                "player 1 won on the winning team but the banner reported defeat");

            SetPrivateField(
                _controller,
                "_outcome",
                BattleOutcome.TeamVictory(MatchTopology2v2.TeamBlue));
            Assert.That(
                _controller.BattleStatusMessage,
                Is.EqualTo("DEFEAT"),
                "the opposing team won and the local player is not on it");

            // The player-victory arms must keep working: a team fix that made every
            // outcome read through WinningTeam would report VICTORY for an enemy win.
            SetPrivateField(_controller, "_outcome", BattleOutcome.Victory(EnemyPlayer));
            Assert.That(_controller.BattleStatusMessage, Is.EqualTo("DEFEAT"));

            SetPrivateField(_controller, "_outcome", BattleOutcome.Victory(LocalPlayer));
            Assert.That(_controller.BattleStatusMessage, Is.EqualTo("VICTORY"));

            SetPrivateField(_controller, "_outcome", BattleOutcome.Draw);
            Assert.That(_controller.BattleStatusMessage, Is.EqualTo("DRAW"));

            SetPrivateField(_controller, "_outcome", BattleOutcome.InProgress);
            Assert.That(_controller.BattleStatusMessage, Is.EqualTo("BATTLE IN PROGRESS"));
        }

        private static void SetPrivateField(object target, string name, object value)
        {
            var field = target.GetType().GetField(
                name,
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(
                field,
                Is.Not.Null,
                $"{target.GetType().Name} renamed or removed the field {name}");

            field.SetValue(target, value);
        }

        /// <summary>
        /// Binds a private instance method once, outside a measurement window:
        /// <c>MethodInfo.Invoke</c> allocates per call, so a zero-allocation claim
        /// made through reflection would fail for the harness's own reason.
        /// </summary>
        private static System.Action PrivateAction(object target, string methodName)
        {
            var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(
                method,
                Is.Not.Null,
                $"{target.GetType().Name} renamed or removed the method {methodName}");

            return (System.Action)System.Delegate.CreateDelegate(
                typeof(System.Action),
                target,
                method);
        }

        private PrototypeUnit SpawnUnit(PlayerId owner, Vector3 position)
        {
            var unitObject = new GameObject("Unit " + owner);
            _createdObjects.Add(unitObject);
            unitObject.transform.position = position;

            var unit = unitObject.AddComponent<PrototypeUnit>();
            // Only Initialize — no EntityId assignment.
            // InitializeLocalHost will assign server-authoritative EntityIds.
            unit.Initialize(owner);
            return unit;
        }

        private PrototypeUnit GetUnit(CoreEntityId entity)
        {
            var field = typeof(PrototypeRtsController).GetField(
                "_registry",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var registry = (UnitRegistry)field.GetValue(_controller);
            return registry.TryGetUnit(entity, out var unit) ? unit : null;
        }

        private static void InvokePrivateMethod(
            object target,
            string methodName,
            params object[] args)
        {
            var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Instance);
            method?.Invoke(target, args);
        }
    }
}
