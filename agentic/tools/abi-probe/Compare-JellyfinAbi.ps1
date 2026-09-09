<#
.SYNOPSIS
    Diffs the public API surface of two Jellyfin package versions.

.DESCRIPTION
    Answers "does this Jellyfin version change anything the plugin binds to?" with evidence rather
    than assumption. A clean `dotnet build` proves the plugin still compiles; it does not tell you
    what moved underneath it, which members were dropped, or whether a signature changed in a way a
    future edit would trip over.

    For each version the script materialises the full dependency closure into a temporary directory
    (via `dotnet publish` of a throwaway project that references Jellyfin.Controller and
    Jellyfin.Model), dumps the public and protected API surface of the Jellyfin assemblies with
    AbiProbe, and diffs the two dumps.

    Nothing is written inside the repository and no Jellyfin assembly is left behind: the working
    directory defaults to a new folder under the system temp path and is removed unless -KeepWork
    is given.

.PARAMETER From
    Baseline package version, e.g. '12.0.0-rc4'.

.PARAMETER To
    Comparison package version, e.g. '12.0.0'.

.PARAMETER WorkDir
    Directory for intermediate files. Defaults to a fresh folder under the system temp path.

.PARAMETER KeepWork
    Keep the working directory (and the surface dumps) instead of deleting it.

.EXAMPLE
    ./Compare-JellyfinAbi.ps1 -From 12.0.0-rc4 -To 12.0.0

.NOTES
    Requires the .NET SDK and network access to nuget.org (or a warm package cache).
    Exit code 0 = no differences, 1 = differences found, 2 = the probe failed to run.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$From,
    [Parameter(Mandatory)][string]$To,
    [string]$WorkDir,
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'
$probeDir = $PSScriptRoot

if (-not $WorkDir) {
    $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("jf-abi-" + [guid]::NewGuid().ToString('n').Substring(0, 8))
}
$null = New-Item -ItemType Directory -Force -Path $WorkDir

# The Jellyfin assemblies these packages bring in. Everything else in the closure is a reference.
$jellyfinAssemblies = @(
    'MediaBrowser.Controller.dll',
    'MediaBrowser.Model.dll',
    'MediaBrowser.Common.dll',
    'Jellyfin.Data.dll',
    'Jellyfin.Extensions.dll',
    'Jellyfin.Database.Implementations.dll',
    'Emby.Naming.dll'
)

function New-Closure {
    param([string]$Version, [string]$Destination)

    $proj = Join-Path $Destination 'Ref'
    $null = New-Item -ItemType Directory -Force -Path $proj

    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Jellyfin.Controller" Version="$Version" />
    <PackageReference Include="Jellyfin.Model" Version="$Version" />
  </ItemGroup>
</Project>
"@ | Set-Content -Path (Join-Path $proj 'Ref.csproj') -Encoding utf8

    'class Anchor { }' | Set-Content -Path (Join-Path $proj 'Anchor.cs') -Encoding utf8

    $out = Join-Path $proj 'out'
    Write-Host "  restoring and publishing $Version ..."
    $log = & dotnet publish $proj -c Release -o $out 2>&1
    if ($LASTEXITCODE -ne 0) {
        $log | Write-Host
        throw "publish failed for $Version"
    }

    # Isolate the Jellyfin assemblies so only they are dumped; the rest of the closure
    # is passed to the probe as --refs so signatures can be decoded.
    $asm = Join-Path $Destination 'asm'
    $null = New-Item -ItemType Directory -Force -Path $asm
    foreach ($name in $jellyfinAssemblies) {
        $src = Join-Path $out $name
        if (Test-Path $src) { Copy-Item $src $asm }
    }

    [pscustomobject]@{ Assemblies = $asm; Refs = $out }
}

try {
    Write-Host "Building AbiProbe ..."
    $probeOut = Join-Path $WorkDir 'probe'
    $log = & dotnet build $probeDir -c Release -o $probeOut 2>&1
    if ($LASTEXITCODE -ne 0) {
        $log | Write-Host
        throw 'AbiProbe build failed'
    }
    $probeDll = Join-Path $probeOut 'AbiProbe.dll'

    $results = @{}
    foreach ($v in @($From, $To)) {
        Write-Host "Resolving $v ..."
        $dest = Join-Path $WorkDir ($v -replace '[^A-Za-z0-9.-]', '_')
        $null = New-Item -ItemType Directory -Force -Path $dest
        $closure = New-Closure -Version $v -Destination $dest

        $surface = Join-Path $WorkDir ("surface-" + ($v -replace '[^A-Za-z0-9.-]', '_') + '.txt')
        & dotnet $probeDll $surface $closure.Assemblies --refs $closure.Refs
        if ($LASTEXITCODE -ne 0) { throw "AbiProbe failed for $v" }
        $results[$v] = $surface
    }

    $a = Get-Content $results[$From] | Sort-Object
    $b = Get-Content $results[$To] | Sort-Object
    $delta = Compare-Object -ReferenceObject $a -DifferenceObject $b

    $removed = @($delta | Where-Object SideIndicator -eq '<=')
    $added = @($delta | Where-Object SideIndicator -eq '=>')

    Write-Host ''
    Write-Host "=== $From -> $To ===" -ForegroundColor Cyan
    Write-Host "removed: $($removed.Count)   added: $($added.Count)"

    if ($removed.Count -gt 0) {
        Write-Host ''
        Write-Host 'REMOVED (breaking candidates):' -ForegroundColor Yellow
        $removed | ForEach-Object { "  $($_.InputObject)" }
    }

    if ($added.Count -gt 0) {
        Write-Host ''
        Write-Host 'ADDED:' -ForegroundColor Green
        $added | ForEach-Object { "  $($_.InputObject)" }
    }

    if ($KeepWork) {
        Write-Host ''
        Write-Host "surface dumps kept in $WorkDir"
    }

    if ($delta.Count -gt 0) { exit 1 } else { exit 0 }
}
catch {
    Write-Error $_
    exit 2
}
finally {
    # Jellyfin server assemblies must never outlive the run that fetched them.
    if (-not $KeepWork -and (Test-Path $WorkDir)) {
        Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
    }
}
