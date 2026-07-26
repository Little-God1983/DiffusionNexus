using System.Net;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Covers <see cref="OnnxModelManager"/> (issue #443): status thresholds, the download state machine,
/// temp-file → move behaviour, cleanup on failure, and the in-progress guard. Driven entirely through
/// the injected HttpClient seam with a fake handler — no network.
///
/// Two production WARTS are pinned as-is (NOT fixed): the constructor performs I/O
/// (<c>Directory.CreateDirectory</c>) and mutates the injected <c>HttpClient.Timeout</c>. See the two
/// dedicated tests below.
/// </summary>
public class OnnxModelManagerTests : IDisposable
{
    private readonly string _root;

    public OnnxModelManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"onnx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Models(string name) => Path.Combine(_root, name);

    // ── Constructor warts (pinned, deliberately not fixed) ──

    [Fact]
    public void Constructor_CreatesModelsDirectory_AsASideEffect()
    {
        // WART: the ctor does filesystem I/O. Pinned so a refactor that removes the side effect is a
        // conscious, visible change.
        var dir = Models("brand-new");
        Directory.Exists(dir).Should().BeFalse();

        _ = new OnnxModelManager(dir, new HttpClient(new FakeHttpHandler(_ => Ok([]))));

        Directory.Exists(dir).Should().BeTrue();
    }

    [Fact]
    public void Constructor_MutatesInjectedHttpClientTimeout()
    {
        // WART: the ctor reaches into the caller-owned HttpClient and overwrites its Timeout. Pinned.
        var http = new HttpClient(new FakeHttpHandler(_ => Ok([])));
        http.Timeout = TimeSpan.FromSeconds(5);

        _ = new OnnxModelManager(Models("m"), http);

        http.Timeout.Should().Be(TimeSpan.FromMinutes(30));
    }

    // ── Path composition ──

    [Fact]
    public void ModelPaths_AreComposedFromBasePath()
    {
        var basePath = Models("models");
        var mgr = new OnnxModelManager(basePath, new HttpClient(new FakeHttpHandler(_ => Ok([]))));

        mgr.Rmbg14ModelPath.Should().Be(Path.Combine(basePath, "rmbg-1.4.onnx"));
        mgr.UltraSharp4xModelPath.Should().Be(Path.Combine(basePath, "4x-UltraSharp.onnx"));
    }

    // ── Status thresholds ──

    [Fact]
    public void GetRmbg14Status_ReflectsFilePresenceAndSize()
    {
        var basePath = Models("m");
        var mgr = new OnnxModelManager(basePath, new HttpClient(new FakeHttpHandler(_ => Ok([]))));

        mgr.GetRmbg14Status().Should().Be(ModelStatus.NotDownloaded);

        WriteFileOfSize(mgr.Rmbg14ModelPath, 149_000_000); // just under the 150MB floor
        mgr.GetRmbg14Status().Should().Be(ModelStatus.Corrupted);

        WriteFileOfSize(mgr.Rmbg14ModelPath, 150_000_000);
        mgr.GetRmbg14Status().Should().Be(ModelStatus.Ready);
    }

    [Fact]
    public void GetUltraSharp4xStatus_ReflectsFilePresenceAndSize()
    {
        var basePath = Models("m");
        var mgr = new OnnxModelManager(basePath, new HttpClient(new FakeHttpHandler(_ => Ok([]))));

        mgr.GetUltraSharp4xStatus().Should().Be(ModelStatus.NotDownloaded);

        WriteFileOfSize(mgr.UltraSharp4xModelPath, 59_000_000); // just under the 60MB floor
        mgr.GetUltraSharp4xStatus().Should().Be(ModelStatus.Corrupted);

        WriteFileOfSize(mgr.UltraSharp4xModelPath, 60_000_000);
        mgr.GetUltraSharp4xStatus().Should().Be(ModelStatus.Ready);
    }

    // ── Download state machine ──

    [Fact]
    public async Task Download_Success_WritesFinalFile_LeavesNoTempFile_AndReturnsTrue()
    {
        var content = RandomBytes(4096);
        var handler = new FakeHttpHandler(_ => Ok(content));
        var mgr = new OnnxModelManager(Models("m"), new HttpClient(handler));
        var progress = new RecordingProgress<ModelDownloadProgress>();

        var ok = await mgr.DownloadRmbg14ModelAsync(progress);

        ok.Should().BeTrue();
        File.Exists(mgr.Rmbg14ModelPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(mgr.Rmbg14ModelPath)).Should().Equal(content);
        File.Exists(mgr.Rmbg14ModelPath + ".download").Should().BeFalse("the temp file is moved to the final path");
        progress.Items.Should().Contain(p => p.Status == "Download complete");
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Download_OverwritesExistingDestinationFile()
    {
        var mgr = new OnnxModelManager(Models("m"), new HttpClient(new FakeHttpHandler(_ => Ok(RandomBytes(2048)))));
        await File.WriteAllTextAsync(mgr.Rmbg14ModelPath, "stale-and-corrupt"); // small → Corrupted, not Ready

        var ok = await mgr.DownloadRmbg14ModelAsync();

        ok.Should().BeTrue();
        (await File.ReadAllBytesAsync(mgr.Rmbg14ModelPath)).Length.Should().Be(2048);
    }

    [Fact]
    public async Task Download_WhenModelAlreadyReady_ShortCircuits_WithoutHttp()
    {
        var handler = new FakeHttpHandler(_ => Ok(RandomBytes(16)));
        var mgr = new OnnxModelManager(Models("m"), new HttpClient(handler));
        WriteFileOfSize(mgr.Rmbg14ModelPath, 150_000_000); // already Ready

        var progress = new RecordingProgress<ModelDownloadProgress>();
        var ok = await mgr.DownloadRmbg14ModelAsync(progress);

        ok.Should().BeTrue();
        handler.CallCount.Should().Be(0, "a ready model must not trigger a download");
        progress.Items.Should().Contain(p => p.Status == "Model already downloaded");
    }

    [Fact]
    public async Task Download_HttpFailure_ReturnsFalse_AndCleansUpTempFile()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var mgr = new OnnxModelManager(Models("m"), new HttpClient(handler));

        var ok = await mgr.DownloadRmbg14ModelAsync();

        ok.Should().BeFalse();
        File.Exists(mgr.Rmbg14ModelPath).Should().BeFalse();
        File.Exists(mgr.Rmbg14ModelPath + ".download").Should().BeFalse();
    }

    [Fact]
    public async Task Download_WhenCancelled_ThrowsOperationCanceled_AndLeavesNoFiles()
    {
        var handler = new FakeHttpHandler(_ => Ok(RandomBytes(4096)));
        var mgr = new OnnxModelManager(Models("m"), new HttpClient(handler));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await mgr.DownloadRmbg14ModelAsync(cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(mgr.Rmbg14ModelPath).Should().BeFalse();
        File.Exists(mgr.Rmbg14ModelPath + ".download").Should().BeFalse();
    }

    [Fact]
    public async Task Download_WhileSameModelDownloadInProgress_SecondCallReturnsFalse()
    {
        // Gate the handler so the first download is parked mid-flight (guard set), then fire a second.
        var gate = new TaskCompletionSource();
        var handler = new FakeHttpHandler(_ => Ok(RandomBytes(4096)), gate.Task);
        var mgr = new OnnxModelManager(Models("m"), new HttpClient(handler));

        var first = mgr.DownloadRmbg14ModelAsync();          // parked at the gated GetAsync; guard set
        mgr.GetRmbg14Status().Should().Be(ModelStatus.Downloading);

        var second = await mgr.DownloadRmbg14ModelAsync();   // guard set → refused
        second.Should().BeFalse();

        gate.SetResult();
        (await first).Should().BeTrue();
    }

    // ── Delete ──

    [Fact]
    public void Delete_RemovesFile_WhenPresent_AndIsNoOp_WhenAbsent()
    {
        var mgr = new OnnxModelManager(Models("m"), new HttpClient(new FakeHttpHandler(_ => Ok([]))));

        var absent = () => mgr.DeleteRmbg14Model();
        absent.Should().NotThrow();

        WriteFileOfSize(mgr.Rmbg14ModelPath, 1024);
        mgr.DeleteRmbg14Model();
        File.Exists(mgr.Rmbg14ModelPath).Should().BeFalse();
    }

    // ── helpers ──

    private static void WriteFileOfSize(string path, long length)
    {
        using var fs = File.Create(path);
        fs.SetLength(length); // fast sparse allocation; FileInfo.Length reports the logical size
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        new Random(1234).NextBytes(b);
        return b;
    }

    private static HttpResponseMessage Ok(byte[] bytes)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = new();
        public void Report(T value) { lock (Items) Items.Add(value); }
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _make;
        private readonly Task _gate;
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> make, Task? gate = null)
        {
            _make = make;
            _gate = gate ?? Task.CompletedTask;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return _make(request);
        }
    }
}
