# DiffusionNexus V2 Documentation

This folder contains all documentation for the V2 refactored architecture.

## Documents

| Document | Description |
|----------|-------------|
| [Domain.md](Domain.md) | Domain entities, enums, and data model |
| [Database.md](Database.md) | SQLite database schema and EF Core configuration |
| [Settings.md](Settings.md) | Application settings system and secure storage |
| [LoraHelper.md](LoraHelper.md) | LoRA Helper module with tile grid and base model display |
| [Civitai-Client.md](Civitai-Client.md) | Typed HTTP client for Civitai REST API |
| [UI-Architecture.md](UI-Architecture.md) | Avalonia UI patterns, MVVM, ViewModels |
| [DataAccess.md](DataAccess.md) | Repository patterns and data access abstractions |
| [DataAccess-Infrastructure.md](DataAccess-Infrastructure.md) | File-based persistence implementations |

## Architecture Overview

```
????????????????????????????????????????????????????????????????????
?                        DiffusionNexus.UI-V2                       ?
?                      (Avalonia MVVM Shell)                        ?
????????????????????????????????????????????????????????????????????
?                      DiffusionNexus.Service                       ?
?                    (Business Logic & APIs)                        ?
????????????????????????????????????????????????????????????????????
? DiffusionNexus.       ?  DiffusionNexus.    ? DiffusionNexus.    ?
? Infrastructure        ?  DataAccess         ? Civitai            ?
? (Image Caching,       ?  (EF Core + SQLite) ? (API Client)       ?
?  Secure Storage)      ?                     ?                    ?
????????????????????????????????????????????????????????????????????
?                       DiffusionNexus.Domain                       ?
?                   (Entities, Enums, Interfaces)                   ?
????????????????????????????????????????????????????????????????????
```

## Key Concepts

### One Tile Per Model
- A **Model** represents a single resource (e.g., "Character LoRA")
- A Model can have multiple **Versions** (v1.0, v2.0, variants)
- Users select which Version from within the Model tile

### Data Sources
- **LocalFile**: Discovered by scanning local .safetensors files
- **CivitaiApi**: Fetched from Civitai API (by hash or ID)
- **Manual**: User-entered data

### Hybrid Image Storage
- **Thumbnails**: Stored as BLOBs in SQLite for instant tile display
- **Full Images**: Cached on disk for detail views
- **URLs**: Kept for re-downloading if needed

### Secure Settings
- **API Keys**: Encrypted using DPAPI (Windows) or AES-256 (cross-platform)
- **Settings**: Stored in SQLite database
- **LoRA Sources**: Multiple folders with enable/disable per source

## Projects

| Project | Description |
|---------|-------------|
| `DiffusionNexus.Domain` | Domain entities, enums, service interfaces |
| `DiffusionNexus.Civitai` | Typed Civitai API client |
| `DiffusionNexus.DataAccess` | EF Core DbContext, repositories |
| `DiffusionNexus.Infrastructure` | Image caching, secure storage services |
| `DiffusionNexus.Service` | Business logic services |
| `DiffusionNexus.UI-V2` | Avalonia desktop application |

## Getting Started

1. **Database Setup**
   ```bash
   cd DiffusionNexus.DataAccess
   dotnet ef database update --context DiffusionNexusCoreDbContext
   ```

2. **Run the Application**
   ```bash
   dotnet run --project DiffusionNexus.UI-V2
   ```

3. **Configure Settings**
   - Open the Settings view from the sidebar
   - Add your Civitai API key (encrypted storage)
   - Configure LoRA source folders
   - Save settings

## Target Framework

- .NET 9
- C# 13
- Entity Framework Core 9
- Avalonia 11.3.10
