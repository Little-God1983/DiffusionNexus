<#
.SYNOPSIS
    Builds and tests DiffusionNexus in Release and installs it as the next stable version.

.DESCRIPTION
    Builds the solution, runs the test suite, publishes the app into a folder named after the
    version under E:\StableVersion (DiffusionNexus-0.11.0.2483), links that folder to the shared
    data folder, and points E:\StableVersion\DiffusionNexus at it - the fixed path the Start Menu
    entry and any taskbar pin open. Older versions stay beside the new one; delete them by hand when
    you like.

    The version is Major.Minor.Build from Directory.Build.props plus the number of commits on
    develop. It is stamped for this build only: nothing is committed or pushed, so the repo is
    exactly as it was afterwards. Building the same develop commit twice is refused.

    Tests come before the publish on purpose: a build you are about to use is the worst place to
    discover a red suite. A failed build or test run leaves the stable root as it was.

    A running DiffusionNexus is left alone - it may be in the middle of a download or a generation -
    and keeps running the version it was started from. The script says so; restart it to get the
    new one.

    That only happens from a clean develop that is level with origin. On any other branch the script
    stops before building anything; give it -InstallDir for a build that goes somewhere else.

    The database and settings live in %LocalAppData%\DiffusionNexus, so every version shares them.
    The two things the app keeps next to its exe are shared through E:\StableVersion\DiffusionNexus-data:
    the Diffusion Canvas outputs folder (a junction) and LoRA sort's mappings.xml (a hard link).

    The published folder is the same self-contained single-file build publish.ps1 makes for a
    release. Unlike publish.ps1 it does not regenerate the third-party notices (CI checks on every
    PR that the committed ones are current), stage the GPL corresponding source or make a zip: this
    build is for this machine. A zip to hand out is publish.ps1.

.PARAMETER InstallDir
    A folder of your own for the build instead of a versioned one. No Start Menu shortcut is written,
    the current link does not move, and any branch is fine - this is the way to try a feature
    branch's build. The build is stamped <props version>-oneoff.g<commit> so it cannot be mistaken
    for a stable one. Must be outside the repo and outside the stable root, and must be empty or a
    folder this script published a one-off build into before - a folder holding anything else is
    refused untouched. It saves into the shared outputs, and gets a copy of the stable version's
    mappings.xml: a change there stays in the one-off build.

.PARAMETER Clean
    With -InstallDir: wipe that folder before publishing. Only ever a folder carrying our install
    marker. A versioned folder always starts empty, so the switch has nothing to do there.

.PARAMETER SkipTests
    Skip the test run. For when you have just run the suite.

.PARAMETER NoShortcut
    Leave the Start Menu shortcut as it is. A one-off -InstallDir build never writes it.

.EXAMPLE
    .\Scripts\build.ps1
    The next stable version: build, test, publish, link, make current.

.EXAMPLE
    .\Scripts\build.ps1 -InstallDir E:\Builds\DiffusionNexus-test -SkipTests
    Try this branch's build without touching the stable versions or the Start Menu.
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$Clean,
    [switch]$SkipTests,
    [switch]$NoShortcut
)

. (Join-Path $PSScriptRoot '_common.ps1')

# Fixed, not a parameter. A stable build repoints the Start Menu wherever it goes, so a root of your
# own would be a full release somewhere unexpected; -InstallDir is the way to build somewhere else.
$InstallRoot = Resolve-FullPath $DefaultInstallRoot
$DataDir     = Get-DataDir $InstallRoot

# Two ways in: no -InstallDir means the next stable version, with everything that entails; an
# -InstallDir is a one-off build that leaves the stable versions and the Start Menu alone.
$stable = -not $InstallDir
if (-not $stable) { $InstallDir = Resolve-FullPath $InstallDir }

Write-Host ""
Write-Host "  DiffusionNexus - Release build" -ForegroundColor White
if ($stable) { Write-Note "root     $InstallRoot  (next stable version)" }
else         { Write-Note "install  $InstallDir  (one-off build)" }
Write-Note "data     $DataDir"

# --- preflight ---------------------------------------------------------------------------
Write-Step "Checking prerequisites"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail "The .NET SDK is not on your PATH." @("Install the .NET 10 SDK from https://dotnet.microsoft.com/download")
}
Write-Ok ".NET SDK $(dotnet --version)"

