# DiffusionNexus User Guide

## Overview
DiffusionNexus is a desktop tool for managing Stable Diffusion LoRA models and datasets. It automatically builds a searchable library of your models, fetches metadata from Civitai, generates thumbnail previews, and provides tools for dataset management and image processing. The application is built using Avalonia so it runs on Windows, macOS and Linux.

## Installation
1. [Install the .NET 10 Runtime](https://dotnet.microsoft.com/download) for your platform if you don't already have it.
2. Download the latest DiffusionNexus release from the GitHub **Releases** page and extract the archive.
3. Launch the `DiffusionNexus.UI` executable (or `DiffusionNexus.UI.dll` using `dotnet` on Linux/macOS).

## Getting Started
When the application starts you will see the main window with navigation tabs:
- **Dataset Management**: Manage training datasets, versions, and images.
- **Image Edit**: AI-powered tools for background removal, upscaling, and basic editing.
- **Captioning**: Edit captions for training images.
- **Batch Crop/Scale**: Prepare images for training buckets.
- **Lora Helper**: Organize and browse your LoRA collection.
- **Settings**: Configuration.

### Lora Helper Configuration
Before using Lora Helper, open the **Settings** tab:

1. **Civitai API Key** – optional but recommended. Allows downloading additional model metadata.
2. **Lora Helper Folder Path** – choose the directory containing your LoRA models. All subfolders will be scanned.
3. **Generate Video Thumbnails** – when enabled, the first frame of a GIF or MP4 will be used as the preview image if no static thumbnail is present.
4. **Show NSFW by default** – toggles visibility of NSFW models.
5. **A1111/Forge Style prompts** – copies prompts in the web‑UI format when using the copy buttons.

Click **Save Settings**. When you return to **Lora Helper** your models will load and thumbnails will be generated on first run.

## Browsing and Filtering
- Use the folder tree on the left to limit results to a specific directory.
- The search box supports instant filtering and autocomplete suggestions. Type part of a file name to filter the card list.
- Sort models by **Name** or **Date** using the radio buttons and choose ascending or descending order.
- Toggle **Show NSFW** to hide or show models marked as NSFW.

## Model Actions
Each model card contains buttons for common actions:

| Button | Action |
| ------ | ------ |
| 🌐 | Open the model page on Civitai (downloads metadata first if needed) |
| 📋 | Copy trained words or prompt snippet to clipboard |
| N | Copy just the model file name |
| 📂 | Open the folder containing the model |
| ❌ | Delete the model and all associated files |

Use the **Download Metadata** button at the top to fetch missing info for all listed models. The **Refresh** button reloads the library and regenerates thumbnails if settings changed.

## Duplicate Scanner
From within Lora Helper you can run **Scan Duplicates** to find `.safetensors` files with identical content. Choose the folder to scan and DiffusionNexus will compare hashes and show duplicates.

## Keyboard Shortcuts
- `Ctrl+F` focuses the search box.
- `Page Down` / `Page Up` load additional cards when many models are present.

## Troubleshooting
Logs appear in the lower panel. Errors while generating thumbnails or downloading metadata are shown here. If something fails, try refreshing or check that your Civitai API key is valid.

## Uninstalling
Simply delete the application folder. Configuration is stored in `%APPDATA%/DiffusionNexus` on Windows or `$HOME/.config/DiffusionNexus` on Linux/macOS.

