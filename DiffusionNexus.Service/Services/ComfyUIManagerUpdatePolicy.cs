using System.Text.RegularExpressions;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Which ComfyUI version an update should land on.
/// </summary>
internal enum ComfyUIUpdateChannel
{
    /// <summary>Newest <c>vX.Y.Z</c> release tag — ComfyUI-Manager's default policy.</summary>
    Stable,

    /// <summary>Tip of the default branch.</summary>
    Nightly,
}

/// <summary>
/// Mirrors how ComfyUI-Manager decides what "update ComfyUI" means, so this app and the
/// Manager's "Update All" land on the same version instead of overwriting each other.
/// The Manager reads <c>update_policy</c> from its <c>config.ini</c>:
/// <c>stable-comfyui</c> (its default) checks out the newest release tag,
/// <c>nightly-comfyui</c> pulls the default branch. When the two disagreed, the Manager
/// put older code on top of a <c>user/comfyui.db</c> that newer code had already migrated
/// and ComfyUI stopped starting (its database layer only migrates forwards).
/// </summary>
internal static partial class ComfyUIManagerUpdatePolicy
{
    // New ComfyUI keeps the Manager's files in user/__manager, older versions in
    // user/default/ComfyUI-Manager. The Manager migrates between them on its own.
    private static readonly string[] ConfigRelativePaths =
    [
        Path.Combine("user", "__manager", "config.ini"),
        Path.Combine("user", "default", "ComfyUI-Manager", "config.ini"),
    ];

    private static readonly string[] ManagerFolderNames = ["ComfyUI-Manager", "comfyui-manager"];

    /// <summary>
    /// Resolves the channel for the ComfyUI checkout at <paramref name="backendDir"/>.
    /// Without a Manager there is nothing to clash with, so the default branch is followed
    /// (this service's behaviour before the Manager was taken into account).
    /// </summary>
    public static ComfyUIUpdateChannel ResolveChannel(string backendDir)
    {
        var configPath = ConfigRelativePaths
            .Select(relative => Path.Combine(backendDir, relative))
            .FirstOrDefault(File.Exists);

        if (configPath is not null)
        {
            try
            {
                return ParseChannel(File.ReadAllLines(configPath));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            // Unreadable config: the Manager is there, so assume its default.
            return ComfyUIUpdateChannel.Stable;
        }

        var hasManager = ManagerFolderNames.Any(name =>
            Directory.Exists(Path.Combine(backendDir, "custom_nodes", name)));

        // Installed but never started: the Manager will write its default on first run.
        return hasManager ? ComfyUIUpdateChannel.Stable : ComfyUIUpdateChannel.Nightly;
    }

    /// <summary>
    /// Interprets the lines of the Manager's <c>config.ini</c>. Anything other than an
    /// explicit <c>nightly-comfyui</c> is the Manager's default, stable.
    /// </summary>
    public static ComfyUIUpdateChannel ParseChannel(IEnumerable<string> configLines)
    {
        foreach (var line in configLines)
        {
            var separator = line.IndexOf('=');
            if (separator < 0)
                continue;

            if (!line[..separator].Trim().Equals("update_policy", StringComparison.OrdinalIgnoreCase))
                continue;

            return line[(separator + 1)..].Trim().Equals("nightly-comfyui", StringComparison.OrdinalIgnoreCase)
                ? ComfyUIUpdateChannel.Nightly
                : ComfyUIUpdateChannel.Stable;
        }

        return ComfyUIUpdateChannel.Stable;
    }

    /// <summary>
    /// Picks the highest <c>vX.Y.Z</c> tag from <c>git tag</c> output, comparing the parts
    /// numerically. Pre-release and other tags are ignored — the same rule the Manager
    /// applies (<c>^v(\d+)\.(\d+)\.(\d+)$</c>). Returns <c>null</c> when there is none.
    /// </summary>
    public static string? PickLatestReleaseTag(string gitTagOutput)
    {
        (int Major, int Minor, int Patch, string Name)? best = null;

        foreach (var rawLine in gitTagOutput.Split('\n'))
        {
            var match = ReleaseTagRegex().Match(rawLine.Trim());
            if (!match.Success
                || !int.TryParse(match.Groups[1].Value, out var major)
                || !int.TryParse(match.Groups[2].Value, out var minor)
                || !int.TryParse(match.Groups[3].Value, out var patch))
                continue;

            if (best is null || (major, minor, patch).CompareTo((best.Value.Major, best.Value.Minor, best.Value.Patch)) > 0)
                best = (major, minor, patch, match.Value);
        }

        return best?.Name;
    }

    [GeneratedRegex(@"^v(\d+)\.(\d+)\.(\d+)$")]
    private static partial Regex ReleaseTagRegex();
}
