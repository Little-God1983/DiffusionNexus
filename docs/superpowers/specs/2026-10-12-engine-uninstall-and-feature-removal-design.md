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

### The Engine's base is the blank ComfyUI workload

The Engine is built from the catalog workload **Blank ComfyUI + Manager + Triton & SageAttention**
(`1F0A3AEF-15D9-4B8A-80DC-B03F318E414F`) instead of Krea-2-Turbo (`E79C079A-…`) with all its content
stripped. Owner ruling 2026-10-12. Both declare the same environment (Python 3.12, its own `venv`,
torch 2.11.0 + CUDA 13.0, Triton and SageAttention), so the Engine it installs is the same as today
and existing Engines and features are unaffected; the Engine simply no longer reads as "Krea". Its
one node pack, ComfyUI-Manager, stays excluded like every node pack today (the Engine runs headless).
The Canvas feature keeps installing the Krea 2 Turbo workload, Manager included.

The workload is in the app's embedded catalog seed (`DiffusionNexus.UI\Assets\Catalog`) and in the
installed stable and preview catalogs.

### The Engine's models folder is a Base Model Folder

When the Engine install finishes, its models folder is registered in Settings → Base Model Folders,
linked to the Engine, never as the starred default. Today this happens only on the next app start,
through the startup backfill. When it was already registered, the Unified Console says so.

The app reads that folder like any other (search roots), but it is **never a download target** for
Diffusion Nexus Core models: `ModelFolderCatalog.GetDownloadTargetsAsync` (and so every caller of the
default download root) leaves out each enabled folder whose path lies inside an app-managed install,
starred or not. By path, not by the row's package link, because an old backfill linked the user's own
libraries to the Engine row. A blank, relative or drive-root Engine path filters nothing, and a failing
package read offers every folder. With no folder left, the LocalAppData fallback is the default. Without
this, a user with no other folder downloaded Core models into the Engine, and Uninstall deleted them.

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
     of the user's model folders (see *Finding a feature's files*). Features that read as not installed
     contribute nothing. A feature reads as Partial as soon as one of its files is found, so the user's
     own copy of a common file (for example their own `ae.safetensors`) can make a feature they never
     installed read as Partial, and that copy is then listed. Deleting it is always an explicit choice:
     **Delete only what's in the Diffusion Nexus Engine** leaves every listed file where it is.
3. **Confirm.**
   - No outside models: *"Uninstall the Diffusion Nexus Engine? This deletes <folder> (<size>):
     the Engine, its features and the models it downloaded. Your own model folders are not
     touched."* → **Uninstall** / **Cancel**.
   - Model files in the Engine's models folder that no Engine feature declares (not among any
     feature check's found model paths; model extensions only, junctions not entered), when there are
     any: *"It also holds <n> other model file(s) (<size>) that no Engine feature uses, for example
     ones another part of the app downloaded there. They are deleted with the Engine."* Skipped when the
     feature check failed: the app does not guess.
   - Outside models found: the same text plus *"Some models the Engine's features use are in your
     own model folders. Other programs (ComfyUI, A1111, …) may use them too:"*, then a list of each
     file with its folder and size, then **Delete only what's in the Diffusion Nexus Engine** /
     **Delete these models too** / **Cancel**.
