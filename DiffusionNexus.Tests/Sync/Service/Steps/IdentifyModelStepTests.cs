using System.Reflection;
using System.Text.Json;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Service.Services.Sync;
using DiffusionNexus.Service.Services.Sync.Identity;
using DiffusionNexus.Service.Services.Sync.Steps;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using static DiffusionNexus.Tests.Sync.Service.Identity.SafetensorsFixture;

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

    // Safetensors(...) / Meta(...) — the safetensors byte-layout builders this class uses to
    // exercise IdentifyModelStep's header rung against a file SafetensorsHeaderReader actually
    // parses — come from the shared SafetensorsFixture (see the using static above), so this
    // class's copy can never drift from SafetensorsHeaderReaderTests'.

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
        int? civitaiId = null,
        bool isUserEdited = false,
        ModelType type = ModelType.LORA)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = new Model
        {
            Name = name,
            Type = type,
            Source = civitaiId is null ? DataSource.LocalFile : DataSource.CivitaiApi,
            CivitaiId = civitaiId,
            IsUserEdited = isUserEdited,
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

    /// <summary>The stored <see cref="Model.Type"/> — what the kind-correction tests assert on.</summary>
    private async Task<ModelType> LoadTypeAsync(int modelId)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var model = await uow.Models.GetByIdAsync(modelId);
        return model!.Type;
    }

    /// <summary>
    /// Seeds a local-only model (no sidecar, no Civitai id — see <see cref="SeedAsync"/>) whose file
    /// is a real safetensors container built from <paramref name="headerJson"/>, stamped with a
    /// starting <see cref="Model.Type"/> the way the name-only backfill (or a legacy row) would have
    /// left it, and hands back a hand-built <see cref="IdentifyCandidate"/> — the same
    /// skip-<c>SelectAsync</c> pattern <see cref="BuildCandidateAsync"/> already uses below.
    /// </summary>
    /// <remarks>
    /// Deliberately bypasses <see cref="IdentifyModelStep.SelectAsync"/>: <c>SelectIdentifyCandidatesAsync</c>
    /// filters a library-scoped run to <c>LoraFamily</c> (LORA/LoCon/DoRA/Unknown), so a row already
    /// typed <see cref="ModelType.VAE"/> — the exact starting point <see cref="CorrectsAMisnamedLoraFromItsWeights"/>
    /// needs — would never come back as a candidate at all under that scope, for a reason that has
    /// nothing to do with the correction logic under test here. These tests are about what
    /// <c>ExecuteOneAsync</c> does once handed such a row, not about whether a bulk scan would ever
    /// hand it one — the same reasoning <see cref="ExecuteOneAsync_WithASidecarPresent_NeverCallsCivitai"/>
    /// already relies on for its own hand-built candidate.
    /// </remarks>
    /// <param name="rawBytes">
    /// Bytes to write instead of a real container — for the one case that needs a
    /// <c>.safetensors</c> whose header CANNOT be parsed. Default null writes
    /// <paramref name="headerJson"/> as a proper container.
    /// </param>
    private async Task<IdentifyCandidate> GivenLocalModelAsync(string fileName, ModelType type, string headerJson,
        byte[]? rawBytes = null)
    {
        var path = NewModelFile(fileName, rawBytes ?? Safetensors(headerJson));
        var name = Path.GetFileNameWithoutExtension(fileName);
        var (modelId, fileId) = await SeedAsync(name, path, type: type);

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var model = await uow.Models.GetByIdWithIncludesAsync(modelId);
        var versionId = model!.Versions.Single().Id;

        return new IdentifyCandidate(modelId, versionId, fileId, name, path, Sha256: null,
            BaseModelRaw: "???", Outcome: SyncOutcome.None, CheckedAt: null, Attempts: 0, SidecarSignature: null);
    }

    /// <summary>
    /// Runs the step directly over <paramref name="candidate"/> against a 404-on-Civitai client, with
    /// no sidecar on disk — the "not on Civitai and no sidecar" branch that reads the file's header.
    /// </summary>
    private async Task WhenIdentifiedAsync(IdentifyCandidate candidate)
    {
        var step = NewNotFoundStep();
        await step.ExecuteOneAsync(new SyncItem(candidate.ModelId, candidate.Name, candidate), apiKey: null, CancellationToken.None);
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
        // Checked yesterday — inside the 30-day window, so only the force makes it due.
        var recent = await SeedAsync("recent", NewModelFile("f-recent.safetensors"),
            outcome: SyncOutcome.NotIdentified, checkedAt: Now.AddDays(-1), withState: true);
        var gone = await SeedAsync("gone", MissingModelFile("f-gone.safetensors"));

        var items = await NewNotFoundStep().SelectAsync(SyncScope.Library, Options(force: true), Now, CancellationToken.None);

        items.Select(i => i.ModelId).Should().Contain(recent.ModelId);
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
    /// The library-wide force is the plan dialog's "Models not found on Civitai" checkbox, and it
    /// must mean what it says: a Matched model is left alone — whether it carries the CivitaiId
    /// itself or is the duplicate copy that only carries the page id (null CivitaiId, outcome
    /// Matched) — and a hand-edited model is not dragged into a bulk overwrite run. Only the
    /// per-tile scope (the user pointing at one model) re-looks at a Matched row; that path is
    /// pinned by <see cref="Select_ForceIdentifyIncludesMatchedModel"/>. The not-found ones are
    /// still forced past their retry window, which is the checkbox's whole purpose.
    /// </summary>
    [Fact]
    public async Task Select_LibraryForceIdentifyLeavesMatchedAndHandEditedAlone()
    {
        var matched = await SeedAsync("lf-matched", NewModelFile("lf-matched.safetensors"), civitaiId: 77,
            outcome: SyncOutcome.Matched, checkedAt: Now.AddDays(-1), withState: true);
        var duplicate = await SeedAsync("lf-duplicate", NewModelFile("lf-duplicate.safetensors"),
            outcome: SyncOutcome.Matched, checkedAt: Now.AddDays(-1), withState: true);
        var edited = await SeedAsync("lf-edited", NewModelFile("lf-edited.safetensors"), isUserEdited: true,
            outcome: SyncOutcome.NotIdentified, checkedAt: Now.AddDays(-1), withState: true);
        var notFound = await SeedAsync("lf-notfound", NewModelFile("lf-notfound.safetensors"),
            outcome: SyncOutcome.NotIdentified, checkedAt: Now.AddDays(-1), withState: true);

        var items = await NewNotFoundStep().SelectAsync(SyncScope.Library, Options(force: true), Now, CancellationToken.None);

        items.Select(i => i.ModelId).Should().BeEquivalentTo([notFound.ModelId]);
        items.Should().NotContain(i => i.ModelId == matched.ModelId);
        items.Should().NotContain(i => i.ModelId == duplicate.ModelId);
        items.Should().NotContain(i => i.ModelId == edited.ModelId);
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

    /// <summary>
    /// One identify item is two Civitai requests — the hash lookup here and the model page inside
    /// the applier. Pacing between them is the gateway's job now (verified in
    /// <c>CivitaiApiGatewayTests</c>); what this step still owns is making both calls.
    /// </summary>
    [Fact]
    public async Task Execute_MakesBothCivitaiCalls()
    {
        var path = NewModelFile("paced.safetensors");
        var (modelId, _) = await SeedAsync("paced", path);

        var civVersion = NewCivitaiVersion();
        var client = new Mock<ICivitaiClient>();
        client.Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(civVersion);
        client.Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewCivitaiModel(civVersion));

        var step = new IdentifyModelStep(
            Scopes, client.Object,
            new CivitaiMetadataApplier(client.Object, logger: null),
            new SidecarMetadataApplier(),
            logger: null);

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        client.Verify(c => c.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
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

    /// <summary>
    /// Finding 3 (review of PR #547): reordering the sidecar read ahead of the hash lookup left
    /// <c>ExecuteOneAsync</c> returning at the <c>sidecar.Applied</c> branch before
    /// <c>ResolveHashAsync</c> ever ran — so the real bytes were never hashed, and
    /// <c>SidecarMetadataApplier</c>'s own <c>dbFile.HashSHA256 ??= ...</c> let the sidecar's
    /// CLAIMED SHA256 stand as the stored one. This sidecar deliberately lies about the hash (a
    /// syntactically valid but wrong 64-hex digest); the stored <c>ModelFile.HashSHA256</c> must
    /// end up as the real digest of the file's actual bytes, never that claim.
    /// </summary>
    [Fact]
    public async Task Execute_SidecarWithWrongHashStillStoresTheRealDigest()
    {
        var path = NewModelFile("sidecar-wrong-hash.safetensors");
        var (modelId, fileId) = await SeedAsync("sidecar-wrong-hash", path);

        var wrongHash = new string('F', 64);
        var sidecar = """
        {"id":700,"modelId":77,"baseModel":"Pony","trainedWords":["x"],
         "model":{"name":"N","nsfw":false},
         "files":[{"name":"sidecar-wrong-hash.safetensors","primary":true,"hashes":{"SHA256":"__WRONG_HASH__"}}],
         "images":[]}
        """.Replace("__WRONG_HASH__", wrongHash);
        await File.WriteAllTextAsync(Path.Combine(_tempDir.FullName, "sidecar-wrong-hash.civitai.info"), sidecar);

        var expectedHash = FileHasher.Sha256Upper(path);
        expectedHash.Should().NotBe(wrongHash, "the test fixture must not accidentally collide with the lie");

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        (await ReadStateAsync(modelId))!.MetadataOutcome.Should().Be(SyncOutcome.Sidecar);

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var file = await uow.ModelFiles.GetByIdAsync(fileId);
        file!.HashSHA256.Should().Be(expectedHash,
            "the stored hash must be a measurement of the real bytes, never the sidecar's unverified claim");
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

    /// <summary>
    /// Plan C. No Civitai match, no sidecar, but the file's own safetensors header names its
    /// architecture — the identify chain's second rung.
    /// </summary>
    [Fact]
    public async Task Execute_HeaderIdentifiesAnSdxlLora()
    {
        var path = NewModelFile("header-sdxl.safetensors",
            Safetensors(Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base"))));
        var (modelId, _) = await SeedAsync("header-sdxl", path);

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();

        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.Header);
        state.HeaderCheckedAt.Should().Be(state.MetadataCheckedAt);

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.Versions.Single().BaseModelRaw.Should().Be("SDXL 1.0");
        saved.Versions.Single().BaseModel.Should().Be(BaseModelType.SDXL10);

        // C2: the write landed, so the model is stamped exactly like the sidecar branch stamps it —
        // no longer "never synced" to anything that reads LastSyncedAt (TileGroupingHelper's tile
        // ordering among them). Compared against the stamp the step actually wrote (its wall clock,
        // ExecuteOneAsync's own `now`), not the fixture's fixed `Now` used only for due-ness.
        saved.Source.Should().Be(DataSource.LocalFile);
        saved.LastSyncedAt.Should().Be(state.MetadataCheckedAt);

        // Stamped at Now like everything else — the same run's clock is not due again immediately.
        var again = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        again.Select(i => i.ModelId).Should().NotContain(modelId);
    }

    /// <summary>
    /// A row already stamped VAE, whose weights are actually a LoRA's: this rung corrects it, but
    /// only via an explicit per-model sync. A bulk library run's own candidate-selection filter
    /// (<c>SelectIdentifyCandidatesAsync</c>'s LoraFamily set) excludes a VAE-typed row before
    /// <c>ExecuteOneAsync</c> is ever reached, so this direction never fires on an ordinary sync —
    /// see <see cref="GivenLocalModelAsync"/>'s remarks for why this test bypasses
    /// <c>SelectAsync</c> to reach the branch directly instead of asserting on a path that, for
    /// this starting type, a bulk run would never take.
    /// </summary>
    [Fact]
    public async Task CorrectsAMisnamedLoraFromItsWeights()
    {
        // A row the name-only backfill flipped to VAE, whose weights are a LoRA's.
        var candidate = await GivenLocalModelAsync(
            fileName: "vae_finetune_lora.safetensors",
            type: ModelType.VAE,
            headerJson: Tensors("lora_unet_blocks_0.lora_up.weight"));

        await WhenIdentifiedAsync(candidate);

        (await LoadTypeAsync(candidate.ModelId)).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// The second-order effect of the final review's Critical #1, caught in self-review. A
    /// <c>.safetensors</c> we FAILED to open now answers <c>LORA</c> from <c>AssetKindResolver</c> —
    /// correctly, as a safe default for a row that already says LORA. But a default is not a
    /// reading, and it must not unstamp a support kind an earlier, successful reading established:
    /// a VAE row would otherwise be demoted to LORA by a moment's file lock. (Reached only through
    /// an explicit per-model re-check; <c>SelectIdentifyCandidatesAsync</c> filters every other
    /// scope to LoraFamily — see <see cref="GivenLocalModelAsync"/>'s remarks.)
    /// </summary>
    [Fact]
    public async Task AnUnreadableContainerDoesNotDemoteAnAlreadyClassifiedSupportAsset()
    {
        var candidate = await GivenLocalModelAsync(
            fileName: "opaque_name_nobody_can_read.safetensors",
            type: ModelType.VAE,
            headerJson: "",
            rawBytes: [0x01, 0x02, 0x03]);   // not a parsable safetensors header

        await WhenIdentifiedAsync(candidate);

        (await LoadTypeAsync(candidate.ModelId)).Should().Be(ModelType.VAE,
            "we learned nothing about this file, and nothing is not grounds for rewriting its kind");
    }

    /// <summary>
    /// The other direction fires on any run, bulk or explicit: a VAE discovered before the feature
    /// existed, whose row still says LORA and whose name carries no marker, is named by its
    /// weights the moment <c>ExecuteOneAsync</c> reads them — LORA stays inside the LoraFamily set,
    /// so an ordinary library sync reaches this row exactly as an explicit one would.
    /// </summary>
    [Fact]
    public async Task NamesASupportAssetFromItsWeights()
    {
        var candidate = await GivenLocalModelAsync(
            fileName: "opaque_name_nobody_can_read.safetensors",
            type: ModelType.LORA,
            headerJson: Tensors("post_quant_conv.weight"));

        await WhenIdentifiedAsync(candidate);

        (await LoadTypeAsync(candidate.ModelId)).Should().Be(ModelType.VAE);
    }

    /// <summary>
    /// <b>This test asserted the opposite until the #527 smoke, and the assertion was wrong.</b> It
    /// was written for the Task 8 guard <c>Source != DataSource.CivitaiApi</c>, on the reasoning
    /// that a Civitai-sourced row carries an authoritative <c>Type</c> that is not ours to move.
    /// Nothing in a Civitai payload has ever written <c>Model.Type</c>: the column has exactly two
    /// writers in the whole codebase, this step and discovery's own <c>AssetKindResolver</c> call,
    /// while <c>CivitaiMetadataApplier</c> and <c>SidecarMetadataApplier</c> write name, creator,
    /// tags, images, ids and hashes and leave <c>Type</c> alone. So the guard protected a value
    /// Civitai never set, and blocked the correction on every Civitai-touched row — three real text
    /// encoders stayed LORA behind it, one of them (<c>qwen_3_4b</c>) carrying <c>CivitaiApi</c>
    /// with a NULL <c>CivitaiId</c>, i.e. a row Civitai had never identified at all.
    /// <para>
    /// The shape is still worth pinning, because it is still the shape that reaches this branch on
    /// an ordinary library run: a duplicate-page-id row, <c>Source</c> already
    /// <see cref="DataSource.CivitaiApi"/> from an earlier match and <c>CivitaiId</c> still null, is
    /// NOT excluded by <c>SelectIdentifyCandidatesAsync</c>'s <c>CivitaiId == null</c> filter. What
    /// changed is the expected answer. See #550 for the writer that would make Civitai's type
    /// authoritative — and would need a signal saying so, which the <c>Source</c> column is not.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Execute_HeaderCorrectsACivitaiSourcedModelsType()
    {
        var path = NewModelFile("civitai-sourced-type.safetensors", Safetensors(Tensors("post_quant_conv.weight")));
        var (modelId, _) = await SeedAsync("civitai-sourced-type", path);

        // Duplicate-page-id shape: Source already CivitaiApi, CivitaiId still null.
        using (var seedScope = NewScope())
        {
            var seedUow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var seeded = await seedUow.Models.GetByIdAsync(modelId);
            seeded!.Source = DataSource.CivitaiApi;
            await seedUow.SaveChangesAsync();
        }

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        (await LoadTypeAsync(modelId)).Should().Be(ModelType.VAE,
            "the weights are the only thing that has ever decided this column, whatever the Source says");
    }

    /// <summary>
    /// I2 (Task 8 review fix), the <c>IsUserEdited</c> half. An explicit per-model re-check (the
    /// per-tile "Download Metadata" button, <see cref="SyncScope.ForModels"/>) passes
    /// <c>includeMatched: true</c>, which skips <c>SelectIdentifyCandidatesAsync</c>'s
    /// <c>CivitaiId == null &amp;&amp; !IsUserEdited</c> filter entirely — a hand-edited row is
    /// selected, and if this run's hash lookup misses, it reaches the guard just like the
    /// Civitai-sourced row above.
    /// </summary>
    [Fact]
    public async Task Execute_HeaderDoesNotOverwriteAUserEditedModelsType()
    {
        var path = NewModelFile("user-edited-type.safetensors", Safetensors(Tensors("post_quant_conv.weight")));
        var (modelId, _) = await SeedAsync("user-edited-type", path, isUserEdited: true);

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.ForModels(modelId), Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        (await LoadTypeAsync(modelId)).Should().Be(ModelType.LORA,
            "a user-edited model's Type is not ours to move, even though the header disagrees");
    }

    /// <summary>
    /// The header rung supplies ONE field, so it may claim a model nothing else has claimed — but
    /// it must not relabel a model whose name, tags, images and CivitaiId all came from Civitai.
    /// A taken-down upstream page (404) next to a still-readable header is exactly that case.
    /// </summary>
    [Fact]
    public async Task Execute_HeaderWriteDoesNotRelabelACivitaiSourcedModel()
    {
        var path = NewModelFile("civitai-sourced.safetensors",
            Safetensors(Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base"))));
        var (modelId, _) = await SeedAsync("civitai-sourced", path);

        // This model came out of Civitai in an earlier life; its page 404s now.
        using (var seedScope = NewScope())
        {
            var seedUow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var seeded = await seedUow.Models.GetByIdAsync(modelId);
            seeded!.Source = DataSource.CivitaiApi;
            await seedUow.SaveChangesAsync();
        }

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        var state = await ReadStateAsync(modelId);

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);

        // The write lands...
        saved!.Versions.Single().BaseModelRaw.Should().Be("SDXL 1.0");

        // ...and the freshness stamp lands with it, because something really was applied...
        saved.LastSyncedAt.Should().Be(state!.MetadataCheckedAt);

        // ...but one locally-read field does not make a Civitai-sourced model locally sourced.
        saved.Source.Should().Be(DataSource.CivitaiApi);
    }

    /// <summary>
    /// Pony/Illustrious/NoobAI checkpoints are all trained on plain SDXL architecture, so the
    /// model-name hint must win over the coarser architecture reading — <see cref="BaseModelHeaderMap"/>'s
    /// own evaluation order, exercised here through the step.
    /// </summary>
    [Fact]
    public async Task Execute_NameHintBeatsArchitecture()
    {
        var path = NewModelFile("header-pony.safetensors",
            Safetensors(Meta(
                ("modelspec.architecture", "stable-diffusion-xl-v1-base"),
                ("ss_sd_model_name", "ponyDiffusionV6XL"))));
        var (modelId, _) = await SeedAsync("header-pony", path);

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        (await ReadStateAsync(modelId))!.MetadataOutcome.Should().Be(SyncOutcome.Header);

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.Versions.Single().BaseModelRaw.Should().Be("Pony");
    }

    /// <summary>
    /// The header rung only fills a placeholder — a version that already carries a real base model
    /// (from an earlier sidecar apply, say) is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task Execute_HeaderDoesNotOverwriteARealBaseModel()
    {
        var path = NewModelFile("header-real.safetensors",
            Safetensors(Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base"))));
        var (modelId, _) = await SeedAsync("header-real", path);

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = await uow.Models.GetByIdWithIncludesAsync(modelId);
            model!.Versions.Single().BaseModelRaw = "Flux.1 D";
            await uow.SaveChangesAsync();
        }

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        // C1: the write was skipped, so the outcome must NOT claim the header rung — that would
        // read as "Identity source: file header" directly under a Base Model the header never
        // actually supplied. There was no settled identity to preserve (a fresh row), so it falls
        // back to NotIdentified. The header WAS still read, though — HeaderCheckedAt says so.
        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.NotIdentified);
        state.HeaderCheckedAt.Should().NotBeNull();

        using var readScope = NewScope();
        var readUow = readScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await readUow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.Versions.Single().BaseModelRaw.Should().Be("Flux.1 D");

        // C2: no write, no sync stamp either.
        saved.LastSyncedAt.Should().BeNull();
    }

    /// <summary>
    /// SF2. A legacy row blanked by the pre-F6 sidecar-blanking bug carries <c>""</c>, not
    /// <c>"???"</c> — <see cref="SyncStateDeriver.IsPlaceholder"/> already treats that as "carries
    /// no information" and selects it for identification, so <see cref="BaseModelWriter.CanFill"/>
    /// must agree, or the header rung finds the answer, stamps <see cref="SyncOutcome.Header"/> (a
    /// chip that reads as resolved), and never actually writes it.
    /// </summary>
    [Fact]
    public async Task Execute_HeaderFillsALegacyBlankBaseModelRaw()
    {
        var path = NewModelFile("header-blank.safetensors",
            Safetensors(Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base"))));
        var (modelId, _) = await SeedAsync("header-blank", path);

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = await uow.Models.GetByIdWithIncludesAsync(modelId);
            model!.Versions.Single().BaseModelRaw = string.Empty;
            await uow.SaveChangesAsync();
        }

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        (await ReadStateAsync(modelId))!.MetadataOutcome.Should().Be(SyncOutcome.Header);

        using var readScope = NewScope();
        var readUow = readScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await readUow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.Versions.Single().BaseModelRaw.Should().Be("SDXL 1.0", "a blank string is a missing answer, same as \"???\"");
    }

    /// <summary>
    /// The header rung honors <see cref="ModelVersion.IsUserEdited"/> exactly like the sidecar
    /// formats do — a user's own edit is never overwritten, even when the stored value is still the
    /// legacy placeholder.
    /// </summary>
    [Fact]
    public async Task Execute_HeaderRespectsUserEditedVersion()
    {
        var path = NewModelFile("header-useredited.safetensors",
            Safetensors(Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base"))));
        var (modelId, _) = await SeedAsync("header-useredited", path);

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = await uow.Models.GetByIdWithIncludesAsync(modelId);
            model!.Versions.Single().IsUserEdited = true;
            await uow.SaveChangesAsync();
        }

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        // C1: same reasoning as Execute_HeaderDoesNotOverwriteARealBaseModel — CanFill said no
        // (the version is user-edited), so the header rung is not credited with a value it never
        // actually wrote.
        (await ReadStateAsync(modelId))!.MetadataOutcome.Should().Be(SyncOutcome.NotIdentified);

        using var readScope = NewScope();
        var readUow = readScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await readUow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.Versions.Single().BaseModelRaw.Should().Be("???");
    }

    /// <summary>
    /// C1, the reviewer's exact scenario. Run 1: a sidecar identifies the model as Pony. The user
    /// then deletes the <c>.civitai.info</c>. Run 2: 404, no sidecar, and the file's own header
    /// parses as plain SDXL — a real value, but <see cref="BaseModelWriter.CanFill"/> says no
    /// (Pony is already real), so nothing is written. Before this fix the outcome was stamped
    /// <see cref="SyncOutcome.Header"/> anyway, purely because the header *said* something —
    /// leaving the detail panel's "Identity source: file header" row directly under a Base Model
    /// of Pony the header never actually supplied. Both the value and the <c>Sidecar</c> outcome
    /// must survive the header run untouched, and a third run must not churn — the sidecar's
    /// disappearance was already recorded as new evidence and consumed.
    /// </summary>
    [Fact]
    public async Task Execute_SidecarDeletedThenHeaderRunPreservesSidecarOutcome()
    {
        var path = NewModelFile("sidecar-then-header.safetensors",
            Safetensors(Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base"))));
        var (modelId, _) = await SeedAsync("sidecar-then-header", path);

        // Deliberately no "modelId": setting Model.CivitaiId would exclude the model from the next
        // ordinary (non-forced) Library selection entirely — a real effect, but not the one this
        // test is about — so run 2 would never even see it as a candidate.
        var sidecarPath = Path.Combine(_tempDir.FullName, "sidecar-then-header.civitai.info");
        var sidecarJson = """
        {"id":700,"baseModel":"Pony","trainedWords":["x"],
         "model":{"name":"N","nsfw":false},
         "files":[{"name":"sidecar-then-header.safetensors","primary":true,"hashes":{"SHA256":"ABC"}}],
         "images":[]}
        """;
        await File.WriteAllTextAsync(sidecarPath, sidecarJson);

        var step = NewNotFoundStep();

        // Run 1: the sidecar identifies the model as Pony.
        var run1Items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(run1Items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        (await ReadStateAsync(modelId))!.MetadataOutcome.Should().Be(SyncOutcome.Sidecar);

        // The user deletes the sidecar.
        File.Delete(sidecarPath);

        // Run 2: the sidecar's disappearance is new evidence, so this is due immediately — no need
        // to wait out the 30-day window (the same bypass Select_ANewSidecarMakesAHeaderIdentifiedModelDue
        // exercises for a sidecar *appearing*).
        var run2Items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        run2Items.Select(i => i.ModelId).Should().Contain(modelId);

        await step.ExecuteOneAsync(run2Items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.Sidecar,
            "the header never actually wrote anything, so the prior settled identity must be preserved");
        state.HeaderCheckedAt.Should().NotBeNull("the header WAS read this run, even though nothing came of it");

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
            saved!.Versions.Single().BaseModelRaw.Should().Be(
                "Pony", "the sidecar's value must survive a header run that found nothing new to say");
        }

        // A third run must not churn: the deletion was already consumed as evidence on run 2, and
        // run 2's stamp recorded the file's current (sidecar-less) signature.
        var run3Items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        run3Items.Select(i => i.ModelId).Should().NotContain(modelId);
    }

    /// <summary>
    /// A safetensors file whose header parses fine but carries none of the three metadata keys —
    /// the header rung reads it (and stamps <c>HeaderCheckedAt</c>), finds nothing, and the
    /// filename heuristic is the last resort before giving up.
    /// </summary>
    [Fact]
    public async Task Execute_FilenameHeuristicIsTheLastResortBeforeNotIdentified()
    {
        var path = NewModelFile("MyChar_Pony_v2.safetensors",
            Safetensors("""{"tensor.weight":{"dtype":"F16","shape":[4],"data_offsets":[0,8]}}"""));
        var (modelId, _) = await SeedAsync("MyChar_Pony_v2", path);

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.Heuristic);
        // The header WAS read — it just said nothing usable.
        state.HeaderCheckedAt.Should().NotBeNull();

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.Versions.Single().BaseModelRaw.Should().Be("Pony");

        // C2: the heuristic rung's write is stamped exactly like the header rung's.
        saved.Source.Should().Be(DataSource.LocalFile);
        saved.LastSyncedAt.Should().Be(state.MetadataCheckedAt);
    }

    /// <summary>
    /// A non-safetensors model file (a raw <c>.pt</c> checkpoint) never reaches
    /// <see cref="SafetensorsHeaderReader"/>'s successful path — <c>TryRead</c> rejects the
    /// extension outright — so the step must skip straight to the filename heuristic without
    /// stamping a header check that never happened.
    /// </summary>
    [Fact]
    public async Task Execute_NonSafetensorsFileSkipsStraightToHeuristic()
    {
        var path = NewModelFile("style_sdxl.pt");
        var (modelId, _) = await SeedAsync("style_sdxl", path);

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.Heuristic);
        state.HeaderCheckedAt.Should().BeNull();

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.Versions.Single().BaseModelRaw.Should().Be("SDXL 1.0");
    }

    /// <summary>
    /// Every rung — Civitai, sidecar, header, filename — comes up empty: the file is genuinely
    /// unidentifiable and no base model is written.
    /// </summary>
    [Fact]
    public async Task Execute_NothingMatchesStampsNotIdentified()
    {
        var path = NewModelFile("totally_random_name.pt");
        var (modelId, _) = await SeedAsync("totally_random_name", path);

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        (await ReadStateAsync(modelId))!.MetadataOutcome.Should().Be(SyncOutcome.NotIdentified);

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.Versions.Single().BaseModelRaw.Should().Be("???", "nothing in the chain identified it");

        // C2: nothing was written, so the model must not be stamped as synced either.
        saved.LastSyncedAt.Should().BeNull();
    }

    /// <summary>
    /// A sidecar still wins over the header even when the header would have named something
    /// different — the sidecar branch returns before the header is ever read.
    /// </summary>
    [Fact]
    public async Task Execute_SidecarStillBeatsTheHeader()
    {
        var path = NewModelFile("sidecar-vs-header.safetensors",
            Safetensors(Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base"))));
        var (modelId, _) = await SeedAsync("sidecar-vs-header", path);

        var sidecar = """
        {"id":700,"modelId":77,"baseModel":"Pony","trainedWords":["x"],
         "model":{"name":"N","nsfw":false},
         "files":[{"name":"sidecar-vs-header.safetensors","primary":true,"hashes":{"SHA256":"ABC"}}],
         "images":[]}
        """;
        await File.WriteAllTextAsync(Path.Combine(_tempDir.FullName, "sidecar-vs-header.civitai.info"), sidecar);

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        (await ReadStateAsync(modelId))!.MetadataOutcome.Should().Be(SyncOutcome.Sidecar);
    }

    /// <summary>
    /// A Civitai hash hit never falls through to the header/heuristic rungs at all — the response's
    /// own <c>baseModel</c> wins even when the file's header would have said something else.
    /// </summary>
    [Fact]
    public async Task Execute_CivitaiHitNeverConsultsTheHeader()
    {
        var path = NewModelFile("matched-header.safetensors",
            Safetensors(Meta(("ss_sd_model_name", "ponyDiffusionV6XL"))));
        var (modelId, _) = await SeedAsync("matched-header", path);

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
        (await ReadStateAsync(modelId))!.MetadataOutcome.Should().Be(SyncOutcome.Matched);

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.Versions.Single().BaseModelRaw.Should().Be("SDXL 1.0", "the civitai response's baseModel, not the header's Pony hint");
    }

    /// <summary>
    /// Extends the signature-evidence test family (<see cref="Select_ChangedSidecarMakesCandidateDueImmediately"/>)
    /// to the Header outcome: a sidecar dropped in after a header-only identification is new
    /// evidence and beats the 30-day window exactly the way it does for Sidecar/NotIdentified.
    /// </summary>
    [Fact]
    public async Task Select_ANewSidecarMakesAHeaderIdentifiedModelDue()
    {
        var path = NewModelFile("header-then-sidecar.safetensors",
            Safetensors(Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base"))));
        var (modelId, _) = await SeedAsync("header-then-sidecar", path);

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.Header);
        state.SidecarSignature.Should().Be(string.Empty);

        await File.WriteAllTextAsync(Path.Combine(_tempDir.FullName, "header-then-sidecar.civitai.info"), "{}");

        var again = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        again.Select(i => i.ModelId).Should().Contain(modelId);
    }

    /// <summary>
    /// B2. A sidecar that cannot be parsed is still a sidecar with a signature. Stamping the empty
    /// one the applier used to report made the model differ from its own file on the very next
    /// plan, so it was re-hashed and re-queried on every single run — forever, for as long as the
    /// bad file sat there.
    /// </summary>
    [Fact]
    public async Task Execute_MalformedSidecarIsNotDueOnTheNextPlan()
    {
        var path = NewModelFile("malformed.safetensors");
        var (modelId, _) = await SeedAsync("malformed", path);

        // Truncated mid-object: JsonDocument.Parse throws.
        await File.WriteAllTextAsync(Path.Combine(_tempDir.FullName, "malformed.civitai.info"), "{ \"name\": 123 ");
        var expectedSignature = SidecarMetadataApplier.Find(path).Signature;
        expectedSignature.Should().NotBeEmpty();

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();

        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.NotIdentified);
        state.SidecarSignature.Should().Be(expectedSignature);

        // The next plan sees a file that has not changed since it was looked at, so it leaves it be.
        var next = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        next.Select(i => i.ModelId).Should().NotContain(modelId);
    }

    /// <summary>
    /// B3. A kohya training config saved as <c>MyLora.json</c> is not metadata. The applier used to
    /// report every existing sidecar as applied, so the model was stamped <see cref="SyncOutcome.Sidecar"/>
    /// — a verdict that says "we know what this is" about a file nothing was read from.
    /// </summary>
    [Fact]
    public async Task Execute_UnrelatedJsonSidecarStampsNotIdentified()
    {
        var path = NewModelFile("MyLora.safetensors");
        var (modelId, _) = await SeedAsync("MyLora", path);

        await File.WriteAllTextAsync(
            Path.Combine(_tempDir.FullName, "MyLora.json"),
            """{"ss_learning_rate":"1e-4"}""");
        var expectedSignature = SidecarMetadataApplier.Find(path).Signature;

        var step = NewNotFoundStep();
        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items.Single(i => i.ModelId == modelId), apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();

        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.NotIdentified);
        // The signature is still recorded, so the file is not re-read until it changes.
        state.SidecarSignature.Should().Be(expectedSignature);

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        (await uow.Models.GetByIdAsync(modelId))!.LastSyncedAt.Should().BeNull();
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
        // Probe relative to the stamp the step actually wrote (its wall clock), not the fixture's
        // fixed Now — anchoring on Now made this a date bomb that started failing 2026-08-22.
        var checkedAt = state.MetadataCheckedAt!.Value;
        var policy = SyncRetryPolicy.Default;
        policy.IsIdentifyDue(SyncOutcome.Error, checkedAt, state.MetadataAttempts, checkedAt.AddHours(1), force: false)
            .Should().BeFalse();
        policy.IsIdentifyDue(SyncOutcome.Error, checkedAt, state.MetadataAttempts, checkedAt.AddDays(2), force: false)
            .Should().BeTrue();
        // …and gives up after MaxErrorAttempts.
        policy.IsIdentifyDue(SyncOutcome.Error, checkedAt, policy.MaxErrorAttempts, checkedAt.AddDays(2), force: false)
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

    /// <summary>
    /// I1. A rejected save is a fault of one item's data, not of the run. It used to escape the
    /// step's catch filter entirely, so <c>LibrarySyncService</c> rethrew it and one bad model
    /// aborted the whole sync — every model behind it in the queue silently went unchecked.
    /// </summary>
    /// <remarks>
    /// The collision is a real one, not a synthetic constraint: <c>Creator.Username</c> is uniquely
    /// indexed, and the applier renames a model's existing creator to whatever the response says
    /// without checking whether that name is already taken (a creator renaming themselves on
    /// Civitai to a name another local model's creator already holds does exactly this).
    /// </remarks>
    [Fact]
    public async Task Execute_DbUpdateExceptionIsRecordedNotRethrown()
    {
        var path = NewModelFile("db-conflict.safetensors");
        var (modelId, _) = await SeedAsync("db-conflict", path);

        // The candidate owns creator "old-name"; another model already owns "author", which is the
        // username the Civitai response carries.
        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = await uow.Models.GetByIdWithIncludesAsync(modelId);
            model!.Creator = new Creator { Username = "old-name" };

            var other = new Model { Name = "other", Type = ModelType.LORA, Source = DataSource.CivitaiApi };
            other.Creator = new Creator { Username = "author" };
            await uow.Models.AddAsync(other);
            await uow.SaveChangesAsync();
        }

        var civVersion = NewCivitaiVersion();
        var civModel = new CivitaiModel
        {
            Id = 77,
            Name = "Civitai Name",
            Tags = [],
            ModelVersions = [civVersion],
            Creator = new CivitaiCreator { Username = "author" },
        };

        var step = NewStep(c =>
        {
            c.Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(civVersion);
            c.Setup(x => x.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(civModel);
        });

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var item = items.Single(i => i.ModelId == modelId);

        var result = await step.ExecuteOneAsync(item, apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Skipped.Should().BeFalse();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();

        // Recorded as an attempt, so the retry policy bounds it instead of retrying forever.
        var state = await ReadStateAsync(modelId);
        state!.MetadataOutcome.Should().Be(SyncOutcome.Error);
        state.MetadataAttempts.Should().Be(1);
        state.LastError.Should().NotBeNullOrWhiteSpace();

        // The rejected graph is gone: the stamp is the only thing that survived the failure.
        using var readScope = NewScope();
        var readUow = readScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await readUow.Models.GetByIdWithIncludesAsync(modelId);
        saved!.Name.Should().Be("db-conflict");
        saved.Creator!.Username.Should().Be("old-name");
    }

    /// <summary>
    /// Renamed from <c>Execute_CancellationDuringSidecarDoesNotStamp</c>. Under the old (hash
    /// first) ordering this exercised the sidecar branch's own cancellation check; under the new
    /// (sidecar first) ordering the candidate has no sidecar on disk, so
    /// <see cref="SidecarMetadataApplier.ApplyAsync"/> returns "nothing applied" long before the
    /// token is ever cancelled — the cancel below fires as a side effect of the mocked hash lookup,
    /// which still answers its normal 404 (<c>ReturnsAsync</c>, not a throw), so execution falls
    /// through into the header rung. What actually throws is the pre-existing
    /// <c>ct.ThrowIfCancellationRequested()</c> immediately after the header read, further down —
    /// not the sidecar branch's check. <see cref="Execute_CancellationDuringARealSidecarReadDoesNotStamp"/>
    /// below is what pins the sidecar branch's own check with a real sidecar on disk.
    /// </summary>
    [Fact]
    public async Task Execute_CancellationDuringHashLookupSurfacesAtTheHeaderCheck()
    {
        var path = NewModelFile("cancel-after-hash.safetensors");
        var (modelId, _) = await SeedAsync("cancel-after-hash", path);

        using var cts = new CancellationTokenSource();

        // No sidecar on disk: the sidecar branch is a fast "nothing applied" before any of this
        // runs. The hash lookup below cancels the token as a side effect but still returns its
        // normal (non-throwing) 404 answer, so control falls through to the header rung, where the
        // header read's own post-check is what actually observes the cancellation.
        var step = NewStep(c => c
            .Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync((CivitaiModelVersion?)null));

        var items = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        var item = items.Single(i => i.ModelId == modelId);

        var act = () => step.ExecuteOneAsync(item, apiKey: null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await ReadStateAsync(modelId)).Should().BeNull("a cancelled item is work not done, not a model Civitai has never heard of");
    }

    /// <summary>
    /// I3, the check this actually needs to pin. The candidate carries a REAL sidecar on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A naively pre-cancelled token does NOT reach the sidecar branch at all in this codebase — a
    /// first attempt at this test (a plain <c>cts.Cancel()</c> before calling <c>ExecuteOneAsync</c>)
    /// was instead caught by the step's own opening "model still exists?" read
    /// (<c>uow.Models.GetByIdAsync</c>, line ~161, *before* the try block), because that call goes
    /// through EF Core's real async query pipeline (<c>SqliteCommand.ExecuteReaderAsync</c>), which
    /// itself throws on an already-cancelled token — confirmed by inspecting the thrown exception's
    /// stack trace, which bottomed out at <c>RepositoryBase.GetByIdAsync</c>, nowhere near the
    /// sidecar branch. A second attempt let that one call complete on the real token (cancelling
    /// only once it returned) but was STILL not isolating the target line: with no sidecar match,
    /// the "no Civitai hit" path falls through <see cref="IdentifyModelStep.ResolveHashAsync"/> —
    /// which, given no stored hash, re-hashes the file and independently observes the same
    /// cancelled token — and even with a stored hash bypassing that, the header rung a few lines
    /// further down carries its OWN pre-existing, unconditional <c>ct.ThrowIfCancellationRequested()</c>
    /// (unrelated to this task, always executed) that intercepts it just the same. Every "no match"
    /// path independently notices the same cancellation via its own real I/O or its own explicit
    /// check, so removing the post-sidecar line changed nothing there — a second tautology.
    /// </para>
    /// <para>
    /// This version instead routes through the Civitai-MATCH branch, which returns immediately
    /// after <c>RecordMatchAsync</c> and never reaches the header rung, and uses a stored SHA256 so
    /// <c>ResolveHashAsync</c> never touches the file. <see cref="ForwardingProxy"/> wraps the real
    /// <see cref="IUnitOfWork"/>/<see cref="IModelRepository"/>/<see cref="ISyncStateRepository"/> so
    /// every DB call downstream of the sidecar branch — <c>CivitaiMetadataApplier</c>'s reads,
    /// <c>RecordMatchAsync</c>'s own re-fetch, every <c>SaveChangesAsync</c> — runs with
    /// <see cref="CancellationToken.None"/> instead of the real (by-then cancelled) token, so NONE of
    /// them can independently notice the cancellation. The one exception is the very first
    /// <c>Models.GetByIdAsync</c> call (the opening guard): it runs on the real, not-yet-cancelled
    /// token so it completes normally, and the token becomes cancelled the instant it returns —
    /// deterministically sequenced, no wall-clock race against real file I/O. What is deliberately
    /// left un-neutered is <see cref="SidecarMetadataApplier.ApplyAsync"/>'s own sidecar file read,
    /// which uses the raw <c>ct</c> parameter directly (not the proxied <c>uow</c>): by the time it
    /// runs, the token is cancelled, so that real read throws internally and the applier's own
    /// <c>catch (OperationCanceledException) when (ct.IsCancellationRequested)</c> swallows it,
    /// reporting <c>Applied = false</c> — the exact shape it also uses for "there is no sidecar".
    /// With every other check neutralised, ONLY the step's own post-sidecar
    /// <c>ct.ThrowIfCancellationRequested()</c> stands between that and a silently completed
    /// <see cref="SyncOutcome.Matched"/> stamp on a model whose sidecar read was actually cancelled.
    /// </para>
    /// <para>
    /// Verified not to be a tautology exactly as the review asked: with the post-sidecar
    /// <c>ct.ThrowIfCancellationRequested()</c> temporarily deleted, this test FAILS — no exception
    /// is observed and the model is instead stamped <see cref="SyncOutcome.Matched"/>. Restoring the
    /// line makes it pass again. See task-8-report.md for the exact before/after runs (both attempts
    /// above included).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Execute_CancellationDuringARealSidecarReadDoesNotStamp()
    {
        var candidate = await CreateCandidateWithSidecarAsync(storedSha256: new string('a', 64));

        using var cts = new CancellationTokenSource();
        using var scope = NewScope();
        var realUow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Every call except the FIRST Models.GetByIdAsync (the opening "still exists?" guard) is
        // cancellation-immune: its token argument is silently replaced with None. That first call
        // alone runs on the real token and cancels it the moment it returns.
        var getByIdCalls = 0;
        var wrappedModels = ForwardingProxy.Wrap<IModelRepository>(realUow.Models, new()
        {
            [nameof(IModelRepository.GetByIdAsync)] = (method, args) =>
            {
                if (Interlocked.Increment(ref getByIdCalls) == 1)
                {
                    var first = (Task<Model?>)method.Invoke(realUow.Models, args)!;
                    return CancelThenReturn(first, cts);
                }

                return method.Invoke(realUow.Models, StripCancellation(args));
            },
        });
        var wrappedSyncStates = ForwardingProxy.Wrap<ISyncStateRepository>(realUow.SyncStates, new());
        var wrappedUow = ForwardingProxy.Wrap<IUnitOfWork>(realUow, new()
        {
            ["get_Models"] = (_, _) => wrappedModels,
            ["get_SyncStates"] = (_, _) => wrappedSyncStates,
        });

        var civVersion = NewCivitaiVersion();
        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(civVersion);
        client.Setup(c => c.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewCivitaiModel(civVersion));

        var step = new IdentifyModelStep(
            new SingleInstanceScopeFactory(wrappedUow),
            client.Object,
            new CivitaiMetadataApplier(client.Object),
            new SidecarMetadataApplier());

        var act = () => step.ExecuteOneAsync(
            new SyncItem(candidate.ModelId, candidate.Name, candidate), apiKey: null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await ReadStateAsync(candidate.ModelId)).Should().BeNull(
            "a cancelled sidecar read is work not done, not a model that must wait 30 days as Matched");

        // Pins the routing assumption the ForwardingProxy above is built on. The proxy routes by
        // raw call count ("first call is real and cancels afterward; every later call is
        // cancellation-immune"), which only isolates the post-sidecar guard if exactly ONE
        // Models.GetByIdAsync call happens before that guard fires — the opening "still exists?"
        // read at line ~169. With the guard intact, that is the ONLY call this run makes: it throws
        // before the flow ever reaches RecordMatchAsync's own GetByIdAsync (line ~327), so that
        // second, cancellation-stripped routing branch is never exercised at all here.
        //
        // If a future change added, removed or reordered a Models.GetByIdAsync call ahead of the
        // guard, this count would shift silently — the proxy would keep neutering "every call after
        // the first" against a different actual call, and the test could keep passing for a reason
        // disconnected from the line it exists to guard, exactly the tautological-test failure mode
        // this test was written to escape. A failure here means: go re-verify (per the remarks
        // above, by temporarily deleting the step's post-sidecar ct.ThrowIfCancellationRequested())
        // that this test still actually exercises that line before trusting it again.
        getByIdCalls.Should().Be(1,
            "the routing above assumes the post-sidecar guard fires after exactly one real " +
            "Models.GetByIdAsync call and before any other; a different count means this test may " +
            "no longer be isolating the post-sidecar cancellation check");
    }

    private static async Task<Model?> CancelThenReturn(Task<Model?> inner, CancellationTokenSource cts)
    {
        var value = await inner.ConfigureAwait(false);
        cts.Cancel();
        return value;
    }

    private static object?[]? StripCancellation(object?[]? args) =>
        args?.Select(a => a is CancellationToken ? (object)CancellationToken.None : a).ToArray();

    /// <summary>
    /// A <see cref="DispatchProxy"/> that forwards every call on <typeparamref name="T"/> to a real
    /// inner instance with its <see cref="CancellationToken"/> argument (if any) replaced by
    /// <see cref="CancellationToken.None"/>, except member names present in <c>overrides</c> — those
    /// get full control over how (and whether) the inner call happens. Avoids hand-writing
    /// forwarding stubs for every member of a wide repository interface just to change one, and lets
    /// a test prove that a SPECIFIC cancellation check is the thing standing between a cancelled
    /// token and an incorrect stamp, rather than some other, unrelated call incidentally noticing
    /// the same cancellation on its own.
    /// </summary>
    private class ForwardingProxy : DispatchProxy
    {
        private object _inner = null!;
        private Dictionary<string, Func<MethodInfo, object?[]?, object?>> _overrides = null!;

        public static T Wrap<T>(T inner, Dictionary<string, Func<MethodInfo, object?[]?, object?>> overrides) where T : class
        {
            var proxy = Create<T, ForwardingProxy>();
            var self = (ForwardingProxy)(object)proxy;
            self._inner = inner;
            self._overrides = overrides;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (_overrides.TryGetValue(targetMethod!.Name, out var handler)) return handler(targetMethod, args);
            return targetMethod.Invoke(_inner, StripCancellation(args));
        }
    }

    /// <summary>An <see cref="IServiceScopeFactory"/> that always hands back the same
    /// pre-built <see cref="IUnitOfWork"/> — lets a test control exactly which instance
    /// <see cref="IdentifyModelStep"/>'s own <c>_scopes.CreateScope()</c> resolves.</summary>
    private sealed class SingleInstanceScopeFactory(IUnitOfWork uow) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(uow);

        private sealed class Scope(IUnitOfWork uow) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new Provider(uow);
            public void Dispose() { }
        }

        private sealed class Provider(IUnitOfWork uow) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(IUnitOfWork) ? uow : null;
        }
    }

    [Fact]
    public void Step_DescribesItselfForThePlanView()
    {
        var step = NewNotFoundStep();

        step.Kind.Should().Be(SyncStepKind.IdentifyModel);
        step.Description.Should().NotBeNullOrWhiteSpace();
        step.EstimatedPerItem.Should().Be(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// F1. A .civitai.info next to the file already answers the question the hash lookup would
    /// ask, and reading it costs no request and no multi-gigabyte hash — so a sidecar-bearing
    /// candidate must never even reach the hash/by-hash call. Built directly with a hand-made
    /// <see cref="IdentifyCandidate"/> (skipping <c>SelectAsync</c>) so this pins ExecuteOneAsync's
    /// own ordering regardless of what candidate selection happens to do.
    /// </summary>
    [Fact]
    public async Task ExecuteOneAsync_WithASidecarPresent_NeverCallsCivitai()
    {
        var candidate = await CreateCandidateWithSidecarAsync();

        var client = new Mock<ICivitaiClient>();
        var step = new IdentifyModelStep(
            Scopes, client.Object,
            new CivitaiMetadataApplier(client.Object),
            new SidecarMetadataApplier());

        var result = await step.ExecuteOneAsync(new SyncItem(candidate.ModelId, candidate.Name, candidate),
            apiKey: null, CancellationToken.None);

        result.Should().Be(SyncItemResult.Success);
        client.Verify(c => c.GetModelVersionByHashAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The mirror image: no sidecar on disk means the hash/by-hash rung still runs.</summary>
    [Fact]
    public async Task ExecuteOneAsync_WithNoSidecar_StillAsksCivitaiByHash()
    {
        var candidate = await CreateCandidateWithoutSidecarAsync();

        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CivitaiModelVersion?)null);
        var step = new IdentifyModelStep(
            Scopes, client.Object,
            new CivitaiMetadataApplier(client.Object),
            new SidecarMetadataApplier());

        await step.ExecuteOneAsync(new SyncItem(candidate.ModelId, candidate.Name, candidate),
            apiKey: null, CancellationToken.None);

        client.Verify(c => c.GetModelVersionByHashAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Seeds a model/version/file row for a fresh temp file that also carries a sidecar,
    /// then hands back the same <see cref="IdentifyCandidate"/> shape SelectAsync would produce.
    /// <paramref name="storedSha256"/> lets a caller give the candidate an already-valid hash, the
    /// same way <see cref="Execute_ReusesAStoredValidHashInsteadOfRehashing"/> does, so
    /// <c>ResolveHashAsync</c> never touches the file.</summary>
    private async Task<IdentifyCandidate> CreateCandidateWithSidecarAsync(string? storedSha256 = null)
    {
        var path = NewModelFile("gateway-sidecar-present.safetensors");
        WriteSidecar(path);
        return await BuildCandidateAsync("gateway-sidecar-present", path, storedSha256);
    }

    /// <summary>Same as above, minus the sidecar.</summary>
    private async Task<IdentifyCandidate> CreateCandidateWithoutSidecarAsync()
    {
        var path = NewModelFile("gateway-sidecar-absent.safetensors");
        return await BuildCandidateAsync("gateway-sidecar-absent", path);
    }

    /// <summary>Reuses <see cref="SeedAsync"/> — the same seeding every other test in this fixture
    /// uses — then reads back the version id it created so the candidate can be built directly,
    /// bypassing SelectAsync entirely.</summary>
    private async Task<IdentifyCandidate> BuildCandidateAsync(string name, string path, string? storedSha256 = null)
    {
        var (modelId, fileId) = await SeedAsync(name, path, sha256: storedSha256);

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var model = await uow.Models.GetByIdWithIncludesAsync(modelId);
        var versionId = model!.Versions.Single().Id;

        return new IdentifyCandidate(modelId, versionId, fileId, name, path, Sha256: storedSha256,
            BaseModelRaw: "???", Outcome: SyncOutcome.None, CheckedAt: null, Attempts: 0, SidecarSignature: null);
    }

    /// <summary>Writes a real <c>.civitai.info</c> sidecar in the shape
    /// <see cref="SidecarMetadataApplier"/>'s civitai.info reader expects (top-level id, modelId,
    /// name, baseModel, trainedWords, files[]) — confirmed against ApplyCivitaiInfoFormatAsync.</summary>
    private static void WriteSidecar(string modelFilePath)
    {
        var sidecar = Path.ChangeExtension(modelFilePath, ".civitai.info");
        File.WriteAllText(sidecar, """
        {
          "id": 4242,
          "modelId": 900,
          "name": "v1.0",
          "baseModel": "SDXL 1.0",
          "trainedWords": ["trigger"],
          "files": [{ "name": "model.safetensors", "hashes": { "SHA256": "ABC123" } }]
        }
        """);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        try { _tempDir.Delete(recursive: true); } catch { /* best effort */ }
    }
}
