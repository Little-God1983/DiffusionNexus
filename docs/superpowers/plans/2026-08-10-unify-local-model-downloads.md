# Unify Local Model Downloads onto the Download Coordinator — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every locally-downloaded ONNX model — the existing RMBG-1.4 background-removal model and the new WD14 image-tagging model — reports its download through `IDownloadCoordinator`, the same status-bar / Unified Console pipeline that LoRA, Civitai, and captioning-model downloads already use, instead of a page-local progress bar nobody outside that one panel can see.

**Architecture:** `OnnxModelManager` gains a third model entry (WD14 ViT Tagger v3: an `.onnx` file plus its `selected_tags.csv` tag list) using the same per-model method shape already used for RMBG-1.4 and 4x-UltraSharp — including renaming HuggingFace's generic `model.onnx` to a descriptive on-disk filename, exactly like the existing `rmbg-1.4.onnx`. `OnnxModelManager` itself stays coordinator-agnostic (it only knows `IProgress<ModelDownloadProgress>`, as today). The actual coordinator wiring happens one layer up, in `BackgroundRemovalViewModel`, by wrapping the manager call in `IDownloadCoordinator.EnqueueAsync(...)` — the exact adapter shape `CaptioningModelsDialogViewModel` already uses for GGUF captioning models. `IDownloadCoordinator` turns out to already be threaded as far down as `LoraDatasetHelperViewModel` (verified: `App.axaml.cs:945` resolves it from DI and passes it in); this plan only needs to forward that existing value three hops further, into `ImageEditTabViewModel → ImageEditorViewModel → BackgroundRemovalViewModel`.

**Tech Stack:** .NET, xUnit + FluentAssertions, existing `IDownloadCoordinator` / `DownloadCoordinator` (`DiffusionNexus.Infrastructure`), `Microsoft.ML.OnnxRuntime` (untouched by this plan).

## Global Constraints

- Never modify `IDownloadCoordinator` / `DownloadCoordinator` — they already do exactly what's needed; this plan only adds callers.
- New on-disk model filenames must never be the literal HuggingFace default `model.onnx` — always a descriptive name, matching the existing `rmbg-1.4.onnx` / `4x-UltraSharp.onnx` convention.
- New/modified constructor parameters follow this codebase's existing convention for optional service dependencies: `IFoo? foo = null`, trailing, never required — these ViewModels are constructed both via DI and via `new` in tests/design-time code.
- 4x-UltraSharp is out of scope: it is currently orphaned (no live ViewModel consumer — confirmed via repo search), so there is no real call site to retrofit. Leave its `OnnxModelManager` methods as they are.
- Do not touch `IUnifiedLogger` / `ITaskTracker` directly. `IDownloadCoordinator` already reaches the Unified Console transitively (`DownloadCoordinator` → `IActivityLogService` → `ActivityLogServiceBridge` → `IUnifiedLogger`/`ITaskTracker`, wired in `DiffusionNexus.Infrastructure/ServiceCollectionExtensions.cs:54`) — going through the coordinator alone is correct and sufficient.

---

## File Structure

- **Modify** `DiffusionNexus.Service/Services/OnnxModelManager.cs` — add WD14 tagger constants, paths, status check, download method (model `.onnx` + tags `.csv`), delete method.
- **Create** `DiffusionNexus.Tests/Service/Services/OnnxModelManagerTests.cs` — status transitions and renamed-file assertions for the new WD14 entry, using a fake `HttpMessageHandler` (no real network calls).
- **Modify** `DiffusionNexus.UI/ViewModels/BackgroundRemovalViewModel.cs` — accept optional `IDownloadCoordinator?`; route the download through it when present, falling back to the current local-progress behavior when not (design-time / tests).
- **Modify** `DiffusionNexus.UI/ViewModels/ImageEditorViewModel.cs` — thread `IDownloadCoordinator?` from constructor to the `BackgroundRemovalViewModel` it creates.
- **Modify** `DiffusionNexus.UI/ViewModels/Tabs/ImageEditTabViewModel.cs` — thread `IDownloadCoordinator?` from constructor to the `ImageEditorViewModel` it creates.
- **Modify** `DiffusionNexus.UI/ViewModels/LoraDatasetHelperViewModel.cs` — forward its *already-received* `downloadCoordinator` parameter into the `ImageEditTabViewModel` it creates (one-line change; no signature change needed here).
- **Create** `DiffusionNexus.Tests/ViewModels/BackgroundRemovalViewModelTests.cs` — asserts a supplied fake `IDownloadCoordinator` is used, and that behavior is unchanged when none is supplied.

