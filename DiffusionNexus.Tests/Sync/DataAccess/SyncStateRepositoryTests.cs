using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.Sync;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Tests.Sync.DataAccess;

/// <summary>
/// Covers <c>ISyncStateRepository</c>: legacy-row discovery, get-or-create,
/// and the three scope-aware candidate selections.
/// </summary>
public sealed class SyncStateRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public SyncStateRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();
    }

    private IServiceScope NewScope() => _serviceProvider.CreateScope();

    // helper used by every test here
    private static Model NewLocalModel(string name, string path, int? civitaiId = null, bool userEdited = false,
        ModelType type = ModelType.LORA, bool withTag = false, int? versionCivitaiId = null, bool withImage = false)
    {
        var m = new Model { Name = name, Type = type, Source = DataSource.LocalFile, CivitaiId = civitaiId, IsUserEdited = userEdited };
        var v = new ModelVersion { Name = "v1", CivitaiId = versionCivitaiId, BaseModelRaw = "???" };
        v.Files.Add(new ModelFile { FileName = Path.GetFileName(path), LocalPath = path, IsLocalFileValid = true, IsPrimary = true, HashSHA256 = "AA" });
        if (withImage) v.Images.Add(new ModelImage { Url = "https://x/y.jpeg" });
        m.Versions.Add(v);
        if (withTag) m.Tags.Add(new ModelTag { Tag = new Tag { Name = "style", NormalizedName = "style" } });
        return m;
    }

    [Fact]
    public async Task GetModelIdsWithoutStateReturnsOnlyLegacyRows()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var legacy = NewLocalModel("legacy", @"C:\m\legacy.safetensors");
        var stated = NewLocalModel("stated", @"C:\m\stated.safetensors");
        await uow.Models.AddAsync(legacy);
        await uow.Models.AddAsync(stated);
        await uow.SaveChangesAsync();

        await uow.SyncStates.GetOrCreateAsync(stated.Id);
        await uow.SaveChangesAsync();

        var ids = await uow.SyncStates.GetModelIdsWithoutStateAsync();

        ids.Should().BeEquivalentTo(new[] { legacy.Id });
    }

    [Fact]
    public async Task GetOrCreateAddsDefaultRowOnce()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = NewLocalModel("m", @"C:\m\m.safetensors");
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        var first = await uow.SyncStates.GetOrCreateAsync(model.Id);
        var second = await uow.SyncStates.GetOrCreateAsync(model.Id);

        second.Should().BeSameAs(first);

        await uow.SaveChangesAsync();

        var stored = await uow.SyncStates.GetByModelIdAsync(model.Id);
        stored.Should().NotBeNull();
        stored!.ModelId.Should().Be(model.Id);
        stored.MetadataOutcome.Should().Be(SyncOutcome.None);
        stored.MetadataAttempts.Should().Be(0);
    }

    [Fact]
    public async Task IdentifyCandidatesExcludeCivitaiMatchedInvalidAndNonLora()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var inLora = NewLocalModel("in-lora", @"C:\m\in-lora.safetensors");
        var outMatched = NewLocalModel("out-matched", @"C:\m\out-matched.safetensors", civitaiId: 5);
        var outInvalid = NewLocalModel("out-invalid", @"C:\m\out-invalid.safetensors");
        outInvalid.Versions.First().Files.First().IsLocalFileValid = false;
        var outCheckpoint = NewLocalModel("out-checkpoint", @"C:\m\out-checkpoint.safetensors", type: ModelType.Checkpoint);
        var inUnknown = NewLocalModel("in-unknown", @"C:\m\in-unknown.safetensors", type: ModelType.Unknown);

        foreach (var m in new[] { inLora, outMatched, outInvalid, outCheckpoint, inUnknown })
            await uow.Models.AddAsync(m);
        await uow.SaveChangesAsync();

        var candidates = await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.Library);

        candidates.Select(c => c.Name).Should().BeEquivalentTo(new[] { "in-lora", "in-unknown" });
    }

    [Fact]
    public async Task IdentifyCandidatesCarryStateFields()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = NewLocalModel("stateful", @"C:\m\stateful.safetensors");
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        var checkedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var state = await uow.SyncStates.GetOrCreateAsync(model.Id);
        state.MetadataOutcome = SyncOutcome.NotIdentified;
        state.MetadataCheckedAt = checkedAt;
        state.MetadataAttempts = 2;
        state.SidecarSignature = "sig";
        await uow.SaveChangesAsync();

        var candidates = await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.Library);

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.ModelId.Should().Be(model.Id);
        candidate.VersionId.Should().Be(model.Versions.First().Id);
        candidate.FileId.Should().Be(model.Versions.First().Files.First().Id);
        candidate.Name.Should().Be("stateful");
        candidate.LocalPath.Should().Be(@"C:\m\stateful.safetensors");
        candidate.Sha256.Should().Be("AA");
        candidate.BaseModelRaw.Should().Be("???");
        candidate.Outcome.Should().Be(SyncOutcome.NotIdentified);
        candidate.CheckedAt.Should().Be(checkedAt);
        candidate.Attempts.Should().Be(2);
        candidate.SidecarSignature.Should().Be("sig");
    }

    [Fact]
    public async Task SourceFolderScopeIsBoundaryAware()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var inRoot = NewLocalModel("in-root", @"E:\Loras\a.safetensors");
        var outSibling = NewLocalModel("out-sibling", @"E:\Loras_backup\b.safetensors");
        var inNested = NewLocalModel("in-nested", @"e:\loras\sub\c.safetensors");

        foreach (var m in new[] { inRoot, outSibling, inNested })
            await uow.Models.AddAsync(m);
        await uow.SaveChangesAsync();

        var candidates = await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.ForFolder(@"E:\Loras"));

        candidates.Select(c => c.Name).Should().BeEquivalentTo(new[] { "in-root", "in-nested" });
    }

    [Fact]
    public async Task ModelsScopeFiltersByIds()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var a = NewLocalModel("a", @"C:\m\a.safetensors");
        var b = NewLocalModel("b", @"C:\m\b.safetensors");
        var c = NewLocalModel("c", @"C:\m\c.safetensors");

        foreach (var m in new[] { a, b, c })
            await uow.Models.AddAsync(m);
        await uow.SaveChangesAsync();

        var candidates = await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.ForModels(a.Id, c.Id));

        candidates.Select(x => x.Name).Should().BeEquivalentTo(new[] { "a", "c" });
    }

    [Fact]
    public async Task TagCandidatesRequireCivitaiIdAndNoTagsAndNotUserEdited()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var inNoTags = NewLocalModel("in-no-tags", @"C:\m\in-no-tags.safetensors", civitaiId: 7);
        var outTagged = NewLocalModel("out-tagged", @"C:\m\out-tagged.safetensors", civitaiId: 8, withTag: true);
        var outUserEdited = NewLocalModel("out-user-edited", @"C:\m\out-user-edited.safetensors", civitaiId: 9, userEdited: true);
        var outNoCivitaiId = NewLocalModel("out-no-civitai-id", @"C:\m\out-no-civitai-id.safetensors");

        foreach (var m in new[] { inNoTags, outTagged, outUserEdited, outNoCivitaiId })
            await uow.Models.AddAsync(m);
        await uow.SaveChangesAsync();

        var tagsCheckedAt = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var state = await uow.SyncStates.GetOrCreateAsync(inNoTags.Id);
        state.TagsCheckedAt = tagsCheckedAt;
        await uow.SaveChangesAsync();

        var candidates = await uow.SyncStates.SelectTagCandidatesAsync(SyncScope.Library);

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.ModelId.Should().Be(inNoTags.Id);
        candidate.CivitaiModelId.Should().Be(7);
        candidate.Name.Should().Be("in-no-tags");
        candidate.TagsCheckedAt.Should().Be(tagsCheckedAt);
    }

    [Fact]
    public async Task ImageCandidatesRequireVersionCivitaiIdAndNoImages()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = NewLocalModel("imaged", @"C:\m\imaged.safetensors", civitaiId: 1, versionCivitaiId: 10);

        var withImage = new ModelVersion { Name = "v2", CivitaiId = 11, BaseModelRaw = "???" };
        withImage.Images.Add(new ModelImage { Url = "https://x/z.jpeg" });
        model.Versions.Add(withImage);

        model.Versions.Add(new ModelVersion { Name = "v3", CivitaiId = null, BaseModelRaw = "???" });

        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        var imagesCheckedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var state = await uow.SyncStates.GetOrCreateAsync(model.Id);
        state.ImagesCheckedAt = imagesCheckedAt;
        await uow.SaveChangesAsync();

        var candidates = await uow.SyncStates.SelectImageCandidatesAsync(SyncScope.Library);

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.ModelId.Should().Be(model.Id);
        candidate.VersionId.Should().Be(model.Versions.First(v => v.CivitaiId == 10).Id);
        candidate.CivitaiVersionId.Should().Be(10);
        candidate.Name.Should().Be("imaged");
        candidate.ImagesCheckedAt.Should().Be(imagesCheckedAt);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
