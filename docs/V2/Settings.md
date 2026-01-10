# DiffusionNexus Settings System

Application settings management for DiffusionNexus V2.

**Projects involved:**
- `DiffusionNexus.Domain` - Entities and interfaces
- `DiffusionNexus.Infrastructure` - Secure storage implementation
- `DiffusionNexus.Service` - Settings service implementation
- `DiffusionNexus.DataAccess` - Database persistence
- `DiffusionNexus.UI-V2` - Settings UI

## Architecture

```
???????????????????????????????????????????????????????????????????
?                    SettingsView (UI-V2)                         ?
?                   SettingsViewModel                             ?
???????????????????????????????????????????????????????????????????
?                    IAppSettingsService                          ?
?                   AppSettingsService                            ?
???????????????????????????????????????????????????????????????????
?   ISecureStorage         ?   DiffusionNexusCoreDbContext        ?
?   SecureStorageService   ?   (SQLite)                           ?
???????????????????????????????????????????????????????????????????
?                    AppSettings Entity                           ?
?                    LoraSource Entity                            ?
???????????????????????????????????????????????????????????????????
```

## Entities

### AppSettings

Singleton entity (Id = 1) storing all application settings.

```csharp
public class AppSettings
{
    public int Id { get; set; } = 1;  // Always 1 (singleton)
    
    // API Keys (encrypted)
    public string? EncryptedCivitaiApiKey { get; set; }
    
    // LoRA Helper settings
    public ICollection<LoraSource> LoraSources { get; set; }
    public bool ShowNsfw { get; set; }
    public bool GenerateVideoThumbnails { get; set; } = true;
    public bool ShowVideoPreview { get; set; }
    public bool UseForgeStylePrompts { get; set; } = true;
    public bool MergeLoraSources { get; set; }
    
    // LoRA Sort settings
    public string? LoraSortSourcePath { get; set; }
    public string? LoraSortTargetPath { get; set; }
    public bool DeleteEmptySourceFolders { get; set; }
    
    // Metadata
    public DateTimeOffset UpdatedAt { get; set; }
}
```

### LoraSource

Represents a LoRA model source folder.

```csharp
public class LoraSource
{
    public int Id { get; set; }
    public int AppSettingsId { get; set; }
    public string FolderPath { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int Order { get; set; }
    
    public AppSettings? AppSettings { get; set; }
}
```

## Services

### ISecureStorage

Interface for encrypting/decrypting sensitive data.

```csharp
public interface ISecureStorage
{
    string? Encrypt(string? plainText);
    string? Decrypt(string? cipherText);
}
```

**Implementation:** `SecureStorageService` in `DiffusionNexus.Infrastructure`

- **Windows:** Uses DPAPI (`ProtectedData.Protect/Unprotect`)
- **Linux/macOS:** Uses AES-256 with PBKDF2 key derivation

### IAppSettingsService

Interface for managing application settings.

```csharp
public interface IAppSettingsService
{
    Task<AppSettings> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);
    
    // Convenience methods
    Task<string?> GetCivitaiApiKeyAsync(CancellationToken ct = default);
    Task SetCivitaiApiKeyAsync(string? apiKey, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetEnabledLoraSourcesAsync(CancellationToken ct = default);
    
    // LoRA source management
    Task<LoraSource> AddLoraSourceAsync(string folderPath, CancellationToken ct = default);
    Task RemoveLoraSourceAsync(int sourceId, CancellationToken ct = default);
    Task UpdateLoraSourceAsync(LoraSource source, CancellationToken ct = default);
}
```

## Database Schema

### AppSettings Table

| Column | Type | Description |
|--------|------|-------------|
| Id | INTEGER | Always 1 (singleton) |
| EncryptedCivitaiApiKey | TEXT(2000) | Encrypted API key |
| ShowNsfw | INTEGER | Boolean |
| GenerateVideoThumbnails | INTEGER | Boolean (default: true) |
| ShowVideoPreview | INTEGER | Boolean |
| UseForgeStylePrompts | INTEGER | Boolean (default: true) |
| MergeLoraSources | INTEGER | Boolean |
| LoraSortSourcePath | TEXT(1000) | Default source path |
| LoraSortTargetPath | TEXT(1000) | Default target path |
| DeleteEmptySourceFolders | INTEGER | Boolean |
| UpdatedAt | TEXT | Last modified timestamp |

### LoraSources Table

| Column | Type | Description |
|--------|------|-------------|
| Id | INTEGER | Primary key |
| AppSettingsId | INTEGER | Foreign key to AppSettings |
| FolderPath | TEXT(1000) | Folder path (required) |
| IsEnabled | INTEGER | Boolean |
| Order | INTEGER | Display order |

## UI Components

### SettingsViewModel

Located in `DiffusionNexus.UI-V2/ViewModels/SettingsViewModel.cs`

**Observable Properties:**
- `CivitaiApiKey` - Decrypted API key (memory only)
- `ShowNsfw`, `GenerateVideoThumbnails`, etc. - Boolean settings
- `LoraSources` - Collection of `LoraSourceViewModel`
- `HasChanges` - Dirty flag
- `StatusMessage` - User feedback

