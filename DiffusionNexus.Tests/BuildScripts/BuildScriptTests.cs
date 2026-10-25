using System.Diagnostics;
using System.Text.RegularExpressions;
using DiffusionNexus.Tests.Helpers;
using FluentAssertions;

namespace DiffusionNexus.Tests.BuildScripts;

/// <summary>The helpers in Scripts\_common.ps1, dot-sourced the way build.ps1 does it.</summary>
public sealed class BuildScriptHelperTests : BuildScriptTestBase
{
    [Theory]
    [InlineData("0.11.0.1", 2483, "0.11.0.2483")]
    [InlineData("0.11.0", 7, "0.11.0.7")]
    [InlineData("1.2.3.4", 65535, "1.2.3.65535")]
    public void TheStableVersion_IsThePropsBuildWithTheCommitCount(string current, int count, string expected)
    {
        var result = Pwsh.RunCommon($"Get-StableVersion '{current}' {count}");

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Trim().Should().Be(expected);
    }

    [Fact]
    public void ACommitCountAWindowsFileVersionCannotHold_IsRefused()
    {
        // Each part of a Windows file version is 16 bits. The build would otherwise fail deep inside
        // the compiler with nothing to say why.
        var result = Pwsh.RunCommon("Get-StableVersion '0.11.0.1' 65536");

        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("65535");
    }

    [Theory]
    [InlineData("'feat(ui): add a tab', 'fix: a crash'", "0.12.0", "0.13.0 True")]
    [InlineData("'feat!: drop the old setting'", "0.12.0", "0.13.0 True")]
    [InlineData("'fix: a crash', 'docs: readme', 'chore(deps): bump'", "0.12.0", "0.12.1 True")]
    [InlineData("'fix: the feat: button'", "0.12.0", "0.12.1 True")]
    [InlineData("'fix: a crash'", "1.0.0", "1.0.0 True")]
    [InlineData("'feat: a tab'", "0.13.0", "0.13.0 True")]
    [InlineData("", "0.12.0", "0.12.0 False")]
    public void TheNextRelease_RaisesMinorForAFeatureAndBuildOtherwise_NeverBelowTheProps(string subjects, string floor, string expected)
    {
        // The last release was v0.12.0. A feat: anywhere since raises the middle number; anything else
        // the third. The props number is a floor, the one way to jump (to 1.0.0, say). Nothing new since
        // the tag is nothing to release, and the version is the tag's own.
        var result = Pwsh.RunCommon(
            $"$r = Get-NextReleaseVersion -LastTag 'v0.12.0' -Subjects @({subjects}) -Floor '{floor}'; \"$($r.Version) $($r.New)\"");

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Trim().Should().Be(expected);
    }

    [Fact]
    public void WithNoReleaseTagYet_TheFirstReleaseIsTheProps()
    {
        var result = Pwsh.RunCommon(
            "$r = Get-NextReleaseVersion -LastTag '' -Subjects @('feat: a tab') -Floor '0.12.0'; \"$($r.Version) $($r.New)\"");

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Trim().Should().Be("0.12.0 True");
    }

    [Fact]
    public void TheLastRelease_IsTheHighestPlainVersionTag_NotTheNewestOrTheFirstListed()
    {
        // git lists tags by name, so v0.9.12 sorts after v0.12.0 as text. A tag that is not a plain
        // vX.Y.Z (a pre-release, someone's marker) is no release.
        var git =
            "function Invoke-Git { param([string[]]$Arguments, [switch]$Quiet) $global:LASTEXITCODE = 0; " +
            "switch ($Arguments[0]) { " +
            "'tag' { 'v0.12.0'; 'v0.9.12'; 'v1.0.0-rc.1'; 'nightly'; 'v0.11.0' } " +
            "'log' { Add-Content -LiteralPath $env:TEMP_LOG -Value ($Arguments -join ' '); 'fix: a crash' } } }";
        using var temp = new TempFolder();
        var log = Path.Combine(temp.Path, "git.log");

        var result = Pwsh.RunCommon(
            $"$env:TEMP_LOG = '{log}'; {git}; $r = Get-ReleaseVersion; \"$($r.LastTag) $($r.Version) $($r.New)\"");

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Trim().Should().Be("v0.12.0 0.12.1 True");
        File.ReadAllText(log).Should().Contain("v0.12.0..HEAD").And.Contain("--no-merges");
    }

    [Fact]
    public void AGitLogThatFails_StopsTheRelease()
    {
        // A shallow clone without the tag's commit makes git log exit non-zero with nothing on stdout,
        // which would otherwise read as "nothing new".
        var git =
            "function Invoke-Git { param([string[]]$Arguments, [switch]$Quiet) $global:LASTEXITCODE = 0; " +
            "switch ($Arguments[0]) { 'tag' { 'v0.12.0' } 'log' { $global:LASTEXITCODE = 128 } } }";

        var result = Pwsh.RunCommon($"{git}; Get-ReleaseVersion");

        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("v0.12.0");
    }

    [Theory]
    [InlineData("abc1234 p1 p2", 0)]
    [InlineData("abc1234 p1", 1)]
    public void AReleaseOfASquashOrRebaseMerge_IsRefused(string parents, int expectedExitCode)
    {
        // A squash or rebase merge into main has one parent. Its subject is the PR title, so the feat:
        // commits are lost, and develop never contains the commit the tag would go on: every later
        // count would start from the merge before it.
        var git =
            "function Invoke-Git { param([string[]]$Arguments, [switch]$Quiet) $global:LASTEXITCODE = 0; " +
            $"if ($Arguments[0] -eq 'rev-list' -and $Arguments -contains '--parents') {{ '{parents}' }} }}";

        var result = Pwsh.RunCommon($"{git}; Assert-ReleaseMerge");

        result.ExitCode.Should().Be(expectedExitCode, result.Output);
        if (expectedExitCode != 0)
        {
            result.Output.Should().Contain("merge commit");
        }
    }

    [Theory]
    [InlineData(@"root\DiffusionNexus-0.11.0.9", true)]
    [InlineData(@"root", false)]
    [InlineData(@"rootOld\DiffusionNexus", false)]
    [InlineData(@"builds\DiffusionNexus", false)]
    public void APathIsUnderARoot_OnlyBelowAFolderBoundary(string path, bool under)
    {
        // In a folder of the test's own: the check opens the folders it is asked about, and the real
        // E:\StableVersion is not this test's to open.
        using var temp = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        Directory.CreateDirectory(Path.Combine(temp.Path, "rootOld"));

        var result = Pwsh.RunCommon($"Test-PathUnder '{Path.Combine(temp.Path, path)}' '{root}'");

        result.Output.Trim().Should().BeEquivalentTo(under.ToString());
    }

    [Theory]
    [InlineData(@"alias\DiffusionNexus-0.11.0.9", true)]
    [InlineData(@"alias", true)]
    [InlineData(@"root\DiffusionNexus-0.11.0.9", true)]
    [InlineData(@"rootOld\DiffusionNexus-0.11.0.9", false)]
    public void APathThroughAnAliasOfTheRoot_IsStillUnderTheRoot(string path, bool under)
    {
        // E:\STABLE~1\..., a junction or a subst drive leads into E:\StableVersion without matching
        // it as a string. The folder asked about does not exist yet - a new one-off build's target.
        using var temp = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;

        var result = Pwsh.RunCommon(
            $"New-Item -ItemType Junction -Path '{Path.Combine(temp.Path, "alias")}' -Target '{root}' | Out-Null; " +
            $"Test-PathUnder '{Path.Combine(temp.Path, path)}' '{root}' -OrEqual");

        result.Output.Trim().Should().BeEquivalentTo(under.ToString());
    }

