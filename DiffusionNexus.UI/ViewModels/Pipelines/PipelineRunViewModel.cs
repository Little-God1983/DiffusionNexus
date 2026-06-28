using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Diffusion;
using DiffusionNexus.UI.Services.Pipelines;
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
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private double _totalProgress;
    [ObservableProperty] private string _currentProcessingStatus = string.Empty;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _totalImageCount;

    /// <summary>The most recent test render, shown in the run UI so the user can tune strength.</summary>
    [ObservableProperty] private Bitmap? _testResultImage;

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

        _cts = new CancellationTokenSource();
        IsProcessing = true;
        CurrentProcessingStatus = "Generating test image…";
        try
        {
            var png = await ProcessOneImageAsync(input, isTestRun: true, backend, _cts.Token).ConfigureAwait(true);
            TestResultImage = DecodeBitmap(png);
            CurrentProcessingStatus = "Test image ready — adjust the strength and try again.";
        }
        catch (OperationCanceledException)
        {
            CurrentProcessingStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
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

        try
        {
            for (var i = 0; i < inputs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var input = inputs[i];
                CurrentProcessingStatus = $"[{i + 1}/{TotalImageCount}] Generating {Path.GetFileName(input)}…";

                var png = await ProcessOneImageAsync(input, isTestRun: false, backend, ct).ConfigureAwait(true);
                var outputPath = await _outputWriter.WriteAsync(target, input, png, ct).ConfigureAwait(true);

                TestResultImage = DecodeBitmap(png);
                CompletedCount = i + 1;
                TotalProgress = (i + 1) * 100.0 / TotalImageCount;
                UnifiedLogger?.Info(LogCategory.General, "Pipelines", $"Generated {Path.GetFileName(outputPath)}");
            }

            CurrentProcessingStatus = $"Done — {CompletedCount} image(s) generated.";
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

    private static Bitmap? DecodeBitmap(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
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
    }
}
