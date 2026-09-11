using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Controls;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Covers the "Send Selected To… → Workflows" submenu of <see cref="ImageActionsViewModel"/>: which
/// entries keep the submenu visible and that the Batch Metadata Distiller hand-off rides the shared
/// workflow navigation event.
/// </summary>
public sealed class ImageActionsWorkflowMenuTests
{
    private static ImageActionsViewModel MakeActions(Mock<IDatasetEventAggregator>? aggregator = null) =>
        new(Mock.Of<IDatasetState>(), (aggregator ?? new Mock<IDatasetEventAggregator>()).Object);

    [Fact]
    public void WorkflowsMenu_StaysVisible_WhenOnlyMetadataDistillerRemains()
    {
        var vm = MakeActions();
        vm.ShowSendToAnimeToReal = false;
        vm.ShowSendToImageEdit = false;

        vm.ShowWorkflowsMenu.Should().BeTrue("the distiller alone justifies the submenu");
        vm.ShowSendMenu.Should().BeTrue();

        vm.ShowSendToMetadataDistiller = false;

        vm.ShowWorkflowsMenu.Should().BeFalse("no workflow entries are left");
    }

    [Fact]
    public async Task SendToWorkflow_MetadataDistiller_PublishesWorkflowNavigationWithSelectedPaths()
    {
        var aggregator = new Mock<IDatasetEventAggregator>();
        NavigateToWorkflowEventArgs? published = null;
        aggregator
            .Setup(a => a.PublishNavigateToWorkflow(It.IsAny<NavigateToWorkflowEventArgs>()))
            .Callback<NavigateToWorkflowEventArgs>(e => published = e);

        var vm = MakeActions(aggregator);
        var paths = new[] { @"C:\gallery\a.png", @"C:\gallery\b.png" };
        vm.PathProvider = () => Task.FromResult(new ImageActionPaths(paths));
        vm.CanAct = true;

        await vm.SendToWorkflowCommand.ExecuteAsync("batch-metadata-distiller");

        published.Should().NotBeNull();
        published!.WorkflowId.Should().Be("batch-metadata-distiller");
        published.ImagePaths.Should().Equal(paths);
    }
}
