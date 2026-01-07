# LoRA Dataset Helper Refactoring Documentation

## Overview

The LoRA Dataset Helper module was refactored to address state synchronization issues and improve architecture. The main problems solved:

1. **State not updating across components** - Changes made in one tab (e.g., Image Edit) were not reflected in other tabs (e.g., Dataset Management)
2. **Tight coupling** - Components were directly dependent on each other
3. **No clear separation of concerns** - The main ViewModel was handling too many responsibilities
4. **Code duplication** - File extension arrays and type checking methods were duplicated across multiple files

## Architecture

### Design Patterns Used

#### 1. Event Aggregator Pattern (Pub/Sub)
Enables loose coupling between components. Any component can publish events, and any component can subscribe without knowing about each other.

```
???????????????????????????????????????????????????????????????????
?                    IDatasetEventAggregator                       ?
?  (Central message bus for cross-component communication)        ?
???????????????????????????????????????????????????????????????????
?  Publishers:                    Subscribers:                     ?
?  - DatasetManagementViewModel   - ImageEditTabViewModel          ?
?  - ImageEditTabViewModel        - DatasetManagementViewModel     ?
?  - ImageViewerViewModel         - Any future components          ?
?  - DatasetImageViewModel        ?                                ?
?  - ImageEditorViewModel         ?                                ?
???????????????????????????????????????????????????????????????????
```

#### 2. Shared State Pattern
Centralized state management via `IDatasetState` service.

```
???????????????????????????????????????????????????????????????????
?                      IDatasetState                               ?
?  (Single source of truth for all dataset-related state)         ?
???????????????????????????????????????????????????????????????????
?  - ActiveDataset          - DatasetImages                        ?
?  - IsViewingDataset       - AvailableCategories                  ?
?  - SelectionCount         - EditorDatasetImages                  ?
?  - SelectedEditorImage    - StatusMessage                        ?
???????????????????????????????????????????????????????????????????
```

#### 3. Coordinator Pattern
`LoraDatasetHelperViewModel` acts as a coordinator, delegating work to specialized tab ViewModels.

```
???????????????????????????????????????????????????????????????????
?              LoraDatasetHelperViewModel (Coordinator)            ?
?  - Manages tab switching                                         ?
?  - Forwards DialogService to children                            ?
?  - Subscribes to navigation events                               ?
???????????????????????????????????????????????????????????????????
?         ?                              ?                         ?
?         ?                              ?                         ?
?  ????????????????????      ???????????????????????              ?
?  ?DatasetManagement ?      ? ImageEditTabViewModel?              ?
?  ?   ViewModel      ?      ?                      ?              ?
?  ????????????????????      ???????????????????????              ?
???????????????????????????????????????????????????????????????????
```

#### 4. DRY Principle - Centralized Utilities
`MediaFileExtensions` utility class consolidates all file type constants and helper methods.

## Key Implementation Details

### MediaFileExtensions Utility Class
**Location:** `DiffusionNexus.UI-V2/Utilities/MediaFileExtensions.cs`

Centralizes all file type detection logic that was previously duplicated across multiple ViewModels:

```csharp
public static class MediaFileExtensions
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];
    public static readonly string[] VideoExtensions = [".mp4", ".mov", ".webm", ".avi", ".mkv", ".wmv", ".flv", ".m4v"];
    public static readonly string[] CaptionExtensions = [".txt", ".caption"];
    public static readonly string[] MediaExtensions = [..ImageExtensions, ..VideoExtensions];

    public static bool IsImageFile(string filePath);
    public static bool IsVideoFile(string filePath);
    public static bool IsMediaFile(string filePath);
    public static bool IsCaptionFile(string filePath);
    public static bool IsVideoThumbnailFile(string filePath);
    public static string GetVideoThumbnailPath(string videoPath);
    public static bool IsDisplayableMediaFile(string filePath);
}
```

