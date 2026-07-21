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
    private const string DefaultBaseUrl = "http://127.0.0.1:8188";

    private static readonly ILogger Logger = Log.ForContext<ComfyUIWrapperService>();

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly string _baseUrl;
    private readonly string _clientId;
    private bool _disposed;

    /// <summary>
    /// Creates a new ComfyUIWrapperService targeting the specified ComfyUI server using an
    /// internally-owned <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="baseUrl">
    /// Base URL of the ComfyUI server (e.g. <c>http://127.0.0.1:8188</c>).
    /// </param>
    /// <remarks>
    /// This convenience overload constructs (and owns) a default <see cref="HttpClient"/>
    /// configured with a 10-minute timeout. It delegates to the
    /// <see cref="ComfyUIWrapperService(HttpClient, string, bool)"/> seam so production call
    /// sites remain unchanged while tests can inject a stub <see cref="HttpMessageHandler"/>.
    /// </remarks>
    public ComfyUIWrapperService(string baseUrl = DefaultBaseUrl)
        : this(CreateDefaultHttpClient(baseUrl), baseUrl, disposeHttpClient: true)
    {
    }

    /// <summary>
    /// Creates a new ComfyUIWrapperService driven by a caller-supplied <see cref="HttpClient"/>.
    /// This is the testable seam: pass an <see cref="HttpClient"/> backed by a stub
    /// <see cref="HttpMessageHandler"/> to exercise the HTTP paths without a live server.
    /// </summary>
    /// <param name="httpClient">The HTTP client used for all ComfyUI REST calls.</param>
    /// <param name="baseUrl">
    /// Base URL of the ComfyUI server. Still required even when <paramref name="httpClient"/>
    /// is injected, because it derives the WebSocket URL and the image <c>/view</c> URLs.
    /// </param>
    /// <param name="disposeHttpClient">
    /// When <c>true</c>, <see cref="Dispose"/> disposes <paramref name="httpClient"/>.
    /// Defaults to <c>false</c> so externally-owned clients (e.g. from HttpClientFactory) survive.
    /// </param>
    public ComfyUIWrapperService(
        HttpClient httpClient,
        string baseUrl = DefaultBaseUrl,
        bool disposeHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _baseUrl = baseUrl.TrimEnd('/');
        _clientId = Guid.NewGuid().ToString();
        _httpClient = httpClient;
        _disposeHttpClient = disposeHttpClient;

        TryConfigureBaseAddress(_httpClient, _baseUrl);
    }

    private static HttpClient CreateDefaultHttpClient(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        return new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromMinutes(10) // VL models can be slow
        };
    }

    private static void TryConfigureBaseAddress(HttpClient client, string baseUrl)
    {
        // Relative request URIs (e.g. "/prompt") only resolve when a BaseAddress is set.
        // An injected client may not have one; a default (or already-used) client will.
        // HttpClient throws if BaseAddress is mutated after the first request, so guard it.
        if (client.BaseAddress is not null)
        {
            return;
        }

        try
        {
            client.BaseAddress = new Uri(baseUrl);
        }
        catch (InvalidOperationException)
        {
            // Client has already been used - the caller owns the BaseAddress.
        }
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

        var payload = BuildPromptPayload(
            workflow,
            nodeModifiers,
            _clientId,
            nodeId => Logger.Warning("Node {NodeId} not found in workflow; skipping modifier", nodeId));

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

            var parsed = ParseProgressMessage(msg, promptId);

            Logger.Debug("WebSocket event: {EventType} for prompt {PromptId}", parsed.Type, promptId);

            switch (parsed.Action)
            {
                case ComfyUIProgressAction.Error:
                    Logger.Error(
                        "ComfyUI execution error in node {NodeType}: {Error}",
                        parsed.ErrorNodeType,
                        parsed.ErrorDetail);
                    throw new InvalidOperationException(parsed.ExecutionErrorMessage);

                case ComfyUIProgressAction.Completed:
                    Logger.Information("Workflow execution completed for prompt {PromptId}", promptId);
                    return;

                case ComfyUIProgressAction.Report:
                    progress?.Report(parsed.ReportText!);
                    break;

                case ComfyUIProgressAction.None:
                default:
                    if (parsed.QueueRemaining.HasValue)
                    {
                        Logger.Debug("Queue remaining: {QueueRemaining}", parsed.QueueRemaining.Value);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Pure parser for the ComfyUI WebSocket progress protocol. Given a single text frame and
    /// the prompt being awaited, decides what the caller should do without performing any I/O.
    /// Preserves the exact behavior of the previous inline loop, including which events are
    /// ignored (unknown types, events for a different prompt, zero-max progress).
    /// </summary>
    /// <param name="message">The raw WebSocket text frame (a JSON document).</param>
    /// <param name="promptId">The prompt ID currently being awaited.</param>
    /// <returns>A <see cref="ComfyUIProgressMessage"/> describing the decoded event.</returns>
    internal static ComfyUIProgressMessage ParseProgressMessage(string message, string promptId)
    {
        var json = JsonNode.Parse(message);
        var type = json?["type"]?.GetValue<string>();

        switch (type)
        {
            case "execution_error":
            {
                var data = json?["data"];
                var errorPromptId = data?["prompt_id"]?.GetValue<string>();
                if (errorPromptId == promptId)
                {
                    var nodeType = data?["node_type"]?.GetValue<string>() ?? "unknown";
                    var detail = data?["exception_message"]?.GetValue<string>() ?? "Unknown execution error";
                    return new ComfyUIProgressMessage(
                        ComfyUIProgressAction.Error, type, ErrorNodeType: nodeType, ErrorDetail: detail);
                }

                return new ComfyUIProgressMessage(ComfyUIProgressAction.None, type);
            }

            case "executing":
            {
                var data = json?["data"];
                var nodeId = data?["node"]?.GetValue<string>();
                var currentPromptId = data?["prompt_id"]?.GetValue<string>();

                // node == null means execution finished for this prompt.
                if (nodeId is null && currentPromptId == promptId)
                {
                    return new ComfyUIProgressMessage(ComfyUIProgressAction.Completed, type);
                }

                // Report which node is currently executing.
                if (currentPromptId == promptId && nodeId is not null)
                {
                    var nodeLabel = nodeId switch
                    {
                        Qwen3VqaNodeId => "Running Qwen3-VL inference (first run may download the model...)",
                        LoadImageNodeId => "Loading image...",
                        _ => $"Executing node {nodeId}..."
                    };
                    return new ComfyUIProgressMessage(ComfyUIProgressAction.Report, type, ReportText: nodeLabel);
                }

                return new ComfyUIProgressMessage(ComfyUIProgressAction.None, type);
            }

            case "progress":
            {
                var data = json?["data"];
                var value = data?["value"]?.GetValue<int>() ?? 0;
                var max = data?["max"]?.GetValue<int>() ?? 0;
                if (max > 0)
                {
                    return new ComfyUIProgressMessage(
                        ComfyUIProgressAction.Report, type, ReportText: $"Progress: {value}/{max}");
                }

                return new ComfyUIProgressMessage(ComfyUIProgressAction.None, type);
            }

            case "status":
            {
                var queueRemaining = json?["data"]?["status"]?["exec_info"]?["queue_remaining"]?.GetValue<int>();
                return new ComfyUIProgressMessage(ComfyUIProgressAction.None, type, QueueRemaining: queueRemaining);
            }

            default:
                return new ComfyUIProgressMessage(ComfyUIProgressAction.None, type);
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

        if (outputs is not JsonObject)
        {
            Logger.Warning("No outputs found for prompt {PromptId}. Raw history: {History}",
                promptId, historyText.Length > 2000 ? historyText[..2000] + "..." : historyText);
            return new ComfyUIResult();
        }

        var comfyResult = ParseHistoryOutputs(json, promptId, _baseUrl);

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
        float temperature = 0.7f,
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
                    node["inputs"]!["temperature"] = temperature;
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
    public async Task<IReadOnlyList<string>> GetModelsInFolderAsync(
        string folderName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Logger.Debug("Querying ComfyUI /models/{FolderName} for downloaded models", folderName);

        using var response = await _httpClient.GetAsync($"/models/{folderName}", ct);
        if (!response.IsSuccessStatusCode)
        {
            Logger.Warning("ComfyUI /models/{FolderName} returned {StatusCode}", folderName, response.StatusCode);
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var json = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);

        if (json.ValueKind != JsonValueKind.Array)
        {
            Logger.Debug("Unexpected response format from /models/{FolderName}", folderName);
            return [];
        }

        var models = new List<string>();
        foreach (var item in json.EnumerateArray())
        {
            var value = item.GetString();
            if (value is not null)
            {
                models.Add(value);
            }
        }

        Logger.Information("ComfyUI folder {FolderName} contains {Count} model(s)", folderName, models.Count);
        return models;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetNodeInputOptionsAsync(
        string nodeType,
        string inputName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Logger.Debug("Querying /object_info/{NodeType} for input {InputName} options", nodeType, inputName);

        using var response = await _httpClient.GetAsync($"/object_info/{Uri.EscapeDataString(nodeType)}", ct);
        if (!response.IsSuccessStatusCode)
        {
            Logger.Warning("/object_info/{NodeType} returned {StatusCode}", nodeType, response.StatusCode);
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var json = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);

        // Structure: { "NodeType": { "input": { "required": { "inputName": [["opt1","opt2"], {}] } } } }
        if (json.ValueKind == JsonValueKind.Object &&
            json.TryGetProperty(nodeType, out var nodeInfo) &&
            nodeInfo.TryGetProperty("input", out var inputSection) &&
            inputSection.TryGetProperty("required", out var required) &&
            required.TryGetProperty(inputName, out var inputDef) &&
            inputDef.ValueKind == JsonValueKind.Array &&
            inputDef.GetArrayLength() > 0)
        {
            var firstElement = inputDef[0];
            if (firstElement.ValueKind == JsonValueKind.Array)
            {
                var options = new List<string>();
                foreach (var item in firstElement.EnumerateArray())
                {
                    var value = item.GetString();
                    if (value is not null)
                    {
                        options.Add(value);
                    }
                }

                Logger.Information(
                    "Node {NodeType} input {InputName} has {Count} option(s)",
                    nodeType, inputName, options.Count);
                return options;
            }
        }

        Logger.Debug("No options found for node {NodeType} input {InputName}", nodeType, inputName);
        return [];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }

        _disposed = true;
    }

    /// <summary>
    /// Pure builder for the ComfyUI <c>/prompt</c> request envelope. Applies the caller-supplied
    /// per-node modifiers to the workflow, then wraps it in the exact JSON shape ComfyUI and its
    /// plugins expect. Performs no I/O.
    /// </summary>
    /// <param name="workflow">The parsed API-format workflow (mutated in place by the modifiers).</param>
    /// <param name="nodeModifiers">Node ID to mutation action. Missing nodes are skipped.</param>
    /// <param name="clientId">The client ID; echoed as <c>client_id</c> and <c>extra_pnginfo.workflow.id</c>.</param>
    /// <param name="onNodeNotFound">
    /// Optional callback invoked with the node ID whenever a modifier targets a node absent from
    /// the workflow. Kept as a callback so this function stays pure (the caller does the logging).
    /// </param>
    /// <returns>The <c>/prompt</c> payload envelope.</returns>
    /// <remarks>
    /// The <c>extra_data.extra_pnginfo.workflow</c> block encodes two <b>undocumented,
    /// reverse-engineered</b> contracts. Changing these bytes will silently break queuing:
    /// <list type="bullet">
    /// <item><c>workflow_name</c> + <c>id</c> are required by the comfyui_queue_manager plugin
    /// (qm_queue.py lines 221-222).</item>
    /// <item><c>nodes</c> (an empty array) is required by the ShowText|pysssss node
    /// (show_text.py line 34).</item>
    /// </list>
    /// </remarks>
    internal static JsonObject BuildPromptPayload(
        JsonNode workflow,
        IReadOnlyDictionary<string, Action<JsonNode>> nodeModifiers,
        string clientId,
        Action<string>? onNodeNotFound = null)
    {
        // Apply caller-supplied modifications to individual nodes.
        foreach (var (nodeId, modifier) in nodeModifiers)
        {
            var node = workflow[nodeId];
            if (node is not null)
            {
                modifier(node);
            }
            else
            {
                onNodeNotFound?.Invoke(nodeId);
            }
        }

        return new JsonObject
        {
            ["prompt"] = workflow,
            ["client_id"] = clientId,
            ["extra_data"] = new JsonObject
            {
                ["extra_pnginfo"] = new JsonObject
                {
                    ["workflow"] = new JsonObject
                    {
                        // Required by comfyui_queue_manager plugin (qm_queue.py lines 221-222)
                        ["workflow_name"] = "DiffusionNexus",
                        ["id"] = clientId,
                        // Required by ShowText|pysssss node (show_text.py line 34)
                        ["nodes"] = new JsonArray()
                    }
                }
            }
        };
    }

    /// <summary>
    /// Pure parser for a ComfyUI <c>/history/{promptId}</c> document. Extracts all text and image
    /// outputs for the given prompt into a <see cref="ComfyUIResult"/>. Performs no I/O.
    /// </summary>
    /// <param name="historyJson">The parsed <c>/history</c> response.</param>
    /// <param name="promptId">The prompt whose outputs to extract.</param>
    /// <param name="baseUrl">Base URL used to build image <c>/view</c> URLs.</param>
    internal static ComfyUIResult ParseHistoryOutputs(JsonNode? historyJson, string promptId, string baseUrl)
    {
        var result = new ComfyUIResult();

        if (historyJson?[promptId]?["outputs"] is not JsonObject outputNodes)
        {
            return result;
        }

        foreach (var (_, nodeOutput) in outputNodes)
        {
            ExtractTextOutputs(nodeOutput, result);
            ExtractImageOutputs(nodeOutput, baseUrl, result);
        }

        return result;
    }

    internal static void ExtractTextOutputs(JsonNode? nodeOutput, ComfyUIResult result)
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

    internal static void ExtractImageOutputs(JsonNode? nodeOutput, string baseUrl, ComfyUIResult result)
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
                Url: $"{baseUrl}/view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}"));
        }
    }
}

