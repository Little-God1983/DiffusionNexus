using System;

namespace DiffusionNexus.UI.Startup;

/// <summary>
/// Decides which Win32 rendering mode the app requests. Hardware rendering
/// (Avalonia's default: ANGLE with automatic software fallback) is the norm;
/// setting DIFFUSIONNEXUS_SOFTWARE_RENDERING=1 forces the software compositor
/// as an escape hatch for machines with broken GPU drivers.
/// </summary>
public static class RenderingConfig
{
    public const string SoftwareRenderingEnvVar = "DIFFUSIONNEXUS_SOFTWARE_RENDERING";

    public static bool UseSoftwareRendering(Func<string, string?> getEnvironmentVariable)
    {
        var value = getEnvironmentVariable(SoftwareRenderingEnvVar);
        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
