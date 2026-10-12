# Engine Uninstall and Feature Removal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user uninstall the Diffusion Nexus Engine and remove single Engine features, make every item on the Engine tile either work or say why it can't, and build the Engine from the blank ComfyUI workload.

**Architecture:** Feature downloads move into the Engine's own `models` folder, so "inside the Engine folder" means "the Engine downloaded it". A pure planner (`EngineRemovalPlanner`) turns disk checks of every feature into a removal plan (node packs, Engine models, models in the user's own folders, kept items). `EngineRemover` does the deleting under the Engine's maintenance hold after stopping it. The Installation Manager drives Uninstall Engine; the Features dialog drives Remove feature; both confirm through the existing `IDialogService.ShowOptionsAsync`.

**Tech Stack:** .NET 10, Avalonia 11 (compiled bindings), CommunityToolkit.Mvvm, xUnit + FluentAssertions + Moq, DiffusionNexus.Installer.SDK 2.x (NuGet).

**Spec:** `docs/superpowers/specs/2026-10-12-engine-uninstall-and-feature-removal-design.md`

## Global Constraints

- Repo: `E:\Repos\DiffusionNexus`, branch `feature/engine-uninstall-and-feature-removal` (already exists, spec commits on it). Never commit to `develop`/`main`.
- Windows-only app (owner ruling): no Linux branches, no `TODO: Linux` additions.
- Engine base workload id: `1F0A3AEF-15D9-4B8A-80DC-B03F318E414F` ("Blank ComfyUI + Manager + Triton & SageAttention").
- Only the Engine changes where models download; the Installer Manager's Workloads button on the user's own ComfyUI installs is unchanged.
- Settings rows (Base Model Folders, Generation Galleries, LoRA sources) are removed **by path inside the Engine folder**, never by package id.
- Files that another Installed or Partial feature still uses are never deleted, in any mode.
- Copy, verbatim:
  - Settings tooltip on the Engine: `The app manages the Diffusion Nexus Engine's settings. To install it somewhere else, uninstall it and install it again.`
  - Make Default tooltip on the Engine: `The Engine runs in the background for Diffusion Nexus features, so it can't be your default ComfyUI.`
  - Options when models sit in the user's own folders: `Delete only what's in the Diffusion Nexus Engine` (primary, last) / `Delete all models of this feature, wherever they are` (feature) or `Delete these models too` (uninstall) / `Cancel` (first).
- Every step logs to the Unified Console: `LogCategory.Installation`, source `"Diffusion Nexus Engine"` (standing rule: features log their steps).
- Commit after each task; push after each task (`git push`). Commit messages end with:
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
- Test command base: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~<Name>"` from the repo root.
- Before the PR: `git diff --numstat origin/develop` vs `git diff -w --numstat origin/develop` must not show CRLF flips.

## Review Focus

1. **A junction inside the Engine folder that points at the user's library** (e.g. `ComfyUI\models\loras` → `D:\Models\loras`): Uninstall must remove the link, never the files behind it. Test in Task 6.
2. **A corrupted Engine row whose path is a drive root** (`D:\`) or blank: Uninstall must refuse to delete it. Test in Task 6.
3. **Uninstall clicked twice** (the second click while the first waits on its dialog or deletes): the second call must do nothing. Test in Task 8.
4. **The Engine starting while a removal runs** (Canvas or an editor feature calls `EnsureRunningAsync`): the maintenance hold must block it, and a refused hold must report "busy" without deleting anything. Test in Task 6.
5. **One file locked by another program** during Remove feature: the rest is still deleted, the locked file is named in the message and the console, and the row re-checks to Partial. Test in Task 9.

---

## File Structure

| File | Responsibility |
|---|---|
| `DiffusionNexus.UI/Services/Engine/ManagedEngineInstaller.cs` (modify) | Base workload id → blank ComfyUI. |
| `DiffusionNexus.UI/Services/Engine/ManagedEngineLocator.cs` (modify) | New `ModelsFolder(installRoot)`. |
| `DiffusionNexus.UI/Services/IWorkloadInstallService.cs`, `WorkloadInstallService.cs` (modify) | `modelsRootOverride` parameter. |
| `DiffusionNexus.UI/Services/Diffusion/BaseModelFolderRegistrar.cs` (modify) | App-managed row registers only its own models folder. |
| `DiffusionNexus.UI/Services/ConfigurationChecker/*` (modify) | `CollectAllMatches` option, `AllFoundPaths` result. |
| `DiffusionNexus.UI/Services/Engine/EngineFeatureChecks.cs` (new) | Loads the disk check of every feature. |
| `DiffusionNexus.UI/Services/Engine/EngineRemovalPlanner.cs` (new) | Pure: checks → removal plan. |
| `DiffusionNexus.UI/Services/Engine/EngineRemovalMessages.cs` (new) | Pure: dialog text, options, choice mapping. |
| `DiffusionNexus.UI/Services/Engine/EngineRemover.cs` (new) | Busy check, stop, maintenance hold, deletion. |
| `DiffusionNexus.UI/ViewModels/InstallerPackageCardViewModel.cs`, `Views/InstallerManagerView.axaml` (modify) | Tile menu and tooltips. |
| `DiffusionNexus.UI/ViewModels/InstallerManagerViewModel.cs` (modify) | Open Folder wiring, models-folder registration on install, Uninstall Engine flow, Features dialog hand-off. |
| `DiffusionNexus.UI/ViewModels/EngineFeaturesViewModel.cs`, `EngineFeatureRowViewModel.cs`, `Views/Dialogs/EngineFeaturesDialog.axaml` (modify) | Remove feature. |

---

### Task 1: Build the Engine from the blank ComfyUI workload

**Files:**
- Modify: `DiffusionNexus.UI/Services/Engine/ManagedEngineInstaller.cs:11-27`
- Modify: `DiffusionNexus.UI/Services/Engine/EngineFeatureCatalog.cs:60`
- Test: `DiffusionNexus.Tests/Engine/ManagedEngineInstallerTests.cs`

**Interfaces:**
- Produces: `ManagedEngineInstaller.BaseConfigurationId == "1F0A3AEF-15D9-4B8A-80DC-B03F318E414F"`.

- [ ] **Step 1: Write the failing test** — add to `ManagedEngineInstallerTests`:

```csharp
// Owner ruling 2026-10-12: the Engine is the blank ComfyUI workload, not Krea-2-Turbo stripped bare.
[Fact]
public void TheEngineIsBuiltFromTheBlankComfyUiWorkload()
{
    ManagedEngineInstaller.BaseConfigurationId.Should().Be("1F0A3AEF-15D9-4B8A-80DC-B03F318E414F");
}
```

- [ ] **Step 2: Run it** — `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ManagedEngineInstallerTests"`. Expected: the new test FAILS (`E79C079A-…`).

- [ ] **Step 3: Implement.** In `ManagedEngineInstaller.cs` replace the constant and its doc, and the first paragraph of the class doc:

```csharp
/// <summary>
/// Drives the SDK's installation coordinator to produce a bare, app-owned ComfyUI.
///
/// The base engine is built from the "Blank ComfyUI + Manager + Triton &amp; SageAttention" workload
/// with its one node pack (ComfyUI-Manager) excluded, like every model, node and workflow: the Engine
/// runs headless. No torch settings are authored here — ...   (keep the rest of the existing paragraph)
/// </summary>
```

```csharp
/// <summary>
/// Catalog id of the workload that defines the engine base: "Blank ComfyUI + Manager + Triton &amp;
/// SageAttention" (owner ruling 2026-10-12; was Krea-2-Turbo with all content stripped, which declares
/// the same Python, venv, torch, CUDA, Triton and SageAttention).
/// </summary>
public const string BaseConfigurationId = "1F0A3AEF-15D9-4B8A-80DC-B03F318E414F";
```

In `EngineFeatureCatalog.cs:60` change the doc of `Krea2Turbo` to:

```csharp
/// <summary>Krea 2 Turbo — the Canvas feature's workload.</summary>
```

In `ManagedEngineInstallerTests.cs` rename `BuildKreaConfiguration` → `BuildBaseConfiguration` (all call sites) and set `Name = "Blank ComfyUI + Manager + Triton & SageAttention"`. The test data inside (models, node, workflow) stays: it proves everything the workload declares is excluded.

- [ ] **Step 4: Run** the same filter. Expected: all PASS.

- [ ] **Step 5: Commit and push**

```bash
git add DiffusionNexus.UI/Services/Engine/ManagedEngineInstaller.cs DiffusionNexus.UI/Services/Engine/EngineFeatureCatalog.cs DiffusionNexus.Tests/Engine/ManagedEngineInstallerTests.cs
git commit -m "feat(engine): build the Engine from the blank ComfyUI workload (1f0a3aef)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 2: Feature downloads land in the Engine's own models folder

**Files:**
- Modify: `DiffusionNexus.UI/Services/Engine/ManagedEngineLocator.cs`
- Modify: `DiffusionNexus.UI/Services/IWorkloadInstallService.cs:47-56`
- Modify: `DiffusionNexus.UI/Services/WorkloadInstallService.cs:43-52,180-181,1105-1113`
- Modify: `DiffusionNexus.UI/ViewModels/EngineFeaturesViewModel.cs:183-199`
- Modify (mechanical): `DiffusionNexus.Tests/Engine/EngineFeaturesViewModelTests.cs`, `DiffusionNexus.Tests/Engine/EngineWorkloadsViewModelTests.cs`
- Test: `DiffusionNexus.Tests/Engine/ManagedEngineLocatorTests.cs`, `DiffusionNexus.Tests/InstallerManager/WorkloadInstallServiceModelsRootTests.cs` (new)

**Interfaces:**
- Produces: `static string ManagedEngineLocator.ModelsFolder(string installRoot)` — `<repository>\models`, repository resolved like the installer and checker (`<root>\ComfyUI` when `<root>\ComfyUI\main.py` exists, else `<root>`).
- Produces: `IWorkloadInstallService.InstallSelectedAsync(..., Func<CancellationToken>? skipDownloadTokenProvider = null, string? modelsRootOverride = null, CancellationToken cancellationToken = default)`.
- Produces: `internal static string? WorkloadInstallService.ResolveModelBaseFolder(string repositoryPath, string? modelsRootOverride)`.

- [ ] **Step 1: Write the failing tests.** Add to `ManagedEngineLocatorTests` (it already uses temp folders; follow its fixture):

```csharp
[Fact]
public void ModelsFolder_IsNextToMainPy_ForANestedLayout()
{
    var root = Directory.CreateTempSubdirectory("dn-engine-models-").FullName;
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "ComfyUI"));
        File.WriteAllText(Path.Combine(root, "ComfyUI", "main.py"), "");

        ManagedEngineLocator.ModelsFolder(root).Should().Be(Path.Combine(root, "ComfyUI", "models"));
    }
    finally { Directory.Delete(root, recursive: true); }
}

[Fact]
public void ModelsFolder_IsUnderTheRoot_ForADirectLayout()
{
    ManagedEngineLocator.ModelsFolder(@"C:\Engine\ComfyUI").Should().Be(@"C:\Engine\ComfyUI\models");
}
```

Create `DiffusionNexus.Tests/InstallerManager/WorkloadInstallServiceModelsRootTests.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Engine feature downloads go to the Engine's own models folder (owner ruling 2026-10-12), not to the
/// first library of its extra_model_paths.yaml, which is the user's starred Base Model Folder.
/// </summary>
public sealed class WorkloadInstallServiceModelsRootTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("dn-models-root-");

    public void Dispose() { try { _temp.Delete(recursive: true); } catch { /* best effort */ } }

    private string Repo()
    {
        var repo = Path.Combine(_temp.FullName, "ComfyUI");
        var library = Path.Combine(_temp.FullName, "Library");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(repo, "extra_model_paths.yaml"),
            $"dn_models_1:\n    base_path: {library.Replace('\\', '/')}\n");
        return repo;
    }

    [Fact]
    public void WithoutOverride_TheFirstYamlLibraryWins_AsBefore()
    {
        var repo = Repo();
        WorkloadInstallService.ResolveModelBaseFolder(repo, modelsRootOverride: null)
            .Should().Be(Path.Combine(_temp.FullName, "Library"));
    }

    [Fact]
    public void WithOverride_TheOverrideWins_AndAModelLandsInItsCategory()
    {
        var repo = Repo();
        var models = Path.Combine(repo, "models");

        var baseFolder = WorkloadInstallService.ResolveModelBaseFolder(repo, models);
        var destination = ModelDestinationResolver.Resolve(
            new InstallationConfiguration(), new ModelDownload { Name = "vae", Destination = "models/vae" }, repo, baseFolder);

        baseFolder.Should().Be(models);
        destination.Should().Be(Path.Combine(models, "vae"));
    }
}
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ManagedEngineLocatorTests|FullyQualifiedName~WorkloadInstallServiceModelsRootTests"`. Expected: build FAILS (`ModelsFolder`, `ResolveModelBaseFolder` missing).

- [ ] **Step 3: Implement.**

`ManagedEngineLocator.cs` — add `using DiffusionNexus.UI.Services.ConfigurationChecker;` and:

```csharp
/// <summary>
/// The Engine's own models folder: <c>models</c> in the ComfyUI repository, resolved the way the
/// workload installer and the checker resolve it. Feature downloads land here (owner ruling
/// 2026-10-12), so a file inside it is one the Engine downloaded.
/// </summary>
public static string ModelsFolder(string installRoot)
{
    var type = ConfigurationCheckerService.DetectInstallationType(installRoot);
    return Path.Combine(ConfigurationCheckerService.GetRepositoryPath(installRoot, type), "models");
}
```

`IWorkloadInstallService.cs` — add the parameter and its doc between `skipDownloadTokenProvider` and `cancellationToken`:

