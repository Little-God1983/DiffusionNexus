using DiffusionNexus.Civitai;
using DiffusionNexus.UI.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DiffusionNexus.Tests.Civitai;

/// <summary>
/// Guards the DI-level sharing invariant <c>AddCivitaiGateway</c> exists to establish: one pacer,
/// one cooldown and one cache behind both lanes of <see cref="ICivitaiClient"/>. Before this test,
/// nothing in the suite would fail if a future edit re-registered <see cref="ICivitaiClient"/>, or
/// added a second <see cref="CivitaiResponseCache"/> — the app would still build and every other
/// test would still pass while the gateway quietly paced, cooled down and cached nothing.
/// </summary>
public sealed class CivitaiGatewayServiceCollectionExtensionsTests
{
    private static ServiceProvider BuildProvider()
        => new ServiceCollection().AddCivitaiGateway().BuildServiceProvider();

    [Fact]
    public void DefaultAndBackgroundClients_AreDifferentGatewayInstances()
    {
        using var provider = BuildProvider();

        var interactive = provider.GetRequiredService<ICivitaiClient>();
        var background = provider.GetRequiredKeyedService<ICivitaiClient>("background");

        interactive.Should().BeOfType<CivitaiApiGateway>();
        background.Should().BeOfType<CivitaiApiGateway>();
        interactive.Should().NotBeSameAs(background, "the two lanes must be separate gateway " +
            "instances so each can carry its own spacing interval — they differ in lane, not in " +
            "the collaborators backing them");
    }

    [Fact]
    public void ICivitaiApiCache_ResolvesToTheSameObjectAsTheConcreteCache()
    {
        using var provider = BuildProvider();

        var concrete = provider.GetRequiredService<CivitaiResponseCache>();
        var viaInterface = provider.GetRequiredService<ICivitaiApiCache>();

        viaInterface.Should().BeSameAs(concrete, "ICivitaiApiCache is a factory over the one " +
            "CivitaiResponseCache singleton, not a second cache — NoteApiKey (concrete-only) and " +
            "anything resolving the interface must see the same store");
    }

    /// <summary>
    /// <see cref="CivitaiApiGateway"/> holds its pacer, cooldown and cache in private fields, so the
    /// two gateway instances' collaborators cannot be compared directly without adding accessors to
    /// production code purely for this test. What is asserted instead: the three collaborator types
    /// are registered as singletons, so both gateway factories — which resolve them by
    /// <c>sp.GetRequiredService</c> against the same provider — necessarily receive the same
    /// instances. Resolving each twice and asserting reference equality is the reachable proxy for
    /// "both lanes share one pacer, one cooldown, one cache".
    /// </summary>
    [Fact]
    public void SharedCollaborators_AreSingletonsWithinTheProvider()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<CivitaiRateLimitCooldown>()
            .Should().BeSameAs(provider.GetRequiredService<CivitaiRateLimitCooldown>(),
                "one cooldown is what makes a single 429 pause every surface");

        provider.GetRequiredService<CivitaiResponseCache>()
            .Should().BeSameAs(provider.GetRequiredService<CivitaiResponseCache>(),
                "one cache is what stops the same model page being fetched twice");

        provider.GetRequiredService<ICivitaiRequestPacer>()
            .Should().BeSameAs(provider.GetRequiredService<ICivitaiRequestPacer>(),
                "one pacer timestamp is what makes background work space itself behind interactive work");

        // Not required by the invariant (the gateway wraps the raw client, not the other way
        // round), but the raw CivitaiClient is also registered as a singleton, so both lanes end
        // up wrapping the same inner client too.
        provider.GetRequiredService<CivitaiClient>()
            .Should().BeSameAs(provider.GetRequiredService<CivitaiClient>());
    }
}
