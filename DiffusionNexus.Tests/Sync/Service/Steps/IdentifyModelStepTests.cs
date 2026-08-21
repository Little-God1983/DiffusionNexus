using System.Text.Json;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
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

        _tempDir = Directory.CreateTempSubdirectory("dn-identify-");

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));

        // The step scopes the selection to the enabled LoRA sources, which for these tests is the
        // temp folder every seeded file lives in.
        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.GetEnabledLoraSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { _tempDir.FullName });
        services.AddTransient(_ => settings.Object);

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();
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
        bool withState = false,
        int? civitaiId = null)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = new Model
        {
            Name = name,
            Type = ModelType.LORA,
            Source = civitaiId is null ? DataSource.LocalFile : DataSource.CivitaiApi,
            CivitaiId = civitaiId,
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

    /// <summary>
    /// C1. The per-tile "Download Metadata" button forces this step over one model that has, in
    /// the overwhelming majority of cases, already been matched. Selecting only models without a
    /// Civitai id meant the forced run planned nothing, reported nothing succeeded, and the detail
    /// view told the user "No metadata found on Civitai for this file." about a file Civitai knows
    /// perfectly well.
    /// </summary>
    [Fact]
    public async Task Select_ForceIdentifyIncludesMatchedModel()
    {
        var path = NewModelFile("already-matched.safetensors");
        var matched = await SeedAsync("already-matched", path, civitaiId: 77,
            outcome: SyncOutcome.Matched, checkedAt: Now.AddDays(-1), withState: true);

        var forced = await NewNotFoundStep().SelectAsync(SyncScope.ForModels(matched.ModelId), Options(force: true), Now, CancellationToken.None);
        forced.Select(i => i.ModelId).Should().Contain(matched.ModelId);

        // The bulk run still leaves it alone: there is nothing to identify about a matched model.
        var bulk = await NewNotFoundStep().SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        bulk.Select(i => i.ModelId).Should().NotContain(matched.ModelId);
    }

    /// <summary>
    /// C1, the execution half: a forced re-fetch of an already-matched model must write the fresh
    /// response over the stored data and leave the model matched — not fail, and not demote it.
    /// </summary>
    [Fact]
    public async Task Execute_ForceOnMatchedModelReappliesMetadata()
    {
        var path = NewModelFile("refetch.safetensors");
        var (modelId, _) = await SeedAsync("refetch", path, civitaiId: 77,
            outcome: SyncOutcome.Matched, checkedAt: Now.AddDays(-1), withState: true);

        var civVersion = NewCivitaiVersion();
        var step = NewStep(c =>
        {
            c.Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(civVersion);
            c.Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(NewCivitaiModel(civVersion));
        });

        var items = await step.SelectAsync(SyncScope.ForModels(modelId), Options(force: true), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FailureReason.Should().BeNull();

        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.Matched);
        state.MetadataAttempts.Should().Be(0);
        state.LastError.Should().BeNull();

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.CivitaiId.Should().Be(77, "the id it already owned is still its own");
        saved.Name.Should().Be("Civitai Name", "the fresh response was actually written, not just re-stamped");
        saved.Versions.Single().BaseModelRaw.Should().Be("SDXL 1.0");
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

    /// <summary>
    /// R5. A row derived from a legacy model carries no signature at all, and "never recorded" is
    /// not "changed": treating null as an empty signature made every legacy model that happens to
    /// own a sidecar due on the first run, which is exactly the herd R1 set out to prevent (the
    /// live dry run still planned 200 identify items against ~83 genuinely-unchecked models).
    /// </summary>
    [Fact]
    public async Task Select_NullStoredSignatureIsNotAChangeTrigger()
    {
        var path = NewModelFile("derived.safetensors");
        await File.WriteAllTextAsync(Path.Combine(_tempDir.FullName, "derived.civitai.info"), "{}");

        // Exactly what SyncStateDeriver produces for a legacy unidentified model: stamped now,
        // never attempted, no signature ever recorded.
        var candidate = await SeedAsync("derived", path,
            outcome: SyncOutcome.NotIdentified, checkedAt: Now,
            sidecarSignature: null, withState: true);

        var items = await NewNotFoundStep().SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);

        items.Select(i => i.ModelId).Should().NotContain(candidate.ModelId);

        // The user asking for it explicitly still gets it, and so does the regular 30-day check.
        var forced = await NewNotFoundStep().SelectAsync(SyncScope.Library, Options(force: true), Now, CancellationToken.None);
        forced.Select(i => i.ModelId).Should().Contain(candidate.ModelId);

        var later = await NewNotFoundStep().SelectAsync(
            SyncScope.Library, Options(), Now.Add(SyncRetryPolicy.Default.NotIdentifiedRetryAfter), CancellationToken.None);
        later.Select(i => i.ModelId).Should().Contain(candidate.ModelId);
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
        var (modelId, fileId) = await SeedAsync("cancel", path);

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

        // ...but the SHA256 it had to compute survives, because it is committed before the network
        // call. Re-hashing a multi-gigabyte file on the next run is the cost of getting this wrong.
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var file = await uow.ModelFiles.GetByIdAsync(fileId);
        file!.HashSHA256.Should().Be(FileHasher.Sha256Upper(path));
    }

    [Fact]
    public async Task Execute_ModelDeletedBetweenSelectAndExecuteIsSkippedWithoutStamping()
    {
        var path = NewModelFile("deleted.safetensors");
        var (modelId, _) = await SeedAsync("deleted", path);

        var civVersion = NewCivitaiVersion();
        var step = NewStep(c =>
        {
            c.Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(civVersion);
            c.Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(NewCivitaiModel(civVersion));
        });

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var item = items.Single(i => i.ModelId == modelId);

        // The user deletes the model in the UI while the run is in flight.
        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var doomed = await uow.Models.GetByIdAsync(modelId);
            uow.Models.Remove(doomed!);
            await uow.SaveChangesAsync();
        }

        var result = await step.ExecuteOneAsync(item, apiKey: null, CancellationToken.None);

        result.Skipped.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        // Stamping would Add a ModelSyncState whose PK/FK points at a row that no longer exists.
        (await ReadStateAsync(modelId)).Should().BeNull();
    }

    [Fact]
    public async Task Execute_ApplierReportingNothingAppliedStampsError()
    {
        var path = NewModelFile("empty-apply.safetensors");
        var (modelId, _) = await SeedAsync("empty-apply", path);

        // ModelId 0 => the applier never fetches the full model and never reaches its model-level
        // branch, so CivitaiId/CivitaiModelPageId stay null. Stamping that Matched would be a
        // permanent dead end: terminal for the retry policy, invisible to the tags/images steps.
        var civVersion = NewCivitaiVersion(modelId: 0);
        var step = NewStep(c => c
            .Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(civVersion));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Skipped.Should().BeFalse();
        result.FailureReason.Should().Contain("applied nothing");

        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.Error);
        state.MetadataAttempts.Should().Be(1);
        state.LastError.Should().Contain($"model {modelId}");
        state.LastError.Should().Contain($"version {civVersion.Id}");
    }

    [Fact]
    public async Task Execute_MatchedIsStillRecordedWhenADuplicateLocalCopyOwnsTheCivitaiId()
    {
        // A second local copy of the same LoRA: the applier refuses to move the (unique) CivitaiId
        // but does set CivitaiModelPageId. That is a real match and must NOT be reported as an error.
        var first = NewModelFile("dup-a.safetensors");
        var second = NewModelFile("dup-b.safetensors");
        var (ownerId, _) = await SeedAsync("dup-a", first);
        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var owner = await uow.Models.GetByIdAsync(ownerId);
            owner!.CivitaiId = 77;
            await uow.SaveChangesAsync();
        }
        var (modelId, _) = await SeedAsync("dup-b", second);

        var civVersion = NewCivitaiVersion(id: 701);
        var step = NewStep(c =>
        {
            c.Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(civVersion);
            c.Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(NewCivitaiModel(civVersion));
        });

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();

        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.Matched);

        using var readScope = NewScope();
        var readUow = readScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await readUow.Models.GetByIdAsync(modelId);
        saved!.CivitaiId.Should().BeNull();
        saved.CivitaiModelPageId.Should().Be(77);
    }

    [Fact]
    public async Task Execute_JsonExceptionStampsErrorWithoutPersistingPartialGraph()
    {
        var path = NewModelFile("bad-json.safetensors");
        var (modelId, _) = await SeedAsync("bad-json", path);

        // Civitai changed a response shape: CivitaiClient deliberately does not retry a JsonException.
        var step = NewStep(c => c
            .Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new JsonException("unexpected token")));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();

        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.Error);
        state.MetadataAttempts.Should().Be(1);

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.CivitaiId.Should().BeNull();
        saved.Name.Should().Be("bad-json");
    }

    [Fact]
    public async Task Execute_JsonExceptionFromInsideTheApplierDiscardsTheTrackedGraph()
    {
        var path = NewModelFile("bad-json-2.safetensors");
        var (modelId, _) = await SeedAsync("bad-json-2", path);

        // The hash lookup succeeds; the shape change is in the follow-up full-model call, which the
        // applier makes *after* the graph is loaded - this is the path ClearChangeTracker guards.
        var step = NewStep(c =>
        {
            c.Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(NewCivitaiVersion());
            c.Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new JsonException("unexpected token"));
        });

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        (await ReadStateAsync(modelId))!.MetadataOutcome.Should().Be(SyncOutcome.Error);

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.CivitaiId.Should().BeNull();
        saved.Versions.Single().BaseModelRaw.Should().Be("???");
        // The hash still made it to disk - it was committed before the network call.
        saved.Versions.Single().Files.Single().HashSHA256.Should().Be(FileHasher.Sha256Upper(path));
    }

    [Fact]
    public async Task Execute_MatchedAfterErrorClearsLastErrorAndAttempts()
    {
        var path = NewModelFile("recovers.safetensors");
        var (modelId, _) = await SeedAsync("recovers", path,
            outcome: SyncOutcome.Error, checkedAt: Now.AddDays(-2), attempts: 2,
            sidecarSignature: "sig-x", withState: true);

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var state = await uow.SyncStates.GetOrCreateAsync(modelId);
            state.LastError = "x";
            await uow.SaveChangesAsync();
        }

        var civVersion = NewCivitaiVersion();
        var step = NewStep(c =>
        {
            c.Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(civVersion);
            c.Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(NewCivitaiModel(civVersion));
        });

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();

        var after = await ReadStateAsync(modelId);
        after!.MetadataOutcome.Should().Be(SyncOutcome.Matched);
        after.MetadataAttempts.Should().Be(0);
        after.LastError.Should().BeNull();
        // Matched says nothing about the sidecar, so a later fallback must still see the old one.
        after.SidecarSignature.Should().Be("sig-x");
    }

    [Fact]
    public async Task Execute_ModelDeletedBeforeWork_IsSkippedOnSidecarBranch()
    {
        var path = NewModelFile("deleted-sidecar.safetensors");
        var (modelId, _) = await SeedAsync("deleted-sidecar", path);

        // 404 on Civitai: without an up-front existence check this lands on the sidecar/NotIdentified
        // branch, which stamps unconditionally — a state row whose PK/FK points at a deleted model,
        // i.e. a DbUpdateException outside the step's narrow catch filter.
        var client = new Mock<ICivitaiClient>();
        client.Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CivitaiModelVersion?)null);
        var step = new IdentifyModelStep(Scopes, client.Object,
            new CivitaiMetadataApplier(client.Object), new SidecarMetadataApplier());

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var item = items.Single(i => i.ModelId == modelId);

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var doomed = await uow.Models.GetByIdAsync(modelId);
            uow.Models.Remove(doomed!);
            await uow.SaveChangesAsync();
        }

        var result = await step.ExecuteOneAsync(item, apiKey: null, CancellationToken.None);

        result.Skipped.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        (await ReadStateAsync(modelId)).Should().BeNull();

        // The check is hoisted above the hash and the request: a deleted model costs neither.
        client.Verify(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