---

### Task 1: WD14 tagger model management in `OnnxModelManager`

**Files:**
- Modify: `DiffusionNexus.Service/Services/OnnxModelManager.cs`
- Test: `DiffusionNexus.Tests/Service/Services/OnnxModelManagerTests.cs` (create)

**Interfaces:**
- Produces: `OnnxModelManager.Wd14TaggerModelPath` (`string`), `OnnxModelManager.Wd14TaggerTagsPath` (`string`), `OnnxModelManager.GetWd14TaggerStatus()` (`ModelStatus`), `OnnxModelManager.DownloadWd14TaggerModelAsync(IProgress<ModelDownloadProgress>?, CancellationToken)` (`Task<bool>`), `OnnxModelManager.DeleteWd14TaggerModel()` (`void`). Consumed by Plan B's `ImageTaggingService`.

- [ ] **Step 1: Write the failing tests**

```csharp
// DiffusionNexus.Tests/Service/Services/OnnxModelManagerTests.cs
using System.Net;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

public sealed class OnnxModelManagerTests : IDisposable
{
    private readonly string _root;

    public OnnxModelManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dn-onnxmgr-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Returns a fixed byte payload for any request — no real network access.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly byte[] _content;
        public FakeHandler(byte[] content) => _content = content;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_content)
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void GetWd14TaggerStatus_ReturnsNotDownloaded_WhenNeitherFileExists()
    {
        var manager = new OnnxModelManager(_root, httpClient: null);

        manager.GetWd14TaggerStatus().Should().Be(ModelStatus.NotDownloaded);
    }

    [Fact]
    public async Task DownloadWd14TaggerModelAsync_WritesDescriptiveFileNames_NotGenericHuggingFaceNames()
    {
        var manager = new OnnxModelManager(_root, new HttpClient(new FakeHandler(new byte[400])));

        await manager.DownloadWd14TaggerModelAsync();

        File.Exists(Path.Combine(_root, "wd-vit-tagger-v3.onnx")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "wd-vit-tagger-v3-tags.csv")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "model.onnx")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "selected_tags.csv")).Should().BeFalse();
    }

    [Fact]
    public async Task GetWd14TaggerStatus_ReturnsCorrupted_WhenModelFileIsUndersized()
    {
        // FakeHandler returns a 400-byte payload for every request — far
        // below the real ~379MB model, so the size-sanity check must catch it.
        var manager = new OnnxModelManager(_root, new HttpClient(new FakeHandler(new byte[400])));

        await manager.DownloadWd14TaggerModelAsync();

        manager.GetWd14TaggerStatus().Should().Be(ModelStatus.Corrupted);
    }

    [Fact]
    public async Task DownloadWd14TaggerModelAsync_ReturnsTrue_WhenAlreadyDownloaded()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "wd-vit-tagger-v3.onnx"), new byte[350_000_000]);
        File.WriteAllBytes(Path.Combine(_root, "wd-vit-tagger-v3-tags.csv"), new byte[310_000]);
        var manager = new OnnxModelManager(_root, httpClient: null);

        var result = await manager.DownloadWd14TaggerModelAsync();

        result.Should().BeTrue();
        manager.GetWd14TaggerStatus().Should().Be(ModelStatus.Ready);
    }

    [Fact]
    public void DeleteWd14TaggerModel_RemovesBothFiles()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "wd-vit-tagger-v3.onnx"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_root, "wd-vit-tagger-v3-tags.csv"), new byte[10]);
        var manager = new OnnxModelManager(_root, httpClient: null);

        manager.DeleteWd14TaggerModel();

        File.Exists(Path.Combine(_root, "wd-vit-tagger-v3.onnx")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "wd-vit-tagger-v3-tags.csv")).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~OnnxModelManagerTests"`
