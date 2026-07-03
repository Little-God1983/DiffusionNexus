# Feedback Button — DiffusionNexus Main App UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Feedback" entry to the main window's sidebar (next to Youtube/Patreon/Civitai) that opens a dialog, captures a screenshot, collects a title/description (+ optional detail fields) and the last 500 lines of the in-app unified log, and submits it via the SDK's `IFeedbackReportingService`.

**Architecture:** A `FeedbackDialog` (Avalonia `Window`) lives in `DiffusionNexus.UI/Views/Dialogs`, following this app's existing dialog conventions. `IDialogService`/`DialogService` (already the established abstraction every other dialog in this app goes through) gains a new `ShowFeedbackDialogAsync()` method that resolves `IFeedbackReportingService` and `IUnifiedLogger` from `App.Services`, builds the log tail, captures a screenshot of the main window, and shows the dialog — mirroring exactly how `ShowDownloadLoraDialogAsync` resolves its own dependencies internally.

**Tech Stack:** .NET 10 / C#, Avalonia UI, CommunityToolkit.Mvvm (`[RelayCommand]`).

This plan is **Plan C of 3** for the in-app feedback feature (spec: `DiffusionNexus.Installer.SDK/docs/superpowers/specs/2026-07-03-feedback-bug-report-design.md`, in the SDK repo). It depends on **Plan A** (SDK repo, `docs/superpowers/plans/2026-07-03-feedback-sdk-and-relay.md`) being available as a referenced SDK NuGet version, since this app pins a specific SDK NuGet version rather than using a local project reference (per this app's own `.github/copilot-instructions.md` and prior project convention — check the SDK `PackageReference` version in `DiffusionNexus.UI.csproj` and bump it to Plan A's published version, e.g. `1.2.24`, before starting). It also needs **the deployed relay URL from Plan A Task 5** — this plan uses a placeholder that must be swapped for the real one (see Task 3, Step 1). Plan C is independent of **Plan B** (`DiffusionNexus.Installers` repo) — they can be implemented in either order or in parallel once Plan A is done.

## Global Constraints

