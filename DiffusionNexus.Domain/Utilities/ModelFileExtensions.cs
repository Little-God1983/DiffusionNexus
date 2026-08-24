namespace DiffusionNexus.Domain.Utilities;

/// <summary>
/// The model-file extensions this application recognizes, split by the question being asked.
/// </summary>
/// <remarks>
/// There were seven of these and they all disagreed. The sorter's scan omitted <c>.sft</c>, so a
/// file saved under the short spelling of the very container the identity chain reads was invisible
/// to it — found only because a user's file quietly failed to appear.
/// <para>
/// One flat list is NOT the fix, and briefly being one caused a second bug: these lists answer two
/// different questions, and a set wide enough for the first is dangerous for the second. Recognizing
/// a name as a model's is advisory — over-recognizing costs nothing. Enumerating a folder decides
/// which files get physically relocated, so every entry there is a file the sorter will move.
/// Merging them silently widened the sorter to <c>.bin</c> and <c>.gguf</c>, which meant a root
/// holding <c>pytorch_model.bin</c> had it filed into a base-model folder.
/// </para>
/// <para>
/// Lives in Domain rather than beside any one consumer: discovery, sorting, hashing and the identity
/// chain all read it, and the last time a cross-cutting predicate needed a home
/// (<see cref="LocalPathRoots"/>) it landed here for the same reason.
/// </para>
/// </remarks>
public static class ModelFileExtensions
{
    /// <summary>
    /// Files the application will enumerate, discover into the library, and MOVE. Deliberately
    /// narrower than <see cref="Recognized"/>: adding an entry here relocates users' files.
    /// </summary>
    /// <remarks>
    /// <c>.sft</c> is here because it is the same container as <c>.safetensors</c> under its short
    /// name — the header reader has always read it, so a library that could not discover one was
    /// inconsistent with its own identity chain, not conservative.
    /// </remarks>
    public static readonly string[] Sortable =
    {
        ".safetensors", ".sft", ".ckpt", ".pt", ".pth",
    };

    /// <summary>
    /// Files whose NAME should be read as a model's — stripping an extension before a name hint,
    /// or spotting a model reference while hashing resources. Advisory only: nothing here is moved
    /// or discovered on the strength of it, so a wider set is the safer one.
    /// </summary>
    public static readonly string[] Recognized =
    {
        ".safetensors", ".sft", ".ckpt", ".pt", ".pth", ".bin", ".gguf",
    };

    /// <summary>
    /// The subset that is a safetensors container — the only files whose header a reader can parse.
    /// <c>.sft</c> is the standard short alias, so a readable header under either spelling must not
    /// fall through to a lower-confidence filename guess.
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
