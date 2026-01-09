using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// ViewModel for the Notes sub-tab within dataset version detail view.
/// Provides a journal-like interface with a list of notes and a text editor.
/// </summary>
public partial class NotesTabViewModel : ObservableObject, IDialogServiceAware
{
    private string _notesFolderPath = string.Empty;
    private NoteViewModel? _selectedNote;
    private string _editorContent = string.Empty;
    private bool _isLoading;
    private string? _statusMessage;
    private readonly IDatasetEventAggregator _eventAggregator;

    /// <summary>
    /// Gets or sets the dialog service for confirmations.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// Collection of notes in the current version.
    /// </summary>
    public ObservableCollection<NoteViewModel> Notes { get; } = [];

    /// <summary>
    /// Path to the Notes folder for the current version.
    /// </summary>
    public string NotesFolderPath
    {
        get => _notesFolderPath;
        set => SetProperty(ref _notesFolderPath, value);
    }

    /// <summary>
    /// Currently selected note for editing.
    /// </summary>
    public NoteViewModel? SelectedNote
    {
        get => _selectedNote;
        set
        {
            // Auto-save previous note if it has changes
            if (_selectedNote is not null && _selectedNote.HasUnsavedChanges)
            {
                _selectedNote.Save();
            }

            if (SetProperty(ref _selectedNote, value))
            {
                EditorContent = value?.Content ?? string.Empty;
                OnPropertyChanged(nameof(HasSelectedNote));
                OnPropertyChanged(nameof(SelectedNoteTitle));
            }
        }
    }

    /// <summary>
    /// Content in the text editor (bound to the selected note).
    /// </summary>
    public string EditorContent
    {
        get => _editorContent;
        set
        {
            if (SetProperty(ref _editorContent, value))
            {
                // Update the selected note's content
                if (_selectedNote is not null)
                {
                    _selectedNote.UpdateContent(value);
                }
            }
        }
    }

    /// <summary>
    /// Whether notes are currently loading.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// <summary>
    /// Status message for user feedback.
    /// </summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Whether there are no notes.
    /// </summary>
    public bool HasNoNotes => Notes.Count == 0;

    /// <summary>
    /// Whether there are notes.
    /// </summary>
    public bool HasNotes => Notes.Count > 0;

    /// <summary>
    /// Whether a note is currently selected.
    /// </summary>
    public bool HasSelectedNote => _selectedNote is not null;

    /// <summary>
    /// Title of the selected note.
    /// </summary>
    public string SelectedNoteTitle => _selectedNote?.Title ?? "No note selected";

    /// <summary>
    /// Whether any note has unsaved changes.
    /// </summary>
    public bool HasUnsavedChanges => Notes.Any(n => n.HasUnsavedChanges);

    // Commands
    public IRelayCommand CreateNoteCommand { get; }
    public IAsyncRelayCommand<NoteViewModel?> DeleteNoteCommand { get; }
    public IRelayCommand SaveCurrentNoteCommand { get; }
    public IRelayCommand SaveAllNotesCommand { get; }
    public IRelayCommand RefreshCommand { get; }

