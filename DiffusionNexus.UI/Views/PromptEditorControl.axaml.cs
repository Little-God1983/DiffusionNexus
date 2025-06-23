using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Classes;
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace DiffusionNexus.UI.Views;

public partial class PromptEditorControl : UserControl
{

    public PromptEditorControl()
    {
        InitializeComponent();
    }
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}