# Feedback v3 — Disclaimer + Success Panel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a privacy disclaimer + required checkbox gating Submit, and replace the success panel's private-repo issue link/Open-Issue button with a "Submit another" flow that resets the form in place.

**Architecture:** Both changes live entirely in `FeedbackDialog.axaml`/`.axaml.cs` (code-behind driven, no bindings — matches the existing file's style). The checkbox gate is enforced via `SubmitButton.IsEnabled`, recomputed from a new `_isSubmitting` flag AND the checkbox state, so submitting-in-progress and consent-not-given compose correctly. "Submit another" reuses the same reset-field logic a fresh dialog-open already performs, minus screenshot re-capture (which the dialog has no way to do meaningfully on its own).

**Tech Stack:** .NET 10 / C#, Avalonia UI.

**Spec:** `DiffusionNexus.Installer.SDK/docs/superpowers/specs/2026-07-05-feedback-v3-disclaimer-screenshot-fix-design.md` (SDK repo, branch `feature/feedback-report-type-email` — "Disclaimer + checkbox" and "Success panel redesign" sections).

## Global Constraints

- Work in the existing worktree `E:\Repos\DiffusionNexus\.claude\worktrees\feature+feedback-main-app-ui`, branch `feature/feedback-main-app-ui` (PR #396) — commit directly to it.
- Disclaimer visual style: `Border Background="#1E1E1E" CornerRadius="4" Padding="16"` — copied from the app's existing first-launch disclaimer (`DiffusionNexusMainWindow.axaml`), not a new style.
- Disclaimer copy, exact: "This report includes the last 500 lines of the application log, which may contain local file and folder names — for example downloaded LoRA or model filenames and their paths. If a screenshot is attached, it reflects whatever was on your screen at the time; please make sure it doesn't contain anything NSFW or otherwise sensitive before submitting."
- Checkbox label, exact: "I understand and accept this"
- Checkbox starts unchecked on dialog open AND after every "Submit another" reset — never remembered/persisted (unlike the e-mail field).
- `Submit` must be disabled whenever the checkbox is unchecked, in addition to (not replacing) the existing title/description/e-mail validation already inside `OnSubmitClick`.
- "Submit another" must NOT attempt to re-capture a screenshot (the dialog has no window reference to capture something other than itself) — it clears the screenshot to none.
- "Submit another" keeps the e-mail field as-is (it's the one field meant to persist across submissions in the session).
- No automated UI test infra for this dialog (per v1/v2 precedent) — verified by `dotnet build` + full `DiffusionNexus.Tests` suite + a manual checklist.

---

### Task 1: Disclaimer + checkbox gate

**Files:**
- Modify: `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml`
- Modify: `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: `DisclaimerCheckBox` (x:Name), `_isSubmitting` field, `UpdateSubmitEnabled()` method — consumed by Task 2's "Submit another" reset (must call `UpdateSubmitEnabled()` after unchecking the box).

- [ ] **Step 1: Add the disclaimer block to the XAML**

In `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml`, insert this immediately after the two-column `<Grid ColumnDefinitions="*,24,*">...</Grid>` block closes (i.e. right after its closing `</Grid>` tag, before `<TextBlock x:Name="StatusText" ...`):

```xml
                        <Border Background="#1E1E1E" CornerRadius="4" Padding="16">
                            <StackPanel Spacing="12">
                                <TextBlock TextWrapping="Wrap" FontSize="13" Foreground="#ccccee" LineHeight="20">
                                    This report includes the last 500 lines of the application log, which may contain local file and folder names — for example downloaded LoRA or model filenames and their paths. If a screenshot is attached, it reflects whatever was on your screen at the time; please make sure it doesn't contain anything NSFW or otherwise sensitive before submitting.
                                </TextBlock>
                                <CheckBox x:Name="DisclaimerCheckBox" Content="I understand and accept this" Foreground="White" FontSize="13"/>
                            </StackPanel>
                        </Border>
```

- [ ] **Step 2: Wire the gate in code-behind**

In `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml.cs`:

(a) Add a new field, next to the existing `private string? _lastIssueUrl;`:

```csharp
    private bool _isSubmitting;
```

(b) In the constructor, after the existing `RemoveScreenshotButton.Click += ...` block and before `CloseAfterSuccessButton.Click += ...`, add:

```csharp
        DisclaimerCheckBox.IsCheckedChanged += (_, _) => UpdateSubmitEnabled();
        UpdateSubmitEnabled();
```

(c) Add the new method, placed right before `SetSubmitting`:

```csharp
    private void UpdateSubmitEnabled()
    {
        SubmitButton.IsEnabled = !_isSubmitting && DisclaimerCheckBox.IsChecked == true;
    }
```

(d) Replace `SetSubmitting` in full:

```csharp
    private void SetSubmitting(bool submitting)
    {
        _isSubmitting = submitting;
        UpdateSubmitEnabled();
        CancelButton.IsEnabled = !submitting;
        SubmittingIndicator.IsVisible = submitting;
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build DiffusionNexus.UI`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml.cs
git commit -m "feat: add privacy disclaimer checkbox gating feedback submit"
```

---

### Task 2: Success panel — "Submit another" replaces the issue link

**Files:**
- Modify: `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml`
- Modify: `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml.cs`

**Interfaces:**
- Consumes: Task 1's `DisclaimerCheckBox`, `_isSubmitting`, `UpdateSubmitEnabled()`.
- Produces: nothing downstream (last task in this plan).

- [ ] **Step 1: Replace the SuccessPanel XAML**

In `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml`, replace the entire `SuccessPanel` block:

```xml
                    <StackPanel x:Name="SuccessPanel" Spacing="12" IsVisible="False">
                        <TextBlock Text="Thanks! Your feedback was submitted." FontSize="16" Foreground="White" FontWeight="SemiBold"/>
                        <TextBlock x:Name="IssueUrlText" Foreground="#88aaff" TextWrapping="Wrap"/>
                        <Button x:Name="IssueUrlButton" Classes="secondary-btn" Content="Open Issue"/>
                        <Button x:Name="CloseAfterSuccessButton" Classes="primary-btn" Content="Close"/>
                    </StackPanel>
```

with:

```xml
                    <StackPanel x:Name="SuccessPanel" Spacing="12" IsVisible="False">
                        <TextBlock Text="Thanks! Your feedback was submitted." FontSize="16" Foreground="White" FontWeight="SemiBold"/>
                        <Grid ColumnDefinitions="*,10,*">
                            <Button x:Name="SubmitAnotherButton" Grid.Column="0" Classes="secondary-btn" Content="Submit another"/>
                            <Button x:Name="CloseAfterSuccessButton" Grid.Column="2" Classes="primary-btn" Content="Close"/>
                        </Grid>
                    </StackPanel>
```

- [ ] **Step 2: Update the code-behind**

In `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml.cs`:

(a) Remove the `private string? _lastIssueUrl;` field entirely (no longer read or written anywhere once this task is done).

(b) In the constructor, remove this block entirely:

```csharp
        IssueUrlButton.Click += (_, _) =>
        {
            if (_lastIssueUrl is not null) OpenUrl(_lastIssueUrl);
        };
```

and add, in its place:

```csharp
        SubmitAnotherButton.Click += OnSubmitAnotherClick;
```

(c) Remove the `OpenUrl` static method entirely (its only caller was the block just removed).

(d) Replace `ShowSuccess` in full — drop the now-unused `issueUrl` parameter and the `IssueUrlText`/`_lastIssueUrl` assignments:

```csharp
    private void ShowSuccess(string? email)
    {
        WasSubmitted = true;
        SubmittedEmail = string.IsNullOrEmpty(email) ? null : email;

        FormPanel.IsVisible = false;
        SuccessPanel.IsVisible = true;
    }
```

(e) In `OnSubmitClick`, change the success call site from `ShowSuccess(result.IssueUrl!, email);` to:

```csharp
            ShowSuccess(email);
```

(f) Add the new handler, placed right after `OnSubmitClick`:

```csharp
    private void OnSubmitAnotherClick(object? sender, RoutedEventArgs e)
    {
        TitleBox.Text = string.Empty;
        DescriptionBox.Text = string.Empty;
        WhatHappenedBox.Text = string.Empty;
        WhatShouldHaveHappenedBox.Text = string.Empty;
        _screenshotBytes = null;
        UpdateScreenshotPreview();
        TypeBugRadio.IsChecked = true;
        DisclaimerCheckBox.IsChecked = false;
        StatusText.IsVisible = false;
        UpdateSubmitEnabled();

        SuccessPanel.IsVisible = false;
        FormPanel.IsVisible = true;
    }
```

Note: `EmailBox.Text` is deliberately left untouched — the e-mail carries over to the next report.

- [ ] **Step 3: Build**

Run: `dotnet build DiffusionNexus.UI`
Expected: Build succeeded, 0 errors. (If `IssueUrlText`/`IssueUrlButton`/`_lastIssueUrl`/`OpenUrl` are referenced anywhere else in the file, the compiler will point at the exact line — everything about those four should be gone.)

- [ ] **Step 4: Full test suite**

Run: `dotnet test DiffusionNexus.Tests -c Debug`
Expected: all pass (was 1982).

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml.cs
git commit -m "feat: replace feedback success panel's issue link with Submit another"
```

- [ ] **Step 6: Manual verification checklist (user, on a real display)**

1. Open the dialog — disclaimer box is visible above the Cancel/Submit row, Submit is disabled.
2. Check the disclaimer checkbox — Submit becomes enabled (assuming title/description are also filled).
3. Uncheck it again — Submit disables immediately.
4. Submit a real report — success panel shows "Submit another" and "Close", no issue link or "Open Issue" button anywhere.
5. Click "Submit another" — form is back, title/description/what-happened/what-should-have-happened are empty, screenshot preview is empty, report type is back to "Bug report", disclaimer checkbox is unchecked (Submit disabled again), but the e-mail field still shows what you typed before.
6. Fill it in again, check the disclaimer, submit a second time — succeeds, "Submit another" still works repeatedly.
7. Click "Close" — dialog closes normally.
