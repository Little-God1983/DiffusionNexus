using System.Reflection;
using DiffusionNexus.Civitai;
using DiffusionNexus.UI.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DiffusionNexus.Tests.Civitai;

/// <summary>
/// Guards the DI-level sharing invariant <c>AddCivitaiGateway</c> exists to establish: one pacer,
/// one cooldown and one cache behind both lanes of <see cref="ICivitaiClient"/>. The real guard is
/// <see cref="GatewayInstances_ShareTheSamePacerCooldownAndCache"/>, which reflects into the two
/// constructed <see cref="CivitaiApiGateway"/> instances and compares their collaborators directly —
/// it is the one proven (see task-6-report.md, fix round 2) to fail if a lane factory is changed to
/// build its own private collaborator instead of resolving the shared one. The other tests here check
/// adjacent, real but narrower facts: that the two lanes are distinct instances, that the cache
/// interface and concrete type never diverge, and (as a cheap, admittedly non-dispositive sanity
/// check) that the collaborator types keep singleton lifetime in the container.
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
    /// NOT a wiring test. This only proves that <c>AddSingleton</c> does what <c>AddSingleton</c>
    /// always does — resolving the same type twice from the same container yields the same
    /// instance. It passes even if a gateway factory is changed to build its own private
    /// <c>new CivitaiRequestPacer()</c> instead of resolving the shared one, because it never looks
    /// inside either gateway. <see cref="GatewayInstances_ShareTheSamePacerCooldownAndCache"/> below
    /// is the test that actually catches that mutation; keep this one only as a cheap sanity check
    /// that the container itself is configured with singleton lifetimes, not as evidence the
    /// gateways use them.
    /// </summary>
    [Fact]
    public void CollaboratorServices_HaveSingletonLifetimeWithinTheProvider_ContainerCheckOnly()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<CivitaiRateLimitCooldown>()
            .Should().BeSameAs(provider.GetRequiredService<CivitaiRateLimitCooldown>());

        provider.GetRequiredService<CivitaiResponseCache>()
            .Should().BeSameAs(provider.GetRequiredService<CivitaiResponseCache>());

        provider.GetRequiredService<ICivitaiRequestPacer>()
            .Should().BeSameAs(provider.GetRequiredService<ICivitaiRequestPacer>());

        provider.GetRequiredService<CivitaiClient>()
            .Should().BeSameAs(provider.GetRequiredService<CivitaiClient>());
    }

    /// <summary>
    /// The real guard for the load-bearing invariant: not "the collaborator types are singletons"
    /// (guaranteed by <c>AddSingleton</c> regardless of what the gateway factories do with them —
    /// see <see cref="CollaboratorServices_HaveSingletonLifetimeWithinTheProvider_ContainerCheckOnly"/>)
    /// but "the two constructed <see cref="CivitaiApiGateway"/> instances actually hold the same
    /// pacer, cooldown and cache object". <see cref="CivitaiApiGateway"/> has no public accessors for
    /// its collaborators and none were added for this test — reflecting over the private fields is
    /// the only way to look inside both instances and compare, and is the instrument the review that
    /// commissioned this test explicitly sanctioned. Verified to actually fail the mutation it exists
    /// to catch — see the fix-round-2 section of task-6-report.md.
    /// </summary>
    [Fact]
    public void GatewayInstances_ShareTheSamePacerCooldownAndCache()
    {
        using var provider = BuildProvider();

        var interactive = (CivitaiApiGateway)provider.GetRequiredService<ICivitaiClient>();
        var background = (CivitaiApiGateway)provider.GetRequiredKeyedService<ICivitaiClient>("background");

        GetPrivateField(interactive, "_pacer").Should().BeSameAs(GetPrivateField(background, "_pacer"),
            "both lanes must share one pacer — the pacing timestamp is the process's single opinion " +
            "about when it last spoke to Civitai, and a lane with its own pacer would space nothing " +
            "against the other");

        GetPrivateField(interactive, "_cooldown").Should().BeSameAs(GetPrivateField(background, "_cooldown"),
            "both lanes must share one cooldown — a 429 drawn by either lane must pause both");

        GetPrivateField(interactive, "_cache").Should().BeSameAs(GetPrivateField(background, "_cache"),
            "both lanes must share one cache — otherwise the same model page is fetched twice, once " +
            "per lane");
    }

    /// <summary>
    /// Reads a private instance field off <see cref="CivitaiApiGateway"/> by name. Fails the test
    /// explicitly — rather than returning <c>null</c> and letting a <c>BeSameAs(null)</c> pass
    /// vacuously — if the field cannot be found, so a future rename breaks loudly instead of quietly
    /// disarming the assertion above.
    /// </summary>
    private static object GetPrivateField(CivitaiApiGateway gateway, string fieldName)
    {
        var field = typeof(CivitaiApiGateway).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field is null)
        {
            Assert.Fail($"CivitaiApiGateway no longer has a private field named '{fieldName}' — " +
                "update this test's field name (and check whether the collaborator it names was " +
                "renamed or restructured) instead of letting the assertion above pass on a null it " +
                "never actually compared.");
        }

        var value = field.GetValue(gateway);
        if (value is null)
        {
            Assert.Fail($"CivitaiApiGateway.{fieldName} was null on a constructed gateway instance.");
        }

        return value;
    }
}
