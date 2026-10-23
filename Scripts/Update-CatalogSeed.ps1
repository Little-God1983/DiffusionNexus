<#
.SYNOPSIS
    Replaces the embedded catalog seed with the latest stable catalog release.

.DESCRIPTION
    Downloads manifest.json and catalog.zip from the catalog repo's latest stable release (or the
    tag -Version names), verifies the archive against the manifest's sha256, replaces the two files
    under DiffusionNexus.UI/Assets/Catalog, and prints the version it embedded and the commit to
    make. A Preview manifest is refused: the seed is always a stable catalog.

    The seed is what a fresh or offline machine sees before the startup update check has applied
    anything; without it the Installer Manager lists no workloads. Ported from the 3.x installer
    (Into-The-Latent/DiffusionNexus.Installer, Scripts/Update-CatalogSeed.ps1).

    Exit codes: 0 the seed was written (unchanged when it already was that release); 2 nothing was
    replaced - the release could not be downloaded, is not a Stable manifest, or its archive does
    not match its manifest.

.PARAMETER RepoRoot
    This repo. Defaults to the folder above this script.

.PARAMETER ReleaseBase
    The catalog repo's releases page. Default: DIFFUSIONNEXUS_CATALOG_RELEASES when set, else
    https://github.com/Into-The-Latent/DiffusionNexus.Catalog/releases.

.PARAMETER Version
    Embed stable release vN instead of the latest: a deliberate hold-back.

.EXAMPLE
    pwsh Scripts/Update-CatalogSeed.ps1
    git add DiffusionNexus.UI/Assets/Catalog
    git commit -m "chore(catalog): embed the v5 stable catalog seed"
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$ReleaseBase,
    [int]$Version
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

trap {
    Write-Host "Catalog seed NOT updated: $($_.Exception.Message) Nothing was replaced." -ForegroundColor Red
    exit 2
}

. (Join-Path $PSScriptRoot 'CatalogRelease.ps1')
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$assets = if ($Version) { "$(Get-CatalogReleaseBase $ReleaseBase)/download/v$Version" } else { "$(Get-CatalogReleaseBase $ReleaseBase)/latest/download" }

# -LiteralPath throughout: a checkout under a folder with [ ] in its name is still this repo.
$seedDir = Join-Path $RepoRoot $CatalogSeedFolder
if (-not (Test-Path -LiteralPath $seedDir -PathType Container)) { throw "$seedDir does not exist. Is $RepoRoot the DiffusionNexus repo?" }

$before = @('manifest.json', 'catalog.zip' | ForEach-Object { Get-Sha256 (Join-Path $seedDir $_) })

# Both assets to a temp folder first, verified there, then moved with the originals kept to put
# back: the seed is never left half replaced without saying so.
$temp = Join-Path ([IO.Path]::GetTempPath()) ("catalogseed-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $manifestPath = Join-Path $temp 'manifest.json'
    $zipPath = Join-Path $temp 'catalog.zip'
    $manifest = Read-CatalogReleaseManifest "$assets/manifest.json" $manifestPath 'the release manifest'
    if ($manifest.Channel -ne 'Stable') { throw "$assets/manifest.json is a $($manifest.Channel) manifest, not a Stable one; the seed is always a stable catalog." }
    if ($Version -and $manifest.Version -ne $Version) { throw "$assets/manifest.json says catalogVersion $($manifest.Version), not $Version." }
    # The archive from the release the manifest names: a second latest redirect may already serve a
    # tag published in between.
    Save-CatalogAsset "$(Get-CatalogReleaseBase $ReleaseBase)/download/v$($manifest.Version)/catalog.zip" $zipPath
    $zipHash = Get-Sha256 $zipPath
    if ($zipHash -ne $manifest.Sha256) { throw "the downloaded catalog.zip does not match its manifest: its sha256 is $zipHash, the manifest says $($manifest.Sha256)." }

    # The originals are copied aside first, so a move that fails (a file held open by a build or a
    # scanner) puts them back: the seed is the old one or the new one, never half of each.
    $names = 'manifest.json', 'catalog.zip'
    $saved = Join-Path $temp 'previous'
    New-Item -ItemType Directory -Path $saved | Out-Null
    foreach ($name in $names) {
        $file = Join-Path $seedDir $name
        if (Test-Path -LiteralPath $file -PathType Leaf) { Copy-Item -LiteralPath $file -Destination (Join-Path $saved $name) }
    }
    try {
        foreach ($name in $names) { Move-Item -LiteralPath (Join-Path $temp $name) -Destination (Join-Path $seedDir $name) -Force }
    } catch {
        $failure = $_.Exception.Message
        foreach ($name in $names) {
            $copy = Join-Path $saved $name
            if (Test-Path -LiteralPath $copy -PathType Leaf) {
                try { Copy-Item -LiteralPath $copy -Destination (Join-Path $seedDir $name) -Force } catch { }
            }
        }
        $now = @($names | ForEach-Object { Get-Sha256 (Join-Path $seedDir $_) })
        if (($now -join ' ') -eq ($before -join ' ')) { throw "could not replace the seed files ($failure); both were put back." }
        Write-Host "Catalog seed HALF replaced: could not replace the seed files ($failure), and putting the old ones back failed too." -ForegroundColor Red
        Write-Host "Restore them with: git restore $CatalogSeedFolder - then run this script again once nothing holds the files open." -ForegroundColor Red
        exit 2
    }
} finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }

$after = @('manifest.json', 'catalog.zip' | ForEach-Object { Get-Sha256 (Join-Path $seedDir $_) })
$state = if (($before -join ' ') -eq ($after -join ' ')) { ' - unchanged, it already was' } else { '' }
Write-Host "Embedded catalog v$($manifest.Version) ($($manifest.Short), stable) under $CatalogSeedFolder$state. ($assets)" -ForegroundColor Green
Write-Host "Commit it: git add $CatalogSeedFolder; git commit -m `"chore(catalog): embed the v$($manifest.Version) stable catalog seed`""
exit 0
