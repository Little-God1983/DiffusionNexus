# Uninstall the Diffusion Nexus Engine and remove its features

Today a user who installed the Diffusion Nexus Engine, or one of its features, has no way to get rid
of it again. The Engine tile also shows Settings, Open Folder and Make Default as clickable items
that do nothing, with no word on why. This spec gives the Engine an uninstall, gives every installed
feature a Remove, and makes every item on the Engine tile either work or say why it can't.

Owner rulings (2026-10-12):
- The Engine's models live in the folder the user picked for the Engine. Only the Engine's own
  downloads change; the user's ComfyUI installs and the app's model management stay as they are.
- The Engine's models folder is registered in Settings → Base Model Folders.
- Removal tells the user when some of a feature's models come from one of their own model folders
  (ones other programs such as ComfyUI or A1111 may use), lists them with their paths, and offers:
  **Delete only what's in the Diffusion Nexus Engine** / **Delete all models of this feature,
  wherever they are** / **Cancel**.

## What changes for the user

### Feature downloads land in the Engine folder

Installing a feature (Installation Manager → Diffusion Nexus Engine → Features) downloads its models
into `<Engine folder>\ComfyUI\models\<category>` (or `<Engine folder>\models\<category>` for a
layout whose `main.py` sits at the root: the `models` folder next to `main.py`). Today they land in
the first folder of the Engine's `extra_model_paths.yaml`, which is the user's starred Base Model
Folder or `%LocalAppData%\DiffusionNexus\Models`.

The Engine still **reads** every Base Model Folder, so a model the user already has is found and not
downloaded again. Only where new downloads go changes.

The Installer Manager's **Workloads** button on the user's own ComfyUI installs is unchanged.

### The Engine's models folder is a Base Model Folder

When the Engine install finishes, its models folder is registered in Settings → Base Model Folders,
linked to the Engine, never as the starred default. Today this happens only on the next app start,
through the startup backfill.

Only that one folder is registered for the Engine. Today the backfill registers every root of the
Engine's `extra_model_paths.yaml` and re-links the user's own folders (for example `D:\Models`) to the
Engine row (`AppSettingsService.AddBaseModelFolderAsync` re-links an existing row to the latest
package). That stops: for the app-managed row, the registrar registers only the Engine's own models
folder and leaves rows outside it alone.

### The Engine tile

| Item | Behaviour |
|---|---|
| **Open Folder** | Opens the Engine's install folder. A missing folder logs and does nothing, as for any tile. |
| **Settings** (kebab item and gear) | Greyed out. Tooltip: *"The app manages the Diffusion Nexus Engine's settings. To install it somewhere else, uninstall it and install it again."* |
| **Make Default** | Greyed out. Tooltip: *"The Engine runs in the background for Diffusion Nexus features, so it can't be your default ComfyUI."* |
| **Uninstall Engine…** | New. Replaces Remove and Delete from Disk on this tile. Shown whenever the Engine has a row: installed, incomplete or missing. |
| Remove / Delete from Disk | Not shown on the Engine tile. Other tiles are unchanged. |

### Uninstall Engine

1. **Busy check.** If a generation is running on the Engine (`HasActiveJobs`), or a feature install
   or an Engine update is running (`IInstallActivity`), the app says so and stops: *"The Diffusion
   Nexus Engine is busy (…). Wait until it finishes, then uninstall."* Nothing is changed.
2. **Plan.** The app works out what would be deleted:
   - the Engine folder: custom nodes, Python environment and every model inside it, with its size;
   - the models of every Installed or Partial feature that sit **outside** the Engine folder, in one
     of the user's model folders (see *Finding a feature's files*). Features that are not installed
     contribute nothing, so a file the user has for their own reasons is never listed because of a
     feature they never installed.
3. **Confirm.**
   - No outside models: *"Uninstall the Diffusion Nexus Engine? This deletes <folder> (<size>):
     the Engine, its features and the models it downloaded. Your own model folders are not
     touched."* → **Uninstall** / **Cancel**.
   - Outside models found: the same text plus *"Some models the Engine's features use are in your
     own model folders. Other programs (ComfyUI, A1111, …) may use them too:"*, then a list of each
     file with its folder and size, then **Delete only what's in the Diffusion Nexus Engine** /
     **Delete these models too** / **Cancel**.
