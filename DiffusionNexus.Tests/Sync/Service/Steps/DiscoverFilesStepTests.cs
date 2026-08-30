using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Service.Services.Sync.Steps;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Sync.Service.Steps;

/// <summary>
/// Covers <see cref="DiscoverFilesStep"/> — the thin adapter that lets the existing
/// <see cref="IModelSyncService.DiscoverNewFilesAsync"/> disk scan participate in a sync run,
/// including the discovered count the report reads back off the step (#521 WP2).
/// </summary>
public sealed class DiscoverFilesStepTests
{
    private static Model NewModel(string name) => new() { Name = name, Type = ModelType.LORA, Source = DataSource.LocalFile };

    /// <summary>Builds a provider whose only registration is the (scoped) sync-service mock.</summary>
    private static (DiscoverFilesStep Step, ServiceProvider Provider) NewStep(Mock<IModelSyncService> sync)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => sync.Object);
        var provider = services.BuildServiceProvider();
        return (new DiscoverFilesStep(provider.GetRequiredService<IServiceScopeFactory>()), provider);
    }

    private static SyncOptions Options() => new(new HashSet<SyncStepKind> { SyncStepKind.DiscoverFiles });

    [Fact]
    public async Task Select_ReturnsASinglePseudoItemBecauseTheCountIsUnknownUntilTheScanRuns()
    {
        var sync = new Mock<IModelSyncService>();
        var (step, provider) = NewStep(sync);
        using var _ = provider;

        var items = await step.SelectAsync(SyncScope.Library, Options(), DateTimeOffset.UtcNow, CancellationToken.None);

        items.Should().ContainSingle();
        items[0].ModelId.Should().Be(0);
        items[0].Payload.Should().Be(SyncScope.Library);
        step.Kind.Should().Be(SyncStepKind.DiscoverFiles);
        step.EstimatedPerItem.Should().Be(TimeSpan.FromSeconds(2));
        step.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Execute_StoresTheDiscoveredCountForTheReport()
    {
        var sync = new Mock<IModelSyncService>();
        sync.Setup(s => s.DiscoverNewFilesAsync(It.IsAny<IProgress<SyncProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiscoveryResult { NewModels = [NewModel("a"), NewModel("b"), NewModel("c")] });
        var (step, provider) = NewStep(sync);
        using var _ = provider;

        var items = await step.SelectAsync(SyncScope.Library, Options(), DateTimeOffset.UtcNow, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items[0], apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        step.DiscoveredCount.Should().Be(3);
    }

    /// <summary>
    /// #537. A repoint — a moved file hash-matched to an existing invalid-path row — is a change
    /// the scan committed, not a new file. It travels beside the discovered count so a
    /// repoint-only scan stops reading as "nothing happened".
    /// </summary>
    [Fact]
    public async Task Execute_StoresTheRepointedCountForTheReport()
    {
        var sync = new Mock<IModelSyncService>();
        sync.Setup(s => s.DiscoverNewFilesAsync(It.IsAny<IProgress<SyncProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiscoveryResult { RepointedCount = 12 });
        var (step, provider) = NewStep(sync);
        using var _ = provider;

        var items = await step.SelectAsync(SyncScope.Library, Options(), DateTimeOffset.UtcNow, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items[0], apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        step.DiscoveredCount.Should().Be(0, "nothing was added");
        step.RepointedCount.Should().Be(12, "but twelve rows changed, and the report is how anyone learns that");
    }

    /// <summary>
    /// #527 round 2. ReclassifySupportAssetsAsync now runs INSIDE DiscoverNewFilesAsync itself, so
    /// the step reads the count off the DiscoveryResult rather than calling the pass a second
    /// time. This pins that reading, and — just as importantly — that the step does NOT also call
    /// ReclassifySupportAssetsAsync directly any more: since the pass is self-terminating (a
    /// reclassified row no longer matches its own candidate query), a second direct call here
    /// would silently return 0 and overwrite the real count with it, undoing the whole fix.
    /// </summary>
    [Fact]
    public async Task Execute_StoresTheReclassifiedCountForTheReport()
    {
        var sync = new Mock<IModelSyncService>();
        sync.Setup(s => s.DiscoverNewFilesAsync(It.IsAny<IProgress<SyncProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiscoveryResult { ReclassifiedCount = 35 });
        var (step, provider) = NewStep(sync);
        using var _ = provider;

        var items = await step.SelectAsync(SyncScope.Library, Options(), DateTimeOffset.UtcNow, CancellationToken.None);
        var result = await step.ExecuteOneAsync(items[0], apiKey: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        step.ReclassifiedCount.Should().Be(35, "35 rows just stopped claiming to be LoRAs, and the report is how anyone learns that");
        // Both parameters use It.IsAny: the compiler bakes a bare It.IsAny<CancellationToken>()
        // call into an expression pinning excludeModelIds to its literal default (null), which
        // would let a future call made with a non-null exclusion set slip past this guard unseen.
        sync.Verify(s => s.ReclassifySupportAssetsAsync(It.IsAny<CancellationToken>(), It.IsAny<IReadOnlySet<int>?>()), Times.Never,
            "DiscoverNewFilesAsync already ran the pass internally; a second direct call here would double-run it and, being self-terminating, silently report 0");
    }

    [Fact]
    public async Task Execute_FailureReportsTheReasonAndLeavesNoStaleCount()
    {
        var sync = new Mock<IModelSyncService>();
        sync.SetupSequence(s => s.DiscoverNewFilesAsync(It.IsAny<IProgress<SyncProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiscoveryResult { NewModels = [NewModel("a")], RepointedCount = 2 })
            .ThrowsAsync(new IOException("source folder unavailable"));
        var (step, provider) = NewStep(sync);
        using var _ = provider;

        var items = await step.SelectAsync(SyncScope.Library, Options(), DateTimeOffset.UtcNow, CancellationToken.None);

        (await step.ExecuteOneAsync(items[0], apiKey: null, CancellationToken.None)).Succeeded.Should().BeTrue();
        step.DiscoveredCount.Should().Be(1);
        step.RepointedCount.Should().Be(2);

        var failure = await step.ExecuteOneAsync(items[0], apiKey: null, CancellationToken.None);

        failure.Succeeded.Should().BeFalse();
        failure.FailureReason.Should().Contain("source folder unavailable");
        step.DiscoveredCount.Should().Be(0);
        step.RepointedCount.Should().Be(0, "a failed scan may not leave the previous scan's count standing");
    }

    [Fact]
    public async Task Execute_CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        var sync = new Mock<IModelSyncService>();
        sync.Setup(s => s.DiscoverNewFilesAsync(It.IsAny<IProgress<SyncProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ThrowsAsync(new OperationCanceledException());
        var (step, provider) = NewStep(sync);
        using var _ = provider;

        var items = await step.SelectAsync(SyncScope.Library, Options(), DateTimeOffset.UtcNow, CancellationToken.None);
        var act = () => step.ExecuteOneAsync(items[0], apiKey: null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
