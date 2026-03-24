using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Wraps a single affected file path with inline caption editing capabilities.
/// Used in the Dataset Quality detail panel to let users manually fix caption issues.
/// </summary>
public class EditableAffectedFile : ObservableObject
{
    private readonly Stack<string> _undoStack = new();
    private readonly Stack<string> _redoStack = new();
    private string _captionText = string.Empty;
    private string _originalText = string.Empty;
    private bool _isExpanded;
    private bool _isLoaded;
    private bool _isSaving;
    private bool _isUndoingOrRedoing;

    /// <summary>
    /// Creates a new <see cref="EditableAffectedFile"/> for the given file path.
    /// </summary>
    /// <param name="filePath">Absolute path to the caption file.</param>
    /// <param name="onSaved">Callback invoked after a caption is saved to disk.</param>
    public EditableAffectedFile(string filePath, Func<EditableAffectedFile, Task>? onSaved = null)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        ImagePath = ResolveCorrespondingImage(filePath);
        _onSaved = onSaved;

        ToggleExpandCommand = new RelayCommand(ToggleExpand);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => HasUnsavedChanges && !_isSaving);
        ResetCommand = new RelayCommand(Reset, () => HasUnsavedChanges);
        UndoCommand = new RelayCommand(Undo, () => _undoStack.Count > 0);
        RedoCommand = new RelayCommand(Redo, () => _redoStack.Count > 0);
    }

    private readonly Func<EditableAffectedFile, Task>? _onSaved;

    /// <summary>
    /// Absolute path to the caption file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// File name only (for display).
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Absolute path to the corresponding image file, or <c>null</c> if none found.
    /// </summary>
    public string? ImagePath { get; }

    /// <summary>
    /// The editable caption text.
    /// </summary>
    public string CaptionText
    {
        get => _captionText;
        set
        {
            if (_captionText != value && !_isUndoingOrRedoing)
            {
                _undoStack.Push(_captionText);
                _redoStack.Clear();
                UndoCommand.NotifyCanExecuteChanged();
                RedoCommand.NotifyCanExecuteChanged();
            }

            if (SetProperty(ref _captionText, value))
            {
                OnPropertyChanged(nameof(HasUnsavedChanges));
                SaveCommand.NotifyCanExecuteChanged();
                ResetCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Whether the caption has been modified from its on-disk version.
    /// </summary>
    public bool HasUnsavedChanges => _isLoaded && _captionText != _originalText;

    /// <summary>
    /// Whether the editor for this file is expanded/visible.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        private set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Toggles the expanded state and lazy-loads file content on first expand.
    /// </summary>
    public IRelayCommand ToggleExpandCommand { get; }

    /// <summary>
    /// Saves the current caption text to disk.
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// Resets the caption text to its original on-disk content.
    /// </summary>
    public IRelayCommand ResetCommand { get; }

    /// <summary>
    /// Undoes the last caption edit.
    /// </summary>
    public IRelayCommand UndoCommand { get; }

    /// <summary>
    /// Redoes a previously undone caption edit.
    /// </summary>
    public IRelayCommand RedoCommand { get; }

    private void ToggleExpand()
    {
        if (!IsExpanded && !_isLoaded)
        {
            LoadFromDisk();
        }

        IsExpanded = !IsExpanded;
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(FilePath))
        {
            _originalText = string.Empty;
            _captionText = string.Empty;
            _isLoaded = true;
            OnPropertyChanged(nameof(CaptionText));
            return;
        }

        var text = File.ReadAllText(FilePath);
        _originalText = text;
        _captionText = text;
        _isLoaded = true;
        OnPropertyChanged(nameof(CaptionText));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private async Task SaveAsync()
    {
        if (!HasUnsavedChanges) return;

        _isSaving = true;
        SaveCommand.NotifyCanExecuteChanged();

        try
        {
            await Task.Run(() => File.WriteAllText(FilePath, _captionText)).ConfigureAwait(false);

            _originalText = _captionText;
            OnPropertyChanged(nameof(HasUnsavedChanges));
            SaveCommand.NotifyCanExecuteChanged();
            ResetCommand.NotifyCanExecuteChanged();

            if (_onSaved is not null)
            {
                await _onSaved(this).ConfigureAwait(false);
            }
        }
        finally
        {
            _isSaving = false;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    private void Reset()
    {
        CaptionText = _originalText;
        _undoStack.Clear();
        _redoStack.Clear();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;

        _isUndoingOrRedoing = true;
        try
        {
            var previous = _undoStack.Pop();
            _redoStack.Push(_captionText);
            if (SetProperty(ref _captionText, previous, nameof(CaptionText)))
            {
                OnPropertyChanged(nameof(HasUnsavedChanges));
                SaveCommand.NotifyCanExecuteChanged();
                ResetCommand.NotifyCanExecuteChanged();
            }
        }
        finally
        {
            _isUndoingOrRedoing = false;
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        }
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;

        _isUndoingOrRedoing = true;
        try
        {
            var next = _redoStack.Pop();
            _undoStack.Push(_captionText);
            if (SetProperty(ref _captionText, next, nameof(CaptionText)))
            {
                OnPropertyChanged(nameof(HasUnsavedChanges));
                SaveCommand.NotifyCanExecuteChanged();
                ResetCommand.NotifyCanExecuteChanged();
            }
        }
        finally
        {
            _isUndoingOrRedoing = false;
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Finds the image file that corresponds to a caption file (same base name, image extension).
    /// </summary>
    private static string? ResolveCorrespondingImage(string captionPath)
    {
        var directory = Path.GetDirectoryName(captionPath);
        if (string.IsNullOrEmpty(directory))
            return null;

        var baseName = Path.GetFileNameWithoutExtension(captionPath);

        foreach (var ext in SupportedMediaTypes.ImageExtensions)
        {
            var candidate = Path.Combine(directory, baseName + ext);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
