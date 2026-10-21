# Shared settings and helpers for the DiffusionNexus stable build scripts.
# Dot-sourced by build.ps1 - not meant to be run on its own.

$ErrorActionPreference = 'Stop'

# PowerShell 7.4+ turns a non-zero native exit code into a thrown exception. We check
# $LASTEXITCODE ourselves so the failure prints as a readable message, not a stack trace.
if (Test-Path Variable:\PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$script:RepoRoot  = Split-Path -Parent $PSScriptRoot
$script:Solution  = Join-Path $RepoRoot 'DiffusionNexus.sln'
$script:UiProject = Join-Path $RepoRoot 'DiffusionNexus.UI\DiffusionNexus.UI.csproj'

# Every stable build gets a folder of its own under this root, named after the app and the version it
# was built at: E:\StableVersion\DiffusionNexus-0.11.0.2483. Older versions stay beside it.
# E:\StableVersion\DiffusionNexus is a junction to the newest one.
#
# The version is Major.Minor.Build from the <Version> in Directory.Build.props plus the number of
# commits on develop, stamped for the build only. Nothing is committed or pushed: develop takes
# commits only through PRs, and release.yml stamps its own revision on main the same way, from
# the run number. A commit count only grows on develop, so every new develop commit gets a new
# folder, and building the same commit twice is refused.
#
# The database and the settings live in %LocalAppData%\DiffusionNexus whichever folder the app
# runs from. Two things of the user's are written next to the exe: the Diffusion Canvas outputs
# folder (OutputsFolderRegistrar) and LoRA sort's mappings.xml (CustomTagMapXmlService). Those go
# to the shared data folder beside the versions, so a new version folder holds program files only.
$script:AppName            = 'DiffusionNexus'
$script:DefaultInstallRoot = 'E:\StableVersion'
$script:ReleaseBranch      = 'develop'
$script:PropsPath          = Join-Path $RepoRoot 'Directory.Build.props'
$script:AppExeName         = 'DiffusionNexus.UI.exe'
$script:ShortcutName       = 'Diffusion Nexus.lnk'
$script:OutputsName        = 'outputs'
$script:MappingsName       = 'mappings.xml'

# build.ps1 drops this in the install folder. Nothing is ever deleted from a folder that does
# not carry it, so a mistyped -InstallDir cannot take out a neighbouring release.
$script:InstallMarkerName = 'diffusionnexus-install.json'

function Write-Step { param([string]$Message) Write-Host ""; Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note { param([string]$Message) Write-Host "    $Message" -ForegroundColor DarkGray }
function Write-Ok   { param([string]$Message) Write-Host "    [ok] $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "    [!]  $Message" -ForegroundColor Yellow }

function Fail {
    param([string]$Message, [string[]]$Hints)
    Write-Host ""
    Write-Host "    [x] $Message" -ForegroundColor Red
    foreach ($hint in $Hints) { Write-Host "        $hint" -ForegroundColor Yellow }
    Write-Host ""
    exit 1
}

# Absolute path, with no requirement that it already exists.
function Resolve-FullPath {
    param([string]$Path)
    return [System.IO.Path]::GetFullPath([System.IO.Path]::Combine((Get-Location).ProviderPath, $Path))
}

# The compiled helper behind Resolve-RealPath and Get-FolderId. A type added with Add-Type stays in
# the PowerShell window until it closes, so the name is this repo's own and carries a number: change
# the number whenever the C# changes, or a window that ran an older copy keeps using that one. The
# C# is kept to what Windows PowerShell 5.1 compiles.
function Get-PathInfoType {
    $name = 'DiffusionNexus.BuildScripts.PathInfoV2'
    if (-not ($name -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiffusionNexus.BuildScripts
{
    public static class PathInfoV2
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public long Created;
            public long Accessed;
            public long Written;
            public uint VolumeSerial;
            public uint SizeHigh;
            public uint SizeLow;
            public uint Links;
            public uint IndexHigh;
            public uint IndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

        // No access asked for, every kind of sharing allowed, and BACKUP_SEMANTICS so a folder opens.
        private static SafeFileHandle Open(string path)
        {
            return CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        }

        public static string RealPath(string path)
        {
            using (SafeFileHandle handle = Open(path))
            {
                if (handle.IsInvalid) { return null; }
                StringBuilder buffer = new StringBuilder(4096);
                uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) { return null; }
                string real = buffer.ToString();
                if (real.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) { return @"\\" + real.Substring(8); }
                if (real.StartsWith(@"\\?\", StringComparison.Ordinal)) { return real.Substring(4); }
                return real;
            }
        }

        // Which file or folder it is, however the path is spelled: the serial number of the volume
        // and the number of the file on that volume.
        public static string Id(string path)
        {
            using (SafeFileHandle handle = Open(path))
            {
                if (handle.IsInvalid) { return null; }
                FileInformation information;
                if (!GetFileInformationByHandle(handle, out information)) { return null; }
                return information.VolumeSerial.ToString("x8") + ":" + information.IndexHigh.ToString("x8") + information.IndexLow.ToString("x8");
            }
        }
    }
}
'@
    }
    return ($name -as [type])
}

# The path with every alias taken out of it: an 8.3 name (E:\STABLE~1), a junction or symlink, a
# subst drive. Windows only resolves those for something that exists, so the nearest existing
# ancestor is asked and whatever is not there yet is put back on the end. Falls back to the plain
# full path when Windows cannot say.
function Resolve-RealPath {
    param([string]$Path)
    $full = Resolve-FullPath $Path
    $existing = $full
    $missing  = @()
    while ($existing -and -not (Test-Path -LiteralPath $existing)) {
        $missing  = @([System.IO.Path]::GetFileName($existing.TrimEnd('\'))) + $missing
        $existing = [System.IO.Path]::GetDirectoryName($existing.TrimEnd('\'))
    }
    if (-not $existing) { return $full }
    $real = (Get-PathInfoType)::RealPath($existing)
    if (-not $real) { return $full }
    foreach ($name in $missing) { $real = Join-Path $real $name }
    return $real
}

# Which file or folder a path leads to, as an ID that is the same through every spelling of it, or
# $null for something that does not exist. A real path cannot do this for a share that leads back
# to this PC: \\localhost\E$\StableVersion stays a share path, and only the ID says it is
# E:\StableVersion. Two hard links of one file have the same ID too.
function Get-FolderId {
    param([string]$Path)
    return (Get-PathInfoType)::Id((Resolve-FullPath $Path))
}

# True when two paths lead to the same file or folder, whether or not it exists yet.
function Test-SamePath {
    param([string]$Path, [string]$Other)
    if ((Resolve-RealPath $Path).TrimEnd('\') -ieq (Resolve-RealPath $Other).TrimEnd('\')) { return $true }
    $id = Get-FolderId $Path
    return ($null -ne $id) -and ($id -eq (Get-FolderId $Other))
}

# Asks where the path really goes, not how it is spelled, so an alias of the parent is still under
# it, whether or not the folder itself exists yet. Two questions, because each sees what the other
# cannot: the real path takes out an 8.3 name (E:\STABLE~1), a junction and a subst drive; the ID
# of each folder above the path catches a share or a mapped drive that leads back to this PC.
function Test-PathUnder {
    param([string]$Path, [string]$Parent, [switch]$OrEqual)
    $full   = (Resolve-RealPath $Path).TrimEnd('\')
    $parent = (Resolve-RealPath $Parent).TrimEnd('\')
    if ($OrEqual -and $full.Equals($parent, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if ($full.StartsWith($parent + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }

    $parentId = Get-FolderId $Parent
    if (-not $parentId) { return $false }
    $folder = (Resolve-FullPath $Path).TrimEnd('\')
    if (-not $OrEqual) { $folder = [System.IO.Path]::GetDirectoryName($folder) }
    while ($folder) {
        if ((Get-FolderId $folder) -eq $parentId) { return $true }
        $folder = [System.IO.Path]::GetDirectoryName($folder.TrimEnd('\'))
    }
    return $false
}

# True for a folder name that is one of our version folders: the app name, a dash and a digit.
# E:\StableVersion also holds DiffusionNexus-installer-2.0 and DiffusionNexus-data, which are not.
function Test-VersionFolderName {
    param([string]$Name)
    return $Name -match ('^' + [regex]::Escape($AppName) + '-\d')
}

# DiffusionNexus processes started from this install folder. With -AnyVersion, every one started from
# a versioned folder under the root or through the current link - "the stable build". A Debug build
# from the repo is never among them, nor the old single exe that sits directly in the root.
function Get-AppProcess {
    param([string]$InstallDir, [switch]$AnyVersion, [string]$InstallRoot = $DefaultInstallRoot)
    $all = @(Get-CimInstance Win32_Process -Filter "Name = '$AppExeName'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath })
    # Windows reports the exe path the way the process was started - through an 8.3 name, a
    # junction, a subst drive - so real paths are compared, never the spelling.
    if ($AnyVersion) {
        # Exactly our folders: the current link itself, or a versioned folder directly under the
        # real root. Not a neighbour whose name happens to start the same way.
        $current = Get-CurrentLinkPath $InstallRoot
        $root    = (Resolve-RealPath $InstallRoot).TrimEnd('\')
        return @($all | Where-Object {
            $folder = [System.IO.Path]::GetDirectoryName((Resolve-RealPath $_.ExecutablePath))
            (Test-PathUnder $_.ExecutablePath $current) -or
            (([System.IO.Path]::GetDirectoryName($folder) -ieq $root) -and (Test-VersionFolderName (Split-Path -Leaf $folder)))
        })
    }
    $exePath = Join-Path (Resolve-FullPath $InstallDir) $AppExeName
    return @($all | Where-Object { Test-SamePath $_.ExecutablePath $exePath })
}

# git with this script's error handling switched off for the call. Windows PowerShell 5.1, which the .cmd
# wrapper falls back to, turns a native command's stderr into an error record, and under
# $ErrorActionPreference = 'Stop' that ends the script with a stack trace. -Quiet drops stderr; without
# it the user sees git's own message. Check $LASTEXITCODE afterwards.
function Invoke-Git {
    param([string[]]$Arguments, [switch]$Quiet)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Quiet) { & git -C $RepoRoot @Arguments 2>$null }
        else        { & git -C $RepoRoot @Arguments }
    }
    finally { $ErrorActionPreference = $previous }
}

# The short commit of HEAD, or '' when git is missing or this is no checkout.
function Get-HeadCommit {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return '' }
    $commit = "$(Invoke-Git @('rev-parse', '--short', 'HEAD') -Quiet)".Trim()
    if ($LASTEXITCODE -ne 0) { return '' }
    return $commit
}

# A Start Menu entry the user can pin to the taskbar with a right-click. Pinning itself
# cannot be scripted on Windows 11.
function Write-StartMenuShortcut {
    param([string]$InstallDir)
    $programs = [Environment]::GetFolderPath('Programs')
    $linkPath = Join-Path $programs $ShortcutName
    $shell    = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($linkPath)
    $shortcut.TargetPath       = Join-Path $InstallDir $AppExeName
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.IconLocation     = (Join-Path $InstallDir $AppExeName) + ',0'
    $shortcut.Description      = 'Diffusion Nexus'
    $shortcut.Save()
    return $linkPath
}

# Deletes the link itself and never what it points at. Remove-Item -Recurse on a junction
# has historically walked into the target and deleted the real files.
function Remove-DirectoryLink {
    param([string]$Path)
    [System.IO.Directory]::Delete($Path, $false)
}

# Where a directory link points, or $null for a real folder or nothing at all.
function Get-DirectoryLinkTarget {
    param([string]$Path)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item -and $item.PSObject.Properties['Target'] -and @($item.Target)[0]) { return @($item.Target)[0] }
    return $null
}

# The parsed install marker of a folder, or $null when there is none, it cannot be read, or it is
# another app's.
function Read-InstallMarker {
    param([string]$InstallDir)
    $path = Join-Path $InstallDir $InstallMarkerName
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { $marker = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    catch { return $null }
    if ($null -eq $marker -or $marker.app -ne $AppName) { return $null }
    return $marker
}

# True only for a folder a previous build.ps1 published into. Everything destructive in these
# scripts is gated on this, so a folder we did not create is never cleaned. With -OneOff, only a
# one-off build's folder - a second line behind the path check, since a stable release is never a
# one-off's folder.
function Test-OurInstall {
    param([string]$InstallDir, [switch]$OneOff)
    $marker = Read-InstallMarker $InstallDir
    if ($null -eq $marker) { return $false }
    return (-not $OneOff) -or ("$($marker.version)" -match '-oneoff\.')
}

# True for a finished build. A stable folder is marked complete only once it has been made current;
# a one-off folder once its publish succeeded. Only a real true counts: a hand-edited "false" is
# not finished.
function Test-InstallComplete {
    param([string]$InstallDir)
    $marker = Read-InstallMarker $InstallDir
    return ($null -ne $marker) -and ($marker.complete -eq $true)
}

# True when the current link beside this folder points at it - the folder the Start Menu opens.
function Test-InstallCurrent {
    param([string]$InstallDir)
    $target = Get-DirectoryLinkTarget (Get-CurrentLinkPath (Split-Path -Parent $InstallDir))
    if (-not $target) { return $false }
    return (Test-SamePath $target $InstallDir)
}

# A stable build never republishes over a version folder that was made current. The number comes
# from the commit count, so the same number means the same develop commit was built before - and
# its folder may be the one the Start Menu opens, perhaps with DiffusionNexus running from it. Both
# signs are asked, the marker and the link itself, so a run that died between moving the link and
# marking the folder is still caught. A folder that never became current - an interrupted publish,
# a link that could not be made - is neither, so build.ps1 still cleans that one; unless somebody
# started DiffusionNexus from it by hand, in which case the clean would stop at the locked exe with
# half the folder gone.
function Assert-StableTargetFree {
    param([string]$InstallDir, [string]$Version)
    if ((Test-InstallComplete $InstallDir) -or (Test-InstallCurrent $InstallDir)) {
        $marker = Read-InstallMarker $InstallDir
        $built  = if ($marker -and $marker.commit) { " (commit $($marker.commit))" } else { '' }
        Fail "$Version is already published in $InstallDir$built." @(
            "A finished stable folder is never republished over - it may be the version the Start Menu opens right now.",
            "The number comes from the commits on $ReleaseBranch, so $ReleaseBranch has not moved since that build.",
            "Nothing was touched. Merge something new into $ReleaseBranch, then:  git pull; .\Scripts\build.ps1",
            "To try uncommitted or branch work, use a folder of your own:  .\Scripts\build.ps1 -InstallDir 'E:\Builds\$AppName-test'"
        )
    }
    $running = @(Get-AppProcess $InstallDir)
    if ($running.Count -gt 0) {
        Fail "DiffusionNexus is running from $InstallDir (PID $($running[0].ProcessId))." @(
            "That folder holds a build of $Version that was never made current, and this run would rebuild it.",
            "Close that DiffusionNexus, then run the build again."
        )
    }
}

# Deletes an install folder with its marker last. The marker is what lets a later run clean the
# folder, so a delete that stops half-way - a file still held open - must not have taken it first.
# The name is compared, not the full path: Get-ChildItem writes an 8.3 name out long, so a full
# path built from C:\Users\LITTLE~1\... never matches. A link in the folder - the outputs junction
# into the shared data folder - is unlinked, never deleted recursively: that would walk into the
# user's images.
function Remove-InstallFolder {
    param([string]$InstallDir)
    foreach ($item in @(Get-ChildItem -LiteralPath $InstallDir -Force | Where-Object { $_.Name -ine $InstallMarkerName })) {
        if ($item.PSIsContainer -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-DirectoryLink $item.FullName
        }
        else {
            Remove-Item -LiteralPath $item.FullName -Recurse -Force
        }
    }
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
}

function Write-InstallMarker {
    param([string]$InstallDir, [string]$Version = '', [switch]$Complete)
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    [ordered]@{
        app         = $AppName
        version     = $Version
        complete    = [bool]$Complete
        installedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        commit      = Get-HeadCommit
        note        = 'Written by Scripts/build.ps1. Without this file the scripts refuse to clean this folder.'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallDir $InstallMarkerName) -Encoding UTF8
}

function Test-DirectoryHasContent {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    return (@(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue).Count -gt 0)
}

# What publish.ps1 strips from a release folder: the EF Core design-time build hosts, the ONNX
# runtime's static library stub and the debug symbols. None of them is needed to run the app.
function Remove-PublishLeftovers {
    param([string]$PublishDir)
    $removed = 0
    foreach ($name in 'BuildHost-net472', 'BuildHost-netcore', 'onnxruntime.lib') {
        $path = Join-Path $PublishDir $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force; $removed++ }
    }
    foreach ($pdb in @(Get-ChildItem -LiteralPath $PublishDir -Filter '*.pdb' -File -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $pdb.FullName -Force
        $removed++
    }
    return $removed
}

# What every published folder is checked for.
function Write-PayloadReport {
    param([string]$PublishDir)
    $files  = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File)
    $sizeMb = ($files | Measure-Object Length -Sum).Sum / 1MB
    Write-Note ("{0:N1} MB in {1:N0} files" -f $sizeMb, $files.Count)

    # Video playback. Without it the app still starts, and only a video preview says something is
    # wrong, so it is worth a word here.
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDir 'libvlc\win-x64\libvlc.dll'))) {
        Write-Warn "libvlc\win-x64\libvlc.dll is missing - this build cannot play videos"
    }
}

# --- versioned stable builds ---------------------------------------------------------------

function Get-VersionedInstallDir {
    param([string]$InstallRoot, [string]$Version)
    return Join-Path $InstallRoot "$AppName-$Version"
}

# The fixed path the Start Menu shortcut and any taskbar pin target: E:\StableVersion\DiffusionNexus,
# a junction build.ps1 points at the newest versioned folder, so a pin made months ago keeps opening
# the current build.
function Get-CurrentLinkPath {
    param([string]$InstallRoot = $DefaultInstallRoot)
    return Join-Path (Resolve-FullPath $InstallRoot) $AppName
}

# The folder beside the versions that holds the user's outputs and mappings.xml for all of them.
function Get-DataDir {
    param([string]$InstallRoot = $DefaultInstallRoot)
    return Join-Path (Resolve-FullPath $InstallRoot) "$AppName-data"
}

# Points the fixed path at the folder just published: creates the junction, or retargets the one an
# earlier build made. A real folder of that name is left alone and reported - the shortcut then has to
# point at the versioned folder directly. Returns $true when the link now points at the target.
function Set-CurrentLink {
    param([string]$InstallRoot, [string]$Target)
    $linkPath = Get-CurrentLinkPath $InstallRoot
    $target   = (Resolve-FullPath $Target).TrimEnd('\')

    if (Test-Path -LiteralPath $linkPath) {
        $existingTarget = Get-DirectoryLinkTarget $linkPath
        if (-not $existingTarget) {
            Write-Warn "$linkPath is a real folder, not a link to a version - left alone"
            return $false
        }
        if ((Resolve-FullPath $existingTarget).TrimEnd('\') -ieq $target) { return $true }
        Remove-DirectoryLink $linkPath
    }

    New-Item -ItemType Junction -Path $linkPath -Target $target | Out-Null
    return $true
}

# Points <InstallDir>\outputs at the shared outputs folder, so the images Diffusion Canvas saves
# next to the exe land in one place for every version. A junction needs no admin rights. A real
# outputs folder is refused, never merged or deleted: it holds images.
function Set-OutputsLink {
    param([string]$InstallDir, [string]$DataDir)
    $target = Join-Path $DataDir $OutputsName
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $linkPath = Join-Path $InstallDir $OutputsName

    if (Test-Path -LiteralPath $linkPath) {
        $existing = Get-DirectoryLinkTarget $linkPath
        if (-not $existing) {
            Fail "$linkPath is a real folder, not a link to your shared outputs." @(
                "It may hold images Diffusion Canvas saved there. Nothing in it was touched.",
                "Move its images into $target, delete the empty folder, then run the build again."
            )
        }
        if (Test-SamePath $existing $target) { return }
        Remove-DirectoryLink $linkPath
    }
    New-Item -ItemType Junction -Path $linkPath -Target $target | Out-Null
}

# mappings.xml is a file, and a file link needs admin rights, so it is shared through a hard link:
# one file under two names, which the app's StreamWriter rewrites in place. What the version the
# Start Menu opened last has is the truth: if its mappings.xml is not the shared one yet - the first
# build, or the app recreated the file after "delete all mappings" - its content becomes the shared
# one. A version without the file gets none, so deleted mappings stay deleted. Returns what happened.
function Set-MappingsLink {
    param([string]$InstallDir, [string]$DataDir, [string]$PreviousDir)
    $shared = Join-Path $DataDir $MappingsName
    $own    = Join-Path $InstallDir $MappingsName
    $before = if ($PreviousDir) { Join-Path $PreviousDir $MappingsName } else { $null }

    if (-not $before -or -not (Test-Path -LiteralPath $before -PathType Leaf)) { return 'none yet' }
    if (-not (Test-SamePath $before $shared)) {
        New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
        # Written into the existing file, not copied over it, so every hard link to it sees the change.
        [IO.File]::WriteAllBytes($shared, [IO.File]::ReadAllBytes($before))
    }

    if (Test-Path -LiteralPath $own) {
        if (Test-SamePath $own $shared) { return 'linked' }
        return 'own copy'
    }
    try {
        New-Item -ItemType HardLink -Path $own -Target $shared | Out-Null
        return 'linked'
    }
    catch {
        # Another drive than the data folder: a hard link cannot cross volumes.
        Copy-Item -LiteralPath $shared -Destination $own
        return 'copied'
    }
}

# Unlinks the outputs junction of every older version folder nothing runs from, so deleting an old
# version by hand can never reach the shared images. A version still running keeps its link: the
# app there may be saving an image right now. Returns the folders it could not unlink.
function Remove-SupersededOutputsLinks {
    param([string]$InstallRoot, [string]$Current)
    $kept = @()
    foreach ($dir in @(Get-ChildItem -LiteralPath $InstallRoot -Directory -Force -ErrorAction SilentlyContinue)) {
        if (-not (Test-VersionFolderName $dir.Name)) { continue }
        if ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        if (Test-SamePath $dir.FullName $Current) { continue }
        if (-not (Test-OurInstall $dir.FullName)) { continue }
        $link = Join-Path $dir.FullName $OutputsName
        if (-not (Get-DirectoryLinkTarget $link)) { continue }
        if (@(Get-AppProcess $dir.FullName).Count -gt 0) { $kept += $dir.FullName; continue }
        Remove-DirectoryLink $link
    }
    return $kept
}

# Directory.Build.props is hand-written XML with comments in it, so it is read as one string and
# only the number is looked at. It is never written: the version is stamped for the build only.
function Get-PropsVersion {
    if (-not (Test-Path -LiteralPath $PropsPath)) {
        Fail "Cannot find $PropsPath." @("The version lives in its <Version> element.")
    }
    $found = [regex]::Matches([IO.File]::ReadAllText($PropsPath), '<Version>([^<]+)</Version>')
    if ($found.Count -ne 1) {
        Fail "Expected exactly one <Version> element in Directory.Build.props, found $($found.Count)."
    }
    $current = $found[0].Groups[1].Value.Trim()
    if ($current -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        Fail "Cannot use '$current' - expected a numeric version like 0.11.0 or 0.11.0.1."
    }
    return $current
}

# Major.Minor.Build from the props, and the commit count as the fourth part. Windows keeps each part
# of a file version in 16 bits, so a count above 65535 cannot be stamped.
function Get-StableVersion {
    param([string]$Current, [int]$CommitCount)
    if ($CommitCount -gt 65535) {
        Fail "develop has $CommitCount commits, more than the 65535 a Windows file version can hold." @(
            "Raise Major.Minor.Build in Directory.Build.props through a PR, and change Get-StableVersion to count from that commit."
        )
    }
    $n = @($Current -split '\.' | ForEach-Object { [int]$_ })
    return '{0}.{1}.{2}.{3}' -f $n[0], $n[1], $n[2], $CommitCount
}

# The number of commits HEAD has, or a refusal. A shallow clone counts only what it fetched, which
# would hand out a number some earlier build already had.
function Get-CommitCount {
    $shallow = "$(Invoke-Git @('rev-parse', '--is-shallow-repository') -Quiet)".Trim()
    if ($LASTEXITCODE -ne 0) { Fail "git could not tell whether this clone is shallow." }
    if ($shallow -eq 'true') {
        Fail "This clone is shallow, so its commit count is not the real one." @(
            "Fetch the whole history, then run this again:  git fetch --unshallow"
        )
    }
    $count = "$(Invoke-Git @('rev-list', '--count', 'HEAD') -Quiet)".Trim()
    if ($LASTEXITCODE -ne 0 -or $count -notmatch '^\d+$') {
        Fail "git could not count the commits of HEAD."
    }
    return [int]$count
}

# The version a one-off -InstallDir build is stamped with, so the title bar or a log line can never
# pass it off as the stable build of the same number. The 'g' in front of the hash is load-bearing:
# a short hash can be all digits with a leading zero (0123456, about 1 commit in 270), which is not a
# valid SemVer prerelease identifier, and NuGet's restore then fails with nothing but MSB4181.
function Get-OneOffVersion {
    param([string]$Current, [string]$Commit)
    if (-not $Commit) { return "$Current-oneoff.nogit" }
    return "$Current-oneoff.g$Commit"
}

# Everything that has to be true before a build may go into the stable root. This refuses rather
# than asks: a yes/no here would put a "stable" build of unmerged code on disk, under a number a
# later develop commit is going to have.
function Assert-ReleaseReady {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Fail "git is not on your PATH." @("The stable build takes its version from the commits on $ReleaseBranch, so it needs git.")
    }

    $branch = "$(Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD') -Quiet)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not $branch) {
        Fail "$RepoRoot is not a git checkout."
    }
    if ($branch -ne $ReleaseBranch) {
        Fail "You are on branch '$branch', not $ReleaseBranch." @(
            "Stable builds are only made from $ReleaseBranch, so the stable folder only ever holds merged code.",
            "Merge the branch through its PR, then:  git checkout $ReleaseBranch; git pull",
            "To try this branch's build, give it a folder of its own outside $($DefaultInstallRoot):",
            "  .\Scripts\build.ps1 -InstallDir 'E:\Builds\$AppName-test'"
        )
    }

    # Untracked files count too: the SDK compiles every *.cs it finds whether git knows it or
    # not, so a new file that was never added would go into a build stamped with a commit that
    # does not contain it. bin\ and obj\ are ignored, so they never show up here.
    # The exit code decides, not the output: a git that fails here prints nothing to stdout, and
    # nothing reads as "no changes".
    $dirty = @(Invoke-Git @('status', '--porcelain') -Quiet)
    if ($LASTEXITCODE -ne 0) {
        Fail "git status failed (exit code $LASTEXITCODE), so the working tree could not be checked." @(
            "Run git status yourself to see why - a damaged index or a safe.directory refusal are the usual causes."
        )
    }
    if ($dirty.Count -gt 0) {
        Fail "The working tree has uncommitted or untracked files." @(
            @("A stable build has to match a commit on $ReleaseBranch. Commit, stash, ignore or delete these first:") +
            @($dirty | Select-Object -First 8 | ForEach-Object { "  $_" })
        )
    }

    Write-Note "fetching origin/$ReleaseBranch"
    Invoke-Git @('fetch', '--quiet', 'origin', $ReleaseBranch)
    if ($LASTEXITCODE -ne 0) {
        Fail "git fetch failed." @("Check your network and GitHub login, then run this script again.")
    }

    # Both directions. Behind means the build would not be of what is on origin; ahead means it
    # would be of commits that never went through a PR, and the number it takes from the count
    # would later belong to a different commit on origin.
    $counts = "$(Invoke-Git @('rev-list', '--left-right', '--count', "HEAD...origin/$ReleaseBranch") -Quiet)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not ($counts -match '^(\d+)\s+(\d+)$')) {
        Fail "Could not compare $ReleaseBranch with origin/$ReleaseBranch." @("Does the remote 'origin' have a '$ReleaseBranch' branch?")
    }
    $ahead  = [int]$Matches[1]
    $behind = [int]$Matches[2]
    if ($ahead -gt 0) {
        Fail "$ReleaseBranch is $ahead commit(s) ahead of origin/$ReleaseBranch." @(
            "A stable build is of code that is already on origin. $ReleaseBranch takes commits only through PRs.",
            "Move them to a branch and open a PR:  git branch feature/<name>; git reset --hard origin/$ReleaseBranch"
        )
    }
    if ($behind -gt 0) {
        Fail "$ReleaseBranch is $behind commit(s) behind origin/$ReleaseBranch." @("git pull, then run this script again.")
    }
    Write-Ok "on $ReleaseBranch, clean, level with origin"
}
