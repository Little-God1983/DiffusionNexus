using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using DiffusionNexus.DataAccess.UnitOfWork;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Controls;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Models.Distiller;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Distiller;
using DiffusionNexus.UI.Services.Lora;
using DiffusionNexus.UI.Services.Pipelines;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Serilog;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>A longest-side resize option offered by the distiller output card.</summary>
public sealed record ResizeChoice(string Label, int? MaxDimension)
{
    public override string ToString() => Label;
}

/// <summary>A PNG compression option offered by the distiller output card.</summary>
public sealed record CompressionChoice(string Label, bool Recompress)
{
    public override string ToString() => Label;
}

/// <summary>
/// Run screen for the Batch Metadata Distiller: load loose images, auto-detect embedded prompts,
/// hand-curate per image, define batch-wide delete/replace rule sets, and write clean, CivitAI-readable
/// copies. Implements <see cref="IPipelineRun"/> so it can live in the Workflows gallery, but shares
/// none of the generation machinery in <see cref="PipelineRunViewModel"/>.
/// </summary>
public partial class BatchMetadataDistillerViewModel : ViewModelBase, IPipelineRun
{
    private static readonly ILogger Logger = Log.ForContext<BatchMetadataDistillerViewModel>();

    private readonly ILoraCatalog _loraCatalog;
    private readonly IDialogService? _dialogs;
    private readonly IUnifiedLogger? _log;
    private readonly IServiceProvider? _services;
    private readonly MetadataDistillerService _distiller;
    private readonly ImageMetadataParser _parser = new();
    private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;

    // Rule-set persistence (AppSettings.DistillerRuleSetsJson): suppressed while the saved sets are
    // being loaded, then any edit re-serializes immediately and flushes to the DB after a short debounce.
    private bool _suppressRuleSetSave;
    private CancellationTokenSource? _saveDebounceCts;
    private string? _pendingRuleSetsJson;

    public string Title => "Batch Metadata Distiller";
    public event EventHandler? CloseRequested;
    public ResourceMonitorViewModel? ResourceMonitor { get; set; } // no GPU use; ignored

    public ObservableCollection<string> ImagePaths { get; } = [];
    [ObservableProperty] private string? _selectedImagePath;

    public ObservableCollection<DistillerItemViewModel> Items { get; } = [];
    [ObservableProperty] private DistillerItemViewModel? _selectedItem;

    public ObservableCollection<PromptRuleSetViewModel> RuleSets { get; } = [];

    [ObservableProperty] private bool _stripWorkflow = true;
    [ObservableProperty] private bool _computeHashes = true;
    [ObservableProperty] private string? _outputFolder;

    /// <summary>Optional output resize (longest side) and PNG recompression choices.</summary>
    public IReadOnlyList<ResizeChoice> ResizeChoices { get; } =
    [
        new("Keep original size", null),
        new("Max 4096 px", 4096),
        new("Max 2048 px", 2048),
        new("Max 1536 px", 1536),
        new("Max 1024 px", 1024),
    ];

    public IReadOnlyList<CompressionChoice> CompressionChoices { get; } =
    [
        new("Keep original compression", false),
        new("Maximum PNG compression (lossless, slower)", true),
    ];

    [ObservableProperty] private ResizeChoice _selectedResize;
    [ObservableProperty] private CompressionChoice _selectedCompression;

    /// <summary>"12.4 MB → ~5.8 MB (−53%)" preview for the selected image, or null when inactive.</summary>
    [ObservableProperty] private string? _sizeEstimateText;
    private CancellationTokenSource? _estimateCts;

    /// <summary>Per-rule occurrence lines produced by the rules "Test" dry run.</summary>
    public ObservableCollection<string> TestResults { get; } = [];

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _withMetadataCount;
    public string DetectionSummary => $"{WithMetadataCount} / {TotalCount} images have embedded metadata";

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "";

