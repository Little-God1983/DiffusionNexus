using System.Text.RegularExpressions;
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
using Microsoft.Extensions.Logging;

namespace DiffusionNexus.Tests.Sync.DataAccess;

/// <summary>
/// Covers <c>ISyncStateRepository.SelectThumbnailCandidatesAsync</c> and
/// <c>IModelRepository.GetImageByIdAsync</c>.
/// </summary>
/// <remarks>
/// Two properties carry the whole selection. First, the image it picks per version must be the one
/// <see cref="ModelVersion.PrimaryImage"/> would return, because that is the image the tile
/// displays — pick a different one and the sync reports a thumbnail for something nobody looks at.
/// Second, the query must never carry <c>ThumbnailData</c> into the process; a library-wide run
/// touches every image row there is.
/// </remarks>
public sealed class SyncStateRepositoryThumbnailTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public SyncStateRepositoryThumbnailTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>().Database.EnsureCreated();
    }

    private IServiceScope NewScope() => _serviceProvider.CreateScope();

    /// <summary>The enabled LoRA sources every seed lives under; without them nothing is in the library.</summary>
    private static readonly string[] Roots = [@"C:\m"];

    private static readonly DateTimeOffset Attempted = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private const string Static = "https://image.civitai.com/abc/width=450/still.jpeg";
    private const string Video = "https://image.civitai.com/abc/width=450/clip.mp4";

    private static ModelImage Img(
        string url, int sortOrder = 0, string? mediaType = null, bool nsfw = false,
        byte[]? thumbnail = null, DateTimeOffset? attemptedAt = null, string? failure = null)
        => new()
        {
            Url = url,
            SortOrder = sortOrder,
            MediaType = mediaType,
            IsNsfw = nsfw,
            ThumbnailData = thumbnail,
            ThumbnailAttemptedAt = attemptedAt,
            ThumbnailFailure = failure,
        };

    /// <summary>
    /// Seeds one model with one version, one visible primary file under <see cref="Roots"/>, and
    /// <paramref name="images"/> in the order given — which is the order they get their ids in, and
    /// therefore the order <c>ModelVersion.Images</c> comes back in.
    /// </summary>
    private async Task<(int ModelId, int VersionId, IReadOnlyList<int> ImageIds)> SeedAsync(
        string name, int? civitaiId = null, params ModelImage[] images)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = new Model { Name = name, Type = ModelType.LORA, Source = DataSource.LocalFile, CivitaiId = civitaiId };
        var version = new ModelVersion { Name = name + " v1", CivitaiId = civitaiId };
        version.Files.Add(new ModelFile
        {
            FileName = name + ".safetensors",
            LocalPath = $@"C:\m\{name}.safetensors",
            IsPrimary = true,
            IsLocalFileValid = true,
        });
        foreach (var image in images) version.Images.Add(image);
        model.Versions.Add(version);

        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        return (model.Id, version.Id, images.Select(i => i.Id).ToList());
    }

    private async Task<IReadOnlyList<ThumbnailCandidate>> SelectAsync(SyncScope? scope = null)
    {
        using var s = NewScope();
        var uow = s.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await uow.SyncStates.SelectThumbnailCandidatesAsync(scope ?? SyncScope.Library, Roots);
    }

    /// <summary>
    /// The rank is <c>PrimaryImage</c>'s: a static image beats a video even when the video sorts
    /// first, because the video is what the tile would refuse to show.
    /// </summary>
    [Fact]
    public async Task ThumbnailCandidates_PickThePrimaryImagePerVersion()
    {
        var seed = await SeedAsync("mixed", null,
            Img(Video, sortOrder: 0, mediaType: "video"),
            Img(Static, sortOrder: 1));

        var candidates = await SelectAsync();

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.ImageId.Should().Be(seed.ImageIds[1]);
        candidate.Url.Should().Be(Static);
        candidate.MediaType.Should().BeNull();
        candidate.ModelId.Should().Be(seed.ModelId);
        candidate.VersionId.Should().Be(seed.VersionId);
        candidate.Name.Should().Be("mixed v1");
    }

    /// <summary>
    /// The question is only ever asked of the primary. A secondary without bytes is not work: the
    /// tile shows the primary, and thumbnailing everything else would multiply a library-wide run
    /// by the image count per version.
    /// </summary>
    [Fact]
    public async Task ThumbnailCandidates_SkipVersionsWhosePrimaryHasAThumbnail()
    {
        await SeedAsync("done", null,
            Img(Static, sortOrder: 0, thumbnail: [1, 2, 3]),
            Img(Static + "?2", sortOrder: 1));

        (await SelectAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// A zero-length BLOB is not a thumbnail. The flag is <c>!= null AND &lt;&gt; X''</c> for
    /// exactly this row, which <c>ModelImage.HasThumbnail</c> also refuses to call a thumbnail.
    /// </summary>
    [Fact]
    public async Task ThumbnailCandidates_TreatAnEmptyBlobAsNoThumbnail()
    {
        var seed = await SeedAsync("empty-blob", null, Img(Static, thumbnail: []));

        (await SelectAsync()).Should().ContainSingle().Which.ImageId.Should().Be(seed.ImageIds[0]);
    }

    /// <summary>
    /// User-uploaded thumbnails are the user's. They have no fetchable source, so they are not
    /// ranked at all: a version whose only image is one contributes nothing, and a version that has
    /// one alongside a real image is thumbnailed from the real image.
    /// </summary>
    [Fact]
    public async Task ThumbnailCandidates_ExcludeUserThumbnailRows()
    {
        await SeedAsync("only-user", null, Img("user-thumbnail://abc123"));
        var mixed = await SeedAsync("user-plus-real", null,
            Img("user-thumbnail://def456"),
            Img(Static, sortOrder: 1));

        var candidates = await SelectAsync();

        candidates.Should().ContainSingle()
            .Which.Should().Match<ThumbnailCandidate>(c => c.VersionId == mixed.VersionId && c.ImageId == mixed.ImageIds[1]);
    }

    /// <summary>
    /// A row with no URL has nothing to fetch, so it must not be ranked either. Left in, a blank-URL
    /// row flagged <c>video</c> would win its version's rank, fail <c>VideoNoPoster</c> — a soft
    /// failure — and be re-offered on every run forever while the version's real image never got a
    /// thumbnail. (Carried ruling from the Task 3 review.)
    /// </summary>
    [Fact]
    public async Task ThumbnailCandidates_ExcludeBlankUrlRows()
    {
        await SeedAsync("only-blank", null, Img("", mediaType: "video"));
        var mixed = await SeedAsync("blank-plus-real", null,
            Img("   ", mediaType: "video"),
            Img(Static, sortOrder: 1));

        var candidates = await SelectAsync();

        candidates.Should().ContainSingle()
            .Which.Should().Match<ThumbnailCandidate>(c => c.VersionId == mixed.VersionId && c.ImageId == mixed.ImageIds[1]);
    }

    /// <summary>
    /// Deliberately unlike <c>SelectImageCandidatesAsync</c>: there is no <c>CivitaiId</c> filter.
    /// A local-only model whose preview is a sibling file on disk is exactly the case the thumbnail
    /// pipeline's local rung exists for.
    /// </summary>
    [Fact]
    public async Task ThumbnailCandidates_IncludeModelsWithoutCivitaiId()
    {
        var seed = await SeedAsync("local", null, Img(@"file://C:\m\local.preview.png"));

        var candidate = (await SelectAsync()).Should().ContainSingle().Subject;
        candidate.ImageId.Should().Be(seed.ImageIds[0]);
        candidate.Url.Should().Be(@"file://C:\m\local.preview.png");
        candidate.LocalPath.Should().Be(@"C:\m\local.safetensors");
    }

    /// <summary>
    /// <c>LocalPath</c> is the version's <i>primary file</i>'s — the provider probes its directory
    /// for a sibling preview when the recorded one has gone — and the retry columns come across
    /// verbatim so the caller's <c>IsThumbnailDue</c> has something to decide on.
    /// </summary>
    [Fact]
    public async Task ThumbnailCandidates_CarryThePrimaryFilesPathAndTheRetryState()
    {
        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = new Model { Name = "two-files", Type = ModelType.LORA, Source = DataSource.LocalFile };
            var version = new ModelVersion { Name = "two-files v1" };
            // Added first, so it owns the lower id — the primary must still win.
            version.Files.Add(new ModelFile { FileName = "extra.safetensors", LocalPath = @"C:\m\extra.safetensors", IsLocalFileValid = true });
            version.Files.Add(new ModelFile { FileName = "main.safetensors", LocalPath = @"C:\m\main.safetensors", IsPrimary = true, IsLocalFileValid = true });
            version.Images.Add(Img(Static, attemptedAt: Attempted, failure: ThumbnailFailureReason.HttpError));
            model.Versions.Add(version);
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
        }

        var candidate = (await SelectAsync()).Should().ContainSingle().Subject;
        candidate.LocalPath.Should().Be(@"C:\m\main.safetensors");
        candidate.ThumbnailAttemptedAt.Should().Be(Attempted);
        candidate.ThumbnailFailure.Should().Be(ThumbnailFailureReason.HttpError);
    }

    /// <summary>
    /// SQLite promises no row order without an ORDER BY, so the ordering is applied in memory and
    /// asserted: repeated runs must hand the step the same work in the same sequence.
    /// </summary>
    [Fact]
    public async Task ThumbnailCandidates_OrderByModelThenVersion()
    {
        var first = await SeedAsync("a", null, Img(Static));
        var second = await SeedAsync("b", null, Img(Static));

        int extraVersionId;
        using (var scope = NewScope())
        {
            // A second version on the FIRST model, created last so it owns the highest version id.
            // Model order therefore has to beat version id, or this lands at the end.
            var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
            var extra = new ModelVersion { Name = "a v2", ModelId = first.ModelId };
            extra.Images.Add(Img(Static));
            context.ModelVersions.Add(extra);
            await context.SaveChangesAsync();
            extraVersionId = extra.Id;
        }

        var candidates = await SelectAsync();

        candidates.Select(c => c.ModelId).Should().Equal(first.ModelId, first.ModelId, second.ModelId);
        candidates.Select(c => c.VersionId).Should().Equal(first.VersionId, extraVersionId, second.VersionId);
    }

    /// <summary>
    /// The library predicate applies exactly as it does to every other selection: a model whose file
    /// sits outside the enabled roots is invisible, unless the user pointed at it by id.
    /// </summary>
    [Fact]
    public async Task ThumbnailCandidates_RespectTheLibraryAndExplicitIds()
    {
        int outsideId;
        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = new Model { Name = "outside", Type = ModelType.LORA, Source = DataSource.LocalFile };
            var version = new ModelVersion { Name = "outside v1" };
            version.Files.Add(new ModelFile { FileName = "o.safetensors", LocalPath = @"D:\elsewhere\o.safetensors", IsPrimary = true, IsLocalFileValid = true });
            version.Images.Add(Img(Static));
            model.Versions.Add(version);
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            outsideId = model.Id;
        }

        var inside = await SeedAsync("inside", null, Img(Static));

        (await SelectAsync()).Select(c => c.ModelId).Should().Equal(inside.ModelId);
        (await SelectAsync(SyncScope.ForModels(outsideId))).Select(c => c.ModelId).Should().Equal(outsideId);
    }

    /// <summary>
    /// R8 discipline, per the global constraint. The flag needs the column, so the SQL mentions it —
    /// but only inside <c>IS NOT NULL</c> and <c>&lt;&gt; X''</c>, both of which SQLite answers
    /// without handing the bytes over. Neutralise exactly those two forms and nothing may be left:
    /// anything remaining would be the column in the projection, and a library-wide run selects
    /// every image row there is.
    /// </summary>
    [Fact]
    public async Task ThumbnailCandidates_NeverSelectTheBlobColumn()
    {
        var sql = new List<string>();

        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options
            .UseSqlite(connection)
            .LogTo(sql.Add, [DbLoggerCategory.Database.Command.Name], LogLevel.Information));

        using var provider = services.BuildServiceProvider();

        using (var seedScope = provider.CreateScope())
        {
            seedScope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>().Database.EnsureCreated();

            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var heavy = new Model { Name = "heavy", Type = ModelType.LORA, Source = DataSource.LocalFile };
            var heavyVersion = new ModelVersion { Name = "heavy v1" };
            heavyVersion.Files.Add(new ModelFile { FileName = "heavy.safetensors", LocalPath = @"C:\m\heavy.safetensors", IsPrimary = true, IsLocalFileValid = true });
            // A megabyte of it, so a projection that dragged it along would be unmistakable.
            heavyVersion.Images.Add(Img(Static, thumbnail: new byte[1024 * 1024]));
            heavy.Versions.Add(heavyVersion);

            var bare = new Model { Name = "bare", Type = ModelType.LORA, Source = DataSource.LocalFile };
            var bareVersion = new ModelVersion { Name = "bare v1" };
            bareVersion.Files.Add(new ModelFile { FileName = "bare.safetensors", LocalPath = @"C:\m\bare.safetensors", IsPrimary = true, IsLocalFileValid = true });
            bareVersion.Images.Add(Img(Static));
            bare.Versions.Add(bareVersion);

            await uow.Models.AddAsync(heavy);
            await uow.Models.AddAsync(bare);
            await uow.SaveChangesAsync();
        }

        sql.Clear();

        using (var scope = provider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var candidates = await uow.SyncStates.SelectThumbnailCandidatesAsync(SyncScope.Library, Roots);

            candidates.Should().ContainSingle("only the version without bytes is work")
                .Which.Name.Should().Be("bare v1");
        }

        var captured = string.Join("\n", sql);

        // Positive first: if the flag ever stopped being computed in SQL the assertion below would
        // pass for the wrong reason.
        captured.Should().Contain("ThumbnailData", "the emptiness flag is answered inside SQLite");

        var withoutFlag = Regex.Replace(
            captured,
            @"""\w+""\.""ThumbnailData"" (IS NOT NULL|<> X'')",
            "<has-thumbnail-flag>");

        withoutFlag.Should().NotContain("ThumbnailData");
    }

    /// <summary>
    /// The contract, asserted against the property itself over a spread of shapes: whatever the tile
    /// would display is what gets thumbnailed.
    /// </summary>
    /// <remarks>
    /// The last two seeds are the interesting ones. <c>PrimaryImage</c> is
    /// <c>Images.FirstOrDefault(...)</c> and nothing orders that collection — no EF configuration
    /// does, and the generated SQL orders only by the principal key — so "first" is the order
    /// SQLite returns the rows in, which is ascending <c>Id</c>. <c>SortOrder</c> is therefore NOT
    /// a tie-break the property has, and the selection must not invent one: <c>sort-vs-id</c> pins
    /// that, and would fail loudly if a provider upgrade ever changed the collection's order.
    /// </remarks>
    [Fact]
    public async Task ThumbnailCandidates_ParityWithPrimaryImageProperty()
    {
        var seeds = new List<(string Name, int VersionId)>();

        async Task Seed(string name, params ModelImage[] images)
            => seeds.Add((name, (await SeedAsync(name, null, images)).VersionId));

        // static first, video second
        await Seed("static-then-video", Img(Static, 0), Img(Video, 1, mediaType: "video"));
        // video first, static second — the static still wins
        await Seed("video-then-static", Img(Video, 0, mediaType: "video"), Img(Static, 1));
        // no clean image: an NSFW still beats a safe video, because a video cannot be shown at all
        await Seed("nsfw-still-vs-safe-video", Img(Static, 0, nsfw: true), Img(Video, 1, mediaType: "video"));
        // nothing but videos: the NSFW one loses to the safe one regardless of position
        await Seed("nsfw-video-then-safe-video",
            Img(Video, 0, mediaType: "video", nsfw: true), Img(Video + "?2", 1, mediaType: "video"));
        // last resort: a single NSFW video is still the primary
        await Seed("nsfw-video-only", Img(Video, 0, mediaType: "video", nsfw: true));
        // two equals whose SortOrder disagrees with their id order — id order is what the property sees
        await Seed("sort-vs-id", Img(Static + "?first", 9), Img(Static + "?second", 0));

        var candidates = (await SelectAsync()).ToDictionary(c => c.VersionId);

        using var scope = NewScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();

        foreach (var (name, versionId) in seeds)
        {
            var version = await context.ModelVersions.AsNoTracking()
                .Include(v => v.Images)
                .FirstAsync(v => v.Id == versionId);

            candidates.Should().ContainKey(versionId, "{0} has no thumbnail yet", name);
            candidates[versionId].ImageId.Should().Be(version.PrimaryImage!.Id, "{0}", name);
        }
    }

    /// <summary>
    /// The step mutates the row it is handed, so this one is deliberately tracked — and a row deleted
    /// between selection and execution comes back null rather than throwing.
    /// </summary>
    [Fact]
    public async Task GetImageByIdReturnsATrackedRowOrNull()
    {
        var seed = await SeedAsync("tracked", null, Img(Static));

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var image = await uow.Models.GetImageByIdAsync(seed.ImageIds[0]);
            image.Should().NotBeNull();

            // A second read of the same id comes back as the very same instance, which only a
            // tracked query does — AsNoTracking would materialise a second object.
            (await uow.Models.GetImageByIdAsync(seed.ImageIds[0])).Should().BeSameAs(image);

            image!.ThumbnailFailure = ThumbnailFailureReason.HttpError;
            image.ThumbnailAttemptedAt = Attempted;
            (await uow.SaveChangesAsync()).Should().Be(1, "the change is picked up without an explicit Update");

            (await uow.Models.GetImageByIdAsync(seed.ImageIds[0] + 5000)).Should().BeNull();
        }

        using var check = NewScope();
        var stored = await check.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .Models.GetImageByIdAsync(seed.ImageIds[0]);
        stored!.ThumbnailFailure.Should().Be(ThumbnailFailureReason.HttpError);
        stored.ThumbnailAttemptedAt.Should().Be(Attempted);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
