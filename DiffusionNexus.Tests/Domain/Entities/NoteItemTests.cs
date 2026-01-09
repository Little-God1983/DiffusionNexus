using DiffusionNexus.Domain.Entities;
using FluentAssertions;

namespace DiffusionNexus.Tests.Domain.Entities;

/// <summary>
/// Unit tests for <see cref="NoteItem"/>.
/// Tests creation, content manipulation, title extraction, and saving.
/// </summary>
public class NoteItemTests : IDisposable
{
    private readonly string _testTempPath;

    public NoteItemTests()
    {
        _testTempPath = Path.Combine(Path.GetTempPath(), $"NoteItemTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempPath);
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

    #region FromFile Tests

    [Fact]
    public void FromFile_WithNullPath_ThrowsArgumentNullException()
    {
        var act = () => NoteItem.FromFile(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromFile_LoadsContentFromFile()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "test_note.txt");
        File.WriteAllText(filePath, "Test note content");

        // Act
        var item = NoteItem.FromFile(filePath);

        // Assert
        item.Content.Should().Be("Test note content");
        item.FilePath.Should().Be(filePath);
        item.FileName.Should().Be("test_note.txt");
        item.Id.Should().Be("test_note");
    }

    [Fact]
    public void FromFile_WhenFileDoesNotExist_SetsEmptyContent()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "nonexistent.txt");

        // Act
        var item = NoteItem.FromFile(filePath);

        // Assert
        item.Content.Should().BeEmpty();
    }