```csharp
/// <param name="modelsRootOverride">
/// Base folder for model downloads instead of the first library in the install's
/// extra_model_paths.yaml. The Engine passes its own models folder; null keeps the yaml rule.
/// </param>
...
        Func<CancellationToken>? skipDownloadTokenProvider = null,
        string? modelsRootOverride = null,
        CancellationToken cancellationToken = default);
```

`WorkloadInstallService.cs` — same parameter in the implementation signature; replace line 181 and add the helper next to `ResolveExtraModelBasePath`:

```csharp
            // The Engine downloads into its own models folder; other installs into their yaml's first library.
            var extraBasePath = ResolveModelBaseFolder(repositoryPath, modelsRootOverride);
```

```csharp
/// <summary>
/// Where model downloads go: <paramref name="modelsRootOverride"/> when given, else the first
/// <c>base_path</c> of the install's extra_model_paths.yaml (null: relative to the repository).
/// </summary>
internal static string? ResolveModelBaseFolder(string repositoryPath, string? modelsRootOverride) =>
    string.IsNullOrWhiteSpace(modelsRootOverride) ? ResolveExtraModelBasePath(repositoryPath) : modelsRootOverride;
```

`EngineFeaturesViewModel.cs:198-199` — pass the Engine's models folder:

```csharp
                        skipDownloadTokenProvider: null,
                        modelsRootOverride: ManagedEngineLocator.ModelsFolder(_engineRoot),
                        ct);
```

`WorkloadsViewModel.cs:323-326` passes `ct` positionally after `skipTokenProvider`; change it to `cancellationToken: ct` so it still binds after the new parameter.

Mechanical test update (Moq expression trees cannot use optional arguments, so every setup/verify of `InstallSelectedAsync` must list the new argument):
- In `EngineFeaturesViewModelTests.cs` and `EngineWorkloadsViewModelTests.cs` replace every
  `It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()` with
  `It.IsAny<Func<CancellationToken>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()`,
  and in every `Callback((…, Func<CancellationToken>? _, CancellationToken _) =>` insert `string? _, ` before `CancellationToken _`.
  (Use the Edit tool with `replace_all`; do not use `sed -i`.)
- In `EngineWorkloadsViewModelTests.cs` the Setup at line ~171 passes literal values; add `It.IsAny<string?>()` in the same position.

Add one assertion test to `EngineFeaturesViewModelTests`:

```csharp
// Owner ruling 2026-10-12: Engine features download into the Engine, so its files can be told apart.
[Fact]
public async Task Install_DownloadsIntoTheEnginesOwnModelsFolder()
{
    _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(0, 1, 2, 3);
    _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
    string? passed = null;
    _installer.Setup(i => i.InstallSelectedAsync(It.IsAny<InstallationConfiguration>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
            It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
            It.IsAny<Func<CancellationToken>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
        .Callback((InstallationConfiguration _, string _, IReadOnlyList<CustomNodeCheckResult> _, IReadOnlyList<ModelCheckResult> _,
            int _, IProgress<WorkloadInstallProgress>? _, IProgress<DownloadProgress>? _, Func<CancellationToken>? _, string? root, CancellationToken _) =>
            passed = root)
        .ReturnsAsync("2 model(s) downloaded");
    var vm = Sut(EngineFeature.InpaintOutpaint);
    await vm.LoadCommand.ExecuteAsync(null);

    await vm.InstallSelectedCommand.ExecuteAsync(null);

    passed.Should().Be(@"C:\Engine\ComfyUI\models");
}
```

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~ManagedEngineLocatorTests|FullyQualifiedName~WorkloadInstallService|FullyQualifiedName~EngineFeaturesViewModelTests|FullyQualifiedName~EngineWorkloadsViewModelTests|FullyQualifiedName~WorkloadsViewModel"`. Expected: all PASS.

- [ ] **Step 5: Commit and push**

```bash
git add -A DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "feat(engine): feature downloads land in the Engine's own models folder" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 3: Register only the Engine's models folder, right after the install

**Files:**
- Modify: `DiffusionNexus.UI/Services/Diffusion/BaseModelFolderRegistrar.cs:104-118`
- Modify: `DiffusionNexus.UI/ViewModels/InstallerManagerViewModel.cs:1266-1287` (Engine install success path)
- Modify: `DiffusionNexus.Tests/InstallerManager/EngineTestHarness.cs` (optional `registrar` parameter)
- Test: `DiffusionNexus.Tests/Services/BaseModelFolderRegistrarTests.cs`, `DiffusionNexus.Tests/InstallerManager/EngineInstallFlowTests.cs`

**Interfaces:**
- Consumes: `ManagedEngineLocator.ModelsFolder` (Task 2).
- Produces: `BaseModelFolderRegistrar.ResolveModelRoots(package)` returns `[<engine>\…\models]` (when it exists) for `IsAppManaged` rows, nothing else.

- [ ] **Step 1: Write the failing tests.** In `BaseModelFolderRegistrarTests` (uses `Dir(...)` and `Package(...)` helpers):

```csharp
// The Engine's yaml lists every user library so it can read them. Registering those for the Engine
// re-linked D:\Models to the Engine row on every start; only its own models folder belongs to it.
[Fact]
public void ResolveModelRoots_AppManagedEngine_IsOnlyItsOwnModelsFolder()
{
    var engine = Dir("Engine", "ComfyUI");
    File.WriteAllText(Path.Combine(engine, "main.py"), "# comfy");
    var models = Dir("Engine", "ComfyUI", "models");
    var library = Dir("UserLibrary");
    File.WriteAllText(Path.Combine(engine, "extra_model_paths.yaml"),
        $"dn_models_1:\n    base_path: {library.Replace('\\', '/')}\n");
    var package = Package(engine, InstallerType.ComfyUI);
    package.IsAppManaged = true;

    BaseModelFolderRegistrar.ResolveModelRoots(package).Should().Equal(models);
}
```

In `EngineTestHarness.CreateInstallerManagerViewModel` add parameter `BaseModelFolderRegistrar? registrar = null` and pass `baseModelFolderRegistrar: registrar` to the constructor. Then in `EngineInstallFlowTests`:

```csharp
[Fact]
public async Task SuccessfulInstall_RegistersTheEnginesModelsFolder_RightAway()
{
    var temp = Directory.CreateTempSubdirectory("dn-engine-install-").FullName;
    var root = Path.Combine(temp, "Engine"); // chosen while it does not exist yet, as a real pick would be
    try
    {
        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "main.py"), "");
            })
            .ReturnsAsync(new EngineInstallOutcome(true, false, "done", root));
        var settings = new Mock<IAppSettingsService>();
        var registered = new List<string>();
        settings.Setup(s => s.AddBaseModelFolderAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((p, _, _) => registered.Add(p))
            .ReturnsAsync(true);
        var aggregator = new Mock<IDatasetEventAggregator>();
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [], engineInstaller: installer.Object, chosenFolder: root, onPackageAdded: _ => { },
            eventAggregatorMock: aggregator, registrar: new BaseModelFolderRegistrar(settings.Object));
        vm.IsEngineTileVisible = true;

        await vm.InstallEngineAsync();

        registered.Should().Equal(Path.Combine(root, "models"));
        aggregator.Verify(a => a.PublishSettingsSaved(It.IsAny<SettingsSavedEventArgs>()), Times.Once,
            "Settings → Base Model Folders reloads and shows the new folder");
    }
    finally { Directory.Delete(temp, recursive: true); }
}
```

(Usings: `DiffusionNexus.Domain.Services`, `DiffusionNexus.UI.Services.Diffusion`, `DiffusionNexus.Installer.SDK.Services`.)

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~BaseModelFolderRegistrarTests|FullyQualifiedName~EngineInstallFlowTests"`. Expected: both new tests FAIL.

- [ ] **Step 3: Implement.** `BaseModelFolderRegistrar.ResolveModelRoots`, before the ComfyUI branch:

```csharp
        // The Engine reads every user library through its yaml; those belong to the user, and
        // registering them for the Engine re-linked them to its row on every start. Its own
        // models folder, where its feature downloads land, is the one root it owns.
        if (package.IsAppManaged)
        {
            var models = Engine.ManagedEngineLocator.ModelsFolder(package.InstallationPath);
            return Directory.Exists(models) ? [models] : [];
        }
```

Add a sentence to the class doc: "The app-managed Engine contributes only its own models folder."

`InstallerManagerViewModel` — in the Engine install success path, right after the `_engineModelPaths` follow-up block (before "announce the new installation"):

```csharp
                    // Its models folder, where feature downloads land, is a Base Model Folder from the
                    // start: Settings shows it and the app's model features read it without a restart.
                    if (_baseModelFolderRegistrar is not null)
                    {
                        var registeredPackage = package;
                        await RunEngineFollowUpAsync("register the Engine's models folder", async () =>
                        {
                            Directory.CreateDirectory(Services.Engine.ManagedEngineLocator.ModelsFolder(registeredPackage.InstallationPath));
                            if (await _baseModelFolderRegistrar.EnsureRegisteredAsync([registeredPackage]) > 0)
                            {
                                _unifiedLogger.Info(LogCategory.Installation, EngineLogSource,
                                    $"Registered the Engine's models folder as a Base Model Folder: {Services.Engine.ManagedEngineLocator.ModelsFolder(registeredPackage.InstallationPath)}");
                                _eventAggregator.PublishSettingsSaved(new SettingsSavedEventArgs());
                            }
                        });
                    }
```

Check `SuccessfulInstall_PublishesEngineChanged_NotSettingsSaved` still passes (its harness has no registrar, so nothing is registered and no SettingsSaved is published).

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~BaseModelFolderRegistrarTests|FullyQualifiedName~Engine"`. Expected: all PASS.

- [ ] **Step 5: Commit and push**

```bash
git add -A DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "feat(engine): register only the Engine's own models folder, right after the install" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 4: The checker can report every copy of a model

**Files:**
- Modify: `DiffusionNexus.UI/Services/ConfigurationChecker/Models/ConfigurationCheckOptions.cs`
- Modify: `DiffusionNexus.UI/Services/ConfigurationChecker/Models/ModelCheckResult.cs`
- Modify: `DiffusionNexus.UI/Services/ConfigurationChecker/ConfigurationCheckerService.cs:325-414`
- Test: `DiffusionNexus.Tests/InstallerManager/ConfigurationCheckerAllMatchesTests.cs` (new)

**Interfaces:**
- Produces: `ConfigurationCheckOptions.CollectAllMatches` (bool, default false).
- Produces: `ModelCheckResult.AllFoundPaths` (`IReadOnlyList<string>`, default `[]`): every existing file for the model across all candidate names and search folders, de-duplicated by full path, first = `FoundAtPath`. With `CollectAllMatches = false` it holds at most the first hit (unchanged speed for readiness checks).

- [ ] **Step 1: Write the failing test** — new file:

```csharp
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>Removal must see every copy of a model, not only the first one the check finds.</summary>
public sealed class ConfigurationCheckerAllMatchesTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("dn-checker-all-");

    public void Dispose() { try { _temp.Delete(recursive: true); } catch { /* best effort */ } }

    private (string Repo, string EngineCopy, string LibraryCopy) TwoCopies()
    {
        var repo = Path.Combine(_temp.FullName, "Engine");
        var library = Path.Combine(_temp.FullName, "Library");
        Directory.CreateDirectory(Path.Combine(repo, "models", "vae"));
        Directory.CreateDirectory(Path.Combine(library, "vae"));
        File.WriteAllText(Path.Combine(repo, "main.py"), "");
        File.WriteAllText(Path.Combine(repo, "extra_model_paths.yaml"),
            $"dn_models_1:\n    base_path: {library.Replace('\\', '/')}\n");
        var engineCopy = Path.Combine(repo, "models", "vae", "ae.safetensors");
        var libraryCopy = Path.Combine(library, "vae", "ae.safetensors");
        File.WriteAllText(engineCopy, "x");
        File.WriteAllText(libraryCopy, "x");
        return (repo, engineCopy, libraryCopy);
    }

    private static InstallationConfiguration Config()
    {
        var config = new InstallationConfiguration { Name = "t" };
        config.ModelDownloads.Add(new ModelDownload
        {
            Name = "ae", Destination = "models/vae",
            DownloadLinks = [new ModelDownloadLink { Url = "https://example.com/ae.safetensors" }]
        });
        return config;
    }

    [Fact]
    public async Task CollectAllMatches_FindsTheEngineCopy_AndTheLibraryCopy()
    {
        var (repo, engineCopy, libraryCopy) = TwoCopies();

        var result = await new ConfigurationCheckerService().CheckConfigurationAsync(
            Config(), repo, new ConfigurationCheckOptions { CollectAllMatches = true });

        var model = result.ModelResults.Single();
        model.AllFoundPaths.Should().BeEquivalentTo([engineCopy, libraryCopy]);
        model.FoundAtPath.Should().Be(model.AllFoundPaths[0]);
    }

    [Fact]
    public async Task Default_StopsAtTheFirstCopy()
    {
        var (repo, engineCopy, _) = TwoCopies();

        var result = await new ConfigurationCheckerService().CheckConfigurationAsync(Config(), repo);

        var model = result.ModelResults.Single();
        model.FoundAtPath.Should().Be(engineCopy, "the explicit destination is searched first");
        model.AllFoundPaths.Should().Equal(engineCopy);
    }
}
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ConfigurationCheckerAllMatchesTests"`. Expected: build FAILS.

- [ ] **Step 3: Implement.**

`ConfigurationCheckOptions`:

```csharp
    /// <summary>
    /// Report every copy of each model (all candidate names, all search folders) in
    /// <see cref="ModelCheckResult.AllFoundPaths"/> instead of stopping at the first. Removal needs
    /// it; readiness checks leave it off for speed.
    /// </summary>
    public bool CollectAllMatches { get; init; }
```

