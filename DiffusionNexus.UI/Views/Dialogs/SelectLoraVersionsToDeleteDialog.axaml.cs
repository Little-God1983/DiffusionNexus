using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Entities;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for selecting which LoRA versions/files to delete from a grouped tile.
/// </summary>
public partial class SelectLoraVersionsToDeleteDialog : Window, INotifyPropertyChanged
{
    private bool _hasSelection;
    private string _selectionSummary = "No files selected";
    private string _message = string.Empty;

    public new event PropertyChangedEventHandler? PropertyChanged;

    public SelectLoraVersionsToDeleteDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets the message displayed at the top of the dialog.
    /// </summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (_message != value)
            {
                _message = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
            }
        }
    }

    /// <summary>
    /// Gets the collection of version items to display.
    /// </summary>
    public ObservableCollection<LoraVersionDeleteItem> VersionItems { get; } = [];

    /// <summary>
    /// Gets whether any files are selected.
    /// </summary>
    public bool HasSelection
    {
        get => _hasSelection;
        private set
        {
            if (_hasSelection != value)
            {
                _hasSelection = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelection)));
            }
        }
    }

    /// <summary>
    /// Gets the selection summary text.
    /// </summary>
    public string SelectionSummary
    {
        get => _selectionSummary;
        private set
        {
            if (_selectionSummary != value)
            {
                _selectionSummary = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionSummary)));
            }
        }
    }

    /// <summary>
    /// Gets the result after the dialog closes. Null if cancelled.
    /// </summary>
    public LoraDeleteResult? Result { get; private set; }

    /// <summary>
    /// Populates the dialog with the versions from a grouped LoRA tile.
    /// </summary>
    /// <param name="displayName">The model display name.</param>
    /// <param name="versions">All versions across grouped models.</param>
    /// <param name="allGroupedModels">All model entities in the group (for DB removal).</param>
    public SelectLoraVersionsToDeleteDialog WithVersions(
        string displayName,
        IEnumerable<ModelVersion> versions,
        IReadOnlyList<Model> allGroupedModels)
    {
        Title = $"Delete - {displayName}";
        Message = $"Select which versions of '{displayName}' to delete:";

        foreach (var version in versions)
        {
            var primaryFile = version.Files.FirstOrDefault(f => f.IsPrimary)
                              ?? version.Files.FirstOrDefault();

            var fileName = primaryFile?.FileName ?? version.Name ?? "Unknown file";
            var lastDot = fileName.LastIndexOf('.');
            var displayFileName = lastDot > 0 ? fileName[..lastDot] : fileName;

            var localPath = primaryFile?.LocalPath;
            var fileExists = !string.IsNullOrEmpty(localPath) && File.Exists(localPath);
            var fileSizeText = primaryFile?.SizeKB > 0
                ? FormatFileSize(primaryFile.SizeKB)
                : null;

            // Find the parent model for this version (needed for DB removal)
            var parentModel = allGroupedModels.FirstOrDefault(m =>
                m.Versions.Any(v => v.Id == version.Id));

            var item = new LoraVersionDeleteItem
            {
                Version = version,
                ParentModel = parentModel,
                FileName = displayFileName,
                BaseModelDisplay = version.BaseModelRaw ?? "Unknown base model",
                DetailText = BuildDetailText(fileSizeText, fileExists, localPath),
                LocalPath = localPath
            };

            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == "IsSelected")
                    UpdateSelectionState();
            };

            VersionItems.Add(item);
        }

        UpdateSelectionState();
        return this;
    }

    private void UpdateSelectionState()
    {
        var selectedCount = VersionItems.Count(v => v.IsSelected);
        var totalCount = VersionItems.Count;

        HasSelection = selectedCount > 0;

        SelectionSummary = selectedCount switch
        {
            0 => "No files selected",
            _ when selectedCount == totalCount => $"All {totalCount} files selected - entire LoRA will be deleted",
            _ => $"{selectedCount} of {totalCount} files selected"
        };
    }

    private static string BuildDetailText(string? fileSizeText, bool fileExists, string? localPath)
    {
        var parts = new List<string>();
        if (fileSizeText is not null) parts.Add(fileSizeText);
        if (!fileExists && localPath is not null) parts.Add("(file missing)");
        return parts.Count > 0 ? string.Join(" - ", parts) : string.Empty;
    }

    private static string FormatFileSize(double sizeKb)
    {
        return sizeKb switch
        {
            >= 1_048_576 => $"{sizeKb / 1_048_576.0:F1} GB",
            >= 1_024 => $"{sizeKb / 1_024.0:F1} MB",
            _ => $"{sizeKb} KB"
        };
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var item in VersionItems) item.IsSelected = true;
    }

    private void OnClearSelectionClick(object? sender, RoutedEventArgs e)
    {
        foreach (var item in VersionItems) item.IsSelected = false;
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        var selected = VersionItems.Where(v => v.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Result = LoraDeleteResult.Cancelled();
            Close(false);
            return;
        }

        Result = new LoraDeleteResult
        {
            Confirmed = true,
            SelectedItems = selected,
            DeleteAll = selected.Count == VersionItems.Count
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = LoraDeleteResult.Cancelled();
        Close(false);
    }
}

/// <summary>
/// Represents a LoRA version/file in the delete selection dialog.
/// </summary>
public partial class LoraVersionDeleteItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// The model version entity.
    /// </summary>
    public required ModelVersion Version { get; init; }

    /// <summary>
    /// The parent model entity that owns this version.
    /// </summary>
    public Model? ParentModel { get; init; }

    /// <summary>
    /// Display filename (without extension).
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Base model display text (e.g., "Z-Image-Turbo", "Flux.1 D").
    /// </summary>
    public required string BaseModelDisplay { get; init; }

    /// <summary>
    /// Detail line (file size, missing indicator).
    /// </summary>
    public required string DetailText { get; init; }

    /// <summary>
    /// Local file path on disk (may be null).
    /// </summary>
    public string? LocalPath { get; init; }
}

/// <summary>
/// Result from the LoRA version delete dialog.
/// </summary>
public sealed class LoraDeleteResult
{
    /// <summary>Whether the user confirmed deletion.</summary>
    public bool Confirmed { get; init; }

    /// <summary>The selected items to delete.</summary>
    public List<LoraVersionDeleteItem> SelectedItems { get; init; } = [];

    /// <summary>Whether all versions are selected (entire LoRA should be removed).</summary>
    public bool DeleteAll { get; init; }

    /// <summary>Creates a cancelled result.</summary>
    public static LoraDeleteResult Cancelled() => new() { Confirmed = false };
}