**Commands:**
- `LoadCommand` - Load settings from database
- `SaveCommand` - Save settings to database
- `DeleteApiKeyCommand` - Clear the API key
- `AddLoraSourceCommand` - Add new source folder
- `RemoveLoraSourceCommand` - Remove source folder
- `BrowseLoraSourceCommand` - Open folder picker

### SettingsView

Located in `DiffusionNexus.UI-V2/Views/SettingsView.axaml`

**Layout:**
```
???????????????????????????????????????????
? Settings                                ?
???????????????????????????????????????????
? ? General                               ?
?   Civitai API Key: [•••••••••] [Clear]  ?
???????????????????????????????????????????
? ? LoRA Helper                           ?
?   Source Folders                        ?
?   [+ Add Source Folder]                 ?
?   ???????????????????????????????????   ?
?   ? ? [C:\Models\LoRA] [Browse] [?] ?   ?
?   ???????????????????????????????????   ?
?   ?????????????????????????????????     ?
?   Display Options                       ?
?   ? Show NSFW content by default        ?
?   ? Automatic thumbnail generation      ?
?   ? Show video preview                  ?
?   ? A1111/Forge style prompts           ?
?   ? Merge LoRA sources by base model    ?
???????????????????????????????????????????
? ? LoRA Sort                             ?
???????????????????????????????????????????
?            [Unsaved changes] [Save]     ?
???????????????????????????????????????????
```

## Dependency Injection Setup

In `App.axaml.cs`:

```csharp
private static void ConfigureServices(IServiceCollection services)
{
    // Database
    services.AddDiffusionNexusCoreDatabase();

    // Infrastructure (secure storage, image caching)
    services.AddInfrastructureServices();

    // Application services
    services.AddScoped<IAppSettingsService, AppSettingsService>();

    // ViewModels
    services.AddTransient<SettingsViewModel>();
}
```

## Usage Examples

### Getting Settings in a Service

```csharp
public class MyService
{
    private readonly IAppSettingsService _settings;
    
    public MyService(IAppSettingsService settings)
    {
        _settings = settings;
    }
    
    public async Task DoWorkAsync()
    {
        // Get decrypted API key
        var apiKey = await _settings.GetCivitaiApiKeyAsync();
        
        // Get enabled LoRA folders
        var folders = await _settings.GetEnabledLoraSourcesAsync();
        
        // Get full settings
        var settings = await _settings.GetSettingsAsync();
        if (settings.ShowNsfw)
        {
            // Include NSFW content
        }
    }
}
```

### Modifying Settings

```csharp
public async Task UpdateSettingsAsync()
{
    var settings = await _settings.GetSettingsAsync();
    
    settings.ShowNsfw = true;
    settings.GenerateVideoThumbnails = false;
    
    await _settings.SaveSettingsAsync(settings);
}
```

### Adding a LoRA Source

```csharp
// Using convenience method
var source = await _settings.AddLoraSourceAsync(@"D:\Models\LoRA");

// Or manually
var settings = await _settings.GetSettingsAsync();
settings.LoraSources.Add(new LoraSource
{
    FolderPath = @"D:\Models\LoRA",
    IsEnabled = true
});
await _settings.SaveSettingsAsync(settings);
```

## Security

### API Key Encryption

The Civitai API key is encrypted before storage:

1. **Windows:** Uses Windows Data Protection API (DPAPI)
   - Encrypted with current user's credentials
   - Cannot be decrypted by other users or on other machines

2. **Linux/macOS:** Uses AES-256 encryption
   - Key derived from username + domain + process path
   - Salt and IV stored with ciphertext
   - PBKDF2 with 100,000 iterations

```csharp
// Encryption flow
var encrypted = _secureStorage.Encrypt(apiKey);
settings.EncryptedCivitaiApiKey = encrypted;

// Decryption flow
var apiKey = _secureStorage.Decrypt(settings.EncryptedCivitaiApiKey);
```

## Migration

To add the AppSettings tables to an existing database:

```bash
cd DiffusionNexus.DataAccess
dotnet ef migrations add AddAppSettings --context DiffusionNexusCoreDbContext --output-dir Migrations/Core
dotnet ef database update --context DiffusionNexusCoreDbContext
```

## File Locations

| Component | Path |
|-----------|------|
| AppSettings entity | `DiffusionNexus.Domain/Entities/AppSettings.cs` |
| LoraSource entity | `DiffusionNexus.Domain/Entities/LoraSource.cs` |
| ISecureStorage | `DiffusionNexus.Domain/Services/ISecureStorage.cs` |
| IAppSettingsService | `DiffusionNexus.Domain/Services/IAppSettingsService.cs` |
| SecureStorageService | `DiffusionNexus.Infrastructure/Services/SecureStorageService.cs` |
| AppSettingsService | `DiffusionNexus.Service/Services/AppSettingsService.cs` |
| SettingsViewModel | `DiffusionNexus.UI-V2/ViewModels/SettingsViewModel.cs` |
| SettingsView | `DiffusionNexus.UI-V2/Views/SettingsView.axaml` |
| DbContext config | `DiffusionNexus.DataAccess/Data/DiffusionNexusCoreDbContext.cs` |
