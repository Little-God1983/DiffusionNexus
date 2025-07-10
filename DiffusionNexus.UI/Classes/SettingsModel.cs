using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace DiffusionNexus.UI.Classes
{
    [ObservableObject]
    public partial class SettingsModel
    {

        [ObservableProperty] private string? _encryptedCivitaiApiKey;
        [ObservableProperty] private string? _loraSortSourcePath;
        [ObservableProperty] private string? _loraSortTargetPath;
        [ObservableProperty] private string? _loraHelperFolderPath;
        [ObservableProperty] private bool _deleteEmptySourceFolders;
        [ObservableProperty] private bool _generateVideoThumbnails = true;
        [ObservableProperty] private bool _showNsfw;
        [ObservableProperty] private bool _useForgeStylePrompts = true;

        [JsonIgnore]
        public string? CivitaiApiKey { get; set; }
    }
}
