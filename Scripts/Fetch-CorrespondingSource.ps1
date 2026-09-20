<#
.SYNOPSIS
    Stages the corresponding source for the GPL binaries this application redistributes.

.DESCRIPTION
    GPL-2.0 section 3 accepts three routes for source: (a) accompany the binary with it,
    (b) a written offer valid three years, (c) pass along an offer received. This script
    implements route (a) - the source ships in the release, beside the binaries - which is
    why THIRD-PARTY-NOTICES.txt makes no offer and names no contact point.

    Everything is driven by Scripts/license-data/corresponding-source.json: which archives,
    from where, and the SHA-256 of each individual source. Downloads are cached, so only the
    first publish on a machine pays for them.

    Fail-closed by design. The publish stops if no source for a component verifies, or if the
    cached source no longer corresponds to the shipped binaries. Shipping a release with
    missing or mismatched source is worse than not shipping one: mismatched source looks like
    compliance while being useless to the recipient.

    A source is accepted only when its BYTES verify. A host answering 200 with an interstitial
    HTML page - SourceForge does exactly this - is rejected and the next source is tried, rather
    than ending the search and failing the release permanently.

.PARAMETER OutputDir
    The publish directory. A 'source' folder is created inside it.

.PARAMETER CacheDir
    Where verified archives are kept between builds. Defaults to .source-cache at the repository
    root, which is git-ignored - these are tens of megabytes of third-party archives and do not
    belong in the history.

.PARAMETER Check
    Verify that every component can be satisfied, without writing to OutputDir. Run in CI so that
    mirror rot surfaces on a pull request instead of in the middle of a release.

.NOTES
    TODO: Linux Implementation for Task Corresponding-Source - the correspondence check reads
    libvlc.dll's Windows version resource. A Linux build would use VideoLAN.LibVLC.Linux and
    need its own anchor; the manifest and staging logic are reusable as they stand.
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$CacheDir,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
if (-not $CacheDir) { $CacheDir = Join-Path $RepoRoot '.source-cache' }

# Validated before anything is downloaded: discovering a bad path after pulling ~43 MB across
# five hosts wastes the operator's time for no reason.
if (-not $Check) {
    if (-not $OutputDir) { throw 'OutputDir is required unless -Check is given.' }
    if (-not (Test-Path $OutputDir)) { throw "OutputDir '$OutputDir' does not exist." }
}

$manifest = Get-Content (Join-Path $ScriptDir 'license-data/corresponding-source.json') -Raw | ConvertFrom-Json

Write-Host ''
Write-Host 'Corresponding source (GPL-2.0 section 3a)' -ForegroundColor Cyan
Write-Host ('-' * 60)

# --------------------------------------------------------- correspondence
# The NuGet package version and the upstream VLC release it carries are different numbers
# (3.0.23.1 vs 3.0.23), so the binary is the authority on what these sources must match.
$expectedPackage = $manifest.correspondsTo.packageVersion
$csproj = Get-Content (Join-Path $RepoRoot 'DiffusionNexus.UI/DiffusionNexus.UI.csproj') -Raw
$actualPackage = [regex]::Match(
    $csproj,
    '<PackageReference\b[^>]*Include="VideoLAN\.LibVLC\.Windows"[^>]*Version="([^"]+)"').Groups[1].Value

if ($actualPackage -ne $expectedPackage) {
    throw ("The project references VideoLAN.LibVLC.Windows $actualPackage but " +
           "corresponding-source.json is pinned to $expectedPackage. Refresh the manifest " +
           "(versions come from contrib/src/<name>/rules.mak inside the VLC tarball) before releasing.")
}

# Honours a globalPackagesFolder from NuGet.config, which a build machine may well set, rather
# than assuming the default location.
$packagesRoot = $env:NUGET_PACKAGES
if (-not $packagesRoot) {
    $nugetConfig = Get-ChildItem -Path $RepoRoot -Filter 'NuGet.config' -File -ErrorAction SilentlyContinue |
                   Select-Object -First 1
    if ($nugetConfig) {
        $configured = [regex]::Match(
            (Get-Content $nugetConfig.FullName -Raw),
            '<add\s+key="globalPackagesFolder"\s+value="([^"]+)"',
            'IgnoreCase').Groups[1].Value
        if ($configured) {
            $packagesRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $configured))
        }
    }
}
if (-not $packagesRoot) { $packagesRoot = Join-Path $env:USERPROFILE '.nuget/packages' }

$libvlc = Join-Path $packagesRoot "videolan.libvlc.windows/$actualPackage/build/x64/libvlc.dll"
if (-not (Test-Path $libvlc)) {
    throw "Cannot verify correspondence: $libvlc is missing. Run: dotnet restore DiffusionNexus.UI -r win-x64"
}

$binaryVersion = (Get-Item $libvlc).VersionInfo.FileVersion
if ($binaryVersion -ne $manifest.correspondsTo.upstreamVersion) {
    throw ("libvlc.dll reports VLC $binaryVersion but the staged source is for " +
           "$($manifest.correspondsTo.upstreamVersion). Shipping source for a different build is " +
           "worse than shipping none - it reads as compliance and is useless. Refresh the manifest.")
}
Write-Host "Corresponds to        : VLC $binaryVersion (package $actualPackage)"

# ------------------------------------------------------------- resolve
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
$staged = @()

function Test-Archive {
    param([string]$Path, [string]$ExpectedSha256)
    if (-not (Test-Path $Path)) { return $false }
    return (Get-FileHash $Path -Algorithm SHA256).Hash.ToLower() -eq $ExpectedSha256.ToLower()
}

