using Avalonia.Controls;
using Avalonia.Layout;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.Classes
{
    public class DialogService : IDialogService
    {
        private readonly Window _owner;

        public DialogService(Window owner)
        {
            _owner = owner;
        }

        public async Task<bool?> ShowConfirmationAsync(string message, bool allowCancel = false)
        {
            var tcs = new TaskCompletionSource<bool?>();
            var dialog = new Window
            {
                Width = 300,
                Height = 150,
                Title = "Confirm",
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var yesButton = new Button { Content = "Yes", Width = 80 };
            var noButton = new Button { Content = "No", Width = 80 };
            Button? cancelButton = null;

            yesButton.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
            noButton.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 10
            };
            panel.Children.Add(yesButton);
            panel.Children.Add(noButton);

            if (allowCancel)
            {
                cancelButton = new Button { Content = "Cancel", Width = 80 };
                cancelButton.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
                panel.Children.Add(cancelButton);
            }

            dialog.Content = new StackPanel
            {
                Spacing = 10,
                Margin = new Thickness(10),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    panel
                }
            };

            await dialog.ShowDialog(_owner);
            return await tcs.Task;
        }

        public async Task<string?> ShowInputAsync(string message, string? defaultValue = null)
        {
            var tcs = new TaskCompletionSource<string?>();
            var dialog = new Window
            {
                Width = 300,
                Height = 170,
                Title = "Input",
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var textBox = new TextBox { Text = defaultValue ?? string.Empty };
            var okButton = new Button { Content = "OK", Width = 80 };
            var cancelButton = new Button { Content = "Cancel", Width = 80 };

            okButton.Click += (_, _) => { tcs.TrySetResult(textBox.Text); dialog.Close(); };
            cancelButton.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

            dialog.Content = new StackPanel
            {
                Spacing = 10,
                Margin = new Thickness(10),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    textBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Spacing = 10,
                        Children = { okButton, cancelButton }
                    }
                }
            };

            await dialog.ShowDialog(_owner);
            return await tcs.Task;
        }
    }
}
