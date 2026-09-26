using System.Runtime.CompilerServices;

// The overlay render pass keeps its instance buffers and the procedural resource
// factory below `internal` on purpose — a caller that reached in could put a NaN in
// front of the GPU — but the EditMode suite has to read them to prove the step 3.4
// remediation holds (per-chunk isolation, ownership of the created material).
[assembly: InternalsVisibleTo("GlobalFront.Tests.EditMode")]
