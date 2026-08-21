using System.Net.Http;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Service.Services.Sync;
using DiffusionNexus.Service.Services.Sync.Steps;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Sync.Service.Steps;

/// <summary>
/// Covers <see cref="IdentifyModelStep"/> — the sync step that replaces the ViewModel's
/// Phase 1 / 1b / per-tile metadata copy (#521 WP2): candidate selection under the retry
/// policy, and the outcome stamping every execution path must leave behind.
/// </summary>
public sealed class IdentifyModelStepTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly DirectoryInfo _tempDir;

    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    public IdentifyModelStepTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();

        _tempDir = Directory.CreateTempSubdirectory("dn-identify-");
    }

    private IServiceScope NewScope() => _serviceProvider.CreateScope();

    private IServiceScopeFactory Scopes => _serviceProvider.GetRequiredService<IServiceScopeFactory>();

    /// <summary>Creates a real (tiny, non-empty) model file on disk and returns its full path.</summary>
    private string NewModelFile(string fileName, byte[]? content = null)
    {
        var path = Path.Combine(_tempDir.FullName, fileName);
        File.WriteAllBytes(path, content ?? [0x01, 0x02, 0x03]);
        return path;
    }

    /// <summary>A path inside the temp dir that deliberately does not exist on disk.</summary>
    private string MissingModelFile(string fileName) => Path.Combine(_tempDir.FullName, fileName);

    /// <summary>
    /// Seeds a LoRA-family model with one version and one primary local file, plus (optionally)
    /// the sync state row a previous run would have left. Returns (modelId, fileId).
    /// </summary>
    private async Task<(int ModelId, int FileId)> SeedAsync(
        string name,
        string localPath,
        string? sha256 = null,
        SyncOutcome outcome = SyncOutcome.None,
        DateTimeOffset? checkedAt = null,
        int attempts = 0,
        string? sidecarSignature = null,
        bool withState = false)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = new Model
        {
            Name = name,
            Type = ModelType.LORA,
            Source = DataSource.LocalFile,
        };
        var version = new ModelVersion { Name = "v1", BaseModelRaw = "???" };
        version.Files.Add(new ModelFile
        {
            FileName = Path.GetFileName(localPath),
            LocalPath = localPath,
            IsLocalFileValid = true,
            IsPrimary = true,
            HashSHA256 = sha256,
        });
        model.Versions.Add(version);

        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        if (withState)
        {
            var state = await uow.SyncStates.GetOrCreateAsync(model.Id);
            state.MetadataOutcome = outcome;
            state.MetadataCheckedAt = checkedAt;
            state.MetadataAttempts = attempts;
            state.SidecarSignature = sidecarSignature;
            state.UpdatedAt = checkedAt ?? Now;
            await uow.SaveChangesAsync();
        }

        return (model.Id, version.Files.First().Id);
    }

    private async Task<ModelSyncState?> ReadStateAsync(int modelId)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await uow.SyncStates.GetByModelIdAsync(modelId);
    }

    private static CivitaiModelVersion NewCivitaiVersion(int id = 700, int modelId = 77) => new()
    {
        Id = id,
        ModelId = modelId,
        Name = "civitai v1",
        BaseModel = "SDXL 1.0",
        TrainedWords = ["x"],
        Images = [],
        Files =
        [
            new CivitaiModelFile
            {
                Id = 900,
                Primary = true,
                // A deliberately different hash: the locally computed one must win (it is the truth
                // about the bytes on disk), so the applier's `??=` must find the field already set.
                Hashes = new CivitaiFileHashes { SHA256 = "DEADBEEF" },
            },
        ],
    };

    private static CivitaiModel NewCivitaiModel(CivitaiModelVersion version, int id = 77) => new()
    {
        Id = id,
        Name = "Civitai Name",
        Tags = [],
        ModelVersions = [version],
    };

    /// <summary>Builds the step over a client mock configured by <paramref name="configure"/>.</summary>
    private IdentifyModelStep NewStep(Action<Mock<ICivitaiClient>> configure)
    {
        var client = new Mock<ICivitaiClient>();
        configure(client);
        return new IdentifyModelStep(
            Scopes,
            client.Object,
            new CivitaiMetadataApplier(client.Object),
            new SidecarMetadataApplier());
    }

    /// <summary>A step whose hash lookup always 404s (returns null).</summary>
    private IdentifyModelStep NewNotFoundStep() => NewStep(c => c
        .Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((CivitaiModelVersion?)null));

    private static SyncOptions Options(bool force = false) =>
        new(new HashSet<SyncStepKind> { SyncStepKind.IdentifyModel }, ForceIdentify: force);

    [Fact]
    public async Task Select_AppliesRetryPolicyAndSkipsMissingFiles()
    {
        // Never checked → due (no state row at all).
        var fresh = await SeedAsync("fresh", NewModelFile("fresh.safetensors"));
        // Matched → never re-checked.
        var matched = await SeedAsync("matched", NewModelFile("matched.safetensors"),
            outcome: SyncOutcome.Matched, checkedAt: Now.AddDays(-400), withState: true);
        // NotIdentified 31 days ago → past the 30-day window → due.
        var stale = await SeedAsync("stale", NewModelFile("stale.safetensors"),
            outcome: SyncOutcome.NotIdentified, checkedAt: Now.AddDays(-31), withState: true);
        // NotIdentified yesterday → inside the window → not due.
        var recent = await SeedAsync("recent", NewModelFile("recent.safetensors"),
            outcome: SyncOutcome.NotIdentified, checkedAt: Now.AddDays(-1), withState: true);
        // Never checked but the file is gone from disk → skipped (a network call cannot help).
        var gone = await SeedAsync("gone", MissingModelFile("gone.safetensors"));

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);

        items.Select(i => i.ModelId).Should().BeEquivalentTo([fresh.ModelId, stale.ModelId]);
        items.Should().NotContain(i => i.ModelId == matched.ModelId);
        items.Should().NotContain(i => i.ModelId == recent.ModelId);
        items.Should().NotContain(i => i.ModelId == gone.ModelId);
        items.Should().OnlyContain(i => i.Payload is IdentifyCandidate);
    }

    [Fact]
    public async Task Select_ForceIdentifyOverridesTheWindowButNotAMissingFile()
    {
        var matched = await SeedAsync("matched", NewModelFile("f-matched.safetensors"),
            outcome: SyncOutcome.Matched, checkedAt: Now.AddDays(-1), withState: true);
        var gone = await SeedAsync("gone", MissingModelFile("f-gone.safetensors"));

        var items = await NewNotFoundStep().SelectAsync(SyncScope.Library, Options(force: true), Now, CancellationToken.None);

        items.Select(i => i.ModelId).Should().Contain(matched.ModelId);
        items.Select(i => i.ModelId).Should().NotContain(gone.ModelId);
    }

    [Fact]
    public async Task Select_ChangedSidecarMakesCandidateDueImmediately()
    {
        var path = NewModelFile("sidecar.safetensors");
        // Checked yesterday → the 30-day window says "not due"…
        var candidate = await SeedAsync("sidecar", path,
            outcome: SyncOutcome.NotIdentified, checkedAt: Now.AddDays(-1),
            sidecarSignature: "stale|0|0", withState: true);

        // …but a sidecar appeared since, so its signature no longer matches the stored one.
        await File.WriteAllTextAsync(Path.Combine(_tempDir.FullName, "sidecar.civitai.info"), "{}");

        var items = await NewNotFoundStep().SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);

        items.Select(i => i.ModelId).Should().Contain(candidate.ModelId);
    }

    [Fact]
    public async Task Select_UnchangedSidecarSignatureStaysInsideTheWindow()
    {
        var path = NewModelFile("unchanged.safetensors");
        await File.WriteAllTextAsync(Path.Combine(_tempDir.FullName, "unchanged.civitai.info"), "{}");
        var signature = SidecarMetadataApplier.Find(path).Signature;

        var candidate = await SeedAsync("unchanged", path,
            outcome: SyncOutcome.Sidecar, checkedAt: Now.AddDays(-1),
            sidecarSignature: signature, withState: true);

        var items = await NewNotFoundStep().SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);

        items.Select(i => i.ModelId).Should().NotContain(candidate.ModelId);
    }

    [Fact]
    public async Task Execute_MatchedStampsMatchedAndAppliesMetadata()
    {
        var path = NewModelFile("match.safetensors");
        var (modelId, fileId) = await SeedAsync("match", path);
        var expectedHash = FileHasher.Sha256Upper(path);

        var civVersion = NewCivitaiVersion();
        string? lookedUpHash = null;
        var step = NewStep(c =>
        {
            c.Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string?, CancellationToken>((h, _, _) => lookedUpHash = h)
                .ReturnsAsync(civVersion);
            c.Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(NewCivitaiModel(civVersion));
        });

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.FailureReason.Should().BeNull();
        lookedUpHash.Should().Be(expectedHash);

        var state = await ReadStateAsync(modelId);
        state.Should().NotBeNull();
        state!.MetadataOutcome.Should().Be(SyncOutcome.Matched);
        state.MetadataCheckedAt.Should().NotBeNull();
        state.MetadataAttempts.Should().Be(0);

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.CivitaiId.Should().Be(77);

        var file = await uow.ModelFiles.GetByIdAsync(fileId);
        file!.HashSHA256.Should().Be(expectedHash);
    }

    [Fact]
    public async Task Execute_ReusesAStoredValidHashInsteadOfRehashing()
    {
        var path = NewModelFile("stored.safetensors");
        var stored = new string('a', 64);   // 64-hex, lowercase → reused, normalized to upper
        var (modelId, _) = await SeedAsync("stored", path, sha256: stored);

        string? lookedUpHash = null;
        var step = NewStep(c => c
            .Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, CancellationToken>((h, _, _) => lookedUpHash = h)
            .ReturnsAsync((CivitaiModelVersion?)null));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        lookedUpHash.Should().Be(stored.ToUpperInvariant());
        lookedUpHash.Should().NotBe(FileHasher.Sha256Upper(path));
    }

    [Fact]
    public async Task Execute_404WithSidecarStampsSidecar()
    {
        var path = NewModelFile("has-sidecar.safetensors");
        var (modelId, _) = await SeedAsync("has-sidecar", path);

        var sidecar = """
        {"id":700,"modelId":77,"baseModel":"Pony","trainedWords":["x"],
         "model":{"name":"N","nsfw":false},
         "files":[{"name":"has-sidecar.safetensors","primary":true,"hashes":{"SHA256":"ABC"}}],
         "images":[]}
        """;
        await File.WriteAllTextAsync(Path.Combine(_tempDir.FullName, "has-sidecar.civitai.info"), sidecar);
        var expectedSignature = SidecarMetadataApplier.Find(path).Signature;

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();

        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.Sidecar);
        state.MetadataCheckedAt.Should().NotBeNull();
        state.MetadataAttempts.Should().Be(0);
        state.SidecarSignature.Should().Be(expectedSignature);

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.Versions.Single().BaseModelRaw.Should().Be("Pony");
    }

    [Fact]
    public async Task Execute_404WithoutSidecarStampsNotIdentified()
    {
        var path = NewModelFile("no-sidecar.safetensors");
        var (modelId, _) = await SeedAsync("no-sidecar", path);

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();

        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.NotIdentified);
        state.MetadataCheckedAt.Should().NotBeNull();
        state.MetadataAttempts.Should().Be(0);
        // "" is the signature of "no sidecar exists" — stored so the next run can notice one appearing.
        state.SidecarSignature.Should().Be(string.Empty);
    }

    [Fact]
    public async Task Execute_HttpErrorStampsErrorAndIncrementsAttempts()
    {
        var path = NewModelFile("boom.safetensors");
        var (modelId, _) = await SeedAsync("boom", path);

        var step = NewStep(c => c
            .Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset")));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Skipped.Should().BeFalse();
        result.FailureReason.Should().Contain("connection reset");

        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.Error);
        state.MetadataAttempts.Should().Be(1);
        state.LastError.Should().Contain("connection reset");
        state.MetadataCheckedAt.Should().NotBeNull();

        // The error is retried, but only after the short window — not on the very next run.
        var policy = SyncRetryPolicy.Default;
        policy.IsIdentifyDue(SyncOutcome.Error, state.MetadataCheckedAt, state.MetadataAttempts, Now, force: false)
            .Should().BeFalse();
        policy.IsIdentifyDue(SyncOutcome.Error, state.MetadataCheckedAt, state.MetadataAttempts, Now.AddDays(2), force: false)
            .Should().BeTrue();
        // …and gives up after MaxErrorAttempts.
        policy.IsIdentifyDue(SyncOutcome.Error, state.MetadataCheckedAt, policy.MaxErrorAttempts, Now.AddDays(2), force: false)
            .Should().BeFalse();
    }

    [Fact]
    public async Task Execute_HttpClientTimeoutIsAnErrorNotACancellation()
    {
        var path = NewModelFile("timeout.safetensors");
        var (modelId, _) = await SeedAsync("timeout", path);

        // HttpClient surfaces its own timeout as TaskCanceledException with the caller's token NOT cancelled.
        var step = NewStep(c => c
            .Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.Error);
        state.MetadataAttempts.Should().Be(1);
    }

    [Fact]
    public async Task Execute_TruncatesLastErrorTo500Chars()
    {
        var path = NewModelFile("long-error.safetensors");
        var (modelId, _) = await SeedAsync("long-error", path);

        var step = NewStep(c => c
            .Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(new string('x', 2000))));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        var state = await ReadStateAsync(modelId);
        state!.LastError.Should().HaveLength(500);
    }

    [Fact]
    public async Task Execute_CancellationDoesNotStamp()
    {
        var path = NewModelFile("cancel.safetensors");
        var (modelId, _) = await SeedAsync("cancel", path);

        using var cts = new CancellationTokenSource();
        var step = NewStep(c => c
            .Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ThrowsAsync(new OperationCanceledException()));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var item = items.Single(i => i.ModelId == modelId);

        var act = () => step.ExecuteOneAsync(item, apiKey: null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        // No state row was written: a cancelled item is untouched work, not a failed attempt.
        (await ReadStateAsync(modelId)).Should().BeNull();
    }

    [Fact]
    public void Step_DescribesItselfForThePlanView()
    {
        var step = NewNotFoundStep();

        step.Kind.Should().Be(SyncStepKind.IdentifyModel);
        step.Description.Should().NotBeNullOrWhiteSpace();
        step.EstimatedPerItem.Should().Be(TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        try { _tempDir.Delete(recursive: true); } catch { /* best effort */ }
    }
}
