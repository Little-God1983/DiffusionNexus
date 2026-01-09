using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for comparing current data with a backup before restoring.
/// </summary>
public partial class BackupCompareDialog : Window
{
    private BackupCompareData? _currentStats;
    private BackupCompareData? _backupStats;

    public BackupCompareDialog()
    {
        InitializeComponent();
        // DataContext is set in WithData() after data is available
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Whether the user chose to restore the backup.
    /// </summary>
    public bool ShouldRestore { get; private set; }

    /// <summary>
    /// Configures the dialog with comparison data.
    /// </summary>
    public BackupCompareDialog WithData(BackupCompareData currentStats, BackupCompareData backupStats)
    {
        _currentStats = currentStats;
        _backupStats = backupStats;
        
        // Set DataContext after data is available so bindings work
        DataContext = this;
        
        return this;
    }

    // Properties for binding
    public string CurrentLabel => _currentStats?.Label ?? "Current";
    public string BackupLabel => _backupStats?.Label ?? "Backup";
    
    public string CurrentDateText => _currentStats?.Date.ToString("yyyy-MM-dd HH:mm") ?? "N/A";
    public string BackupDateText => _backupStats?.Date.ToString("yyyy-MM-dd HH:mm") ?? "N/A";
    
    public int CurrentDatasets => _currentStats?.DatasetCount ?? 0;
    public int BackupDatasets => _backupStats?.DatasetCount ?? 0;
    
    public int CurrentImages => _currentStats?.ImageCount ?? 0;
    public int BackupImages => _backupStats?.ImageCount ?? 0;
    
    public int CurrentVideos => _currentStats?.VideoCount ?? 0;
    public int BackupVideos => _backupStats?.VideoCount ?? 0;
    
    public int CurrentCaptions => _currentStats?.CaptionCount ?? 0;
    public int BackupCaptions => _backupStats?.CaptionCount ?? 0;

    public string CurrentSizeText => FormatSize(_currentStats?.TotalSizeBytes ?? 0);
    public string BackupSizeText => FormatSize(_backupStats?.TotalSizeBytes ?? 0);

    /// <summary>
    /// Formats bytes as a human-readable string (KB, MB, GB).
    /// </summary>
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    private void OnRestoreClick(object? sender, RoutedEventArgs e)
    {
        ShouldRestore = true;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        ShouldRestore = false;
        Close(false);
    }
}
