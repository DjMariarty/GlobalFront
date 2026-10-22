namespace GlobalFront.Client
{
    /// <summary>
    /// How a tactical slice obtains its authoritative state (Phase 4, step
    /// 4.2, ADR-014 / OD-38):
    /// <list type="bullet">
    /// <item><see cref="LocalLoopback"/> — the in-process host the slice has
    /// always owned; the default and the only mode with no socket.</item>
    /// <item><see cref="ListenServer"/> — reserved for a listen server; the
    /// slice does not implement it yet and never selects it implicitly.</item>
    /// <item><see cref="DedicatedClient"/> — no local host: ticks and unit
    /// positions arrive exclusively from a dedicated server through the
    /// replication receiver, and commands leave through the transport.</item>
    /// </list>
    /// </summary>
    public enum SliceNetworkMode : byte
    {
        LocalLoopback = 0,
        ListenServer = 1,
        DedicatedClient = 2
    }
}