foreach ($c in $manifest.components) {
    $resolved = $null

    # A cached copy from any of this component's sources is good enough - they are all
    # corresponding source for the same binaries, whatever the packaging.
    foreach ($s in $c.sources) {
        $cached = Join-Path $CacheDir $s.file
        if (Test-Archive -Path $cached -ExpectedSha256 $s.sha256) {
            $resolved = [pscustomobject]@{ Source = $s; Path = $cached; FromCache = $true }
            break
        }
    }

    if (-not $resolved) {
        foreach ($s in $c.sources) {
            $cached = Join-Path $CacheDir $s.file
            try {
                Write-Host "  $($c.name): fetching $($s.url)"
                Invoke-WebRequest -Uri $s.url -OutFile $cached -TimeoutSec 300 `
                                  -MaximumRedirection 10 -UserAgent 'Mozilla/5.0'
            } catch {
                Write-Host "    unreachable" -ForegroundColor DarkGray
                if (Test-Path $cached) { Remove-Item -LiteralPath $cached -Force }
                continue
            }

            # The hash is checked HERE, not after the loop. A 200 response is not evidence of
            # the right bytes: mirrors serve interstitial pages, rate-limit notices and stale
            # files, and accepting the first non-throwing response would end the search on one
            # of those and fail the release with mirrors still untried.
            if (Test-Archive -Path $cached -ExpectedSha256 $s.sha256) {
                $resolved = [pscustomobject]@{ Source = $s; Path = $cached; FromCache = $false }
                break
            }

            Write-Host "    wrong content (hash mismatch), trying the next source" -ForegroundColor Yellow
            Remove-Item -LiteralPath $cached -Force
        }
    }

    if (-not $resolved) {
        throw ("No source for $($c.name) could be verified. All $($c.sources.Count) of its " +
               "sources were tried and none delivered the expected bytes. The release cannot " +
               "ship without it." + [Environment]::NewLine +
               "Fix by placing a verified copy in $CacheDir under one of the file names in " +
               "Scripts/license-data/corresponding-source.json, or by adding a working source to " +
               "that manifest. Do NOT substitute a different version to make this pass - it would " +
               "no longer be the corresponding source for the shipped binaries.")
    }

    $sizeMb = (Get-Item $resolved.Path).Length / 1MB
    $origin = if ($resolved.FromCache) { 'cached' } else { 'downloaded' }
    Write-Host ("  {0,-10} {1,7:N1} MB  verified ({2})" -f $c.name, $sizeMb, $origin) -ForegroundColor Green
    $staged += [pscustomobject]@{ Component = $c; Source = $resolved.Source; Path = $resolved.Path; SizeMb = $sizeMb }
}

if ($Check) {
    Write-Host ''
    Write-Host 'Corresponding source is complete and verified.' -ForegroundColor Green
    exit 0
}

# ------------------------------------------------------------- stage
$sourceDir = Join-Path $OutputDir 'source'
New-Item -ItemType Directory -Force -Path $sourceDir | Out-Null

foreach ($s in $staged) {
    Copy-Item -LiteralPath $s.Path -Destination (Join-Path $sourceDir $s.Source.file) -Force
}

$readme = New-Object System.Text.StringBuilder
[void]$readme.AppendLine('CORRESPONDING SOURCE')
[void]$readme.AppendLine('====================')
[void]$readme.AppendLine()
[void]$readme.AppendLine('This folder exists to satisfy the GNU General Public License, version 2, section 3.')
[void]$readme.AppendLine('Some of the components distributed with this application are licensed under the GPL,')
[void]$readme.AppendLine('and section 3 requires that their source accompany the binaries. It does: it is here,')
[void]$readme.AppendLine('in this folder, which is why no written offer is made anywhere in the notices.')
[void]$readme.AppendLine()
[void]$readme.AppendLine('None of this is the source of DiffusionNexus itself. DiffusionNexus is MIT-licensed')
[void]$readme.AppendLine('and its source is at https://github.com/Little-God1983/DiffusionNexus. The archives')
[void]$readme.AppendLine('below are unmodified upstream releases by their own authors.')
[void]$readme.AppendLine()
[void]$readme.AppendLine("These correspond to VLC $binaryVersion, as shipped in libvlc/win-x64/ of this release.")
[void]$readme.AppendLine()

foreach ($s in $staged) {
    $c = $s.Component
    [void]$readme.AppendLine(('-' * 78))
    [void]$readme.AppendLine("$($c.name) $($c.version)")
    [void]$readme.AppendLine("  file    : $($s.Source.file)")
    [void]$readme.AppendLine("  sha256  : $($s.Source.sha256)")
    # The source actually used, not the manifest's first entry: recording a URL the file did
    # not come from is a small lie in a document whose only job is being accurate, and the
    # first entry has been a dead host before now.
    [void]$readme.AppendLine("  obtained: $($s.Source.url)")
    [void]$readme.AppendLine("  covers  : $($c.describes)")
    if ($c.versionNote) { [void]$readme.AppendLine("  note    : $($c.versionNote)") }
    [void]$readme.AppendLine()
}

[void]$readme.AppendLine(('-' * 78))
[void]$readme.AppendLine('Full licence texts for every component of this application, GPL and otherwise, are')
[void]$readme.AppendLine('in THIRD-PARTY-NOTICES.txt beside this folder.')

Set-Content -Path (Join-Path $sourceDir 'README.txt') -Value $readme.ToString() -Encoding UTF8

$total = ($staged | Measure-Object -Property SizeMb -Sum).Sum
Write-Host ''
Write-Host ("Staged {0} archives ({1:N1} MB) into source/" -f $staged.Count, $total) -ForegroundColor Green