`ModelCheckResult`:

```csharp
    /// <summary>
    /// Every existing file for this model, first = <see cref="FoundAtPath"/>. Holds at most one
    /// entry unless <see cref="ConfigurationCheckOptions.CollectAllMatches"/> was set.
    /// </summary>
    public IReadOnlyList<string> AllFoundPaths { get; init; } = [];
```

`ConfigurationCheckerService.CheckSingleModel` — replace the body from `var searchedPaths` down to (not including) `var hasVramProfiles` with:

```csharp
        var searchedPaths = new List<string>();
        var found = new List<string>();

        foreach (var fileName in fileNames)
        {
            foreach (var candidate in CandidatePaths(configuration, model, repositoryPath, modelSearchPaths, options, fileName))
            {
                searchedPaths.Add(candidate);
                if (!File.Exists(candidate))
                    continue;

                var full = Path.GetFullPath(candidate);
                if (!found.Contains(full, StringComparer.OrdinalIgnoreCase))
                    found.Add(full);

                if (!options.CollectAllMatches)
                    break;
            }

            if (found.Count > 0 && !options.CollectAllMatches)
                break;
        }

        var foundPath = found.Count > 0 ? found[0] : string.Empty;
```

add `AllFoundPaths = found,` to the returned `ModelCheckResult`, and add the helper below `CheckSingleModel`:

```csharp
    /// <summary>
    /// Where a file named <paramref name="fileName"/> may sit, in search order: the model's explicit
    /// destination, then each search folder and its direct subfolders.
    /// </summary>
    private static IEnumerable<string> CandidatePaths(
        InstallationConfiguration configuration,
        ModelDownload model,
        string repositoryPath,
        List<string> modelSearchPaths,
        ConfigurationCheckOptions options,
        string fileName)
    {
        if (!string.IsNullOrWhiteSpace(model.Destination))
        {
            var destinationDir = ModelDestinationResolver.Resolve(
                configuration, model, repositoryPath, options.ModelBaseFolder, options.FolderPathOverrides);
            yield return Path.Combine(destinationDir, fileName);
        }

        foreach (var basePath in modelSearchPaths)
        {
            yield return Path.Combine(basePath, fileName);

            if (!Directory.Exists(basePath))
                continue;

            List<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(basePath).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                continue; // Skip inaccessible directories
            }

            foreach (var subDir in subDirs)
                yield return Path.Combine(subDir, fileName);
        }
    }
```

Note: `FoundAtPath` is now a full path (`Path.GetFullPath`); every path the checker builds is already rooted, so existing callers see the same string.

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~ConfigurationChecker|FullyQualifiedName~EngineFeatureBackend|FullyQualifiedName~Workloads"`. Expected: all PASS.

- [ ] **Step 5: Commit and push**

```bash
git add -A DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "feat(checker): report every copy of a model on request" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 5: Plan what a removal deletes, and say it

**Files:**
- Create: `DiffusionNexus.UI/Services/Engine/EngineFeatureChecks.cs`
- Create: `DiffusionNexus.UI/Services/Engine/EngineRemovalPlanner.cs`
- Create: `DiffusionNexus.UI/Services/Engine/EngineRemovalMessages.cs`
- Test: `DiffusionNexus.Tests/Engine/EngineRemovalPlannerTests.cs`, `DiffusionNexus.Tests/Engine/EngineRemovalMessagesTests.cs`

**Interfaces:**
- Consumes: `ModelCheckResult.AllFoundPaths`, `ConfigurationCheckOptions.CollectAllMatches` (Task 4); `FolderPathMatch` (existing).
- Produces:

```csharp
public sealed record EngineFeatureCheck(EngineFeature Feature, string DisplayName, IReadOnlyList<ConfigurationCheckResult> Results) { public bool IsPresent { get; } }
public static class EngineFeatureChecks { public static Task<IReadOnlyList<EngineFeatureCheck>> LoadAsync(ICatalog, IConfigurationCheckerService, string engineRoot, CancellationToken); }
public sealed record PlannedFile(string Path, long Bytes);
public sealed record KeptItem(string Name, string Path, string NeededBy);
public sealed record EngineRemovalPlan(IReadOnlyList<string> NodePackFolders, IReadOnlyList<PlannedFile> EngineModels, IReadOnlyList<PlannedFile> OutsideModels, IReadOnlyList<KeptItem> Kept) { long EngineBytes; bool HasAnythingToDelete; }
public static class EngineRemovalPlanner {
    static EngineRemovalPlan PlanFeatureRemoval(string engineRoot, IReadOnlyList<EngineFeatureCheck> checks, EngineFeature feature, Func<string, long>? sizeOf = null);
    static IReadOnlyList<PlannedFile> PlanUninstallOutsideModels(string engineRoot, IReadOnlyList<EngineFeatureCheck> checks, Func<string, long>? sizeOf = null);
    static long FolderSize(string folder);
}
public enum RemovalChoice { Cancel, EngineOnly, Everything }
public static class EngineRemovalMessages { /* see Step 3 */ }
```

- [ ] **Step 1: Write the failing tests.** `EngineRemovalPlannerTests.cs`:

```csharp
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class EngineRemovalPlannerTests
{
    private const string Root = @"C:\Engine\ComfyUI";
    private static long Size(string _) => 1000;

    private static ConfigurationCheckResult Result(IEnumerable<(string Path, bool Installed)> nodes, IEnumerable<string[]> models) => new()
    {
        OverallStatus = ConfigurationStatus.Partial,
        CustomNodesStatus = ConfigurationStatus.Partial,
        ModelsStatus = ConfigurationStatus.Partial,
        InstallationType = ComfyUIInstallationType.Manual,
        CustomNodeResults = nodes.Select(n => new CustomNodeCheckResult
        {
            Id = Guid.NewGuid(), Name = Path.GetFileName(n.Path), Url = "u", IsInstalled = n.Installed, ExpectedPath = n.Path
        }).ToList(),
        ModelResults = models.Select(paths => new ModelCheckResult
        {
            Id = Guid.NewGuid(), Name = paths.Length > 0 ? Path.GetFileName(paths[0]) : "missing",
            IsInstalled = paths.Length > 0, SearchedPaths = [],
            FoundAtPath = paths.FirstOrDefault() ?? "", AllFoundPaths = paths
        }).ToList()
    };

    private static EngineFeatureCheck Check(EngineFeature f, string name, ConfigurationCheckResult r) => new(f, name, [r]);

    private const string Gguf = Root + @"\custom_nodes\ComfyUI-GGUF";
    private const string Usdu = Root + @"\custom_nodes\ComfyUI_UltimateSDUpscale";
    private const string EngineVae = Root + @"\models\vae\qwen_image_vae.safetensors";
    private const string EngineUnet = Root + @"\models\diffusion_models\qwen-image-Q4_K_S.gguf";
    private const string UserAe = @"D:\Models\vae\ae.safetensors";
    private const string EngineUpscaler = Root + @"\models\upscale_models\4x-UltraSharp.pth";

    [Fact]
    public void Feature_SharedNodePackAndModel_AreKept_AndNamed()
    {
        var checks = new[]
        {
            Check(EngineFeature.InpaintOutpaint, "Inpaint & Outpaint", Result([(Gguf, true)], [[EngineVae], [EngineUnet]])),
            Check(EngineFeature.OutpaintVision, "Outpaint Vision", Result([(Gguf, true)], [[EngineVae]])),
        };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.InpaintOutpaint, Size);

        plan.NodePackFolders.Should().BeEmpty();
        plan.EngineModels.Select(f => f.Path).Should().Equal(EngineUnet);
        plan.Kept.Should().BeEquivalentTo([
            new KeptItem("ComfyUI-GGUF", Gguf, "Outpaint Vision"),
            new KeptItem("qwen_image_vae.safetensors", EngineVae, "Outpaint Vision")]);
    }

    [Fact]
    public void Feature_SharedOnlyWithAFeatureThatIsNotInstalled_IsDeleted()
    {
        var checks = new[]
        {
            Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([(Usdu, true)], [[EngineUpscaler]])),
            Check(EngineFeature.Canvas, "Canvas · Krea 2 Turbo", Result([(Usdu, false)], [Array.Empty<string>()])),
        };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.BatchUpscale, Size);

        plan.NodePackFolders.Should().Equal(Usdu);
        plan.EngineModels.Select(f => f.Path).Should().Equal(EngineUpscaler);
        plan.Kept.Should().BeEmpty();
    }

    [Fact]
    public void Feature_EveryCopyCounts_InsideAndOutsideTheEngine()
    {
        var engineAe = Root + @"\models\vae\ae.safetensors";
        var checks = new[] { Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([], [[engineAe, UserAe]])) };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.BatchUpscale, Size);

        plan.EngineModels.Select(f => f.Path).Should().Equal(engineAe);
        plan.OutsideModels.Should().Equal(new PlannedFile(UserAe, 1000));
    }

    [Fact]
    public void Feature_AFolderNamedLikeTheEngineButBesideIt_IsOutside()
    {
        var sibling = @"C:\Engine\ComfyUI-Backup\models\vae\ae.safetensors";
        var checks = new[] { Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([], [[sibling]])) };

        EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.BatchUpscale, Size)
            .OutsideModels.Select(f => f.Path).Should().Equal(sibling);
    }

    [Fact]
    public void Uninstall_ListsOutsideModelsOfPresentFeaturesOnly()
    {
        var checks = new[]
        {
            Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([(Usdu, true)], [[EngineUpscaler, UserAe]])),
            Check(EngineFeature.Canvas, "Canvas · Krea 2 Turbo", Result([], [Array.Empty<string>()])),
        };

        EngineRemovalPlanner.PlanUninstallOutsideModels(Root, checks, Size)
            .Should().Equal(new PlannedFile(UserAe, 1000));
    }

    [Fact]
    public void Uninstall_AFileTwoFeaturesUse_IsListedOnce()
    {
        var checks = new[]
        {
            Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([], [[UserAe]])),
            Check(EngineFeature.BatchUpscaleVision, "Batch Upscale Vision", Result([], [[UserAe]])),
        };

        EngineRemovalPlanner.PlanUninstallOutsideModels(Root, checks, Size).Should().HaveCount(1);
    }
}
```

`EngineRemovalMessagesTests.cs`:

```csharp
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class EngineRemovalMessagesTests
{
    [Theory]
    [InlineData(0, false, RemovalChoice.Cancel)]
    [InlineData(1, false, RemovalChoice.EngineOnly)]
    [InlineData(-1, false, RemovalChoice.Cancel)]
    [InlineData(0, true, RemovalChoice.Cancel)]
    [InlineData(1, true, RemovalChoice.Everything)]
    [InlineData(2, true, RemovalChoice.EngineOnly)]
    [InlineData(-1, true, RemovalChoice.Cancel)]
    public void ToChoice_MapsTheButtonIndex(int index, bool hasOutside, RemovalChoice expected) =>
        EngineRemovalMessages.ToChoice(index, hasOutside).Should().Be(expected);

    [Fact]
    public void FeatureOptions_WithOutsideModels_PutTheSafeChoiceLast()
    {
        EngineRemovalMessages.FeatureOptions(hasOutside: true).Should().Equal(
            "Cancel", "Delete all models of this feature, wherever they are", "Delete only what's in the Diffusion Nexus Engine");
        EngineRemovalMessages.FeatureOptions(hasOutside: false).Should().Equal("Cancel", "Remove");
    }

    [Fact]
    public void UninstallOptions_WithOutsideModels_PutTheSafeChoiceLast()
    {
        EngineRemovalMessages.UninstallOptions(hasOutside: true).Should().Equal(
            "Cancel", "Delete these models too", "Delete only what's in the Diffusion Nexus Engine");
        EngineRemovalMessages.UninstallOptions(hasOutside: false).Should().Equal("Cancel", "Uninstall");
    }

    [Fact]
    public void FeatureRemoval_NamesCountsSizeKeptItemsAndOutsideFiles()
    {
        var plan = new EngineRemovalPlan(
            [@"C:\E\custom_nodes\ComfyUI-GGUF"],
            [new PlannedFile(@"C:\E\models\unet\a.gguf", 2L * 1024 * 1024 * 1024)],
            [new PlannedFile(@"D:\Models\vae\ae.safetensors", 300L * 1024 * 1024)],
            [new KeptItem("qwen_image_vae.safetensors", @"C:\E\models\vae\qwen_image_vae.safetensors", "Outpaint Vision")]);

        var text = EngineRemovalMessages.FeatureRemoval("Inpaint & Outpaint", plan);

        text.Should().StartWith("Remove Inpaint & Outpaint?");
        text.Should().Contain("1 custom node pack and 1 model (2.0 GB) from the Diffusion Nexus Engine");
        text.Should().Contain("qwen_image_vae.safetensors (used by Outpaint Vision)");
        text.Should().Contain("Other programs (ComfyUI, A1111, …) may use them too");
        text.Should().Contain(@"D:\Models\vae\ae.safetensors (300 MB)");
        text.Should().Contain("Python packages");
    }

    [Fact]
    public void Uninstall_WithoutOutsideModels_SaysTheUsersFoldersAreNotTouched()
    {
        var text = EngineRemovalMessages.Uninstall(@"C:\E", 6L * 1024 * 1024 * 1024, []);

        text.Should().StartWith("Uninstall the Diffusion Nexus Engine?");
        text.Should().Contain(@"This deletes C:\E (6.0 GB)");
        text.Should().Contain("Your own model folders are not touched.");
    }
}
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~EngineRemoval"`. Expected: build FAILS.