    public NotesTabViewModel(IDatasetEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));

        CreateNoteCommand = new RelayCommand(CreateNote);
        DeleteNoteCommand = new AsyncRelayCommand<NoteViewModel?>(DeleteNoteAsync);
        SaveCurrentNoteCommand = new RelayCommand(SaveCurrentNote, () => _selectedNote?.HasUnsavedChanges == true);
        SaveAllNotesCommand = new RelayCommand(SaveAllNotes, () => HasUnsavedChanges);
        RefreshCommand = new RelayCommand(LoadNotes);
    }

    /// <summary>
    /// Initializes the tab for a specific version folder.
    /// </summary>
    /// <param name="versionFolderPath">Path to the version folder (e.g., Dataset/V1).</param>
    public void Initialize(string versionFolderPath)
    {
        NotesFolderPath = Path.Combine(versionFolderPath, "Notes");
        LoadNotes();
    }

    /// <summary>
    /// Loads notes from the Notes folder.
    /// </summary>
    public void LoadNotes()
    {
        // Save any pending changes first
        SaveAllNotes();

        Notes.Clear();
        SelectedNote = null;

        if (!Directory.Exists(NotesFolderPath))
        {
            NotifyCollectionChanged();
            return;
        }

        try
        {
            var files = Directory.EnumerateFiles(NotesFolderPath, "*.txt")
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .ToList();

            foreach (var filePath in files)
            {
                var item = NoteItem.FromFile(filePath);
                Notes.Add(new NoteViewModel(item, this));
            }

            // Select the first note if available
            if (Notes.Count > 0)
            {
                SelectedNote = Notes[0];
            }

            StatusMessage = Notes.Count > 0
                ? $"Loaded {Notes.Count} note(s)"
                : null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading notes: {ex.Message}";
        }

        NotifyCollectionChanged();
    }

    /// <summary>
    /// Creates a new note.
    /// </summary>
    private void CreateNote()
    {
        // Ensure folder exists
        Directory.CreateDirectory(NotesFolderPath);

        var newItem = NoteItem.CreateNew(NotesFolderPath);
        var noteVm = new NoteViewModel(newItem, this);
        
        Notes.Insert(0, noteVm);
        SelectedNote = noteVm;

        StatusMessage = "Created new note";
        NotifyCollectionChanged();
    }

    /// <summary>
    /// Deletes a note.
    /// </summary>
    private async Task DeleteNoteAsync(NoteViewModel? noteVm)
    {
        if (noteVm is null || DialogService is null) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Note",
            $"Delete '{noteVm.Title}'?\n\nThis action cannot be undone.");

        if (!confirm) return;

        try
        {
            if (File.Exists(noteVm.FilePath))
            {
                File.Delete(noteVm.FilePath);
            }

            Notes.Remove(noteVm);
            
            if (SelectedNote == noteVm)
            {
                SelectedNote = Notes.FirstOrDefault();
            }

            StatusMessage = $"Deleted note";
            NotifyCollectionChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error deleting note: {ex.Message}";
        }
    }

    /// <summary>
    /// Saves the currently selected note.
    /// </summary>
    private void SaveCurrentNote()
    {
        if (_selectedNote is null || !_selectedNote.HasUnsavedChanges) return;

        try
        {
            _selectedNote.Save();
            StatusMessage = "Note saved";
            NotifyCommandsCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error saving note: {ex.Message}";
        }
    }

    /// <summary>
    /// Saves all notes with unsaved changes.
    /// </summary>
    private void SaveAllNotes()
    {
        var saved = 0;
        foreach (var note in Notes.Where(n => n.HasUnsavedChanges))
        {
            try
            {
                note.Save();
                saved++;
            }
            catch
            {
                // Continue with other notes
            }
        }

        if (saved > 0)
        {
            StatusMessage = $"Saved {saved} note(s)";
        }

        NotifyCommandsCanExecuteChanged();
    }

    internal void NotifyNoteChanged(NoteViewModel note)
    {
        NotifyCommandsCanExecuteChanged();
        
        // Update editor content if this is the selected note
        if (note == _selectedNote)
        {
            OnPropertyChanged(nameof(SelectedNoteTitle));
        }
    }

    private void NotifyCollectionChanged()
    {
        OnPropertyChanged(nameof(HasNoNotes));
        OnPropertyChanged(nameof(HasNotes));
        NotifyCommandsCanExecuteChanged();
    }

    private void NotifyCommandsCanExecuteChanged()
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        SaveCurrentNoteCommand.NotifyCanExecuteChanged();
        SaveAllNotesCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// ViewModel wrapper for individual notes.
/// </summary>
public partial class NoteViewModel : ObservableObject
{
    private readonly NotesTabViewModel _parent;
    private readonly NoteItem _item;

    public string Id => _item.Id;
    public string Title => _item.Title;
    public string Preview => _item.Preview;
    public string FileName => _item.FileName;
    public string FilePath => _item.FilePath;
    public DateTime ModifiedAt => _item.ModifiedAt;
    public bool HasUnsavedChanges => _item.HasUnsavedChanges;

    public string Content
    {
        get => _item.Content;
        set
        {
            if (_item.Content != value)
            {
                _item.UpdateContent(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Preview));
                OnPropertyChanged(nameof(HasUnsavedChanges));
                _parent.NotifyNoteChanged(this);
            }
        }
    }

    public NoteViewModel(NoteItem item, NotesTabViewModel parent)
    {
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
    }

    /// <summary>
    /// Updates the content from external source (e.g., editor binding).
    /// </summary>
    public void UpdateContent(string newContent)
    {
        if (_item.Content != newContent)
        {
            _item.UpdateContent(newContent);
            OnPropertyChanged(nameof(Content));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Preview));
            OnPropertyChanged(nameof(HasUnsavedChanges));
            _parent.NotifyNoteChanged(this);
        }
    }

    /// <summary>
    /// Saves the note to disk.
    /// </summary>
    public void Save()
    {
        _item.Save();
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(ModifiedAt));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Preview));
    }
}
