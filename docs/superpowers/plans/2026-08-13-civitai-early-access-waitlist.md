# Civitai Early-Access Waitlist Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users waitlist early-access (temporarily paywalled) Civitai LoRAs; a Waitlist tab in the browser's queue panel shows countdowns, re-checks entries against the API on demand, and moves confirmed-free entries into the download queue.

**Architecture:** A new `CivitaiWaitlist` service + `CivitaiWaitlistEntry` model mirror the existing `CivitaiDownloadQueue`/`CivitaiDownloadJob` pattern (ObservableObject, JSON persistence in LocalAppData, `persistPathOverride` test seam, constructed in `LoraViewerViewModel` and passed into `CivitaiBrowserViewModel`). The queue side panel is wrapped in a Queue|Waitlist `TabControl`; the existing `EarlyAccessConfirmDialog` gains AddToWaitlist/OpenWebsite options; the card VM gains a permanent-paid badge flag.

**Tech Stack:** .NET 9 / Avalonia 11, CommunityToolkit.Mvvm (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`), System.Text.Json, xUnit + FluentAssertions + Moq.

**Spec:** `docs/superpowers/specs/2026-08-13-civitai-early-access-waitlist-design.md`

## Global Constraints

- Work happens in repo `e:\Repos\DiffusionNexus` on branch `feature/civitai-early-access-waitlist` (already created).
- All timestamps are UTC `DateTimeOffset`; every time-dependent method takes a `DateTimeOffset? utcNow = null` test seam (pattern: `CivitaiEarlyAccessExtensions.IsEarlyAccessActive`).
- EA state is NEVER decided by reading DTO fields inline. "Gated right now" = `version.IsEarlyAccessActive(now)`. "Never becomes free" = the new `version.IsPermanentlyPaid()` extension (Task 1). No other direct reads of `EarlyAccessDeadline`/`PaidAccess` outside `CivitaiEarlyAccess.cs` and the waitlist's deadline capture (`version.EarlyAccessDeadline ?? version.PaidAccess?.EndsAt`).
- Every feature step gets Unified Console logging: `IUnifiedLogger`, `LogCategory.Download`, source string `"CivitaiWaitlist"` (standing project rule: a hang must show the last successful step).
- Unit tests must NOT initialize Avalonia. No `DispatcherTimer` in tests — guard timer creation with `Avalonia.Application.Current is null`.
- Tests never touch the real LocalAppData persistence files — always pass `persistPathOverride`.
- Test project: `DiffusionNexus.Tests` (xUnit + FluentAssertions + Moq). Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~<name>"`.
- Before the final commit of the last task: `dotnet build e:\Repos\DiffusionNexus\DiffusionNexus.sln -c Release` must succeed (project rule: sln build before push).
- Culture: assertions must not depend on locale-formatted dates/numbers (German-locale machines run this suite).

---

### Task 1: `IsPermanentlyPaid` extension + `CivitaiWaitlistEntry` model

**Files:**
- Modify: `DiffusionNexus.Civitai\Models\CivitaiEarlyAccess.cs`
- Create: `DiffusionNexus.UI\Services\CivitaiBrowser\CivitaiWaitlistEntry.cs`
- Test: `DiffusionNexus.Tests\Viewer\CivitaiWaitlistEntryTests.cs`

**Interfaces:**
- Consumes: `CivitaiModelVersion`, `CivitaiPaidAccess` (existing records in `DiffusionNexus.Civitai\Models\CivitaiModelVersion.cs`).
- Produces:
  - `public static bool IsPermanentlyPaid(this CivitaiModelVersion? version)` on `CivitaiEarlyAccessExtensions`.
  - `public enum WaitlistEntryStatus { Waiting, Available, PermanentlyPaid, Unavailable, CheckFailed }` (namespace `DiffusionNexus.UI.Services.CivitaiBrowser`).
  - `public partial class CivitaiWaitlistEntry : ObservableObject` with init properties `ModelId (int)`, `VersionId (int)`, `ModelName (string)`, `VersionName (string)`, `BaseModel (string)`, `Category (string)`, `FileName (string)`, `DownloadUrl (string)`, `SizeDisplay (string)`, `SizeBytes (long)`, `ExpectedSha256 (string?)`, `PreviewImageUrl (string?)`, `IsNsfw (bool)`, `AddedAt (DateTimeOffset)`; observable properties `EarlyAccessDeadline (DateTimeOffset?)`, `LastCheckedAt (DateTimeOffset?)`, `Status (WaitlistEntryStatus)`, `StatusDetail (string?)`, `IsAvailable (bool)`, `CountdownDisplay (string?)`; methods `public void RefreshAvailability(DateTimeOffset? utcNow = null)`; computed `public IBrush StatusForeground`.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests\Viewer\CivitaiWaitlistEntryTests.cs`:

```csharp
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the waitlist entry's local availability computation: countdown text,
/// deadline-passed promotion to Available, terminal statuses, and the
/// IsPermanentlyPaid extension that gates what may be waitlisted at all.
/// </summary>
public sealed class CivitaiWaitlistEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

    private static CivitaiWaitlistEntry Entry(DateTimeOffset? deadline, WaitlistEntryStatus status = WaitlistEntryStatus.Waiting) => new()
    {
        ModelId = 1,
        VersionId = 2,
        ModelName = "Model",
        VersionName = "v1",
        EarlyAccessDeadline = deadline,
        Status = status
    };

    [Fact]
    public void FutureDeadline_CountsDownInDaysAndHours()
    {
        var e = Entry(Now.AddDays(2).AddHours(4).AddMinutes(30));
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeFalse();
        e.Status.Should().Be(WaitlistEntryStatus.Waiting);
        e.CountdownDisplay.Should().Be("free in 2d 4h");
    }

    [Fact]
    public void FutureDeadline_UnderADay_CountsDownInHoursAndMinutes()
    {
        var e = Entry(Now.AddHours(3).AddMinutes(12));
        e.RefreshAvailability(Now);
        e.CountdownDisplay.Should().Be("free in 3h 12m");
    }

    [Fact]
    public void FutureDeadline_UnderAnHour_CountsDownInMinutes()
    {
        var e = Entry(Now.AddMinutes(45));
        e.RefreshAvailability(Now);
        e.CountdownDisplay.Should().Be("free in 45m");
    }

    [Fact]
    public void PassedDeadline_BecomesAvailable()
    {
        var e = Entry(Now.AddMinutes(-1));
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeTrue();
        e.Status.Should().Be(WaitlistEntryStatus.Available);
        e.CountdownDisplay.Should().Be("Available now");
    }

    [Fact]
    public void AvailableEntry_WithExtendedDeadline_DemotesBackToWaiting()
    {
        // Re-check discovered the creator extended early access.
        var e = Entry(Now.AddDays(3), WaitlistEntryStatus.Available);
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeFalse();
        e.Status.Should().Be(WaitlistEntryStatus.Waiting);
    }

    [Fact]
    public void PermanentlyPaid_IsNeverAvailable_EvenWithPassedDeadline()
    {
        var e = Entry(Now.AddDays(-5), WaitlistEntryStatus.PermanentlyPaid);
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeFalse();
        e.Status.Should().Be(WaitlistEntryStatus.PermanentlyPaid);
        e.CountdownDisplay.Should().Be("Permanently paid — won't become free");
    }

    [Fact]
    public void UnavailableEntry_StaysUnavailable()
    {
        var e = Entry(null, WaitlistEntryStatus.Unavailable);
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeFalse();
        e.CountdownDisplay.Should().Be("No longer available on Civitai");
    }

    [Fact]
    public void CheckFailedEntry_WithPassedDeadline_StillBecomesAvailable()
    {
        // A stale network failure must not pin the entry — move-to-queue re-verifies anyway.
        var e = Entry(Now.AddMinutes(-1), WaitlistEntryStatus.CheckFailed);
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeTrue();
        e.Status.Should().Be(WaitlistEntryStatus.Available);
    }

    [Fact]
    public void NoDeadline_NonAvailableStatus_ShowsUnknownEndDate()
    {
        var e = Entry(null, WaitlistEntryStatus.Waiting);
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeFalse();
        e.CountdownDisplay.Should().Be("Early access — no end date published");
    }

    [Fact]
    public void NoDeadline_AvailableStatus_StaysAvailable()
    {
        // A re-check that confirmed "free" clears the deadline and sets Available.
        var e = Entry(null, WaitlistEntryStatus.Available);
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeTrue();
        e.CountdownDisplay.Should().Be("Available now");
    }

    [Fact]
    public void IsPermanentlyPaid_TrueOnlyForPermanentPaidAccess()
    {
        new CivitaiModelVersion { Id = 1, PaidAccess = new CivitaiPaidAccess { Permanent = true } }
            .IsPermanentlyPaid().Should().BeTrue();
        new CivitaiModelVersion { Id = 2, PaidAccess = new CivitaiPaidAccess { Permanent = false, EndsAt = Now.AddDays(7) } }
            .IsPermanentlyPaid().Should().BeFalse();
        new CivitaiModelVersion { Id = 3 }.IsPermanentlyPaid().Should().BeFalse();
        ((CivitaiModelVersion?)null).IsPermanentlyPaid().Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiWaitlistEntryTests"`
Expected: compile FAILURE — `CivitaiWaitlistEntry`, `WaitlistEntryStatus`, `IsPermanentlyPaid` do not exist.

- [ ] **Step 3: Implement the extension**

In `DiffusionNexus.Civitai\Models\CivitaiEarlyAccess.cs`, append inside `CivitaiEarlyAccessExtensions` (after `IsEarlyAccessActive`):

```csharp
    /// <summary>
    /// True when the version is paywalled forever (<c>paidAccess.permanent</c>) —
    /// it will never lapse into a free download, so waiting for it is pointless.
    /// Companion to <see cref="IsEarlyAccessActive"/>: that answers "gated right
    /// now?", this answers "gated forever?". Keep both here so no consumer reads
    /// the raw fields.
    /// </summary>
    public static bool IsPermanentlyPaid(this CivitaiModelVersion? version)
        => version?.PaidAccess?.Permanent == true;
```

- [ ] **Step 4: Implement the entry model**

Create `DiffusionNexus.UI\Services\CivitaiBrowser\CivitaiWaitlistEntry.cs`:

```csharp
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.Services.CivitaiBrowser;

/// <summary>
/// Lifecycle of a waitlisted early-access version. Waiting/Available flip locally
/// from the stored deadline; the other three are only ever assigned by an API
/// re-check and (except CheckFailed) are terminal — the timer never clears them.
/// </summary>
public enum WaitlistEntryStatus
{
    Waiting,
    Available,
    PermanentlyPaid,
    Unavailable,
    CheckFailed
}

/// <summary>
/// One early-access version the user is waiting on. Carries everything needed to
/// build a <see cref="CivitaiDownloadJob"/> later without re-browsing, plus the
/// deadline captured at add/re-check time. Availability is computed locally
/// (UTC deadline vs UTC now — Civitai timestamps are UTC ISO-8601, so no offset
/// handling); no API call happens outside explicit re-checks.
/// </summary>
public partial class CivitaiWaitlistEntry : ObservableObject
{
    public int ModelId { get; init; }
    public int VersionId { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public string VersionName { get; init; } = string.Empty;
    public string BaseModel { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string SizeDisplay { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string? PreviewImageUrl { get; init; }

    /// <summary>Routes "open on Civitai" to civitai.red (full page for NSFW) vs civitai.com.</summary>
    public bool IsNsfw { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    /// <summary>When early access ends (UTC). Null = no end date published, or confirmed free.</summary>
    [ObservableProperty]
    private DateTimeOffset? _earlyAccessDeadline;

    /// <summary>Last successful API re-check. Kept unchanged on CheckFailed so the user sees the last good data's age.</summary>
    [ObservableProperty]
    private DateTimeOffset? _lastCheckedAt;

    [ObservableProperty]
    private WaitlistEntryStatus _status = WaitlistEntryStatus.Waiting;

    /// <summary>Human-readable note from the last re-check (error text, "no longer exists", …).</summary>
    [ObservableProperty]
    private string? _statusDetail;

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private string? _countdownDisplay;

    /// <summary>
    /// Recomputes <see cref="IsAvailable"/>, <see cref="CountdownDisplay"/>, and the
    /// Waiting↔Available flip from the stored deadline. Terminal statuses
    /// (PermanentlyPaid, Unavailable) are never overridden here — only a re-check
    /// assigns or clears them. CheckFailed entries still promote when the deadline
    /// passes: move-to-queue re-verifies before enqueueing anyway.
    /// </summary>
    public void RefreshAvailability(DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? DateTimeOffset.UtcNow;

        switch (Status)
        {
            case WaitlistEntryStatus.PermanentlyPaid:
                IsAvailable = false;
                CountdownDisplay = "Permanently paid — won't become free";
                return;
            case WaitlistEntryStatus.Unavailable:
                IsAvailable = false;
                CountdownDisplay = "No longer available on Civitai";
                return;
        }

        if (EarlyAccessDeadline is { } deadline)
        {
            if (deadline <= now)
            {
                Status = WaitlistEntryStatus.Available;
                IsAvailable = true;
                CountdownDisplay = "Available now";
            }
            else
            {
                if (Status == WaitlistEntryStatus.Available) Status = WaitlistEntryStatus.Waiting;
                IsAvailable = false;
                CountdownDisplay = FormatCountdown(deadline - now);
            }
        }
        else
        {
            // No deadline stored: Available means a re-check confirmed "free";
            // anything else is a gate whose end date Civitai didn't publish.
            IsAvailable = Status == WaitlistEntryStatus.Available;
            CountdownDisplay = IsAvailable ? "Available now" : "Early access — no end date published";
        }
    }

    private static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining.TotalDays >= 1) return $"free in {(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1) return $"free in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"free in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }

    // Same allocate-once brush pattern as CivitaiDownloadJob.StatusForeground.
    private static readonly IBrush AvailableBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush DeadBrush = new SolidColorBrush(Color.Parse("#F87171"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush NeutralBrush = new SolidColorBrush(Color.Parse("#B3B3B3"));

    /// <summary>Green when downloadable, red for dead entries, amber for a failed check, neutral while counting down.</summary>
    public IBrush StatusForeground => Status switch
    {
        WaitlistEntryStatus.Available => AvailableBrush,
        WaitlistEntryStatus.PermanentlyPaid or WaitlistEntryStatus.Unavailable => DeadBrush,
        WaitlistEntryStatus.CheckFailed => WarnBrush,
        _ => NeutralBrush
    };

    partial void OnStatusChanged(WaitlistEntryStatus value) => OnPropertyChanged(nameof(StatusForeground));
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiWaitlistEntryTests"`
Expected: PASS (11 tests).

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Civitai/Models/CivitaiEarlyAccess.cs DiffusionNexus.UI/Services/CivitaiBrowser/CivitaiWaitlistEntry.cs DiffusionNexus.Tests/Viewer/CivitaiWaitlistEntryTests.cs
git commit -m "Add waitlist entry model and IsPermanentlyPaid extension" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: `CivitaiWaitlist` service — add/remove/dedup, counts, persistence

**Files:**
- Create: `DiffusionNexus.UI\Services\CivitaiBrowser\CivitaiWaitlist.cs`
- Test: `DiffusionNexus.Tests\Viewer\CivitaiWaitlistTests.cs`

**Interfaces:**
- Consumes: `CivitaiWaitlistEntry`, `WaitlistEntryStatus` (Task 1); `CivitaiResultViewModel` (existing — `Model`, `Name`, `Category`, `IsNsfw`, `Versions`); `CivitaiVersionPickItemViewModel` (existing — `Version`, `Name`, `BaseModel`, `SizeBytes`, `SizeDisplay`); `ICivitaiClient`, `IUnifiedLogger`.
- Produces: `public sealed class CivitaiWaitlist : ObservableObject` with:
  - ctor `CivitaiWaitlist(ICivitaiClient? civitaiClient, IUnifiedLogger? logger, string? persistPathOverride = null)`
  - `ObservableCollection<CivitaiWaitlistEntry> Entries { get; }`
  - `int AvailableCount { get; }` / `bool HasAvailable { get; }` (raised on collection change and every `RefreshAvailability`)
  - `bool TryAdd(CivitaiResultViewModel result, CivitaiVersionPickItemViewModel pick, DateTimeOffset? utcNow = null)`
  - `void Remove(CivitaiWaitlistEntry entry)`
  - `void RefreshAvailability(DateTimeOffset? utcNow = null)`
  - (Tasks 3–4 add `RefreshAllAsync`, `RefreshEntryAsync`, `MoveReadyToQueueAsync` to this class.)

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests\Viewer\CivitaiWaitlistTests.cs`:

```csharp
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the waitlist service: add/dedup, permanent-paid rejection, available
/// counting, and the JSON persist/restore round-trip via the path override.
/// </summary>
public sealed class CivitaiWaitlistTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-waitlist-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private string PersistPath(string name = "waitlist.json") => Path.Combine(_tempDir, name);

    private CivitaiWaitlist Create(string? file = null)
        => new(null, null, persistPathOverride: PersistPath(file ?? "waitlist.json"));

    internal static CivitaiModelVersion Version(
        int id,
        DateTimeOffset? deadline,
        bool? permanent = null)
        => new()
        {
            Id = id,
            Name = $"v{id}",
            BaseModel = "Krea 2",
            DownloadUrl = $"https://civitai.example/api/download/models/{id}",
            EarlyAccessDeadline = deadline,
            PaidAccess = permanent is null && deadline is null
                ? null
                : new CivitaiPaidAccess { Permanent = permanent, EndsAt = deadline }
        };

    internal static (CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick) Card(
        CivitaiModelVersion version, int modelId = 77, string name = "Test LoRA")
    {
        var model = new CivitaiModel { Id = modelId, Name = name, ModelVersions = [version] };
        var result = new CivitaiResultViewModel(model, showNsfwPreviews: false);
        return (result, result.Versions[0]);
    }

    [Fact]
    public void TryAdd_CapturesDeadlineAndPayloadFromBrowseData()
    {
        var wl = Create();
        var (result, pick) = Card(Version(3224172, Now.AddDays(7)));

        wl.TryAdd(result, pick, Now).Should().BeTrue();

        var e = wl.Entries.Single();
        e.VersionId.Should().Be(3224172);
        e.ModelId.Should().Be(77);
        e.ModelName.Should().Be("Test LoRA");
        e.EarlyAccessDeadline.Should().Be(Now.AddDays(7));
        e.AddedAt.Should().Be(Now);
        e.Status.Should().Be(WaitlistEntryStatus.Waiting);
        e.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void TryAdd_SameVersionTwice_IsRejected()
    {
        var wl = Create();
        var (result, pick) = Card(Version(10, Now.AddDays(7)));

        wl.TryAdd(result, pick, Now).Should().BeTrue();
        wl.TryAdd(result, pick, Now).Should().BeFalse();
        wl.Entries.Should().HaveCount(1);
    }

    [Fact]
    public void TryAdd_PermanentlyPaidVersion_IsRejected()
    {
        var wl = Create();
        var (result, pick) = Card(Version(11, deadline: null, permanent: true));

        wl.TryAdd(result, pick, Now).Should().BeFalse("permanently paid versions never become free");
        wl.Entries.Should().BeEmpty();
    }

    [Fact]
    public void AvailableCount_TracksDeadlines()
    {
        var wl = Create();
        var (r1, p1) = Card(Version(1, Now.AddDays(-1)), modelId: 1);
        wl.TryAdd(r1, p1, Now);
        var (r2, p2) = Card(Version(2, Now.AddDays(5)), modelId: 2);
        wl.TryAdd(r2, p2, Now);

        wl.RefreshAvailability(Now);

        wl.AvailableCount.Should().Be(1);
        wl.HasAvailable.Should().BeTrue();
    }

    [Fact]
    public void PersistRestore_RoundTripsEntries()
    {
        var file = "roundtrip.json";
        var wl = Create(file);
        var (result, pick) = Card(Version(42, Now.AddDays(3)));
        wl.TryAdd(result, pick, Now);

        var restored = Create(file);

        var e = restored.Entries.Single();
        e.VersionId.Should().Be(42);
        e.ModelName.Should().Be("Test LoRA");
        e.EarlyAccessDeadline.Should().Be(Now.AddDays(3));
        e.Status.Should().Be(WaitlistEntryStatus.Waiting);
        e.DownloadUrl.Should().Be("https://civitai.example/api/download/models/42");
    }

    [Fact]
    public void Restore_RecomputesAvailability_ForDeadlinesThatPassedWhileClosed()
    {
        var file = "stale.json";
        var wl = Create(file);
        var (result, pick) = Card(Version(43, DateTimeOffset.UtcNow.AddMilliseconds(-1)));
        wl.TryAdd(result, pick);

        var restored = Create(file);

        restored.Entries.Single().IsAvailable.Should().BeTrue();
        restored.AvailableCount.Should().Be(1);
    }

    [Fact]
    public void Remove_DropsEntryAndPersists()
    {
        var file = "remove.json";
        var wl = Create(file);
        var (result, pick) = Card(Version(50, Now.AddDays(2)));
        wl.TryAdd(result, pick, Now);

        wl.Remove(wl.Entries.Single());

        wl.Entries.Should().BeEmpty();
        Create(file).Entries.Should().BeEmpty("removal must be persisted");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiWaitlistTests"`
Expected: compile FAILURE — `CivitaiWaitlist` does not exist.

- [ ] **Step 3: Implement the service**

Create `DiffusionNexus.UI\Services\CivitaiBrowser\CivitaiWaitlist.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;

namespace DiffusionNexus.UI.Services.CivitaiBrowser;

/// <summary>
/// Early-access waitlist: versions the user wants once their paywall lapses.
/// Deadlines are captured from browse data at add time ("check once") and only
/// re-fetched on the explicit Update / Move-to-queue actions; the countdown and
/// tab badge are computed locally. Mirrors <see cref="CivitaiDownloadQueue"/>:
/// ObservableObject (not DI-registered), JSON snapshot in LocalAppData, and a
/// persist-path override so tests never touch the real file.
/// </summary>
public sealed class CivitaiWaitlist : ObservableObject
{
    private const string PersistFileName = "civitai-waitlist.json";

    private readonly string? _persistPathOverride;
    private readonly ICivitaiClient? _civitaiClient;
    private readonly IUnifiedLogger? _logger;

    public CivitaiWaitlist(
        ICivitaiClient? civitaiClient,
        IUnifiedLogger? logger,
        string? persistPathOverride = null)
    {
        _civitaiClient = civitaiClient;
        _logger = logger;
        _persistPathOverride = persistPathOverride;
        Entries.CollectionChanged += (_, _) => RaiseCounts();
        TryRestore();
        RefreshAvailability();
    }

    public ObservableCollection<CivitaiWaitlistEntry> Entries { get; } = [];

    /// <summary>Number of entries currently downloadable — drives the tab badge.</summary>
    public int AvailableCount => Entries.Count(e => e.IsAvailable);

    public bool HasAvailable => AvailableCount > 0;

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(AvailableCount));
        OnPropertyChanged(nameof(HasAvailable));
    }

    /// <summary>
    /// Adds a browse-result version to the waitlist. Rejects duplicates (by
    /// version id, same rule as the queue) and permanently paid versions —
    /// those never become free, so waiting is pointless. The deadline comes
    /// from the version data already loaded in the browser; no API call.
    /// </summary>
    public bool TryAdd(CivitaiResultViewModel result, CivitaiVersionPickItemViewModel pick, DateTimeOffset? utcNow = null)
    {
        if (result.Model is null) return false;

        if (pick.Version.IsPermanentlyPaid())
        {
            _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
                $"Not waitlisted (permanently paid): {result.Name} — {pick.Name}");
            return false;
        }

        if (Entries.Any(e => e.VersionId == pick.Version.Id))
        {
            _logger?.Debug(LogCategory.Download, "CivitaiWaitlist",
                $"Duplicate waitlist add skipped: {result.Name} ({pick.Name}) — version {pick.Version.Id} already listed");
            return false;
        }

        var primary = pick.Version.Files.FirstOrDefault(f => f.Primary == true) ?? pick.Version.Files.FirstOrDefault();
        var entry = new CivitaiWaitlistEntry
        {
            ModelId = result.Model.Id,
            VersionId = pick.Version.Id,
            ModelName = result.Name,
            VersionName = pick.Name,
            BaseModel = pick.BaseModel,
            Category = result.Category,
            FileName = primary?.Name ?? $"{result.Name}_{pick.Version.Id}.safetensors",
            DownloadUrl = primary?.DownloadUrl ?? pick.Version.DownloadUrl ?? string.Empty,
            SizeBytes = pick.SizeBytes,
            SizeDisplay = pick.SizeDisplay,
            ExpectedSha256 = primary?.Hashes?.SHA256,
            PreviewImageUrl = pick.Version.Images.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Url))?.Url,
            IsNsfw = result.IsNsfw,
            AddedAt = utcNow ?? DateTimeOffset.UtcNow,
            EarlyAccessDeadline = pick.Version.EarlyAccessDeadline ?? pick.Version.PaidAccess?.EndsAt
        };
        entry.RefreshAvailability(utcNow);
        Entries.Add(entry);
        Persist();
        _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
            $"Waitlisted: {result.Name} — {pick.Name} (free {entry.EarlyAccessDeadline?.ToString("u") ?? "at unknown date"})",
            $"VersionId: {pick.Version.Id}\nDeadline: {entry.EarlyAccessDeadline?.ToString("u") ?? "(none)"}\nFile: {entry.FileName}");
        return true;
    }

    public void Remove(CivitaiWaitlistEntry entry)
    {
        Entries.Remove(entry);
        Persist();
        _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
            $"Removed from waitlist: {entry.ModelName} — {entry.VersionName}");
    }

    /// <summary>
    /// Local-only tick: recomputes every entry's countdown/availability and the
    /// badge counts from stored deadlines. Called by the UI timer — zero API calls.
    /// </summary>
    public void RefreshAvailability(DateTimeOffset? utcNow = null)
    {
        foreach (var e in Entries) e.RefreshAvailability(utcNow);
        RaiseCounts();
    }

    #region Persistence

    private string GetPersistPath()
    {
        if (_persistPathOverride is not null) return _persistPathOverride;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiffusionNexus");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, PersistFileName);
    }

    private void Persist()
    {
        try
        {
            var snapshot = Entries.Select(e => new PersistedEntry
            {
                ModelId = e.ModelId,
                VersionId = e.VersionId,
                ModelName = e.ModelName,
                VersionName = e.VersionName,
                BaseModel = e.BaseModel,
                Category = e.Category,
                FileName = e.FileName,
                DownloadUrl = e.DownloadUrl,
                SizeDisplay = e.SizeDisplay,
                SizeBytes = e.SizeBytes,
                ExpectedSha256 = e.ExpectedSha256,
                PreviewImageUrl = e.PreviewImageUrl,
                IsNsfw = e.IsNsfw,
                AddedAt = e.AddedAt,
                EarlyAccessDeadline = e.EarlyAccessDeadline,
                LastCheckedAt = e.LastCheckedAt,
                Status = e.Status,
                StatusDetail = e.StatusDetail
            }).ToList();
            File.WriteAllText(GetPersistPath(), JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Download, "CivitaiWaitlist", $"Persist failed: {ex.Message}");
        }
    }

    private void TryRestore()
    {
        try
        {
            var path = GetPersistPath();
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return;
            var snapshot = JsonSerializer.Deserialize<List<PersistedEntry>>(json);
            if (snapshot is null) return;
            foreach (var p in snapshot)
            {
                var entry = new CivitaiWaitlistEntry
                {
                    ModelId = p.ModelId,
                    VersionId = p.VersionId,
                    ModelName = p.ModelName,
                    VersionName = p.VersionName,
                    BaseModel = p.BaseModel,
                    Category = p.Category,
                    FileName = p.FileName,
                    DownloadUrl = p.DownloadUrl,
                    SizeDisplay = p.SizeDisplay,
                    SizeBytes = p.SizeBytes,
                    ExpectedSha256 = p.ExpectedSha256,
                    PreviewImageUrl = p.PreviewImageUrl,
                    IsNsfw = p.IsNsfw,
                    AddedAt = p.AddedAt,
                    EarlyAccessDeadline = p.EarlyAccessDeadline,
                    LastCheckedAt = p.LastCheckedAt,
                    Status = p.Status,
                    StatusDetail = p.StatusDetail
                };
                Entries.Add(entry);
            }
            _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
                $"Restored {Entries.Count} waitlist entr{(Entries.Count == 1 ? "y" : "ies")} from disk.");
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Download, "CivitaiWaitlist", $"Restore failed: {ex.Message}");
        }
    }

    private sealed class PersistedEntry
    {
        public int ModelId { get; set; }
        public int VersionId { get; set; }
        public string ModelName { get; set; } = string.Empty;
        public string VersionName { get; set; } = string.Empty;
        public string BaseModel { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string SizeDisplay { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string? ExpectedSha256 { get; set; }
        public string? PreviewImageUrl { get; set; }
        public bool IsNsfw { get; set; }
        public DateTimeOffset AddedAt { get; set; }
        public DateTimeOffset? EarlyAccessDeadline { get; set; }
        public DateTimeOffset? LastCheckedAt { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public WaitlistEntryStatus Status { get; set; }

        public string? StatusDetail { get; set; }
    }

    #endregion
}
```

Note: `_civitaiClient` is unused until Task 3 — that's expected; suppress nothing, the field is referenced in the next task.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiWaitlistTests"`
Expected: PASS (7 tests). Also run the Task 1 filter again — still green.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/CivitaiBrowser/CivitaiWaitlist.cs DiffusionNexus.Tests/Viewer/CivitaiWaitlistTests.cs
git commit -m "Add CivitaiWaitlist service with JSON persistence" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Re-check against the Civitai API (`RefreshEntryAsync` / `RefreshAllAsync`)

**Files:**
- Modify: `DiffusionNexus.UI\Services\CivitaiBrowser\CivitaiWaitlist.cs`
- Test: `DiffusionNexus.Tests\Viewer\CivitaiWaitlistRefreshTests.cs`

**Interfaces:**
- Consumes: `ICivitaiClient.GetModelVersionAsync(int modelVersionId, string? apiKey = null, CancellationToken cancellationToken = default)` → `Task<CivitaiModelVersion?>`; returns **null on HTTP 404** and **throws `HttpRequestException`** on other failures (verified in `CivitaiClient.cs:221-246`). `IsEarlyAccessActive` / `IsPermanentlyPaid` extensions.
- Produces (on `CivitaiWaitlist`):
  - `public async Task<CivitaiModelVersion?> RefreshEntryAsync(CivitaiWaitlistEntry entry, string? apiKey, CancellationToken ct = default, DateTimeOffset? utcNow = null)` — returns the fetched version (null on 404/error) so callers can reuse it without a second fetch.
  - `public async Task RefreshAllAsync(string? apiKey, CancellationToken ct = default, DateTimeOffset? utcNow = null)`

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests\Viewer\CivitaiWaitlistRefreshTests.cs`:

```csharp
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the API re-check outcome matrix: deadline extended, confirmed free,
/// switched to permanent, deleted (404 → null), and network failure. Errors keep
/// the old data so a flaky connection can't wipe a countdown.
/// </summary>
public sealed class CivitaiWaitlistRefreshTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-waitlist-refresh").FullName;
    private readonly Mock<ICivitaiClient> _client = new();

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private CivitaiWaitlist CreateWithEntry(out CivitaiWaitlistEntry entry, DateTimeOffset? deadline = null)
    {
        var wl = new CivitaiWaitlist(_client.Object, null,
            persistPathOverride: Path.Combine(_tempDir, $"{Guid.NewGuid():N}.json"));
        var (result, pick) = CivitaiWaitlistTests.Card(
            CivitaiWaitlistTests.Version(500, deadline ?? Now.AddDays(2)));
        wl.TryAdd(result, pick, Now);
        entry = wl.Entries.Single();
        return wl;
    }

    private void ClientReturns(CivitaiModelVersion? version) =>
        _client.Setup(c => c.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(version);

    [Fact]
    public async Task ExtendedDeadline_UpdatesDeadlineAndStaysWaiting()
    {
        var wl = CreateWithEntry(out var entry);
        ClientReturns(CivitaiWaitlistTests.Version(500, Now.AddDays(14)));

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.EarlyAccessDeadline.Should().Be(Now.AddDays(14));
        entry.Status.Should().Be(WaitlistEntryStatus.Waiting);
        entry.LastCheckedAt.Should().Be(Now);
    }

    [Fact]
    public async Task ConfirmedFree_BecomesAvailable()
    {
        var wl = CreateWithEntry(out var entry);
        // No EA signals at all → IsEarlyAccessActive == false.
        ClientReturns(new CivitaiModelVersion
        {
            Id = 500,
            Name = "v500",
            BaseModel = "Krea 2",
            DownloadUrl = "https://civitai.example/api/download/models/500"
        });

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.Status.Should().Be(WaitlistEntryStatus.Available);
        entry.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task SwitchedToPermanent_IsFlaggedNotDeleted()
    {
        var wl = CreateWithEntry(out var entry);
        ClientReturns(CivitaiWaitlistTests.Version(500, deadline: null, permanent: true));

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.Status.Should().Be(WaitlistEntryStatus.PermanentlyPaid);
        entry.IsAvailable.Should().BeFalse();
        wl.Entries.Should().ContainSingle("flagged entries stay listed until the user removes them");
    }

    [Fact]
    public async Task DeletedVersion_404_IsFlaggedUnavailable()
    {
        var wl = CreateWithEntry(out var entry);
        ClientReturns(null);

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.Status.Should().Be(WaitlistEntryStatus.Unavailable);
        entry.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task NetworkError_KeepsOldDeadlineAndLastChecked()
    {
        var wl = CreateWithEntry(out var entry, deadline: Now.AddDays(2));
        var beforeChecked = entry.LastCheckedAt;
        _client.Setup(c => c.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new HttpRequestException("boom"));

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.Status.Should().Be(WaitlistEntryStatus.CheckFailed);
        entry.StatusDetail.Should().Contain("boom");
        entry.EarlyAccessDeadline.Should().Be(Now.AddDays(2), "a flaky connection must not wipe the countdown");
        entry.LastCheckedAt.Should().Be(beforeChecked);
    }

    [Fact]
    public async Task RefreshAll_ChecksEveryEntryAndUpdatesCounts()
    {
        var wl = new CivitaiWaitlist(_client.Object, null,
            persistPathOverride: Path.Combine(_tempDir, "all.json"));
        var (r1, p1) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(1, Now.AddDays(2)), modelId: 1);
        var (r2, p2) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(2, Now.AddDays(2)), modelId: 2);
        wl.TryAdd(r1, p1, Now);
        wl.TryAdd(r2, p2, Now);
        // Entry 1 is now free; entry 2 got extended.
        _client.Setup(c => c.GetModelVersionAsync(1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CivitaiModelVersion { Id = 1, Name = "v1", BaseModel = "Krea 2" });
        _client.Setup(c => c.GetModelVersionAsync(2, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(CivitaiWaitlistTests.Version(2, Now.AddDays(30)));

        await wl.RefreshAllAsync(apiKey: null, utcNow: Now);

        wl.AvailableCount.Should().Be(1);
        wl.Entries.Single(e => e.VersionId == 2).EarlyAccessDeadline.Should().Be(Now.AddDays(30));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiWaitlistRefreshTests"`
Expected: compile FAILURE — `RefreshEntryAsync`/`RefreshAllAsync` do not exist. (If `Card`/`Version` visibility errors appear: they are declared `internal static` in `CivitaiWaitlistTests` — same assembly, so they resolve.)

- [ ] **Step 3: Implement the re-check**

Add to `CivitaiWaitlist` (below `RefreshAvailability`, above `#region Persistence`):

```csharp
    // CivitaiClient retries 429s but has NO client-side throttle — cap concurrent
    // re-checks the same way CivitaiResultViewModel gates video extraction.
    private static readonly SemaphoreSlim s_refreshGate = new(3, 3);

    /// <summary>
    /// Re-checks one entry against the API and applies the outcome matrix:
    /// still gated → deadline updated (creators extend early access); free →
    /// Available; permanent → flagged (never auto-removed); 404 → Unavailable;
    /// network error → CheckFailed with old data kept. Returns the fetched
    /// version so move-to-queue can reuse it without a second fetch.
    /// </summary>
    public async Task<CivitaiModelVersion?> RefreshEntryAsync(
        CivitaiWaitlistEntry entry, string? apiKey, CancellationToken ct = default, DateTimeOffset? utcNow = null)
    {
        if (_civitaiClient is null) return null;
        var now = utcNow ?? DateTimeOffset.UtcNow;

        await s_refreshGate.WaitAsync(ct).ConfigureAwait(false);
        CivitaiModelVersion? version = null;
        try
        {
            _logger?.Debug(LogCategory.Download, "CivitaiWaitlist",
                $"Re-checking: {entry.ModelName} — {entry.VersionName} (version {entry.VersionId})");
            version = await _civitaiClient.GetModelVersionAsync(entry.VersionId, apiKey, ct).ConfigureAwait(false);

            if (version is null)
            {
                entry.Status = WaitlistEntryStatus.Unavailable;
                entry.StatusDetail = "The model version no longer exists on Civitai.";
            }
            else if (version.IsPermanentlyPaid())
            {
                entry.Status = WaitlistEntryStatus.PermanentlyPaid;
                entry.StatusDetail = null;
            }
            else if (version.IsEarlyAccessActive(now))
            {
                entry.EarlyAccessDeadline = version.EarlyAccessDeadline ?? version.PaidAccess?.EndsAt;
                entry.Status = WaitlistEntryStatus.Waiting;
                entry.StatusDetail = null;
            }
            else
            {
                entry.EarlyAccessDeadline = version.EarlyAccessDeadline;
                entry.Status = WaitlistEntryStatus.Available;
                entry.StatusDetail = null;
            }
            entry.LastCheckedAt = now;
            _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
                $"Re-check result: {entry.ModelName} — {entry.VersionName} → {entry.Status}" +
                (entry.EarlyAccessDeadline is { } d ? $" (deadline {d:u})" : ""));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Old deadline and LastCheckedAt survive — a flaky connection must
            // not wipe a countdown the user is relying on.
            entry.Status = WaitlistEntryStatus.CheckFailed;
            entry.StatusDetail = ex.Message;
            _logger?.Warn(LogCategory.Download, "CivitaiWaitlist",
                $"Re-check failed for {entry.ModelName} — {entry.VersionName}: {ex.Message}");
        }
        finally
        {
            s_refreshGate.Release();
        }

        entry.RefreshAvailability(utcNow);
        return version;
    }

    /// <summary>"Update all" — re-checks every entry (gated to 3 concurrent), then persists.</summary>
    public async Task RefreshAllAsync(string? apiKey, CancellationToken ct = default, DateTimeOffset? utcNow = null)
    {
        if (_civitaiClient is null)
        {
            RefreshAvailability(utcNow);
            return;
        }
        var entries = Entries.ToList();
        _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
            $"Updating waitlist: re-checking {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} against Civitai…");
        await Task.WhenAll(entries.Select(e => RefreshEntryAsync(e, apiKey, ct, utcNow)));
        Persist();
        RefreshAvailability(utcNow);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiWaitlist"`
Expected: PASS (Task 1 + 2 + 3 filters all green — the shared filter catches all three classes).

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/CivitaiBrowser/CivitaiWaitlist.cs DiffusionNexus.Tests/Viewer/CivitaiWaitlistRefreshTests.cs
git commit -m "Add waitlist API re-check with outcome matrix" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Queue handoff — `EnqueueFromWaitlist` + `MoveReadyToQueueAsync`

**Files:**
- Modify: `DiffusionNexus.UI\Services\CivitaiBrowser\CivitaiDownloadQueue.cs` (add overload after `Enqueue`, ~line 274)
- Modify: `DiffusionNexus.UI\Services\CivitaiBrowser\CivitaiWaitlist.cs`
- Test: `DiffusionNexus.Tests\Viewer\CivitaiWaitlistMoveToQueueTests.cs`

**Interfaces:**
- Consumes: `CivitaiDownloadQueue.Jobs`, `CivitaiDownloadJob`, `Destination.BuildTargetDirectory(string, string)`, `RefreshEntryAsync` (Task 3).
- Produces:
  - `public CivitaiDownloadJob? EnqueueFromWaitlist(CivitaiWaitlistEntry entry, CivitaiModelVersion? freshVersion)` on `CivitaiDownloadQueue` (null on version-id duplicate).
  - `public async Task<int> MoveReadyToQueueAsync(CivitaiDownloadQueue queue, string? apiKey, CancellationToken ct = default, DateTimeOffset? utcNow = null)` on `CivitaiWaitlist` — returns number moved.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests\Viewer\CivitaiWaitlistMoveToQueueTests.cs`:

```csharp
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers "Move ready to queue": only deadline-passed entries move, each is
/// re-verified against the API first, confirmed-free ones become queue jobs and
/// leave the waitlist, still-gated ones stay with a corrected deadline.
/// </summary>
public sealed class CivitaiWaitlistMoveToQueueTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-waitlist-move").FullName;
    private readonly Mock<ICivitaiClient> _client = new();

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private CivitaiDownloadQueue Queue() => new(null, null, null, null,
        persistPathOverride: Path.Combine(_tempDir, $"q-{Guid.NewGuid():N}.json"));

    private CivitaiWaitlist Waitlist(ICivitaiClient? client) => new(client, null,
        persistPathOverride: Path.Combine(_tempDir, $"w-{Guid.NewGuid():N}.json"));

    private static CivitaiModelVersion FreeVersion(int id) => new()
    {
        Id = id,
        Name = $"v{id}",
        BaseModel = "Krea 2",
        DownloadUrl = $"https://civitai.example/api/download/models/{id}"
    };

    [Fact]
    public async Task ReadyEntry_ConfirmedFree_MovesToQueueAndLeavesWaitlist()
    {
        var wl = Waitlist(_client.Object);
        var (r, p) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(600, Now.AddMinutes(-5)));
        wl.TryAdd(r, p, Now);
        _client.Setup(c => c.GetModelVersionAsync(600, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(FreeVersion(600));
        var queue = Queue();

        var moved = await wl.MoveReadyToQueueAsync(queue, apiKey: null, utcNow: Now);

        moved.Should().Be(1);
        wl.Entries.Should().BeEmpty();
        var job = queue.Jobs.Single();
        job.VersionId.Should().Be(600);
        job.IsEarlyAccess.Should().BeFalse("the version was just verified free");
        job.Status.Should().Be(JobStatus.Queued);
        job.CivitaiVersion.Should().NotBeNull("the fresh version avoids a re-fetch at download time");
    }

    [Fact]
    public async Task ReadyEntry_StillGatedAfterReVerify_StaysWithCorrectedDeadline()
    {
        var wl = Waitlist(_client.Object);
        var (r, p) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(601, Now.AddMinutes(-5)));
        wl.TryAdd(r, p, Now);
        // Creator extended EA: API now says 10 more days.
        _client.Setup(c => c.GetModelVersionAsync(601, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(CivitaiWaitlistTests.Version(601, Now.AddDays(10)));
        var queue = Queue();

        var moved = await wl.MoveReadyToQueueAsync(queue, apiKey: null, utcNow: Now);

        moved.Should().Be(0);
        queue.Jobs.Should().BeEmpty();
        var entry = wl.Entries.Single();
        entry.EarlyAccessDeadline.Should().Be(Now.AddDays(10));
        entry.Status.Should().Be(WaitlistEntryStatus.Waiting);
    }

    [Fact]
    public async Task NotYetReadyEntries_AreNeverTouched()
    {
        var wl = Waitlist(_client.Object);
        var (r, p) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(602, Now.AddDays(3)));
        wl.TryAdd(r, p, Now);
        var queue = Queue();

        var moved = await wl.MoveReadyToQueueAsync(queue, apiKey: null, utcNow: Now);

        moved.Should().Be(0);
        _client.Verify(c => c.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never, "entries still counting down must not trigger API calls");
        wl.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task VersionAlreadyInQueue_EntryIsRemovedWithoutDuplicateJob()
    {
        var wl = Waitlist(_client.Object);
        var (r, p) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(603, Now.AddMinutes(-5)));
        wl.TryAdd(r, p, Now);
        _client.Setup(c => c.GetModelVersionAsync(603, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(FreeVersion(603));
        var queue = Queue();
        queue.Jobs.Add(new CivitaiDownloadJob { VersionId = 603, ModelName = "Test LoRA", VersionName = "v603" });

        var moved = await wl.MoveReadyToQueueAsync(queue, apiKey: null, utcNow: Now);

        moved.Should().Be(1, "the entry's goal (version queued) is met either way");
        queue.Jobs.Should().HaveCount(1);
        wl.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task NoClient_MovesFromStoredDataOnly()
    {
        // Headless/design-time: no API to verify against — trust the local countdown.
        var wl = Waitlist(null);
        var (r, p) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(604, Now.AddMinutes(-5)));
        wl.TryAdd(r, p, Now);
        var queue = Queue();

        var moved = await wl.MoveReadyToQueueAsync(queue, apiKey: null, utcNow: Now);

        moved.Should().Be(1);
        queue.Jobs.Single().DownloadUrl.Should().Be("https://civitai.example/api/download/models/604");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiWaitlistMoveToQueueTests"`
Expected: compile FAILURE — `MoveReadyToQueueAsync`/`EnqueueFromWaitlist` do not exist.

- [ ] **Step 3: Implement the queue overload**

In `DiffusionNexus.UI\Services\CivitaiBrowser\CivitaiDownloadQueue.cs`, insert directly after the existing `Enqueue` method (after its closing brace, ~line 274):

```csharp
    /// <summary>
    /// Enqueues a waitlist entry whose early-access gate has lapsed. Prefers file
    /// metadata from <paramref name="freshVersion"/> (the just-re-verified API
    /// response) and falls back to the data captured at waitlist-add time —
    /// <paramref name="freshVersion"/> is null only in headless/no-client runs.
    /// Returns null when the version is already queued (same dedup as Enqueue).
    /// </summary>
    public CivitaiDownloadJob? EnqueueFromWaitlist(CivitaiWaitlistEntry entry, CivitaiModelVersion? freshVersion)
    {
        if (Jobs.Any(j => j.VersionId == entry.VersionId))
        {
            _logger?.Debug(LogCategory.Download, "CivitaiQueue",
                $"Waitlist move skipped duplicate: {entry.ModelName} ({entry.VersionName}) — version {entry.VersionId} already in queue");
            return null;
        }

        var primary = freshVersion?.Files.FirstOrDefault(f => f.Primary == true) ?? freshVersion?.Files.FirstOrDefault();
        var job = new CivitaiDownloadJob
        {
            ModelId = entry.ModelId,
            VersionId = entry.VersionId,
            ModelName = entry.ModelName,
            VersionName = entry.VersionName,
            BaseModel = entry.BaseModel,
            Category = entry.Category,
            FileName = primary?.Name ?? entry.FileName,
            DownloadUrl = primary?.DownloadUrl ?? freshVersion?.DownloadUrl ?? entry.DownloadUrl,
            SizeDisplay = entry.SizeDisplay,
            SizeBytes = entry.SizeBytes,
            IsEarlyAccess = false, // just verified as no longer gated
            ExpectedSha256 = primary?.Hashes?.SHA256 ?? entry.ExpectedSha256,
            PreviewImageUrl = entry.PreviewImageUrl,
            CivitaiVersion = freshVersion
        };
        job.ExpectedTargetDir = Destination.BuildTargetDirectory(job.BaseModel, job.Category);
        Jobs.Add(job);
        Persist();
        RaiseCountsChanged();
        _logger?.Info(LogCategory.Download, "CivitaiQueue",
            $"Enqueued from waitlist: {entry.ModelName} — {entry.VersionName} ({entry.BaseModel}, {entry.SizeDisplay})",
            $"VersionId: {entry.VersionId}\nFile: {job.FileName}\nUrl: {job.DownloadUrl}");
        return job;
    }
```

- [ ] **Step 4: Implement the move on `CivitaiWaitlist`**

Add below `RefreshAllAsync`:

```csharp
    /// <summary>
    /// "Move ready to queue": takes entries whose countdown has ended, re-verifies
    /// each against the API (stored deadlines go stale — creators extend early
    /// access or flip it to permanent), enqueues the confirmed-free ones, and keeps
    /// the rest on the waitlist with their corrected state. Returns number moved.
    /// </summary>
    public async Task<int> MoveReadyToQueueAsync(
        CivitaiDownloadQueue queue, string? apiKey, CancellationToken ct = default, DateTimeOffset? utcNow = null)
    {
        var ready = Entries.Where(e => e.IsAvailable).ToList();
        if (ready.Count == 0) return 0;

        _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
            $"Move to queue: verifying {ready.Count} ready entr{(ready.Count == 1 ? "y" : "ies")}…");

        var moved = 0;
        foreach (var entry in ready)
        {
            CivitaiModelVersion? version = null;
            if (_civitaiClient is not null)
            {
                version = await RefreshEntryAsync(entry, apiKey, ct, utcNow);
                if (entry.Status != WaitlistEntryStatus.Available)
                {
                    _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
                        $"Kept on waitlist after re-check: {entry.ModelName} — {entry.VersionName} ({entry.Status})");
                    continue;
                }
            }

            // A null job means the version is already queued — the entry's goal is
            // met either way, so it leaves the waitlist in both cases.
            queue.EnqueueFromWaitlist(entry, version);
            Entries.Remove(entry);
            moved++;
        }

        Persist();
        RefreshAvailability(utcNow);
        _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
            $"Move to queue complete: {moved} of {ready.Count} moved.");
        return moved;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiWaitlist"`
Expected: PASS (all four waitlist test classes).

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/Services/CivitaiBrowser/CivitaiDownloadQueue.cs DiffusionNexus.UI/Services/CivitaiBrowser/CivitaiWaitlist.cs DiffusionNexus.Tests/Viewer/CivitaiWaitlistMoveToQueueTests.cs
git commit -m "Add waitlist-to-queue handoff with re-verification" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: Browser VM integration — ctor wiring, commands, countdown timer

**Files:**
- Modify: `DiffusionNexus.UI\ViewModels\CivitaiBrowser\CivitaiBrowserViewModel.cs` (ctors ~lines 39-97, commands region ~line 400)
- Modify: `DiffusionNexus.UI\ViewModels\LoraViewerViewModel.cs:324-325`
- Modify: `DiffusionNexus.Tests\Viewer\CivitaiBrowserClearQueueTests.cs:29-34, 92-94`
- Modify: `DiffusionNexus.Tests\Viewer\CivitaiBrowserViewModelBaseModelFilterTests.cs:27`
- Modify: `DiffusionNexus.Tests\Viewer\LoraViewerFilterPersistenceTests.cs:103-108`
- Test: `DiffusionNexus.Tests\Viewer\CivitaiBrowserWaitlistCommandTests.cs`

**Interfaces:**
- Consumes: `CivitaiWaitlist` (Tasks 2-4), `GetApiKeyAsync()` (existing private, `CivitaiBrowserViewModel.cs:1001`).
- Produces (on `CivitaiBrowserViewModel`):
  - ctor gains a `CivitaiWaitlist waitlist` parameter between `queue` and `sharedBaseModelSource`.
  - `public CivitaiWaitlist Waitlist { get; }` (expression-bodied over `_waitlist`)
  - `public Action<string> UrlOpener { get; set; }` (test seam; default `Process.Start` with `UseShellExecute`)
  - commands: `RemoveWaitlistEntryCommand`, `OpenWaitlistEntryOnCivitaiCommand` (both take `CivitaiWaitlistEntry?`), `UpdateWaitlistCommand`, `MoveReadyWaitlistToQueueCommand`
  - `private void OpenUrl(string url)` helper (used again in Task 6)

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests\Viewer\CivitaiBrowserWaitlistCommandTests.cs`:

```csharp
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the browser VM's waitlist commands: remove, open-on-Civitai (with the
/// NSFW civitai.red host swap), and move-ready reporting to the status bar.
/// </summary>
public sealed class CivitaiBrowserWaitlistCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-browser-waitlist").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private (CivitaiBrowserViewModel Vm, CivitaiWaitlist Waitlist, CivitaiDownloadQueue Queue) Create()
    {
        var queue = new CivitaiDownloadQueue(null, null, null, null,
            persistPathOverride: Path.Combine(_tempDir, "queue.json"));
        var waitlist = new CivitaiWaitlist(null, null,
            persistPathOverride: Path.Combine(_tempDir, "waitlist.json"));
        var vm = new CivitaiBrowserViewModel(null, null, null, queue, waitlist, null);
        return (vm, waitlist, queue);
    }

    private static CivitaiWaitlistEntry Entry(int versionId, bool nsfw = false, DateTimeOffset? deadline = null) => new()
    {
        ModelId = 900,
        VersionId = versionId,
        ModelName = "Model",
        VersionName = $"v{versionId}",
        DownloadUrl = $"https://civitai.example/api/download/models/{versionId}",
        IsNsfw = nsfw,
        EarlyAccessDeadline = deadline
    };

    [Fact]
    public void RemoveWaitlistEntry_RemovesFromService()
    {
        var (vm, waitlist, _) = Create();
        var entry = Entry(1);
        waitlist.Entries.Add(entry);

        vm.RemoveWaitlistEntryCommand.Execute(entry);

        waitlist.Entries.Should().BeEmpty();
    }

    [Fact]
    public void OpenWaitlistEntry_UsesCivitaiCom_AndVersionDeepLink()
    {
        var (vm, waitlist, _) = Create();
        string? opened = null;
        vm.UrlOpener = url => opened = url;
        var entry = Entry(2);
        waitlist.Entries.Add(entry);

        vm.OpenWaitlistEntryOnCivitaiCommand.Execute(entry);

        opened.Should().Be("https://civitai.com/models/900?modelVersionId=2");
    }

    [Fact]
    public void OpenWaitlistEntry_NsfwModel_UsesCivitaiRed()
    {
        var (vm, _, _) = Create();
        string? opened = null;
        vm.UrlOpener = url => opened = url;

        vm.OpenWaitlistEntryOnCivitaiCommand.Execute(Entry(3, nsfw: true));

        opened.Should().StartWith("https://civitai.red/");
    }

    [Fact]
    public async Task MoveReadyCommand_ReportsCountInStatusMessage()
    {
        var (vm, waitlist, queue) = Create();
        var entry = Entry(4, deadline: Now.AddMinutes(-1));
        entry.RefreshAvailability(Now);
        waitlist.Entries.Add(entry);

        await vm.MoveReadyWaitlistToQueueCommand.ExecuteAsync(null);

        queue.Jobs.Should().ContainSingle();
        vm.StatusMessage.Should().Contain("1");
    }

    [Fact]
    public async Task UpdateCommand_WithoutClient_StillRefreshesCountsWithoutThrowing()
    {
        var (vm, waitlist, _) = Create();
        var entry = Entry(5, deadline: DateTimeOffset.UtcNow.AddMilliseconds(-1));
        waitlist.Entries.Add(entry);

        await vm.UpdateWaitlistCommand.ExecuteAsync(null);

        waitlist.AvailableCount.Should().Be(1);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiBrowserWaitlistCommandTests"`
Expected: compile FAILURE — 6-arg ctor and the four commands do not exist.

- [ ] **Step 3: Extend `CivitaiBrowserViewModel`**

3a. Field (next to `private readonly CivitaiDownloadQueue _queue;`, line 28):

```csharp
    private readonly CivitaiWaitlist _waitlist;
    private Avalonia.Threading.DispatcherTimer? _waitlistCountdownTimer;
```

3b. Design-time ctor (lines 39-44) — replace the delegation line:

```csharp
    public CivitaiBrowserViewModel()
        : this(null, null, null, new CivitaiDownloadQueue(null), new CivitaiWaitlist(null, null), null)
```

3c. Runtime ctor (line 46) — add the parameter between `queue` and `sharedBaseModelSource`, assign it, and start the timer. New signature and additions:

```csharp
    public CivitaiBrowserViewModel(
        ICivitaiClient? civitaiClient,
        IAppSettingsService? settingsService,
        IUnifiedLogger? logger,
        CivitaiDownloadQueue queue,
        CivitaiWaitlist waitlist,
        ObservableCollection<BaseModelFilterItem>? sharedBaseModelSource)
```

after `_queue = queue;` add:

```csharp
        _waitlist = waitlist;
        StartWaitlistCountdownTimer();
```

3d. In the `Results + Queue` region (below `public CivitaiDownloadQueue Queue => _queue;`, line 202):

```csharp
    public CivitaiWaitlist Waitlist => _waitlist;
```

3e. New members in the `Commands` region (after `RetryJobAsync`, ~line 403):

```csharp
    /// <summary>
    /// Launches URLs in the default browser. Swappable so tests can capture the
    /// URL instead of actually spawning a browser window.
    /// </summary>
    public Action<string> UrlOpener { get; set; } = url =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    private void OpenUrl(string url)
    {
        try
        {
            UrlOpener(url);
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.General, "CivitaiWaitlist", $"Failed to launch browser for {url}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RemoveWaitlistEntry(CivitaiWaitlistEntry? entry)
    {
        if (entry is not null) _waitlist.Remove(entry);
    }

    [RelayCommand]
    private void OpenWaitlistEntryOnCivitai(CivitaiWaitlistEntry? entry)
    {
        if (entry is null) return;
        // civitai.com hides NSFW from unauthenticated visitors; route those to the mirror.
        var host = entry.IsNsfw ? "civitai.red" : "civitai.com";
        OpenUrl($"https://{host}/models/{entry.ModelId}?modelVersionId={entry.VersionId}");
    }

    /// <summary>"Update" button on the Waitlist tab — re-checks every entry against the API.</summary>
    [RelayCommand]
    private async Task UpdateWaitlistAsync()
    {
        StatusMessage = "Checking waitlist against Civitai…";
        var apiKey = await GetApiKeyAsync();
        await _waitlist.RefreshAllAsync(apiKey);
        StatusMessage = $"Waitlist updated — {_waitlist.AvailableCount} of {_waitlist.Entries.Count} available.";
    }

    /// <summary>"Move ready to queue" button — re-verifies ready entries, enqueues the free ones.</summary>
    [RelayCommand]
    private async Task MoveReadyWaitlistToQueueAsync()
    {
        var apiKey = await GetApiKeyAsync();
        var moved = await _waitlist.MoveReadyToQueueAsync(_queue, apiKey);
        StatusMessage = moved == 0
            ? "No waitlist entries are ready to download yet."
            : $"Moved {moved} LoRA{(moved == 1 ? "" : "s")} from the waitlist to the download queue.";
    }

    /// <summary>
    /// Local 1-minute tick keeping countdowns and the tab badge fresh without any
    /// API traffic. Skipped headless (unit tests / no Avalonia app) — DispatcherTimer
    /// needs a running Avalonia dispatcher.
    /// </summary>
    private void StartWaitlistCountdownTimer()
    {
        if (Avalonia.Application.Current is null) return;
        _waitlistCountdownTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _waitlistCountdownTimer.Tick += (_, _) => _waitlist.RefreshAvailability();
        _waitlistCountdownTimer.Start();
    }
```

Add `using DiffusionNexus.UI.Services.CivitaiBrowser;` only if not already present (it is — line 13).

- [ ] **Step 4: Update all construction sites**

4a. `DiffusionNexus.UI\ViewModels\LoraViewerViewModel.cs` — replace lines 324-325:

```csharp
        var queue = new CivitaiDownloadQueue(downloadService, _logger, _civitaiClient, destination);
        var waitlist = new CivitaiWaitlist(_civitaiClient, _logger);
        BrowserViewModel = new CivitaiBrowserViewModel(_civitaiClient, _settingsService, _logger, queue, waitlist, AvailableBaseModels);
```

4b. `DiffusionNexus.Tests\Viewer\CivitaiBrowserClearQueueTests.cs` — in `Create()` (line 29-31) and the headless test (line 92-94), add a waitlist argument after the queue:

```csharp
        var vm = new CivitaiBrowserViewModel(null, null, null, queue,
            new CivitaiWaitlist(null, null, persistPathOverride: Path.Combine(_tempDir, "waitlist.json")), null)
```

(headless variant uses `"waitlist-headless.json"`.)

4c. `DiffusionNexus.Tests\Viewer\CivitaiBrowserViewModelBaseModelFilterTests.cs:27`:

```csharp
        var vm = new CivitaiBrowserViewModel(null, null, null, new CivitaiDownloadQueue(null),
            new CivitaiWaitlist(null, null, persistPathOverride: Path.Combine(Path.GetTempPath(), $"dn-waitlist-{Guid.NewGuid():N}.json")), source);
```

(Note: `new CivitaiDownloadQueue(null)` at this call site already touches the real queue file via the 1-arg ctor — pre-existing behavior, leave it.)

4d. `DiffusionNexus.Tests\Viewer\LoraViewerFilterPersistenceTests.cs:103-108` — add the named argument:

```csharp
        var browser = new CivitaiBrowserViewModel(
            civitaiClient: null,
            settingsService: null,
            logger: null,
            queue: new CivitaiDownloadQueue(null, null, null, null, persistPathOverride: queuePersist),
            waitlist: new CivitaiWaitlist(null, null, persistPathOverride: Path.Combine(Path.GetTempPath(), $"dn-waitlist-{Guid.NewGuid():N}.json")),
            sharedBaseModelSource: source);
```

Add `using DiffusionNexus.UI.Services.CivitaiBrowser;` to any of these test files that lack it.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiBrowser"`
Expected: PASS — new command tests plus all pre-existing browser tests (clear-queue, base-model filter, filter persistence).

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiBrowserViewModel.cs DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs DiffusionNexus.Tests/Viewer/CivitaiBrowserClearQueueTests.cs DiffusionNexus.Tests/Viewer/CivitaiBrowserViewModelBaseModelFilterTests.cs DiffusionNexus.Tests/Viewer/LoraViewerFilterPersistenceTests.cs DiffusionNexus.Tests/Viewer/CivitaiBrowserWaitlistCommandTests.cs
git commit -m "Wire waitlist into browser VM with commands and countdown timer" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: Early-access dialog — waitlist + open-website options, permanent-paid listing

**Files:**
- Modify: `DiffusionNexus.UI\Views\Dialogs\EarlyAccessConfirmDialog.axaml.cs`
- Modify: `DiffusionNexus.UI\Views\Dialogs\EarlyAccessConfirmDialog.axaml`
- Modify: `DiffusionNexus.UI\ViewModels\CivitaiBrowser\CivitaiBrowserViewModel.cs` (`EnqueueWithEarlyAccessPromptAsync`, ~line 872)
- Test: `DiffusionNexus.Tests\Viewer\EarlyAccessChoiceTests.cs`

**Interfaces:**
- Consumes: `CivitaiWaitlist.TryAdd`, `OpenUrl` (Task 5), `IsPermanentlyPaid` (Task 1), `CivitaiDownloadQueue.Enqueue`.
- Produces:
  - `EarlyAccessConfirmResult` gains `AddToWaitlist`, `OpenWebsite`.
  - `EarlyAccessConfirmDialog(IReadOnlyList<string> earlyAccessTitles, IReadOnlyList<string>? permanentTitles = null)` — `EarlyAccessTitles` now means **temporary** EA (waitlistable); permanent items are the second list.
  - `public bool IsPermanentlyPaid { get; }` on `CivitaiVersionPickItemViewModel`.
  - `public void ApplyEarlyAccessChoice(EarlyAccessConfirmResult choice, List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> pairs)` on `CivitaiBrowserViewModel` — public so tests drive every branch without a dialog.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests\Viewer\EarlyAccessChoiceTests.cs`:

```csharp
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using DiffusionNexus.UI.Views.Dialogs;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the dialog-choice handling for early-access selections: waitlisting
/// temporary-EA picks (permanent ones are skipped — they never become free),
/// opening the Civitai pages, and the pre-existing skip/add-anyway paths.
/// </summary>
public sealed class EarlyAccessChoiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-ea-choice").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private (CivitaiBrowserViewModel Vm, CivitaiWaitlist Waitlist, CivitaiDownloadQueue Queue, List<string> Opened) Create()
    {
        var queue = new CivitaiDownloadQueue(null, null, null, null,
            persistPathOverride: Path.Combine(_tempDir, "queue.json"));
        var waitlist = new CivitaiWaitlist(null, null,
            persistPathOverride: Path.Combine(_tempDir, "waitlist.json"));
        var vm = new CivitaiBrowserViewModel(null, null, null, queue, waitlist, null);
        var opened = new List<string>();
        vm.UrlOpener = opened.Add;
        return (vm, waitlist, queue, opened);
    }

    private static List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> MixedPairs()
    {
        var free = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(1, deadline: null), modelId: 101, name: "Free LoRA");
        var tempEa = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(2, Now.AddDays(7)), modelId: 102, name: "EA LoRA");
        var permanent = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(3, deadline: null, permanent: true), modelId: 103, name: "Paid LoRA");
        return [free, tempEa, permanent];
    }

    [Fact]
    public void AddToWaitlist_QueuesFreeItems_WaitlistsTempEa_SkipsPermanent()
    {
        var (vm, waitlist, queue, _) = Create();

        vm.ApplyEarlyAccessChoice(EarlyAccessConfirmResult.AddToWaitlist, MixedPairs());

        queue.Jobs.Should().ContainSingle(j => j.VersionId == 1, "non-EA picks download immediately");
        waitlist.Entries.Should().ContainSingle(e => e.VersionId == 2, "temporary EA is waitlistable");
        waitlist.Entries.Should().NotContain(e => e.VersionId == 3, "permanently paid never becomes free");
        vm.StatusMessage.Should().Contain("permanently paid");
    }

    [Fact]
    public void OpenWebsite_QueuesFreeItems_OpensOnePageDistinctPerEaModel()
    {
        var (vm, _, queue, opened) = Create();

        vm.ApplyEarlyAccessChoice(EarlyAccessConfirmResult.OpenWebsite, MixedPairs());

        queue.Jobs.Should().ContainSingle(j => j.VersionId == 1);
        opened.Should().BeEquivalentTo(
            "https://civitai.com/models/102",
            "https://civitai.com/models/103");
    }

    [Fact]
    public void SkipEarlyAccess_QueuesOnlyNonEa()
    {
        var (vm, waitlist, queue, _) = Create();

        vm.ApplyEarlyAccessChoice(EarlyAccessConfirmResult.SkipEarlyAccess, MixedPairs());

        queue.Jobs.Should().ContainSingle(j => j.VersionId == 1);
        waitlist.Entries.Should().BeEmpty();
    }

    [Fact]
    public void AddAnyway_QueuesEverything()
    {
        var (vm, _, queue, _) = Create();

        vm.ApplyEarlyAccessChoice(EarlyAccessConfirmResult.AddAnyway, MixedPairs());

        queue.Jobs.Should().HaveCount(3);
    }

    [Fact]
    public void Cancel_DoesNothing()
    {
        var (vm, waitlist, queue, opened) = Create();

        vm.ApplyEarlyAccessChoice(EarlyAccessConfirmResult.Cancel, MixedPairs());

        queue.Jobs.Should().BeEmpty();
        waitlist.Entries.Should().BeEmpty();
        opened.Should().BeEmpty();
    }

    [Fact]
    public void PickItem_ExposesPermanentFlag()
    {
        var (_, pick) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(9, deadline: null, permanent: true));
        pick.IsPermanentlyPaid.Should().BeTrue();

        var (_, tempPick) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(10, Now.AddDays(7)));
        tempPick.IsPermanentlyPaid.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EarlyAccessChoiceTests"`
Expected: compile FAILURE — `AddToWaitlist`, `ApplyEarlyAccessChoice`, `IsPermanentlyPaid` (pick) do not exist.

- [ ] **Step 3: Extend the pick-item VM**

In `DiffusionNexus.UI\ViewModels\CivitaiBrowser\CivitaiVersionPickItemViewModel.cs`, in the ctor after `IsEarlyAccess = version.IsEarlyAccessActive();` add:

```csharp
        IsPermanentlyPaid = version.IsPermanentlyPaid();
```

and after `public bool IsEarlyAccess { get; }` add:

```csharp
    /// <summary>True when the version is paywalled forever — waitlisting is pointless.</summary>
    public bool IsPermanentlyPaid { get; }
```

- [ ] **Step 4: Extend the dialog code-behind**

Replace the enum and constructor block in `DiffusionNexus.UI\Views\Dialogs\EarlyAccessConfirmDialog.axaml.cs`:

```csharp
public enum EarlyAccessConfirmResult
{
    Cancel,
    SkipEarlyAccess,
    AddAnyway,
    AddToWaitlist,
    OpenWebsite
}

public partial class EarlyAccessConfirmDialog : Window
{
    public EarlyAccessConfirmResult Result { get; private set; } = EarlyAccessConfirmResult.Cancel;

    /// <summary>Temporary early-access titles — these CAN be waitlisted.</summary>
    public IReadOnlyList<string> EarlyAccessTitles { get; }

    /// <summary>Permanently paid titles — never free, excluded from the waitlist.</summary>
    public IReadOnlyList<string> PermanentTitles { get; }

    public int EarlyAccessCount => EarlyAccessTitles.Count + PermanentTitles.Count;
    public bool HasWaitlistable => EarlyAccessTitles.Count > 0;
    public bool HasPermanent => PermanentTitles.Count > 0;

    /// <summary>Design-time / XAML loader constructor.</summary>
    public EarlyAccessConfirmDialog() : this([], []) { }

    public EarlyAccessConfirmDialog(
        IReadOnlyList<string> earlyAccessTitles,
        IReadOnlyList<string>? permanentTitles = null)
    {
        EarlyAccessTitles = earlyAccessTitles;
        PermanentTitles = permanentTitles ?? [];
        DataContext = this;
        InitializeComponent();
    }
```

Keep `InitializeComponent`, `OnCancelClick`, `OnSkipClick`, `OnAddAnywayClick` unchanged and add:

```csharp
    private void OnAddToWaitlistClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = EarlyAccessConfirmResult.AddToWaitlist;
        Close();
    }

    private void OnOpenWebsiteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = EarlyAccessConfirmResult.OpenWebsite;
        Close();
    }
```

- [ ] **Step 5: Rewrite the dialog XAML**

Replace the full content of `DiffusionNexus.UI\Views\Dialogs\EarlyAccessConfirmDialog.axaml` with:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:dlg="using:DiffusionNexus.UI.Views.Dialogs"
        x:Class="DiffusionNexus.UI.Views.Dialogs.EarlyAccessConfirmDialog"
        x:DataType="dlg:EarlyAccessConfirmDialog"
        Title="Early Access models in selection"
        Width="640"
        SizeToContent="Height"
        WindowStartupLocation="CenterOwner"
        CanResize="False"
        Background="#2D2D30">

  <StackPanel Margin="20" Spacing="14">

    <StackPanel Orientation="Horizontal" Spacing="10">
      <TextBlock Text="&#x26A0;" FontSize="22" Foreground="#FBBF24" VerticalAlignment="Center"/>
      <TextBlock Text="Early Access detected" FontSize="18" FontWeight="Bold" Foreground="White" VerticalAlignment="Center"/>
    </StackPanel>

    <TextBlock Foreground="#CCCCCC" FontSize="13" TextWrapping="Wrap">
      <Run Text="{Binding EarlyAccessCount}" FontWeight="SemiBold"/><Run Text=" version(s) in your selection are "/>
      <Run Text="Early Access" Foreground="#C084FC" FontWeight="SemiBold"/><Run Text=" — the creator has paywalled them on Civitai for a limited time. Until that period ends, downloading requires buying access on the website (the app's download would fail with HTTP 401). When early access ends, the download becomes free for everyone."/>
    </TextBlock>

    <TextBlock Foreground="#CCCCCC" FontSize="13" TextWrapping="Wrap"
               IsVisible="{Binding HasWaitlistable}">
      <Run Text="Add to waitlist" FontWeight="SemiBold" Foreground="#2DD4BF"/><Run Text=" tracks these versions on the Waitlist tab next to the download queue: each shows a countdown to its release, and once free they can be moved into the queue with one click."/>
    </TextBlock>

    <Border Background="#1A1A1A" CornerRadius="6" Padding="12,10" MaxHeight="180"
            IsVisible="{Binding HasWaitlistable}">
      <ScrollViewer VerticalScrollBarVisibility="Auto">
        <ItemsControl ItemsSource="{Binding EarlyAccessTitles}">
          <ItemsControl.ItemTemplate>
            <DataTemplate x:DataType="x:String">
              <StackPanel Orientation="Horizontal" Spacing="6" Margin="0,2">
                <Border Background="#669333EA" CornerRadius="3" Padding="4,1" VerticalAlignment="Center">
                  <TextBlock Text="EA" FontSize="9" Foreground="White" FontWeight="SemiBold"/>
                </Border>
                <TextBlock Text="{Binding}" Foreground="#DDDDDD" FontSize="12" TextWrapping="Wrap"/>
              </StackPanel>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </ScrollViewer>
    </Border>

    <StackPanel Spacing="6" IsVisible="{Binding HasPermanent}">
      <TextBlock Foreground="#FCA5A5" FontSize="13" TextWrapping="Wrap"
                 Text="These versions are permanently paid — they will never become free, so the waitlist can't help. Buying on Civitai is the only way to get them:"/>
      <Border Background="#1A1A1A" CornerRadius="6" Padding="12,10" MaxHeight="140">
        <ScrollViewer VerticalScrollBarVisibility="Auto">
          <ItemsControl ItemsSource="{Binding PermanentTitles}">
            <ItemsControl.ItemTemplate>
              <DataTemplate x:DataType="x:String">
                <StackPanel Orientation="Horizontal" Spacing="6" Margin="0,2">
                  <Border Background="#66DC2626" CornerRadius="3" Padding="4,1" VerticalAlignment="Center">
                    <TextBlock Text="PAID" FontSize="9" Foreground="White" FontWeight="SemiBold"/>
                  </Border>
                  <TextBlock Text="{Binding}" Foreground="#DDDDDD" FontSize="12" TextWrapping="Wrap"/>
                </StackPanel>
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
        </ScrollViewer>
      </Border>
    </StackPanel>

    <WrapPanel HorizontalAlignment="Right" ItemSpacing="8" LineSpacing="8">
      <Button Content="Cancel"
              MinWidth="90"
              Click="OnCancelClick"/>
      <Button Content="Skip EA, add the rest"
              MinWidth="150"
              Click="OnSkipClick"/>
      <Button Content="Open on Civitai"
              MinWidth="130"
              Click="OnOpenWebsiteClick"
              ToolTip.Tip="Opens each Early Access model's page in your browser so you can buy and download manually"/>
      <Button Content="Add to waitlist"
              MinWidth="130"
              Click="OnAddToWaitlistClick"
              IsEnabled="{Binding HasWaitlistable}"
              Background="#0D9488"
              Foreground="White"
              FontWeight="SemiBold"
              ToolTip.Tip="Track these on the Waitlist tab and download them when early access ends (non-EA items download now)"/>
      <Button Content="Add anyway"
              MinWidth="110"
              Click="OnAddAnywayClick"
              Background="#9333EA"
              Foreground="White"
              FontWeight="SemiBold"
              ToolTip.Tip="Enqueue everything now — EA downloads will fail unless your Civitai account has access"/>
    </WrapPanel>

  </StackPanel>
</Window>
```

(Note: if the Avalonia version rejects `ItemSpacing`/`LineSpacing` on `WrapPanel` at build time, drop both attributes and set `Margin="0,0,8,8"` on each Button instead.)

- [ ] **Step 6: Rework the VM prompt funnel**

In `CivitaiBrowserViewModel.EnqueueWithEarlyAccessPromptAsync` (~line 872): keep everything through the no-owner fallback (lines 875-905) unchanged. Replace from `var titles = …` (line 907) to the end of the method's `switch` (line 934) with:

```csharp
        var tempTitles = eaPairs
            .Where(p => !p.Pick.IsPermanentlyPaid)
            .Select(p => $"{p.Result.Name} — {p.Pick.Name}")
            .Distinct()
            .ToList();
        var permanentTitles = eaPairs
            .Where(p => p.Pick.IsPermanentlyPaid)
            .Select(p => $"{p.Result.Name} — {p.Pick.Name}")
            .Distinct()
            .ToList();

        var dialog = new Views.Dialogs.EarlyAccessConfirmDialog(tempTitles, permanentTitles);
        await dialog.ShowDialog(owner);
        ApplyEarlyAccessChoice(dialog.Result, pairs);
```

Then add the new public method directly below `EnqueueWithEarlyAccessPromptAsync`:

```csharp
    /// <summary>
    /// Applies the user's early-access dialog choice to the pending (result, pick)
    /// pairs. Public (not shown-dialog-coupled) so every branch is unit-testable.
    /// </summary>
    public void ApplyEarlyAccessChoice(
        Views.Dialogs.EarlyAccessConfirmResult choice,
        List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> pairs)
    {
        var eaPairs = pairs.Where(p => p.Pick.IsEarlyAccess).ToList();
        var nonEa = pairs.Where(p => !p.Pick.IsEarlyAccess).ToList();

        switch (choice)
        {
            case Views.Dialogs.EarlyAccessConfirmResult.Cancel:
                _logger?.Info(LogCategory.Download, "CivitaiQueue",
                    $"Enqueue cancelled by user — {pairs.Count} version(s) NOT added ({eaPairs.Count} were EA).");
                return;

            case Views.Dialogs.EarlyAccessConfirmResult.SkipEarlyAccess:
                foreach (var (r, p) in nonEa) _queue.Enqueue(r, p);
                _logger?.Info(LogCategory.Download, "CivitaiQueue",
                    $"Skipped {eaPairs.Count} Early Access version(s); enqueued {nonEa.Count} non-EA.");
                return;

            case Views.Dialogs.EarlyAccessConfirmResult.AddAnyway:
                foreach (var (r, p) in pairs) _queue.Enqueue(r, p);
                _logger?.Info(LogCategory.Download, "CivitaiQueue",
                    $"User confirmed Early Access enqueue; {pairs.Count} version(s) added ({eaPairs.Count} are EA and will likely 401).");
                return;

            case Views.Dialogs.EarlyAccessConfirmResult.AddToWaitlist:
                foreach (var (r, p) in nonEa) _queue.Enqueue(r, p);
                var added = 0;
                var skippedPermanent = 0;
                foreach (var (r, p) in eaPairs)
                {
                    if (p.IsPermanentlyPaid) { skippedPermanent++; continue; }
                    if (_waitlist.TryAdd(r, p)) added++;
                }
                StatusMessage = skippedPermanent == 0
                    ? $"Added {added} LoRA{(added == 1 ? "" : "s")} to the waitlist."
                    : $"Added {added} LoRA{(added == 1 ? "" : "s")} to the waitlist — {skippedPermanent} permanently paid item{(skippedPermanent == 1 ? "" : "s")} skipped (never becomes free).";
                _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
                    $"Dialog choice AddToWaitlist: {added} waitlisted, {skippedPermanent} permanent skipped, {nonEa.Count} non-EA enqueued.");
                return;

            case Views.Dialogs.EarlyAccessConfirmResult.OpenWebsite:
                foreach (var (r, p) in nonEa) _queue.Enqueue(r, p);
                foreach (var result in eaPairs.Select(p => p.Result).Distinct())
                {
                    if (result.Model is null) continue;
                    var host = result.IsNsfw ? "civitai.red" : "civitai.com";
                    OpenUrl($"https://{host}/models/{result.Model.Id}");
                }
                _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
                    $"Dialog choice OpenWebsite: opened {eaPairs.Select(p => p.Result).Distinct().Count()} model page(s), {nonEa.Count} non-EA enqueued.");
                return;
        }
    }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EarlyAccessChoiceTests|FullyQualifiedName~CivitaiEarlyAccess"`
Expected: PASS — new choice tests and the pre-existing `CivitaiEarlyAccessDetectionTests`.

- [ ] **Step 8: Commit**

```bash
git add DiffusionNexus.UI/Views/Dialogs/EarlyAccessConfirmDialog.axaml DiffusionNexus.UI/Views/Dialogs/EarlyAccessConfirmDialog.axaml.cs DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiVersionPickItemViewModel.cs DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiBrowserViewModel.cs DiffusionNexus.Tests/Viewer/EarlyAccessChoiceTests.cs
git commit -m "Offer waitlist and open-website choices in the early-access dialog" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: "Paywalled" badge on browse cards

**Files:**
- Modify: `DiffusionNexus.UI\ViewModels\CivitaiBrowser\CivitaiResultViewModel.cs` (ctor ~line 51, properties ~line 135)
- Modify: `DiffusionNexus.UI\Views\CivitaiBrowser\CivitaiBrowserView.axaml` (badge stack, lines 286-291)
- Test: `DiffusionNexus.Tests\Viewer\CivitaiResultPaywalledBadgeTests.cs`

**Interfaces:**
- Consumes: `IsPermanentlyPaid` extension (Task 1), latest-version semantic (`model.ModelVersions.FirstOrDefault()`, same as `IsEarlyAccess` — see the deliberate-latest-only comment at `CivitaiResultViewModel.cs:49-50`).
- Produces: `public bool IsPermanentlyPaid { get; private init; }` and `public bool ShowEarlyAccessBadge => IsEarlyAccess && !IsPermanentlyPaid;` on `CivitaiResultViewModel`.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests\Viewer\CivitaiResultPaywalledBadgeTests.cs`:

```csharp
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the card-level paywall flags: "Paywalled" shows only for permanently
/// paid latest versions and suppresses the "Early Access" badge (never both).
/// </summary>
public sealed class CivitaiResultPaywalledBadgeTests
{
    private static readonly DateTimeOffset Future = DateTimeOffset.UtcNow.AddDays(7);

    [Fact]
    public void PermanentLatestVersion_ShowsPaywalledInsteadOfEarlyAccess()
    {
        var (result, _) = CivitaiWaitlistTests.Card(
            CivitaiWaitlistTests.Version(1, deadline: null, permanent: true));

        result.IsPermanentlyPaid.Should().BeTrue();
        result.IsEarlyAccess.Should().BeTrue("permanent paid access is an active gate");
        result.ShowEarlyAccessBadge.Should().BeFalse("Paywalled replaces the EA badge");
    }

    [Fact]
    public void TemporaryEaLatestVersion_ShowsEarlyAccessBadgeOnly()
    {
        var (result, _) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(2, Future));

        result.IsPermanentlyPaid.Should().BeFalse();
        result.ShowEarlyAccessBadge.Should().BeTrue();
    }

    [Fact]
    public void FreeLatestVersion_ShowsNeitherBadge()
    {
        var (result, _) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(3, deadline: null));

        result.IsPermanentlyPaid.Should().BeFalse();
        result.ShowEarlyAccessBadge.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiResultPaywalledBadgeTests"`
Expected: compile FAILURE — `IsPermanentlyPaid`/`ShowEarlyAccessBadge` do not exist on the result VM.

- [ ] **Step 3: Extend the result VM**

In `CivitaiResultViewModel.cs`, after `IsEarlyAccess = first.IsEarlyAccessActive();` (line 51) add:

```csharp
        IsPermanentlyPaid = first.IsPermanentlyPaid();
```

After `public bool IsEarlyAccess { get; private init; }` (line 135) add:

```csharp
    /// <summary>Latest version is paywalled forever (paidAccess.permanent) — same
    /// latest-version-only semantic as <see cref="IsEarlyAccess"/>.</summary>
    public bool IsPermanentlyPaid { get; private init; }

    /// <summary>"Early Access" badge visibility — suppressed when the stronger
    /// "Paywalled" badge applies, so the two never stack.</summary>
    public bool ShowEarlyAccessBadge => IsEarlyAccess && !IsPermanentlyPaid;
```

- [ ] **Step 4: Update the card badge stack**

In `CivitaiBrowserView.axaml`, replace the Early Access badge `Border` (lines 286-291) with:

```xml
                          <Border IsVisible="{Binding ShowEarlyAccessBadge}"
                                  Background="#AA9333EA"
                                  CornerRadius="4"
                                  Padding="6,2">
                            <TextBlock Text="Early Access" FontSize="10" Foreground="White"/>
                          </Border>
                          <Border IsVisible="{Binding IsPermanentlyPaid}"
                                  Background="#AADC2626"
                                  CornerRadius="4"
                                  Padding="6,2"
                                  ToolTip.Tip="Permanently paid — this model never becomes free; it can only be bought on Civitai">
                            <TextBlock Text="Paywalled" FontSize="10" Foreground="White"/>
                          </Border>
```

- [ ] **Step 5: Run the tests + build to verify**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiResultPaywalledBadgeTests"`
Expected: PASS.
Run: `dotnet build e:\Repos\DiffusionNexus\DiffusionNexus.UI\DiffusionNexus.UI.csproj`
Expected: build SUCCESS (validates the XAML change compiles).

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiResultViewModel.cs DiffusionNexus.UI/Views/CivitaiBrowser/CivitaiBrowserView.axaml DiffusionNexus.Tests/Viewer/CivitaiResultPaywalledBadgeTests.cs
git commit -m "Add Paywalled badge for permanently paid models" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: Waitlist tab UI in the queue side panel

**Files:**
- Modify: `DiffusionNexus.UI\Views\CivitaiBrowser\CivitaiBrowserView.axaml` (queue panel `Border`, lines 445-611)

**Interfaces:**
- Consumes: `Waitlist` property + `RemoveWaitlistEntryCommand`, `OpenWaitlistEntryOnCivitaiCommand`, `UpdateWaitlistCommand`, `MoveReadyWaitlistToQueueCommand` (Task 5); `CivitaiWaitlistEntry` bindables `ModelName`, `VersionName`, `BaseModel`, `SizeDisplay`, `CountdownDisplay`, `StatusDetail`, `StatusForeground`, `LastCheckedAt` (Task 1); `CivitaiWaitlist.Entries`, `AvailableCount`, `HasAvailable` (Task 2). The `svc:` XAML namespace already maps to `DiffusionNexus.UI.Services.CivitaiBrowser` (line 5).
- Produces: Queue|Waitlist `TabControl` inside the side panel; Waitlist tab header carries the available-count badge.

- [ ] **Step 1: Wrap the queue panel in a TabControl**

The queue panel `Border` (line 445) currently directly contains a `Grid RowDefinitions="Auto,Auto,*,Auto"` (line 451) holding all queue content through line 610. Change the structure to:

```xml
      <Border Grid.Column="1"
              Width="340"
              IsVisible="{Binding IsQueuePanelOpen}"
              Background="#161616"
              BorderBrush="#2A2A2A"
              BorderThickness="1,0,0,0">
        <TabControl Padding="0">
          <TabItem Header="Queue" FontSize="13">
            <!-- UNCHANGED: the entire existing Grid RowDefinitions="Auto,Auto,*,Auto"
                 from old lines 451-610 moves here verbatim -->
          </TabItem>
          <TabItem FontSize="13">
            <TabItem.Header>
              <StackPanel Orientation="Horizontal" Spacing="5">
                <TextBlock Text="Waitlist" VerticalAlignment="Center"/>
                <!-- Upper-right count badge: number of entries whose early access
                     has lapsed. Hidden while nothing is ready. -->
                <Border IsVisible="{Binding $parent[UserControl].((vm:CivitaiBrowserViewModel)DataContext).Waitlist.HasAvailable}"
                        Background="#22C55E"
                        CornerRadius="8"
                        MinWidth="16"
                        Padding="4,0"
                        VerticalAlignment="Top">
                  <TextBlock Text="{Binding $parent[UserControl].((vm:CivitaiBrowserViewModel)DataContext).Waitlist.AvailableCount}"
                             FontSize="10"
                             FontWeight="Bold"
                             Foreground="White"
                             HorizontalAlignment="Center"/>
                </Border>
              </StackPanel>
            </TabItem.Header>
            <!-- Waitlist tab content (Step 2) -->
          </TabItem>
        </TabControl>
      </Border>
```

Indent the moved queue Grid one level; make no other change to it.

- [ ] **Step 2: Add the Waitlist tab content**

Inside the second `TabItem` (after `</TabItem.Header>`):

```xml
            <Grid RowDefinitions="Auto,*,Auto">

              <Border Grid.Row="0" Padding="12,10" BorderBrush="#2A2A2A" BorderThickness="0,0,0,1">
                <StackPanel Spacing="8">
                  <TextBlock Text="Early Access Waitlist" FontWeight="SemiBold"/>
                  <TextBlock Text="These LoRAs are paywalled until their early-access period ends. Countdowns tick locally; Update re-checks Civitai for changed dates."
                             Opacity="0.6"
                             FontSize="11"
                             TextWrapping="Wrap"/>
                  <StackPanel Orientation="Horizontal" Spacing="6">
                    <Button Content="Update"
                            Command="{Binding UpdateWaitlistCommand}"
                            Padding="8,4"
                            ToolTip.Tip="Re-check every entry against the Civitai API (deadlines can be extended or removed by the creator)"/>
                    <Button Content="Move ready to queue"
                            Command="{Binding MoveReadyWaitlistToQueueCommand}"
                            Padding="8,4"
                            Classes="accent"
                            ToolTip.Tip="Re-verify entries whose countdown has ended and add the confirmed-free ones to the download queue"/>
                  </StackPanel>
                </StackPanel>
              </Border>

              <ScrollViewer Grid.Row="1" Padding="8">
                <ItemsControl ItemsSource="{Binding Waitlist.Entries}">
                  <ItemsControl.ItemTemplate>
                    <DataTemplate x:DataType="svc:CivitaiWaitlistEntry">
                      <Border Margin="2" Padding="8" Background="#1E1E1E" CornerRadius="4">
                        <Grid RowDefinitions="Auto,Auto,Auto,Auto" ColumnDefinitions="*,Auto">
                          <TextBlock Grid.Row="0" Grid.Column="0"
                                     Text="{Binding ModelName}"
                                     FontWeight="SemiBold"
                                     TextTrimming="CharacterEllipsis"
                                     VerticalAlignment="Center"/>
                          <StackPanel Grid.Row="0" Grid.Column="1"
                                      Orientation="Horizontal"
                                      Spacing="2"
                                      VerticalAlignment="Center">
                            <Button Content="&#x1F310;"
                                    FontSize="11"
                                    Padding="6,2"
                                    ToolTip.Tip="Open on Civitai (buy early access there if you don't want to wait)"
                                    Command="{Binding $parent[UserControl].((vm:CivitaiBrowserViewModel)DataContext).OpenWaitlistEntryOnCivitaiCommand}"
                                    CommandParameter="{Binding}"/>
                            <Button Content="&#x2715;"
                                    FontSize="10"
                                    Padding="6,2"
                                    ToolTip.Tip="Remove from waitlist"
                                    Command="{Binding $parent[UserControl].((vm:CivitaiBrowserViewModel)DataContext).RemoveWaitlistEntryCommand}"
                                    CommandParameter="{Binding}"/>
                          </StackPanel>
                          <StackPanel Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="2" Orientation="Horizontal" Spacing="6">
                            <TextBlock Text="{Binding VersionName}" Opacity="0.6" FontSize="11"/>
                            <TextBlock Text="·" Opacity="0.4" FontSize="11"/>
                            <TextBlock Text="{Binding BaseModel}" Opacity="0.6" FontSize="11"/>
                            <TextBlock Text="·" Opacity="0.4" FontSize="11"/>
                            <TextBlock Text="{Binding SizeDisplay}" Opacity="0.6" FontSize="11"/>
                          </StackPanel>
                          <TextBlock Grid.Row="2" Grid.Column="0" Grid.ColumnSpan="2"
                                     Text="{Binding CountdownDisplay}"
                                     Foreground="{Binding StatusForeground}"
                                     FontSize="11"
                                     FontWeight="SemiBold"
                                     Margin="0,4,0,0"/>
                          <TextBlock Grid.Row="3" Grid.Column="0" Grid.ColumnSpan="2"
                                     Text="{Binding StatusDetail}"
                                     IsVisible="{Binding StatusDetail, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"
                                     Opacity="0.6"
                                     FontSize="10"
                                     TextWrapping="Wrap"
                                     Margin="0,2,0,0"/>
                        </Grid>
                      </Border>
                    </DataTemplate>
                  </ItemsControl.ItemTemplate>
                </ItemsControl>
              </ScrollViewer>

              <Border Grid.Row="2" Padding="12,8" BorderBrush="#2A2A2A" BorderThickness="0,1,0,0">
                <TextBlock Opacity="0.7" FontSize="11">
                  <Run Text="Entries: "/><Run Text="{Binding Waitlist.Entries.Count}"/>
                  <Run Text=" · Available: "/><Run Text="{Binding Waitlist.AvailableCount}"/>
                </TextBlock>
              </Border>

            </Grid>
```

- [ ] **Step 3: Build to verify the XAML compiles**

Run: `dotnet build e:\Repos\DiffusionNexus\DiffusionNexus.UI\DiffusionNexus.UI.csproj`
Expected: build SUCCESS. (Avalonia compiled bindings will fail the build on any typo in `x:DataType`/paths — this is the test for this task.)

- [ ] **Step 4: Run the full browser test filter**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~Civitai"`
Expected: PASS (no regressions).

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Views/CivitaiBrowser/CivitaiBrowserView.axaml
git commit -m "Add Waitlist tab with countdown rows and available-count badge" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 9: Full verification + release build

**Files:**
- No new files. Fix anything the full run surfaces.

- [ ] **Step 1: Run the complete test suite**

Run: `dotnet test e:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj`
Expected: PASS, zero failures. Fix any regression before proceeding (report what broke and how it was fixed).

- [ ] **Step 2: Release solution build (project rule before push)**

Run: `dotnet build e:\Repos\DiffusionNexus\DiffusionNexus.sln -c Release`
Expected: build SUCCESS, zero errors.

- [ ] **Step 3: Commit any fixes**

```bash
git status --short
```

If fixes were needed, commit them:

```bash
git add -A
git commit -m "Fix full-suite regressions from waitlist feature" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

- [ ] **Step 4: Report the manual GUI smoke checklist (do NOT perform automatically)**

These need a human with the running app; list them in the final summary:

1. Browse Civitai → select a mix of free + EA LoRAs → "Add to queue" → dialog shows both lists, "Add to waitlist" waitlists the temporary-EA ones and downloads the free ones.
2. Waitlist tab shows the entries with countdowns; badge appears on the tab when a deadline passes.
3. "Update" re-checks; "Move ready to queue" moves an actually-free entry into the queue and downloads it.
4. A permanently paid model card shows the red "Paywalled" badge (no purple EA badge).
5. Waitlist survives app restart (entries restored from `%LocalAppData%\DiffusionNexus\civitai-waitlist.json`).
6. "Open on Civitai" from a waitlist row opens the right model page.
