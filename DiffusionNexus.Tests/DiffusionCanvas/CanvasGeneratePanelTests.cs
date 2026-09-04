using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.UI.Services.Lora;
using DiffusionNexus.UI.ViewModels.Controls;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// The generate panel (issue #518 region B): the values it shows have to be the values that run, and a
/// control the selected backend cannot honour has to say so rather than quietly do nothing.
/// </summary>
public class CanvasGeneratePanelTests
{
    private static DiffusionCanvasViewModel Canvas(FakeDiffusionBackend backend)
    {
        var vm = new DiffusionCanvasViewModel(backend) { PromptText = "a lighthouse at dusk" };
        vm.SelectedModel.Should().NotBeNull("the engine catalog populates the dropdown on selection");

        vm.BitmapDecoder = _ =>
        {
            var sentinel = (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));
            GC.SuppressFinalize(sentinel);
            return sentinel;
        };
        vm.OutputsWriter = (bytes, seed) => $"C:\\fake-outputs\\{seed}-{bytes.Length}.png";
        return vm;
    }

    // ────────────────────────────── The panel's values reach the backend ──────────────────────────────

    [Fact]
    public async Task EveryPanelValueReachesTheRequest()
    {
        // Before region B these were all hard-nulled at the single request construction site, with the
        // backend applying its own defaults. The panel now shows values, so it has to send them.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.NegativePromptText = "plastic skin, watermark";
        vm.Steps = 17;
        vm.Cfg = 3.5f;
        vm.SelectedSampler = "dpmpp2m";
        vm.SelectedScheduler = "karras";

        await vm.GenerateCommand.ExecuteAsync(null);

        var request = backend.LastRequest!;
        request.NegativePrompt.Should().Be("plastic skin, watermark");
        request.Steps.Should().Be(17);
        request.Cfg.Should().Be(3.5f);
        request.Sampler.Should().Be("dpmpp2m");
        request.Scheduler.Should().Be("karras");
    }

    [Fact]
    public async Task AnEmptyNegativePromptIsSentAsNothingRatherThanAnEmptyString()
    {
        // "The user typed nothing" must mean "unchanged", not "an empty negative" — the two are not
        // guaranteed to be the same conditioning.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.NegativePromptText = "   ";

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.LastRequest!.NegativePrompt.Should().BeNull();
    }

    [Fact]
    public async Task ThePanelIsFrozenForTheWholeBatch()
    {
        // A batch is one batch. Editing mid-run must not land on image three of four.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 3;
        vm.Steps = 10;
        backend.BeforeRun = run =>
        {
            if (run == 1)
            {
                vm.Steps = 99;
                vm.NegativePromptText = "typed while it was running";
            }
        };

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.Requests.Should().HaveCount(3);
        backend.Requests.Should().OnlyContain(r => r.Steps == 10, "the batch ran on the values it started with");
        backend.Requests.Should().OnlyContain(r => r.NegativePrompt == null);
    }

    // ────────────────────────────── Per-model defaults ──────────────────────────────

    [Fact]
    public void SelectingAModelAdoptsItsOwnSamplingDefaults()
    {
        // The panel sends what it shows, so leaving the previous model's numbers in place would silently
        // override the new model's defaults. FLUX.2-klein wants 20 steps where Qwen wants 4.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);

        var descriptor = backend.Catalog.TryGet(FakeDiffusionBackend.ModelKey)!;
        vm.Steps.Should().Be(descriptor.DefaultSteps);
        vm.Cfg.Should().Be(descriptor.DefaultCfg);
        vm.SelectedSampler.Should().Be(descriptor.DefaultSampler);
        vm.SelectedScheduler.Should().Be(descriptor.DefaultScheduler);
    }

    [Fact]
    public void TheModelOptionCarriesItsDescriptorSoNoCatalogRequeryIsNeeded()
    {
        // Re-querying the local catalog is a recursive multi-root disk walk; doing it on every selection
        // change would freeze the UI thread.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);

        vm.SelectedModel!.Descriptor.Should().NotBeNull();
        vm.SelectedModel.Descriptor!.Key.Should().Be(FakeDiffusionBackend.ModelKey);
    }

    // ────────────────────────────── Sampling summary ──────────────────────────────

    [Fact]
    public void TheSamplingHeaderSummarisesItself()
    {
        var vm = Canvas(new FakeDiffusionBackend());
        vm.SelectedSampler = "euler";
        vm.Steps = 9;
        vm.Cfg = 1.0f;

        vm.SamplingSummary.Should().Be("euler · 9 · cfg 1.0");
    }

    [Fact]
    public void TheSamplingSummaryIsRaisedWhenAnyOfItsPartsChanges()
    {
        // The section is collapsed by default, so the header is the only place these values are readable.
        var vm = Canvas(new FakeDiffusionBackend());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Steps = 12;
        vm.Cfg = 2.5f;
        vm.SelectedSampler = "heun";

        raised.Should().Contain(nameof(DiffusionCanvasViewModel.SamplingSummary));
        raised.Count(n => n == nameof(DiffusionCanvasViewModel.SamplingSummary)).Should().Be(3);
    }

    [Fact]
    public void NumericLabelsUseAnInvariantDecimalPoint()
    {
        // This suite runs on a German-locale machine, where a XAML StringFormat renders 1.0 as "1,0" —
        // beside a header reading "cfg 1.0", and a decimal comma in a numeric readout also reads as a
        // thousands separator. The canvas readout and the Image Editor already format these in the VM.
        var vm = Canvas(new FakeDiffusionBackend());
        vm.Cfg = 1.0f;
        vm.DenoiseStrength = 0.65;

        vm.GuidanceText.Should().Be("Guidance: 1.0");
        vm.DenoiseText.Should().Be("Denoise: 0.65");
        vm.SamplingSummary.Should().Contain("cfg 1.0");
    }

    [Fact]
    public void TheGuidanceAndDenoiseLabelsAreRaisedWhenTheirValuesChange()
    {
        var vm = Canvas(new FakeDiffusionBackend());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Cfg = 4.5f;
        vm.DenoiseStrength = 0.3;

        raised.Should().Contain(nameof(DiffusionCanvasViewModel.GuidanceText));
        raised.Should().Contain(nameof(DiffusionCanvasViewModel.DenoiseText));
    }

    [Fact]
    public void OnlySamplersTheBackendActuallyMapsAreOffered()
    {
        // The local backend's mapper falls through to euler for anything it does not know, silently and
        // without logging, so a dropdown built from a wider list would let a user pick a dead sampler.
        var vm = Canvas(new FakeDiffusionBackend());

        vm.AvailableSamplers.Should().Contain("euler").And.Contain("dpmpp2m");
        vm.AvailableSamplers.Should().NotContain("this-sampler-does-not-exist");
        vm.AvailableSchedulers.Should().Contain("simple").And.Contain("karras");
    }

    // ────────────────────────────── Seed ──────────────────────────────

    [Fact]
    public void TheSeedReadsAsRandomUntilItIsLocked()
    {
        // "Random" is a state, not a value; showing a stale number beside a random toggle invites the
        // reader to believe it.
        var vm = Canvas(new FakeDiffusionBackend());

        vm.UseRandomSeed.Should().BeTrue();
        vm.SeedText.Should().Be("Random");

        vm.RandomizeSeedCommand.Execute(null);

        vm.UseRandomSeed.Should().BeFalse();
        vm.Seed.Should().NotBeNull();
        vm.SeedText.Should().Be(vm.Seed!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ALockedSeedWithNoValueSaysSoRatherThanShowingNothing()
    {
        var vm = Canvas(new FakeDiffusionBackend());

        vm.UseRandomSeed = false;

        vm.SeedText.Should().Be("Not set");
    }

    [Fact]
    public async Task TheSeedTheLastImageUsedCanBeLockedAndReused()
    {
        // The whole point of a seed control: "I liked that one, now vary the prompt".
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);

        vm.ReuseLastSeedCommand.CanExecute(null).Should().BeFalse("nothing has generated yet");

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.LastUsedSeed.Should().NotBeNull();
        vm.ReuseLastSeedCommand.CanExecute(null).Should().BeTrue();

        vm.ReuseLastSeedCommand.Execute(null);

        vm.UseRandomSeed.Should().BeFalse();
        vm.Seed.Should().Be(vm.LastUsedSeed);
    }

    [Fact]
    public async Task ARandomSeedIsStillSentAsNullSoTheBackendRollsItAndReportsBack()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.UseRandomSeed = true;

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.LastRequest!.Seed.Should().BeNull("null means the backend picks one and echoes it back");
    }

    // ────────────────────────────── Capability gating ──────────────────────────────

    private static BackendCapabilities Without(BackendFeature feature, string reason) =>
        new(new Dictionary<BackendFeature, string> { [feature] = reason });

    [Fact]
    public void AnUnsupportedFeatureIsReportedWithTheBackendsOwnReason()
    {
        var backend = new FakeDiffusionBackend
        {
            Capabilities = Without(BackendFeature.SamplerSelection, "this fake bakes its sampler in"),
        };
        var vm = Canvas(backend);

        vm.IsSamplerSelectionSupported.Should().BeFalse();
        vm.SamplerSelectionLimitation.Should().Be("this fake bakes its sampler in");

        // Everything it did not object to stays available.
        vm.IsNegativePromptSupported.Should().BeTrue();
        vm.NegativePromptLimitation.Should().BeNull();
        vm.IsLoraSupported.Should().BeTrue();
    }

    [Fact]
    public void SwitchingBackendReGatesThePanel()
    {
        // The controls are bound to these projections, so they have to be raised when the backend changes
        // or the panel keeps offering what the new backend cannot do.
        var backend = new FakeDiffusionBackend
        {
            Capabilities = Without(BackendFeature.Loras, "this fake cannot load LoRAs"),
        };
        var vm = Canvas(backend);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Key == CanvasBackendKeys.Local);

        raised.Should().Contain(nameof(DiffusionCanvasViewModel.IsLoraSupported));
        raised.Should().Contain(nameof(DiffusionCanvasViewModel.LoraLimitation));
        vm.IsLoraSupported.Should().BeTrue("the local backend does load LoRAs");
    }

    [Fact]
    public void TheCancelTooltipTellsTheTruthPerBackend()
    {
        // The local backend cannot interrupt mid-sample and the engine can; a single tooltip would be
        // wrong on one of them.
        var interruptible = Canvas(new FakeDiffusionBackend());
        interruptible.CancelTooltip.Should().Contain("interrupts");

        var notInterruptible = Canvas(new FakeDiffusionBackend
        {
            Capabilities = Without(BackendFeature.MidSampleInterrupt, "the image being sampled finishes first"),
        });
        notInterruptible.CancelTooltip.Should().Be("the image being sampled finishes first");
    }

    // ────────────────────────────── LoRA rows ──────────────────────────────

    [Fact]
    public async Task EnabledLoraRowsReachTheRequestAndDisabledOnesDoNot()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Loras.Add(new LoraPickerItemViewModel { FilePath = "C:\\loras\\a.safetensors", Strength = 0.8, IsEnabled = true });
        vm.Loras.Add(new LoraPickerItemViewModel { FilePath = "C:\\loras\\b.safetensors", Strength = 0.5, IsEnabled = false });

        await vm.GenerateCommand.ExecuteAsync(null);

        var loras = backend.LastRequest!.Loras;
        loras.Should().ContainSingle();
        loras[0].FilePath.Should().Be("C:\\loras\\a.safetensors");
        loras[0].Strength.Should().BeApproximately(0.8f, 0.0001f);
    }

    [Fact]
    public async Task ARowWithNoResolvedFileIsSkipped()
    {
        // A picker row exists before anything is chosen in it.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Loras.Add(new LoraPickerItemViewModel { FilePath = null, IsEnabled = true });

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.LastRequest!.Loras.Should().BeEmpty();
    }

    [Fact]
    public async Task TheSameLoraPickedTwiceIsAppliedOnce()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Loras.Add(new LoraPickerItemViewModel { FilePath = "C:\\loras\\a.safetensors", Strength = 0.8, IsEnabled = true });
        vm.Loras.Add(new LoraPickerItemViewModel { FilePath = "C:\\LORAS\\A.SAFETENSORS", Strength = 0.4, IsEnabled = true });

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.LastRequest!.Loras.Should().ContainSingle("applying it twice would double its strength");
    }

    [Fact]
    public void ThePromptLengthIsCountedInCharactersAndNeverCapped()
    {
        // Deliberately not tokens: a real count needs the model's tokenizer, and the familiar 77 limit is
        // CLIP's, which does not apply to the T5-based models this canvas runs.
        var vm = Canvas(new FakeDiffusionBackend());
        vm.PromptText = new string('x', 900);

        vm.PromptLengthText.Should().Be("900 characters");
        vm.PromptLengthText.Should().NotContain("/");
    }

    [Fact]
    public void ThePromptLengthIsRaisedAsThePromptChanges()
    {
        var vm = Canvas(new FakeDiffusionBackend());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.PromptText = "a new idea";

        raised.Should().Contain(nameof(DiffusionCanvasViewModel.PromptLengthText));
    }

    [Fact]
    public void WithNoLoraCatalogThePickerIsSimplyEmptyRatherThanExplained()
    {
        // Design time and the test seam have no catalog; that is not a user-facing problem to narrate.
        var vm = Canvas(new FakeDiffusionBackend());

        vm.AvailableLoras.Should().BeEmpty();
        vm.LoraUnavailableMessage.Should().BeNull();
    }
}

