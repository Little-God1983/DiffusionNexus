# DiffusionNexus UI V2 Architecture

A refactored Avalonia UI shell with improved architecture, MVVM patterns, and clean separation of concerns.

**Project**: `DiffusionNexus.UI-V2`

## Project Structure

```
DiffusionNexus.UI-V2/
??? App.axaml                              # Application entry point (Dark theme)
??? App.axaml.cs                           # App initialization & DI setup
??? Program.cs                             # Application entry point
??? ViewLocator.cs                         # Convention-based view resolution
??? app.manifest                           # Windows application manifest
??? DiffusionNexus.UI-V2.csproj           # Project file
?
??? Assets/                                # Icons, images, resources
?
??? Services/                              # Application services
?   ??? IDialogService.cs                  # Dialog service interfaces
?   ??? DialogService.cs                   # Avalonia dialog implementation
?
??? ViewModels/                            # MVVM ViewModels
?   ??? ViewModelBase.cs                   # Base class for all ViewModels
?   ??? BusyViewModelBase.cs               # Extended base with busy state
?   ??? DiffusionNexusMainWindowViewModel.cs  # Main window ViewModel
?
??? Views/                                 # UI Views
    ??? ViewBase.cs                        # Generic base for views
    ??? DiffusionNexusMainWindow.axaml     # Main application window
    ??? DiffusionNexusMainWindow.axaml.cs
    ??? Controls/                          # Reusable UI controls
        ??? ControlBase.cs                 # Base for reusable controls
```

## Architecture Overview

### Class Hierarchy

```
????????????????????????????????????????????????????????????????????
?                         VIEWMODELS                               ?
????????????????????????????????????????????????????????????????????
?                                                                  ?
?   ObservableObject (CommunityToolkit.Mvvm)                      ?
?         ?                                                        ?
?         ?                                                        ?
?   ?????????????????????                                         ?
?   ?  ViewModelBase    ? ??? Debug logging, property notifications?
?   ?????????????????????                                         ?
?            ?                                                     ?
?            ?                                                     ?
?   ??????????????????????????                                    ?
?   ?  BusyViewModelBase     ? ??? IsBusy, BusyMessage,           ?
?   ?                        ?     RunBusyAsync(), DialogService  ?
?   ??????????????????????????                                    ?
?              ?                                                   ?
?              ?                                                   ?
?   ????????????????????????????????????                          ?
?   ?  Your Feature ViewModels         ?                          ?
?   ?  (LoraSortViewModel, etc.)       ?                          ?
?   ????????????????????????????????????                          ?
?                                                                  ?
????????????????????????????????????????????????????????????????????

????????????????????????????????????????????????????????????????????
?                           VIEWS                                  ?
????????????????????????????????????????????????????????????????????
?                                                                  ?
?   UserControl (Avalonia)                                        ?
?         ?                                                        ?
?         ??????????????????????????????????                      ?
?         ?                                ?                      ?
?   ???????????????????????         ?????????????????????        ?
?   ?  ViewBase<TVM>      ?         ?  ControlBase      ?        ?
?   ?                     ?         ?                   ?        ?
?   ?  • Creates own VM   ?         ?  • Receives DC    ?        ?
?   ?  • Injects services ?         ?    from parent    ?        ?
?   ?  • Typed ViewModel  ?         ?  • Auto-inject    ?        ?
?   ???????????????????????         ?????????????????????        ?
?             ?                              ?                    ?
?             ?                              ?                    ?
?   ???????????????????????         ?????????????????????        ?
?   ?  Feature Views      ?         ?  Reusable         ?        ?
?   ?  (LoraSortView)     ?         ?  Controls         ?        ?
?   ???????????????????????         ?????????????????????        ?
?                                                                  ?
????????????????????????????????????????????????????????????????????
```

## Components Reference

### ViewModelBase

Base class for all ViewModels with property change notifications.

```csharp
public abstract class ViewModelBase : ObservableObject
{
    // Automatic debug logging for property changes
}
```

**When to use:** All ViewModels should inherit from this (or BusyViewModelBase).

---

### BusyViewModelBase

Extended ViewModel base with busy state management and dialog support.

