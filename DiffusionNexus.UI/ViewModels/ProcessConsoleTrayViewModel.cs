using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the bottom console tray in the Installer Manager.
/// Holds one tab per running process and supports pin/open behavior
/// matching the Image Comparer tray pattern.
/// </summary>
public partial class ProcessConsoleTrayViewModel : ViewModelBase
{
    /// <summary>
    /// The tabs representing running processes. Each tab references an
    /// <see cref="InstallerPackageCardViewModel"/> whose ConsoleLines drive the output.
    /// </summary>
    public ObservableCollection<InstallerPackageCardViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private InstallerPackageCardViewModel? _selectedTab;

    /// <summary>
    /// When pinned the tray stays open; when unpinned it collapses.
    /// Defaults to true when the first process starts.
    /// </summary>
    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isTrayOpen;

    /// <summary>
    /// True when at least one tab exists.
    /// </summary>
    public bool HasTabs => Tabs.Count > 0;

    public ProcessConsoleTrayViewModel()
    {
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTabs));

            // Auto-close tray when last tab is removed
            if (Tabs.Count == 0)
            {
                IsTrayOpen = false;
                IsPinned = false;
            }
        };
    }

    /// <summary>
    /// Adds a tab for the given card if not already present, and selects it.
    /// Pins the tray open by default so the user sees the console immediately.
    /// </summary>
    public void OpenTab(InstallerPackageCardViewModel card)
    {
        if (!Tabs.Contains(card))
            Tabs.Add(card);

        SelectedTab = card;
        IsTrayOpen = true;
        IsPinned = true;
    }

    /// <summary>
    /// Removes the tab for the given card.
    /// </summary>
    public void CloseTab(InstallerPackageCardViewModel card)
    {
        var index = Tabs.IndexOf(card);
        if (index < 0) return;

        Tabs.RemoveAt(index);

        if (SelectedTab == card)
            SelectedTab = Tabs.Count > 0 ? Tabs[Math.Min(index, Tabs.Count - 1)] : null;
    }

    [RelayCommand]
    private void CloseSelectedTab()
    {
        if (SelectedTab is not null)
            CloseTab(SelectedTab);
    }

    partial void OnIsPinnedChanged(bool value)
    {
        // When the user unpins, collapse the tray
        if (!value)
            IsTrayOpen = false;
        else
            IsTrayOpen = true;
    }
}