- [ ] **Step 3: Implement.** `EngineFeatureChecks.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>One Engine feature as the disk check found it, every copy of every model included.</summary>
public sealed record EngineFeatureCheck(
    EngineFeature Feature,
    string DisplayName,
    IReadOnlyList<ConfigurationCheckResult> Results)
{
    /// <summary>Installed or Partial: at least one of its node packs or (non-placeholder) models is on disk.</summary>
    public bool IsPresent => Results.Any(r =>
        r.CustomNodeResults.Any(n => n.IsInstalled) || r.ModelResults.Any(m => m.IsInstalled && !m.IsPlaceholder));
}

/// <summary>Checks every Engine feature against the Engine folder, for removal planning.</summary>
public static class EngineFeatureChecks
{
    /// <summary>
    /// One check per feature in <see cref="EngineFeatureCatalog.All"/> order. A workload the catalog
    /// lacks, or a check that returns nothing, contributes nothing: its files are not known.
    /// </summary>
    public static async Task<IReadOnlyList<EngineFeatureCheck>> LoadAsync(
        ICatalog catalog, IConfigurationCheckerService checker, string engineRoot, CancellationToken ct)
    {
        var options = new ConfigurationCheckOptions { CollectAllMatches = true };
        var checks = new List<EngineFeatureCheck>();

        foreach (var definition in EngineFeatureCatalog.All)
        {
            var results = new List<ConfigurationCheckResult>();
            foreach (var workloadId in definition.WorkloadIds)
            {
                var config = await catalog.GetWorkloadAsync(workloadId, ct);
                if (config is null) continue;

                var result = await checker.CheckConfigurationAsync(config, engineRoot, options, ct);
                if (result is not null) results.Add(result);
            }

            checks.Add(new EngineFeatureCheck(definition.Feature, definition.DisplayName, results));
        }

        return checks;
    }
}
```

`EngineRemovalPlanner.cs`:

```csharp
namespace DiffusionNexus.UI.Services.Engine;

/// <summary>A file a removal would delete, with its size.</summary>
public sealed record PlannedFile(string Path, long Bytes);

/// <summary>An item a removal keeps because another installed feature still uses it.</summary>
public sealed record KeptItem(string Name, string Path, string NeededBy);

/// <summary>What removing one feature deletes, keeps, and asks about.</summary>
public sealed record EngineRemovalPlan(
    IReadOnlyList<string> NodePackFolders,
    IReadOnlyList<PlannedFile> EngineModels,
    IReadOnlyList<PlannedFile> OutsideModels,
    IReadOnlyList<KeptItem> Kept)
{
    public long EngineBytes => EngineModels.Sum(f => f.Bytes);

    public bool HasAnythingToDelete => NodePackFolders.Count > 0 || EngineModels.Count > 0 || OutsideModels.Count > 0;
}

/// <summary>
/// Turns disk checks into removal plans. A file inside the Engine folder is one the Engine downloaded
/// (feature downloads land in its models folder); anything else is in one of the user's own folders.
/// A node pack or model that another present feature also uses is kept, matched by full path.
/// </summary>
public static class EngineRemovalPlanner
{
    public static EngineRemovalPlan PlanFeatureRemoval(
        string engineRoot, IReadOnlyList<EngineFeatureCheck> checks, EngineFeature feature, Func<string, long>? sizeOf = null)
    {
        sizeOf ??= FileSize;
        var target = checks.Single(c => c.Feature == feature);
        var others = checks.Where(c => c.Feature != feature && c.IsPresent).ToList();

        var nodeOwners = Owners(others, c => c.Results
            .SelectMany(r => r.CustomNodeResults).Where(n => n.IsInstalled).Select(n => n.ExpectedPath));
        var modelOwners = Owners(others, c => c.Results
            .SelectMany(r => r.ModelResults).Where(m => m.IsInstalled).SelectMany(m => m.AllFoundPaths));

        var nodePacks = new List<string>();
        var engineModels = new List<PlannedFile>();
        var outside = new List<PlannedFile>();
        var kept = new List<KeptItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in target.Results.SelectMany(r => r.CustomNodeResults).Where(n => n.IsInstalled))
        {
            if (!seen.Add(Key(node.ExpectedPath))) continue;
            if (nodeOwners.TryGetValue(Key(node.ExpectedPath), out var owner))
                kept.Add(new KeptItem(node.Name, node.ExpectedPath, owner));
            else
                nodePacks.Add(node.ExpectedPath);
        }

        foreach (var path in target.Results.SelectMany(r => r.ModelResults)
                     .Where(m => m.IsInstalled && !m.IsPlaceholder).SelectMany(m => m.AllFoundPaths))
        {
            if (!seen.Add(Key(path))) continue;
            if (modelOwners.TryGetValue(Key(path), out var owner))
                kept.Add(new KeptItem(System.IO.Path.GetFileName(path), path, owner));
            else if (FolderPathMatch.Contains(engineRoot, path))
                engineModels.Add(new PlannedFile(path, sizeOf(path)));
            else
                outside.Add(new PlannedFile(path, sizeOf(path)));
        }

        return new EngineRemovalPlan(nodePacks, engineModels, outside, kept);
    }

    /// <summary>Models of present features that sit outside the Engine folder, each once.</summary>
    public static IReadOnlyList<PlannedFile> PlanUninstallOutsideModels(
        string engineRoot, IReadOnlyList<EngineFeatureCheck> checks, Func<string, long>? sizeOf = null)
    {
        sizeOf ??= FileSize;
        return checks.Where(c => c.IsPresent)
            .SelectMany(c => c.Results).SelectMany(r => r.ModelResults)
            .Where(m => m.IsInstalled && !m.IsPlaceholder)
            .SelectMany(m => m.AllFoundPaths)
            .Where(p => !FolderPathMatch.Contains(engineRoot, p))
            .DistinctBy(Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new PlannedFile(p, sizeOf(p)))
            .ToList();
    }

    /// <summary>Total size of the files under <paramref name="folder"/>, not following junctions; 0 when it is gone.</summary>
    public static long FolderSize(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
        };
        return new DirectoryInfo(folder).EnumerateFiles("*", options).Sum(f => f.Length);
    }

    private static Dictionary<string, string> Owners(
        IEnumerable<EngineFeatureCheck> features, Func<EngineFeatureCheck, IEnumerable<string>> paths)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in features)
            foreach (var path in paths(feature))
                owners.TryAdd(Key(path), feature.DisplayName);
        return owners;
    }

    private static string Key(string path) => FolderPathMatch.Normalize(path) ?? path;

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception) { return 0; }
    }
}
```

`EngineRemovalMessages.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>The user's answer to a removal question.</summary>
public enum RemovalChoice { Cancel, EngineOnly, Everything }

/// <summary>
/// Text and buttons for removing a feature and uninstalling the Engine, shown through
/// <c>IDialogService.ShowOptionsAsync</c>: Cancel first, the safe choice last (primary).
/// </summary>
public static class EngineRemovalMessages
{
    public const string FeatureTitle = "Remove Engine feature";
    public const string UninstallTitle = "Uninstall the Diffusion Nexus Engine";
    private const string Cancel = "Cancel";
    private const string EngineOnly = "Delete only what's in the Diffusion Nexus Engine";

    public static string[] FeatureOptions(bool hasOutside) => hasOutside
        ? [Cancel, "Delete all models of this feature, wherever they are", EngineOnly]
        : [Cancel, "Remove"];

    public static string[] UninstallOptions(bool hasOutside) => hasOutside
        ? [Cancel, "Delete these models too", EngineOnly]
        : [Cancel, "Uninstall"];

    /// <summary>Maps the dialog's button index (-1 = closed) back to a choice.</summary>
    public static RemovalChoice ToChoice(int index, bool hasOutside) => (hasOutside, index) switch
    {
        (false, 1) => RemovalChoice.EngineOnly,
        (true, 1) => RemovalChoice.Everything,
        (true, 2) => RemovalChoice.EngineOnly,
        _ => RemovalChoice.Cancel
    };

    public static string FeatureRemoval(string featureName, EngineRemovalPlan plan)
    {
        var text = new StringBuilder($"Remove {featureName}?");
        text.Append($"\n\nThis deletes {Count(plan.NodePackFolders.Count, "custom node pack")} and " +
                    $"{Count(plan.EngineModels.Count, "model")} ({FormatSize(plan.EngineBytes)}) from the Diffusion Nexus Engine.");

        if (plan.Kept.Count > 0)
        {
            text.Append("\n\nKept, because another installed feature needs it:");
            foreach (var item in plan.Kept)
                text.Append($"\n• {item.Name} (used by {item.NeededBy})");
        }

        AppendOutside(text, "Some of this feature's models are in your own model folders.", plan.OutsideModels);
        text.Append("\n\nPython packages the feature added stay in the Engine's environment; Uninstall Engine removes them.");
        return text.ToString();
    }

    public static string NothingToRemove(string featureName, EngineRemovalPlan plan)
    {
        var text = new StringBuilder($"Nothing of {featureName} can be deleted: every file it uses is needed by another installed feature.");
        foreach (var item in plan.Kept)
            text.Append($"\n• {item.Name} (used by {item.NeededBy})");
        return text.ToString();
    }

    public static string Uninstall(string folder, long folderBytes, IReadOnlyList<PlannedFile> outside)
    {
        var text = new StringBuilder("Uninstall the Diffusion Nexus Engine?");
        text.Append($"\n\nThis deletes {folder} ({FormatSize(folderBytes)}): the Engine, its features and the models it downloaded.");
        if (outside.Count == 0)
            text.Append(" Your own model folders are not touched.");
        AppendOutside(text, "Some models the Engine's features use are in your own model folders.", outside);
        return text.ToString();
    }

    public static string UninstallMissingFolder(string folder) =>
        $"Uninstall the Diffusion Nexus Engine?\n\nIts folder {folder} is already gone. The app forgets the Engine and its model folder in Settings.";

    public static string Busy(string reason) =>
        $"The Diffusion Nexus Engine is busy ({reason}). Wait until it finishes, then try again.";

    public static string Failures(string headline, IReadOnlyList<RemovalFailure> failures)
    {
        var text = new StringBuilder(headline);
        text.Append("\n\nCould not be deleted:");
        foreach (var failure in failures)
            text.Append($"\n• {failure.Path} — {failure.Reason}");
        return text.ToString();
    }

    public static string FormatSize(long bytes)
    {
        const double gb = 1024d * 1024 * 1024, mb = 1024d * 1024;
        return bytes >= gb
            ? (bytes / gb).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : Math.Round(bytes / mb).ToString(CultureInfo.InvariantCulture) + " MB";
    }

    private static void AppendOutside(StringBuilder text, string lead, IReadOnlyList<PlannedFile> outside)
    {
        if (outside.Count == 0) return;
        text.Append($"\n\n{lead} Other programs (ComfyUI, A1111, …) may use them too:");
        foreach (var file in outside)
            text.Append($"\n• {file.Path} ({FormatSize(file.Bytes)})");
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
```

`RemovalFailure` is defined in Task 6; to keep this task compiling on its own, define it here at the bottom of `EngineRemovalMessages.cs` instead:

```csharp
/// <summary>A path a removal could not delete, and why.</summary>
public sealed record RemovalFailure(string Path, string Reason);
```

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~EngineRemoval"`. Expected: all PASS.

- [ ] **Step 5: Commit and push**