/// <summary>
/// The decoded meaning of a single ComfyUI WebSocket progress frame, produced by
/// <see cref="ComfyUIWrapperService.ParseProgressMessage"/>.
/// </summary>
internal enum ComfyUIProgressAction
{
    /// <summary>Nothing to do (unknown type, an event for another prompt, idle status, etc.).</summary>
    None = 0,

    /// <summary>Report <see cref="ComfyUIProgressMessage.ReportText"/> to the progress reporter.</summary>
    Report,

    /// <summary>Execution finished for the awaited prompt; the wait should complete.</summary>
    Completed,

    /// <summary>An execution error occurred for the awaited prompt; the wait should throw.</summary>
    Error
}

/// <summary>
/// A decoded ComfyUI WebSocket progress frame. Immutable value describing what the caller
/// should do, keeping <see cref="ComfyUIWrapperService.ParseProgressMessage"/> free of I/O.
/// </summary>
internal readonly record struct ComfyUIProgressMessage(
    ComfyUIProgressAction Action,
    string? Type = null,
    string? ReportText = null,
    string? ErrorNodeType = null,
    string? ErrorDetail = null,
    int? QueueRemaining = null)
{
    /// <summary>
    /// The exception message to surface when <see cref="Action"/> is
    /// <see cref="ComfyUIProgressAction.Error"/>. Preserves the historical wording exactly.
    /// </summary>
    public string ExecutionErrorMessage =>
        $"ComfyUI workflow failed in node '{ErrorNodeType}': {ErrorDetail}";
}
