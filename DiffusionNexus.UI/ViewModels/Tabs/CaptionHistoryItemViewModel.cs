using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Represents a single completed caption result in the processing history.
/// Supports inline caption editing with undo/redo/reset/save.
/// </summary>
public sealed class CaptionHistoryItemViewModel : ViewModelBase, IDisposable
{
    private const int PreviewLength = 160;

    private bool _isExpanded;
    private bool _disposed;
    private string _editableCaption;
    private readonly string _originalCaption;
    private bool _hasUnsavedChanges;
    private bool _isUndoingOrRedoing;
    private bool _isCaptionCompleted;
    private bool _isProcessing;
    private readonly Stack<string> _undoStack = new();
    private readonly Stack<string> _redoStack = new();

    /// <summary>
    /// Creates a new history item from a completed captioning result.
    /// </summary>
    public CaptionHistoryItemViewModel(string imagePath, string caption, Bitmap? thumbnail)
    {
        ImagePath = imagePath;
        FileName = Path.GetFileName(imagePath);
        FullCaption = caption;
        _originalCaption = caption;
        _editableCaption = caption;
        CaptionPreview = caption.Length > PreviewLength
            ? string.Concat(caption.AsSpan(0, PreviewLength), "...")
            : caption;
        HasMoreText = caption.Length > PreviewLength;
        Thumbnail = thumbnail;

        UndoCaptionCommand = new RelayCommand(UndoCaption, () => _undoStack.Count > 0);
        RedoCaptionCommand = new RelayCommand(RedoCaption, () => _redoStack.Count > 0);
        RevertCaptionCommand = new RelayCommand(RevertCaption, () => HasUnsavedChanges);
        SaveCaptionCommand = new RelayCommand(SaveCaption, () => HasUnsavedChanges);
    }

    /// <summary>
    /// Full path to the source image.
    /// </summary>
    public string ImagePath { get; }

    /// <summary>
    /// Display file name.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Full generated caption text (original, immutable).
    /// </summary>
    public string FullCaption { get; }

    /// <summary>
    /// Truncated caption for the list view.
    /// </summary>
    public string CaptionPreview { get; }

    /// <summary>
    /// Whether the full caption is longer than the preview.
    /// </summary>
    public bool HasMoreText { get; }

    /// <summary>
    /// Thumbnail bitmap for the list view.
    /// </summary>
    public Bitmap? Thumbnail { get; }

    /// <summary>
    /// Whether the full caption is shown inline.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Whether this item's caption has been generated (not currently processing).
    /// </summary>
    public bool IsCaptionCompleted
    {
        get => _isCaptionCompleted;
        set => SetProperty(ref _isCaptionCompleted, value);
    }

    /// <summary>
    /// Whether this item is currently being processed by the captioning backend.
    /// </summary>
    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }

    /// <summary>
    /// The editable caption text. Tracks undo/redo and unsaved changes.
    /// </summary>
    public string EditableCaption
    {
        get => _editableCaption;
        set
        {
            if (_isUndoingOrRedoing) return;

            if (SetProperty(ref _editableCaption, value))
            {
                _undoStack.Push(_editableCaption);
                _redoStack.Clear();
                HasUnsavedChanges = _editableCaption != _originalCaption;
                UndoCaptionCommand.NotifyCanExecuteChanged();
                RedoCaptionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Whether the caption has been modified since last save.
    /// </summary>
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (SetProperty(ref _hasUnsavedChanges, value))
            {
                RevertCaptionCommand.NotifyCanExecuteChanged();
                SaveCaptionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The caption text to display based on expanded state.
    /// </summary>
    public string DisplayCaption => IsExpanded ? FullCaption : CaptionPreview;

    /// <summary>
    /// Command to undo the last caption edit.
    /// </summary>
    public IRelayCommand UndoCaptionCommand { get; }

    /// <summary>
    /// Command to redo a previously undone caption edit.
    /// </summary>
    public IRelayCommand RedoCaptionCommand { get; }

    /// <summary>
    /// Command to revert caption to the original generated text.
    /// </summary>
    public IRelayCommand RevertCaptionCommand { get; }

    /// <summary>
    /// Command to save the edited caption to disk.
    /// </summary>
    public IRelayCommand SaveCaptionCommand { get; }

    /// <summary>
    /// Updates the caption after generation completes. Resets undo/redo state.
    /// </summary>
    public void SetCompletedCaption(string caption)
    {
        _isUndoingOrRedoing = true;
        try
        {
            _undoStack.Clear();
            _redoStack.Clear();
            SetProperty(ref _editableCaption, caption, nameof(EditableCaption));
            HasUnsavedChanges = false;
            IsProcessing = false;
            IsCaptionCompleted = true;
        }
        finally
        {
            _isUndoingOrRedoing = false;
            UndoCaptionCommand.NotifyCanExecuteChanged();
            RedoCaptionCommand.NotifyCanExecuteChanged();
        }
    }

    private void UndoCaption()
    {
        if (_undoStack.Count == 0) return;

        _isUndoingOrRedoing = true;
        try
        {
            var previous = _undoStack.Pop();
            _redoStack.Push(_editableCaption);
            if (SetProperty(ref _editableCaption, previous, nameof(EditableCaption)))
            {
                HasUnsavedChanges = _editableCaption != _originalCaption;
            }
        }
        finally
        {
            _isUndoingOrRedoing = false;
            UndoCaptionCommand.NotifyCanExecuteChanged();
            RedoCaptionCommand.NotifyCanExecuteChanged();
        }
    }

    private void RedoCaption()
    {
        if (_redoStack.Count == 0) return;

        _isUndoingOrRedoing = true;
        try
        {
            var next = _redoStack.Pop();
            _undoStack.Push(_editableCaption);
            if (SetProperty(ref _editableCaption, next, nameof(EditableCaption)))
            {
                HasUnsavedChanges = _editableCaption != _originalCaption;
            }
        }
        finally
        {
            _isUndoingOrRedoing = false;
            UndoCaptionCommand.NotifyCanExecuteChanged();
            RedoCaptionCommand.NotifyCanExecuteChanged();
        }
    }

    private void RevertCaption()
    {
        _isUndoingOrRedoing = true;
        try
        {
            _undoStack.Clear();
            _redoStack.Clear();
            if (SetProperty(ref _editableCaption, _originalCaption, nameof(EditableCaption)))
            {
                HasUnsavedChanges = false;
            }
        }
        finally
        {
            _isUndoingOrRedoing = false;
            UndoCaptionCommand.NotifyCanExecuteChanged();
            RedoCaptionCommand.NotifyCanExecuteChanged();
        }
    }

    private void SaveCaption()
    {
        try
        {
            var dir = Path.GetDirectoryName(ImagePath) ?? ".";
            var captionPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(ImagePath) + ".txt");
            File.WriteAllText(captionPath, _editableCaption);
            HasUnsavedChanges = false;
        }
        catch (IOException)
        {
            // File may be in use or read-only
        }
        catch (UnauthorizedAccessException)
        {
            // No permission to write
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        Thumbnail?.Dispose();
        _disposed = true;
    }
}