    [Fact]
    public void OnlyAFolderWithOurMarker_IsOurs()
    {
        using var temp = new TempFolder();
        var ours = Directory.CreateDirectory(Path.Combine(temp.Path, "ours")).FullName;
        var foreign = Directory.CreateDirectory(Path.Combine(temp.Path, "foreign")).FullName;
        var unmarked = Directory.CreateDirectory(Path.Combine(temp.Path, "unmarked")).FullName;
        File.WriteAllText(Path.Combine(foreign, MarkerName), """{ "app": "RawCutX" }""");
        File.WriteAllText(Path.Combine(unmarked, "keep.txt"), "x");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{ours}' '0.11.0.9'; " +
            $"(Test-OurInstall '{ours}'), (Test-OurInstall '{foreign}'), (Test-OurInstall '{unmarked}') -join ','");

        result.Output.Trim().Should().Be("True,False,False");
    }

    [Theory]
    [InlineData("DiffusionNexus-0.11.0.2483", true)]
    [InlineData("DiffusionNexus-installer-2.0", false)]
    [InlineData("DiffusionNexus-data", false)]
    [InlineData("DiffusionNexus", false)]
    [InlineData("DiffusionNexus_old-0.11.0.1", false)]
    public void OnlyTheAppNameADashAndADigit_IsAVersionFolder(string name, bool isVersion)
    {
        // E:\StableVersion already holds DiffusionNexus-installer-2.0, and the data folder sits beside
        // the versions. Neither is a build whose app could be "the stable build" or lose a link.
        var result = Pwsh.RunCommon($"Test-VersionFolderName '{name}'");

        result.Output.Trim().Should().BeEquivalentTo(isVersion.ToString());
    }

    [Theory]
    [InlineData("0123456")]
    [InlineData("1234567")]
    [InlineData("f31a120")]
    [InlineData("")]
    public void AOneOffStamp_IsNeverANumericPrereleaseIdentifier(string commit)
    {
        // NuGet's restore rejects a SemVer prerelease identifier that is all digits with a leading
        // zero: -p:Version=0.11.0.1-oneoff.0123456 fails with MSB4181, about 1 commit in 270. The
        // rule itself is checked, not the spelling: every dot-separated part after the dash is a
        // number without a leading zero, or has a letter or a dash in it.
        var result = Pwsh.RunCommon($"Get-OneOffVersion '0.11.0.1' '{commit}'");

        var stamp = result.Output.Trim();
        stamp.Should().StartWith("0.11.0.1-oneoff.").And.EndWith(commit);
        stamp[(stamp.IndexOf('-') + 1)..].Split('.').Should().AllSatisfy(
            identifier => identifier.Should().MatchRegex(@"^(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)$"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(128, 1)]
    public void AFailingGitStatus_IsNotACleanTree(int statusExitCode, int expectedExitCode)
    {
        // A corrupt index or a safe.directory refusal makes git status print nothing to stdout and
        // exit non-zero. Empty output on its own reads as "no changes", so the exit code has to be
        // what decides.
        var result = Pwsh.RunCommon($"{FakeGit(status: $"$global:LASTEXITCODE = {statusExitCode}")}; Assert-ReleaseReady");

        result.ExitCode.Should().Be(expectedExitCode, result.Output);
        if (expectedExitCode != 0)
        {
            result.Output.Should().Contain("git status failed");
        }
    }

    [Theory]
    [InlineData("1`t0", "ahead of origin")]
    [InlineData("0`t3", "behind origin")]
    [InlineData("2`t3", "ahead of origin")]
    public void ADevelopThatIsNotLevelWithOrigin_IsRefused(string leftRight, string expected)
    {
        // Ahead: the build would be of commits no PR has seen, and its number - the commit count -
        // would later belong to a different commit on origin. Behind: not what is on origin.
        var result = Pwsh.RunCommon($"{FakeGit(leftRight: leftRight)}; Assert-ReleaseReady");

        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain(expected);
    }

    [Fact]
    public void AStableBuildFromAnotherBranch_IsRefused()
    {
        var result = Pwsh.RunCommon($"{FakeGit(branch: "feature/x")}; Assert-ReleaseReady");

        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("not develop");
    }

    [Fact]
    public void AShallowClone_IsRefused()
    {
        // A shallow clone counts only what it fetched, so it would hand out a number an earlier build had.
        var result = Pwsh.RunCommon($"{FakeGit(shallow: "true")}; Get-CommitCount");

        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("git fetch --unshallow");
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void AFinishedStableFolder_IsNeverRepublishedOver(bool complete, int expectedExitCode)
    {
        // The same number is the same develop commit, built before. Its folder may be the one the
        // Start Menu opens, possibly running. A folder an interrupted publish left half-done was
        // never made current, so it may go.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "DiffusionNexus-0.11.0.9");
        var marker = complete ? "-Complete" : "";

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '0.11.0.9' {marker}; Assert-StableTargetFree '{folder}' '0.11.0.9'");

        result.ExitCode.Should().Be(expectedExitCode, result.Output);
        if (complete)
        {
            result.Output.Should().Contain("already published");
        }
    }

    [Fact]
    public void TheFolderTheCurrentLinkPointsAt_IsNeverRepublishedOver_WhateverItsMarkerSays()
    {
        // The link moves first and the marker is written second. A run that dies between the two
        // leaves the live folder without its finished mark; the link itself still says it is live.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "DiffusionNexus-0.11.0.9");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '0.11.0.9'; " +
            $"New-Item -ItemType Junction -Path '{Path.Combine(temp.Path, "DiffusionNexus")}' -Target '{folder}' | Out-Null; " +
            $"Assert-StableTargetFree '{folder}' '0.11.0.9'");

        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("already published");
    }

    [Theory]
    [InlineData("true", 1)]
    [InlineData("false", 0)]
    [InlineData("\"false\"", 0)]
    public void OnlyARealTrue_MarksAFolderFinished(string completeJson, int expectedExitCode)
    {
        // A marker edited by hand to "complete": "false" is a non-empty string, which a plain
        // [bool] cast reads as true.
        using var temp = new TempFolder();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "DiffusionNexus-0.11.0.9")).FullName;
        File.WriteAllText(
            Path.Combine(folder, MarkerName),
            $$"""{ "app": "DiffusionNexus", "version": "0.11.0.9", "complete": {{completeJson}} }""");

        var result = Pwsh.RunCommon($"Assert-StableTargetFree '{folder}' '0.11.0.9'");

        result.ExitCode.Should().Be(expectedExitCode, result.Output);
    }

    [Theory]
    [InlineData("0.11.0.9", false)]
    [InlineData("0.11.0.1-oneoff.gf31a120", true)]
    public void AOneOffBuild_OnlyReusesAOneOffFolder(string markedVersion, bool reusable)
    {
        // A second line behind the path check: a stable release is never a one-off's folder.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "DiffusionNexus-test");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '{markedVersion}' -Complete; Test-OurInstall '{folder}' -OneOff");

        result.Output.Trim().Should().BeEquivalentTo(reusable.ToString());
    }

    [Theory]
    [InlineData(@"\\localhost\")]
    [InlineData(@"\\127.0.0.1\")]
    [InlineData(@"\\?\UNC\localhost\")]
    public void APathThroughAShareToThisPc_IsStillUnderTheRoot(string server)
    {
        // \\localhost\E$\StableVersion\... is E:\StableVersion\..., but Windows keeps a share path a
        // share path, so no spelling of it ever starts with the root. Which folder it is decides.
        using var temp = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        Directory.CreateDirectory(Path.Combine(temp.Path, "rootOld"));
        var share = server + temp.Path[0] + "$" + temp.Path[2..];
        if (!Directory.Exists(share))
        {
            return; // No admin share to this drive for this user: nothing to ask.
        }

        var result = Pwsh.RunCommon(
            $"Test-PathUnder '{share}\\root\\DiffusionNexus-0.11.0.9' '{root}' -OrEqual; " +
            $"Test-PathUnder '{share}\\root' '{root}' -OrEqual; " +
            $"Test-PathUnder '{share}\\root' '{root}'; " +
            $"Test-PathUnder '{share}\\rootOld\\DiffusionNexus-0.11.0.9' '{root}' -OrEqual");

        Lines(result.Output).Should().Equal("True", "True", "False", "False");
    }

    [Fact]
    public void TheFolderTheCurrentLinkPointsAt_IsCurrent_HoweverTheLinkSpellsIt()
    {
        // A link remade by hand through another spelling of the root still points at the same
        // folder. "alias" stands in for E:\STABLE~1 or a subst drive.
        using var temp = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(root, "DiffusionNexus-0.11.0.9")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(root, "DiffusionNexus-0.11.0.8")).FullName;
        var alias = Path.Combine(temp.Path, "alias");

        var result = Pwsh.RunCommon(
            $"New-Item -ItemType Junction -Path '{alias}' -Target '{root}' | Out-Null; " +
            $"New-Item -ItemType Junction -Path '{Path.Combine(root, "DiffusionNexus")}' -Target '{alias}\\DiffusionNexus-0.11.0.9' | Out-Null; " +
            $"Test-InstallCurrent '{folder}'; Test-InstallCurrent '{other}'");

        Lines(result.Output).Should().Equal("True", "False");
    }

    [Fact]
    public void AFolderId_CarriesTheRealVolumeSerial()
    {
        // BY_HANDLE_FILE_INFORMATION packs its FILETIMEs on 4 bytes. Declared as 8-aligned longs,
        // every field after them moved, the serial read as 0 and C:\ and E:\ got the same ID - so
        // folders on two drives could pass for one.
        using var temp = new TempFolder();
        var drive = temp.Path[..2];

        var result = Pwsh.RunCommon(
            $"(Get-FolderId '{temp.Path}').Split(':')[0]; " +
            $"(Get-CimInstance Win32_LogicalDisk -Filter \"DeviceID='{drive}'\").VolumeSerialNumber");

        var lines = Lines(result.Output);
        lines.Should().HaveCount(2, result.Output);
        lines[0].Should().BeEquivalentTo(lines[1]);
    }

    [Fact]
    public void ACurrentLinkThatCannotBeRemade_KeepsPointingAtTheOldVersion()
    {
        // The old link is deleted before the new one is made. When the new one fails - a virus
        // scanner, access denied - the Start Menu entry and every taskbar pin would lead nowhere.
        using var temp = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var old = Directory.CreateDirectory(Path.Combine(root, "DiffusionNexus-0.11.0.8")).FullName;
        var link = Path.Combine(root, "DiffusionNexus");

        var result = Pwsh.RunCommon(
            $"New-Item -ItemType Junction -Path '{link}' -Target '{old}' | Out-Null; " +
            $"Set-CurrentLink '{root}' '{Path.Combine(root, "no|such|folder")}'");

        result.ExitCode.Should().Be(1, result.Output);
        result.Output.Should().Contain("still opens the previous version");
        new DirectoryInfo(link).LinkTarget.Should().Be(old);
    }

    [Fact]
    public void ARunningApp_IsFoundByItsRealFolder_HoweverItWasStarted()
    {
        // The real lookup, with stand-ins that keep running: copies of ping.exe under the app's name.
        // One is started through the current link, the way the Start Menu starts DiffusionNexus, and
        // Windows then reports the link's path - not the version folder's - as where it runs from.
        // The other runs from DiffusionNexus-installer-2.0, a neighbour that is no version of ours.
        using var temp = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(root, "DiffusionNexus-0.11.0.9")).FullName;
        var neighbour = Directory.CreateDirectory(Path.Combine(root, "DiffusionNexus-0.11.0.8")).FullName;
        var installer = Directory.CreateDirectory(Path.Combine(root, "DiffusionNexus-installer-2.0")).FullName;
        var link = Path.Combine(root, "DiffusionNexus");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), Path.Combine(folder, ExeName));
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), Path.Combine(installer, ExeName));
        Pwsh.Run($"New-Item -ItemType Junction -Path '{link}' -Target '{folder}' | Out-Null").ExitCode.Should().Be(0);

        using var app = StartStandIn(Path.Combine(link, ExeName));
        using var stranger = StartStandIn(Path.Combine(installer, ExeName));
        try
        {
            var result = Pwsh.RunCommon(
                $"(Get-AppProcess '{folder}').ProcessId; 'by folder'; " +
                $"(Get-AppProcess -AnyVersion -InstallRoot '{root}').ProcessId; 'by root'; " +
                $"(Get-AppProcess '{neighbour}').ProcessId; 'neighbour'; " +
                $"(Get-AppProcess -AnyVersion -InstallRoot '{temp.Path}').ProcessId; 'other root'");

            Lines(result.Output).Should().Equal(
                app.Id.ToString(), "by folder", app.Id.ToString(), "by root", "neighbour", "other root");
        }
        finally
        {
            app.Kill();
            stranger.Kill();
            app.WaitForExit();
            stranger.WaitForExit();
        }
    }

    [Fact]
    public void ACleanThatStopsHalfWay_LeavesTheMarker_AlsoThroughAShortPath()
    {
        // C:\Users\LITTLE~1\... is what %TEMP% looks like for a user name with a space in it. The
        // folder listing writes that name out long, so a marker looked for by its full path is never
        // found, and goes with everything else.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "a long folder name", "DiffusionNexus-0.11.0.9");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '0.11.0.9'; " +
            $"Set-Content '{folder}\\free.dll' x; Set-Content '{folder}\\zz-held.dll' x; " +
            $"$short = (New-Object -ComObject Scripting.FileSystemObject).GetFolder('{folder}').ShortPath; " +
            $"if ($short -ieq '{folder}') {{ 'no short names here'; return }}; " +
            $"$held = [IO.File]::Open('{folder}\\zz-held.dll', 'Open', 'Read', 'None'); " +
            $"try {{ Remove-InstallFolder $short; 'cleaned' }} catch {{ 'stopped' }} finally {{ $held.Dispose() }}");

        if (result.Output.Trim() == "no short names here")
        {
            return; // The volume keeps no 8.3 names, so there is no short spelling to pass.
        }

        result.Output.Trim().Should().Be("stopped");
        File.Exists(Path.Combine(folder, MarkerName)).Should().BeTrue();
        File.Exists(Path.Combine(folder, "free.dll")).Should().BeFalse();
    }

    [Fact]
    public void ACleanThatStopsHalfWay_LeavesTheMarker()
    {
        // The marker is what lets the next run clean the folder. A plain recursive delete can take it
        // before it stops at a file somebody holds open - leaving a folder every later run refuses to
        // publish into and refuses to clean.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "DiffusionNexus-0.11.0.9");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '0.11.0.9'; " +
            $"Set-Content '{folder}\\free.dll' x; Set-Content '{folder}\\zz-held.dll' x; " +
            $"$held = [IO.File]::Open('{folder}\\zz-held.dll', 'Open', 'Read', 'None'); " +
            $"try {{ Remove-InstallFolder '{folder}'; 'cleaned' }} catch {{ 'stopped' }} finally {{ $held.Dispose() }}");

        result.Output.Trim().Should().Be("stopped");
        File.Exists(Path.Combine(folder, MarkerName)).Should().BeTrue();
        File.Exists(Path.Combine(folder, "free.dll")).Should().BeFalse();
    }

    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    public void ACleanUnlinksTheOutputsJunction_AndNeverEntersIt(string shell)
    {
        // The outputs folder of every version is a junction to the user's generated images. A
        // recursive delete that follows it takes them with the old version. build.cmd falls back to
        // Windows PowerShell 5.1, so both are asked.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "DiffusionNexus-0.11.0.9");
        var images = Directory.CreateDirectory(Path.Combine(temp.Path, "DiffusionNexus-data", "outputs")).FullName;
        var image = Path.Combine(images, "canvas-0001.png");
        File.WriteAllText(image, "an image");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '0.11.0.9'; Set-Content '{folder}\\app.dll' x; " +
            $"New-Item -ItemType Junction -Path '{folder}\\outputs' -Target '{images}' | Out-Null; " +
            $"Remove-InstallFolder '{folder}'",
            shell);

        result.ExitCode.Should().Be(0, result.Output);
        Directory.Exists(folder).Should().BeFalse();
        File.Exists(image).Should().BeTrue();
    }

    [Fact]
    public void TheStableBuild_HasNoRootOfItsOwnToPointElsewhere()
    {
        // A different -InstallRoot would still repoint the real Start Menu shortcut - a trap.
        // -InstallDir is the one way to build somewhere else.
        var buildScript = Path.Combine(Pwsh.ScriptsFolder, "build.ps1");

        var result = Pwsh.Run($"(Get-Command '{buildScript}').Parameters.Keys -join ','");

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Trim().Split(',').Should().Contain("InstallDir").And.NotContain("InstallRoot");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePayloadReport_WarnsAboutAMissingVideoRuntime(bool healthy)
    {
        // Without LibVLC the app still starts; only a video preview would say something is wrong.
        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.Path, ExeName), "x");
        if (healthy)
        {
            var vlc = Directory.CreateDirectory(Path.Combine(temp.Path, @"libvlc\win-x64")).FullName;
            File.WriteAllText(Path.Combine(vlc, "libvlc.dll"), "x");
        }

        var result = Pwsh.RunCommon($"Write-PayloadReport '{temp.Path}' *>&1 | Out-String");

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Contains("libvlc.dll is missing").Should().Be(!healthy);
    }
}

