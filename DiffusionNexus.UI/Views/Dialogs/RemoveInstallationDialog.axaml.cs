using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Confirmation dialog for removing an installation from the Installer Manager,
/// with checkboxes (checked by default) to also remove the settings folders linked
/// to it. Checkboxes for which no folder exists are disabled and unchecked.
/// Files on disk are never touched.
/// </summary>
public partial class RemoveInstallationDialog : Window
{
    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<RemoveInstallationDialog, string>(nameof(Message), defaultValue: string.Empty);

    public static readonly StyledProperty<string> GalleryLabelProperty =
        AvaloniaProperty.Register<RemoveInstallationDialog, string>(nameof(GalleryLabel), defaultValue: string.Empty);

    public static readonly StyledProperty<string> LoraSourceLabelProperty =
        AvaloniaProperty.Register<RemoveInstallationDialog, string>(nameof(LoraSourceLabel), defaultValue: string.Empty);

    public static readonly StyledProperty<string> BaseModelFolderLabelProperty =
        AvaloniaProperty.Register<RemoveInstallationDialog, string>(nameof(BaseModelFolderLabel), defaultValue: string.Empty);

    public static readonly StyledProperty<bool> HasGalleriesProperty =
        AvaloniaProperty.Register<RemoveInstallationDialog, bool>(nameof(HasGalleries));

    public static readonly StyledProperty<bool> HasLoraSourcesProperty =
        AvaloniaProperty.Register<RemoveInstallationDialog, bool>(nameof(HasLoraSources));

    public static readonly StyledProperty<bool> HasBaseModelFoldersProperty =
        AvaloniaProperty.Register<RemoveInstallationDialog, bool>(nameof(HasBaseModelFolders));

    public static readonly StyledProperty<bool> RemoveGalleriesProperty =
        AvaloniaProperty.Register<RemoveInstallationDialog, bool>(nameof(RemoveGalleries));

    public static readonly StyledProperty<bool> RemoveLoraSourcesProperty =
        AvaloniaProperty.Register<RemoveInstallationDialog, bool>(nameof(RemoveLoraSources));

    public static readonly StyledProperty<bool> RemoveBaseModelFoldersProperty =
        AvaloniaProperty.Register<RemoveInstallationDialog, bool>(nameof(RemoveBaseModelFolders));

    public RemoveInstallationDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public string Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string GalleryLabel
    {
        get => GetValue(GalleryLabelProperty);
        set => SetValue(GalleryLabelProperty, value);
    }

    public string LoraSourceLabel
    {
        get => GetValue(LoraSourceLabelProperty);
        set => SetValue(LoraSourceLabelProperty, value);
    }

    public string BaseModelFolderLabel
    {
        get => GetValue(BaseModelFolderLabelProperty);
        set => SetValue(BaseModelFolderLabelProperty, value);
    }

    public bool HasGalleries
    {
        get => GetValue(HasGalleriesProperty);
        set => SetValue(HasGalleriesProperty, value);
    }

    public bool HasLoraSources
    {
        get => GetValue(HasLoraSourcesProperty);
        set => SetValue(HasLoraSourcesProperty, value);
    }

    public bool HasBaseModelFolders
    {
        get => GetValue(HasBaseModelFoldersProperty);
        set => SetValue(HasBaseModelFoldersProperty, value);
    }

    public bool RemoveGalleries
    {
        get => GetValue(RemoveGalleriesProperty);
        set => SetValue(RemoveGalleriesProperty, value);
    }

    public bool RemoveLoraSources
    {
        get => GetValue(RemoveLoraSourcesProperty);
        set => SetValue(RemoveLoraSourcesProperty, value);
    }

    public bool RemoveBaseModelFolders
    {
        get => GetValue(RemoveBaseModelFoldersProperty);
        set => SetValue(RemoveBaseModelFoldersProperty, value);
    }

    /// <summary>The user's choice; <see cref="RemoveInstallationResult.Cancelled"/> until Yes is clicked.</summary>
    public RemoveInstallationResult Result { get; private set; } = RemoveInstallationResult.Cancelled();

    /// <summary>Fills the dialog from the prompt. Checkboxes default to checked where folders exist.</summary>
    public void Configure(RemoveInstallationPrompt prompt)
    {
        Message = $"Remove \"{prompt.InstallationName}\" from the Installer Manager?\n\nThis will NOT delete any files on disk.";

        GalleryLabel = ComposeLabel("Gallery", prompt.GalleryFolders);
        LoraSourceLabel = ComposeLabel("LoRA Source", prompt.LoraSourceFolders);
        BaseModelFolderLabel = ComposeLabel("Base Model Folder", prompt.BaseModelFolders);

        HasGalleries = prompt.GalleryFolders.Count > 0;
        HasLoraSources = prompt.LoraSourceFolders.Count > 0;
        HasBaseModelFolders = prompt.BaseModelFolders.Count > 0;

        RemoveGalleries = HasGalleries;
        RemoveLoraSources = HasLoraSources;
        RemoveBaseModelFolders = HasBaseModelFolders;
    }

    private static string ComposeLabel(string what, System.Collections.Generic.IReadOnlyList<string> folders) =>
        folders.Count == 0
            ? $"{what} — none linked"
            : $"{what}\n{string.Join("\n", folders)}";

    private void OnYesClick(object? sender, RoutedEventArgs e)
    {
        Result = new RemoveInstallationResult(
            Confirmed: true,
            RemoveGalleries: HasGalleries && RemoveGalleries,
            RemoveLoraSources: HasLoraSources && RemoveLoraSources,
            RemoveBaseModelFolders: HasBaseModelFolders && RemoveBaseModelFolders);
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = RemoveInstallationResult.Cancelled();
        Close(false);
    }
}
