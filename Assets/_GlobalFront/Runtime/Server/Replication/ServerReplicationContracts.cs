using System;
using GlobalFront.Core.Model;
using GlobalFront.Server.Transport;

namespace GlobalFront.Server.Replication
{
    /// <summary>
    /// Non-allocating view of the authoritative world for the replication
    /// emitter (Phase 2.6, step 2.6.4). The production source wraps
    /// <see cref="MatchServer.CopySnapshots"/>; tests substitute a
    /// preallocated fake so the hot path can be proven zero-GC.
    /// </summary>
    public interface IServerSnapshotSource
    {
        /// <summary>
        /// Copies the whole world into <paramref name="destination"/> in
        /// deterministic entity-id order. Returns the record count, or -1 when
        /// the destination cannot hold the world (nothing is copied then).
        /// </summary>
        int CopySnapshots(Span<ServerUnitSnapshot> destination);
    }

    /// <summary>Production snapshot source over the authoritative match.</summary>
    public readonly struct MatchServerSnapshotSource : IServerSnapshotSource
    {
        private readonly MatchServer _server;

        public MatchServerSnapshotSource(MatchServer server)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        public int CopySnapshots(Span<ServerUnitSnapshot> destination) =>
            _server.CopySnapshots(destination);
    }

    /// <summary>
    /// Outbound/inbound seam between the replication emitter and the transport
    /// layer (Phase 2.6, step 2.6.4). Deltas and keyframe slices leave as
    /// opaque C2 payloads addressed to one session; client feedback (acks and
    /// repair requests) arrives as decoded C0 payload slices. Tests substitute
    /// a non-allocating fake to keep the emitter hot path GC-measurable.
    /// </summary>
    public interface IReplicationTransport
    {
        /// <summary>
        /// Sends one opaque payload to one session on the snapshot channel.
        /// <paramref name="envelopeTick"/> is the C2 ordering key: the target
        /// tick for deltas, <c>(Tick &lt;&lt; 8) | SliceIndex</c> for keyframe
        /// slices (R&amp;D §3.4).
        /// </summary>
        bool SendToSession(SessionId session, ulong envelopeTick, byte[] buffer, int length);

        /// <summary>Raised when a session finished handshake.</summary>
        event Action<SessionId, MatchId, PlayerId> SessionAttached;

        /// <summary>Raised when a session is lost or closed.</summary>
        event Action<SessionId, TransportDisconnectReason> SessionDetached;

        /// <summary>
        /// Raised for a valid client feedback payload (SnapshotAck or
        /// ReplicationRequest). The payload is valid only inside the handler
        /// call.
        /// </summary>
        event Action<ReplicationFeedbackMessage> FeedbackReceived;
    }

    /// <summary>
    /// Production transport seam over <see cref="ServerTransportHost"/>.
    /// Compose it (and the emitter) BEFORE clients connect: attachments that
    /// happened before the emitter existed are not replayed.
    /// </summary>
    public sealed class TransportReplicationAdapter : IReplicationTransport
    {
        private readonly ServerTransportHost _host;

        public TransportReplicationAdapter(ServerTransportHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _host.SessionAttached += OnSessionAttached;
            _host.SessionDetached += OnSessionDetached;
            _host.ReplicationFeedbackReceived += OnFeedback;
        }

        public bool SendToSession(SessionId session, ulong envelopeTick, byte[] buffer, int length) =>
            _host.SendSnapshot(session, envelopeTick, buffer, length);

        public event Action<SessionId, MatchId, PlayerId> SessionAttached;

        public event Action<SessionId, TransportDisconnectReason> SessionDetached;

        public event Action<ReplicationFeedbackMessage> FeedbackReceived;

        private void OnSessionAttached(SessionId session, MatchId match, PlayerId player) =>
            SessionAttached?.Invoke(session, match, player);

        private void OnSessionDetached(SessionId session, TransportDisconnectReason reason) =>
            SessionDetached?.Invoke(session, reason);

        private void OnFeedback(ReplicationFeedbackMessage message) =>
            FeedbackReceived?.Invoke(message);
    }
}
