using System;
using System.Reflection;
using GlobalFront.Server.Transport;
using NUnit.Framework;

namespace GlobalFront.Tests.EditMode.Server.Transport
{
    /// <summary>
    /// Block 4 / audit P2-01: every terminal branch of the LiteNetLib receive
    /// path returns its pooled buffer. The pool's own monotonic Rent/Return
    /// accounting is the witness: the outstanding count must be identical
    /// before and after a rate-limited packet, a queue-overflow drop, a
    /// teardown of a partial fragment group, and a shutdown with a non-empty
    /// queue.
    ///
    /// The private receive entry points are driven directly, the same way
    /// <c>LiteNetLibCarrierIntegrationTests</c> drives the snapshot handler:
    /// the worker thread and the socket are the only layers omitted.
    /// </summary>
    [TestFixture]
    public sealed class LiteNetLibBufferAccountingTests
    {
        private LiteNetLibCarrier _carrier;

        [SetUp]
        public void SetUp()
        {
            _carrier = LiteNetLibCarrier.CreateServer(new SystemTransportClock());
        }

        [TearDown]
        public void TearDown()
        {
            _carrier?.Dispose();
            _carrier = null;
        }

        [Test]
        public void RateLimitedControlPacket_ReturnsItsRentedBuffer()
        {
            var receive = BindControlHandler(_carrier);
            var limiter = new RateLimiter();
            var payload = new byte[16];
            var baseline = TransportBufferPool.OutstandingCount;

            for (var index = 0; index < TransportProtocol.RateLimitPacketsPerSecond; index++)
            {
                receive(4242, limiter, payload, 0, payload.Length);
            }

            var afterAccepted = TransportBufferPool.OutstandingCount;
            Assert.That(
                afterAccepted - baseline,
                Is.EqualTo(TransportProtocol.RateLimitPacketsPerSecond),
                "each queued control message owns exactly one pooled buffer");

            receive(4242, limiter, payload, 0, payload.Length);

            Assert.That(
                TransportBufferPool.OutstandingCount,
                Is.EqualTo(afterAccepted),
                "a rate-limited packet must hand its rented buffer back (P2-01)");
            Assert.That(_carrier.Metrics.DroppedRateLimited, Is.EqualTo(1));

            DrainEvents();
            Assert.That(
                TransportBufferPool.OutstandingCount, Is.EqualTo(baseline),
                "after the accepted messages are consumed the pool balances again");
        }

        [Test]
        public void QueueOverflowDrop_ReturnsItsRentedBuffer()
        {
            var enqueue = BindEnqueueEvent(_carrier);
            var start = TransportBufferPool.OutstandingCount;
            var queued = new byte[TransportProtocol.MaxQueuedReceiveEvents][];
            for (var index = 0; index < queued.Length; index++)
            {
                queued[index] = TransportBufferPool.Rent(16);
                enqueue(MessageEvent(queued[index], 16));
            }

            var baseline = TransportBufferPool.OutstandingCount;
            Assert.That(
                baseline - start, Is.EqualTo(queued.Length),
                "the saturated queue owns one pooled buffer per message");

            var overflowed = TransportBufferPool.Rent(16);
            enqueue(MessageEvent(overflowed, 16));

            Assert.That(
                TransportBufferPool.OutstandingCount, Is.EqualTo(baseline),
                "a message shed by queue pressure must return its buffer (P2-01)");
            Assert.That(
                _carrier.Metrics.DroppedQueueOverflow, Is.EqualTo(1));
            Assert.That(
                _carrier.EventQueueDepth,
                Is.EqualTo(TransportProtocol.MaxQueuedReceiveEvents));

            DrainEvents();
            Assert.That(
                TransportBufferPool.OutstandingCount, Is.EqualTo(start),
                "draining the queue releases every buffer it owned");
        }

        [Test]
        public void Shutdown_ReturnsEveryQueuedBuffer()
        {
            var enqueue = BindEnqueueEvent(_carrier);
            var start = TransportBufferPool.OutstandingCount;
            for (var index = 0; index < 8; index++)
            {
                enqueue(MessageEvent(TransportBufferPool.Rent(16), 16));
            }

            Assert.That(TransportBufferPool.OutstandingCount - start, Is.EqualTo(8));

            _carrier.Shutdown(TransportDisconnectReason.ServerShutdown);

            Assert.That(
                TransportBufferPool.OutstandingCount, Is.EqualTo(start),
                "shutdown must not orphan the buffers still sitting in the event queue (P2-01)");
            Assert.That(_carrier.EventQueueDepth, Is.Zero);
            _carrier.Dispose();
            _carrier = null;
        }

