using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;

namespace DiffusionNexus.UI.Views.Controls
{
    public partial class BlacklistProfileControl : UserControl
    {
        public BlacklistProfileControl()
        {
            InitializeComponent();

            this.FindControl<Button>("SaveButton")?.AddHandler(Button.ClickEvent, (_, e) => SaveClicked?.Invoke(this, e));
            this.FindControl<Button>("SaveAsButton")?.AddHandler(Button.ClickEvent, (_, e) => SaveAsClicked?.Invoke(this, e));
            this.FindControl<Button>("ApplyButton")?.AddHandler(Button.ClickEvent, (_, e) => ApplyListClicked?.Invoke(this, e));
            this.FindControl<Button>("SaveProfileButton")?.AddHandler(Button.ClickEvent, (_, e) => SaveProfileClicked?.Invoke(this, e));
            this.FindControl<Button>("DeleteProfileButton")?.AddHandler(Button.ClickEvent, (_, e) => DeleteProfileClicked?.Invoke(this, e));
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        public event EventHandler<RoutedEventArgs>? SaveClicked;
        public event EventHandler<RoutedEventArgs>? SaveAsClicked;
        public event EventHandler<RoutedEventArgs>? ApplyListClicked;
        public event EventHandler<RoutedEventArgs>? SaveProfileClicked;
        public event EventHandler<RoutedEventArgs>? DeleteProfileClicked;
    }
}