```bash
git add -A DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "feat(engine): plan what removing a feature or the Engine deletes, and word the question" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 6: EngineRemover — stop, hold, delete

**Files:**
- Create: `DiffusionNexus.UI/Services/Engine/EngineRemover.cs`
- Test: `DiffusionNexus.Tests/Engine/EngineRemoverTests.cs`

**Interfaces:**
- Consumes: `IManagedEngineControl` (`IsRunning`, `IsStarting`, `HasActiveJobs`, `StopIfIdleAsync`, `TryBeginMaintenanceAsync`), `IInstallActivity.Running`, `RemovalFailure` (Task 5).
- Produces:

```csharp
public interface IRemovalFileSystem { void DeleteDirectory(string path); void DeleteFile(string path); }
public sealed record EngineRemovalResult(string? BusyReason, IReadOnlyList<RemovalFailure> Failed) { bool WasBusy; static Busy(string); }
public sealed class EngineRemover
{
    public EngineRemover(IManagedEngineControl? engine, IInstallActivity? installActivity, IUnifiedLogger? logger = null, IRemovalFileSystem? fileSystem = null);
    public string? BusyReason();
    public Task<EngineRemovalResult> RemoveAsync(IReadOnlyList<string> directories, IReadOnlyList<string> files);
}
```

- [ ] **Step 1: Write the failing tests** — `EngineRemoverTests.cs`:

```csharp
using System.Diagnostics;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public sealed class EngineRemoverTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("dn-engine-remover-");
    private readonly Mock<IManagedEngineControl> _engine = new();
    private readonly Mock<IDisposable> _hold = new();

    public EngineRemoverTests()
    {
        _engine.Setup(e => e.TryBeginMaintenanceAsync()).ReturnsAsync(_hold.Object);
        _engine.Setup(e => e.StopIfIdleAsync()).ReturnsAsync(true);
    }

    public void Dispose() { try { _temp.Delete(recursive: true); } catch { /* best effort */ } }

    private string Folder(string name)
    {
        var path = Path.Combine(_temp.FullName, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "f.txt"), "x");
        return path;
    }

    [Fact]
    public async Task AGenerationRunning_IsBusy_AndNothingIsDeleted()
    {
        var folder = Folder("Engine");
        _engine.SetupGet(e => e.HasActiveJobs).Returns(true);

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync([folder], []);

        result.WasBusy.Should().BeTrue();
        Directory.Exists(folder).Should().BeTrue();
        _engine.Verify(e => e.StopIfIdleAsync(), Times.Never);
    }

    [Fact]
    public async Task AnInstallRunning_IsBusy()
    {
        var activity = new InstallActivity();
        using var _ = activity.Begin("Diffusion Nexus Engine features");

        var result = await new EngineRemover(_engine.Object, activity).RemoveAsync([Folder("Engine")], []);

        result.BusyReason.Should().Contain("Diffusion Nexus Engine features");
    }

    [Fact]
    public async Task ARunningIdleEngine_IsStoppedFirst_ThenDeletedUnderTheHold()
    {
        var folder = Folder("Engine");
        _engine.SetupGet(e => e.IsRunning).Returns(true);
        var order = new List<string>();
        _engine.Setup(e => e.StopIfIdleAsync()).Callback(() => order.Add("stop")).ReturnsAsync(true);
        _engine.Setup(e => e.TryBeginMaintenanceAsync()).Callback(() => order.Add("hold")).ReturnsAsync(_hold.Object);

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync([folder], []);

        order.Should().Equal("stop", "hold");
        result.Failed.Should().BeEmpty();
        Directory.Exists(folder).Should().BeFalse();
        _hold.Verify(h => h.Dispose(), Times.Once);
    }

    // Review focus 4: the Engine started between the check and the hold.
    [Fact]
    public async Task AHoldThatIsRefused_IsBusy_AndNothingIsDeleted()
    {
        var folder = Folder("Engine");
        _engine.Setup(e => e.TryBeginMaintenanceAsync()).ReturnsAsync((IDisposable?)null);

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync([folder], []);

        result.WasBusy.Should().BeTrue();
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public async Task ALockedFile_IsReported_AndTheRestIsDeleted()
    {
        var a = Path.Combine(_temp.FullName, "a.bin");
        var b = Path.Combine(_temp.FullName, "b.bin");
        File.WriteAllText(a, "x");
        File.WriteAllText(b, "x");
        using var locked = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync([], [a, b]);

        result.Failed.Should().ContainSingle().Which.Path.Should().Be(a);
        File.Exists(b).Should().BeFalse();
    }

    // Review focus 2: a corrupted row must never delete a drive.
    [Fact]
    public async Task ADriveRoot_IsRefused()
    {
        var fs = new Mock<IRemovalFileSystem>();

        var result = await new EngineRemover(_engine.Object, null, fileSystem: fs.Object).RemoveAsync([@"D:\"], []);

        result.Failed.Should().ContainSingle().Which.Reason.Should().Contain("drive");
        fs.Verify(f => f.DeleteDirectory(It.IsAny<string>()), Times.Never);
    }

    // Review focus 1: a junction inside the Engine pointing at the user's library loses the link only.
    [Fact]
    public async Task AJunctionInsideTheFolder_IsRemoved_ItsTargetIsKept()
    {
        var engine = Folder("Engine");
        var library = Folder("Library");
        var link = Path.Combine(engine, "loras");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{library}\"")
            { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
        Directory.Exists(link).Should().BeTrue("the test needs the junction");

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync([engine], []);

        result.Failed.Should().BeEmpty();
        Directory.Exists(engine).Should().BeFalse();
        File.Exists(Path.Combine(library, "f.txt")).Should().BeTrue("the user's files behind the junction stay");
    }

    [Fact]
    public async Task WithoutAnEngine_ItStillDeletes()
    {
        var folder = Folder("Engine");

        var result = await new EngineRemover(null, null).RemoveAsync([folder], []);

        result.WasBusy.Should().BeFalse();
        Directory.Exists(folder).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~EngineRemoverTests"`. Expected: build FAILS.

- [ ] **Step 3: Implement** `EngineRemover.cs`:

```csharp
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Serilog;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>Deletes for <see cref="EngineRemover"/>; a seam so tests can simulate failures.</summary>
public interface IRemovalFileSystem
{
    void DeleteDirectory(string path);
    void DeleteFile(string path);
}

/// <summary>
/// Deletes without following junctions or symlinks: a link inside the Engine that points at the
/// user's library is removed, the files behind it stay. Read-only flags (git packs) are cleared first.
/// </summary>
public sealed class RemovalFileSystem : IRemovalFileSystem
{
    public void DeleteDirectory(string path)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            file.Attributes = FileAttributes.Normal;

        // .NET removes a junction or symlink found during a recursive delete without entering it.
        Directory.Delete(path, recursive: true);
    }

    public void DeleteFile(string path)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }
}

/// <summary>Outcome of <see cref="EngineRemover.RemoveAsync"/>.</summary>
public sealed record EngineRemovalResult(string? BusyReason, IReadOnlyList<RemovalFailure> Failed)
{
    public bool WasBusy => BusyReason is not null;

    public static EngineRemovalResult Busy(string reason) => new(reason, []);
}

/// <summary>
/// Removes Engine files safely: refuses while a generation or an install runs, stops an idle Engine,
/// and deletes under the Engine's maintenance hold so nothing starts it from a half-deleted folder.
/// </summary>
public sealed class EngineRemover
{
    private const string LogSource = "Diffusion Nexus Engine";
    private static readonly ILogger Logger = Log.ForContext<EngineRemover>();

    private readonly IManagedEngineControl? _engine;
    private readonly IInstallActivity? _installActivity;
    private readonly IUnifiedLogger? _logger;
    private readonly IRemovalFileSystem _fileSystem;

    public EngineRemover(
        IManagedEngineControl? engine,
        IInstallActivity? installActivity,
        IUnifiedLogger? logger = null,
        IRemovalFileSystem? fileSystem = null)
    {
        _engine = engine;
        _installActivity = installActivity;
        _logger = logger;
        _fileSystem = fileSystem ?? new RemovalFileSystem();
    }

    /// <summary>Why nothing may be removed right now, or null.</summary>
    public string? BusyReason()
    {
        if (_installActivity?.Running is { } running) return $"{running} is being installed";
        if (_engine is { HasActiveJobs: true }) return "a generation is running on it";
        return null;
    }

    /// <summary>
    /// Deletes <paramref name="directories"/> then <paramref name="files"/>. Missing paths are skipped;
    /// each path that fails is reported and the rest still goes. Drive roots are never deleted.
    /// </summary>
    public async Task<EngineRemovalResult> RemoveAsync(IReadOnlyList<string> directories, IReadOnlyList<string> files)
    {
        if (BusyReason() is { } busy) return Refuse(busy);

        IDisposable? hold = null;
        if (_engine is not null)
        {
            if ((_engine.IsRunning || _engine.IsStarting) && !await _engine.StopIfIdleAsync())
                return Refuse("a generation is running on it");
            Info("Engine stopped for the removal.");

            hold = await _engine.TryBeginMaintenanceAsync();
            if (hold is null) return Refuse("it is starting");
        }

        using (hold)
        {
            var failed = await Task.Run(() => Delete(directories, files));
            Info(failed.Count == 0
                ? $"Removal finished: {directories.Count} folder(s) and {files.Count} file(s)."
                : $"Removal finished with {failed.Count} item(s) not deleted.");
            return new EngineRemovalResult(null, failed);
        }
    }

    private List<RemovalFailure> Delete(IReadOnlyList<string> directories, IReadOnlyList<string> files)
    {
        var failed = new List<RemovalFailure>();

        foreach (var directory in directories)
        {
            if (IsDriveRoot(directory))
            {
                failed.Add(new RemovalFailure(directory, "a drive is never deleted"));
                Warn($"Refused to delete {directory}: it is a drive.");
                continue;
            }

            if (!Directory.Exists(directory)) continue;
            Try(directory, () => _fileSystem.DeleteDirectory(directory), failed);
        }

        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            Try(file, () => _fileSystem.DeleteFile(file), failed);
        }

        return failed;
    }

    private void Try(string path, Action delete, List<RemovalFailure> failed)
    {
        try
        {
            delete();
            Info($"Deleted {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed.Add(new RemovalFailure(path, ex.Message));
            Warn($"Could not delete {path}: {ex.Message}");
        }
    }

    private static bool IsDriveRoot(string path)
    {
        var full = FolderPathMatch.Normalize(path);
        return full is null || string.Equals(Path.GetPathRoot(full)?.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private EngineRemovalResult Refuse(string reason)
    {
        Info($"Removal refused: the Engine is busy ({reason}). Nothing was changed.");
        return EngineRemovalResult.Busy(reason);
    }

    private void Info(string message)
    {
        Logger.Information("Engine removal: {Message}", message);
        _logger?.Info(LogCategory.Installation, LogSource, message);
    }

    private void Warn(string message)
    {
        Logger.Warning("Engine removal: {Message}", message);
        _logger?.Warn(LogCategory.Installation, LogSource, message);
    }
}
```

If `AJunctionInsideTheFolder_IsRemoved_ItsTargetIsKept` fails because `Directory.Delete` enters the junction on this runtime, replace the last line of `DeleteDirectory` with an explicit walk: for each entry, a directory with `FileAttributes.ReparsePoint` → `Directory.Delete(entry, recursive: false)`; a plain directory → recurse; a file → delete; then delete the folder itself.

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~EngineRemoverTests"`. Expected: all PASS.

- [ ] **Step 5: Commit and push**

```bash
git add -A DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "feat(engine): EngineRemover stops an idle Engine and deletes under its maintenance hold" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 7: The Engine tile says why, and Open Folder works

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/InstallerPackageCardViewModel.cs` (properties ~95-106, 187-197, 255-281, events ~343-378, commands 546-578)
- Modify: `DiffusionNexus.UI/Views/InstallerManagerView.axaml:234-250,286-290`
- Modify: `DiffusionNexus.UI/ViewModels/InstallerManagerViewModel.cs:1036-1042` (`CreateEngineCard`)
- Test: `DiffusionNexus.Tests/InstallerManager/EngineTileMenuTests.cs` (new)

**Interfaces:**
- Produces on the card: `SettingsTooltip`, `MakeDefaultTooltip`, `OpenFolderTooltip` (string?), `ShowRemoveMenuItem`, `ShowEngineUninstallMenuItem` (bool), `UninstallEngineCommand`, event `Func<InstallerPackageCardViewModel, Task>? EngineUninstallRequested`. `OpenSettingsCommand`, `MakeDefaultCommand`, `OpenFolderCommand` get a CanExecute.

- [ ] **Step 1: Write the failing tests** — `EngineTileMenuTests.cs`:

```csharp
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>Every item on the Engine and Core tiles either works or says why it can't.</summary>
public class EngineTileMenuTests
{
    private static InstallerPackage EngineRow(string path) => new()
    {
        Id = 7, Name = "Diffusion Nexus Engine", InstallationPath = path, Type = InstallerType.ComfyUI, IsAppManaged = true
    };

    [Fact]
    public void Engine_SettingsAndMakeDefault_AreGreyed_WithTheirReasons()
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(EngineRow(@"C:\Nowhere\Engine"));

        card.OpenSettingsCommand.CanExecute(null).Should().BeFalse();
        card.SettingsTooltip.Should().Be(
            "The app manages the Diffusion Nexus Engine's settings. To install it somewhere else, uninstall it and install it again.");
        card.MakeDefaultCommand.CanExecute(null).Should().BeFalse();
        card.MakeDefaultTooltip.Should().Be(
            "The Engine runs in the background for Diffusion Nexus features, so it can't be your default ComfyUI.");
        card.OpenFolderCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Engine_WithARow_OffersUninstall_NotRemoveOrDelete()
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(EngineRow(@"C:\Nowhere\Engine"));

        card.ShowEngineUninstallMenuItem.Should().BeTrue("a missing folder still has a row to forget");
        card.ShowRemoveMenuItem.Should().BeFalse();
        card.ShowDeleteFromDiskMenuItem.Should().BeFalse();
    }

    [Fact]
    public void Engine_WithoutARow_HasNothingToUninstall()
    {
        InstallerPackageCardViewModel.CreateEngineCard(null).ShowEngineUninstallMenuItem.Should().BeFalse();
    }

    [Fact]
    public void Core_SettingsOpenFolderAndMakeDefault_AreGreyed_WithReasons()
    {
        var card = InstallerPackageCardViewModel.CreateCoreCard();

        card.OpenSettingsCommand.CanExecute(null).Should().BeFalse();
        card.OpenFolderCommand.CanExecute(null).Should().BeFalse();
        card.MakeDefaultCommand.CanExecute(null).Should().BeFalse();
        card.SettingsTooltip.Should().Contain("built into the app");
        card.OpenFolderTooltip.Should().Contain("built into the app");
    }

    [Fact]
    public void OrdinaryCard_KeepsEveryItem()
    {
        var card = new InstallerPackageCardViewModel(new InstallerPackage
        {
            Id = 3, Name = "My ComfyUI", InstallationPath = @"C:\Comfy", Type = InstallerType.ComfyUI
        });

        card.OpenSettingsCommand.CanExecute(null).Should().BeTrue();
        card.SettingsTooltip.Should().Be("Settings");
        card.MakeDefaultCommand.CanExecute(null).Should().BeTrue();
        card.ShowRemoveMenuItem.Should().BeTrue();
        card.ShowEngineUninstallMenuItem.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~EngineTileMenuTests"`. Expected: build FAILS.

- [ ] **Step 3: Implement** in `InstallerPackageCardViewModel.cs`.

On `_type` and `_isEngine` add (keep existing attributes):

```csharp
    [NotifyPropertyChangedFor(nameof(SettingsTooltip))]
    [NotifyPropertyChangedFor(nameof(MakeDefaultTooltip))]
    [NotifyPropertyChangedFor(nameof(OpenFolderTooltip))]
    [NotifyPropertyChangedFor(nameof(ShowRemoveMenuItem))]
    [NotifyPropertyChangedFor(nameof(ShowDeleteFromDiskMenuItem))]
    [NotifyPropertyChangedFor(nameof(ShowEngineUninstallMenuItem))]
    [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(MakeDefaultCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
```

On `_isEngineInstalling` add `[NotifyPropertyChangedFor(nameof(ShowEngineUninstallMenuItem))]`.

In `ApplyEngineFolder`, after `_engineFolder = folder;`:

```csharp
        OnPropertyChanged(nameof(HasEngineRow));
        OnPropertyChanged(nameof(ShowEngineUninstallMenuItem));
```

Replace `ShowDeleteFromDiskMenuItem`, the `CanRemoveOrDelete` doc, `RemoveTooltip` and `DeleteFromDiskTooltip` with:

```csharp
    public bool ShowDeleteFromDiskMenuItem => !IsMissing && !IsEngine;

    /// <summary>Remove drops a row; the Engine is uninstalled with its own item instead.</summary>
    public bool ShowRemoveMenuItem => !IsEngine;

    /// <summary>
    /// Uninstall Engine: whenever the Engine has a row (installed, incomplete or missing folder), and not
    /// while it installs.
    /// </summary>
    public bool ShowEngineUninstallMenuItem => IsEngine && HasEngineRow && !IsEngineInstalling;

    /// <summary>
    /// The ordinary Remove and Delete from Disk never act on the Engine (it has Uninstall Engine) nor on
    /// Core (no row, nothing on disk).
    /// </summary>
    private bool CanRemoveOrDelete() => !IsEngine && !IsCore;

    /// <summary>Why Remove is greyed out; null where it is not.</summary>
    public string? RemoveTooltip => IsCore ? "This is built into the app, so there is nothing to remove." : null;

    /// <summary>Why Delete from Disk is greyed out; null where it is not.</summary>
    public string? DeleteFromDiskTooltip => IsCore ? "This is built into the app, so there is nothing on disk to delete." : null;

    private bool CanOpenSettings() => !IsEngine && !IsCore;

    /// <summary>The gear's and the menu item's tooltip: why Settings is greyed out, else "Settings".</summary>
    public string SettingsTooltip =>
        IsEngine ? "The app manages the Diffusion Nexus Engine's settings. To install it somewhere else, uninstall it and install it again."
        : IsCore ? "This is built into the app: it has no settings of its own."
        : "Settings";

    private bool CanMakeDefault() => !IsEngine && !IsCore;

    public string? MakeDefaultTooltip =>
        IsEngine ? "The Engine runs in the background for Diffusion Nexus features, so it can't be your default ComfyUI."
        : IsCore ? "This is built into the app, so it can't be a default installation."
        : null;

    private bool CanOpenFolder() => !IsCore;

    public string? OpenFolderTooltip => IsCore ? "This is built into the app, so there is no folder to open." : null;
```

Change the three command attributes:

```csharp
    [RelayCommand(CanExecute = nameof(CanOpenSettings))]
    private async Task OpenSettingsAsync() ...

    [RelayCommand(CanExecute = nameof(CanMakeDefault))]
    private async Task MakeDefaultAsync() ...

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder() ...
```

Add the event (next to `EngineInstallRequested`) and the command:

```csharp
    /// <summary>Raised when the user chose Uninstall Engine on the Engine tile.</summary>
    public event Func<InstallerPackageCardViewModel, Task>? EngineUninstallRequested;

    [RelayCommand]
    private async Task UninstallEngineAsync()
    {
        if (EngineUninstallRequested is not null)
            await EngineUninstallRequested.Invoke(this);
    }
```

`InstallerManagerView.axaml` kebab menu (lines 234-250) becomes:

```xml
<MenuItem Header="Settings"
          Command="{Binding OpenSettingsCommand}"
          ToolTip.Tip="{Binding SettingsTooltip}"
          ToolTip.ShowOnDisabled="True"/>
<MenuItem Header="Open Folder"
          Command="{Binding OpenFolderCommand}"
          ToolTip.Tip="{Binding OpenFolderTooltip}"
          ToolTip.ShowOnDisabled="True"/>
<MenuItem Header="Make Default"
          Command="{Binding MakeDefaultCommand}"
          ToolTip.Tip="{Binding MakeDefaultTooltip}"
          ToolTip.ShowOnDisabled="True"/>
<Separator/>
<MenuItem Header="Remove"
          Command="{Binding RemoveCommand}"
          IsVisible="{Binding ShowRemoveMenuItem}"
          ToolTip.Tip="{Binding RemoveTooltip}"
          ToolTip.ShowOnDisabled="True"/>
<MenuItem Header="Delete from Disk"
          Command="{Binding DeleteFromDiskCommand}"
          IsVisible="{Binding ShowDeleteFromDiskMenuItem}"
          ToolTip.Tip="{Binding DeleteFromDiskTooltip}"
          ToolTip.ShowOnDisabled="True"
          Foreground="#DC3545"/>
<MenuItem Header="Uninstall Engine…"
          Command="{Binding UninstallEngineCommand}"
          IsVisible="{Binding ShowEngineUninstallMenuItem}"
          Foreground="#DC3545"/>
```

The gear button (lines 286-290):

```xml
<Button Classes="icon-btn"
        Command="{Binding OpenSettingsCommand}"
        ToolTip.Tip="{Binding SettingsTooltip}"
        ToolTip.ShowOnDisabled="True">
```

`InstallerManagerViewModel.CreateEngineCard` — wire Open Folder (the uninstall handler arrives in Task 8):

```csharp
        card.OpenFolderRequested += OnOpenFolderRequested;
```

Update the summary of `CreateEngineCard` to "Wires install, Features, Open Folder and Uninstall Engine."

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~EngineTileMenuTests|FullyQualifiedName~InstallerManager"`. Expected: all PASS. Fix any existing test that asserted the old Engine `RemoveTooltip`/`DeleteFromDiskTooltip` text (search `can't be removed from this list` in `DiffusionNexus.Tests`) to assert `ShowRemoveMenuItem`/`ShowDeleteFromDiskMenuItem` false instead.

- [ ] **Step 5: Build the UI project** to catch XAML binding errors: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj`. Expected: 0 errors.

- [ ] **Step 6: Commit and push**

```bash
git add -A DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "feat(engine): the Engine tile greys out Settings and Make Default with reasons, opens its folder, offers Uninstall" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 8: Uninstall Engine

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/InstallerManagerViewModel.cs` (`CreateEngineCard`, new handler, new helpers next to `ShowEngineBusyForUpdateAsync`)
- Test: `DiffusionNexus.Tests/InstallerManager/EngineUninstallTests.cs` (new)

**Interfaces:**
- Consumes: `EngineRemover` (Task 6), `EngineFeatureChecks`, `EngineRemovalPlanner`, `EngineRemovalMessages`, `RemovalChoice` (Task 5), `card.EngineUninstallRequested` (Task 7).
- Produces: `internal Task InstallerManagerViewModel.UninstallEngineAsync(InstallerPackageCardViewModel card)` (handler, internal for tests).

- [ ] **Step 1: Write the failing tests** — `EngineUninstallTests.cs`:

```csharp
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

public sealed class EngineUninstallTests : IDisposable
{
    private readonly string _root;
    private readonly Mock<IDialogService> _dialog = new();
    private readonly Mock<IManagedEngineControl> _engine = new();
    private readonly Mock<IAppSettingsRepository> _settingsRepo = new();
    private readonly Mock<IDatasetEventAggregator> _events = new();
    private readonly AppSettings _settings;
    private readonly List<InstallerPackage> _removedRows = [];

    public EngineUninstallTests()
    {
        _root = Directory.CreateTempSubdirectory("dn-engine-uninstall-").FullName;
        File.WriteAllText(Path.Combine(_root, "main.py"), "");
        Directory.CreateDirectory(Path.Combine(_root, "models"));

        _engine.Setup(e => e.TryBeginMaintenanceAsync()).ReturnsAsync(new Mock<IDisposable>().Object);
        _engine.Setup(e => e.StopIfIdleAsync()).ReturnsAsync(true);

        _settings = new AppSettings
        {
            Id = 1,
            ComfyUiServerMode = ComfyUiServerMode.Engine,
            BaseModelFolders =
            [
                new BaseModelFolder { Id = 1, FolderPath = Path.Combine(_root, "models"), InstallerPackageId = 7 },
                new BaseModelFolder { Id = 2, FolderPath = @"D:\Models", InstallerPackageId = 7 } // re-linked by an old backfill
            ],
            ImageGalleries = [new ImageGallery { Id = 3, FolderPath = Path.Combine(_root, "output") }],
            LoraSources = []
        };
        _settingsRepo.Setup(r => r.GetSettingsWithIncludesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_settings);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private InstallerPackage Row() => new()
    {
        Id = 7, Name = "Diffusion Nexus Engine", InstallationPath = _root, Type = InstallerType.ComfyUI, IsAppManaged = true
    };

    private async Task<(InstallerManagerViewModel Vm, InstallerPackageCardViewModel Card)> CreateAsync()
    {
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [Row()], dialogMock: _dialog, engine: _engine.Object, appSettingsMock: _settingsRepo,
            eventAggregatorMock: _events, onPackageRemoved: p => _removedRows.Add(p));
        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        return (vm, vm.InstallerCards.Single(c => c.IsEngine));
    }

    private void Answer(int index) =>
        _dialog.Setup(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>())).ReturnsAsync(index);

    [Fact]
    public async Task Confirmed_DeletesTheFolder_ForgetsTheEngine_AndSwitchesTheServer()
    {
        Answer(1); // "Uninstall"
        var (vm, card) = await CreateAsync();

        await vm.UninstallEngineAsync(card);

        Directory.Exists(_root).Should().BeFalse();
        _removedRows.Should().ContainSingle().Which.Id.Should().Be(7);
        _settingsRepo.Verify(r => r.RemoveBaseModelFolder(It.Is<BaseModelFolder>(f => f.Id == 1)), Times.Once);
        _settingsRepo.Verify(r => r.RemoveBaseModelFolder(It.Is<BaseModelFolder>(f => f.Id == 2)), Times.Never,
            "D:\\Models is the user's, whatever row it is linked to");
        _settingsRepo.Verify(r => r.RemoveImageGallery(It.Is<ImageGallery>(g => g.Id == 3)), Times.Once);
        _settings.ComfyUiServerMode.Should().Be(ComfyUiServerMode.CustomUrl);
        vm.InstallerCards.Single(c => c.IsEngine).ShowEngineInstallButton.Should().BeTrue();
        _events.Verify(e => e.PublishEngineChanged(It.IsAny<EngineChangedEventArgs>()), Times.Once);
        _events.Verify(e => e.PublishSettingsSaved(It.IsAny<SettingsSavedEventArgs>()), Times.Once);
    }

    [Fact]
    public async Task Cancelled_ChangesNothing()
    {
        Answer(0);
        var (vm, card) = await CreateAsync();

        await vm.UninstallEngineAsync(card);

        Directory.Exists(_root).Should().BeTrue();
        _removedRows.Should().BeEmpty();
        _engine.Verify(e => e.StopIfIdleAsync(), Times.Never);
    }

    [Fact]
    public async Task Busy_SaysWhy_AndAsksNothing()
    {
        _engine.SetupGet(e => e.HasActiveJobs).Returns(true);
        var (vm, card) = await CreateAsync();

        await vm.UninstallEngineAsync(card);

        _dialog.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("a generation is running"))), Times.Once);
        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>()), Times.Never);
        Directory.Exists(_root).Should().BeTrue();
    }

    [Fact]
    public async Task MissingFolder_StillForgetsTheEngine()
    {
        Directory.Delete(_root, recursive: true);
        Answer(1);
        var (vm, card) = await CreateAsync();

        await vm.UninstallEngineAsync(card);

        _removedRows.Should().ContainSingle();
        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("already gone")), It.IsAny<string[]>()), Times.Once);
    }

    [Fact]
    public async Task ServerOnCustomUrl_StaysThere()
    {
        _settings.ComfyUiServerMode = ComfyUiServerMode.CustomUrl;
        Answer(1);
        var (vm, card) = await CreateAsync();

        await vm.UninstallEngineAsync(card);

        _settings.ComfyUiServerMode.Should().Be(ComfyUiServerMode.CustomUrl);
    }

    // Review focus 3: a second click while the first uninstall waits on its dialog does nothing.
    [Fact]
    public async Task ASecondUninstall_WhileTheFirstRuns_DoesNothing()
    {
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialog.Setup(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>())).Returns(gate.Task);
        var (vm, card) = await CreateAsync();

        var first = vm.UninstallEngineAsync(card);
        await vm.UninstallEngineAsync(card);
        gate.SetResult(0);
        await first;

        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>()), Times.Once);
    }
}
```

`EngineChangedEventArgs`, `SettingsSavedEventArgs`, `ComfyUiServerMode` usings: add `using DiffusionNexus.Domain.Enums;` (already) and the namespace of the event args (`DiffusionNexus.UI.Services`, check with Grep for `class EngineChangedEventArgs`).

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~EngineUninstallTests"`. Expected: build FAILS (`UninstallEngineAsync` missing).

