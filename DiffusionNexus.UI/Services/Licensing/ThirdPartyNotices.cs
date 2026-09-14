using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DiffusionNexus.UI.Services.Licensing;

/// <summary>
/// The generated third-party index, embedded at build time so the About screen works offline
/// and always matches the binaries it ships with.
/// </summary>
/// <remarks>
/// The file is produced by Scripts/Generate-ThirdPartyNotices.ps1 and verified fresh by CI;
/// this class only reads it. Read once through <see cref="Lazy{T}"/>: the payload is ~100 KB
/// and must not be re-decoded on every render of the screen that shows it.
/// </remarks>
public static class ThirdPartyNotices
{
    public const string ResourceName = "THIRD-PARTY-NOTICES.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lazy<IReadOnlyList<ThirdPartyComponent>> Components =
        new(ReadResource, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The embedded component list.</summary>
    public static IReadOnlyList<ThirdPartyComponent> Load() => Components.Value;

    /// <summary>Parses the index from JSON. Exposed so callers can be tested without the resource.</summary>
    public static IReadOnlyList<ThirdPartyComponent> Parse(string json)
    {
        var raw = JsonSerializer.Deserialize<List<RawComponent>>(json, SerializerOptions) ?? [];
        var result = new List<ThirdPartyComponent>(raw.Count);
        foreach (var entry in raw)
        {
            result.Add(new ThirdPartyComponent(
                entry.Id ?? string.Empty,
                entry.Version ?? string.Empty,
                entry.License ?? string.Empty,
                entry.Copyright ?? string.Empty,
                entry.Authors ?? string.Empty,
                entry.ProjectUrl ?? string.Empty,
                entry.LicenseText ?? string.Empty));
        }

        return result;
    }

    private static IReadOnlyList<ThirdPartyComponent> ReadResource()
    {
        using var stream = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' is missing. Run Scripts/Generate-ThirdPartyNotices.ps1 and rebuild.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private sealed class RawComponent
    {
        public string? Id { get; set; }
        public string? Version { get; set; }
        public string? License { get; set; }
        public string? Copyright { get; set; }
        public string? Authors { get; set; }
        public string? ProjectUrl { get; set; }
        public string? LicenseText { get; set; }
    }
}