4. **Run**, under `TryBeginMaintenanceAsync` so nothing can start the Engine meanwhile:
   - stop the Engine (`IManagedEngineControl.StopAsync`);
   - delete the Engine folder (the same force delete Delete from Disk uses), then the chosen outside
     models;
   - remove the Base Model Folder rows whose path lies inside the Engine folder (by path, never by
     package id, so a user folder that a past backfill linked to the Engine survives);
   - remove the Engine row (its other Base Model Folder links are set to null by the existing FK);
   - if Settings → ComfyUI Server is set to the Engine, switch it to the custom URL, keeping the
     saved URL;
   - publish `InstallerPackagesChanged`, `EngineChanged` and `SettingsSaved`.
5. **Result.** The tile goes back to "Not installed" with its Install button. The editor's features
   and Settings already report "not installed" from the folder probe.

**Partial failure.** If some files can't be deleted (locked by another program, access denied), the
app keeps the Engine row and its Base Model Folder, so the tile shows "Installation incomplete" and
Uninstall Engine stays available for another try. The message lists what could not be deleted.
Outside models that failed to delete are listed the same way. A missing folder is not a failure: the
row and registrations are removed and the user is told the folder was already gone.

### Remove a feature

The Features dialog shows **Remove…** on every row that is Installed or Partial. It is disabled while
the dialog installs, with the Engine busy, or with an install running (same busy check as above,
shown as the tooltip).

1. **Plan.** For the feature's workload(s):
   - **Custom nodes:** each node pack folder under the Engine's `custom_nodes` that **no other
     installed feature** uses. A pack another installed feature needs is kept.
   - **Models:** every copy of each of the feature's model files, wherever it is found (all quant
     names, all search folders; not just the first match). A file that **another installed feature**
     also uses is kept, matched by category and file name (the same file has a different id in each
     workload).
   - Kept items are listed with the feature that still needs them: *"Kept: qwen_image_vae.safetensors
     (used by Outpaint Vision)."*
2. **Confirm.**
   - Every model to delete is inside the Engine: *"Remove <feature>? This deletes <n> custom node
     pack(s) and <n> model(s) (<size>) from the Diffusion Nexus Engine."* → **Remove** / **Cancel**.
   - Some are in the user's own model folders: the same warning and list as for the Engine uninstall,
     then **Delete only what's in the Diffusion Nexus Engine** / **Delete all models of this feature,
     wherever they are** / **Cancel**.
3. **Run**, under `TryBeginMaintenanceAsync`: stop the Engine if it runs (ComfyUI holds its node
   packs' Python modules and memory-maps loaded models, so Windows refuses to delete them), delete
   the node pack folders, then the models. The Engine starts again on the next use.
4. **Result.** The dialog checks again and the row shows its new status. `EngineChanged` is
   published, so an open Inpaint/Outpaint panel re-checks its readiness. Files that could not be
   deleted are listed in the message and the Unified Console.

Python packages that a feature installed into the Engine's environment (its `requirements.txt`, the
llama-cpp-python wheel) stay; the catalog does not list them, so they cannot be removed safely.
Uninstall Engine removes them with the environment.

## Finding a feature's files

