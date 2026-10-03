using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using DiffusionNexus.UI.Views.Controls;
using FluentAssertions;

namespace DiffusionNexus.IntegrationTests;

/// <summary>
/// <see cref="LayerStackPanel"/> owns the rule that a layer list keeps its selected layer (#594): a
/// ListBox drops its selection on a Ctrl+click of the selected row, when its items are cleared and while
/// its host is away, and none of those may reach the host as a null. Bound to the real canvas and editor
/// view models, the way both screens bind it.
/// </summary>
/// <remarks>
/// Deliberately no <c>TestAppHost</c>: the panel needs no services. The headless session is themeless, so
/// the ListBox realizes no rows; selection runs on its items source regardless, which is what these
/// tests drive. A Ctrl+click is reproduced by its effect, the ListBox setting its own selection to null.
/// </remarks>
public class LayerStackPanelSelectionTests
{
    private static (Window Window, LayerStackPanel Panel, ListBox List) Host(object dataContext, string items, string selected)
    {
        var panel = new LayerStackPanel();
        panel.Bind(LayerStackPanel.ItemsProperty, new Binding(items));
        panel.Bind(LayerStackPanel.SelectedItemProperty, new Binding(selected, BindingMode.TwoWay));
        var window = new Window { Width = 400, Height = 600, DataContext = dataContext, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, panel, panel.FindControl<ListBox>("LayerList")!);
    }

    private static (ObservableCollection<GenerationFrameViewModel> Frames, CanvasLayerStackViewModel Stack, GenerationFrameViewModel A, GenerationFrameViewModel B) Canvas()
    {
        var frames = new ObservableCollection<GenerationFrameViewModel>();
        var stack = new CanvasLayerStackViewModel(frames);
        var a = new GenerationFrameViewModel { Name = "a", Width = 64, Height = 64 };
        var b = new GenerationFrameViewModel { Name = "b", Width = 64, Height = 64 };
        frames.Add(a);
        frames.Add(b);
        stack.SelectedLayer = b;
        return (frames, stack, a, b);
    }