/// <summary>The real build.ps1, run in a <see cref="BuildScriptTestBase.FakeRepo"/>.</summary>
public sealed class BuildScriptRunTests : BuildScriptTestBase
{
    [Fact]
    public void AStableBuild_PublishesLinksTheDataAndMarksTheFolderFinished_AndLeavesTheRepoAlone()
    {
        using var repo = new FakeRepo();

        var result = repo.Build();

        result.ExitCode.Should().Be(0, result.Output);
        var folder = repo.VersionFolder("0.11.0.2483");
        FakeRepo.IsMarkedComplete(folder).Should().BeTrue();
        repo.CurrentLinkTarget.Should().Be(folder);
        new DirectoryInfo(Path.Combine(folder, "outputs")).LinkTarget.Should().Be(Path.Combine(repo.DataDir, "outputs"));
        File.Exists(Path.Combine(folder, "LICENSE")).Should().BeTrue();
        File.Exists(Path.Combine(folder, "THIRD-PARTY-NOTICES.txt")).Should().BeTrue();
        repo.Calls.Should().Contain("shortcut " + repo.CurrentLink);
        repo.Calls.Where(call => call.StartsWith("dotnet ")).Should().Equal(
            "dotnet build DiffusionNexus.sln -p:Version=0.11.0.2483",
            "dotnet test DiffusionNexus.sln",
            "dotnet publish DiffusionNexus.UI.csproj -p:Version=0.11.0.2483");
        repo.Calls.Should().NotContain(call => call.StartsWith("git commit") || call.StartsWith("git push"));
        repo.PropsText.Should().Be(FakeRepo.OriginalProps);
    }