Expected: FAIL to compile — `GetWd14TaggerStatus`, `DownloadWd14TaggerModelAsync`, `DeleteWd14TaggerModel` don't exist yet.

- [ ] **Step 3: Add the WD14 tagger entry to `OnnxModelManager`**

Add alongside the existing `Rmbg14`/`UltraSharp4x` constant blocks (after line 22, before `private readonly string _modelsBasePath;`):

```csharp
    // WD14 ViT Tagger v3 (booru-style image tags + content rating, one ONNX pass)
    private const string Wd14TaggerModelFileName = "wd-vit-tagger-v3.onnx";
    private const string Wd14TaggerModelUrl = "https://huggingface.co/SmilingWolf/wd-vit-tagger-v3/resolve/main/model.onnx";
    private const long ExpectedWd14TaggerSizeBytes = 379_000_000; // ~379MB

    private const string Wd14TaggerTagsFileName = "wd-vit-tagger-v3-tags.csv";
    private const string Wd14TaggerTagsUrl = "https://huggingface.co/SmilingWolf/wd-vit-tagger-v3/resolve/main/selected_tags.csv";
    private const long ExpectedWd14TaggerTagsSizeBytes = 308_000; // ~308KB
```

Add to the `private bool _isDownloadingRmbg14;` / `_isDownloadingUltraSharp4x;` field group:

```csharp
    private bool _isDownloadingWd14Tagger;
```

Add alongside `Rmbg14ModelPath` / `UltraSharp4xModelPath`:

```csharp
    /// <summary>Gets the full path to the WD14 tagger ONNX model file.</summary>
    public string Wd14TaggerModelPath => Path.Combine(_modelsBasePath, Wd14TaggerModelFileName);

    /// <summary>Gets the full path to the WD14 tagger's tag list CSV.</summary>
    public string Wd14TaggerTagsPath => Path.Combine(_modelsBasePath, Wd14TaggerTagsFileName);
```

Add alongside `GetRmbg14Status()` / `GetUltraSharp4xStatus()`:

```csharp
    /// <summary>
    /// Gets the status of the WD14 tagger. Both the model and its tag list
    /// must be present and correctly sized — the tagger is unusable without
    /// its CSV, so a missing/corrupt CSV counts the whole entry as not ready.
    /// </summary>
    public ModelStatus GetWd14TaggerStatus()
    {
        lock (_downloadLock)
        {
            if (_isDownloadingWd14Tagger)
                return ModelStatus.Downloading;
        }

        if (!File.Exists(Wd14TaggerModelPath) || !File.Exists(Wd14TaggerTagsPath))
            return ModelStatus.NotDownloaded;

        var modelInfo = new FileInfo(Wd14TaggerModelPath);
        if (modelInfo.Length < 300_000_000)
            return ModelStatus.Corrupted;

        var tagsInfo = new FileInfo(Wd14TaggerTagsPath);
        if (tagsInfo.Length < 100_000)
            return ModelStatus.Corrupted;

        return ModelStatus.Ready;
    }
```

Add alongside `DownloadRmbg14ModelAsync` / `DownloadUltraSharp4xModelAsync`:

```csharp
    /// <summary>
    /// Downloads the WD14 ViT Tagger v3 model and its tag list from HuggingFace.
    /// Two files, downloaded sequentially; both must succeed for the entry to be Ready.
    /// </summary>
    public async Task<bool> DownloadWd14TaggerModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var status = GetWd14TaggerStatus();
        if (status == ModelStatus.Ready)
        {
            progress?.Report(new ModelDownloadProgress(
                ExpectedWd14TaggerSizeBytes, ExpectedWd14TaggerSizeBytes, "Model already downloaded"));
            return true;
        }

        lock (_downloadLock)
        {
            if (_isDownloadingWd14Tagger)
            {
                Log.Warning("WD14 tagger model download already in progress");
                return false;
            }
            _isDownloadingWd14Tagger = true;
        }

        try
        {
            var modelOk = await DownloadModelInternalAsync(
                Wd14TaggerModelUrl, Wd14TaggerModelPath, ExpectedWd14TaggerSizeBytes,
                "WD14 Tagger", progress, cancellationToken);

            if (!modelOk)
                return false;

            var tagsOk = await DownloadModelInternalAsync(
                Wd14TaggerTagsUrl, Wd14TaggerTagsPath, ExpectedWd14TaggerTagsSizeBytes,
                "WD14 Tagger tag list", progress, cancellationToken);

            return tagsOk;
        }
        finally
        {
            lock (_downloadLock)
            {
                _isDownloadingWd14Tagger = false;
            }
        }
    }
```

