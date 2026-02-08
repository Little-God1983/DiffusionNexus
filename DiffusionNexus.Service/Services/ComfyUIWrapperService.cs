using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for executing ComfyUI workflows via the ComfyUI HTTP/WebSocket API.
/// Handles image upload, workflow queuing, progress tracking, and result retrieval.
/// </summary>
public sealed class ComfyUIWrapperService : IComfyUIWrapperService
{
    private const string Qwen3VlWorkflowFileName = "Assets/Workflows/Qwen-3VL-autocaption.json";
    private const string LoadImageNodeId = "100";
    private const string Qwen3VqaNodeId = "705";

    private static readonly ILogger Logger = Log.ForContext<ComfyUIWrapperService>();

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _clientId;
    private bool _disposed;

    /// <summary>
    /// Creates a new ComfyUIWrapperService targeting the specified ComfyUI server.
    /// </summary>
    /// <param name="baseUrl">
    /// Base URL of the ComfyUI server (e.g. <c>http://127.0.0.1:8188</c>).
    /// </param>
    public ComfyUIWrapperService(string baseUrl = "http://127.0.0.1:8188")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _baseUrl = baseUrl.TrimEnd('/');
        _clientId = Guid.NewGuid().ToString();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromMinutes(10) // VL models can be slow
        };
    }

    /// <inheritdoc />
    public async Task<string> UploadImageAsync(string localFilePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localFilePath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Logger.Debug("Uploading image {FilePath} to ComfyUI", localFilePath);

        using var form = new MultipartFormDataContent();
        var fileBytes = await File.ReadAllBytesAsync(localFilePath, ct);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        var fileName = Path.GetFileName(localFilePath);
        form.Add(fileContent, "image", fileName);
        form.Add(new StringContent("true"), "overwrite");

        using var response = await _httpClient.PostAsync("/upload/image", form, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadAsStringAsync(ct);
        var json = JsonSerializer.Deserialize<JsonElement>(result);
        var storedName = json.GetProperty("name").GetString()
                         ?? throw new InvalidOperationException("ComfyUI did not return a filename after upload.");

        Logger.Information("Uploaded image as {StoredName}", storedName);
        return storedName;
    }

    /// <inheritdoc />
    public async Task<string> QueueWorkflowAsync(
        string workflowJsonPath,
        Dictionary<string, Action<JsonNode>> nodeModifiers,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowJsonPath);
        ArgumentNullException.ThrowIfNull(nodeModifiers);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Logger.Debug("Loading workflow from {Path}", workflowJsonPath);

        var workflowText = await File.ReadAllTextAsync(workflowJsonPath, ct);
        var workflow = JsonNode.Parse(workflowText)
                       ?? throw new InvalidOperationException($"Failed to parse workflow JSON from {workflowJsonPath}.");

        // Apply caller-supplied modifications to individual nodes
        foreach (var (nodeId, modifier) in nodeModifiers)
        {
            var node = workflow[nodeId];
            if (node is not null)
            {
                modifier(node);
            }
            else
            {
                Logger.Warning("Node {NodeId} not found in workflow; skipping modifier", nodeId);
            }
        }

        var payload = new JsonObject
        {
            ["prompt"] = workflow,
            ["client_id"] = _clientId,
            ["extra_data"] = new JsonObject
            {
                ["extra_pnginfo"] = new JsonObject
                {
                    ["workflow"] = new JsonObject
                    {
                        // Required by comfyui_queue_manager plugin (qm_queue.py lines 221-222)
                        ["workflow_name"] = "DiffusionNexus",
                        ["id"] = _clientId,
                        // Required by ShowText|pysssss node (show_text.py line 34)
                        ["nodes"] = new JsonArray()
                    }
                }
            }
        };

        var content = new StringContent(
            payload.ToJsonString(),
            Encoding.UTF8,
            "application/json");

        Logger.Debug("Queuing workflow on ComfyUI server");
        using var response = await _httpClient.PostAsync("/prompt", content, ct);
        response.EnsureSuccessStatusCode();

        var resultText = await response.Content.ReadAsStringAsync(ct);
        var resultJson = JsonNode.Parse(resultText)
                         ?? throw new InvalidOperationException("ComfyUI returned an unparseable response from /prompt.");

        var promptId = resultJson["prompt_id"]?.GetValue<string>()
                       ?? throw new InvalidOperationException("ComfyUI response did not contain a prompt_id.");

        Logger.Information("Workflow queued with prompt ID {PromptId}", promptId);
        return promptId;
    }

    /// <inheritdoc />
    public async Task WaitForCompletionAsync(
        string promptId,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var ws = new ClientWebSocket();
        var wsUrl = _baseUrl.Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
                            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase);

        Logger.Debug("Connecting WebSocket to {WsUrl} for prompt {PromptId}", wsUrl, promptId);
        await ws.ConnectAsync(new Uri($"{wsUrl}/ws?clientId={_clientId}"), ct);

        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open)
        {
            ct.ThrowIfCancellationRequested();

            // Read the full message, assembling fragments if necessary
            var msg = await ReadFullWebSocketMessageAsync(ws, buffer, ct);
            if (msg is null)
            {
                continue;
            }

            var json = JsonNode.Parse(msg);
            var type = json?["type"]?.GetValue<string>();

            Logger.Debug("WebSocket event: {EventType} for prompt {PromptId}", type, promptId);
            progress?.Report($"Event: {type}");

            // Detect execution errors and propagate them
            if (type is "execution_error")
            {
                var errorData = json?["data"];
                var errorPromptId = errorData?["prompt_id"]?.GetValue<string>();
                if (errorPromptId == promptId)
                {
                    var nodeType = errorData?["node_type"]?.GetValue<string>() ?? "unknown";
                    var errorMsg = errorData?["exception_message"]?.GetValue<string>() ?? "Unknown execution error";
                    Logger.Error("ComfyUI execution error in node {NodeType}: {Error}", nodeType, errorMsg);
                    throw new InvalidOperationException(
                        $"ComfyUI workflow failed in node '{nodeType}': {errorMsg}");
                }
            }

            if (type is not "executing")
            {
                continue;
            }

            var data = json?["data"];
            var nodeId = data?["node"]?.GetValue<string>();
            var currentPromptId = data?["prompt_id"]?.GetValue<string>();

            // node == null means execution finished for this prompt
            if (nodeId is null && currentPromptId == promptId)
            {
                Logger.Information("Workflow execution completed for prompt {PromptId}", promptId);
                return;
            }
        }
    }

    /// <summary>
    /// Reads a complete WebSocket text message, assembling fragments when the message
    /// exceeds the buffer size. Returns null for non-text messages.
    /// </summary>
    private static async Task<string?> ReadFullWebSocketMessageAsync(
        ClientWebSocket ws,
        byte[] buffer,
        CancellationToken ct)
    {
        var result = await ws.ReceiveAsync(buffer, ct);
        if (result.MessageType != WebSocketMessageType.Text)
        {
            return null;
        }

        // Fast path: message fits in a single buffer read
        if (result.EndOfMessage)
        {
            return Encoding.UTF8.GetString(buffer, 0, result.Count);
        }

        // Slow path: assemble fragments for large messages
        using var ms = new MemoryStream();
        ms.Write(buffer, 0, result.Count);

        while (!result.EndOfMessage)
        {
            result = await ws.ReceiveAsync(buffer, ct);
            ms.Write(buffer, 0, result.Count);
        }

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <inheritdoc />
    public async Task<ComfyUIResult> GetResultAsync(string promptId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Logger.Debug("Fetching results for prompt {PromptId}", promptId);

        using var response = await _httpClient.GetAsync($"/history/{promptId}", ct);
        response.EnsureSuccessStatusCode();

        var historyText = await response.Content.ReadAsStringAsync(ct);
        var json = JsonNode.Parse(historyText)
                   ?? throw new InvalidOperationException("ComfyUI returned an unparseable /history response.");

        var outputs = json[promptId]?["outputs"];
        var comfyResult = new ComfyUIResult();

        if (outputs is not JsonObject outputNodes)
        {
            Logger.Warning("No outputs found for prompt {PromptId}. Raw history: {History}",
                promptId, historyText.Length > 2000 ? historyText[..2000] + "..." : historyText);
            return comfyResult;
        }

        foreach (var (nodeId, nodeOutput) in outputNodes)
        {
            Logger.Debug("Processing output for node {NodeId}: {Keys}",
                nodeId, nodeOutput is JsonObject obj ? string.Join(", ", obj.Select(kv => kv.Key)) : "null");
            ExtractTextOutputs(nodeOutput, comfyResult);
            ExtractImageOutputs(nodeOutput, comfyResult);
        }

        if (comfyResult.Texts.Count == 0 && comfyResult.Images.Count == 0)
        {
            Logger.Warning("No text or image outputs extracted for prompt {PromptId}. Output nodes: {Outputs}",
                promptId, outputs.ToJsonString().Length > 2000 ? outputs.ToJsonString()[..2000] + "..." : outputs.ToJsonString());
        }

        Logger.Information(
            "Retrieved {TextCount} text(s) and {ImageCount} image(s) for prompt {PromptId}",
            comfyResult.Texts.Count,
            comfyResult.Images.Count,
            promptId);

        return comfyResult;
    }

    /// <inheritdoc />
    public async Task<byte[]> DownloadImageAsync(ComfyUIImage image, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Logger.Debug("Downloading image {Filename} from ComfyUI", image.Filename);

        using var response = await _httpClient.GetAsync(image.Url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <inheritdoc />
    public async Task<string?> GenerateCaptionAsync(
        string imagePath,
        string prompt,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var workflowPath = Path.Combine(AppContext.BaseDirectory, Qwen3VlWorkflowFileName);
        if (!File.Exists(workflowPath))
        {
            throw new FileNotFoundException(
                $"Qwen3-VL workflow file not found at {workflowPath}. Ensure the workflow JSON is deployed with the application.",
                workflowPath);
        }

        Logger.Information("Generating caption for {ImagePath} using Qwen3-VL workflow", imagePath);

        // Upload image to ComfyUI's input folder so the LoadImage node can reference it
        var uploadedFileName = await UploadImageAsync(imagePath, ct);

        var promptId = await QueueWorkflowAsync(workflowPath,
            new Dictionary<string, Action<JsonNode>>
            {
                [LoadImageNodeId] = node =>
                {
                    node["inputs"]!["image"] = uploadedFileName;
                },
                [Qwen3VqaNodeId] = node =>
                {
                    node["inputs"]!["text"] = prompt;
                }
            }, ct);

        await WaitForCompletionAsync(promptId, progress, ct);

        var result = await GetResultAsync(promptId, ct);
        var caption = result.Texts.FirstOrDefault();

        Logger.Information(
            "Caption generation {Result} for {ImagePath}",
            caption is not null ? "succeeded" : "returned no text",
            imagePath);

        return caption;
    }

    /// <inheritdoc />
    public async Task<HashSet<string>> GetInstalledNodeTypesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Logger.Debug("Querying ComfyUI /object_info for installed node types");

        using var response = await _httpClient.GetAsync("/object_info", ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var objectInfo = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);

        var nodeTypes = new HashSet<string>(StringComparer.Ordinal);
        if (objectInfo.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in objectInfo.EnumerateObject())
            {
                nodeTypes.Add(property.Name);
            }
        }

        Logger.Information("ComfyUI server has {Count} registered node types", nodeTypes.Count);
        return nodeTypes;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> CheckRequiredNodesAsync(
        IEnumerable<string> requiredNodeTypes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requiredNodeTypes);

        var installed = await GetInstalledNodeTypesAsync(ct);
        var missing = requiredNodeTypes.Where(n => !installed.Contains(n)).ToList();

        if (missing.Count > 0)
        {
            Logger.Warning("ComfyUI server is missing required custom nodes: {MissingNodes}",
                string.Join(", ", missing));
        }

        return missing;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient.Dispose();
        _disposed = true;
    }

    private void ExtractTextOutputs(JsonNode? nodeOutput, ComfyUIResult result)
    {
        if (nodeOutput?["text"] is not JsonArray textArray)
        {
            return;
        }

        foreach (var element in textArray)
        {
            // ShowText|pysssss with INPUT_IS_LIST/OUTPUT_IS_LIST wraps text in a nested array:
            // "text": [["caption"]] instead of "text": ["caption"]
            if (element is JsonArray innerArray)
            {
                foreach (var inner in innerArray)
                {
                    var text = inner?.GetValue<string>();
                    if (text is not null)
                    {
                        result.Texts.Add(text);
                    }
                }
            }
            else
            {
                var text = element?.GetValue<string>();
                if (text is not null)
                {
                    result.Texts.Add(text);
                }
            }
        }
    }

    private void ExtractImageOutputs(JsonNode? nodeOutput, ComfyUIResult result)
    {
        if (nodeOutput?["images"] is not JsonArray imageArray)
        {
            return;
        }

        foreach (var img in imageArray)
        {
            if (img is null)
            {
                continue;
            }

            var filename = img["filename"]?.GetValue<string>();
            if (filename is null)
            {
                continue;
            }

            var subfolder = img["subfolder"]?.GetValue<string>() ?? "";
            var type = img["type"]?.GetValue<string>() ?? "output";

            result.Images.Add(new ComfyUIImage(
                Filename: filename,
                Subfolder: subfolder,
                Type: type,
                Url: $"{_baseUrl}/view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}"));
        }
    }
}