    [Theory]
    [InlineData("feat: a tab|fix: a crash", "0.12.0.2483")]
    [InlineData("fix: a crash", "0.11.1.2483")]
    [InlineData("", "0.11.0.2483")]
    public void AStableBuild_CarriesTheVersionTheNextReleaseGets(string subjects, string expected)
    {
        // release.yml counts up from the last tag, so the stable build of develop does too: the number
        // in the title bar is the one this code will ship as. With nothing new since the tag, develop
        // is the release itself.
        using var repo = new FakeRepo();

        var result = repo.Build(before: $"$env:FAKE_TAGS = 'v0.11.0'; $env:FAKE_LOG = '{subjects}'");

        result.ExitCode.Should().Be(0, result.Output);
        repo.CurrentLinkTarget.Should().Be(repo.VersionFolder(expected));
        repo.PropsText.Should().Be(FakeRepo.OriginalProps);
    }

    [Fact]
    public void AStableBuild_FetchesTheReleaseTags()
    {
        // Release tags sit on main's merge commits, which develop never contains, so fetching develop
        // alone never brings them: the build would count from an old tag and stamp a number that has
        // already shipped.
        using var repo = new FakeRepo();

        var result = repo.Build();

        result.ExitCode.Should().Be(0, result.Output);
        repo.Calls.Should().Contain(call => call.StartsWith("git fetch") && call.Contains("--tags") && call.EndsWith("origin develop"));
    }

    [Fact]
    public void ASecondRunOfTheSameCommit_LeavesTheInstalledVersionAlone()
    {
        // develop has not moved, so the count - and the folder - are the same as last time. That
        // folder is the one the Start Menu opens.
        using var repo = new FakeRepo();
        repo.Build().ExitCode.Should().Be(0);
        var folder = repo.VersionFolder("0.11.0.2483");

        var result = repo.Build();

        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("0.11.0.2483 is already published");
        File.Exists(Path.Combine(folder, ExeName)).Should().BeTrue();
        repo.Calls.Count(call => call.StartsWith("dotnet build")).Should().Be(1);
    }

