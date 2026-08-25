using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Covers the S5 <c>IsUserEdited</c> guard in <see cref="LoraDownloadService.PersistDownloadedModelAsync"/>:
/// the persister must never overwrite a user's own name/description/tags/base-model, while Civitai
/// linkage (CivitaiId/CivitaiModelPageId/Source/LastSyncedAt) and on-disk file facts stay writable.
/// Mirrors the DI/in-memory-SQLite fixture pattern from
/// <c>DiffusionNexus.Tests.Sync.Service.CivitaiMetadataApplierTests</c>.
/// </summary>
public sealed class LoraDownloadServicePersistTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly string _tempDir;

    public LoraDownloadServicePersistTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();

        _tempDir = Path.Combine(Path.GetTempPath(), "dn-lora-download-persist-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private IServiceScope NewScope() => _serviceProvider.CreateScope();

    /// <summary>
    /// PersistDownloadedModelAsync reads <c>FileInfo(filePath).Length</c> for the download it is
    /// persisting, so that path must exist on disk (unlike the seeded model's existing file paths,
    /// which are DB facts only and are never touched by the method under test).
    /// </summary>
    private string NewRealFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    /// <summary>Builds a service whose <see cref="ICivitaiClient"/> answers every model-page
    /// lookup with <paramref name="civitaiModel"/>, wired to this fixture's scope factory so
    /// <c>PersistDownloadedModelAsync</c> writes into the in-memory SQLite database.</summary>
    private LoraDownloadService NewService(CivitaiModel? civitaiModel)
    {
        var client = new Mock<ICivitaiClient>();
        client
            .Setup(c => c.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(civitaiModel);
        return new LoraDownloadService(
            client.Object,
            settingsService: null,
            logger: null,
            apiKeyProvider: null,
            scopeFactory: _serviceProvider.GetRequiredService<IServiceScopeFactory>());
    }

    private static Model NewSeedModel(
        string name,
        string? description,
        string existingFilePath,
        bool modelUserEdited,
        string handTagName = "hand")
    {
        var model = new Model
        {
            Name = name,
            Description = description,
            Type = ModelType.LORA,
            Source = DataSource.LocalFile,
            IsUserEdited = modelUserEdited,
        };
        model.Tags.Add(new ModelTag { Tag = new Tag { Name = handTagName, NormalizedName = handTagName } });

        var version = new ModelVersion
        {
            Name = "v1",
            BaseModelRaw = "???",
        };
        version.Files.Add(new ModelFile
        {
            FileName = Path.GetFileName(existingFilePath),
            LocalPath = existingFilePath,
            IsLocalFileValid = true,
            IsPrimary = true,
        });

        model.Versions.Add(version);
        return model;
    }

    private static CivitaiModelVersion NewCivitaiVersion(int id = 700, int modelId = 77, string sha256 = "ABC") => new()
    {
        Id = id,
        ModelId = modelId,
        Name = "civitai v1",
        Description = "version description",
        BaseModel = "SDXL 1.0",
        DownloadUrl = "https://civitai.com/api/download/models/700",
        PublishedAt = new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero),
        EarlyAccessTimeFrame = 3,
        Files =
        [
            new CivitaiModelFile
            {
                Id = 900,
                Primary = true,
                Hashes = new CivitaiFileHashes { SHA256 = sha256 },
            },
        ],
        Stats = new CivitaiVersionStats { DownloadCount = 123 },
    };

    private static CivitaiModel NewCivitaiModel(CivitaiModelVersion version, int id = 77, params string[] tags) => new()
    {
        Id = id,
        Name = "Civitai Name",
        Description = "Civitai description",
        Nsfw = false,
        Poi = false,
        Tags = tags,
        ModelVersions = [version],
        AllowNoCredit = true,
        AllowDerivatives = true,
        AllowDifferentLicense = false,
    };

    [Fact]
    public async Task Persist_ExistingUserEditedModel_KeepsNameDescriptionAndTags()
    {
        int modelId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = NewSeedModel("My name", "Mine", @"C:\m\old-model.safetensors", modelUserEdited: true);
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
        }

        var civVersion = NewCivitaiVersion();
        var service = NewService(NewCivitaiModel(civVersion, tags: ["style", "anime"]));
        var newFile = NewRealFile("new-model.safetensors");

        var outcome = await service.PersistDownloadedModelAsync(newFile, civVersion, existingModelId: modelId);

        outcome.Should().Be(MetadataPersistOutcome.Complete);

        using var scope = NewScope();
        var check = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await check.Models.GetByIdWithIncludesAsync(modelId);

        saved.Should().NotBeNull();
        saved!.Name.Should().Be("My name", "user-edited text must never be overwritten");
        saved.Description.Should().Be("Mine");
        saved.Tags.Should().ContainSingle().Which.Tag!.NormalizedName.Should().Be("hand");

        // Civitai linkage is not user text — it must still update.
        saved.CivitaiModelPageId.Should().Be(77);
        saved.CivitaiId.Should().Be(77);
        saved.LastSyncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Persist_ExistingUneditedModel_TakesCivitaiText()
    {
        int modelId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            // Description left null: the production code writes it with `??=` (fill-if-missing)
            // even on an unedited model, so a control row must start with no description to
            // observe the write — this is unrelated to the S5 guard under test.
            var model = NewSeedModel("Old name", description: null, @"C:\m\old-control.safetensors", modelUserEdited: false);
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
        }

        var civVersion = NewCivitaiVersion();
        var service = NewService(NewCivitaiModel(civVersion, tags: ["style", "anime"]));
        var newFile = NewRealFile("new-control.safetensors");

        var outcome = await service.PersistDownloadedModelAsync(newFile, civVersion, existingModelId: modelId);

        outcome.Should().Be(MetadataPersistOutcome.Complete);

        using var scope = NewScope();
        var check = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await check.Models.GetByIdWithIncludesAsync(modelId);

        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Civitai Name");
        saved.Description.Should().Be("Civitai description");
        saved.Tags.Select(t => t.Tag!.NormalizedName).Should().BeEquivalentTo(new[] { "style", "anime" });
        saved.CivitaiModelPageId.Should().Be(77);
        saved.CivitaiId.Should().Be(77);
    }

    [Fact]
    public async Task Persist_DuplicateVersionUserEdited_KeepsItsBaseModel()
    {
        const string oldPath = @"C:\m\old-version.safetensors";

        int modelId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = new Model
            {
                Name = "local model",
                Type = ModelType.LORA,
                Source = DataSource.LocalFile,
                IsUserEdited = false,
            };
            var seedVersion = new ModelVersion
            {
                CivitaiId = 700, // matches the version we persist below — routes to the duplicate-version branch.
                Name = "my version",
                Description = "my version description",
                BaseModelRaw = "Pony",
                BaseModel = BaseModelType.Pony,
                IsUserEdited = true,
            };
            seedVersion.Files.Add(new ModelFile
            {
                FileName = Path.GetFileName(oldPath),
                LocalPath = oldPath,
                IsLocalFileValid = true,
                IsPrimary = true,
            });
            model.Versions.Add(seedVersion);

            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
        }

        var civVersion = NewCivitaiVersion();
        var service = NewService(NewCivitaiModel(civVersion));
        var newPath = NewRealFile("new-version.safetensors");

        var outcome = await service.PersistDownloadedModelAsync(newPath, civVersion, existingModelId: modelId);

        outcome.Should().Be(MetadataPersistOutcome.Complete);

        using var scope = NewScope();
        var check = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await check.Models.GetByIdWithIncludesAsync(modelId);

        saved.Should().NotBeNull();
        var version = saved!.Versions.Should().ContainSingle().Which;

        version.BaseModelRaw.Should().Be("Pony", "the user hand-fixed this and it must survive the re-download");
        version.BaseModel.Should().Be(BaseModelType.Pony);
        version.Name.Should().Be("my version");
        version.Description.Should().Be("my version description");

        // Files are facts about disk, not user text — the new download's file must still attach.
        version.Files.Select(f => f.LocalPath).Should().BeEquivalentTo(new[] { oldPath, newPath });
    }

    /// <summary>
    /// Review finding: the hash-fallback duplicate match can land on a version that already
    /// carries a DIFFERENT non-null CivitaiId — the same bytes re-listed upstream under a new
    /// version id. That version is already linked (frozen), so a re-download matched to it by
    /// hash must never bleed the new version's Name/BaseModel onto it, even though it isn't
    /// <c>IsUserEdited</c>. Locks in the "linkage null-check gates the whole block, including
    /// text" restructuring — a decoupled text-write guarded only by <c>IsUserEdited</c> would
    /// wrongly refresh this row.
    /// </summary>
    [Fact]
    public async Task Persist_HashFallbackMatchOnAlreadyLinkedVersion_DoesNotBleedTextAcrossVersions()
    {
        const string oldPath = @"C:\m\v500.safetensors";
        const string sharedHash = "SHAREDHASH123";

        int modelId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = new Model
            {
                Name = "local model",
                Type = ModelType.LORA,
                Source = DataSource.LocalFile,
                IsUserEdited = false,
            };
            var seedVersion = new ModelVersion
            {
                CivitaiId = 500, // already linked to a DIFFERENT version id than the one we persist below.
                Name = "v500 name",
                Description = "v500 description",
                BaseModelRaw = "Pony",
                BaseModel = BaseModelType.Pony,
                IsUserEdited = false,
            };
            seedVersion.Files.Add(new ModelFile
            {
                FileName = Path.GetFileName(oldPath),
                LocalPath = oldPath,
                IsLocalFileValid = true,
                IsPrimary = true,
                HashSHA256 = sharedHash, // same bytes as the version we persist below, re-listed under CivitaiId 700 upstream.
            });
            model.Versions.Add(seedVersion);

            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
        }

        // Downloaded as a DIFFERENT Civitai version id (700), but the file's SHA256 matches the
        // already-linked version 500's file — this is what routes to it via the hash fallback
        // instead of the (non-matching) direct CivitaiId lookup.
        var civVersion = NewCivitaiVersion(id: 700, sha256: sharedHash);
        var service = NewService(NewCivitaiModel(civVersion));
        var newPath = NewRealFile("v700-bytes.safetensors");

        var outcome = await service.PersistDownloadedModelAsync(newPath, civVersion, existingModelId: modelId);

        outcome.Should().Be(MetadataPersistOutcome.Complete);

        using var scope = NewScope();
        var check = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await check.Models.GetByIdWithIncludesAsync(modelId);

        saved.Should().NotBeNull();
        var version = saved!.Versions.Should().ContainSingle().Which;

        version.CivitaiId.Should().Be(500, "a version already linked to one Civitai id must never be relinked to another via a hash-fallback match");
        version.Name.Should().Be("v500 name", "text must stay frozen once a version is linked, matching pre-task behavior");
        version.BaseModelRaw.Should().Be("Pony");

        // The new file is still a fact about disk — it attaches even though the metadata froze.
        version.Files.Select(f => f.LocalPath).Should().BeEquivalentTo(new[] { oldPath, newPath });
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
