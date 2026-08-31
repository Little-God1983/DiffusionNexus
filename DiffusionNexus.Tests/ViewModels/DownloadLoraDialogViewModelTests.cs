using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Covers the Download-LoRA dialog's early-access/paywall awareness: resolving a gated
/// version must raise the same notice flags the detail panel and browser show, so the
/// user learns about the gate in the preview instead of from a failed transfer.
/// </summary>
public class DownloadLoraDialogViewModelTests
{
    private static readonly DateTimeOffset Now =
        new(DateTime.UtcNow.Date.AddHours(10), TimeSpan.Zero);

    private static CivitaiModelVersion Version(int id, DateTimeOffset? deadline, bool? permanent = null)
        => new()
        {
            Id = id,
            ModelId = 123,
            Name = $"v{id}",
            BaseModel = "Krea 2",
            DownloadUrl = $"https://civitai.example/api/download/models/{id}",
            EarlyAccessDeadline = deadline,
            PaidAccess = permanent is null ? null : new CivitaiPaidAccess { Permanent = permanent }
        };

    private static DownloadLoraDialogViewModel CreateVm(CivitaiModelVersion version)
    {
        var model = new CivitaiModel { Id = 123, Name = "Test LoRA", ModelVersions = [version] };
        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelAsync(123, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        var apiKeys = new Mock<ICivitaiApiKeyProvider>();
        apiKeys.Setup(k => k.GetApiKeyAsync()).ReturnsAsync((string?)null);

        return new DownloadLoraDialogViewModel(client.Object, null, null, null, apiKeys.Object)
        {
            UrlText = "https://civitai.com/models/123"
        };
    }

    [Fact]
    public async Task Search_ResolvingAnEarlyAccessVersion_RaisesTheEaNotice()
    {
        var vm = CreateVm(Version(1, Now.AddDays(7)));

        await vm.SearchCommand.ExecuteAsync(null);

        vm.HasPreview.Should().BeTrue();
        vm.ShowEarlyAccessNotice.Should().BeTrue();
        vm.IsPermanentlyPaid.Should().BeFalse();
    }

    [Fact]
    public async Task Search_ResolvingAPermanentlyPaidVersion_RaisesThePaidNoticeNotEa()
    {
        var vm = CreateVm(Version(2, deadline: null, permanent: true));

        await vm.SearchCommand.ExecuteAsync(null);

        vm.HasPreview.Should().BeTrue();
        vm.IsPermanentlyPaid.Should().BeTrue();
        vm.ShowEarlyAccessNotice.Should().BeFalse("the stronger PAID notice wins, never both");
    }

    [Fact]
    public async Task Search_ResolvingAFreeVersion_RaisesNoNotice()
    {
        var vm = CreateVm(Version(3, deadline: null));

        await vm.SearchCommand.ExecuteAsync(null);

        vm.HasPreview.Should().BeTrue();
        vm.ShowEarlyAccessNotice.Should().BeFalse();
        vm.IsPermanentlyPaid.Should().BeFalse();
    }

    [Fact]
    public async Task Search_AfterAGatedResult_ResetsTheNoticeForTheNextLookup()
    {
        // One client that knows a gated model and a free one; same VM searches both in turn.
        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelAsync(123, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModel { Id = 123, Name = "EA LoRA", ModelVersions = [Version(4, Now.AddDays(7))] });
        client.Setup(c => c.GetModelAsync(456, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModel { Id = 456, Name = "Free LoRA", ModelVersions = [Version(5, deadline: null)] });
        var apiKeys = new Mock<ICivitaiApiKeyProvider>();
        apiKeys.Setup(k => k.GetApiKeyAsync()).ReturnsAsync((string?)null);
        var vm = new DownloadLoraDialogViewModel(client.Object, null, null, null, apiKeys.Object)
        {
            UrlText = "https://civitai.com/models/123"
        };

        await vm.SearchCommand.ExecuteAsync(null);
        vm.ShowEarlyAccessNotice.Should().BeTrue("precondition");

        vm.UrlText = "https://civitai.com/models/456";
        await vm.SearchCommand.ExecuteAsync(null);

        vm.ShowEarlyAccessNotice.Should().BeFalse("stale notices must not linger on the next lookup");
        vm.IsPermanentlyPaid.Should().BeFalse();
    }
}
