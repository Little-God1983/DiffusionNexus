using DiffusionNexus.Domain.Entities;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.LoraDatasetHelper.ViewModels;

/// <summary>
/// Unit tests for <see cref="NotesTabViewModel"/>.
/// Tests initialization, note loading, creation, editing, and saving.
/// </summary>
public class NotesTabViewModelTests : IDisposable
{
    private readonly string _testTempPath;
    private readonly Mock<IDatasetEventAggregator> _mockEventAggregator;

    public NotesTabViewModelTests()
    {
        _testTempPath = Path.Combine(Path.GetTempPath(), $"NotesTabTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempPath);
        _mockEventAggregator = new Mock<IDatasetEventAggregator>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testTempPath))
        {
            try
            {
                Directory.Delete(_testTempPath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors in tests
            }
        }
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullEventAggregator_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new NotesTabViewModel(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("eventAggregator");
    }

    [Fact]
    public void Constructor_InitializesWithEmptyCollection()
    {
        // Act
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);

        // Assert
        vm.Notes.Should().BeEmpty();
        vm.HasNoNotes.Should().BeTrue();
        vm.HasNotes.Should().BeFalse();
        vm.SelectedNote.Should().BeNull();
        vm.HasSelectedNote.Should().BeFalse();
        vm.EditorContent.Should().BeEmpty();
    }

    #endregion

    #region Initialize Tests

    [Fact]
    public void Initialize_SetsNotesFolderPath()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(versionPath);

        // Act
        vm.Initialize(versionPath);

