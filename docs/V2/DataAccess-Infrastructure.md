# DiffusionNexus DataAccess Infrastructure

File-based persistence implementations for core data access abstractions.

**Project**: `DiffusionNexus.DataAccess.Infrastructure`

## Overview

This project provides file-based persistence implementations. Two serializer adapters are included:

- `JsonSerializerAdapter` using `System.Text.Json`
- `XmlSerializerAdapter` using `System.Xml.Serialization`

## FileConfigStore

`FileConfigStore` persists configuration objects to disk using whichever serializer is registered through dependency injection.

### Usage

```csharp
// Register with JSON serialization
services.AddSingleton<ISerializer, JsonSerializerAdapter>();
services.AddSingleton<IConfigStore, FileConfigStore>();

// Or with XML serialization
services.AddSingleton<ISerializer, XmlSerializerAdapter>();
services.AddSingleton<IConfigStore, FileConfigStore>();
```

### Example

```csharp
public class SettingsService(IConfigStore configStore)
{
    public async Task<AppSettings> LoadSettingsAsync()
    {
        return await configStore.GetAsync<AppSettings>("app-settings") 
               ?? new AppSettings();
    }
    
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await configStore.SetAsync("app-settings", settings);
    }
}
```

## Serializer Adapters

### JsonSerializerAdapter

Uses `System.Text.Json` for JSON serialization.

```csharp
public class JsonSerializerAdapter : ISerializer
{
    public string Serialize<T>(T value);
    public T? Deserialize<T>(string data);
}
```

### XmlSerializerAdapter

Uses `System.Xml.Serialization` for XML serialization.

```csharp
public class XmlSerializerAdapter : ISerializer
{
    public string Serialize<T>(T value);
    public T? Deserialize<T>(string data);
}
```

## File Locations

Configuration files are stored in:
- `%LOCALAPPDATA%/DiffusionNexus/Config/` (default)

Each configuration key maps to a file:
- `app-settings` ? `app-settings.json` (or `.xml`)
