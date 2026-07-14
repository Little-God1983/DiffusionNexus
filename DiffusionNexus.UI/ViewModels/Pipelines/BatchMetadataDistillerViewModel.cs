using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Models.Distiller;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Distiller;
using DiffusionNexus.UI.Services.Lora;
using DiffusionNexus.UI.Services.Pipelines;
using Serilog;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

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
    private readonly MetadataDistillerService _distiller;
    private readonly ImageMetadataParser _parser = new();
    private CancellationTokenSource? _cts;

    public string Title => "Batch Metadata Distiller";
    public event EventHandler? CloseRequested;
    public ResourceMonitorViewModel? ResourceMonitor { get; set; } // no GPU use; ignored

    public ObservableCollection<string> ImagePaths { get; } = [];
    [ObservableProperty] private string? _selectedImagePath;

    public ObservableCollection<DistillerItemViewModel> Items { get; } = [];
    [ObservableProperty] private DistillerItemViewModel? _selectedItem;

    public ObservableCollection<PromptRuleSetViewModel> RuleSets { get; } = [];

    [ObservableProperty] private bool _stripWorkflow = true;
    [ObservableProperty] private bool _computeHashes;
    [ObservableProperty] private string? _outputFolder;

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
        IDialogService? dialogs = null)
    {
        _loraCatalog = loraCatalog;
        _dialogs = dialogs;
        var hasher = new ImageResourceHasher(loraCatalog, async _ => await installer.ResolveModelsRootAsync());
        _distiller = new MetadataDistillerService(hasher);

        ImagePaths.CollectionChanged += OnImagePathsChanged;
    }

    partial void OnWithMetadataCountChanged(int value) => OnPropertyChanged(nameof(DetectionSummary));
    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(DetectionSummary));

    partial void OnSelectedImagePathChanged(string? value) =>
        SelectedItem = Items.FirstOrDefault(i => string.Equals(i.Path, value, StringComparison.OrdinalIgnoreCase));

    private void OnImagePathsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var it in Items) { it.PropertyChanged -= OnItemPropertyChanged; it.Dispose(); }
            Items.Clear();
            RecomputeCounts();
            return;
        }

        if (e.NewItems is not null)
            foreach (string path in e.NewItems.OfType<string>())
                _ = AddItemAsync(path);

        if (e.OldItems is not null)
            foreach (string path in e.OldItems.OfType<string>())
            {
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
        if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return;

        ImageGenerationData data;
        try { data = await Task.Run(() => _parser.Parse(path)); }
        catch (Exception ex) { Logger.Warning(ex, "Distiller: parse failed for {Path}", path); return; }

        var item = new DistillerItemViewModel(path, data);
        Items.Add(item);
        item.PropertyChanged += OnItemPropertyChanged;
        if (SelectedItem is null && item.HasMetadata) { SelectedItem = item; SelectedImagePath = item.Path; }
        RecomputeCounts();
        await item.LoadThumbnailAsync();
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
    private void AddReplaceSet() => RuleSets.Add(new PromptRuleSetViewModel { Name = $"Replace set {RuleSets.Count + 1}", IsReplace = true });

    [RelayCommand]
    private void RemoveRuleSet(PromptRuleSetViewModel? set) { if (set is not null) RuleSets.Remove(set); }

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
            var options = new DistillOptions { StripWorkflow = StripWorkflow, ComputeHashes = ComputeHashes, OutputFolder = OutputFolder };

            var progress = new Progress<int>(done =>
                Dispatcher.UIThread.Post(() =>
                {
                    Progress = items.Count == 0 ? 0 : (double)done / items.Count * 100;
                    StatusText = $"{done} / {items.Count}";
                }));

            var result = await _distiller.DistillAsync(items, rules, options, progress, _cts.Token);

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
        ImagePaths.CollectionChanged -= OnImagePathsChanged;
    }
}
