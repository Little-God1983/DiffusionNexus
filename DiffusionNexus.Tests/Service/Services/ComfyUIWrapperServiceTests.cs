using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

/// <summary>
/// Unit tests for <see cref="ComfyUIWrapperService"/>.
///
/// <para>
/// This service underpins every ComfyUI feature (captioning, inpaint, upscale, outpaint)
/// yet shipped with zero tests (issue #440). The highest-value logic is the
/// workflow/payload envelope construction, which encodes <b>undocumented</b> contracts with
/// the <c>comfyui_queue_manager</c> and <c>ShowText|pysssss</c> plugins: those exact JSON
/// bytes ARE the spec. The tests below pin the byte shape so a silent contract drift fails
/// loudly. WebSocket progress-protocol parsing and history/result parsing are pinned the
/// same way, and the HTTP paths are driven through an injected fake handler
/// (the <c>FakeHttpHandler</c> pattern from <c>CivitaiClientTests</c>).
/// </para>
/// </summary>
public class ComfyUIWrapperServiceTests
{
    private const string BaseUrl = "http://127.0.0.1:8188";

    // Node IDs are private consts on the service; mirror them here so the label-mapping
    // contract is asserted against literal wire values, not against the implementation.
    private const string LoadImageNodeId = "100";
    private const string Qwen3VqaNodeId = "705";