        // Assert
        vm.NotesFolderPath.Should().Be(Path.Combine(versionPath, "Notes"));
    }

    [Fact]
    public void Initialize_WhenNotesFolderDoesNotExist_LoadsEmptyCollection()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_NoNotes");
        Directory.CreateDirectory(versionPath);

        // Act
        vm.Initialize(versionPath);

        // Assert
        vm.Notes.Should().BeEmpty();
        vm.HasNoNotes.Should().BeTrue();
    }

    [Fact]
    public void Initialize_WhenNotesFolderHasFiles_LoadsNotes()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_WithNotes");
        var notesPath = Path.Combine(versionPath, "Notes");
        Directory.CreateDirectory(notesPath);

        // Create test note files
        File.WriteAllText(Path.Combine(notesPath, "note_2024-01-01.txt"), "First note content");
        File.WriteAllText(Path.Combine(notesPath, "note_2024-01-02.txt"), "Second note content");

        // Act
        vm.Initialize(versionPath);

        // Assert
        vm.Notes.Should().HaveCount(2);
        vm.HasNotes.Should().BeTrue();
        vm.HasNoNotes.Should().BeFalse();
    }

    [Fact]
    public void Initialize_SelectsFirstNoteByDefault()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_SelectFirst");
        var notesPath = Path.Combine(versionPath, "Notes");
        Directory.CreateDirectory(notesPath);

        File.WriteAllText(Path.Combine(notesPath, "note_001.txt"), "First note");
        File.WriteAllText(Path.Combine(notesPath, "note_002.txt"), "Second note");

        // Act
        vm.Initialize(versionPath);

        // Assert
        vm.SelectedNote.Should().NotBeNull();
        vm.HasSelectedNote.Should().BeTrue();
    }

    [Fact]
    public void Initialize_IgnoresNonTxtFiles()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_Mixed");
        var notesPath = Path.Combine(versionPath, "Notes");
        Directory.CreateDirectory(notesPath);

        File.WriteAllText(Path.Combine(notesPath, "note.txt"), "Valid note");
        File.WriteAllText(Path.Combine(notesPath, "readme.md"), "Markdown file");
        File.WriteAllText(Path.Combine(notesPath, "config.json"), "{}");

        // Act
        vm.Initialize(versionPath);

        // Assert
        vm.Notes.Should().HaveCount(1);
    }

    #endregion

    #region CreateNote Tests

    [Fact]
    public void CreateNoteCommand_CreatesNewNote()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_CreateNote");
        Directory.CreateDirectory(versionPath);
        vm.Initialize(versionPath);

        // Act
        vm.CreateNoteCommand.Execute(null);

        // Assert
        vm.Notes.Should().HaveCount(1);
        vm.HasNotes.Should().BeTrue();
    }

    [Fact]
    public void CreateNoteCommand_SelectsNewNote()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_SelectNewNote");
        Directory.CreateDirectory(versionPath);
        vm.Initialize(versionPath);

        // Act
        vm.CreateNoteCommand.Execute(null);

        // Assert
        vm.SelectedNote.Should().NotBeNull();
        vm.SelectedNote.Should().Be(vm.Notes[0]);
    }

    [Fact]
    public void CreateNoteCommand_InsertsAtBeginning()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_InsertNote");
        var notesPath = Path.Combine(versionPath, "Notes");
        Directory.CreateDirectory(notesPath);

        File.WriteAllText(Path.Combine(notesPath, "existing_note.txt"), "Existing note");
        vm.Initialize(versionPath);

        // Act
        vm.CreateNoteCommand.Execute(null);

        // Assert
        vm.Notes[0].HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void CreateNoteCommand_CreatesNotesFolderIfNotExists()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_NoFolder");
        Directory.CreateDirectory(versionPath);
        vm.Initialize(versionPath);

        // Act
        vm.CreateNoteCommand.Execute(null);

        // Assert
        Directory.Exists(vm.NotesFolderPath).Should().BeTrue();
    }

    #endregion

    #region SelectedNote Tests

    [Fact]
    public void SelectedNote_WhenChanged_UpdatesEditorContent()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_Editor");
        var notesPath = Path.Combine(versionPath, "Notes");
        Directory.CreateDirectory(notesPath);

        File.WriteAllText(Path.Combine(notesPath, "note_001.txt"), "First note content");
        File.WriteAllText(Path.Combine(notesPath, "note_002.txt"), "Second note content");
        vm.Initialize(versionPath);

        // Act
        var secondNote = vm.Notes.First(n => n.Content == "Second note content");
        vm.SelectedNote = secondNote;

        // Assert
        vm.EditorContent.Should().Be("Second note content");
    }

    [Fact]
    public void SelectedNote_WhenNull_ClearsEditorContent()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_ClearEditor");
        var notesPath = Path.Combine(versionPath, "Notes");
        Directory.CreateDirectory(notesPath);

        File.WriteAllText(Path.Combine(notesPath, "note.txt"), "Some content");
        vm.Initialize(versionPath);
        vm.SelectedNote.Should().NotBeNull();

        // Act
        vm.SelectedNote = null;

        // Assert
        vm.EditorContent.Should().BeEmpty();
        vm.HasSelectedNote.Should().BeFalse();
    }

    [Fact]
    public void SelectedNoteTitle_ReturnsNoteTitle()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_Title");
        var notesPath = Path.Combine(versionPath, "Notes");
        Directory.CreateDirectory(notesPath);

        File.WriteAllText(Path.Combine(notesPath, "note.txt"), "My Note Title\nSome body content");
        vm.Initialize(versionPath);

        // Assert
        vm.SelectedNoteTitle.Should().Be("My Note Title");
    }

    [Fact]
    public void SelectedNoteTitle_WhenNoSelection_ReturnsDefaultText()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);

        // Assert
        vm.SelectedNoteTitle.Should().Be("No note selected");
    }

    #endregion

    #region EditorContent Tests

    [Fact]
    public void EditorContent_WhenChanged_UpdatesSelectedNoteContent()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_EditorUpdate");
        var notesPath = Path.Combine(versionPath, "Notes");
        Directory.CreateDirectory(notesPath);

        File.WriteAllText(Path.Combine(notesPath, "note.txt"), "Original content");
        vm.Initialize(versionPath);

        // Act
        vm.EditorContent = "Updated content";

        // Assert
        vm.SelectedNote!.Content.Should().Be("Updated content");
        vm.SelectedNote.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void EditorContent_WhenNoSelectedNote_DoesNotThrow()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);

        // Act
        var act = () => vm.EditorContent = "Some content";

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region HasUnsavedChanges Tests

    [Fact]
    public void HasUnsavedChanges_WhenNewNoteCreated_ReturnsTrue()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_Unsaved");
        Directory.CreateDirectory(versionPath);
        vm.Initialize(versionPath);

        // Act
        vm.CreateNoteCommand.Execute(null);

        // Assert
        vm.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void HasUnsavedChanges_WhenNoteModified_ReturnsTrue()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_Modified");
        var notesPath = Path.Combine(versionPath, "Notes");
        Directory.CreateDirectory(notesPath);

        File.WriteAllText(Path.Combine(notesPath, "note.txt"), "Original");
        vm.Initialize(versionPath);
        vm.HasUnsavedChanges.Should().BeFalse();

        // Act
        vm.EditorContent = "Modified";

        // Assert
        vm.HasUnsavedChanges.Should().BeTrue();
    }

    #endregion

    #region LoadNotes Tests

    [Fact]
    public void LoadNotes_SortsByModifiedDateDescending()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_Sorting");
        var notesPath = Path.Combine(versionPath, "Notes");
        Directory.CreateDirectory(notesPath);

        var oldNotePath = Path.Combine(notesPath, "old_note.txt");
        File.WriteAllText(oldNotePath, "Old note");
        File.SetLastWriteTime(oldNotePath, DateTime.Now.AddDays(-7));

        var newNotePath = Path.Combine(notesPath, "new_note.txt");
        File.WriteAllText(newNotePath, "New note");
        File.SetLastWriteTime(newNotePath, DateTime.Now);

        // Act
        vm.Initialize(versionPath);

        // Assert - newest should be first
        vm.Notes[0].Content.Should().Be("New note");
        vm.Notes[1].Content.Should().Be("Old note");
    }

    [Fact]
    public void LoadNotes_ClearsExistingNotes()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_Clear");
        var notesPath = Path.Combine(versionPath, "Notes");
        Directory.CreateDirectory(notesPath);

        File.WriteAllText(Path.Combine(notesPath, "note.txt"), "Content");
        vm.Initialize(versionPath);
        vm.Notes.Should().HaveCount(1);

        // Delete the file
        File.Delete(Path.Combine(notesPath, "note.txt"));

        // Act
        vm.RefreshCommand.Execute(null);

        // Assert
        vm.Notes.Should().BeEmpty();
    }

    #endregion

    #region StatusMessage Tests

    [Fact]
    public void StatusMessage_WhenNotesLoaded_ShowsCount()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_Status");
        var notesPath = Path.Combine(versionPath, "Notes");
        Directory.CreateDirectory(notesPath);

        File.WriteAllText(Path.Combine(notesPath, "note1.txt"), "Note 1");
        File.WriteAllText(Path.Combine(notesPath, "note2.txt"), "Note 2");

        // Act
        vm.Initialize(versionPath);

        // Assert
        vm.StatusMessage.Should().Contain("2");
    }

    [Fact]
    public void StatusMessage_WhenNoteCreated_ShowsCreatedMessage()
    {
        // Arrange
        var vm = new NotesTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_CreateStatus");
        Directory.CreateDirectory(versionPath);
        vm.Initialize(versionPath);

        // Act
        vm.CreateNoteCommand.Execute(null);

        // Assert
        vm.StatusMessage.Should().Contain("Created");
    }

    #endregion
}