```csharp
public abstract partial class BusyViewModelBase : ViewModelBase, 
    IDialogServiceAware, IBusyViewModel
{
    // Properties
    bool IsBusy { get; set; }
    string? BusyMessage { get; set; }
    IDialogService? DialogService { get; set; }
    
    // Methods
    void RunBusy(Action action, string? message = null);
    Task RunBusyAsync(Func<Task> action, string? message = null);
    Task<T> RunBusyAsync<T>(Func<Task<T>> action, string? message = null);
}
```

**When to use:** ViewModels that need loading states or file dialogs.

**Example:**
```csharp
public partial class LoraSortViewModel : BusyViewModelBase
{
    [RelayCommand]
    private async Task LoadDataAsync()
    {
        await RunBusyAsync(async () =>
        {
            var data = await _service.LoadAsync();
            Items = new ObservableCollection<Item>(data);
        }, "Loading data...");
    }
    
    [RelayCommand]
    private async Task SelectFolderAsync()
    {
        var path = await DialogService!.ShowOpenFolderDialogAsync("Select Folder");
        if (path != null)
        {
            SelectedPath = path;
        }
    }
}
```

---

### ViewBase\<TViewModel\>

Generic base class for views that own their ViewModel.

```csharp
public abstract class ViewBase<TViewModel> : UserControl 
    where TViewModel : ViewModelBase, new()
{
    protected TViewModel ViewModel { get; }  // Typed access to DataContext
    protected Window? ParentWindow { get; }  // Access to parent window
}
```

**When to use:** Feature views that are navigation targets (modules).

**Example:**
```csharp
// Code-behind
public partial class LoraSortView : ViewBase<LoraSortViewModel>
{
    public LoraSortView()
    {
        InitializeComponent();
    }
    
    // ViewModel is automatically created and DialogService injected
}
```

```xml
<!-- AXAML - No need to set DataContext, it's done in ViewBase -->
<local:ViewBase x:TypeArguments="vm:LoraSortViewModel"
                xmlns:local="using:DiffusionNexus.UI.Views"
                xmlns:vm="using:DiffusionNexus.UI.ViewModels"
                x:Class="DiffusionNexus.UI.Views.LoraSortView">
    <Grid>
        <!-- Your UI here -->
    </Grid>
</local:ViewBase>
```

---

### ControlBase

Base class for reusable controls that receive DataContext from parent.

```csharp
public abstract class ControlBase : UserControl
{
    protected Window? ParentWindow { get; }
    protected DiffusionNexusMainWindowViewModel? MainWindowViewModel { get; }
}
```

**When to use:** Reusable controls embedded in views (settings panels, custom inputs).

**Example:**
```csharp
public partial class ProcessingOverlayControl : ControlBase
{
    public ProcessingOverlayControl()
    {
        InitializeComponent();
    }
    // DialogService auto-injected if DataContext is IDialogServiceAware
}
```

---

### IDialogService

Interface for dialog operations (file pickers, message boxes).

```csharp
public interface IDialogService
{
    Task<string?> ShowOpenFileDialogAsync(string title, string? filter = null);
    Task<string?> ShowSaveFileDialogAsync(string title, string? defaultFileName = null, string? filter = null);
    Task<string?> ShowOpenFolderDialogAsync(string title);
    Task ShowMessageAsync(string title, string message);
    Task<bool> ShowConfirmAsync(string title, string message);
}
```

**Testability:** Mock this interface in unit tests to avoid UI dependencies.

---

### ModuleItem

Represents a navigation module in the sidebar.

```csharp
public partial class ModuleItem : ObservableObject
{
    string Name { get; set; }
    string IconPath { get; set; }
    object? View { get; set; }
}
```

**Example - Registering modules:**
```csharp
public DiffusionNexusMainWindowViewModel()
{
    RegisterModule(new ModuleItem("Lora Sort", "avares://DiffusionNexus.UI/Assets/LoraSort.png", new LoraSortView()));
    RegisterModule(new ModuleItem("Prompt Edit", "avares://DiffusionNexus.UI/Assets/PromptEdit.png", new PromptEditView()));
}
```

---

## Quick Start: Adding a New Module

### Step 1: Create the ViewModel