    [Fact]
    public void ANewCommit_GetsANewFolder_AndTheOldOneStillSavesIntoTheSharedOutputs()
    {
        // The old folder stays for a rollback. Started from there by hand, Canvas saves next to its
        // exe - which has to stay the shared folder, or the rollback's images end up in a folder the
        // next clean-up deletes.
        using var repo = new FakeRepo();
        repo.Build(before: "$env:FAKE_COMMIT_COUNT = '100'").ExitCode.Should().Be(0);
        var old = repo.VersionFolder("0.11.0.100");

        var result = repo.Build(before: "$env:FAKE_COMMIT_COUNT = '101'");

        result.ExitCode.Should().Be(0, result.Output);
        repo.CurrentLinkTarget.Should().Be(repo.VersionFolder("0.11.0.101"));
        File.Exists(Path.Combine(old, ExeName)).Should().BeTrue();
        File.WriteAllText(Path.Combine(old, "outputs", "rollback-0001.png"), "an image");
        File.ReadAllText(Path.Combine(repo.DataDir, "outputs", "rollback-0001.png")).Should().Be("an image");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnAppStartedThroughTheCurrentLink_IsToldItNowLoadsTheNewVersionsFiles(bool throughTheLink)
    {
        // The Start Menu starts the exe through E:\StableVersion\DiffusionNexus. Once that link points
        // at the new folder, every file the running app opens beside its exe later - LibVLC, the LGPL
        // assemblies, workflows - comes from the new version. "Still running the old version" is not
        // what it does, so the build has to say what it does.
        using var repo = new FakeRepo();
        repo.Build(before: "$env:FAKE_COMMIT_COUNT = '100'").ExitCode.Should().Be(0);
        var exe = Path.Combine(throughTheLink ? repo.CurrentLink : repo.VersionFolder("0.11.0.100"), ExeName);

        var result = repo.Build(before: $"$env:FAKE_COMMIT_COUNT = '101'; $env:FAKE_RUNNING_FROM = '{exe}'");

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Contains("was started through").Should().Be(throughTheLink, result.Output);
        result.Output.Contains("is still running from").Should().Be(!throughTheLink, result.Output);
    }

    [Fact]
    public void AFailedPublish_DoesNotClaimTheStableRootIsUntouched()
    {
        // The folder was claimed before the publish, so a publish that stops half-way leaves it behind.
        using var repo = new FakeRepo();

        var result = repo.Build(before: "$env:FAKE_PUBLISH_FAILS = '1'");

        result.ExitCode.Should().Be(1);
        result.Output.Should().NotContain("The stable root is as it was");
        result.Output.Should().Contain("the next run of this commit cleans it");
        repo.CurrentLinkTarget.Should().BeNull();
    }

    [Fact]
    public void TheMappingsOfTheCurrentVersion_AreSharedWithTheNextOne()
    {
        // mappings.xml is written next to the exe. The new version starts with what the current one
        // has, and from then on both are one file: a save in either is seen by the other.
        using var repo = new FakeRepo();
        repo.Build(before: "$env:FAKE_COMMIT_COUNT = '100'").ExitCode.Should().Be(0);
        var old = repo.VersionFolder("0.11.0.100");
        File.WriteAllText(Path.Combine(old, "mappings.xml"), "<saved by the old version />");

        var result = repo.Build(before: "$env:FAKE_COMMIT_COUNT = '101'");

        result.ExitCode.Should().Be(0, result.Output);
        var mappings = Path.Combine(repo.VersionFolder("0.11.0.101"), "mappings.xml");
        File.ReadAllText(mappings).Should().Be("<saved by the old version />");
        // The way CustomTagMapXmlService saves: a StreamWriter over the path, which truncates in place.
        using (var writer = new StreamWriter(mappings))
        {
            writer.Write("<saved by the new version />");
        }

        File.ReadAllText(Path.Combine(repo.DataDir, "mappings.xml")).Should().Be("<saved by the new version />");
    }

    [Fact]
    public void MappingsDeletedInTheCurrentVersion_StayDeleted()
    {
        // "Delete all mappings" deletes the file next to the exe, and so its link to the shared one.
        // The shared file still has the old content, which must not come back.
        using var repo = new FakeRepo();
        repo.Build(before: "$env:FAKE_COMMIT_COUNT = '100'").ExitCode.Should().Be(0);
        File.WriteAllText(Path.Combine(repo.VersionFolder("0.11.0.100"), "mappings.xml"), "<old />");
        repo.Build(before: "$env:FAKE_COMMIT_COUNT = '101'").ExitCode.Should().Be(0);
        File.Delete(Path.Combine(repo.VersionFolder("0.11.0.101"), "mappings.xml"));

        var result = repo.Build(before: "$env:FAKE_COMMIT_COUNT = '102'");

        result.ExitCode.Should().Be(0, result.Output);
        File.Exists(Path.Combine(repo.VersionFolder("0.11.0.102"), "mappings.xml")).Should().BeFalse();
    }

    [Fact]
    public void WithNoCurrentVersionToAsk_TheSharedMappingsAreStillCarriedOn()
    {
        // The current link is gone (a remake that failed twice) or its folder was deleted by hand.
        // "No current version" is not "the current version has no mappings": the shared file is all
        // there is, and dropping it would lose the mappings for every later version.
        using var repo = new FakeRepo();
        repo.Build(before: "$env:FAKE_COMMIT_COUNT = '100'").ExitCode.Should().Be(0);
        File.WriteAllText(Path.Combine(repo.VersionFolder("0.11.0.100"), "mappings.xml"), "<kept />");
        repo.Build(before: "$env:FAKE_COMMIT_COUNT = '101'").ExitCode.Should().Be(0);
        Directory.Delete(repo.CurrentLink);

        var result = repo.Build(before: "$env:FAKE_COMMIT_COUNT = '102'");

        result.ExitCode.Should().Be(0, result.Output);
        File.ReadAllText(Path.Combine(repo.VersionFolder("0.11.0.102"), "mappings.xml")).Should().Be("<kept />");
    }

    [Fact]
    public void AOneOffBuild_GetsACopyOfTheMappings_AndNeverWritesTheStableOnes()
    {
        // A one-off is unmerged code. Through a hard link, its saves - a changed format, a save bug -
        // would land in the file every stable version reads.
        using var repo = new FakeRepo();
        repo.Build(before: "$env:FAKE_COMMIT_COUNT = '100'").ExitCode.Should().Be(0);
        var stable = Path.Combine(repo.VersionFolder("0.11.0.100"), "mappings.xml");
        File.WriteAllText(stable, "<stable />");
        var folder = Path.Combine(repo.Outside, "DiffusionNexus-test");

        var result = repo.Build($"-InstallDir '{folder}'");

        result.ExitCode.Should().Be(0, result.Output);
        var oneOff = Path.Combine(folder, "mappings.xml");
        File.ReadAllText(oneOff).Should().Be("<stable />");
        File.WriteAllText(oneOff, "<one-off />");
        File.ReadAllText(stable).Should().Be("<stable />");
        File.Exists(Path.Combine(repo.DataDir, "mappings.xml")).Should().BeFalse();
    }

    [Fact]
    public void AnAppStartedThroughALinkThatDidNotMove_IsNotToldItLoadsTheNewFiles()
    {
        // A junction of the user's own to an old version - a rollback shortcut - still leads there.
        using var repo = new FakeRepo();
        repo.Build(before: "$env:FAKE_COMMIT_COUNT = '100'").ExitCode.Should().Be(0);
        var own = Path.Combine(repo.Outside, "rollback");
        Pwsh.Run($"New-Item -ItemType Junction -Path '{own}' -Target '{repo.VersionFolder("0.11.0.100")}' | Out-Null").ExitCode.Should().Be(0);

        var result = repo.Build(before: $"$env:FAKE_COMMIT_COUNT = '101'; $env:FAKE_RUNNING_FROM = '{Path.Combine(own, ExeName)}'");

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Should().NotContain("was started through").And.Contain("is still running from");
    }

    [Fact]
    public void ARealOutputsFolder_IsNeverMergedOrDeleted()
    {
        // It may hold images saved there before the link existed.
        using var temp = new TempFolder();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "DiffusionNexus-0.11.0.9")).FullName;
        var image = Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "outputs")).FullName, "canvas-0001.png");
        File.WriteAllText(image, "an image");

        var result = Pwsh.RunCommon($"Set-OutputsLink '{folder}' '{Path.Combine(temp.Path, "DiffusionNexus-data")}'");

        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("is a real folder, not a link to your shared outputs");
        File.Exists(image).Should().BeTrue();
    }


    [Fact]
    public void AFolderThatNeverBecameCurrent_IsRebuiltByTheNextRun()
    {
        // Published, but the link could not be made: the folder was never used. Marked finished at
        // that point, every later run of this commit would refuse it for good.
        using var repo = new FakeRepo();
        var folder = repo.VersionFolder("0.11.0.2483");
        var failed = repo.Build(before: "$env:FAKE_LINK_FAILS = '1'");
        failed.Output.Should().Contain("the junction could not be made");
        FakeRepo.IsMarkedComplete(folder).Should().BeFalse();

        var result = repo.Build();

        result.ExitCode.Should().Be(0, result.Output);
        FakeRepo.IsMarkedComplete(folder).Should().BeTrue();
    }

    [Fact]
    public void ABuildSomebodyStartedByHandBeforeItWasCurrent_IsNotCleanedUnderThem()
    {
        // The folder is not finished, so the guard would let the next run clean it - and the clean
        // would delete what it can and stop at the locked exe.
        using var repo = new FakeRepo();
        var folder = repo.VersionFolder("0.11.0.2483");
        var exe = Path.Combine(folder, ExeName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, MarkerName), """{ "app": "DiffusionNexus", "version": "0.11.0.2483", "complete": false }""");
        File.WriteAllText(exe, "x");

        var result = repo.Build(before: $"$env:FAKE_RUNNING_FROM = '{exe}'");

        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain($"DiffusionNexus is running from {folder}");
        File.Exists(exe).Should().BeTrue();
        repo.Calls.Should().NotContain(call => call.StartsWith("dotnet "));
    }

    [Fact]
    public void AStableBuildFromAFeatureBranch_StopsBeforeBuilding()
    {
        using var repo = new FakeRepo();

        var result = repo.Build(before: "$env:FAKE_BRANCH = 'feature/x'");

        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("not develop");
        repo.Calls.Should().NotContain(call => call.StartsWith("dotnet "));
        Directory.Exists(repo.VersionFolder("0.11.0.2483")).Should().BeFalse();
    }

    [Fact]
    public void AOneOffBuild_IsStampedAndLeavesTheStableRootAlone()
    {
        // AssemblyVersion and FileVersion follow $(Version) in Directory.Build.props and must be
        // numeric, so they keep the plain number while the shown version carries the stamp. With no
        // release tag yet, that number is the props floor.
        using var repo = new FakeRepo();
        var folder = Path.Combine(repo.Outside, "DiffusionNexus-test");

        var result = repo.Build($"-InstallDir '{folder}'", before: "$env:FAKE_BRANCH = 'feature/x'");

        result.ExitCode.Should().Be(0, result.Output);
        File.ReadAllText(Path.Combine(folder, MarkerName)).Should().Contain("\"version\": \"0.11.0.0-oneoff.gabc1234\"");
        FakeRepo.IsMarkedComplete(folder).Should().BeTrue();
        repo.Calls.Should().Contain(
            "dotnet publish DiffusionNexus.UI.csproj -p:Version=0.11.0.0-oneoff.gabc1234 -p:AssemblyVersion=0.11.0.0 -p:FileVersion=0.11.0.0");
        repo.Calls.Should().NotContain(call => call.StartsWith("shortcut"));
        repo.CurrentLinkTarget.Should().BeNull();
    }

    [Fact]
    public void AOneOffBuild_NeverLinksTheSharedOutputs()
    {
        // Started once, the app registers <its folder>\outputs in the shared settings. Through a link
        // into the stable images the Generation Gallery would list each of them twice, and unmerged
        // code would save into and clean up the stable images.
        using var repo = new FakeRepo();
        var folder = Path.Combine(repo.Outside, "DiffusionNexus-test");

        var result = repo.Build($"-InstallDir '{folder}'");

        result.ExitCode.Should().Be(0, result.Output);
        Directory.Exists(Path.Combine(folder, "outputs")).Should().BeFalse();
    }

    [Fact]
    public void AOneOffBuild_CarriesTheVersionTheNextReleaseGets()
    {
        // The props number is only a floor and falls behind with every release. A one-off stamped
        // from it would sit below every build that has shipped since.
        using var repo = new FakeRepo();
        var folder = Path.Combine(repo.Outside, "DiffusionNexus-test");

        var result = repo.Build(
            $"-InstallDir '{folder}'",
            before: "$env:FAKE_BRANCH = 'feature/x'; $env:FAKE_TAGS = 'v0.11.0'; $env:FAKE_LOG = 'feat: a tab'");

        result.ExitCode.Should().Be(0, result.Output);
        repo.Calls.Should().Contain(
            "dotnet publish DiffusionNexus.UI.csproj -p:Version=0.12.0.0-oneoff.gabc1234 -p:AssemblyVersion=0.12.0.0 -p:FileVersion=0.12.0.0");
    }

    [Fact]
    public void AOneOffBuildOfUncommittedChanges_SaysSoInItsVersion()
    {
        // Two one-offs of the same commit with different uncommitted edits are different code.
        using var repo = new FakeRepo();
        var folder = Path.Combine(repo.Outside, "DiffusionNexus-test");

        var result = repo.Build($"-InstallDir '{folder}'", before: "$env:FAKE_DIRTY = '1'");

        result.ExitCode.Should().Be(0, result.Output);
        File.ReadAllText(Path.Combine(folder, MarkerName)).Should().Contain("\"version\": \"0.11.0.0-oneoff.gabc1234.dirty\"");
    }

    [Fact]
    public void AOneOffFolderGivenWithATrailingBackslash_ReachesThePublishWithoutIt()
    {
        // Windows PowerShell 5.1 quotes a path with a space as "E:\My Builds\x\", and the backslash
        // then escapes the closing quote: dotnet reads the rest of the line as part of the path.
        using var repo = new FakeRepo();
        var folder = Path.Combine(repo.Outside, "My Builds", "DiffusionNexus-test");

        var result = repo.Build($"-InstallDir '{folder}\\'");

        result.ExitCode.Should().Be(0, result.Output);
        repo.Calls.Should().Contain("output " + folder);
    }

    [Fact]
    public void AOneOffBuild_ThroughAnAliasOfTheStableRoot_IsRefused()
    {
        // The folder does not exist yet, so there is no marker to read: only the real path can tell
        // that this one-off would land in the stable root, under a stable version's name.
        using var repo = new FakeRepo();
        var alias = Path.Combine(repo.Outside, "alias");

        var result = repo.Build(
            $"-InstallDir '{Path.Combine(alias, "DiffusionNexus-0.11.0.2483")}'",
            before: $"New-Item -ItemType Junction -Path '{alias}' -Target '{repo.Root}' | Out-Null");

        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("must be outside the stable root");
        Directory.Exists(repo.VersionFolder("0.11.0.2483")).Should().BeFalse();
    }

    [Fact]
    public void ABuildWhoseCleanStopsHalfWay_LeavesAFolderTheNextRunCanClean()
    {
        // build.ps1 itself has to delete the marker last, not only the helper it calls: one file in
        // the folder is held open, the clean stops there, and the next run must still own the folder.
        using var repo = new FakeRepo();
        var folder = Path.Combine(repo.Outside, "DiffusionNexus-test");
        repo.Build($"-InstallDir '{folder}'").ExitCode.Should().Be(0);
        var held = Path.Combine(folder, "zz-held.dll");
        File.WriteAllText(held, "x");

        PwshResult stopped;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            stopped = repo.Build($"-InstallDir '{folder}' -Clean");
        }

        stopped.ExitCode.Should().Be(1);
        stopped.Output.Should().Contain("Could not clean");
        File.Exists(Path.Combine(folder, MarkerName)).Should().BeTrue();
        File.Exists(Path.Combine(folder, ExeName)).Should().BeFalse();

        var result = repo.Build($"-InstallDir '{folder}' -Clean");

        result.ExitCode.Should().Be(0, result.Output);
        File.Exists(held).Should().BeFalse();
    }

    [Fact]
    public void AOneOffBuild_NeverCleansAStableFolder()
    {
        using var repo = new FakeRepo();
        var folder = Directory.CreateDirectory(Path.Combine(repo.Outside, "looks-like-a-test-folder")).FullName;
        File.WriteAllText(Path.Combine(folder, MarkerName), """{ "app": "DiffusionNexus", "version": "0.11.0.9", "complete": true }""");
        File.WriteAllText(Path.Combine(folder, ExeName), "x");

        var result = repo.Build($"-InstallDir '{folder}' -Clean");

        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("not a one-off DiffusionNexus build");
        File.Exists(Path.Combine(folder, ExeName)).Should().BeTrue();
    }
}

