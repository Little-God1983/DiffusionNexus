---
name: csharp-conventions
description: Use when writing, changing or reviewing C# / .NET code, .axaml views, ViewModels, converters, DI registrations, EF Core entities or migrations, NuGet references, or xUnit tests in the DiffusionNexus repo (Avalonia, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, EF Core SQLite, Moq, FluentAssertions).
---

# DiffusionNexus C# / Avalonia conventions

The rules here come from the existing code. When this file and the surrounding code disagree, match the
surrounding code and say so. Short version + build/test commands: `CLAUDE.md` at the repo root.

## Stack

| Concern | What the repo uses |
|---|---|
| Runtime | `net10.0` (C# 14 default), Nullable + ImplicitUsings on, Windows x64 only |
| UI | Avalonia 11.3.22, FluentTheme, dark variant, compiled bindings by default |
| MVVM | CommunityToolkit.Mvvm 8.4 (source generators) |
| DI | Microsoft.Extensions.DependencyInjection, built by hand in `App.ConfigureServices` |
| Data | EF Core 10 + SQLite (`DiffusionNexusCoreDbContext`), repositories + `IUnitOfWork` |
| Logging | `IUnifiedLogger` (user-visible Unified Console) + static Serilog `Log` (file diagnostics) |
| Tests | xUnit 2.9, Moq 4.21, FluentAssertions 7, Avalonia.Headless(.XUnit) |

## ViewModel

Field-backed `[ObservableProperty]` (661 attributes in `DiffusionNexus.UI`, zero partial-property uses). The class must
be `partial`. Inject services through the constructor. A parameterless constructor exists only for the designer / `ViewBase<T>`.
Marshal to the UI thread through `IUiScheduler`.

The example below is a **model for new code**, not a copy of an existing class. It is close to
`ResourceMonitorViewModel`, which predates the scheduler seam and derives `ObservableObject`. For a real class that
uses the seam, see `SettingsViewModel` (constructor parameter `uiScheduler`, `_uiScheduler.Post(...)` in `OnExternalSettingsSaved`).

```csharp
namespace DiffusionNexus.UI.ViewModels;

public partial class GpuStatusViewModel : ViewModelBase   // ViewModelBase: ViewLocator only matches these
{
    private readonly IResourceMonitorService _service;
    private readonly IUiScheduler _ui;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty]
    private double _vramPercent;

    public GpuStatusViewModel(IResourceMonitorService service, IUiScheduler? uiScheduler = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _ui = uiScheduler ?? AvaloniaUiScheduler.Instance;
    }

    /// <summary>Design-time constructor.</summary>
    public GpuStatusViewModel() : this(new ResourceMonitorService()) { }

    public bool HasError => ErrorText is not null;

    [RelayCommand]   // generates RefreshCommand (IAsyncRelayCommand); the token cancels a running refresh
    private async Task RefreshAsync(CancellationToken ct)
    {
        // Sample on the pool so a slow driver call never stalls the UI thread…
        var snapshot = await Task.Run(() => _service.GetSnapshotAsync(ct), ct);

        // …and apply the result on the UI thread through the seam (inline in tests).
        await _ui.InvokeAsync(() =>
        {
            VramPercent = snapshot.VramTotalMB > 0 ? snapshot.VramUsedMB * 100.0 / snapshot.VramTotalMB : 0;
            ErrorText = snapshot.GpuAvailable ? null : snapshot.Error;
        });
    }

    partial void OnVramPercentChanged(double value) { /* react to a change here, not in a setter */ }
}
```

- Base classes: `ViewModelBase` (plain), `BusyViewModelBase` (`IsBusy` / `BusyMessage`). Tabs with a refresh button implement `IRefreshableTab`.
- `[RelayCommand(CanExecute = nameof(CanRun))]` + `[NotifyCanExecuteChangedFor(nameof(RunCommand))]` on the inputs.
- Collections are `ObservableCollection<T>`. Mutate them only on the UI thread.
- Work started off the UI thread comes back through `_ui.Post(...)` / `await _ui.InvokeAsync(...)`.
  Older code calls `Dispatcher.UIThread` directly. Use the seam in new code (tests swap in `ImmediateUiScheduler`).

## View ↔ ViewModel wiring

The view for the model above. It has the same shape as the real `Views/Controls/ResourceMonitorView.axaml`, which binds
`IsVisible="{Binding !IsGpuAvailable}"` where this binds `HasError`.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:vm="using:DiffusionNexus.UI.ViewModels"
             mc:Ignorable="d"
             x:Class="DiffusionNexus.UI.Views.Controls.GpuStatusView"
             x:DataType="vm:GpuStatusViewModel">
  <Design.DataContext><vm:GpuStatusViewModel/></Design.DataContext>
  <StackPanel Orientation="Horizontal" Spacing="6">
    <ProgressBar Value="{Binding VramPercent}" Maximum="100" Width="130" Foreground="#0E9C6A"/>
    <TextBlock Text="{Binding ErrorText}" IsVisible="{Binding HasError}" Foreground="#C8A03A"/>
    <Button Content="↻" Command="{Binding RefreshCommand}"/>
  </StackPanel>
</UserControl>
```

- The parent hands over the DataContext: `<controls:ResourceMonitorView DataContext="{Binding ResourceMonitor}"/>`.
  Content hosts resolve views by name through `ViewLocator` (`…ViewModels.X.FooViewModel` → `…Views.X.FooView`,
  matching only `ViewModelBase`), so a new pair needs no registration if the namespaces mirror each other.
- Every new view declares `x:DataType` (98 of 102 `.axaml` files do; `App.axaml`, `ImageCompareControl`,
  `DownloadLoraMovedDialog` and `VramSelectionDialog` don't). Compiled bindings then catch typos at build time. Don't switch them off,
  and don't use `ReflectionBinding` (the repo has one).
- Negation and nested paths work in compiled bindings: `IsVisible="{Binding !IsBusy}"`.
- Feature views that own their VM can derive from `ViewBase<TViewModel>`. Reusable controls that inherit
  their DataContext use `ControlBase`. Most views are plain `UserControl`, which is fine.
- Avalonia, not WPF: `.axaml`, CSS-like selectors in `<UserControl.Styles>`, pseudo-classes (`:pointerover`),
  no triggers, `ToolTip.Tip`, `IsVisible` instead of `Visibility`.
- Code-behind holds view-only plumbing: `InitializeComponent`, visual-tree attach/detach, timers that just
  execute a VM command, drag/drop, pointer and canvas handling. Decisions and data go in the ViewModel.
  Some large code-behinds (e.g. `ImageEditView.axaml.cs`) predate this. Don't grow them.
- Check `DiffusionNexus.UI/REUSABLES.md` before writing a control, and add a row when you create one.

## Service + DI

Lower layers expose `AddXxx(this IServiceCollection)` (`AddDataAccessLayer`, `AddInfrastructureServices`,
`AddLibrarySync`, `AddCivitaiGateway`, …). The UI layer registers the rest in `App.ConfigureServices`, with a
one-line comment saying *why* each lifetime was chosen:

```csharp
// Singleton - maintains ONNX session
services.AddSingleton<IImageTaggingService, ImageTaggingService>();

// Transient so each consumer gets its own UoW/DbContext
services.AddTransient<IAppSettingsService, AppSettingsService>();

// Fresh scope per read: parallel readiness checks must not share one DbContext.
services.AddSingleton<IComfyUiClientProvider>(sp =>
{
    var scopes = sp.GetRequiredService<IServiceScopeFactory>();
    return new ComfyUiClientProvider(
        async ct =>
        {
            using var scope = scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IAppSettingsService>()
                .GetComfyUiServerConnectionAsync(ct);
        },
        sp.GetRequiredService<IEngineRootResolver>(),
        sp.GetRequiredService<IManagedComfyUiEngine>(),
        sp.GetService<IUnifiedLogger>());   // optional dependency → GetService
});
```

- Interfaces for cross-layer contracts live in `DiffusionNexus.Domain/Services`. UI-only ones sit next to their implementation.
- A singleton never captures a scoped `DbContext` / `IUnitOfWork`: it takes `IServiceScopeFactory` and opens a scope per operation.
- Required dependencies use `GetRequiredService`, optional ones `GetService` + a nullable constructor parameter.
- `App.Services` (static) exists for the composition root and legacy call sites. Don't add new service-locator calls inside ViewModels.

## Error handling and logging

This is a pattern, not a copy. The real startup catch around `OutputsFolderRegistrar` (`App.axaml.cs`) logs to Serilog
only. New code should also tell the Unified Console.

```csharp
try
{
    await registrar.EnsureRegisteredAsync(ct);
}
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    throw;
}
catch (Exception ex)
{
    // Unified Console: what the user sees. Category + source + plain-language message.
    _logger?.Error(LogCategory.Configuration, "OutputsFolder", "Could not register the outputs folder.", ex);
    Serilog.Log.Warning(ex, "OutputsFolderRegistrar failed");      // file log for diagnosis
}
```

- `IUnifiedLogger`: `Info/Warn/Error(LogCategory, source, message, detail|ex)`. New features log their steps
  there so a user report shows what happened.
- Fire-and-forget (`_ = Task.Run(async () => { try { … } catch (Exception ex) { Log… } })`) always catches inside,
  because an unobserved exception there is only logged and never reaches the user.
- User-facing failures go through `IDialogService.ShowMessageAsync` / `ShowConfirmAsync`. A message box
  says what happens next, not only the exception text.
- Background checks fall through on failure and never block startup or the UI.

## Unit test

```csharp
namespace DiffusionNexus.Tests.ViewModels;

