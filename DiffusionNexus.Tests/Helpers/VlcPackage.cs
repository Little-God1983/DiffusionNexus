using System;
using System.IO;
using System.Text.RegularExpressions;

namespace DiffusionNexus.Tests.Helpers;

/// <summary>
/// Locates the restored VideoLAN.LibVLC.Windows payload. One copy of the version regex and the
/// NuGet root resolution, because they were written three and two times respectively and each
/// copy was a place for them to drift.
/// </summary>
public static class VlcPackage
{
    /// <summary>The VideoLAN.LibVLC.Windows version the UI project references, or null.</summary>
    public static string? ReferencedVersion
    {
        get
        {
            var match = Regex.Match(
                File.ReadAllText(RepoRoot.Combine("DiffusionNexus.UI", "DiffusionNexus.UI.csproj")),
                "<PackageReference\\b[^>]*Include=\"VideoLAN\\.LibVLC\\.Windows\"[^>]*Version=\"([^\"]+)\"",
                RegexOptions.IgnoreCase);

            return match.Success ? match.Groups[1].Value : null;
        }
    }

    /// <summary>The package's win-x64 payload directory, or null when it is not restored.</summary>
    public static string? PayloadRoot
    {
        get
        {
            var version = ReferencedVersion;
            if (version is null)
                return null;

            var root = Path.Combine(GlobalPackagesFolder, "videolan.libvlc.windows", version, "build", "x64");
            return Directory.Exists(root) ? root : null;
        }
    }

    /// <summary>
    /// NUGET_PACKAGES, else a globalPackagesFolder configured in the repository's NuGet.config,
    /// else the default. A build machine may well redirect it, and assuming otherwise turns a
    /// licence guard into a test that silently finds nothing.
    /// </summary>
    private static string GlobalPackagesFolder
    {
        get
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
                return fromEnvironment;

            var config = Path.Combine(RepoRoot.Path, "NuGet.config");
            if (File.Exists(config))
            {
                var configured = Regex.Match(
                    File.ReadAllText(config),
                    "<add\\s+key=\"globalPackagesFolder\"\\s+value=\"([^\"]+)\"",
                    RegexOptions.IgnoreCase);

                if (configured.Success)
                    return Path.GetFullPath(Path.Combine(RepoRoot.Path, configured.Groups[1].Value));
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        }
    }
}
