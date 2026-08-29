using GlobalFront.Server.Transport;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode
{
    /// <summary>
    /// Phase 2.5 fragmentation / reassembly tests (ADR-009 limits):
    /// interleaving, eviction, lifetime, fragment-count limits, oversized
    /// messages and the per-peer memory budget — all on the virtual
    /// transport clock.
    /// </summary>
    [TestFixture]
    public sealed class FragmentationTests
    {
        [Test]
        public void Reassembly_CompletesFromInterleavedFragments()
        {
            var assembler = new FragmentAssembler();
            var data = new byte[TransportProtocol.FragmentPayloadBytes * 3 + 10];
            for (var index = 0; index < data.Length; index++)
            {
                data[index] = (byte)(index & 0xFF);
            }

            var fragmentCount = MessageFragmenter.GetFragmentCount(data.Length);
            Assert.That(fragmentCount, Is.EqualTo(4));

            MessageFragmenter.GetFragmentPayload(data.Length, 0, out var offset0, out var length0);
            MessageFragmenter.GetFragmentPayload(data.Length, 1, out var offset1, out var length1);
            MessageFragmenter.GetFragmentPayload(data.Length, 2, out var offset2, out var length2);
            MessageFragmenter.GetFragmentPayload(data.Length, 3, out var offset3, out var length3);

            // Interleave fragments of a second group between fragments of
            // the first group.
            var otherA = new byte[] { 7, 7 };
            var otherB = new byte[] { 9 };

            Assert.That(assembler.AddFragment(1, 0, 4, data, offset0, length0, 0), Is.Null);
            Assert.That(assembler.AddFragment(2, 0, 2, otherA, 0, otherA.Length, 10), Is.Null,
                "independent group stays incomplete until all fragments arrive");
            Assert.That(assembler.AddFragment(1, 2, 4, data, offset2, length2, 20), Is.Null);
            var otherComplete = assembler.AddFragment(2, 1, 2, otherB, 0, otherB.Length, 25);
            Assert.That(otherComplete, Is.EqualTo(new byte[] { 7, 7, 9 }),
                "independent group completes separately");
            Assert.That(assembler.AddFragment(1, 1, 4, data, offset1, length1, 30), Is.Null);

            var complete = assembler.AddFragment(1, 3, 4, data, offset3, length3, 40);
            Assert.That(complete, Is.Not.Null);
            Assert.That(complete, Is.EqualTo(data));
        }

        [Test]
        public void Reassembly_EvictsOldestGroup_WhenGroupLimitExceeded()
        {
            var assembler = new FragmentAssembler();
            var fragment = new byte[] { 1, 2, 3 };

            for (ushort group = 1; group <= TransportProtocol.MaxFragmentGroups; group++)
            {
                assembler.AddFragment(group, 0, 3, fragment, 0, fragment.Length, 0);
            }

            Assert.That(assembler.GroupCount, Is.EqualTo(TransportProtocol.MaxFragmentGroups));

            // A new group evicts the oldest (group 1).
            assembler.AddFragment(99, 0, 3, fragment, 0, fragment.Length, 100);
            Assert.That(assembler.GroupCount, Is.EqualTo(TransportProtocol.MaxFragmentGroups));

            // Group 1 must no longer complete.
            var completed = assembler.AddFragment(1, 1, 3, fragment, 0, fragment.Length, 110);
            Assert.That(completed, Is.Null);
            completed = assembler.AddFragment(1, 2, 3, fragment, 0, fragment.Length, 120);
            Assert.That(completed, Is.Null);
        }

        [Test]
        public void Reassembly_ExpiresGroups_AfterLifetime()
        {
            var assembler = new FragmentAssembler();
            var fragment = new byte[] { 1 };
            assembler.AddFragment(1, 0, 2, fragment, 0, fragment.Length, 0);

            assembler.Expire(TransportProtocol.FragmentLifetimeMs + 1);
            Assert.That(assembler.GroupCount, Is.EqualTo(0));

            // The expired group cannot complete.
            Assert.That(
                assembler.AddFragment(1, 1, 2, fragment, 0, fragment.Length, 2001), Is.Null);
        }

        [Test]
        public void Reassembly_RejectsTooManyFragments()
        {
            var assembler = new FragmentAssembler();
            var fragment = new byte[] { 1 };

            Assert.That(
                assembler.AddFragment(
                    1, 0, (ushort)(TransportProtocol.MaxFragmentsPerGroup + 1),
                    fragment, 0, fragment.Length, 0),
                Is.Null);
            Assert.That(assembler.GroupCount, Is.EqualTo(0));
        }

        [Test]
        public void Reassembly_DiscardsOversizedMessage()
        {
            var assembler = new FragmentAssembler();
            var fragment = new byte[TransportProtocol.FragmentPayloadBytes];
            var fragmentsNeeded =
                (TransportProtocol.MaxMessageBytes / TransportProtocol.FragmentPayloadBytes) + 2;

            for (var index = 0; index < fragmentsNeeded; index++)
            {
                var result = assembler.AddFragment(
                    1, (ushort)index, (ushort)fragmentsNeeded, fragment, 0, fragment.Length, index);
                if (result != null)
                {
                    Assert.Fail("an oversized message must never complete");
                }

                if (assembler.GroupCount == 0)
                {
                    return; // group discarded once the size cap was crossed
                }
            }

            Assert.Fail("the oversized group must be discarded");
        }

        [Test]
        public void Reassembly_RespectsPerPeerMemoryBudget()
        {
            var assembler = new FragmentAssembler();
            var fragment = new byte[TransportProtocol.FragmentPayloadBytes];

            // Groups stay INCOMPLETE (199 of 200 fragments), so they keep
            // holding reassembly memory and exercise admission/eviction.
            var groupSizeFragments = 200;
            var groupsCreated = 0;
            for (var group = 1; group <= 20; group++)
            {
                var started = assembler.GroupCount;
                for (var index = 0; index < groupSizeFragments - 1; index++)
                {
                    assembler.AddFragment(
                        (ushort)group, (ushort)index, (ushort)groupSizeFragments,
                        fragment, 0, fragment.Length, 0);
                }

                if (assembler.GroupCount > started)
                {
                    groupsCreated++;
                }

                Assert.That(assembler.AllocatedBytes,
                    Is.LessThanOrEqualTo(TransportProtocol.ReassemblyBudgetBytes),
                    "reassembly memory must stay within the per-peer budget");
                Assert.That(assembler.GroupCount,
                    Is.LessThanOrEqualTo(TransportProtocol.MaxFragmentGroups),
                    "concurrent group count must stay within the limit");
            }

            Assert.That(groupsCreated, Is.GreaterThan(0));

            // The earliest group must have been evicted: its missing final
            // fragment can no longer complete it.
            Assert.That(
                assembler.AddFragment(1, 199, (ushort)groupSizeFragments, fragment, 0, fragment.Length, 10),
                Is.Null,
                "an evicted group must not complete after eviction");
        }

        [Test]
        public void Reassembly_DiscardGroup_RemovesStaleGroupSafely()
        {
            var assembler = new FragmentAssembler();
            var fragment = new byte[] { 1, 2 };

            Assert.That(assembler.AddFragment(7, 0, 3, fragment, 0, fragment.Length, 0), Is.Null);
            Assert.That(assembler.GroupCount, Is.EqualTo(1));

            Assert.That(assembler.DiscardGroup(7), Is.True,
                "the stale group must be discardable");
            Assert.That(assembler.GroupCount, Is.EqualTo(0));

            // Stale late fragments of the discarded group cannot complete it:
            // indices 1 and 2 alone (without 0) never form the message.
            Assert.That(
                assembler.AddFragment(7, 1, 3, fragment, 0, fragment.Length, 5), Is.Null);
            Assert.That(
                assembler.AddFragment(7, 2, 3, fragment, 0, fragment.Length, 6), Is.Null);

            Assert.That(assembler.DiscardGroup(9), Is.False,
                "discarding an unknown group is a safe no-op");
        }

        [Test]
        public void Carrier_LargeReliableMessage_SurvivesLossAndReassembles()
        {
            var profile = new ImpairmentProfile
            {
                LossProbability = 0.1,
                Seed = 21
            };
            var pipe = new VirtualNetworkPipe(profile);
            var clock = new VirtualTransportClock();
            var serverAddress = pipe.CreateEndpoint();
            var clientAddress = pipe.CreateEndpoint();
            var server = OwnDatagramCarrier.CreateServer(pipe, serverAddress, clock);
            var client = OwnDatagramCarrier.CreateClient(pipe, clientAddress, serverAddress, clock);
            var clientConnection = client.ConnectToServer("virtual", 0);

            var serverConnection = -1;
            byte[] received = null;

            void Pump(long milliseconds)
            {
                pipe.Advance(milliseconds);
                clock.Advance(milliseconds);
                server.Pump(clock.NowMs);
                client.Pump(clock.NowMs);
                while (server.TryDequeueEvent(out var transportEvent))
                {
                    if (transportEvent.Type == TransportEventType.Connected)
                    {
                        serverConnection = transportEvent.ConnectionId;
                    }
                    else if (transportEvent.Type == TransportEventType.Message)
                    {
                        received = new byte[transportEvent.Length];
                        System.Array.Copy(transportEvent.Data, received, transportEvent.Length);
                    }
                }
            }

            var elapsed = 0L;
            while (serverConnection < 0 && elapsed < 2000)
            {
                Pump(5);
                elapsed += 5;
            }

            Assert.That(serverConnection, Is.GreaterThanOrEqualTo(0));

            // 100 KB message: exercises fragmentation + reassembly + ARQ.
            var message = new byte[100 * 1024];
            for (var index = 0; index < message.Length; index++)
            {
                message[index] = (byte)((index * 31) & 0xFF);
            }

            Assert.That(
                client.Send(clientConnection, TransportChannel.Command, message, 0, message.Length),
                Is.True);

            // A live peer answers: periodic ack-bait keeps RTT samples and
            // retransmissions fast, exactly like an endpoint would.
            var ackBait = new byte[] { 1 };
            elapsed = 0;
            while (received == null && elapsed < 120000)
            {
                if (elapsed % 20 == 0)
                {
                    server.Send(serverConnection, TransportChannel.Control, ackBait, 0, ackBait.Length);
                }

                Pump(10);
                elapsed += 10;
            }

            Assert.That(received, Is.Not.Null, "large message must arrive despite loss");
            Assert.That(received, Is.EqualTo(message));
        }

        [Test]
        public void Carrier_RejectsMessageAboveMaxSize()
        {
            var pipe = new VirtualNetworkPipe(new ImpairmentProfile());
            var clock = new VirtualTransportClock();
            var serverAddress = pipe.CreateEndpoint();
            var clientAddress = pipe.CreateEndpoint();
            var server = OwnDatagramCarrier.CreateServer(pipe, serverAddress, clock);
            var client = OwnDatagramCarrier.CreateClient(pipe, clientAddress, serverAddress, clock);
            var clientConnection = client.ConnectToServer("virtual", 0);

            var oversized = new byte[TransportProtocol.MaxMessageBytes + 1];
            Assert.That(
                client.Send(clientConnection, TransportChannel.Command, oversized, 0, oversized.Length),
                Is.False, "messages above the ADR-009 ceiling must be rejected");
            Assert.That(client.Metrics.DroppedMalformed, Is.GreaterThan(0));
        }
    }
}
