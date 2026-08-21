using System.Diagnostics;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Service.Services.Sync;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace DiffusionNexus.Tests.Sync.Service;

/// <summary>
/// Covers the one-time backfill that gives every pre-existing model a
/// <see cref="ModelSyncState"/> row derived from data already in the database.
/// </summary>
public sealed class SyncStateInitializerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly ITestOutputHelper _output;

    public SyncStateInitializerTests(ITestOutputHelper output)
    {
        _output = output;

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();
    }

    private SyncStateInitializer NewInitializer() =>
        new(_serviceProvider.GetRequiredService<IServiceScopeFactory>());

    [Fact]
    public async Task EnsureInitializedBackfillsOnlyLegacyModels()
    {
        var (matched, sidecar, untouched, alreadyStated) = await SeedAsync();

        var stopwatch = Stopwatch.StartNew();
        var created = await NewInitializer().EnsureInitializedAsync();
        stopwatch.Stop();
        _output.WriteLine($"EnsureInitializedAsync over 4 models took {stopwatch.ElapsedMilliseconds} ms");

        created.Should().Be(3);

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        foreach (var id in new[] { matched, sidecar, untouched, alreadyStated })
            (await uow.SyncStates.GetByModelIdAsync(id)).Should().NotBeNull($"model {id} must have a state row");

        (await uow.SyncStates.GetModelIdsWithoutStateAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureInitializedDerivesEachOutcomeFromExistingData()
    {
        var (matched, sidecar, untouched, _) = await SeedAsync();

        await NewInitializer().EnsureInitializedAsync();

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var matchedState = await uow.SyncStates.GetByModelIdAsync(matched);
        matchedState!.MetadataOutcome.Should().Be(SyncOutcome.Matched);
        matchedState.MetadataCheckedAt.Should().Be(SyncedAt);
        matchedState.TagsCheckedAt.Should().Be(SyncedAt);
        matchedState.ImagesCheckedAt.Should().Be(SyncedAt);

        var sidecarState = await uow.SyncStates.GetByModelIdAsync(sidecar);
        sidecarState!.MetadataOutcome.Should().Be(SyncOutcome.Sidecar);
        // Not SyncedAt: an unmatched legacy row is stamped with the derivation time, so the
        // upgrade does not make the whole library due at once (R1, anti-herd).
        sidecarState.MetadataCheckedAt.Should().BeAfter(SyncedAt);
        sidecarState.MetadataCheckedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        sidecarState.TagsCheckedAt.Should().BeNull();
        sidecarState.ImagesCheckedAt.Should().BeNull();

        var untouchedState = await uow.SyncStates.GetByModelIdAsync(untouched);
        untouchedState!.MetadataOutcome.Should().Be(SyncOutcome.None);
        untouchedState.MetadataCheckedAt.Should().BeNull();
    }

    [Fact]
    public async Task SecondRunCreatesNothingAndChangesNothing()
    {
        var (matched, _, _, alreadyStated) = await SeedAsync();

        var initializer = NewInitializer();
        (await initializer.EnsureInitializedAsync()).Should().Be(3);

        var before = await SnapshotUpdatedAtAsync();

        var secondRun = await initializer.EnsureInitializedAsync();

        secondRun.Should().Be(0);
        (await SnapshotUpdatedAtAsync()).Should().BeEquivalentTo(before);

        // Explicit on the two interesting rows: the pre-existing one and a derived one.
        before.Should().ContainKeys(matched, alreadyStated);
    }

    /// <summary>
    /// I4. The backfill runs inside <c>PlanAsync</c>, and on the first launch after the upgrade
    /// that pass takes seconds over a real library — long enough for the user to press the per-tile
    /// button while the bulk sync is already planning. Both calls then read the same "no state row
    /// yet" id list and both Add the same primary keys, and the loser's save is rejected outright:
    /// the second plan dies with a DbUpdateException instead of syncing anything.
    /// </summary>
    [Fact]
    public async Task EnsureInitialized_ConcurrentCallsCreateEachRowOnce()
    {
        await SeedAsync();

        var initializer = NewInitializer();

        var first = Task.Run(() => initializer.EnsureInitializedAsync());
        var second = Task.Run(() => initializer.EnsureInitializedAsync());

        var created = await Task.WhenAll(first, second);

        // Whoever got there first did all three; the other found nothing left to do.
        created.Sum().Should().Be(3, "every legacy model is derived exactly once, by exactly one of the callers");

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        var states = await context.Set<ModelSyncState>().AsNoTracking().CountAsync();
        var models = await context.Set<Model>().AsNoTracking().CountAsync();

        states.Should().Be(models, "one state row per model, no duplicates and none missing");
    }

    private static readonly DateTimeOffset SyncedAt = new(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private async Task<Dictionary<int, DateTimeOffset>> SnapshotUpdatedAtAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        return await context.Set<ModelSyncState>()
            .AsNoTracking()
            .ToDictionaryAsync(s => s.ModelId, s => s.UpdatedAt);
    }

    /// <summary>Three legacy models (one per outcome) plus one that already carries a state row.</summary>
    private async Task<(int Matched, int Sidecar, int Untouched, int AlreadyStated)> SeedAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var matched = NewModel("matched", civitaiId: 42, lastSyncedAt: SyncedAt, source: DataSource.CivitaiApi,
            baseModelRaw: "SDXL 1.0", withTag: true, withImage: true);
        var sidecar = NewModel("sidecar", civitaiId: null, lastSyncedAt: SyncedAt, source: DataSource.LocalFile,
            baseModelRaw: "Illustrious");
        var untouched = NewModel("untouched", civitaiId: null, lastSyncedAt: null, source: DataSource.LocalFile,
            baseModelRaw: "???");
        var alreadyStated = NewModel("already-stated", civitaiId: 99, lastSyncedAt: SyncedAt,
            source: DataSource.CivitaiApi, baseModelRaw: "Pony");

        foreach (var model in new[] { matched, sidecar, untouched, alreadyStated })
            await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        var existing = await uow.SyncStates.GetOrCreateAsync(alreadyStated.Id);
        existing.MetadataOutcome = SyncOutcome.NotIdentified;
        existing.MetadataAttempts = 3;
        existing.UpdatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await uow.SaveChangesAsync();

        return (matched.Id, sidecar.Id, untouched.Id, alreadyStated.Id);
    }

    private static Model NewModel(string name, int? civitaiId, DateTimeOffset? lastSyncedAt, DataSource source,
        string? baseModelRaw, bool withTag = false, bool withImage = false)
    {
        var model = new Model
        {
            Name = name,
            Type = ModelType.LORA,
            Source = source,
            CivitaiId = civitaiId,
            LastSyncedAt = lastSyncedAt,
        };

        var version = new ModelVersion { Name = "v1", BaseModelRaw = baseModelRaw };
        version.Files.Add(new ModelFile
        {
            FileName = name + ".safetensors",
            LocalPath = @"C:\m\" + name + ".safetensors",
            IsLocalFileValid = true,
            IsPrimary = true,
            HashSHA256 = "AA",
        });
        if (withImage) version.Images.Add(new ModelImage { Url = "https://x/y.jpeg" });
        model.Versions.Add(version);

        if (withTag) model.Tags.Add(new ModelTag { Tag = new Tag { Name = name + "-tag", NormalizedName = name + "-tag" } });

        return model;
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