if (-not (Test-Path -LiteralPath $UiProject)) {
    Fail "Cannot find $UiProject." @("Run this script from a clone of the DiffusionNexus repo.")
}

$current = Get-PropsVersion
$version = ''
if ($stable) {
    # Branch, clean tree and origin are checked before a single file is built, so a wrong branch
    # costs seconds, not a build and a test run.
    Assert-ReleaseReady
    $version = Get-StableVersion -Current $current -CommitCount (Get-CommitCount)
    $InstallDir = Get-VersionedInstallDir -InstallRoot $InstallRoot -Version $version
    Assert-StableTargetFree -InstallDir $InstallDir -Version $version
    Write-Ok "version $version"
    Write-Note "install  $InstallDir"
    if ($Clean) { Write-Warn "-Clean has nothing to do here: a versioned folder always starts empty" }
}
else {
    $version = Get-OneOffVersion -Current $current -Commit (Get-HeadCommit)
    Write-Ok "version $version"
    if ($NoShortcut) { Write-Note "-NoShortcut is implied: a one-off build never writes the Start Menu shortcut" }
}

# An install folder inside the repo gets swept up by the next build, and each build then copies the
# previous build's output into itself.
if (Test-PathUnder $InstallDir $RepoRoot) {
    Fail "-InstallDir must be outside the repo ($RepoRoot)." @(
        "Publishing into the repo makes every later build copy the last build into itself.",
        "Leave -InstallDir unset to build the next stable version under $InstallRoot."
    )
}

# The stable root is for versioned builds from develop only. A hand-picked folder in there would be
# a "stable version" that skipped every check above.
if (-not $stable -and (Test-PathUnder $InstallDir $InstallRoot -OrEqual)) {
    Fail "-InstallDir must be outside the stable root ($InstallRoot)." @(
        "That folder holds the stable versions, which only build.ps1 without -InstallDir makes, from $ReleaseBranch.",
        "Put a one-off build somewhere else, for example:",
        "  .\Scripts\build.ps1 -InstallDir 'E:\Builds\$AppName-test'"
    )
}

# The install root is shared with other apps (E:\StableVersion holds CodeSwitchX, RawCutX and
# friends). A fresh versioned build deletes its folder first, so we only ever work in a folder
# that is empty or already carries our install marker - and a one-off only in a one-off's folder,
# a second line behind the path check above.
if ((Test-DirectoryHasContent $InstallDir) -and -not (Test-OurInstall $InstallDir -OneOff:(-not $stable))) {
    $what = if ($stable) { 'a DiffusionNexus install made by this script' } else { 'a one-off DiffusionNexus build made by this script' }
    Fail "$InstallDir already holds files that are not $what." @(
        "Not one of them was touched.",
        "Move that folder out of the way (delete it by hand if it is an old test build), or give the build a folder of its own:",
        "  .\Scripts\build.ps1 -InstallDir 'E:\Builds\$AppName-test'"
    )
}

# A one-off build overwrites its folder in place, and a running DiffusionNexus holds its files. It is
# not closed for you: it may be in the middle of a download or a generation. A stable build publishes
# into a new folder nothing has locked, so it never needs this.
if (-not $stable) {
    $locking = @(Get-AppProcess $InstallDir)
    if ($locking.Count -gt 0) {
        Fail "DiffusionNexus is running from $InstallDir (PID $($locking[0].ProcessId))." @(
            "Close it, then run the build again. It is not closed for you, in case it is busy."
        )
    }
}

# The version the Start Menu opens right now, asked before anything moves: its mappings.xml is the
# one the new build carries on with.
$previousDir = Get-DirectoryLinkTarget (Get-CurrentLinkPath $InstallRoot)

$buildFailedHints = @("Scroll up for the first error.")
if ($stable) { $buildFailedHints += "The stable root is as it was; the current version is unchanged." }
# Once the folder is claimed, it holds a part of a build. It is never made current, and only this
# script's marker is on it, so the next run can clean it.
$publishFailedHints = @("Scroll up for the first error.")
if ($stable) { $publishFailedHints += "The current version is unchanged. $InstallDir holds a part of this build; the next run of this commit cleans it." }
else         { $publishFailedHints += "$InstallDir holds a part of this build; run the build again with -Clean." }

