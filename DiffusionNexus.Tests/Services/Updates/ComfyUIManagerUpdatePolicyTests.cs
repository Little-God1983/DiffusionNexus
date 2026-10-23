using DiffusionNexus.Service.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services.Updates;

/// <summary>
/// Tests for <see cref="ComfyUIManagerUpdatePolicy"/>: the rules that keep this app's
/// ComfyUI updater on the same version ComfyUI-Manager's "Update All" would pick.
/// </summary>
public class ComfyUIManagerUpdatePolicyTests : IDisposable
{
    private readonly string _root;

    public ComfyUIManagerUpdatePolicyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"comfy_policy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void WhenNoManagerIsInstalledThenChannelIsNightly()
    {
        ComfyUIManagerUpdatePolicy.ResolveChannel(_root).Should().Be(ComfyUIUpdateChannel.Nightly);
    }

    [Fact]
    public void WhenManagerIsInstalledButNeverConfiguredThenChannelIsItsDefaultStable()
    {
        Directory.CreateDirectory(Path.Combine(_root, "custom_nodes", "ComfyUI-Manager"));

        ComfyUIManagerUpdatePolicy.ResolveChannel(_root).Should().Be(ComfyUIUpdateChannel.Stable);
    }

    [Fact]
    public void WhenBothConfigLocationsExistThenTheNewOneWins()
    {
        WriteConfig(Path.Combine("user", "__manager"), "update_policy = nightly-comfyui");
        WriteConfig(Path.Combine("user", "default", "ComfyUI-Manager"), "update_policy = stable-comfyui");

        ComfyUIManagerUpdatePolicy.ResolveChannel(_root).Should().Be(ComfyUIUpdateChannel.Nightly);
    }

    [Fact]
    public void WhenOnlyTheLegacyConfigExistsThenItIsRead()
    {
        WriteConfig(Path.Combine("user", "default", "ComfyUI-Manager"), "update_policy = nightly-comfyui");

        ComfyUIManagerUpdatePolicy.ResolveChannel(_root).Should().Be(ComfyUIUpdateChannel.Nightly);
    }

    [Theory]
    [InlineData("update_policy = nightly-comfyui", true)]
    [InlineData("UPDATE_POLICY=Nightly-ComfyUI", true)]
    [InlineData("update_policy = stable-comfyui", false)]
    [InlineData("update_policy = ", false)]
    [InlineData("channel_url = https://example.invalid/nightly", false)]
    public void WhenParsingConfigThenOnlyAnExplicitNightlyPolicyLeavesStable(string line, bool expectNightly)
    {
        ComfyUIManagerUpdatePolicy.ParseChannel(["[default]", "preview_method = none", line])
            .Should().Be(expectNightly ? ComfyUIUpdateChannel.Nightly : ComfyUIUpdateChannel.Stable);
    }

    [Fact]
    public void WhenPickingTheLatestReleaseThenPartsCompareNumericallyAndPreReleasesAreIgnored()
    {
        // Verbatim shape of `git tag -l v*`: lexical order would pick v0.9.0.
        const string tags = "v0.10.0\r\nv0.36.0\nv0.9.0\nv0.37.0-rc1\nv1\nlatest\n";

        ComfyUIManagerUpdatePolicy.PickLatestReleaseTag(tags).Should().Be("v0.36.0");
    }

    [Fact]
    public void WhenThereIsNoReleaseTagThenNullIsReturned()
    {
        ComfyUIManagerUpdatePolicy.PickLatestReleaseTag("latest\nnightly\n").Should().BeNull();
    }

    private void WriteConfig(string relativeDir, string line)
    {
        var dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.ini"), $"[default]\n{line}\n");
    }
}
