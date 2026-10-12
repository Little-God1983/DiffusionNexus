using DiffusionNexus.Domain.Services.UnifiedLogging;
using Serilog;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>Deletes for <see cref="EngineRemover"/>; a seam so tests can simulate failures.</summary>
public interface IRemovalFileSystem
{
    void DeleteDirectory(string path);
    void DeleteFile(string path);
}

/// <summary>
/// Deletes without following junctions or symlinks: a link (the folder itself or one found inside it)
/// that points at the user's library is removed, the files behind it stay. Read-only flags (git packs) are cleared first.
/// </summary>
public sealed class RemovalFileSystem : IRemovalFileSystem
{
    public void DeleteDirectory(string path)
    {
        // A directory that is itself a junction or symlink loses the link only, never its target.
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(path, recursive: false);
            return;
        }

        // An explicit walk: a junction or symlink inside is removed as a link, never entered.
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(entry, recursive: false);
                else
                    DeleteDirectory(entry);
            }
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }

        Directory.Delete(path, recursive: false);
    }

    public void DeleteFile(string path)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }
}

/// <summary>Outcome of <see cref="EngineRemover.RemoveAsync"/>.</summary>
public sealed record EngineRemovalResult(string? BusyReason, IReadOnlyList<RemovalFailure> Failed)
{
    public bool WasBusy => BusyReason is not null;

    public static EngineRemovalResult Busy(string reason) => new(reason, []);
}

/// <summary>
/// Removes Engine files safely: refuses while a generation or an install runs, stops an idle Engine,
/// and deletes under the Engine's maintenance hold so nothing starts it from a half-deleted folder.
/// </summary>
public sealed class EngineRemover
{
    private const string NotFullPath = "not a full path; nothing was deleted";
    private const string LogSource = "Diffusion Nexus Engine";
    private static readonly ILogger Logger = Log.ForContext<EngineRemover>();

    private readonly IManagedEngineControl? _engine;
    private readonly IInstallActivity? _installActivity;
    private readonly IUnifiedLogger? _logger;
    private readonly IRemovalFileSystem _fileSystem;

    public EngineRemover(
        IManagedEngineControl? engine,
        IInstallActivity? installActivity,
        IUnifiedLogger? logger = null,
        IRemovalFileSystem? fileSystem = null)
    {
        _engine = engine;
        _installActivity = installActivity;
        _logger = logger;
        _fileSystem = fileSystem ?? new RemovalFileSystem();
    }

    /// <summary>Why nothing may be removed right now, or null.</summary>
    public string? BusyReason()
    {
        if (_installActivity?.Running is { } running) return $"{running} is being installed";
        if (_engine is { HasActiveJobs: true }) return "a generation is running on it";
        return null;
    }

    /// <summary>
    /// Deletes <paramref name="directories"/> then <paramref name="files"/>. Missing paths are skipped;
    /// each path that fails is reported and the rest still goes. Drive roots are never deleted, and a
    /// item reached through a junction or symlink is refused (it lives elsewhere), and relative paths are
    /// refused. A directory that is itself a link loses the link only. With <paramref name="engineRoot"/>,
    /// only the segments strictly inside it are checked for links, and only for items inside it.
    /// </summary>
    public async Task<EngineRemovalResult> RemoveAsync(
        IReadOnlyList<string> directories, IReadOnlyList<string> files, string? engineRoot = null)
    {
        if (BusyReason() is { } busy) return Refuse(busy);

        IDisposable? hold = null;
        if (_engine is not null)
        {
            if (_engine.IsRunning || _engine.IsStarting)
            {
                if (!await _engine.StopIfIdleAsync())
                    return Refuse("a generation is running on it");
                Info("Engine stopped for the removal.");
            }

            hold = await _engine.TryBeginMaintenanceAsync();
            if (hold is null) return Refuse("it is starting");
        }

        using (hold)
        {
            var failed = await Task.Run(() => Delete(directories, files, engineRoot));
            Info(failed.Count == 0
                ? $"Removal finished: {directories.Count} folder(s) and {files.Count} file(s)."
                : $"Removal finished with {failed.Count} item(s) not deleted.");
            return new EngineRemovalResult(null, failed);
        }
    }

