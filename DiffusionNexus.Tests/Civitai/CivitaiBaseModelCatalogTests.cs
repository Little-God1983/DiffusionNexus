using System.Net;
using System.Text;
using System.Text.Json;
using DiffusionNexus.Civitai;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

/// <summary>
/// Covers the 4-tier fallback chain (memory -> disk TTL -> live GitHub fetch ->
/// bundled snapshot) and the three TypeScript parsing strategies of
/// <see cref="CivitaiBaseModelCatalog"/>. All HTTP goes through a fake handler;
/// the catalog never touches the network here. Parsing is exercised through the
/// public <see cref="CivitaiBaseModelCatalog.GetBaseModelsAsync"/> surface (the
/// live-fetch tier) rather than the internal parser, so the tests pin real
/// end-to-end behavior.
/// </summary>
/// <remarks>
/// Note: <see cref="CivitaiBaseModelCatalog"/> exposes a <c>Dispose()</c> method
/// but does not implement <see cref="IDisposable"/>, so it cannot be used in a
/// <c>using</c> statement. Instances are registered for teardown instead.
/// </remarks>
public class CivitaiBaseModelCatalogTests : IDisposable
{
    // The class's real bundled snapshot has this many entries as of 2026-05.
    // Pinning the exact count is intentional: a silent edit to the snapshot must
    // trip a test, since the whole point of this class is to degrade *loudly*.
    private const int BundledSnapshotCount = 86;

    // --- New (2026) layout: baseModelRecords: BaseModelRecord[] = [ ... ] ---
    private const string NewFormatTs = """
        import { BaseModelRecord } from './types';

        export const baseModelRecords: BaseModelRecord[] = [
          { name: 'SD 1.5', type: 'sd1', description: 'legacy' },
          { name: 'SDXL 1.0', type: 'sdxl' },
          { name: "Flux.1 D", type: 'flux' },
          { name: 'Pony', type: 'sdxl' },
          { name: 'Illustrious', type: 'sdxl' },
        ];

        export const unrelated = 1;
        """;

    // --- Legacy flat array: baseModels = [ '...', "..." ] as const; ---
    private const string LegacyArrayTs = """
        export const baseModels = ['SD 1.4', 'SD 1.5', "SDXL 1.0", 'Pony'] as const;
        """;

    // --- Legacy Record: baseModelLicenses: Record<BaseModel, ...> = { ... }; ---
    private const string LegacyLicensesTs = """
        export const baseModelLicenses: Record<BaseModel, License | undefined> = {
          'SD 1.4': license1,
          'SD 1.5': license1,
          "SDXL 1.0": license2,
          Pony: license3,
        };
        """;

    // --- Shape drift: records exist but the harvested field was renamed away. ---
    private const string DriftedTs = """
        export const baseModelRecords: BaseModelRecord[] = [
          { label: 'SD 1.5', ecosystem: 'sd1' },
          { label: 'SDXL 1.0', ecosystem: 'sdxl' },
        ];
        """;

    // --- Duplicate record names to exercise order-preserving de-duplication. ---
    private const string DuplicateNamesTs = """
        export const baseModelRecords: BaseModelRecord[] = [
          { name: 'SD 1.5' },
          { name: 'SDXL 1.0' },
          { name: 'SD 1.5' },
        ];
        """;

    private readonly string _cacheDir;
    private readonly List<CivitaiBaseModelCatalog> _catalogs = new();
    private readonly List<HttpClient> _httpClients = new();

    public CivitaiBaseModelCatalogTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "CivitaiBaseModelCatalogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        foreach (var catalog in _catalogs)
        {
            try { catalog.Dispose(); } catch { /* ignore */ }
        }
        foreach (var http in _httpClients)
        {
            try { http.Dispose(); } catch { /* ignore */ }
        }
        try
        {
            if (Directory.Exists(_cacheDir))
                Directory.Delete(_cacheDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private string CacheFile => Path.Combine(_cacheDir, "civitai-base-models.json");

    private void WriteDiskCache(params string[] models)
    {
        var payload = new { FetchedAtUtc = DateTime.UtcNow, BaseModels = models };
        File.WriteAllText(CacheFile, JsonSerializer.Serialize(payload));
    }

    private (CivitaiBaseModelCatalog catalog, RecordingHandler handler, List<CivitaiBaseModelCatalogEventKind> events) Create(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new RecordingHandler(responder);
        var http = new HttpClient(handler);
        var catalog = new CivitaiBaseModelCatalog(http, _cacheDir);
        _httpClients.Add(http);
        _catalogs.Add(catalog);

        var events = new List<CivitaiBaseModelCatalogEventKind>();
        catalog.StatusChanged += (_, e) => events.Add(e.Kind);
        return (catalog, handler, events);
    }

    private static HttpResponseMessage Ts(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    // ---------------------------------------------------------------------
    // Parsing strategies (through the live-fetch tier)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task LiveFetch_NewFormat_ParsesRecordNames()
    {
        var (catalog, handler, events) = Create(_ => Ts(NewFormatTs));

        var models = await catalog.GetBaseModelsAsync();

        models.Should().Equal("SD 1.5", "SDXL 1.0", "Flux.1 D", "Pony", "Illustrious");
        handler.Requests.Should().ContainSingle();
        events.Should().Equal(
            CivitaiBaseModelCatalogEventKind.FetchStarted,
            CivitaiBaseModelCatalogEventKind.FetchSucceeded);
    }

    [Fact]
    public async Task LiveFetch_LegacyFlatArray_ParsesArrayLiterals()
    {
        var (catalog, _, _) = Create(_ => Ts(LegacyArrayTs));

        var models = await catalog.GetBaseModelsAsync();

        models.Should().Equal("SD 1.4", "SD 1.5", "SDXL 1.0", "Pony");
    }

    [Fact]
    public async Task LiveFetch_LegacyLicensesRecord_ParsesObjectKeys()
    {
        var (catalog, _, _) = Create(_ => Ts(LegacyLicensesTs));

        var models = await catalog.GetBaseModelsAsync();

        models.Should().Equal("SD 1.4", "SD 1.5", "SDXL 1.0", "Pony");
    }

    [Fact]
    public async Task LiveFetch_DuplicateRecordNames_AreDedupedPreservingOrder()
    {
        var (catalog, _, _) = Create(_ => Ts(DuplicateNamesTs));

        var models = await catalog.GetBaseModelsAsync();

        models.Should().Equal("SD 1.5", "SDXL 1.0");
    }

    // ---------------------------------------------------------------------
    // URL fallback within the live-fetch tier
    // ---------------------------------------------------------------------

    [Fact]
    public async Task LiveFetch_FirstUrlFails_FallsBackToSecondUrl()
    {
        var (catalog, handler, events) = Create(req =>
            req.RequestUri!.AbsoluteUri.Contains("basemodel.constants.ts")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Ts(LegacyArrayTs));

        var models = await catalog.GetBaseModelsAsync();

        models.Should().Equal("SD 1.4", "SD 1.5", "SDXL 1.0", "Pony");
        handler.Requests.Should().HaveCount(2);
        events.Should().Contain(CivitaiBaseModelCatalogEventKind.FetchSucceeded);
    }

    // ---------------------------------------------------------------------
    // Bundled-snapshot fallback tier
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ShapeDrift_NoParseableModels_FallsBackToBundledSnapshot()
    {
        // Upstream renamed the harvested field: the block still matches but yields
        // nothing, so the catalog must degrade to the bundled snapshot, not crash.
        var (catalog, handler, events) = Create(_ => Ts(DriftedTs));

        var models = await catalog.GetBaseModelsAsync();

        models.Should().HaveCount(BundledSnapshotCount);
        models.Should().Contain(new[] { "SD 1.5", "SDXL 1.0", "Flux.2 Klein 9B", "Pony V7", "Upscaler" });
        // Both candidate URLs are tried before giving up.
        handler.Requests.Should().HaveCount(2);
        events.Should().Contain(CivitaiBaseModelCatalogEventKind.FetchFailed)
            .And.Contain(CivitaiBaseModelCatalogEventKind.UsedBundledFallback);
    }

    [Fact]
    public async Task AllUrlsReturnServerError_FallsBackToBundledSnapshot()
    {
        var (catalog, handler, events) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var models = await catalog.GetBaseModelsAsync();

        models.Should().HaveCount(BundledSnapshotCount);
        models.Should().OnlyHaveUniqueItems();
        handler.Requests.Should().HaveCount(2);
        events.Should().Contain(CivitaiBaseModelCatalogEventKind.UsedBundledFallback);
    }

    // ---------------------------------------------------------------------
    // Disk-cache tier (TTL)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task FreshDiskCache_IsUsedWithoutAnyHttpCall()
    {
        WriteDiskCache("Cached A", "Cached B");
        // Handler would 500 if touched — proving no fetch happens.
        var (catalog, handler, events) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var models = await catalog.GetBaseModelsAsync();

        models.Should().Equal("Cached A", "Cached B");
        handler.Requests.Should().BeEmpty();
        events.Should().Equal(CivitaiBaseModelCatalogEventKind.UsedDiskCache);
    }

    [Fact]
    public async Task StaleDiskCache_IsBypassed_AndLiveFetchRuns()
    {
        WriteDiskCache("Stale A", "Stale B");
        // TTL is 7 days; make the cache 8 days old.
        File.SetLastWriteTimeUtc(CacheFile, DateTime.UtcNow.AddDays(-8));

        var (catalog, handler, events) = Create(_ => Ts(NewFormatTs));

        var models = await catalog.GetBaseModelsAsync();

        models.Should().Equal("SD 1.5", "SDXL 1.0", "Flux.1 D", "Pony", "Illustrious");
        models.Should().NotContain("Stale A");
        handler.Requests.Should().ContainSingle();
        events.Should().NotContain(CivitaiBaseModelCatalogEventKind.UsedDiskCache);
        events.Should().Contain(CivitaiBaseModelCatalogEventKind.FetchSucceeded);
    }

    [Fact]
    public async Task SuccessfulFetch_WritesDiskCache_ReadableByAFreshCatalog()
    {
        var (catalog, _, _) = Create(_ => Ts(NewFormatTs));
        await catalog.GetBaseModelsAsync();

        File.Exists(CacheFile).Should().BeTrue();

        // A brand-new catalog over the same directory reads the persisted cache
        // and must not perform any HTTP (handler 500s if touched).
        var (catalog2, handler2, events2) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var models = await catalog2.GetBaseModelsAsync();

        models.Should().Equal("SD 1.5", "SDXL 1.0", "Flux.1 D", "Pony", "Illustrious");
        handler2.Requests.Should().BeEmpty();
        events2.Should().Equal(CivitaiBaseModelCatalogEventKind.UsedDiskCache);
    }

    // ---------------------------------------------------------------------
    // Memory-cache tier & forceRefresh
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SecondCall_ServedFromMemory_WithoutRefetching()
    {
        var (catalog, handler, _) = Create(_ => Ts(NewFormatTs));

        var first = await catalog.GetBaseModelsAsync();
        var second = await catalog.GetBaseModelsAsync();

        second.Should().BeSameAs(first);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ForceRefresh_BypassesMemoryCache_AndRefetches()
    {
        var (catalog, handler, _) = Create(_ => Ts(NewFormatTs));

        await catalog.GetBaseModelsAsync();
        await catalog.GetBaseModelsAsync(forceRefresh: true);

        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ForceRefresh_BypassesFreshDiskCache_AndRefetches()
    {
        WriteDiskCache("Cached A", "Cached B");
        var (catalog, handler, events) = Create(_ => Ts(LegacyArrayTs));

        var models = await catalog.GetBaseModelsAsync(forceRefresh: true);

        models.Should().Equal("SD 1.4", "SD 1.5", "SDXL 1.0", "Pony");
        handler.Requests.Should().ContainSingle();
        events.Should().NotContain(CivitaiBaseModelCatalogEventKind.UsedDiskCache);
    }

    // ---------------------------------------------------------------------
    // Properties & cancellation
    // ---------------------------------------------------------------------

    [Fact]
    public void CacheFilePath_ResolvesInsideProvidedCacheDirectory()
    {
        var (catalog, _, _) = Create(_ => new HttpResponseMessage());

        catalog.CacheFilePath.Should().Be(CacheFile);
    }

    [Fact]
    public async Task CacheTimestampUtc_NullBeforeFetch_SetAfterSuccessfulFetch()
    {
        var (catalog, _, _) = Create(_ => Ts(NewFormatTs));

        catalog.CacheTimestampUtc.Should().BeNull();

        await catalog.GetBaseModelsAsync();

        catalog.CacheTimestampUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task GetBaseModelsAsync_WithAlreadyCancelledToken_Throws()
    {
        var (catalog, _, _) = Create(_ => Ts(NewFormatTs));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await catalog.GetBaseModelsAsync(cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }
}
