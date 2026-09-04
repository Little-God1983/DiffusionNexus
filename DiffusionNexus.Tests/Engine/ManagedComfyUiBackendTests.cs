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
    public void Catalog_AlwaysResolvesTheKrea2ModelEvenBeforeTheEngineIsInstalled()
    {
        // Regression guard for a gap flagged in review: the Canvas looks up
        // SelectedModel.Key against backend.Catalog.TryGet(...) before ever calling
        // GenerateAsync. ComfyUiModelCatalog's disk scan has no notion of Krea 2 (it only
        // recognizes the sd.cpp-loadable models), so without the Krea2Model/EngineModelCatalog
        // union, this lookup would return null on every engine install and generation could
        // never resolve its own model.
        var backend = Create(installRoot: null, hasTemplate: true);

        var descriptor = backend.Catalog.TryGet("krea2");

        descriptor.Should().NotBeNull("the engine backend always knows Krea 2, independent of install state");
        descriptor!.DisplayName.Should().Be("Krea 2 Turbo");
        backend.Catalog.ListAvailable().Should().Contain(d => d.Key == "krea2");
    }

    [Fact]
    public async Task Generate_RefusesAModelItCannotActuallyRun()
    {
        // This backend submits ONE graph regardless of what is asked of it, while its catalog unions Krea 2
        // with a real disk scan of the engine's models root. Before the guard, selecting a discovered model
        // ran the Krea 2 GGUF and reported the result under the other model's name: a wrong image, labelled
        // convincingly.
        var backend = Create(null, hasTemplate: true);
        var request = new DiffusionRequest
        {
            ModelKey = "flux2-klein", Prompt = "a cat", Width = 1024, Height = 1024
        };

        var items = new List<DiffusionStreamItem>();
        await foreach (var item in backend.GenerateAsync(request))
            items.Add(item);

        var last = items.Should().ContainSingle().Subject;
        last.Progress.Phase.Should().Be(DiffusionPhase.Completed);
        last.Result.Should().BeNull();
        last.Progress.Message.Should().Contain("Krea 2 Turbo");
        last.Progress.Message.Should().Contain("flux2-klein");
    }

    [Fact]
    public async Task Generate_RefusesTheForeignModelBeforeProbingReadiness()
    {
        // The guard is cheap and must come first: probing spawns python and polls for up to two minutes,
        // and none of that work can change the answer for a model this backend cannot run.
        var backend = Create(null, hasTemplate: false);
        var request = new DiffusionRequest
        {
            ModelKey = "z-image-turbo", Prompt = "a cat", Width = 1024, Height = 1024
        };

        var items = new List<DiffusionStreamItem>();
        await foreach (var item in backend.GenerateAsync(request))
            items.Add(item);

        // Not installed AND not configured, yet the message is about the model rather than either of those.
        items.Should().ContainSingle().Which.Progress.Message.Should().Contain("z-image-turbo");
    }

    [Fact]
    public void Capabilities_DeclareWhatTheWorkflowActuallyHonours()
    {
        var backend = Create(null, hasTemplate: true);

        backend.Capabilities.Supports(BackendFeature.NegativePrompt).Should()
            .BeTrue("the patcher writes node 35");
        backend.Capabilities.Supports(BackendFeature.SamplerSelection).Should()
            .BeFalse("sampler and scheduler are baked into the template's KSampler");
        backend.Capabilities.Supports(BackendFeature.Loras).Should()
            .BeFalse("the graph's LoRA loader node is never patched");
        backend.Capabilities.Supports(BackendFeature.MidSampleInterrupt).Should()
            .BeTrue("cancellation POSTs /interrupt");
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
