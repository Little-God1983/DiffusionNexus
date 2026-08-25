using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Infrastructure;

/// <summary>
/// Covers <see cref="CivitaiApiKeyProvider"/> — the single Civitai API-key lookup that
/// replaced five verbatim copies (spec §1 RC5). Fresh-scope-per-call is the point: a
/// long-lived <see cref="IAppSettingsService"/> can hold a stale cached AppSettings entity
/// loaded before the key was saved, so the provider must resolve a NEW scoped instance
/// rather than reuse one it was constructed with.
/// </summary>
public class CivitaiApiKeyProviderTests
{
    private static IServiceScopeFactory BuildScopeFactory(IAppSettingsService scopedSettings)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => scopedSettings);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task GetApiKeyAsync_WithScopeFactory_ReturnsScopedServiceKey()
    {
        var scopedSettings = new Mock<IAppSettingsService>();
        scopedSettings.Setup(s => s.GetCivitaiApiKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("key-from-scope");
        var scopeFactory = BuildScopeFactory(scopedSettings.Object);

        var sut = new CivitaiApiKeyProvider(scopeFactory);

        var result = await sut.GetApiKeyAsync();

        result.Should().Be("key-from-scope");
    }

    [Fact]
    public async Task GetApiKeyAsync_NullFactory_FallsBackToInjectedSettings()
    {
        var fallbackSettings = new Mock<IAppSettingsService>();
        fallbackSettings.Setup(s => s.GetCivitaiApiKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("key-from-fallback");

        var sut = new CivitaiApiKeyProvider(scopeFactory: null, fallbackSettings: fallbackSettings.Object);

        var result = await sut.GetApiKeyAsync();

        result.Should().Be("key-from-fallback");
    }

    [Fact]
    public async Task GetApiKeyAsync_NeitherFactoryNorFallback_ReturnsNullWithoutThrowing()
    {
        var sut = new CivitaiApiKeyProvider(scopeFactory: null, fallbackSettings: null);

        var result = await sut.GetApiKeyAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetApiKeyAsync_ScopeFactoryPresent_PrefersScopeOverFallback()
    {
        var scopedSettings = new Mock<IAppSettingsService>();
        scopedSettings.Setup(s => s.GetCivitaiApiKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("key-from-scope");
        var scopeFactory = BuildScopeFactory(scopedSettings.Object);

        var fallbackSettings = new Mock<IAppSettingsService>();
        fallbackSettings.Setup(s => s.GetCivitaiApiKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("key-from-fallback");

        var sut = new CivitaiApiKeyProvider(scopeFactory, fallbackSettings.Object);

        var result = await sut.GetApiKeyAsync();

        result.Should().Be("key-from-scope");
    }
}
