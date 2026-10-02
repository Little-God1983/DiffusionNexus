using DiffusionNexus.Installer.SDK.Catalog.Packaging;

namespace DiffusionNexus.UI.Services.Catalog;

/// <summary>Where the catalog channel the app follows came from. Logged at startup.</summary>
public enum CatalogChannelSource { Default, Setting, Environment }

/// <summary>
/// Environment wins over the saved setting, which wins over Stable. A copy of the 3.x installer's
/// resolver (DiffusionNexus.Installer.Core/Updates/CatalogChannelResolver.cs): both apps share
/// %LocalAppData%\DiffusionNexus\catalog, so they must agree on the channel, or each startup would
/// apply over the other's content. The main app only reads the setting; the installer owns it.
/// </summary>
public static class CatalogChannelResolver
{
    public const string EnvironmentVariable = "DIFFUSIONNEXUS_CATALOG_CHANNEL";

    public static (CatalogChannel Channel, CatalogChannelSource Source) Resolve(string? environmentValue, string? savedValue)
    {
        if (TryParse(environmentValue, out var fromEnvironment)) return (fromEnvironment, CatalogChannelSource.Environment);
        if (TryParse(savedValue, out var fromSetting)) return (fromSetting, CatalogChannelSource.Setting);
        return (CatalogChannel.Stable, CatalogChannelSource.Default);
    }

    /// <summary>
    /// Word match only. Enum.TryParse(ignoreCase) also accepts "0" and "1", and a stray digit in a
    /// settings file must not silently pick a channel.
    /// </summary>
    public static bool TryParse(string? value, out CatalogChannel channel)
    {
        var word = value?.Trim();
        if (string.Equals(word, nameof(CatalogChannel.Stable), StringComparison.OrdinalIgnoreCase)) { channel = CatalogChannel.Stable; return true; }
        if (string.Equals(word, nameof(CatalogChannel.Preview), StringComparison.OrdinalIgnoreCase)) { channel = CatalogChannel.Preview; return true; }
        channel = CatalogChannel.Stable;
        return false;
    }
}
