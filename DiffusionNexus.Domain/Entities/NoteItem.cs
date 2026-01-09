namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Represents a note/journal entry for a dataset version.
/// Notes are stored as individual .txt files in the Notes subfolder.
/// </summary>
public class NoteItem
{
    /// <summary>
    /// Unique identifier for the note (typically the filename without extension).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display title of the note (first line or first N words of content).
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Preview text (first ~50 characters of content for list display).
    /// </summary>
    public string Preview { get; set; } = string.Empty;

    /// <summary>
    /// Full text content of the note.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Full file path to the note file.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// File name with extension (e.g., "note_001.txt").
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// When the note was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the note was last modified.
    /// </summary>
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Whether the note has unsaved changes.
    /// </summary>
    public bool HasUnsavedChanges { get; set; }

    /// <summary>
    /// Creates a NoteItem from a file path, loading its content.
    /// </summary>
    /// <param name="filePath">Path to the note file.</param>
    /// <returns>A new NoteItem instance.</returns>
    public static NoteItem FromFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var fileInfo = new FileInfo(filePath);
        var content = File.Exists(filePath) ? File.ReadAllText(filePath) : string.Empty;
        var title = ExtractTitle(content, fileInfo.Name);
        var preview = ExtractPreview(content);

        return new NoteItem
        {
            Id = Path.GetFileNameWithoutExtension(filePath),
            Title = title,
            Preview = preview,
            Content = content,
            FilePath = filePath,
            FileName = fileInfo.Name,
            CreatedAt = fileInfo.Exists ? fileInfo.CreationTime : DateTime.Now,
            ModifiedAt = fileInfo.Exists ? fileInfo.LastWriteTime : DateTime.Now,
            HasUnsavedChanges = false
        };
    }

    /// <summary>
    /// Creates a new empty note with a generated filename.
    /// </summary>
    /// <param name="notesFolderPath">Path to the Notes folder.</param>
    /// <returns>A new NoteItem instance.</returns>
    public static NoteItem CreateNew(string notesFolderPath)
    {
        ArgumentNullException.ThrowIfNull(notesFolderPath);

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var fileName = $"note_{timestamp}.txt";
        var filePath = Path.Combine(notesFolderPath, fileName);

        return new NoteItem
        {
            Id = Path.GetFileNameWithoutExtension(fileName),
            Title = "New Note",
            Preview = string.Empty,
            Content = string.Empty,
            FilePath = filePath,
            FileName = fileName,
            CreatedAt = DateTime.Now,
            ModifiedAt = DateTime.Now,
            HasUnsavedChanges = true
        };
    }

    /// <summary>
    /// Saves the note content to disk.
    /// </summary>
    public void Save()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(FilePath, Content);
        ModifiedAt = DateTime.Now;
        HasUnsavedChanges = false;

        // Update title and preview after save
        Title = ExtractTitle(Content, FileName);
        Preview = ExtractPreview(Content);
    }

    /// <summary>
    /// Updates the content and marks as having unsaved changes.
    /// </summary>
    /// <param name="newContent">The new content.</param>
    public void UpdateContent(string newContent)
    {
        if (Content != newContent)
        {
            Content = newContent;
            HasUnsavedChanges = true;
            Title = ExtractTitle(newContent, FileName);
            Preview = ExtractPreview(newContent);
        }
    }

    /// <summary>
    /// Extracts a title from the content (first line or first few words).
    /// </summary>
    private static string ExtractTitle(string content, string fallbackFileName)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Path.GetFileNameWithoutExtension(fallbackFileName);
        }

        // Get first line
        var firstLine = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return Path.GetFileNameWithoutExtension(fallbackFileName);
        }

        // Limit to first 50 characters
        return firstLine.Length > 50 ? firstLine[..50] + "..." : firstLine;
    }

    /// <summary>
    /// Extracts a preview from the content (first ~60 characters).
    /// </summary>
    private static string ExtractPreview(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        // Clean up whitespace and get first chunk
        var cleaned = content.Replace('\n', ' ').Replace('\r', ' ').Trim();
        
        // Get first 10 words approximately (or 60 chars)
        if (cleaned.Length <= 60)
        {
            return cleaned;
        }

        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(10);
        var preview = string.Join(' ', words);
        
        return preview.Length > 60 ? preview[..60] + "..." : preview + "...";
    }
}
