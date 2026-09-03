using System;
using GlobalFront.Core.Snapshot;
using GlobalFront.Server.Transport;

namespace GlobalFront.Client.Replication
{
    /// <summary>
    /// Kind of the feedback record the bridge sends on C0. Maps onto the
    /// transport message types <see cref="TransportMessageType.SnapshotAck"/>
    /// and <see cref="TransportMessageType.ReplicationRequest"/>.
    /// </summary>
    public enum ReplicationFeedbackKind : byte
    {
        Ack = 1,
        Request = 2
    }

    /// <summary>
    /// Uplink seam between the replication bridge and the transport endpoint
    /// (Phase 2.6, step 2.6.4). Production wraps
    /// <see cref="ClientTransportEndpoint"/>; tests substitute a
    /// non-allocating fake so the hot path can be proven zero-GC.
    /// </summary>
    public interface IReplicationUplink
    {
        /// <summary>
        /// Queues one encoded feedback record for delivery on the reliable
        /// control channel. The payload buffer is caller-owned and reused.
        /// </summary>
        bool TrySendFeedback(ReplicationFeedbackKind kind, byte[] payload, int length);

        /// <summary>Raised for every C2 snapshot payload with its envelope tick.</summary>
        event Action<ulong, byte[]> SnapshotPayloadReceived;
    }

    /// <summary>Production uplink over the client transport endpoint.</summary>
    public sealed class ClientTransportUplink : IReplicationUplink
    {
        private readonly ClientTransportEndpoint _endpoint;
        private readonly Action<ulong, byte[]> _onSnapshot;

        public ClientTransportUplink(ClientTransportEndpoint endpoint)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _onSnapshot = OnSnapshot;
            _endpoint.SnapshotReceived += _onSnapshot;
        }

        public event Action<ulong, byte[]> SnapshotPayloadReceived;

        public bool TrySendFeedback(ReplicationFeedbackKind kind, byte[] payload, int length)
        {
            var type = kind == ReplicationFeedbackKind.Ack
                ? TransportMessageType.SnapshotAck
                : TransportMessageType.ReplicationRequest;
            return _endpoint.TrySendReplicationFeedback(type, payload, length);
        }

