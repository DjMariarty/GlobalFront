#pragma warning disable CS0067
using System;
using System.Collections.Generic;
using System.Linq;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Simulation;
using GlobalFront.Core.Snapshot;
using GlobalFront.Core.Visibility;
using GlobalFront.Server;
using GlobalFront.Server.Replication;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode.Server.Replication
{
    /// <summary>
    /// Step 4.3.2 integration coverage of per-client replication filtering
    /// (ADR-014, OD-39/OD-40): keyframes, deltas, removals, anti-maphack,
    /// packet-loss resumes, per-client state checksums and the zero-GC hot
    /// path, driven through the real emitter, codec and client receiver.
    ///
    /// The harness feeds encoded packets straight into
    /// <see cref="ClientReplicationReceiver"/> instead of the virtual network
    /// pipe: the transport is the only layer omitted, everything from the
    /// snapshot source down to <see cref="ClientReplicationWorld"/> is the
    /// production implementation.
    /// </summary>
    [TestFixture]
    public sealed class FogOfWarReplicationTests
    {
        private static readonly PlayerId Player1 = new PlayerId(1);
        private static readonly PlayerId Player2 = new PlayerId(2);

        [Test]
        public void Keyframe_CarriesOnlyUnitsVisibleToThatClient()
        {
            var harness = new Harness();
            var own = new EntityId(1);
            var nearEnemy = new EntityId(2);
            var farEnemy = new EntityId(3);
            var farOwn = new EntityId(4);
            harness.Source.Spawn(own, Player1, 0, 0);
            harness.Source.Spawn(nearEnemy, Player2, 5_000, 0);
            harness.Source.Spawn(farEnemy, Player2, 500_000, 0);
            harness.Source.Spawn(farOwn, Player1, 1_000_000, 0);

            var client = harness.AddClient(Player1);

            Assert.That(client.LastKeyframe, Is.Not.Null, "the baseline must install");
            Assert.That(
                Ids(client.LastKeyframe), Is.EquivalentTo(new ulong[] { 1, 2, 4 }),
                "own units and enemies inside the sight radius form the baseline");
            Assert.That(client.LastKeyframe.Length, Is.EqualTo(3));
            Assert.That(client.Receiver.World.Contains(farEnemy), Is.False,
                "a far enemy must never leak into the baseline");
            Assert.That(client.Receiver.World.LiveCount, Is.EqualTo(3));
        }

        [Test]
        public void Reveal_EnemyEntersSight_ClientReceivesAddAndSpawnsUnit()
        {
            var harness = new Harness();
            var own = new EntityId(1);
            var enemy = new EntityId(2);
            harness.Source.Spawn(own, Player1, 0, 0);
            harness.Source.Spawn(enemy, Player2, 500_000, 0);
            var client = harness.AddClient(Player1);

            Assert.That(client.Receiver.World.Contains(enemy), Is.False,
                "the hidden enemy must not be in the baseline");

            harness.Source.Move(enemy, 5_000, 0);
            harness.Step();

            var delta = client.Deltas[client.Deltas.Count - 1];
            Assert.That(delta.Header.Tick, Is.EqualTo(1UL));
            Assert.That(delta.Adds.Length, Is.EqualTo(1), "0 -> 1 must be one ADD");
            Assert.That(delta.Updates.Length, Is.Zero);
            Assert.That(delta.Removes.Length, Is.Zero);
            Assert.That(delta.Adds[0].Entity, Is.EqualTo(enemy));

            Assert.That(client.Receiver.TryGetUnit(enemy, out var state), Is.True,
                "the client world must spawn the revealed unit");
            Assert.That(state.Position, Is.EqualTo(new WorldPointMm(5_000, 0)));
            Assert.That(client.Receiver.World.LiveCount, Is.EqualTo(2));
            Assert.That(client.Receiver.InconsistentApplyCount, Is.Zero);
        }

        [Test]
        public void Hide_EnemyLeavesSight_SendsFogOfWarRemove()
        {
            var harness = new Harness();
            var own = new EntityId(1);
            var enemy = new EntityId(2);
            harness.Source.Spawn(own, Player1, 0, 0);
            harness.Source.Spawn(enemy, Player2, 5_000, 0);
            var client = harness.AddClient(Player1);

            Assert.That(client.Receiver.World.Contains(enemy), Is.True,
                "the near enemy must be visible at the baseline");

            harness.Source.Move(enemy, 500_000, 0);
            harness.Step();

            var delta = client.Deltas[client.Deltas.Count - 1];
            Assert.That(delta.Removes.Length, Is.EqualTo(1), "1 -> 0 must be one REMOVE");
            Assert.That((DeltaRemoveCause)delta.Removes[0].Cause,
                Is.EqualTo(DeltaRemoveCause.FogOfWarHidden),
                "a hidden but living enemy is a fog removal, not a destruction");
            Assert.That(delta.Adds.Length, Is.Zero);
            Assert.That(delta.Updates.Length, Is.Zero);

            Assert.That(client.Receiver.World.FogOfWarRemoveCount, Is.EqualTo(1));
            Assert.That(client.Receiver.World.DestroyedRemoveCount, Is.Zero);
            Assert.That(client.Receiver.World.Contains(enemy), Is.False);
        }

        [Test]
        public void Destroyed_InSight_SendsDestroyedRemove()
        {
            var harness = new Harness();
            var own = new EntityId(1);
            var enemy = new EntityId(2);
            harness.Source.Spawn(own, Player1, 0, 0);
            harness.Source.Spawn(enemy, Player2, 5_000, 0);
            var client = harness.AddClient(Player1);

            harness.Source.Despawn(enemy);
            harness.Step();

            var delta = client.Deltas[client.Deltas.Count - 1];
            Assert.That(delta.Removes.Length, Is.EqualTo(1));
            Assert.That((DeltaRemoveCause)delta.Removes[0].Cause,
                Is.EqualTo(DeltaRemoveCause.Destroyed),
                "an entity that left the authoritative roster is destroyed, not hidden");
            Assert.That(client.Receiver.World.DestroyedRemoveCount, Is.EqualTo(1));
            Assert.That(client.Receiver.World.FogOfWarRemoveCount, Is.Zero);
            Assert.That(client.Receiver.World.Contains(enemy), Is.False);
        }

        [Test]
        public void HiddenEnemy_MovesAndAttacks_NoRecordsLeak()
        {
            var harness = new Harness();
            var own = new EntityId(1);
            var enemy = new EntityId(2);
            harness.Source.Spawn(own, Player1, 0, 0);
            harness.Source.Spawn(enemy, Player2, 500_000, 0);
            var client = harness.AddClient(Player1);

            var baselineDeltas = client.Deltas.Count;
            for (var tick = 0; tick < 45; tick++)
            {
                harness.Source.Move(enemy, 500_000 + (tick * 100), tick * 50);
                harness.Source.SetAttackTarget(enemy, own);
                harness.Source.Damage(enemy, 100 - tick);
                harness.Step();
            }

            var leakedRecords = 0;
            var leakedEnemyRecords = 0;
            for (var index = baselineDeltas; index < client.Deltas.Count; index++)
            {
                var delta = client.Deltas[index];
                leakedRecords += delta.Adds.Length + delta.Updates.Length + delta.Removes.Length;
                leakedEnemyRecords += CountRecordsFor(delta, enemy);
            }

            Assert.That(leakedRecords, Is.EqualTo(0),
                "0 -> 0 transitions must never reach the wire (anti-maphack)");
            Assert.That(leakedEnemyRecords, Is.EqualTo(0),
                "no record may describe an entity the client is not allowed to know");
            Assert.That(harness.Emitter.EmittedDeltaCount, Is.GreaterThanOrEqualTo(1),
                "keep-alive deltas must prove the stream was alive while filtering emptied it");
            Assert.That(client.Receiver.World.LiveCount, Is.EqualTo(1));
            Assert.That(client.Receiver.World.Contains(enemy), Is.False);
        }

        [Test]
        public void LostHideDelta_DeltaResumeReplaysCumulativeWindowWithoutDuplicate()
        {
            var harness = new Harness();
            var own = new EntityId(1);
            var enemy = new EntityId(2);
            harness.Source.Spawn(own, Player1, 0, 0);
            harness.Source.Spawn(enemy, Player2, 5_000, 0);
            var client = harness.AddClient(Player1);
            Assert.That(client.Receiver.World.Contains(enemy), Is.True);

            // Tick 1: the enemy hides; the delta carrying the REMOVE is lost.
            harness.Transport.DropSession = client.Session;
            harness.Transport.DropNext = true;
            harness.Source.Move(enemy, 500_000, 0);
            harness.Step();

            Assert.That(harness.Transport.DroppedCount, Is.EqualTo(1));
            Assert.That(client.Receiver.LastAppliedTick, Is.EqualTo(0UL),
                "the lost delta must not move the confirmed tick");
            Assert.That(client.Receiver.World.Contains(enemy), Is.True,
                "the client still holds the enemy it knew at tick 0");

            // Tick 2: the own unit moves, so the delta is based on the lost tick 1.
            harness.Source.Move(own, 1_000, 0);
            harness.Step();

            var gapDelta = client.Deltas[client.Deltas.Count - 1];
            Assert.That(gapDelta.Header.BaseTick, Is.EqualTo(1UL));
            Assert.That(client.Receiver.LastAppliedTick, Is.EqualTo(0UL),
                "a dependency gap must not be applied");

            // The receiver asks for a cumulative resume from its confirmed tick.
            harness.Transport.RequestDeltaResume(client.Session, client.Receiver.LastAppliedTick);
            harness.Deliver();

            Assert.That(harness.Emitter.DeltaResumeServedCount, Is.EqualTo(1),
                "the emitter must serve the framed cumulative delta");
            Assert.That(client.Receiver.LastAppliedTick, Is.EqualTo(2UL),
                "the repaired window must end at the target tick");
            Assert.That(client.Receiver.World.DuplicateAddCount, Is.Zero,
                "a cumulative resume must not replay an entity the client still holds");
            Assert.That(client.Receiver.InconsistentApplyCount, Is.Zero);
            Assert.That(client.Receiver.World.FogOfWarRemoveCount, Is.EqualTo(1));
            Assert.That(client.Receiver.World.Contains(enemy), Is.False);

            harness.Step(3);
            Assert.That(client.Receiver.World.LiveCount, Is.EqualTo(1));
            Assert.That(client.Receiver.World.Contains(enemy), Is.False);
        }

        [Test]
        public void TwoClientsWithDisjointVisibility_PerClientChecksumsConverge()
        {
            var harness = new Harness();
            var unit1 = new EntityId(1);
            var unit2 = new EntityId(2);
            harness.Source.Spawn(unit1, Player1, 0, 0);
            harness.Source.Spawn(unit2, Player2, 500_000, 0);
            var clientA = harness.AddClient(Player1);
            var clientB = harness.AddClient(Player2);

            Assert.That(clientA.Receiver.World.LiveCount, Is.EqualTo(1));
            Assert.That(clientB.Receiver.World.LiveCount, Is.EqualTo(1));
            Assert.That(clientA.Receiver.World.Contains(unit2), Is.False);
            Assert.That(clientB.Receiver.World.Contains(unit1), Is.False);

            for (var tick = 0; tick < 45; tick++)
            {
                harness.Source.Move(unit1, tick * 10, 0);
                harness.Source.Move(unit2, 500_000 - (tick * 10), 0);
                harness.Step();
            }

            Assert.That(clientA.Receiver.ChecksumMismatchCount, Is.EqualTo(0),
                "client A's checksum mismatch");
            Assert.That(clientB.Receiver.ChecksumMismatchCount, Is.EqualTo(0),
                "client B's checksum mismatch");
            Assert.That(harness.Emitter.StateChecksumCount, Is.GreaterThanOrEqualTo(4),
                "both clients must have received checksummed deltas (2 ticks x 2 clients)");
            Assert.That(ChecksummedDeltas(clientA), Is.GreaterThanOrEqualTo(2));
            Assert.That(ChecksummedDeltas(clientB), Is.GreaterThanOrEqualTo(2));
            Assert.That(clientA.Receiver.World.LiveCount, Is.EqualTo(1));
            Assert.That(clientB.Receiver.World.LiveCount, Is.EqualTo(1));
        }

        [Test]
        public void SixtyTicksWithFogOfWar_AllocateNothing()
        {
            var harness = new Harness();
            harness.Transport.CapturePayloads = false;
            for (var index = 0; index < 128; index++)
            {
                harness.Source.Spawn(new EntityId((ulong)(index + 1)), Player1, index * 256, 0);
            }

            harness.AddClient(Player1);

            var tick = 0UL;
            for (var iteration = 0; iteration < 200; iteration++)
            {
                harness.Source.Move(
                    new EntityId((ulong)((iteration % 128) + 1)),
                    iteration * 3, iteration * 7);
                harness.Emitter.OnTickCompleted(++tick);
            }

            var deltasBefore = harness.Emitter.EmittedDeltaCount;

            // Block-bodied lambda on purpose: the negated constraint only
            // receives the delegate itself when NUnit binds Assert.That.
            Assert.That(
                () =>
                {
                    for (var iteration = 0; iteration < 60; iteration++)
                    {
                        harness.Source.Move(
                            new EntityId((ulong)((iteration % 128) + 1)),
                            1000 + iteration, 500 + iteration);
                        harness.Emitter.OnTickCompleted(++tick);
                    }
                },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "the fog-of-war replication pipeline must be allocation-free");

            Assert.That(harness.Emitter.EmittedDeltaCount - deltasBefore, Is.EqualTo(60),
                "every measured tick must stream a delta");
            Assert.That(harness.Emitter.SnapshotShortfallCount, Is.Zero);
            Assert.That(harness.Emitter.SendFailureCount, Is.Zero);
            Assert.That(harness.Emitter.KeyframeFallbackCount, Is.Zero);
        }

        [Test]
        public void FilteredDeltaBeyondPayloadBudget_FallsBackToKeyframe()
        {
            var harness = new Harness();
            for (var index = 0; index < 900; index++)
            {
                harness.Source.Spawn(
                    new EntityId((ulong)(index + 1)), Player1, index * 100, 0);
            }

            var client = harness.AddClient(Player1);
            var viewFound = harness.Emitter.TryGetClientView(client.Session, out var view);
            Assert.That(viewFound, Is.True);
            Assert.That(view.KeyframeInFlight, Is.True);

            // The 36 KB baseline is paced in ~16 KB slices; tick until it drains.
            for (var tick = 0; tick < 64; tick++)
            {
                harness.Step();
                harness.Emitter.TryGetClientView(client.Session, out view);
                if (!view.KeyframeInFlight)
                {
                    break;
                }
            }

            Assert.That(view.KeyframeInFlight, Is.False, "the baseline must finish streaming");
            Assert.That(client.LastKeyframe, Is.Not.Null);
            Assert.That(client.LastKeyframe.Length, Is.EqualTo(900));

            for (var index = 0; index < 900; index++)
            {
                harness.Source.Move(new EntityId((ulong)(index + 1)), (index * 100) + 1, 0);
            }

            var fallbacksBefore = harness.Emitter.KeyframeFallbackCount;
            var keyframesBefore = harness.Emitter.EmittedKeyframeCount;
            harness.Step();

            Assert.That(harness.Emitter.KeyframeFallbackCount, Is.GreaterThan(fallbacksBefore),
                "a filtered delta above MaxPayloadBytes must re-baseline");
            Assert.That(harness.Emitter.EmittedKeyframeCount, Is.GreaterThan(keyframesBefore),
                "the fallback must actually start a keyframe");
        }

        [Test]
        public void DisabledFilter_KeepsTheGlobalUnfilteredStream()
        {
            var harness = new Harness(filterEnabled: false);
            var own = new EntityId(1);
            var enemy = new EntityId(2);
            harness.Source.Spawn(own, Player1, 0, 0);
            harness.Source.Spawn(enemy, Player2, 500_000, 0);
            var client = harness.AddClient(Player1);

            Assert.That(client.LastKeyframe.Length, Is.EqualTo(2),
                "without fog every unit is replicated");
            Assert.That(client.Receiver.World.Contains(enemy), Is.True);
            Assert.That(harness.Filter.History.Count, Is.Zero,
                "a disabled filter must not record visibility masks");

            harness.Source.Move(enemy, 500_100, 0);
            harness.Step();

            var delta = client.Deltas[client.Deltas.Count - 1];
            Assert.That(delta.Updates.Length, Is.EqualTo(1));
            Assert.That(delta.Updates[0].Entity, Is.EqualTo(enemy));
            Assert.That(client.Receiver.TryGetUnit(enemy, out var state), Is.True);
            Assert.That(state.Position, Is.EqualTo(new WorldPointMm(500_100, 0)));
        }

        [Test]
        public void EntitiesOutsideMaskRange_AreIgnoredWithoutThrowing()
        {
            var harness = new Harness();
            var own = new EntityId(1);
            var nearEnemy = new EntityId(2);
            var outOfRange1 = new EntityId(1025);
            var outOfRange2 = new EntityId(2048);
            harness.Source.Spawn(own, Player1, 0, 0);
            harness.Source.Spawn(nearEnemy, Player2, 5_000, 0);
            harness.Source.Spawn(outOfRange1, Player1, 10_000, 0);
            harness.Source.Spawn(outOfRange2, Player1, 15_000, 0);

            var client = harness.AddClient(Player1);

            Assert.That(harness.Filter.IgnoredUnitCount, Is.EqualTo(2),
                "both unaddressable entities must be counted, not thrown on");
            Assert.That(client.LastKeyframe.Length, Is.EqualTo(2));
            Assert.That(harness.Filter.IsVisible(0, Player1, new EntityId(2000)), Is.False);

            harness.Source.Move(outOfRange1, 20_000, 0);
            harness.Source.Move(outOfRange2, 25_000, 0);
            harness.Step();

            foreach (var delta in client.Deltas)
            {
                Assert.That(delta.Adds.All(record => record.Entity.Value <= BitMask1024.BitCount), Is.True);
                Assert.That(delta.Updates.All(record => record.Entity.Value <= BitMask1024.BitCount), Is.True);
                Assert.That(delta.Removes.All(record => record.Entity.Value <= BitMask1024.BitCount), Is.True);
            }

            Assert.That(harness.Filter.IgnoredUnitCount, Is.EqualTo(4),
                "every tick re-counts the unaddressable entities");
        }

        [Test]
        public void ServerHost_AttachReplication_AcceptsTheFilterOverload()
        {
            var host = new ServerHost(new MatchServer());
            var filter = new FogOfWarReplicationFilter();
            var emitter = host.AttachReplication(
                new FowTransport(), ServerReplicationEmitterConfig.Default, filter);

            Assert.That(emitter.Filter, Is.SameAs(filter));
            Assert.That(host.Replication, Is.SameAs(emitter));
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static ulong[] Ids(DeltaAddRecord[] records)
        {
            var ids = new ulong[records.Length];
            for (var index = 0; index < records.Length; index++)
            {
                ids[index] = records[index].Entity.Value;
            }

            return ids;
        }

        private static int CountRecordsFor(DecodedDelta delta, EntityId entity)
        {
            var count = 0;
            for (var index = 0; index < delta.Adds.Length; index++)
            {
                if (delta.Adds[index].Entity == entity)
                {
                    count++;
                }
            }

            for (var index = 0; index < delta.Updates.Length; index++)
            {
                if (delta.Updates[index].Entity == entity)
                {
                    count++;
                }
            }

            for (var index = 0; index < delta.Removes.Length; index++)
            {
                if (delta.Removes[index].Entity == entity)
                {
                    count++;
                }
            }

            return count;
        }

        private static int ChecksummedDeltas(FowClient client)
        {
            var count = 0;
            for (var index = 0; index < client.Deltas.Count; index++)
            {
                if ((client.Deltas[index].Header.Flags & (byte)DeltaFlags.HasChecksum) != 0)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Deterministic ordered world fed to the emitter.</summary>
        private sealed class FowSource : IServerSnapshotSource
        {
            private readonly ServerUnitSnapshot[] _units =
                new ServerUnitSnapshot[SimulationConstants.MaxUnitsPerMatch];

            public int Count { get; private set; }

            public void Spawn(
                EntityId entity,
                PlayerId owner,
                int x,
                int z,
                int health = 100,
                bool autoAcquire = false)
            {
                var snapshot = new ServerUnitSnapshot(
                    entity, owner, new WorldPointMm(x, z), health, false, default, default, autoAcquire);
                var index = Count;
                while (index > 0 && _units[index - 1].Entity.Value > entity.Value)
                {
                    _units[index] = _units[index - 1];
                    index--;
                }

                _units[index] = snapshot;
                Count++;
            }

            public void Move(EntityId entity, int x, int z)
            {
                var index = RequireIndex(entity);
                var unit = _units[index];
                _units[index] = new ServerUnitSnapshot(
                    unit.Entity, unit.Owner, new WorldPointMm(x, z), unit.CurrentHealth,
                    unit.HasMoveTarget, unit.MoveTarget, unit.AttackTarget,
                    unit.AutoAcquireEnemies, unit.UnitKind);
            }

            public void Damage(EntityId entity, int health)
            {
                var index = RequireIndex(entity);
                var unit = _units[index];
                _units[index] = new ServerUnitSnapshot(
                    unit.Entity, unit.Owner, unit.Position, health,
                    unit.HasMoveTarget, unit.MoveTarget, unit.AttackTarget,
                    unit.AutoAcquireEnemies, unit.UnitKind);
            }

            public void SetAttackTarget(EntityId entity, EntityId target)
            {
                var index = RequireIndex(entity);
                var unit = _units[index];
                _units[index] = new ServerUnitSnapshot(
                    unit.Entity, unit.Owner, unit.Position, unit.CurrentHealth,
                    unit.HasMoveTarget, unit.MoveTarget, target,
                    unit.AutoAcquireEnemies, unit.UnitKind);
            }

            public void Despawn(EntityId entity)
            {
                var index = RequireIndex(entity);
                for (var shift = index; shift < Count - 1; shift++)
                {
                    _units[shift] = _units[shift + 1];
                }

                Count--;
            }

            public int CopySnapshots(Span<ServerUnitSnapshot> destination)
            {
                if (destination.Length < Count)
                {
                    return -1;
                }

                _units.AsSpan(0, Count).CopyTo(destination);
                return Count;
            }

            private int RequireIndex(EntityId entity)
            {
                for (var index = 0; index < Count; index++)
                {
                    if (_units[index].Entity == entity)
                    {
                        return index;
                    }
                }

                throw new InvalidOperationException($"entity {entity} is not in the world");
            }
        }

        /// <summary>Records outbound packets and delivers client feedback.</summary>
        private sealed class FowTransport : IReplicationTransport
        {
            public sealed class Packet
            {
                public SessionId Session;
                public ulong EnvelopeTick;
                public byte[] Payload;
                public int Length;
            }

            public readonly List<Packet> Packets = new List<Packet>();
            public bool CapturePayloads = true;
            public SessionId DropSession;
            public bool DropNext;
            public int DroppedCount;

            public event Action<SessionId, MatchId, PlayerId> SessionAttached;

            public event Action<SessionId, MatchId, PlayerId> SessionReattached;

            public event Action<SessionId> PostSessionReattached;

            public event Action<SessionId, TransportDisconnectReason> SessionDetached;

            public event Action<ReplicationFeedbackMessage> FeedbackReceived;

            public void Attach(SessionId session, PlayerId player) =>
                SessionAttached?.Invoke(session, default, player);

            public void Reattach(SessionId session, PlayerId player) =>
                SessionReattached?.Invoke(session, default, player);

            public void RequestSnapshot(SessionId session) =>
                SendRequest(session, new ReplicationRequestWire(
                    ReplicationRequestWireKind.SnapshotRequest, 1, 0, 0, 0));

            public void RequestDeltaResume(SessionId session, ulong lastAppliedTick) =>
                SendRequest(session, new ReplicationRequestWire(
                    ReplicationRequestWireKind.DeltaResume, 1, lastAppliedTick, 0, 0));

            public bool SendToSession(SessionId session, ulong envelopeTick, byte[] buffer, int length)
            {
                if (DropNext && session == DropSession)
                {
                    DropNext = false;
                    DroppedCount++;
                    return true;
                }

                if (!CapturePayloads)
                {
                    return true;
                }

                var payload = new byte[length];
                Array.Copy(buffer, 0, payload, 0, length);
                Packets.Add(new Packet
                {
                    Session = session,
                    EnvelopeTick = envelopeTick,
                    Payload = payload,
                    Length = length
                });
                return true;
            }

            private void SendRequest(SessionId session, in ReplicationRequestWire request)
            {
                var buffer = new byte[ReplicationRequestCodec.SizeBytes];
                Assert.That(
                    ReplicationRequestCodec.TryEncode(request, buffer, out var written),
                    Is.EqualTo(ReplicationRequestCodecResult.Ok));
                FeedbackReceived?.Invoke(new ReplicationFeedbackMessage(
                    session, TransportMessageType.ReplicationRequest, buffer, 0, written));
            }
        }

        /// <summary>Decoded delta packet retained for assertions.</summary>
        private sealed class DecodedDelta
        {
            public DecodedDelta(
                in DeltaSnapshotHeader header,
                ReadOnlySpan<DeltaAddRecord> adds,
                ReadOnlySpan<DeltaUpdateRecord> updates,
                ReadOnlySpan<DeltaRemoveRecord> removes)
            {
                Header = header;
                Adds = adds.ToArray();
                Updates = updates.ToArray();
                Removes = removes.ToArray();
            }

            public DeltaSnapshotHeader Header { get; }

            public DeltaAddRecord[] Adds { get; }

            public DeltaUpdateRecord[] Updates { get; }

            public DeltaRemoveRecord[] Removes { get; }
        }

        /// <summary>One client: real receiver + mirror fed from encoded packets.</summary>
        private sealed class FowClient
        {
            private readonly DeltaAddRecord[] _adds = new DeltaAddRecord[1024];
            private readonly DeltaUpdateRecord[] _updates = new DeltaUpdateRecord[4096];
            private readonly DeltaRemoveRecord[] _removes = new DeltaRemoveRecord[1024];
            private readonly DeltaAddRecord[] _keyframe =
                new DeltaAddRecord[SimulationConstants.MaxUnitsPerMatch];

            private ushort _keyframeSeq;
            private ulong _keyframeTick;
            private int _expectedParts;
            private int _partsReceived;
            private int _expectedUnits;
            private int _keyframeCount;
            private long _nowMs;

            public FowClient(SessionId session, PlayerId player)
            {
                Session = session;
                Player = player;
            }

            public SessionId Session { get; }

            public PlayerId Player { get; }

            public ClientReplicationReceiver Receiver { get; } =
                new ClientReplicationReceiver(ClientReplicationWorld.DefaultCapacity);

            public List<DecodedDelta> Deltas { get; } = new List<DecodedDelta>();

            public DeltaAddRecord[] LastKeyframe { get; private set; }

            public ClientReplicationOutcome LastOutcome { get; private set; }

            public void Receive(FowTransport.Packet packet)
            {
                var payload = packet.Payload.AsSpan(0, packet.Length);
                if (payload[0] == KeyframeSliceCodec.MessageType)
                {
                    var slice = KeyframeSliceCodec.TryDecodeSlice(
                        payload, _adds, out var sliceHeader, out var read);
                    Assert.That(slice, Is.EqualTo(KeyframeSliceCodecResult.Ok));
                    AccumulateKeyframe(in sliceHeader, _adds.AsSpan(0, read));
                    return;
                }

                Assert.That(payload[0], Is.EqualTo(DeltaSnapshotProtocol.MessageTypeDelta));
                var decode = DeltaSnapshotWireCodec.TryDecode(
                    payload, out var header, _adds, _updates, _removes);
                Assert.That(decode, Is.EqualTo(DeltaCodecResult.Ok));

                var decoded = new DecodedDelta(
                    in header,
                    _adds.AsSpan(0, header.AddCount),
                    _updates.AsSpan(0, header.UpdateCount),
                    _removes.AsSpan(0, header.RemoveCount));
                Deltas.Add(decoded);

                LastOutcome = Receiver.ReceiveDelta(
                    NextNowMs(),
                    in header,
                    header.KeyframeRef,
                    _adds.AsSpan(0, header.AddCount),
                    _updates.AsSpan(0, header.UpdateCount),
                    _removes.AsSpan(0, header.RemoveCount));
            }

            private void AccumulateKeyframe(
                in KeyframeSliceHeader header,
                ReadOnlySpan<DeltaAddRecord> records)
            {
                if (_keyframeSeq != header.KeyframeSeq)
                {
                    _keyframeSeq = header.KeyframeSeq;
                    _keyframeTick = header.Tick;
                    _expectedParts = header.PartCount;
                    _expectedUnits = (int)header.TotalUnitCount;
                    _partsReceived = 0;
                    _keyframeCount = 0;
                }

                records.CopyTo(_keyframe.AsSpan(_keyframeCount));
                _keyframeCount += records.Length;
                _partsReceived++;
                if (_partsReceived < _expectedParts)
                {
                    return;
                }

                Assert.That(_keyframeCount, Is.EqualTo(_expectedUnits),
                    "the assembled keyframe must hold the declared unit count");
                LastKeyframe = _keyframe.AsSpan(0, _keyframeCount).ToArray();
                var outcome = Receiver.ReceiveKeyframe(
                    NextNowMs(), _keyframeSeq, _keyframeTick,
                    _keyframe.AsSpan(0, _keyframeCount));
                LastOutcome = outcome;
                Assert.That(outcome, Is.EqualTo(ClientReplicationOutcome.BaselineInstalled));
            }

            private long NextNowMs() => _nowMs += 50;
        }

        /// <summary>Emitter + one transport + clients, steppable tick by tick.</summary>
        private sealed class Harness
        {
            private int _packetCursor;

            public Harness(
                bool filterEnabled = true,
                ServerReplicationEmitterConfig? config = null)
            {
                Filter = new FogOfWarReplicationFilter(filterEnabled);
                Emitter = new ServerReplicationEmitter(
                    Source, Transport, config ?? ServerReplicationEmitterConfig.Default, Filter);
            }

            public FowSource Source { get; } = new FowSource();

            public FowTransport Transport { get; } = new FowTransport();

            public FogOfWarReplicationFilter Filter { get; }

            public ServerReplicationEmitter Emitter { get; }

            public List<FowClient> Clients { get; } = new List<FowClient>();

            public ulong Tick { get; private set; }

            public FowClient AddClient(PlayerId player)
            {
                var client = new FowClient(new SessionId(Guid.NewGuid()), player);
                Clients.Add(client);
                Transport.Attach(client.Session, player);
                Transport.RequestSnapshot(client.Session);
                Deliver();
                return client;
            }

            public void Step(int count = 1)
            {
                for (var index = 0; index < count; index++)
                {
                    Emitter.OnTickCompleted(++Tick);
                    Deliver();
                }
            }

            public void Deliver()
            {
                for (; _packetCursor < Transport.Packets.Count; _packetCursor++)
                {
                    var packet = Transport.Packets[_packetCursor];
                    for (var index = 0; index < Clients.Count; index++)
                    {
                        if (Clients[index].Session == packet.Session)
                        {
                            Clients[index].Receive(packet);
                            break;
                        }
                    }
                }
            }
        }
    }
}
