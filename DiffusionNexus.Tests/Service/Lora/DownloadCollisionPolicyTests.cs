using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using DiffusionNexus.Service.Services.Lora;
using FluentAssertions;

namespace DiffusionNexus.Tests.Service.Lora;

/// <summary>
/// Covers <see cref="DownloadCollisionPolicy.ResolveAsync"/> — the one collision policy for
/// every Civitai download path (spec §4.4, S4), moved out of the (now-deleted)
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

    [Fact]
    public async Task ResolveAsync_NoVersionId_PlainNameHoldsForeignBytes_TakesTheFirstFreeNumberedName()
    {
        // A local-only version maps CivitaiId ?? 0, so "{stem}_0" is not version-unique at all —
        // two different local models in one folder both claimed it and the second download
        // overwrote the first's weights. Without a usable version id the numeric sequence is the
        // only safe naming, and an existing name we cannot prove is ours is never returned.
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1.safetensors"), "someone else's weights");

        var resolution = await DownloadCollisionPolicy.ResolveAsync(
            _dir, "V1.safetensors", versionId: 0, expectedSha256: Sha256Of("mine"), CancellationToken.None);

        resolution.TargetPath.Should().Be(Path.Combine(_dir, "V1_2.safetensors"));
        resolution.ExistingContentMatches.Should().BeFalse();
        File.Exists(Path.Combine(_dir, "V1_0.safetensors")).Should().BeFalse(
            "the meaningless _0 name must never be handed out");
        (await File.ReadAllTextAsync(Path.Combine(_dir, "V1.safetensors")))
            .Should().Be("someone else's weights", "a foreign file must be left exactly as it was");
    }

    [Fact]
    public async Task ResolveAsync_NoVersionId_NumberedNameHoldsIdenticalContent_ReusesIt()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1.safetensors"), "someone else's weights");
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1_2.safetensors"), "mine");

        var resolution = await DownloadCollisionPolicy.ResolveAsync(
            _dir, "V1.safetensors", versionId: 0, expectedSha256: Sha256Of("mine"), CancellationToken.None);

        resolution.TargetPath.Should().Be(Path.Combine(_dir, "V1_2.safetensors"));
        resolution.ExistingContentMatches.Should().BeTrue(
            "hash-proof reuse is what makes a re-download of the same local file idempotent");
    }

    [Fact]
    public async Task ResolveAsync_NoVersionId_WalksPastEveryUnprovableName()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1.safetensors"), "first model");
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1_2.safetensors"), "second model");
        await File.WriteAllTextAsync(Path.Combine(_dir, "V1_3.safetensors"), "third model");

        var resolution = await DownloadCollisionPolicy.ResolveAsync(
            _dir, "V1.safetensors", versionId: 0, expectedSha256: null, CancellationToken.None);

        resolution.TargetPath.Should().Be(Path.Combine(_dir, "V1_4.safetensors"));
        resolution.ExistingContentMatches.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_PlainNameAccessDenied_CannotProveOwnership_SoItSuffixesWithoutThrowing()
    {
        // Hostile-disk case (S4): the colliding file exists but is ACL-denied to us — the
        // hash probe must not let UnauthorizedAccessException escape ResolveAsync. Denied
        // OR unreadable, either way we can't prove the file is ours, so it gets the same
        // "don't overwrite it" treatment as the locked/IOException case above.
        var path = Path.Combine(_dir, "V1.safetensors");
        await File.WriteAllTextAsync(path, "someone else's weights");

        var fileInfo = new FileInfo(path);
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var denyRule = new FileSystemAccessRule(currentUser, FileSystemRights.Read, AccessControlType.Deny);
        var accessControl = fileInfo.GetAccessControl();
        accessControl.AddAccessRule(denyRule);
        fileInfo.SetAccessControl(accessControl);

        try
        {
            var resolution = await DownloadCollisionPolicy.ResolveAsync(
                _dir, "V1.safetensors", versionId: 3204603, expectedSha256: Sha256Of("mine"), CancellationToken.None);

            resolution.TargetPath.Should().Be(Path.Combine(_dir, "V1_3204603.safetensors"),
                "an access-denied file must never be reported as a content match, and must never crash the download");
            resolution.ExistingContentMatches.Should().BeFalse();
        }
        finally
        {
            // Remove the deny rule before Dispose() tries to delete the temp dir.
            var cleanup = fileInfo.GetAccessControl();
            cleanup.RemoveAccessRule(denyRule);
            fileInfo.SetAccessControl(cleanup);
        }
    }
}