- [ ] **Step 3: Implement** in `InstallerManagerViewModel.cs`.

In `CreateEngineCard`:

```csharp
        card.EngineUninstallRequested += UninstallEngineAsync;
```

New members (place after `OnEngineInstallRequestedAsync`):

```csharp
    // One uninstall at a time: a second click while the first asks or deletes does nothing.
    private bool _engineUninstallRunning;

    private Services.Engine.EngineRemover CreateEngineRemover() =>
        new(_engine as Services.Engine.IManagedEngineControl, _installActivity, _unifiedLogger);

    /// <summary>
    /// Uninstall Engine: refuses while busy, shows what goes (and the models in the user's own folders,
    /// with the choice to delete those too), stops the Engine, deletes its folder, then forgets it:
    /// settings folders inside it (by path), its row, and the Engine as ComfyUI Server.
    /// </summary>
    internal async Task UninstallEngineAsync(InstallerPackageCardViewModel card)
    {
        if (_engineUninstallRunning) return;
        _engineUninstallRunning = true;
        try
        {
            var remover = CreateEngineRemover();
            if (remover.BusyReason() is { } busy)
            {
                await _dialogService.ShowMessageAsync(Services.Engine.EngineRemovalMessages.UninstallTitle,
                    Services.Engine.EngineRemovalMessages.Busy(busy));
                return;
            }

            var root = card.InstallationPath;
            var folder = await Task.Run(() => Services.Engine.ManagedEngineLocator.Probe(root));
            IReadOnlyList<Services.Engine.PlannedFile> outside = [];
            long folderBytes = 0;
            if (folder != Services.Engine.EngineFolderState.Missing)
            {
                var checks = folder == Services.Engine.EngineFolderState.Installed
                    ? await Services.Engine.EngineFeatureChecks.LoadAsync(_catalog, _checkerService, root, CancellationToken.None)
                    : [];
                outside = await Task.Run(() => Services.Engine.EngineRemovalPlanner.PlanUninstallOutsideModels(root, checks));
                folderBytes = await Task.Run(() => Services.Engine.EngineRemovalPlanner.FolderSize(root));
            }

            _unifiedLogger.Info(LogCategory.Installation, EngineLogSource,
                $"Uninstall planned: {root} ({Services.Engine.EngineRemovalMessages.FormatSize(folderBytes)}, {folder}); " +
                $"{outside.Count} model(s) of its features sit in your own folders.");

            var hasOutside = outside.Count > 0;
            var message = folder == Services.Engine.EngineFolderState.Missing
                ? Services.Engine.EngineRemovalMessages.UninstallMissingFolder(root)
                : Services.Engine.EngineRemovalMessages.Uninstall(root, folderBytes, outside);
            var choice = Services.Engine.EngineRemovalMessages.ToChoice(
                await _dialogService.ShowOptionsAsync(Services.Engine.EngineRemovalMessages.UninstallTitle, message,
                    Services.Engine.EngineRemovalMessages.UninstallOptions(hasOutside)),
                hasOutside);
            if (choice == Services.Engine.RemovalChoice.Cancel)
            {
                _unifiedLogger.Info(LogCategory.Installation, EngineLogSource, "Uninstall cancelled. Nothing was changed.");
                return;
            }

            _unifiedLogger.Info(LogCategory.Installation, EngineLogSource, choice == Services.Engine.RemovalChoice.Everything
                ? "Uninstalling the Engine and the listed models in your own folders."
                : "Uninstalling the Engine; your own model folders stay as they are.");

            var files = choice == Services.Engine.RemovalChoice.Everything ? outside.Select(f => f.Path).ToList() : [];
            var directories = folder == Services.Engine.EngineFolderState.Missing ? [] : new List<string> { root };
            var result = await remover.RemoveAsync(directories, files);
            if (result.WasBusy)
            {
                await _dialogService.ShowMessageAsync(Services.Engine.EngineRemovalMessages.UninstallTitle,
                    Services.Engine.EngineRemovalMessages.Busy(result.BusyReason!));
                return;
            }

            // Anything of the Engine folder left: keep the row so Uninstall stays on the tile for another try.
            if (result.Failed.Any(f => FolderPathMatch.Contains(root, f.Path)))
            {
                card.ApplyEngineFolder(await Task.Run(() => Services.Engine.ManagedEngineLocator.Probe(root)));
                _eventAggregator.PublishEngineChanged(new EngineChangedEventArgs());
                await _dialogService.ShowMessageAsync(Services.Engine.EngineRemovalMessages.UninstallTitle,
                    Services.Engine.EngineRemovalMessages.Failures(
                        "The Diffusion Nexus Engine was not fully uninstalled. Close the programs that use these files, then choose Uninstall Engine again.",
                        result.Failed));
                return;
            }

            await ForgetEngineAsync(root);

            var index = InstallerCards.IndexOf(card);
            if (index >= 0) InstallerCards[index] = CreateEngineCard(null);
            _eventAggregator.PublishInstallerPackagesChanged(new InstallerPackagesChangedEventArgs());
            _eventAggregator.PublishEngineChanged(new EngineChangedEventArgs());

            if (result.Failed.Count > 0)
                await _dialogService.ShowMessageAsync(Services.Engine.EngineRemovalMessages.UninstallTitle,
                    Services.Engine.EngineRemovalMessages.Failures("The Diffusion Nexus Engine was uninstalled.", result.Failed));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Engine uninstall failed");
            _unifiedLogger.Error(LogCategory.Installation, EngineLogSource, $"Uninstall failed: {ex.Message}", ex);
            await _dialogService.ShowMessageAsync("Error", $"Uninstalling the Engine failed: {ex.Message}");
        }
        finally
        {
            _engineUninstallRunning = false;
        }
    }

    /// <summary>
    /// Drops what pointed at the Engine: settings folders whose path lies inside it (by path: an old
    /// startup backfill linked the user's own libraries to the Engine row), the app-managed row(s), and
    /// the Engine as ComfyUI Server (switched to the custom URL, which keeps its saved address).
    /// </summary>
    private async Task ForgetEngineAsync(string engineRoot)
    {
        var (unitOfWork, owned) = RentUnitOfWork();
        try
        {
            var settings = await unitOfWork.AppSettings.GetSettingsWithIncludesAsync();
            var removed = 0;
            foreach (var row in settings.BaseModelFolders.Where(f => FolderPathMatch.Contains(engineRoot, f.FolderPath)).ToList())
            { unitOfWork.AppSettings.RemoveBaseModelFolder(row); removed++; }
            foreach (var row in settings.ImageGalleries.Where(g => FolderPathMatch.Contains(engineRoot, g.FolderPath)).ToList())
            { unitOfWork.AppSettings.RemoveImageGallery(row); removed++; }
            foreach (var row in settings.LoraSources.Where(s => FolderPathMatch.Contains(engineRoot, s.FolderPath)).ToList())
            { unitOfWork.AppSettings.RemoveLoraSource(row); removed++; }

            var switched = settings.ComfyUiServerMode == ComfyUiServerMode.Engine;
            if (switched) settings.ComfyUiServerMode = ComfyUiServerMode.CustomUrl;

            foreach (var package in (await unitOfWork.InstallerPackages.GetAllAsync()).Where(p => p.IsAppManaged).ToList())
                unitOfWork.InstallerPackages.Remove(package);

            await unitOfWork.SaveChangesAsync();

            _unifiedLogger.Info(LogCategory.Installation, EngineLogSource,
                $"Engine uninstalled: forgot {removed} settings folder(s) inside it" +
                (switched ? "; ComfyUI Server switched to the custom URL." : "."));
            if (removed > 0 || switched)
                _eventAggregator.PublishSettingsSaved(new SettingsSavedEventArgs());
        }
        finally
        {
            if (owned) unitOfWork.Dispose();
        }
    }
```

