<#
.SYNOPSIS
    Generates third-party attribution for the shipped DiffusionNexus application.

.DESCRIPTION
    Reads the restore graph of DiffusionNexus.UI — the project publish.ps1 actually ships —
    and emits two files from one model, so they can never disagree:

      THIRD-PARTY-NOTICES.txt    the flat legal document, shipped beside the exe
      third-party-notices.json   the index embedded into the UI assembly for the About screen

    Self-contained single-file publishing embeds the .NET runtime packs, each of which ships
    its own THIRD-PARTY-NOTICES.TXT (zlib, Brotli, ICU, Unicode data). Those are reproduced.

    IMPORTANT: a plain 'dotnet restore' does NOT populate the runtime-pack download
    dependencies this script reads for section 3 — that graph only appears when the project
    is restored for a concrete RuntimeIdentifier. You MUST restore with a RID first:

      dotnet restore DiffusionNexus.UI -r win-x64 -p:SelfContained=true
      pwsh Scripts/Generate-ThirdPartyNotices.ps1

    The script fails closed on this: if no runtime-pack notice files are found, it throws
    rather than silently shipping a notices document missing ~1000+ lines of required
    attribution (zlib, Brotli, ICU, Unicode data).

    Hand-authored entries the graph cannot express live in Scripts/license-data/supplements.json.

.PARAMETER ProjectDir
    Project whose restore graph is read. Defaults to DiffusionNexus.UI.

.PARAMETER RuntimeIdentifier
    RID whose runtime packs are reproduced in section 3. Defaults to win-x64, the only RID
    DiffusionNexus ships. Must match a RID the project was actually restored for (see
    .DESCRIPTION) — restoring for a different or no RID yields zero runtime-pack notices.

.PARAMETER AllowNoRuntimePacks
    Escape hatch: don't throw when zero runtime-pack notice files are found. Only pass this
    for a deliberate framework-dependent build that will never embed the runtime packs; for
    every self-contained build this is exactly the failure mode that must NOT be silenced.

.PARAMETER Check
    Do not write. Compare the committed files against what would be generated and exit
    non-zero on any difference. VERSION NUMBERS ARE NORMALISED OUT of the comparison: this
    repo floats Microsoft.EntityFrameworkCore 10.0.* and Microsoft.Extensions.* 10.0.*, and a
    version-sensitive gate would redden CI on an unrelated PR the day a patch ships.
    Attribution obligations attach to the component and its licence, not its patch number.
    Everything else is compared exactly, INCLUDING CASE.

.EXAMPLE
    dotnet restore DiffusionNexus.UI -r win-x64 -p:SelfContained=true
    pwsh Scripts/Generate-ThirdPartyNotices.ps1

.EXAMPLE
    pwsh Scripts/Generate-ThirdPartyNotices.ps1 -Check