4. **Uninstalling state.** From the confirmation through the forget step the tile shows
   *"Uninstalling..."* with a status line, hides Features, Install and Uninstall Engine, and the
   Features dialog (from the tile or the editor's "Install …" link) refuses to open, says why and
   logs it: its yaml sync and installs would write into the folder being deleted. The uninstall holds
   an `IInstallActivity` handle ("Diffusion Nexus Engine uninstall") for that time, so a catalog apply
   or another install waits. The installs are checked once more right before the handle is taken (an
   install started while the question was open refuses the uninstall), and the remover is told the
   caller holds its own handle (`installActivityHeldByCaller`) so it does not refuse itself; a running
   generation still refuses. A refusal, a failure or an error ends the state and releases the handle.
5. **Run**, under `TryBeginMaintenanceAsync` so nothing can start the Engine meanwhile:
   - stop the Engine if it runs (`IManagedEngineControl.StopIfIdleAsync`); a generation that is
     running makes the removal refuse as busy, and nothing is deleted;
   - delete the Engine folder with `EngineRemover`'s own walk (not Delete from Disk's force delete):
     read-only flags are cleared, a junction or symlink found inside is removed as a link and never
     entered, and an entry that cannot be deleted is reported while the walk goes on; then the chosen
     outside models;
   - remove the Base Model Folder rows whose path lies inside the Engine folder (by path, never by
     package id, so a user folder that a past backfill linked to the Engine survives); Generation
     Galleries and LoRA sources inside the Engine folder are forgotten the same way (the startup
     outputs registrar can link the Engine's `output` folder as a gallery);
   - remove the Engine row (its other Base Model Folder links are set to null by the existing FK);
   - if Settings → ComfyUI Server is set to the Engine, switch it to the custom URL, keeping the
     saved URL;
   - publish `InstallerPackagesChanged`, `EngineChanged` and `SettingsSaved`.
6. **Result.** The tile goes back to "Not installed" with its Install button. The editor's features
   and Settings already report "not installed" from the folder probe. When the Engine folder itself
   was a junction or symlink, only the link went: the user is told so, with the folder the files stay
   in (or "the folder it pointed to" when the target cannot be read), and the console logs it.

**Deletion safety.** A folder that is itself a junction or symlink loses only the link. A link found
inside the Engine while a folder is deleted (for example `ComfyUI\models\loras` pointing at
`D:\Models\loras`) is removed as a link, never entered, so the files behind it stay. A file or folder
listed for deletion that is reached through a link inside the Engine folder is refused and reported,
never followed. Drive roots, blank and relative paths are
never deleted. Link checks are scoped to the Engine folder: a link above it does not block.

**Corrupted row.** An Engine row whose path is blank, relative or a drive root is only forgotten:
nothing is sized or deleted, and the settings entries stay as they are.

**Model check fails.** If checking which models the features use fails, Uninstall continues with the
Engine folder only (the failure is logged).

**Partial failure.** If some files can't be deleted (locked by another program, access denied), the
app keeps the Engine row and its Base Model Folder, so the tile shows "Installation incomplete" and
Uninstall Engine stays available for another try. The message lists what could not be deleted. One
locked entry does not stop the rest: the folder walk goes on past it (IOException,
UnauthorizedAccessException), every entry that could not be deleted is reported on its own, and a
folder that stays only because something in it failed is not reported again.
Outside models that failed to delete are listed the same way. A missing folder is not a failure: the
row and registrations are removed and the user is told the folder was already gone.

### Remove a feature

The Features dialog shows **Remove…** on every row that is Installed or Partial. It is disabled while
the dialog itself installs or removes. Clicking it while the Engine is busy (a generation or another
install) says why and changes nothing.

1. **Plan.** The dialog shows *"Checking <feature>'s files…"* while it works out, for the feature's
   workload(s):
   - **Custom nodes:** each node pack folder under the Engine's `custom_nodes` that **no other
     installed feature** uses. A pack another installed feature needs is kept.
   - **Models:** every copy of each of the feature's model files, wherever it is found (all quant
     names, all search folders; not just the first match). A file that **another installed feature**
     also uses is kept, matched by the full path the checker found (the same file has a different id in
     each workload).
   - "Another installed feature" means one fully installed and present on its own (see *Used by
     another feature* below); a Partial feature protects nothing. A feature present only through this
     feature's files goes with it and is named in the question; one that loses some files but keeps
     others is named as left Partial.
   - Kept items are listed with the feature that still needs them: *"Kept: qwen_image_vae.safetensors
     (used by Outpaint Vision)."*
2. **Confirm.**
   - Every model to delete is inside the Engine: *"Remove <feature>? This deletes <n> custom node
     pack(s) and <n> model(s) (<size>) from the Diffusion Nexus Engine."* → **Remove** / **Cancel**.
   - Some are in the user's own model folders: the same warning and list as for the Engine uninstall,
     then **Delete only what's in the Diffusion Nexus Engine** / **Delete all models of this feature,
     wherever they are** / **Cancel**. When nothing of the feature is in the Engine (no node pack, no
     Engine model), the counting sentence is replaced by *"Nothing of it is in the Diffusion Nexus
     Engine's own folder."*
3. **Run**, under `TryBeginMaintenanceAsync`: stop the Engine if it runs (ComfyUI holds its node
   packs' Python modules and memory-maps loaded models, so Windows refuses to delete them), delete
   the node pack folders, then the models. The Engine starts again on the next use.
4. **Result.** The dialog checks again and the row shows its new status. `EngineChanged` is
   published, so an open Inpaint/Outpaint panel re-checks its readiness. Files that could not be
   deleted are listed in the message and the Unified Console.

If checking which models the feature uses fails, Remove… says the files could not be checked and
changes nothing. If every file of the feature is needed by another installed feature, it says so and
changes nothing. Choosing "only what's in the Engine" when nothing of the feature is in the Engine
leaves everything as it is and says so.

Python packages that a feature installed into the Engine's environment (its `requirements.txt`, the
llama-cpp-python wheel) stay; the catalog does not list them, so they cannot be removed safely.
Uninstall Engine removes them with the environment.