        [Test]
        public void PeerRemoval_ReleasesPartialFragmentGroup()
        {
            var receiveFragment = BindSnapshotHandler(_carrier);
            var removePeer = BindRemovePeerState(_carrier);
            const int connectionId = 5150;
            const int peerId = 77;
            var start = TransportBufferPool.OutstandingCount;

            var fragment = BuildSnapshotDatagram(
                messageId: 1, sequence: 1, fragmentIndex: 0, fragmentCount: 2, payloadLength: 64);
            receiveFragment(connectionId, fragment, 0, fragment.Length);

            Assert.That(
                TransportBufferPool.OutstandingCount - start, Is.EqualTo(1),
                "the incomplete group owns one pooled fragment slice");

            removePeer(peerId, connectionId);

            Assert.That(
                TransportBufferPool.OutstandingCount, Is.EqualTo(start),
                "dropping a peer must Reset its assembler instead of orphaning the slices (P2-01)");
        }

        private void DrainEvents()
        {
            while (_carrier.TryDequeueEvent(out var transportEvent))
            {
                _carrier.ReturnEventBuffer(transportEvent.Data);
            }
        }

        private static TransportEvent MessageEvent(byte[] data, int length) =>
            new TransportEvent
            {
                Type = TransportEventType.Message,
                ConnectionId = 1,
                Channel = TransportChannel.Control,
                Data = data,
                Length = length
            };

        private static Action<int, RateLimiter, byte[], int, int> BindControlHandler(
            LiteNetLibCarrier carrier)
        {
            var method = typeof(LiteNetLibCarrier).GetMethod(
                "HandleControlReceive",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null,
                "HandleControlReceive must remain the control-packet receive entry point");
            return (Action<int, RateLimiter, byte[], int, int>)Delegate.CreateDelegate(
                typeof(Action<int, RateLimiter, byte[], int, int>),
                carrier,
                method);
        }

        private static Action<TransportEvent> BindEnqueueEvent(LiteNetLibCarrier carrier)
        {
            var method = typeof(LiteNetLibCarrier).GetMethod(
                "EnqueueEvent",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null,
                "EnqueueEvent must remain the bounded-queue entry point");
            return (Action<TransportEvent>)Delegate.CreateDelegate(
                typeof(Action<TransportEvent>),
                carrier,
                method);
        }

        private static Action<int, byte[], int, int> BindSnapshotHandler(
            LiteNetLibCarrier carrier)
        {
            var method = typeof(LiteNetLibCarrier).GetMethod(
                "HandleSnapshotReceive",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null,
                "HandleSnapshotReceive must remain the snapshot receive entry point");
            return (Action<int, byte[], int, int>)Delegate.CreateDelegate(
                typeof(Action<int, byte[], int, int>),
                carrier,
                method);
        }

        private static Action<int, int> BindRemovePeerState(LiteNetLibCarrier carrier)
        {
            var method = typeof(LiteNetLibCarrier).GetMethod(
                "RemovePeerState",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null,
                "RemovePeerState must remain the per-peer teardown path");
            return (Action<int, int>)Delegate.CreateDelegate(
                typeof(Action<int, int>),
                carrier,
                method);
        }

        private static byte[] BuildSnapshotDatagram(
            ushort messageId,
            ushort sequence,
            ushort fragmentIndex,
            ushort fragmentCount,
            int payloadLength)
        {
            var datagram = new byte[8 + payloadLength];
            datagram[0] = (byte)(messageId & 0xFF);
            datagram[1] = (byte)((messageId >> 8) & 0xFF);
            datagram[2] = (byte)(sequence & 0xFF);
            datagram[3] = (byte)((sequence >> 8) & 0xFF);
            datagram[4] = (byte)(fragmentIndex & 0xFF);
            datagram[5] = (byte)((fragmentIndex >> 8) & 0xFF);
            datagram[6] = (byte)(fragmentCount & 0xFF);
            datagram[7] = (byte)((fragmentCount >> 8) & 0xFF);
            return datagram;
        }
    }
}
