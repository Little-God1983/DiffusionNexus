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
/// From then on the folder file alone decides: a legacy file left behind is ignored. Reading never
/// changes anything on disk. Only displayable media files are rated; captions are ignored.
/// </para>
/// <para>
/// Synchronous on purpose: <see cref="DatasetImageViewModel"/> loads and saves its rating
/// synchronously from many call sites. Reads are served from a per-folder cache that is checked
/// against the file's write time and length, so another writer (a second instance, a restored
/// backup) is picked up. Every write reads the file again first, so it keeps another writer's
/// changes; two instances writing in the same millisecond can still lose one of them. A file that
/// cannot be read is never cached and never written over.
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
    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>();

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
    /// The rating of a media file: its entry in the folder file, else (only while the folder has no
    /// ratings file yet) its legacy <c>.rating</c> sidecar, else <see cref="ImageRatingStatus.Unrated"/>.
    /// Files that are not displayable media (captions, thumbnails) are never rated.
    /// </summary>
    public ImageRatingStatus Get(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!IsRatable(imagePath))
        {
            return ImageRatingStatus.Unrated;
        }

        var folder = FolderOf(imagePath);
        lock (LockFor(folder))
        {
            var ratings = Load(folder);
            if (ratings.Entries.TryGetValue(Path.GetFileName(imagePath), out var status))
            {
                return status;
            }

            if (!ratings.UsesLegacy)
            {
                return ImageRatingStatus.Unrated;
            }
        }

        return ReadLegacy(Path.ChangeExtension(imagePath, LegacyExtension)) ?? ImageRatingStatus.Unrated;
    }

    /// <summary>
    /// Sets the rating of a media file. A folder that still holds legacy <c>.rating</c> files is
    /// converted first. Returns false when the folder file could not be read or written; nothing
    /// is changed on disk then.
    /// </summary>
    public bool Set(string imagePath, ImageRatingStatus status)
        => SetMany([new KeyValuePair<string, ImageRatingStatus>(imagePath, status)]);

    /// <summary>
    /// Sets many ratings with one write per folder (a version copy rates thousands of files).
    /// Returns false when any folder could not be updated.
    /// </summary>
    public bool SetMany(IEnumerable<KeyValuePair<string, ImageRatingStatus>> ratings)
    {
        ArgumentNullException.ThrowIfNull(ratings);

        var ok = true;
        foreach (var group in ratings
                     .Where(r => !string.IsNullOrWhiteSpace(r.Key) && IsRatable(r.Key))
                     .GroupBy(r => FolderOf(r.Key), StringComparer.OrdinalIgnoreCase))
        {
            ok &= SetInFolder(group.Key, group.ToList());
        }

        return ok;
    }

    private bool SetInFolder(string folder, List<KeyValuePair<string, ImageRatingStatus>> ratings)
    {
        lock (LockFor(folder))
        {
            // Clearing images that have no rating changes nothing, so it converts nothing either
            var cached = Load(folder);
            if (ratings.All(r => r.Value == ImageRatingStatus.Unrated
                                 && !cached.Entries.ContainsKey(Path.GetFileName(r.Key))
                                 && !(cached.UsesLegacy && File.Exists(Path.ChangeExtension(r.Key, LegacyExtension)))))
            {
                return true;
            }

            // Read the file again before writing, so a change made by another instance is kept
            var current = Load(folder, reread: true);
            if (current.State == FileState.Unreadable)
            {
                return false;
            }

            var entries = new Dictionary<string, ImageRatingStatus>(current.Entries, StringComparer.OrdinalIgnoreCase);
            var conversion = current.UsesLegacy ? ConvertLegacy(folder, entries) : LegacyConversion.None;
            if (conversion is null)
            {
                return false;
            }

            var changed = false;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, status) in ratings)
            {
                var name = Path.GetFileName(path);
                names.Add(name);
                if (status == ImageRatingStatus.Unrated)
                {
                    changed |= entries.Remove(name);
                }
                else
                {
                    changed |= !entries.TryGetValue(name, out var previous) || previous != status;
                    entries[name] = status;
                }
            }

            if (!changed && conversion.LegacyFiles.Count == 0 && current.State == FileState.Valid)
            {
                return true;
            }

            if (!Persist(folder, entries, current.State == FileState.Corrupt, names))
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
        if (!IsRatable(imagePath))
        {
            return;
        }

        var folder = FolderOf(imagePath);
        lock (LockFor(folder))
        {
            var name = Path.GetFileName(imagePath);
            if (Load(folder).Entries.ContainsKey(name))
            {
                var current = Load(folder, reread: true);
                if (current.State == FileState.Valid && current.Entries.ContainsKey(name))
                {
                    var entries = new Dictionary<string, ImageRatingStatus>(current.Entries, StringComparer.OrdinalIgnoreCase);
                    entries.Remove(name);
                    Persist(folder, entries, setAsideCorrupt: false, keep: NoNames);
                }
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
        if (!FilePaths.AreSame(sourceImagePath, destinationImagePath))
        {
            Remove(sourceImagePath);
        }
    }

    private static bool IsRatable(string path) => MediaFileExtensions.IsDisplayableMediaFile(path);

    private static string FolderOf(string imagePath)
        => Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(imagePath))!);

    private object LockFor(string folder) => _locks.GetOrAdd(folder, _ => new object());

    /// <summary>
    /// The folder's ratings, from the cache while the file's write time and length are unchanged.
    /// A read that failed (a locked file) is never cached, so the next call tries again.
    /// </summary>
    private FolderRatings Load(string folder, bool reread = false)
    {
        var file = new FileInfo(Path.Combine(folder, RatingsFileName));
        var stamp = file.Exists ? (file.LastWriteTimeUtc, file.Length) : (DateTime.MinValue, -1L);

        if (!reread && _cache.TryGetValue(folder, out var cached) && cached.Stamp == stamp)
        {
            return cached;
        }

        var loaded = file.Exists ? Parse(file.FullName, stamp) : FolderRatings.Empty(stamp, FileState.Missing);
        if (loaded.State == FileState.Unreadable)
        {
            _cache.TryRemove(folder, out _);
        }
        else
        {
            _cache[folder] = loaded;
        }

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

            return new FolderRatings(entries, stamp, FileState.Valid);
        }
        catch (JsonException ex)
        {
            Logger.Warning(ex, "Ratings file {Path} is not valid", path);
            _loggerProvider()?.Warn(LogCategory.FileSystem, LogSource,
                $"{path} is not valid; falling back to legacy .rating files", ex.Message);
            return FolderRatings.Empty(stamp, FileState.Corrupt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked file is not corrupt: never set it aside, never write over it
            Logger.Warning(ex, "Could not read ratings file {Path}", path);
            _loggerProvider()?.Warn(LogCategory.FileSystem, LogSource,
                $"Could not read {path}; ratings in this folder are not changed until it can be read", ex.Message);
            return FolderRatings.Empty(stamp, FileState.Unreadable);
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
    /// folder file wins, the same precedence <see cref="Get"/> applies. Null when the folder could
    /// not be listed: converting without its legacy ratings would lose them.
    /// </summary>
    private static LegacyConversion? ConvertLegacy(string folder, Dictionary<string, ImageRatingStatus> entries)
    {
        List<string> legacyFiles;
        ILookup<string, string> mediaByBaseName;
        try
        {
            legacyFiles = Directory.EnumerateFiles(folder, "*" + LegacyExtension)
                .Where(f => string.Equals(Path.GetExtension(f), LegacyExtension, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (legacyFiles.Count == 0)
            {
                return LegacyConversion.None;
            }

            mediaByBaseName = Directory.EnumerateFiles(folder)
                .Where(MediaFileExtensions.IsDisplayableMediaFile)
                .ToLookup(f => Path.GetFileNameWithoutExtension(f), f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not list {Folder} for legacy ratings", folder);
            return null;
        }

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
                if (entries.TryAdd(name, status))
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
        try
        {
            return Directory.EnumerateFiles(folder, baseName + ".*")
                .Any(f => MediaFileExtensions.IsDisplayableMediaFile(f)
                          && string.Equals(Path.GetFileNameWithoutExtension(f), baseName, StringComparison.OrdinalIgnoreCase)
                          && !FilePaths.AreSame(f, imagePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // when in doubt, keep the sidecar
        }
    }

    /// <summary>
    /// Writes the folder file through a temp file and a rename, so a crash never leaves it half
    /// written. Entries of files that are gone are dropped, except the ones in
    /// <paramref name="keep"/> (just set by the caller), so a deleted file's rating never sticks to
    /// a later file of the same name. An empty map still writes the file: it marks the folder as
    /// converted, so leftover legacy files stay ignored. Updates the cache on success.
    /// </summary>
    private bool Persist(string folder, Dictionary<string, ImageRatingStatus> entries, bool setAsideCorrupt,
        IReadOnlySet<string> keep)
    {
        var path = Path.Combine(folder, RatingsFileName);
        var temp = path + ".tmp";
        try
        {
            DropMissingFiles(folder, entries, keep);

            if (setAsideCorrupt && File.Exists(path))
            {
                File.Move(path, path + ".bad", overwrite: true);
                Logger.Warning("Set aside unreadable ratings file {Path} as {Bad}", path, path + ".bad");
                _loggerProvider()?.Warn(LogCategory.FileSystem, LogSource,
                    $"{path} was unreadable; kept it as {RatingsFileName}.bad and started a new one");
            }

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

            var file = new FileInfo(path);
            _cache[folder] = new FolderRatings(entries, (file.LastWriteTimeUtc, file.Length), FileState.Valid);
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

    private void DropMissingFiles(string folder, Dictionary<string, ImageRatingStatus> entries, IReadOnlySet<string> keep)
    {
        HashSet<string> present;
        try
        {
            present = Directory.EnumerateFiles(folder)
                .Select(f => Path.GetFileName(f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // keep everything when the folder cannot be listed
        }

        var gone = entries.Keys.Where(name => !present.Contains(name) && !keep.Contains(name)).ToList();
        foreach (var name in gone)
        {
            entries.Remove(name);
        }

        if (gone.Count > 0)
        {
            var message = $"Dropped {gone.Count} rating(s) of files no longer in \"{folder}\"";
            Logger.Information("{Message}", message);
            _loggerProvider()?.Info(LogCategory.FileSystem, LogSource, message);
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
            // A leftover legacy file is harmless: a folder with a ratings file ignores them
            Logger.Warning(ex, "Could not delete {Path}", path);
        }
    }

    private enum FileState
    {
        /// <summary>No ratings file: the folder has not been converted.</summary>
        Missing,
        Valid,
        /// <summary>The file exists but is not valid JSON; it is set aside on the next write.</summary>
        Corrupt,
        /// <summary>The file exists but could not be read (locked); never cached, never overwritten.</summary>
        Unreadable
    }

    private sealed record FolderRatings(
        IReadOnlyDictionary<string, ImageRatingStatus> Entries,
        (DateTime WriteTime, long Length) Stamp,
        FileState State)
    {
        /// <summary>Legacy sidecars count only until the folder has a ratings file of its own.</summary>
        public bool UsesLegacy => State is FileState.Missing or FileState.Corrupt;

        public static FolderRatings Empty((DateTime, long) stamp, FileState state)
            => new(new Dictionary<string, ImageRatingStatus>(StringComparer.OrdinalIgnoreCase), stamp, state);
    }

    private sealed record LegacyConversion(List<string> LegacyFiles, int Applied, int Orphans)
    {
        public static LegacyConversion None { get; } = new([], 0, 0);
    }
}
