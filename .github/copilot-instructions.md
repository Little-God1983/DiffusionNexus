# Copilot Instructions

This is an Avalonia Project Written in C# / .net 10

In this project use the Nuget packages DiffusionNexus.Installer.SDK from its original source but for looking up code reference use the local copy that lives here: E:\Repos\DiffusionNexus.Installer.SDK

## General Guidelines
- Consult also the Claude.md for instructions if exists.
- When a bug is found, always check if a unit test can be created to cover/reproduce it before fixing. Ensure that the unit test effectively captures the bug scenario.
- Before any Database Entity classes, IEntityTypeConfigurations or the database migrations are modified execute the publish.ps1 script to make sure there is a last backup of the app with a working database.
- When A Keyboard Shortcut is Added make sure its documented in a file called DiffusionNexus.UI\\Doc\\Shortcuts.md
- When fixing a failing Unit test, check thoroughly if its the unit test that is in need of fixing or if its the code that has an actual bug.
- Every time new UI elements are added or UI is built from the ground up, always look first for reusable components in the project before creating new ones. The catalog of reusable controls, base classes, dialogs, converters and helpers is `DiffusionNexus.UI\REUSABLES.md` — check it before writing a new control, and add a row to it in the same commit whenever you create a new reusable piece.

## Database Management
- The app has ONE database and reads ONE JSON catalog. Do NOT mix them up:
  - **Diffusion_Nexus-core.db** — belongs to the DiffusionNexus application itself (managed by `DiffusionNexusCoreDbContext` in the `DiffusionNexus.DataAccess` project). Stored at `%LocalAppData%/DiffusionNexus/Data/`. Contains app-specific data (models, settings, installed packages, etc.).
  - **Installer SDK workload catalog** — JSON, not a database (Installer SDK 2.x, package `DiffusionNexus.Installer.SDK.Catalog`). Installed at `%LocalAppData%\DiffusionNexus\catalog\`, shared with the 3.x installer, read through `ICatalog`. The app follows the catalog channel the installer saved and never writes it. A stable seed is embedded in `DiffusionNexus.UI/Assets/Catalog` (refresh with `Scripts/Update-CatalogSeed.ps1`); a background check applies updates at startup. Point at a `DiffusionNexus.Catalog` checkout with the env var `DIFFUSIONNEXUS_CATALOG_PATH`.
- The legacy SDK 1.x database `%LocalAppData%\diffusion_nexus.db` (with its backups and `db_override.txt`) is no longer used by this app, but the Legacy 1.x installer still reads it: never delete it.

## Code Style
- Use specific formatting rules
- Follow naming conventions

## Project-Specific Rules
- The ComfyUI Qwen3\_VQA custom node stores its models in ComfyUI's models/prompt\_generator folder, not in the HuggingFace cache.
- Windows is the only supported platform. Do not add cross-platform hooks, `TODO: Linux Implementation` markers or non-Windows code paths for their own sake.
