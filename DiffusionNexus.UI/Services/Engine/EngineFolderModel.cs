namespace DiffusionNexus.UI.Services.Engine;

/// <summary>One file of an <see cref="EngineFolderModel"/>, with its exact size at the pinned revision.</summary>
public sealed record EngineFolderModelFile(string Path, long Size);

/// <summary>
/// A model that a node pack loads as a whole folder from a fixed place under the Engine's own
/// <c>models</c> directory, so the catalog (one file per link, picked by VRAM) cannot describe it.
/// The Qwen3-VL node is the case: it looks only in <c>models/prompt_generator/&lt;name&gt;</c> and,
/// when that folder is missing, downloads the model itself in the middle of a Generate. It never
/// checks the files inside, so a download that was cut off stays broken (#607). The Features dialog
/// downloads these up front, and readiness checks every file by size.
/// </summary>
/// <param name="Name">Folder name and the name shown to the user.</param>
/// <param name="RelativeFolder">Folder under the Engine root, with forward slashes.</param>
/// <param name="DownloadBaseUrl">Base URL of the pinned revision; a file's URL is this plus its path.</param>
/// <param name="Files">Every file the model needs, with exact sizes.</param>
public sealed record EngineFolderModel(
    string Name,
    string RelativeFolder,
    string DownloadBaseUrl,
    IReadOnlyList<EngineFolderModelFile> Files)
{
    public long TotalSize => Files.Sum(f => f.Size);

    public string FolderPath(string engineRoot) =>
        System.IO.Path.Combine(engineRoot, RelativeFolder.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string FilePath(string engineRoot, EngineFolderModelFile file) =>
        System.IO.Path.Combine(FolderPath(engineRoot), file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string FileUrl(EngineFolderModelFile file) => $"{DownloadBaseUrl.TrimEnd('/')}/{file.Path}";

    /// <summary>Files that are absent or not exactly their expected size.</summary>
    public IReadOnlyList<EngineFolderModelFile> MissingFiles(string engineRoot) =>
        Files.Where(f =>
        {
            var info = new FileInfo(FilePath(engineRoot, f));
            return !info.Exists || info.Length != f.Size;
        }).ToList();

    public bool IsComplete(string engineRoot) => MissingFiles(engineRoot).Count == 0;
}

/// <summary>The folder models Engine features need.</summary>
public static class EngineFolderModels
{
    /// <summary>
    /// Qwen3-VL-4B-Instruct-FP8, loaded by the Qwen3_VQA node of the Outpaint Vision workflow
    /// (the workflow's default model). Pinned to a commit so the sizes below stay true.
    /// </summary>
    public static EngineFolderModel Qwen3VL4BInstructFp8 { get; } = new(
        "Qwen3-VL-4B-Instruct-FP8",
        "models/prompt_generator/Qwen3-VL-4B-Instruct-FP8",
        "https://huggingface.co/Qwen/Qwen3-VL-4B-Instruct-FP8/resolve/fefbb44cbcce8d1bb7e20b920b94f77432b3446d",
        [
            new("chat_template.json", 5497),
            new("config.json", 12037),
            new("generation_config.json", 241),
            new("model-00001-of-00002.safetensors", 5366863440),
            new("model-00002-of-00002.safetensors", 654372016),
            new("model.safetensors.index.json", 91517),
            new("preprocessor_config.json", 336),
            new("tokenizer.json", 10179867),
            new("tokenizer_config.json", 10868),
            new("video_preprocessor_config.json", 331),
            new("vocab.json", 4957462),
        ]);
}
