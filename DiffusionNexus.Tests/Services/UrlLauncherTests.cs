using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.Services;

public sealed class UrlLauncherTests
{
    [Fact]
    public void TryOpen_HandsTheUrlToTheOpener()
    {
        string? opened = null;

        var result = UrlLauncher.TryOpen("https://example.com", url => opened = url, logger: null, "Test");

        result.Should().BeTrue();
        opened.Should().Be("https://example.com");
    }

    [Fact]
    public void TryOpen_WhenTheBrowserCannotStart_WarnsUnderTheSource_InsteadOfThrowing()
    {
        var logger = new Mock<IUnifiedLogger>();

        var result = UrlLauncher.TryOpen(
            "https://example.com",
            _ => throw new System.ComponentModel.Win32Exception("no browser"),
            logger.Object,
            "ServerMessages");

        result.Should().BeFalse();
        logger.Verify(l => l.Warn(LogCategory.General, "ServerMessages",
            It.Is<string>(m => m.Contains("https://example.com") && m.Contains("no browser")), It.IsAny<string?>()), Times.Once);
    }
}
