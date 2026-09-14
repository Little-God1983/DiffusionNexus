namespace DiffusionNexus.Service.Services;

/// <summary>
/// Locates the ffmpeg/ffprobe pair on disk.
/// </summary>
/// <remarks>
/// FFMpegCore runs <c>ffprobe</c> for media analysis and <c>ffmpeg</c> for conversion, so a
/// directory is only usable when it holds BOTH. Accepting a directory with just one of them
/// moves the failure from discovery (where it can be handled) to the first analysis call
/// (where it surfaces as an opaque process error).
/// </remarks>
public static class FFmpegBinaryLocator
{
    /// <summary>The platform-specific ffmpeg executable name.</summary>
    public static string FFmpegFileName { get; } = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>The platform-specific ffprobe executable name.</summary>
    public static string FFprobeFileName { get; } = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

    /// <summary>
    /// True when <paramref name="directory"/> contains both executables.
    /// </summary>
    public static bool HasBothBinaries(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        try
        {
            return File.Exists(Path.Combine(directory, FFmpegFileName))
                && File.Exists(Path.Combine(directory, FFprobeFileName));
        }
        catch (ArgumentException)
        {
            // Invalid path characters — treat as "not here" rather than throwing at a caller
            // that is only asking a question.
            return false;
        }
    }

    /// <summary>
    /// Searches PATH for a directory holding both executables, or null when none does.
    /// </summary>
    public static string? FindOnPath()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var entry in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = entry.Trim();
            if (HasBothBinaries(dir))
                return dir;
        }

        return null;
    }
}