**Benefits:**
- Single source of truth for file type constants
- Eliminates code duplication across ViewModels
- Easy to add new file types in one place
- Consistent behavior across all components

### Instance Synchronization Pattern

**Problem:** Different tabs create their own `DatasetImageViewModel` instances from the file system. When you "Send to Editor", the `ImageEditTabViewModel` creates **new** instances. Rating changes on one instance don't automatically update the other.

**Solution:** Event handlers find matching instances by file path and sync their state:

```csharp
private void OnImageRatingChanged(object? sender, ImageRatingChangedEventArgs e)
{
    // Find matching image by file path and sync the rating
    var matchingImage = DatasetImages.FirstOrDefault(img =>
        string.Equals(img.ImagePath, e.Image.ImagePath, StringComparison.OrdinalIgnoreCase));

    if (matchingImage is not null && matchingImage != e.Image)
    {
        // Update our instance - triggers PropertyChanged for UI update
        matchingImage.RatingStatus = e.NewRating;
    }
}
```

This pattern ensures that:
- Rating changes in Image Editor ? update Dataset Management grid
- Rating changes in ImageViewer ? update Dataset Management grid
- Rating changes in Dataset Management ? update Image Editor (if same image is being edited)

### Proper Resource Cleanup with IDisposable

All ViewModels that subscribe to events implement `IDisposable` to prevent memory leaks:

```csharp
public partial class LoraDatasetHelperViewModel : ViewModelBase, IDialogServiceAware, IDisposable
{
    private bool _disposed;

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // Unsubscribe from events
            _state.StateChanged -= OnStateChanged;
            _eventAggregator.NavigateToImageEditorRequested -= OnNavigateToImageEditor;

            // Dispose child ViewModels
            (DatasetManagement as IDisposable)?.Dispose();
            (ImageEdit as IDisposable)?.Dispose();
        }

        _disposed = true;
    }
}
```

## Components

### Services

#### IDatasetEventAggregator / DatasetEventAggregator
**Location:** `DiffusionNexus.UI-V2/Services/DatasetEventAggregator.cs`

Central event bus for publishing and subscribing to dataset-related events.

**Events Published:**
| Event | When Raised | Data |
|-------|-------------|------|
| `ActiveDatasetChanged` | Dataset selection changes | Current/Previous dataset |
| `DatasetCreated` | New dataset created | Dataset info |
| `DatasetDeleted` | Dataset deleted | Dataset info, version |
| `DatasetMetadataChanged` | Category/Type/Description changes | Dataset, change type |
| `DatasetImagesLoaded` | Images loaded for a dataset | Dataset, image list |
| `ImageAdded` | Images added to dataset | Dataset, added images |
| `ImageDeleted` | Image deleted | Dataset, image path |
| `ImageSaved` | Image saved (from editor) | Image path, original path |
| `ImageRatingChanged` | Rating status changes | Image, new/previous rating |
| `CaptionChanged` | Caption modified | Image, was saved flag |
| `VersionCreated` | New version created | Dataset, version info |
| `NavigateToImageEditorRequested` | "Send to Editor" clicked | Image, dataset |

**Usage Example:**
```csharp
// Subscribe (in constructor)
_eventAggregator.ImageRatingChanged += OnImageRatingChanged;

// Publish
_eventAggregator.PublishImageRatingChanged(new ImageRatingChangedEventArgs
{
    Image = image,
    NewRating = ImageRatingStatus.Approved,
    PreviousRating = ImageRatingStatus.Unrated
});

// Cleanup (in Dispose or destructor)
_eventAggregator.ImageRatingChanged -= OnImageRatingChanged;
```

#### IDatasetState / DatasetStateService
**Location:** `DiffusionNexus.UI-V2/Services/DatasetStateService.cs`

Singleton service that holds all shared state for the LoRA Dataset Helper.

