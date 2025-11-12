using DiffusionNexus.Service.Services;
using DiffusionNexus.Service.Classes;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.LoraSort.Services;

public class CivitaiModelServiceTests
{
    [Fact]
    public void TryParseModelUrl_ShouldHandleSluggedModelLinkWithVersionQuery()
    {
        var apiClient = new Mock<ICivitaiApiClient>();
        var service = new CivitaiModelService(apiClient.Object);

        var url = "https://civitai.com/models/2108995/qwen-x-pony?modelVersionId=2385870";

        var result = service.TryParseModelUrl(url, out var reference);

        result.Should().BeTrue();
        reference.ModelId.Should().Be(2108995);
        reference.ModelVersionId.Should().Be(2385870);
    }
}
