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

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isTrayOpen;

    /// <summary>
    /// Fixed height constants matching the Image Comparer tray pattern.
    /// </summary>
    public double TrayHeight => 300d;

    /// <summary>
    /// Height of the handle bar when collapsed.
    /// </summary>
    public double TrayHandleHeight => 40d;

    /// <summary>
    /// The current visible height — full when open, handle-only when collapsed.
    /// </summary>
    public double TrayVisibleHeight => IsTrayOpen ? TrayHeight : TrayHandleHeight;

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
    /// </summary>
    public void OpenTab(InstallerPackageCardViewModel card)
    {
        if (!Tabs.Contains(card))
            Tabs.Add(card);

        SelectedTab = card;
        IsTrayOpen = true;
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

    partial void OnIsTrayOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(TrayVisibleHeight));
    }

    partial void OnIsPinnedChanged(bool value)
    {
        if (value)
            IsTrayOpen = true;
    }
}
