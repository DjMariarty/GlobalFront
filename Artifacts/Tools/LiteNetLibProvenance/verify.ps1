[CmdletBinding()]
param(
    [string]$Dll = "",
    [string]$Compare = "",
    [string]$Dotnet = "",
    [string]$MonoCecil = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
if (-not $Dll)
{
    $Dll = Join-Path $repoRoot "Assets\_GlobalFront\ThirdParty\LiteNetLib\LiteNetLib.dll"
}

if (-not (Test-Path $Dll))
{
    throw "DLL not found: $Dll"
}

$expectedSize = 111616
$expectedSha = "D92942B58CADB8C1E891275B30454C4E0E52E864B0582D789795FEB724A05D15"
$expectedApiHash = "716E8912AEA2DB249F869BF4A881C704A730D53F871713A733D336C6A36699D6"
$expectedIlHash = "F1B8AD5D3F13C95172DB486A2495C27F3D3AECD7411242C7A806CD5A4594DBE3"

$actual = Get-Item $Dll
$actualSha = (Get-FileHash -Algorithm SHA256 -Path $Dll).Hash.ToUpperInvariant()
if ($actual.Length -ne $expectedSize -or $actualSha -ne $expectedSha)
{
    throw "Pinned artifact mismatch: size=$($actual.Length), sha256=$actualSha"
}

if (-not $Dotnet -or -not $MonoCecil)
{
    $unityRoot = Join-Path $env:ProgramFiles "Unity\Hub\Editor"
    if (Test-Path $unityRoot)
    {
        foreach ($editor in Get-ChildItem $unityRoot -Directory | Sort-Object Name -Descending)
        {
            $dotnetCandidate = Join-Path $editor.FullName "Editor\Data\DotNetSdk\dotnet.exe"
            $cecilCandidate = Join-Path $editor.FullName "Editor\Data\il2cpp\build\deploy\Mono.Cecil.dll"
            if ((-not $Dotnet) -and (Test-Path $dotnetCandidate))
            {
                $Dotnet = $dotnetCandidate
            }

            if ((-not $MonoCecil) -and (Test-Path $cecilCandidate))
            {
                $MonoCecil = $cecilCandidate
            }

            if ($Dotnet -and $MonoCecil)
            {
                break
            }
        }
    }
}

if (-not $Dotnet -or -not (Test-Path $Dotnet))
{
    throw "Unity dotnet.exe not found. Pass -Dotnet <path>."
}

if (-not $MonoCecil -or -not (Test-Path $MonoCecil))
{
    throw "Mono.Cecil.dll not found. Pass -MonoCecil <path>."
}

$project = Join-Path $PSScriptRoot "LiteNetLibProvenance.csproj"
$probeArgs = @(
    "run",
    "--project", $project,
    "-p:MonoCecilPath=$MonoCecil",
    "--",
    $Dll
)
if ($Compare)
{
    $probeArgs += $Compare
}

$output = & $Dotnet @probeArgs
if ($LASTEXITCODE -ne 0)
{
    throw "LiteNetLibProvenance failed with exit code $LASTEXITCODE"
}

$output | Write-Host

function Get-ProbeValue([string]$Key)
{
    $line = $output | Where-Object { $_ -like "$Key=*" } | Select-Object -First 1
    if (-not $line)
    {
        throw "Probe did not report $Key"
    }

    return $line.Substring($Key.Length + 1)
}

$apiHash = Get-ProbeValue "apiHash"
$ilHash = Get-ProbeValue "ilHash"
$members = Get-ProbeValue "publicApiMembers"
if ($apiHash -ne $expectedApiHash)
{
    throw "API hash mismatch: $apiHash"
}

if ($ilHash -ne $expectedIlHash)
{
    throw "IL hash mismatch: $ilHash"
}

if ($members -ne "611")
{
    throw "Public API member count mismatch: $members"
}

Write-Host "Provenance verification passed: size=$expectedSize, sha256=$expectedSha, apiHash=$apiHash, ilHash=$ilHash, publicApiMembers=611"