/// <summary>build.cmd in a real cmd, and the helper that tells it whether Explorer started it.</summary>
public sealed class BuildCmdTests : BuildScriptTestBase
{
    [Fact]
    public void AGoodBuild_DoesNotWaitForAKey_UnlessExplorerStartedIt()
    {
        // PowerShell, Git Bash, a VS Code task and a CI step all run a .cmd as cmd /c "<path>",
        // exactly as a double click does. Their window stays open, or nobody is there - and a caller
        // that leaves stdin open would wait for ever. This test process is such a caller. -? is a
        // quick success.
        var result = Cmd.Run($"/c \"\"{BuildCmd}\" -?\"");

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Should().NotContain(PauseLine);
    }

    [Fact]
    public void AFailedBuild_WaitsForAKey()
    {
        // An unknown switch stops PowerShell before build.ps1 runs a line.
        var result = Cmd.Run($"/c \"\"{BuildCmd}\" -NoSuchSwitch\"");

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain(PauseLine);
    }

    [Theory]
    [InlineData("always", "-?", true)]
    [InlineData("never", "-NoSuchSwitch", false)]
    [InlineData("never ", "-NoSuchSwitch", false)]
    [InlineData(" Always", "-?", true)]
    public void TheWait_CanBeForcedOrSwitchedOff(string setting, string arguments, bool waits)
    {
        // "always" is what a double click amounts to; "never" is for a caller that must not hang.
        // The space is what "set DIFFUSIONNEXUS_BUILD_PAUSE=never && build.cmd" stores at the end.
        var result = Cmd.Run($"/c \"\"{BuildCmd}\" {arguments}\"", ("DIFFUSIONNEXUS_BUILD_PAUSE", setting));

        result.Output.Contains(PauseLine).Should().Be(waits, result.Output);
    }

