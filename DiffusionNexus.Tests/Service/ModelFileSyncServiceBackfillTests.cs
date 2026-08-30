using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using DiffusionNexus.Service.Services.Sync.Identity;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Service;

/// <summary>
/// Covers <see cref="ModelFileSyncService.ReclassifySupportAssetsAsync"/> (#527): every row in a
/// library that predates this feature still says <c>LORA</c>, including the VAEs, text encoders,
/// ControlNets and upscalers the issue is about. This is the one-shot pass that fixes those rows in
/// place, from the file name alone, without ever touching a row Civitai already identified —
/// scoped, since the Task 8 review (Important #2), to the pickles (<c>.ckpt</c>/<c>.pt</c>/
/// <c>.pth</c>) a header can never help with; a <c>.safetensors</c>/<c>.sft</c> row is left exactly
/// as it is, for <c>IdentifyModelStep</c> to decide from its weights instead. Mirrors
/// <see cref="ModelFileSyncServiceDiscoveryKindTests"/>'s fixture (kept-open SQLite connection,
/// scope held for the test's life) because its bodies call <c>_service</c> directly as a field,
/// including twice in the same test (<see cref="IsIdempotent"/>).
/// </summary>
public sealed class ModelFileSyncServiceBackfillTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly IServiceScope _scope;
    private readonly ModelFileSyncService _service;

    public ModelFileSyncServiceBackfillTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using (var initScope = _serviceProvider.CreateScope())
        {
            var context = initScope.ServiceProvider
                .GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>();
            context.Database.EnsureCreated();
        }

        // Held for the life of the test, not a per-call `using` — the brief's test bodies call
        // `_service` directly as a field, including twice in IsIdempotent, so the backing
        // IUnitOfWork/DbContext has to outlive a single ReclassifySupportAssetsAsync call.
        _scope = _serviceProvider.CreateScope();
        var uow = _scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        _service = new ModelFileSyncService(uow, new Mock<IAppSettingsService>().Object);
    }

    public void Dispose()
    {
        _scope.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Inserts a minimal local-file row — one Model, one Version, one File named
    /// "{name}{extension}" — plus a ModelSyncState carrying the given outcome. This is the shape
    /// both the candidate query (Type/Source/MetadataOutcome) and the reclassification loop
    /// (the file name) read; no bytes ever touch disk because classification here is name-only.
    /// </summary>
    /// <param name="extension">
    /// Defaults to <c>.safetensors</c> — the shape most fixtures in this file used before the
    /// Task 8 review scoped this pass off safetensors containers entirely (Important #2: their
    /// real kind is decided by <c>IdentifyModelStep</c> from their weights, not guessed here from
    /// their name). Callers that want to prove the pass still fires pass a pickle extension
    /// (<c>.ckpt</c>/<c>.pt</c>/<c>.pth</c>) instead — the one shape this pass still classifies.
    /// </param>
    private async Task<int> GivenModelAsync(string name, ModelType type, DataSource source, SyncOutcome outcome, string extension = ".safetensors")
    {
        var model = new Model { Name = name, Type = type, Source = source };
        var version = new ModelVersion { Name = "v1", BaseModelRaw = "???", Model = model };
        version.Files.Add(new ModelFile
        {
            FileName = $"{name}{extension}",
            IsPrimary = true,
            ModelVersion = version,
        });
        model.Versions.Add(version);
        model.SyncState = new ModelSyncState { MetadataOutcome = outcome };

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();
        return model.Id;
    }

    /// <summary>
    /// The other legacy shape the candidate query has to reach: a row discovered before the sync
    /// state table existed (or before this model was ever planned) has no <see cref="ModelSyncState"/>
    /// row at all — not one carrying <see cref="SyncOutcome.None"/>, but no row, period. That is
    /// precisely the cohort #527 exists for, so it has to be a candidate too.
    /// </summary>
    /// <param name="extension">See <see cref="GivenModelAsync"/>'s parameter of the same name.</param>
    private async Task<int> GivenModelWithoutSyncStateAsync(string name, ModelType type, DataSource source, string extension = ".safetensors")
    {
        var model = new Model { Name = name, Type = type, Source = source };
        var version = new ModelVersion { Name = "v1", BaseModelRaw = "???", Model = model };
        version.Files.Add(new ModelFile
        {
            FileName = $"{name}{extension}",
            IsPrimary = true,
            ModelVersion = version,
        });
        model.Versions.Add(version);

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();
        return model.Id;
    }

    /// <summary>
    /// Re-reads Type through a brand-new scope, so the assertion proves what round-tripped through
    /// the real Type-as-string SQLite column — not merely what the in-memory instance still holds
    /// after ReclassifySupportAssetsAsync returned. Same technique as the Kind/Repoint suites'
    /// verify scopes.
    /// </summary>
    private async Task<ModelType> LoadTypeAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var model = await uow.Models.GetByIdAsync(id);
        return model!.Type;
    }

    /// <summary>
    /// Every row in a library that predates #527 says LORA. The pass targets exactly the cohort
    /// Civitai has already failed on, which is where the support assets are — but (Task 8 review,
    /// Important #2) only the pickle cohort: a <c>.ckpt</c>/<c>.pt</c>/<c>.pth</c> file has no
    /// header for anything to fall back on, so this is the only evidence it will ever get.
    /// </summary>
    [Fact]
    public async Task ReclassifiesAnUnidentifiedLocalRow()
    {
        var id = await GivenModelAsync("Wan2_2_VAE_bf16", ModelType.LORA, DataSource.LocalFile, SyncOutcome.NotIdentified, extension: ".ckpt");

        var changed = await _service.ReclassifySupportAssetsAsync(CancellationToken.None);

        changed.Should().Be(1);
        (await LoadTypeAsync(id)).Should().Be(ModelType.VAE);
    }

    /// <summary>
    /// The scoping fix itself (Task 8 review, Important #2). Before this fix, the fixture above
    /// (same name, <c>.safetensors</c> instead of <c>.ckpt</c>) WAS reclassified here — this test
    /// used to fail with <c>changed == 1</c> where it now expects <c>0</c>. A safetensors
    /// container's real kind is a fact <c>IdentifyModelStep</c> can read directly from its weights;
    /// guessing from its name here, on weaker evidence, only for that read to possibly disagree
    /// moments or days later, is strictly worse than leaving the row exactly as it is — it is
    /// already <c>NotIdentified</c>, i.e. already due for that step, so nothing is lost by waiting.
    /// </summary>
    [Fact]
    public async Task LeavesASafetensorsRowAloneEvenWhenItsNameLooksLikeASupportAsset()
    {
        var id = await GivenModelAsync("Wan2_2_VAE_bf16", ModelType.LORA, DataSource.LocalFile, SyncOutcome.NotIdentified, extension: ".safetensors");

        var changed = await _service.ReclassifySupportAssetsAsync(CancellationToken.None);

        changed.Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// A model Civitai identified carries an authoritative type. Our name guess must never
    /// overrule it — that is the difference between filling a blank and overwriting an answer.
    /// Left on <c>.safetensors</c> (the default extension): the candidate query itself excludes a
    /// Matched row before the loop ever inspects a file name or extension, so this stays a
    /// query-level guarantee regardless of the Important #2 scoping.
    /// </summary>
    [Fact]
    public async Task LeavesAMatchedRowAlone()
    {
        var id = await GivenModelAsync("vae_finetune_lora", ModelType.LORA, DataSource.LocalFile, SyncOutcome.Matched);

        var changed = await _service.ReclassifySupportAssetsAsync(CancellationToken.None);

        changed.Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// An ordinary LoRA's name never reads as a support asset in the first place — proved on a
    /// pickle extension so this stays a claim about <see cref="AssetKindClassifier"/>'s name
    /// reading, not merely a restatement of the blanket safetensors skip
    /// <see cref="LeavesASafetensorsRowAloneEvenWhenItsNameLooksLikeASupportAsset"/> covers.
    /// </summary>
    [Fact]
    public async Task LeavesAnOrdinaryLoraAlone()
    {
        var id = await GivenModelAsync("MyChar_Pony_v2", ModelType.LORA, DataSource.LocalFile, SyncOutcome.NotIdentified, extension: ".ckpt");

        var changed = await _service.ReclassifySupportAssetsAsync(CancellationToken.None);

        changed.Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// The pass runs on every discovery. It has to be free the second time: a row it reclassified
    /// no longer satisfies Type == LORA, so it is not a candidate again. On a pickle extension —
    /// the only shape this pass still reclassifies after Important #2.
    /// </summary>
    [Fact]
    public async Task IsIdempotent()
    {
        await GivenModelAsync("SD3-VAE", ModelType.LORA, DataSource.LocalFile, SyncOutcome.NotIdentified, extension: ".ckpt");

        (await _service.ReclassifySupportAssetsAsync(CancellationToken.None)).Should().Be(1);
        (await _service.ReclassifySupportAssetsAsync(CancellationToken.None)).Should().Be(0);
    }

    /// <summary>
    /// A model discovered before any sync state row existed for it has no ModelSyncState at all —
    /// not one carrying <see cref="SyncOutcome.None"/>, but no row, period. That is exactly the
    /// legacy shape #527 exists for, and the candidate query's <c>m.SyncState == null</c> branch
    /// has to reach it, not just the two MetadataOutcome branches. On a pickle extension so the
    /// Important #2 scoping does not mask this branch's own coverage.
    /// </summary>
    [Fact]
    public async Task ReclassifiesARowWithNoSyncStateAtAll()
    {
        var id = await GivenModelWithoutSyncStateAsync("LTX_T5_encoder", ModelType.LORA, DataSource.LocalFile, extension: ".pth");

        var changed = await _service.ReclassifySupportAssetsAsync(CancellationToken.None);

        changed.Should().Be(1);
        (await LoadTypeAsync(id)).Should().Be(ModelType.TextEncoder);
    }

    /// <summary>
    /// SyncOutcome.None is "never attempted" — distinct from both the no-row case above (no state
    /// exists yet) and NotIdentified ("attempted, and nothing answered"). The candidate query's
    /// third OR branch exists specifically for this outcome and has to reach it too. On a pickle
    /// extension so the Important #2 scoping does not mask this branch's own coverage.
    /// </summary>
    [Fact]
    public async Task ReclassifiesARowWithSyncOutcomeNone()
    {
        var id = await GivenModelAsync("SDXL_VAE_fp16", ModelType.LORA, DataSource.LocalFile, SyncOutcome.None, extension: ".ckpt");

        var changed = await _service.ReclassifySupportAssetsAsync(CancellationToken.None);

        changed.Should().Be(1);
        (await LoadTypeAsync(id)).Should().Be(ModelType.VAE);
    }

    /// <summary>
    /// #527 round 2 — the regression that would have caught the wiring gap: ReclassifySupportAssetsAsync
    /// used to have exactly one caller (DiscoverFilesStep), reached only via the bulk "Download
    /// Missing Metadata" button. The passive background reconcile path calls
    /// <see cref="IModelSyncService.DiscoverNewFilesAsync"/> directly and never went through that
    /// step, so a user who merely opened or refreshed the Viewer on a legacy library never got the
    /// backfill. Proves the fix at its true entry point: a PLAIN call to DiscoverNewFilesAsync —
    /// not ReclassifySupportAssetsAsync — reclassifies a pre-existing row and reports it, even
    /// though the configured source folder has nothing new on disk (it does not even exist here),
    /// which is deliberately the common case: a library that has already been fully indexed once
    /// hits "no new files" on every ordinary refresh, and that is exactly the case the legacy
    /// backfill has to reach. On a pickle extension: after Important #2, the equivalent
    /// <c>.safetensors</c> fixture would report <c>ReclassifiedCount == 0</c> here — correctly,
    /// since that row is left for <c>IdentifyModelStep</c> — which would prove nothing about this
    /// wiring path.
    /// </summary>
    [Fact]
    public async Task APlainDiscoverNewFilesAsyncCallReclassifiesALegacyRowAndReportsIt()
    {
        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.GetEnabledLoraSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([@"C:\does-not-exist-527-round2"]);

        var id = await GivenModelAsync("Wan2_2_VAE_bf16", ModelType.LORA, DataSource.LocalFile, SyncOutcome.NotIdentified, extension: ".ckpt");

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var sut = new ModelFileSyncService(uow, settings.Object);

        var result = await sut.DiscoverNewFilesAsync();

        result.NewModels.Should().BeEmpty("nothing new is on disk — the source folder does not even exist");
        result.RepointedCount.Should().Be(0, "nothing moved either");
        result.ReclassifiedCount.Should().Be(1,
            "the legacy row still has to be corrected even though this scan found nothing new to discover");
        (await LoadTypeAsync(id)).Should().Be(ModelType.VAE);
    }
}