/// <summary>
/// Unit tests for <see cref="NoteViewModel"/>.
/// </summary>
public class NoteViewModelTests : IDisposable
{
    private readonly string _testTempPath;
    private readonly Mock<IDatasetEventAggregator> _mockEventAggregator;

    public NoteViewModelTests()
    {
        _testTempPath = Path.Combine(Path.GetTempPath(), $"NoteVmTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempPath);
        _mockEventAggregator = new Mock<IDatasetEventAggregator>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testTempPath))
        {
            try
            {
                Directory.Delete(_testTempPath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void Constructor_WithNullItem_ThrowsArgumentNullException()
    {
        // Arrange
        var parent = new NotesTabViewModel(_mockEventAggregator.Object);

        // Act
        var act = () => new NoteViewModel(null!, parent);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("item");
    }

    [Fact]
    public void Constructor_WithNullParent_ThrowsArgumentNullException()
    {
        // Arrange
        var item = new NoteItem { FileName = "test.txt" };

        // Act
        var act = () => new NoteViewModel(item, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("parent");
    }

    [Fact]
    public void Properties_DelegateToItem()
    {
        // Arrange
        var notesPath = Path.Combine(_testTempPath, "Notes");
        Directory.CreateDirectory(notesPath);
        var filePath = Path.Combine(notesPath, "test_note.txt");
        File.WriteAllText(filePath, "Test content for note");

        var item = NoteItem.FromFile(filePath);
        var parent = new NotesTabViewModel(_mockEventAggregator.Object);

        // Act
        var vm = new NoteViewModel(item, parent);

        // Assert
        vm.Id.Should().Be(item.Id);
        vm.Title.Should().Be(item.Title);
        vm.Preview.Should().Be(item.Preview);
        vm.FileName.Should().Be(item.FileName);
        vm.FilePath.Should().Be(item.FilePath);
        vm.Content.Should().Be(item.Content);
    }

    [Fact]
    public void UpdateContent_SetsHasUnsavedChanges()
    {
        // Arrange
        var notesPath = Path.Combine(_testTempPath, "Notes_Update");
        Directory.CreateDirectory(notesPath);
        var filePath = Path.Combine(notesPath, "note.txt");
        File.WriteAllText(filePath, "Original");

        var item = NoteItem.FromFile(filePath);
        var parent = new NotesTabViewModel(_mockEventAggregator.Object);
        var vm = new NoteViewModel(item, parent);

        // Act
        vm.UpdateContent("Modified");

        // Assert
        vm.HasUnsavedChanges.Should().BeTrue();
        vm.Content.Should().Be("Modified");
    }

    [Fact]
    public void Save_PersistsContentToDisk()
    {
        // Arrange
        var notesPath = Path.Combine(_testTempPath, "Notes_Save");
        Directory.CreateDirectory(notesPath);
        var filePath = Path.Combine(notesPath, "note.txt");
        File.WriteAllText(filePath, "Original");

        var item = NoteItem.FromFile(filePath);
        var parent = new NotesTabViewModel(_mockEventAggregator.Object);
        var vm = new NoteViewModel(item, parent);

        vm.UpdateContent("Updated content");

        // Act
        vm.Save();

        // Assert
        vm.HasUnsavedChanges.Should().BeFalse();
        File.ReadAllText(filePath).Should().Be("Updated content");
    }

    [Fact]
    public void Content_WhenChanged_UpdatesTitleAndPreview()
    {
        // Arrange
        var notesPath = Path.Combine(_testTempPath, "Notes_TitlePreview");
        Directory.CreateDirectory(notesPath);
        var filePath = Path.Combine(notesPath, "note.txt");
        File.WriteAllText(filePath, "Original Title\nOriginal body");

        var item = NoteItem.FromFile(filePath);
        var parent = new NotesTabViewModel(_mockEventAggregator.Object);
        var vm = new NoteViewModel(item, parent);

        // Act
        vm.UpdateContent("New Title\nNew body content");

        // Assert
        vm.Title.Should().Be("New Title");
        vm.Preview.Should().Contain("New Title");
    }
}