```csharp
// ViewModels/MyFeatureViewModel.cs
namespace DiffusionNexus.UI.ViewModels;

public partial class MyFeatureViewModel : BusyViewModelBase
{
    [ObservableProperty]
    private string _title = "My Feature";
    
    [RelayCommand]
    private async Task DoSomethingAsync()
    {
        await RunBusyAsync(async () =>
        {
            await Task.Delay(1000); // Your async work
        }, "Working...");
    }
}
```

### Step 2: Create the View (AXAML)

```xml
<!-- Views/MyFeatureView.axaml -->
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:DiffusionNexus.UI.ViewModels"
             x:Class="DiffusionNexus.UI.Views.MyFeatureView"
             x:DataType="vm:MyFeatureViewModel">
    
    <Grid Margin="20">
        <StackPanel Spacing="10">
            <TextBlock Text="{Binding Title}" FontSize="24" FontWeight="Bold"/>
            <Button Content="Do Something" Command="{Binding DoSomethingCommand}"/>
            
            <!-- Busy overlay -->
            <TextBlock Text="{Binding BusyMessage}" 
                       IsVisible="{Binding IsBusy}"
                       Foreground="Orange"/>
        </StackPanel>
    </Grid>
</UserControl>
```

### Step 3: Create the View (Code-behind)

```csharp
// Views/MyFeatureView.axaml.cs
namespace DiffusionNexus.UI.Views;

public partial class MyFeatureView : ViewBase<MyFeatureViewModel>
{
    public MyFeatureView()
    {
        InitializeComponent();
    }
}
```

### Step 4: Register the Module

```csharp
// In DiffusionNexusMainWindowViewModel.cs
public DiffusionNexusMainWindowViewModel()
{
    RegisterModule(new ModuleItem(
        "My Feature", 
        "avares://DiffusionNexus.UI/Assets/myfeature.png", 
        new MyFeatureView()));
}
```

---

## Testing Guidelines

### Mocking DialogService

```csharp
public class MyFeatureViewModelTests
{
    [Fact]
    public async Task SelectFolder_SetsSelectedPath()
    {
        // Arrange
        var mockDialog = new Mock<IDialogService>();
        mockDialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>()))
                  .ReturnsAsync("C:\\TestFolder");
        
        var vm = new MyFeatureViewModel { DialogService = mockDialog.Object };
        
        // Act
        await vm.SelectFolderCommand.ExecuteAsync(null);
        
        // Assert
        Assert.Equal("C:\\TestFolder", vm.SelectedPath);
    }
}
```

### Testing Busy States

```csharp
[Fact]
public async Task DoSomething_SetsBusyDuringExecution()
{
    var vm = new MyFeatureViewModel();
    var wasBusy = false;
    
    vm.PropertyChanged += (s, e) =>
    {
        if (e.PropertyName == nameof(vm.IsBusy) && vm.IsBusy)
            wasBusy = true;
    };
    
    await vm.DoSomethingCommand.ExecuteAsync(null);
    
    Assert.True(wasBusy);
    Assert.False(vm.IsBusy); // Should be false after completion
}
```

---

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Avalonia | 11.3.9 | UI Framework |
| Avalonia.Desktop | 11.3.9 | Desktop platform support |
| Avalonia.Themes.Fluent | 11.3.9 | Modern Fluent theme |
| Avalonia.Fonts.Inter | 11.3.9 | Inter font family |
| CommunityToolkit.Mvvm | 8.4.0 | MVVM source generators |

---

## Migration from UI-V1

| V1 Pattern | V2 Pattern |
|------------|------------|
| `UserControl` with manual `AttachedToVisualTree` | `ViewBase<TViewModel>` |
| Manual `DataContext = new ViewModel()` | Automatic in `ViewBase<T>` constructor |
| Manual DialogService injection | Automatic via `IDialogServiceAware` |
| Scattered busy state logic | Centralized in `BusyViewModelBase` |
| Per-control service hookup | Automatic in `ControlBase` |

---

## Conventions

1. **Naming**: ViewModels end with `ViewModel`, Views end with `View`
2. **ViewLocator**: Automatically maps `*ViewModel` to `*View`
3. **Namespace**: All in `DiffusionNexus.UI` (root namespace)
4. **Commands**: Use `[RelayCommand]` attribute from CommunityToolkit.Mvvm
5. **Properties**: Use `[ObservableProperty]` for bindable properties