        private void OnSnapshot(ulong tick, byte[] payload) =>
            SnapshotPayloadReceived?.Invoke(tick, payload);
    }

    /// <summary>
    /// Protocol-significant configuration of the client replication bridge
    /// (Phase 2.6, step 2.6.4). Decode sinks must never be smaller than the
    /// sender's budgets: a legal single-part delta never exceeds the 8 KB
    /// payload bound, and slices must fit the slice budget the server uses.
    /// </summary>
    public readonly struct ClientTransportReplicationBridgeConfig
        : IEquatable<ClientTransportReplicationBridgeConfig>
    {
        public const int DefaultMaxKeyframeUnits = ClientReplicationWorld.DefaultCapacity;
        public const int DefaultMaxParts = 256;
        public const int DefaultMaxSlicePayloadBytes = 16384;
        public const int DefaultAddSink = 256;
        public const int DefaultUpdateSink = 4096;
        public const int DefaultRemoveSink = 256;

        public ClientTransportReplicationBridgeConfig(
            int maxKeyframeUnits,
            int maxParts,
            int maxSlicePayloadBytes,
            int addSink,
            int updateSink,
            int removeSink)
        {
            if (maxKeyframeUnits < 1 || maxParts < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxKeyframeUnits));
            }

            if (maxSlicePayloadBytes < KeyframeSliceCodec.HeaderSizeBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSlicePayloadBytes));
            }

            if (addSink < 1 || updateSink < 1 || removeSink < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(addSink));
            }

            MaxKeyframeUnits = maxKeyframeUnits;
            MaxParts = maxParts;
            MaxSlicePayloadBytes = maxSlicePayloadBytes;
            AddSink = addSink;
            UpdateSink = updateSink;
            RemoveSink = removeSink;
        }

        /// <summary>Largest keyframe the assembly buffer can hold.</summary>
        public int MaxKeyframeUnits { get; }

        /// <summary>Largest part count the assembly tracker can address.</summary>
        public int MaxParts { get; }

        /// <summary>Slice payload budget the decoder accepts (must be ≥ the sender's).</summary>
        public int MaxSlicePayloadBytes { get; }

        public int AddSink { get; }

        public int UpdateSink { get; }

        public int RemoveSink { get; }

        public static ClientTransportReplicationBridgeConfig Default =>
            new ClientTransportReplicationBridgeConfig(
                DefaultMaxKeyframeUnits,
                DefaultMaxParts,
                DefaultMaxSlicePayloadBytes,
                DefaultAddSink,
                DefaultUpdateSink,
                DefaultRemoveSink);

        public bool Equals(ClientTransportReplicationBridgeConfig other) =>
            MaxKeyframeUnits == other.MaxKeyframeUnits &&
            MaxParts == other.MaxParts &&
            MaxSlicePayloadBytes == other.MaxSlicePayloadBytes &&
            AddSink == other.AddSink &&
            UpdateSink == other.UpdateSink &&
            RemoveSink == other.RemoveSink;

        public override bool Equals(object obj) =>
            obj is ClientTransportReplicationBridgeConfig other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            MaxKeyframeUnits, MaxParts, MaxSlicePayloadBytes, AddSink, UpdateSink, RemoveSink);
    }

    /// <summary>
    /// Client-side network bridge of the replication stream (Phase 2.6, step
    /// 2.6.4, ADR-010): the missing link between the transport endpoint and
    /// the <see cref="ClientReplicationReceiver"/>.
    ///
    /// Inbound (C2 payloads surfaced by the endpoint): the first payload byte
    /// classifies the packet — a keyframe slice (0x02) is assembled with its
    /// <c>(KeyframeSeq, PartIndex)</c> peers into the full unit set and then
    /// handed to <see cref="ClientReplicationReceiver.ReceiveKeyframe"/>, a
    /// delta packet (0x03) is decoded into preallocated sinks and handed to
    /// <see cref="ClientReplicationReceiver.ReceiveDelta"/> with the keyframe
    /// generation of the last assembled baseline (delta wire v1 keeps the
    /// reference off the wire; the bridge supplies the value the sender
    /// used), and the legacy full snapshot (0x01) is ignored.
    ///
    /// Outbound (C0): every pump drains the receiver's cadence-gated
    /// acknowledgement into a 34-byte <see cref="SnapshotAck"/> record and the
    /// FSM's repair schedule into wire requests
    /// (<see cref="ReplicationRequestWire"/>), keeping the uplink at 34 B ×
    /// 10 Hz plus rare requests.
    ///
    /// Timing is injected as a monotonic <c>nowMs</c> per pump, exactly like
    /// <c>ClientTransportEndpoint.Pump(long)</c>; payloads dispatched between
    /// pumps are stamped with the last pumped time. Zero-GC: all buffers are
    /// preallocated, assembly state lives in fixed arrays, and only the
    /// transport carrier itself (outside this layer) allocates per datagram.
    /// </summary>
    public sealed class ClientTransportReplicationBridge
    {
        private readonly IReplicationUplink _uplink;
        private readonly ClientReplicationReceiver _receiver;
        private readonly ClientTransportReplicationBridgeConfig _config;
        private readonly DeltaAddRecord[] _addSink;
        private readonly DeltaUpdateRecord[] _updateSink;
        private readonly DeltaRemoveRecord[] _removeSink;
        private readonly DeltaAddRecord[] _sliceScratch;
        private readonly DeltaAddRecord[] _keyframeUnits;
        private readonly bool[] _receivedParts;
        private readonly byte[] _ackBuffer = new byte[SnapshotAckCodec.SizeBytes];
        private readonly byte[] _requestBuffer = new byte[ReplicationRequestCodec.SizeBytes];

        private long _lastPumpMs;
        private bool _assemblyActive;
        private ushort _assemblySeq;
        private ulong _assemblyTick;
        private int _assemblyPartCount;
        private int _assemblyReceivedCount;
        private int _assemblyUnitCount;
        private ushort _assembledKeyframeSeq;

        // Diagnostics.
        private int _assembledKeyframeCount;
        private int _assembledSliceCount;
        private int _droppedStaleSliceCount;
        private int _droppedDuplicateSliceCount;
        private int _malformedPayloadCount;
        private int _ignoredLegacySnapshotCount;
        private int _forwardedDeltaCount;
        private int _multipartDeltaCount;
        private int _sentAckCount;
        private int _sentRequestCount;

        public ClientTransportReplicationBridge(IReplicationUplink uplink, ClientReplicationReceiver receiver)
            : this(uplink, receiver, ClientTransportReplicationBridgeConfig.Default)
        {
        }

        public ClientTransportReplicationBridge(
            IReplicationUplink uplink,
            ClientReplicationReceiver receiver,
            in ClientTransportReplicationBridgeConfig config)
        {
            _uplink = uplink ?? throw new ArgumentNullException(nameof(uplink));
            _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
            _config = config;

            _addSink = new DeltaAddRecord[config.AddSink];
            _updateSink = new DeltaUpdateRecord[config.UpdateSink];
            _removeSink = new DeltaRemoveRecord[config.RemoveSink];
            _sliceScratch = new DeltaAddRecord[Math.Max(1,
                KeyframeSliceCodec.MaxRecordsForSliceBudget(config.MaxSlicePayloadBytes))];
            _keyframeUnits = new DeltaAddRecord[config.MaxKeyframeUnits];
            _receivedParts = new bool[config.MaxParts];

            _uplink.SnapshotPayloadReceived += OnSnapshotPayload;
        }

        public ClientReplicationReceiver Receiver => _receiver;

        public ClientTransportReplicationBridgeConfig Config => _config;

        /// <summary>Keyframe generation the bridge currently forwards deltas with.</summary>
        public ushort AssembledKeyframeSeq => _assembledKeyframeSeq;

        public int AssembledKeyframeCount => _assembledKeyframeCount;
        public int AssembledSliceCount => _assembledSliceCount;
        public int DroppedStaleSliceCount => _droppedStaleSliceCount;
        public int DroppedDuplicateSliceCount => _droppedDuplicateSliceCount;
        public int MalformedPayloadCount => _malformedPayloadCount;
        public int IgnoredLegacySnapshotCount => _ignoredLegacySnapshotCount;
        public int ForwardedDeltaCount => _forwardedDeltaCount;
        public int MultipartDeltaCount => _multipartDeltaCount;
        public int SentAckCount => _sentAckCount;
        public int SentRequestCount => _sentRequestCount;

        /// <summary>
        /// Drives the receiver's repair schedule and drains its feedback:
        /// call once per pump with the transport clock value.
        /// </summary>
        public void Pump(long nowMs)
        {
            _lastPumpMs = nowMs;
            _receiver.Update(nowMs);

            while (_receiver.TryTakeAck(nowMs, out var ack))
            {
                if (SnapshotAckCodec.TryEncode(ack, _ackBuffer, out var written) ==
                    SnapshotAckCodecResult.Ok &&
                    _uplink.TrySendFeedback(ReplicationFeedbackKind.Ack, _ackBuffer, written))
                {
                    _sentAckCount++;
                }
            }

            while (_receiver.TryTakeRequest(out var request))
            {
                var kind = request.Kind == ReplicationRequestKind.SnapshotRequest
                    ? ReplicationRequestWireKind.SnapshotRequest
                    : ReplicationRequestWireKind.DeltaResume;
                var wire = new ReplicationRequestWire(
                    kind,
                    (byte)Math.Min(request.Attempt, byte.MaxValue),
                    request.LastAppliedTick,
                    request.BaseKeyframeTick,
                    request.KeyframeTick);
                if (ReplicationRequestCodec.TryEncode(wire, _requestBuffer, out var written) ==
                    ReplicationRequestCodecResult.Ok &&
                    _uplink.TrySendFeedback(ReplicationFeedbackKind.Request, _requestBuffer, written))
                {
                    _sentRequestCount++;
                }
            }
        }

        /// <summary>
        /// Feeds one C2 snapshot payload to the replication pipeline. Also the
        /// direct entry point for tests; the transport path goes through
        /// <see cref="IReplicationUplink.SnapshotPayloadReceived"/>, which
        /// stamps <see cref="Pump"/>'s time.
        /// </summary>
        public void HandleSnapshotPayload(ulong envelopeTick, byte[] payload, int length, long nowMs)
        {
            if (payload == null || length < 1 || length > payload.Length)
            {
                _malformedPayloadCount++;
                return;
            }

            switch (payload[0])
            {
                case KeyframeSliceCodec.MessageType:
                    HandleKeyframeSlice(payload, length, nowMs);
                    return;
                case DeltaSnapshotProtocol.MessageTypeDelta:
                    HandleDelta(payload, length, nowMs);
                    return;
                default:
                    // Legacy full snapshots (0x01) and everything unknown are
                    // not part of the 2.6 stream.
                    _ignoredLegacySnapshotCount++;
                    return;
            }
        }

        private void OnSnapshotPayload(ulong envelopeTick, byte[] payload) =>
            HandleSnapshotPayload(envelopeTick, payload, payload?.Length ?? 0, _lastPumpMs);

        // ------------------------------------------------------------------
        // Delta path
        // ------------------------------------------------------------------

        private void HandleDelta(byte[] payload, int length, long nowMs)
        {
            var result = DeltaSnapshotWireCodec.TryDecode(
                payload.AsSpan(0, length),
                out var header,
                _addSink,
                _updateSink,
                _removeSink);
            if (result != DeltaCodecResult.Ok)
            {
                _malformedPayloadCount++;
                return;
            }

            if (header.PartCount > 1)
            {
                // The emitter never splits deltas (an oversized merge falls
                // back to a keyframe); the receiver's documented multipart
                // report keeps the contract visible either way.
                _multipartDeltaCount++;
            }

            _forwardedDeltaCount++;
            _ = _receiver.ReceiveDelta(
                nowMs,
                in header,
                _assembledKeyframeSeq,
                _addSink,
                _updateSink,
                _removeSink);
        }

        // ------------------------------------------------------------------
        // Keyframe slice assembly
        // ------------------------------------------------------------------

        private void HandleKeyframeSlice(byte[] payload, int length, long nowMs)
        {
            var result = KeyframeSliceCodec.TryDecodeSlice(
                payload.AsSpan(0, length),
                _sliceScratch,
                out var header,
                out var recordsRead);
            if (result != KeyframeSliceCodecResult.Ok)
            {
                _malformedPayloadCount++;
                return;
            }

            if (_assemblyActive && header.KeyframeSeq != _assemblySeq)
            {
                if (IsNewerSeq(header.KeyframeSeq, _assemblySeq))
                {
                    ResetAssembly();
                }
                else
                {
                    _droppedStaleSliceCount++;
                    return;
                }
            }

            if (!_assemblyActive)
            {
                if (header.PartCount > _config.MaxParts ||
                    header.TotalUnitCount > (uint)_config.MaxKeyframeUnits)
                {
                    _malformedPayloadCount++;
                    return;
                }

                _assemblyActive = true;
                _assemblySeq = header.KeyframeSeq;
                _assemblyTick = header.Tick;
                _assemblyPartCount = header.PartCount;
                _assemblyReceivedCount = 0;
                _assemblyUnitCount = 0;
                Array.Clear(_receivedParts, 0, _receivedParts.Length);
            }

            if (header.Tick != _assemblyTick ||
                header.PartIndex >= _assemblyPartCount ||
                _assemblyUnitCount + recordsRead > _keyframeUnits.Length)
            {
                _malformedPayloadCount++;
                ResetAssembly();
                return;
            }

            if (_receivedParts[header.PartIndex])
            {
                _droppedDuplicateSliceCount++;
                return;
            }

            for (var index = 0; index < recordsRead; index++)
            {
                _keyframeUnits[_assemblyUnitCount++] = _sliceScratch[index];
            }

            _receivedParts[header.PartIndex] = true;
            _assemblyReceivedCount++;
            _assembledSliceCount++;

            if (_assemblyReceivedCount < _assemblyPartCount)
            {
                return;
            }

            if (_assemblyUnitCount != (int)header.TotalUnitCount)
            {
                // The parts disagree with the declared total: the assembly is
                // corrupt and must never reach the receiver.
                _malformedPayloadCount++;
                ResetAssembly();
                return;
            }

            if (!IsNewerSeq(_assemblySeq, _assembledKeyframeSeq))
            {
                // A completed assembly at or behind the installed baseline is
                // a late duplicate: installing it would rewind the client.
                _droppedStaleSliceCount++;
                ResetAssembly();
                return;
            }

            var outcome = _receiver.ReceiveKeyframe(
                nowMs,
                _assemblySeq,
                _assemblyTick,
                _keyframeUnits.AsSpan(0, _assemblyUnitCount));
            if (outcome == ClientReplicationOutcome.BaselineInstalled)
            {
                _assembledKeyframeSeq = _assemblySeq;
                _assembledKeyframeCount++;
            }

            ResetAssembly();
        }

        private void ResetAssembly()
        {
            _assemblyActive = false;
            _assemblySeq = 0;
            _assemblyTick = 0;
            _assemblyPartCount = 0;
            _assemblyReceivedCount = 0;
            _assemblyUnitCount = 0;
        }

        /// <summary>Serial-number comparison with wrap-around at 0x8000.</summary>
        private static bool IsNewerSeq(ushort candidate, ushort current) =>
            candidate != current && (ushort)(candidate - current) < 0x8000;
    }
}
