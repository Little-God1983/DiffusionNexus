# Feedback v4 — Dialog: Screenshot Removal, Bug-Only Log, Red Disclaimer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove screenshot support from the feedback dialog, attach the log tail only to bug reports, and restyle the disclaimer as a red-accented warning with the new wording.

**Architecture:** One cohesive task — the XAML is a full-file replacement (two-column field redistribution + red disclaimer land together), with matched code-behind edits, a slimmed `DialogService`, and deletion of the now-orphaned `ScreenshotCapture` helper. SDK and `IDialogService` interface are untouched (no republish, IntegrationTests stub unaffected).

**Tech Stack:** .NET 10 / C#, Avalonia UI.

**Spec:** `DiffusionNexus.Installer.SDK/docs/superpowers/specs/2026-07-05-feedback-v4-private-repo-no-screenshots-design.md` (SDK repo, branch `feature/feedback-report-type-email`).

## Global Constraints

- Work in the worktree `E:\Repos\DiffusionNexus\.claude\worktrees\feature+feedback-main-app-ui`, branch `feature/feedback-main-app-ui` (PR #396) — commit directly.
- Window stays **1040×800**, `CanResize="False"`. Left column: report type, Title, Description. Right column: What happened, What should have happened, E-mail. Disclaimer full-width below, then StatusText/SubmittingIndicator/buttons. SuccessPanel unchanged.
- Disclaimer style, exact: `Border Background="#1E1E1E" BorderBrush="#FF6B6B" BorderThickness="1" CornerRadius="4" Padding="16"`; bold red lead line `Before you submit` in `#FF6B6B` (matching the first-launch disclaimer's red accent); checkbox behavior unchanged (unchecked on open and after every "Submit another", gates Submit via `UpdateSubmitEnabled()`).
- Disclaimer body, exact: `Bug reports include the last 500 lines of the application log to help with debugging — these can contain local file and folder names, for example downloaded LoRA or model filenames. Feedback and feature requests don't include the log. Screenshots aren't supported, so no unintended images can end up in a report.`
- Checkbox label, exact (unchanged): `I understand and accept this`
- Log tail: `FeedbackReport.LogTail` is set **only when the selected type is Bug** (`reportType == FeedbackReportType.Bug ? _logTail : null`). `DialogService` still computes the log tail unconditionally and passes it in — the dialog decides at submit time (the radio can change while the dialog is open).
- After the change, no reference to `ScreenshotCapture`, `_screenshotBytes`, `ScreenshotPng`, `UpdateScreenshotPreview`, `OnReplaceScreenshotClick`, `ScreenshotPreviewImage`, `ReplaceScreenshotButton`, or `RemoveScreenshotButton` may remain anywhere in `DiffusionNexus.UI`.
- No automated dialog test infra (v1-v3 precedent): verified by `dotnet build` + full `DiffusionNexus.Tests` suite + manual checklist.

---

### Task 1: Dialog v4

**Files:**
- Modify: `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml` (full replacement below)
- Modify: `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml.cs`
- Modify: `DiffusionNexus.UI/Services/DialogService.cs` (`ShowFeedbackDialogAsync`)
- Delete: `DiffusionNexus.UI/Services/ScreenshotCapture.cs` (after the grep in Step 4 confirms it's orphaned)

**Interfaces:**
- Consumes: existing `UpdateSubmitEnabled()`/`DisclaimerCheckBox`/`_isSubmitting` (v3), `IAppSettingsService.Get/SetFeedbackReporterEmailAsync` (unchanged), SDK 1.2.25 types.
- Produces: `FeedbackDialog(IFeedbackReportingService feedbackService, FeedbackProduct product, string appVersion, string logTail, string? initialEmail)` — 5-parameter constructor (screenshot parameter removed). Nothing downstream.

- [ ] **Step 1: Replace the XAML in full**

Replace `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml` with:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="using:DiffusionNexus.UI.Views.Dialogs"
        x:Class="DiffusionNexus.UI.Views.Dialogs.FeedbackDialog"
        x:DataType="local:FeedbackDialog"
        Title="Send Feedback"
        Width="1040"
        Height="800"
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

        <Style Selector="RadioButton">
            <Setter Property="Foreground" Value="White"/>
        </Style>
    </Window.Styles>

    <Border Padding="24">
        <Grid RowDefinitions="Auto,*">
            <TextBlock Grid.Row="0" Text="Send Feedback" FontSize="22" FontWeight="Bold" Foreground="White" Margin="0,0,0,16"/>

            <ScrollViewer Grid.Row="1">
                <StackPanel Spacing="14">
                    <StackPanel x:Name="FormPanel" Spacing="14">
                        <Grid ColumnDefinitions="*,24,*">
                            <!-- Left column: report type + title + description -->
                            <StackPanel Grid.Column="0" Spacing="14">
                                <StackPanel Spacing="6">
                                    <TextBlock Classes="field-label" Text="Report type"/>
                                    <StackPanel Orientation="Horizontal" Spacing="18">
                                        <RadioButton x:Name="TypeFeedbackRadio" GroupName="ReportType" Content="Feedback"/>
                                        <RadioButton x:Name="TypeBugRadio" GroupName="ReportType" Content="Bug report" IsChecked="True"/>
                                        <RadioButton x:Name="TypeFeatureRadio" GroupName="ReportType" Content="Feature request"/>
                                    </StackPanel>
                                </StackPanel>

                                <StackPanel Spacing="4">
                                    <TextBlock Classes="field-label" Text="Title *"/>
                                    <TextBox x:Name="TitleBox" Watermark="Short summary of the problem"/>
                                </StackPanel>

                                <StackPanel Spacing="4">
                                    <TextBlock Classes="field-label" Text="Description *"/>
                                    <TextBox x:Name="DescriptionBox" Watermark="Describe the problem" AcceptsReturn="True" Height="150" TextWrapping="Wrap"/>
                                </StackPanel>
                            </StackPanel>

                            <!-- Right column: optional details + e-mail -->
                            <StackPanel Grid.Column="2" Spacing="14">
                                <StackPanel Spacing="4">
                                    <TextBlock Classes="field-label" Text="What happened (optional)"/>
                                    <TextBox x:Name="WhatHappenedBox" AcceptsReturn="True" Height="70" TextWrapping="Wrap"/>
                                </StackPanel>

                                <StackPanel Spacing="4">
                                    <TextBlock Classes="field-label" Text="What should have happened (optional)"/>
                                    <TextBox x:Name="WhatShouldHaveHappenedBox" AcceptsReturn="True" Height="70" TextWrapping="Wrap"/>
                                </StackPanel>

                                <StackPanel Spacing="4">
                                    <TextBlock Classes="field-label" Text="E-mail (optional — in case we have questions)"/>
                                    <TextBox x:Name="EmailBox" Watermark="you@example.com"/>
                                </StackPanel>
                            </StackPanel>
                        </Grid>

                        <Border Background="#1E1E1E" BorderBrush="#FF6B6B" BorderThickness="1" CornerRadius="4" Padding="16">
                            <StackPanel Spacing="12">
                                <TextBlock Text="Before you submit" FontWeight="Bold" FontSize="14" Foreground="#FF6B6B"/>
                                <TextBlock TextWrapping="Wrap" FontSize="13" Foreground="#ccccee" LineHeight="20">
                                    Bug reports include the last 500 lines of the application log to help with debugging — these can contain local file and folder names, for example downloaded LoRA or model filenames. Feedback and feature requests don't include the log. Screenshots aren't supported, so no unintended images can end up in a report.
                                </TextBlock>
                                <CheckBox x:Name="DisclaimerCheckBox" Content="I understand and accept this" Foreground="White" FontSize="13"/>
                            </StackPanel>
                        </Border>

                        <TextBlock x:Name="StatusText" Foreground="#ff8080" TextWrapping="Wrap" IsVisible="False"/>

                        <TextBlock x:Name="SubmittingIndicator" Text="Submitting..." Foreground="#ccccee" IsVisible="False"/>

                        <Grid ColumnDefinitions="*,10,*" Margin="0,10,0,0">
                            <Button x:Name="CancelButton" Grid.Column="0" Classes="secondary-btn" Content="Cancel"/>
                            <Button x:Name="SubmitButton" Grid.Column="2" Classes="primary-btn" Content="Submit"/>
                        </Grid>
                    </StackPanel>

                    <StackPanel x:Name="SuccessPanel" Spacing="12" IsVisible="False">
                        <TextBlock Text="Thanks! Your feedback was submitted." FontSize="16" Foreground="White" FontWeight="SemiBold"/>
                        <Grid ColumnDefinitions="*,10,*">
                            <Button x:Name="SubmitAnotherButton" Grid.Column="0" Classes="secondary-btn" Content="Submit another"/>
                            <Button x:Name="CloseAfterSuccessButton" Grid.Column="2" Classes="primary-btn" Content="Close"/>
                        </Grid>
                    </StackPanel>
                </StackPanel>
            </ScrollViewer>
        </Grid>
    </Border>
</Window>
```

- [ ] **Step 2: Update the code-behind**

In `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml.cs`:

(a) Remove these usings (their only consumers are being deleted): `using Avalonia.Media.Imaging;`, `using Avalonia.Platform.Storage;`, `using DiffusionNexus.UI.Services;`

(b) Remove the `private byte[]? _screenshotBytes;` field.

(c) Constructors become 5-parameter (drop `initialScreenshot`):

```csharp
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
        string? initialEmail)
```

In the main constructor body: delete `_screenshotBytes = initialScreenshot;`, delete the `UpdateScreenshotPreview();` call, delete the `ReplaceScreenshotButton.Click += OnReplaceScreenshotClick;` line and the whole `RemoveScreenshotButton.Click += ...` lambda block. Everything else (email prefill, disclaimer wiring, submit/cancel/success wiring) stays.

(d) Delete the entire `UpdateScreenshotPreview()` method and the entire `OnReplaceScreenshotClick` method.

(e) In `OnSubmitClick`'s `FeedbackReport` initializer: delete the `ScreenshotPng = _screenshotBytes,` line and change the `LogTail` line to:

```csharp
            LogTail = reportType == FeedbackReportType.Bug ? _logTail : null,
```

(f) In `OnSubmitAnotherClick`: delete the two lines `_screenshotBytes = null;` and `UpdateScreenshotPreview();` — the rest of the reset stays exactly as is.

- [ ] **Step 3: Slim DialogService**

In `DiffusionNexus.UI/Services/DialogService.cs`, `ShowFeedbackDialogAsync`: delete the screenshot block:

```csharp
        byte[]? screenshot = null;
        try
        {
            screenshot = ScreenshotCapture.CaptureWindowPng(_window);
        }
        catch
        {
            // Screenshot capture is best-effort — the dialog works fine without one.
        }
```

and change the construction to the 5-argument form:

```csharp
        var dialog = new FeedbackDialog(
            feedbackService,
            DiffusionNexus.Installer.SDK.Shared.Services.Feedback.FeedbackProduct.MainApp,
            appVersion,
            logTail,
            rememberedEmail);
```

(`BuildLogTail` and the e-mail prefill/persist logic stay unchanged.)

- [ ] **Step 4: Delete the orphaned helper**

Run: `grep -rn "ScreenshotCapture" DiffusionNexus.UI --include=*.cs --include=*.axaml`
Expected: after Steps 2-3, the ONLY hits are inside `DiffusionNexus.UI/Services/ScreenshotCapture.cs` itself. If anything else references it, STOP and report (don't delete). Otherwise delete the file:

```bash
git rm DiffusionNexus.UI/Services/ScreenshotCapture.cs
```

- [ ] **Step 5: Build + full test suite**

Run: `dotnet build DiffusionNexus.UI` then `dotnet build DiffusionNexus.IntegrationTests` then `dotnet test DiffusionNexus.Tests -c Debug`
Expected: 0 errors everywhere; all tests pass (was 1982). Then verify the constraint grep:

Run: `grep -rniE "screenshotcapture|_screenshotBytes|ScreenshotPng|UpdateScreenshotPreview|OnReplaceScreenshotClick|ScreenshotPreviewImage|ReplaceScreenshotButton|RemoveScreenshotButton" DiffusionNexus.UI --include=*.cs --include=*.axaml`
Expected: zero hits.

- [ ] **Step 6: Commit**

```bash
git add -A DiffusionNexus.UI
git commit -m "feat: feedback dialog v4 - drop screenshots, bug-only log tail, red disclaimer"
```

- [ ] **Step 7: Manual verification checklist (user, real display)**

1. Dialog opens 1040×800: report type + title + description left, optional details + e-mail right, red-bordered disclaimer below with bold red "Before you submit".
2. Submit disabled until the checkbox is checked.
3. Submit a **bug report** → issue in `DiffusionNexus.Feedback` has the log-tail section.
4. "Submit another" → form resets (checkbox unchecked again), switch type to **Feedback**, submit → issue has NO log-tail section.
5. No screenshot UI anywhere; created issues contain no image.
