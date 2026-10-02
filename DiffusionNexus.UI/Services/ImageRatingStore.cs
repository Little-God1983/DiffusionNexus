using System.Collections.Concurrent;
using System.Text.Json;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// The Ready / Trash rating of media files, kept in one <c>.ratings.json</c> per folder and keyed
/// by full file name (issue #317).
/// <para>
/// Before this store every image had its own <c>&lt;base name&gt;.rating</c> sidecar. Those legacy
/// files are still read as a fallback, and a folder is converted only when a rating in it changes:
/// every legacy rating is copied into the folder file, applied to each media file sharing that base
/// name (the old key), and the legacy files are deleted once the folder file is safely written.
/// Reading never changes anything on disk.
/// </para>
/// <para>
/// Synchronous on purpose: <see cref="DatasetImageViewModel"/> loads and saves its rating
/// synchronously from many call sites. Reads are served from a per-folder cache that is checked
/// against the file's write time and length, so another writer (a second instance, a restored
/// backup) is picked up.
/// </para>
/// </summary>
public sealed class ImageRatingStore
{
    /// <summary>The per-folder ratings file.</summary>
    public const string RatingsFileName = ".ratings.json";

    /// <summary>Extension of the legacy per-image rating sidecar.</summary>
    public const string LegacyExtension = ".rating";

    private const int CurrentVersion = 1;
    private const string LogSource = "Image Ratings";
    private static readonly ILogger Logger = Log.ForContext<ImageRatingStore>();
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    /// <summary>The app-wide store; logs to the Unified Console once the app's services exist.</summary>
    public static ImageRatingStore Shared { get; } = new(() => App.Services?.GetService<IUnifiedLogger>());