    private static (ComfyUIWrapperService sut, FakeHttpHandler handler) CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string baseUrl = BaseUrl)
    {
        var handler = new FakeHttpHandler(responder);
        var http = new HttpClient(handler);
        return (new ComfyUIWrapperService(http, baseUrl, disposeHttpClient: true), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ---------------------------------------------------------------------------------
    // Item 1: BuildPromptPayload - the undocumented plugin-contract envelope (:106-124)
    // ---------------------------------------------------------------------------------

    [Fact]
    public void BuildPromptPayload_ProducesExactEnvelopeBytes()
    {
        var workflow = JsonNode.Parse("""{"100":{"inputs":{"image":"x.png"}}}""")!;

        var payload = ComfyUIWrapperService.BuildPromptPayload(
            workflow,
            new Dictionary<string, Action<JsonNode>>(),
            "cid-123");

        // These exact bytes are the reverse-engineered contract with comfyui_queue_manager
        // (workflow_name + id) and ShowText|pysssss (nodes[]). Do NOT loosen this assertion.
        payload.ToJsonString().Should().Be(
            "{\"prompt\":{\"100\":{\"inputs\":{\"image\":\"x.png\"}}}," +
            "\"client_id\":\"cid-123\"," +
            "\"extra_data\":{\"extra_pnginfo\":{\"workflow\":{" +
            "\"workflow_name\":\"DiffusionNexus\"," +
            "\"id\":\"cid-123\"," +
            "\"nodes\":[]}}}}");
    }

    [Fact]
    public void BuildPromptPayload_SetsPromptClientIdWorkflowNameAndNodes()
    {
        var workflow = JsonNode.Parse("""{"1":{"a":1}}""")!;

        var payload = ComfyUIWrapperService.BuildPromptPayload(
            workflow, new Dictionary<string, Action<JsonNode>>(), "the-client-id");

        payload["prompt"]!["1"]!["a"]!.GetValue<int>().Should().Be(1);
        payload["client_id"]!.GetValue<string>().Should().Be("the-client-id");

        var inner = payload["extra_data"]!["extra_pnginfo"]!["workflow"]!;
        inner["workflow_name"]!.GetValue<string>().Should().Be("DiffusionNexus");
        inner["id"]!.GetValue<string>().Should().Be("the-client-id");
        inner["nodes"].Should().BeOfType<JsonArray>();
        ((JsonArray)inner["nodes"]!).Count.Should().Be(0);
    }

    [Fact]
    public void BuildPromptPayload_AppliesModifiersToMatchingNodes()
    {
        var workflow = JsonNode.Parse("""{"100":{"inputs":{"image":"old.png"}}}""")!;

        var payload = ComfyUIWrapperService.BuildPromptPayload(
            workflow,
            new Dictionary<string, Action<JsonNode>>
            {
                ["100"] = n => n["inputs"]!["image"] = "new.png"
            },
            "cid");

        payload["prompt"]!["100"]!["inputs"]!["image"]!.GetValue<string>().Should().Be("new.png");
    }

    [Fact]
    public void BuildPromptPayload_MissingNode_SkipsModifierAndInvokesCallback()
    {
        var workflow = JsonNode.Parse("""{"100":{"inputs":{}}}""")!;
        var notFound = new List<string>();

        var payload = ComfyUIWrapperService.BuildPromptPayload(
            workflow,
            new Dictionary<string, Action<JsonNode>>
            {
                // Must NOT run because node 999 does not exist.
                ["999"] = _ => throw new InvalidOperationException("modifier for missing node ran")
            },
            "cid",
            notFound.Add);

        notFound.Should().ContainSingle().Which.Should().Be("999");
        payload.Should().NotBeNull();
    }

    [Fact]
    public void BuildPromptPayload_MissingNode_WithNoCallback_DoesNotThrow()
    {
        var workflow = JsonNode.Parse("""{"100":{}}""")!;

        var act = () => ComfyUIWrapperService.BuildPromptPayload(
            workflow,
            new Dictionary<string, Action<JsonNode>> { ["nope"] = _ => { } },
            "cid");

        act.Should().NotThrow();
    }

    // ---------------------------------------------------------------------------------
    // Item 3: ParseProgressMessage - WebSocket progress protocol (:147-281)
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ParseProgressMessage_ExecutionError_MatchingPrompt_YieldsError()
    {
        const string json = """
            {"type":"execution_error","data":{"prompt_id":"p1","node_type":"Qwen3_VQA","exception_message":"boom"}}
            """;

        var result = ComfyUIWrapperService.ParseProgressMessage(json, "p1");

        result.Action.Should().Be(ComfyUIProgressAction.Error);
        result.ErrorNodeType.Should().Be("Qwen3_VQA");
        result.ErrorDetail.Should().Be("boom");
        result.ExecutionErrorMessage.Should().Be("ComfyUI workflow failed in node 'Qwen3_VQA': boom");
    }

    [Fact]
    public void ParseProgressMessage_ExecutionError_MissingFields_UsesDefaults()
    {
        const string json = """{"type":"execution_error","data":{"prompt_id":"p1"}}""";

        var result = ComfyUIWrapperService.ParseProgressMessage(json, "p1");

        result.Action.Should().Be(ComfyUIProgressAction.Error);
        result.ErrorNodeType.Should().Be("unknown");
        result.ErrorDetail.Should().Be("Unknown execution error");
    }

    [Fact]
    public void ParseProgressMessage_ExecutionError_DifferentPrompt_Ignored()
    {
        const string json = """{"type":"execution_error","data":{"prompt_id":"other","node_type":"X","exception_message":"y"}}""";

        var result = ComfyUIWrapperService.ParseProgressMessage(json, "p1");

        result.Action.Should().Be(ComfyUIProgressAction.None);
    }

    [Fact]
    public void ParseProgressMessage_Executing_NullNode_MatchingPrompt_IsCompleted()
    {
        const string json = """{"type":"executing","data":{"node":null,"prompt_id":"p1"}}""";

        var result = ComfyUIWrapperService.ParseProgressMessage(json, "p1");

        result.Action.Should().Be(ComfyUIProgressAction.Completed);
    }

    [Fact]
    public void ParseProgressMessage_Executing_NullNode_DifferentPrompt_Ignored()
    {
        const string json = """{"type":"executing","data":{"node":null,"prompt_id":"other"}}""";

        var result = ComfyUIWrapperService.ParseProgressMessage(json, "p1");

        result.Action.Should().Be(ComfyUIProgressAction.None);
    }

    [Fact]
    public void ParseProgressMessage_Executing_Qwen3VqaNode_ReportsInferenceLabel()
    {
        var json = "{\"type\":\"executing\",\"data\":{\"node\":\"" + Qwen3VqaNodeId + "\",\"prompt_id\":\"p1\"}}";

        var result = ComfyUIWrapperService.ParseProgressMessage(json, "p1");

        result.Action.Should().Be(ComfyUIProgressAction.Report);
        result.ReportText.Should().Be("Running Qwen3-VL inference (first run may download the model...)");
    }

    [Fact]
    public void ParseProgressMessage_Executing_LoadImageNode_ReportsLoadingLabel()
    {
        var json = "{\"type\":\"executing\",\"data\":{\"node\":\"" + LoadImageNodeId + "\",\"prompt_id\":\"p1\"}}";

        var result = ComfyUIWrapperService.ParseProgressMessage(json, "p1");

        result.Action.Should().Be(ComfyUIProgressAction.Report);
        result.ReportText.Should().Be("Loading image...");
    }

    [Fact]
    public void ParseProgressMessage_Executing_OtherNode_ReportsGenericLabel()
    {
        const string json = """{"type":"executing","data":{"node":"42","prompt_id":"p1"}}""";

        var result = ComfyUIWrapperService.ParseProgressMessage(json, "p1");

        result.Action.Should().Be(ComfyUIProgressAction.Report);
        result.ReportText.Should().Be("Executing node 42...");
    }

    [Fact]
    public void ParseProgressMessage_Executing_Node_DifferentPrompt_Ignored()
    {
        const string json = """{"type":"executing","data":{"node":"42","prompt_id":"other"}}""";

        var result = ComfyUIWrapperService.ParseProgressMessage(json, "p1");

        result.Action.Should().Be(ComfyUIProgressAction.None);
    }

    [Fact]
    public void ParseProgressMessage_Progress_WithMax_ReportsProgress()
    {
        const string json = """{"type":"progress","data":{"value":3,"max":10}}""";

        var result = ComfyUIWrapperService.ParseProgressMessage(json, "p1");

        result.Action.Should().Be(ComfyUIProgressAction.Report);
        result.ReportText.Should().Be("Progress: 3/10");
    }

    [Fact]
    public void ParseProgressMessage_Progress_ZeroMax_Ignored()
    {
        const string json = """{"type":"progress","data":{"value":0,"max":0}}""";

        var result = ComfyUIWrapperService.ParseProgressMessage(json, "p1");

        result.Action.Should().Be(ComfyUIProgressAction.None);
    }

    [Fact]
    public void ParseProgressMessage_Status_ExposesQueueRemaining()
    {
        const string json = """{"type":"status","data":{"status":{"exec_info":{"queue_remaining":4}}}}""";

        var result = ComfyUIWrapperService.ParseProgressMessage(json, "p1");

        result.Action.Should().Be(ComfyUIProgressAction.None);
        result.QueueRemaining.Should().Be(4);
    }

    [Fact]
    public void ParseProgressMessage_UnknownType_Ignored()
    {
        const string json = """{"type":"executed","data":{}}""";

        var result = ComfyUIWrapperService.ParseProgressMessage(json, "p1");

        result.Action.Should().Be(ComfyUIProgressAction.None);
    }

    [Fact]
    public void ParseProgressMessage_JsonNullLiteral_Ignored()
    {
        var result = ComfyUIWrapperService.ParseProgressMessage("null", "p1");

        result.Action.Should().Be(ComfyUIProgressAction.None);
    }

    [Fact]
    public void ParseProgressMessage_MessageWithoutType_Ignored()
    {
        var result = ComfyUIWrapperService.ParseProgressMessage("""{"data":{}}""", "p1");

        result.Action.Should().Be(ComfyUIProgressAction.None);
    }

    // ---------------------------------------------------------------------------------
    // History / result parsing (:282+, ShowText nested-array + image URL contracts)
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ExtractTextOutputs_FlatArray_AddsEachText()
    {
        var node = JsonNode.Parse("""{"text":["a","b"]}""");
        var result = new ComfyUIResult();

        ComfyUIWrapperService.ExtractTextOutputs(node, result);

        result.Texts.Should().Equal("a", "b");
    }

    [Fact]
    public void ExtractTextOutputs_NestedArray_UnwrapsShowTextPysssssContract()
    {
        // ShowText|pysssss with INPUT_IS_LIST/OUTPUT_IS_LIST wraps text as [["caption"]].
        var node = JsonNode.Parse("""{"text":[["caption"]]}""");
        var result = new ComfyUIResult();

        ComfyUIWrapperService.ExtractTextOutputs(node, result);

        result.Texts.Should().Equal("caption");
    }

    [Fact]
    public void ExtractImageOutputs_BuildsEscapedViewUrl()
    {
        var node = JsonNode.Parse("""{"images":[{"filename":"a b.png","subfolder":"sub dir","type":"output"}]}""");
        var result = new ComfyUIResult();

        ComfyUIWrapperService.ExtractImageOutputs(node, BaseUrl, result);

        result.Images.Should().ContainSingle();
        var img = result.Images[0];
        img.Filename.Should().Be("a b.png");
        img.Subfolder.Should().Be("sub dir");
        img.Type.Should().Be("output");
        img.Url.Should().Be($"{BaseUrl}/view?filename=a%20b.png&subfolder=sub%20dir&type=output");
    }

    [Fact]
    public void ExtractImageOutputs_MissingFilename_Skipped()
    {
        var node = JsonNode.Parse("""{"images":[{"subfolder":"s","type":"output"}]}""");
        var result = new ComfyUIResult();

        ComfyUIWrapperService.ExtractImageOutputs(node, BaseUrl, result);

        result.Images.Should().BeEmpty();
    }

    [Fact]
    public void ParseHistoryOutputs_ExtractsTextAndImages()
    {
        var history = JsonNode.Parse("""
            {"p1":{"outputs":{
                "9":{"text":[["hello"]]},
                "10":{"images":[{"filename":"out.png","subfolder":"","type":"output"}]}
            }}}
            """);

        var result = ComfyUIWrapperService.ParseHistoryOutputs(history, "p1", BaseUrl);

        result.Texts.Should().Equal("hello");
        result.Images.Should().ContainSingle();
        result.Images[0].Filename.Should().Be("out.png");
    }

    [Fact]
    public void ParseHistoryOutputs_NoOutputsForPrompt_ReturnsEmpty()
    {
        var history = JsonNode.Parse("""{"other":{"outputs":{}}}""");

        var result = ComfyUIWrapperService.ParseHistoryOutputs(history, "p1", BaseUrl);

        result.Texts.Should().BeEmpty();
        result.Images.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------
    // Item 2: HttpClient constructor seam - HTTP paths driven by an injected handler
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Ctor_NullHttpClient_Throws()
    {
        var act = () => new ComfyUIWrapperService((HttpClient)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task UploadImageAsync_PostsToUploadEndpoint_ReturnsStoredName()
    {
        var tempImage = Path.GetTempFileName();
        await File.WriteAllBytesAsync(tempImage, [1, 2, 3]);
        var (sut, handler) = CreateService(_ => Json(HttpStatusCode.OK, """{"name":"stored.png"}"""));

        try
        {
            using (sut)
            {
                var stored = await sut.UploadImageAsync(tempImage);
                stored.Should().Be("stored.png");
            }

            handler.Requests.Should().ContainSingle();
            handler.Requests[0].Method.Should().Be(HttpMethod.Post);
            handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/upload/image");
        }
        finally
        {
            File.Delete(tempImage);
        }
    }

    [Fact]
    public async Task QueueWorkflowAsync_PostsPayloadToPrompt_ReturnsPromptId()
    {
        var workflowFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(workflowFile, """{"100":{"inputs":{"image":"x.png"}}}""");
        string? capturedBody = null;
        var (sut, handler) = CreateService(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, """{"prompt_id":"pid-9"}""");
        });

        try
        {
            using (sut)
            {
                var promptId = await sut.QueueWorkflowAsync(
                    workflowFile, new Dictionary<string, Action<JsonNode>>());
                promptId.Should().Be("pid-9");
            }

            handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/prompt");
            capturedBody.Should().Contain("\"workflow_name\":\"DiffusionNexus\"");
            capturedBody.Should().Contain("\"nodes\":[]");
        }
        finally
        {
            File.Delete(workflowFile);
        }
    }

    [Fact]
    public async Task GetResultAsync_ParsesHistoryOutputs()
    {
        var (sut, handler) = CreateService(_ => Json(HttpStatusCode.OK, """
            {"p1":{"outputs":{"9":{"text":[["caption"]]}}}}
            """));

        using (sut)
        {
            var result = await sut.GetResultAsync("p1");
            result.Texts.Should().Equal("caption");
        }

        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/history/p1");
    }

    [Fact]
    public async Task DownloadImageAsync_GetsImageBytes()
    {
        var (sut, _) = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([9, 8, 7])
        });

        using (sut)
        {
            var image = new ComfyUIImage("f.png", "", "output", $"{BaseUrl}/view?filename=f.png");
            var bytes = await sut.DownloadImageAsync(image);
            bytes.Should().Equal(9, 8, 7);
        }
    }

    [Fact]
    public async Task GetInstalledNodeTypesAsync_ReturnsNodeKeys()
    {
        var (sut, handler) = CreateService(_ => Json(HttpStatusCode.OK,
            """{"LoadImage":{},"Qwen3_VQA":{},"ShowText|pysssss":{}}"""));

        using (sut)
        {
            var types = await sut.GetInstalledNodeTypesAsync();
            types.Should().BeEquivalentTo("LoadImage", "Qwen3_VQA", "ShowText|pysssss");
        }

        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/object_info");
    }

    [Fact]
    public async Task CheckRequiredNodesAsync_ReturnsOnlyMissing()
    {
        var (sut, _) = CreateService(_ => Json(HttpStatusCode.OK, """{"LoadImage":{}}"""));

        using (sut)
        {
            var missing = await sut.CheckRequiredNodesAsync(["LoadImage", "Qwen3_VQA"]);
            missing.Should().Equal("Qwen3_VQA");
        }
    }

    [Fact]
    public async Task GetModelsInFolderAsync_NonSuccess_ReturnsEmpty()
    {
        var (sut, _) = CreateService(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        using (sut)
        {
            var models = await sut.GetModelsInFolderAsync("prompt_generator");
            models.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task GetModelsInFolderAsync_ReturnsModelNames()
    {
        var (sut, handler) = CreateService(_ => Json(HttpStatusCode.OK, """["a.safetensors","b.gguf"]"""));

        using (sut)
        {
            var models = await sut.GetModelsInFolderAsync("prompt_generator");
            models.Should().Equal("a.safetensors", "b.gguf");
        }

        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/models/prompt_generator");
    }

    [Fact]
    public async Task GetNodeInputOptionsAsync_ParsesFirstElementOptionList()
    {
        var (sut, _) = CreateService(_ => Json(HttpStatusCode.OK, """
            {"Qwen3_VQA":{"input":{"required":{"model":[["m1","m2"],{}]}}}}
            """));

        using (sut)
        {
            var options = await sut.GetNodeInputOptionsAsync("Qwen3_VQA", "model");
            options.Should().Equal("m1", "m2");
        }
    }

    [Fact]
    public void Dispose_DoesNotDisposeExternallyOwnedHttpClient()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var sut = new ComfyUIWrapperService(http, BaseUrl, disposeHttpClient: false);

        sut.Dispose();

        // If the HttpClient had been disposed, SendAsync would throw ObjectDisposedException.
        var act = async () => await http.GetAsync("/object_info");
        act.Should().NotThrowAsync<ObjectDisposedException>();

        http.Dispose();
    }

    [Fact]
    public void LegacyStringCtor_StillConstructs()
    {
        // Call sites like App.axaml.cs use `new ComfyUIWrapperService()`; that path must
        // keep working and own its internal HttpClient.
        using var sut = new ComfyUIWrapperService();
        sut.Should().NotBeNull();
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();

        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }
}
