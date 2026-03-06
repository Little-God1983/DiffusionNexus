namespace DiffusionNexus.UI.Services.SpellCheck;

/// <summary>
/// Persists custom user dictionary words to a plain text file in %LocalAppData%/DiffusionNexus/Data/.
/// Each line contains one word (case-insensitive matching).
/// </summary>
public sealed class UserDictionaryService : IUserDictionaryService
{
    private readonly HashSet<string> _words = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _filePath;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a UserDictionaryService that persists to the specified file,
    /// or defaults to %LocalAppData%/DiffusionNexus/Data/user_dictionary.txt.
    /// </summary>
    public UserDictionaryService(string? filePath = null)
    {
        _filePath = filePath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiffusionNexus", "Data", "user_dictionary.txt");
        Load();
    }

    /// <inheritdoc />
    public bool Contains(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        lock (_lock) { return _words.Contains(word); }
    }

    /// <inheritdoc />
    public void Add(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        lock (_lock)
        {
            if (_words.Add(word.Trim()))
            {
                Save();
            }
        }
    }

    /// <inheritdoc />
    public void Remove(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        lock (_lock)
        {
            if (_words.Remove(word.Trim()))
            {
                Save();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetAll()
    {
        lock (_lock) { return new HashSet<string>(_words, StringComparer.OrdinalIgnoreCase); }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            foreach (var line in File.ReadAllLines(_filePath))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    _words.Add(trimmed);
                }
            }

            Serilog.Log.Information("User dictionary loaded with {Count} words from {Path}", _words.Count, _filePath);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to load user dictionary from {Path}", _filePath);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllLines(_filePath, _words.OrderBy(w => w, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to save user dictionary to {Path}", _filePath);
        }
    }
}