# --- build and test ----------------------------------------------------------------------
# PowerShell unwraps a one-element array from an `if`, and splatting a plain string with @ hands
# MSBuild the characters one by one - so the array is built explicitly rather than cast.
# Directory.Build.props derives AssemblyVersion and FileVersion from $(Version), and both must be
# numeric, so a one-off build keeps the props number there; only the version the app shows carries
# the stamp.
$versionArgs = @("-p:Version=$version")
if (-not $stable) { $versionArgs += @("-p:AssemblyVersion=$current", "-p:FileVersion=$current") }

Write-Step "Building (Release)"
dotnet build $Solution --configuration Release --nologo -v minimal @versionArgs
if ($LASTEXITCODE -ne 0) {
    Fail "The build failed (exit code $LASTEXITCODE)." $buildFailedHints
}

if ($SkipTests) {
    Write-Step "Tests SKIPPED (-SkipTests)"
}
else {
    Write-Step "Running tests"
    dotnet test $Solution --configuration Release --no-build --nologo -v minimal
    if ($LASTEXITCODE -ne 0) {
        Fail "The test run failed (exit code $LASTEXITCODE)." @(
            @("Nothing was published.") + @($buildFailedHints | Select-Object -Skip 1)
        )
    }
}

# --- clean -------------------------------------------------------------------------------
# A versioned folder always starts empty. It can only exist from an earlier run of this exact
# commit that failed after claiming it, before the folder became current.
if (($stable -or $Clean) -and (Test-DirectoryHasContent $InstallDir)) {
    Write-Step "Cleaning $InstallDir"

    # Belt and braces: the guard above already rejected a folder that is not ours, but a recursive
    # delete checks for itself too.
    if (-not (Test-OurInstall $InstallDir -OneOff:(-not $stable))) {
        Fail "Refusing to clean $InstallDir - it carries no DiffusionNexus install marker." @(
            "Only a folder this script published into is ever deleted."
        )
    }

    try { Remove-InstallFolder $InstallDir }
    catch {
        Fail "Could not clean $InstallDir - $($_.Exception.Message)" @(
            "Something is probably still holding a file there. Close it and try again.",
            "Your shared outputs in $DataDir were not touched."
        )
    }
    Write-Ok "removed"
}

# --- claim the folder ----------------------------------------------------------------------
# Before the publish, not after. A publish interrupted part-way (Ctrl+C, a build error, a virus
# scanner holding a file) would otherwise leave a folder full of files and no marker, which every
# later run refuses to publish into *and* refuses to clean. The checks above established the folder
# is empty or ours, so claiming it here is safe. The marker says "complete": false until a one-off's
# publish has succeeded or a stable build has been made current. A complete stable folder is never
# cleaned again (Assert-StableTargetFree), so "complete" must not be said of a build that never
# became current - the next run has to be able to rebuild that one.
Write-InstallMarker -InstallDir $InstallDir -Version $version

