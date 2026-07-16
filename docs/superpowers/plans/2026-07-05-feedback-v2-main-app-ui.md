# Feedback Dialog v2 — Main App UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rework the FeedbackDialog per the v2 spec — 1040×800 two-column layout, report-type radio buttons, optional persisted reporter e-mail, real relay URL, SDK 1.2.25.

**Architecture:** The dialog stays a code-behind-driven `Window` (named controls, no bindings). The e-mail persists in the existing `AppSettings` singleton EF entity (one row, `Id = 1`) via a new nullable string column + a targeted getter/setter pair on `IAppSettingsService`, mirroring the `FavoriteLoraSourcePath` precedent. `DialogService.ShowFeedbackDialogAsync` prefills the e-mail before constructing the dialog and persists it after a successful submit.

**Tech Stack:** .NET 10 / C#, Avalonia UI, EF Core (SQLite, migrations under `DiffusionNexus.DataAccess/Migrations/Core`), DiffusionNexus.Installer.SDK 1.2.25 NuGet.

**Spec:** `DiffusionNexus.Installer.SDK/docs/superpowers/specs/2026-07-05-feedback-v2-report-type-email-design.md` (SDK repo, branch `feature/feedback-report-type-email`).

**Dependency:** Tasks 2–3 require SDK **1.2.25** published to GitHub Packages (Plan A in the SDK repo — `docs/superpowers/plans/2026-07-05-feedback-v2-sdk-and-relay.md` there). Task 1 has no SDK dependency and can run first regardless.

## Global Constraints

