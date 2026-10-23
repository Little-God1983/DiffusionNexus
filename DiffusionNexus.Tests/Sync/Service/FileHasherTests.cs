using DiffusionNexus.Service.Services.Sync;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service;

/// <summary>
/// Covers <see cref="FileHasher"/> — the single SHA256 implementation the sync pipeline uses.
/// Decision D2: hashes are stored and compared as <b>uppercase</b> hex, so the hasher must
/// never emit a lowercase digest (a lowercase hash silently misses every stored comparison).
/// </summary>
public sealed class FileHasherTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("dn-hasher-");

    private string NewFile(string name, byte[] content)
    {
        var path = Path.Combine(_tempDir.FullName, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>The SHA256 of zero bytes — the canonical published test vector, uppercased.</summary>
    private const string EmptySha256Upper = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";

    [Fact]
    public async Task FileHasher_ProducesUppercaseSha256()
    {
        var empty = NewFile("empty.safetensors", []);

        FileHasher.Sha256Upper(empty).Should().Be(EmptySha256Upper);
        (await FileHasher.Sha256UpperAsync(empty, CancellationToken.None)).Should().Be(EmptySha256Upper);
    }

    [Fact]
    public async Task FileHasher_SyncAndAsyncAgreeOnNonEmptyContent()
    {
        // "abc" → the second published SHA256 vector.
        var abc = NewFile("abc.safetensors", "abc"u8.ToArray());
        const string AbcSha256Upper = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";

        FileHasher.Sha256Upper(abc).Should().Be(AbcSha256Upper);
        (await FileHasher.Sha256UpperAsync(abc, CancellationToken.None)).Should().Be(AbcSha256Upper);
    }

    public void Dispose()
    {
        try { _tempDir.Delete(recursive: true); } catch { /* best effort */ }
    }
}
