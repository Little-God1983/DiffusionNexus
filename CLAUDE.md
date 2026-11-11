# DiffusionNexus — Claude Code guide

Avalonia desktop app (C# / .NET 10, Windows only) for managing local AI models, datasets and
generations. Also read and follow [.github/copilot-instructions.md](.github/copilot-instructions.md):
its rules (one DB vs. one JSON catalog, `publish.ps1` before schema changes, `REUSABLES.md`,
`Shortcuts.md`, bug → test first) apply here too.
Detailed conventions with code examples: the `csharp-conventions` skill
([.claude/skills/csharp-conventions/SKILL.md](.claude/skills/csharp-conventions/SKILL.md)).

## Solution layout (`DiffusionNexus.sln`)

| Project | Role |
|---|---|
| `DiffusionNexus.UI` | The app (`WinExe`, x64). Avalonia 11.3 views, ViewModels, UI services, DI composition root in `App.axaml.cs`. |
| `DiffusionNexus.Domain` | Entities, enums, models and service *interfaces* (e.g. `IUnifiedLogger`, `IAppSettingsService`). No dependencies. |
| `DiffusionNexus.DataAccess` | EF Core 10 + SQLite: `DiffusionNexusCoreDbContext`, configurations, repositories, unit of work, `Migrations/Core`. |
| `DiffusionNexus.Infrastructure` | Cross-cutting services (secure storage, image cache, activity log, HTTP). |
| `DiffusionNexus.Civitai` | Civitai API client and models. |
| `DiffusionNexus.Service` | Application services (sync, metadata, dataset quality, ONNX/FFmpeg work). |
| `DiffusionNexus.Inference` | Local inference: stable-diffusion.cpp (StableDiffusion.NET) and LLamaSharp captioning. |
| `DiffusionNexus.Tests` | xUnit unit tests (Moq, FluentAssertions, Avalonia.Headless without a platform). |
| `DiffusionNexus.IntegrationTests` | Headless Avalonia UI tests (`[AvaloniaFact]`, `TestAppHost`), run serially. |
| `DiffusionNexus.Inference.Sandbox` | Console scratch app, **not in the solution**. |

The Installer SDK comes in as NuGet packages (`DiffusionNexus.Installer.SDK.*` 2.3.1, from GitHub
Packages). Read its source at `E:\Repos\DiffusionNexus.Installer.SDK`, but never reference it
as a project.

## Build and test

```powershell
dotnet build DiffusionNexus.sln                      # ~25 s warm, 0 errors, 25 known warnings (all in DiffusionNexus.Tests)
dotnet build DiffusionNexus.UI                       # UI/ViewModel change only (pulls its dependencies)
dotnet test  DiffusionNexus.Tests --no-build --filter "FullyQualifiedName~SettingsViewModel"   # one area: seconds
dotnet test  DiffusionNexus.IntegrationTests --no-build   # headless UI tests: 56, ~2 s
dotnet test  DiffusionNexus.sln --no-build           # everything: ~7,800 tests, ~4 min (2 online canaries skip)
```

- **After every change: build, then run the tests for the area you touched, before reporting back.**
  Run the full suite before a push / PR, and in the background, because it takes ~4 min.
  XAML errors (AVLN codes) only show up in `dotnet build`, so always build after editing `.axaml`.
  Don't add warnings. The 25 existing ones (CA1416, CS4014, CS8602, xUnit1031/1026 in tests) are known.
- Always name the solution or project. The root also holds a stray `DiffusionNexus.csproj` + `Program.cs`
  ("Hello, World", not in the solution). A bare `dotnet build` only works because MSBuild prefers the
  same-named `.sln`. Never build that root csproj: it would glob every `.cs` in the repo.
- Restore needs the env var `GITHUB_PACKAGES_TOKEN` (see `nuget.config`) for the SDK packages.
- CI (`.github/workflows/dotnet.yml`, windows-latest): `dotnet build -c Release` then `dotnet test --no-build -c Release`.
- Online canaries are opt-in: `[OnlineFact]` runs only with `DIFFUSIONNEXUS_SDK_ONLINE_TESTS=1`.

## Language and project settings

- All projects: `net10.0`, `Nullable` enable, `ImplicitUsings` enable, no `LangVersion` → C# 14 default.
  No `TreatWarningsAsErrors`. The version lives in `Directory.Build.props`. Don't bump it by hand: a merge into `main` releases.
- New files use file-scoped namespaces. Generated migrations and about 20 older files (mostly `DiffusionNexus.Service/Classes`)
  use block namespaces. Records, `required` members and collection expressions (`[]`)
  are used widely. Primary constructors are rare (fine for small new types, match the file you're in).
- `.editorconfig` only pins `charset = utf-8`. Keep files UTF-8 (issue #441: mojibake from lossy saves).

## UI / MVVM conventions (summary)

- **CommunityToolkit.Mvvm 8.4**: `[ObservableProperty] private T _field;` (field style, never C# 14
  partial properties), `[RelayCommand]`, `[NotifyPropertyChangedFor(nameof(...))]`,
  `partial void OnXChanged(...)`. ViewModels are `partial` and derive from `ViewModelBase`
  (or `BusyViewModelBase` / `ObservableObject`).
- **Views**: `.axaml` + `.axaml.cs`, `x:DataType` on the root. Compiled bindings are on by default
  (`AvaloniaUseCompiledBindingsByDefault`). DataContext arrives from the parent
  (`DataContext="{Binding Child}"`) or through `ViewLocator` (`…ViewModels.FooViewModel` → `…Views.FooView`).
- **DI**: `Microsoft.Extensions.DependencyInjection`, composed by hand in `App.ConfigureServices`
  (`App.axaml.cs`), using the `AddXxx()` extensions from the lower layers. VMs take services through optional constructor parameters.
- **Threading** (new code): marshal through the injected `IUiScheduler` (testable), not `Dispatcher.UIThread`.
  Older ViewModels (29 files) still call the dispatcher directly. Leave them unless the task is to convert them.
  New code is async all the way, passes `CancellationToken` and never blocks with `.Result` / `.Wait()`.
  About eight such calls remain, e.g. in `App.axaml.cs` shutdown and `OnnxSessionHost`. Don't copy them.
- **Dialogs**: `IDialogService` (VM implements `IDialogServiceAware`). **Logging**: `IUnifiedLogger` for anything
  the user should see in the Unified Console, static Serilog `Log` for diagnostics.
- **Styling**: Fluent theme, `RequestedThemeVariant="Dark"`. Styles live in the view (`<UserControl.Styles>`),
  colors are literal hex like the neighbouring views. Check `DiffusionNexus.UI/REUSABLES.md` before writing a new control.

## Where new files go

| Kind | Location / naming |
|---|---|
| ViewModel | `DiffusionNexus.UI/ViewModels[/Area]/FooViewModel.cs` |
| View | `DiffusionNexus.UI/Views[/Area]/FooView.axaml(.cs)`, same sub-namespace as the VM (ViewLocator) |
| Reusable control | `DiffusionNexus.UI/Views/Controls/` (+ row in `REUSABLES.md`) |
| Converter / behavior | `DiffusionNexus.UI/Converters/`, `DiffusionNexus.UI/Behaviors/` |
| UI service | `DiffusionNexus.UI/Services[/Area]/` (interface `IFoo` next to `Foo`) |
| Domain contract | `DiffusionNexus.Domain/Services/IFoo.cs` |
| Test | `DiffusionNexus.Tests/<mirrored area>/FooTests.cs` |

## Gotchas

- **DB schema**: run `publish.ps1` (backup) before touching entities, configurations or migrations. Migrations:
  `dotnet ef migrations add <Name> --project DiffusionNexus.DataAccess --startup-project DiffusionNexus.DataAccess --context DiffusionNexusCoreDbContext --output-dir Migrations/Core`.
- `App.CompleteStartupAsync` must resume on the UI thread: no `ConfigureAwait(false)` there.
- In `App.ConfigureServices`, ICaptioningBackend / IFeatureBackend registration order prevents a DI cycle. Read its comments before reordering.
- `.githooks/pre-push` (`core.hooksPath`) blocks pushes to `main`/`develop` and branch names outside
  `feature/ bugfix/ hotfix/ release/` (`bug/` still passes, for old branches only). It checks the branch being
  pushed *to*, so `git push origin HEAD:develop` is caught too; tags are not checked.
- Windows only: no cross-platform code paths or `TODO: Linux` markers.
- Keyboard shortcuts → document in `DiffusionNexus.UI/Doc/Shortcuts.md`.
