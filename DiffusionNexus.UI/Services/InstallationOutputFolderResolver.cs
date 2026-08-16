using System;
using System.Collections.Generic;
using System.IO;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Resolves the image output folders an installation writes into.
///
/// Only the launcher knows the truth: several ComfyUI installations commonly point
/// <c>--output-directory</c> at one shared gallery folder, while at most one of them owns
/// the <see cref="ImageGallery"/> row's FK. Removing that one installation must therefore
/// not offer to drop the gallery — the other installations keep filling it. The database
/// alone cannot see that; the startup script can.
///
/// Mirrors the detection <see cref="ViewModels.AddExistingInstallationDialogViewModel"/>
/// performs when an installation is added, so both agree on where images land.
/// </summary>
public static class InstallationOutputFolderResolver
{
    private const string OutputDirectoryArgument = "--output-directory";

    /// <summary>
    /// Every folder <paramref name="package"/> may write images to: the explicit
    /// <c>--output-directory</c> from its startup script, plus the conventional
    /// subfolder for its installer type when that folder exists on disk.
    /// Never throws — an unreadable script simply contributes nothing.
    /// </summary>
    public static IReadOnlyList<string> Resolve(InstallerPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var folders = new List<string>();

        if (string.IsNullOrWhiteSpace(package.InstallationPath))
        {
            return folders;
        }

        var configured = ParseOutputDirectoryFromLauncher(package);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // A relative --output-directory resolves against the installation, exactly
            // as it would when the script runs (it cd's to its own folder first).
            folders.Add(Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(package.InstallationPath, configured));
        }

        foreach (var subfolder in ConventionalOutputSubfolders(package.Type))
        {
            var candidate = Path.Combine(package.InstallationPath, subfolder);
            if (SafeDirectoryExists(candidate))
            {
                folders.Add(candidate);
            }
        }

        return folders;
    }

    /// <summary>
    /// The conventional output subfolders for an installer type, relative to the
    /// installation root. ComfyUI gets both the plain and the Windows-portable layout
    /// (<c>ComfyUI\output</c>) because the registered path may be either level.
    /// </summary>
    private static IReadOnlyList<string> ConventionalOutputSubfolders(InstallerType type) => type switch
    {
        InstallerType.ComfyUI => ["output", Path.Combine("ComfyUI", "output")],
        InstallerType.Automatic1111 => ["outputs"],
        InstallerType.Forge => ["outputs"],
        InstallerType.Fooocus => ["outputs"],
        InstallerType.InvokeAI => [Path.Combine("outputs", "images")],
        InstallerType.SwarmUI => ["Output"],
        InstallerType.FluxGym => ["outputs"],
        InstallerType.AIToolkit => ["output", Path.Combine("AI-Toolkit", "output")],
        _ => []
    };

    /// <summary>
    /// Reads <c>--output-directory</c> out of the installation's startup script.
    /// Supports both <c>--output-directory=PATH</c> and <c>--output-directory PATH</c>.
    /// </summary>
    private static string? ParseOutputDirectoryFromLauncher(InstallerPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.ExecutablePath))
        {
            return null;
        }

        string scriptPath;
        try
        {
            scriptPath = Path.IsPathRooted(package.ExecutablePath)
                ? package.ExecutablePath
                : Path.Combine(package.InstallationPath, package.ExecutablePath);

            if (!File.Exists(scriptPath))
            {
                return null;
            }

            foreach (var rawLine in File.ReadLines(scriptPath))
            {
                var parsed = ParseOutputDirectoryArgument(rawLine);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Failed to read the startup script of '{Name}' for {Argument}.",
                package.Name, OutputDirectoryArgument);
        }

        return null;
    }

    /// <summary>Extracts the argument's value from one script line, or null when absent.</summary>
    internal static string? ParseOutputDirectoryArgument(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var argIndex = line.IndexOf(OutputDirectoryArgument, StringComparison.OrdinalIgnoreCase);
        if (argIndex < 0)
        {
            return null;
        }

        var afterArgument = line[(argIndex + OutputDirectoryArgument.Length)..];

        string rawPath;
        if (afterArgument.StartsWith('='))
        {
            rawPath = afterArgument[1..].Trim();
        }
        else if (afterArgument.StartsWith(' '))
        {
            rawPath = afterArgument.Trim();
        }
        else
        {
            // "--output-directoryX" — a different argument that merely starts the same.
            return null;
        }

        rawPath = rawPath.Trim('"', '\'');

        // Stop at the next argument on the same line.
        var nextArgument = rawPath.IndexOf(" --", StringComparison.Ordinal);
        if (nextArgument >= 0)
        {
            rawPath = rawPath[..nextArgument].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(rawPath) ? null : rawPath;
    }

    /// <summary>Existence check that treats an invalid or unreachable path as "absent".</summary>
    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
