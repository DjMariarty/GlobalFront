# LiteNetLib provenance probe

Independent, repeatable verification of the pinned
`Assets/_GlobalFront/ThirdParty/LiteNetLib/LiteNetLib.dll`.

The probe uses Mono.Cecil (shipped with Unity) to report:

- file size and SHA-256;
- assembly identity (`AssemblyFileVersion`,
  `AssemblyInformationalVersion`, target framework, debuggable mode);
- public type/method/field/property/event counts and the 611-member
  convention used by `BUILD_PROVENANCE.md`;
- a deterministic public-API signature hash;
- a deterministic IL instruction-stream hash;
- the simulation methods and their `[Conditional]` attributes;
- an optional method-level comparison against another DLL.

## Run

```powershell
powershell -ExecutionPolicy Bypass -File `
  Artifacts\Tools\LiteNetLibProvenance\verify.ps1
```

The script locates `dotnet.exe` and `Mono.Cecil.dll` in the Unity editor
installation. Override them if needed:

```powershell
powershell -ExecutionPolicy Bypass -File `
  Artifacts\Tools\LiteNetLibProvenance\verify.ps1 `
  -Dotnet "C:\Path\To\dotnet.exe" `
  -MonoCecil "C:\Path\To\Mono.Cecil.dll"
```

Compare the pinned `SIMULATE_NETWORK` build with the official v1.3.5 NuGet
`netstandard2.0` binary:

```powershell
powershell -ExecutionPolicy Bypass -File `
  Artifacts\Tools\LiteNetLibProvenance\verify.ps1 `
  -Compare "C:\Path\To\nuget135\lib\netstandard2.0\LiteNetLib.dll"
```

## Pinned values

| Metric | Value |
| --- | --- |
| Size | 111,616 bytes |
| SHA-256 | `D92942B58CADB8C1E891275B30454C4E0E52E864B0582D789795FEB724A05D15` |
| API signature hash | `716E8912AEA2DB249F869BF4A881C704A730D53F871713A733D336C6A36699D6` |
| IL hash | `F1B8AD5D3F13C95172DB486A2495C27F3D3AECD7411242C7A806CD5A4594DBE3` |
| Public API members | 611 (raw entries incl. accessors/events: 702) |

The pinned SHA-256 identifies the audited file. A rebuild from the same
commit and command reproduces the same size and IL/API hashes, but not
necessarily the same PE bytes: the MVID and debug-directory identity differ
per build host. The tool reports the semantic hashes so an auditor can
distinguish code changes from build-identity metadata.

The hash algorithm is implemented in `Program.cs`: sorted API signatures
and sorted IL instruction lines, SHA-256 over UTF-8 lines. Keep the algorithm
stable when extending the tool, otherwise the recorded hashes change.
