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
/// Covers <see cref="FetchImagesStep"/> — the images half of the "checked and empty" fix
/// (#521 WP2). Stamping is per model while the work is per version, so the interesting
/// cases are all about the grouping: one stamp after the last version, none if any failed.
/// </summary>
public sealed class FetchImagesStepTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    public FetchImagesStepTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));

        // Candidates are scoped to the enabled LoRA sources, and every seeded file lives in the
        // system temp folder.
        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.GetEnabledLoraSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Path.GetTempPath() });
        services.AddTransient(_ => settings.Object);

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();

        Step = new FetchImagesStep(Scopes, new CivitaiMetadataApplier(Client.Object));
    }

    private IServiceScope NewScope() => _serviceProvider.CreateScope();

    private IServiceScopeFactory Scopes => _serviceProvider.GetRequiredService<IServiceScopeFactory>();

    /// <summary>
    /// Seeds a Civitai-identified model with one version per entry of
    /// <paramref name="versionCivitaiIds"/>. Returns (modelId, versionIds in the same order).
    /// </summary>
    private async Task<(int ModelId, IReadOnlyList<int> VersionIds)> SeedAsync(
        string name,
        int civitaiId,
        DateTimeOffset? imagesCheckedAt = null,
        params int[] versionCivitaiIds)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = new Model
        {
            Name = name,
            Type = ModelType.LORA,
            Source = DataSource.CivitaiApi,
            CivitaiId = civitaiId,
        };

        foreach (var versionCivitaiId in versionCivitaiIds)
        {
            var version = new ModelVersion
            {
                Name = $"v{versionCivitaiId}",
                BaseModelRaw = "SDXL 1.0",
                CivitaiId = versionCivitaiId,
            };
            version.Files.Add(new ModelFile
            {
                FileName = $"{name}-{versionCivitaiId}.safetensors",
                LocalPath = Path.Combine(Path.GetTempPath(), $"{name}-{versionCivitaiId}.safetensors"),
                IsLocalFileValid = true,
                IsPrimary = true,
            });
            model.Versions.Add(version);
        }

        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        if (imagesCheckedAt is not null)
        {
            var state = await uow.SyncStates.GetOrCreateAsync(model.Id);
            state.ImagesCheckedAt = imagesCheckedAt;
            state.UpdatedAt = imagesCheckedAt.Value;
            await uow.SaveChangesAsync();
        }

        return (model.Id, model.Versions.Select(v => v.Id).ToList());
    }

    private async Task<ModelSyncState?> ReadStateAsync(int modelId)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await uow.SyncStates.GetByModelIdAsync(modelId);
    }

    private async Task<int> CountImagesAsync(int modelId, int versionId)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var model = await uow.Models.GetByIdWithIncludesAsync(modelId);
        return model!.Versions.Single(v => v.Id == versionId).Images.Count;
    }

    private static CivitaiModelImage NewImage(long id) => new()
    {
        Id = id,
        Url = $"https://img/{id}.jpeg",
        Type = "image",
        Width = 512,
        Height = 768,
    };

    private static CivitaiModelVersion NewCivitaiVersion(int id, params CivitaiModelImage[] images) => new()
    {
        Id = id,
        ModelId = 77,
        Name = $"civitai v{id}",
        BaseModel = "SDXL 1.0",
        TrainedWords = [],
        Images = images,
        Files = [],
    };

    private FetchImagesStep NewStep(Action<Mock<ICivitaiClient>> configure)
    {
        var client = new Mock<ICivitaiClient>();
        configure(client);
        return new FetchImagesStep(Scopes, new CivitaiMetadataApplier(client.Object));
    }

    /// <summary>A step that answers every version fetch with a version carrying one image.</summary>
    private FetchImagesStep NewStep() => NewStep(c => c
        .Setup(x => x.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((int id, string? _, CancellationToken _) => NewCivitaiVersion(id, NewImage(id * 10))));

    private static SyncOptions Options(bool force = false) =>
        new(new HashSet<SyncStepKind> { SyncStepKind.FetchImages }, ForceImages: force);

    [Fact]
    public async Task Select_ReturnsOnlyNeverChecked_UnlessForced()
    {
        var fresh = await SeedAsync("fresh", civitaiId: 101, versionCivitaiIds: 701);
        var checkedAlready = await SeedAsync("checked", civitaiId: 102, imagesCheckedAt: Now.AddDays(-400), versionCivitaiIds: 702);

        var step = NewStep();

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        items.Select(i => i.ModelId).Should().BeEquivalentTo([fresh.ModelId]);

        var forced = await step.SelectAsync(SyncScope.Library, Options(force: true), Now, CancellationToken.None);
        forced.Select(i => i.ModelId).Should().BeEquivalentTo([fresh.ModelId, checkedAlready.ModelId]);
    }

    [Fact]
    public async Task Select_GroupsVersionsPerModel()
    {
        var multi = await SeedAsync("multi", civitaiId: 101, versionCivitaiIds: [701, 702, 703]);
        var single = await SeedAsync("single", civitaiId: 102, versionCivitaiIds: 704);

        var items = await NewStep().SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);

        // One item per MODEL — stamping is per model, so the versions must travel together.
        items.Should().HaveCount(2);
        items.Select(i => i.ModelId).Should().BeEquivalentTo([multi.ModelId, single.ModelId]);

        var multiItem = items.Single(i => i.ModelId == multi.ModelId);
        multiItem.Name.Should().Be("multi");
        var candidates = multiItem.Payload.Should().BeAssignableTo<IReadOnlyList<ImageCandidate>>().Subject;
        candidates.Should().HaveCount(3);
        candidates.Select(c => c.CivitaiVersionId).Should().BeEquivalentTo([701, 702, 703]);
        candidates.Select(c => c.VersionId).Should().BeEquivalentTo(multi.VersionIds);

        items.Single(i => i.ModelId == single.ModelId).Payload
            .Should().BeAssignableTo<IReadOnlyList<ImageCandidate>>().Which.Should().HaveCount(1);
    }

    [Fact]
    public async Task Execute_StampsAfterAllVersionsOfModel()
    {
        var seeded = await SeedAsync("multi", civitaiId: 101, versionCivitaiIds: [701, 702]);

        // The mock is built here rather than via NewStep() so the call count can be pinned: the
        // single stamp must be earned by every version having run, not by the loop exiting early.
        var client = new Mock<ICivitaiClient>();
        client
            .Setup(x => x.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, string? _, CancellationToken _) => NewCivitaiVersion(id, NewImage(id * 10)));
        var step = new FetchImagesStep(Scopes, new CivitaiMetadataApplier(client.Object));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Skipped.Should().BeFalse();

        client.Verify(
            c => c.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        (await CountImagesAsync(seeded.ModelId, seeded.VersionIds[0])).Should().Be(1);
        (await CountImagesAsync(seeded.ModelId, seeded.VersionIds[1])).Should().Be(1);

        // Exactly one stamp, on the model, after the last version.
        var state = await ReadStateAsync(seeded.ModelId);
        state.Should().NotBeNull();
        state!.ImagesCheckedAt.Should().NotBeNull();

        var second = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        second.Should().BeEmpty();
    }

    /// <summary>
    /// One image item is one <i>model</i> but N Civitai calls, one per version. Pacing itself is
    /// the gateway's job now (verified in <c>CivitaiApiGatewayTests</c>); what this step still owns
    /// is making exactly one call per version rather than one per item.
    /// </summary>
    [Fact]
    public async Task Execute_MakesOneCivitaiCallPerVersion()
    {
        await SeedAsync("multi", civitaiId: 101, versionCivitaiIds: [701, 702, 703]);

        var client = new Mock<ICivitaiClient>();
        client
            .Setup(x => x.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, string? _, CancellationToken _) => NewCivitaiVersion(id, NewImage(id * 10)));

        var step = new FetchImagesStep(Scopes, new CivitaiMetadataApplier(client.Object, logger: null));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(), apiKey: null, CancellationToken.None);

        client.Verify(
            c => c.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task Execute_ZeroImagesStillStampsChecked()
    {
        // Same bug as the tags step: a version Civitai has no images for is a final answer.
        var seeded = await SeedAsync("imageless", civitaiId: 101, versionCivitaiIds: 701);

        var step = NewStep(c => c
            .Setup(x => x.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, string? _, CancellationToken _) => NewCivitaiVersion(id)));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        (await CountImagesAsync(seeded.ModelId, seeded.VersionIds[0])).Should().Be(0);
        (await ReadStateAsync(seeded.ModelId))!.ImagesCheckedAt.Should().NotBeNull();

        var second = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        second.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_NullVersionStampsCheckedAndIsSkipped()
    {
        var seeded = await SeedAsync("gone", civitaiId: 101, versionCivitaiIds: 701);

        var step = NewStep(c => c
            .Setup(x => x.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CivitaiModelVersion?)null));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Skipped.Should().BeTrue();
        (await ReadStateAsync(seeded.ModelId))!.ImagesCheckedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Execute_AnyVersionFailureLeavesModelUnstamped()
    {
        var seeded = await SeedAsync("half-broken", civitaiId: 101, versionCivitaiIds: [701, 702]);

        var step = NewStep(c =>
        {
            c.Setup(x => x.GetModelVersionAsync(701, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(NewCivitaiVersion(701, NewImage(7010)));
            c.Setup(x => x.GetModelVersionAsync(702, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("connection reset"));
        });

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Skipped.Should().BeFalse();
        result.FailureReason.Should().Contain("connection reset");

        // No stamp at all: the whole model is retried next run, images already appended stay.
        (await ReadStateAsync(seeded.ModelId)).Should().BeNull();

        var second = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        second.Select(i => i.ModelId).Should().Contain(seeded.ModelId);
    }

    [Fact]
    public async Task Execute_ErrorDoesNotStamp()
    {
        var seeded = await SeedAsync("boom", civitaiId: 101, versionCivitaiIds: 701);

        var step = NewStep(c => c
            .Setup(x => x.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset")));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("connection reset");
        (await ReadStateAsync(seeded.ModelId)).Should().BeNull();
    }

    /// <summary>
    /// I2. Only a 404 comes back as <c>null</c>; a restricted or early-access version throws with
    /// its status attached. Civitai refusing us is as final as Civitai not having the version, so
    /// it is stamped — otherwise every restricted model is re-asked on every run forever.
    /// </summary>
    [Fact]
    public async Task Execute_ForbiddenResponseStampsCheckedAndIsSkipped()
    {
        var seeded = await SeedAsync("forbidden", civitaiId: 101, versionCivitaiIds: 701);

        var step = NewStep(c => c
            .Setup(x => x.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Civitai API 403 for /model-versions/701", null, System.Net.HttpStatusCode.Forbidden)));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Skipped.Should().BeTrue("a refusal is an answer, not a failure to be retried");
        result.FailureReason.Should().BeNull();

        (await ReadStateAsync(seeded.ModelId))!.ImagesCheckedAt.Should().NotBeNull();

        var second = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        second.Select(i => i.ModelId).Should().NotContain(seeded.ModelId);
    }

    /// <summary>
    /// I2, the other side: 5xx is the server having a moment, not an answer about this model.
    /// </summary>
    [Fact]
    public async Task Execute_ServerErrorDoesNotStamp()
    {
        var seeded = await SeedAsync("unavailable", civitaiId: 101, versionCivitaiIds: 701);

        var step = NewStep(c => c
            .Setup(x => x.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Civitai API 503 for /model-versions/701", null, System.Net.HttpStatusCode.ServiceUnavailable)));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Skipped.Should().BeFalse();
        result.FailureReason.Should().Contain("503");

        (await ReadStateAsync(seeded.ModelId)).Should().BeNull();

        var second = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        second.Select(i => i.ModelId).Should().Contain(seeded.ModelId);
    }

    [Fact]
    public async Task Execute_CancellationDoesNotStamp()
    {
        var seeded = await SeedAsync("cancel", civitaiId: 101, versionCivitaiIds: 701);

        using var cts = new CancellationTokenSource();
        var step = NewStep(c => c
            .Setup(x => x.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ThrowsAsync(new OperationCanceledException()));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var item = items.Single();

        var act = () => step.ExecuteOneAsync(item, apiKey: null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await ReadStateAsync(seeded.ModelId)).Should().BeNull();
    }

    [Fact]
    public void Step_DescribesItselfForThePlanView()
    {
        var step = NewStep();

        step.Kind.Should().Be(SyncStepKind.FetchImages);
        step.Description.Should().Be("Fetch image records");
        step.EstimatedPerItem.Should().Be(TimeSpan.FromSeconds(1.6));
    }

    /// <summary>
    /// Client + Step shared across the "one model call" tests below: they exercise
    /// <see cref="FetchImagesStep.ExecuteOneAsync"/> directly against a synthetic
    /// <see cref="SyncItem"/>, without seeding the DB — the local model/version ids never need to
    /// resolve to anything, since these tests only assert which Civitai endpoints were called.
    /// </summary>
    private Mock<ICivitaiClient> Client { get; } = new();

    private FetchImagesStep Step { get; }

    /// <summary>
    /// Builds a synthetic <see cref="SyncItem"/> for one model with the given Civitai version ids,
    /// and defaults the model page (<see cref="SetModelPageVersions"/>) to describe all of them —
    /// call <see cref="SetModelPageVersions"/> again afterwards to make the page omit some.
    /// </summary>
    private SyncItem CreateItemWithVersions(int civitaiModelId, int[] civitaiVersionIds)
    {
        SetModelPageVersions(civitaiModelId, civitaiVersionIds);

        var candidates = civitaiVersionIds
            .Select(civitaiVersionId => new ImageCandidate(
                ModelId: civitaiModelId,
                VersionId: civitaiVersionId,
                CivitaiVersionId: civitaiVersionId,
                CivitaiModelId: civitaiModelId,
                Name: $"model-{civitaiModelId}",
                ImagesCheckedAt: null))
            .ToList();

        return new SyncItem(civitaiModelId, $"model-{civitaiModelId}", candidates);
    }

    /// <summary>Stubs <c>GetModelAsync(civitaiModelId, ...)</c> to describe exactly these versions.</summary>
    private void SetModelPageVersions(int civitaiModelId, int[] civitaiVersionIds)
    {
        Client
            .Setup(c => c.GetModelAsync(civitaiModelId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModel
            {
                Id = civitaiModelId,
                ModelVersions = civitaiVersionIds.Select(v => NewCivitaiVersion(v, NewImage(v * 10L))).ToList(),
            });
    }

    [Fact]
    public async Task ExecuteOneAsync_ThreeVersionsOfOneModel_MakesOneModelCall()
    {
        var item = CreateItemWithVersions(civitaiModelId: 900, civitaiVersionIds: [10, 11, 12]);

        await Step.ExecuteOneAsync(item, apiKey: null, CancellationToken.None);

        Client.Verify(c => c.GetModelAsync(900, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Client.Verify(c => c.GetModelVersionAsync(
            It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteOneAsync_VersionMissingFromTheModelPage_FallsBackToTheVersionCall()
    {
        // The model page describes 10 and 11 but not 12 — that one still needs its own call
        // rather than being silently recorded as "no images".
        var item = CreateItemWithVersions(civitaiModelId: 900, civitaiVersionIds: [10, 11, 12]);
        SetModelPageVersions(900, [10, 11]);

        await Step.ExecuteOneAsync(item, apiKey: null, CancellationToken.None);

        Client.Verify(c => c.GetModelAsync(900, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Client.Verify(c => c.GetModelVersionAsync(12, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Client.Verify(c => c.GetModelVersionAsync(10, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