**Key Properties:**
| Property | Type | Description |
|----------|------|-------------|
| `ActiveDataset` | `DatasetCardViewModel?` | Currently open dataset |
| `IsViewingDataset` | `bool` | True when inside a dataset |
| `DatasetImages` | `ObservableCollection<DatasetImageViewModel>` | Images in active dataset |
| `SelectionCount` | `int` | Number of selected images |
| `SelectedEditorImage` | `DatasetImageViewModel?` | Image being edited |
| `StatusMessage` | `string?` | Current status text |

**State Change Notification:**
The service raises `StateChanged` events with the property name, allowing ViewModels to react to specific changes.

### ViewModels

#### LoraDatasetHelperViewModel
**Location:** `DiffusionNexus.UI-V2/ViewModels/LoraDatasetHelperViewModel.cs`

Main coordinator ViewModel. Minimal responsibilities:
- Tab index management
- DialogService forwarding to child ViewModels
- Navigation event handling

**Dependencies:**
- `IAppSettingsService` - For settings access
- `IDatasetEventAggregator` - For event subscription
- `IDatasetState` - For state access
- `IVideoThumbnailService` - For video thumbnail generation

#### DatasetManagementViewModel
**Location:** `DiffusionNexus.UI-V2/ViewModels/Tabs/DatasetManagementViewModel.cs`

Handles the Dataset Management tab functionality:
- Dataset listing, creation, deletion
- Image/video management
- Selection and bulk operations
- Version management
- Export functionality

**Key Event Publishing:**
- Publishes `DatasetCreated`, `DatasetDeleted` when managing datasets
- Publishes `ImageRatingChanged` for bulk rating operations
- Publishes `NavigateToImageEditor` when sending images to editor

#### ImageEditTabViewModel
**Location:** `DiffusionNexus.UI-V2/ViewModels/Tabs/ImageEditTabViewModel.cs`

Handles the Image Edit tab functionality:
- Dataset/version/image selection for editing
- Image editor integration
- Save operations

**Key Event Subscriptions:**
- Subscribes to `NavigateToImageEditorRequested` to receive images
- Subscribes to `DatasetImagesLoaded` to update available images

**Key Event Publishing:**
- Publishes `ImageSaved` when saving edited images

### Views

#### LoraDatasetHelperView
**Location:** `DiffusionNexus.UI-V2/Views/LoraDatasetHelperView.axaml`

Main view containing TabControl with two tabs:
1. Dataset Management
2. Image Edit

**Binding Changes:**
All bindings now use sub-ViewModel paths:
```xml
<!-- Before (broken) -->
<ListBox ItemsSource="{Binding GroupedDatasets}" />

<!-- After (correct) -->
<ListBox ItemsSource="{Binding DatasetManagement.GroupedDatasets}" />
```

## Dependency Injection Registration

**Location:** `DiffusionNexus.UI-V2/App.axaml.cs`

```csharp
// Dataset Helper services (singletons - shared state across all components)
services.AddSingleton<IDatasetEventAggregator, DatasetEventAggregator>();
services.AddSingleton<IDatasetState, DatasetStateService>();

// ViewModels (scoped to app lifetime)
services.AddScoped<LoraDatasetHelperViewModel>();
```

**Why Singletons?**
- `IDatasetEventAggregator` - Must be same instance so all publishers/subscribers share the same event bus
- `IDatasetState` - Single source of truth; all components must see the same state

## Data Flow Examples

### Example 1: Rating an Image in ImageViewer Updates Dataset Management

```
????????????????????     ???????????????????????     ??????????????????????
? ImageViewerVM    ?     ? EventAggregator     ?     ? DatasetManagement  ?
?                  ?     ?                     ?     ? ViewModel          ?
????????????????????     ???????????????????????     ??????????????????????
? User clicks      ??????? PublishImageRating  ??????? OnImageRating      ?
? "Approve"        ?     ? Changed()           ?     ? Changed()          ?
?                  ?     ?                     ?     ? (updates counts)   ?
????????????????????     ???????????????????????     ??????????????????????
```

