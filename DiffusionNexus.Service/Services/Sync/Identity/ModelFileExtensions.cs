namespace DiffusionNexus.Service.Services.Sync.Identity;

/// <summary>
/// The model-file extensions this application recognizes, in one place.
/// </summary>
/// <remarks>
/// There were four of these, and they disagreed. The sorter's scan list omitted <c>.sft</c>, so a
/// file saved under the short spelling of the very same container the identity chain reads was
/// invisible to the sorter — found only because a user's file quietly failed to appear. Patching
/// that one list left <c>.bin</c> and <c>.gguf</c> invisible for exactly the same reason, and
/// <c>.pth</c> known to the sorter alone. A shared list makes the whole class of bug
/// unrepresentable rather than fixing one instance of it: adding a format is one edit here.
/// </remarks>
public static class ModelFileExtensions
{
    /// <summary>Every recognized model-file extension, lowercase, dot-prefixed.</summary>
    public static readonly string[] All =
    {
        ".safetensors", ".pt", ".pth", ".ckpt", ".bin", ".sft", ".gguf",
    };

    /// <summary>
    /// The subset that is a safetensors container — the only files whose header
    /// <see cref="SafetensorsHeaderReader"/> can read. <c>.sft</c> is the standard short alias for
    /// the same format, so a readable header under either spelling must not fall through to the
    /// lower-confidence filename guess.
    /// </summary>
    public static readonly string[] SafetensorsContainers = { ".safetensors", ".sft" };

    /// <summary>Whether <paramref name="filePath"/> ends in one of <paramref name="extensions"/>.</summary>
    public static bool Matches(string filePath, string[] extensions)
    {
        var extension = Path.GetExtension(filePath);
        foreach (var candidate in extensions)
        {
            if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