    public BatchMetadataDistillerViewModel(
        PipelineManifest manifest,
        ILoraCatalog loraCatalog,
        IPipelineAssetInstaller installer,
        IDialogService? dialogs = null,
        IUnifiedLogger? unifiedLogger = null,
        IServiceProvider? services = null)
    {
        _loraCatalog = loraCatalog;
        _dialogs = dialogs;
        _log = unifiedLogger;
        _services = services;
        _selectedResize = ResizeChoices[0];
        _selectedCompression = CompressionChoices[0];

        // Checkpoint hashes are looked up in the library DB first (so a model in ANY registered
        // install is found, reusing its stored hash), then via a disk scan across every models root;
        // LoRAs resolve through the catalog inside the hasher. `services` is null in tests — then
        // only the disk scan runs.
        Func<CancellationToken, Task<IReadOnlyList<TrackedModelFile>>>? trackedModelFiles = services is null
            ? null
            : async ct =>
            {
                using var scope = services.CreateScope();
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var files = await uow.ModelFiles.GetAllWithLocalPathAsync(ct).ConfigureAwait(false);
                return files
                    .Select(f => new TrackedModelFile(f.FileName, f.LocalPath, f.HashAutoV2, f.HashSHA256))
                    .ToList();
            };

        var hasher = new ImageResourceHasher(
            loraCatalog,
            ct => installer.ResolveModelsRootsAsync(ct),
            trackedModelFiles);
        _distiller = new MetadataDistillerService(hasher, unifiedLogger);

        ImagePaths.CollectionChanged += OnImagePathsChanged;
        RuleSets.CollectionChanged += OnRuleSetsChanged;
        _ = LoadRuleSetsAsync();
    }

