using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.CivitaiBrowser;

/// <summary>The recipe view of a pasted Civitai image link (#628); see <c>CivitaiRecipeViewModel</c>.</summary>
public partial class CivitaiRecipeView : UserControl
{
    public CivitaiRecipeView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
