using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class CanvasBackendSelectionTests
{
    [Fact]
    public void Canvas_OffersBothBackendsAndDefaultsToTheLocalOne()
    {
        var vm = new DiffusionCanvasViewModel();

        vm.AvailableBackends.Select(b => b.Key)
            .Should().BeEquivalentTo([CanvasBackendKeys.Local, CanvasBackendKeys.Engine]);
        vm.SelectedBackend!.Key.Should().Be(CanvasBackendKeys.Local,
            "the engine is opt-in until it can generate");
    }

    [Fact]
    public void Canvas_KeepsTheSelectedBackend()
    {
        var vm = new DiffusionCanvasViewModel();

        vm.SelectedBackend = vm.AvailableBackends.Single(b => b.Key == CanvasBackendKeys.Engine);

        vm.SelectedBackend.Key.Should().Be(CanvasBackendKeys.Engine);
    }
}
