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

    Packages other than the runtime packs carry notice files too - SkiaSharp and HarfBuzzSharp
    ship ~2,700 lines covering the native code compiled into libSkiaSharp.dll and
    libHarfBuzzSharp.dll, which self-contained publishing embeds into the exe. Every package in
    the closure is therefore probed for a notice file, and the script refuses to emit unless
    each one found is either reproduced (a bundledNotices entry) or attested as a byte-for-byte
    duplicate of one that is (a noticesAlreadyCovered entry, re-verified on every run). This has
    to be structural: -Check compares generated output against committed output, so a notice the
    generator never looks for is missing from both sides and compares equal.

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

# ------------------------------------------------------------- notice files
# A "notice file" is a package's own THIRD-PARTY-NOTICES / NOTICE document: attribution for
# third-party code compiled INTO that package's binaries, which no licence expression can
# express. The search is recursive, and returns EVERY match rather than the first, because a
# package that splits its notice or moves it into a subdirectory must not lose the remainder
# silently - that is the whole failure mode this script exists to prevent.
function Get-NoticeFiles {
    param([string]$Dir)
    return @(Get-ChildItem -Path $Dir -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'THIRD-PARTY-NOTICES*' -or $_.Name -like 'NOTICE*' } |
        Sort-Object FullName)
}

function Get-NoticeRelativePath {
    param([string]$Dir, [string]$FullName)
    return (($FullName.Substring($Dir.Length + 1)) -replace '\\', '/')
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
        foreach ($file in (Get-NoticeFiles $dir)) {
            $runtimePacks += [pscustomobject]@{ Name = $d.name; Version = $v; FileName = $file.Name; File = $file.FullName }
        }
    }
}
$runtimePacks = @($runtimePacks | Sort-Object Name, Version, FileName | Group-Object Name, FileName | ForEach-Object { $_.Group[0] })
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

# ------------------------------------------------------ fail-closed notice probe
# -Check can never catch a gap in THIS generator: it compares generated output against
# committed output, so a notice file the generator never looks for is absent from both sides
# and compares equal. The defence has to be structural. Probe every package in the closure and
# refuse to emit unless each notice file found is either
#   reproduced - a bundledNotices entry naming that exact file, or
#   attested   - a noticesAlreadyCovered entry naming a reproduction it duplicates byte for
#                byte, RE-VERIFIED here on every run so the attestation cannot rot silently.
# Anything else throws, naming the package and the file.
$coveredEntries = @($supplements.noticesAlreadyCovered)

foreach ($cov in $coveredEntries) {
    $pkg = $components | Where-Object { $_.Id -eq $cov.packageId } | Select-Object -First 1
    if (-not $pkg) {
        throw "noticesAlreadyCovered entry for '$($cov.packageId)' is no longer in the restore graph. Update supplements.json."
    }
    $own = Join-Path $pkg.PackageDir $cov.file
    if (-not (Test-Path $own)) {
        throw "noticesAlreadyCovered entry for '$($cov.packageId)' names '$($cov.file)', which that package does not contain. Update supplements.json."
    }

    # duplicateOf points at something this document actually reproduces: a runtime pack
    # (section 3's first half) or a bundledNotices package (its second half).
    $sourceFile = $null
    $rp = $runtimePacks | Where-Object { $_.Name -eq $cov.duplicateOf } | Select-Object -First 1
    if ($rp) {
        $sourceFile = $rp.File
    }
    else {
        $src = $supplements.bundledNotices | Where-Object { $_.packageId -eq $cov.duplicateOf } | Select-Object -First 1
        if ($src) {
            $srcPkg = $components | Where-Object { $_.Id -eq $src.packageId } | Select-Object -First 1
            if ($srcPkg) { $sourceFile = Join-Path $srcPkg.PackageDir $src.file }
        }
    }
    if (-not $sourceFile -or -not (Test-Path $sourceFile)) {
        throw "noticesAlreadyCovered entry for '$($cov.packageId)' claims to duplicate '$($cov.duplicateOf)', which this document does not reproduce (no such runtime pack and no such bundledNotices package in this graph)."
    }

    if ((Normalize-Text (Get-Content $own -Raw)) -cne (Normalize-Text (Get-Content $sourceFile -Raw))) {
        throw "Package '$($cov.packageId)' no longer ships the same '$($cov.file)' as '$($cov.duplicateOf)'. The notice has diverged, so it must be reproduced through its own bundledNotices entry instead of being attested as a duplicate."
    }
}