- Work in the existing worktree `E:\Repos\DiffusionNexus\.claude\worktrees\feature+feedback-main-app-ui`, branch `feature/feedback-main-app-ui` (PR #396) — commit directly to it.
- Window size exactly **1040×800**, `CanResize="False"` stays.
- Radio order left-to-right: **Feedback, Bug report, Feature request** — with **Bug report preselected** (`IsChecked="True"`).
- Real relay URL, exact value: `https://diffusionnexus-feedback-relay.diffusionnexus.workers.dev`
- SDK package refs: all five `DiffusionNexus.Installer.SDK.*` go `1.2.24` → `1.2.25`.
- New SDK surface consumed (defined by Plan A): `FeedbackReportType` enum (`Feedback`, `Bug`, `FeatureRequest`) and `FeedbackReport.ReportType` (required) + `FeedbackReport.Email` (`string?`), namespace `DiffusionNexus.Installer.SDK.Shared.Services.Feedback`.
- E-mail column: plain `string?`, **no** `HasMaxLength` config — matches the `FavoriteLoraSourcePath` precedent exactly (length is enforced client-side + relay-side instead).
- Do **NOT** add `FeedbackReporterEmail` to `AppSettingsService.SaveSettingsAsync`'s scalar-copy block — the Settings screen builds a detached snapshot without it, so copying it there would wipe the stored e-mail to null on every Settings save. Leaving it out means Settings-screen saves don't touch it. This is deliberate; add a code comment saying so is NOT needed (the property just isn't referenced there).
- E-mail client validation (only when non-empty): exactly one `@`, non-empty local part, domain part contains `.` and is ≥ 3 chars.
- No automated UI test infra for dialogs — Tasks 2–3 verified by `dotnet build` + the full `DiffusionNexus.Tests` suite still passing + manual checklist. Task 1 is additionally covered by the existing test suite passing (it touches Domain/Service/DataAccess).

---

### Task 1: `FeedbackReporterEmail` setting (entity + service + migration)

**Files:**
- Modify: `DiffusionNexus.Domain/Entities/AppSettings.cs` (add property near `FavoriteLoraSourcePath`, ~line 86)
- Modify: `DiffusionNexus.Domain/Services/IAppSettingsService.cs` (declare getter/setter pair near the FavoriteLoraSource pair, ~lines 90-100)
- Modify: `DiffusionNexus.Service/Services/AppSettingsService.cs` (implement pair near `Get/SetFavoriteLoraSourceAsync`, ~lines 386-406)
- Create: EF migration `AddFeedbackReporterEmail` under `DiffusionNexus.DataAccess/Migrations/Core/` (+ `.Designer.cs`, + snapshot update)

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces (consumed by Task 3's `DialogService` changes):
  - `Task<string?> GetFeedbackReporterEmailAsync(CancellationToken cancellationToken = default);`
  - `Task SetFeedbackReporterEmailAsync(string? email, CancellationToken cancellationToken = default);`

- [ ] **Step 1: Add the entity property**

In `DiffusionNexus.Domain/Entities/AppSettings.cs`, directly after the `FavoriteLoraSourcePath` property:

```csharp
    /// <summary>
    /// Reporter e-mail remembered by the in-app feedback dialog (optional; pre-fills the
    /// dialog's e-mail field). Null when the user never entered one.
    /// </summary>
    public string? FeedbackReporterEmail { get; set; }
```

- [ ] **Step 2: Declare the interface pair**

In `DiffusionNexus.Domain/Services/IAppSettingsService.cs`, directly after the `SetFavoriteLoraSourceAsync` declaration:

```csharp
    /// <summary>Gets the remembered feedback-reporter e-mail, or null if not set.</summary>
    Task<string?> GetFeedbackReporterEmailAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores the feedback-reporter e-mail; whitespace/empty clears it to null.</summary>
    Task SetFeedbackReporterEmailAsync(string? email, CancellationToken cancellationToken = default);
```

- [ ] **Step 3: Implement in AppSettingsService**

In `DiffusionNexus.Service/Services/AppSettingsService.cs`, directly after `SetFavoriteLoraSourceAsync` (model: lines 386-406):

```csharp
    public async Task<string?> GetFeedbackReporterEmailAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _unitOfWork.AppSettings.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return settings?.FeedbackReporterEmail;
    }

    public async Task SetFeedbackReporterEmailAsync(string? email, CancellationToken cancellationToken = default)
    {
        var settings = await _unitOfWork.AppSettings.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings is null) return;

        settings.FeedbackReporterEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

(Match the exact `GetSettingsAsync` call shape used by `GetFavoriteLoraSourceAsync` in this file — if it uses a different repository method name, mirror that one.)

- [ ] **Step 4: Create the migration**

First check whether design-time EF tooling works here:

Run: `dotnet ef --version` (from the worktree root). If the tool is missing, try `dotnet tool restore`. Then check for a design-time factory: `grep -r "IDesignTimeDbContextFactory" DiffusionNexus.DataAccess DiffusionNexus.UI --include=*.cs -l`

**Path A (preferred, if `dotnet ef` is usable):**

```bash
dotnet ef migrations add AddFeedbackReporterEmail --project DiffusionNexus.DataAccess --startup-project <the project that hosts the design-time factory or DiffusionNexus.UI> --context DiffusionNexusCoreDbContext --output-dir Migrations/Core
```

Verify the generated `Up` is exactly one `AddColumn<string>` (name `FeedbackReporterEmail`, table `AppSettings`, type `TEXT`, nullable true) and `Down` is the matching `DropColumn`. If the generator emits anything else (phantom diffs), STOP and report DONE_WITH_CONCERNS — do not commit a migration containing unrelated changes.

**Path B (fallback — hand-author, exact precedent `20260524230659_AddFavoriteLoraSourcePath.cs`):**
1. Create `DiffusionNexus.DataAccess/Migrations/Core/<utcnow-yyyyMMddHHmmss>_AddFeedbackReporterEmail.cs`:

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddFeedbackReporterEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FeedbackReporterEmail",
                table: "AppSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeedbackReporterEmail",
                table: "AppSettings");
        }
    }
}
```

(Adjust the namespace to match the sibling migrations' actual namespace.)
2. Copy the **latest** existing migration's `.Designer.cs` in that folder to `<same-timestamp>_AddFeedbackReporterEmail.Designer.cs`; change its class name to `AddFeedbackReporterEmail`, its `[Migration("...")]` id to `<timestamp>_AddFeedbackReporterEmail`, and add, inside the `AppSettings` entity block (alphabetical placement among the other `b.Property<string>` lines):

```csharp
                    b.Property<string>("FeedbackReporterEmail")
                        .HasColumnType("TEXT");
```

3. Add the same two-line property to the `AppSettings` block of `DiffusionNexus.DataAccess/Migrations/Core/DiffusionNexusCoreDbContextModelSnapshot.cs`.

- [ ] **Step 5: Build + run the test suite**

Run: `dotnet build` then `dotnet test DiffusionNexus.Tests -c Debug`
Expected: build clean; all existing tests pass (was 1982 passing).

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Domain/Entities/AppSettings.cs DiffusionNexus.Domain/Services/IAppSettingsService.cs DiffusionNexus.Service/Services/AppSettingsService.cs DiffusionNexus.DataAccess/Migrations/Core/
git commit -m "feat: add FeedbackReporterEmail app setting with migration"
```

---

### Task 2: SDK 1.2.25 + real relay URL

**Files:**
- Modify: `DiffusionNexus.UI/DiffusionNexus.UI.csproj` (lines ~69-73)
- Modify: `DiffusionNexus.UI/App.axaml.cs` (lines ~849-857)

**Interfaces:**
- Consumes: SDK 1.2.25 on GitHub Packages (published by Plan A in the SDK repo). If `dotnet restore` cannot find 1.2.25, STOP and report BLOCKED.
- Produces: `FeedbackReportType` / `FeedbackReport.ReportType` / `FeedbackReport.Email` available to Task 3.

- [ ] **Step 1: Bump the five package refs**

In `DiffusionNexus.UI/DiffusionNexus.UI.csproj` change all five `DiffusionNexus.Installer.SDK.*` `PackageReference` versions from `1.2.24` to `1.2.25`.

- [ ] **Step 2: Swap the relay URL**

In `DiffusionNexus.UI/App.axaml.cs`, replace the registration block's placeholder URL and stale TODO comment:

```csharp
        // Feedback reporting service (posts to the Cloudflare Worker relay, which holds
        // the GitHub credential — see docs/superpowers/plans/2026-07-03-feedback-sdk-and-relay.md
        // in the DiffusionNexus.Installer.SDK repo).
        services.AddSingleton<IFeedbackReportingService>(_ => new FeedbackReportingService(
            new FeedbackReportingServiceOptions
            {
                RelayUrl = "https://diffusionnexus-feedback-relay.diffusionnexus.workers.dev"
            }));
```

- [ ] **Step 3: Restore + build**

Run: `dotnet restore DiffusionNexus.UI` then `dotnet build DiffusionNexus.UI`
Expected: restore pulls 1.2.25; build clean; `FeedbackReportType` resolvable (Task 3 will prove it).

- [ ] **Step 4: Commit**

```bash
git add DiffusionNexus.UI/DiffusionNexus.UI.csproj DiffusionNexus.UI/App.axaml.cs
git commit -m "feat: point feedback service at the live relay and bump SDK to 1.2.25"
```

---

### Task 3: Dialog rework (layout, radios, e-mail) + DialogService wiring

**Files:**
- Modify: `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml` (full-file replacement below)
- Modify: `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml.cs`
- Modify: `DiffusionNexus.UI/Services/DialogService.cs` (`ShowFeedbackDialogAsync`, ~line 626)

**Interfaces:**
- Consumes: Task 1's `Get/SetFeedbackReporterEmailAsync` on `IAppSettingsService`; Task 2's SDK 1.2.25 types; existing `ScreenshotCapture`.
- Produces: nothing downstream.

- [ ] **Step 1: Replace the XAML**

Replace `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml` in full with:

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
                            <!-- Left column: report type + text fields + e-mail -->
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
                                    <TextBox x:Name="DescriptionBox" Watermark="Describe the problem" AcceptsReturn="True" Height="110" TextWrapping="Wrap"/>
                                </StackPanel>

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

                            <!-- Right column: screenshot -->
                            <StackPanel Grid.Column="2" Spacing="6">
                                <TextBlock Classes="field-label" Text="Screenshot"/>
                                <Border Background="#2a2a4a" CornerRadius="6" Padding="8">
                                    <Image x:Name="ScreenshotPreviewImage" MaxHeight="420" Stretch="Uniform"/>
                                </Border>
                                <Grid ColumnDefinitions="*,10,*">
                                    <Button x:Name="ReplaceScreenshotButton" Grid.Column="0" Classes="secondary-btn" Content="Replace Screenshot..."/>
                                    <Button x:Name="RemoveScreenshotButton" Grid.Column="2" Classes="secondary-btn" Content="Remove Screenshot"/>
                                </Grid>
                            </StackPanel>
                        </Grid>

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

- [ ] **Step 2: Update the code-behind**

In `DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml.cs`:

(a) Add two public result properties after `_lastIssueUrl`:

```csharp
    /// <summary>True once a report was successfully submitted.</summary>
    public bool WasSubmitted { get; private set; }

    /// <summary>The e-mail the user submitted with (null/empty allowed). Only meaningful when <see cref="WasSubmitted"/>.</summary>
    public string? SubmittedEmail { get; private set; }
```

(b) Extend both constructors with a 6th parameter `string? initialEmail`:

```csharp
    public FeedbackDialog() : this(
        new FeedbackReportingService(new FeedbackReportingServiceOptions { RelayUrl = "https://example.com" }),
        FeedbackProduct.MainApp,
        "0.0.0",
        string.Empty,
        null,
        null)
    {
    }

    public FeedbackDialog(
        IFeedbackReportingService feedbackService,
        FeedbackProduct product,
        string appVersion,
        string logTail,
        byte[]? initialScreenshot,
        string? initialEmail)
```

and inside the main constructor body, after `_screenshotBytes = initialScreenshot;`:

```csharp
        EmailBox.Text = initialEmail;
```

(c) In `OnSubmitClick`, after the title/description validation block and before `SetSubmitting(true);`:

```csharp
        var email = EmailBox.Text?.Trim();
        if (!string.IsNullOrEmpty(email) && !LooksLikeEmail(email))
        {
            StatusText.Text = "That e-mail address doesn't look valid — fix it or leave the field empty.";
            StatusText.IsVisible = true;
            return;
        }

        var reportType = TypeFeedbackRadio.IsChecked == true ? FeedbackReportType.Feedback
            : TypeFeatureRadio.IsChecked == true ? FeedbackReportType.FeatureRequest
            : FeedbackReportType.Bug;
```

(d) In the `FeedbackReport` object initializer, add:

```csharp
            ReportType = reportType,
            Email = string.IsNullOrEmpty(email) ? null : email,
```

(e) In `ShowSuccess`, record the result (add as the first lines of the method — `email` must be passed in; change the call site from `ShowSuccess(result.IssueUrl!)` to `ShowSuccess(result.IssueUrl!, email)` and the signature to `private void ShowSuccess(string issueUrl, string? email)`):

```csharp
        WasSubmitted = true;
        SubmittedEmail = string.IsNullOrEmpty(email) ? null : email;
```

(f) Add the validation helper at the bottom of the class:

```csharp
    private static bool LooksLikeEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@')) return false;
        var domain = email[(at + 1)..];
        return domain.Length >= 3 && domain.Contains('.');
    }
```

- [ ] **Step 3: Wire prefill + persistence in DialogService**

In `DiffusionNexus.UI/Services/DialogService.cs`, `ShowFeedbackDialogAsync`: resolve `IAppSettingsService` (same pattern as `ShowDownloadLoraVersionDialogAsync`), prefill, pass the 6th ctor arg, persist after the dialog closes:

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

        var appSettings = App.Services?.GetService<IAppSettingsService>();
        string? rememberedEmail = null;
        if (appSettings is not null)
        {
            try
            {
                rememberedEmail = await appSettings.GetFeedbackReporterEmailAsync();
            }
            catch
            {
                // Prefill is best-effort.
            }
        }

        var dialog = new FeedbackDialog(
            feedbackService,
            DiffusionNexus.Installer.SDK.Shared.Services.Feedback.FeedbackProduct.MainApp,
            appVersion,
            logTail,
            screenshot,
            rememberedEmail);

        await dialog.ShowDialog(_window);

        if (dialog.WasSubmitted && appSettings is not null)
        {
            try
            {
                await appSettings.SetFeedbackReporterEmailAsync(dialog.SubmittedEmail);
            }
            catch
            {
                // Remembering the e-mail is best-effort.
            }
        }
    }
```

(`IAppSettingsService` is already `using`-imported in DialogService — verify, add if missing.)

- [ ] **Step 4: Build + full test suite**

Run: `dotnet build` then `dotnet test DiffusionNexus.Tests -c Debug`
Expected: clean build, all tests pass.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml DiffusionNexus.UI/Views/Dialogs/FeedbackDialog.axaml.cs DiffusionNexus.UI/Services/DialogService.cs
git commit -m "feat: feedback dialog v2 - two-column layout, report type, reporter e-mail"
```

- [ ] **Step 6: Manual verification checklist (user, on a real display)**

1. Dialog opens at 1040×800; form left, screenshot right.
2. Radio row shows Feedback / Bug report / Feature request with Bug report preselected.
3. Enter an invalid e-mail (`foo@bar`) → inline validation blocks submit; clear it → submit allowed.
4. Submit a real report → issue created (correct `type:*` + `source:main-app` labels, e-mail line in body); close the test issue.
5. Reopen the dialog → e-mail is pre-filled from the previous submit; restart the app → still pre-filled.
6. Settings screen: save any unrelated setting, reopen feedback dialog → e-mail still remembered (not wiped).
