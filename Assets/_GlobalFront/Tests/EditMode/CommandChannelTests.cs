using GlobalFront.Client;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using GlobalFront.Server.Sessions;
using NUnit.Framework;
using UnityEngine;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Tests for the Command Channel abstraction (Phase 2.2) under the
    /// Phase 2.4 session gate (ADR-008). Validates that:
    /// - ICommandChannel decouples PrototypeCommandQueue from LocalMatchHost
    /// - LocalCommandChannel is session-attributed and delegates to the
    ///   host's session-gated submission
    /// - ForwardPending uses the channel abstraction
    /// - QueueStop works through the channel
    /// </summary>
    public sealed class CommandChannelTests
    {
        private const int FormationSpacingMm = 3200;
        private const int MovementSpeedMmPerTick = 350;

        private static readonly CombatStats TestCombatStats = new CombatStats(
            maximumHealth: 100,
            damage: 25,
            rangeMm: 7000,
            cooldownTicks: 10);

        private readonly System.Collections.Generic.List<GameObject> _createdObjects =
            new System.Collections.Generic.List<GameObject>();

        private LocalMatchHost _host;
        private LocalCommandChannel _channel;
        private UnitRegistry _registry;
        private PrototypeCommandQueue _queue;
        private PlayerId _player1;
        private PlayerId _player2;
        private MatchId _match;
        private SessionId _localSession;
        private SessionId _enemySession;

        [SetUp]
        public void SetUp()
        {
            _host = new LocalMatchHost();
            _registry = new UnitRegistry();
            _player1 = new PlayerId(1);
            _player2 = new PlayerId(2);

            // Create presentation units
            CreatePresentationUnit(_player1, new Vector3(0f, 0f, 0f));
            CreatePresentationUnit(_player2, new Vector3(5f, 0f, 0f));

            // Build MatchConfig template
            var specs = new[]
            {
                new UnitSpawnSpec(_player1, new WorldPointMm(0, 0), MovementSpeedMmPerTick, false),
                new UnitSpawnSpec(_player2, new WorldPointMm(5000, 0), MovementSpeedMmPerTick, false)
            };
            var config = new MatchConfig(TestCombatStats, specs);

            // Phase 2.4 (ADR-008): session-aware initialization. The server
            // assigns the PlayerIds; the template owners here coincide with
            // the assignment by construction (join order = template order).
            _match = _host.CreateSessionMatch(2);
            _localSession = _host.CreateSession(_host.CreateConnectionHandle());
            _enemySession = _host.CreateSession(_host.CreateConnectionHandle());
            Assert.That(_host.TryJoinMatch(_localSession, _match, out var assignedLocal),
                Is.EqualTo(JoinResult.Assigned));
            Assert.That(_host.TryJoinMatch(_enemySession, _match, out var assignedEnemy),
                Is.EqualTo(JoinResult.Assigned));
            Assert.That(assignedLocal, Is.EqualTo(_player1));
            Assert.That(assignedEnemy, Is.EqualTo(_player2));
            Assert.That(_host.TryStartSessionMatch(_match, config, out var entityIds), Is.True);

            // Assign EntityIds to presentation units deterministically by
            // owner: FindObjectsByType ordering is not contractually stable,
            // so the pairing follows the config spec order instead.
            var units = Object.FindObjectsByType<PrototypeUnit>(FindObjectsSortMode.None);
            for (var specIndex = 0; specIndex < config.UnitCount; specIndex++)
            {
                for (var unitIndex = 0; unitIndex < units.Length; unitIndex++)
                {
                    var unit = units[unitIndex];
                    if (unit.Entity.IsValid || unit.Owner != config.Units[specIndex].Owner)
                    {
                        continue;
                    }

                    unit.AssignAuthoritativeEntity(entityIds[specIndex]);
                    break;
                }
            }

            // Refresh registry
            _registry.Refresh(_player1);

            // Channel attributed to the local session; queue follows.
            _channel = new LocalCommandChannel(_host, _localSession);
            _queue = new PrototypeCommandQueue(_registry, _player1, FormationSpacingMm);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _createdObjects)
            {
                if (obj != null)
                    Object.DestroyImmediate(obj);
            }
            _createdObjects.Clear();
        }

        [Test]
        public void LocalCommandChannel_WrapsLocalMatchHost()
        {
            Assert.That(_channel.Host, Is.SameAs(_host));
        }

        [Test]
        public void LocalCommandChannel_ExposesItsSession()
        {
            Assert.That(_channel.Session, Is.EqualTo(_localSession));
        }

        [Test]
        public void LocalCommandChannel_ForeignSessionSubmission_IsRejectedByGate()
        {
            var enemyChannel = new LocalCommandChannel(_host, _enemySession);
            var header = new CommandHeader(_player1, 1, 1, GameCommandType.Move);
            var entities = new[] { new CoreEntityId(1) };
            var destination = new WorldPointMm(1000, 0);
            var formation = new FormationSpec(1, FormationSpacingMm, CardinalFacing.North);

            var result = enemyChannel.TrySubmitMove(header, entities, destination, formation);

            Assert.That(result, Is.EqualTo(MatchCommandRejection.SessionRejected),
                "An identity mismatch at the session gate surfaces as SessionRejected.");
        }

        [Test]
        public void LocalCommandChannel_TrySubmitMove_DelegatesToHost()
        {
            var header = new CommandHeader(_player1, 1, 1, GameCommandType.Move);
            var entities = new[] { new CoreEntityId(1) };
            var destination = new WorldPointMm(1000, 0);
            var formation = new FormationSpec(1, FormationSpacingMm, CardinalFacing.North);

            var result = _channel.TrySubmitMove(header, entities, destination, formation);

            Assert.That(result, Is.EqualTo(MatchCommandRejection.None));
        }

        [Test]
        public void LocalCommandChannel_TrySubmitAttack_DelegatesToHost()
        {
            var header = new CommandHeader(_player1, 1, 1, GameCommandType.Attack);
            var attackers = new[] { new CoreEntityId(1) };
            var target = new CoreEntityId(2);

            var result = _channel.TrySubmitAttack(header, attackers, target);

            Assert.That(result, Is.EqualTo(MatchCommandRejection.None));
        }

        [Test]
        public void LocalCommandChannel_TrySubmitStop_DelegatesToHost()
        {
            var header = new CommandHeader(_player1, 1, 1, GameCommandType.Stop);
            var entities = new[] { new CoreEntityId(1) };

            var result = _channel.TrySubmitStop(header, entities);

            Assert.That(result, Is.EqualTo(MatchCommandRejection.None));
        }

        [Test]
        public void ForwardPending_UsesICommandChannel()
        {
            var entity = new CoreEntityId(1);
            var destination = new WorldPointMm(2000, 0);

            _queue.QueueMove(destination, new[] { entity }, requestedTick: 1);
            Assert.That(_queue.PendingCount, Is.EqualTo(1));

            _queue.ForwardPending(currentTick: 1, _channel);

            Assert.That(_queue.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void ForwardPending_RejectsNullChannel()
        {
            var entity = new CoreEntityId(1);
            _queue.QueueMove(new WorldPointMm(1000, 0), new[] { entity }, requestedTick: 1);

            Assert.That(
                () => _queue.ForwardPending(currentTick: 1, channel: null),
                Throws.ArgumentNullException);
        }

        [Test]
        public void QueueStop_EnqueuesStopCommand()
        {
            var entity = new CoreEntityId(1);

            _queue.QueueStop(new[] { entity }, requestedTick: 1);

            Assert.That(_queue.PendingCount, Is.EqualTo(1));
        }

        [Test]
        public void QueueStop_RejectsEmptySelection()
        {
            _queue.QueueStop(new CoreEntityId[0], requestedTick: 1);

            Assert.That(_queue.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void ForwardPending_SubmitsStopCommandThroughChannel()
        {
            var entity = new CoreEntityId(1);

            _queue.QueueStop(new[] { entity }, requestedTick: 1);
            Assert.That(_queue.PendingCount, Is.EqualTo(1));

            _queue.ForwardPending(currentTick: 1, _channel);

            Assert.That(_queue.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void ForwardPending_MixedCommandTypes_AllSubmittedThroughChannel()
        {
            var entity1 = new CoreEntityId(1);
            var entity2 = new CoreEntityId(2);

            _queue.QueueMove(new WorldPointMm(1000, 0), new[] { entity1 }, requestedTick: 1);
            _queue.QueueAttack(entity2, new[] { entity1 }, requestedTick: 2);
            _queue.QueueStop(new[] { entity1 }, requestedTick: 3);

            Assert.That(_queue.PendingCount, Is.EqualTo(3));

            _queue.ForwardPending(currentTick: 3, _channel);

            Assert.That(_queue.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void ForwardPendingToHost_StillWorks_SessionAttributed()
        {
            var entity = new CoreEntityId(1);

            _queue.QueueMove(new WorldPointMm(1000, 0), new[] { entity }, requestedTick: 1);
            _queue.ForwardPendingToHost(currentTick: 1, _host, _localSession);

            Assert.That(_queue.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void QueuedPrototypeCommand_StopCommand_IsAccessible()
        {
            var header = new CommandHeader(_player1, 1, 1, GameCommandType.Stop);
            var entities = new[] { new CoreEntityId(1) };

            StopCommand.TryCreate(header, entities, out var stopCommand, out _);
            var queued = PrototypeCommandQueue.QueuedPrototypeCommand.FromStop(stopCommand);

            Assert.That(queued.Stop, Is.Not.Null);
            Assert.That(queued.Move, Is.Null);
            Assert.That(queued.Attack, Is.Null);
            Assert.That(queued.Header.Type, Is.EqualTo(GameCommandType.Stop));
        }

        private PrototypeUnit CreatePresentationUnit(PlayerId owner, Vector3 position)
        {
            var unitObject = new GameObject($"Unit_{owner}");
            _createdObjects.Add(unitObject);
            var unit = unitObject.AddComponent<PrototypeUnit>();
            unit.Initialize(owner);
            unitObject.transform.position = position;
            return unit;
        }
    }
}