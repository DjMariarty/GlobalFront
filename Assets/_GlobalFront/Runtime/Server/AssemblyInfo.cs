using System.Runtime.CompilerServices;

// TransportBufferPool stays `internal` on purpose: it is an implementation detail
// of the receive path, not a general-purpose allocator. The EditMode suite needs
// its Rent/Return accounting to prove that every terminal path (rate-limit drop,
// queue overflow, teardown) actually hands its buffer back — the audit P2-01
// regression witness (Block 4).
[assembly: InternalsVisibleTo("GlobalFront.Tests.EditMode")]
