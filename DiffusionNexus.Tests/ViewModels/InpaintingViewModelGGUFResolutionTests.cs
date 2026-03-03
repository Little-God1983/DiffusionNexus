using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;
using System.Text.Json.Nodes;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Tests that <see cref="InpaintingViewModel.ProcessInpaintAsync"/> dynamically resolves
/// the correct Qwen Image 2512 GGUF variant from the ComfyUI server before queuing.
/// </summary>
public class InpaintingViewModelGGUFResolutionTests
{
    private readonly Mock<IComfyUIWrapperService> _comfyMock = new();
    private readonly InpaintingViewModel _sut;
    private readonly List<string?> _statusMessages = [];

    public InpaintingViewModelGGUFResolutionTests()
    {
        _sut = new InpaintingViewModel(
            hasImage: () => true,
            deactivateOtherTools: _ => { },
            comfyUiService: _comfyMock.Object,
            eventAggregator: null);

        _sut.PositivePrompt = "test prompt";
        _sut.StatusMessageChanged += (_, msg) => _statusMessages.Add(msg);

        // Default: upload returns a filename
        _comfyMock
            .Setup(s => s.UploadImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("uploaded.png");
    }

    [Fact]
    public async Task WhenNoQwenGGUFModelAvailable_ThenShowsErrorAndDoesNotQueueWorkflow()
    {
        // Arrange — server has GGUF models but none matching qwen-image-2512
        _comfyMock
            .Setup(s => s.GetNodeInputOptionsAsync("UnetLoaderGGUF", "unet_name", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "flux1-dev-Q8_0.gguf", "sd15.gguf" });

        // Act
        await _sut.ProcessInpaintAsync(CreateTempImage());

        // Assert
        _sut.HasError.Should().BeTrue("the error bar should stay visible when no model is found");
        _statusMessages.Should().Contain(m => m != null && m.Contains("No Qwen Image 2512 GGUF model found"));
        _comfyMock.Verify(
            s => s.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenOnlyQ4KMAvailable_ThenUsesQ4KM()
    {
        // Arrange
        SetupAvailableModels("qwen-image-2512-Q4_K_M.gguf");
        SetupQueueAndResult();

        string? capturedUnetName = null;
        _comfyMock
            .Setup(s => s.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Dictionary<string, Action<JsonNode>>, CancellationToken>((_, modifiers, _) =>
            {
                capturedUnetName = CaptureUnetName(modifiers);
            })
            .ReturnsAsync("prompt-1");

        // Act
        await _sut.ProcessInpaintAsync(CreateTempImage());

        // Assert
        capturedUnetName.Should().Be("qwen-image-2512-Q4_K_M.gguf");
    }

    [Fact]
    public async Task WhenMultipleVariantsAvailable_ThenPrefersHighestQuality()
    {
        // Arrange — Q4_K_M and Q8_0 both present; Q8_0 should win
        SetupAvailableModels("qwen-image-2512-Q4_K_M.gguf", "qwen-image-2512-Q8_0.gguf");
        SetupQueueAndResult();

        string? capturedUnetName = null;
        _comfyMock
            .Setup(s => s.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Dictionary<string, Action<JsonNode>>, CancellationToken>((_, modifiers, _) =>
            {
                capturedUnetName = CaptureUnetName(modifiers);
            })
            .ReturnsAsync("prompt-1");

        // Act
        await _sut.ProcessInpaintAsync(CreateTempImage());

        // Assert
        capturedUnetName.Should().Be("qwen-image-2512-Q8_0.gguf");
    }

    [Fact]
    public async Task WhenQ6KAndQ4Available_ThenPrefersQ6K()
    {
        // Arrange
        SetupAvailableModels("qwen-image-2512-Q4_0.gguf", "qwen-image-2512-Q6_K.gguf");
        SetupQueueAndResult();

        string? capturedUnetName = null;
        _comfyMock
            .Setup(s => s.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Dictionary<string, Action<JsonNode>>, CancellationToken>((_, modifiers, _) =>
            {
                capturedUnetName = CaptureUnetName(modifiers);
            })
            .ReturnsAsync("prompt-1");

        // Act
        await _sut.ProcessInpaintAsync(CreateTempImage());

        // Assert
        capturedUnetName.Should().Be("qwen-image-2512-Q6_K.gguf");
    }

    [Fact]
    public async Task WhenGetNodeInputOptionsThrows_ThenFallsBackToDefaultQ8()
    {
        // Arrange — API call fails; should fall back to hardcoded default
        _comfyMock
            .Setup(s => s.GetNodeInputOptionsAsync("UnetLoaderGGUF", "unet_name", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));
        SetupQueueAndResult();

        string? capturedUnetName = null;
        _comfyMock
            .Setup(s => s.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Dictionary<string, Action<JsonNode>>, CancellationToken>((_, modifiers, _) =>
            {
                capturedUnetName = CaptureUnetName(modifiers);
            })
            .ReturnsAsync("prompt-1");

        // Act
        await _sut.ProcessInpaintAsync(CreateTempImage());

        // Assert — falls back to default Q8_0
        capturedUnetName.Should().Be("qwen-image-2512-Q8_0.gguf");
    }

    [Fact]
    public async Task WhenServerReturnsEmptyList_ThenShowsErrorMessage()
    {
        // Arrange
        _comfyMock
            .Setup(s => s.GetNodeInputOptionsAsync("UnetLoaderGGUF", "unet_name", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        // Act
        await _sut.ProcessInpaintAsync(CreateTempImage());

        // Assert
        _sut.HasError.Should().BeTrue("the error bar should stay visible when no model is found");
        _statusMessages.Should().Contain(m => m != null && m.Contains("No Qwen Image 2512 GGUF model found"));
        _comfyMock.Verify(
            s => s.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #region Helpers

    private void SetupAvailableModels(params string[] models)
    {
        _comfyMock
            .Setup(s => s.GetNodeInputOptionsAsync("UnetLoaderGGUF", "unet_name", It.IsAny<CancellationToken>()))
            .ReturnsAsync(models.ToList());
    }

    private void SetupQueueAndResult()
    {
        _comfyMock
            .Setup(s => s.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("prompt-1");

        _comfyMock
            .Setup(s => s.WaitForCompletionAsync("prompt-1", It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _comfyMock
            .Setup(s => s.GetResultAsync("prompt-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComfyUIResult());
    }

    /// <summary>Extracts the unet_name value set by the node modifier for node "15".</summary>
    private static string? CaptureUnetName(Dictionary<string, Action<JsonNode>> modifiers)
    {
        if (!modifiers.TryGetValue("15", out var modifier))
            return null;

        var fakeNode = JsonNode.Parse("""{"inputs": {"unet_name": "placeholder"}}""")!;
        modifier(fakeNode);
        return fakeNode["inputs"]?["unet_name"]?.GetValue<string>();
    }

    private static string CreateTempImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_inpaint_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]); // minimal PNG header bytes
        return path;
    }

    #endregion
}