    private readonly Func<IUnifiedLogger?> _loggerProvider;
    private readonly ConcurrentDictionary<string, FolderRatings> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);

    public ImageRatingStore(Func<IUnifiedLogger?>? loggerProvider = null)
    {
        _loggerProvider = loggerProvider ?? (() => null);
    }

    /// <summary>
    /// The rating of a media file: its entry in the folder file, else its legacy
    /// <c>.rating</c> sidecar, else <see cref="ImageRatingStatus.Unrated"/>.
    /// </summary>
    public ImageRatingStatus Get(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var folder = FolderOf(imagePath);

        lock (LockFor(folder))
        {
            var ratings = Load(folder);
            if (ratings.Entries.TryGetValue(Path.GetFileName(imagePath), out var status))
            {
                return status;
            }
        }

        return ReadLegacy(Path.ChangeExtension(imagePath, LegacyExtension)) ?? ImageRatingStatus.Unrated;
    }

    /// <summary>
    /// Sets the rating of a media file. A folder that still holds legacy <c>.rating</c> files is
    /// converted first. Returns false when the folder file could not be written; the legacy files
    /// are then left untouched.
    /// </summary>
    public bool Set(string imagePath, ImageRatingStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var folder = FolderOf(imagePath);

        lock (LockFor(folder))
        {
            var current = Load(folder);
            var name = Path.GetFileName(imagePath);

            // Clearing an image that has no rating changes nothing, so it converts nothing either
            if (status == ImageRatingStatus.Unrated
                && !current.Entries.ContainsKey(name)
                && !File.Exists(Path.ChangeExtension(imagePath, LegacyExtension)))
            {
                return true;
            }

            var entries = new Dictionary<string, ImageRatingStatus>(current.Entries, StringComparer.OrdinalIgnoreCase);
            var conversion = ConvertLegacy(folder, entries);


            bool changed;
            if (status == ImageRatingStatus.Unrated)
            {
                changed = entries.Remove(name);
            }
            else
            {
                changed = !entries.TryGetValue(name, out var previous) || previous != status;
                entries[name] = status;
            }

            if (!changed && conversion.LegacyFiles.Count == 0 && !current.Corrupt)
            {
                return true;
            }

            if (!Persist(folder, entries, current.Corrupt))
            {
                return false;
            }

            if (conversion.LegacyFiles.Count > 0)
            {
                DeleteLegacyFiles(conversion.LegacyFiles);
                var message = $"Converted {conversion.LegacyFiles.Count} legacy .rating file(s) in \"{folder}\" " +
                              $"to {RatingsFileName} ({conversion.Applied} rating(s) carried over, " +
                              $"{conversion.Orphans} without an image dropped)";
                Logger.Information("{Message}", message);
                _loggerProvider()?.Info(LogCategory.FileSystem, LogSource, message);
            }

            return true;
        }
    }

    /// <summary>
    /// Forgets the rating of a media file that was deleted or moved away. Does not convert the
    /// folder: its legacy sidecar is deleted only when no other media file still shares it.
    /// </summary>
    public void Remove(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var folder = FolderOf(imagePath);

        lock (LockFor(folder))
        {
            var current = Load(folder);
            var name = Path.GetFileName(imagePath);
            if (!current.Corrupt && current.Entries.ContainsKey(name))
            {
                var entries = new Dictionary<string, ImageRatingStatus>(current.Entries, StringComparer.OrdinalIgnoreCase);
                entries.Remove(name);
                Persist(folder, entries, setAsideCorrupt: false);
            }

            var legacy = Path.ChangeExtension(imagePath, LegacyExtension);
            if (File.Exists(legacy) && !HasOtherMediaWithBaseName(folder, imagePath))
            {
                DeleteLegacyFiles([legacy]);
            }
        }
    }

    /// <summary>Gives <paramref name="destinationImagePath"/> the rating of <paramref name="sourceImagePath"/>.</summary>
    public void Copy(string sourceImagePath, string destinationImagePath)
        => Set(destinationImagePath, Get(sourceImagePath));

    /// <summary>Carries a rating to a media file's new path and forgets the old one.</summary>
    public void Move(string sourceImagePath, string destinationImagePath)
    {
        Copy(sourceImagePath, destinationImagePath);
        if (!string.Equals(Path.GetFullPath(sourceImagePath), Path.GetFullPath(destinationImagePath),
                StringComparison.OrdinalIgnoreCase))
        {
            Remove(sourceImagePath);
        }
    }

    private static string FolderOf(string imagePath)
        => Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(imagePath))!);

    private object LockFor(string folder) => _locks.GetOrAdd(folder, _ => new object());

    private FolderRatings Load(string folder)
    {
        var file = new FileInfo(Path.Combine(folder, RatingsFileName));
        var stamp = file.Exists ? (file.LastWriteTimeUtc, file.Length) : (DateTime.MinValue, -1L);

        if (_cache.TryGetValue(folder, out var cached) && cached.Stamp == stamp)
        {
            return cached;
        }

        var loaded = file.Exists ? Parse(file.FullName, stamp) : FolderRatings.Empty(stamp);
        _cache[folder] = loaded;
        return loaded;
    }

    private FolderRatings Parse(string path, (DateTime, long) stamp)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("ratings", out var ratings)
                || ratings.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Missing \"ratings\" object.");
            }

            var entries = new Dictionary<string, ImageRatingStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in ratings.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && TryParseStatus(property.Value.GetString(), out var status))
                {
                    entries[property.Name] = status;
                }
            }

            return new FolderRatings(entries, stamp, Corrupt: false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A locked file is not corrupt: report it, but never set it aside.
            var corrupt = ex is JsonException;
            Logger.Warning(ex, "Could not read ratings file {Path}", path);
            _loggerProvider()?.Warn(LogCategory.FileSystem, LogSource,
                $"Could not read {path}; falling back to legacy .rating files", ex.Message);
            return new FolderRatings(new Dictionary<string, ImageRatingStatus>(StringComparer.OrdinalIgnoreCase),
                stamp, corrupt);
        }
    }

    private static bool TryParseStatus(string? text, out ImageRatingStatus status)
    {
        return Enum.TryParse(text?.Trim(), ignoreCase: true, out status)
               && Enum.IsDefined(status)
               && status != ImageRatingStatus.Unrated;
    }

    private static ImageRatingStatus? ReadLegacy(string legacyPath)
    {
        try
        {
            if (File.Exists(legacyPath) && TryParseStatus(File.ReadAllText(legacyPath), out var status))
            {
                return status;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable sidecar: treat as unrated, as the per-image files always did.
        }

        return null;
    }

    /// <summary>
    /// Folds the folder's legacy sidecars into <paramref name="entries"/>. An entry already in the
    /// folder file wins, the same precedence <see cref="Get"/> applies.
    /// </summary>
    private static LegacyConversion ConvertLegacy(string folder, Dictionary<string, ImageRatingStatus> entries)
    {
        List<string> legacyFiles;
        try
        {
            legacyFiles = Directory.EnumerateFiles(folder, "*" + LegacyExtension)
                .Where(f => string.Equals(Path.GetExtension(f), LegacyExtension, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LegacyConversion([], 0, 0);
        }

        if (legacyFiles.Count == 0)
        {
            return new LegacyConversion([], 0, 0);
        }

        var mediaByBaseName = Directory.EnumerateFiles(folder)
            .Where(MediaFileExtensions.IsDisplayableMediaFile)
            .ToLookup(Path.GetFileNameWithoutExtension, Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        int applied = 0, orphans = 0;
        foreach (var legacy in legacyFiles)
        {
            var media = mediaByBaseName[Path.GetFileNameWithoutExtension(legacy)].ToList();
            if (media.Count == 0)
            {
                orphans++;
                continue;
            }

            if (ReadLegacy(legacy) is not { } status)
            {
                continue;
            }

            foreach (var name in media)
            {
                if (entries.TryAdd(name!, status))
                {
                    applied++;
                }
            }
        }

        return new LegacyConversion(legacyFiles, applied, orphans);
    }

    private static bool HasOtherMediaWithBaseName(string folder, string imagePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(imagePath);
        var fullPath = Path.GetFullPath(imagePath);
        try
        {
            return Directory.EnumerateFiles(folder, baseName + ".*")
                .Any(f => MediaFileExtensions.IsDisplayableMediaFile(f)
                          && string.Equals(Path.GetFileNameWithoutExtension(f), baseName, StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(Path.GetFullPath(f), fullPath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // when in doubt, keep the sidecar
        }
    }

    /// <summary>
    /// Writes the folder file through a temp file and a rename, so a crash never leaves it half
    /// written. An empty map deletes the file. Updates the cache on success.
    /// </summary>
    private bool Persist(string folder, Dictionary<string, ImageRatingStatus> entries, bool setAsideCorrupt)
    {
        var path = Path.Combine(folder, RatingsFileName);
        var temp = path + ".tmp";
        try
        {
            if (setAsideCorrupt && File.Exists(path))
            {
                File.Move(path, path + ".bad", overwrite: true);
                Logger.Warning("Set aside unreadable ratings file {Path} as {Bad}", path, path + ".bad");
                _loggerProvider()?.Warn(LogCategory.FileSystem, LogSource,
                    $"{path} was unreadable; kept it as {RatingsFileName}.bad and started a new one");
            }

            if (entries.Count == 0)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                using (var stream = File.Create(temp))
                using (var writer = new Utf8JsonWriter(stream, WriterOptions))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("version", CurrentVersion);
                    writer.WriteStartObject("ratings");
                    foreach (var (name, status) in entries.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        writer.WriteString(name, status.ToString());
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                File.Move(temp, path, overwrite: true);
            }

            var file = new FileInfo(path);
            var stamp = file.Exists ? (file.LastWriteTimeUtc, file.Length) : (DateTime.MinValue, -1L);
            _cache[folder] = new FolderRatings(entries, stamp, Corrupt: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            Logger.Warning(ex, "Could not write ratings file {Path}", path);
            _loggerProvider()?.Error(LogCategory.FileSystem, LogSource, $"Could not save ratings to {path}", ex);
            return false;
        }
    }

    private static void DeleteLegacyFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Debug(ex, "Could not delete {Path}", path);
        }
    }

    private sealed record FolderRatings(
        IReadOnlyDictionary<string, ImageRatingStatus> Entries,
        (DateTime WriteTime, long Length) Stamp,
        bool Corrupt)
    {
        public static FolderRatings Empty((DateTime, long) stamp)
            => new(new Dictionary<string, ImageRatingStatus>(StringComparer.OrdinalIgnoreCase), stamp, false);
    }

    private sealed record LegacyConversion(List<string> LegacyFiles, int Applied, int Orphans);
}
