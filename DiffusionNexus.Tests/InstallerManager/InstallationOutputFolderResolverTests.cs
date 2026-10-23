using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Covers <see cref="InstallationOutputFolderResolver"/> — reading the image output
/// folders an installation writes to out of its startup script, so the remove flow can
/// tell a folder that is exclusively this installation's from one several installations
/// share.
/// </summary>
public sealed class InstallationOutputFolderResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dn-output-resolver").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private InstallerPackage Package(string? executable = "run_nvidia.bat", InstallerType type = InstallerType.ComfyUI)
        => new()
        {
            Id = 1,
            Name = "ComfyUI",
            InstallationPath = _root,
            ExecutablePath = executable,
            Type = type
        };

    private void WriteLauncher(string contents, string name = "run_nvidia.bat")
        => File.WriteAllText(Path.Combine(_root, name), contents);

    [Fact]
    public void Resolve_ReadsAbsoluteOutputDirectoryFromTheLauncher()
    {
        // Verbatim shape of the real script this bug was found with.
        WriteLauncher("""
            call "%~dp0venv\Scripts\activate.bat"
            python main.py --windows-standalone-build --output-directory E:\AI\comfy_output
            """);

        var folders = InstallationOutputFolderResolver.Resolve(Package());

        folders.Should().Contain(@"E:\AI\comfy_output");
    }

    [Fact]
    public void Resolve_RelativeOutputDirectory_ResolvesAgainstTheInstallation()
    {
        WriteLauncher(@"python main.py --output-directory ..\shared_output");

        var folders = InstallationOutputFolderResolver.Resolve(Package());

        folders.Should().Contain(Path.Combine(_root, @"..\shared_output"));
    }

    [Fact]
    public void Resolve_IncludesTheConventionalOutputFolderWhenItExists()
    {
        var conventional = Path.Combine(_root, "output");
        Directory.CreateDirectory(conventional);

        var folders = InstallationOutputFolderResolver.Resolve(Package(executable: null));

        folders.Should().Contain(conventional);
    }

    [Fact]
    public void Resolve_SkipsConventionalFoldersThatDoNotExist()
    {
        var folders = InstallationOutputFolderResolver.Resolve(Package(executable: null));

        folders.Should().BeEmpty("a folder that was never created is not in use by anything");
    }

    [Fact]
    public void Resolve_MissingLauncher_IsNotAnError()
    {
        var folders = InstallationOutputFolderResolver.Resolve(Package(executable: "does-not-exist.bat"));

        folders.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_EmptyInstallationPath_YieldsNothing()
    {
        var package = new InstallerPackage
        {
            Id = 1,
            Name = "Broken",
            InstallationPath = string.Empty,
            ExecutablePath = "run.bat",
            Type = InstallerType.ComfyUI
        };

        InstallationOutputFolderResolver.Resolve(package).Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"python main.py --output-directory E:\out", @"E:\out")]
    [InlineData(@"python main.py --output-directory=E:\out", @"E:\out")]
    [InlineData(@"python main.py --output-directory ""E:\my out""", @"E:\my out")]
    [InlineData(@"python main.py --output-directory E:\out --listen", @"E:\out")]
    [InlineData(@"python main.py --OUTPUT-DIRECTORY E:\out", @"E:\out")]
    [InlineData(@"python main.py --listen", null)]
    [InlineData(@"python main.py --output-directoryish E:\out", null)]
    [InlineData(@"python main.py --output-directory", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseOutputDirectoryArgument_HandlesTheArgumentForms(string? line, string? expected)
    {
        InstallationOutputFolderResolver.ParseOutputDirectoryArgument(line).Should().Be(expected);
    }
}
