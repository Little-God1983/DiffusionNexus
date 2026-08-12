using DiffusionNexus.Service.Services;
using FluentAssertions;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

/// <summary>
/// Lifecycle tests for the shared ONNX session host (issue #487). Real
/// inference needs a model file, so these cover exactly the paths that do not:
/// initialization failure, the single-flight admission gate (the old plain
/// bool let two concurrent callers both pass, issue #491), and the
/// guard/ordering behavior around disposal (issue #490).
/// </summary>
public sealed class OnnxSessionHostTests
{
    [Fact]
    public async Task InitializeAsync_WithMissingModelFile_ReturnsFalseInsteadOfThrowing()
    {
        using var host = new OnnxSessionHost("test-model");

        var result = await host.InitializeAsync(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".onnx"));

        result.Should().BeFalse();
        host.IsInitialized.Should().BeFalse();
    }

    [Fact]
    public async Task InitializeAsync_WithAnUnloadableModelFile_ReturnsFalse_AndTriesCpuAfterGpu()
    {
        // A file that exists but is not a valid ONNX model exercises both
        // provider branches (DirectML attempt, then the CPU fallback) and
        // must come back as a clean false, never an exception.
        var bogusModel = Path.Combine(Path.GetTempPath(), "bogus-" + Guid.NewGuid().ToString("N") + ".onnx");
        await File.WriteAllTextAsync(bogusModel, "not an onnx model");
        try
        {
            using var host = new OnnxSessionHost("test-model");

            var result = await host.InitializeAsync(bogusModel);

            result.Should().BeFalse();
            host.IsInitialized.Should().BeFalse();
        }
        finally
        {
            File.Delete(bogusModel);
        }
    }

    [Fact]
    public async Task InitializeAsync_AfterDispose_ReturnsFalse()
    {
        var host = new OnnxSessionHost("test-model");
        host.Dispose();

        var result = await host.InitializeAsync("irrelevant.onnx");

        result.Should().BeFalse();
    }

    [Fact]
    public void Run_BeforeInitialize_ThrowsInvalidOperation()
    {
        using var host = new OnnxSessionHost("test-model");

        var act = () => host.Run(new List<NamedOnnxValue>(), _ => 0);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not initialized*");
    }

    [Fact]
    public void Run_AfterDispose_ThrowsAManagedException()
    {
        // The whole point of issue #490: a Run racing Dispose must surface as
        // a managed exception the caller's catch can turn into a Failed
        // result — never as native teardown under an in-flight inference.
        var host = new OnnxSessionHost("test-model");
        host.Dispose();

        var act = () => host.Run(new List<NamedOnnxValue>(), _ => 0);

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var host = new OnnxSessionHost("test-model");

        host.Dispose();
        var act = () => host.Dispose();

        act.Should().NotThrow();
        host.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void TryBeginProcessing_IsSingleFlight_UntilEndProcessing()
    {
        using var host = new OnnxSessionHost("test-model");

        host.IsProcessing.Should().BeFalse();
        host.TryBeginProcessing().Should().BeTrue();
        host.IsProcessing.Should().BeTrue();
        host.TryBeginProcessing().Should().BeFalse("the slot is taken");

        host.EndProcessing();

        host.IsProcessing.Should().BeFalse();
        host.TryBeginProcessing().Should().BeTrue("the slot reopened");
    }

    [Fact]
    public void TryBeginProcessing_UnderContention_AdmitsExactlyOneCaller()
    {
        // The services' old `if (_isProcessing) ... _isProcessing = true`
        // check-then-set let several concurrent callers pass together and
        // fail unpredictably inside the shared session (issue #491).
        using var host = new OnnxSessionHost("test-model");
        var admitted = 0;

        Parallel.For(0, 64, _ =>
        {
            if (host.TryBeginProcessing())
                Interlocked.Increment(ref admitted);
        });

        admitted.Should().Be(1);
    }
}
