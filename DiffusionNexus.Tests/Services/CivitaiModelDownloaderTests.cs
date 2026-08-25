using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Infrastructure.Services;
using DiffusionNexus.Service.Services.Sync;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Download;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Covers <see cref="CivitaiModelDownloader"/> — the ONE Civitai download path (spec §4.4).
/// It owns the file pick, the collision policy, the single coordinator enqueue (D3), SHA256
/// verification, persistence, the Tags+Thumbnails completion sync and the
/// <see cref="ILibraryChangeNotifier"/> signal, so the five callers that each carried their own
/// half of that list can stop diverging.
/// </summary>
public sealed class CivitaiModelDownloaderTests : IDisposable
{
    private const string Url = "https://civitai.test/api/download/models/4242";

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best effort — a leaked temp dir must never fail a test run
            }
        }
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dn-downloader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static CivitaiModelVersion Version(
        int id = 4242, string fileName = "model.safetensors", string? sha256 = null, string? url = Url)
        => new()
        {
            Id = id,
            Name = "v1",
            Files =
            [
                new CivitaiModelFile
                {
                    Id = 1,
                    Name = fileName,
                    Primary = true,
                    DownloadUrl = url,
                    Hashes = sha256 is null ? null : new CivitaiFileHashes { SHA256 = sha256 },
                },
            ],
        };

    private static string Sha256Of(string content)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, content);
            return FileHasher.Sha256Upper(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Transport whose DownloadFileAsync writes <paramref name="content"/> then reports success.</summary>
    private static Mock<ILoraDownloadService> Transport(
        string? content = "downloaded-bytes",
        bool succeed = true,
        bool metadataIncomplete = false,
        MetadataPersistOutcome persistOutcome = MetadataPersistOutcome.Complete)
    {
        var transport = new Mock<ILoraDownloadService>();
        transport
            .Setup(t => t.DownloadFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CivitaiModelVersion>(), It.IsAny<string>(),
                It.IsAny<Action<double, string>?>(), It.IsAny<Action?>(), It.IsAny<Action?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<Action?>()))
            .Callback<string, string, CivitaiModelVersion, string, Action<double, string>?, Action?, Action?, int?, CancellationToken, bool, Action?>(
                (_, targetPath, _, _, reportProgress, completed, failed, _, _, _, incomplete) =>
                {
                    reportProgress?.Invoke(0.5, "Downloading");
                    if (content is not null) File.WriteAllText(targetPath, content);
                    if (metadataIncomplete) incomplete?.Invoke();
                    if (succeed) completed?.Invoke();
                    else failed?.Invoke();
                })
            .Returns(Task.CompletedTask);
        transport
            .Setup(t => t.PersistDownloadedModelAsync(It.IsAny<string>(), It.IsAny<CivitaiModelVersion>(), It.IsAny<int?>()))
            .ReturnsAsync(persistOutcome);
        return transport;
    }

    private static Mock<IDownloadCoordinator> Coordinator()
    {
        var coordinator = new Mock<IDownloadCoordinator>();
        coordinator
            .Setup(c => c.EnqueueAsync(
                It.IsAny<string>(),
                It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>> work, CancellationToken ct)
                => work(new Progress<DownloadTaskProgress>(), ct));
        return coordinator;
    }

    private static Mock<ILibrarySyncService> Sync(bool isRunning = false)
    {
        var sync = new Mock<ILibrarySyncService>();
        sync.SetupGet(s => s.IsRunning).Returns(isRunning);
        sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncScope scope, SyncOptions options, CancellationToken _)
                => new SyncPlan(scope, options, [], DateTimeOffset.UtcNow));
        sync.Setup(s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SyncPlan plan, IProgress<LibrarySyncProgress>? _, CancellationToken __)
                => new SyncReport(plan, [], [], false, TimeSpan.Zero, 0));
        return sync;
    }

    /// <summary>Scope factory whose scoped IUnitOfWork resolves any local path to <paramref name="modelId"/>.</summary>
    private static IServiceScopeFactory ScopeFactory(int? modelId)
    {
        var repository = new Mock<IModelRepository>();
        repository
            .Setup(r => r.FindByLocalFilePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelId is null ? null : new Model { Id = modelId.Value, Name = "resolved" });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.Models).Returns(repository.Object);
        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class DirectProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = [];

        public void Report(DownloadProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task HappyPath_Completes_PersistsNotifiesAndPlansTagsAndThumbnails()
    {
        var dir = NewTempDir();
        var transport = Transport();
        var sync = Sync();
        var notifier = new LibraryChangeNotifier();
        var notified = new List<int>();
        notifier.ModelDownloaded += (_, e) => notified.Add(e.ModelId);
        SyncScope? plannedScope = null;
        SyncOptions? plannedOptions = null;
        sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .Callback<SyncScope, SyncOptions, CancellationToken>((scope, options, _) =>
            {
                plannedScope = scope;
                plannedOptions = options;
            })
            .ReturnsAsync((SyncScope scope, SyncOptions options, CancellationToken _)
                => new SyncPlan(scope, options, [], DateTimeOffset.UtcNow));

        var downloader = new CivitaiModelDownloader(
            transport.Object, Coordinator().Object, sync.Object, notifier, ScopeFactory(77));

        var outcome = await downloader.DownloadAsync(new DownloadRequest(Version(), dir, DownloadTrigger.Dialog));

        outcome.Status.Should().Be(DownloadStatus.Completed);
        outcome.Success.Should().BeTrue();
        outcome.Error.Should().BeNull();
        outcome.FinalPath.Should().Be(Path.Combine(dir, "model.safetensors"));
        outcome.RenamedForCollision.Should().BeFalse();
        outcome.ModelId.Should().Be(77);
        notified.Should().ContainSingle().Which.Should().Be(77);
        // SyncScope is a record over an IReadOnlyList, so its generated equality is reference-based
        // on ModelIds — compare the parts, not the record.
        plannedScope!.Kind.Should().Be(SyncScopeKind.Models);
        plannedScope.ModelIds.Should().Equal(77);
        plannedOptions!.Steps.Should().BeEquivalentTo(new[] { SyncStepKind.FetchTags, SyncStepKind.Thumbnails });
        sync.Verify(
            s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Enqueues_TaskName_IntoTheCoordinator_Once()
    {
        var dir = NewTempDir();
        var coordinator = Coordinator();

        var downloader = new CivitaiModelDownloader(
            Transport().Object, coordinator.Object, Sync().Object, new LibraryChangeNotifier(), ScopeFactory(1));

        await downloader.DownloadAsync(new DownloadRequest(Version(), dir, DownloadTrigger.BrowseQueue));

        coordinator.Verify(
            c => c.EnqueueAsync(
                "Download model.safetensors",
                It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Collision_WithForeignBytes_SuffixesVersionId_AndLeavesForeignFileUntouched()
    {
        var dir = NewTempDir();
        var foreign = Path.Combine(dir, "model.safetensors");
        File.WriteAllText(foreign, "someone-elses-weights");

        var downloader = new CivitaiModelDownloader(
            Transport().Object, Coordinator().Object, Sync().Object, new LibraryChangeNotifier(), ScopeFactory(9));

        var outcome = await downloader.DownloadAsync(
            new DownloadRequest(Version(sha256: Sha256Of("downloaded-bytes")), dir, DownloadTrigger.BrowseQueue));

        outcome.Status.Should().Be(DownloadStatus.Completed);
        outcome.RenamedForCollision.Should().BeTrue();
        outcome.FinalPath.Should().Be(Path.Combine(dir, "model_4242.safetensors"));
        File.ReadAllText(foreign).Should().Be("someone-elses-weights");
    }

    [Fact]
    public async Task ByteIdenticalFileOnDisk_ReusesExisting_WithoutTransferring()
    {
        var dir = NewTempDir();
        var existing = Path.Combine(dir, "model.safetensors");
        File.WriteAllText(existing, "already-here");
        var transport = Transport();

        var downloader = new CivitaiModelDownloader(
            transport.Object, Coordinator().Object, Sync().Object, new LibraryChangeNotifier(), ScopeFactory(4));

        var outcome = await downloader.DownloadAsync(
            new DownloadRequest(Version(sha256: Sha256Of("already-here")), dir, DownloadTrigger.Dialog));

        outcome.Status.Should().Be(DownloadStatus.ReusedExisting);
        outcome.Success.Should().BeTrue();
        outcome.FinalPath.Should().Be(existing);
        outcome.ModelId.Should().Be(4);
        transport.Verify(
            t => t.DownloadFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CivitaiModelVersion>(), It.IsAny<string>(),
                It.IsAny<Action<double, string>?>(), It.IsAny<Action?>(), It.IsAny<Action?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<Action?>()),
            Times.Never);
        transport.Verify(
            t => t.PersistDownloadedModelAsync(existing, It.IsAny<CivitaiModelVersion>(), It.IsAny<int?>()),
            Times.Once);
    }

    [Theory]
    [InlineData(MetadataPersistOutcome.Partial)]
    [InlineData(MetadataPersistOutcome.Failed)]
    public async Task ReusedFileWhoseMetadataDidNotPersist_ReportsCompletedMetadataIncomplete(
        MetadataPersistOutcome persistOutcome)
    {
        // The bytes were already on disk, but the Civitai model-page fetch failed — so the library
        // gains a row with no description, tags or preview. The transfer path already surfaces that
        // as CompletedMetadataIncomplete ("Done — no metadata"); reporting the reuse path as a clean
        // ReusedExisting was exactly the silence that status was introduced to eliminate.
        var dir = NewTempDir();
        var existing = Path.Combine(dir, "model.safetensors");
        File.WriteAllText(existing, "already-here");

        var downloader = new CivitaiModelDownloader(
            Transport(persistOutcome: persistOutcome).Object, Coordinator().Object, Sync().Object,
            new LibraryChangeNotifier(), ScopeFactory(4));

        var outcome = await downloader.DownloadAsync(
            new DownloadRequest(Version(sha256: Sha256Of("already-here")), dir, DownloadTrigger.Dialog));

        outcome.Status.Should().Be(DownloadStatus.CompletedMetadataIncomplete);
        outcome.Success.Should().BeTrue("the file is on disk — only its metadata is missing");
        outcome.FinalPath.Should().Be(existing);
    }

    [Fact]
    public async Task MetadataIncompleteCallback_YieldsCompletedMetadataIncomplete()
    {
        var dir = NewTempDir();

        var downloader = new CivitaiModelDownloader(
            Transport(metadataIncomplete: true).Object, Coordinator().Object, Sync().Object,
            new LibraryChangeNotifier(), ScopeFactory(12));

        var outcome = await downloader.DownloadAsync(new DownloadRequest(Version(), dir, DownloadTrigger.DetailPanel));

        outcome.Status.Should().Be(DownloadStatus.CompletedMetadataIncomplete);
        outcome.Success.Should().BeTrue();
        outcome.ModelId.Should().Be(12);
    }

    [Fact]
    public async Task TransportReportsFailure_YieldsFailed_AndDoesNotNotifyOrSync()
    {
        var dir = NewTempDir();
        var sync = Sync();
        var notifier = new LibraryChangeNotifier();
        var notified = 0;
        notifier.ModelDownloaded += (_, _) => notified++;

        var downloader = new CivitaiModelDownloader(
            Transport(content: null, succeed: false).Object, Coordinator().Object, sync.Object, notifier, ScopeFactory(5));

        var outcome = await downloader.DownloadAsync(new DownloadRequest(Version(), dir, DownloadTrigger.BrowseQueue));

        outcome.Status.Should().Be(DownloadStatus.Failed);
        outcome.Success.Should().BeFalse();
        outcome.Error.Should().NotBeNullOrWhiteSpace();
        outcome.ModelId.Should().BeNull();
        notified.Should().Be(0);
        sync.Verify(
            s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CancelledDuringTransfer_YieldsCancelled()
    {
        var dir = NewTempDir();
        using var cts = new CancellationTokenSource();
        var transport = new Mock<ILoraDownloadService>();
        transport
            .Setup(t => t.DownloadFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CivitaiModelVersion>(), It.IsAny<string>(),
                It.IsAny<Action<double, string>?>(), It.IsAny<Action?>(), It.IsAny<Action?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<Action?>()))
            .Callback<string, string, CivitaiModelVersion, string, Action<double, string>?, Action?, Action?, int?, CancellationToken, bool, Action?>(
                (_, _, _, _, _, _, failed, _, _, _, _) =>
                {
                    cts.Cancel();
                    failed?.Invoke();
                })
            .Returns(Task.CompletedTask);

        var downloader = new CivitaiModelDownloader(
            transport.Object, null, Sync().Object, new LibraryChangeNotifier(), ScopeFactory(3));

        var outcome = await downloader.DownloadAsync(
            new DownloadRequest(Version(), dir, DownloadTrigger.Dialog), progress: null, ct: cts.Token);

        outcome.Status.Should().Be(DownloadStatus.Cancelled);
        outcome.ModelId.Should().BeNull();
    }

    [Fact]
    public async Task CoordinatorSideCancel_YieldsCancelled_EvenThoughTheCallersTokenIsUntouched()
    {
        // The real DownloadCoordinator runs the work against a token LINKED to the caller's, which
        // the flyout Cancel button and shutdown also signal, then swallows the cancellation and
        // returns false. Deciding from the caller's ct alone reported a user cancel as "Failed".
        var dir = NewTempDir();
        using var linked = new CancellationTokenSource();
        var transport = new Mock<ILoraDownloadService>();
        transport
            .Setup(t => t.DownloadFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CivitaiModelVersion>(), It.IsAny<string>(),
                It.IsAny<Action<double, string>?>(), It.IsAny<Action?>(), It.IsAny<Action?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<Action?>()))
            .Callback<string, string, CivitaiModelVersion, string, Action<double, string>?, Action?, Action?, int?, CancellationToken, bool, Action?>(
                (_, _, _, _, _, _, failed, _, _, _, _) =>
                {
                    linked.Cancel();
                    failed?.Invoke();
                })
            .Returns(Task.CompletedTask);
        var coordinator = new Mock<IDownloadCoordinator>();
        coordinator
            .Setup(c => c.EnqueueAsync(
                It.IsAny<string>(),
                It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string _, Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>> work, CancellationToken _) =>
            {
                try
                {
                    return await work(new Progress<DownloadTaskProgress>(), linked.Token);
                }
                catch (OperationCanceledException)
                {
                    return false; // exactly what DownloadCoordinator does
                }
            });
        var notifier = new LibraryChangeNotifier();
        var notified = 0;
        notifier.ModelDownloaded += (_, _) => notified++;

        using var callerCts = new CancellationTokenSource();
        var downloader = new CivitaiModelDownloader(
            transport.Object, coordinator.Object, Sync().Object, notifier, ScopeFactory(17));

        var outcome = await downloader.DownloadAsync(
            new DownloadRequest(Version(), dir, DownloadTrigger.BrowseQueue), progress: null, ct: callerCts.Token);

        callerCts.IsCancellationRequested.Should().BeFalse("only the coordinator's linked token was cancelled");
        outcome.Status.Should().Be(DownloadStatus.Cancelled);
        outcome.Error.Should().Be("cancelled");
        outcome.ModelId.Should().BeNull();
        notified.Should().Be(0);
    }

    [Fact]
    public async Task CancelledWhileStillQueued_YieldsCancelled()
    {
        // A queued task cancelled before a slot frees up never reaches the work delegate: the
        // coordinator's only pre-work await is the slot wait on that same linked token.
        var dir = NewTempDir();
        var coordinator = new Mock<IDownloadCoordinator>();
        coordinator
            .Setup(c => c.EnqueueAsync(
                It.IsAny<string>(),
                It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var downloader = new CivitaiModelDownloader(
            Transport().Object, coordinator.Object, Sync().Object, new LibraryChangeNotifier(), ScopeFactory(2));

        var outcome = await downloader.DownloadAsync(new DownloadRequest(Version(), dir, DownloadTrigger.BrowseQueue));

        outcome.Status.Should().Be(DownloadStatus.Cancelled);
        outcome.ModelId.Should().BeNull();
    }

    [Fact]
    public async Task WrittenBytesDoNotMatchExpectedHash_YieldsHashMismatch_AndLeavesFileOnDisk()
    {
        var dir = NewTempDir();
        var notifier = new LibraryChangeNotifier();
        var notified = 0;
        notifier.ModelDownloaded += (_, _) => notified++;

        var downloader = new CivitaiModelDownloader(
            Transport(content: "corrupt-bytes").Object, Coordinator().Object, Sync().Object, notifier, ScopeFactory(6));

        var outcome = await downloader.DownloadAsync(
            new DownloadRequest(Version(sha256: Sha256Of("the-real-bytes")), dir, DownloadTrigger.BrowseQueue));

        outcome.Status.Should().Be(DownloadStatus.HashMismatch);
        outcome.Success.Should().BeFalse();
        outcome.Error.Should().NotBeNullOrWhiteSpace();
        File.Exists(Path.Combine(dir, "model.safetensors")).Should().BeTrue();
        notified.Should().Be(0);
    }

    [Fact]
    public async Task SyncAlreadyRunning_SkipsExecute_ButStillNotifies()
    {
        var dir = NewTempDir();
        var sync = Sync(isRunning: true);
        var notifier = new LibraryChangeNotifier();
        var notified = new List<int>();
        notifier.ModelDownloaded += (_, e) => notified.Add(e.ModelId);

        var downloader = new CivitaiModelDownloader(
            Transport().Object, Coordinator().Object, sync.Object, notifier, ScopeFactory(21));

        var outcome = await downloader.DownloadAsync(new DownloadRequest(Version(), dir, DownloadTrigger.Waitlist));

        outcome.Status.Should().Be(DownloadStatus.Completed);
        sync.Verify(
            s => s.ExecuteAsync(It.IsAny<SyncPlan>(), It.IsAny<IProgress<LibrarySyncProgress>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        notified.Should().ContainSingle().Which.Should().Be(21);
    }

    [Fact]
    public async Task CompletionSyncThrowing_DoesNotFailTheDownload()
    {
        var dir = NewTempDir();
        var sync = Sync();
        sync.Setup(s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("a sync is already running"));
        var notifier = new LibraryChangeNotifier();
        var notified = 0;
        notifier.ModelDownloaded += (_, _) => notified++;

        var downloader = new CivitaiModelDownloader(
            Transport().Object, Coordinator().Object, sync.Object, notifier, ScopeFactory(31));

        var outcome = await downloader.DownloadAsync(new DownloadRequest(Version(), dir, DownloadTrigger.Pipeline));

        outcome.Status.Should().Be(DownloadStatus.Completed);
        outcome.ModelId.Should().Be(31);
        notified.Should().Be(1);
    }

    [Fact]
    public async Task WithoutCoordinator_RunsInline_AndTellsTransportToReportToActivityLog()
    {
        var dir = NewTempDir();
        var transport = Transport();

        var downloader = new CivitaiModelDownloader(
            transport.Object, null, Sync().Object, new LibraryChangeNotifier(), ScopeFactory(8));

        var outcome = await downloader.DownloadAsync(new DownloadRequest(Version(), dir, DownloadTrigger.Dialog));

        outcome.Status.Should().Be(DownloadStatus.Completed);
        transport.Verify(
            t => t.DownloadFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CivitaiModelVersion>(), It.IsAny<string>(),
                It.IsAny<Action<double, string>?>(), It.IsAny<Action?>(), It.IsAny<Action?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>(), true, It.IsAny<Action?>()),
            Times.Once);
    }

    [Fact]
    public async Task WithCoordinator_TransportDoesNotDoubleReportToActivityLog()
    {
        var dir = NewTempDir();
        var transport = Transport();

        var downloader = new CivitaiModelDownloader(
            transport.Object, Coordinator().Object, Sync().Object, new LibraryChangeNotifier(), ScopeFactory(8));

        await downloader.DownloadAsync(new DownloadRequest(Version(), dir, DownloadTrigger.Dialog));

        transport.Verify(
            t => t.DownloadFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CivitaiModelVersion>(), It.IsAny<string>(),
                It.IsAny<Action<double, string>?>(), It.IsAny<Action?>(), It.IsAny<Action?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>(), false, It.IsAny<Action?>()),
            Times.Once);
    }

    [Fact]
    public async Task WithoutScopeFactory_StillSucceeds_WithNullModelId_AndSkipsCompletionAndNotify()
    {
        var dir = NewTempDir();
        var sync = Sync();
        var notifier = new LibraryChangeNotifier();
        var notified = 0;
        notifier.ModelDownloaded += (_, _) => notified++;

        var downloader = new CivitaiModelDownloader(
            Transport().Object, Coordinator().Object, sync.Object, notifier, scopeFactory: null);

        var outcome = await downloader.DownloadAsync(new DownloadRequest(Version(), dir, DownloadTrigger.Dialog));

        outcome.Status.Should().Be(DownloadStatus.Completed);
        outcome.Success.Should().BeTrue();
        outcome.ModelId.Should().BeNull();
        notified.Should().Be(0);
        sync.Verify(
            s => s.PlanAsync(It.IsAny<SyncScope>(), It.IsAny<SyncOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NoDownloadUrlAnywhere_FailsBeforeTouchingDisk()
    {
        var dir = NewTempDir();
        var version = new CivitaiModelVersion { Id = 99, Files = [] };

        var downloader = new CivitaiModelDownloader(
            Transport().Object, Coordinator().Object, Sync().Object, new LibraryChangeNotifier(), ScopeFactory(1));

        var outcome = await downloader.DownloadAsync(new DownloadRequest(version, dir, DownloadTrigger.Dialog));

        outcome.Status.Should().Be(DownloadStatus.Failed);
        outcome.Error.Should().Be("no download URL");
        outcome.FinalPath.Should().BeNull();
        Directory.EnumerateFileSystemEntries(dir).Should().BeEmpty();
    }

    [Fact]
    public async Task UnusableTargetDirectory_IsReportedAsFailed_NotThrown()
    {
        var dir = NewTempDir();
        var blocked = Path.Combine(dir, "not-a-directory");
        File.WriteAllText(blocked, "a file sits where the folder should be");

        var downloader = new CivitaiModelDownloader(
            Transport().Object, Coordinator().Object, Sync().Object, new LibraryChangeNotifier(), ScopeFactory(1));

        var outcome = await downloader.DownloadAsync(new DownloadRequest(Version(), blocked, DownloadTrigger.Dialog));

        outcome.Status.Should().Be(DownloadStatus.Failed);
        outcome.Error.Should().Be("target directory unavailable");
    }

    [Fact]
    public async Task FileNameOverrideAndTaskName_AreHonoured()
    {
        var dir = NewTempDir();
        var coordinator = Coordinator();

        var downloader = new CivitaiModelDownloader(
            Transport().Object, coordinator.Object, Sync().Object, new LibraryChangeNotifier(), ScopeFactory(1));

        var outcome = await downloader.DownloadAsync(new DownloadRequest(Version(), dir, DownloadTrigger.Pipeline)
        {
            FileNameOverride = "renamed.safetensors",
            TaskName = "Pipeline asset",
        });

        outcome.FinalPath.Should().Be(Path.Combine(dir, "renamed.safetensors"));
        outcome.RenamedForCollision.Should().BeFalse();
        coordinator.Verify(
            c => c.EnqueueAsync(
                "Pipeline asset",
                It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task VersionWithoutFiles_FallsBackToVersionDownloadUrl_AndSynthesisesAFileName()
    {
        var dir = NewTempDir();
        var version = new CivitaiModelVersion { Id = 555, DownloadUrl = Url, Files = [] };

        var downloader = new CivitaiModelDownloader(
            Transport().Object, Coordinator().Object, Sync().Object, new LibraryChangeNotifier(), ScopeFactory(2));

        var outcome = await downloader.DownloadAsync(new DownloadRequest(version, dir, DownloadTrigger.Dialog));

        outcome.Status.Should().Be(DownloadStatus.Completed);
        outcome.FinalPath.Should().Be(Path.Combine(dir, "model_555.safetensors"));
    }

    [Fact]
    public async Task Progress_IsForwardedToTheCaller()
    {
        var dir = NewTempDir();
        var progress = new DirectProgress();

        var downloader = new CivitaiModelDownloader(
            Transport().Object, null, Sync().Object, new LibraryChangeNotifier(), ScopeFactory(1));

        await downloader.DownloadAsync(new DownloadRequest(Version(), dir, DownloadTrigger.Dialog), progress);

        progress.Reports.Should().ContainSingle();
        progress.Reports[0].Percent.Should().Be(50);
        progress.Reports[0].Message.Should().Be("Downloading");
    }

    [Fact]
    public async Task ExistingModelId_IsPassedThroughToTheTransport()
    {
        var dir = NewTempDir();
        var transport = Transport();

        var downloader = new CivitaiModelDownloader(
            transport.Object, Coordinator().Object, Sync().Object, new LibraryChangeNotifier(), ScopeFactory(1));

        await downloader.DownloadAsync(new DownloadRequest(Version(), dir, DownloadTrigger.DetailPanel)
        {
            ExistingModelId = 314,
        });

        transport.Verify(
            t => t.DownloadFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CivitaiModelVersion>(), It.IsAny<string>(),
                It.IsAny<Action<double, string>?>(), It.IsAny<Action?>(), It.IsAny<Action?>(),
                314, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<Action?>()),
            Times.Once);
    }
}
