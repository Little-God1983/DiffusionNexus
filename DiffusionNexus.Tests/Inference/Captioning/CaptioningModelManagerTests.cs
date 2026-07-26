using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Inference.Captioning;
using FluentAssertions;

namespace DiffusionNexus.Tests.Inference.Captioning;

/// <summary>
/// Unit tests for <see cref="CaptioningModelManager"/>. Uses temp directories
/// for real (but tiny) placeholder files, and the manager's
/// <c>fileSizeProbe</c> constructor seam to fake multi-gigabyte GGUF sizes for
/// the model/mmproj size-threshold checks — no fixture ever writes more than a
/// few bytes to disk. See issue #444: an earlier version of this file believed
/// <see cref="FileStream.SetLength"/> produced NTFS-sparse files; it does not
/// (sparseness requires an explicit <c>FSCTL_SET_SPARSE</c> control code that
/// .NET doesn't expose), so tests written that way allocated real multi-GB
/// files and failed with <see cref="IOException"/> on disk-constrained machines.
/// </summary>
public sealed class CaptioningModelManagerTests : IDisposable
{
    private readonly string _root;
    private readonly CaptioningModelManager _manager;

    public CaptioningModelManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dn-capmgr-" + Guid.NewGuid().ToString("N"));
        _manager = new CaptioningModelManager(_root, httpClient: null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Creates a real file at <paramref name="absolutePath"/> containing exactly
    /// <paramref name="length"/> bytes (default: empty). This is genuinely tiny
    /// real content — not a sparse-file trick — so callers that care about the
    /// file's actual on-disk size pass a small honest length (see the
    /// "default real-filesystem probe" test below); callers that only need the
    /// file to <em>exist</em> pair this with <see cref="FakeSizes"/> to fake out
    /// whatever logical size <see cref="CaptioningModelManager"/> should see.
    /// </summary>
    private static void CreateFile(string absolutePath, int length = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllBytes(absolutePath, new byte[length]);
    }

    /// <summary>
    /// Builds a <c>fileSizeProbe</c> delegate for the <see cref="CaptioningModelManager"/>
    /// test seam: reports the given fake length for each listed path (case-insensitive)
    /// and falls back to the real <see cref="FileInfo.Length"/> for anything else. Lets a
    /// test assert size-threshold behaviour (e.g. the 80% corruption check, or the
    /// "already downloaded" short-circuit) against a real-but-empty placeholder file
    /// instead of writing gigabytes of real content.
    /// </summary>
    private static Func<string, long> FakeSizes(params (string Path, long Length)[] sizes)
    {
        var map = sizes.ToDictionary(s => s.Path, s => s.Length, StringComparer.OrdinalIgnoreCase);
        return path => map.TryGetValue(path, out var length) ? length : new FileInfo(path).Length;
    }

    #region Constructor

    [Fact]
    public void Ctor_CreatesModelsDirectory()
    {
        var path = Path.Combine(_root, "models-fresh");
        Directory.Exists(path).Should().BeFalse();

        _ = new CaptioningModelManager(path, httpClient: null);

        Directory.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Ctor_DefaultPath_DoesNotThrow()
    {
        // Default ctor must build a usable manager without arguments.
        // Exercising it covers the default-folder branch (LocalAppData).
        var manager = new CaptioningModelManager();
        manager.SearchPaths.Should().NotBeEmpty();
    }

    [Fact]
    public void SearchPaths_StartsWithBasePath()
    {
        _manager.SearchPaths[0].Should().Be(_root);
    }

    #endregion

    #region Static helpers — IsDownloadable / IsTiered / VRAM tiers

    [Theory]
    [InlineData(CaptioningModelType.LLaVA_v1_6_34B, true)]
    [InlineData(CaptioningModelType.Qwen2_5_VL_7B, true)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B, true)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption, true)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B_NSFW_Caption_V4, true)]
    public void IsDownloadable_TrueForKnownModels(CaptioningModelType modelType, bool expected)
    {
        CaptioningModelManager.IsDownloadable(modelType).Should().Be(expected);
    }

    [Theory]
    [InlineData(CaptioningModelType.LLaVA_v1_6_34B, false)]
    [InlineData(CaptioningModelType.Qwen2_5_VL_7B, false)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B, false)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption, true)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B_NSFW_Caption_V4, true)]
    public void IsTieredDownloadable_OnlyTrueForVramTieredModels(CaptioningModelType modelType, bool expected)
    {
        CaptioningModelManager.IsTieredDownloadable(modelType).Should().Be(expected);
    }

    [Fact]
    public void GetSupportedVramTiers_ReturnsCanonicalSetForTieredModels()
    {
        var tiers = CaptioningModelManager.GetSupportedVramTiers(
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption);

        tiers.Should().Equal(8, 12, 16, 24, 32);
    }

    [Fact]
    public void GetSupportedVramTiers_EmptyForNonTieredModels()
    {
        CaptioningModelManager.GetSupportedVramTiers(CaptioningModelType.LLaVA_v1_6_34B)
            .Should().BeEmpty();
    }

    [Fact]
    public void GetSupportedVramTiers_ReturnsACopyEachCall()
    {
        // Mutating the returned array must not affect the next caller.
        var a = CaptioningModelManager.GetSupportedVramTiers(
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption);
        a[0] = -999;
        var b = CaptioningModelManager.GetSupportedVramTiers(
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption);

        b[0].Should().Be(8);
    }

    #endregion

    #region Display-name / Description

    [Theory]
    [InlineData(CaptioningModelType.LLaVA_v1_6_34B)]
    [InlineData(CaptioningModelType.Qwen2_5_VL_7B)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B_NSFW_Caption_V4)]
    public void GetDisplayName_ReturnsNonEmptyHumanReadableString(CaptioningModelType modelType)
    {
        var name = CaptioningModelManager.GetDisplayName(modelType);
        name.Should().NotBeNullOrWhiteSpace();
        // Friendlier than the raw enum value.
        name.Should().NotBe(modelType.ToString());
    }

    [Theory]
    [InlineData(CaptioningModelType.LLaVA_v1_6_34B)]
    [InlineData(CaptioningModelType.Qwen2_5_VL_7B)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B_NSFW_Caption_V4)]
    public void GetDescription_ReturnsNonEmptyString(CaptioningModelType modelType)
    {
        CaptioningModelManager.GetDescription(modelType).Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region Path resolution

    [Theory]
    [InlineData(CaptioningModelType.LLaVA_v1_6_34B)]
    [InlineData(CaptioningModelType.Qwen2_5_VL_7B)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B)]
    public void GetModelPath_NonTieredReturnsAbsolutePathInBaseFolderWhenMissing(CaptioningModelType modelType)
    {
        var path = _manager.GetModelPath(modelType);

        Path.IsPathRooted(path).Should().BeTrue();
        path.Should().StartWith(_root);
        Path.GetExtension(path).Should().Be(".gguf");
    }

    [Theory]
    [InlineData(CaptioningModelType.LLaVA_v1_6_34B)]
    [InlineData(CaptioningModelType.Qwen2_5_VL_7B)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B)]
    public void GetClipProjectorPath_NonTieredReturnsAbsolutePathInBaseFolderWhenMissing(CaptioningModelType modelType)
    {
        var path = _manager.GetClipProjectorPath(modelType);

        Path.IsPathRooted(path).Should().BeTrue();
        path.Should().StartWith(_root);
        Path.GetExtension(path).Should().Be(".gguf");
    }

    [Theory]
    [InlineData(CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B_NSFW_Caption_V4)]
    public void GetModelPath_TieredFallsBackToSmallestTierWhenNothingOnDisk(CaptioningModelType modelType)
    {
        // No tier is on disk, so the manager should return the path for the
        // smallest defined tier (8 GB) inside the base folder.
        var defaultPath = _manager.GetModelPath(modelType);
        var explicitSmallTierPath = _manager.GetModelPath(modelType, vramGb: 8);

        Path.GetFileName(defaultPath).Should().Be(Path.GetFileName(explicitSmallTierPath));
    }

    [Fact]
    public void GetModelPath_TieredWithExplicitTier_PicksLargestThatFits()
    {
        // 22 GB doesn't match any tier exactly; should pick the next-smaller (16 GB).
        var pathAt16 = _manager.GetModelPath(
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption, vramGb: 16);
        var pathAt22 = _manager.GetModelPath(
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption, vramGb: 22);

        Path.GetFileName(pathAt16).Should().Be(Path.GetFileName(pathAt22));
    }

    [Fact]
    public void GetModelPath_TieredBelowSmallestThrows()
    {
        var act = () => _manager.GetModelPath(
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption, vramGb: 4);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetExpectedTierTotalBytes_SumsModelPlusMmproj()
    {
        var total = _manager.GetExpectedTierTotalBytes(
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption, vramGb: 8);

        // Two non-zero file sizes summed — should comfortably exceed each individually.
        total.Should().BeGreaterThan(1_000_000_000);
    }

    #endregion

    #region Expected sizes

    [Theory]
    [InlineData(CaptioningModelType.LLaVA_v1_6_34B)]
    [InlineData(CaptioningModelType.Qwen2_5_VL_7B)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B_NSFW_Caption_V4)]
    public void GetExpectedModelSize_PositiveForEveryKnownModel(CaptioningModelType modelType)
    {
        _manager.GetExpectedModelSize(modelType).Should().BeGreaterThan(0);
    }

    #endregion

    #region Status

    [Theory]
    [InlineData(CaptioningModelType.LLaVA_v1_6_34B)]
    [InlineData(CaptioningModelType.Qwen2_5_VL_7B)]
    [InlineData(CaptioningModelType.Qwen3_VL_8B)]
    public void GetModelStatus_NotDownloaded_WhenNoFilesPresent(CaptioningModelType modelType)
    {
        _manager.GetModelStatus(modelType).Should().Be(CaptioningModelStatus.NotDownloaded);
    }

    [Fact]
    public void GetModelStatus_Corrupted_WhenModelFileWayUnderExpectedSize()
    {
        // Plant the model + mmproj files but make the model file way under the
        // expected (multi-gigabyte) size, using genuinely tiny real content —
        // this test deliberately exercises the manager's *default*, unmocked
        // file-size probe (real FileInfo.Length), so a handful of real bytes is
        // "way under" the 80% threshold regardless of exact byte count.
        var modelPath = _manager.GetModelPath(CaptioningModelType.Qwen2_5_VL_7B);
        var mmprojPath = _manager.GetClipProjectorPath(CaptioningModelType.Qwen2_5_VL_7B);

        CreateFile(modelPath, length: 64);
        // mmproj: any non-zero file is fine; status only checks the model size.
        CreateFile(mmprojPath, length: 32);

        _manager.GetModelStatus(CaptioningModelType.Qwen2_5_VL_7B)
            .Should().Be(CaptioningModelStatus.Corrupted);
    }

    [Fact]
    public void GetModelStatus_Ready_WhenBothFilesPresentAtExpectedSize()
    {
        // Real files stay empty placeholders; the injected fileSizeProbe fakes
        // the multi-gigabyte "on disk" sizes GetModelStatus's 80%-threshold
        // check reads, so this test costs bytes rather than gigabytes.
        var modelPath = _manager.GetModelPath(CaptioningModelType.Qwen2_5_VL_7B);
        var mmprojPath = _manager.GetClipProjectorPath(CaptioningModelType.Qwen2_5_VL_7B);
        var expectedSize = _manager.GetExpectedModelSize(CaptioningModelType.Qwen2_5_VL_7B);

        CreateFile(modelPath);
        CreateFile(mmprojPath);

        var manager = new CaptioningModelManager(_root, httpClient: null,
            fileSizeProbe: FakeSizes((modelPath, expectedSize), (mmprojPath, expectedSize / 4)));

        manager.GetModelStatus(CaptioningModelType.Qwen2_5_VL_7B)
            .Should().Be(CaptioningModelStatus.Ready);
    }

    [Fact]
    public void GetModelInfo_PopulatesAllFields()
    {
        var info = _manager.GetModelInfo(CaptioningModelType.LLaVA_v1_6_34B);

        info.ModelType.Should().Be(CaptioningModelType.LLaVA_v1_6_34B);
        info.Status.Should().Be(CaptioningModelStatus.NotDownloaded);
        info.FilePath.Should().NotBeNullOrWhiteSpace();
        info.ExpectedSizeBytes.Should().BeGreaterThan(0);
        info.DisplayName.Should().NotBeNullOrWhiteSpace();
        info.Description.Should().NotBeNullOrWhiteSpace();
        // No file on disk yet, so reported size is zero.
        info.FileSizeBytes.Should().Be(0);
    }

    #endregion

    #region DownloadModelAsync — short-circuit paths

    [Fact]
    public async Task DownloadModelAsync_TieredCalledOnNonTieredOverload_ShortCircuitsToFalse()
    {
        // Non-tiered overload must refuse a tiered model rather than guessing a tier.
        var ok = await _manager.DownloadModelAsync(
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption,
            progress: null,
            cancellationToken: CancellationToken.None);

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task DownloadModelAsync_NonTieredAlreadyPresent_ShortCircuitsToTrue()
    {
        // Pre-plant empty placeholder files for Qwen3_VL_8B; the injected
        // fileSizeProbe reports them as being at full expected size, so the
        // manager short-circuits without either a real multi-gigabyte fixture
        // or an HTTP call.
        var modelPath = _manager.GetModelPath(CaptioningModelType.Qwen3_VL_8B);
        var mmprojPath = _manager.GetClipProjectorPath(CaptioningModelType.Qwen3_VL_8B);
        var modelSize = _manager.GetExpectedModelSize(CaptioningModelType.Qwen3_VL_8B);

        CreateFile(modelPath);
        CreateFile(mmprojPath);

        var manager = new CaptioningModelManager(_root, httpClient: null,
            fileSizeProbe: FakeSizes((modelPath, modelSize), (mmprojPath, modelSize / 4)));

        // Use a synchronous IProgress so the callback has definitely fired
        // before we observe it. Progress<T> would post to a SynchronizationContext
        // (or the ThreadPool when none is present) and races the test thread.
        var progressReports = new List<ModelDownloadProgress>();
        var progress = new SyncProgress(progressReports.Add);

        var ok = await manager.DownloadModelAsync(
            CaptioningModelType.Qwen3_VL_8B,
            progress: progress,
            cancellationToken: CancellationToken.None);

        ok.Should().BeTrue();
        // No HTTP call should have been issued — the short-circuit path reports
        // 'already downloaded' as the final progress.
        progressReports.Should().NotBeEmpty();
        progressReports[^1].Status.Should().Contain("already downloaded");
    }

    /// <summary>Synchronous IProgress shim — runs the callback inline.</summary>
    private sealed class SyncProgress : IProgress<ModelDownloadProgress>
    {
        private readonly Action<ModelDownloadProgress> _onReport;
        public SyncProgress(Action<ModelDownloadProgress> onReport) => _onReport = onReport;
        public void Report(ModelDownloadProgress value) => _onReport(value);
    }

    [Fact]
    public async Task DownloadModelAsync_TieredAlreadyPresent_ShortCircuitsToTrue()
    {
        // Tiered overload short-circuits when the chosen tier's pair is present.
        // Empty placeholder files stand in for the real GGUFs; the injected
        // fileSizeProbe reports each as comfortably over its 80% threshold
        // (tierTotal covers both files, so it's an over-sized fake for either).
        var modelPath = _manager.GetModelPath(
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption, vramGb: 8);
        var mmprojPath = _manager.GetClipProjectorPath(
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption, vramGb: 8);
        var tierTotal = _manager.GetExpectedTierTotalBytes(
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption, vramGb: 8);

        CreateFile(modelPath);
        CreateFile(mmprojPath);

        var manager = new CaptioningModelManager(_root, httpClient: null,
            fileSizeProbe: FakeSizes((modelPath, tierTotal), (mmprojPath, tierTotal)));

        var ok = await manager.DownloadModelAsync(
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption,
            vramGb: 8,
            progress: null,
            cancellationToken: CancellationToken.None);

        ok.Should().BeTrue();
    }

    #endregion

    #region DeleteModel

    [Fact]
    public void DeleteModel_RemovesBothFilesWhenPresent()
    {
        var modelPath = _manager.GetModelPath(CaptioningModelType.Qwen3_VL_8B);
        var mmprojPath = _manager.GetClipProjectorPath(CaptioningModelType.Qwen3_VL_8B);

        CreateFile(modelPath, length: 100);
        CreateFile(mmprojPath, length: 100);

        _manager.DeleteModel(CaptioningModelType.Qwen3_VL_8B);

        File.Exists(modelPath).Should().BeFalse();
        File.Exists(mmprojPath).Should().BeFalse();
    }

    [Fact]
    public void DeleteModel_DoesNotThrowWhenFilesAbsent()
    {
        var act = () => _manager.DeleteModel(CaptioningModelType.Qwen3_VL_8B);
        act.Should().NotThrow();
    }

    #endregion

    #region Download destinations

    [Fact]
    public void GetDownloadDestinations_AlwaysIncludesDefaultCoreFolder()
    {
        var destinations = _manager.GetDownloadDestinations();

        destinations.Should().NotBeEmpty();
        destinations.Should().Contain(d => d.IsDefault && d.Path == _root);
    }

    #endregion
}