`EngineFeatureRemovalPlanner` (new, `Services\Engine\`) turns a set of features into a removal plan.
Inputs: the Engine install root, the feature(s) to remove, every Engine feature's workload(s) from the
catalog, and the configuration check of every feature (so the planner knows which other features are
installed).

- **Node packs:** `<repo>\custom_nodes\<PathNormalizer.GetRepositoryName(url)>`, the same name the
  installer and the checker use.
- **Models:** the checker's candidate file names for a model (every quant's URL-derived name and
  `<Name><ext>`), searched in every path the checker searches (the Engine's `models`, every
  `extra_model_paths.yaml` root and category folder, and one subfolder level below each), collecting
  **every** hit instead of the first. Candidate name building and the search paths are shared with
  `ConfigurationCheckerService`, not copied.
- **Inside or outside:** a file is the Engine's when its full path lies inside the Engine install
  root (`FolderPathMatch`); everything else is the user's.
- **"Used by another feature":** a model or node pack also declared by a feature that is still
  Installed or Partial and is not being removed. For Uninstall Engine, nothing is kept: every feature
  goes.

Known gap: a model whose catalog URL does not carry its file name (Krea 2 Turbo's Identity Edit LoRA,
`…/api/download/models/3139172?fileId=…`, which the checker expects as `3139172`) is not found by
name, so Remove leaves it in place. It sits in the Engine folder from now on, so Uninstall Engine
removes it. Fixing the catalog entry is separate work.

## Pieces

- `WorkloadInstallService.InstallSelectedAsync`: new optional `modelsRootOverride`. When set, it is
  the base folder for model downloads instead of the first `extra_model_paths.yaml` root.
  `EngineFeaturesViewModel` passes `<repo>\models`.
- `BaseModelFolderRegistrar.ResolveModelRoots`: for an `IsAppManaged` package, only `<repo>\models`.
  Called right after a successful Engine install as well as by the startup backfill.
- `EngineFeatureRemovalPlanner` + its plan record (node packs to delete, Engine models to delete,
  outside models, kept items with the feature that keeps them, sizes).
- `EngineRemovalService` (new, `Services\Engine\`): busy check, maintenance lock, stop, delete,
  registrations, row, server mode, events. One method for Uninstall Engine, one for Remove feature.
  File deletion goes through an injectable seam so tests can simulate locked files.
- A confirm dialog (`EngineRemovalDialog`) for both flows: message, optional "your model folders"
  list with path and size, kept list, and the two or three buttons. Checks
  `DiffusionNexus.UI/REUSABLES.md` first for an existing list/confirm dialog to build on.
- `InstallerPackageCardViewModel`: `ShowEngineUninstallMenuItem`, Settings and Make Default
  `CanExecute` false on the Engine with the tooltips above, Remove and Delete from Disk hidden on the
  Engine.
- `InstallerManagerViewModel.CreateEngineCard`: wires Open Folder and Uninstall.
- `EngineFeatureRowViewModel` / `EngineFeaturesViewModel`: Remove command, busy tooltip, re-check
  after removal.

Every step logs to the Unified Console (category Installation, source "Diffusion Nexus Engine"):
plan (counts, sizes, kept items with reasons), the user's choice, each deleted folder or file, each
failure with its reason, and the end result.

## Testing

Unit tests (temp folders, fakes for the engine control, install activity, settings and dialogs):
- Planner: a node pack or model shared with another installed feature is kept and named; one shared
  only with a not-installed feature is deleted; every copy is found, not just the first; inside vs
  outside the Engine; all quant names probed; Uninstall plans the whole folder plus outside models.
- Removal service: refuses while a job runs or an install is active, and changes nothing; stops the
  Engine before deleting; "only Engine" never touches outside files; "all" deletes them; a locked
  file keeps the row and reports the file; a missing folder removes the row; Base Model Folder rows
  are removed by path, and a user folder linked to the Engine row survives; server mode switches
  only when it was Engine.
- Registrar: the app-managed row registers only its own models folder and never re-links an existing
  user folder.
- `WorkloadInstallService`: with `modelsRootOverride`, a model's destination is under it.
- Card: Settings and Make Default disabled with tooltips on the Engine; Uninstall shown for installed,
  incomplete and missing Engine; Remove/Delete hidden on the Engine only.

Manual smoke on the BenQ monitor, against a real Engine:
1. Install a feature → its models land in `<Engine>\ComfyUI\models`; Settings lists that folder as a
   Base Model Folder linked to the Engine.
2. Remove Batch Upscale with Batch Upscale Vision installed → shared models and node packs kept and
   named; the rest deleted; both rows show the right status.
3. Remove a feature whose model sits in `D:\Models` → the warning lists it; "only Engine" leaves it.
4. Uninstall Engine while it runs idle → stopped, folder gone, Base Model Folder row gone, `D:\Models`
   still registered, tile back to Install, Inpaint shows "Install the Diffusion Nexus Engine".
5. Uninstall Engine during a generation → refused, nothing changed.
6. Engine tile: Open Folder opens the folder; Settings and Make Default greyed out with their
   tooltips.