#>
[CmdletBinding()]
param(
    [string]$ProjectDir = 'DiffusionNexus.UI',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputPath,
    [string]$JsonOutputPath,
    [switch]$Check,
    [switch]$AllowNoRuntimePacks
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dataDir  = Join-Path $PSScriptRoot 'license-data'
$textsDir = Join-Path $dataDir 'texts'
if (-not $OutputPath)     { $OutputPath     = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt' }
if (-not $JsonOutputPath) { $JsonOutputPath = Join-Path $repoRoot 'third-party-notices.json' }

function Normalize-Text { param([string]$Text) ; return ($Text -replace "`r`n", "`n") }

# Blank out version numbers in the shapes THIS script emits, and nowhere else. Targeting our
# own line formats keeps version strings inside reproduced licence texts untouched.
#
# KNOWN LIMIT: the section-1 pattern can consume at most 14 characters of the licence-label
# field, so a licence id LONGER than 14 characters (LGPL-2.1-or-later, CC-BY-NC-SA-3.0) leaves
# its line un-normalised, and that component's version does take part in the -Check comparison.
# Deliberate: a width-independent pattern would also match lines inside reproduced licence texts
# that end in a version-like token, normalising away genuine drift in a legal document.
# Over-reporting drift is the safe direction of error — the failure mode is one CI run whose
# message already says to regenerate and commit. Inert today: every floating-version package in
# this graph (EF Core, Microsoft.Extensions.*) is MIT.
function Normalize-Versions {
    param([string]$Text)
    $v = '\d+\.\d+(?:\.\d+)*(?:-[0-9A-Za-z.\-]+)?'
    $t = $Text
    $t = $t -replace "(?m)^(\s{2}\S.{0,13}\s+\S+)\s+$v\s*$", '$1 <version>'
    $t = $t -replace "(?m)^(\s{2}- \S+)\s+$v\s*$", '$1 <version>'
    $t = $t -replace "(?m)^(### \S+)\s+$v(\s+\(runtime pack\))\s*$", '$1 <version>$2'
    return $t
}

function Normalize-JsonVersions {
    param([string]$Json)
    return ($Json -replace '("version"\s*:\s*")[^"]*(")', '${1}<version>${2}')
}

# ---------------------------------------------------------------- NuGet cache
$nugetRoot = $env:NUGET_PACKAGES
if (-not $nugetRoot) { $nugetRoot = Join-Path $env:USERPROFILE '.nuget/packages' }
if (-not (Test-Path $nugetRoot)) { throw "NuGet package cache not found at '$nugetRoot'." }

$assets = Join-Path $repoRoot $ProjectDir 'obj' 'project.assets.json'
if (-not (Test-Path $assets)) { throw "Restore graph not found at '$assets'. Run 'dotnet restore' first." }

Write-Host "Reading restore graph : $assets"
Write-Host "NuGet cache           : $nugetRoot"

$graph = Get-Content $assets -Raw | ConvertFrom-Json
$supplements = Get-Content (Join-Path $dataDir 'supplements.json') -Raw | ConvertFrom-Json

# Build-only, auto-referenced SDK assets follow the INSTALLED SDK version, so listing them
# would make the freshness gate fail on every SDK patch bump.
$autoReferenced = @()
foreach ($fw in $graph.project.frameworks.PSObject.Properties) {
    foreach ($dep in $fw.Value.dependencies.PSObject.Properties) {
        if ($dep.Value.autoReferenced -eq $true) { $autoReferenced += $dep.Name }
    }
}

# ------------------------------------------------------- collect .NET packages
function Get-NuspecField {
    param([string]$Xml, [string]$Field)
    $m = [regex]::Match($Xml, "<$Field>(?<v>[^<]*)</$Field>")
    if ($m.Success) { return [System.Net.WebUtility]::HtmlDecode($m.Groups['v'].Value).Trim() }
    return ''
}

$components = @()
foreach ($entry in $graph.libraries.PSObject.Properties) {
    if ($entry.Value.type -ne 'package') { continue }
    $id, $version = $entry.Name -split '/', 2

    $skip = $false
    foreach ($prefix in $supplements.excludePrefixes) {
        if ($id.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $skip = $true; break }
    }
    if ($skip) { continue }
    if ($autoReferenced -contains $id) { continue }

    # NuGet's global-packages layout lower-cases BOTH segments.
    $pkgDir = Join-Path $nugetRoot $id.ToLowerInvariant() $version.ToLowerInvariant()
    $nuspec = Get-ChildItem -Path $pkgDir -Filter '*.nuspec' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $nuspec) { throw "Package '$id/$version' is not in the NuGet cache. Run 'dotnet restore' first." }

    $xml = Get-Content $nuspec.FullName -Raw

    $license = ''
    $licenseFile = ''
    $mExpr = [regex]::Match($xml, '<license type="expression">(?<v>[^<]+)</license>')
    $mFile = [regex]::Match($xml, '<license type="file">(?<v>[^<]+)</license>')
    if ($mExpr.Success) { $license = $mExpr.Groups['v'].Value }
    elseif ($mFile.Success) { $license = 'FILE'; $licenseFile = $mFile.Groups['v'].Value }

    # A package can declare no machine-readable licence at all (e.g. only the deprecated
    # <licenseUrl> element) and still need shipping. Only an EXACT id match here can promote
    # it out of UNDECLARED — this is a manually-verified attestation per package, not a
    # loophole: an unlisted package still fails closed below.
    if (-not $license) {
        $override = $supplements.licenseOverrides | Where-Object { $_.packageId -eq $id } | Select-Object -First 1
        if ($override) { $license = $override.license }
    }

    $components += [pscustomobject]@{
        Id          = $id
        Version     = $version
        License     = if ($license) { $license } else { 'UNDECLARED' }
        LicenseFile = $licenseFile
        Copyright   = Get-NuspecField $xml 'copyright'
        Authors     = Get-NuspecField $xml 'authors'
        ProjectUrl  = Get-NuspecField $xml 'projectUrl'
        PackageDir  = $pkgDir
    }
}
$components = @($components | Sort-Object Id)
Write-Host ("Packages resolved     : {0}" -f $components.Count)

$undeclared = $components | Where-Object { $_.License -eq 'UNDECLARED' }
if ($undeclared) {
    $names = ($undeclared | ForEach-Object { $_.Id }) -join ', '
    throw "These packages declare no license and need a manual supplement entry: $names"
}

# A package whose nuspec points at a licence FILE has no SPDX text to print, so it must be
# reproduced verbatim through a bundledNotices entry or it silently contributes nothing.
foreach ($c in ($components | Where-Object { $_.License -eq 'FILE' })) {
    if (-not ($supplements.bundledNotices | Where-Object { $_.packageId -eq $c.Id })) {
        throw "Package '$($c.Id)' ships its license as a file ('$($c.LicenseFile)'). Add a bundledNotices entry for it in supplements.json."
    }
}

# ---------------------------------------------- runtime packs (self-contained)
# downloadDependencies is only populated per-RID: a plain 'dotnet restore' leaves it empty
# (or a lone null entry) and this loop would silently do nothing. The -RuntimeIdentifier
# filter also guards a multi-RID graph from mixing in another platform's packs.
$runtimePacks = @()
foreach ($fw in $graph.project.frameworks.PSObject.Properties) {
    foreach ($d in @($fw.Value.downloadDependencies)) {
        if (-not $d) { continue }
        if ($d.name -notlike "*.$RuntimeIdentifier") { continue }
        $v = (($d.version -replace '[\[\]\(\)]', '') -split ',')[0].Trim()
        $dir = Join-Path $nugetRoot $d.name.ToLowerInvariant() $v.ToLowerInvariant()
        if (-not (Test-Path $dir)) { continue }
        $file = Get-ChildItem -Path $dir -Filter 'THIRD-PARTY-NOTICES*' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($file) {
            $runtimePacks += [pscustomobject]@{ Name = $d.name; Version = $v; File = $file.FullName }
        }
    }
}
$runtimePacks = @($runtimePacks | Sort-Object Name, Version | Group-Object Name | ForEach-Object { $_.Group[0] })
Write-Host ("Runtime pack notices  : {0}" -f $runtimePacks.Count)

# Self-contained single-file publishing embeds these packs into the shipped exe, carrying
# ~1000+ lines of required attribution (zlib, Brotli, ICU, Unicode data). Finding none is
# almost always a restore that never targeted a RID, not an application with nothing to
# report — fail closed rather than silently ship a notices document missing that attribution.
if ($runtimePacks.Count -eq 0 -and -not $AllowNoRuntimePacks) {
    throw "No runtime-pack notice files found for RuntimeIdentifier '$RuntimeIdentifier'. " +
          "A plain 'dotnet restore' does not pull runtime packs into the graph. Restore with a RID first: " +
          "dotnet restore $ProjectDir -r $RuntimeIdentifier -p:SelfContained=true " +
          "-- then re-run this script. Pass -AllowNoRuntimePacks only for a deliberate " +
          "framework-dependent build that will never embed the runtime packs."
}

# ------------------------------------------------------- resolve licence texts
#
# Every file under Scripts/license-data/texts/ is reproduced VERBATIM into the shipped legal
# document (Get-Content -Raw, no stripping) — so none of them may carry anything we authored.
# They must be byte-faithful to their canonical licence source.
#
# In particular: MIT.txt is the canonical SPDX MIT body (https://spdx.org/licenses/MIT.html)
# with the "Copyright (c) <year> <holder>" placeholder line removed. That is safe ONLY because
# the per-package copyright line is emitted separately, from nuspec metadata, immediately above
# this text in section 2 (see the "Applies to the following components" loop below) — so every
# MIT component still gets a copyright line, just not from this shared file. This holds only
# while every MIT component in the graph declares a real copyright or authors field; if one ever
# doesn't, that component prints "(no copyright notice declared)" instead, and at that point
# MIT.txt should get the placeholder line back rather than silently attributing to no one.
function Get-LicenseTextFor {
    param([pscustomobject]$Component)
    if ($Component.License -eq 'FILE') {
        $b = $supplements.bundledNotices | Where-Object { $_.packageId -eq $Component.Id } | Select-Object -First 1
        $file = Join-Path $Component.PackageDir $b.file
        if (-not (Test-Path $file)) { throw "Notice file '$($b.file)' not found in package '$($Component.Id)'." }
        return (Get-Content $file -Raw).TrimEnd()
    }
    $textFile = Join-Path $textsDir ($Component.License + '.txt')
    if (-not (Test-Path $textFile)) {
        throw "No license text for '$($Component.License)' (needed by $($Component.Id)). Add Scripts/license-data/texts/$($Component.License).txt."
    }
    return (Get-Content $textFile -Raw).TrimEnd()
}

# --------------------------------------------------------------------- emit
$sb   = [System.Text.StringBuilder]::new()
$rule = '=' * 80
$thin = '-' * 80
function Add-Line { param([string]$Text = '') ; [void]$sb.AppendLine($Text) }

Add-Line $rule
Add-Line 'THIRD-PARTY SOFTWARE NOTICES AND INFORMATION'
Add-Line 'DiffusionNexus'
Add-Line $rule
Add-Line ''
Add-Line 'This product incorporates material from the projects listed below. The original'
Add-Line 'copyright notices and the licenses under which we received such material are set'
Add-Line 'forth in this document.'
Add-Line ''
Add-Line 'Sections 1 and 2 are the complete restore closure of the shipped application. It is'
Add-Line 'a superset of what the released binaries actually link: a few entries may be'
Add-Line 'build-time only. They are listed anyway so the notice is never narrower than the'
Add-Line 'product. Section 3 reproduces notice files carried inside shipped components, and'
Add-Line 'section 4 covers material no package graph expresses.'
Add-Line ''
Add-Line 'The frameworks this application installs on request (ComfyUI, Forge and others) are'
Add-Line 'not part of this product. They are fetched from their own repositories at install'
Add-Line 'time under their own licenses, which they carry themselves.'
Add-Line ''
Add-Line 'GENERATED FILE - DO NOT EDIT BY HAND.'
Add-Line 'Regenerate with:  dotnet restore DiffusionNexus.UI -r win-x64 -p:SelfContained=true'
Add-Line '                  pwsh Scripts/Generate-ThirdPartyNotices.ps1'
Add-Line 'Inputs:           DiffusionNexus.UI/obj/project.assets.json  (restore graph, RID-restored)'
Add-Line '                  <nuget cache>/<runtime pack>/THIRD-PARTY-NOTICES.TXT'
Add-Line '                  Scripts/license-data/supplements.json      (hand-authored entries)'
Add-Line ''

Add-Line $thin
Add-Line '1. COMPONENT INVENTORY'
Add-Line $thin
Add-Line ''
foreach ($c in $components) {
    if ($c.License -eq 'FILE') { $label = 'see sect. 3' } else { $label = $c.License }
    Add-Line ("  {0,-14} {1} {2}" -f $label, $c.Id, $c.Version)
}
Add-Line ''

Add-Line $thin
Add-Line '2. LICENSE TEXTS'
Add-Line $thin
Add-Line ''

$byLicense = $components | Where-Object { $_.License -ne 'FILE' } | Group-Object License | Sort-Object Name
foreach ($group in $byLicense) {
    $textFile = Join-Path $textsDir ($group.Name + '.txt')
    if (-not (Test-Path $textFile)) {
        throw "No license text for '$($group.Name)'. Add Scripts/license-data/texts/$($group.Name).txt."
    }
    Add-Line ("### {0}" -f $group.Name)
    Add-Line ''
    Add-Line 'Applies to the following components:'
    foreach ($c in ($group.Group | Sort-Object Id)) {
        if ($c.Copyright) { $attribution = $c.Copyright }
        elseif ($c.Authors) { $attribution = "Copyright (c) $($c.Authors)" }
        else { $attribution = '(no copyright notice declared)' }
        Add-Line ("  - {0} {1}" -f $c.Id, $c.Version)
        Add-Line ("      {0}" -f $attribution)
    }
    Add-Line ''
    Add-Line (Get-Content $textFile -Raw).TrimEnd()
    Add-Line ''
    Add-Line $thin
    Add-Line ''
}

Add-Line '3. BUNDLED NOTICE FILES'
Add-Line $thin
Add-Line ''
Add-Line 'Notice files that ship inside the .NET runtime packs embedded by self-contained'
Add-Line 'publishing, and inside any package that carries its own, reproduced verbatim.'
Add-Line ''
foreach ($rp in $runtimePacks) {
    Add-Line ("### {0} {1} (runtime pack)" -f $rp.Name, $rp.Version)
    Add-Line ("Source: {0}" -f (Split-Path -Leaf $rp.File))
    Add-Line ''
    Add-Line (Get-Content $rp.File -Raw).TrimEnd()
    Add-Line ''
    Add-Line $thin
    Add-Line ''
}
if ($runtimePacks.Count -eq 0 -and (-not $supplements.bundledNotices -or $supplements.bundledNotices.Count -eq 0)) {
    Add-Line 'No shipped component carries its own notice file.'
    Add-Line ''
    Add-Line $thin
    Add-Line ''
}
foreach ($b in $supplements.bundledNotices) {
    $pkg = $components | Where-Object { $_.Id -eq $b.packageId } | Select-Object -First 1
    if (-not $pkg) { throw "Bundled-notice package '$($b.packageId)' is no longer in the restore graph. Update supplements.json." }
    $file = Join-Path $pkg.PackageDir $b.file
    if (-not (Test-Path $file)) { throw "Notice file '$($b.file)' not found in package '$($b.packageId)'." }
    Add-Line ("### {0}" -f $b.title)
    Add-Line ("Source: {0} {1} :: {2}" -f $pkg.Id, $pkg.Version, $b.file)
    Add-Line ''
    if ($b.note) {
        Add-Line ("NOTE: {0}" -f $b.note)
        Add-Line ''
    }
    Add-Line (Get-Content $file -Raw).TrimEnd()
    Add-Line ''
    Add-Line $thin
    Add-Line ''
}

Add-Line '4. COMPONENTS NOT EXPRESSED BY A PACKAGE GRAPH'
Add-Line $thin
Add-Line ''
foreach ($s in $supplements.supplements) {
    $file = Join-Path $textsDir $s.textFile
    if (-not (Test-Path $file)) { throw "Supplement text '$($s.textFile)' not found." }
    Add-Line ("### {0} - {1}" -f $s.title, $s.license)
    Add-Line ''
    Add-Line ("NOTE: {0}" -f $s.note)
    Add-Line ''
    Add-Line (Get-Content $file -Raw).TrimEnd()
    Add-Line ''
    Add-Line $thin
    Add-Line ''
}

Add-Line 'END OF THIRD-PARTY NOTICES'

# Normalise to pure CRLF. Upstream notice texts carry mixed endings, and a mixed-ending
# output would not round-trip through git's autocrlf normalisation, which would make the
# CI freshness check flap.
$text = $sb.ToString() -replace "`r`n", "`n" -replace "`n", "`r`n"

# ----------------------------------------------------------------- JSON index
$jsonComponents = @()
foreach ($c in $components) {
    $jsonComponents += [pscustomobject][ordered]@{
        id          = $c.Id
        version     = $c.Version
        license     = if ($c.License -eq 'FILE') { 'See notice file' } else { $c.License }
        copyright   = $c.Copyright
        authors     = $c.Authors
        projectUrl  = $c.ProjectUrl
        licenseText = Normalize-Text (Get-LicenseTextFor $c)
    }
}
foreach ($rp in $runtimePacks) {
    $jsonComponents += [pscustomobject][ordered]@{
        id          = $rp.Name
        version     = $rp.Version
        license     = 'MIT'
        copyright   = 'Copyright (c) .NET Foundation and Contributors'
        authors     = 'Microsoft'
        projectUrl  = 'https://github.com/dotnet/runtime'
        licenseText = Normalize-Text ((Get-Content $rp.File -Raw).TrimEnd())
    }
}
foreach ($s in $supplements.supplements) {
    $jsonComponents += [pscustomobject][ordered]@{
        id          = $s.title
        version     = ''
        license     = $s.license
        copyright   = ''
        authors     = ''
        projectUrl  = ''
        licenseText = (Normalize-Text ("NOTE: " + $s.note + "`n`n" + (Get-Content (Join-Path $textsDir $s.textFile) -Raw).TrimEnd()))
    }
}
$json = ($jsonComponents | ConvertTo-Json -Depth 5 -AsArray)
$jsonText = (Normalize-Text $json) -replace "`n", "`r`n"

# --------------------------------------------------------------------- write
if ($Check) {
    $failed = $false

    foreach ($pair in @(
        @{ Path = $OutputPath;     Expected = $text;     Normalizer = { param($t) Normalize-Versions (Normalize-Text $t) } },
        @{ Path = $JsonOutputPath; Expected = $jsonText; Normalizer = { param($t) Normalize-JsonVersions (Normalize-Text $t) } }
    )) {
        if (-not (Test-Path $pair.Path)) {
            Write-Host "::error::$($pair.Path) is missing. Run: pwsh Scripts/Generate-ThirdPartyNotices.ps1"
            $failed = $true
            continue
        }
        $committed = & $pair.Normalizer (Get-Content $pair.Path -Raw)
        $expected  = & $pair.Normalizer $pair.Expected
        if ($committed.TrimEnd() -cne $expected.TrimEnd()) {
            Write-Host "::error::$($pair.Path) is out of date. Run: pwsh Scripts/Generate-ThirdPartyNotices.ps1 and commit the result."
            Write-Host 'First differing lines:'
            Compare-Object ($committed -split "`n") ($expected -split "`n") |
                Select-Object -First 30 |
                Format-Table -AutoSize | Out-String | Write-Host
            $failed = $true
        }
    }

    if ($failed) { exit 1 }
    Write-Host 'Third-party notices are current.'
    return
}

[System.IO.File]::WriteAllText($OutputPath, $text, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText($JsonOutputPath, $jsonText, [System.Text.UTF8Encoding]::new($false))

$lineCount = ($text -split "`n").Count
Write-Host ("Wrote                 : {0} ({1:N0} bytes, {2:N0} lines)" -f $OutputPath, (Get-Item $OutputPath).Length, $lineCount)
Write-Host ("Wrote                 : {0} ({1:N0} bytes, {2:N0} components)" -f $JsonOutputPath, (Get-Item $JsonOutputPath).Length, $jsonComponents.Count)
