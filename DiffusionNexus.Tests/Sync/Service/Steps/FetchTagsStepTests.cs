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
/// Covers <see cref="FetchTagsStep"/> — the direct fix for the production bug where 68 models
/// with genuinely zero tags on Civitai were re-fetched on every single run because
/// "checked and empty" was never recorded (#521 WP2).
/// </summary>
public sealed class FetchTagsStepTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    public FetchTagsStepTests()
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
    }

    private IServiceScope NewScope() => _serviceProvider.CreateScope();

    private IServiceScopeFactory Scopes => _serviceProvider.GetRequiredService<IServiceScopeFactory>();

    /// <summary>
    /// Seeds a Civitai-identified model (a tag candidate requires <c>CivitaiId</c>), optionally
    /// with an already-stamped sync state and/or existing tags.
    /// </summary>
    private async Task<int> SeedAsync(
        string name,
        int civitaiId,
        DateTimeOffset? tagsCheckedAt = null,
        bool userEdited = false,
        params string[] tags)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = new Model
        {
            Name = name,
            Type = ModelType.LORA,
            Source = DataSource.CivitaiApi,
            CivitaiId = civitaiId,
            IsUserEdited = userEdited,
        };
        var version = new ModelVersion { Name = "v1", BaseModelRaw = "SDXL 1.0" };
        version.Files.Add(new ModelFile
        {
            FileName = name + ".safetensors",
            LocalPath = Path.Combine(Path.GetTempPath(), name + ".safetensors"),
            IsLocalFileValid = true,
            IsPrimary = true,
        });
        model.Versions.Add(version);

        foreach (var tagName in tags)
        {
            model.Tags.Add(new ModelTag { Tag = new Tag { Name = tagName, NormalizedName = tagName.ToLowerInvariant() } });
        }

        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        if (tagsCheckedAt is not null)
        {
            var state = await uow.SyncStates.GetOrCreateAsync(model.Id);
            state.TagsCheckedAt = tagsCheckedAt;
            state.UpdatedAt = tagsCheckedAt.Value;
            await uow.SaveChangesAsync();
        }

        return model.Id;
    }

    private async Task<ModelSyncState?> ReadStateAsync(int modelId)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await uow.SyncStates.GetByModelIdAsync(modelId);
    }

    private async Task<IReadOnlyList<string>> ReadTagsAsync(int modelId)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var model = await uow.Models.GetByIdWithIncludesAsync(modelId);
        return model!.Tags.Select(t => t.Tag!.Name).OrderBy(n => n).ToList();
    }

    private static CivitaiModel NewCivitaiModel(int id = 77, params string[] tags) => new()
    {
        Id = id,
        Name = "Civitai Name",
        Tags = tags,
        ModelVersions = [],
    };

    /// <summary>Builds the step over a client mock configured by <paramref name="configure"/>.</summary>
    private FetchTagsStep NewStep(Action<Mock<ICivitaiClient>> configure)
    {
        var client = new Mock<ICivitaiClient>();
        configure(client);
        return new FetchTagsStep(Scopes, new CivitaiMetadataApplier(client.Object));
    }

    /// <summary>A step whose model fetch returns a model carrying <paramref name="tags"/>.</summary>
    private FetchTagsStep NewStep(params string[] tags) => NewStep(c => c
        .Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(NewCivitaiModel(tags: tags)));

    private static SyncOptions Options(bool force = false) =>
        new(new HashSet<SyncStepKind> { SyncStepKind.FetchTags }, ForceTags: force);

    /// <summary>
    /// The tags step makes exactly one Civitai model fetch per item. Pacing itself is the
    /// gateway's job now (verified in <c>CivitaiApiGatewayTests</c>); what this step still owns
    /// is making exactly one call, not zero and not a call per tag.
    /// </summary>
    [Fact]
    public async Task Execute_MakesOneCivitaiCallPerItem()
    {
        await SeedAsync("paced", civitaiId: 101);

        var client = new Mock<ICivitaiClient>();
        client.Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewCivitaiModel(tags: ["style"]));

        var step = new FetchTagsStep(Scopes, new CivitaiMetadataApplier(client.Object, logger: null));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(), apiKey: null, CancellationToken.None);

        client.Verify(c => c.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Select_ReturnsOnlyNeverChecked_UnlessForced()
    {
        var fresh = await SeedAsync("fresh", civitaiId: 101);
        var checkedAlready = await SeedAsync("checked", civitaiId: 102, tagsCheckedAt: Now.AddDays(-400));

        var step = NewStep();

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        items.Select(i => i.ModelId).Should().BeEquivalentTo([fresh]);
        items.Should().OnlyContain(i => i.Payload is TagCandidate);

        var forced = await step.SelectAsync(SyncScope.Library, Options(force: true), Now, CancellationToken.None);
        forced.Select(i => i.ModelId).Should().BeEquivalentTo([fresh, checkedAlready]);
    }

    [Fact]
    public async Task Execute_ZeroTagsStillStampsChecked()
    {
        // The 68-models bug, as a test: Civitai answers with an empty tag list, which is a real,
        // final answer. Recording it is the only thing that stops the next run asking again.
        var modelId = await SeedAsync("tagless", civitaiId: 101);

        var step = NewStep();   // no tags in the response
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.FailureReason.Should().BeNull();

        var state = await ReadStateAsync(modelId);
        state.Should().NotBeNull();
        state!.TagsCheckedAt.Should().NotBeNull();

        // The model still has zero tags, so the repository still offers it as a candidate — the
        // step's own policy filter is what must now exclude it.
        var second = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        second.Select(i => i.ModelId).Should().NotContain(modelId);
    }

    [Fact]
    public async Task Execute_TagsAreWrittenAndStamped()
    {
        var modelId = await SeedAsync("tagged", civitaiId: 101);

        var step = NewStep("style", "anime");
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        (await ReadTagsAsync(modelId)).Should().BeEquivalentTo(["anime", "style"]);
        (await ReadStateAsync(modelId))!.TagsCheckedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Execute_NullModelStampsCheckedAndIsSkipped()
    {
        // The model is gone from Civitai (404). Asking again every run is exactly the bug we are
        // fixing, so this is stamped as final even though nothing was written.
        var modelId = await SeedAsync("gone", civitaiId: 101);

        var step = NewStep(c => c
            .Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CivitaiModel?)null));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Skipped.Should().BeTrue();
        result.FailureReason.Should().BeNull();

        (await ReadStateAsync(modelId))!.TagsCheckedAt.Should().NotBeNull();

        var second = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        second.Select(i => i.ModelId).Should().NotContain(modelId);
    }

    [Fact]
    public async Task Execute_ErrorDoesNotStamp()
    {
        var modelId = await SeedAsync("boom", civitaiId: 101);

        var step = NewStep(c => c
            .Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset")));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Skipped.Should().BeFalse();
        result.FailureReason.Should().Contain("connection reset");

        // A transient fault must leave no trace: the item comes back on the next run.
        (await ReadStateAsync(modelId)).Should().BeNull();

        var second = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        second.Select(i => i.ModelId).Should().Contain(modelId);
    }

    /// <summary>
    /// I2. Only a 404 comes back as <c>null</c>; an early-access or otherwise restricted page
    /// throws with its status attached. That is just as final an answer as a 404 — Civitai will
    /// refuse again tomorrow — so it has to be stamped, or the model is re-asked on every single
    /// run for as long as it exists, which is the exact bug this step was written to fix.
    /// </summary>
    [Fact]
    public async Task Execute_ForbiddenResponseStampsCheckedAndIsSkipped()
    {
        var modelId = await SeedAsync("forbidden", civitaiId: 101);

        var step = NewStep(c => c
            .Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Civitai API 403 for /models/101", null, System.Net.HttpStatusCode.Forbidden)));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Skipped.Should().BeTrue("a refusal is an answer, not a failure to be retried");
        result.FailureReason.Should().BeNull();

        (await ReadStateAsync(modelId))!.TagsCheckedAt.Should().NotBeNull();

        var second = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        second.Select(i => i.ModelId).Should().NotContain(modelId);
    }

    /// <summary>
    /// I2, the other side: 5xx (and 429, and a bare connection failure) are the server having a
    /// moment, not an answer about this model, so nothing is recorded and the item comes back.
    /// </summary>
    [Fact]
    public async Task Execute_ServerErrorDoesNotStamp()
    {
        var modelId = await SeedAsync("unavailable", civitaiId: 101);

        var step = NewStep(c => c
            .Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Civitai API 503 for /models/101", null, System.Net.HttpStatusCode.ServiceUnavailable)));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Skipped.Should().BeFalse();
        result.FailureReason.Should().Contain("503");

        (await ReadStateAsync(modelId)).Should().BeNull();

        var second = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        second.Select(i => i.ModelId).Should().Contain(modelId);
    }

    [Fact]
    public async Task Execute_CancellationDoesNotStamp()
    {
        var modelId = await SeedAsync("cancel", civitaiId: 101);

        using var cts = new CancellationTokenSource();
        var step = NewStep(c => c
            .Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ThrowsAsync(new OperationCanceledException()));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var item = items.Single(i => i.ModelId == modelId);

        var act = () => step.ExecuteOneAsync(item, apiKey: null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await ReadStateAsync(modelId)).Should().BeNull();
    }

    [Fact]
    public async Task Execute_EmptyTagListClearsStaleTagsUnlessUserEdited()
    {
        // An empty tag list from Civitai is authoritative: tags that were removed upstream must go.
        var plain = await SeedAsync("plain", civitaiId: 101, tags: "stale");
        // …but never for a model whose metadata the user owns.
        var edited = await SeedAsync("edited", civitaiId: 102, userEdited: true, tags: "mine");

        var step = NewStep();   // empty tag list

        // Driven through ExecuteOneAsync directly: both models already hold tags (and one is
        // user-edited), so SelectTagCandidatesAsync filters them out — yet an item can still reach
        // execution in that state, because the user may edit or tag a model while the run is in
        // flight. The step must behave correctly for the item it is handed, whoever handed it over.
        var plainItem = new SyncItem(plain, "plain", new TagCandidate(plain, 101, "plain", null));
        var editedItem = new SyncItem(edited, "edited", new TagCandidate(edited, 102, "edited", null));

        (await step.ExecuteOneAsync(plainItem, apiKey: null, CancellationToken.None)).Succeeded.Should().BeTrue();
        (await step.ExecuteOneAsync(editedItem, apiKey: null, CancellationToken.None)).Succeeded.Should().BeTrue();

        (await ReadTagsAsync(plain)).Should().BeEmpty();
        (await ReadTagsAsync(edited)).Should().BeEquivalentTo(["mine"]);

        // Both are stamped either way — the question was asked and answered for both.
        (await ReadStateAsync(plain))!.TagsCheckedAt.Should().NotBeNull();
        (await ReadStateAsync(edited))!.TagsCheckedAt.Should().NotBeNull();
    }

    [Fact]
    public void Step_DescribesItselfForThePlanView()
    {
        var step = NewStep();

        step.Kind.Should().Be(SyncStepKind.FetchTags);
        step.Description.Should().Be("Fetch tags");
        step.EstimatedPerItem.Should().Be(TimeSpan.FromSeconds(1.6));
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
