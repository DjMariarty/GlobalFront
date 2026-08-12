using System;

namespace GlobalFront.Server.Snapshot
{
    /// <summary>
    /// Thrown when snapshot serialization or deserialization encounters an error.
    /// This includes:
    /// - Unsupported protocol version
    /// - Malformed packet data
    /// - Insufficient packet length
    /// - Invalid field values
    /// </summary>
    public sealed class SnapshotSerializationException : Exception
    {
        public SnapshotSerializationException(string message)
            : base(message)
        {
        }

        public SnapshotSerializationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}