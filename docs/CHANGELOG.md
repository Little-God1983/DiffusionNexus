# Changelog

## Unreleased
- **Major**: Upgraded to .NET 10.
- **Database**: Integrated SQLite Database with Entity Framework Core for metadata persistence and performance.
- **New Feature**: Dataset Management tab with version control, branching, and image captioning.
- **New Feature**: Batch Crop/Scale tool for training preparation.
- **New Feature**: Image Editor with AI background removal (RMBG-1.4) and AI upscaling (4x-UltraSharp).
- **New Feature**: "Notes" and "Epochs" sub-tabs in version details.
- **UI**: Added "Presentation" view for model showcasing.
- **Refactor**: Renamed UI project to DiffusionNexus.UI (in progress).
- Added busy overlay with progress and cancellation for LoRA sort.
- Consolidated metadata parsing helpers into internal ModelMetadataUtils.
- Automatic log expansion during processing.
- Added path validation, disk space check and progress reporting.
- New unit tests for helper methods.
