using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Issue #630: a pasted link or ID resolves to the models shown as browser cards. A bare number
/// is looked up as a model ID and as a version ID — "if it can be two things it should yield two
/// things".
/// </summary>
public sealed class CivitaiLinkLookupTests
{
    private readonly Mock<ICivitaiClient> _client = new();

    private static CivitaiModel Model(int id, params int[] versionIds) => new()
    {
        Id = id,
        Name = $"Model {id}",
        ModelVersions = versionIds.Select(v => new CivitaiModelVersion { Id = v, ModelId = id, Name = $"v{v}" }).ToList(),
    };

    private void HasModel(CivitaiModel model) =>
        _client.Setup(c => c.GetModelAsync(model.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);

    private void HasVersion(int versionId, int modelId) =>
        _client.Setup(c => c.GetModelVersionAsync(versionId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = versionId, ModelId = modelId, Name = $"v{versionId}" });

    private Task<CivitaiLinkLookupResult> Resolve(string text)
    {
        CivitaiLinkQuery.TryParse(text, out var query).Should().BeTrue();
        return new CivitaiLinkLookup(_client.Object, null).ResolveAsync(query!, null, CancellationToken.None);
    }

    [Fact]
    public async Task BareNumber_ThatIsBothAModelAndAVersion_YieldsTwoCards()
    {
        HasModel(Model(500, 501, 502));
        HasVersion(500, modelId: 77);
        HasModel(Model(77, 600, 500, 400));

        var result = await Resolve("500");

        result.Message.Should().BeNull();
        result.Matches.Should().HaveCount(2);
        result.Matches[0].Model.Id.Should().Be(500);
        result.Matches[0].VersionId.Should().BeNull("a model match pre-ticks the latest version");
        result.Matches[0].MatchLabel.Should().Be("Model ID 500");
        result.Matches[1].Model.Id.Should().Be(77);
        result.Matches[1].VersionId.Should().Be(500);
        result.Matches[1].MatchLabel.Should().Be("Version ID 500");
    }

    [Fact]
    public async Task BareNumber_ThatIsOnlyAVersion_YieldsThatVersionsModel()
    {
        HasVersion(3175491, modelId: 2815542);
        HasModel(Model(2815542, 3175491, 3000000));

        var result = await Resolve("3175491");

        result.Matches.Should().ContainSingle();
        result.Matches[0].Model.Id.Should().Be(2815542);
        result.Matches[0].VersionId.Should().Be(3175491);
    }

    [Fact]
    public async Task BareNumber_ThatIsAVersionOfTheModelItNames_YieldsOneCard()
    {
        HasModel(Model(42, 43, 42));
        HasVersion(42, modelId: 42);

        var result = await Resolve("42");

        result.Matches.Should().ContainSingle();
        result.Matches[0].VersionId.Should().Be(42);
    }

    [Fact]
    public async Task BareNumber_ThatIsNothing_SaysSo()
    {
        var result = await Resolve("123");

        result.Matches.Should().BeEmpty();
        result.Message.Should().Be("Civitai has no model and no version with ID 123.");
    }

    [Fact]
    public async Task VersionLink_FetchesTheModelOnce_AndPreticksTheLinkedVersion()
    {
        HasModel(Model(2815542, 3200000, 3175491));

        var result = await Resolve("https://civitai.red/models/2815542/x?modelVersionId=3175491");

        result.Matches.Should().ContainSingle();
        result.Matches[0].VersionId.Should().Be(3175491);
        _client.Verify(c => c.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never, "the model id in the link already names the model");
    }

    [Fact]
    public async Task VersionLink_WhoseVersionTheModelNoLongerLists_FallsBackToTheLatest()
    {
        HasModel(Model(10, 11, 12));

        var result = await Resolve("https://civitai.com/models/10?modelVersionId=999");

        result.Matches.Should().ContainSingle();
        result.Matches[0].VersionId.Should().BeNull();
    }

    /// <summary>
    /// Review finding: the version endpoint knows a version the model listing does not (unpublished,
    /// hidden). The card must not claim "Version ID N" while the latest is what gets ticked.
    /// </summary>
    [Fact]
    public async Task VersionTheModelDoesNotList_IsNotClaimed_AndTheUserIsTold()
    {
        HasVersion(456, modelId: 77);
        HasModel(Model(77, 600, 500));

        var result = await Resolve("https://civitai.com/models?modelVersionId=456");

        result.Matches.Should().ContainSingle();
        result.Matches[0].VersionId.Should().BeNull();
        result.Matches[0].MatchLabel.Should().NotContain("Version ID 456");
        result.Message.Should().Contain("456").And.Contain("latest");
    }

    [Fact]
    public async Task ModelLinkWithAVersionTheModelDoesNotList_TellsTheUser()
    {
        HasModel(Model(10, 11, 12));

        var result = await Resolve("https://civitai.com/models/10?modelVersionId=999");

        result.Message.Should().Contain("999").And.Contain("latest");
    }

    /// <summary>
    /// Review finding: a bare id runs two requests; one failing (429 / 5xx) must not throw away the
    /// card the other one found.
    /// </summary>
    [Fact]
    public async Task BareNumber_OneRequestFailing_StillShowsWhatTheOtherFound()
    {
        HasModel(Model(2815542, 3175491));
        _client.Setup(c => c.GetModelVersionAsync(2815542, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited", null, System.Net.HttpStatusCode.TooManyRequests));

        var result = await Resolve("2815542");

        result.Matches.Should().ContainSingle();
        result.Matches[0].Model.Id.Should().Be(2815542);
        result.Message.Should().Contain("version");
    }

    [Fact]
    public async Task BareNumber_BothRequestsFailing_Throws()
    {
        _client.Setup(c => c.GetModelAsync(5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down"));
        _client.Setup(c => c.GetModelVersionAsync(5, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down"));

        var act = () => Resolve("5");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    /// <summary>Review finding: the version was found; the empty result is its model's fault, not the version's.</summary>
    [Fact]
    public async Task VersionWhoseModelIsGone_BlamesTheModel()
    {
        HasVersion(456, modelId: 77);

        var result = await Resolve("https://civitai.com/models?modelVersionId=456");

        result.Matches.Should().BeEmpty();
        result.Message.Should().Contain("model 77");
    }

    [Fact]
    public async Task ArchivedModel_IsNotShown_AndSaysWhy()
    {
        HasModel(Model(10, 11) with { Mode = CivitaiModelMode.Archived });

        var result = await Resolve("https://civitai.com/models/10");

        result.Matches.Should().BeEmpty();
        result.Message.Should().Contain("no longer available");
    }

    [Fact]
    public async Task LinkToAnImagePage_SaysItIsNotAModelLink_WithoutCallingCivitai()
    {
        var result = await Resolve("https://civitai.com/images/123");

        result.Matches.Should().BeEmpty();
        result.Message.Should().Contain("does not point at a model");
        _client.VerifyNoOtherCalls();
    }
}
