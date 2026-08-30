using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services;
using DiffusionNexus.Service.Services.Lora;
using DiffusionNexus.UI.Services.Lora.Sorting;
using DiffusionNexus.Tests.Sync.Service.Identity;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Sorter;

public sealed class LoraSorterViewModelTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-sortervm-");
    private readonly Mock<IAppSettingsService> _settings = new();
    private readonly Mock<IModelSyncService> _sync = new();

    public void Dispose()
    {
        // UnauthorizedAccessException also shows up here: recursive delete of a tree containing a
        // directory junction hits the reparse point itself.
        try { _root.Delete(recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string SourceRoot => Path.Combine(_root.FullName, "Loras");

    private string WriteLora(string relative)
    {
        var path = Path.Combine(SourceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "weights");
        return path;
    }

    /// <summary>A model file with a real safetensors header, for the rungs that read one.</summary>
    private string WriteSafetensors(string relative, string headerJson)
    {
        var path = Path.Combine(SourceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, SafetensorsFixture.Safetensors(headerJson));
        return path;
    }

    /// <summary>
    /// <paramref name="type"/> stands in for the DB row's own <c>Model.Type</c> column — the source
    /// Task 11 reads a DB-known candidate's kind from, now that discovery and the identify step
    /// (Tasks 6-8) keep it current. Defaults to <see cref="ModelType.LORA"/> so the large majority of
    /// call sites that only care about the base model need not think about kind at all.
    /// </summary>
    private static InstalledModelFile Installed(string path, string baseModel, string tag, ModelType type = ModelType.LORA)
    {
        var model = new Model { Type = type, Tags = { new ModelTag { Tag = new Tag { Name = tag } } } };
        var version = new ModelVersion { BaseModelRaw = baseModel };
        var file = new ModelFile { LocalPath = path };
        return new InstalledModelFile(model, version, file, Path.GetDirectoryName(path)!);
    }

    private LoraSorterViewModel CreateVm(long freeSpace = long.MaxValue,
        IReadOnlyList<InstalledModelFile>? cached = null,
        Func<string, long>? getAvailableSpace = null,
        Func<string, bool>? fileExistsOnDisk = null,
        Func<string, string>? resolverHash = null,
        Func<string, CancellationToken, Task>? deleteEmptyDirectories = null,
        Func<CancellationToken, Task<IReadOnlyList<InstalledModelFile>>>? loadCachedFiles = null,
        IUnifiedLogger? logger = null)
    {
        _settings.Setup(s => s.GetEnabledLoraSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([SourceRoot]);
        _settings.Setup(s => s.GetFavoriteLoraSourceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _sync.Setup(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached ?? []);

        return new LoraSorterViewModel(
            _settings.Object, _sync.Object, logger,
            pathUpdater: Mock.Of<ILocalPathUpdater>(),
            metadataResolver: new SorterMetadataResolver(null, () => Task.FromResult<string?>(null),
                Path.Combine(_root.FullName, "cache"), resolverHash ?? (_ => "hash"), logger: null),
            fileOperations: new FileOperations(),
            getAvailableSpace: getAvailableSpace ?? (_ => freeSpace),
            hashFile: _ => "hash",
            fileExistsOnDisk: fileExistsOnDisk ?? File.Exists,
            historyDirectory: Path.Combine(_root.FullName, "history"),
            deleteEmptyDirectories: deleteEmptyDirectories,
            loadCachedFiles: loadCachedFiles);
    }

    [Fact]
    public async Task PreviewGroupsCachedFilesByBaseModelAndCategory()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var b = WriteLora(@"flat\b.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(a, "SDXL 1.0", "character"),
            Installed(b, "Illustrious", "style"),
        ]);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(2);
        var rootNames = vm.PreviewRoots.Select(n => n.Name);
        rootNames.Should().Contain(["SDXL 1.0", "Illustrious"]);
        vm.PreviewRoots.First(n => n.Name == "SDXL 1.0")
            .Children.Select(c => c.Name).Should().Contain("Character");
    }

    /// <summary>
    /// ModelFileSyncService stamps every locally-discovered model BaseModelRaw = "???", and
    /// IdentifyModelStep only clears that when a library sync actually runs — it is due-gated on
    /// 30-day windows and attempt caps. Pointing the sorter at a registered LoRA root takes the
    /// DB-known branch for nearly every file, so without asking the file here the feature would do
    /// almost nothing for exactly the self-trained LoRAs it exists for: the same file would be
    /// identified from its header while sitting in an unregistered folder, and dumped into Unknown
    /// the moment it was registered.
    /// </summary>
    [Fact]
    public async Task APlaceholderBaseModelOnADbKnownRowIsIdentifiedFromTheFile()
    {
        var path = Path.Combine(SourceRoot, "flat", "selftrained.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, SafetensorsFixture.Safetensors(
            SafetensorsFixture.Meta(("modelspec.architecture", "stable-diffusion-xl-v1-base"))));

        var vm = CreateVm(cached: [Installed(path, "???", "character")]);

        await vm.InitializeAsync();

        vm.PreviewRoots.Select(n => n.Name).Should()
            .Contain("SDXL 1.0", "the row says \"???\", but the file itself does not")
            .And.NotContain(LoraPathBuilder.UnknownFolderName);
    }

    /// <summary>
    /// The name rung is off by default, so a LoRA only its name can identify sits in Unknown — and
    /// the hint tells the user, in numbers taken from their own library, what turning it on buys.
    /// </summary>
    [Fact]
    public async Task ANameOnlyLoraStaysUnknownAndTheHintOffersTheFix()
    {
        var path = WriteLora(@"flat\MyChar_Pony_v2.safetensors");
        var vm = CreateVm(cached: [Installed(path, "???", "character")]);

        await vm.InitializeAsync();

        vm.GuessBaseModelFromFileName.Should().BeFalse("the lowest-confidence rung is opt-in");
        vm.PreviewRoots.Select(n => n.Name).Should().Contain(LoraPathBuilder.UnknownFolderName);
        vm.NameGuessHint.Should().Be(
            "1 LoRA could not be identified — sorting by name will fix 1 of them.");
    }

    /// <summary>Turning it on files the LoRA by its name and says so in the past tense.</summary>
    [Fact]
    public async Task TurningOnNameSortingFilesTheLoraAndRewordsTheHint()
    {
        var path = WriteLora(@"flat\MyChar_Pony_v2.safetensors");
        var vm = CreateVm(cached: [Installed(path, "???", "character")]);
        await vm.InitializeAsync();

        vm.GuessBaseModelFromFileName = true;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.PreviewRoots.Select(n => n.Name).Should()
            .Contain("Pony").And.NotContain(LoraPathBuilder.UnknownFolderName);
        vm.NameGuessHint.Should().Be(
            "Sorting by name identified 1 of 1 otherwise-unidentified LoRA.");
    }

    /// <summary>
    /// A file the name cannot help with is counted as unidentified but not as fixable, so the offer
    /// never overstates itself — "will fix 4 of them" has to be 4, not "some of them".
    /// </summary>
    [Fact]
    public async Task TheHintCountsOnlyTheFilesTheNameCanActuallyFix()
    {
        var named = WriteLora(@"flat\MyChar_Pony_v2.safetensors");
        var mute = WriteLora(@"flat\untitled_final.safetensors");
        var vm = CreateVm(cached: [Installed(named, "???", "character"), Installed(mute, "???", "character")]);

        await vm.InitializeAsync();

        vm.NameGuessHint.Should().Be(
            "2 LoRAs could not be identified — sorting by name will fix 1 of them.");
    }

    /// <summary>Nothing to offer, nothing said — the hint is not a permanent fixture of the panel.</summary>
    [Fact]
    public async Task TheHintIsSilentWhenTheNameRungWouldChangeNothing()
    {
        var path = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(path, "SDXL 1.0", "character")]);

        await vm.InitializeAsync();

        vm.NameGuessHint.Should().BeNull();
    }

    /// <summary>
    /// The guess travels on the candidate rather than being baked into its base model, so toggling
    /// the option re-plans off the candidate cache instead of re-walking the disk. On a browsed
    /// folder of thousands of unknown files that is the difference between an instant checkbox and
    /// a full re-resolve, so it is pinned by counting hashes: resolution hashes, planning does not
    /// (these candidates carry no colliding names).
    /// </summary>
    [Fact]
    public async Task TogglingNameSortingDoesNotReResolveCandidates()
    {
        WriteLora(@"flat\MyChar_Pony_v2.safetensors");
        var hashCalls = 0;
        var vm = CreateVm(cached: [], resolverHash: _ => { hashCalls++; return "hash"; });
        await vm.InitializeAsync();

        var afterFirstPass = hashCalls;
        afterFirstPass.Should().BeGreaterThan(0, "the unknown file was resolved from disk");

        vm.GuessBaseModelFromFileName = true;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.PreviewRoots.Select(n => n.Name).Should().Contain("Pony");
        hashCalls.Should().Be(afterFirstPass, "toggling the option must re-plan, not re-resolve");
    }

    /// <summary>
    /// #527 (Task 9): this used to demonstrate the bug itself — a VAE misfiled into its LoRAs'
    /// base-model folder, invisible until a stray file turned up after the sort ("a folder's chips
    /// are the union of everything beneath it, so a base-model folder about to receive a VAE
    /// alongside its LoRAs says so before anything moves"). Task 9 fixes that at the routing layer
    /// instead: a support asset never lands under a base-model folder at all, so <c>Qwen</c>'s chip
    /// set can no longer show anything but the LoRA it actually holds. The union mechanism itself
    /// (<c>Absorb</c>) still matters and is still exercised — just now proven by confirming the VAE
    /// landed, correctly labelled, in its own folder instead of by finding it mixed into someone
    /// else's.
    /// <para>
    /// Task 11: a DB-known row's kind now comes from its own <c>Model.Type</c> rather than its file
    /// name whenever the base model is already real (not a placeholder) — this row's is "Qwen" — so
    /// the VAE is typed explicitly via <c>Installed</c>'s <c>type:</c> rather than relying on the
    /// "vae" token the file name still carries for readability.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ASupportAssetNoLongerMixesIntoItsBaseModelFoldersChips()
    {
        var lora = WriteLora(@"flat\MyChar.safetensors");
        var vae = WriteLora(@"flat\qwen_image_vae.safetensors");
        var vm = CreateVm(cached:
            [Installed(lora, "Qwen", "character"), Installed(vae, "Qwen", "character", type: ModelType.VAE)]);

        await vm.InitializeAsync();

        vm.PreviewRoots.Single(n => n.Name == "Qwen")
            .AssetKinds.Should().ContainSingle().Which.Should().Be("LoRA");
        vm.PreviewRoots.Single(n => n.Name == "VAE")
            .AssetKinds.Should().ContainSingle().Which.Should().Be("VAE");
    }

    /// <summary>
    /// #527 regression: <c>CivitaiMetadataApplier</c> writes <c>BaseModelRaw</c> unconditionally on a
    /// hash match, not gated on <c>Type</c> — so a support asset synced against Civitai before this
    /// feature existed keeps discovery's old blanket <c>Type = LORA</c> stamp forever; nothing in the
    /// identify pipeline ever revisits it once its base model is real. Before Task 11 this file was
    /// still reclassified from its name on every preview pass regardless of the DB row, so it
    /// displayed correctly despite the stale column. Edit (a) must not trust that stale "LORA" as
    /// though it were evidence: a row saying LORA proves nothing (it is what every file was stamped
    /// before this feature existed), so the file's own name still gets asked, exactly as before.
    /// </summary>
    [Fact]
    public async Task ARealBaseModelRowStuckAtTypeLoraStillClassifiesFromItsName()
    {
        var vae = WriteLora(@"flat\sdxl_vae.safetensors");
        // type: defaults to ModelType.LORA — the stale value a legacy, Civitai-matched row keeps.
        var vm = CreateVm(cached: [Installed(vae, "SDXL 1.0", "character")]);

        await vm.InitializeAsync();

        vm.PreviewRoots.Single(n => n.Name == "VAE")
            .AssetKinds.Should().ContainSingle().Which.Should().Be("VAE");
    }

    /// <summary>
    /// Pins the FULL chip order deliberately, not incidentally: LoRA first, then VAE, ControlNet,
    /// Upscaler, Text Encoder — the <c>ModelTypeExtensions.SupportAssetKinds</c> order — via
    /// <c>SortPreviewNodeViewModel</c>'s <c>ChipOrder</c> comparer. The sibling test above (LoRA +
    /// VAE only) cannot discriminate ChipOrder from <c>ModelType</c>'s own raw numeric order:
    /// LoRA(5) sorts before VAE(12) either way. Five kinds can, because ModelType's persisted values
    /// (LORA=5, Controlnet=8, Upscaler=10, VAE=12, TextEncoder=19) would put ControlNet and Upscaler
    /// BEFORE VAE if nothing overrode them — the opposite of the order asserted here.
    /// </summary>
    /// <remarks>
    /// #527 (Task 9): five kinds can no longer land under one DESTINATION folder — each support kind
    /// now gets its own flat folder beside the base-model ones, so <c>PreviewRoots</c> never again
    /// absorbs more than one kind into a single node. They still share a SOURCE folder before the
    /// sort runs, though, and <c>Absorb</c> rolls every kind up through the ancestor chain on both
    /// sides of the pane (<c>LoraSorterViewModel.AddFileNode</c>) — so <c>SourceRoots</c>' "flat"
    /// node is now the one place all five still co-occur, and the ChipOrder comparer under test is
    /// the exact same one used for both trees.
    /// <para>
    /// Task 11: with a real ("Qwen") base model already on the row, none of these enters the
    /// placeholder-only header-read branch, so each kind has to be stated via <c>Installed</c>'s
    /// <c>type:</c> — the DB row's own <c>Model.Type</c> — rather than left to the file name.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFolderOrdersItsChipsDeliberatelyNotByModelTypesRawPersistedValue()
    {
        var lora = WriteLora(@"flat\MyChar.safetensors");
        var vae = WriteLora(@"flat\qwen_image_vae.safetensors");
        var controlNet = WriteLora(@"flat\qwen_image_controlnet.safetensors");
        var upscaler = WriteLora(@"flat\qwen_image_upscaler.safetensors");
        var textEncoder = WriteLora(@"flat\clip_l.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(lora, "Qwen", "character"),
            Installed(vae, "Qwen", "character", type: ModelType.VAE),
            Installed(controlNet, "Qwen", "character", type: ModelType.Controlnet),
            Installed(upscaler, "Qwen", "character", type: ModelType.Upscaler),
            Installed(textEncoder, "Qwen", "character", type: ModelType.TextEncoder),
        ]);

        await vm.InitializeAsync();

        var flat = vm.SourceRoots.Single(n => n.Name == "flat");
        flat.AssetKinds.Should().BeEquivalentTo(
            ["LoRA", "VAE", "ControlNet", "Upscaler", "Text Encoder"],
            o => o.WithStrictOrdering());
    }

    /// <summary>The mark is subtree-wide: a base-model folder is finished only when everything
    /// under it is, which is the whole point of rolling it up rather than reading one node.</summary>
    [Fact]
    public async Task AFolderIsUnfinishedWhenAnythingBeneathItIsUnidentified()
    {
        var known = WriteLora(@"flat\a.safetensors");
        var unknown = WriteLora(@"flat\BRFHE7KV2VWXY8N3D4SXR4XCT0.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(known, "SDXL 1.0", "character"),
            Installed(unknown, "???", "character"),
        ]);

        await vm.InitializeAsync();

        vm.PreviewRoots.Single(n => n.Name == "SDXL 1.0").IsIdentified.Should().BeTrue();

        var unknownFolder = vm.PreviewRoots.Single(n => n.Name == LoraPathBuilder.UnknownFolderName);
        unknownFolder.IsUnidentified.Should().BeTrue();
        unknownFolder.StatusTooltip.Should().Contain("sort into Unknown");

        // ...and through the category level too, not just at the root.
        unknownFolder.Children.Where(c => !c.IsFile)
            .Should().OnlyContain(c => c.IsUnidentified);
    }

    /// <summary>
    /// "Identified" has to mean what the tree is actually drawing — and a file the name rung placed
    /// is NOT identified, it is guessed. Marking it ✓ under "every file here has a base model"
    /// turned the one screen that could audit the lowest-confidence rung into an endorsement of it.
    /// </summary>
    [Fact]
    public async Task TurningOnNameSortingMarksTheFolderGuessedNotIdentified()
    {
        var path = WriteLora(@"flat\MyChar_Pony_v2.safetensors");
        var vm = CreateVm(cached: [Installed(path, "???", "character")]);
        await vm.InitializeAsync();

        vm.PreviewRoots.Single(n => n.Name == LoraPathBuilder.UnknownFolderName)
            .IsUnidentified.Should().BeTrue();

        vm.GuessBaseModelFromFileName = true;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        var pony = vm.PreviewRoots.Single(n => n.Name == "Pony");
        pony.IsGuessed.Should().BeTrue();
        pony.IsIdentified.Should().BeFalse("a name is not a reading of the file");
        pony.StatusTooltip.Should().Contain("named, not read");
        pony.AssetKinds.Should().ContainSingle().Which.Should().Be("LoRA");
    }

    /// <summary>
    /// The other side of the same rule: a file whose header answered is marked read, not guessed,
    /// even while the name rung is on and its name would have produced the same label. Comparing
    /// BaseModelRaw against NameGuess would have called this one guessed.
    /// </summary>
    [Fact]
    public async Task AHeaderReadStaysIdentifiedWhileTheNameRungIsOn()
    {
        var path = WriteSafetensors(@"flat\MyChar_Pony_v2.safetensors",
            SafetensorsFixture.Meta(("ss_base_model_version", "sdxl_base_v1-0")));
        var vm = CreateVm(cached: [Installed(path, "???", "character")]);
        vm.GuessBaseModelFromFileName = true;

        await vm.InitializeAsync();

        var node = vm.PreviewRoots.Single(n => n.Name == "SDXL 1.0");
        node.IsIdentified.Should().BeTrue();
        node.StatusTooltip.Should().Be("Every file here has a base model.");
    }

    /// <summary>
    /// A folder's mark is the WORST thing under it: one unidentified file outranks any number of
    /// guessed ones, which outrank read ones. Otherwise "some files here" would depend on the order
    /// files happened to arrive in.
    /// </summary>
    [Fact]
    public async Task AFolderTakesTheWorstMarkBeneathIt()
    {
        var read = WriteSafetensors(@"flat\a_pony.safetensors",
            SafetensorsFixture.Meta(("ss_base_model_version", "sdxl_base_v1-0")));
        var guessed = WriteLora(@"flat\b_sdxl_style.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(read, "???", "character"),
            Installed(guessed, "???", "character"),
        ]);
        vm.GuessBaseModelFromFileName = true;

        await vm.InitializeAsync();

        // Both land in SDXL 1.0 — one because its header said so, one because its name did.
        var node = vm.PreviewRoots.Single(n => n.Name == "SDXL 1.0");
        node.IsGuessed.Should().BeTrue("the guessed file drags the folder off ✓");
    }

    /// <summary>
    /// The hint's numbers belong to one folder. Deselecting the source clears the tree, the summary
    /// and the plan — leaving the hint behind advertised "5 LoRAs could not be identified…" over an
    /// empty tree, for a library that is no longer selected.
    /// </summary>
    [Fact]
    public async Task DeselectingTheSourceClearsTheHintWithTheTree()
    {
        var vm = CreateVm(cached: [Installed(WriteLora(@"flat\MyChar_Pony_v2.safetensors"), "???", "character")]);
        await vm.InitializeAsync();
        vm.NameGuessHint.Should().NotBeNull();

        vm.SelectedSourceFolder = null;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.PreviewRoots.Should().BeEmpty();
        vm.NameGuessHint.Should().BeNull();
    }

    /// <summary>
    /// The sorter physically relocates everything its enumeration yields, so the scan list is not
    /// the place to be generous. Routing it at the merged extension set widened it to .bin and
    /// .gguf — a root holding pytorch_model.bin would have had it filed into a base-model folder,
    /// wearing a [LoRA] chip because the classifier is name-based and nothing would flag it.
    /// </summary>
    [Fact]
    public async Task WeightFormatsTheSorterDoesNotOwnAreNotPlannedForAMove()
    {
        WriteLora(@"flat\pytorch_model.bin");
        WriteLora(@"flat\quantized.gguf");
        var moved = WriteLora(@"flat\MyChar.sft");

        var vm = CreateVm();
        await vm.InitializeAsync();

        var planned = vm.PreviewRoots
            .SelectMany(Flatten)
            .Where(n => n.IsFile)
            .Select(n => n.Name)
            .ToList();

        planned.Should().Contain(Path.GetFileName(moved),
            "the short safetensors spelling is the same container the header reader already reads");
        planned.Should().NotContain("pytorch_model.bin").And.NotContain("quantized.gguf");
    }

    private static IEnumerable<SortPreviewNodeViewModel> Flatten(SortPreviewNodeViewModel node)
        => new[] { node }.Concat(node.Children.SelectMany(Flatten));

    /// <summary>A file node carries its own kind and its own mark, so expanding an unfinished folder
    /// shows which files are the problem rather than only that some are.</summary>
    /// <remarks>
    /// Pre-#527 this VAE (placeholder base model) sorted into <c>Unknown\Character\</c>, which is
    /// where the file node used to be found. Task 9 routes a support asset to its own flat
    /// <c>VAE\</c> folder regardless of base model, so the lookup path changed. Task 11 changes the
    /// mark itself, too: a VAE has no base model to be missing, so its placeholder no longer counts
    /// against it — <c>IdentityOf</c> now reports it Identified rather than Unidentified.
    /// <para>
    /// Final-review Critical #1: the fixture writes a REAL safetensors header now, because a
    /// <c>.safetensors</c> whose header cannot be read is no longer named from its file name — an
    /// unreadable container is not evidence, and guessing there is the one verdict a user cannot
    /// undo. A genuine VAE has readable weights that say <c>post_quant_conv</c>, so this fixture is
    /// also the more honest one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFileNodeCarriesItsOwnKindAndMark()
    {
        var vae = WriteSafetensors(@"flat\sdxl_vae.safetensors",
            SafetensorsFixture.Tensors("post_quant_conv.weight"));
        var vm = CreateVm(cached: [Installed(vae, "???", "character")]);

        await vm.InitializeAsync();

        var fileNode = vm.PreviewRoots
            .Single(n => n.Name == "VAE")
            .Children.Single(c => c.IsFile);

        fileNode.AssetKinds.Should().ContainSingle().Which.Should().Be("VAE");
        fileNode.IsIdentified.Should().BeTrue("a support asset has no base model to be missing (#527)");
        fileNode.StatusTooltip.Should().Be("Every file here has a base model.");
    }

    /// <summary>
    /// #527: the count could never reach zero because ~35 files in a real library are not LoRAs at
    /// all. They are identified — we know exactly what they are — just not as LoRAs.
    /// </summary>
    [Fact]
    public async Task TheHintDoesNotCountSupportAssetsAsUnidentifiedLoras()
    {
        // Browsed (not DB-known), so the kind comes from SorterMetadataResolver rather than a row's
        // Type. The VAE carries a real header (final-review Critical #1: a .safetensors we cannot
        // read stays LORA, so a name-only fixture would no longer stand for a VAE at all); the LoRA
        // stays header-less because nothing here needs it to be readable.
        WriteSafetensors(@"flat\Wan2_2_VAE_bf16.safetensors",
            SafetensorsFixture.Tensors("post_quant_conv.weight"));
        WriteLora(@"flat\mystery_lora.safetensors");
        var vm = CreateVm();

        await vm.InitializeAsync();

        vm.NameGuessHint.Should().NotContain("2 LoRAs",
            "only the one file that is actually a LoRA can be an unidentified LoRA");
    }

    /// <summary>
    /// A VAE has no base model and never will. Marking its folder ✗ for that would ask the wrong
    /// question of it and leave the tree permanently unfinished. Real header, for the reason given
    /// on <see cref="AFileNodeCarriesItsOwnKindAndMark"/> (final-review Critical #1).
    /// </summary>
    [Fact]
    public async Task ASupportAssetDoesNotPoisonItsFoldersMark()
    {
        WriteSafetensors(@"flat\Wan2_2_VAE_bf16.safetensors",
            SafetensorsFixture.Tensors("post_quant_conv.weight"));
        var vm = CreateVm();

        await vm.InitializeAsync();

        var folder = vm.PreviewRoots.Single(n => n.Name == "VAE");
        folder.IsUnidentified.Should().BeFalse();
        folder.IsIdentified.Should().BeTrue();
        folder.AssetKinds.Should().ContainSingle().Which.Should().Be("VAE");
    }

    [Fact]
    public async Task BaseModelOnlyModeFlattensCategoryLevel()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();

        vm.IncludeCategory = false;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.PreviewRoots.First(n => n.Name == "SDXL 1.0")
            .Children.Where(c => !c.IsFile).Should().BeEmpty();
    }

    [Fact]
    public async Task InsufficientDiskSpaceBlocksStartWithReason()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(freeSpace: 0, cached: [Installed(a, "SDXL 1.0", "character")]);
        vm.IsMove = false; // copy → RequiredBytes > 0, and 0 free < margin
        vm.CustomTargetFolder = Path.Combine(_root.FullName, "Elsewhere");

        await vm.InitializeAsync();

        vm.HasEnoughSpace.Should().BeFalse();
        vm.BlockReason.Should().NotBeNull();
        vm.StartSortingCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task CopyIntoSourceRootIsBlocked()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();

        vm.IsMove = false; // target still "same as source"
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.StartSortingCommand.CanExecute(null).Should().BeFalse();
        vm.BlockReason.Should().Contain("source");
    }

    [Fact]
    public async Task UnknownFileInBrowsedFolderIsResolvedIntoUnknownBuckets()
    {
        WriteLora(@"flat\mystery.safetensors"); // no DB row, no sidecar, no client → Unknown
        var vm = CreateVm(cached: []);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        vm.PreviewRoots.Single().Name.Should().Be("Unknown");
    }

    [Fact]
    public async Task UnreadableDbKnownFileIsSkippedNotFatalAndIsReported()
    {
        // FileSizeBytes is null on this row, so the size comes from new FileInfo(path).Length —
        // which throws FileNotFoundException for a path that is gone. One bad file out of 3000
        // used to abort the whole preview with zero candidates.
        var good = WriteLora(@"flat\good.safetensors");
        var vanished = Path.Combine(SourceRoot, "flat", "gone.safetensors");
        var vm = CreateVm(
            cached: [Installed(good, "SDXL 1.0", "character"), Installed(vanished, "SDXL 1.0", "character")],
            // The row survives the existence check; the file itself does not. Scoped to that one
            // path so the planner's real collision probing is untouched.
            fileExistsOnDisk: p => string.Equals(p, vanished, StringComparison.OrdinalIgnoreCase) || File.Exists(p));

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        vm.StatusMessage.Should().Contain("1 file(s) skipped");
        FlattenNames(vm.PreviewRoots).Should().NotContain("gone.safetensors");
    }

    [Fact]
    public async Task UnreadableBrowsedFileIsSkippedNotFatalAndIsReported()
    {
        // The resolver hashes the file first; deleting it there is a deterministic stand-in for the
        // enumerate-then-vanish race, and makes the following new FileInfo(path).Length throw.
        var doomed = WriteLora(@"flat\doomed.safetensors");
        WriteLora(@"flat\fine.safetensors");
        var vm = CreateVm(cached: [], resolverHash: p =>
        {
            if (string.Equals(p, doomed, StringComparison.OrdinalIgnoreCase)) File.Delete(p);
            return "hash";
        });

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        vm.StatusMessage.Should().Contain("1 file(s) skipped");
        FlattenNames(vm.PreviewRoots).Should().NotContain("doomed.safetensors");
    }

    [Fact]
    public async Task AFailedDbKnownFileIsSkippedOnceAndNeverReResolvedAsUnknown()
    {
        // The path must be registered as DB-known BEFORE the per-file reads that can throw
        // (size, sidecar probing). Registered afterwards, a .safetensors held open by a running
        // ComfyUI was skipped AND then re-enumerated as unknown: a full-file SHA256 plus a
        // serialized Civitai round-trip on the same file, "2 file(s) skipped" reported for one
        // file, and — if the second attempt succeeded — a candidate built from API metadata
        // instead of its own DB row. The existence probe stands in for that per-file read: it
        // fails for this one path only, and the file itself is really there to be enumerated.
        var locked = WriteLora(@"flat\locked.safetensors");
        var good = WriteLora(@"flat\good.safetensors");
        var resolverCalls = new List<string>();
        var vm = CreateVm(
            cached: [Installed(locked, "SDXL 1.0", "character"), Installed(good, "SDXL 1.0", "character")],
            fileExistsOnDisk: p => string.Equals(p, locked, StringComparison.OrdinalIgnoreCase)
                ? throw new IOException("The process cannot access the file because it is being used by another process.")
                : File.Exists(p),
            resolverHash: p =>
            {
                resolverCalls.Add(p);
                return "hash";
            });

        await vm.InitializeAsync();

        vm.StatusMessage.Should().Contain("1 file(s) skipped");
        vm.TransferCount.Should().Be(1);
        resolverCalls.Should().NotContain(locked);
        FlattenNames(vm.PreviewRoots).Should().NotContain("locked.safetensors");
    }

    [Fact]
    public async Task WhitespaceOnlyLocalPathIsSkippedNotFatalAndIsReported()
    {
        // Path.GetFullPath (called inside the IsWithin boundary check) throws ArgumentException
        // for a whitespace-only string. A blank/malformed ModelFile.LocalPath row used to abort
        // the whole pass with zero candidates because the boundary check ran before the per-file
        // try/catch could absorb it.
        var good = WriteLora(@"flat\good.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(good, "SDXL 1.0", "character"),
            Installed("   ", "SDXL 1.0", "character"),
        ]);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        vm.StatusMessage.Should().Contain("1 file(s) skipped");
    }

    [Fact]
    public async Task AProgrammingDefectInTheResolverIsSurfacedNotCountedAsALockedFile()
    {
        // ArgumentException was added to the per-file "locked/unreadable" filter to absorb a
        // whitespace-only LocalPath — but that filter also wraps ResolveAsync, so it swallowed
        // ArgumentNullException/ArgumentOutOfRangeException from anywhere inside the resolver, the
        // Civitai client or the sidecar locator. A systematic one on a 3000-file folder reported
        // "3000 file(s) skipped (locked/unreadable)" over an empty preview, pointing the user at
        // their disk instead of at the bug.
        WriteLora(@"flat\mystery.safetensors");
        var vm = CreateVm(cached: [], resolverHash: _ => throw new ArgumentNullException("cacheDirectory"));

        await vm.InitializeAsync();

        vm.StatusMessage.Should().Contain("Preview failed");
        vm.StatusMessage.Should().NotContain("skipped");
    }

    [Fact]
    public async Task TheSkippedFileNoteSurvivesAnOptionToggle()
    {
        var good = WriteLora(@"flat\good.safetensors");
        var vanished = Path.Combine(SourceRoot, "flat", "gone.safetensors");
        var vm = CreateVm(
            cached: [Installed(good, "SDXL 1.0", "character"), Installed(vanished, "SDXL 1.0", "character")],
            fileExistsOnDisk: p => string.Equals(p, vanished, StringComparison.OrdinalIgnoreCase) || File.Exists(p));
        await vm.InitializeAsync();

        vm.IncludeCategory = false;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        // The toggle re-plans from the cache without re-resolving, so the note must come with it.
        vm.StatusMessage.Should().Contain("1 file(s) skipped");
    }

    [Fact]
    public async Task BrowsedFolderFileWithSidecarTagsLandsInItsCategoryFolder()
    {
        // LoadCachedFilesAsync hard-filters to enabled LoRA source roots, so a browsed folder gets
        // no DB metadata at all — this is the NORMAL path for the "Browse any folder" feature. The
        // category used to be hardcoded to Unknown, dumping a fully resolved library into
        // <Target>\SDXL 1.0\Unknown\ instead of \Character\.
        var lora = WriteLora(@"flat\hero.safetensors");
        File.WriteAllText(Path.ChangeExtension(lora, null) + ".civitai.info",
            """{"baseModel":"SDXL 1.0","id":4242,"model":{"tags":["character"]}}""");
        var vm = CreateVm(cached: []);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        var root = vm.PreviewRoots.Should().ContainSingle().Subject;
        root.Name.Should().Be("SDXL 1.0");
        root.Children.Should().ContainSingle(c => !c.IsFile).Which.Name.Should().Be("Character");
    }

    [Fact]
    public async Task SiblingFolderSharingPrefixIsNotSwept()
    {
        // Source "...\Loras" must not sweep "...\Loras_backup" — a bare StartsWith would match
        // the shared name prefix even though the sibling folder is a different location.
        var a = WriteLora(@"flat\a.safetensors");
        var siblingDir = Path.Combine(_root.FullName, "Loras_backup");
        Directory.CreateDirectory(siblingDir);
        var b = Path.Combine(siblingDir, "b.safetensors");
        File.WriteAllText(b, "weights");

        var vm = CreateVm(cached:
        [
            Installed(a, "SDXL 1.0", "character"),
            Installed(b, "SDXL 1.0", "character"),
        ]);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        FlattenNames(vm.PreviewRoots).Should().NotContain("b.safetensors");
    }

    [Fact]
    public async Task RunResultMessageSurvivesPostRunRecompute()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        vm.DialogService = ConfirmingDialogService();

        await vm.InitializeAsync();

        var sortCompleted = false;
        vm.SortCompleted += (_, _) => sortCompleted = true;

        await vm.StartSortingCommand.ExecuteAsync(null);

        vm.StatusMessage.Should().Contain("Done");
        sortCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task AnUndeletableEmptyFolderDoesNotTurnASuccessfulSortIntoAFailure()
    {
        // DiskUtility.DeleteEmptyDirectories calls Directory.Delete with no guard, and it runs
        // AFTER every file has been transferred and taskHandle.Complete() has reported success. An
        // Explorer window or AV holding one now-empty folder used to unwind into StartSortingAsync's
        // catch: no post-run recompute, SortCompleted never fired, and the user saw
        // "Sorting failed: Access to the path … is denied" over a preview listing dead paths.
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")],
            deleteEmptyDirectories: (_, _) => throw new UnauthorizedAccessException("Access to the path 'flat' is denied."));
        vm.DialogService = ConfirmingDialogService();
        await vm.InitializeAsync();
        vm.DeleteEmptySourceFolders = true;

        var sortCompleted = false;
        vm.SortCompleted += (_, _) => sortCompleted = true;

        await vm.StartSortingCommand.ExecuteAsync(null);

        vm.StatusMessage.Should().StartWith("Done:");
        vm.StatusMessage.Should().Contain("some empty folders could not be removed");
        sortCompleted.Should().BeTrue();
        // The post-run recompute ran: the tree was rebuilt from disk, where the DB's old path no
        // longer exists, so the "SDXL 1.0" bucket the pre-run preview showed is gone.
        vm.PreviewRoots.Select(n => n.Name).Should().NotContain("SDXL 1.0");
    }

    [Fact]
    public async Task TheEmptyFolderCleanupFollowsThePlanAndStaysInStepWithTheCheckbox()
    {
        // The gate used to read the live checkbox after the run, while the run it was deciding
        // about used the options captured at plan time. It now reads plan.IsMove plus the checkbox
        // as snapshotted when Start captured the plan — so a box ticked after the preview was
        // computed still applies, without the plan needing to be re-stamped or re-planned.
        var a = WriteLora(@"flat\a.safetensors");
        string? cleanedRoot = null;
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")],
            deleteEmptyDirectories: (root, _) =>
            {
                cleanedRoot = root;
                return Task.CompletedTask;
            });
        vm.DialogService = ConfirmingDialogService();

        await vm.InitializeAsync(); // planned with the box unchecked
        vm.DeleteEmptySourceFolders = true;

        await vm.StartSortingCommand.ExecuteAsync(null);

        cleanedRoot.Should().Be(SourceRoot);
    }

    [Fact]
    public async Task ARunPlannedWithoutTheCheckboxDoesNotCleanUp()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var cleanups = 0;
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")],
            deleteEmptyDirectories: (_, _) =>
            {
                cleanups++;
                return Task.CompletedTask;
            });
        vm.DialogService = ConfirmingDialogService();
        await vm.InitializeAsync();

        await vm.StartSortingCommand.ExecuteAsync(null);

        cleanups.Should().Be(0);
    }

    [Fact]
    public async Task ChangingAnOptionClearsTheRunResultBanner()
    {
        // After a completed run the Done-banner shows; the next user action clears it.
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        vm.DialogService = ConfirmingDialogService();
        await vm.InitializeAsync();
        await vm.StartSortingCommand.ExecuteAsync(null);
        vm.StatusMessage.Should().Contain("Done");

        vm.IncludeCategory = !vm.IncludeCategory;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.StatusMessage.Should().BeNull();
    }

    [Fact]
    public async Task TickingDeleteEmptyFoldersWhileTheConfirmDialogIsOpenDoesNotAbortTheRun()
    {
        // Keeping the armed plan in step with the checkbox by re-stamping it
        // (`_lastPlan = _lastPlan with { … }`) minted a NEW plan reference, and the confirm-time
        // ReferenceEquals guard — which exists to catch a genuine re-plan landing behind the
        // dialog — could not tell the two apart. Ticking the box in front of the open dialog
        // therefore aborted the run the user had just confirmed with "Preview changed while
        // confirming". The flag is snapshotted when Start captures the plan instead, so nothing
        // reassigns _lastPlan and the guard keeps its original meaning.
        var a = WriteLora(@"flat\a.safetensors");
        string? cleanedRoot = null;
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")],
            deleteEmptyDirectories: (root, _) =>
            {
                cleanedRoot = root;
                return Task.CompletedTask;
            });
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() =>
            {
                vm.DeleteEmptySourceFolders = true;
                return true;
            });
        vm.DialogService = dialog.Object;
        await vm.InitializeAsync();

        await vm.StartSortingCommand.ExecuteAsync(null);

        vm.StatusMessage.Should().StartWith("Done:");
        vm.StatusMessage.Should().NotContain("Preview changed");
        // The snapshot is taken before the dialog opens, so the late tick governs the NEXT run.
        cleanedRoot.Should().BeNull();
    }

    private static IDialogService ConfirmingDialogService()
    {
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        return dialog.Object;
    }

    [Fact]
    public async Task OptionToggleDoesNotReEnumerateDisk()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();

        var before = vm.TransferCount;
        before.Should().BeGreaterThan(0);

        // If the option toggle re-walked the disk, this deleted file would drop out of the
        // DB-known candidate set (fileExistsOnDisk check) and TransferCount would fall.
        File.Delete(a);

        vm.IncludeCategory = false;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.TransferCount.Should().Be(before);
    }

    [Fact]
    public async Task RefreshRebuildsCandidatesFromDisk()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();
        vm.TransferCount.Should().Be(1);

        File.Delete(a);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.TransferCount.Should().Be(0);
    }

    [Fact]
    public async Task CancellingAPassKeepsTheRenderedPreviewAndMarksItStale()
    {
        // Cancel used to run the full DisarmPlan: the 42-file tree, the summary and the disk line
        // all vanished, under a status line asserting "preview not updated" and a block reason
        // telling the user to press Refresh — three mutually contradictory statements. Start must
        // be disarmed (its plan is gone); the tree must stay, flagged as possibly stale.
        var a = WriteLora(@"flat\a.safetensors");
        var secondPassStarted = new TaskCompletionSource();
        var passes = 0;
        var vm = CreateVm(loadCachedFiles: async ct =>
        {
            if (Interlocked.Increment(ref passes) == 1)
                return [Installed(a, "SDXL 1.0", "character")];
            secondPassStarted.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        });

        await vm.InitializeAsync();
        vm.PreviewRoots.Should().NotBeEmpty();

        var second = vm.RefreshCommand.ExecuteAsync(null);
        await secondPassStarted.Task;
        vm.CancelSortCommand.Execute(null);
        await second;

        vm.PreviewRoots.Select(n => n.Name).Should().Equal("SDXL 1.0");
        vm.PreviewSummary.Should().NotBeNull();
        vm.StatusMessage.Should().Contain("stale");
        vm.BlockReason.Should().Contain("stale");
        vm.TransferCount.Should().Be(0);
        vm.HasEnoughSpace.Should().BeFalse();
        vm.StartSortingCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task RefreshClearsACancelledPreviewBanner()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();

        // Simulate the post-cancel state the Cancel path leaves behind.
        vm.CancelSortCommand.Execute(null);
        vm.StatusMessage = "Cancelled — the preview shown may be stale; press Refresh to rebuild it.";

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.StatusMessage.Should().BeNull();
        vm.TransferCount.Should().Be(1); // preview genuinely rebuilt
    }

    [Fact]
    public async Task PreviewFailureIsSurfacedNotSwallowed()
    {
        var vm = CreateVm(cached: []);
        _sync.Setup(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        await vm.InitializeAsync(); // must not throw

        vm.StatusMessage.Should().Contain("Preview failed");
    }

    [Fact]
    public async Task AnInjectedLoadCachedFilesDelegateIsUsedInsteadOfTheSharedSyncService()
    {
        // Production passes a delegate that opens a fresh DI scope per call, so the sorter never
        // touches the session-long DbContext the scoped LoraViewerViewModel holds. If the VM
        // silently fell back to the shared IModelSyncService, this would preview nothing.
        var a = WriteLora(@"flat\a.safetensors");
        var calls = 0;
        var vm = CreateVm(cached: [], loadCachedFiles: _ =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<InstalledModelFile>>(
                [Installed(a, "SDXL 1.0", "character")]);
        });

        await vm.InitializeAsync();

        calls.Should().Be(1);
        _sync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Never);
        vm.PreviewRoots.Single().Name.Should().Be("SDXL 1.0");
    }

    [Fact]
    public async Task DesignTimeConstructorKeepsItsDemoDataAndStartsNoBackgroundWork()
    {
        // "SelectedSourceFolder = SourceFolders[0]" fired the recompute hook with _isInitializing
        // still false, so the design-time VM kicked off real disk and DriveInfo work against
        // C:\Demo\Loras; its continuation then cleared PreviewRoots and zeroed TransferCount,
        // showing an empty tree — the opposite of what the demo data is for. LoraViewerViewModel's
        // design-time ctor builds this VM too, so every "new LoraViewerViewModel()" in the suite
        // spawned filesystem I/O off the test thread with any exception unobserved.
        var vm = new LoraSorterViewModel();

        // Synchronous and deterministic: RunBusyAsync sets IsBusy before its first await, so a
        // still-false IsBusy on return from the ctor proves no pass was started at all.
        vm.IsBusy.Should().BeFalse();
        vm.TransferCount.Should().Be(17);
        vm.PreviewRoots.Should().HaveCount(2);

        await Task.Delay(250);

        vm.TransferCount.Should().Be(17);
        vm.PreviewRoots.Should().HaveCount(2);
        vm.PreviewSummary.Should().Contain("17 files will move");
    }

    [Fact]
    public async Task AFailedPassDisarmsThePlanFromTheSuccessfulOneBeforeIt()
    {
        // Preview S1 succeeds; the next pass throws. Start used to stay armed against S1's plan
        // while the confirm dialog interpolated the LIVE mode and target — "42 files will be copied
        // into S2" for a plan that moves files inside S1. ReferenceEquals(_lastPlan, plan) could not
        // catch it, because nothing had reassigned _lastPlan.
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();
        vm.TransferCount.Should().Be(1);
        vm.StartSortingCommand.CanExecute(null).Should().BeTrue();

        _sync.Setup(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.TransferCount.Should().Be(0);
        vm.HasEnoughSpace.Should().BeFalse();
        vm.PreviewRoots.Should().BeEmpty();
        vm.PreviewSummary.Should().BeNull();
        vm.BlockReason.Should().Contain("db down");
        vm.StartSortingCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task ASupersededPreviewPassCommitsNothingAndTheOverlayOutlivesIt()
    {
        // Pass A (slow) is superseded by pass B, which finishes first. Two things used to break:
        // RunBusyAsync's shared IsBusy dropped the overlay when B finished — arming Start against
        // whatever A had left behind — and A, on resuming, painted its own stale result over B's.
        var a = WriteLora(@"flat\a.safetensors");
        var passAStarted = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();
        var passes = 0;

        var vm = CreateVm(loadCachedFiles: async _ =>
        {
            if (Interlocked.Increment(ref passes) == 1)
            {
                passAStarted.SetResult();
                await releaseA.Task;
                return [Installed(a, "PassA", "character")];
            }
            return [Installed(a, "PassB", "character")];
        });

        var passA = vm.InitializeAsync();
        await passAStarted.Task;
        vm.IsBusy.Should().BeTrue();

        await vm.RefreshCommand.ExecuteAsync(null); // pass B, start to finish

        vm.IsBusy.Should().BeTrue("pass A is still in flight");
        vm.StartSortingCommand.CanExecute(null).Should().BeFalse();
        vm.PreviewRoots.Select(n => n.Name).Should().Equal("PassB");

        releaseA.SetResult();
        await passA;

        vm.IsBusy.Should().BeFalse("the last pass has now left");
        // A must not repaint over B.
        vm.PreviewRoots.Select(n => n.Name).Should().Equal("PassB");
        vm.TransferCount.Should().Be(1);
    }

    [Fact]
    public async Task ASupersededPassThatRunsToSuccessStillCommitsNothing()
    {
        // The nastier half of the same bug: cancelling a pass's token does NOT stop a delegate
        // that is already running (Task.Run only refuses to *start* one), so a superseded pass
        // reaches the SUCCESS path with a complete, stale plan and used to overwrite _lastPlan,
        // the tree and the disk gate for good. Gated inside the planner's on-disk collision probe,
        // which only ever sees paths under the built target directory.
        var a = WriteLora(@"flat\a.safetensors");
        var planningA = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();
        var probes = 0;
        var passes = 0;

        var vm = CreateVm(
            fileExistsOnDisk: p =>
            {
                if (p.Contains("Character", StringComparison.OrdinalIgnoreCase)
                    && Interlocked.Increment(ref probes) == 1)
                {
                    planningA.SetResult();
                    releaseA.Task.GetAwaiter().GetResult();
                }
                return File.Exists(p);
            },
            loadCachedFiles: _ => Task.FromResult<IReadOnlyList<InstalledModelFile>>(
                Interlocked.Increment(ref passes) == 1
                    ? [Installed(a, "PassA", "character")]
                    : [Installed(a, "PassB", "character")]));

        var passA = vm.InitializeAsync();
        await planningA.Task;

        await vm.RefreshCommand.ExecuteAsync(null); // pass B finishes while A sits in the planner

        releaseA.SetResult();
        await passA; // A now runs to completion on the success path

        vm.PreviewRoots.Select(n => n.Name).Should().Equal("PassB");
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task DeeplyNestedUnknownFilesAreEnumerated()
    {
        WriteLora(@"a\b\c\d\deep.safetensors");
        WriteLora(@"top.safetensors");
        var vm = CreateVm(cached: []);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(2);
    }

    [Fact]
    public async Task FilesCarryingTheHiddenOrSystemAttributeAreStillEnumerated()
    {
        // EnumerationOptions.AttributesToSkip defaults to Hidden | System and applies to FILES as
        // well as directories. A model file restored by a backup tool, copied off a NAS, or living
        // under a folder a sync client marked System silently never reached the preview — no
        // warning, no log line, and the skipped-file note stayed at 0 because nothing threw.
        // Directory.GetFiles, which this walk replaced, returned such entries.
        WriteLora(@"flat\normal.safetensors");
        var system = WriteLora(@"flat\system.safetensors");
        var hidden = WriteLora(@"flat\hidden.safetensors");
        File.SetAttributes(system, File.GetAttributes(system) | FileAttributes.System);
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var vm = CreateVm(cached: []);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(3);
        FlattenNames(vm.PreviewRoots).Should().Contain(["system.safetensors", "hidden.safetensors"]);
    }

    [Fact]
    public async Task DirectoryJunctionsAreNotFollowed()
    {
        // The walk skips reparse points, which is what stops a junction pointing at itself or an
        // ancestor from growing the enumeration without bound (a probe confirmed the unguarded
        // options happily produce "real\loop\real\loop\real\..." forever). Asserted here against a
        // junction to a *sibling* folder so a regression fails the test instead of hanging it.
        var real = WriteLora(@"real\x.safetensors");
        var outside = Path.Combine(_root.FullName, "Outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "hidden.safetensors"), "weights");

        var link = Path.Combine(SourceRoot, "link");
        CreateJunction(link, outside);
        Directory.Exists(link).Should().BeTrue("mklink /J needs no elevation and CI is windows-latest");

        var vm = CreateVm(cached: []);
        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        FlattenNames(vm.PreviewRoots).Should().NotContain("hidden.safetensors");
        real.Should().NotBeNull();
    }

    [Fact]
    public async Task AnInaccessibleSubfolderStillYieldsAPartialPreview()
    {
        // A permission-denied subtree must cost that subtree, not the preview. The predecessor of
        // this test never actually created an inaccessible folder, so the guarantee rested purely
        // on EnumerationOptions.IgnoreInaccessible with no local coverage. This one denies the
        // current user read access for real, via icacls.
        var reachable = WriteLora(@"open\reachable.safetensors");
        var denied = WriteLora(@"locked\secret.safetensors");
        var deniedDir = Path.GetDirectoryName(denied)!;
        var user = Environment.UserName;

        // xunit 2.x has no runtime Assert.Skip, so an unusable environment is reported by logging
        // the reason and returning. What must NOT depend on the environment is the cleanup: every
        // path after the ACE is applied — including the "the deny did not bite" bail-out — has to
        // remove it again, or an elevated run leaves a permanently denied temp tree behind that
        // Dispose() cannot delete either.
        if (!OperatingSystem.IsWindows())
        {
            Skipped("icacls is Windows-only; the walk's behaviour is asserted on the CI platform.");
            return;
        }

        if (!RunIcacls($"\"{deniedDir}\" /deny \"{user}:(OI)(CI)(RX)\""))
        {
            // icacls itself failed, so no ACE was applied and there is nothing to undo.
            Skipped($"icacls could not deny {user} access to {deniedDir}.");
            return;
        }

        try
        {
            if (!IsInaccessible(deniedDir))
            {
                // Elevated sessions keep access through the Administrators ACE, so the deny cannot
                // be proven to bite here. Skipping beats asserting something the environment did
                // not do — but the ACE still comes off in the finally below.
                Skipped($"the deny ACE on {deniedDir} does not bite in this session (elevated?).");
                return;
            }

            var vm = CreateVm(cached: []);

            await vm.InitializeAsync();

            vm.TransferCount.Should().Be(1);
            FlattenNames(vm.PreviewRoots).Should().Contain("reachable.safetensors");
            FlattenNames(vm.PreviewRoots).Should().NotContain("secret.safetensors");
            vm.StatusMessage.Should().NotContain("Preview failed");
            reachable.Should().NotBeNull();
        }
        finally
        {
            RunIcacls($"\"{deniedDir}\" /remove:d \"{user}\"");
        }
    }

    /// <summary>Reports an environment-driven skip; xunit 2.x cannot skip a running test.</summary>
    private static void Skipped(string reason)
        => Console.WriteLine($"[skipped] {reason}");

    private static bool IsInaccessible(string directory)
    {
        try
        {
            Directory.EnumerateFiles(directory).ToList();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool RunIcacls(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "icacls.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        return process is not null && process.WaitForExit(30_000) && process.ExitCode == 0;
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        process.WaitForExit();
    }

    [Fact]
    public async Task UnknowableFreeSpaceOnAnExistingTargetFailsOpenInsteadOfBlockingTheRun()
    {
        // new DriveInfo(@"\\nas\share\") throws ArgumentException and there is no free-space
        // number to give for a UNC target — but the folder is right there, so the run may proceed.
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")],
            getAvailableSpace: _ => throw new ArgumentException("Drive name must be a root directory."));

        await vm.InitializeAsync();

        vm.HasEnoughSpace.Should().BeTrue();
        vm.DiskSummary.Should().Contain("unknown");
        vm.BlockReason.Should().BeNull();
        vm.StartSortingCommand.CanExecute(null).Should().BeTrue();
        vm.StatusMessage.Should().NotContain("Preview failed");
    }

    [Theory]
    // An unplugged Z:\ throws DriveNotFoundException (an IOException). Failing open on it armed
    // Start, and the executor then threw on CreateDirectory for every single file:
    // "Done: 0 sorted, 0 duplicates skipped, 412 failed." Unreachable is not unknowable.
    [InlineData(typeof(DriveNotFoundException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task AnUnreachableTargetBlocksTheRunWithAStatedReason(Type exceptionType)
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")],
            getAvailableSpace: _ => throw (Exception)Activator.CreateInstance(exceptionType)!);

        await vm.InitializeAsync();

        vm.HasEnoughSpace.Should().BeFalse();
        vm.BlockReason.Should().Contain("not reachable");
        vm.StartSortingCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task AMissingTargetFolderBlocksEvenWhenTheProbeIsUnanswerable()
    {
        // Same ArgumentException as the UNC case, but the target root does not exist — there is
        // nothing to sort into, so this must not inherit the UNC fail-open.
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")],
            getAvailableSpace: _ => throw new ArgumentException("Drive name must be a root directory."));
        vm.CustomTargetFolder = Path.Combine(_root.FullName, "NoSuchTarget");

        await vm.InitializeAsync();

        vm.HasEnoughSpace.Should().BeFalse();
        vm.BlockReason.Should().Contain("not reachable");
    }

    [Fact]
    public async Task SameVolumeMoveIsNotBlockedByTheOneGigabyteMargin()
    {
        // A same-volume move is a directory-entry rename — RequiredBytes is 0, so the margin
        // must not apply. Otherwise the primary use case (reorganizing in place on the near-full
        // drive the library lives on) reports "Not enough free space".
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(freeSpace: 1_000, cached: [Installed(a, "SDXL 1.0", "character")]);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        vm.HasEnoughSpace.Should().BeTrue();
        vm.BlockReason.Should().BeNull();
        vm.StartSortingCommand.CanExecute(null).Should().BeTrue();
    }

    [Theory]
    // A dedicated LoRA drive as the source: Path.TrimEndingDirectorySeparator deliberately does NOT
    // trim a root path, so the old boundary check read the 'L' of "E:\Loras" instead of a separator
    // and declared every file on the drive to be outside it.
    [InlineData(@"E:\Loras\a.safetensors", @"E:\", true)]
    [InlineData(@"E:\a.safetensors", @"E:\", true)]
    [InlineData(@"E:\", @"E:\", true)]
    // Prefix-sharing siblings still must not match.
    [InlineData(@"E:\Loras_backup\b.safetensors", @"E:\Loras", false)]
    [InlineData(@"E:\Loras\a.safetensors", @"E:\Loras", true)]
    // Trailing separators on either input are irrelevant.
    [InlineData(@"E:\Loras\a.safetensors", @"E:\Loras\", true)]
    [InlineData(@"E:\Loras", @"E:\Loras\", true)]
    [InlineData(@"E:\Loras\", @"E:\Loras", true)]
    [InlineData(@"E:\Other\a.safetensors", @"E:\Loras", false)]
    public void IsWithinHandlesDriveRootsAndPrefixSiblings(string path, string root, bool expected)
        => LoraSorterViewModel.IsWithin(path, root).Should().Be(expected);

    [Fact]
    public async Task PreviewAndRunReportTimedStepsToTheUnifiedConsole()
    {
        // Standing project rule: every step reports to the Unified Console WITH timings, so an
        // exported log can tell "slow" from "hung" by which step last succeeded. Before this the
        // slowest loop in the feature reported only into BusyMessage, and the branch had no
        // Stopwatch anywhere.
        var lines = new List<string>();
        var logger = new Mock<IUnifiedLogger>();
        logger.Setup(l => l.Info(LogCategory.FileSystem, "LoraSorter", It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((LogCategory _, string _, string message, string? _) => lines.Add(message));

        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")], logger: logger.Object);
        vm.DialogService = ConfirmingDialogService();

        await vm.InitializeAsync();
        await vm.StartSortingCommand.ExecuteAsync(null);

        lines.Should().Contain(l => l.StartsWith("Resolving candidates under"));
        lines.Should().Contain(l => l.StartsWith("Candidate resolution finished:") && l.Contains(" ms"));
        lines.Should().Contain(l => l.StartsWith("Plan built for") && l.Contains(" ms:"));
        lines.Should().Contain(l => l.StartsWith("Sort started:"));
        lines.Should().Contain(l => l.StartsWith("Sort finished:") && l.Contains(" ms"));
    }

    // ------------------------------------------------------------------------------------------
    // Before → after preview. The pane draws the library as it is now beside the library as it
    // would be, and one click pairs a row with its counterpart. Both trees come out of the same
    // LoraSortPlan, so nothing here reads disk a second time.
    // ------------------------------------------------------------------------------------------

    /// <summary>The left tree is the folders the files are in today, nesting and all — not a
    /// re-derivation of anything, just the plan read from the other end.</summary>
    [Fact]
    public async Task TheSourceTreeMirrorsTheFoldersTheFilesAreInNow()
    {
        var a = WriteLora(@"unsorted\a.safetensors");
        var b = WriteLora(@"dump\new\b.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(a, "SDXL 1.0", "character"),
            Installed(b, "Pony", "style"),
        ]);

        await vm.InitializeAsync();

        vm.SourceRoots.Select(n => n.Name).Should().BeEquivalentTo(["unsorted", "dump"]);
        vm.SourceRoots.Single(n => n.Name == "dump").Children.Single().Name.Should().Be("new");
        FlattenNames(vm.SourceRoots).Should().Contain("b.safetensors");
    }

    /// <summary>
    /// A duplicate is skipped because an identical copy already sits at the destination, so nothing
    /// arrives and the target tree rightly has nothing to draw. The file is still on the user's
    /// disk though, and the "now" side is the one side that can say so.
    /// </summary>
    [Fact]
    public async Task ASkippedDuplicateAppearsOnTheSourceSideOnly()
    {
        var first = WriteLora(@"x\V1.safetensors");
        var second = WriteLora(@"y\V1.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(first, "SDXL 1.0", "character"),
            Installed(second, "SDXL 1.0", "character"),
        ]);

        await vm.InitializeAsync();

        var skipped = vm.SourceRoots.SelectMany(Flatten).Where(n => n.IsSkippedDuplicate).ToList();
        skipped.Should().ContainSingle().Which.Note.Should().Be("duplicate — skipped");
        vm.PreviewRoots.SelectMany(Flatten).Count(n => n.IsFile).Should().Be(1,
            "only the copy that actually arrives belongs on the destination side");
    }

    /// <summary>A file that is already where it belongs is dimmed on both sides — the source row
    /// has to explain why it is not in the leaving count.</summary>
    [Fact]
    public async Task AFileThatIsAlreadyWhereItBelongsSaysSoOnTheSourceSide()
    {
        var inPlace = WriteLora(@"SDXL 1.0\Character\a.safetensors");
        var vm = CreateVm(cached: [Installed(inPlace, "SDXL 1.0", "character")]);

        await vm.InitializeAsync();

        var node = vm.SourceRoots.SelectMany(Flatten).Single(n => n.IsFile);
        node.IsAlreadyInPlace.Should().BeTrue();
        node.Note.Should().Be("already here");
    }

    /// <summary>
    /// "all N leave", never "empties". The plan knows about model files and their sidecars, not
    /// whatever else is in that folder, and <i>Delete empty source folders</i> only removes
    /// directories that are genuinely empty when it runs — so a folder emptying is not something
    /// this preview is in a position to promise.
    /// </summary>
    [Fact]
    public async Task ASourceFolderWhoseFilesAllLeaveSaysSoWithoutPromisingItEmpties()
    {
        var a = WriteLora(@"unsorted\a.safetensors");
        var b = WriteLora(@"unsorted\b.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(a, "SDXL 1.0", "character"),
            Installed(b, "Pony", "style"),
        ]);

        await vm.InitializeAsync();

        var note = vm.SourceRoots.Single(n => n.Name == "unsorted").Note;
        note.Should().Be("all 2 leave");
        note.Should().NotContain("empt");
    }

    /// <summary>A folder losing some of its files says how many, and one losing none says that
    /// instead — the count is the whole point of the line.</summary>
    [Fact]
    public async Task ASourceFolderThatOnlyPartlyEmptiesSaysHowMany()
    {
        var stays = WriteLora(@"SDXL 1.0\Character\stays.safetensors");
        var goes = WriteLora(@"SDXL 1.0\Character\goes.safetensors");
        var untouched = WriteLora(@"Pony\Style\settled.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(stays, "SDXL 1.0", "character"),
            Installed(goes, "Pony", "style"),
            Installed(untouched, "Pony", "style"),
        ]);

        await vm.InitializeAsync();

        Note(vm.SourceRoots, "SDXL 1.0", "Character").Should().Be("1 of 2 leave");
        Note(vm.SourceRoots, "Pony", "Style").Should().Be("1 file stays");
    }

    private static string? Note(IEnumerable<SortPreviewNodeViewModel> roots, string root, string child)
        => roots.Single(n => n.Name == root).Children.Single(n => n.Name == child).Note;

    /// <summary>The pairing itself: click a file, its counterpart lights, and the folders above it
    /// open so the lit row is somewhere a user can actually see.</summary>
    [Fact]
    public async Task SelectingASourceFileLightsTheDestinationItLandsIn()
    {
        var a = WriteLora(@"unsorted\a.safetensors");
        var b = WriteLora(@"unsorted\b.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(a, "SDXL 1.0", "character"),
            Installed(b, "Pony", "style"),
        ]);
        await vm.InitializeAsync();

        var source = vm.SourceRoots.SelectMany(Flatten).Single(n => n.Name == "a.safetensors");
        vm.SelectPreviewNodeCommand.Execute(source);

        source.IsSelected.Should().BeTrue();
        var lit = vm.PreviewRoots.SelectMany(Flatten).Where(n => n.IsLinked).ToList();
        lit.Should().ContainSingle().Which.Name.Should().Be("a.safetensors");
        vm.PreviewRoots.Single(n => n.Name == "SDXL 1.0").IsExpanded.Should()
            .BeTrue("a highlight inside a collapsed folder is a highlight nobody sees");
    }

    /// <summary>Clicking a folder answers "where does all of this go?" — every destination lights,
    /// but only one is the scroll target, because scrolling to twelve rows means scrolling to
    /// none of them.</summary>
    [Fact]
    public async Task SelectingASourceFolderLightsEveryDestinationItsFilesReach()
    {
        var a = WriteLora(@"unsorted\a.safetensors");
        var b = WriteLora(@"unsorted\b.safetensors");
        var c = WriteLora(@"unsorted\c.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(a, "SDXL 1.0", "character"),
            Installed(b, "Pony", "style"),
            Installed(c, "Illustrious", "concept"),
        ]);
        await vm.InitializeAsync();

        vm.SelectPreviewNodeCommand.Execute(vm.SourceRoots.Single(n => n.Name == "unsorted"));

        var lit = vm.PreviewRoots.SelectMany(Flatten).Where(n => n.IsLinked).ToList();
        lit.Should().HaveCount(3);
        lit.Count(n => n.IsPrimaryLink).Should().Be(1);
    }

    /// <summary>The link runs both ways: a destination row can say where its file is coming from.</summary>
    [Fact]
    public async Task SelectingADestinationFileLightsWhereItComesFrom()
    {
        var a = WriteLora(@"dump\new\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();

        var target = vm.PreviewRoots.SelectMany(Flatten).Single(n => n.IsFile);
        vm.SelectPreviewNodeCommand.Execute(target);

        vm.SourceRoots.SelectMany(Flatten).Where(n => n.IsLinked)
            .Should().ContainSingle().Which.Name.Should().Be("a.safetensors");
        vm.SourceRoots.Single(n => n.Name == "dump").IsExpanded.Should().BeTrue();
    }

    /// <summary>One link at a time. A second click leaves no trace of the first, or the pane
    /// accumulates highlights until it means nothing.</summary>
    [Fact]
    public async Task ASecondSelectionClearsTheFirst()
    {
        var a = WriteLora(@"unsorted\a.safetensors");
        var b = WriteLora(@"unsorted\b.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(a, "SDXL 1.0", "character"),
            Installed(b, "Pony", "style"),
        ]);
        await vm.InitializeAsync();

        var files = vm.SourceRoots.SelectMany(Flatten).Where(n => n.IsFile).ToList();
        vm.SelectPreviewNodeCommand.Execute(files.Single(n => n.Name == "a.safetensors"));
        vm.SelectPreviewNodeCommand.Execute(files.Single(n => n.Name == "b.safetensors"));

        files.Single(n => n.Name == "a.safetensors").IsSelected.Should().BeFalse();
        vm.PreviewRoots.SelectMany(Flatten).Where(n => n.IsLinked)
            .Should().ContainSingle().Which.Name.Should().Be("b.safetensors");
    }

    /// <summary>
    /// A re-plan invalidates every pairing that was on screen — the nodes the highlight referred to
    /// no longer exist. Ticking <i>Sort by name</i> is the cheapest way to reach that: it re-plans
    /// from the cached candidates without touching disk.
    /// </summary>
    [Fact]
    public async Task RePlanningClearsTheLink()
    {
        var a = WriteLora(@"unsorted\MyChar_Pony_v2.safetensors");
        var vm = CreateVm(cached: [Installed(a, "???", "character")]);
        await vm.InitializeAsync();

        vm.SelectPreviewNodeCommand.Execute(vm.SourceRoots.Single(n => n.Name == "unsorted"));
        vm.PreviewRoots.SelectMany(Flatten).Should().Contain(n => n.IsLinked);

        vm.GuessBaseModelFromFileName = true;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.SourceRoots.SelectMany(Flatten).Should().NotContain(n => n.IsSelected);
        vm.PreviewRoots.SelectMany(Flatten).Should().NotContain(n => n.IsLinked || n.IsPrimaryLink);
    }

    /// <summary>The source tree is plan state like everything else Start depends on: a failed pass
    /// that clears the destination tree must not leave the other half of the picture standing.</summary>
    [Fact]
    public async Task DeselectingTheSourceFolderClearsBothTrees()
    {
        var a = WriteLora(@"unsorted\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();
        vm.SourceRoots.Should().NotBeEmpty();

        vm.SelectedSourceFolder = null;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.SourceRoots.Should().BeEmpty();
        vm.PreviewRoots.Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------
    // Per-pane search. Each tree has its own box filtering only itself: a library of a few
    // thousand LoRAs is not something you audit by scrolling, and the two panes are asked
    // different questions ("where is this file now?" against "what is landing in Unknown?").
    // ------------------------------------------------------------------------------------------

    /// <summary>The basic promise: type, and what is left is what matched.</summary>
    [Fact]
    public async Task FilteringAPaneHidesTheRowsThatDoNotMatch()
    {
        var vm = await ThreeFileVm();

        vm.SourceFilter.Text = "keep";

        var files = vm.SourceRoots.SelectMany(Flatten).Where(n => n.IsFile).ToList();
        files.Single(n => n.Name == "keep-me.safetensors").IsVisible.Should().BeTrue();
        files.Where(n => n.Name != "keep-me.safetensors").Should().OnlyContain(n => !n.IsVisible);
    }

    /// <summary>Matching a folder is a way of asking for the folder, so everything under it stays —
    /// otherwise typing a base-model name would return an empty folder.</summary>
    [Fact]
    public async Task AFolderThatMatchesKeepsEverythingUnderIt()
    {
        var vm = await ThreeFileVm();

        vm.SourceFilter.Text = "unsorted";

        vm.SourceRoots.Single(n => n.Name == "unsorted").Children
            .Should().OnlyContain(n => n.IsVisible, "the folder itself is what was asked for");
        // The other half of the claim: a folder match is still a filter, not a no-op.
        vm.SourceRoots.Single(n => n.Name == "settled").IsVisible.Should().BeFalse();
        vm.SourceFilter.Summary.Should().Be("2 of 3 files");
    }

    /// <summary>A match inside a collapsed folder is a match nobody sees — but a folder that matched
    /// on its own name is already the answer, so it is left as the user had it.</summary>
    [Fact]
    public async Task FoldersOpenForAMatchBeneathThemButNotForOneOnThemselves()
    {
        var vm = await ThreeFileVm();

        vm.SourceFilter.Text = "keep";
        vm.SourceRoots.Single(n => n.Name == "unsorted").IsExpanded.Should().BeTrue();

        vm.SourceFilter.Text = "unsorted";
        vm.SourceRoots.Single(n => n.Name == "unsorted").IsExpanded.Should()
            .BeFalse("the folder row itself is the match — there is nothing hidden to reveal");
    }

    /// <summary>The count is the point of the box on a library you cannot eyeball.</summary>
    [Fact]
    public async Task TheFilterSaysHowManyFilesItKept()
    {
        var vm = await ThreeFileVm();

        vm.SourceFilter.Summary.Should().BeNull("an unfiltered pane has no count worth showing");

        vm.SourceFilter.Text = "keep";

        vm.SourceFilter.Summary.Should().Be("1 of 3 files");
        vm.SourceFilter.HasNoMatches.Should().BeFalse();
    }

    /// <summary>An empty tree under a box with text in it reads as a broken preview unless the pane
    /// says otherwise.</summary>
    [Fact]
    public async Task AFilterThatMatchesNothingSaysSo()
    {
        var vm = await ThreeFileVm();

        vm.SourceFilter.Text = "nothing-is-called-this";

        vm.SourceFilter.HasNoMatches.Should().BeTrue();
        vm.SourceFilter.Summary.Should().Be("0 of 3 files");
        vm.SourceRoots.SelectMany(Flatten).Should().OnlyContain(n => !n.IsVisible);
    }

    /// <summary>Clearing puts the tree back — including the folders the search opened, which the
    /// user never asked to have open.</summary>
    [Fact]
    public async Task ClearingTheFilterRestoresEveryRowAndTheExpansionItFound()
    {
        var vm = await ThreeFileVm();
        vm.SourceFilter.Text = "keep";
        // Stated up front so the restore below is a restore of something.
        vm.SourceRoots.SelectMany(Flatten).Should().Contain(n => !n.IsVisible);
        vm.SourceRoots.Single(n => n.Name == "unsorted").IsExpanded.Should().BeTrue();

        vm.SourceFilter.ClearCommand.Execute(null);

        vm.SourceFilter.Text.Should().BeNullOrEmpty();
        vm.SourceFilter.Summary.Should().BeNull();
        vm.SourceRoots.SelectMany(Flatten).Should().OnlyContain(n => n.IsVisible);
        vm.SourceRoots.Single(n => n.Name == "unsorted").IsExpanded.Should().BeFalse();
    }

    /// <summary>
    /// A re-plan rebuilds both trees from scratch. Dropping the text the user typed would mean
    /// every option toggle silently un-filters the pane they were reading.
    /// </summary>
    [Fact]
    public async Task TheFilterSurvivesARePlanAndAppliesToTheNewTree()
    {
        var vm = await ThreeFileVm();
        vm.SourceFilter.Text = "keep";

        vm.IncludeCategory = false;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.SourceFilter.Text.Should().Be("keep");
        vm.SourceFilter.Summary.Should().Be("1 of 3 files");
        vm.SourceRoots.SelectMany(Flatten).Where(n => n.IsFile)
            .Where(n => n.IsVisible).Should().ContainSingle().Which.Name.Should().Be("keep-me.safetensors");
    }

    /// <summary>Two boxes, two questions. Neither pane may answer for the other.</summary>
    [Fact]
    public async Task TheTwoPanesFilterIndependently()
    {
        var vm = await ThreeFileVm();

        vm.SourceFilter.Text = "keep";

        vm.SourceRoots.SelectMany(Flatten).Should().Contain(n => !n.IsVisible,
            "the pane the box belongs to did filter");
        vm.PreviewRoots.SelectMany(Flatten).Should().OnlyContain(n => n.IsVisible);
        vm.TargetFilter.Summary.Should().BeNull();
    }

    /// <summary>Three files across two source folders, headed for three different destinations.</summary>
    private async Task<LoraSorterViewModel> ThreeFileVm()
    {
        var keep = WriteLora(@"unsorted\keep-me.safetensors");
        var other = WriteLora(@"unsorted\other.safetensors");
        var third = WriteLora(@"settled\third.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(keep, "SDXL 1.0", "character"),
            Installed(other, "Pony", "style"),
            Installed(third, "Illustrious", "concept"),
        ]);
        await vm.InitializeAsync();
        return vm;
    }

    // ------------------------------------------------------------------------------------------
    // Open in folder, row ordering, and the geometry the two of them are read in.
    // ------------------------------------------------------------------------------------------

    /// <summary>A source file is somewhere the user can go and look at right now.</summary>
    [Fact]
    public async Task OpeningASourceFileSelectsItInExplorer()
    {
        var a = WriteLora(@"unsorted\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        var launcher = new Mock<IProcessLauncher>();
        vm.ProcessLauncher = launcher.Object;
        await vm.InitializeAsync();

        vm.OpenInFolderCommand.Execute(vm.SourceRoots.SelectMany(Flatten).Single(n => n.IsFile));

        launcher.Verify(l => l.OpenFolderAndSelectFile(a), Times.Once);
    }

    /// <summary>A folder row opens the folder itself — there is no one file to select.</summary>
    [Fact]
    public async Task OpeningASourceFolderOpensThatFolder()
    {
        var a = WriteLora(@"unsorted\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        var launcher = new Mock<IProcessLauncher>();
        vm.ProcessLauncher = launcher.Object;
        await vm.InitializeAsync();

        vm.OpenInFolderCommand.Execute(vm.SourceRoots.Single(n => n.Name == "unsorted"));

        launcher.Verify(l => l.OpenFolder(Path.GetDirectoryName(a)!), Times.Once);
    }

    /// <summary>
    /// The destination tree describes folders that mostly do not exist yet — nothing has been
    /// sorted. Opening the nearest thing that does exist would take the user somewhere they did not
    /// click, so the row simply says it cannot be opened.
    /// </summary>
    [Fact]
    public async Task ADestinationFolderThatDoesNotExistYetCannotBeOpened()
    {
        var a = WriteLora(@"unsorted\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        var launcher = new Mock<IProcessLauncher>();
        vm.ProcessLauncher = launcher.Object;
        await vm.InitializeAsync();

        var destination = vm.PreviewRoots.Single(n => n.Name == "SDXL 1.0");
        var source = vm.SourceRoots.Single(n => n.Name == "unsorted");

        // Stated as a pair, because "nothing happened" is what a menu item that does not work
        // looks like too: the same click on the row beside it must open something.
        destination.CanOpenInFolder.Should().BeFalse();
        source.CanOpenInFolder.Should().BeTrue();

        vm.OpenInFolderCommand.Execute(destination);
        launcher.VerifyNoOtherCalls();

        vm.OpenInFolderCommand.Execute(source);
        launcher.Verify(l => l.OpenFolder(Path.GetDirectoryName(a)!), Times.Once);
    }

    /// <summary>
    /// Sorting into a folder the library already has is the common case, and there "show me where
    /// this lands" is answerable before anything moves.
    /// </summary>
    [Fact]
    public async Task ADestinationFileFallsBackToTheFolderItWillLandIn()
    {
        var a = WriteLora(@"unsorted\a.safetensors");
        var destinationFolder = Path.Combine(SourceRoot, "SDXL 1.0", "Character");
        Directory.CreateDirectory(destinationFolder);
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        var launcher = new Mock<IProcessLauncher>();
        vm.ProcessLauncher = launcher.Object;
        await vm.InitializeAsync();

        var landing = vm.PreviewRoots.SelectMany(Flatten).Single(n => n.IsFile);
        landing.CanOpenInFolder.Should().BeTrue("the file is not there yet, but its folder is");

        vm.OpenInFolderCommand.Execute(landing);

        launcher.Verify(l => l.OpenFolder(destinationFolder), Times.Once);
    }

    /// <summary>
    /// Only the top-level folders were ever ordered; everything below them came out in whatever
    /// order the plan happened to produce, which is why a deep tree read as arbitrary.
    /// </summary>
    [Fact]
    public async Task SortingByNameOrdersEveryLevelNotJustTheRoots()
    {
        var vm = await SizedVm();

        vm.PreviewSortOrder = PreviewSortOrder.Name;

        vm.SourceRoots.Select(n => n.Name).Should().ContainInOrder("alpha", "unsorted");
        vm.SourceRoots.Single(n => n.Name == "unsorted").Children.Select(n => n.Name)
            .Should().ContainInOrder("a.safetensors", "b.safetensors", "c.safetensors");
    }

    /// <summary>Biggest first, at every level — the order that answers "what is taking up the
    /// space", and the one the pane has always used for its roots.</summary>
    [Fact]
    public async Task SortingBySizePutsTheBiggestFirstAtEveryLevel()
    {
        var vm = await SizedVm();
        vm.PreviewSortOrder = PreviewSortOrder.Name;

        vm.PreviewSortOrder = PreviewSortOrder.Size;

        vm.SourceRoots.Select(n => n.Name).Should().ContainInOrder("unsorted", "alpha");
        // b, a, c by size — where the plan produced them in the order b, c, a, so this cannot pass
        // by accident on insertion order.
        vm.SourceRoots.Single(n => n.Name == "unsorted").Children.Select(n => n.Name)
            .Should().ContainInOrder("b.safetensors", "a.safetensors", "c.safetensors");
    }

    /// <summary>
    /// Re-ordering is not re-planning: the same node objects are moved around, so a click-to-link
    /// highlight and a typed search filter both survive changing the order.
    /// </summary>
    [Fact]
    public async Task ChangingTheOrderKeepsTheRowsAndWhateverStateTheyCarry()
    {
        var vm = await SizedVm();
        var before = vm.SourceRoots.SelectMany(Flatten).Single(n => n.Name == "a.safetensors");
        vm.SelectPreviewNodeCommand.Execute(before);

        vm.PreviewSortOrder = PreviewSortOrder.Name;

        // The order did change — otherwise "the rows survived" is a claim about nothing.
        vm.SourceRoots.First().Name.Should().Be("alpha");
        vm.SourceRoots.SelectMany(Flatten).Single(n => n.Name == "a.safetensors")
            .Should().BeSameAs(before);
        before.IsSelected.Should().BeTrue();
        vm.PreviewRoots.SelectMany(Flatten).Should().Contain(n => n.IsLinked);
    }

    /// <summary>
    /// Depth is what indents a row's own name instead of the container holding its children —
    /// indenting the container moved every row's right edge in too, which left the chips, marks and
    /// sizes ragged down the pane. The path is what the context menu opens.
    /// </summary>
    [Fact]
    public async Task EveryRowKnowsItsDepthAndWhereItIsOnDisk()
    {
        var a = WriteLora(@"dump\new\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();

        var dump = vm.SourceRoots.Single(n => n.Name == "dump");
        var nested = dump.Children.Single();
        var file = nested.Children.Single();

        dump.Depth.Should().Be(0);
        nested.Depth.Should().Be(1);
        file.Depth.Should().Be(2);

        dump.FullPath.Should().Be(Path.Combine(SourceRoot, "dump"));
        nested.FullPath.Should().Be(Path.Combine(SourceRoot, "dump", "new"));
        file.FullPath.Should().Be(a);
        vm.PreviewRoots.SelectMany(Flatten).Single(n => n.IsFile).FullPath
            .Should().Be(Path.Combine(SourceRoot, "SDXL 1.0", "Character", "a.safetensors"));
    }

    // ------------------------------------------------------------------------------------------
    // Ignoring files that are already where they belong. On a settled library those are most of
    // the rows, and a preview that is mostly things which are not going to happen is hard to read.
    // ------------------------------------------------------------------------------------------

    /// <summary>Ignore means gone — from both sides, so the panes still describe the same set.</summary>
    [Fact]
    public async Task IgnoringSettledFilesDropsThemFromBothTrees()
    {
        var vm = await SettledVm();

        vm.IgnoreFilesAlreadyInPlace = true;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        FlattenNames(vm.SourceRoots).Should().Contain("mover.safetensors").And.NotContain("settled.safetensors");
        FlattenNames(vm.PreviewRoots).Should().Contain("mover.safetensors").And.NotContain("settled.safetensors");
    }

    /// <summary>A folder holding nothing but settled files has nothing left to say.</summary>
    [Fact]
    public async Task AFolderWhoseFilesAreAllSettledDisappearsWithThem()
    {
        var vm = await SettledVm();
        vm.SourceRoots.Select(n => n.Name).Should().Contain("Pony");

        vm.IgnoreFilesAlreadyInPlace = true;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.SourceRoots.Select(n => n.Name).Should().NotContain("Pony");
    }

    /// <summary>
    /// The folder note counts the rows that are actually there. Keeping the hidden files in the
    /// denominator would have it describe a folder the pane is no longer drawing.
    /// </summary>
    [Fact]
    public async Task TheFolderNoteCountsOnlyTheRowsStillShown()
    {
        var vm = await SettledVm();
        Note(vm.SourceRoots, "SDXL 1.0", "Character").Should().Be("1 of 2 leave");

        vm.IgnoreFilesAlreadyInPlace = true;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        Note(vm.SourceRoots, "SDXL 1.0", "Character").Should().Be("1 file leaves");
    }

    /// <summary>
    /// The count of what was hidden is the one place the denominator survives, so the pane never
    /// silently claims a library is smaller than it is.
    /// </summary>
    [Fact]
    public async Task TheSummarySaysTheSettledFilesWereHiddenRatherThanDroppingThem()
    {
        var vm = await SettledVm();
        vm.PreviewSummary.Should().Contain("2 already in place").And.NotContain("hidden");

        vm.IgnoreFilesAlreadyInPlace = true;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.PreviewSummary.Should().Contain("2 already in place (hidden)");
    }

    /// <summary>
    /// This hides rows; it must not change a single byte of what the run does. A settled file was
    /// never going to be touched — that is what settled means.
    /// </summary>
    [Fact]
    public async Task IgnoringChangesTheViewAndNothingElse()
    {
        var vm = await SettledVm();
        var before = vm.TransferCount;
        var rowsBefore = vm.SourceRoots.SelectMany(Flatten).Count(n => n.IsFile);

        vm.IgnoreFilesAlreadyInPlace = true;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        // Paired on purpose: "the run did not change" is only a claim about something if the view
        // demonstrably did.
        vm.SourceRoots.SelectMany(Flatten).Count(n => n.IsFile).Should().BeLessThan(rowsBefore);
        vm.TransferCount.Should().Be(before);
        vm.CanStart.Should().BeTrue();
    }

    /// <summary>Two settled files and one mover, so hiding is visible at file, folder and note level.</summary>
    private async Task<LoraSorterViewModel> SettledVm()
    {
        var settled = WriteLora(@"SDXL 1.0\Character\settled.safetensors");
        var mover = WriteLora(@"SDXL 1.0\Character\mover.safetensors");
        var alsoSettled = WriteLora(@"Pony\Style\quiet.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(settled, "SDXL 1.0", "character"),
            Installed(mover, "Illustrious", "concept"),
            Installed(alsoSettled, "Pony", "style"),
        ]);
        await vm.InitializeAsync();
        return vm;
    }

    /// <summary>The pane opens on the order the plan produced, having sorted nothing.</summary>
    [Fact]
    public async Task ThePaneStartsOnTheOrderThePlanProduced()
    {
        var vm = await SizedVm();

        vm.PreviewSortOrder.Should().Be(PreviewSortOrder.Default);
        vm.SourceRoots.Single(n => n.Name == "unsorted").Children.Select(n => n.Name)
            .Should().ContainInOrder("b.safetensors", "c.safetensors", "a.safetensors");
    }

    /// <summary>
    /// Default is a real third option, not just "whatever was there before the user touched the
    /// picker" — going back to it has to undo the sort, which means the plan's own order is
    /// remembered rather than recomputed.
    /// </summary>
    [Fact]
    public async Task ReturningToDefaultRestoresThePlanOrder()
    {
        var vm = await SizedVm();
        vm.PreviewSortOrder = PreviewSortOrder.Name;
        vm.SourceRoots.Single(n => n.Name == "unsorted").Children.Select(n => n.Name)
            .Should().ContainInOrder("a.safetensors", "b.safetensors", "c.safetensors");

        vm.PreviewSortOrder = PreviewSortOrder.Default;

        vm.SourceRoots.Single(n => n.Name == "unsorted").Children.Select(n => n.Name)
            .Should().ContainInOrder("b.safetensors", "c.safetensors", "a.safetensors");
    }

    /// <summary>
    /// Three orders that all disagree: the plan produces b, c, a; by size that is b, a, c; by name
    /// a, b, c. Nothing here can pass on insertion order by accident.
    /// </summary>
    private async Task<LoraSorterViewModel> SizedVm()
    {
        WriteSized(@"unsorted\b.safetensors", 100);
        WriteSized(@"unsorted\c.safetensors", 10);
        WriteSized(@"unsorted\a.safetensors", 50);
        WriteSized(@"alpha\d.safetensors", 5);
        var vm = CreateVm(cached:
        [
            Installed(Path.Combine(SourceRoot, @"unsorted\b.safetensors"), "SDXL 1.0", "character"),
            Installed(Path.Combine(SourceRoot, @"unsorted\c.safetensors"), "SDXL 1.0", "character"),
            Installed(Path.Combine(SourceRoot, @"unsorted\a.safetensors"), "SDXL 1.0", "character"),
            Installed(Path.Combine(SourceRoot, @"alpha\d.safetensors"), "Pony", "style"),
        ]);
        await vm.InitializeAsync();
        return vm;
    }

    private void WriteSized(string relative, int bytes)
    {
        var path = Path.Combine(SourceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public async Task ExcludingAFolderTakesItsFilesOutOfThePlanButKeepsThemDimmedInBothTrees()
    {
        var vm = await LightningVm();
        vm.TransferCount.Should().Be(2, "before excluding, the Lightning file sorts into Unknown");

        await vm.ExcludeFolderCommand.ExecuteAsync(vm.SourceRoots.Single(n => n.Name == "Lightning"));

        vm.TransferCount.Should().Be(1);
        var sourceRow = vm.SourceRoots.SelectMany(Flatten).Single(n => n.Name == "accel_high_noise.safetensors");
        sourceRow.IsExcluded.Should().BeTrue();
        sourceRow.IsDimmed.Should().BeTrue();
        // The file stays where it is, and "after sorting" says so: same folder, dimmed.
        var afterRow = vm.PreviewRoots.SelectMany(Flatten).Single(n => n.Name == "accel_high_noise.safetensors");
        afterRow.IsExcluded.Should().BeTrue();
        vm.PreviewRoots.Single(n => n.Name == "Lightning").IsExcluded.Should().BeTrue();
        FlattenNames(vm.PreviewRoots).Should().NotContain("Unknown");
    }

    [Fact]
    public async Task StartSortingLeavesAnExcludedFolderUntouched()
    {
        var vm = await LightningVm();
        vm.DialogService = ConfirmingDialogService();
        await vm.ExcludeFolderCommand.ExecuteAsync(vm.SourceRoots.Single(n => n.Name == "Lightning"));

        await vm.StartSortingCommand.ExecuteAsync(null);

        File.Exists(Path.Combine(SourceRoot, @"Lightning\accel_high_noise.safetensors"))
            .Should().BeTrue("excluded files are not the run's to move");
        Directory.Exists(Path.Combine(SourceRoot, "Unknown")).Should().BeFalse();
        File.Exists(Path.Combine(SourceRoot, @"SDXL 1.0\Character\mover.safetensors"))
            .Should().BeTrue("the rest of the plan still runs");
    }

    [Fact]
    public async Task SortingTheFolderAgainRestoresIt()
    {
        var vm = await LightningVm();
        await vm.ExcludeFolderCommand.ExecuteAsync(vm.SourceRoots.Single(n => n.Name == "Lightning"));
        vm.TransferCount.Should().Be(1);

        await vm.RemoveExclusionCommand.ExecuteAsync(Path.Combine(SourceRoot, "Lightning"));

        vm.TransferCount.Should().Be(2);
        vm.SourceRoots.SelectMany(Flatten).Should().OnlyContain(n => !n.IsExcluded);
        _settings.Verify(s => s.SetLoraSorterExcludedFoldersJsonAsync(null, It.IsAny<CancellationToken>()),
            Times.Once, "an emptied list clears the stored value instead of persisting []");
    }

    [Fact]
    public async Task TheSummaryCountsExcludedFiles()
    {
        var vm = await LightningVm();
        vm.PreviewSummary.Should().NotContain("excluded");

        await vm.ExcludeFolderCommand.ExecuteAsync(vm.SourceRoots.Single(n => n.Name == "Lightning"));

        vm.PreviewSummary.Should().Contain("1 excluded");
    }

    [Fact]
    public async Task ExclusionsArePersistedWhenAddedAndAppliedWhenLoaded()
    {
        var vm = await LightningVm();
        await vm.ExcludeFolderCommand.ExecuteAsync(vm.SourceRoots.Single(n => n.Name == "Lightning"));
        _settings.Verify(s => s.SetLoraSorterExcludedFoldersJsonAsync(
            It.Is<string?>(j => j != null && j.Contains("Lightning")), It.IsAny<CancellationToken>()), Times.Once);

        // A fresh VM finds the stored list and applies it before the first preview.
        _settings.Setup(s => s.GetLoraSorterExcludedFoldersJsonAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Text.Json.JsonSerializer.Serialize(new[] { Path.Combine(SourceRoot, "Lightning") }));
        var reloaded = CreateVm(cached: LightningLibrary());
        await reloaded.InitializeAsync();

        reloaded.ExcludedFolders.Should().ContainSingle(f => f.EndsWith("Lightning"));
        reloaded.HasExcludedFolders.Should().BeTrue();
        reloaded.TransferCount.Should().Be(1);
    }

    [Fact]
    public async Task ANameGuessInsideAnExcludedFolderNeitherMovesTheFileNorCountsInTheHint()
    {
        // A guessable name (the heuristic reads "Pony") inside the excluded folder: exclusion is
        // checked before the name rung gets a say, so the file neither moves nor pads the hint.
        var guessable = WriteLora(@"Lightning\MyChar_Pony_v2.safetensors");
        var vm = CreateVm(cached: [.. LightningLibrary(), Installed(guessable, "???", "character")]);
        await vm.InitializeAsync();
        await vm.ExcludeFolderCommand.ExecuteAsync(vm.SourceRoots.Single(n => n.Name == "Lightning"));

        vm.GuessBaseModelFromFileName = true;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        FlattenNames(vm.PreviewRoots).Should().NotContain("Pony");
        vm.NameGuessHint.Should().BeNull("the only guessable file is excluded, so there is nothing to offer");
        vm.TransferCount.Should().Be(1);
    }

    [Fact]
    public async Task HidingSettledFilesKeepsExcludedRowsVisible()
    {
        var settled = WriteLora(@"SDXL 1.0\Character\settled.safetensors");
        var vm = CreateVm(cached: [.. LightningLibrary(), Installed(settled, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();
        await vm.ExcludeFolderCommand.ExecuteAsync(vm.SourceRoots.Single(n => n.Name == "Lightning"));

        vm.IgnoreFilesAlreadyInPlace = true;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        // Hiding noise is one thing; hiding a choice the user made is another.
        FlattenNames(vm.SourceRoots).Should().Contain("accel_high_noise.safetensors").And.NotContain("settled.safetensors");
        FlattenNames(vm.PreviewRoots).Should().Contain("accel_high_noise.safetensors");
    }

    [Fact]
    public async Task AnExcludedUnidentifiedFileShowsNoMarkAndPoisonsNoAncestor()
    {
        var vm = await LightningVm();
        vm.SourceRoots.Single(n => n.Name == "Lightning").IsUnidentified
            .Should().BeTrue("before excluding, the placeholder row honestly reads ✗");

        await vm.ExcludeFolderCommand.ExecuteAsync(vm.SourceRoots.Single(n => n.Name == "Lightning"));

        var folder = vm.SourceRoots.Single(n => n.Name == "Lightning");
        folder.IsUnidentified.Should().BeFalse("a file the user excluded cannot be 'unfinished'");
        folder.Note.Should().Be("excluded — won't be sorted");
        var file = folder.Children.Single(n => n.IsFile);
        file.IsUnidentified.Should().BeFalse();
        file.IsIdentified.Should().BeFalse("an excluded row shows no mark at all");
    }

    [Fact]
    public async Task TheParentFolderNoteDoesNotCountExcludedFilesAsLeaving()
    {
        // Lightning nested under a parent that also holds a mover: the parent's note counts only
        // the file that is actually going somewhere.
        var mover = WriteLora(@"mixed\mover2.safetensors");
        var nested = WriteLora(@"mixed\Lightning\nested_accel.safetensors");
        var vm = CreateVm(cached: [Installed(mover, "SDXL 1.0", "character"), Installed(nested, "???", "character")]);
        await vm.InitializeAsync();

        var lightning = vm.SourceRoots.Single(n => n.Name == "mixed").Children.Single(n => n.Name == "Lightning");
        await vm.ExcludeFolderCommand.ExecuteAsync(lightning);

        vm.SourceRoots.Single(n => n.Name == "mixed").Note.Should().Be("1 of 2 leave");
    }

    [Fact]
    public async Task TheSearchFilterStillFindsExcludedRows()
    {
        var vm = await LightningVm();
        await vm.ExcludeFolderCommand.ExecuteAsync(vm.SourceRoots.Single(n => n.Name == "Lightning"));

        vm.SourceFilter.Text = "accel";

        vm.SourceRoots.SelectMany(Flatten).Single(n => n.Name == "accel_high_noise.safetensors")
            .IsVisible.Should().BeTrue();
    }

    [Fact]
    public async Task OnlySourceSideFoldersOfferExclusion()
    {
        var vm = await LightningVm();

        var sourceFolder = vm.SourceRoots.Single(n => n.Name == "Lightning");
        sourceFolder.CanExclude.Should().BeTrue();
        var sourceFile = sourceFolder.Children.Single(n => n.IsFile);
        sourceFile.CanExclude.Should().BeFalse();
        var destinationFolder = vm.PreviewRoots.First(n => !n.IsFile);
        destinationFolder.CanExclude.Should().BeFalse();

        // The commands enforce the same rule rather than trusting the menu to.
        await vm.ExcludeFolderCommand.ExecuteAsync(sourceFile);
        await vm.ExcludeFolderCommand.ExecuteAsync(destinationFolder);
        vm.ExcludedFolders.Should().BeEmpty();

        await vm.ExcludeFolderCommand.ExecuteAsync(sourceFolder);
        vm.SourceRoots.Single(n => n.Name == "Lightning").CanUnexclude.Should().BeTrue();
    }

    [Fact]
    public async Task HidingSettledFilesDoesNotMarkTheirFolderExcluded()
    {
        // A folder holding a settled file plus an excluded subfolder: hide-settled drops the
        // settled row from the tree, but the folder is not thereby "all excluded" — dimming it
        // and taking away its "Never sort this folder" menu would punish it for a view checkbox.
        var settled = WriteLora(@"SDXL 1.0\Character\settled.safetensors");
        var nested = WriteLora(@"SDXL 1.0\Character\Lightning\accel.safetensors");
        var mover = WriteLora(@"flat\mover.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(settled, "SDXL 1.0", "character"),
            Installed(nested, "???", "character"),
            Installed(mover, "Pony", "style"),
        ]);
        await vm.InitializeAsync();
        await vm.ExcludeFolderCommand.ExecuteAsync(
            vm.SourceRoots.SelectMany(Flatten).Single(n => n.Name == "Lightning"));

        vm.IgnoreFilesAlreadyInPlace = true;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        var character = vm.SourceRoots.Single(n => n.Name == "SDXL 1.0").Children.Single(n => n.Name == "Character");
        character.IsExcluded.Should().BeFalse("its settled file is hidden, not excluded");
        character.IsDimmed.Should().BeFalse();
        character.CanExclude.Should().BeTrue();
        character.Children.Single(n => n.Name == "Lightning").IsExcluded.Should().BeTrue();
        // Same blind spot on the destination side, where the folder would mis-dim.
        vm.PreviewRoots.Single(n => n.Name == "SDXL 1.0").Children.Single(n => n.Name == "Character")
            .IsExcluded.Should().BeFalse();
    }

    [Fact]
    public async Task RemovingAnExclusionForgivesTheStoredPathSpelling()
    {
        // The stored entry uses forward slashes (a yaml-registered source, a hand-synced settings
        // file); the context menu hands over the tree's backslash spelling. Equivalent paths must
        // un-exclude rather than silently no-op under a visible menu item.
        _settings.Setup(s => s.GetLoraSorterExcludedFoldersJsonAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Text.Json.JsonSerializer.Serialize(
                new[] { Path.Combine(SourceRoot, "Lightning").Replace('\\', '/') }));
        var vm = CreateVm(cached: LightningLibrary());
        await vm.InitializeAsync();
        vm.TransferCount.Should().Be(1, "the forward-slash entry still excludes the folder");

        await vm.RemoveExclusionCommand.ExecuteAsync(Path.Combine(SourceRoot, "Lightning"));

        vm.ExcludedFolders.Should().BeEmpty();
        vm.TransferCount.Should().Be(2);
    }

    [Fact]
    public async Task TogglingHideSettledRebuildsTheTreeWithoutReplanning()
    {
        // The checkbox is view-only: only the tree build and the summary read it. Re-running the
        // planner — lazy collision hashing and all — for a display toggle is the kind of "minutes
        // on a large library" work the candidate cache exists to avoid.
        var probes = 0;
        var settled = WriteLora(@"SDXL 1.0\Character\settled.safetensors");
        var mover = WriteLora(@"flat\mover3.safetensors");
        var vm = CreateVm(
            cached: [Installed(settled, "SDXL 1.0", "character"), Installed(mover, "SDXL 1.0", "character")],
            fileExistsOnDisk: path => { probes++; return File.Exists(path); });
        await vm.InitializeAsync();
        var probesAfterPlanning = probes;
        probesAfterPlanning.Should().BeGreaterThan(0, "sanity: planning consults the disk, so the counter can tell a re-plan");

        vm.IgnoreFilesAlreadyInPlace = true;

        // Synchronous view work from the plan already in hand: no awaited command, no disk probes.
        FlattenNames(vm.SourceRoots).Should().NotContain("settled.safetensors");
        vm.PreviewSummary.Should().Contain("(hidden)");
        probes.Should().Be(probesAfterPlanning, "hiding settled rows is a view change, not a re-plan");
    }

    /// <summary>One curated Lightning folder (placeholder row, unguessable name) plus one ordinary
    /// mover, so exclusion is visible in the plan, the trees and the summary.</summary>
    private InstalledModelFile[] LightningLibrary()
    {
        var accel = Path.Combine(SourceRoot, @"Lightning\accel_high_noise.safetensors");
        if (!File.Exists(accel)) WriteLora(@"Lightning\accel_high_noise.safetensors");
        var mover = Path.Combine(SourceRoot, @"flat\mover.safetensors");
        if (!File.Exists(mover)) WriteLora(@"flat\mover.safetensors");
        return
        [
            Installed(accel, "???", "character"),
            Installed(mover, "SDXL 1.0", "character"),
        ];
    }

    private async Task<LoraSorterViewModel> LightningVm()
    {
        var vm = CreateVm(cached: LightningLibrary());
        await vm.InitializeAsync();
        return vm;
    }

    private static IEnumerable<string> FlattenNames(IEnumerable<SortPreviewNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node.Name;
            foreach (var childName in FlattenNames(node.Children))
                yield return childName;
        }
    }
}