    [Fact]
    public void ASettingThatIsNeitherWord_IsIgnoredOutLoud()
    {
        var result = Cmd.Run($"/c \"\"{BuildCmd}\" -NoSuchSwitch\"", ("DIFFUSIONNEXUS_BUILD_PAUSE", "off"));

        result.Output.Should().Contain("neither never nor always").And.Contain(PauseLine);
    }

    [Theory]
    [InlineData("/c", true, 0)]
    [InlineData("/c", false, 1)]
    [InlineData("/k", true, 1)]
    public void StartedByExplorer_MeansACmdSlashC_WhoseParentIsExplorer(string cmdSwitch, bool parentIsTheStarter, int expected)
    {
        // A test cannot have Explorer as its parent, so the helper is told to look for this test
        // process instead. The chain is a double click's: helper, cmd /c "<file>", starter. cmd /k
        // is an open Command Prompt, which runs a typed .cmd inside itself: Explorer's child too,
        // but no double click.
        using var temp = new TempFolder();
        using var self = Process.GetCurrentProcess();
        var starter = parentIsTheStarter ? Path.GetFileName(self.MainModule!.FileName) : "somebody-else.exe";
        var wrapper = Path.Combine(temp.Path, "wrapper.cmd");
        File.WriteAllText(
            wrapper,
            $"@pwsh -NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(Pwsh.ScriptsFolder, "_started-by-explorer.ps1")}\" -StarterName \"{starter}\"\r\n" +
            "@echo result=%ERRORLEVEL%\r\n");

        var result = Cmd.Run($"{cmdSwitch} \"\"{wrapper}\"\"");

        result.Output.Should().Contain($"result={expected}");
    }
}

/// <summary>
/// Scripts\_common.ps1, build.ps1 and build.cmd: the stable build that publishes into E:\StableVersion
/// from develop. They are run in a real pwsh and a real cmd, because the rules worth pinning - which
/// version a build gets, which folder may be deleted, where the user's outputs live - are in
/// PowerShell, not in C#. Three test classes, so xunit runs the slow groups side by side.
/// </summary>
public abstract class BuildScriptTestBase
{
    protected const string PauseLine = "Press any key to continue";
    protected const string MarkerName = "diffusionnexus-install.json";
    protected const string ExeName = "DiffusionNexus.UI.exe";

