using System.Text.Json;
using DiffusionNexus.Civitai.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

/// <summary>
/// #628: a recipe matches a LoRA weight to a Civitai file by any hash kind the image's tool wrote —
/// AutoV2 (A1111), AutoV3 or SHA256_12 (other tools) — so every kind Civitai lists must be readable.
/// </summary>
public sealed class CivitaiFileHashesTests
{
    [Fact]
    public void AllValues_ListsEveryHashKindIncludingAutoV3AndSha256_12()
    {
        var hashes = JsonSerializer.Deserialize<CivitaiFileHashes>(
            """{"AutoV1":"F19D324D","AutoV2":"990D0DF641","SHA256":"990D0DF6418709EF","CRC32":"FC9A8795","BLAKE3":"D514","AutoV3":"47A98D0C95BE","SHA256_12":"990D0DF64187"}""")!;

        hashes.AllValues().Should().BeEquivalentTo(
            "F19D324D", "990D0DF641", "990D0DF6418709EF", "FC9A8795", "D514", "47A98D0C95BE", "990D0DF64187");
    }

    [Fact]
    public void AllValues_SkipsMissingKinds()
    {
        new CivitaiFileHashes { AutoV2 = "ABC", SHA256 = "" }.AllValues().Should().Equal("ABC");
    }
}
