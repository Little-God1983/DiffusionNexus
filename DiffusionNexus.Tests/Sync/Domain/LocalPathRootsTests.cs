using DiffusionNexus.Domain.Utilities;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Domain;

/// <summary>
/// Covers <see cref="LocalPathRoots"/> — the single "is this file inside that source folder?"
/// predicate (R6). It existed twice before, in two different spellings: the viewer's
/// (<c>ModelFileSyncService.MatchEnabledRoot</c>, <c>\</c>-or-<c>/</c> and
/// <c>OrdinalIgnoreCase</c>) and the sync repository's (one baked-in
/// <c>Path.DirectorySeparatorChar</c> and an ASCII-only fold), so a file the viewer showed could
/// be invisible to the sync and vice versa.
/// </summary>
public sealed class LocalPathRootsTests
{
    [Theory]
    [InlineData(@"C:\Loras\a.safetensors", @"C:\Loras")]
    [InlineData(@"C:\Loras\sub\a.safetensors", @"C:\Loras")]
    // The root may or may not carry a trailing separator, in either spelling.
    [InlineData(@"C:\Loras\a.safetensors", @"C:\Loras\")]
    [InlineData(@"C:\Loras\a.safetensors", @"C:\Loras/")]
    // The separator at the boundary may be either spelling, whichever the root used.
    [InlineData(@"C:\Loras/a.safetensors", @"C:\Loras")]
    [InlineData(@"C:/Loras/a.safetensors", @"C:/Loras")]
    [InlineData(@"C:/Loras\a.safetensors", @"C:/Loras")]
    // ASCII casing is irrelevant…
    [InlineData(@"c:\loras\a.safetensors", @"C:\LORAS")]
    // …and so is non-ASCII casing, which is what an ASCII-only fold got wrong.
    [InlineData(@"E:\Öffentlich\Loras\a.safetensors", @"E:\ÖFFENTLICH\Loras")]
    // The root itself counts as being under itself.
    [InlineData(@"C:\Loras", @"C:\Loras")]
    public void IsUnder_True(string path, string root)
        => LocalPathRoots.IsUnder(path, root).Should().BeTrue();

    [Theory]
    // Boundary-aware: a sibling folder whose name merely starts the same way is not inside.
    [InlineData(@"C:\Loras_backup\a.safetensors", @"C:\Loras")]
    [InlineData(@"C:\LorasBackup", @"C:\Loras")]
    [InlineData(@"D:\Loras\a.safetensors", @"C:\Loras")]
    [InlineData(@"C:\Other\a.safetensors", @"C:\Loras")]
    // Inside the root itself the spelling has to agree: the point of the shared function is that
    // both sides answer identically, not that both get cleverer than the viewer already was.
    [InlineData(@"C:/Loras/a.safetensors", @"C:\Loras")]
    // A root that trims away to nothing matches nothing — it must not become "everything".
    [InlineData(@"C:\Loras\a.safetensors", @"\")]
    [InlineData(@"C:\Loras\a.safetensors", "")]
    [InlineData(@"C:\Loras\a.safetensors", "   ")]
    public void IsUnder_False(string path, string root)
        => LocalPathRoots.IsUnder(path, root).Should().BeFalse();

    [Fact]
    public void IsUnder_NullOrEmptyPathIsNeverUnderAnything()
    {
        LocalPathRoots.IsUnder(null, @"C:\Loras").Should().BeFalse();
        LocalPathRoots.IsUnder("", @"C:\Loras").Should().BeFalse();
    }

    [Fact]
    public void IsUnderAny_TrueWhenAnyRootContainsIt()
    {
        string[] roots = [@"D:\Other", @"C:\Loras"];

        LocalPathRoots.IsUnderAny(@"C:\Loras\a.safetensors", roots).Should().BeTrue();
        LocalPathRoots.IsUnderAny(@"E:\Elsewhere\a.safetensors", roots).Should().BeFalse();
        LocalPathRoots.IsUnderAny(@"C:\Loras\a.safetensors", []).Should().BeFalse();
    }
}
