using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services.Diffusion;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Services.Diffusion;

/// <summary>
/// Unit tests for <see cref="OutputsFolderRegistrar"/>: registering the local-diffusion
/// outputs folder in <c>AppSettings.ImageGalleries</c> exactly once, the display-order
/// computation, and the swallow-everything failure policy that keeps startup alive.
/// </summary>
public class OutputsFolderRegistrarTests
{
    private readonly Mock<IAppSettingsService> _settingsService = new(MockBehavior.Loose);

    private OutputsFolderRegistrar CreateSut() => new(_settingsService.Object);

    private void GivenSettings(AppSettings settings) =>
        _settingsService
            .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

    private static ImageGallery Gallery(string path, int order) =>
        new() { FolderPath = path, Order = order, IsEnabled = true };

    #region Construction

    [Fact]
    public void WhenSettingsServiceIsNullThenConstructorThrows()
    {
        var act = () => new OutputsFolderRegistrar(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenOutputsDirectoryReadThenItSitsNextToTheRunningExecutable()
    {
        OutputsFolderRegistrar.OutputsDirectory
            .Should().Be(Path.Combine(AppContext.BaseDirectory, "outputs"));
    }

    #endregion

    #region Registration

    [Fact]
    public async Task WhenNoGalleriesRegisteredThenOutputsFolderIsAddedAndSaved()
    {
        var settings = new AppSettings();
        GivenSettings(settings);

        await CreateSut().EnsureRegisteredAsync();

        settings.ImageGalleries.Should().ContainSingle();
        var added = settings.ImageGalleries.Single();
        added.FolderPath.Should().Be(OutputsFolderRegistrar.OutputsDirectory);
        added.IsEnabled.Should().BeTrue();
        added.Order.Should().Be(0);
        _settingsService.Verify(s => s.SaveSettingsAsync(settings, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenGalleriesCollectionIsNullThenItIsInitialisedBeforeAdding()
    {
        var settings = new AppSettings { ImageGalleries = null! };
        GivenSettings(settings);

        await CreateSut().EnsureRegisteredAsync();

        settings.ImageGalleries.Should().NotBeNull();
        settings.ImageGalleries.Should().ContainSingle()
            .Which.FolderPath.Should().Be(OutputsFolderRegistrar.OutputsDirectory);
        _settingsService.Verify(s => s.SaveSettingsAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenOutputsFolderAlreadyRegisteredThenNothingIsAddedOrSaved()
    {
        var settings = new AppSettings();
        settings.ImageGalleries.Add(Gallery(OutputsFolderRegistrar.OutputsDirectory, 0));
        GivenSettings(settings);

        await CreateSut().EnsureRegisteredAsync();

        settings.ImageGalleries.Should().ContainSingle();
        _settingsService.Verify(
            s => s.SaveSettingsAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenRegisteredPathDiffersOnlyByCasingThenItIsTreatedAsAlreadyRegistered()
    {
        var settings = new AppSettings();
        settings.ImageGalleries.Add(
            Gallery(OutputsFolderRegistrar.OutputsDirectory.ToUpperInvariant(), 0));
        GivenSettings(settings);

        await CreateSut().EnsureRegisteredAsync();

        settings.ImageGalleries.Should().ContainSingle();
        _settingsService.Verify(
            s => s.SaveSettingsAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenCalledTwiceThenTheFolderIsRegisteredOnlyOnce()
    {
        var settings = new AppSettings();
        GivenSettings(settings);
        var sut = CreateSut();

        await sut.EnsureRegisteredAsync();
        await sut.EnsureRegisteredAsync();

        settings.ImageGalleries.Should().ContainSingle();
        _settingsService.Verify(
            s => s.SaveSettingsAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Order computation

    [Fact]
    public async Task WhenOtherGalleriesExistThenNewEntryTakesTheNextOrder()
    {
        var settings = new AppSettings();
        settings.ImageGalleries.Add(Gallery(@"C:\gallery\a", 3));
        settings.ImageGalleries.Add(Gallery(@"C:\gallery\b", 7));
        GivenSettings(settings);

        await CreateSut().EnsureRegisteredAsync();

        settings.ImageGalleries
            .Single(g => g.FolderPath == OutputsFolderRegistrar.OutputsDirectory)
            .Order.Should().Be(8);
    }

    [Fact]
    public async Task WhenExistingOrdersAreOutOfSequenceThenTheMaximumDrivesTheNextOrder()
    {
        var settings = new AppSettings();
        settings.ImageGalleries.Add(Gallery(@"C:\gallery\a", 12));
        settings.ImageGalleries.Add(Gallery(@"C:\gallery\b", 2));
        GivenSettings(settings);

        await CreateSut().EnsureRegisteredAsync();

        settings.ImageGalleries
            .Single(g => g.FolderPath == OutputsFolderRegistrar.OutputsDirectory)
            .Order.Should().Be(13);
    }

    [Fact]
    public async Task WhenExistingOrdersAreNegativeThenTheNextOrderStillIncrementsTheMaximum()
    {
        var settings = new AppSettings();
        settings.ImageGalleries.Add(Gallery(@"C:\gallery\a", -5));
        GivenSettings(settings);

        await CreateSut().EnsureRegisteredAsync();

        settings.ImageGalleries
            .Single(g => g.FolderPath == OutputsFolderRegistrar.OutputsDirectory)
            .Order.Should().Be(-4);
    }

    #endregion

    #region Failure policy

    [Fact]
    public async Task WhenLoadingSettingsFailsThenStartupIsNotBlocked()
    {
        _settingsService
            .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var act = async () => await CreateSut().EnsureRegisteredAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WhenSavingSettingsFailsThenStartupIsNotBlocked()
    {
        GivenSettings(new AppSettings());
        _settingsService
            .Setup(s => s.SaveSettingsAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var act = async () => await CreateSut().EnsureRegisteredAsync();

        await act.Should().NotThrowAsync();
    }

    #endregion
}
