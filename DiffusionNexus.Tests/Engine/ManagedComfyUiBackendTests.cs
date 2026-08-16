using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.UI.Services.Diffusion;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class ManagedComfyUiBackendTests
{
    private static ManagedComfyUiBackend Create(string? installRoot, bool hasTemplate)
        => new(new ManagedComfyUiEngine(unifiedLogger: null),
               () => Task.FromResult(installRoot),
               new StubTemplateSource(hasTemplate));

    private sealed class StubTemplateSource(bool hasTemplate) : IWorkflowTemplateSource
    {
        public bool HasTemplate => hasTemplate;
        public string? LoadTemplateJson() => hasTemplate ? "{}" : null;
    }

    [Fact]
    public void DisplayName_IdentifiesTheEngine()
    {
        Create(null, hasTemplate: false).DisplayName.Should().Be("Diffusion Nexus Engine");
    }

    [Fact]
    public async Task IsAvailable_IsFalseAndSaysSoWhenTheEngineIsNotInstalled()
    {
        var backend = Create(null, hasTemplate: true);

        var available = await backend.IsAvailableAsync();

        available.Should().BeFalse();
        backend.MissingRequirements.Should().ContainSingle()
            .Which.Should().Contain("not installed");
    }

    [Fact]
    public async Task IsAvailable_IsFalseAndSaysSoWhenNoWorkflowIsConfigured()
    {
        var root = Path.Combine(Path.GetTempPath(), "dn-engine-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.py"), "# comfy");
        try
        {
            var backend = Create(root, hasTemplate: false);

            var available = await backend.IsAvailableAsync();

            available.Should().BeFalse();
            backend.MissingRequirements.Should().Contain(r => r.Contains("workflow"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Generate_WithoutWorkflow_YieldsACompletedItemCarryingTheReason()
    {
        var backend = Create(null, hasTemplate: false);
        var request = new DiffusionRequest
        {
            ModelKey = "krea2", Prompt = "a cat", Width = 1024, Height = 1024
        };

        var items = new List<DiffusionStreamItem>();
        await foreach (var item in backend.GenerateAsync(request))
            items.Add(item);

        items.Should().NotBeEmpty();
        var last = items[^1];
        last.Progress.Phase.Should().Be(DiffusionPhase.Completed);
        last.Result.Should().BeNull("failures are data, not exceptions, on this seam");
        last.Progress.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Generate_PropagatesCallerCancellation()
    {
        var backend = Create(null, hasTemplate: false);
        var request = new DiffusionRequest
        {
            ModelKey = "krea2", Prompt = "a cat", Width = 1024, Height = 1024
        };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () =>
        {
            await foreach (var _ in backend.GenerateAsync(request, cts.Token)) { }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