Add alongside `DeleteRmbg14Model()` / `DeleteUltraSharp4xModel()`:

```csharp
    /// <summary>Deletes both WD14 tagger files if they exist.</summary>
    public void DeleteWd14TaggerModel()
    {
        try
        {
            if (File.Exists(Wd14TaggerModelPath))
                File.Delete(Wd14TaggerModelPath);
            if (File.Exists(Wd14TaggerTagsPath))
                File.Delete(Wd14TaggerTagsPath);
            Log.Information("WD14 tagger model deleted: {Path}", Wd14TaggerModelPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete WD14 tagger model: {Path}", Wd14TaggerModelPath);
            throw;
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~OnnxModelManagerTests"`
Expected: PASS (5 tests)

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.Service/Services/OnnxModelManager.cs DiffusionNexus.Tests/Service/Services/OnnxModelManagerTests.cs
git commit -m "feat: add WD14 tagger model management to OnnxModelManager"
```

---

### Task 2: Route `BackgroundRemovalViewModel`'s download through `IDownloadCoordinator`

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/BackgroundRemovalViewModel.cs:11-36` (fields/ctor), `:345-392` (`ExecuteDownloadModelAsync`)
- Test: `DiffusionNexus.Tests/ViewModels/BackgroundRemovalViewModelTests.cs` (create)

**Interfaces:**
- Consumes: `IDownloadCoordinator.EnqueueAsync(string, Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>, CancellationToken)` (`DiffusionNexus.Domain.Services`), `IBackgroundRemovalService.DownloadModelAsync(IProgress<ModelDownloadProgress>?, CancellationToken)`.
- Produces: `BackgroundRemovalViewModel(Func<bool>, Action<string>, IBackgroundRemovalService?, IDownloadCoordinator?)` — new 4th optional parameter. Consumed by Task 3.

- [ ] **Step 1: Write the failing tests**

```csharp
// DiffusionNexus.Tests/ViewModels/BackgroundRemovalViewModelTests.cs
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

public sealed class BackgroundRemovalViewModelTests
{
    private static Mock<IBackgroundRemovalService> MakeService(bool downloadResult = true)
    {
        var mock = new Mock<IBackgroundRemovalService>();
        mock.Setup(s => s.DownloadModelAsync(It.IsAny<IProgress<ModelDownloadProgress>>(), It.IsAny<CancellationToken>()))
            .Callback<IProgress<ModelDownloadProgress>?, CancellationToken>((p, _) =>
                p?.Report(new ModelDownloadProgress(100, 100, "Download complete")))
            .ReturnsAsync(downloadResult);
        return mock;
    }

    [Fact]
    public async Task DownloadModelCommand_WithCoordinator_EnqueuesThroughIt()
    {
        var service = MakeService();
        var coordinator = new Mock<IDownloadCoordinator>();
        coordinator
            .Setup(c => c.EnqueueAsync(
                It.IsAny<string>(),
                It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>, CancellationToken>(
                (name, action, ct) => action(new Progress<DownloadTaskProgress>(), ct));

        var vm = new BackgroundRemovalViewModel(() => true, _ => { }, service.Object, coordinator.Object);

        await vm.DownloadModelCommand.ExecuteAsync(null);

        coordinator.Verify(c => c.EnqueueAsync(
            It.Is<string>(n => n.Contains("RMBG")),
            It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.DownloadModelAsync(It.IsAny<IProgress<ModelDownloadProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DownloadModelCommand_WithoutCoordinator_StillDownloadsDirectly()
    {
        var service = MakeService();
        var vm = new BackgroundRemovalViewModel(() => true, _ => { }, service.Object, downloadCoordinator: null);

        await vm.DownloadModelCommand.ExecuteAsync(null);

        service.Verify(s => s.DownloadModelAsync(It.IsAny<IProgress<ModelDownloadProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~BackgroundRemovalViewModelTests"`
