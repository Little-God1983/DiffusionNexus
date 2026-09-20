<#
.SYNOPSIS
    Stages the corresponding source for the GPL binaries this application redistributes.

.DESCRIPTION
    GPL-2.0 section 3 accepts three routes for source: (a) accompany the binary with it,
    (b) a written offer valid three years, (c) pass along an offer received. This script
    implements route (a) - the source ships in the release, beside the binaries - which is
    why THIRD-PARTY-NOTICES.txt makes no offer and names no contact point.

    Everything is driven by Scripts/license-data/corresponding-source.json: which archives,
    from where, and their SHA-256. Downloads are cached, so only the first publish on a
    machine pays for them.

    Fail-closed by design. The publish stops if an archive cannot be fetched, if a hash does
    not match, or if the cached source no longer corresponds to the shipped binaries. Shipping
    a release with missing or mismatched source is worse than not shipping one: mismatched
    source looks like compliance while being useless to the recipient.

.PARAMETER OutputDir
    The publish directory. A 'source' folder is created inside it.

.PARAMETER CacheDir
    Where downloaded archives are kept between builds. Defaults to .source-cache at the
    repository root, which is git-ignored - these are tens of megabytes of third-party
    archives and do not belong in the history.

.PARAMETER Check
    Verify the cache and the correspondence without writing to OutputDir.

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

$manifestPath = Join-Path $ScriptDir 'license-data/corresponding-source.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

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

$packageRoot = Join-Path ($env:NUGET_PACKAGES ?? (Join-Path $env:USERPROFILE '.nuget/packages')) `
                         "videolan.libvlc.windows/$actualPackage/build/x64"
$libvlc = Join-Path $packageRoot 'libvlc.dll'
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

# ------------------------------------------------------------- fetch
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
$staged = @()

foreach ($c in $manifest.components) {
    $cached = Join-Path $CacheDir $c.file

    if (Test-Path $cached) {
        $hash = (Get-FileHash $cached -Algorithm SHA256).Hash.ToLower()
        if ($hash -ne $c.sha256.ToLower()) {
            # A corrupt or tampered cache entry must not be reused silently.
            Remove-Item -LiteralPath $cached -Force
            Write-Host "  $($c.name): cached copy failed its hash, refetching" -ForegroundColor Yellow
        }
    }

    if (-not (Test-Path $cached)) {
        $fetched = $false
        foreach ($url in $c.urls) {
            try {
                Write-Host "  $($c.name): downloading from $url"
                # Mirrors go stale and hosts disappear; the hash below is what makes trying
                # several of them safe.
                Invoke-WebRequest -Uri $url -OutFile $cached -TimeoutSec 300 -MaximumRedirection 10 -UserAgent 'Mozilla/5.0'
                $fetched = $true
                break
            } catch {
                Write-Host "    unavailable" -ForegroundColor DarkGray
                if (Test-Path $cached) { Remove-Item -LiteralPath $cached -Force }
            }
        }
        if (-not $fetched) {
            throw ("Could not download the corresponding source for $($c.name) from any of its " +
                   "$($c.urls.Count) sources. The release cannot ship without it. Fetch " +
                   "$($c.file) by hand into $CacheDir, or add a working mirror to " +
                   "Scripts/license-data/corresponding-source.json.")
        }
    }

    $hash = (Get-FileHash $cached -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $c.sha256.ToLower()) {
        throw ("$($c.file) does not match its pinned SHA-256." + [Environment]::NewLine +
               "  expected $($c.sha256.ToLower())" + [Environment]::NewLine +
               "  actual   $hash" + [Environment]::NewLine +
               "Either the upstream archive changed or the download was tampered with. Do not " +
               "update the pin without establishing which.")
    }

    $sizeMb = (Get-Item $cached).Length / 1MB
    Write-Host ("  {0,-10} {1,7:N1} MB  verified" -f $c.name, $sizeMb) -ForegroundColor Green
    $staged += [pscustomobject]@{ Component = $c; Path = $cached; SizeMb = $sizeMb }
}

if ($Check) {
    Write-Host ''
    Write-Host 'Corresponding source is complete and verified.' -ForegroundColor Green
    exit 0
}

if (-not $OutputDir) { throw 'OutputDir is required unless -Check is given.' }

# ------------------------------------------------------------- stage
$sourceDir = Join-Path $OutputDir 'source'
New-Item -ItemType Directory -Force -Path $sourceDir | Out-Null

foreach ($s in $staged) {
    Copy-Item -LiteralPath $s.Path -Destination (Join-Path $sourceDir $s.Component.file) -Force
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
    [void]$readme.AppendLine("  file    : $($c.file)")
    [void]$readme.AppendLine("  sha256  : $($c.sha256)")
    [void]$readme.AppendLine("  origin  : $($c.urls[0])")
    [void]$readme.AppendLine("  covers  : $($c.describes)")
    [void]$readme.AppendLine()
}

[void]$readme.AppendLine(('-' * 78))
[void]$readme.AppendLine('Full licence texts for every component of this application, GPL and otherwise, are')
[void]$readme.AppendLine('in THIRD-PARTY-NOTICES.txt beside this folder.')

Set-Content -Path (Join-Path $sourceDir 'README.txt') -Value $readme.ToString() -Encoding UTF8

$total = ($staged | Measure-Object -Property SizeMb -Sum).Sum
Write-Host ''
Write-Host ("Staged {0} archives ({1:N1} MB) into source/" -f $staged.Count, $total) -ForegroundColor Green
