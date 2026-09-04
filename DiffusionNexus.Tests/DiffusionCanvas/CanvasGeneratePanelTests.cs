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
        //
        // Every value here is deliberately different from the view model's own field initialisers
        // (9 / 1.0f / "euler" / "simple"). An earlier version of this test used a descriptor that fell back
        // to those same values, so it compared "euler" to "euler" and stayed green with the adoption
        // deleted.
        var backend = new FakeDiffusionBackend
        {
            DefaultSteps = 27,
            DefaultCfg = 6.5f,
            DefaultSampler = "dpmpp2m",
            DefaultScheduler = "karras",
        };

        var vm = Canvas(backend);

        vm.Steps.Should().Be(27);
        vm.Cfg.Should().Be(6.5f);
        vm.SelectedSampler.Should().Be("dpmpp2m");
        vm.SelectedScheduler.Should().Be("karras");
    }

    [Fact]
    public async Task TheAdoptedDefaultsAreWhatActuallyRuns()
    {
        // The adoption is only worth anything if the adopted values reach the backend: the request used to
        // send null here and let the backend apply the model's defaults itself.
        var backend = new FakeDiffusionBackend
        {
            DefaultSteps = 27,
            DefaultCfg = 6.5f,
            DefaultSampler = "dpmpp2m",
            DefaultScheduler = "karras",
        };
        var vm = Canvas(backend);

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.LastRequest!.Steps.Should().Be(27);
        backend.LastRequest.Cfg.Should().Be(6.5f);
        backend.LastRequest.Sampler.Should().Be("dpmpp2m");
        backend.LastRequest.Scheduler.Should().Be("karras");
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
    public async Task ALoraTheModelAlreadyAppliesIsNotAppliedTwice()
    {
        // Qwen-Image-2512 carries a mandatory 4-step Lightning LoRA on its descriptor, and the backend
        // stacks descriptor LoRAs before request ones. A user who picks that same file by hand would
        // otherwise get it at double strength, and every image comes out over-baked.
        var backend = new FakeDiffusionBackend
        {
            DefaultLoras = [new LoraReference(@"C:\loras\lightning.safetensors", 1.0f)],
        };
        var vm = Canvas(backend);
        vm.Loras.Add(new LoraPickerItemViewModel
        {
            // Deliberately a different case: the dedup has to match the way a file system would.
            FilePath = @"C:\LORAS\LIGHTNING.SAFETENSORS", Strength = 1.0, IsEnabled = true,
        });
        vm.Loras.Add(new LoraPickerItemViewModel
        {
            FilePath = @"C:\loras\style.safetensors", Strength = 0.6, IsEnabled = true,
        });

        await vm.GenerateCommand.ExecuteAsync(null);

        var loras = backend.LastRequest!.Loras;
        loras.Should().ContainSingle("the model already applies the Lightning LoRA itself");
        loras[0].FilePath.Should().Be(@"C:\loras\style.safetensors");
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
    public void ABackendThatCannotLoadLorasKeepsTheRowsTheUserAlreadyPicked()
    {
        // The rows' dropdowns are bound to AvailableLoras, and clearing that collection drives each row's
        // SelectedItem to null, taking its file path with it. Emptying the list when the picker is merely
        // disabled therefore threw the user's picks away on a backend switch, with no message.
        var backend = new FakeDiffusionBackend
        {
            Capabilities = Without(BackendFeature.Loras, "this fake cannot load LoRAs"),
        };
        var vm = Canvas(backend);
        vm.Loras.Add(new LoraPickerItemViewModel
        {
            FilePath = @"C:\loras\a.safetensors", DisplayName = "a", Strength = 0.8, IsEnabled = true,
        });

        // Re-select the same backend option to force the reload path the switch takes.
        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Key == CanvasBackendKeys.Local);
        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Key == CanvasBackendKeys.Engine);

        vm.Loras.Should().ContainSingle();
        vm.Loras[0].FilePath.Should().Be(@"C:\loras\a.safetensors", "the pick survives a backend that cannot use it");
        vm.LoraUnavailableMessage.Should().Be("this fake cannot load LoRAs");
    }

    [Fact]
    public async Task LorasChosenOnAnotherBackendAreStillSentAndTheUserIsNotLiedTo()
    {
        // The rows survive, so the request still carries them and the backend ignores them. What must not
        // happen is the picks vanishing silently: the panel says why, and the console warns at Generate.
        var backend = new FakeDiffusionBackend
        {
            Capabilities = Without(BackendFeature.Loras, "this fake cannot load LoRAs"),
        };
        var vm = Canvas(backend);
        vm.Loras.Add(new LoraPickerItemViewModel
        {
            FilePath = @"C:\loras\a.safetensors", Strength = 0.8, IsEnabled = true,
        });

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.IsLoraSupported.Should().BeFalse();
        vm.LoraLimitation.Should().Be("this fake cannot load LoRAs");
        backend.LastRequest!.Loras.Should().ContainSingle(
            "the row is still configured; the backend is what ignores it, and the panel says so");
    }

    // ────────────────────────────── Disabled controls still explain themselves ──────────────────────────────

    [Fact]
    public void TheDisabledRegionDButtonsAlwaysExplainThemselves()
    {
        // These tooltips were briefly bound to the SAMPLER limitation, which is null on a backend that
        // supports samplers. A disabled control with an empty tooltip is the exact failure the capability
        // surface exists to prevent, and a borrowed sentence is worse still.
        var vm = Canvas(new FakeDiffusionBackend());

        vm.ControlNetTooltip.Should().NotBeNullOrWhiteSpace().And.Contain("region D");
        vm.MaskTooltip.Should().NotBeNullOrWhiteSpace().And.Contain("region D");
        vm.ControlNetTooltip.Should().NotBe(vm.MaskTooltip);
    }

    [Fact]
    public void ADisabledButtonAlsoCarriesTheBackendsOwnReasonWhenThereIsOne()
    {
        var vm = Canvas(new FakeDiffusionBackend
        {
            Capabilities = Without(BackendFeature.ControlNet, "this fake has no control path"),
        });

        vm.ControlNetTooltip.Should().Contain("region D", "the feature is not built either way");
        vm.ControlNetTooltip.Should().Contain("this fake has no control path");
        vm.ControlNetTooltip.Should().NotContain(vm.SamplerSelectionLimitation ?? "\u0000never\u0000");
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
        var image = ModelBaseModelLabels.ForModelKey("qwen-image-2512");
        var edit = ModelBaseModelLabels.ForModelKey("qwen-image-edit-2511");

        // Asserted non-empty first, on purpose. Comparing the two directly is satisfied by both being null,
        // so deleting both entries from the map used to leave this green while the two models with the
        // largest LoRA ecosystems silently lost their filter.
        image.Should().NotBeNullOrEmpty().And.Contain("Qwen");
        edit.Should().BeEquivalentTo(image);
    }

    [Fact]
    public void EveryModelTheCanvasCanSelectHasAnEntry()
    {
        // A key renamed or an entry lost in a merge sends LoadLorasForSelectedModelAsync down the "unknown
        // model" branch, which shows an empty picker. That should be a failing test, not a silent gap.
        string[] keys = ["flux2-klein", "z-image-turbo", "qwen-image-2512", "qwen-image-edit-2511", "krea2"];

        foreach (var key in keys)
            ModelBaseModelLabels.IsKnown(key).Should().BeTrue($"'{key}' is selectable in the canvas");

        ModelBaseModelLabels.ForModelKey("z-image-turbo").Should().NotBeNullOrEmpty().And.Contain("ZImageTurbo");
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