$uncovered = @()
foreach ($c in $components) {
    foreach ($file in (Get-NoticeFiles $c.PackageDir)) {
        $rel = Get-NoticeRelativePath $c.PackageDir $file.FullName
        if ($supplements.bundledNotices     | Where-Object { $_.packageId -eq $c.Id -and $_.file -eq $rel }) { continue }
        if ($coveredEntries                 | Where-Object { $_.packageId -eq $c.Id -and $_.file -eq $rel }) { continue }
        $uncovered += ("{0} {1} :: {2}" -f $c.Id, $c.Version, $rel)
    }
}
if ($uncovered.Count -gt 0) {
    throw ("These packages carry a notice file that this document neither reproduces nor attests as already covered:`n  " +
           ($uncovered -join "`n  ") +
           "`nIn Scripts/license-data/supplements.json, add a bundledNotices entry naming the file (to reproduce it verbatim), " +
           "or - only if the file is byte-for-byte identical to a notice already reproduced - a noticesAlreadyCovered entry with " +
           "duplicateOf and reason. A notice file that is neither is ~unbounded missing attribution in a legal document.")
}
Write-Host ("Package notice files  : {0} attested as duplicates of a reproduced notice" -f $coveredEntries.Count)

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
    # A FILE-licensed component has no SPDX text to route to: its licence IS one of its bundled
    # files, emitted by the bundled-notice loops (section 3 of the document, and the per-package
    # loop of the JSON index). Routing it here as well would print it twice, and picking "the
    # first bundled entry" would pick the wrong file for a package that has more than one.
    if ($Component.License -eq 'FILE') {
        throw "Get-LicenseTextFor called for FILE-licensed component '$($Component.Id)'; its text comes from its bundledNotices entries."
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
    # Carry everything the text document carries for this component, in the same order, so the
    # two can never disagree: the licence text, then every notice file bundled for it, each
    # behind the NOTE that explains why it is there. Both were previously dropped here - the
    # About screen showed a bare MIT for SkiaSharp while the text file reproduced 2,716 lines
    # of native attribution, and showed FILE-licensed packages' texts with no explanation.
    $parts = @()
    if ($c.License -ne 'FILE') { $parts += (Get-LicenseTextFor $c) }
    foreach ($b in ($supplements.bundledNotices | Where-Object { $_.packageId -eq $c.Id })) {
        $file = Join-Path $c.PackageDir $b.file
        if (-not (Test-Path $file)) { throw "Notice file '$($b.file)' not found in package '$($c.Id)'." }
        if ($b.note) { $parts += ("NOTE: " + $b.note) }
        $parts += (Get-Content $file -Raw).TrimEnd()
    }

    $jsonComponents += [pscustomobject][ordered]@{
        id          = $c.Id
        version     = $c.Version
        license     = if ($c.License -eq 'FILE') { 'See notice file' } else { $c.License }
        copyright   = $c.Copyright
        authors     = $c.Authors
        projectUrl  = $c.ProjectUrl
        licenseText = Normalize-Text ($parts -join "`n`n")
    }
}
# A pack contributing more than one notice file would otherwise produce two rows with the same
# id, which reads as a duplicate rather than as two documents.
$rpFileCounts = @{}
foreach ($rp in $runtimePacks) {
    if ($rpFileCounts.ContainsKey($rp.Name)) { $rpFileCounts[$rp.Name]++ } else { $rpFileCounts[$rp.Name] = 1 }
}
foreach ($rp in $runtimePacks) {
    $jsonComponents += [pscustomobject][ordered]@{
        id          = if ($rpFileCounts[$rp.Name] -gt 1) { "{0} ({1})" -f $rp.Name, $rp.FileName } else { $rp.Name }
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
