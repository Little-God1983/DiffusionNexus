using System.Security.Cryptography;
using System.Text;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers <see cref="CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync"/>.
/// Civitai file names are frequently generic ("V1.safetensors"), so two
/// unrelated models routed to the same BaseModel/Category folder collide:
/// the second download overwrote the first model's weights on disk, and the
/// path-based DB dedup in PersistDownloadedModelAsync then skipped registering
/// it — downloaded to 100% over and over yet never installed (user-reported,
/// model 2839119 "[Krea2] Light projection Concept" clobbering
/// "Joschek's Gimp for Krea 2" at ...\Krea 2\Concept\V1.safetensors).
/// </summary>
public sealed class CivitaiDownloadTargetCollisionTests : IDisposable
{
    private readonly string _dir;

    public CivitaiDownloadTargetCollisionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dn-collide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static string Sha256Of(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public async Task NoExistingFile_KeepsPlainFileName()
    {
        var target = await CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync(
            _dir, "V1.safetensors", versionId: 3204603, expectedSha256: Sha256Of("mine"), CancellationToken.None);

        target.Should().Be(Path.Combine(_dir, "V1.safetensors"));
    }

    [Fact]
    public async Task ForeignFileAtTarget_AppendsVersionId()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1.safetensors"), "someone else's weights");

        var target = await CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync(
            _dir, "V1.safetensors", versionId: 3204603, expectedSha256: Sha256Of("mine"), CancellationToken.None);

        target.Should().Be(Path.Combine(_dir, "V1_3204603.safetensors"),
            "a different model's file must never be overwritten");
    }

    [Fact]
    public async Task OwnPreviousDownloadAtTarget_KeepsPlainFileName()
    {
        // Re-download of the same version over its own file — overwriting in
        // place is correct and avoids piling up suffixed duplicates.
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1.safetensors"), "mine");

        var target = await CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync(
            _dir, "V1.safetensors", versionId: 3204603, expectedSha256: Sha256Of("mine"), CancellationToken.None);

        target.Should().Be(Path.Combine(_dir, "V1.safetensors"));
    }

    [Fact]
    public async Task ExistingFileButNoExpectedHash_AppendsVersionId()
    {
        // Without an expected hash we can't prove the existing file is ours —
        // the safe default is to not overwrite it.
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1.safetensors"), "unknowable");

        var target = await CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync(
            _dir, "V1.safetensors", versionId: 3204603, expectedSha256: null, CancellationToken.None);

        target.Should().Be(Path.Combine(_dir, "V1_3204603.safetensors"));
    }

    [Fact]
    public async Task SuffixedNameIsStableAcrossRetries()
    {
        // The suffix is the Civitai version id — deterministic, so a retry (or
        // a later re-download) of the same version lands on the same file
        // instead of generating V1_1, V1_2, ... clutter.
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1.safetensors"), "someone else's weights");
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1_3204603.safetensors"), "stale previous attempt");

        var first = await CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync(
            _dir, "V1.safetensors", versionId: 3204603, expectedSha256: Sha256Of("mine"), CancellationToken.None);
        var second = await CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync(
            _dir, "V1.safetensors", versionId: 3204603, expectedSha256: Sha256Of("mine"), CancellationToken.None);

        first.Should().Be(second).And.Be(Path.Combine(_dir, "V1_3204603.safetensors"));
    }
}