## Finding a feature's files

`EngineRemovalPlanner` (`Services\Engine\`) turns a set of features into a removal plan.
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
- **"Used by another feature":** a model or node pack (matched by full path) also used by a feature that
  is not being removed, is **fully installed** (every workload in the catalog, no node pack and no
  non-placeholder model missing) and is **present on its own**: it has at least one installed node pack
  or (non-placeholder) model file on disk that is not one of the removed feature's own files. A model
  the checker cannot find by name (its download URL carries no file name, see the known gap below) does
  not count as missing.
  - A **Partial** feature protects nothing: it does not work as it is. Without this rule features that
    were each removed once (Outpaint Vision, Batch Upscale Vision and Canvas, left with {Qwen3-VL, VAE},
    {Qwen3-VL, 4x-UltraSharp, Ultimate SD Upscale} and {VAE, 4x-UltraSharp, Ultimate SD Upscale}) kept
    the shared files for one another, and every further Remove said nothing could be deleted. A Partial
    feature that loses some of its files is named in the confirmation (*"Batch Upscale Vision loses the
    files it shares with this feature, except those another feature keeps; it will show as Partial."*).
  - A feature whose files on disk are all the removed feature's (Inpaint & Outpaint inside Outpaint
    Vision, Batch Upscale inside Batch Upscale Vision) reads as present only through them, protects
    nothing and goes with it; the confirmation names it (*"Inpaint & Outpaint uses only files of this
    feature, so it is removed too."*). When a third feature keeps one of its files, it is named as left
    Partial instead (the sentence above). Without this rule two such features kept the shared files for
    each other and neither could ever be removed.
  - So with both of a subset pair installed, removing the small one first keeps everything (the big one
    needs it) and removing the big one next deletes the shared files; removing the big one first deletes
    the shared files at once. A removal that deletes a file leaves the removed feature Partial, so it
    protects nothing afterwards: when every feature has a file of its own (Outpaint Vision, Batch Upscale
    Vision and Canvas do), removing each once, in any order, removes every file. A feature with no file
    of its own (the subset features above) whose files are all needed by other fully installed features
    keeps everything until one of those is removed.
    For Uninstall Engine, nothing is kept: every feature goes.
- **Catalog gaps:** when the catalog has no description of another feature's workload, which files that
  feature shares is unknown, so Remove… deletes nothing and says so (*"The catalog has no description of
  <X>, so the app cannot tell which files <X> shares. Update the catalog or switch its channel, then try
  again. Uninstall Engine still works."*). Uninstall Engine then leaves out the count of model files no
  feature uses (it would count that feature's files too).
- **Names searched for removal:** when every copy is collected, a URL-derived name that the installer
  renames to its configured name (`<Name><ext>`) is replaced by that name, not searched beside it: a
  generic HuggingFace name (`diffusion_pytorch_model.safetensors`) found in the user's library belongs to
  some other model. The readiness check still searches both.

Known gap: a model whose catalog URL does not carry its file name (Krea 2 Turbo's Identity Edit LoRA,
`…/api/download/models/3139172?fileId=…`, which the checker expects as `3139172`) is not found by
name, so Remove leaves it in place. It sits in the Engine folder from now on, so Uninstall Engine
removes it. Fixing the catalog entry is separate work.

## Pieces

- `ManagedEngineInstaller.BaseConfigurationId` → `1F0A3AEF-15D9-4B8A-80DC-B03F318E414F`; its doc
  comments and `ManagedEngineInstallerTests` follow.
- `WorkloadInstallService.InstallSelectedAsync`: new optional `modelsRootOverride`. When set, it is
  the base folder for model downloads instead of the first `extra_model_paths.yaml` root.
  `EngineFeaturesViewModel` passes `<repo>\models`.
- `BaseModelFolderRegistrar.ResolveModelRoots`: for an `IsAppManaged` package, only `<repo>\models`.
  Called right after a successful Engine install as well as by the startup backfill.
- `EngineRemovalPlanner` + its plan record (node packs to delete, Engine models to delete,
  outside models, kept items with the feature that keeps them, sizes).
- `EngineRemover` (`Services\Engine\`): busy check, stop, maintenance hold, delete. One `RemoveAsync`
  serves Uninstall Engine and Remove feature (`installActivityHeldByCaller` for the uninstall, which
  holds its own install-activity handle). File deletion goes through an injectable seam
  (`IRemovalFileSystem`, whose `DeleteDirectory` returns the entries it could not delete) so tests can
  simulate locked files; `InstallerManagerViewModel` takes the same seam for the uninstall. The link,
  drive and relative-path checks stay in `EngineRemover`.
- `ModelFolderCatalog.GetDownloadTargetsAsync`: leaves out folders inside an app-managed install
  (reads the installer packages through an optional `IUnitOfWork`); `GetSearchRootsAsync` unchanged. The registrations, the row, the server
  mode and the events are the uninstall flow's job: `UninstallEngineAsync` and `ForgetEngineAsync` in
  `InstallerManagerViewModel`.
- `EngineRemovalPlanner` and `EngineRemovalMessages` (`Services\Engine\`): the plan and the wording.
  `EngineFeatureChecks` loads the disk check of every feature for the planner.
- Confirmation goes through `IDialogService.ShowOptionsAsync` (Cancel first, the safe choice last), so
  there is no new dialog; the text, with the "your model folders" list (path and size) and the kept
  list, comes from `EngineRemovalMessages`.
- `InstallerPackageCardViewModel`: `ShowEngineUninstallMenuItem`, Settings and Make Default
  `CanExecute` false on the Engine with the tooltips above, Remove and Delete from Disk hidden on the
  Engine. Open Folder is greyed with a reason when the Engine has no row or its folder is missing. The
  Core tile gets the same treatment: Settings, Open Folder and Make Default greyed with "built into
  the app" tooltips.
- `InstallerManagerViewModel.CreateEngineCard`: wires Open Folder and Uninstall.
- `EngineFeatureRowViewModel` / `EngineFeaturesViewModel`: Remove command (`RemoveFeatureAsync`),
  re-check after removal. The Features dialog's Close is disabled while a removal runs.

Every step logs to the Unified Console (category Installation, source "Diffusion Nexus Engine"):
plan (counts, sizes, kept items with reasons), the user's choice, each deleted folder or file, each
failure with its reason, and the end result.

## Testing

Unit tests (temp folders, fakes for the engine control, install activity, settings and dialogs):
- Planner: a node pack or model shared with another installed feature is kept and named; one shared
  only with a not-installed feature is deleted; overlapping features (one's files a subset of the
  other's) can both be removed in either order, and a feature present only through the removed
  one's files is named as going with it (or as left Partial when a third feature keeps one of its
  files); a Partial feature protects nothing, so Outpaint Vision, Batch Upscale Vision and Canvas
  removed once each, in several orders, leave nothing behind, while a model the checker cannot find
  by name does not make a feature Partial; Uninstall counts the model files in the Engine's models
  folder that no feature declares; every copy is found, not just the first; inside vs
  outside the Engine; all quant names probed; Uninstall plans the whole folder plus outside models.
- `EngineRemover` (and the uninstall flow): refuses while a job runs or an install is active, and changes nothing; stops the
  Engine before deleting; "only Engine" never touches outside files; "all" deletes them; a locked
  file keeps the row and reports the file; a locked file in the middle of a folder is reported and
  everything else in the folder is deleted; the tile shows "Uninstalling..." and the Features dialog
  refuses while the uninstall runs; an Engine folder that is a junction loses only the link and the
  user is told where the files stay; a failure outside the Engine folder does not keep the row; a missing folder removes the row; Base Model Folder rows
  are removed by path, and a user folder linked to the Engine row survives; server mode switches
  only when it was Engine.
- Registrar: the app-managed row registers only its own models folder and never re-links an existing
  user folder.
- `ModelFolderCatalog`: with only the Engine's folder registered the default download target is the
  LocalAppData fallback; with a user folder too, the user folder is the default and the Engine's is
  not offered; search roots still include the Engine's folder.
- `WorkloadInstallService`: with `modelsRootOverride`, a model's destination is under it.
- Card: Settings and Make Default disabled with tooltips on the Engine; Uninstall shown for installed,
  incomplete and missing Engine; Remove/Delete hidden on the Engine only.

Manual smoke on the BenQ monitor, against a real Engine:
0. Fresh Engine install → built from the blank workload (Unified Console names it), `venv` present,
   torch 2.11.0 + cu130 read back, no custom nodes.
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
