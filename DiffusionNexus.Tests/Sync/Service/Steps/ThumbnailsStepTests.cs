using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Service.Services.Sync.Steps;
using DiffusionNexus.Service.Services.Sync.Thumbnails;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Sync.Service.Steps;

/// <summary>
/// Covers <see cref="ThumbnailsStep"/> — the step that turns a due image row into stored bytes
/// (#521 Plan B). The provider is mocked throughout: what is under test is the step's bookkeeping,
/// not the ladder that produces the bytes (<c>ThumbnailProviderTests</c> owns that).
/// </summary>
/// <remarks>
/// The structural difference from <see cref="FetchImagesStep"/> is the point of most of these
/// tests: work is per <i>image</i> and the record of it lives on that image, so nothing groups and
/// nothing is stamped on the model. One item is one HTTP request, which is what lets the plan's
/// count double as its request count.
/// </remarks>
public sealed class ThumbnailsStepTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<IThumbnailProvider> _provider = new(MockBehavior.Strict);

    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The one enabled LoRA source every seeded model file lives under.</summary>
    private const string Root = @"C:\m";

    private const string StillUrl = "https://image.civitai.com/abc/width=450/still.jpeg";
    private const string VideoUrl = "https://image.civitai.com/abc/width=450/clip.mp4";

    public ThumbnailsStepTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));

        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.GetEnabledLoraSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Root });
        services.AddTransient(_ => settings.Object);

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>().Database.EnsureCreated();
    }

    private IServiceScope NewScope() => _serviceProvider.CreateScope();

    private IServiceScopeFactory Scopes => _serviceProvider.GetRequiredService<IServiceScopeFactory>();

    private ThumbnailsStep NewStep() => new(Scopes, _provider.Object);

    private static SyncOptions Options(bool force = false) =>
        new(new HashSet<SyncStepKind> { SyncStepKind.Thumbnails }, ForceThumbnails: force);

    private static ModelImage Img(
        string url = StillUrl, string? mediaType = null, byte[]? thumbnail = null,
        DateTimeOffset? attemptedAt = null, string? failure = null)
        => new()
        {
            Url = url,
            MediaType = mediaType,
            ThumbnailData = thumbnail,
            ThumbnailAttemptedAt = attemptedAt,
            ThumbnailFailure = failure,
        };

    /// <summary>
    /// Seeds one model with one version per entry of <paramref name="imagePerVersion"/> — each
    /// version owning that single image and a visible primary file under <see cref="Root"/>.
    /// Returns the model id and the version/image ids in the order given.
    /// </summary>
    private async Task<(int ModelId, IReadOnlyList<int> VersionIds, IReadOnlyList<int> ImageIds)> SeedAsync(
        string name, params ModelImage[] imagePerVersion)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = new Model { Name = name, Type = ModelType.LORA, Source = DataSource.LocalFile };

        for (var i = 0; i < imagePerVersion.Length; i++)
        {
            var version = new ModelVersion { Name = $"{name} v{i + 1}", BaseModelRaw = "SDXL 1.0" };
            version.Files.Add(new ModelFile
            {
                FileName = $"{name}-{i + 1}.safetensors",
                LocalPath = Path.Combine(Root, $"{name}-{i + 1}.safetensors"),
                IsPrimary = true,
                IsLocalFileValid = true,
            });
            version.Images.Add(imagePerVersion[i]);
            model.Versions.Add(version);
        }

        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        return (model.Id, model.Versions.Select(v => v.Id).ToList(), imagePerVersion.Select(i => i.Id).ToList());
    }

    private async Task<ModelImage?> ReadImageAsync(int imageId)
    {
        using var scope = NewScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().Models.GetImageByIdAsync(imageId);
    }

    private void SetupProvider(ThumbnailResult result, Action<ThumbnailRequest>? capture = null)
        => _provider
            .Setup(p => p.ProduceAsync(It.IsAny<ThumbnailRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ThumbnailRequest request, CancellationToken _) =>
            {
                capture?.Invoke(request);
                return result;
            });

    private static ThumbnailPayload Payload() => new([1, 2, 3, 4, 5], "image/jpeg", 450, 675);

    // ----------------------------------------------------------------- Select

    /// <summary>
    /// One item per image, never per model: two versions of one model are two thumbnails to fetch
    /// and two rows to record, so grouping them the way the images step does would hide half the
    /// work from the plan and tie two unrelated outcomes together.
    /// </summary>
    [Fact]
    public async Task Select_ReturnsOneItemPerDueImage()
    {
        var twoVersions = await SeedAsync("two", Img(), Img(VideoUrl, mediaType: "video"));
        await SeedAsync("done", Img(thumbnail: [1, 2, 3]));

        var items = await NewStep().SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);

        items.Should().HaveCount(2, "the version with bytes already is not work");
        items.Select(i => i.ModelId).Should().AllBeEquivalentTo(twoVersions.ModelId);
        items.Select(i => i.Name).Should().Equal("two v1", "two v2");

        var candidates = items.Select(i => i.Payload.Should().BeOfType<ThumbnailCandidate>().Subject).ToList();
        candidates.Select(c => c.ImageId).Should().Equal(twoVersions.ImageIds);
        candidates.Select(c => c.VersionId).Should().Equal(twoVersions.VersionIds);
        candidates[0].Url.Should().Be(StillUrl);
        candidates[1].MediaType.Should().Be("video");
        candidates[0].LocalPath.Should().Be(Path.Combine(Root, "two-1.safetensors"));
    }

    /// <summary>
    /// A hard failure is a final answer — asking again costs a request to learn nothing — so only
    /// an explicit force brings the row back. The soft one comes back on its own once the retry
    /// window has passed.
    /// </summary>
    [Fact]
    public async Task Select_HonoursForceThumbnails()
    {
        var hard = await SeedAsync("hard", Img(attemptedAt: Now.AddDays(-400), failure: ThumbnailFailureReason.Http404));
        var soft = await SeedAsync("soft", Img(attemptedAt: Now.AddDays(-400), failure: ThumbnailFailureReason.HttpError));

        var step = NewStep();

        var due = await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None);
        due.Select(i => i.ModelId).Should().BeEquivalentTo([soft.ModelId], "a 404 is not retried without a force");

        var forced = await step.SelectAsync(SyncScope.Library, Options(force: true), Now, CancellationToken.None);
        forced.Select(i => i.ModelId).Should().BeEquivalentTo([hard.ModelId, soft.ModelId]);
    }

    // ---------------------------------------------------------------- Execute

    [Fact]
    public async Task Execute_SuccessPersistsBytesAndStamps()
    {
        var seeded = await SeedAsync("good", Img());
        SetupProvider(ThumbnailResult.Ok(Payload()));

        var step = NewStep();
        var item = (await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None)).Single();

        var result = await step.ExecuteOneAsync(item, apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Skipped.Should().BeFalse();

        // Read back through a fresh scope: the point is what landed in the database, not what the
        // step's own tracked entity remembers.
        var stored = await ReadImageAsync(seeded.ImageIds[0]);
        stored!.ThumbnailData.Should().Equal(1, 2, 3, 4, 5);
        stored.ThumbnailMimeType.Should().Be("image/jpeg");
        stored.ThumbnailWidth.Should().Be(450);
        stored.ThumbnailHeight.Should().Be(675);
        stored.ThumbnailAttemptedAt.Should().NotBeNull();
        stored.ThumbnailFailure.Should().BeNull();

        // ...and the row is no longer work, which is the whole point of recording it.
        (await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_FailureStampsReasonAndCountsAsFailed()
    {
        var seeded = await SeedAsync("gone", Img());
        SetupProvider(ThumbnailResult.Fail(ThumbnailFailureReason.Http404));

        var step = NewStep();
        var item = (await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None)).Single();

        var result = await step.ExecuteOneAsync(item, apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Skipped.Should().BeFalse("a failure the user may want to see is not a no-op");
        result.FailureReason.Should().Be(ThumbnailFailureReason.Http404);

        var stored = await ReadImageAsync(seeded.ImageIds[0]);
        stored!.ThumbnailFailure.Should().Be(ThumbnailFailureReason.Http404);
        stored.ThumbnailAttemptedAt.Should().NotBeNull();
        stored.ThumbnailData.Should().BeNull();

        // Recorded, therefore not re-asked: this is the incremental half of the feature.
        (await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_ImageDeletedMidRunSkips()
    {
        var seeded = await SeedAsync("doomed", Img());

        var step = NewStep();
        var item = (await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None)).Single();

        // The user deletes the model between planning and execution.
        using (var scope = NewScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
            context.ModelImages.Remove(await context.ModelImages.FirstAsync(i => i.Id == seeded.ImageIds[0]));
            await context.SaveChangesAsync();
        }

        var result = await step.ExecuteOneAsync(item, apiKey: null, CancellationToken.None);

        result.Skipped.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        _provider.Verify(p => p.ProduceAsync(It.IsAny<ThumbnailRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "a row that is gone is not worth a request");
    }

    /// <summary>
    /// Selection is one scope and execution another, so a per-tile fetch (or a previous item of the
    /// same run) may have filled the row in between. The entity is loaded fresh from the database
    /// here, so the deferred-BLOB sentinel never applies — these bytes are always real.
    /// </summary>
    [Fact]
    public async Task Execute_ImageFilledSinceSelectionSkips()
    {
        var seeded = await SeedAsync("raced", Img());

        var step = NewStep();
        var item = (await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None)).Single();

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var image = await uow.Models.GetImageByIdAsync(seeded.ImageIds[0]);
            image!.ThumbnailData = [7, 7, 7];
            await uow.SaveChangesAsync();
        }

        var result = await step.ExecuteOneAsync(item, apiKey: null, CancellationToken.None);

        result.Skipped.Should().BeTrue();
        _provider.Verify(p => p.ProduceAsync(It.IsAny<ThumbnailRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // Untouched, above all not overwritten by a second fetch of the same image.
        (await ReadImageAsync(seeded.ImageIds[0]))!.ThumbnailData.Should().Equal(7, 7, 7);
    }

    /// <summary>
    /// The constraint that keeps a library-wide run from pulling gigabytes: in bulk, a video is
    /// worth one poster request and nothing more. The permission exists, but it is the user's to
    /// give, one model at a time.
    /// </summary>
    [Fact]
    public async Task Execute_NeverPassesAllowVideoDownload()
    {
        await SeedAsync("clip", Img(VideoUrl, mediaType: "video"));

        ThumbnailRequest? seen = null;
        SetupProvider(ThumbnailResult.Fail(ThumbnailFailureReason.VideoNoPoster), request => seen = request);

        var step = NewStep();
        var item = (await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None)).Single();

        await step.ExecuteOneAsync(item, apiKey: null, CancellationToken.None);

        seen.Should().NotBeNull();
        seen!.AllowVideoDownload.Should().BeFalse("a bulk run downloads no video, ever");
        seen.Url.Should().Be(VideoUrl);
        seen.MediaType.Should().Be("video");
        seen.ModelLocalPath.Should().Be(Path.Combine(Root, "clip-1.safetensors"));
    }

    /// <summary>
    /// A cancelled item is work not done, not work that failed: nothing may be stamped, or the row
    /// is written off as attempted when nobody ever asked the CDN about it.
    /// </summary>
    [Fact]
    public async Task Execute_CancellationRethrowsUnstamped()
    {
        var seeded = await SeedAsync("cancelled", Img());

        using var cts = new CancellationTokenSource();
        _provider
            .Setup(p => p.ProduceAsync(It.IsAny<ThumbnailRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (ThumbnailRequest _, CancellationToken ct) =>
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return ThumbnailResult.Ok(Payload());
            });

        var step = NewStep();
        var item = (await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None)).Single();

        var execute = () => step.ExecuteOneAsync(item, apiKey: null, cts.Token);
        await execute.Should().ThrowAsync<OperationCanceledException>();

        var stored = await ReadImageAsync(seeded.ImageIds[0]);
        stored!.ThumbnailAttemptedAt.Should().BeNull("a cancelled attempt is not an attempt");
        stored.ThumbnailFailure.Should().BeNull();
    }

    /// <summary>
    /// The catch ladder's own case. The provider answers with reasons rather than exceptions, so
    /// anything thrown here is disk or database — one failed item, named by its type, and the run
    /// carries on.
    /// </summary>
    [Fact]
    public async Task Execute_ItemFaultFailsTheItemWithoutStamping()
    {
        var seeded = await SeedAsync("faulty", Img());

        _provider
            .Setup(p => p.ProduceAsync(It.IsAny<ThumbnailRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("the disk went away"));

        var step = NewStep();
        var item = (await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None)).Single();

        var result = await step.ExecuteOneAsync(item, apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Skipped.Should().BeFalse();
        result.FailureReason.Should().Be(nameof(IOException));

        var stored = await ReadImageAsync(seeded.ImageIds[0]);
        stored!.ThumbnailAttemptedAt.Should().BeNull("nothing was recorded, so the row stays due");
        stored.ThumbnailFailure.Should().BeNull();

        (await step.SelectAsync(SyncScope.Library, Options(), Now, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task Execute_RejectsAPayloadThatIsNotAThumbnailCandidate()
    {
        var execute = () => NewStep().ExecuteOneAsync(new SyncItem(1, "wrong", new object()), apiKey: null, CancellationToken.None);

        await execute.Should().ThrowAsync<ArgumentException>().WithMessage($"*{nameof(ThumbnailCandidate)}*");
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