Expected: FAIL to compile — no 4-argument `BackgroundRemovalViewModel` constructor yet.

- [ ] **Step 3: Add the field and constructor parameter**

In `DiffusionNexus.UI/ViewModels/BackgroundRemovalViewModel.cs`, replace:

```csharp
    private readonly Func<bool> _hasImage;
    private readonly Action<string> _deactivateOtherTools;
    private readonly IBackgroundRemovalService? _service;

    private bool _isPanelOpen;
    private bool _isBusy;
    private string? _status;
    private int _progress;

    public BackgroundRemovalViewModel(
        Func<bool> hasImage,
        Action<string> deactivateOtherTools,
        IBackgroundRemovalService? service)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);
        _hasImage = hasImage;
        _deactivateOtherTools = deactivateOtherTools;
        _service = service;

        RemoveBackgroundCommand = new AsyncRelayCommand(ExecuteRemoveBackgroundAsync, CanExecuteRemoveBackground);
        RemoveBackgroundToLayerCommand = new AsyncRelayCommand(ExecuteRemoveBackgroundToLayerAsync, CanExecuteRemoveBackground);
        DownloadModelCommand = new AsyncRelayCommand(ExecuteDownloadModelAsync, CanExecuteDownloadModel);
    }
```

with:

```csharp
    private readonly Func<bool> _hasImage;
    private readonly Action<string> _deactivateOtherTools;
    private readonly IBackgroundRemovalService? _service;
    private readonly IDownloadCoordinator? _downloadCoordinator;

    private bool _isPanelOpen;
    private bool _isBusy;
    private string? _status;
    private int _progress;

    public BackgroundRemovalViewModel(
        Func<bool> hasImage,
        Action<string> deactivateOtherTools,
        IBackgroundRemovalService? service,
        IDownloadCoordinator? downloadCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);
        _hasImage = hasImage;
        _deactivateOtherTools = deactivateOtherTools;
        _service = service;
        _downloadCoordinator = downloadCoordinator;

        RemoveBackgroundCommand = new AsyncRelayCommand(ExecuteRemoveBackgroundAsync, CanExecuteRemoveBackground);
        RemoveBackgroundToLayerCommand = new AsyncRelayCommand(ExecuteRemoveBackgroundToLayerAsync, CanExecuteRemoveBackground);
        DownloadModelCommand = new AsyncRelayCommand(ExecuteDownloadModelAsync, CanExecuteDownloadModel);
    }
```

- [ ] **Step 4: Route the download through the coordinator when present**

Replace the body of `ExecuteDownloadModelAsync` (currently lines 345-392):

```csharp
    private async Task ExecuteDownloadModelAsync()
    {
        if (_service is null)
        {
            StatusMessageChanged?.Invoke(this, "Background removal service not available");
            return;
        }

        IsBusy = true;
        Progress = 0;
        Status = "Downloading model...";

        try
        {
            bool success;

            if (_downloadCoordinator is not null)
            {
                success = await _downloadCoordinator.EnqueueAsync(
                    "RMBG-1.4 background removal model",
                    async (taskProgress, ct) =>
                    {
                        var fileProgress = new Progress<ModelDownloadProgress>(p =>
                        {
                            if (p.Percentage >= 0)
                                Progress = (int)p.Percentage;
                            Status = p.Status;

                            var percent = p.TotalBytes > 0
                                ? (int)((double)p.BytesDownloaded / p.TotalBytes * 100.0)
                                : 0;
                            taskProgress.Report(new DownloadTaskProgress(percent, p.Status));
                        });

                        return await _service.DownloadModelAsync(fileProgress, ct);
                    });
            }
            else
            {
                var progress = new Progress<ModelDownloadProgress>(p =>
                {
                    if (p.Percentage >= 0)
                        Progress = (int)p.Percentage;
                    Status = p.Status;
                });

                success = await _service.DownloadModelAsync(progress);
            }

            if (success)
            {
                StatusMessageChanged?.Invoke(this, "RMBG-1.4 model downloaded successfully");
                RefreshModelStatus();
            }
            else
            {
                StatusMessageChanged?.Invoke(this, "Failed to download background removal model");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessageChanged?.Invoke(this, "Model download cancelled");
        }
        catch (Exception ex)
        {
            StatusMessageChanged?.Invoke(this, $"Model download failed: {ex.Message}");
        }
        finally
        {
            Status = null;
            Progress = 0;
            IsBusy = false;
        }
    }
```