If `settings.ImageGalleries` / `LoraSources` / `BaseModelFolders` can be null on `AppSettings`, guard with `?? []` (check the entity).

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~EngineUninstallTests|FullyQualifiedName~InstallerManager|FullyQualifiedName~Engine"`. Expected: all PASS.

- [ ] **Step 5: Commit and push**

```bash
git add -A DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "feat(engine): Uninstall Engine — confirm, stop, delete, forget its settings folders and the Engine as server" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 9: Remove a feature from the Features dialog

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/EngineFeatureRowViewModel.cs`
- Modify: `DiffusionNexus.UI/ViewModels/EngineFeaturesViewModel.cs` (ctor, new command, flags)
- Modify: `DiffusionNexus.UI/Views/Dialogs/EngineFeaturesDialog.axaml:36-46`
- Modify: `DiffusionNexus.UI/ViewModels/InstallerManagerViewModel.cs:815-857` (`OpenEngineFeaturesAsync`)
- Test: `DiffusionNexus.Tests/Engine/EngineFeatureRemovalTests.cs` (new)

**Interfaces:**
- Consumes: Tasks 4–6.
- Produces: `EngineFeatureRowViewModel.CanRemove`; `EngineFeaturesViewModel(..., IInstallActivity? installActivity = null, IDialogService? dialogs = null, EngineRemover? remover = null)`; `RemoveFeatureCommand` (parameter: row); `bool DidRemove`.

- [ ] **Step 1: Write the failing tests** — `EngineFeatureRemovalTests.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public sealed class EngineFeatureRemovalTests : IDisposable
{
    private readonly string _root;
    private readonly Mock<ICatalog> _catalog = new();
    private readonly Mock<IConfigurationCheckerService> _checker = new();
    private readonly Mock<IDialogService> _dialog = new();
    private readonly Mock<IRemovalFileSystem> _fs = new();
    private readonly Dictionary<Guid, ConfigurationCheckResult> _state = new();
    private readonly string _gguf, _vae, _unet;

    public EngineFeatureRemovalTests()
    {
        _root = Directory.CreateTempSubdirectory("dn-feature-remove-").FullName;
        _gguf = Path.Combine(_root, "custom_nodes", "ComfyUI-GGUF");
        _vae = Path.Combine(_root, "models", "vae", "qwen_image_vae.safetensors");
        _unet = Path.Combine(_root, "models", "diffusion_models", "q.gguf");
        Directory.CreateDirectory(_gguf);
        Directory.CreateDirectory(Path.GetDirectoryName(_vae)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_unet)!);
        File.WriteAllText(_vae, "x");
        File.WriteAllText(_unet, "x");

        foreach (var id in EngineFeatureCatalog.AllWorkloadIds)
        {
            _catalog.Setup(c => c.GetWorkloadAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InstallationConfiguration { Id = id, Name = id.ToString() });
            _state[id] = Missing();
        }
        _checker.Setup(c => c.CheckConfigurationAsync(It.IsAny<InstallationConfiguration>(), _root,
                It.IsAny<ConfigurationCheckOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstallationConfiguration c, string _, ConfigurationCheckOptions? _, CancellationToken _) => _state[c.Id]);

        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result([_gguf], [_vae, _unet]);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private static ConfigurationCheckResult Result(string[] nodes, string[] models) => new()
    {
        OverallStatus = ConfigurationStatus.Full, CustomNodesStatus = ConfigurationStatus.Full,
        ModelsStatus = ConfigurationStatus.Full, InstallationType = ComfyUIInstallationType.Manual,
        CustomNodeResults = nodes.Select(n => new CustomNodeCheckResult
            { Id = Guid.NewGuid(), Name = Path.GetFileName(n), Url = "u", IsInstalled = true, ExpectedPath = n }).ToList(),
        ModelResults = models.Select(m => new ModelCheckResult
            { Id = Guid.NewGuid(), Name = Path.GetFileName(m), IsInstalled = true, SearchedPaths = [], FoundAtPath = m, AllFoundPaths = [m] }).ToList()
    };

    // Nothing on disk. Not Result([], []): a check with no items reads as Installed (nothing missing).
    private static ConfigurationCheckResult Missing() => Result([], []) with
    {
        ModelResults = [new ModelCheckResult { Id = Guid.NewGuid(), Name = "m", IsInstalled = false, SearchedPaths = [] }]
    };

    private EngineFeaturesViewModel Sut() =>
        new(_catalog.Object, _checker.Object, new Mock<IWorkloadInstallService>().Object, _root,
            freeSpaceProbe: _ => 0, dialogs: _dialog.Object, remover: new EngineRemover(null, null, fileSystem: _fs.Object));

    private void Answer(int index) =>
        _dialog.Setup(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>())).ReturnsAsync(index);

    private static EngineFeatureRowViewModel Row(EngineFeaturesViewModel vm, EngineFeature f) =>
        vm.Rows.Single(r => r.Definition.Feature == f);

    [Fact]
    public async Task Remove_DeletesItsNodePacksAndModels_AndRechecks()
    {
        Answer(1);
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);
        var row = Row(vm, EngineFeature.InpaintOutpaint);
        row.CanRemove.Should().BeTrue();
        _fs.Setup(f => f.DeleteFile(It.IsAny<string>()))
            .Callback(() => _state[EngineFeatureCatalog.InpaintingQwen2512] = Missing());

        await vm.RemoveFeatureCommand.ExecuteAsync(row);

        _fs.Verify(f => f.DeleteDirectory(_gguf), Times.Once);
        _fs.Verify(f => f.DeleteFile(_vae), Times.Once);
        _fs.Verify(f => f.DeleteFile(_unet), Times.Once);
        vm.DidRemove.Should().BeTrue();
        row.Status.Should().Be(EngineFeatureStatus.NotInstalled);
    }

    [Fact]
    public async Task Remove_KeepsWhatAnotherInstalledFeatureUses()
    {
        _state[EngineFeatureCatalog.OutpaintingQwen2512] = Result([_gguf], [_vae]);
        Answer(1);
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.RemoveFeatureCommand.ExecuteAsync(Row(vm, EngineFeature.InpaintOutpaint));

        _fs.Verify(f => f.DeleteDirectory(_gguf), Times.Never);
        _fs.Verify(f => f.DeleteFile(_vae), Times.Never);
        _fs.Verify(f => f.DeleteFile(_unet), Times.Once);
        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(),
            It.Is<string>(m => m.Contains("used by Outpaint Vision")), It.IsAny<string[]>()));
    }

    [Fact]
    public async Task Remove_EngineOnly_LeavesTheUsersCopy()
    {
        var userCopy = Path.Combine(Directory.CreateTempSubdirectory("dn-user-lib-").FullName, "ae.safetensors");
        File.WriteAllText(userCopy, "x");
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result([], [_unet, userCopy]);
        Answer(2); // "Delete only what's in the Diffusion Nexus Engine"
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.RemoveFeatureCommand.ExecuteAsync(Row(vm, EngineFeature.InpaintOutpaint));

        _fs.Verify(f => f.DeleteFile(_unet), Times.Once);
        _fs.Verify(f => f.DeleteFile(userCopy), Times.Never);
    }

    [Fact]
    public async Task Remove_Everything_AlsoDeletesTheUsersCopy()
    {
        var userCopy = Path.Combine(Directory.CreateTempSubdirectory("dn-user-lib-").FullName, "ae.safetensors");
        File.WriteAllText(userCopy, "x");
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result([], [_unet, userCopy]);
        Answer(1); // "Delete all models of this feature, wherever they are"
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.RemoveFeatureCommand.ExecuteAsync(Row(vm, EngineFeature.InpaintOutpaint));

        _fs.Verify(f => f.DeleteFile(userCopy), Times.Once);
    }

    [Fact]
    public async Task Cancel_DeletesNothing()
    {
        Answer(0);
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.RemoveFeatureCommand.ExecuteAsync(Row(vm, EngineFeature.InpaintOutpaint));

        _fs.VerifyNoOtherCalls();
        vm.DidRemove.Should().BeFalse();
    }

    // Review focus 5: one locked file is named; the rest still goes; the row re-checks to Partial.
    [Fact]
    public async Task ALockedFile_IsNamed_TheRestIsDeleted_AndTheRowIsPartial()
    {
        Answer(1);
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);
        _fs.Setup(f => f.DeleteFile(_vae)).Throws(new IOException("in use by another process"));
        _fs.Setup(f => f.DeleteFile(_unet))
            .Callback(() => _state[EngineFeatureCatalog.InpaintingQwen2512] = Result([], [_vae]) with
            {
                ModelResults = [new ModelCheckResult { Id = Guid.NewGuid(), Name = "q", IsInstalled = false, SearchedPaths = [] },
                                new ModelCheckResult { Id = Guid.NewGuid(), Name = "v", IsInstalled = true, SearchedPaths = [], FoundAtPath = _vae, AllFoundPaths = [_vae] }]
            });

        await vm.RemoveFeatureCommand.ExecuteAsync(Row(vm, EngineFeature.InpaintOutpaint));

        _fs.Verify(f => f.DeleteFile(_unet), Times.Once);
        _dialog.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains(_vae) && m.Contains("in use"))));
        Row(vm, EngineFeature.InpaintOutpaint).Status.Should().Be(EngineFeatureStatus.Partial);
    }

    [Fact]
    public async Task NotInstalledRow_CannotBeRemoved()
    {
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);

        var row = Row(vm, EngineFeature.BatchUpscale);
        row.CanRemove.Should().BeFalse();
        vm.RemoveFeatureCommand.CanExecute(row).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~EngineFeatureRemovalTests"`. Expected: build FAILS.

