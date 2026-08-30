using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
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
/// place, from the file name alone, without ever touching a row Civitai already identified. Mirrors
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
    /// "{name}.safetensors" — plus a ModelSyncState carrying the given outcome. This is the shape
    /// both the candidate query (Type/Source/MetadataOutcome) and the reclassification loop
    /// (the file name) read; no bytes ever touch disk because classification here is name-only.
    /// </summary>
    private async Task<int> GivenModelAsync(string name, ModelType type, DataSource source, SyncOutcome outcome)
    {
        var model = new Model { Name = name, Type = type, Source = source };
        var version = new ModelVersion { Name = "v1", BaseModelRaw = "???", Model = model };
        version.Files.Add(new ModelFile
        {
            FileName = $"{name}.safetensors",
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
    /// Civitai has already failed on, which is where the support assets are.
    /// </summary>
    [Fact]
    public async Task ReclassifiesAnUnidentifiedLocalRow()
    {
        var id = await GivenModelAsync("Wan2_2_VAE_bf16", ModelType.LORA, DataSource.LocalFile, SyncOutcome.NotIdentified);

        var changed = await _service.ReclassifySupportAssetsAsync(CancellationToken.None);

        changed.Should().Be(1);
        (await LoadTypeAsync(id)).Should().Be(ModelType.VAE);
    }

    /// <summary>
    /// A model Civitai identified carries an authoritative type. Our name guess must never
    /// overrule it — that is the difference between filling a blank and overwriting an answer.
    /// </summary>
    [Fact]
    public async Task LeavesAMatchedRowAlone()
    {
        var id = await GivenModelAsync("vae_finetune_lora", ModelType.LORA, DataSource.LocalFile, SyncOutcome.Matched);

        var changed = await _service.ReclassifySupportAssetsAsync(CancellationToken.None);

        changed.Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
    }

    [Fact]
    public async Task LeavesAnOrdinaryLoraAlone()
    {
        var id = await GivenModelAsync("MyChar_Pony_v2", ModelType.LORA, DataSource.LocalFile, SyncOutcome.NotIdentified);

        var changed = await _service.ReclassifySupportAssetsAsync(CancellationToken.None);

        changed.Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// The pass runs on every discovery. It has to be free the second time: a row it reclassified
    /// no longer satisfies Type == LORA, so it is not a candidate again.
    /// </summary>
    [Fact]
    public async Task IsIdempotent()
    {
        await GivenModelAsync("SD3-VAE", ModelType.LORA, DataSource.LocalFile, SyncOutcome.NotIdentified);

        (await _service.ReclassifySupportAssetsAsync(CancellationToken.None)).Should().Be(1);
        (await _service.ReclassifySupportAssetsAsync(CancellationToken.None)).Should().Be(0);
    }
}
