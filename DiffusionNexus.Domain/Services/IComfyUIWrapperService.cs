namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Represents a single image returned by a ComfyUI workflow execution.
/// </summary>
/// <param name="Filename">The filename as stored by ComfyUI.</param>
/// <param name="Subfolder">The subfolder within ComfyUI's output directory.</param>
/// <param name="Type">The image type (e.g. "output", "temp").</param>
/// <param name="Url">The full URL to fetch the image from ComfyUI.</param>
public record ComfyUIImage(
    string Filename,
    string Subfolder,
    string Type,
    string Url);

/// <summary>
/// Aggregated result of a ComfyUI workflow execution, containing text and image outputs.
/// </summary>
public sealed class ComfyUIResult
{
    /// <summary>
    /// Text outputs produced by the workflow (e.g. captions).
    /// </summary>
    public List<string> Texts { get; } = [];

    /// <summary>
    /// Image outputs produced by the workflow.
    /// </summary>
    public List<ComfyUIImage> Images { get; } = [];
}

/// <summary>
/// Service for executing ComfyUI workflows via the ComfyUI HTTP/WebSocket API.
/// Supports uploading images, queuing API-format workflows, tracking progress, and
/// retrieving results (text and images).
/// </summary>
public interface IComfyUIWrapperService : IDisposable
{
    /// <summary>
    /// Uploads a local image file to ComfyUI's input folder so it can be referenced by workflows.
    /// </summary>
    /// <param name="localFilePath">Absolute path to the image on disk.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The filename as stored by ComfyUI (use this in workflow node inputs).</returns>
    Task<string> UploadImageAsync(string localFilePath, CancellationToken ct = default);

    /// <summary>
    /// Loads an API-format workflow JSON, applies caller-supplied modifications to individual
    /// nodes, then queues the workflow for execution on the ComfyUI server.
    /// </summary>
    /// <param name="workflowJsonPath">Path to the API-format workflow JSON file.</param>
    /// <param name="nodeModifiers">
    /// A dictionary mapping node IDs to actions that mutate the node's JSON before submission.
    /// For example, setting the image filename on a LoadImage node.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The prompt ID assigned by ComfyUI (used to track and fetch results).</returns>
    Task<string> QueueWorkflowAsync(
        string workflowJsonPath,
        Dictionary<string, Action<System.Text.Json.Nodes.JsonNode>> nodeModifiers,
        CancellationToken ct = default);

    /// <summary>
    /// Waits for a queued workflow to finish executing by listening on the ComfyUI WebSocket.
    /// </summary>
    /// <param name="promptId">The prompt ID returned by <see cref="QueueWorkflowAsync"/>.</param>
    /// <param name="progress">Optional progress reporter that receives WebSocket event messages.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WaitForCompletionAsync(
        string promptId,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves the outputs (text and images) produced by a completed workflow execution.
    /// </summary>
    /// <param name="promptId">The prompt ID of the completed execution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ComfyUIResult"/> containing all text and image outputs.</returns>
    Task<ComfyUIResult> GetResultAsync(string promptId, CancellationToken ct = default);

    /// <summary>
    /// Downloads the raw bytes of an output image from the ComfyUI server.
    /// </summary>
    /// <param name="image">The image descriptor returned inside <see cref="ComfyUIResult"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The image bytes.</returns>
    Task<byte[]> DownloadImageAsync(ComfyUIImage image, CancellationToken ct = default);

    /// <summary>
    /// Generates a caption for a local image using the Qwen3-VL workflow on ComfyUI.
    /// Queues the built-in captioning workflow, waits for completion, and returns the caption text.
    /// </summary>
    /// <param name="imagePath">Absolute path to the image file on disk (must be accessible by the ComfyUI server).</param>
    /// <param name="prompt">The captioning prompt to send to the model.</param>
    /// <param name="temperature">Inference temperature (0.0–2.0). Lower values are more deterministic.</param>
    /// <param name="progress">Optional progress reporter that receives WebSocket event messages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The generated caption text, or <c>null</c> if no text was returned.</returns>
    Task<string?> GenerateCaptionAsync(
        string imagePath,
        string prompt,
        float temperature = 0.7f,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Queries the ComfyUI server's <c>/object_info</c> endpoint and returns the set of
    /// all registered node class types (e.g. "LoadImage", "Qwen3_VQA", "ShowText|pysssss").
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A set of installed node type names.</returns>
    Task<HashSet<string>> GetInstalledNodeTypesAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks whether all of the specified custom node types are installed on the ComfyUI server.
    /// </summary>
    /// <param name="requiredNodeTypes">The node class_type names to check for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of node type names that are <b>not</b> installed. Empty if all are present.</returns>
    Task<IReadOnlyList<string>> CheckRequiredNodesAsync(
        IEnumerable<string> requiredNodeTypes,
        CancellationToken ct = default);

    /// <summary>
    /// Queries the ComfyUI <c>/models/{folder}</c> endpoint to list model files that are
    /// physically present in the specified model folder on the server (e.g. <c>"prompt_generator"</c>).
    /// </summary>
    /// <param name="folderName">The model folder name as registered by the custom node (e.g. <c>"prompt_generator"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A list of model file/directory names present in the folder, or an empty list if the
    /// folder does not exist or the endpoint is not available.
    /// </returns>
    Task<IReadOnlyList<string>> GetModelsInFolderAsync(
        string folderName,
        CancellationToken ct = default);

    /// <summary>
    /// Queries <c>/object_info/{nodeType}</c> to retrieve the available options for a
    /// specific input of a ComfyUI node. This is the authoritative way to check which
    /// models a node can see, because it returns exactly the values ComfyUI shows in
    /// its own UI dropdown.
    /// </summary>
    /// <param name="nodeType">The node class_type (e.g. <c>"Qwen3_VQA"</c>).</param>
    /// <param name="inputName">The input name to inspect (e.g. <c>"model"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The list of valid option strings for that input, or an empty list if the node
    /// or input is not found.
    /// </returns>
    Task<IReadOnlyList<string>> GetNodeInputOptionsAsync(
        string nodeType,
        string inputName,
        CancellationToken ct = default);
}
