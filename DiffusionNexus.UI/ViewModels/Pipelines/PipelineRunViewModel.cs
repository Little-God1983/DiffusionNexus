using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Diffusion;
using DiffusionNexus.UI.Services.Pipelines;
using DiffusionNexus.UI.ViewModels.Controls;
using Serilog;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>
/// Abstract base for a guided pipeline "run" screen. Owns everything common to every image pipeline:
/// the shared input source (a saved dataset OR loose images), the output-destination options, the
/// "Generate test image" / "Generate all" commands, progress + cancellation, and writing results.
/// A concrete pipeline only supplies <see cref="ProcessOneImageAsync"/> (and any extra controls).
/// </summary>
public abstract partial class PipelineRunViewModel : ViewModelBase, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PipelineRunViewModel>();
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    protected IPipelineAssetInstaller Installer { get; }
    protected LocalDiffusionBackendProvider BackendProvider { get; }
    protected IDialogService Dialogs { get; }
    protected IUnifiedLogger? UnifiedLogger { get; }

    private readonly IPipelineOutputWriter _outputWriter;
    private readonly IDatasetState _datasetState;
    private CancellationTokenSource? _cts;
    private string? _lastTempPreviewPath;

    public PipelineManifest Manifest { get; }
    public abstract string Title { get; }

    /// <summary>
    /// GPU VRAM + RAM monitor shown atop the run UI. Assigned by the host to the SAME instance the
    /// gallery uses, so there's a single nvidia-smi poller (the visible view drives the timer).
    /// </summary>
    public ResourceMonitorViewModel? ResourceMonitor { get; set; }

    /// <summary>Raised when the user clicks Back; the host clears its active run.</summary>
    public event EventHandler? CloseRequested;

    // ── Input source (dataset vs loose images) ──────────────────────────────────
    [ObservableProperty] private bool _isSingleImageMode;
    public bool IsDatasetMode => !IsSingleImageMode;

    public ObservableCollection<string> SingleImagePaths { get; } = [];
    public bool HasSingleImage => SingleImagePaths.Count > 0;

    /// <summary>The thumbnail the user clicked in the loose-image list (drives "Generate test image").</summary>
    [ObservableProperty] private string? _selectedInputImagePath;

    public ObservableCollection<DatasetCardViewModel> AvailableDatasets => _datasetState.Datasets;
    public ObservableCollection<EditorVersionItem> AvailableDatasetVersions { get; } = [];
    [ObservableProperty] private DatasetCardViewModel? _selectedDataset;
    [ObservableProperty] private EditorVersionItem? _selectedDatasetVersion;

    // ── Output options (depend on input mode) ───────────────────────────────────
    public ObservableCollection<PipelineOutputOption> AvailableOutputOptions { get; } = [];
    [ObservableProperty] private PipelineOutputOption? _selectedOutputOption;

    // ── Shared img2img settings (common to image-to-image pipelines) ─────────────
    [ObservableProperty] private string _prompt;

    /// <summary>Image influence = denoise strength (0 = keep input, 1 = ignore input).</summary>
    [ObservableProperty] private double _imageInfluence;

    // ── Progress ────────────────────────────────────────────────────────────────
    // NotifyCanExecuteChangedFor: while one generate command runs, IsProcessing=true must disable
    // BOTH buttons (CanGenerate checks !IsProcessing) — otherwise a second click clobbers the shared
    // _cts (breaking Cancel) and races the Outputs collection.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateTestCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateAllCommand))]
    private bool _isProcessing;
    [ObservableProperty] private double _totalProgress;
    [ObservableProperty] private string _currentProcessingStatus = string.Empty;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _totalImageCount;

    // ── Outputs (drive the reusable status strip + before/after comparison) ──────
    /// <summary>One entry per produced image, with input/output paths + live status (colours the strip).</summary>
    public ObservableCollection<ImageStatusItemViewModel> Outputs { get; } = [];

    /// <summary>The output tile selected in the strip; its input/output paths feed the comparison view.</summary>
    [ObservableProperty] private ImageStatusItemViewModel? _selectedOutput;

    protected PipelineRunViewModel(
        PipelineManifest manifest,
        IPipelineAssetInstaller installer,
        LocalDiffusionBackendProvider backendProvider,
        IPipelineOutputWriter outputWriter,
        IDatasetState datasetState,
        IDialogService dialogs,
        IUnifiedLogger? unifiedLogger,
        string defaultPrompt,
        double defaultImageInfluence)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Installer = installer ?? throw new ArgumentNullException(nameof(installer));
        BackendProvider = backendProvider ?? throw new ArgumentNullException(nameof(backendProvider));
        _outputWriter = outputWriter ?? throw new ArgumentNullException(nameof(outputWriter));
        _datasetState = datasetState ?? throw new ArgumentNullException(nameof(datasetState));
        Dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        UnifiedLogger = unifiedLogger;
        _prompt = defaultPrompt;
        _imageInfluence = defaultImageInfluence;

        // ImageListInputControl mutates SingleImagePaths in place; subscribe so HasSingleImage + the
        // command CanExecute update (binding alone doesn't notify).
        SingleImagePaths.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSingleImage));
            GenerateTestCommand.NotifyCanExecuteChanged();
            GenerateAllCommand.NotifyCanExecuteChanged();
        };

        RecomputeOutputOptions();
    }

    partial void OnIsSingleImageModeChanged(bool value)
    {
        if (value) SelectedDataset = null;
        else SingleImagePaths.Clear();

        OnPropertyChanged(nameof(IsDatasetMode));
        RecomputeOutputOptions();
        GenerateTestCommand.NotifyCanExecuteChanged();
        GenerateAllCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedDatasetChanged(DatasetCardViewModel? value)
    {
        AvailableDatasetVersions.Clear();
        if (value is not null)
        {
            IsSingleImageMode = false;
            PopulateVersions(value);
        }

        RecomputeOutputOptions();
        GenerateTestCommand.NotifyCanExecuteChanged();
        GenerateAllCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedDatasetVersionChanged(EditorVersionItem? value)
    {
        if (SelectedDataset is not null && value is not null)
            SelectedDataset.CurrentVersion = value.Version;
    }

    private void PopulateVersions(DatasetCardViewModel dataset)
    {
        foreach (var version in dataset.GetAllVersionNumbers())
        {
            var folder = dataset.GetVersionFolderPath(version);
            var count = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder).Count(IsImageFile)
                : 0;
            AvailableDatasetVersions.Add(EditorVersionItem.Create(version, count));
        }
    }

    private void RecomputeOutputOptions()
    {
        AvailableOutputOptions.Clear();
        if (IsSingleImageMode)
        {
            AvailableOutputOptions.Add(new PipelineOutputOption(PipelineOutputMode.InputFolderInPlace, "Save in the input folder"));
            AvailableOutputOptions.Add(new PipelineOutputOption(PipelineOutputMode.PickFolder, "Choose an output folder…"));
        }
        else
        {
            AvailableOutputOptions.Add(new PipelineOutputOption(PipelineOutputMode.NewDatasetVersion, "Save as a new dataset version"));
            AvailableOutputOptions.Add(new PipelineOutputOption(PipelineOutputMode.PickFolder, "Choose an output folder…"));
        }

        SelectedOutputOption = AvailableOutputOptions[0];
    }

    /// <summary>Resolves the selected input images uniformly across both input modes.</summary>
    protected IReadOnlyList<string> GetImagePaths()
    {
        if (IsSingleImageMode)
            return SingleImagePaths.ToList();

        if (SelectedDataset is { } dataset)
        {
            var path = dataset.CurrentVersionFolderPath;
            if (Directory.Exists(path))
                return Directory.EnumerateFiles(path).Where(IsImageFile).ToList();
        }

        return [];
    }

    private static bool IsImageFile(string file) =>
        ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant());

    // ── Commands ────────────────────────────────────────────────────────────────

    private bool CanGenerate() =>
        !IsProcessing && (IsSingleImageMode ? HasSingleImage : SelectedDataset is not null);

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateTestAsync()
    {
        var input = SelectedInputImagePath is { } sel && File.Exists(sel)
            ? sel
            : GetImagePaths().FirstOrDefault(File.Exists);

        if (input is null)
        {
            await Dialogs.ShowMessageAsync(Title, "No input image is available to test.");
            return;
        }

        var backend = await ResolveBackendAsync();
        if (backend is null)
            return;

        var item = new ImageStatusItemViewModel
        {
            FileName = Path.GetFileName(input),
            InputPath = input,
            Status = ImageProcessingStatus.Processing,
        };
        Outputs.Clear();
        Outputs.Add(item);
        SelectedOutput = item;
        _ = LoadThumbnailAsync(item);

        _cts = new CancellationTokenSource();
        IsProcessing = true;
        CurrentProcessingStatus = "Generating test image…";
        try
        {
            var png = await ProcessOneImageAsync(input, isTestRun: true, backend, _cts.Token).ConfigureAwait(true);
            // The test isn't written to the chosen destination; stash it in temp so the comparison
            // control (which loads from a path) can show the before/after.
            item.OutputPath = WriteTempPng(png, input);
            item.Status = ImageProcessingStatus.Done;
            CurrentProcessingStatus = "Test image ready — adjust the settings and try again.";
        }
        catch (OperationCanceledException)
        {
            item.Status = ImageProcessingStatus.Failed;
            CurrentProcessingStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            item.Status = ImageProcessingStatus.Failed;
            Log.Error(ex, "Pipeline test generation failed.");
            UnifiedLogger?.Error(LogCategory.General, "Pipelines", $"Test generation failed: {ex.Message}", ex);
            CurrentProcessingStatus = $"Failed: {ex.Message}";
            await Dialogs.ShowMessageAsync("Generation failed", ex.Message);
        }
        finally
        {
            IsProcessing = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAllAsync()
    {
        var inputs = GetImagePaths().Where(File.Exists).ToList();
        if (inputs.Count == 0)
        {
            await Dialogs.ShowMessageAsync(Title, "No input images were found.");
            return;
        }

        if (SelectedOutputOption is not { } outputOption)
            return;

        var backend = await ResolveBackendAsync();
        if (backend is null)
            return;

        // Prepare the output destination once (creates the new dataset version / picks the folder).
        PipelineOutputTarget? target;
        try
        {
            target = await _outputWriter.PrepareAsync(outputOption, SelectedDataset).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to prepare pipeline output.");
            await Dialogs.ShowMessageAsync("Output error", ex.Message);
            return;
        }
        if (target is null)
            return; // user cancelled the folder picker

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsProcessing = true;
        CompletedCount = 0;
        TotalImageCount = inputs.Count;
        TotalProgress = 0;

        // Pre-populate the strip with one pending tile per input, then update each as it runs.
        Outputs.Clear();
        SelectedOutput = null;
        var items = inputs
            .Select(p => new ImageStatusItemViewModel { FileName = Path.GetFileName(p), InputPath = p })
            .ToList();
        foreach (var it in items)
            Outputs.Add(it);
        _ = LoadThumbnailsAsync(items);

        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var item = items[i];
                CurrentProcessingStatus = $"[{i + 1}/{TotalImageCount}] Generating {item.FileName}…";
                item.Status = ImageProcessingStatus.Processing;

                try
                {
                    var png = await ProcessOneImageAsync(item.InputPath, isTestRun: false, backend, ct).ConfigureAwait(true);
                    var outputPath = await _outputWriter.WriteAsync(target, item.InputPath, png, ct).ConfigureAwait(true);

                    item.OutputPath = outputPath;
                    item.Status = ImageProcessingStatus.Done;
                    // Auto-show the FIRST completed result, then leave selection to the user so the
                    // strip/compare don't yank away a tile they clicked to inspect mid-batch.
                    SelectedOutput ??= item;
                    UnifiedLogger?.Info(LogCategory.General, "Pipelines", $"Generated {Path.GetFileName(outputPath)}");
                }
                catch (OperationCanceledException)
                {
                    item.Status = ImageProcessingStatus.Failed;
                    throw;
                }
                catch (Exception ex)
                {
                    // One failure marks its tile red but does not abort the rest of the batch.
                    item.Status = ImageProcessingStatus.Failed;
                    Log.Error(ex, "Pipeline generation failed for {Input}.", item.InputPath);
                    UnifiedLogger?.Error(LogCategory.General, "Pipelines", $"Failed {item.FileName}: {ex.Message}", ex);
                }

                CompletedCount = i + 1;
                TotalProgress = (i + 1) * 100.0 / TotalImageCount;
            }

            var done = Outputs.Count(o => o.Status == ImageProcessingStatus.Done);
            var failed = Outputs.Count(o => o.Status == ImageProcessingStatus.Failed);
            CurrentProcessingStatus = failed > 0
                ? $"Done — {done} generated, {failed} failed."
                : $"Done — {done} image(s) generated.";
        }
        catch (OperationCanceledException)
        {
            CurrentProcessingStatus = $"Cancelled after {CompletedCount}/{TotalImageCount}.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Pipeline batch generation failed.");
            UnifiedLogger?.Error(LogCategory.General, "Pipelines", $"Batch generation failed: {ex.Message}", ex);
            CurrentProcessingStatus = $"Failed: {ex.Message}";
            await Dialogs.ShowMessageAsync("Generation failed", ex.Message);
        }
        finally
        {
            IsProcessing = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelGenerate() => _cts?.Cancel();

    [RelayCommand]
    private void Back() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private async Task<IDiffusionBackend?> ResolveBackendAsync()
    {
        var backend = await BackendProvider.TryGetAsync().ConfigureAwait(true);
        if (backend is null)
        {
            await Dialogs.ShowMessageAsync("Local renderer unavailable",
                "The local renderer needs a ComfyUI installation's models folder. Add a ComfyUI installation " +
                "in the Installer Manager, then try again.");
        }
        return backend;
    }

    /// <summary>Loads a small input thumbnail for each tile (best-effort, off the UI thread for decode).</summary>
    private static async Task LoadThumbnailsAsync(IReadOnlyList<ImageStatusItemViewModel> items)
    {
        foreach (var item in items)
            await LoadThumbnailAsync(item).ConfigureAwait(true);
    }

    private static async Task LoadThumbnailAsync(ImageStatusItemViewModel item)
    {
        try
        {
            var bitmap = await Task.Run(() => EfficientImageDecoder.DecodeThumbnail(item.InputPath, 120)).ConfigureAwait(false);
            if (bitmap is not null)
            {
                // Assign the bound Bitmap on the UI thread regardless of which thread called us
                // (call-site-independent, matching the Batch Upscale loader this was extracted from).
                await Dispatcher.UIThread.InvokeAsync(() => item.Thumbnail = bitmap);
            }
        }
        catch
        {
            // Thumbnail is decorative — never let it break a run.
        }
    }

    /// <summary>
    /// Writes a generated PNG to a temp file so the path-based comparison control can display the test
    /// result. A fresh file name each call forces the control to re-decode; the previous one is deleted
    /// so repeated "Generate test" clicks don't leak PNGs (the rest are cleaned up in <see cref="Dispose"/>).
    /// </summary>
    private string WriteTempPng(byte[] png, string inputPath)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DiffusionNexus", "pipeline-preview");
        Directory.CreateDirectory(dir);
        var name = $"{Path.GetFileNameWithoutExtension(inputPath)}_{Guid.NewGuid():N}.png";
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, png);

        TryDeleteTempPreview();
        _lastTempPreviewPath = path;
        return path;
    }

    private void TryDeleteTempPreview()
    {
        if (_lastTempPreviewPath is null)
            return;
        try { File.Delete(_lastTempPreviewPath); } catch { /* best-effort temp cleanup */ }
        _lastTempPreviewPath = null;
    }

    /// <summary>
    /// THE inheritance hook: a concrete pipeline turns one input image into one output PNG (model,
    /// prompt, LoRAs, strength, etc.). Called once per image by both Generate commands.
    /// </summary>
    protected abstract Task<byte[]> ProcessOneImageAsync(
        string inputPath, bool isTestRun, IDiffusionBackend backend, CancellationToken cancellationToken);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        TryDeleteTempPreview();
    }
}