    partial void OnWithMetadataCountChanged(int value) => OnPropertyChanged(nameof(DetectionSummary));
    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(DetectionSummary));

    partial void OnSelectedImagePathChanged(string? value) =>
        SelectedItem = Items.FirstOrDefault(i => string.Equals(i.Path, value, StringComparison.OrdinalIgnoreCase));

    partial void OnSelectedItemChanged(DistillerItemViewModel? value)
    {
        TestRulesCommand.NotifyCanExecuteChanged();
        UpdateSizeEstimate();
    }

    partial void OnSelectedResizeChanged(ResizeChoice value) => UpdateSizeEstimate();
    partial void OnSelectedCompressionChanged(CompressionChoice value) => UpdateSizeEstimate();

    /// <summary>
    /// Recomputes the "original → estimated" file-size preview for the selected image by actually
    /// re-encoding it in memory with the chosen settings (off the UI thread, superseding runs cancelled).
    /// </summary>
    private void UpdateSizeEstimate()
    {
        _estimateCts?.Cancel();

        var item = SelectedItem;
        var resize = SelectedResize?.MaxDimension;
        var recompress = SelectedCompression?.Recompress == true;

        if (item is null || !item.IsPng || (resize is null && !recompress))
        {
            SizeEstimateText = null;
            return;
        }

        _estimateCts = new CancellationTokenSource();
        _ = EstimateSizeAsync(item.Path, resize, recompress, _estimateCts.Token);
    }

    private async Task EstimateSizeAsync(string path, int? maxDimension, bool recompress, CancellationToken ct)
    {
        SizeEstimateText = "Estimating size…";
        try
        {
            var level = recompress ? PngReencoder.MaxZlibLevel : PngReencoder.DefaultZlibLevel;
            var reencoded = await Task.Run(() => PngReencoder.Reencode(path, maxDimension, level), ct);
            if (ct.IsCancellationRequested) return;

            if (reencoded is null)
            {
                SizeEstimateText = "Could not estimate (image not decodable).";
                return;
            }

            var original = new FileInfo(path).Length;
            var estimated = (long)reencoded.Bytes.Length;
            var pct = original > 0 ? (double)(estimated - original) / original * 100 : 0;
            SizeEstimateText =
                $"Selected image: {FormatBytes(original)} → ~{FormatBytes(estimated)} ({pct:+0;−0}%), {reencoded.Width}×{reencoded.Height}";
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Distiller: size estimate failed for {Path}", path);
            if (!ct.IsCancellationRequested) SizeEstimateText = "Could not estimate size.";
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };

    private void OnImagePathsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var it in Items) { it.PropertyChanged -= OnItemPropertyChanged; it.Dispose(); }
            Items.Clear();
            _knownPaths.Clear();
            RecomputeCounts();
            return;
        }

        if (e.NewItems is not null)
            foreach (string path in e.NewItems.OfType<string>())
                _ = AddItemAsync(path);

        if (e.OldItems is not null)
            foreach (string path in e.OldItems.OfType<string>())
            {
                _knownPaths.Remove(path);
                var existing = Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.PropertyChanged -= OnItemPropertyChanged;
                    Items.Remove(existing);
                    existing.Dispose();
                }
            }

        RecomputeCounts();
    }

    private async Task AddItemAsync(string path)
    {
        // O(1) dedupe; also reserves the path immediately so concurrent adds of the same file don't race.
        if (!_knownPaths.Add(path)) return;

        ImageGenerationData data;
        try { data = await Task.Run(() => _parser.Parse(path)); }
        catch (Exception ex)
        {
            _knownPaths.Remove(path);
            Logger.Warning(ex, "Distiller: parse failed for {Path}", path);
            _log?.Warn(LogCategory.General, "Distiller", $"Failed to read {Path.GetFileName(path)}: {ex.Message}");
            return;
        }

        var item = new DistillerItemViewModel(path, data);
        Items.Add(item);
        item.PropertyChanged += OnItemPropertyChanged;
        if (SelectedItem is null && item.HasMetadata) { SelectedItem = item; SelectedImagePath = item.Path; }
        RecomputeCounts();

        // Instrument the extraction result so the Unified Console shows what was recovered per image.
        _log?.Info(LogCategory.General, "Distiller",
            $"Loaded {Path.GetFileName(path)}: metadata={data.HasData}, sampler={data.SamplerName ?? "-"}, " +
            $"steps={(data.Steps?.ToString() ?? "-")}, loras={data.Loras.Count}");
    }

    private void RecomputeCounts()
    {
        TotalCount = Items.Count;
        WithMetadataCount = Items.Count(i => i.HasMetadata);
        DistillCommand.NotifyCanExecuteChanged();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DistillerItemViewModel.IncludeInRun))
            DistillCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Back() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void AddDeleteSet() => RuleSets.Add(new PromptRuleSetViewModel { Name = $"Delete set {RuleSets.Count + 1}", IsReplace = false });

    [RelayCommand]
    private void AddReplaceSet()
    {
        var set = new PromptRuleSetViewModel { Name = $"Replace set {RuleSets.Count + 1}", IsReplace = true };
        set.Pairs.Add(new ReplacePairViewModel()); // start with one empty search→replacement row
        RuleSets.Add(set);
    }

    [RelayCommand]
    private void RemoveRuleSet(PromptRuleSetViewModel? set) { if (set is not null) RuleSets.Remove(set); }

    #region Rule-set persistence

    private void OnRuleSetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var set in e.OldItems.OfType<PromptRuleSetViewModel>())
                set.Changed -= OnRuleSetEdited;
        if (e.NewItems is not null)
            foreach (var set in e.NewItems.OfType<PromptRuleSetViewModel>())
                set.Changed += OnRuleSetEdited;

        TestRulesCommand.NotifyCanExecuteChanged();
        ScheduleSaveRuleSets();
    }

    private void OnRuleSetEdited(object? sender, EventArgs e) => ScheduleSaveRuleSets();

    /// <summary>
    /// Serializes the current rule sets (on the UI thread, so the collections aren't touched from a
    /// worker) and schedules a debounced DB write. Rapid edits keep replacing the pending snapshot;
    /// only the latest one is flushed.
    /// </summary>
    private void ScheduleSaveRuleSets()
    {
        if (_services is null || _suppressRuleSetSave) return;

        _pendingRuleSetsJson = JsonSerializer.Serialize(RuleSets.Select(r => r.ToData()).ToList());
        _saveDebounceCts?.Cancel();
        _saveDebounceCts = new CancellationTokenSource();
        _ = FlushRuleSetsAfterDelayAsync(_saveDebounceCts.Token);
    }

    private async Task FlushRuleSetsAfterDelayAsync(CancellationToken ct)
    {
        try { await Task.Delay(800, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; } // superseded by a newer edit or disposed

        await SaveRuleSetsJsonAsync(_pendingRuleSetsJson).ConfigureAwait(false);
    }

    private async Task SaveRuleSetsJsonAsync(string? json)
    {
        if (_services is null || json is null) return;
        try
        {
            using var scope = _services.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var settings = await uow.AppSettings.GetSettingsWithIncludesAsync().ConfigureAwait(false);
            settings.DistillerRuleSetsJson = json;
            await uow.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Distiller: failed to save rule sets");
            _log?.Warn(LogCategory.General, "Distiller", $"Could not save rule sets: {ex.Message}");
        }
    }

    /// <summary>Restores the saved rule sets from AppSettings when the screen opens.</summary>
    private async Task LoadRuleSetsAsync()
    {
        if (_services is null) return;
        try
        {
            string? json;
            using (var scope = _services.CreateScope())
            {
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var settings = await uow.AppSettings.GetSettingsAsync().ConfigureAwait(false);
                json = settings?.DistillerRuleSetsJson;
            }
            if (string.IsNullOrWhiteSpace(json)) return;

            var data = JsonSerializer.Deserialize<List<PromptRuleSetData>>(json);
            if (data is null || data.Count == 0) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _suppressRuleSetSave = true;
                try
                {
                    foreach (var d in data)
                        RuleSets.Add(PromptRuleSetViewModel.FromData(d));
                }
                finally { _suppressRuleSetSave = false; }
            });
            _log?.Info(LogCategory.General, "Distiller", $"Loaded {data.Count} saved rule set(s).");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Distiller: failed to load saved rule sets");
            _log?.Warn(LogCategory.General, "Distiller", $"Could not load saved rule sets: {ex.Message}");
        }
    }

    #endregion

    public bool CanTestRules => SelectedItem is not null && RuleSets.Count > 0;

    /// <summary>
    /// Dry-runs the enabled rule sets against the selected image's prompts: reports per-rule
    /// occurrence counts and highlights the matches directly in the prompt editors.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestRules))]
    private void TestRules()
    {
        var item = SelectedItem;
        if (item is null) return;

        var sets = RuleSets.Select(r => r.ToModel()).ToList();
        var positive = item.Positive ?? "";
        var negative = item.Negative ?? "";

        var posResults = PromptRuleEngine.Simulate(positive, sets);
        var negResults = PromptRuleEngine.Simulate(negative, sets);

        TestResults.Clear();

        // Cross-check delete words vs replace search terms: a deleted word can't be replaced.
        foreach (var c in PromptRuleEngine.FindConflicts(sets))
            TestResults.Add($"⚠ Conflict: \"{c.Term}\" is deleted by \"{c.DeleteSetName}\" and is a search term in \"{c.ReplaceSetName}\" — whichever set runs first, the other won't match this word.");

        var any = false;
        // Simulate returns the same rules in the same order for both prompts — merge by index.
        for (var i = 0; i < posResults.Count; i++)
        {
            var p = posResults[i].Count;
            var n = i < negResults.Count ? negResults[i].Count : 0;
            any |= p + n > 0;
            TestResults.Add(string.IsNullOrWhiteSpace(negative)
                ? $"{posResults[i].Description} — {p} occurrence(s)"
                : $"{posResults[i].Description} — {p} in positive, {n} in negative");
        }
        if (TestResults.Count == 0)
            TestResults.Add("No enabled rules to test.");
        else if (!any)
            TestResults.Add("No occurrences found in the selected image.");

        item.PositiveHighlights = ToHighlights(PromptRuleEngine.FindMatches(positive, sets));
        item.NegativeHighlights = ToHighlights(PromptRuleEngine.FindMatches(negative, sets));
    }

    private static IReadOnlyList<TextHighlightRange>? ToHighlights(IReadOnlyList<PromptMatch> matches) =>
        matches.Count == 0
            ? null
            : matches.Select(m => new TextHighlightRange(m.Start, m.Length,
                m.IsReplace ? TextHighlightKind.Change : TextHighlightKind.Removal)).ToList();

    public bool CanDistill => !IsRunning && !string.IsNullOrWhiteSpace(OutputFolder) && Items.Any(i => i.IncludeInRun);

    [RelayCommand(CanExecute = nameof(CanDistill))]
    private async Task DistillAsync()
    {
        if (!CanDistill) return;

        _cts = new CancellationTokenSource();
        IsRunning = true;
        Progress = 0;
        try
        {
            var items = Items.Where(i => i.IncludeInRun)
                .Select(i => new DistillItem(i.Path, i.BuildEditedData(), i.Positive ?? "", i.Negative, i.IncludedLoras()))
                .ToList();
            var rules = RuleSets.Select(r => r.ToModel()).ToList();
            var options = new DistillOptions
            {
                StripWorkflow = StripWorkflow,
                ComputeHashes = ComputeHashes,
                OutputFolder = OutputFolder,
                ResizeMaxDimension = SelectedResize?.MaxDimension,
                RecompressPng = SelectedCompression?.Recompress == true,
            };

            var progress = new Progress<int>(done =>
                Dispatcher.UIThread.Post(() =>
                {
                    Progress = items.Count == 0 ? 0 : (double)done / items.Count * 100;
                    StatusText = $"{done} / {items.Count}";
                }));

            _log?.Info(LogCategory.General, "Distiller",
                $"Distilling {items.Count} image(s) → {OutputFolder} (strip workflow={StripWorkflow}, hashes={ComputeHashes}, " +
                $"resize={(options.ResizeMaxDimension?.ToString() ?? "off")}, recompress={options.RecompressPng})");

            // Run the batch on a background thread so the UI — and the progress bar — stays responsive.
            var result = await Task.Run(
                () => _distiller.DistillAsync(items, rules, options, progress, _cts.Token), _cts.Token);

            _log?.Info(LogCategory.General, "Distiller",
                $"Distill finished: {result.Written} written, {result.Failures.Count} failed → {OutputFolder}");

            StatusText = result.Failures.Count == 0
                ? $"Done — {result.Written} image(s) written to {OutputFolder}"
                : $"Done — {result.Written} written, {result.Failures.Count} failed";

            if (result.Failures.Count > 0 && _dialogs is not null)
                await _dialogs.ShowMessageAsync("Some images failed",
                    string.Join("\n", result.Failures.Take(20).Select(f => $"{Path.GetFileName(f.SourcePath)}: {f.Error}")));
        }
        catch (OperationCanceledException) { StatusText = "Cancelled"; }
        catch (Exception ex)
        {
            Logger.Error(ex, "Distill run failed");
            if (_dialogs is not null) await _dialogs.ShowMessageAsync("Distill failed", ex.Message);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    partial void OnIsRunningChanged(bool value) => DistillCommand.NotifyCanExecuteChanged();
    partial void OnOutputFolderChanged(string? value) => DistillCommand.NotifyCanExecuteChanged();

    public void LoadInputImages(IReadOnlyList<string> paths)
    {
        foreach (var p in paths.Where(File.Exists))
            if (!ImagePaths.Contains(p)) ImagePaths.Add(p);
    }

    public void Dispose()
    {
        foreach (var it in Items) { it.PropertyChanged -= OnItemPropertyChanged; it.Dispose(); }
        _cts?.Cancel();
        _cts?.Dispose();
        _estimateCts?.Cancel();
        _estimateCts?.Dispose();
        ImagePaths.CollectionChanged -= OnImagePathsChanged;
        RuleSets.CollectionChanged -= OnRuleSetsChanged;

        // Flush any pending rule-set snapshot immediately — don't lose an edit made just before closing.
        if (_saveDebounceCts is { IsCancellationRequested: false })
        {
            _saveDebounceCts.Cancel();
            _ = SaveRuleSetsJsonAsync(_pendingRuleSetsJson);
        }
    }
}