- This app targets `net10.0`, `WinExe`, `PlatformTarget=x64` (per `DiffusionNexus.UI.csproj`); `AvaloniaUseCompiledBindingsByDefault` is true but existing dialog views in this repo use `x:CompileBindings="False"` on the `Window` root when they use code-behind field access rather than compiled bindings — follow that same pattern for `FeedbackDialog` (it's code-behind driven, not a bound ViewModel, matching `ReplaceDialog`'s simpler siblings — this plan does not add compiled-binding infrastructure).
- All new dialogs go through `IDialogService`, not constructed ad hoc from a ViewModel — this is a hard convention in this codebase (every one of the ~25 `Show*DialogAsync` methods in `IDialogService`/`DialogService` follows it).
- Services not injectable via constructor (because the ViewModel is constructed parameterless, per `DiffusionNexusMainWindowViewModel()`) are resolved via the ambient `App.Services?.GetService<T>()` locator — this is the established pattern used throughout `DiffusionNexusMainWindowViewModel` (see `LoadServerMessagesAsync`, `InitializeStatusBar`). Do not introduce constructor injection for this ViewModel.
- Log tail source: `IUnifiedLogger.GetEntries()` (already registered in DI), each entry formatted via its own `LogEntry.ToDisplayString()`, truncated client-side to the last 500 entries.
- Screenshot cap: match Plan A's service-side guard (4 MB raw PNG bytes) — reuse the same downscaling approach as Plan B's `ScreenshotCapture` (max 1600px longest side). This repo does not reference `DiffusionNexus.Installer.Shared.Avalonia`, so this is a small, deliberate duplication of that helper — not a shared reference (per the approved spec's explicit call-out that two separate UI implementations are expected).
- No automated UI test infrastructure exists for `Window`-based dialogs in this codebase either (none of the ~15 dialog `.axaml.cs` files have a matching test file). Per the approved spec's Testing section, these tasks are verified by `dotnet build` + a manual verification checklist.

---

### Task 1: `ScreenshotCapture` helper

**Files:**
- Create: `DiffusionNexus.UI/Services/ScreenshotCapture.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `public static class ScreenshotCapture { public static byte[] CaptureWindowPng(Window window) }` — consumed by Task 3 (`DialogService.ShowFeedbackDialogAsync`).

- [ ] **Step 1: Implement the helper**

```csharp
// DiffusionNexus.UI/Services/ScreenshotCapture.cs
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Captures a PNG screenshot of an Avalonia window's current visual tree, downscaled
/// if needed to keep the feedback report payload small.
/// </summary>
public static class ScreenshotCapture
{
    private const int MaxDimension = 1600;

    /// <summary>
    /// Renders <paramref name="window"/> to a PNG byte array. If the window is larger
    /// than <see cref="MaxDimension"/> on its longest side, the image is downscaled
    /// (preserving aspect ratio) before encoding.
    /// </summary>
    public static byte[] CaptureWindowPng(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var width = Math.Max(1, (int)window.Bounds.Width);
        var height = Math.Max(1, (int)window.Bounds.Height);
        var pixelSize = new PixelSize(width, height);

        using var fullBitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        fullBitmap.Render(window);

        var largestSide = Math.Max(width, height);
        if (largestSide <= MaxDimension)
        {
            using var stream = new MemoryStream();
            fullBitmap.Save(stream);
            return stream.ToArray();
        }

        var scale = (double)MaxDimension / largestSide;
        var scaledSize = new PixelSize((int)(width * scale), (int)(height * scale));

        using var loadStream = new MemoryStream();
        fullBitmap.Save(loadStream);
        loadStream.Position = 0;

        using var loadedBitmap = new Bitmap(loadStream);
        using var scaledBitmap = loadedBitmap.CreateScaledBitmap(scaledSize);

        using var outStream = new MemoryStream();
        scaledBitmap.Save(outStream);
        return outStream.ToArray();
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build DiffusionNexus.UI`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add DiffusionNexus.UI/Services/ScreenshotCapture.cs
git commit -m "feat: add ScreenshotCapture helper for the feedback dialog"
```

---

### Task 2: `FeedbackDialog` window

**Files:**
- Create: `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml`
- Create: `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml.cs`

**Interfaces:**
- Consumes: `IFeedbackReportingService`, `FeedbackReport`, `FeedbackProduct` from `DiffusionNexus.Installer.SDK.Shared.Services.Feedback` (Plan A, referenced via the SDK NuGet package this app already pins).
- Produces: `public partial class FeedbackDialog : Window` with constructor `FeedbackDialog(IFeedbackReportingService feedbackService, FeedbackProduct product, string appVersion, string logTail, byte[]? initialScreenshot)` — consumed by Task 3 (`DialogService.ShowFeedbackDialogAsync`).

- [ ] **Step 1: Write the XAML**

```xml
<!-- DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml -->
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="DiffusionNexus.UI.Views.Dialogs.FeedbackDialog"
        x:CompileBindings="False"
        Title="Send Feedback"
        Width="520"
        Height="660"
        WindowStartupLocation="CenterOwner"
        CanResize="False">

    <Window.Styles>
        <Style Selector="Window">
            <Setter Property="Background">
                <Setter.Value>
                    <LinearGradientBrush StartPoint="0%,0%" EndPoint="100%,100%">
                        <GradientStop Color="#1a1a2e" Offset="0"/>
                        <GradientStop Color="#16213e" Offset="0.5"/>
                        <GradientStop Color="#0f3460" Offset="1"/>
                    </LinearGradientBrush>
                </Setter.Value>
            </Setter>
        </Style>

        <Style Selector="Button.primary-btn">
            <Setter Property="Background">
                <Setter.Value>
                    <LinearGradientBrush StartPoint="0%,0%" EndPoint="100%,0%">
                        <GradientStop Color="#4CAF50" Offset="0"/>
                        <GradientStop Color="#45a049" Offset="1"/>
                    </LinearGradientBrush>
                </Setter.Value>
            </Setter>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="FontWeight" Value="Bold"/>
            <Setter Property="Padding" Value="24,12"/>
            <Setter Property="CornerRadius" Value="6"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="HorizontalAlignment" Value="Stretch"/>
        </Style>

        <Style Selector="Button.secondary-btn">
            <Setter Property="Background" Value="#3a3a5a"/>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="Padding" Value="20,10"/>
            <Setter Property="CornerRadius" Value="6"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="HorizontalAlignment" Value="Stretch"/>
        </Style>

        <Style Selector="TextBox">
            <Setter Property="Background" Value="#2a2a4a"/>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="BorderBrush" Value="#3a3a5a"/>
            <Setter Property="CornerRadius" Value="4"/>
        </Style>

        <Style Selector="TextBlock.field-label">
            <Setter Property="Foreground" Value="#ccccee"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
        </Style>
    </Window.Styles>

    <Border Padding="24">
        <Grid RowDefinitions="Auto,*">
            <TextBlock Grid.Row="0" Text="Send Feedback" FontSize="22" FontWeight="Bold" Foreground="White" Margin="0,0,0,16"/>

            <ScrollViewer Grid.Row="1">
                <StackPanel Spacing="14">
                    <StackPanel x:Name="FormPanel" Spacing="14">
                        <StackPanel Spacing="4">
                            <TextBlock Classes="field-label" Text="Title *"/>
                            <TextBox x:Name="TitleBox" Watermark="Short summary of the problem"/>
                        </StackPanel>

                        <StackPanel Spacing="4">
                            <TextBlock Classes="field-label" Text="Description *"/>
                            <TextBox x:Name="DescriptionBox" Watermark="Describe the problem" AcceptsReturn="True" Height="70" TextWrapping="Wrap"/>
                        </StackPanel>

                        <StackPanel Spacing="4">
                            <TextBlock Classes="field-label" Text="What happened (optional)"/>
                            <TextBox x:Name="WhatHappenedBox" AcceptsReturn="True" Height="50" TextWrapping="Wrap"/>
                        </StackPanel>

                        <StackPanel Spacing="4">
                            <TextBlock Classes="field-label" Text="What should have happened (optional)"/>
                            <TextBox x:Name="WhatShouldHaveHappenedBox" AcceptsReturn="True" Height="50" TextWrapping="Wrap"/>
                        </StackPanel>

                        <StackPanel Spacing="6">
                            <TextBlock Classes="field-label" Text="Screenshot"/>
                            <Border Background="#2a2a4a" CornerRadius="6" Padding="8">
                                <Image x:Name="ScreenshotPreviewImage" MaxHeight="180" Stretch="Uniform"/>
                            </Border>
                            <Grid ColumnDefinitions="*,10,*">
                                <Button x:Name="ReplaceScreenshotButton" Grid.Column="0" Classes="secondary-btn" Content="Replace Screenshot..."/>
                                <Button x:Name="RemoveScreenshotButton" Grid.Column="2" Classes="secondary-btn" Content="Remove Screenshot"/>
                            </Grid>
                        </StackPanel>

                        <TextBlock x:Name="StatusText" Foreground="#ff8080" TextWrapping="Wrap" IsVisible="False"/>

                        <TextBlock x:Name="SubmittingIndicator" Text="Submitting..." Foreground="#ccccee" IsVisible="False"/>

                        <Grid ColumnDefinitions="*,10,*" Margin="0,10,0,0">
                            <Button x:Name="CancelButton" Grid.Column="0" Classes="secondary-btn" Content="Cancel"/>
                            <Button x:Name="SubmitButton" Grid.Column="2" Classes="primary-btn" Content="Submit"/>
                        </Grid>
                    </StackPanel>

                    <StackPanel x:Name="SuccessPanel" Spacing="12" IsVisible="False">
                        <TextBlock Text="Thanks! Your feedback was submitted." FontSize="16" Foreground="White" FontWeight="SemiBold"/>
                        <TextBlock x:Name="IssueUrlText" Foreground="#88aaff" TextWrapping="Wrap"/>
                        <Button x:Name="IssueUrlButton" Classes="secondary-btn" Content="Open Issue"/>
                        <Button x:Name="CloseAfterSuccessButton" Classes="primary-btn" Content="Close"/>
                    </StackPanel>
                </StackPanel>
            </ScrollViewer>
        </Grid>
    </Border>
</Window>
```

- [ ] **Step 2: Write the code-behind**

```csharp
// DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml.cs
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for composing and submitting an in-app feedback/bug report.
/// </summary>
public partial class FeedbackDialog : Window
{
    private readonly IFeedbackReportingService _feedbackService;
    private readonly FeedbackProduct _product;
    private readonly string _appVersion;
    private readonly string _logTail;
    private byte[]? _screenshotBytes;
    private string? _lastIssueUrl;

    public FeedbackDialog() : this(
        new FeedbackReportingService(new FeedbackReportingServiceOptions { RelayUrl = "https://example.com" }),
        FeedbackProduct.MainApp,
        "0.0.0",
        string.Empty,
        null)
    {
    }

    public FeedbackDialog(
        IFeedbackReportingService feedbackService,
        FeedbackProduct product,
        string appVersion,
        string logTail,
        byte[]? initialScreenshot)
    {
        InitializeComponent();

        _feedbackService = feedbackService;
        _product = product;
        _appVersion = appVersion;
        _logTail = logTail;
        _screenshotBytes = initialScreenshot;

        UpdateScreenshotPreview();

        SubmitButton.Click += OnSubmitClick;
        CancelButton.Click += (_, _) => Close();
        ReplaceScreenshotButton.Click += OnReplaceScreenshotClick;
        RemoveScreenshotButton.Click += (_, _) =>
        {
            _screenshotBytes = null;
            UpdateScreenshotPreview();
        };
        CloseAfterSuccessButton.Click += (_, _) => Close();
        IssueUrlButton.Click += (_, _) =>
        {
            if (_lastIssueUrl is not null) OpenUrl(_lastIssueUrl);
        };
    }

    private void UpdateScreenshotPreview()
    {
        if (_screenshotBytes is { Length: > 0 })
        {
            using var stream = new MemoryStream(_screenshotBytes);
            ScreenshotPreviewImage.Source = new Bitmap(stream);
            ScreenshotPreviewImage.IsVisible = true;
            RemoveScreenshotButton.IsVisible = true;
        }
        else
        {
            ScreenshotPreviewImage.Source = null;
            ScreenshotPreviewImage.IsVisible = false;
            RemoveScreenshotButton.IsVisible = false;
        }
    }

    private async void OnReplaceScreenshotClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a screenshot",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }]
        });

        var file = files.FirstOrDefault();
        if (file is null) return;

        await using var stream = await file.OpenReadAsync();
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        _screenshotBytes = memoryStream.ToArray();
        UpdateScreenshotPreview();
    }

    private async void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text?.Trim() ?? string.Empty;
        var description = DescriptionBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
        {
            StatusText.Text = "Title and description are required.";
            StatusText.IsVisible = true;
            return;
        }

        SetSubmitting(true);

        var report = new FeedbackReport
        {
            Product = _product,
            Title = title,
            Description = description,
            WhatHappened = string.IsNullOrWhiteSpace(WhatHappenedBox.Text) ? null : WhatHappenedBox.Text!.Trim(),
            WhatShouldHaveHappened = string.IsNullOrWhiteSpace(WhatShouldHaveHappenedBox.Text) ? null : WhatShouldHaveHappenedBox.Text!.Trim(),
            ScreenshotPng = _screenshotBytes,
            LogTail = _logTail,
            AppVersion = _appVersion,
            Os = RuntimeInformation.OSDescription,
            TimestampUtc = DateTimeOffset.UtcNow
        };

        var result = await _feedbackService.SubmitAsync(report);

        SetSubmitting(false);

        if (result.Success)
        {
            ShowSuccess(result.IssueUrl!);
        }
        else
        {
            StatusText.Text = $"Couldn't submit feedback: {result.ErrorMessage}. Your text hasn't been lost — try again.";
            StatusText.IsVisible = true;
        }
    }

    private void SetSubmitting(bool submitting)
    {
        SubmitButton.IsEnabled = !submitting;
        CancelButton.IsEnabled = !submitting;
        SubmittingIndicator.IsVisible = submitting;
    }

    private void ShowSuccess(string issueUrl)
    {
        _lastIssueUrl = issueUrl;
        FormPanel.IsVisible = false;
        SuccessPanel.IsVisible = true;
        IssueUrlText.Text = issueUrl;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start("xdg-open", url);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
        }
        catch
        {
            // Ignore URL open failures
        }
    }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build DiffusionNexus.UI`
Expected: Build succeeded, 0 errors. (If `FeedbackReport`/`IFeedbackReportingService`/etc. are not found, the `DiffusionNexus.Installer.SDK.*` `PackageReference` versions in `DiffusionNexus.UI.csproj` need to be bumped to Plan A's published version first.)

- [ ] **Step 4: Commit**

```bash
git add DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml.cs
git commit -m "feat: add FeedbackDialog view for in-app bug reports"
```

---

### Task 3: Wire `IDialogService`, DI registration, and the sidebar command/button

**Files:**
- Modify: `DiffusionNexus.UI/Services/IDialogService.cs`
- Modify: `DiffusionNexus.UI/Services/DialogService.cs`
- Modify: `DiffusionNexus.UI/App.axaml.cs`
- Modify: `DiffusionNexus.UI/ViewModels/DiffusionNexusMainWindowViewModel.cs`
- Modify: `DiffusionNexus.UI/Views/DiffusionNexusMainWindow.axaml`

**Interfaces:**
- Consumes: `ScreenshotCapture` (Task 1), `FeedbackDialog` (Task 2), `IFeedbackReportingService`/`FeedbackProduct` (Plan A), `IUnifiedLogger`/`LogEntry.ToDisplayString()` (already exists in this repo).
- Produces: `DiffusionNexusMainWindowViewModel.OpenFeedbackCommand` (bindable from XAML) — last task in this plan, nothing downstream consumes it.

- [ ] **Step 1: Register the SDK service in DI**

In `DiffusionNexus.UI/App.axaml.cs`, add this registration right after the `DismissedMessageStore` registration (after line 846's closing `});`, i.e. right before `// Configuration checker (singleton - accessible across the entire application)`):

```csharp
        // Feedback reporting service (posts to the Cloudflare Worker relay, which holds
        // the GitHub credential — see docs/superpowers/plans/2026-07-03-feedback-sdk-and-relay.md
        // in the DiffusionNexus.Installer.SDK repo).
        services.AddSingleton<IFeedbackReportingService>(_ => new FeedbackReportingService(
            new FeedbackReportingServiceOptions
            {
                // TODO: replace with the real deployed relay URL from Plan A Task 5's output.
                RelayUrl = "https://diffusionnexus-feedback-relay.example.workers.dev"
            }));
```

Add the required `using` directive near the other `DiffusionNexus.Installer.SDK.Shared.Services` usings at the top of `App.axaml.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
```

**This TODO must be resolved before shipping** — swap the placeholder `RelayUrl` for the actual URL Plan A's Task 5 deployed.

- [ ] **Step 2: Add the method to `IDialogService`**

In `DiffusionNexus.UI/Services/IDialogService.cs`, insert this right before the interface's closing brace (after `Task ShowImageQualityFixerAsync(...)` at line 387, before the `}` at line 388):

```csharp

    /// <summary>
    /// Captures a screenshot of the main window and shows the in-app feedback dialog,
    /// pre-filled with it and with the last 500 lines of the unified log.
    /// </summary>
    Task ShowFeedbackDialogAsync();
```

- [ ] **Step 3: Implement it in `DialogService`**

In `DiffusionNexus.UI/Services/DialogService.cs`, add this method right after `ShowImageQualityFixerAsync` (after line 622's closing `}`, before the class's own closing brace):

```csharp

    public async Task ShowFeedbackDialogAsync()
    {
        var feedbackService = App.Services?.GetService<IFeedbackReportingService>();
        if (feedbackService is null) return;

        var unifiedLogger = App.Services?.GetService<Domain.Services.UnifiedLogging.IUnifiedLogger>();
        var logTail = BuildLogTail(unifiedLogger);

        var appVersion = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";

        byte[]? screenshot = null;
        try
        {
            screenshot = ScreenshotCapture.CaptureWindowPng(_window);
        }
        catch
        {
            // Screenshot capture is best-effort — the dialog works fine without one.
        }

        var dialog = new FeedbackDialog(
            feedbackService,
            DiffusionNexus.Installer.SDK.Shared.Services.Feedback.FeedbackProduct.MainApp,
            appVersion,
            logTail,
            screenshot);

        await dialog.ShowDialog(_window);
    }

    private static string BuildLogTail(Domain.Services.UnifiedLogging.IUnifiedLogger? logger)
    {
        if (logger is null) return string.Empty;

        var entries = logger.GetEntries();
        var tail = entries.Count <= 500 ? entries : entries.Skip(entries.Count - 500);
        return string.Join('\n', tail.Select(e => e.ToDisplayString()));
    }
```

Add the required `using` directive at the top of `DialogService.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
```

- [ ] **Step 4: Add the command to `DiffusionNexusMainWindowViewModel`**

In `DiffusionNexus.UI/ViewModels/DiffusionNexusMainWindowViewModel.cs`, add this command next to `OpenSettings` (after line 357's closing `}`, before the next `[RelayCommand]` at line 359):

```csharp

    [RelayCommand]
    private async Task OpenFeedbackAsync()
    {
        var dialogService = App.Services?.GetService<IDialogService>();
        if (dialogService is null) return;

        await dialogService.ShowFeedbackDialogAsync();
    }
```

- [ ] **Step 5: Add the button to the sidebar**

In `DiffusionNexus.UI/Views/DiffusionNexusMainWindow.axaml`, add a new button to the "Bottom Social Links" `StackPanel`, right after the Civitai button (after line 81's closing `</Button>`, before line 82's closing `</StackPanel>`):

```xml
                                <Button Width="196" Height="48" Margin="2,2"
                                        Command="{Binding OpenFeedbackCommand}"
                                        HorizontalContentAlignment="Left">
                                    <StackPanel Orientation="Horizontal" Spacing="8">
                                        <Border Width="32" Height="32" Background="#3C3C3C" CornerRadius="4">
                                            <TextBlock Text="💬" FontSize="18"
                                                       HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                        </Border>
                                        <TextBlock Text="Feedback" VerticalAlignment="Center"
                                                   IsVisible="{Binding IsMenuOpen}"/>
                                    </StackPanel>
                                </Button>
```

- [ ] **Step 6: Build to verify it compiles**

Run: `dotnet build DiffusionNexus.UI`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Manual verification**

Run: `dotnet run --project DiffusionNexus.UI`
Expected, walking through by hand:
1. Launch the app, confirm a "💬 Feedback" button appears in the sidebar directly below Civitai.
2. Click it — the `FeedbackDialog` opens, pre-populated with a screenshot of the current window.
3. Try submitting with an empty title — confirm the inline validation message appears and nothing is sent.
4. Fill in Title + Description, click Submit — confirm the "Submitting..." indicator appears, then either a success panel with a clickable issue link (if the relay is deployed and reachable) or a friendly error message that preserves your typed text (if not).
5. Click "Replace Screenshot..." and pick a local image file — confirm the preview updates.
6. Click "Remove Screenshot" — confirm the preview clears.
7. Toggle the sidebar collapsed (hamburger icon) — confirm the Feedback icon still shows (label hides, matching the other sidebar buttons).

- [ ] **Step 8: Commit**

```bash
git add DiffusionNexus.UI/Services/IDialogService.cs DiffusionNexus.UI/Services/DialogService.cs DiffusionNexus.UI/App.axaml.cs DiffusionNexus.UI/ViewModels/DiffusionNexusMainWindowViewModel.cs DiffusionNexus.UI/Views/DiffusionNexusMainWindow.axaml
git commit -m "feat: wire the feedback button into the main window sidebar"
```
