using DiffusionNexus.UI.Services.ConfigurationChecker;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// Resolves where the app-owned ComfyUI engine lives and whether it is present on disk.
/// The install root is user-choosable (the engine is 5-8 GB with torch, so forcing it onto
/// C: is not acceptable); this type only supplies the default and the presence check.
/// </summary>
public static class ManagedEngineLocator
{
    /// <summary>
    /// Default install root: <c>%LocalAppData%\DiffusionNexus\Engine\ComfyUI</c>.
    /// Offered as the pre-filled path in the folder picker, never forced.
    /// </summary>
    public static string DefaultInstallRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffusionNexus", "Engine", "ComfyUI");

    /// <summary>
    /// The Engine's own models folder: <c>models</c> in the ComfyUI repository, resolved the way the
    /// workload installer and the checker resolve it. Feature downloads land here (owner ruling
    /// 2026-10-12), so a file inside it is one the Engine downloaded.
    /// </summary>
    public static string ModelsFolder(string installRoot)
    {
        var type = ConfigurationCheckerService.DetectInstallationType(installRoot);
        return Path.Combine(ConfigurationCheckerService.GetRepositoryPath(installRoot, type), "models");
    }

    /// <summary>
    /// True when <paramref name="installRoot"/> contains a ComfyUI entry point. Used to detect
    /// an engine whose database row exists but whose folder was deleted behind the app's back.
    /// </summary>
    public static bool LooksInstalled(string? installRoot) => Probe(installRoot) == EngineFolderState.Installed;

    /// <summary>
    /// What <paramref name="installRoot"/> holds, in one look at the disk: no folder, a folder
    /// without a ComfyUI entry point (an install that did not finish), or a working engine.
    /// </summary>
    public static EngineFolderState Probe(string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return EngineFolderState.Missing;

        return ResolveMainPy(installRoot) is null ? EngineFolderState.Incomplete : EngineFolderState.Installed;
    }

    /// <summary>
    /// Resolves the ComfyUI entry point (main.py) under <paramref name="installRoot"/>, or null
    /// when neither supported layout has one. The single source of truth for "where is main.py" —
    /// both <see cref="LooksInstalled"/> and <c>ManagedComfyUiEngine</c> (which needs the actual
    /// path to launch, not just a bool) go through this rather than each re-checking the two
    /// layouts themselves.
    /// </summary>
    public static string? ResolveMainPy(string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot)) return null;

        // The SDK clones ComfyUI either directly into the root or into a ComfyUI/ subfolder,
        // depending on the resolved layout — accept both, direct taking priority.
        var direct = Path.Combine(installRoot, "main.py");
        if (File.Exists(direct)) return direct;

        var nested = Path.Combine(installRoot, "ComfyUI", "main.py");
        return File.Exists(nested) ? nested : null;
    }
}

/// <summary>What an Engine install folder holds (<see cref="ManagedEngineLocator.Probe"/>).</summary>
public enum EngineFolderState
{
    /// <summary>No folder at that path.</summary>
    Missing,
    /// <summary>The folder exists but holds no ComfyUI entry point.</summary>
    Incomplete,
    /// <summary>A ComfyUI entry point is there.</summary>
    Installed
}