/// <summary>
/// The authored model-to-base-model map behind the LoRA filter.
/// </summary>
public class ModelBaseModelLabelsTests
{
    [Fact]
    public void AKnownModelReturnsTheLabelsItsLorasArePublishedUnder()
    {
        var labels = ModelBaseModelLabels.ForModelKey("flux2-klein");

        labels.Should().NotBeNull();
        labels.Should().Contain("Flux.2 Klein 9B");
        labels.Should().Contain("Flux.2 Klein 9B-base",
            "the catalog matches whole strings exactly, so every published spelling has to be listed");
    }

    [Fact]
    public void KreaIsKnownAndDeliberatelyEmpty()
    {
        // Civitai has no Krea 2 base-model label at all. An empty list means "known, and nothing matches";
        // it must never be turned into a null filter, which the catalog reads as "return everything".
        ModelBaseModelLabels.IsKnown("krea2").Should().BeTrue();
        ModelBaseModelLabels.ForModelKey("krea2").Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void AnUnknownModelIsNullSoTheCallerCanTellItApartFromNoMatches()
    {
        ModelBaseModelLabels.ForModelKey("something-nobody-mapped").Should().BeNull();
        ModelBaseModelLabels.IsKnown("something-nobody-mapped").Should().BeFalse();
        ModelBaseModelLabels.ForModelKey(null).Should().BeNull();
        ModelBaseModelLabels.ForModelKey("  ").Should().BeNull();
    }

    [Fact]
    public void TheTwoQwenModelsShareCivitaisSingleQwenFamilyLabel()
    {
        // Over-broad rather than wrong: Civitai does not separate Qwen-Image from Qwen-Image-Edit.
        ModelBaseModelLabels.ForModelKey("qwen-image-2512").Should()
            .BeEquivalentTo(ModelBaseModelLabels.ForModelKey("qwen-image-edit-2511"));
    }
}

/// <summary>The capability surface itself.</summary>
public class BackendCapabilitiesTests
{
    [Fact]
    public void AnythingNotListedAsALimitationIsSupported()
    {
        // Stated negatively on purpose: a newly added feature stays supported by default rather than
        // silently disabling itself in every backend that has not been updated.
        var caps = new BackendCapabilities(new Dictionary<BackendFeature, string>
        {
            [BackendFeature.ControlNet] = "no control path",
        });

        caps.Supports(BackendFeature.ControlNet).Should().BeFalse();
        caps.LimitationFor(BackendFeature.ControlNet).Should().Be("no control path");

        caps.Supports(BackendFeature.NegativePrompt).Should().BeTrue();
        caps.LimitationFor(BackendFeature.NegativePrompt).Should().BeNull();
    }

