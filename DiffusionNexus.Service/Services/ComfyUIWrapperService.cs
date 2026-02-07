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
            ["client_id"] = _clientId
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

            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var json = JsonNode.Parse(msg);
            var type = json?["type"]?.GetValue<string>();

            progress?.Report($"Event: {type}");

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

    /// <inheritdoc />
    public async Task<ComfyUIResult> GetResultAsync(string promptId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Logger.Debug("Fetching results for prompt {PromptId}", promptId);

        using var response = await _httpClient.GetAsync($"/history/{promptId}", ct);
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))
                   ?? throw new InvalidOperationException("ComfyUI returned an unparseable /history response.");

        var outputs = json[promptId]?["outputs"];
        var comfyResult = new ComfyUIResult();

        if (outputs is not JsonObject outputNodes)
        {
            Logger.Warning("No outputs found for prompt {PromptId}", promptId);
            return comfyResult;
        }

        foreach (var (_, nodeOutput) in outputNodes)
        {
            ExtractTextOutputs(nodeOutput, comfyResult);
            ExtractImageOutputs(nodeOutput, comfyResult);
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

        foreach (var t in textArray)
        {
            var text = t?.GetValue<string>();
            if (text is not null)
            {
                result.Texts.Add(text);
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