# --- publish -----------------------------------------------------------------------------
# The switches publish.ps1 uses for a release, so the stable build is the exe users get. The LGPL
# assemblies and LibVLC stay beside the exe (DiffusionNexus.UI.csproj), not inside it.
Write-Step "Publishing (self-contained single file)"
dotnet publish $UiProject --configuration Release --runtime win-x64 --self-contained true --output $InstallDir --nologo -v minimal `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false @versionArgs
if ($LASTEXITCODE -ne 0) {
    Fail "The publish failed (exit code $LASTEXITCODE)." $publishFailedHints
}

if (-not (Test-Path -LiteralPath (Join-Path $InstallDir $AppExeName))) {
    Fail "The publish reported success but $AppExeName is missing from $InstallDir." $publishFailedHints
}

$removed = Remove-PublishLeftovers $InstallDir
if ($removed -gt 0) { Write-Note "removed $removed build leftovers publish.ps1 strips too" }

# The About screen opens THIRD-PARTY-NOTICES.txt from beside the exe, and the licence travels with
# every copy, as in a release.
foreach ($name in 'THIRD-PARTY-NOTICES.txt', 'LICENSE') {
    $source = Join-Path $RepoRoot $name
    if (-not (Test-Path -LiteralPath $source)) {
        Fail "$name is missing from the repository root." $publishFailedHints
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $InstallDir $name) -Force
}

Write-PayloadReport $InstallDir

if (-not $stable) { Write-InstallMarker -InstallDir $InstallDir -Version $version -Complete }
Write-Ok "published to $InstallDir"

# --- share the user's data -----------------------------------------------------------------
# Before the folder becomes current, so the first start of the new version already sees the
# shared outputs and mappings.
Write-Step "Linking the shared data"
Set-OutputsLink -InstallDir $InstallDir -DataDir $DataDir
Write-Ok "$OutputsName -> $(Join-Path $DataDir $OutputsName)"
switch (Set-MappingsLink -InstallDir $InstallDir -DataDir $DataDir -PreviousDir $previousDir -CopyOnly:(-not $stable)) {
    'linked'   { Write-Ok "$MappingsName shared with $(Join-Path $DataDir $MappingsName)" }
    'one-off copy' { Write-Ok "$MappingsName copied from the stable version - changes in this one-off build stay here" }
    'copied'   { Write-Warn "$MappingsName copied, not shared: $InstallDir is on another drive than $DataDir, so a change there stays there" }
    'own copy' { Write-Warn "$InstallDir already has a $MappingsName of its own - left as it is, not shared" }
    default    { Write-Note "no $MappingsName in the current version - nothing to carry over" }
}

# --- make it the current version ------------------------------------------------------------
# The fixed path E:\StableVersion\DiffusionNexus is what the Start Menu entry and any taskbar pin
# open. Pointing it at the new folder is what makes this build "current" without every pin on the
# machine having to change.
$shortcutDir = $InstallDir
$running = @()
if ($stable) {
    Write-Step "Making $version the current version"
    if (Set-CurrentLink -InstallRoot $InstallRoot -Target $InstallDir) {
        $shortcutDir = Get-CurrentLinkPath $InstallRoot
        Write-Ok "$shortcutDir -> $InstallDir"
    }
    else {
        Write-Warn "no current link; the shortcut will point at $InstallDir directly, and a taskbar pin made earlier still opens the old build"
    }
    # Only now, with the data linked and the folder current. Had either step thrown, the folder
    # would be finished on paper yet never used, and every later run would refuse to rebuild it.
    Write-InstallMarker -InstallDir $InstallDir -Version $version -Complete

    if ($NoShortcut) {
        Write-Ok "Start Menu shortcut left as it is, as asked"
        if ($shortcutDir -eq $InstallDir) {
            Write-Warn "the existing Start Menu shortcut still opens the previous build"
        }
    }
    else {
        $linkPath = Write-StartMenuShortcut $shortcutDir
        Write-Ok "Start Menu shortcut at $linkPath"
        Write-Note "right-click it in the Start Menu and choose 'Pin to taskbar'"
    }

    # Older version folders keep their outputs link: one started by hand for a rollback still saves
    # its images into the shared folder. Deleting such a folder deletes the link, not the images.
    $running = @(Get-AppProcess -AnyVersion -InstallRoot $InstallRoot)
}

# --- done ----------------------------------------------------------------------------------
$exePath = Join-Path $shortcutDir $AppExeName
Write-Host ""
if ($stable) { Write-Host "  Build complete - DiffusionNexus $version" -ForegroundColor Green }
else         { Write-Host "  Build complete - DiffusionNexus $version (one-off)" -ForegroundColor Green }
foreach ($proc in $running) {
    if (Test-StartedThroughLink $proc.ExecutablePath -LeadsTo $InstallDir) {
        Write-Warn ("DiffusionNexus (PID $($proc.ProcessId)) was started through $([System.IO.Path]::GetDirectoryName($proc.ExecutablePath)), which now leads to $version. " +
            "It keeps the old version in memory but loads files it opens later - video, workflows, dictionaries - from the new folder. Restart it soon.")
    }
    else {
        Write-Warn "DiffusionNexus is still running from $($proc.ExecutablePath) (PID $($proc.ProcessId)) - the old version; restart it for $version"
    }
}
if ($stable -and -not $NoShortcut) { Write-Host "  Start it from the Start Menu (Diffusion Nexus), or: " -NoNewline }
else                               { Write-Host "  Start it: " -NoNewline }
Write-Host $exePath -ForegroundColor White
Write-Host ""