### Example 2: Sending Image to Editor

```
????????????????????     ???????????????????????     ??????????????????????
? DatasetManagement?     ? EventAggregator     ?     ? ImageEditTab       ?
? ViewModel        ?     ?                     ?     ? ViewModel          ?
????????????????????     ???????????????????????     ??????????????????????
? SendToImageEdit()??????? PublishNavigateTo   ??????? OnNavigateTo       ?
?                  ?     ? ImageEditor()       ?     ? ImageEditor()      ?
?                  ?     ?                     ?     ? (loads image)      ?
????????????????????     ???????????????????????     ??????????????????????
                                   ?
                                   ?
                         ???????????????????????
                         ? LoraDatasetHelper   ?
                         ? ViewModel           ?
                         ???????????????????????
                         ? OnNavigateTo        ?
                         ? ImageEditor()       ?
                         ? (switches tab)      ?
                         ???????????????????????
```

### Example 3: Saving Image in Editor Updates Dataset

```
????????????????????     ???????????????????????     ??????????????????????
? ImageEditTabVM   ?     ? EventAggregator     ?     ? DatasetManagement  ?
?                  ?     ?                     ?     ? ViewModel          ?
????????????????????     ???????????????????????     ??????????????????????
? SaveImageAsync() ??????? PublishImageSaved() ??????? OnImageSaved()     ?
?                  ?     ?                     ?     ? (refreshes list)   ?
????????????????????     ???????????????????????     ??????????????????????
```

## SOLID Principles Applied

### Single Responsibility (S)
- `LoraDatasetHelperViewModel` - Only coordinates tabs
- `DatasetManagementViewModel` - Only manages datasets
- `ImageEditTabViewModel` - Only handles image editing
- `DatasetEventAggregator` - Only routes events
- `DatasetStateService` - Only manages state
- `MediaFileExtensions` - Only handles file type detection

### Open/Closed (O)
- New event types can be added without modifying existing subscribers
- New tabs can subscribe to events without changing publishers
- New file types can be added to MediaFileExtensions without changing consumers

### Liskov Substitution (L)
- All ViewModels derive from `ObservableObject` or `ViewModelBase`
- Services implement interfaces (`IDatasetEventAggregator`, `IDatasetState`)

### Interface Segregation (I)
- `IDialogServiceAware` - Only for ViewModels needing dialogs
- `IBusyViewModel` - Only for ViewModels with loading states
- `IDisposable` - Only for ViewModels that need cleanup

### Dependency Inversion (D)
- ViewModels depend on interfaces (`IDatasetEventAggregator`, `IDatasetState`)
- Concrete implementations injected via DI

## DRY Principles Applied

### Centralized File Type Detection
Before refactoring:
- `DatasetCardViewModel` had its own `ImageExtensions`, `VideoExtensions`, `MediaExtensions`, `CaptionExtensions`
- `DatasetManagementViewModel` had duplicated arrays
- `DatasetImageViewModel` had duplicated `VideoExtensions`

After refactoring:
- All use `MediaFileExtensions` utility class
- Single source of truth
- Consistent behavior

### Centralized Rating Logic
Before refactoring:
```csharp
private void MarkApproved()
{
    var previousRating = _ratingStatus;
    RatingStatus = _ratingStatus == ImageRatingStatus.Approved 
        ? ImageRatingStatus.Unrated 
        : ImageRatingStatus.Approved;
    SaveRating();
    _eventAggregator?.PublishImageRatingChanged(...);
}

private void MarkRejected()
{
    // Same pattern duplicated
}

private void ClearRating()
{
    // Same pattern duplicated
}
```