- [ ] **Step 3: Implement.**

`EngineFeatureRowViewModel` — on `_status` add `[NotifyPropertyChangedFor(nameof(CanRemove))]`, and:

```csharp
    /// <summary>Remove… shows on rows with something of the feature on disk.</summary>
    public bool CanRemove => Status is EngineFeatureStatus.Installed or EngineFeatureStatus.Partial;
```

`EngineFeaturesViewModel`:
- Ctor: append `IDialogService? dialogs = null, EngineRemover? remover = null`; store `_dialogs`, `_remover`.
- In the row `PropertyChanged` handler add `RemoveFeatureCommand.NotifyCanExecuteChanged();`.
- `[NotifyCanExecuteChangedFor(nameof(RemoveFeatureCommand))]` on `_isInstalling`; new field:

```csharp
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFeatureCommand))]
    private bool _isRemoving;

    /// <summary>True once a removal deleted something, so the caller tells open panels to re-check. Internal setter is a test seam.</summary>
    public bool DidRemove { get; internal set; }
```

- `CanInstallSelected()` becomes `!IsInstalling && !IsRemoving && RowsToInstall.Any();`
- The command:

```csharp
    private bool CanRemoveFeature(EngineFeatureRowViewModel? row) =>
        row is { CanRemove: true } && !IsInstalling && !IsRemoving && _remover is not null && _dialogs is not null;

    /// <summary>
    /// Remove…: plans what goes (node packs and models no other installed feature uses), asks — with the
    /// user's own folders' copies listed and optional — then stops the Engine and deletes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveFeature))]
    private async Task RemoveFeatureAsync(EngineFeatureRowViewModel row)
    {
        if (_remover is null || _dialogs is null) return;
        if (_remover.BusyReason() is { } busy)
        {
            await _dialogs.ShowMessageAsync(EngineRemovalMessages.FeatureTitle, EngineRemovalMessages.Busy(busy));
            return;
        }

        IsRemoving = true;
        try
        {
            var checks = await EngineFeatureChecks.LoadAsync(_catalog, _checker, _engineRoot, CancellationToken.None);
            var plan = await Task.Run(() => EngineRemovalPlanner.PlanFeatureRemoval(_engineRoot, checks, row.Definition.Feature));
            Info($"{row.DisplayName}: remove plan — {plan.NodePackFolders.Count} node pack(s), {plan.EngineModels.Count} Engine model(s) " +
                 $"({EngineRemovalMessages.FormatSize(plan.EngineBytes)}), {plan.OutsideModels.Count} in your own folders, " +
                 $"{plan.Kept.Count} kept ({string.Join(", ", plan.Kept.Select(k => $"{k.Name} for {k.NeededBy}"))}).");

            if (!plan.HasAnythingToDelete)
            {
                await _dialogs.ShowMessageAsync(EngineRemovalMessages.FeatureTitle, EngineRemovalMessages.NothingToRemove(row.DisplayName, plan));
                return;
            }

            var hasOutside = plan.OutsideModels.Count > 0;
            var choice = EngineRemovalMessages.ToChoice(
                await _dialogs.ShowOptionsAsync(EngineRemovalMessages.FeatureTitle,
                    EngineRemovalMessages.FeatureRemoval(row.DisplayName, plan), EngineRemovalMessages.FeatureOptions(hasOutside)),
                hasOutside);
            if (choice == RemovalChoice.Cancel)
            {
                Info($"{row.DisplayName}: removal cancelled. Nothing was changed.");
                return;
            }

            var files = plan.EngineModels.Select(f => f.Path)
                .Concat(choice == RemovalChoice.Everything ? plan.OutsideModels.Select(f => f.Path) : [])
                .ToList();
            Info($"{row.DisplayName}: removing ({(choice == RemovalChoice.Everything ? "your own folders' copies too" : "only what's in the Engine")}).");

            var result = await _remover.RemoveAsync(plan.NodePackFolders, files);
            if (result.WasBusy)
            {
                await _dialogs.ShowMessageAsync(EngineRemovalMessages.FeatureTitle, EngineRemovalMessages.Busy(result.BusyReason!));
                return;
            }

            DidRemove = true;
            if (result.Failed.Count == 0)
            {
                ProgressText = $"Removed {row.DisplayName}.";
                Info(ProgressText);
            }
            else
            {
                ProgressText = $"Removed {row.DisplayName} with problems: {result.Failed.Count} item(s) could not be deleted. See the Unified Console.";
                Warn(ProgressText);
                await _dialogs.ShowMessageAsync(EngineRemovalMessages.FeatureTitle,
                    EngineRemovalMessages.Failures($"{row.DisplayName} was not removed completely.", result.Failed));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Engine feature removal failed");
            _unifiedLogger?.Error(LogCategory.Installation, LogSource, "Feature removal failed", ex);
            ProgressText = $"Remove failed: {ex.Message}";
        }
        finally
        {
            try { await CheckAllAsync(CancellationToken.None); }
            catch (Exception ex) { Warn($"Re-check after removal failed — {ex.Message}", ex); }
            IsRemoving = false;
        }
    }
```

Note: the busy check and "nothing to delete" return before `IsRemoving` matters; the `finally` re-check runs on every path after `IsRemoving = true`, which is harmless.

`EngineFeaturesDialog.axaml` — row grid `ColumnDefinitions="32,*,200,150,96"`, and after the status `TextBlock`:

```xml
<Button Grid.Column="4" Content="Remove…" HorizontalAlignment="Right" VerticalAlignment="Top"
        Classes="close-btn" Padding="10,4"
        IsVisible="{Binding CanRemove}"
        Command="{Binding $parent[ItemsControl].((vm:EngineFeaturesViewModel)DataContext).RemoveFeatureCommand}"
        CommandParameter="{Binding}"/>
```

Change the intro text (line 29) to: `Choose what the Engine should be able to do. The required node packs and models are installed into the Engine only. Shared files are downloaded once. Remove… deletes a feature's files that no other installed feature needs.`

`InstallerManagerViewModel.OpenEngineFeaturesAsync` — construct with the new arguments and handle `DidRemove`:

```csharp
            var vm = new EngineFeaturesViewModel(
                _catalog, _checkerService, _installService, card.InstallationPath,
                _resourceMonitor, _unifiedLogger, preselect, installActivity: _installActivity,
                dialogs: _dialogService, remover: CreateEngineRemover());
```

and after the existing `if (vm.DidInstall) { ... }` block:

```csharp
            if (vm.DidRemove && !vm.DidInstall)
            {
                // Lets an open Inpaint/Outpaint panel and Settings' Engine status re-check.
                _eventAggregator.PublishEngineChanged(new EngineChangedEventArgs());
            }
```

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~EngineFeature|FullyQualifiedName~InstallerManager"`. Expected: all PASS. Then `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj` — 0 errors (compiled-binding cast in the dialog).

- [ ] **Step 5: Commit and push**

```bash
git add -A DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "feat(engine): Remove… on every installed feature, keeping what other features need" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 10: Whole-suite check, docs, issue and PR

**Files:**
- Modify: `DiffusionNexus.UI/REUSABLES.md` (one row for `EngineRemover` / `EngineRemovalPlanner` under Services)
- Modify: `docs/superpowers/specs/2026-10-12-engine-uninstall-and-feature-removal-design.md` (as-built notes)

- [ ] **Step 1: Full test run** — `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj`. Expected: everything passes except failures that also fail on `origin/develop` (known: `CivitaiWaitlistTests.PersistRestore_RoundTripsEntries` time-bomb, `DownloadPreflightChoiceTests.OpenWebsite_NsfwModel_OpensCivitaiRedHost`, order-flaky `GenerationGalleryViewModelTests.TagCloudSearchText_…`). Confirm any other failure on `develop` before calling it pre-existing.

- [ ] **Step 2: Release build** — `dotnet build DiffusionNexus.sln -c Release` (or the repo's `.slnx`). Expected: 0 errors.

- [ ] **Step 3: REUSABLES.md** — add under the services table:

```markdown
| `EngineRemover`, `EngineRemovalPlanner`, `EngineRemovalMessages` | [Services/Engine/](Services/Engine/) | Remove Engine files safely (busy check, stop, maintenance hold, junction-safe delete) and plan/word what a feature removal or Engine uninstall deletes. Files inside the Engine folder are the Engine's; anything else is the user's and is only deleted on explicit choice. |
```

- [ ] **Step 4: Spec as-built** — in the spec's "Pieces": replace the `EngineRemovalDialog` bullet with "Confirmation goes through `IDialogService.ShowOptionsAsync` (Cancel first, the safe choice last); text from `EngineRemovalMessages`.", replace `EngineRemovalService` with `EngineRemover` + the uninstall flow in `InstallerManagerViewModel`, add "Generation Galleries and LoRA sources inside the Engine folder are forgotten too", add "Core tile: Settings, Open Folder and Make Default greyed with 'built into the app' tooltips", and in "Remove a feature" replace "It is disabled while … (shown as the tooltip)" with "Clicking it while the Engine is busy says why and changes nothing."

- [ ] **Step 5: CRLF check** — `git diff --numstat origin/develop` vs `git diff -w --numstat origin/develop`: line counts must match closely; a file with whole-file churn in the first but not the second has flipped line endings — fix before pushing.

- [ ] **Step 6: Commit and push**

```bash
git add -A
git commit -m "docs: REUSABLES + spec as-built for Engine uninstall and feature removal" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

- [ ] **Step 7: Issue + PR** (repo `Little-God1983/DiffusionNexus`; `gh auth switch --user Little-God1983` first). One issue, one PR, base `develop`:

```bash
gh issue create --title "Uninstall the Diffusion Nexus Engine and remove its features" --body-file <issue body: problem (no way to remove Engine/features; Settings/Open Folder silently do nothing), rulings, link to spec>
gh pr create --base develop --head feature/engine-uninstall-and-feature-removal --title "Uninstall the Diffusion Nexus Engine and remove its features (#<issue>)" --body-file <PR body: what changes for the user, closes #<issue>, test results, manual smoke list (spec "Testing"), ending with the 🤖 Generated with Claude Code line>
```

- [ ] **Step 8: Manual smoke** on the **BenQ** monitor (check `BNQ7F05` is connected first; never the LG), against a real Engine, following the spec's "Manual smoke" list 0–6. Record each step's result in the PR.

---

## Self-review notes

- Spec coverage: blank workload (T1), downloads into Engine (T2), registration now + only own folder (T3), every copy (T4), planner/kept/inside-outside/known Civitai gap behaviour (T5 — the gap needs no code: the file is simply not found), stop + hold + busy + partial failure (T6, T8), tile items (T7), uninstall incl. missing folder, server switch, by-path settings removal (T8), feature remove incl. three choices and re-check (T9), logging (every task), docs/PR/smoke (T10).
- Deviations from the spec, recorded in T10 Step 4: `ShowOptionsAsync` instead of a new dialog (REUSABLES rule: generic dialogs first); galleries and LoRA sources inside the Engine folder are also forgotten (the startup outputs registrar can link the Engine's `output` folder as a gallery); the Core tile gets the same greyed-with-reason treatment; a busy Remove… says why on click instead of a disabled tooltip.