    protected static Process StartStandIn(string exe) =>
        Process.Start(new ProcessStartInfo(exe, "-n 120 127.0.0.1")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    /// <summary>A stand-in for Invoke-Git with a develop that is clean and level with origin, unless told otherwise.</summary>
    protected static string FakeGit(string branch = "develop", string status = "", string leftRight = "0`t0", string shallow = "false") =>
        "function Invoke-Git { param([string[]]$Arguments, [switch]$Quiet) " +
        "$global:LASTEXITCODE = 0; " +
        "switch ($Arguments[0]) { " +
        $"'rev-parse' {{ if ($Arguments -contains '--is-shallow-repository') {{ '{shallow}' }} else {{ '{branch}' }} }} " +
        $"'status' {{ {status} }} " +
        $"'rev-list' {{ if ($Arguments -contains '--left-right') {{ \"{leftRight}\" }} else {{ '2483' }} }} }} }}";

    protected static string[] Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    protected static string BuildCmd => Path.Combine(Pwsh.ScriptsFolder, "build.cmd");

    /// <summary>
    /// A throwaway copy of the scripts in a repo-shaped folder, with a stable root of its own. The
    /// copy of _common.ps1 ends with fakes for everything that reaches outside that folder: git, the
    /// SDK, the Start Menu shortcut and the list of running apps. build.ps1 is the real file.
    /// </summary>
    internal sealed class FakeRepo : IDisposable
    {
        public const string OriginalProps = "<Project>\r\n  <PropertyGroup>\r\n    <Version>0.11.0.1</Version>\r\n  </PropertyGroup>\r\n</Project>\r\n";

        private readonly TempFolder _temp = new();

        public FakeRepo()
        {
            Root = Directory.CreateDirectory(Path.Combine(_temp.Path, "stable")).FullName;
            Outside = Directory.CreateDirectory(Path.Combine(_temp.Path, "builds")).FullName;
            var scripts = Directory.CreateDirectory(Path.Combine(Repo, "Scripts")).FullName;
            File.Copy(Path.Combine(Pwsh.ScriptsFolder, "build.ps1"), Path.Combine(scripts, "build.ps1"));
            File.WriteAllText(
                Path.Combine(scripts, "_common.ps1"),
                File.ReadAllText(Path.Combine(Pwsh.ScriptsFolder, "_common.ps1")) + Fakes);
            var project = Directory.CreateDirectory(Path.Combine(Repo, "DiffusionNexus.UI")).FullName;
            File.WriteAllText(Path.Combine(project, "DiffusionNexus.UI.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(Repo, "LICENSE"), "MIT");
            File.WriteAllText(Path.Combine(Repo, "THIRD-PARTY-NOTICES.txt"), "notices");
            File.WriteAllText(Props, OriginalProps);
        }

        private string Repo => Path.Combine(_temp.Path, "repo");

        private string Log => Path.Combine(_temp.Path, "calls.log");

        private string Props => Path.Combine(Repo, "Directory.Build.props");

        /// <summary>The stable root: what E:\StableVersion is on the real machine.</summary>
        public string Root { get; }

        /// <summary>A folder outside both the repo and the stable root, for one-off builds.</summary>
        public string Outside { get; }

        public string DataDir => Path.Combine(Root, "DiffusionNexus-data");

        public string CurrentLink => Path.Combine(Root, "DiffusionNexus");

        public string? CurrentLinkTarget => new DirectoryInfo(CurrentLink).LinkTarget;

        public string[] Calls => File.Exists(Log) ? File.ReadAllLines(Log) : [];

        public string PropsText => File.ReadAllText(Props);

        public string VersionFolder(string version) => Path.Combine(Root, $"DiffusionNexus-{version}");

        public static bool IsMarkedComplete(string folder) =>
            Regex.IsMatch(File.ReadAllText(Path.Combine(folder, MarkerName)), "\"complete\":\\s*true");

        /// <summary>Runs the real build.ps1 against this repo. <paramref name="before"/> sets the fakes' switches.</summary>
        public PwshResult Build(string arguments = "", string before = "") =>
            Pwsh.Run($"{before}\n& '{Path.Combine(Repo, "Scripts", "build.ps1")}' {arguments}");

        public void Dispose() => _temp.Dispose();

        private string Fakes => $$"""


            # --- test fakes: nothing below reaches outside the test's own folder ---
            $script:DefaultInstallRoot = '{{Root}}'
            function Write-FakeCall { param([string]$Line) Add-Content -LiteralPath '{{Log}}' -Value $Line }
            function git {
                $global:LASTEXITCODE = 0
                $rest = @($args | Select-Object -Skip 2)
                Write-FakeCall "git $($rest -join ' ')"
                switch ($rest[0]) {
                    'status' { if ($env:FAKE_DIRTY) { ' M DiffusionNexus.UI/App.axaml.cs' } }
                    'rev-parse' {
                        if ($rest -contains '--short') { 'abc1234' }
                        elseif ($rest -contains '--is-shallow-repository') { 'false' }
                        elseif ($env:FAKE_BRANCH) { $env:FAKE_BRANCH }
                        else { 'develop' }
                    }
                    'rev-list' {
                        if ($rest -contains '--left-right') { "0`t0" }
                        elseif ($env:FAKE_COMMIT_COUNT) { $env:FAKE_COMMIT_COUNT }
                        else { '2483' }
                    }
                    'tag' { if ($env:FAKE_TAGS) { $env:FAKE_TAGS -split ',' } }
                    'log' { if ($env:FAKE_LOG) { $env:FAKE_LOG -split '\|' } }
                }
            }
            function dotnet {
                $global:LASTEXITCODE = 0
                if ($args[0] -eq '--version') { return '10.0.0-fake' }
                $versions = @($args | Where-Object { "$_" -like '-p:*Version=*' })
                Write-FakeCall ((@("dotnet", $args[0], (Split-Path -Leaf $args[1])) + $versions) -join ' ')
                if ($args[0] -ne 'publish') { return }
                $out = $args[[array]::IndexOf($args, '--output') + 1]
                Write-FakeCall "output $out"
                if ($env:FAKE_PUBLISH_FAILS) { New-Item -ItemType File -Path (Join-Path $out 'half.dll') -Force | Out-Null; $global:LASTEXITCODE = 1; return }
                New-Item -ItemType File -Path (Join-Path $out $AppExeName) -Force | Out-Null
            }
            function Write-StartMenuShortcut { param([string]$InstallDir) Write-FakeCall "shortcut $InstallDir"; return 'fake.lnk' }
            function Get-AppProcess {
                param([string]$InstallDir, [switch]$AnyVersion, [string]$InstallRoot)
                if (-not $env:FAKE_RUNNING_FROM) { return @() }
                # As the real lookup: through a link, the folder the link leads to right now.
                if (-not $AnyVersion -and -not (Test-SamePath $env:FAKE_RUNNING_FROM (Join-Path (Resolve-FullPath $InstallDir) $AppExeName))) { return @() }
                return @([pscustomobject]@{ ProcessId = 4242; ExecutablePath = $env:FAKE_RUNNING_FROM })
            }
            if ($env:FAKE_LINK_FAILS) { function Set-CurrentLink { throw 'the junction could not be made' } }

            """;
    }

    internal static class Cmd
    {
        public static PwshResult Run(string arguments, params (string Name, string Value)[] environment) =>
            Shell.Run("cmd.exe", arguments, stdin: "", environment);
    }

    internal sealed record PwshResult(int ExitCode, string Output);

    internal static class Pwsh
    {
        public static string ScriptsFolder { get; } = RepoRoot.Combine("Scripts");

        /// <summary>Runs <paramref name="command"/> after dot-sourcing _common.ps1, as build.ps1 does.</summary>
        public static PwshResult RunCommon(string command, string shell = "pwsh") =>
            Run($". '{Path.Combine(ScriptsFolder, "_common.ps1")}'\n{command}", shell);

        public static PwshResult Run(string script, string shell = "pwsh") =>
            Shell.Run(shell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -", script + "\nexit $LASTEXITCODE\n");
    }

    internal static class Shell
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

        public static PwshResult Run(string fileName, string arguments, string stdin, params (string Name, string Value)[] environment)
        {
            var start = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var (name, value) in environment)
            {
                start.Environment[name] = value;
            }

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();

            // A script that waits - a pause reading a console, a real git asking for a login - would
            // otherwise hang the whole test run with nothing to say which test it was.
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(10));
                var soFar = stdout.IsCompleted && stderr.IsCompleted ? stdout.Result + stderr.Result : "(not readable)";
                throw new TimeoutException($"{fileName} did not finish within {Timeout.TotalSeconds:0} s and was killed. Output so far:\n{soFar}");
            }

            return new PwshResult(process.ExitCode, stdout.Result + stderr.Result);
        }
    }

    internal sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiffusionNexus.BuildScriptTests-" + Guid.NewGuid())).FullName;

        public void Dispose()
        {
            try
            {
                RemoveLinks(new DirectoryInfo(Path));
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup: a lingering handle must not fail the suite.
            }
        }

        /// <summary>A recursive delete refuses a junction, so each one is taken out first - the link, never its target.</summary>
        private static void RemoveLinks(DirectoryInfo folder)
        {
            foreach (var child in folder.EnumerateDirectories())
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    child.Delete();
                }
                else
                {
                    RemoveLinks(child);
                }
            }
        }
    }
}
