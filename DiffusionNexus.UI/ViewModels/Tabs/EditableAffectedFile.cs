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
    private readonly int? _recommendedMinWords;
    private readonly int? _recommendedMaxWords;
    private readonly int _initialWordCount;
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
    /// <param name="recommendedMinWords">Optional lower bound of the recommended word count range.</param>
    /// <param name="recommendedMaxWords">Optional upper bound of the recommended word count range.</param>
    public EditableAffectedFile(
        string filePath,
        Func<EditableAffectedFile, Task>? onSaved = null,
        int? recommendedMinWords = null,
        int? recommendedMaxWords = null)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        ImagePath = ResolveCorrespondingImage(filePath);
        _onSaved = onSaved;
        _recommendedMinWords = recommendedMinWords;
        _recommendedMaxWords = recommendedMaxWords;
        _initialWordCount = PreCountWords(filePath);

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
                OnPropertyChanged(nameof(WordCount));
                OnPropertyChanged(nameof(IsWithinRecommendedRange));
                OnPropertyChanged(nameof(LengthDirectionIndicator));
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
    /// Current word count of the caption text.
    /// Uses a pre-computed value from disk before the file is fully loaded for editing.
    /// </summary>
    public int WordCount => _isLoaded ? CountWords(_captionText) : _initialWordCount;

    /// <summary>
    /// Whether a recommended word count range was provided for this file.
    /// </summary>
    public bool HasRecommendedRange => _recommendedMinWords.HasValue && _recommendedMaxWords.HasValue;

    /// <summary>
    /// Whether the current word count falls within the recommended range.
    /// Always <c>false</c> when no recommended range is set.
    /// </summary>
    public bool IsWithinRecommendedRange =>
        HasRecommendedRange
        && WordCount >= _recommendedMinWords!.Value
        && WordCount <= _recommendedMaxWords!.Value;

    /// <summary>
    /// Visual direction indicator: "←" when the caption is too long,
    /// "→" when too short, or empty when within range or no range is set.
    /// </summary>
    public string LengthDirectionIndicator
    {
        get
        {
            if (!HasRecommendedRange || IsWithinRecommendedRange)
                return string.Empty;

            return WordCount > _recommendedMaxWords!.Value ? "←" : "→";
        }
    }

    /// <summary>
    /// Display string for the recommended word range (e.g. "72–204"), or empty when no range is set.
    /// </summary>
    public string RecommendedRangeDisplay =>
        HasRecommendedRange
            ? $"{_recommendedMinWords}–{_recommendedMaxWords}"
            : string.Empty;

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

    /// <summary>
    /// Expands this file's editor, lazy-loading content on first access.
    /// </summary>
    public void Expand()
    {
        if (!_isLoaded)
        {
            LoadFromDisk();
        }

        IsExpanded = true;
    }

    /// <summary>
    /// Collapses this file's editor.
    /// </summary>
    public void Collapse()
    {
        IsExpanded = false;
    }

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
            OnPropertyChanged(nameof(WordCount));
            OnPropertyChanged(nameof(IsWithinRecommendedRange));
            OnPropertyChanged(nameof(LengthDirectionIndicator));
            return;
        }

        var text = File.ReadAllText(FilePath);
        _originalText = text;
        _captionText = text;
        _isLoaded = true;
        OnPropertyChanged(nameof(CaptionText));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(WordCount));
        OnPropertyChanged(nameof(IsWithinRecommendedRange));
        OnPropertyChanged(nameof(LengthDirectionIndicator));
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
                OnPropertyChanged(nameof(WordCount));
                OnPropertyChanged(nameof(IsWithinRecommendedRange));
                OnPropertyChanged(nameof(LengthDirectionIndicator));
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
                OnPropertyChanged(nameof(WordCount));
                OnPropertyChanged(nameof(IsWithinRecommendedRange));
                OnPropertyChanged(nameof(LengthDirectionIndicator));
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
    /// Reads a caption file from disk and returns the word count without loading editing state.
    /// </summary>
    private static int PreCountWords(string filePath)
    {
        if (!File.Exists(filePath))
            return 0;

        var text = File.ReadAllText(filePath);
        return CountWords(text);
    }

    /// <summary>
    /// Counts whitespace-delimited words in a text string.
    /// </summary>
    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return text.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
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
