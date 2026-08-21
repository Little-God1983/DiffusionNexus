using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
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
using Moq;

namespace DiffusionNexus.Tests.Sync.Service;

/// <summary>
/// Covers <see cref="CivitaiMetadataApplier"/>: the Civitai-response → DB-graph write
/// that used to live inside <c>LoraViewerViewModel.UpdateModelFromCivitaiAsync</c>,
/// plus the two narrower entry points used by the FetchTags / FetchImages steps.
/// </summary>
public sealed class CivitaiMetadataApplierTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public CivitaiMetadataApplierTests()
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

    /// <summary>Local-only model: one version, one primary file, no hashes, no Civitai ids.</summary>
    private static Model NewLocalModel(
        string name,
        string path,
        int? civitaiId = null,
        bool modelUserEdited = false,
        bool versionUserEdited = false)
    {
        var model = new Model
        {
            Name = name,
            Type = ModelType.LORA,
            Source = DataSource.LocalFile,
            CivitaiId = civitaiId,
            IsUserEdited = modelUserEdited,
        };

        var version = new ModelVersion
        {
            Name = "v1",
            BaseModelRaw = "???",
            IsUserEdited = versionUserEdited,
        };
        version.Files.Add(new ModelFile
        {
            FileName = Path.GetFileName(path),
            LocalPath = path,
            IsLocalFileValid = true,
            IsPrimary = true,
        });

        model.Versions.Add(version);
        return model;
    }

    private static CivitaiModelImage NewImage(long id, string url) => new()
    {
        Id = id,
        Url = url,
        Type = "image",
        Nsfw = false,
        Width = 512,
        Height = 768,
        Hash = "blurhash",
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        PostId = 42,
        Username = "author",
        Meta = new CivitaiImageMeta
        {
            Prompt = "a prompt",
            NegativePrompt = "a negative",
            Seed = 1234,
            Steps = 20,
            Sampler = "Euler a",
            CfgScale = 7.0,
        },
    };

    private static CivitaiModelVersion NewCivitaiVersion(
        int id = 700,
        int modelId = 77,
        params CivitaiModelImage[] images) => new()
        {
            Id = id,
            ModelId = modelId,
            Name = "civitai v1",
            Description = "version description",
            BaseModel = "SDXL 1.0",
            DownloadUrl = "https://civitai.com/api/download/models/700",
            PublishedAt = new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero),
            EarlyAccessTimeFrame = 3,
            TrainedWords = ["a", "b"],
            Images = images.Length > 0 ? images : [NewImage(5, "https://img/5.jpeg")],
            Files =
            [
                new CivitaiModelFile
                {
                    Id = 900,
                    Primary = true,
                    Hashes = new CivitaiFileHashes { SHA256 = "ABC", AutoV2 = "AV2", CRC32 = "CRC", BLAKE3 = "B3" },
                },
            ],
            Stats = new CivitaiVersionStats { DownloadCount = 123 },
        };

    private static CivitaiModel NewCivitaiModel(
        CivitaiModelVersion version,
        int id = 77,
        params string[] tags) => new()
        {
            Id = id,
            Name = "Civitai Name",
            Description = "Civitai description",
            Nsfw = false,
            Poi = false,
            Tags = tags,
            Creator = new CivitaiCreator { Username = "author", Image = "https://img/avatar.jpeg" },
            ModelVersions = [version],
            AllowNoCredit = true,
            AllowDerivatives = true,
            AllowDifferentLicense = false,
        };

    private static CivitaiMetadataApplier NewApplier(
        CivitaiModel? model = null,
        CivitaiModelVersion? version = null)
    {
        var client = new Mock<ICivitaiClient>();
        client
            .Setup(c => c.GetModelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        client
            .Setup(c => c.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(version);
        return new CivitaiMetadataApplier(client.Object);
    }

    [Fact]
    public async Task ApplyAsync_WritesCivitaiIdsBaseModelTriggerWordsImagesHashesAndTags()
    {
        int modelId, fileId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = NewLocalModel("local", @"C:\m\local.safetensors");
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
            fileId = model.Versions.First().Files.First().Id;
        }

        var civVersion = NewCivitaiVersion();
        var applier = NewApplier(NewCivitaiModel(civVersion, tags: ["style", "anime"]));

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var applied = await applier.ApplyAsync(uow, modelId, fileId, civVersion, apiKey: null);
            applied.Should().BeTrue();
        }

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);

            saved.Should().NotBeNull();
            saved!.CivitaiId.Should().Be(77);
            saved.CivitaiModelPageId.Should().Be(77);
            saved.Source.Should().Be(DataSource.CivitaiApi);
            saved.LastSyncedAt.Should().NotBeNull();
            saved.Tags.Should().HaveCount(2);

            var version = saved.Versions.Single();
            version.CivitaiId.Should().Be(700);
            version.BaseModelRaw.Should().Be("SDXL 1.0");
            version.TriggerWords.Should().HaveCount(2);
            version.Images.Should().HaveCount(1);
            version.PrimaryFile.Should().NotBeNull();
            version.PrimaryFile!.HashSHA256.Should().Be("ABC");
        }
    }

    [Fact]
    public async Task ApplyAsync_PreservesUserEditedNameDescriptionTagsAndTriggerWords()
    {
        int modelId, fileId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = NewLocalModel("my name", @"C:\m\edited.safetensors",
                modelUserEdited: true, versionUserEdited: true);
            model.Description = "my description";
            model.Tags.Add(new ModelTag { Tag = new Tag { Name = "mine", NormalizedName = "mine" } });
            model.Versions.First().TriggerWords.Add(new TriggerWord { Word = "mytrigger", Order = 0 });
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
            fileId = model.Versions.First().Files.First().Id;
        }

        var civVersion = NewCivitaiVersion();
        var applier = NewApplier(NewCivitaiModel(civVersion, tags: ["style", "anime"]));

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            (await applier.ApplyAsync(uow, modelId, fileId, civVersion, apiKey: null)).Should().BeTrue();
        }

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);

            // User-edited fields survive the sync.
            saved!.Name.Should().Be("my name");
            saved.Description.Should().Be("my description");
            saved.Tags.Should().ContainSingle().Which.Tag!.NormalizedName.Should().Be("mine");

            var version = saved.Versions.Single();
            version.TriggerWords.Should().ContainSingle().Which.Word.Should().Be("mytrigger");

            // Non-user-authored facts are still applied.
            saved.CivitaiId.Should().Be(77);
            version.CivitaiId.Should().Be(700);
            version.Images.Should().HaveCount(1);

            // The base model is authored too (the detail view writes it and stamps IsUserEdited).
            version.BaseModelRaw.Should().Be("???");
        }
    }

    /// <summary>
    /// B1. <c>IsUserEdited</c> on the version guarded the trigger words but not the two text fields
    /// next to them, so a sync silently replaced a version the user had named and described.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_PreservesUserEditedVersionNameAndDescription()
    {
        int modelId, fileId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = NewLocalModel("local", @"C:\m\edited-version.safetensors", versionUserEdited: true);
            var version = model.Versions.First();
            version.Name = "my version";
            version.Description = "my version description";
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
            fileId = version.Files.First().Id;
        }

        var civVersion = NewCivitaiVersion();
        var applier = NewApplier(NewCivitaiModel(civVersion));

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            (await applier.ApplyAsync(uow, modelId, fileId, civVersion, apiKey: null)).Should().BeTrue();
        }

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var version = (await uow.Models.GetByIdWithIncludesAsync(modelId))!.Versions.Single();

            version.Name.Should().Be("my version");
            version.Description.Should().Be("my version description");

            // Facts the user did not author are still applied.
            version.CivitaiId.Should().Be(700);
            version.DownloadUrl.Should().Be("https://civitai.com/api/download/models/700");
            version.Images.Should().HaveCount(1);
        }
    }

    /// <summary>
    /// R1. The base model is not a fact we may refresh over: the detail view lets the user pick it
    /// (<c>ModelDetailViewModel.Editing</c> writes <c>BaseModelRaw</c> + <c>BaseModel</c> and stamps
    /// <c>IsUserEdited</c>), so a sync that rewrote it silently undid that choice.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_PreservesUserEditedBaseModel()
    {
        int modelId, fileId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = NewLocalModel("local", @"C:\m\edited-base.safetensors", versionUserEdited: true);
            var version = model.Versions.First();
            version.BaseModelRaw = "Pony";
            version.BaseModel = BaseModelType.Pony;
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
            fileId = version.Files.First().Id;
        }

        // Civitai says "SDXL 1.0"; the user says Pony.
        var civVersion = NewCivitaiVersion();
        var applier = NewApplier(NewCivitaiModel(civVersion));

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            (await applier.ApplyAsync(uow, modelId, fileId, civVersion, apiKey: null)).Should().BeTrue();
        }

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
            var version = saved!.Versions.Single();

            version.BaseModelRaw.Should().Be("Pony");
            version.BaseModel.Should().Be(BaseModelType.Pony);

            // Everything that is genuinely upstream still lands.
            saved.CivitaiId.Should().Be(77);
            version.CivitaiId.Should().Be(700);
        }
    }

    /// <summary>
    /// R1. <c>BaseModelRaw</c> and the <c>BaseModel</c> enum are two spellings of one answer, and
    /// the editor writes both. A sync that wrote only the raw string left the enum — which is what
    /// the viewer's base-model filter reads — reporting the previous base model forever.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_WritesBaseModelEnumAlongsideRaw()
    {
        int modelId, fileId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = NewLocalModel("local", @"C:\m\base-enum.safetensors");
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
            fileId = model.Versions.First().Files.First().Id;
        }

        var civVersion = NewCivitaiVersion();
        var applier = NewApplier(NewCivitaiModel(civVersion));

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            (await applier.ApplyAsync(uow, modelId, fileId, civVersion, apiKey: null)).Should().BeTrue();
        }

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var version = (await uow.Models.GetByIdWithIncludesAsync(modelId))!.Versions.Single();

            version.BaseModelRaw.Should().Be("SDXL 1.0");
            version.BaseModel.Should().Be(BaseModelType.SDXL10);
        }
    }

    [Fact]
    public async Task ApplyAsync_SkipsCivitaiIdAlreadyOwnedByAnotherModel()
    {
        int modelId, fileId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var mine = NewLocalModel("mine", @"C:\m\mine.safetensors");
            var other = NewLocalModel("other", @"C:\m\other.safetensors", civitaiId: 77);
            await uow.Models.AddAsync(mine);
            await uow.Models.AddAsync(other);
            await uow.SaveChangesAsync();
            modelId = mine.Id;
            fileId = mine.Versions.First().Files.First().Id;
        }

        var civVersion = NewCivitaiVersion();
        var applier = NewApplier(NewCivitaiModel(civVersion));

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var act = async () => await applier.ApplyAsync(uow, modelId, fileId, civVersion, apiKey: null);
            await act.Should().NotThrowAsync();
        }

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);

            saved!.CivitaiId.Should().BeNull();
            saved.CivitaiModelPageId.Should().Be(77);
        }
    }

    /// <summary>
    /// I1. Civitai tag lists are not normalized: "Anime" and "anime" both occur on the same model.
    /// Both fold to one <c>Tag</c> row (by <c>NormalizedName</c>), so adding one <c>ModelTag</c>
    /// each means two rows with the same composite primary key — the save is rejected outright and
    /// the whole item fails, taking a good tag list with it.
    /// </summary>
    [Fact]
    public async Task SyncTags_DeduplicatesCaseVariants()
    {
        int modelId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = NewLocalModel("dupe-tags", @"C:\m\dupe-tags.safetensors");
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
        }

        var civVersion = NewCivitaiVersion();
        // "  anime" as well: the normalizer trims, so leading whitespace is a third spelling of the
        // same tag, and a response that carries two of them must still produce one row.
        var applier = NewApplier(NewCivitaiModel(civVersion, tags: ["Anime", "anime", "  anime", "style"]));

        int? written = null;
        using (var scope = NewScope())
        {
            var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var act = async () => written = await applier.ApplyTagsAsync(work, modelId, civitaiModelId: 77, apiKey: null);
            await act.Should().NotThrowAsync("a duplicate spelling is Civitai's, and it is not a reason to lose the tags");
        }

        written.Should().Be(2);

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
            saved!.Tags.Select(t => t.Tag!.NormalizedName).Should().BeEquivalentTo(new[] { "anime", "style" });
        }
    }

    [Fact]
    public async Task ApplyTagsAsync_EmptyTagListIsAuthoritativeAndClearsStaleTags()
    {
        // A model page that responds with no tags is a real answer, not a missing one: tags removed
        // upstream are removed here too. Returning 0 (not null) is what lets the FetchTags step
        // record "checked and empty" instead of asking again on every run.
        int modelId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = NewLocalModel("tagless", @"C:\m\tagless.safetensors");
            model.Tags.Add(new ModelTag { Tag = new Tag { Name = "stale", NormalizedName = "stale" } });
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
        }

        var civVersion = NewCivitaiVersion();
        var applier = NewApplier(NewCivitaiModel(civVersion));   // Tags = []

        int? written = null;
        using (var scope = NewScope())
        {
            var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var act = async () => written = await applier.ApplyTagsAsync(work, modelId, civitaiModelId: 77, apiKey: null);
            await act.Should().NotThrowAsync();
        }

        written.Should().Be(0);

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
            saved!.Tags.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ApplyTagsAsync_TreatsNullTagListAsAnsweredButNotAuthoritative()
    {
        // `Tags` is annotated non-nullable and defaults to [], but System.Text.Json writes null
        // through for an explicit `"tags": null` — and Civitai's shapes have drifted twice before.
        // A degraded payload must not throw (an NRE escapes the steps' narrow catch filter and
        // takes the whole run down) and must not be mistaken for an authoritative empty list.
        int modelId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = NewLocalModel("null-tags", @"C:\m\degraded-tags.safetensors");
            model.Tags.Add(new ModelTag { Tag = new Tag { Name = "keep", NormalizedName = "keep" } });
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
        }

        var degraded = NewCivitaiModel(NewCivitaiVersion());
        degraded = degraded with { Tags = null! };
        var applier = NewApplier(degraded);

        int? written = null;
        using (var scope = NewScope())
        {
            var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var act = async () => written = await applier.ApplyTagsAsync(work, modelId, civitaiModelId: 77, apiKey: null);
            await act.Should().NotThrowAsync();
        }

        // Non-null, so the step still stamps and stops re-asking…
        written.Should().Be(1);

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
            // …but the tags we already hold survive, unlike the empty-array case above.
            saved!.Tags.Select(t => t.Tag!.Name).Should().BeEquivalentTo(["keep"]);
        }
    }

    [Fact]
    public async Task ApplyTagsAsync_KeepsTagsOfAUserEditedModel()
    {
        int modelId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = NewLocalModel("mine", @"C:\m\mine.safetensors", modelUserEdited: true);
            model.Tags.Add(new ModelTag { Tag = new Tag { Name = "mine", NormalizedName = "mine" } });
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
        }

        var applier = NewApplier(NewCivitaiModel(NewCivitaiVersion()));   // Tags = []

        int? written;
        using (var scope = NewScope())
        {
            var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            written = await applier.ApplyTagsAsync(work, modelId, civitaiModelId: 77, apiKey: null);
        }

        // Still a real answer (so the step stamps), but the user's own tags are untouched.
        written.Should().Be(1);

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
            saved!.Tags.Select(t => t.Tag!.Name).Should().BeEquivalentTo(["mine"]);
        }
    }

    [Fact]
    public async Task ApplyTagsAsync_ReturnsNullWhenCivitaiHasNoSuchModel()
    {
        int modelId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = NewLocalModel("gone", @"C:\m\gone.safetensors");
            model.Tags.Add(new ModelTag { Tag = new Tag { Name = "keep", NormalizedName = "keep" } });
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
        }

        var applier = NewApplier(model: null);   // GetModelAsync 404s

        int? written;
        using (var scope = NewScope())
        {
            var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            written = await applier.ApplyTagsAsync(work, modelId, civitaiModelId: 77, apiKey: null);
        }

        // null, not 0: a dead page must never be mistaken for "Civitai says: no tags"…
        written.Should().BeNull();

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
            // …and must therefore not wipe what we already hold.
            saved!.Tags.Select(t => t.Tag!.Name).Should().BeEquivalentTo(["keep"]);
        }
    }

    [Fact]
    public async Task ApplyImagesAsync_ReturnsNullWhenCivitaiHasNoSuchVersion()
    {
        int modelId, versionId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = NewLocalModel("gone-version", @"C:\m\gone-version.safetensors");
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
            versionId = model.Versions.First().Id;
        }

        var applier = NewApplier(version: null);   // GetModelVersionAsync 404s

        using var scope = NewScope();
        var uow2 = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var added = await applier.ApplyImagesAsync(uow2, modelId, versionId, civitaiVersionId: 700, apiKey: null);

        added.Should().BeNull();
    }

    [Fact]
    public async Task ApplyImagesAsync_AppendsOnlyNewImagesByCivitaiId()
    {
        int modelId, versionId;
        using (var seedScope = NewScope())
        {
            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = NewLocalModel("images", @"C:\m\images.safetensors");
            model.Versions.First().Images.Add(new ModelImage
            {
                CivitaiId = 5,
                Url = "https://img/5.jpeg",
                SortOrder = 0,
            });
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
            versionId = model.Versions.First().Id;
        }

        var civVersion = NewCivitaiVersion(images: [NewImage(5, "https://img/5.jpeg"), NewImage(6, "https://img/6.jpeg")]);
        var applier = NewApplier(version: civVersion);

        int? added;
        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            added = await applier.ApplyImagesAsync(uow, modelId, versionId, civitaiVersionId: 700, apiKey: null);
        }

        added.Should().Be(1);

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
            var images = saved!.Versions.Single().Images.OrderBy(i => i.SortOrder).ToList();

            images.Should().HaveCount(2);
            images.Select(i => i.CivitaiId).Should().BeEquivalentTo(new long?[] { 5, 6 });
            images.Single(i => i.CivitaiId == 6).SortOrder.Should().Be(1);
        }
    }

    [Fact]
    public async Task ApplyAsync_ReturnsFalseWhenModelMissing()
    {
        var civVersion = NewCivitaiVersion();
        var applier = NewApplier(NewCivitaiModel(civVersion));

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var applied = await applier.ApplyAsync(uow, modelId: 4242, fileId: 1, civVersion, apiKey: null);

        applied.Should().BeFalse();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
