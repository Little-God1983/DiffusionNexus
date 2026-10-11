using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services;

/// <summary>#618: the catalog apply waits while any install runs; this says which one.</summary>
public sealed class InstallActivityTests
{
    [Fact]
    public void Running_names_the_newest_install_until_every_install_ended()
    {
        var activity = new InstallActivity();
        var changes = 0;
        activity.Changed += () => changes++;

        var engine = activity.Begin("Diffusion Nexus Engine");
        var workload = activity.Begin("Krea-2-Turbo");
        activity.Running.Should().Be("Krea-2-Turbo");

        workload.Dispose();
        activity.Running.Should().Be("Diffusion Nexus Engine");
        engine.Dispose();
        activity.Running.Should().BeNull();
        changes.Should().Be(4);
    }

    // PR #619 round 3: a throwing subscriber made Begin throw after the entry was added; nothing could end it.
    [Fact]
    public void A_throwing_subscriber_never_leaves_an_install_running()
    {
        var activity = new InstallActivity();
        activity.Changed += () => throw new InvalidOperationException("view gone");

        var handle = activity.Begin("Krea-2-Turbo");
        activity.Running.Should().Be("Krea-2-Turbo");
        handle.Dispose();

        activity.Running.Should().BeNull();
    }

    [Fact]
    public void Ending_an_install_twice_counts_once()
    {
        var activity = new InstallActivity();
        var a = activity.Begin("A");
        var b = activity.Begin("B");

        a.Dispose();
        a.Dispose();

        activity.Running.Should().Be("B");
        b.Dispose();
        activity.Running.Should().BeNull();
    }
}
