using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using GlobalFront.Server.Snapshot;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    [TestFixture]
    public sealed class SnapshotSerializationTests
    {
        private static ServerUnitSnapshot CreateTestSnapshot(
            ulong entityId = 1,
            byte owner = 1,
            int posX = 1000,
            int posZ = 2000,
            int currentHealth = 75,
            bool hasMoveTarget = true,
            int moveTargetX = 5000,
            int moveTargetZ = 6000,
            ulong attackTarget = 0,
            bool autoAcquireEnemies = false)
        {
            return new ServerUnitSnapshot(
                new EntityId(entityId),
                new PlayerId(owner),
                new WorldPointMm(posX, posZ),
                currentHealth,
                hasMoveTarget,
                new WorldPointMm(moveTargetX, moveTargetZ),
                new EntityId(attackTarget),
                autoAcquireEnemies
            );
        }

        [Test]
        public void RoundTrip_SerializeThenDeserialize_Equal()
        {
            var original = CreateTestSnapshot();
            var header = new SnapshotPacketHeader(1, 100, 1);
            var bytes = SnapshotSerializer.Serialize(header, new[] { original });

            var (deserializedHeader, deserializedSnapshots) = SnapshotDeserializer.Deserialize(bytes);

            Assert.That(deserializedHeader, Is.EqualTo(header));
            Assert.That(deserializedSnapshots.Length, Is.EqualTo(1));
            Assert.That(deserializedSnapshots[0], Is.EqualTo(original));
        }

        [Test]
        public void AllFields_ArePreserved()
        {
            var snapshot = new ServerUnitSnapshot(
                new EntityId(12345),
                new PlayerId(7),
                new WorldPointMm(-50000, 80000),
                42,
                true,
                new WorldPointMm(30000, -40000),
                new EntityId(99999),
                true
            );

            var header = new SnapshotPacketHeader(1, 999, 1);
            var bytes = SnapshotSerializer.Serialize(header, new[] { snapshot });
            var (_, result) = SnapshotDeserializer.Deserialize(bytes);

            Assert.That(result[0].Entity.Value, Is.EqualTo(12345));
            Assert.That(result[0].Owner.Value, Is.EqualTo(7));
            Assert.That(result[0].Position.X, Is.EqualTo(-50000));
            Assert.That(result[0].Position.Z, Is.EqualTo(80000));
            Assert.That(result[0].CurrentHealth, Is.EqualTo(42));
            Assert.That(result[0].HasMoveTarget, Is.True);
            Assert.That(result[0].MoveTarget.X, Is.EqualTo(30000));
            Assert.That(result[0].MoveTarget.Z, Is.EqualTo(-40000));
            Assert.That(result[0].AttackTarget.Value, Is.EqualTo(99999));
            Assert.That(result[0].AutoAcquireEnemies, Is.True);
        }

        [Test]
        public void Determinism_IdenticalInputProducesIdenticalBytes()
        {
            var snapshot = CreateTestSnapshot();
            var header = new SnapshotPacketHeader(1, 500, 1);

            var bytes1 = SnapshotSerializer.Serialize(header, new[] { snapshot });
            var bytes2 = SnapshotSerializer.Serialize(header, new[] { snapshot });

            Assert.That(bytes1, Is.EqualTo(bytes2));
        }

        [Test]
        public void GoldenBytes_KnownSnapshotProducesExpectedBytes()
        {
            var snapshot = new ServerUnitSnapshot(
                new EntityId(1),
                new PlayerId(1),
                new WorldPointMm(1000, 2000),
                75,
                true,
                new WorldPointMm(5000, 6000),
                new EntityId(0),
                false
            );

            var header = new SnapshotPacketHeader(1, 100, 1);
            var bytes = SnapshotSerializer.Serialize(header, new[] { snapshot });

            var expected = new byte[]
            {
                // Header (16 bytes)
                0x01, 0x00, 0x00, 0x00, // ProtocolVersion = 1
                0x64, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // Tick = 100
                0x01, 0x00, 0x00, 0x00, // UnitCount = 1

                // Snapshot (39 bytes)
                0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // Entity = 1
                0x01, // Owner = 1
                0xE8, 0x03, 0x00, 0x00, // Position.X = 1000
                0xD0, 0x07, 0x00, 0x00, // Position.Z = 2000
                0x4B, 0x00, 0x00, 0x00, // CurrentHealth = 75
                0x01, // HasMoveTarget = true
                0x88, 0x13, 0x00, 0x00, // MoveTarget.X = 5000
                0x70, 0x17, 0x00, 0x00, // MoveTarget.Z = 6000
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // AttackTarget = 0
                0x00  // AutoAcquireEnemies = false
            };

            Assert.That(bytes, Is.EqualTo(expected));
        }

        [Test]
        public void ProtocolVersionMismatch_IsRejected()
        {
            var snapshot = CreateTestSnapshot();
            var header = new SnapshotPacketHeader(1, 100, 1);
            var bytes = SnapshotSerializer.Serialize(header, new[] { snapshot });

            bytes[0] = 0x02;

            Assert.Throws<SnapshotSerializationException>(() =>
            {
                SnapshotDeserializer.Deserialize(bytes);
            });
        }

        [Test]
        public void TruncatedHeader_IsRejected()
        {
            var bytes = new byte[10];
            bytes[0] = 0x01;

            Assert.Throws<SnapshotSerializationException>(() =>
            {
                SnapshotDeserializer.Deserialize(bytes);
            });
        }

        [Test]
        public void TruncatedPayload_IsRejected()
        {
            var header = new SnapshotPacketHeader(1, 100, 2);
            var bytes = new byte[16 + 20];
            bytes[0] = 0x01;
            BitConverter.GetBytes(100UL).CopyTo(bytes, 4);
            BitConverter.GetBytes(2u).CopyTo(bytes, 12);

            Assert.Throws<SnapshotSerializationException>(() =>
            {
                SnapshotDeserializer.Deserialize(bytes);
            });
        }

        [Test]
        public void InvalidUnitCount_IsRejected()
        {
            var header = new SnapshotPacketHeader(1, 100, 10);
            var bytes = new byte[16 + 50];
            bytes[0] = 0x01;
            BitConverter.GetBytes(100UL).CopyTo(bytes, 4);
            BitConverter.GetBytes(10u).CopyTo(bytes, 12);

            Assert.Throws<SnapshotSerializationException>(() =>
            {
                SnapshotDeserializer.Deserialize(bytes);
            });
        }

        [Test]
        public void EmptySnapshot_SerializesAndDeserializes()
        {
            var header = new SnapshotPacketHeader(1, 200, 0);
            var bytes = SnapshotSerializer.Serialize(header, Array.Empty<ServerUnitSnapshot>());

            var (deserializedHeader, deserializedSnapshots) = SnapshotDeserializer.Deserialize(bytes);

            Assert.That(deserializedHeader, Is.EqualTo(header));
            Assert.That(deserializedSnapshots.Length, Is.EqualTo(0));
        }

        [Test]
        public void MultipleUnits_SerializesAndDeserializes()
        {
            var snapshots = new[]
            {
                CreateTestSnapshot(entityId: 1, posX: 100, posZ: 200),
                CreateTestSnapshot(entityId: 2, posX: 300, posZ: 400),
                CreateTestSnapshot(entityId: 3, posX: 500, posZ: 600)
            };

            var header = new SnapshotPacketHeader(1, 300, 3);
            var bytes = SnapshotSerializer.Serialize(header, snapshots);
            var (_, result) = SnapshotDeserializer.Deserialize(bytes);

            Assert.That(result.Length, Is.EqualTo(3));
            for (int i = 0; i < 3; i++)
            {
                Assert.That(result[i], Is.EqualTo(snapshots[i]));
            }
        }

        [Test]
        public void MaxUnits_SerializesAndDeserializes()
        {
            const int maxUnits = 1000;
            var snapshots = new ServerUnitSnapshot[maxUnits];
            for (int i = 0; i < maxUnits; i++)
            {
                snapshots[i] = CreateTestSnapshot(entityId: (ulong)(i + 1));
            }

            var header = new SnapshotPacketHeader(1, 400, (uint)maxUnits);
            var bytes = SnapshotSerializer.Serialize(header, snapshots);
            var (_, result) = SnapshotDeserializer.Deserialize(bytes);

            Assert.That(result.Length, Is.EqualTo(maxUnits));
            for (int i = 0; i < maxUnits; i++)
            {
                Assert.That(result[i].Entity.Value, Is.EqualTo((ulong)(i + 1)));
            }
        }

        [Test]
        public void SameOrder_ProducesIdenticalBytes()
        {
            var snapshots = new[]
            {
                CreateTestSnapshot(entityId: 1),
                CreateTestSnapshot(entityId: 2),
                CreateTestSnapshot(entityId: 3)
            };

            var header = new SnapshotPacketHeader(1, 500, 3);

            var bytes1 = SnapshotSerializer.Serialize(header, snapshots);
            var bytes2 = SnapshotSerializer.Serialize(header, snapshots);

            Assert.That(bytes1, Is.EqualTo(bytes2));
        }

        [Test]
        public void CanonicalOrder_IsEnforced()
        {
            var snapshots = new[]
            {
                CreateTestSnapshot(entityId: 3),
                CreateTestSnapshot(entityId: 1),
                CreateTestSnapshot(entityId: 2)
            };

            var header = new SnapshotPacketHeader(1, 700, 3);

            Assert.Throws<ArgumentException>(() =>
            {
                SnapshotSerializer.Serialize(header, snapshots);
            });
        }
    }
}