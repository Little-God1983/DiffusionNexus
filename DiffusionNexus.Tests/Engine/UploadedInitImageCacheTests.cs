using DiffusionNexus.UI.Services.Diffusion;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

/// <summary>
/// The per-batch upload cache in the engine backend: a byte-identical scratch file is uploaded once, and
/// nothing about the cache can make the graph name a file the server no longer has.
/// </summary>
public class UploadedInitImageCacheTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dn-upload-cache-{Guid.NewGuid():N}.png");

    public UploadedInitImageCacheTests() => File.WriteAllBytes(_path, [1, 2, 3, 4, 5]);

    public void Dispose()
    {
        try { File.Delete(_path); }
        catch (IOException) { /* best-effort test cleanup */ }
    }

    [Fact]
    public void MissesBeforeAnythingWasRemembered()
    {
        var cache = new UploadedInitImageCache();

        cache.TryGet(_path, _ => true, out _).Should().BeFalse();
    }

    [Fact]
    public void HitsWhileTheFileIsUnchangedAndTheServerStillHasIt()
    {
        var cache = new UploadedInitImageCache();
        cache.Remember(_path, "diffusionnexus_canvas_region.png");

        cache.TryGet(_path, _ => true, out var stored).Should().BeTrue();
        stored.Should().Be("diffusionnexus_canvas_region.png");
    }

    [Fact]
    public void MissesOnceTheFileContentChanges()
    {
        // The canvas rewrites the same path on every Generate, so the next batch's region must be
        // uploaded even though the path (and here the length too) is identical.
        var cache = new UploadedInitImageCache();
        cache.Remember(_path, "stored.png");
        File.WriteAllBytes(_path, [9, 9, 9, 9, 9]);

        cache.TryGet(_path, _ => true, out _).Should().BeFalse();
    }

    [Fact]
    public void MissesAndForgetsWhenTheServerCopyIsGone()
    {
        var cache = new UploadedInitImageCache();
        cache.Remember(_path, "stored.png");
        var asked = new List<string>();

        cache.TryGet(_path, name => { asked.Add(name); return false; }, out _).Should().BeFalse();
        asked.Should().Equal("stored.png");

        // Forgotten: a later probe that would say "yes" must not resurrect the stale entry.
        cache.TryGet(_path, _ => true, out _).Should().BeFalse();
    }

    [Fact]
    public void MissesForADifferentPathEvenWithTheSameContent()
    {
        // The stored name is derived from the file name, so a different path is a different upload.
        var other = Path.Combine(Path.GetTempPath(), $"dn-upload-cache-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(other, [1, 2, 3, 4, 5]);
        try
        {
            var cache = new UploadedInitImageCache();
            cache.Remember(_path, "stored.png");

            cache.TryGet(other, _ => true, out _).Should().BeFalse();
        }
        finally
        {
            File.Delete(other);
        }
    }

    [Fact]
    public void RememberingAnUnreadableFileLeavesTheCacheEmpty()
    {
        var cache = new UploadedInitImageCache();
        cache.Remember(_path, "stored.png");

        cache.Remember(Path.Combine(Path.GetTempPath(), $"dn-missing-{Guid.NewGuid():N}.png"), "ghost.png");

        cache.TryGet(_path, _ => true, out _).Should().BeFalse(
            "a failed Remember must not leave the previous entry live");
    }
}
