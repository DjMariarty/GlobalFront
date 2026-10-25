# LiteNetLib DLL Build Provenance

Audited artifact: `Assets/_GlobalFront/ThirdParty/LiteNetLib/LiteNetLib.dll`
Audit date: 2026-10-26
Owner: GlobalFront transport (Phase 4, Step 4.6.4 / OD-43)

This build exists so the deterministic impairment tests can physically drop
and delay UDP traffic in a **Release** player. It is otherwise stock
LiteNetLib 1.3.5.

## Source

| Field | Value |
| --- | --- |
| Repository | https://github.com/RevenantX/LiteNetLib |
| Build source commit | `cf54c7d7a15a45bd2460cd4a460807329ee7026a` ("drop net471", 2025-12-16) |
| Release line | 1.3.5 |
| Embedded `AssemblyInformationalVersion` | `1.0.0+cf54c7d7a15a45bd2460cd4a460807329ee7026a` |
| Embedded `AssemblyFileVersion` | `1.3.5` |
| Target framework | `netstandard2.0` |
| Configuration | `Release` |
| Licence | MIT (Copyright 2024 Ruslan Pyrch) |

Tag resolution: the GitHub tag `1.3.5` points at
`2c01ac0b413a3461a7b2cd00f47df8b6e152a7db`, while the official NuGet 1.3.5
`netstandard2.0` binary embeds the same
`1.0.0+cf54c7d7a15a45bd2460cd4a460807329ee7026a` suffix as this DLL: the
package was built two commits after the tag (`70ac84d` "set version",
`cf54c7d7` "drop net471"). Those two commits only change target-framework
lists in the project files (they drop `net471`); no `.cs` source file
differs. The build commit is therefore recorded separately from the tag.

## Build command

From the repository root, with the .NET SDK:

```bash
dotnet build LiteNetLib/LiteNetLib.csproj -c Release -f netstandard2.0 -p:SimulateNetwork=true
```

The verification rebuild used the .NET SDK bundled with Unity
(`6000.6.2f1/Editor/Data/DotNetSdk`, SDK 8.0.318).

## Why `SimulateNetwork=true` is required

Stock Release builds compile the simulation entry points out. In
`LiteNetLib/NetManager.cs` at the pinned commit, the receive-side simulation
methods carry:

```csharp
[Conditional("DEBUG"), Conditional("SIMULATE_NETWORK")]
```

`[Conditional]` removes the *calls* from the IL when neither symbol is
defined. The stock Release property group defines only `TRACE`, so
`SimulatePacketLoss` and `SimulateLatency` become inert flags. The project
file appends the symbol when the property is passed:

```xml
<PropertyGroup Condition="'$(SimulateNetwork)'=='true'">
  <DefineConstants>$(DefineConstants);SIMULATE_NETWORK</DefineConstants>
</PropertyGroup>
```

Binary witness: this DLL contains the private
`HandleSimulateOutboundLatency` / `HandleSimulateOutboundPacketLoss` wrappers
(five simulation methods in total), while the official Release NuGet binary
contains three. The PlayMode test
`LiveUDP_UnderPacketLossAndLatency_HandshakeAndCommandSucceed` additionally
proves physical effect with a 100% server-to-client loss pulse, because a
flag read alone cannot distinguish a working simulator from a compiled-out
one.

## Verified binary metrics

| Metric | Value |
| --- | --- |
| File size | **111,616 bytes** |
| SHA-256 | **D92942B58CADB8C1E891275B30454C4E0E52E864B0582D789795FEB724A05D15** |
| Public API members | **611 / 611**, identical to official 1.3.5 |
| Public API signature hash | `716E8912AEA2DB249F869BF4A881C704A730D53F871713A733D336C6A36699D6` |
| IL instruction-stream hash | `F1B8AD5D3F13C95172DB486A2495C27F3D3AECD7411242C7A806CD5A4594DBE3` |
| Assembly version | `1.3.5.0` |
| TargetFramework attribute | `.NETStandard,Version=v2.0` |
| Debuggable attribute | `2` (Release; sequence-point ignoring) |
| Public surface totals | 58 types (44 top-level, 14 nested), 458 public methods, 120 public fields, 54 public properties, 12 public events |

The 611 count uses the API-audit convention: public types + non-accessor
methods + constructors + fields + properties. Concretely:
`58 types + 341 non-accessor methods + 38 constructors + 120 fields + 54 properties = 611`.
Property and event accessors are folded into their owning member, and event
members are not counted separately. Raw entries including every accessor and
event are 702.

## Compatibility with official v1.3.5

Downloaded from NuGet (`LiteNetLib` 1.3.5, `lib/netstandard2.0`):

| Binary | Size | SHA-256 | Public API | IL hash |
| --- | --- | --- | --- | --- |
| This build (`SIMULATE_NETWORK`) | 111,616 | `D92942B5…` | 611 / API `716E8912…` | `F1B8AD5D…` |
| Official NuGet 1.3.5 | 111,104 | `572B27C5…` | 611 / API `716E8912…` | `0CE9214A…` |

The public API is identical in signature and count. The IL differs only
where `SIMULATE_NETWORK` changes compilation: two extra private outbound
wrappers (772 vs 770 methods), simulation hooks present in `SendRaw`,
`OnMessageReceived`, `PollEvents`, `UpdateLogic` and `Stop`, and the
compiler-generated local-function numbering those additions shift
(`145`↔`147`, `192`↔`194`). No public signature and no non-simulation
behaviour differs.

## Reproducibility statement

Rebuilding `cf54c7d7` with the command above and SDK 8.0.318 reproduces:

- the same file size (111,616 bytes);
- the same public API hash (`716E8912…`) and 611 members;
- the same IL instruction-stream hash (`F1B8AD5D…`);
- the same five simulation methods and the same `.NETStandard 2.0` Release
  attributes.

The rebuilt file's SHA-256 is `EA170DF3B3E58024229338CF54060E390D21483F883A54DD670A7ED3DBFF432B`,
not the pinned `D92942B5…`. The two files differ in 399 bytes, confined to
PE/build identity metadata: the module MVID (`d2612f41-…` shipped vs
`de994b22-…` rebuilt), the debug directory/PDB identity and related header
fields. Source semantics, IL and public API are identical. A byte-identical
PE file across build hosts is therefore not claimed; the pinned SHA-256
identifies the audited artifact, and the IL/API hashes allow independent
semantic verification.

## Verification commands

```bash
# Source checkout
git clone https://github.com/RevenantX/LiteNetLib.git
git checkout cf54c7d7a15a45bd2460cd4a460807329ee7026a

# Build
dotnet build LiteNetLib/LiteNetLib.csproj -c Release -f netstandard2.0 \
  -p:SimulateNetwork=true

# Artifact identity
sha256sum LiteNetLib/bin/Release/netstandard2.0/LiteNetLib.dll
```

The audit probe used Mono.Cecil (shipped with Unity) to enumerate public
members and hash the IL instruction streams of the shipped, official and
rebuilt binaries.