    [AvaloniaFact]
    public void TheHostsSelectionSelectsTheRow()
    {
        var (_, stack, _, b) = Canvas();
        var (window, _, list) = Host(stack, nameof(stack.DisplayLayers), nameof(stack.SelectedLayer));
        try
        {
            list.SelectedItem.Should().BeSameAs(b);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PickingARowSelectsItInTheHost()
    {
        var (_, stack, a, _) = Canvas();
        var (window, _, list) = Host(stack, nameof(stack.DisplayLayers), nameof(stack.SelectedLayer));
        try
        {
            list.SelectedItem = a;
            Dispatcher.UIThread.RunJobs();   // the pick reaches the host on the next turn

            stack.SelectedLayer.Should().BeSameAs(a);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ACtrlClickOnTheSelectedRowKeepsTheLayerAndPutsTheRowBack()
    {
        var (_, stack, _, b) = Canvas();
        var (window, _, list) = Host(stack, nameof(stack.DisplayLayers), nameof(stack.SelectedLayer));
        try
        {
            list.SelectedItem = null;   // what a Ctrl+click on the selected row does

            stack.SelectedLayer.Should().BeSameAs(b, "deselecting a row is not choosing no layer");
            Dispatcher.UIThread.RunJobs();
            list.SelectedItem.Should().BeSameAs(b, "the row is highlighted again");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DeletingTheSelectedLayerSelectsTheOneThatTookItsPlace()
    {
        var (_, stack, a, b) = Canvas();
        var (window, _, list) = Host(stack, nameof(stack.DisplayLayers), nameof(stack.SelectedLayer));
        try
        {
            stack.Delete(b).Should().BeTrue();
            Dispatcher.UIThread.RunJobs();

            stack.SelectedLayer.Should().BeSameAs(a);
            list.SelectedItem.Should().BeSameAs(a);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DeletingTheLastLayerLeavesNothingSelected()
    {
        var (frames, stack, a, b) = Canvas();
        var (window, _, list) = Host(stack, nameof(stack.DisplayLayers), nameof(stack.SelectedLayer));
        try
        {
            stack.Delete(b);
            stack.Delete(a);
            Dispatcher.UIThread.RunJobs();

            frames.Should().BeEmpty();
            stack.SelectedLayer.Should().BeNull();
            list.SelectedItem.Should().BeNull();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheHostGoingAwayAndComingBackKeepsAndReselectsTheLayer()
    {
        // A tab switch clears the view's DataContext: the panel's Items and SelectedItem go with it.
        var (_, stack, _, b) = Canvas();
        var (window, _, list) = Host(stack, nameof(stack.DisplayLayers), nameof(stack.SelectedLayer));
        try
        {
            window.DataContext = null;
            Dispatcher.UIThread.RunJobs();
            stack.SelectedLayer.Should().BeSameAs(b);

            window.DataContext = stack;
            Dispatcher.UIThread.RunJobs();

            stack.SelectedLayer.Should().BeSameAs(b);
            list.SelectedItem.Should().BeSameAs(b);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheEditorsSyncNeverClearsTheActiveLayer()
    {
        // The editor rebuilds its rows on every layer change by clearing the collection the ListBox shows.
        // A null reaching the panel's view model would null the core's active layer: strokes go nowhere.
        // The ListBox drops its selection on that clear without raising SelectionChanged, so only a binding
        // on its SelectedItem (the panel's old design) would carry the null out.
        var layers = new LayerStack(10, 10);
        layers.AddLayer("Bottom");
        var top = layers.AddLayer("Top");
        layers.AddLayer("Third");
        layers.ActiveLayer = top;
        var panelVm = new LayerPanelViewModel(hasImage: () => true);
        panelVm.SyncLayers(layers);
        var (window, _, list) = Host(panelVm, nameof(panelVm.Layers), nameof(panelVm.SelectedLayer));
        try
        {
            var raisedNull = false;
            panelVm.LayerSelectionChanged += (_, layer) => raisedNull |= layer is null;

            panelVm.SyncLayers(layers);
            Dispatcher.UIThread.RunJobs();

            raisedNull.Should().BeFalse("a sync is not the user clearing the selection");
            panelVm.SelectedLayer!.Layer.Should().BeSameAs(top);
            list.SelectedItem.Should().BeSameAs(panelVm.SelectedLayer);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AHostThatAnswersAPickWithAnotherRowGetsThatRowHighlighted()
    {
        // Picking a row while a Move/Transform is pending commits it, and the commit rebuilds the editor's
        // rows: the host answers the pick with a new row object. Rebuilding them inside the ListBox's own
        // selection change made Avalonia throw (swallowed by the binding, rows half rebuilt).
        var layers = new LayerStack(10, 10);
        var bottom = layers.AddLayer("Bottom");
        var top = layers.AddLayer("Top");
        layers.ActiveLayer = top;
        var panelVm = new LayerPanelViewModel(hasImage: () => true);
        panelVm.SyncLayers(layers);
        var syncing = false;
        panelVm.LayerSelectionChanged += (_, layer) =>
        {
            if (syncing || layer is null)
                return;
            syncing = true;
            layers.ActiveLayer = layer;
            panelVm.SyncLayers(layers);
            syncing = false;
        };
        var (window, panel, list) = Host(panelVm, nameof(panelVm.Layers), nameof(panelVm.SelectedLayer));
        try
        {
            list.SelectedItem = panelVm.Layers.Single(l => l.Layer == bottom);
            Dispatcher.UIThread.RunJobs();

            panelVm.Layers.Select(l => l.Name).Should().Equal("Top", "Bottom");
            panelVm.SelectedLayer!.Layer.Should().BeSameAs(bottom);
            panel.SelectedItem.Should().BeSameAs(panelVm.SelectedLayer);
            list.SelectedItem.Should().BeSameAs(panelVm.SelectedLayer, "the list shows the host's row");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SwappingTheItemsForAnotherListNeverSelectsARowFromTheOldOne()
    {
        var layers = new LayerStack(10, 10);
        layers.AddLayer("Only");
        var panelVm = new LayerPanelViewModel(hasImage: () => true);
        panelVm.SyncLayers(layers);
        var (window, _, list) = Host(panelVm, nameof(panelVm.Layers), nameof(panelVm.SelectedLayer));
        try
        {
            var stale = panelVm.SelectedLayer;
            panelVm.Layers = new ObservableCollection<LayerViewModel>();
            Dispatcher.UIThread.RunJobs();

            panelVm.SelectedLayer.Should().BeSameAs(stale, "the host has not chosen yet");
            list.SelectedItem.Should().BeNull("the old row is not in the new list");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ACtrlClickInTheEditorKeepsTheActiveLayer()
    {
        var layers = new LayerStack(10, 10);
        layers.AddLayer("Bottom");
        var top = layers.AddLayer("Top");
        var panelVm = new LayerPanelViewModel(hasImage: () => true);
        panelVm.SyncLayers(layers);
        var (window, _, list) = Host(panelVm, nameof(panelVm.Layers), nameof(panelVm.SelectedLayer));
        try
        {
            var raisedNull = false;
            panelVm.LayerSelectionChanged += (_, layer) => raisedNull |= layer is null;

            list.SelectedItem = null;
            Dispatcher.UIThread.RunJobs();

            raisedNull.Should().BeFalse();
            panelVm.SelectedLayer!.Layer.Should().BeSameAs(top);
            list.SelectedItem.Should().BeSameAs(panelVm.SelectedLayer);
        }
        finally
        {
            window.Close();
        }
    }
}