After refactoring:
```csharp
private void MarkApproved() 
    => SetRatingAndPublish(IsApproved ? ImageRatingStatus.Unrated : ImageRatingStatus.Approved);

private void MarkRejected() 
    => SetRatingAndPublish(IsRejected ? ImageRatingStatus.Unrated : ImageRatingStatus.Rejected);

private void ClearRating() 
    => SetRatingAndPublish(ImageRatingStatus.Unrated);

private void SetRatingAndPublish(ImageRatingStatus newRating)
{
    var previousRating = _ratingStatus;
    RatingStatus = newRating;
    SaveRating();
    _eventAggregator?.PublishImageRatingChanged(...);
}
```

### Centralized Version Loading
`ImageEditTabViewModel` now uses `PopulateVersionItemsAsync` shared method for both initial load and refresh.

## File Locations Summary

| Component | Path |
|-----------|------|
| MediaFileExtensions (NEW) | `UI-V2/Utilities/MediaFileExtensions.cs` |
| IDatasetEventAggregator | `UI-V2/Services/DatasetEventAggregator.cs` |
| IDatasetState | `UI-V2/Services/IDatasetState.cs` |
| DatasetStateService | `UI-V2/Services/DatasetStateService.cs` |
| LoraDatasetHelperViewModel | `UI-V2/ViewModels/LoraDatasetHelperViewModel.cs` |
| DatasetManagementViewModel | `UI-V2/ViewModels/Tabs/DatasetManagementViewModel.cs` |
| ImageEditTabViewModel | `UI-V2/ViewModels/Tabs/ImageEditTabViewModel.cs` |
| DatasetCardViewModel | `UI-V2/ViewModels/DatasetCardViewModel.cs` |
| DatasetImageViewModel | `UI-V2/ViewModels/DatasetImageViewModel.cs` |
| LoraDatasetHelperView | `UI-V2/Views/LoraDatasetHelperView.axaml` |
| App (DI Registration) | `UI-V2/App.axaml.cs` |

## Dependency Injection Registration

**Location:** `DiffusionNexus.UI-V2/App.axaml.cs`

```csharp
// Dataset Helper services (singletons - shared state across all components)
services.AddSingleton<IDatasetEventAggregator, DatasetEventAggregator>();
services.AddSingleton<IDatasetState, DatasetStateService>();

// ViewModels (scoped to app lifetime)
services.AddScoped<LoraDatasetHelperViewModel>();
```

## Migration Notes

### Breaking Changes
1. All XAML bindings in `LoraDatasetHelperView.axaml` now require `DatasetManagement.` or `ImageEdit.` prefix
2. `LoraDatasetHelperViewModel` constructor requires `IDatasetEventAggregator` and `IDatasetState`

### Required DI Registrations
```csharp
services.AddSingleton<IDatasetEventAggregator, DatasetEventAggregator>();
services.AddSingleton<IDatasetState, DatasetStateService>();
```

### Disposal
Views should dispose their ViewModels when unloaded to prevent memory leaks from event subscriptions.

## Code Quality Improvements

### Removed Code Duplication
- File extension arrays consolidated to `MediaFileExtensions`
- Rating change logic consolidated to `SetRatingAndPublish` helper
- Version loading logic consolidated to `PopulateVersionItemsAsync`

### Removed Unnecessary Async/Await
- `MigrateLegacyToVersionedAsync` now returns `Task.CompletedTask` instead of using `await`
- `ExportAsSingleFiles` and `ExportAsZip` are now synchronous (they were faking async)

### Added Proper Resource Cleanup
- All event-subscribing ViewModels implement `IDisposable`
- Event handlers are unsubscribed in `Dispose()`
- Child ViewModels are disposed by parent

## Future Extensions

The architecture supports easy extension for:
1. **New tabs** - Create new ViewModel, subscribe to relevant events, implement IDisposable
2. **External integration** - Other modules can subscribe to dataset events
3. **Undo/Redo** - Events can be captured for history
4. **Logging/Analytics** - Subscribe to events for tracking
5. **Testing** - Mock `IDatasetEventAggregator` and `IDatasetState` for unit tests
6. **New file types** - Add to `MediaFileExtensions` in one place