    private List<RemovalFailure> Delete(
        IReadOnlyList<string> directories, IReadOnlyList<string> files, string? engineRoot)
    {
        var failed = new List<RemovalFailure>();

        foreach (var directory in directories)
        {
            if (IsDriveRoot(directory))
            {
                failed.Add(new RemovalFailure(directory, "a drive is never deleted"));
                Warn($"Refused to delete {directory}: it is a drive.");
                continue;
            }

            if (!Path.IsPathFullyQualified(directory))
            {
                failed.Add(new RemovalFailure(directory, NotFullPath));
                Warn($"Refused to delete {directory}: it is not a full path.");
                continue;
            }

            if (!Directory.Exists(directory)) continue;
            if (IsReachedThroughLink(directory, engineRoot))
            {
                failed.Add(new RemovalFailure(directory, "reached through a link; the folder lives elsewhere and was not deleted"));
                Warn($"Refused to delete {directory}: it is reached through a junction or symlink.");
                continue;
            }

            var isLink = IsLink(directory);
            Try(directory, () => _fileSystem.DeleteDirectory(directory), failed,
                isLink ? $"Removed link {directory}; its target was not touched" : null);
        }

        foreach (var file in files)
        {
            if (!Path.IsPathFullyQualified(file))
            {
                failed.Add(new RemovalFailure(file, NotFullPath));
                Warn($"Refused to delete {file}: it is not a full path.");
                continue;
            }

            if (!File.Exists(file)) continue;
            if (IsReachedThroughLink(file, engineRoot))
            {
                failed.Add(new RemovalFailure(file, "reached through a link; the file lives elsewhere and was not deleted"));
                Warn($"Refused to delete {file}: it is reached through a junction or symlink.");
                continue;
            }

            Try(file, () => _fileSystem.DeleteFile(file), failed);
        }

        return failed;
    }

    private void Try(string path, Action delete, List<RemovalFailure> failed, string? successMessage = null)
    {
        try
        {
            delete();
            Info(successMessage ?? $"Deleted {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed.Add(new RemovalFailure(path, ex.Message));
            Warn($"Could not delete {path}: {ex.Message}");
        }
    }

    private static bool IsDriveRoot(string path)
    {
        var full = FolderPathMatch.Normalize(path);
        return full is null || string.Equals(Path.GetPathRoot(full)?.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when an ancestor directory of the item is a junction or symlink: from its parent up to, excluding,
    /// the drive root, or, when <paramref name="engineRoot"/> is given and holds the item, up to, excluding,
    /// that root (a link at or above the root blocks nothing; items outside it are not checked). The item
    /// itself being a link is fine: deleting it removes only the link.
    /// </summary>
    private static bool IsReachedThroughLink(string file, string? engineRoot)
    {
        try
        {
            var full = Path.GetFullPath(file);
            var root = Path.GetPathRoot(full);
            if (engineRoot is not null)
            {
                if (!FolderPathMatch.Contains(engineRoot, full)) return false;
                root = Path.GetFullPath(engineRoot).TrimEnd('\\', '/');
                if (string.Equals(full.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase)) return false;
            }
            for (var dir = Path.GetDirectoryName(full);
                 !string.IsNullOrEmpty(dir) && !string.Equals(dir, root, StringComparison.OrdinalIgnoreCase);
                 dir = Path.GetDirectoryName(dir))
            {
                if (Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true; // cannot tell: do not delete
        }
    }

    private static bool IsLink(string directory)
    {
        try { return new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private EngineRemovalResult Refuse(string reason)
    {
        Info($"Removal refused: the Engine is busy ({reason}). Nothing was changed.");
        return EngineRemovalResult.Busy(reason);
    }

    private void Info(string message)
    {
        Logger.Information("Engine removal: {Message}", message);
        _logger?.Info(LogCategory.Installation, LogSource, message);
    }

    private void Warn(string message)
    {
        Logger.Warning("Engine removal: {Message}", message);
        _logger?.Warn(LogCategory.Installation, LogSource, message);
    }
}