    [Fact]
    public void AllSupportsEverything()
    {
        foreach (var feature in Enum.GetValues<BackendFeature>())
        {
            BackendCapabilities.All.Supports(feature).Should().BeTrue();
            BackendCapabilities.All.LimitationFor(feature).Should().BeNull();
        }
    }

    [Fact]
    public void EveryLimitationTheShippedBackendsDeclareIsAUsableSentence()
    {
        // These strings go in front of the user at the disabled control, so an empty or placeholder one is
        // the failure this whole mechanism exists to prevent.
        var sets = new[]
        {
            DiffusionNexus.Inference.StableDiffusionCpp.StableDiffusionCppBackend.LocalCapabilities,
            DiffusionNexus.UI.Services.Diffusion.ManagedComfyUiBackend.EngineCapabilities,
        };

        foreach (var caps in sets)
        {
            foreach (var feature in Enum.GetValues<BackendFeature>())
            {
                if (caps.Supports(feature))
                    continue;

                var reason = caps.LimitationFor(feature)!;
                reason.Should().NotBeNullOrWhiteSpace();
                reason.Should().NotContain("TODO");
                reason.Length.Should().BeGreaterThan(20, "a one-line reason, not a label");
            }
        }
    }

    [Fact]
    public void TheTwoShippedBackendsDisagreeExactlyWhereTheCodeSaysTheyDo()
    {
        var local = DiffusionNexus.Inference.StableDiffusionCpp.StableDiffusionCppBackend.LocalCapabilities;
        var engine = DiffusionNexus.UI.Services.Diffusion.ManagedComfyUiBackend.EngineCapabilities;

        // The local backend maps the sampler and loads LoRAs; the engine bakes both into its workflow.
        local.Supports(BackendFeature.SamplerSelection).Should().BeTrue();
        engine.Supports(BackendFeature.SamplerSelection).Should().BeFalse();
        local.Supports(BackendFeature.Loras).Should().BeTrue();
        engine.Supports(BackendFeature.Loras).Should().BeFalse();

        // Both honour the negative prompt now that the local one is wired.
        local.Supports(BackendFeature.NegativePrompt).Should().BeTrue();
        engine.Supports(BackendFeature.NegativePrompt).Should().BeTrue();

        // Only the engine can stop the sampler mid-image.
        local.Supports(BackendFeature.MidSampleInterrupt).Should().BeFalse();
        engine.Supports(BackendFeature.MidSampleInterrupt).Should().BeTrue();
    }
}
