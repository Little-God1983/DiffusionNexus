using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>One <c>category: path</c> line inside an <c>extra_model_paths.yaml</c> section.</summary>
/// <param name="Category">The ComfyUI folder key (<c>loras</c>, <c>vae</c>, <c>text_encoders</c>, …).</param>
/// <param name="Value">
/// The path exactly as written — relative to the section's <c>base_path</c>, or absolute. Kept
/// verbatim because ComfyUI itself joins it onto <c>base_path</c> and a rooted value wins, so
/// rewriting it here could only lose information.
/// </param>
public sealed record ComfyCategoryPath(string Category, string Value);

/// <summary>
/// One top-level section of an <c>extra_model_paths.yaml</c> (the <c>comfyui:</c> / <c>a111:</c>
/// blocks). ComfyUI merges every section it finds, so the top-level name is a free-form label.
/// </summary>
public sealed record ComfyExtraModelPathsSection(
    string Name,
    string? BasePath,
    IReadOnlyList<ComfyCategoryPath> Categories);

/// <summary>
/// Reads <c>extra_model_paths.yaml</c> as sections, keeping each section's <c>base_path</c>
/// together with its per-category mapping.
///
/// This exists because a shared model library does not have to use ComfyUI's own folder names.
/// A user pointing several installs at one library typically renames the categories to suit
/// themselves — <c>text_encoders: TextEncoders/</c>, <c>upscale_models: ESRGAN/</c>,
/// <c>unet: DiffusionModels/</c> — and that mapping is the only thing that makes the library
/// readable. <see cref="ConfigurationChecker.ConfigurationCheckerService.ParseExtraModelPathsYaml"/>
/// deliberately flattens all of this into a bare path list, which is right for "search everywhere
/// for a file" and useless for "teach another ComfyUI to read this library".
/// </summary>
public static class ComfyExtraModelPaths
{
    /// <summary>The conventional file name, in the ComfyUI repository root.</summary>
    public const string FileName = "extra_model_paths.yaml";

    /// <summary>
    /// Keys that look like categories but must never be copied into another installation's
    /// configuration:
    /// <list type="bullet">
    /// <item><c>is_default</c> — ComfyUI reads it as "make this section the default save
    ///       location", so copying it would silently redirect where the engine writes.</item>
    /// <item><c>custom_nodes</c> — a real path key, but pointing the engine at a foreign
    ///       install's custom nodes would load arbitrary third-party code into the app-owned
    ///       engine. The engine installs its own nodes per workload.</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> NonCopyableKeys =
        new(StringComparer.OrdinalIgnoreCase) { "is_default", "custom_nodes", "download_model_base" };

    /// <summary>
    /// Parses the file in <paramref name="repositoryPath"/>, or an empty list when it is absent
    /// or unreadable. Never throws: a malformed yaml in someone else's installation must not
    /// break the caller.
    /// </summary>
    public static IReadOnlyList<ComfyExtraModelPathsSection> ParseFile(string? repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            return [];
        }

        try
        {
            var path = Path.Combine(repositoryPath, FileName);
            return File.Exists(path) ? Parse(File.ReadAllLines(path)) : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Parses yaml lines into sections. Indentation-driven and deliberately narrow — it
    /// understands exactly the shapes ComfyUI's own sample file uses (a top-level label, an
    /// optional <c>base_path</c>, single-line category entries, and <c>key: |</c> block scalars
    /// listing several paths) rather than pulling in a YAML dependency.
    /// </summary>
    public static IReadOnlyList<ComfyExtraModelPathsSection> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var sections = new List<ComfyExtraModelPathsSection>();

        string? sectionName = null;
        string? basePath = null;
        var categories = new List<ComfyCategoryPath>();

        // Set while a "key: |" block is open, so the indented value lines that follow are
        // attributed to that key instead of being discarded for having no colon of their own
        // (they are paths, and a Windows path contains a colon anyway — indentation, not
        // punctuation, is what distinguishes them).
        string? blockKey = null;
        var blockIndent = 0;

        void Flush()
        {
            if (sectionName is not null)
            {
                sections.Add(new ComfyExtraModelPathsSection(sectionName, basePath, categories));
            }

            sectionName = null;
            basePath = null;
            categories = [];
            blockKey = null;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = line.Length - trimmed.Length;

            if (indent == 0)
            {
                Flush();

                if (trimmed.EndsWith(':'))
                {
                    sectionName = trimmed[..^1].Trim();
                }

                continue;
            }

            if (sectionName is null)
            {
                continue;
            }

            // Deeper than the key that opened a block scalar → another value for that key.
            if (blockKey is not null && indent > blockIndent)
            {
                var blockValue = Unquote(trimmed);
                if (blockValue.Length > 0 && !NonCopyableKeys.Contains(blockKey))
                {
                    categories.Add(new ComfyCategoryPath(blockKey, blockValue));
                }

                continue;
            }

            blockKey = null;

            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex <= 0)
            {
                continue;
            }

            var key = trimmed[..colonIndex].Trim();
            var value = Unquote(trimmed[(colonIndex + 1)..]);

            if (key.Equals("base_path", StringComparison.OrdinalIgnoreCase))
            {
                basePath = value.Length > 0 ? value : null;
                continue;
            }

            // "key:" / "key: |" / "key: >" all open a block whose values are on the next lines.
            if (value.Length == 0 || value is "|" or ">" or "|-" or ">-")
            {
                blockKey = key;
                blockIndent = indent;
                continue;
            }

            if (!NonCopyableKeys.Contains(key))
            {
                categories.Add(new ComfyCategoryPath(key, value));
            }
        }

        Flush();
        return sections;
    }

    /// <summary>
    /// The category mapping declared for <paramref name="basePath"/> by any of
    /// <paramref name="sections"/>, or an empty list when no section points there. Matching is
    /// path identity, not string equality — <c>D:/Models/</c> in a yaml and <c>D:\Models</c> in
    /// the database are the same folder.
    ///
    /// <para>
    /// Several installations can declare the same library, so identical entries are collapsed —
    /// otherwise two installs agreeing on <c>loras: Lora/</c> would read as one category mapped
    /// onto two folders and be emitted twice.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ComfyCategoryPath> CategoriesFor(
        IEnumerable<ComfyExtraModelPathsSection> sections,
        string basePath)
    {
        ArgumentNullException.ThrowIfNull(sections);

        return
        [
            .. sections
                .Where(s => FolderPathMatch.AreSame(s.BasePath, basePath))
                .SelectMany(s => s.Categories)
                .DistinctBy(c => (c.Category.ToLowerInvariant(), c.Value.ToLowerInvariant()))
        ];
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }
}
