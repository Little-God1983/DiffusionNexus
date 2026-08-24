using System.Text.Json;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Tests.Sync.Service.Identity;
using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Sorter;

public sealed class SorterMetadataResolverTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-sortmeta-");
    private readonly Mock<ICivitaiClient> _client = new();

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch (IOException) { }
    }

    private string In(string name) => Path.Combine(_root.FullName, name);

    private string WriteModel(string name = "lora.safetensors")
    {
        var path = In(name);
        File.WriteAllText(path, "weights");
        return path;
    }

    private SorterMetadataResolver Resolver(ICivitaiClient? client = null, string sha = "abc123")
        => new(client, () => Task.FromResult<string?>(null), In("cache"), _ => sha, logger: null);

    [Fact]
    public async Task CivitaiInfoSidecarWinsWithoutTouchingHashOrApi()
    {
        var model = WriteModel();
        File.WriteAllText(In("lora.civitai.info"), """{"id": 555, "baseModel": "Illustrious"}""");
        var resolver = new SorterMetadataResolver(_client.Object, () => Task.FromResult<string?>(null),
            In("cache"), _ => throw new InvalidOperationException("must not hash"), logger: null);

        var meta = await resolver.ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("Illustrious");
        meta.CivitaiVersionId.Should().Be(555);
        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ApiResultIsReturnedAndCachedByHash()
    {
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 777, BaseModel = "SDXL 1.0" });

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.Should().Be(new ResolvedLoraMetadata("SDXL 1.0", 777, "abc123"));
        File.Exists(In(Path.Combine("cache", "abc123.json"))).Should().BeTrue();
    }

    /// <summary>
    /// The hasher is now <c>FileHasher.Sha256Upper</c> (uppercase, the library-wide convention),
    /// while every cache entry an earlier build wrote is named in lowercase. Keying the store on
    /// the digest as-is would orphan all of them and silently re-fetch the whole library, so the
    /// file name is lower-cased on write...
    /// </summary>
    [Fact]
    public async Task CacheFileNameIsLowercasedForAnUppercaseDigest()
    {
        var model = WriteModel();
        const string upper = "ABC123DEF";
        _client.Setup(c => c.GetModelVersionByHashAsync(upper, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 777, BaseModel = "SDXL 1.0" });

        var meta = await Resolver(_client.Object, sha: upper).ResolveAsync(model);

        meta.Sha256.Should().Be(upper, "the digest itself is passed through as the hasher produced it");
        // Assert on the real directory entry, not File.Exists: NTFS is case-insensitive, so
        // File.Exists("ABC123DEF.json") is true either way and would prove nothing.
        Directory.EnumerateFiles(In("cache")).Select(Path.GetFileName).Should().ContainSingle()
            .Which.Should().Be("abc123def.json",
                "the cache file name must be lower-cased so the store is case-insensitive");
    }

    /// <summary>...and on read, so a cache written by an older (lowercase) build is still served.</summary>
    [Fact]
    public async Task LowercaseCacheEntryIsServedForAnUppercaseDigest()
    {
        var model = WriteModel();
        const string upper = "ABC123DEF";
        Directory.CreateDirectory(In("cache"));
        File.WriteAllText(In(Path.Combine("cache", "abc123def.json")),
            """{"baseModel": "Illustrious", "versionId": 42, "tags": ["anime"]}""");

        var meta = await Resolver(_client.Object, sha: upper).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("Illustrious");
        meta.CivitaiVersionId.Should().Be(42);
        _client.VerifyNoOtherCalls(); // a cache hit must not reach Civitai
    }

    [Fact]
    public async Task SecondResolveIsServedFromCacheWithoutApiCall()
    {
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 777, BaseModel = "SDXL 1.0" });
        var resolver = Resolver(_client.Object);

        await resolver.ResolveAsync(model);
        var second = await resolver.ResolveAsync(model);

        second.BaseModelRaw.Should().Be("SDXL 1.0");
        _client.Verify(c => c.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotFoundIsNegativelyCachedAndSortsAsUnknown()
    {
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CivitaiModelVersion?)null);
        var resolver = Resolver(_client.Object);

        var meta = await resolver.ResolveAsync(model);
        await resolver.ResolveAsync(model);

        meta.BaseModelRaw.Should().BeNull();
        _client.Verify(c => c.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoClientYieldsUnknownWithoutThrowing()
    {
        var model = WriteModel();

        var meta = await Resolver(client: null).ResolveAsync(model);

        meta.Should().Be(new ResolvedLoraMetadata(null, null, "abc123"));
    }

    [Fact]
    public async Task MalformedSidecarFallsThroughToApi()
    {
        var model = WriteModel();
        File.WriteAllText(In("lora.civitai.info"), "{ not json ");
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 1, BaseModel = "Pony" });

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("Pony");
    }

    [Fact]
    public async Task EmptyObjectSidecarIsNotAHitAndFallsThrough()
    {
        var model = WriteModel();
        File.WriteAllText(In("lora.civitai.info"), "{}");
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 2, BaseModel = "Illustrious" });

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.Should().Be(new ResolvedLoraMetadata("Illustrious", 2, "abc123"));
    }

    [Fact]
    public async Task ApiKeyIsReadOncePerPassNotOncePerFile()
    {
        // Review 6.3: the provider opens a DI scope + DbContext + query in production, and
        // it was awaited inline in the API call — 1000 unresolved files meant 1000 reads of
        // a value that cannot change mid-pass.
        var reads = 0;
        var resolver = new SorterMetadataResolver(_client.Object,
            () => { reads++; return Task.FromResult<string?>("key"); },
            In("cache"), p => Path.GetFileNameWithoutExtension(p), logger: null);
        foreach (var i in Enumerable.Range(0, 3)) WriteModel($"lora{i}.safetensors");

        foreach (var i in Enumerable.Range(0, 3))
            await resolver.ResolveAsync(In($"lora{i}.safetensors"));

        reads.Should().Be(1);

        // A new pass re-reads, so a key changed in Settings meanwhile is picked up.
        // (Uses a fresh file: lora0's hash is now in the disk cache and never reaches the API.)
        WriteModel("lora3.safetensors");
        resolver.ResetPerPassCaches();
        await resolver.ResolveAsync(In("lora3.safetensors"));
        reads.Should().Be(2);
    }

    [Fact]
    public async Task ApiKeyProviderFailureDegradesToAnAnonymousLookup()
    {
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 9, BaseModel = "Pony" });
        var resolver = new SorterMetadataResolver(_client.Object,
            () => throw new InvalidOperationException("settings db down"),
            In("cache"), _ => "abc123", logger: null);

        var meta = await resolver.ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("Pony");
    }

    [Fact]
    public async Task UnreadableFileResolvesAsUnknownInsteadOfThrowing()
    {
        // Review 2.3: _hashFile → File.OpenRead throws for a .safetensors held open by a
        // running backend, and it escaped the "never throws" contract, killing the pass.
        var model = WriteModel();
        var resolver = new SorterMetadataResolver(_client.Object, () => Task.FromResult<string?>(null),
            In("cache"), _ => throw new IOException("in use"), logger: null);

        var meta = await resolver.ResolveAsync(model);

        meta.Should().Be(new ResolvedLoraMetadata(null, null, string.Empty));
        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CivitaiShapeChangeResolvesAsUnknownInsteadOfThrowing()
    {
        // CivitaiClient.DeserializeOrThrow raises JsonException on a response-shape change;
        // this repo has been bitten by that twice.
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new JsonException("allowCommercialUse changed shape"));

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.Should().Be(new ResolvedLoraMetadata(null, null, "abc123"));
    }

    [Fact]
    public async Task SidecarTagsAreExposedForCategoryInference()
    {
        // Review 4.2: a fully resolved LoRA in a browsed folder was still forced into
        // Unknown\ because nothing carried its tags out of the sidecar.
        var model = WriteModel();
        File.WriteAllText(In("lora.civitai.info"),
            """{"id": 5, "baseModel": "Pony", "model": {"name": "x", "tags": ["anime", "style"]}}""");

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.Tags.Should().BeEquivalentTo(["anime", "style"]);
        SorterCategoryResolver.InferFolderName(meta.Tags).Should().Be("Style");
    }

    [Fact]
    public async Task TopLevelTagArrayAndTagObjectsAreBothAccepted()
    {
        var model = WriteModel();
        File.WriteAllText(In("lora.civitai.info"),
            """{"id": 6, "baseModel": "Pony", "tags": ["concept", {"name": "extra"}, 42]}""");

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.Tags.Should().BeEquivalentTo(["concept", "extra"]);
    }

    [Fact]
    public async Task SidecarWithoutTagsYieldsAnEmptyTagList()
    {
        var model = WriteModel();
        File.WriteAllText(In("lora.civitai.info"), """{"id": 7, "baseModel": "Pony"}""");

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task AUserCancelDuringTheApiCallUnwindsInsteadOfLoggingANetworkFailure()
    {
        // TaskCanceledException derives from OperationCanceledException — the type the pass
        // deliberately does not swallow. Swallowing it here logged the user's Cancel as a Civitai
        // failure, returned unresolved metadata for a file that was never looked up, and left the
        // cancel to be noticed one file later.
        var model = WriteModel();
        using var cts = new CancellationTokenSource();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("A task was canceled."));
        cts.Cancel();

        var act = () => Resolver(_client.Object).ResolveAsync(model, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AnHttpTimeoutIsStillTreatedAsAnUnresolvedFile()
    {
        // Same exception type, nobody cancelled: HttpClient reports its own timeout this way, and
        // one slow request must cost one file, not the pass.
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));

        var meta = await Resolver(_client.Object).ResolveAsync(model, CancellationToken.None);

        meta.BaseModelRaw.Should().BeNull();
        meta.Sha256.Should().Be("abc123");
    }

    [Fact]
    public async Task MalformedCacheFileIsDeletedAndResolutionFallsThrough()
    {
        var model = WriteModel();
        Directory.CreateDirectory(In("cache"));
        File.WriteAllText(In(Path.Combine("cache", "abc123.json")), "garbage");
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 3, BaseModel = "SDXL 1.0" });

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("SDXL 1.0");
    }

    [Fact]
    public async Task ApiResolvedFileGetsItsCategoryFromTheOwningModelsTags()
    {
        // The headline "browse any folder" case: LoRAs downloaded outside DiffusionNexus have no
        // sidecar, so the by-hash API is the only source — and it returns a model VERSION, which
        // carries no tags. Without the follow-up /models/{id} call every such file landed
        // category-less in <Target>\<BaseModel>\.
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 777, ModelId = 900, BaseModel = "SDXL 1.0" });
        _client.Setup(c => c.GetModelAsync(900, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModel { Id = 900, Tags = ["character", "anime"] });
        var resolver = Resolver(_client.Object);

        var meta = await resolver.ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("SDXL 1.0");
        meta.Tags.Should().BeEquivalentTo(["character", "anime"]);

        // And the tags come back off the per-hash cache, without either call being repeated.
        var second = await resolver.ResolveAsync(model);
        second.Tags.Should().BeEquivalentTo(["character", "anime"]);
        _client.Verify(c => c.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AFailedTagLookupKeepsTheVersionResultAndIsRetriedNextPass()
    {
        // A transient failure of the second call must not cost the base model / version id, and
        // must not be cached as "this model has no tags" — that would make the file permanently
        // category-less, which is exactly the stickiness the per-hash cache is prone to.
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 777, ModelId = 900, BaseModel = "SDXL 1.0" });
        _client.SetupSequence(c => c.GetModelAsync(900, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"))
            .ReturnsAsync(new CivitaiModel { Id = 900, Tags = ["style"] });
        var resolver = Resolver(_client.Object);

        var first = await resolver.ResolveAsync(model);

        first.BaseModelRaw.Should().Be("SDXL 1.0");
        first.CivitaiVersionId.Should().Be(777);
        first.Tags.Should().BeEmpty();

        // Next pass — within the same pass the failure is memoized, so the retry is deliberately
        // not re-attempted per file (that would cost one timeout per file of the same model).
        resolver.ResetPerPassCaches();
        var second = await resolver.ResolveAsync(model);

        second.Tags.Should().BeEquivalentTo(["style"]);
    }

    [Fact]
    public async Task TagsAreLookedUpOncePerModelPerPassNotOncePerFile()
    {
        // Two sibling versions of the same model in one folder: the by-hash call is per file (they
        // are different files), but the follow-up /models/{id} call answers for both. Without the
        // memo a folder of twenty versions of one model paid twenty identical round-trips.
        var a = WriteModel("a.safetensors");
        var b = WriteModel("b.safetensors");
        _client.Setup(c => c.GetModelVersionByHashAsync("sha-a", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 1, ModelId = 900, BaseModel = "SDXL 1.0" });
        _client.Setup(c => c.GetModelVersionByHashAsync("sha-b", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 2, ModelId = 900, BaseModel = "SDXL 1.0" });
        _client.Setup(c => c.GetModelAsync(900, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModel { Id = 900, Tags = ["character"] });
        var resolver = new SorterMetadataResolver(_client.Object, () => Task.FromResult<string?>(null),
            In("cache"), p => "sha-" + Path.GetFileNameWithoutExtension(p), logger: null);

        var first = await resolver.ResolveAsync(a);
        var second = await resolver.ResolveAsync(b);

        first.Tags.Should().BeEquivalentTo(["character"]);
        second.Tags.Should().BeEquivalentTo(["character"]);
        _client.Verify(c => c.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        _client.Verify(c => c.GetModelAsync(900, null, It.IsAny<CancellationToken>()), Times.Once);

        // A new pass may re-ask (tags do change upstream); the per-hash disk cache still spares it
        // here, so use a third file of the same model to show the memo itself was dropped.
        var c3 = WriteModel("c.safetensors");
        _client.Setup(c => c.GetModelVersionByHashAsync("sha-c", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 3, ModelId = 900, BaseModel = "SDXL 1.0" });
        resolver.ResetPerPassCaches();
        await resolver.ResolveAsync(c3);

        _client.Verify(c => c.GetModelAsync(900, null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AModelWithNoTagsIsCachedAsResolvedAndNotReFetched()
    {
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 777, ModelId = 900, BaseModel = "SDXL 1.0" });
        _client.Setup(c => c.GetModelAsync(900, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModel { Id = 900, Tags = [] });
        var resolver = Resolver(_client.Object);

        await resolver.ResolveAsync(model);
        var second = await resolver.ResolveAsync(model);

        second.Tags.Should().BeEmpty();
        _client.Verify(c => c.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(c => c.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- Identity fallback: the header and filename rungs (added with #524) ----

    /// <summary>Wraps a JSON body in braces — keeps the sidecar/cache fixtures below free of
    /// raw-string brace-counting.</summary>
    private static string Sidecar(string body) => "{" + body + "}";

    private string WriteSafetensors(string name, string headerJson)
    {
        var path = In(name);
        File.WriteAllBytes(path, SafetensorsFixture.Safetensors(headerJson));
        return path;
    }

    /// <summary>
    /// The sidecar/cache/by-hash chain answers for files Civitai knows about. For everything else —
    /// self-trained LoRAs, anything trained or fetched outside Civitai — the file itself is still
    /// readable, and the two rungs the DB-side identity chain shipped in #524
    /// (<c>SafetensorsHeaderReader</c> + <c>BaseModelHeaderMap</c>, then
    /// <c>FilenameBaseModelHeuristic</c>) answer where this resolver used to give up and dump the
    /// file into <c>Unknown\</c>.
    /// </summary>
    [Fact]
    public async Task HeaderIdentifiesAFileCivitaiDoesNotKnow()
    {
        // The name says nothing on purpose, so the header is the only thing that can have answered.
        var model = WriteSafetensors("unknown_thing.safetensors",
            SafetensorsFixture.Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base")));
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CivitaiModelVersion?)null);

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("SDXL 1.0");
        meta.Sha256.Should().Be("abc123", "the fallback fills the base model without discarding what the chain did resolve");
        meta.CivitaiVersionId.Should().BeNull("a header cannot supply a Civitai version id");
    }

    /// <summary>
    /// Not a safetensors byte layout at all, so the header rung returns null and the name is all
    /// that is left — the "MyChar_Pony_v2" case the design calls out by name. It is OFFERED, not
    /// applied: the planner turns BaseModelRaw into a physical move, and the sorter's "sort by name"
    /// option is what folds this in once the user has seen how many files it would resolve.
    /// </summary>
    [Fact]
    public async Task AFilenameGuessIsOfferedRatherThanApplied()
    {
        var model = WriteModel("MyChar_Pony_v2.safetensors");
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CivitaiModelVersion?)null);

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.NameGuess.Should().Be("Pony");
        meta.BaseModelRaw.Should().BeNull("a name is a guess about a file, not a reading of it");
    }

    /// <summary>
    /// The header read the actual weights; the name is only a guess about them. Same precedence as
    /// the DB-side chain in <c>IdentifyModelStep</c>.
    /// </summary>
    [Fact]
    public async Task TheHeaderBeatsTheFilenameWhenBothCouldAnswer()
    {
        var model = WriteSafetensors("MyChar_Pony_v2.safetensors",
            SafetensorsFixture.Meta(("modelspec.architecture", "stable-diffusion-v1")));
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CivitaiModelVersion?)null);

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("SD 1.5");
        meta.NameGuess.Should().BeNull("the header answered, so there is nothing left to offer");
    }

    /// <summary>Civitai is rung 1 and stays rung 1: this file's name says Pony and its header says
    /// SDXL, and neither gets to overrule what the API actually answered.</summary>
    [Fact]
    public async Task ACivitaiAnswerIsNeverOverriddenByTheFileItself()
    {
        var model = WriteSafetensors("MyChar_Pony_v2.safetensors",
            SafetensorsFixture.Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base")));
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 777, BaseModel = "Flux.1 D" });

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("Flux.1 D");
    }

    /// <summary>Same for a sidecar, which wins outright without hashing or calling anything.</summary>
    [Fact]
    public async Task ASidecarAnswerIsNeverOverriddenByTheFileItself()
    {
        var model = WriteSafetensors("MyChar_Pony_v2.safetensors",
            SafetensorsFixture.Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base")));
        File.WriteAllText(In("MyChar_Pony_v2.civitai.info"), """{"id": 555, "baseModel": "Illustrious"}""");

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("Illustrious");
        _client.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The per-hash cache means <i>what Civitai said for this hash</i>. A guess must never be
    /// written into it, or the cache would start reporting a filename match as an API answer.
    /// </summary>
    [Fact]
    public async Task AGuessIsNeverWrittenIntoTheByHashCache()
    {
        var model = WriteSafetensors("unknown_thing.safetensors",
            SafetensorsFixture.Meta(("modelspec.architecture", "stable-diffusion-v1")));
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CivitaiModelVersion?)null);

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("SD 1.5", "a 404 is an answer, so the file itself may speak");

        using var cached = JsonDocument.Parse(File.ReadAllText(In(Path.Combine("cache", "abc123.json"))));
        cached.RootElement.GetProperty("baseModel").ValueKind.Should().Be(JsonValueKind.Null,
            "the cache records what Civitai said, not what was guessed from the file");
    }

    /// <summary>
    /// <c>CivitaiClient.GetAsync</c> returns null ONLY for a 404. A rate limit that survived its
    /// three retries, an outage, a non-transient 4xx/5xx and a response-shape change all throw, and
    /// used to be indistinguishable from "Civitai does not know this file". The sorter acts on this
    /// value by moving bytes, and "the next pass retries" does not reach the file system: in move
    /// mode the file has already left the source folder by then. This path is also serial and
    /// unpaced, so one 429 tends to mean every file after it.
    /// </summary>
    [Fact]
    public async Task AFailedLookupIsNotALicenceToGuess()
    {
        var model = WriteSafetensors("MyChar_Pony_v2.safetensors",
            SafetensorsFixture.Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base")));
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited after 3 retries"));

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().BeNull(
            "both rungs would have answered, but nothing authoritative was actually asked");
        meta.NameGuess.Should().BeNull(
            "not even as an offer — the user opting into name sorting did not opt into acting on an outage");
    }

    /// <summary>
    /// A file that will not hash reaches the planner with an empty <c>Sha256</c> — the one value its
    /// "identical content is already there, skip it" guard needs, with no second chance if the lock
    /// outlives the pass. The header opens <c>FileShare.ReadWrite</c> and so reads happily off a file
    /// a trainer holds mid-checkpoint, which is exactly when the hasher's <c>FileShare.Read</c>
    /// fails: filing it into the populated folder where its twin lives would turn a skip into a
    /// renamed duplicate.
    /// </summary>
    [Fact]
    public async Task AFileThatCannotBeHashedIsNotGuessedIntoARealFolder()
    {
        var model = WriteSafetensors("MyChar_Pony_v2.safetensors",
            SafetensorsFixture.Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base")));
        var resolver = new SorterMetadataResolver(_client.Object, () => Task.FromResult<string?>(null),
            In("cache"), _ => throw new IOException("held open by a trainer"), logger: null);

        var meta = await resolver.ResolveAsync(model);

        meta.BaseModelRaw.Should().BeNull();
        meta.NameGuess.Should().BeNull();
        meta.Sha256.Should().BeEmpty();
    }

    /// <summary>
    /// <c>"???"</c> is what <c>ModelFileSyncService</c> stamps on a locally-discovered model, and it
    /// reaches here through a <c>.civitai.info</c> written from such a row.
    /// <c>LoraPathBuilder.IsPlaceholderBaseModel</c> — the predicate that picks the Unknown
    /// folder — treats it as no answer, so the gate deciding whether to ask the file has to agree:
    /// otherwise the file is "resolved" enough to skip its own header yet still lands in Unknown.
    /// </summary>
    [Fact]
    public async Task APlaceholderBaseModelStillReachesTheFilesOwnRungs()
    {
        var model = WriteSafetensors("unknown_thing.safetensors",
            SafetensorsFixture.Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base")));
        File.WriteAllText(In("unknown_thing.civitai.info"), Sidecar(@"""id"": 555, ""baseModel"": ""???"""));

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("SDXL 1.0");
        meta.CivitaiVersionId.Should().Be(555, "the sidecar's other fields survive");
    }

    /// <summary>
    /// <c>TryReadSidecar</c> reports a hit when EITHER <c>baseModel</c> or <c>id</c> is present, so a
    /// sidecar carrying only an id used to end the chain with nothing to sort on. It now falls
    /// through to the file — a real change to "a sidecar wins outright", pinned here. The empty
    /// <c>Sha256</c> is safe in this case and not in the could-not-hash one: nothing tried to hash
    /// this file, so the planner's lazy hash still succeeds.
    /// </summary>
    [Fact]
    public async Task ASidecarCarryingOnlyAnIdFallsThroughToTheFile()
    {
        var model = WriteSafetensors("unknown_thing.safetensors",
            SafetensorsFixture.Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base")));
        File.WriteAllText(In("unknown_thing.civitai.info"), Sidecar(@"""id"": 555"));

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("SDXL 1.0");
        meta.CivitaiVersionId.Should().Be(555);
        meta.Sha256.Should().BeEmpty("the sidecar branch never hashes");
        _client.VerifyNoOtherCalls();
    }

    /// <summary>A base model already on record blocks the guess even when it came from the cache
    /// rather than a live call — the cache is Civitai's answer, just stored.</summary>
    [Fact]
    public async Task ACachedBaseModelBlocksTheGuess()
    {
        var model = WriteSafetensors("MyChar_Pony_v2.safetensors",
            SafetensorsFixture.Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base")));
        Directory.CreateDirectory(In("cache"));
        File.WriteAllText(In(Path.Combine("cache", "abc123.json")),
            Sidecar(@"""baseModel"": ""Flux.1 D"", ""versionId"": 777, ""tags"": []"));

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("Flux.1 D");
        _client.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A cache entry whose tag list never resolved is deliberately re-queried for the tags — but the
    /// base model it already carries is an answer Civitai gave once, and a refresh that fails must
    /// not cost it. Discarding it also downgraded the verdict to <c>CouldNotAsk</c>, which stopped
    /// the file's own header from putting the value back, so a rate limit turned a library of
    /// tag-less entries into a mass move into <c>Unknown\</c>.
    /// </summary>
    [Fact]
    public async Task AFailedTagRefreshKeepsTheBaseModelTheCacheAlreadyHad()
    {
        var model = WriteModel();
        Directory.CreateDirectory(In("cache"));
        File.WriteAllText(In(Path.Combine("cache", "abc123.json")),
            Sidecar(@"""baseModel"": ""Illustrious"", ""versionId"": 777, ""tags"": null"));
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("429 Too Many Requests"));

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("Illustrious");
        meta.CivitaiVersionId.Should().Be(777);
    }

    /// <summary>
    /// The other half of the same rule: an entry that recorded a blank base model recorded an
    /// <i>answer</i>, so a failed refresh of its tags still leaves the file's own rungs licensed.
    /// </summary>
    [Fact]
    public async Task AFailedTagRefreshOnABlankCachedAnswerStillLetsTheFileSpeak()
    {
        var model = WriteSafetensors("unknown_thing.safetensors",
            SafetensorsFixture.Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base")));
        Directory.CreateDirectory(In("cache"));
        File.WriteAllText(In(Path.Combine("cache", "abc123.json")),
            Sidecar(@"""baseModel"": null, ""versionId"": 777, ""tags"": null"));
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("503"));

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("SDXL 1.0", "the header answers what the recorded answer left blank");
    }

    /// <summary>The header read is the only cancellable step in the fallback, and the doc promises
    /// cancellation unwinds the pass instead of being reported as one more unreadable file.</summary>
    [Fact]
    public async Task CancellationDuringTheHeaderReadPropagates()
    {
        var model = WriteSafetensors("unknown_thing.safetensors",
            SafetensorsFixture.Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // No client, so the chain reaches the fallback with no network step to swallow the token.
        var act = () => Resolver(client: null).ResolveAsync(model, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
