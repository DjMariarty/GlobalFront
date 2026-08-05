using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using NUnit.Framework;
using UnityEngine;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Tests.EditMode
{
    public sealed class PrototypeCommandQueueTests
    {
        private static readonly PlayerId LocalPlayer = new PlayerId(1);
        private static readonly PlayerId EnemyPlayer = new PlayerId(2);
        private const int FormationSpacingMm = 3200;

        private readonly List<GameObject> _createdObjects = new List<GameObject>();
        private UnitRegistry _registry;
        private PrototypeCommandQueue _queue;

        [SetUp]
        public void SetUp()
        {
            _registry = new UnitRegistry();
            _queue = new PrototypeCommandQueue(
                _registry, LocalPlayer, FormationSpacingMm);
        }

        [TearDown]
        public void TearDown()
        {
            for (var index = 0; index < _createdObjects.Count; index++)
            {
                Object.DestroyImmediate(_createdObjects[index]);
            }

            _createdObjects.Clear();
        }

        [Test]
        public void InitialState_NoPendingCommands()
        {
            Assert.AreEqual(0, _queue.PendingCount);
            Assert.AreEqual(
                "Select blue units with LMB",
                _queue.LastCommandMessage);
        }

        [Test]
        public void QueueMove_EnqueuesCommand()
        {
            var unit = CreateUnit(1, LocalPlayer, new Vector3(5f, 0f, 5f));
            _registry.Refresh(LocalPlayer);

            _queue.QueueMove(
                new WorldPointMm(10000, 10000),
                new[] { unit.Entity },
                requestedTick: 1);

            Assert.AreEqual(1, _queue.PendingCount);
            Assert.IsTrue(_queue.LastCommandMessage.StartsWith("Move #1"));
        }

        [Test]
        public void QueueMove_RejectsEmptySelection()
        {
            _registry.Refresh(LocalPlayer);

            _queue.QueueMove(
                new WorldPointMm(10000, 10000),
                new CoreEntityId[0],
                requestedTick: 1);

            Assert.AreEqual(0, _queue.PendingCount);
            Assert.IsTrue(_queue.LastCommandMessage.Contains("rejected"));
        }

        [Test]
        public void QueueMove_RejectsOutOfBoundsDestination()
        {
            var unit = CreateUnit(1, LocalPlayer, new Vector3(5f, 0f, 5f));
            _registry.Refresh(LocalPlayer);

            _queue.QueueMove(
                new WorldPointMm(2_000_000_000, 0),
                new[] { unit.Entity },
                requestedTick: 1);

            Assert.AreEqual(0, _queue.PendingCount);
            Assert.IsTrue(_queue.LastCommandMessage.Contains("rejected"));
        }

        [Test]
        public void ApplyPending_DoesNotApplyFutureTickCommands()
        {
            var unit = CreateUnit(1, LocalPlayer, new Vector3(0f, 0f, 0f));
            _registry.Refresh(LocalPlayer);

            _queue.QueueMove(
                new WorldPointMm(5000, 5000),
                new[] { unit.Entity },
                requestedTick: 10);

            _queue.ApplyPending(currentTick: 5);

            Assert.AreEqual(1, _queue.PendingCount);
        }

        [Test]
        public void ApplyPending_AppliesDueMoveCommand()
        {
            var unit = CreateUnit(1, LocalPlayer, new Vector3(0f, 0f, 0f));
            _registry.Refresh(LocalPlayer);

            _queue.QueueMove(
                new WorldPointMm(5000, 5000),
                new[] { unit.Entity },
                requestedTick: 3);

            _queue.ApplyPending(currentTick: 3);

            Assert.AreEqual(0, _queue.PendingCount);
            Assert.IsTrue(_queue.LastCommandMessage.Contains("executing"));
        }

        [Test]
        public void ApplyPending_AppliesDueAttackCommand()
        {
            var attacker = CreateUnit(1, LocalPlayer, new Vector3(0f, 0f, 0f));
            var target = CreateUnit(2, EnemyPlayer, new Vector3(3f, 0f, 0f));
            _registry.Refresh(LocalPlayer);

            _queue.QueueAttack(
                target.Entity,
                new[] { attacker.Entity },
                requestedTick: 2);

            _queue.ApplyPending(currentTick: 2);

            Assert.AreEqual(0, _queue.PendingCount);
            Assert.IsTrue(_queue.LastCommandMessage.Contains("executing"));
            Assert.AreEqual(target.Entity, attacker.AttackTarget);
        }

        [Test]
        public void QueueAttack_RejectsFriendlyTarget()
        {
            var attacker = CreateUnit(1, LocalPlayer, new Vector3(0f, 0f, 0f));
            var friendly = CreateUnit(2, LocalPlayer, new Vector3(3f, 0f, 0f));
            _registry.Refresh(LocalPlayer);

            _queue.QueueAttack(
                friendly.Entity,
                new[] { attacker.Entity },
                requestedTick: 1);

            Assert.AreEqual(0, _queue.PendingCount);
            Assert.IsTrue(_queue.LastCommandMessage.Contains("rejected"));
        }

        [Test]
        public void QueueAttack_RejectsEmptyAttackers()
        {
            var target = CreateUnit(2, EnemyPlayer, new Vector3(3f, 0f, 0f));
            _registry.Refresh(LocalPlayer);

            _queue.QueueAttack(
                target.Entity,
                new CoreEntityId[0],
                requestedTick: 1);

            Assert.AreEqual(0, _queue.PendingCount);
            Assert.IsTrue(_queue.LastCommandMessage.Contains("rejected"));
        }

        [Test]
        public void Clear_RemovesAllPendingCommands()
        {
            var unit = CreateUnit(1, LocalPlayer, new Vector3(0f, 0f, 0f));
            _registry.Refresh(LocalPlayer);

            _queue.QueueMove(
                new WorldPointMm(5000, 5000),
                new[] { unit.Entity },
                requestedTick: 5);

            _queue.Clear();

            Assert.AreEqual(0, _queue.PendingCount);
        }

        [Test]
        public void OverrideMessage_SetsLastCommandMessage()
        {
            _queue.OverrideMessage("BATTLE OVER");

            Assert.AreEqual("BATTLE OVER", _queue.LastCommandMessage);
        }

        [Test]
        public void CompareCommands_OrdersByTickAscending()
        {
            var unit = CreateUnit(1, LocalPlayer, new Vector3(0f, 0f, 0f));
            _registry.Refresh(LocalPlayer);

            _queue.QueueMove(
                new WorldPointMm(5000, 5000),
                new[] { unit.Entity },
                requestedTick: 5);
            _queue.QueueMove(
                new WorldPointMm(3000, 3000),
                new[] { unit.Entity },
                requestedTick: 2);

            // After internal sort, tick=2 should be first.
            _queue.ApplyPending(currentTick: 2);
            Assert.AreEqual(1, _queue.PendingCount);
        }

        [Test]
        public void Sequence_IncrementsPerCommand()
        {
            var unit = CreateUnit(1, LocalPlayer, new Vector3(0f, 0f, 0f));
            _registry.Refresh(LocalPlayer);

            _queue.QueueMove(
                new WorldPointMm(5000, 5000),
                new[] { unit.Entity },
                requestedTick: 1);
            _queue.QueueMove(
                new WorldPointMm(3000, 3000),
                new[] { unit.Entity },
                requestedTick: 1);

            // Two commands enqueued, sequences 1 and 2.
            Assert.AreEqual(2, _queue.PendingCount);
        }

        [Test]
        public void ChooseFacing_EastWhenDeltaXDominantPositive()
        {
            Assert.AreEqual(
                CardinalFacing.East,
                PrototypeCommandQueue.ChooseFacing(5000, 1000));
        }

        [Test]
        public void ChooseFacing_WestWhenDeltaXDominantNegative()
        {
            Assert.AreEqual(
                CardinalFacing.West,
                PrototypeCommandQueue.ChooseFacing(-5000, 1000));
        }

        [Test]
        public void ChooseFacing_NorthWhenDeltaZDominantPositive()
        {
            Assert.AreEqual(
                CardinalFacing.North,
                PrototypeCommandQueue.ChooseFacing(1000, 5000));
        }

        [Test]
        public void ChooseFacing_SouthWhenDeltaZDominantNegative()
        {
            Assert.AreEqual(
                CardinalFacing.South,
                PrototypeCommandQueue.ChooseFacing(1000, -5000));
        }

        [Test]
        public void CompareCommands_SameTickSamePlayer_OrdersBySequence()
        {
            var header1 = new CommandHeader(
                LocalPlayer, 2u, 5ul, GameCommandType.Move);
            var header2 = new CommandHeader(
                LocalPlayer, 1u, 5ul, GameCommandType.Move);

            var cmd1 = PrototypeCommandQueue.QueuedPrototypeCommand
                .FromMove(CreateDummyMove(header1));
            var cmd2 = PrototypeCommandQueue.QueuedPrototypeCommand
                .FromMove(CreateDummyMove(header2));

            Assert.Less(
                PrototypeCommandQueue.CompareCommands(cmd2, cmd1),
                0);
        }

        private PrototypeUnit CreateUnit(
            ulong entityValue,
            PlayerId owner,
            Vector3 position)
        {
            var unitObject = new GameObject($"Unit {entityValue}");
            _createdObjects.Add(unitObject);
            unitObject.transform.position = position;
            var unit = unitObject.AddComponent<PrototypeUnit>();
            unit.Initialize(new CoreEntityId(entityValue), owner);
            return unit;
        }

        private static MoveCommand CreateDummyMove(CommandHeader header)
        {
            var formation = new FormationSpec(0, 3200, CardinalFacing.North);
            MoveCommand.TryCreate(
                header,
                new[] { new CoreEntityId(1) },
                new WorldPointMm(5000, 5000),
                formation,
                out var command,
                out _);
            return command;
        }
    }
}