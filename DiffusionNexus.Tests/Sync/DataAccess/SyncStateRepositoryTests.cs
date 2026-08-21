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

    /// <summary>
    /// The enabled LoRA sources every test seeds under. "In the library" means "has a valid
    /// local file under one of these", so a candidate query without them selects nothing.
    /// </summary>
    private static readonly string[] Roots = [@"C:\m"];

    /// <summary>A moment in the past standing in for "this row was actually verified then".</summary>
    private static readonly DateTimeOffset Verified = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // helper used by every test here
    private static Model NewLocalModel(string name, string path, int? civitaiId = null, bool userEdited = false,
        ModelType type = ModelType.LORA, bool withTag = false, int? versionCivitaiId = null, bool withImage = false,
        bool validFile = true, bool withFile = true, DateTimeOffset? verifiedAt = null)
    {
        var m = new Model { Name = name, Type = type, Source = DataSource.LocalFile, CivitaiId = civitaiId, IsUserEdited = userEdited };
        var v = new ModelVersion { Name = "v1", CivitaiId = versionCivitaiId, BaseModelRaw = "???" };
        if (withFile)
            v.Files.Add(new ModelFile
            {
                FileName = Path.GetFileName(path), LocalPath = path, IsLocalFileValid = validFile,
                LocalFileVerifiedAt = verifiedAt, IsPrimary = true, HashSHA256 = "AA",
            });
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

        // Verified and found missing: the viewer hides it, so the sync must not chase it either.
        var outVerifiedMissing = NewLocalModel("out-verified-missing", @"C:\m\out-verified-missing.safetensors",
            validFile: false, verifiedAt: Verified);

        // A legacy row from before verification existed: IsLocalFileValid defaulted to false and
        // nothing has ever checked it. ModelFileSyncService.LoadCachedFilesAsync keeps exactly
        // these rows and the viewer shows them (issue #380), so the sync library holds them too;
        // IdentifyModelStep still File.Exists-checks before hashing anything.
        var inLegacyUnverified = NewLocalModel("in-legacy-unverified", @"C:\m\in-legacy-unverified.safetensors",
            validFile: false, verifiedAt: null);

        var outCheckpoint = NewLocalModel("out-checkpoint", @"C:\m\out-checkpoint.safetensors", type: ModelType.Checkpoint);
        var inUnknown = NewLocalModel("in-unknown", @"C:\m\in-unknown.safetensors", type: ModelType.Unknown);

        foreach (var m in new[] { inLora, outMatched, outVerifiedMissing, inLegacyUnverified, outCheckpoint, inUnknown })
            await uow.Models.AddAsync(m);
        await uow.SaveChangesAsync();

        var candidates = await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.Library, Roots, includeMatched: false);

        candidates.Select(c => c.Name).Should().BeEquivalentTo(new[] { "in-lora", "in-unknown", "in-legacy-unverified" });

        // A model with no state row projects the documented defaults, not nulls or garbage.
        var stateless = candidates.Single(c => c.Name == "in-lora");
        stateless.Outcome.Should().Be(SyncOutcome.None);
        stateless.CheckedAt.Should().BeNull();
        stateless.Attempts.Should().Be(0);
        stateless.SidecarSignature.Should().BeNull();
    }

    [Fact]
    public async Task IdentifyCandidatesReturnOnePerModelPreferringPrimaryFile()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Two valid files on one version, the SECOND of which is the primary.
        var primaryLast = NewLocalModel("primary-last", @"C:\m\primary-last-a.safetensors");
        primaryLast.Versions.First().Files.First().IsPrimary = false;
        primaryLast.Versions.First().Files.Add(new ModelFile
        {
            FileName = "primary-last-b.safetensors",
            LocalPath = @"C:\m\primary-last-b.safetensors",
            IsLocalFileValid = true,
            IsPrimary = true,
            HashSHA256 = "BB",
        });

        // Two valid files, neither primary — still exactly one candidate.
        var noPrimary = NewLocalModel("no-primary", @"C:\m\no-primary-a.safetensors");
        noPrimary.Versions.First().Files.First().IsPrimary = false;
        noPrimary.Versions.First().Files.Add(new ModelFile
        {
            FileName = "no-primary-b.safetensors",
            LocalPath = @"C:\m\no-primary-b.safetensors",
            IsLocalFileValid = true,
            IsPrimary = false,
            HashSHA256 = "CC",
        });

        await uow.Models.AddAsync(primaryLast);
        await uow.Models.AddAsync(noPrimary);
        await uow.SaveChangesAsync();

        var primaryFile = primaryLast.Versions.First().Files.Single(f => f.IsPrimary);
        var nonPrimaryFile = primaryLast.Versions.First().Files.Single(f => !f.IsPrimary);
        primaryFile.Id.Should().NotBe(nonPrimaryFile.Id);

        var candidates = await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.Library, Roots, includeMatched: false);

        candidates.Should().HaveCount(2);

        var withPrimary = candidates.Single(c => c.Name == "primary-last");
        withPrimary.FileId.Should().Be(primaryFile.Id);
        withPrimary.LocalPath.Should().Be(@"C:\m\primary-last-b.safetensors");

        var withoutPrimary = candidates.Single(c => c.Name == "no-primary");
        withoutPrimary.FileId.Should().BeOneOf(noPrimary.Versions.First().Files.Select(f => f.Id).ToArray());
    }

    /// <summary>
    /// C1. A bulk run has nothing to identify about a model that already carries a Civitai id, but
    /// a forced re-fetch is the user asking for exactly that model — and the per-tile "Download
    /// Metadata" button is a forced re-fetch. Filtering matched models out unconditionally made
    /// that button report "no metadata found on Civitai" for every model that had ever matched,
    /// which on the live library is the majority of it.
    /// </summary>
    [Fact]
    public async Task IdentifyCandidatesIncludeMatchedModelsWhenRequested()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var matched = NewLocalModel("matched", @"C:\m\matched.safetensors", civitaiId: 5);
        await uow.Models.AddAsync(matched);
        await uow.SaveChangesAsync();

        var state = await uow.SyncStates.GetOrCreateAsync(matched.Id);
        state.MetadataOutcome = SyncOutcome.Matched;
        await uow.SaveChangesAsync();

        var forced = await uow.SyncStates.SelectIdentifyCandidatesAsync(
            SyncScope.ForModels(matched.Id), Roots, includeMatched: true);

        var candidate = forced.Should().ContainSingle().Subject;
        candidate.ModelId.Should().Be(matched.Id);
        candidate.Outcome.Should().Be(SyncOutcome.Matched);

        var unforced = await uow.SyncStates.SelectIdentifyCandidatesAsync(
            SyncScope.ForModels(matched.Id), Roots, includeMatched: false);

        unforced.Should().BeEmpty("a bulk run has nothing to identify about a model that is already matched");
    }

    /// <summary>
    /// B1. A bulk run must not offer a model the user has edited by hand: every applier below it
    /// writes over the fields the user authored. A forced/per-tile run still gets it — that is the
    /// user asking — and the appliers protect the authored fields on that path.
    /// </summary>
    [Fact]
    public async Task IdentifyCandidatesExcludeUserEditedModelsUnlessIncludeMatched()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var mine = NewLocalModel("mine", @"C:\m\mine.safetensors", userEdited: true);
        var theirs = NewLocalModel("theirs", @"C:\m\theirs.safetensors");

        foreach (var m in new[] { mine, theirs })
            await uow.Models.AddAsync(m);
        await uow.SaveChangesAsync();

        var bulk = await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.Library, Roots, includeMatched: false);
        bulk.Select(c => c.Name).Should().BeEquivalentTo(new[] { "theirs" },
            "a bulk run has no business rewriting metadata the user authored");

        var forced = await uow.SyncStates.SelectIdentifyCandidatesAsync(
            SyncScope.ForModels(mine.Id), Roots, includeMatched: true);
        forced.Select(c => c.Name).Should().BeEquivalentTo(new[] { "mine" },
            "the user pointing at their own model is the user asking for it");
    }

    /// <summary>
    /// C1, second half. Explicit ids are the user pointing at models, so the LoRA-family filter —
    /// which exists to keep a library-wide run off checkpoints — has no business discarding them.
    /// </summary>
    [Fact]
    public async Task IdentifyCandidatesInModelsScopeIgnoreTheLoraFamilyFilter()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var checkpoint = NewLocalModel("checkpoint", @"C:\m\checkpoint.safetensors", type: ModelType.Checkpoint);
        await uow.Models.AddAsync(checkpoint);
        await uow.SaveChangesAsync();

        (await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.ForModels(checkpoint.Id), Roots, includeMatched: true))
            .Select(c => c.Name).Should().BeEquivalentTo(new[] { "checkpoint" });

        (await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.Library, Roots, includeMatched: true))
            .Should().BeEmpty("a library-wide run still only identifies the LoRA family");
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

        var candidates = await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.Library, Roots, includeMatched: false);

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

        var candidates = await uow.SyncStates.SelectIdentifyCandidatesAsync(
            SyncScope.ForFolder(@"E:\Loras"), [@"E:\Loras", @"E:\Loras_backup"], includeMatched: false);

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

        var candidates = await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.ForModels(a.Id, c.Id), Roots, includeMatched: false);

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

        var candidates = await uow.SyncStates.SelectTagCandidatesAsync(SyncScope.Library, Roots);

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

        var candidates = await uow.SyncStates.SelectImageCandidatesAsync(SyncScope.Library, Roots);

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.ModelId.Should().Be(model.Id);
        candidate.VersionId.Should().Be(model.Versions.First(v => v.CivitaiId == 10).Id);
        candidate.CivitaiVersionId.Should().Be(10);
        candidate.Name.Should().Be("imaged");
        candidate.ImagesCheckedAt.Should().Be(imagesCheckedAt);
    }

    // ------------------------------------------------- library scope (R2/R4)

    /// <summary>
    /// "The library" is what the enabled LoRA sources contain — nothing else. Rows left behind by
    /// a source the user has since disabled or removed are still in the database and were the bulk
    /// of the 545 identify candidates the live dry run planned.
    /// </summary>
    [Fact]
    public async Task LibraryScopeExcludesModelsOutsideTheEnabledSources()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var inSource = NewLocalModel("in-source", @"C:\m\in-source.safetensors");
        var outDisabled = NewLocalModel("out-disabled", @"D:\old-source\out-disabled.safetensors");

        foreach (var m in new[] { inSource, outDisabled })
            await uow.Models.AddAsync(m);
        await uow.SaveChangesAsync();

        var candidates = await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.Library, Roots, includeMatched: false);

        candidates.Select(c => c.Name).Should().BeEquivalentTo(new[] { "in-source" });
    }

    [Fact]
    public async Task LibraryScopeUnionsEveryEnabledSource()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var first = NewLocalModel("first-root", @"C:\m\first-root.safetensors");
        var second = NewLocalModel("second-root", @"E:\Loras\second-root.safetensors");
        var neither = NewLocalModel("neither-root", @"D:\elsewhere\neither-root.safetensors");

        foreach (var m in new[] { first, second, neither })
            await uow.Models.AddAsync(m);
        await uow.SaveChangesAsync();

        var candidates = await uow.SyncStates.SelectIdentifyCandidatesAsync(
            SyncScope.Library, [@"C:\m", @"E:\Loras"], includeMatched: false);

        candidates.Select(c => c.Name).Should().BeEquivalentTo(new[] { "first-root", "second-root" });
    }

    /// <summary>Nothing is enabled, so nothing is in the library — for all three selections.</summary>
    [Fact]
    public async Task NoEnabledSourcesSelectsNothingForLibraryScope()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = NewLocalModel("orphan", @"C:\m\orphan.safetensors", civitaiId: 7, versionCivitaiId: 70);
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        (await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.Library, [], includeMatched: false)).Should().BeEmpty();
        (await uow.SyncStates.SelectTagCandidatesAsync(SyncScope.Library, [])).Should().BeEmpty();
        (await uow.SyncStates.SelectImageCandidatesAsync(SyncScope.Library, [])).Should().BeEmpty();
    }

    /// <summary>
    /// Explicit ids are the user pointing at a model, so the library predicate does not apply —
    /// "sync this one" must work for a model whose file sits outside every enabled source.
    /// </summary>
    [Fact]
    public async Task ModelsScopeIgnoresTheEnabledSources()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var outside = NewLocalModel("outside", @"D:\elsewhere\outside.safetensors");
        await uow.Models.AddAsync(outside);
        await uow.SaveChangesAsync();

        var candidates = await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.ForModels(outside.Id), [], includeMatched: false);

        candidates.Select(c => c.Name).Should().BeEquivalentTo(new[] { "outside" });
    }

    /// <summary>
    /// R4 — ghost rows. A model whose file is gone (or was never valid) can still carry a Civitai
    /// id from an old sync; asking Civitai for its tags spends a request on something the user
    /// cannot see. The identify query has always required a valid local file; tags must too.
    /// </summary>
    [Fact]
    public async Task TagCandidatesRequireAValidLocalFileInTheLibrary()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var present = NewLocalModel("present", @"C:\m\present.safetensors", civitaiId: 1);
        var invalidFile = NewLocalModel("invalid-file", @"C:\m\invalid-file.safetensors", civitaiId: 2, validFile: false, verifiedAt: Verified);
        var noFileAtAll = NewLocalModel("no-file", @"C:\m\no-file.safetensors", civitaiId: 3, withFile: false);
        var outsideLibrary = NewLocalModel("outside", @"D:\elsewhere\outside.safetensors", civitaiId: 4);

        foreach (var m in new[] { present, invalidFile, noFileAtAll, outsideLibrary })
            await uow.Models.AddAsync(m);
        await uow.SaveChangesAsync();

        var candidates = await uow.SyncStates.SelectTagCandidatesAsync(SyncScope.Library, Roots);

        candidates.Select(c => c.Name).Should().BeEquivalentTo(new[] { "present" });
    }

    [Fact]
    public async Task ImageCandidatesRequireAValidLocalFileInTheLibrary()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var present = NewLocalModel("present", @"C:\m\present.safetensors", civitaiId: 1, versionCivitaiId: 10);
        var invalidFile = NewLocalModel("invalid-file", @"C:\m\invalid-file.safetensors", civitaiId: 2, versionCivitaiId: 20, validFile: false, verifiedAt: Verified);
        var noFileAtAll = NewLocalModel("no-file", @"C:\m\no-file.safetensors", civitaiId: 3, versionCivitaiId: 30, withFile: false);
        var outsideLibrary = NewLocalModel("outside", @"D:\elsewhere\outside.safetensors", civitaiId: 4, versionCivitaiId: 40);

        foreach (var m in new[] { present, invalidFile, noFileAtAll, outsideLibrary })
            await uow.Models.AddAsync(m);
        await uow.SaveChangesAsync();

        var candidates = await uow.SyncStates.SelectImageCandidatesAsync(SyncScope.Library, Roots);

        candidates.Select(c => c.Name).Should().BeEquivalentTo(new[] { "present" });
    }

    /// <summary>
    /// F1. The prefix is compared against <c>lower(LocalPath)</c> — SQLite's own <c>lower()</c>,
    /// which folds ASCII only (e_sqlite3 ships without ICU). Folding the root with .NET's full
    /// Unicode <c>ToLowerInvariant</c> turned "Ö" into "ö" on one side of the comparison while the
    /// other side kept "Ö", so an ordinary source folder like <c>E:\Öffentlich\Loras</c> matched
    /// nothing at all and every Library plan came back silently empty.
    /// </summary>
    [Fact]
    public async Task LibraryScopeMatchesRootsWithNonAsciiUppercaseLetters()
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Mixed on purpose: the ASCII letters must still fold, the "Ö" must not.
        const string root = @"C:\Öffentlich\LORAS";

        var identify = NewLocalModel("umlaut-identify", @"C:\Öffentlich\LORAS\umlaut-identify.safetensors");
        var tagsAndImages = NewLocalModel("umlaut-tags", @"C:\Öffentlich\LORAS\sub\umlaut-tags.safetensors",
            civitaiId: 9, versionCivitaiId: 90);

        foreach (var m in new[] { identify, tagsAndImages })
            await uow.Models.AddAsync(m);
        await uow.SaveChangesAsync();

        (await uow.SyncStates.SelectIdentifyCandidatesAsync(SyncScope.Library, [root], includeMatched: false))
            .Select(c => c.Name).Should().BeEquivalentTo(new[] { "umlaut-identify" });
        (await uow.SyncStates.SelectTagCandidatesAsync(SyncScope.Library, [root]))
            .Select(c => c.Name).Should().BeEquivalentTo(new[] { "umlaut-tags" });
        (await uow.SyncStates.SelectImageCandidatesAsync(SyncScope.Library, [root]))
            .Select(c => c.Name).Should().BeEquivalentTo(new[] { "umlaut-tags" });
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
