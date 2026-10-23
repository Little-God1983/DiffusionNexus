using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Infrastructure;

/// <summary>
/// Covers <see cref="LibraryChangeNotifier"/> — the cross-module "the library gained a model"
/// signal raised by the one download path (spec RC5). The Browse queue never notified the
/// Installed tab, so a queued download stayed invisible until a manual refresh; every download
/// path now goes through this notifier instead of a per-view ad-hoc event.
/// </summary>
public sealed class LibraryChangeNotifierTests
{
    [Fact]
    public void NotifyModelDownloaded_RaisesEventWithModelId()
    {
        var notifier = new LibraryChangeNotifier();
        ModelDownloadedEventArgs? received = null;
        object? sender = null;
        notifier.ModelDownloaded += (s, e) => { sender = s; received = e; };

        notifier.NotifyModelDownloaded(5);

        received.Should().NotBeNull();
        received!.ModelId.Should().Be(5);
        sender.Should().BeSameAs(notifier);
    }

    [Fact]
    public void NotifyModelDownloaded_WithNoSubscriber_DoesNotThrow()
    {
        var notifier = new LibraryChangeNotifier();

        var act = () => notifier.NotifyModelDownloaded(7);

        act.Should().NotThrow();
    }

    [Fact]
    public void NotifyModelDownloaded_RaisesForEverySubscriber()
    {
        var notifier = new LibraryChangeNotifier();
        var first = 0;
        var second = 0;
        notifier.ModelDownloaded += (_, e) => first = e.ModelId;
        notifier.ModelDownloaded += (_, e) => second = e.ModelId;

        notifier.NotifyModelDownloaded(11);

        first.Should().Be(11);
        second.Should().Be(11);
    }

    [Fact]
    public void NotifyModelDownloaded_AfterUnsubscribe_DoesNotRaise()
    {
        var notifier = new LibraryChangeNotifier();
        var calls = 0;
        void Handler(object? sender, ModelDownloadedEventArgs e) => calls++;
        notifier.ModelDownloaded += Handler;
        notifier.ModelDownloaded -= Handler;

        notifier.NotifyModelDownloaded(3);

        calls.Should().Be(0);
    }
}