    [Fact]
    public void FromFile_SetsHasUnsavedChangesToFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "saved_note.txt");
        File.WriteAllText(filePath, "Saved content");

        // Act
        var item = NoteItem.FromFile(filePath);

        // Assert
        item.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void FromFile_ExtractsTitleFromFirstLine()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "titled_note.txt");
        File.WriteAllText(filePath, "My Note Title\nBody content goes here");

        // Act
        var item = NoteItem.FromFile(filePath);

        // Assert
        item.Title.Should().Be("My Note Title");
    }

    [Fact]
    public void FromFile_WhenContentEmpty_UsesFallbackTitle()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "empty_note.txt");
        File.WriteAllText(filePath, "");

        // Act
        var item = NoteItem.FromFile(filePath);

        // Assert
        item.Title.Should().Be("empty_note");
    }

    [Fact]
    public void FromFile_TruncatesLongTitles()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "long_title.txt");
        var longTitle = new string('A', 100);
        File.WriteAllText(filePath, longTitle);

        // Act
        var item = NoteItem.FromFile(filePath);

        // Assert
        item.Title.Should().HaveLength(53); // 50 chars + "..."
        item.Title.Should().EndWith("...");
    }

    #endregion

    #region CreateNew Tests

    [Fact]
    public void CreateNew_WithNullPath_ThrowsArgumentNullException()
    {
        var act = () => NoteItem.CreateNew(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateNew_CreatesNoteWithDefaultValues()
    {
        // Act
        var item = NoteItem.CreateNew(_testTempPath);

        // Assert
        item.Title.Should().Be("New Note");
        item.Content.Should().BeEmpty();
        item.Preview.Should().BeEmpty();
        item.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void CreateNew_SetsFilePath()
    {
        // Act
        var item = NoteItem.CreateNew(_testTempPath);

        // Assert
        item.FilePath.Should().StartWith(_testTempPath);
        item.FilePath.Should().EndWith(".txt");
        item.FileName.Should().StartWith("note_");
    }

    [Fact]
    public void CreateNew_SetsTimestamps()
    {
        // Act
        var item = NoteItem.CreateNew(_testTempPath);

        // Assert
        item.CreatedAt.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(5));
        item.ModifiedAt.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(5));
    }

    #endregion

    #region Save Tests

    [Fact]
    public void Save_PersistsContentToFile()
    {
        // Arrange
        var item = NoteItem.CreateNew(_testTempPath);
        item.UpdateContent("Test save content");

        // Act
        item.Save();

        // Assert
        File.Exists(item.FilePath).Should().BeTrue();
        File.ReadAllText(item.FilePath).Should().Be("Test save content");
    }

    [Fact]
    public void Save_SetsHasUnsavedChangesToFalse()
    {
        // Arrange
        var item = NoteItem.CreateNew(_testTempPath);
        item.UpdateContent("Content");
        item.HasUnsavedChanges.Should().BeTrue();

        // Act
        item.Save();

        // Assert
        item.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void Save_UpdatesModifiedAt()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "modify_date.txt");
        File.WriteAllText(filePath, "Original");
        var item = NoteItem.FromFile(filePath);
        var originalModified = item.ModifiedAt;

        // Wait a moment
        Thread.Sleep(50);

        item.UpdateContent("Modified");

        // Act
        item.Save();

        // Assert
        item.ModifiedAt.Should().BeAfter(originalModified);
    }

    [Fact]
    public void Save_CreatesFolderIfNotExists()
    {
        // Arrange
        var nestedPath = Path.Combine(_testTempPath, "nested", "folder");
        var item = NoteItem.CreateNew(nestedPath);
        item.UpdateContent("Content");

        // Act
        item.Save();

        // Assert
        Directory.Exists(nestedPath).Should().BeTrue();
        File.Exists(item.FilePath).Should().BeTrue();
    }

    [Fact]
    public void Save_UpdatesTitleAndPreview()
    {
        // Arrange
        var item = NoteItem.CreateNew(_testTempPath);
        item.UpdateContent("New Title\nBody content");

        // Act
        item.Save();

        // Assert
        item.Title.Should().Be("New Title");
        item.Preview.Should().Contain("New Title");
    }

    #endregion

    #region UpdateContent Tests

    [Fact]
    public void UpdateContent_SetsHasUnsavedChanges()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "update_content.txt");
        File.WriteAllText(filePath, "Original");
        var item = NoteItem.FromFile(filePath);

        // Act
        item.UpdateContent("Modified");

        // Assert
        item.HasUnsavedChanges.Should().BeTrue();
        item.Content.Should().Be("Modified");
    }

    [Fact]
    public void UpdateContent_WhenSameContent_DoesNotSetUnsaved()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "same_content.txt");
        File.WriteAllText(filePath, "Same");
        var item = NoteItem.FromFile(filePath);

        // Act
        item.UpdateContent("Same");

        // Assert
        item.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void UpdateContent_UpdatesTitleFromNewContent()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "title_update.txt");
        File.WriteAllText(filePath, "Old Title\nOld body");
        var item = NoteItem.FromFile(filePath);

        // Act
        item.UpdateContent("New Title\nNew body");

        // Assert
        item.Title.Should().Be("New Title");
    }

    [Fact]
    public void UpdateContent_UpdatesPreviewFromNewContent()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "preview_update.txt");
        File.WriteAllText(filePath, "Old content");
        var item = NoteItem.FromFile(filePath);

        // Act
        item.UpdateContent("New preview content here");

        // Assert
        item.Preview.Should().Contain("New preview");
    }

    #endregion

    #region Preview Tests

    [Fact]
    public void Preview_WhenContentEmpty_ReturnsEmptyString()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "empty_preview.txt");
        File.WriteAllText(filePath, "");

        // Act
        var item = NoteItem.FromFile(filePath);

        // Assert
        item.Preview.Should().BeEmpty();
    }

    [Fact]
    public void Preview_WhenContentShort_ReturnsFullContent()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "short_preview.txt");
        File.WriteAllText(filePath, "Short content");

        // Act
        var item = NoteItem.FromFile(filePath);

        // Assert
        item.Preview.Should().Be("Short content");
    }

    [Fact]
    public void Preview_WhenContentLong_TruncatesWithEllipsis()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "long_preview.txt");
        var longContent = string.Join(" ", Enumerable.Range(1, 50).Select(i => $"word{i}"));
        File.WriteAllText(filePath, longContent);

        // Act
        var item = NoteItem.FromFile(filePath);

        // Assert
        item.Preview.Should().EndWith("...");
    }

    [Fact]
    public void Preview_ReplacesNewlinesWithSpaces()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "newline_preview.txt");
        File.WriteAllText(filePath, "Line1\nLine2\nLine3");

        // Act
        var item = NoteItem.FromFile(filePath);

        // Assert
        item.Preview.Should().NotContain("\n");
        item.Preview.Should().Contain("Line1");
        item.Preview.Should().Contain("Line2");
    }

    #endregion

    #region Default Values Tests

    [Fact]
    public void DefaultConstructor_HasEmptyStrings()
    {
        var item = new NoteItem();

        item.Id.Should().BeEmpty();
        item.Title.Should().BeEmpty();
        item.Preview.Should().BeEmpty();
        item.Content.Should().BeEmpty();
        item.FilePath.Should().BeEmpty();
        item.FileName.Should().BeEmpty();
        item.HasUnsavedChanges.Should().BeFalse();
    }

    #endregion
}
