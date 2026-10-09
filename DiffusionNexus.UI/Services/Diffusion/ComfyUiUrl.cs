using System.Diagnostics.CodeAnalysis;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>
/// Validation for the ComfyUI server URL typed in Settings. A URL without a scheme
/// (<c>127.0.0.1:8188</c>) makes <c>new Uri(...)</c> throw, so every client built from Settings
/// goes through here first.
/// </summary>
public static class ComfyUiUrl
{
    /// <summary>The local ComfyUI default, used when the Settings URL is not usable.</summary>
    public const string Default = "http://127.0.0.1:8188";

    /// <summary>True for an absolute http or https URL.</summary>
    public static bool IsValid([NotNullWhen(true)] string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>The URL when it is valid, otherwise <see cref="Default"/>.</summary>
    public static string OrDefault(string? url) => IsValid(url) ? url! : Default;

    /// <summary>How a bad value reads in a message: blank shows as "(empty)".</summary>
    public static string Describe(string? url) => string.IsNullOrWhiteSpace(url) ? "(empty)" : url;
}