(`DownloadTaskProgress` and `IDownloadCoordinator` are both in `DiffusionNexus.Domain.Services`, already imported by this file's existing `using DiffusionNexus.Domain.Services;` — no new `using` needed.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~BackgroundRemovalViewModelTests"`
Expected: PASS (2 tests)

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/BackgroundRemovalViewModel.cs DiffusionNexus.Tests/ViewModels/BackgroundRemovalViewModelTests.cs
git commit -m "feat: route background removal model download through IDownloadCoordinator"
```

---

### Task 3: Thread `IDownloadCoordinator` down to `BackgroundRemovalViewModel`

`LoraDatasetHelperViewModel` already receives a live `IDownloadCoordinator?` (constructor parameter `downloadCoordinator`, resolved from DI at `App.axaml.cs:945`) — it just doesn't forward it. This task forwards it three hops: `LoraDatasetHelperViewModel → ImageEditTabViewModel → ImageEditorViewModel → BackgroundRemovalViewModel`. No DI registration changes and no `App.axaml.cs` changes are needed.

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/LoraDatasetHelperViewModel.cs:213`
- Modify: `DiffusionNexus.UI/ViewModels/Tabs/ImageEditTabViewModel.cs:286-307`
- Modify: `DiffusionNexus.UI/ViewModels/ImageEditorViewModel.cs:356-373`

**Interfaces:**
- Consumes: `BackgroundRemovalViewModel`'s new 4th constructor parameter from Task 2.

- [ ] **Step 1: Thread through `ImageEditorViewModel`**

In `DiffusionNexus.UI/ViewModels/ImageEditorViewModel.cs`, replace the constructor signature:

```csharp
    public ImageEditorViewModel(
        IDatasetEventAggregator? eventAggregator = null,
        IBackgroundRemovalService? backgroundRemovalService = null,
        IComfyUIWrapperService? comfyUiService = null,
        EditorServices? services = null,
        IFeatureReadinessService? readinessService = null,
        Domain.Services.UnifiedLogging.IUnifiedLogger? unifiedLogger = null)
```

with:

```csharp
    public ImageEditorViewModel(
        IDatasetEventAggregator? eventAggregator = null,
        IBackgroundRemovalService? backgroundRemovalService = null,
        IComfyUIWrapperService? comfyUiService = null,
        EditorServices? services = null,
        IFeatureReadinessService? readinessService = null,
        Domain.Services.UnifiedLogging.IUnifiedLogger? unifiedLogger = null,
        IDownloadCoordinator? downloadCoordinator = null)
```

and replace the line:

```csharp
        BackgroundRemoval = new BackgroundRemovalViewModel(() => HasImage, DeactivateOtherTools, backgroundRemovalService);
```

with:

```csharp
        BackgroundRemoval = new BackgroundRemovalViewModel(() => HasImage, DeactivateOtherTools, backgroundRemovalService, downloadCoordinator);
```

Add `using DiffusionNexus.Domain.Services;` at the top of the file if not already present (check first — most ViewModels in this project already import it for other service interfaces).

- [ ] **Step 2: Thread through `ImageEditTabViewModel`**

In `DiffusionNexus.UI/ViewModels/Tabs/ImageEditTabViewModel.cs`, replace the constructor signature:

```csharp
    public ImageEditTabViewModel(
        IDatasetEventAggregator eventAggregator,
        IDatasetState state,
        IBackgroundRemovalService? backgroundRemovalService = null,
        IComfyUIWrapperService? comfyUiService = null,
        IThumbnailOrchestrator? thumbnailOrchestrator = null,
        IFeatureReadinessService? readinessService = null,
        Domain.Services.UnifiedLogging.IUnifiedLogger? unifiedLogger = null,
        IAppSettingsService? settingsService = null,
        IVideoThumbnailService? videoThumbnailService = null)
```

with:

```csharp
    public ImageEditTabViewModel(
        IDatasetEventAggregator eventAggregator,
        IDatasetState state,
        IBackgroundRemovalService? backgroundRemovalService = null,
        IComfyUIWrapperService? comfyUiService = null,
        IThumbnailOrchestrator? thumbnailOrchestrator = null,
        IFeatureReadinessService? readinessService = null,
        Domain.Services.UnifiedLogging.IUnifiedLogger? unifiedLogger = null,
        IAppSettingsService? settingsService = null,
        IVideoThumbnailService? videoThumbnailService = null,
        IDownloadCoordinator? downloadCoordinator = null)
```

and replace the line:

```csharp
        ImageEditor = new ImageEditorViewModel(_eventAggregator, _backgroundRemovalService, _comfyUiService, readinessService: _readinessService, unifiedLogger: unifiedLogger);
```

with:

```csharp
        ImageEditor = new ImageEditorViewModel(_eventAggregator, _backgroundRemovalService, _comfyUiService, readinessService: _readinessService, unifiedLogger: unifiedLogger, downloadCoordinator: downloadCoordinator);
```

- [ ] **Step 3: Forward from `LoraDatasetHelperViewModel`**

In `DiffusionNexus.UI/ViewModels/LoraDatasetHelperViewModel.cs`, replace the line:

```csharp
        ImageEdit = new ImageEditTabViewModel(eventAggregator, state, backgroundRemovalService, comfyUiService, thumbnailOrchestrator, readinessService, unifiedLogger, settingsService, videoThumbnailService);
```

with:

```csharp
        ImageEdit = new ImageEditTabViewModel(eventAggregator, state, backgroundRemovalService, comfyUiService, thumbnailOrchestrator, readinessService, unifiedLogger, settingsService, videoThumbnailService, downloadCoordinator);
```

(`downloadCoordinator` is already an existing parameter of this constructor — no signature change here.)

- [ ] **Step 4: Build and run the full test suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; full suite passes, including the pre-existing `ThumbnailAwareViewModelTests` and `ImageEditorViewModelSendToBatchUpscaleTests` that construct these types with positional/named args (the new parameter is optional and trailing, so old call sites keep compiling unchanged).

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/LoraDatasetHelperViewModel.cs DiffusionNexus.UI/ViewModels/Tabs/ImageEditTabViewModel.cs DiffusionNexus.UI/ViewModels/ImageEditorViewModel.cs
git commit -m "feat: thread IDownloadCoordinator through to background removal"
```

---

### Task 4: Manual verification

**Files:** none (verification only)

- [ ] **Step 1: Run the app in Debug**

Run: `dotnet run --project DiffusionNexus.UI` (or `DiffusionNexus.UI-V2`, whichever is the current startup project — check `DiffusionNexusCoreDbContextFactory.cs`'s doc comment if unsure)

- [ ] **Step 2: Trigger the RMBG-1.4 download**

Open the Image Editor → Background Removal panel → click "Download Model" (delete `%LocalAppData%\DiffusionNexus\Models\rmbg-1.4.onnx` first if it's already downloaded, to force a fresh download).

- [ ] **Step 3: Confirm visibility in the Unified Console and status bar**

Open the Unified Console panel and the status bar's download flyout. Confirm an entry named "RMBG-1.4 background removal model" appears with live percent, the same way an in-flight LoRA or captioning-model download does. Confirm the entry clears from the flyout on completion and a completion line remains in the Unified Console log.

- [ ] **Step 4: Confirm the WD14 filenames on disk**

This model isn't triggered from the UI yet (that's Plan B/C) — verify Task 1's renaming directly:

```bash
# from a scratch script or a throwaway unit test run — do not commit this file
```

```csharp
var manager = new DiffusionNexus.Service.Services.OnnxModelManager();
await manager.DownloadWd14TaggerModelAsync(new Progress<DiffusionNexus.Domain.Services.ModelDownloadProgress>(
    p => Console.WriteLine($"{p.Percentage:0}% — {p.Status}")));
Console.WriteLine(manager.Wd14TaggerModelPath);
```

Confirm `%LocalAppData%\DiffusionNexus\Models\wd-vit-tagger-v3.onnx` and `wd-vit-tagger-v3-tags.csv` exist (~379MB and ~308KB respectively) and no `model.onnx` or `selected_tags.csv` appears in that folder.