public class SettingsViewModelSchedulerTests
{
    [Fact]
    public void WhenAnExternalSettingsSavedArrivesThenCollectionsReloadSynchronouslyThroughTheScheduler()
    {
        var settingsService = new Mock<IAppSettingsService>();
        settingsService
            .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings { /* … */ });
        var aggregator = new DatasetEventAggregator();
        var scheduler = new ImmediateUiScheduler();          // runs UI posts inline

        var vm = new SettingsViewModel(settingsService.Object, new Mock<ISecureStorage>().Object,
            eventAggregator: aggregator, uiScheduler: scheduler);

        aggregator.PublishSettingsSaved(new SettingsSavedEventArgs());

        scheduler.PostCount.Should().Be(1);
        vm.LoraSources.Should().ContainSingle(s => s.FolderPath == @"C:\Models\Lora");
    }
}
```

- FluentAssertions (`.Should()`) everywhere, with a `because` string when the reason isn't obvious. Moq for interfaces.
- Names read as sentences: `WhenXThenY`, `Subject_Condition_Result` or `Snake_case_sentence`. Match the file.
- Async tests are `async Task` and `await`. No `.Result` (xUnit1031 already warns).
- Temp folders are `Path.Combine(Path.GetTempPath(), "dn-…-" + Guid.NewGuid().ToString("N"))`, deleted in `Dispose`.
- EF tests use in-memory SQLite (`Microsoft.EntityFrameworkCore.Sqlite`, `DataSource=:memory:` with an open connection).
- Tests that need a real Avalonia control go in `DiffusionNexus.IntegrationTests` (`[AvaloniaFact]`, `TestAppHost`).
  Unit tests must not initialize the Avalonia platform, because it deadlocks the suite. Put a seam in front of the platform type.
- Network tests use `[OnlineFact]` (opt-in via `DIFFUSIONNEXUS_SDK_ONLINE_TESTS=1`).
- Bug fix: write a failing test that reproduces it first. When a test fails, decide whether the test or the code is wrong.

## EF Core

- Run `publish.ps1` before changing entities, `IEntityTypeConfiguration`s or migrations (it backs up a working app + DB).
- Add a migration with
  `dotnet ef migrations add <Name> --project DiffusionNexus.DataAccess --startup-project DiffusionNexus.DataAccess --context DiffusionNexusCoreDbContext --output-dir Migrations/Core`.
- Startup migration/repair runs through `DatabaseRecoveryService`. No other production code calls `Migrate()` (tests may).
- A long-lived context's identity map keeps rows another context deleted. Use a fresh scope or `AsNoTracking` for re-reads.

## Anti-patterns

| Don't | Do |
|---|---|
| Business logic in `.axaml.cs` event handlers | Bind a `[RelayCommand]`. Code-behind only executes it |
| `.Result`, `.Wait()`, `GetAwaiter().GetResult()` in new code | `await` all the way, pass `CancellationToken` |
| `async void` (except event handlers that catch everything) | `async Task` + `[RelayCommand]` |
| Setting bound properties / `ObservableCollection` from a pool thread | `_ui.Post` / `await _ui.InvokeAsync` |
| `Dispatcher.UIThread` in a new ViewModel | Inject `IUiScheduler` |
| `OnPropertyChanged("Foo")` | `nameof(Foo)` or `[NotifyPropertyChangedFor(nameof(Foo))]` |
| `{ReflectionBinding}` / `x:CompileBindings="False"` | `x:DataType` + compiled bindings |
| `ConfigureAwait(false)` in VM / UI code that touches state after the await | Plain `await` (library/service code may use it) |
| `new DialogService(...)` in a ViewModel | `IDialogServiceAware` and the view injects it |
| Singleton holding a `DbContext` / `IUnitOfWork` | `IServiceScopeFactory` and a scope per operation |
| New NuGet package, pattern or folder without asking | Reuse what's here, or ask first |
| Cross-platform branches, `TODO: Linux` | Windows only |
| Swallowing exceptions silently | Log to `IUnifiedLogger` (+ Serilog), then fall through |
