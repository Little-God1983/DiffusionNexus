using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Installer.SDK.Shared.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// One community link in the sidebar, inline or in the "More" flyout. Items are rebuilt
/// wholesale when the remote list lands, so no per-property change notification is required.
/// </summary>
public sealed class CommunityLinkItemViewModel
{
    public CommunityLinkItemViewModel(CommunityLink link, Action<CommunityLinkItemViewModel> onOpen)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(onOpen);

        Name = link.Name;
        Url = link.Url;
        Icon = link.Icon;
        OpenCommand = new RelayCommand(() => onOpen(this));
    }

    public string Name { get; }

    public string Url { get; }

    /// <summary>Glyph key from the links document; <c>CommunityLinkIcon</c> maps it, unknown keys included.</summary>
    public string? Icon { get; }

    public ICommand OpenCommand { get; }
}
