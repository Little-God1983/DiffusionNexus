using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Covers <see cref="FolderPathMatch"/>. The case that matters in practice is the trailing
/// separator: paths stored in the database keep it, paths parsed out of a startup script
/// usually don't, and comparing the raw strings made the same folder look like two.
/// </summary>
public sealed class FolderPathMatchTests
{
    [Theory]
    [InlineData(@"E:\AI\comfy_output", @"E:\AI\comfy_output\", true)]
    [InlineData(@"E:\AI\comfy_output\", @"E:\AI\comfy_output", true)]
    [InlineData(@"E:\AI\comfy_output", @"e:\ai\COMFY_OUTPUT", true)]
    [InlineData(@"E:\AI\comfy_output", @"E:\AI\comfy_output\sub", false)]
    [InlineData(@"E:\AI\comfy_output", @"E:\AI\comfy_output2", false)]
    [InlineData(@"E:\AI\comfy_output", "", false)]
    [InlineData(@"E:\AI\comfy_output", null, false)]
    [InlineData(null, null, false)]
    public void AreSame_ComparesFolderIdentityNotStrings(string? left, string? right, bool expected)
    {
        FolderPathMatch.AreSame(left, right).Should().Be(expected);
    }

    [Fact]
    public void AreSame_ToleratesInvalidPaths()
    {
        FolderPathMatch.AreSame("\0bad", @"E:\AI").Should().BeFalse();
    }

    [Theory]
    [InlineData(@"C:\AI\ComfyUI", @"C:\AI\ComfyUI", true)]
    [InlineData(@"C:\AI\ComfyUI", @"C:\AI\ComfyUI\", true)]
    [InlineData(@"C:\AI\ComfyUI", @"C:\AI\ComfyUI\models\loras", true)]
    [InlineData(@"C:\AI\ComfyUI", @"C:\AI\ComfyUI-Backup\loras", false)]
    [InlineData(@"C:\AI\ComfyUI", @"C:\AI", false)]
    [InlineData(@"C:\AI\ComfyUI", null, false)]
    [InlineData(null, @"C:\AI\ComfyUI", false)]
    public void Contains_TreatsEqualAndNestedAsContained(string? root, string? candidate, bool expected)
    {
        FolderPathMatch.Contains(root, candidate).Should().Be(expected);
    }

    [Fact]
    public void Normalize_StripsTheTrailingSeparator()
    {
        FolderPathMatch.Normalize(@"E:\AI\comfy_output\").Should().Be(@"E:\AI\comfy_output");
    }

    [Fact]
    public void Normalize_BlankOrInvalid_IsNull()
    {
        FolderPathMatch.Normalize("").Should().BeNull();
        FolderPathMatch.Normalize("   ").Should().BeNull();
        FolderPathMatch.Normalize(null).Should().BeNull();
        FolderPathMatch.Normalize("\0bad").Should().BeNull();
    }
}
