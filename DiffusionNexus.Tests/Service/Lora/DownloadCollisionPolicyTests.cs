using System.Security.Cryptography;
using System.Text;
using DiffusionNexus.Service.Services.Lora;
using FluentAssertions;

namespace DiffusionNexus.Tests.Service.Lora;

/// <summary>
/// Covers <see cref="DownloadCollisionPolicy.ResolveAsync"/> — the one collision policy for
/// every Civitai download path (spec §4.4, S4), moved from
/// <c>CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync</c>. Civitai file names are
/// frequently generic ("V1.safetensors"), so two unrelated models routed to the same
/// BaseModel/Category folder collide: the second download overwrote the first model's weights
/// on disk, and the path-based DB dedup in PersistDownloadedModelAsync then skipped
/// registering it — downloaded to 100% over and over yet never installed (user-reported,
/// model 2839119 "[Krea2] Light projection Concept" clobbering
/// "Joschek's Gimp for Krea 2" at ...\Krea 2\Concept\V1.safetensors).
/// </summary>
public sealed class DownloadCollisionPolicyTests : IDisposable
{
    private readonly string _dir;

    public DownloadCollisionPolicyTests()
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
    public async Task ResolveAsync_PlainNameFree_UsesIt()
    {
        var resolution = await DownloadCollisionPolicy.ResolveAsync(
            _dir, "V1.safetensors", versionId: 3204603, expectedSha256: Sha256Of("mine"), CancellationToken.None);

        resolution.TargetPath.Should().Be(Path.Combine(_dir, "V1.safetensors"));
        resolution.ExistingContentMatches.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_PlainNameHoldsDifferentContent_AppendsVersionId()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1.safetensors"), "someone else's weights");

        var resolution = await DownloadCollisionPolicy.ResolveAsync(
            _dir, "V1.safetensors", versionId: 3204603, expectedSha256: Sha256Of("mine"), CancellationToken.None);

        resolution.TargetPath.Should().Be(Path.Combine(_dir, "V1_3204603.safetensors"),
            "a different model's file must never be overwritten");
        resolution.ExistingContentMatches.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_PlainNameHoldsIdenticalContent_ReusesWithoutSuffix()
    {
        // Re-download of the same version over its own file — overwriting in
        // place is correct and avoids piling up suffixed duplicates.
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1.safetensors"), "mine");

        var resolution = await DownloadCollisionPolicy.ResolveAsync(
            _dir, "V1.safetensors", versionId: 3204603, expectedSha256: Sha256Of("mine"), CancellationToken.None);

        resolution.TargetPath.Should().Be(Path.Combine(_dir, "V1.safetensors"));
        resolution.ExistingContentMatches.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_SuffixedNameAlreadyHoldsIdenticalContent_ReusesIt()
    {
        // The plain name belongs to a different model; the versioned suffix already
        // holds this exact version's bytes from an earlier run — reuse it as-is.
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1.safetensors"), "someone else's weights");
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1_3204603.safetensors"), "mine");

        var resolution = await DownloadCollisionPolicy.ResolveAsync(
            _dir, "V1.safetensors", versionId: 3204603, expectedSha256: Sha256Of("mine"), CancellationToken.None);

        resolution.TargetPath.Should().Be(Path.Combine(_dir, "V1_3204603.safetensors"));
        resolution.ExistingContentMatches.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_NoExpectedHash_CannotProveOwnership_SoItSuffixes()
    {
        // Without an expected hash we can't prove the existing file is ours —
        // the safe default is to not overwrite it.
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1.safetensors"), "unknowable");

        var resolution = await DownloadCollisionPolicy.ResolveAsync(
            _dir, "V1.safetensors", versionId: 3204603, expectedSha256: null, CancellationToken.None);

        resolution.TargetPath.Should().Be(Path.Combine(_dir, "V1_3204603.safetensors"));
        resolution.ExistingContentMatches.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_SuffixedNameIsStableAcrossRetries()
    {
        // The suffix is the Civitai version id — deterministic, so a retry (or
        // a later re-download) of the same version lands on the same file
        // instead of generating V1_1, V1_2, ... clutter.
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1.safetensors"), "someone else's weights");
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1_3204603.safetensors"), "stale previous attempt");

        var first = await DownloadCollisionPolicy.ResolveAsync(
            _dir, "V1.safetensors", versionId: 3204603, expectedSha256: Sha256Of("mine"), CancellationToken.None);
        var second = await DownloadCollisionPolicy.ResolveAsync(
            _dir, "V1.safetensors", versionId: 3204603, expectedSha256: Sha256Of("mine"), CancellationToken.None);

        first.TargetPath.Should().Be(second.TargetPath).And.Be(Path.Combine(_dir, "V1_3204603.safetensors"));
    }
}
