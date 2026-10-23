# Civitai API Gateway Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the LoRA Viewer / Civitai Browser from tripping HTTP 429 by putting every Civitai API call behind one throttled, cached, 429-aware gateway, and removing the redundant calls the gateway cannot remove by itself.

**Architecture:** A decorator (`CivitaiApiGateway`) wraps the existing `CivitaiClient` and is registered *as* `ICivitaiClient`, so all seventeen production call sites become paced and cached without being edited. It composes three small collaborators: the existing `CivitaiRequestPacer` (moved into the `DiffusionNexus.Civitai` project and given a per-call interval so interactive and background callers get different spacing off one shared timestamp), a new `CivitaiRateLimitCooldown` (a process-wide 429 memory), and a new `CivitaiResponseCache` (bounded TTL + single-flight). Two DI registrations differ only by lane: the default one is interactive, a keyed `"background"` one is used by library sync and the update checker.

**Tech Stack:** C# / .NET 10, Avalonia UI, xUnit + FluentAssertions + Moq, Microsoft.Extensions.DependencyInjection (keyed services).

**Spec:** `docs/superpowers/specs/2026-08-28-civitai-api-gateway-design.md`

## Global Constraints

- Repo: `e:\Repos\DiffusionNexus`. Branch: `feature/civitai-api-gateway` (already created; stay on it — do not cut new branches).
- All projects target `net10.0`. `DiffusionNexus.Civitai` has **zero** project references and **zero** NuGet packages — keep it that way. Nothing added there may reference `DiffusionNexus.Service`, `.Domain`, `.UI`, or `Microsoft.Extensions.*`.
- Tests live in `DiffusionNexus.Tests`, xUnit (`[Fact]`/`[Theory]`), assertions with FluentAssertions (`.Should()`), mocks with Moq.
- Build/test from the repo root with `dotnet build DiffusionNexus.sln` / `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj`.
- Never introduce a real `Task.Delay` into a unit test — the pacer and cooldown both take clock/delay seams; use them.
- Existing behaviour that must not change: `catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)` handlers must keep catching; a 404 must keep surfacing as `null`, not an exception.
- Timing constants (single source, `CivitaiApiGateway`): interactive interval **750 ms**, background interval **1500 ms**, default cooldown **30 s**, max interval multiplier **4×**, multiplier decay **5 min**.
- Cache TTLs: model **15 min**, version **15 min**, by-hash **60 min**, search **2 min**. Store cap **1000** entries, oldest-inserted evicted first.
- Commit after every task. Conventional commit messages, ending with the `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` trailer.

## File Structure

**Created**

| File | Responsibility |
|---|---|
| `DiffusionNexus.Civitai/CivitaiRequestPacer.cs` | Moved from Service. Minimum spacing between requests; now takes a per-call interval. |
| `DiffusionNexus.Civitai/CivitaiRateLimitCooldown.cs` | Process-wide memory of the last 429: cooldown deadline + interval multiplier. |
| `DiffusionNexus.Civitai/CivitaiResponseCache.cs` | Bounded TTL store with single-flight; implements `ICivitaiApiCache`. |
| `DiffusionNexus.Civitai/CivitaiApiGateway.cs` | The `ICivitaiClient` decorator: lane → interval, cooldown wait, cache lookup. |
| `DiffusionNexus.Civitai/CivitaiRateLimitedException.cs` | `HttpRequestException` subclass carrying `RetryAfter`; plus `ICivitaiRateLimitObserver`. |
| `DiffusionNexus.Tests/Civitai/CivitaiApiGatewayTests.cs` | Gateway behaviour: pacing, cooldown, cache, invalidation. |
| `DiffusionNexus.Tests/Civitai/CivitaiResponseCacheTests.cs` | Cache unit tests: TTL, single-flight, eviction, exceptions. |

**Modified**

| File | Change |
|---|---|
| `DiffusionNexus.Civitai/CivitaiClient.cs` | Retry-After date form; rate-limit observer; 429 retry budget 3 → 1; throw `CivitaiRateLimitedException`. |
| `DiffusionNexus.Service/Services/Sync/CivitaiRequestPacer.cs` | Deleted (moved). |
| `DiffusionNexus.Service/Services/Sync/CivitaiMetadataApplier.cs` | Remove three `_pacer.WaitAsync` calls, the `pacer` ctor param and the field; add `ApplyImagesFromModelAsync`. |
| `DiffusionNexus.Service/Services/Sync/Steps/IdentifyModelStep.cs` | Remove the `_pacer.WaitAsync` call and ctor param; read the sidecar before the by-hash call. |
| `DiffusionNexus.Service/Services/Sync/Steps/FetchImagesStep.cs` | One model call per model instead of one version call per version. |
| `DiffusionNexus.Service/Services/Sync/SyncServiceCollectionExtensions.cs` | Drop the pacer registration; resolve the keyed background `ICivitaiClient`. |
| `DiffusionNexus.UI/App.axaml.cs` | Register the gateway (default + keyed background) and its shared collaborators. |
| `DiffusionNexus.UI/Services/LoraUpdateChecker.cs` | Delete the private 60 s cooldown; take the keyed background client. |
| `DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiBrowserViewModel.cs` | No search from the constructor; debounce filter/sort changes. |
| `DiffusionNexus.UI/Views/CivitaiBrowser/CivitaiBrowserView.axaml.cs` | Trigger the first search when the tab is first shown. |
| `DiffusionNexus.UI/Services/CivitaiBrowser/CivitaiDownloadQueue.cs` | Pass the API key on the version rehydrate call. |
| `DiffusionNexus.Service/Services/Sync/LibrarySyncService.cs` | Invalidate the cache when a `Force*` option is set. |

---

### Task 1: Move the pacer into `DiffusionNexus.Civitai` and add a per-call interval

The gateway lives in `DiffusionNexus.Civitai`, which cannot reference `DiffusionNexus.Service`. The pacer must move down. While moving it, give `WaitAsync` an interval parameter so one pacer can serve both lanes off one timestamp.

**Files:**
- Create: `DiffusionNexus.Civitai/CivitaiRequestPacer.cs`
- Delete: `DiffusionNexus.Service/Services/Sync/CivitaiRequestPacer.cs`
- Modify: `DiffusionNexus.Service/Services/Sync/CivitaiMetadataApplier.cs`, `DiffusionNexus.Service/Services/Sync/Steps/IdentifyModelStep.cs`, `DiffusionNexus.Service/Services/Sync/SyncServiceCollectionExtensions.cs` (add `using DiffusionNexus.Civitai;`)
- Test: `DiffusionNexus.Tests/Sync/Service/CivitaiRequestPacerTests.cs` (move to `DiffusionNexus.Tests/Civitai/CivitaiRequestPacerTests.cs`)

**Interfaces:**
- Consumes: nothing.
- Produces: `DiffusionNexus.Civitai.ICivitaiRequestPacer` with `Task WaitAsync(CancellationToken ct = default)` and `Task WaitAsync(TimeSpan minInterval, CancellationToken ct = default)`; `DiffusionNexus.Civitai.CivitaiRequestPacer` (ctor `(TimeSpan? minInterval = null, Func<long>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)`); `DiffusionNexus.Civitai.NoCivitaiRequestPacer.Instance`.

- [ ] **Step 1: Move the file**

```bash
cd e:/Repos/DiffusionNexus
git mv DiffusionNexus.Service/Services/Sync/CivitaiRequestPacer.cs DiffusionNexus.Civitai/CivitaiRequestPacer.cs
git mv DiffusionNexus.Tests/Sync/Service/CivitaiRequestPacerTests.cs DiffusionNexus.Tests/Civitai/CivitaiRequestPacerTests.cs
```

- [ ] **Step 2: Change the namespace and add the interval overload**

In `DiffusionNexus.Civitai/CivitaiRequestPacer.cs`, replace the namespace line:

```csharp
namespace DiffusionNexus.Civitai;
```

Add to the `ICivitaiRequestPacer` interface, below the existing `WaitAsync`:

```csharp
    /// <summary>
    /// As <see cref="WaitAsync(CancellationToken)"/>, but with the caller's own minimum spacing.
    /// The timestamp is shared: a background caller asking for 1.5 s and an interactive caller
    /// asking for 750 ms both measure from whichever request went out last, so background work
    /// spaces itself behind interactive work rather than alongside it.
    /// </summary>
    Task WaitAsync(TimeSpan minInterval, CancellationToken ct = default);
```

In `CivitaiRequestPacer`, replace the body of `WaitAsync` with a delegating pair:

```csharp
    /// <inheritdoc />
    public Task WaitAsync(CancellationToken ct = default) => WaitAsync(_minInterval, ct);

    /// <inheritdoc />
    public async Task WaitAsync(TimeSpan minInterval, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lastCall is { } last)
            {
                var remaining = minInterval - TimeSpan.FromMilliseconds(_clock() - last);
                if (remaining > TimeSpan.Zero) await _delay(remaining, ct).ConfigureAwait(false);
            }

            // Stamped after the wait, not before it: this is the moment the request is released,
            // and the next caller measures from here.
            _lastCall = _clock();
        }
        finally
        {
            _gate.Release();
        }
    }
```

In `NoCivitaiRequestPacer`, add:

```csharp
    /// <inheritdoc />
    public Task WaitAsync(TimeSpan minInterval, CancellationToken ct = default) => Task.CompletedTask;
```

- [ ] **Step 3: Fix the namespace in the moved test and add the new test**

In `DiffusionNexus.Tests/Civitai/CivitaiRequestPacerTests.cs`, change the namespace to `DiffusionNexus.Tests.Civitai` and make sure `using DiffusionNexus.Civitai;` is present (remove `using DiffusionNexus.Service.Services.Sync;` if it is there). Append:

```csharp
    [Fact]
    public async Task WaitAsync_ExplicitInterval_MeasuresAgainstTheSharedTimestamp()
    {
        var now = 0L;
        var waits = new List<TimeSpan>();
        var pacer = new CivitaiRequestPacer(
            minInterval: TimeSpan.FromMilliseconds(1500),
            clock: () => now,
            delay: (d, _) => { waits.Add(d); now += (long)d.TotalMilliseconds; return Task.CompletedTask; });

        // A background call goes out at t=0, then an interactive caller asks for 750 ms spacing.
        await pacer.WaitAsync(TimeSpan.FromMilliseconds(1500));
        now += 100;
        await pacer.WaitAsync(TimeSpan.FromMilliseconds(750));

        waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(650));
    }
```

- [ ] **Step 4: Add the using to the three Service consumers**

Add `using DiffusionNexus.Civitai;` to the top of each of:
- `DiffusionNexus.Service/Services/Sync/CivitaiMetadataApplier.cs` (it already has `using DiffusionNexus.Civitai;` — verify, add nothing if present)
- `DiffusionNexus.Service/Services/Sync/Steps/IdentifyModelStep.cs`
- `DiffusionNexus.Service/Services/Sync/SyncServiceCollectionExtensions.cs`

Do the same for any test file the build flags.

- [ ] **Step 5: Build and run the pacer tests**

```bash
dotnet build DiffusionNexus.sln
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiRequestPacer"
```
Expected: build succeeds, all pacer tests pass including the new one.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(civitai): move the request pacer into the Civitai project

The gateway that will own pacing lives in DiffusionNexus.Civitai, which
cannot reference Service. WaitAsync gains a per-call interval so one pacer
serves both lanes off a single shared timestamp.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Teach `CivitaiClient` to report and describe rate limits

The 429 response is only visible inside the client. The gateway needs to hear about it immediately, and needs the `Retry-After` value — including the HTTP-date form the client ignores today.

**Files:**
- Create: `DiffusionNexus.Civitai/CivitaiRateLimitedException.cs`
- Modify: `DiffusionNexus.Civitai/CivitaiClient.cs`
- Test: `DiffusionNexus.Tests/Civitai/CivitaiClientTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ICivitaiRateLimitObserver` with `void OnRateLimited(TimeSpan? retryAfter)`; `CivitaiRateLimitedException : HttpRequestException` with `TimeSpan? RetryAfter { get; }`; `CivitaiClient` ctor overload accepting `ICivitaiRateLimitObserver? rateLimitObserver`.

- [ ] **Step 1: Write the failing tests**

Append to `DiffusionNexus.Tests/Civitai/CivitaiClientTests.cs`:

```csharp
    private sealed class RecordingRateLimitObserver : ICivitaiRateLimitObserver
    {
        public List<TimeSpan?> Observed { get; } = [];
        public void OnRateLimited(TimeSpan? retryAfter) => Observed.Add(retryAfter);
    }

    [Fact]
    public async Task GetAsync_NotifiesObserver_OnEveryRateLimit_EvenWhenTheRetrySucceeds()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7)) }
            },
            Json(HttpStatusCode.OK, new { id = 1, name = "ok" })
        });

        var observer = new RecordingRateLimitObserver();
        var handler = new FakeHttpHandler(_ => responses.Dequeue());
        using var client = new CivitaiClient(new HttpClient(handler), disposeHttpClient: true, rateLimitObserver: observer)
        {
            RetryDelayOverride = _ => TimeSpan.Zero
        };

        var model = await client.GetModelAsync(1);

        model.Should().NotBeNull();
        observer.Observed.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task GetAsync_ParsesRetryAfterAsHttpDate()
    {
        var observer = new RecordingRateLimitObserver();
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(45)) }
        });
        using var client = new CivitaiClient(new HttpClient(handler), disposeHttpClient: true, rateLimitObserver: observer)
        {
            RetryDelayOverride = _ => TimeSpan.Zero
        };

        var act = async () => await client.GetModelAsync(1);
        var ex = await act.Should().ThrowAsync<CivitaiRateLimitedException>();

        ex.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        ex.Which.RetryAfter.Should().NotBeNull();
        ex.Which.RetryAfter!.Value.Should().BeCloseTo(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(5));
        observer.Observed.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RateLimitedException_IsStillCaughtAsHttpRequestException()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var client = new CivitaiClient(new HttpClient(handler), disposeHttpClient: true)
        {
            RetryDelayOverride = _ => TimeSpan.Zero
        };

        var caught = false;
        try
        {
            await client.GetModelAsync(1);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            caught = true;
        }

        caught.Should().BeTrue();
    }
```

Change the existing `GetAsync_ThrowsAfterMaxRateLimitRetries` expectation from 4 requests to 2, and update its trailing comment:

```csharp
        // Initial attempt + 1 retry = 2 total. The gateway's shared cooldown does the waiting now;
        // three in-call retries used to sleep 10+20+40s while holding a download slot.
        handler.Requests.Should().HaveCount(2);
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiClientTests"
```
Expected: FAIL — `ICivitaiRateLimitObserver` and `CivitaiRateLimitedException` do not exist; the retry-count test fails at 4 vs 2.

- [ ] **Step 3: Create the exception and observer**

`DiffusionNexus.Civitai/CivitaiRateLimitedException.cs`:

```csharp
using System.Net;

namespace DiffusionNexus.Civitai;

/// <summary>
/// Told, as soon as it happens, that Civitai answered 429 — including a 429 the client's own
/// retry then recovers from.
/// </summary>
/// <remarks>
/// The response is only visible inside <see cref="CivitaiClient"/>, and by the time an exception
/// reaches a caller the limit may already have been in force for a minute. Every other surface
/// wants to know at the moment of the first refusal, not at the end of one caller's retries.
/// </remarks>
public interface ICivitaiRateLimitObserver
{
    /// <param name="retryAfter">The parsed Retry-After, or null when the response carried none.</param>
    void OnRateLimited(TimeSpan? retryAfter);
}

/// <summary>
/// A 429 the client gave up on. Derives from <see cref="HttpRequestException"/> with
/// <see cref="HttpRequestException.StatusCode"/> set to 429, so the existing
/// <c>catch (HttpRequestException ex) when (ex.StatusCode == TooManyRequests)</c> handlers
/// across the app keep working; it merely adds the wait the server asked for.
/// </summary>
public sealed class CivitaiRateLimitedException : HttpRequestException
{
    public CivitaiRateLimitedException(string message, TimeSpan? retryAfter)
        : base(message, null, HttpStatusCode.TooManyRequests)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>How long Civitai asked us to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; }
}
```

- [ ] **Step 4: Wire the client**

In `DiffusionNexus.Civitai/CivitaiClient.cs`:

Add the field and constructor parameter (replacing the existing two constructors' bodies as shown):

```csharp
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly ICivitaiRateLimitObserver? _rateLimitObserver;

    /// <summary>
    /// Creates a new CivitaiClient with a default HttpClient.
    /// </summary>
    public CivitaiClient() : this(new HttpClient(), disposeHttpClient: true)
    {
    }

    /// <summary>
    /// Creates a new CivitaiClient with a provided HttpClient.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use.</param>
    /// <param name="disposeHttpClient">Whether to dispose the HttpClient when this client is disposed.</param>
    /// <param name="rateLimitObserver">Told about every 429, including recovered ones.</param>
    public CivitaiClient(
        HttpClient httpClient,
        bool disposeHttpClient = false,
        ICivitaiRateLimitObserver? rateLimitObserver = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _disposeHttpClient = disposeHttpClient;
        _rateLimitObserver = rateLimitObserver;

        TryConfigureBaseAddress(_httpClient);
        TryAddJsonAcceptHeader(_httpClient);
    }
```

Add the Retry-After parser next to `RateLimitDelay`:

```csharp
    /// <summary>
    /// Retry-After in either legal form. The delta form ("120") arrives pre-parsed as
    /// <c>Delta</c>; the HTTP-date form ("Wed, 21 Oct 2026 07:28:00 GMT") only ever populates
    /// <c>Date</c>, and reading Delta alone — which is what this client used to do — silently
    /// discarded it and fell back to a blind backoff.
    /// </summary>
    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }
```

Add the separate 429 budget alongside `maxRetries` in `GetAsync`:

```csharp
        const int maxRetries = 3;

        // One immediate retry, not three. A 429 is a quota, and the gateway's shared cooldown is
        // what waits it out for everybody; three in-call retries meant a single call could sleep
        // 10 + 20 + 40 s while holding a download slot.
        const int maxRateLimitRetries = 1;
```

Replace the 429 branch:

```csharp
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var serverRetryAfter = ParseRetryAfter(response);

                    // Reported before the decision to retry: every other surface should stop
                    // now, not after this caller has finished being optimistic.
                    _rateLimitObserver?.OnRateLimited(serverRetryAfter);

                    if (attempt >= maxRateLimitRetries)
                    {
                        throw new CivitaiRateLimitedException(
                            $"Civitai API rate limited after {maxRateLimitRetries} retries for {endpoint}",
                            serverRetryAfter);
                    }

                    await Task.Delay(RateLimitDelay(attempt, serverRetryAfter), cancellationToken);
                    continue;
                }
```

Replace the unreachable tail throw at the end of the method:

```csharp
        // Only reachable when the transient-retry budget is exhausted without a decisive answer.
        throw new HttpRequestException(
            $"Civitai API gave no usable response after {maxRetries} retries for {endpoint}",
            null,
            System.Net.HttpStatusCode.ServiceUnavailable);
```

- [ ] **Step 5: Run the tests**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiClientTests"
```
Expected: PASS, all of them.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(civitai): report rate limits as they happen, and parse Retry-After dates

CivitaiClient now notifies an optional observer on every 429 (recovered ones
included), throws CivitaiRateLimitedException carrying the server's wait, and
understands the HTTP-date form of Retry-After it used to discard. The in-call
429 retry budget drops from 3 to 1 — a shared cooldown will do that waiting.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: `CivitaiRateLimitCooldown`

**Files:**
- Create: `DiffusionNexus.Civitai/CivitaiRateLimitCooldown.cs`
- Test: `DiffusionNexus.Tests/Civitai/CivitaiRateLimitCooldownTests.cs`

**Interfaces:**
- Consumes: `ICivitaiRateLimitObserver` (Task 2).
- Produces: `CivitaiRateLimitCooldown : ICivitaiRateLimitObserver` with ctor `(Func<long>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)`, `Task WaitAsync(CancellationToken ct)`, `int IntervalMultiplier { get; }`, `void OnRateLimited(TimeSpan? retryAfter)`.

- [ ] **Step 1: Write the failing tests**

`DiffusionNexus.Tests/Civitai/CivitaiRateLimitCooldownTests.cs`:

```csharp
using DiffusionNexus.Civitai;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

public class CivitaiRateLimitCooldownTests
{
    private long _now;
    private readonly List<TimeSpan> _waits = [];

    private CivitaiRateLimitCooldown Create() => new(
        clock: () => _now,
        delay: (d, _) => { _waits.Add(d); _now += (long)d.TotalMilliseconds; return Task.CompletedTask; });

    [Fact]
    public async Task WaitAsync_DoesNotWait_WhenNoRateLimitSeen()
    {
        var cooldown = Create();

        await cooldown.WaitAsync(CancellationToken.None);

        _waits.Should().BeEmpty();
        cooldown.IntervalMultiplier.Should().Be(1);
    }

    [Fact]
    public async Task WaitAsync_HonoursTheServersRetryAfter()
    {
        var cooldown = Create();
        cooldown.OnRateLimited(TimeSpan.FromSeconds(12));

        await cooldown.WaitAsync(CancellationToken.None);

        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task WaitAsync_FallsBackToThirtySeconds_WhenNoRetryAfterGiven()
    {
        var cooldown = Create();
        cooldown.OnRateLimited(null);

        await cooldown.WaitAsync(CancellationToken.None);

        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task WaitAsync_WaitsOnlyTheRemainder_ForALaterCaller()
    {
        var cooldown = Create();
        cooldown.OnRateLimited(TimeSpan.FromSeconds(10));
        _now += 4000;

        await cooldown.WaitAsync(CancellationToken.None);

        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void IntervalMultiplier_DoublesPerRateLimit_CappedAtFour()
    {
        var cooldown = Create();

        cooldown.OnRateLimited(null);
        cooldown.IntervalMultiplier.Should().Be(2);

        cooldown.OnRateLimited(null);
        cooldown.IntervalMultiplier.Should().Be(4);

        cooldown.OnRateLimited(null);
        cooldown.IntervalMultiplier.Should().Be(4);
    }

    [Fact]
    public void IntervalMultiplier_DecaysToOne_AfterFiveQuietMinutes()
    {
        var cooldown = Create();
        cooldown.OnRateLimited(null);

        _now += (long)TimeSpan.FromMinutes(5).TotalMilliseconds + 1;

        cooldown.IntervalMultiplier.Should().Be(1);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiRateLimitCooldown"
```
Expected: FAIL — the type does not exist.

- [ ] **Step 3: Implement**

`DiffusionNexus.Civitai/CivitaiRateLimitCooldown.cs`:

```csharp
namespace DiffusionNexus.Civitai;

/// <summary>
/// The process's memory of the last 429: a deadline everybody waits for, and a multiplier that
/// widens the request spacing while Civitai is unhappy with us.
/// </summary>
/// <remarks>
/// A singleton on purpose. The bug this exists to fix is that each surface used to discover the
/// rate limit on its own and keep digging in the meantime; a second instance would restore it.
/// The clock is <see cref="Environment.TickCount64"/> — monotonic, so an NTP correction cannot
/// turn a 30 s cooldown into an hour.
/// </remarks>
public sealed class CivitaiRateLimitCooldown : ICivitaiRateLimitObserver
{
    /// <summary>How long to stand down when the server named no wait of its own.</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(30);

    /// <summary>Widest the spacing may get: 4× turns 1.5 s into 6 s, not into a stall.</summary>
    public const int MaxIntervalMultiplier = 4;

    /// <summary>Quiet time after which the widened spacing goes back to normal.</summary>
    public static readonly TimeSpan MultiplierDecay = TimeSpan.FromMinutes(5);

    private readonly Func<long> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _lock = new();

    private long _cooldownUntil;
    private long _lastRateLimit;
    private int _multiplier = 1;
    private bool _everRateLimited;

    public CivitaiRateLimitCooldown(
        Func<long>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// The factor the caller should multiply its request spacing by. Decays back to 1 once
    /// <see cref="MultiplierDecay"/> has passed without a 429.
    /// </summary>
    public int IntervalMultiplier
    {
        get
        {
            lock (_lock)
            {
                if (!_everRateLimited) return 1;
                var quietFor = TimeSpan.FromMilliseconds(_clock() - _lastRateLimit);
                return quietFor > MultiplierDecay ? 1 : _multiplier;
            }
        }
    }

    /// <inheritdoc />
    public void OnRateLimited(TimeSpan? retryAfter)
    {
        lock (_lock)
        {
            var now = _clock();
            var wait = retryAfter ?? DefaultCooldown;

            // Extend, never shorten: a second 429 while a longer cooldown is running must not
            // release everyone early.
            var deadline = now + (long)wait.TotalMilliseconds;
            if (!_everRateLimited || deadline > _cooldownUntil) _cooldownUntil = deadline;

            // Read through the property's decay rule so a limit after a long quiet spell starts
            // from 1 again rather than resuming yesterday's penalty.
            var current = !_everRateLimited || TimeSpan.FromMilliseconds(now - _lastRateLimit) > MultiplierDecay
                ? 1
                : _multiplier;
            _multiplier = Math.Min(current * 2, MaxIntervalMultiplier);

            _lastRateLimit = now;
            _everRateLimited = true;
        }
    }

    /// <summary>
    /// Returns once any active cooldown has elapsed. Honours <paramref name="ct"/> so a cancelled
    /// download or a closed dialog does not sit out the full wait.
    /// </summary>
    public async Task WaitAsync(CancellationToken ct = default)
    {
        while (true)
        {
            TimeSpan remaining;
            lock (_lock)
            {
                if (!_everRateLimited) return;
                remaining = TimeSpan.FromMilliseconds(_cooldownUntil - _clock());
            }

            if (remaining <= TimeSpan.Zero) return;
            await _delay(remaining, ct).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiRateLimitCooldown"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(civitai): shared 429 cooldown with adaptive spacing

One 429 now pauses every surface rather than only the caller that drew it,
honouring the server's Retry-After and widening request spacing up to 4x
until five quiet minutes pass.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: `CivitaiResponseCache`

**Files:**
- Create: `DiffusionNexus.Civitai/CivitaiResponseCache.cs`
- Test: `DiffusionNexus.Tests/Civitai/CivitaiResponseCacheTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ICivitaiApiCache` with `void InvalidateModel(int modelId)`, `void InvalidateVersion(int modelVersionId)`, `void Clear()`; `CivitaiResponseCache : ICivitaiApiCache` with ctor `(int capacity = 1000, Func<long>? clock = null)` and `Task<T?> GetOrAddAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> factory) where T : class`; static key helpers `CivitaiResponseCache.ModelKey(int)`, `.VersionKey(int)`, `.HashKey(string)`, `.SearchKey(string)`.

- [ ] **Step 1: Write the failing tests**

`DiffusionNexus.Tests/Civitai/CivitaiResponseCacheTests.cs`:

```csharp
using DiffusionNexus.Civitai;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

public class CivitaiResponseCacheTests
{
    private long _now;
    private CivitaiResponseCache Create(int capacity = 1000) => new(capacity, () => _now);

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task GetOrAddAsync_SecondCallForTheSameKey_DoesNotCallTheFactory()
    {
        var cache = Create();
        var calls = 0;

        var first = await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl,
            () => { calls++; return Task.FromResult<string?>("value"); });
        var second = await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl,
            () => { calls++; return Task.FromResult<string?>("value"); });

        calls.Should().Be(1);
        first.Should().Be("value");
        second.Should().Be("value");
    }

    [Fact]
    public async Task GetOrAddAsync_RefetchesAfterTheTtlExpires()
    {
        var cache = Create();
        var calls = 0;
        Task<string?> Factory() { calls++; return Task.FromResult<string?>("value"); }

        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);
        _now += (long)Ttl.TotalMilliseconds + 1;
        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);

        calls.Should().Be(2);
    }

    [Fact]
    public async Task GetOrAddAsync_CachesNull_SoA404IsNotReAsked()
    {
        var cache = Create();
        var calls = 0;
        Task<string?> Factory() { calls++; return Task.FromResult<string?>(null); }

        (await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory)).Should().BeNull();
        (await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory)).Should().BeNull();

        calls.Should().Be(1);
    }

    [Fact]
    public async Task GetOrAddAsync_DoesNotCacheExceptions()
    {
        var cache = Create();
        var calls = 0;

        Task<string?> Throwing() { calls++; throw new InvalidOperationException("boom"); }

        var act = async () => await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Throwing);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var second = await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl,
            () => Task.FromResult<string?>("recovered"));

        calls.Should().Be(1);
        second.Should().Be("recovered");
    }

    [Fact]
    public async Task GetOrAddAsync_ConcurrentCallersForOneKey_ShareASingleFactoryCall()
    {
        var cache = Create();
        var calls = 0;
        var release = new TaskCompletionSource<string?>();

        Task<string?> Factory() { Interlocked.Increment(ref calls); return release.Task; }

        var a = cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);
        var b = cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);
        release.SetResult("value");

        (await a).Should().Be("value");
        (await b).Should().Be("value");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task InvalidateModel_ForcesTheNextCallToRefetch()
    {
        var cache = Create();
        var calls = 0;
        Task<string?> Factory() { calls++; return Task.FromResult<string?>("value"); }

        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);
        cache.InvalidateModel(7);
        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);

        calls.Should().Be(2);
    }

    [Fact]
    public async Task Capacity_EvictsTheOldestEntryFirst()
    {
        var cache = Create(capacity: 2);
        var calls = 0;
        Task<string?> Factory() { calls++; return Task.FromResult<string?>("value"); }

        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(1), Ttl, Factory);
        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(2), Ttl, Factory);
        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(3), Ttl, Factory); // evicts key 1
        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(1), Ttl, Factory); // refetch

        calls.Should().Be(4);
    }

    [Fact]
    public void HashKey_IsCaseInsensitive()
    {
        CivitaiResponseCache.HashKey("abcDEF").Should().Be(CivitaiResponseCache.HashKey("ABCdef"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiResponseCache"
```
Expected: FAIL — the type does not exist.

- [ ] **Step 3: Implement**

`DiffusionNexus.Civitai/CivitaiResponseCache.cs`:

```csharp
using System.Collections.Concurrent;

namespace DiffusionNexus.Civitai;

/// <summary>
/// Lets a caller drop what the gateway remembers about a model or version, for the paths where
/// the user has explicitly asked for fresh data.
/// </summary>
public interface ICivitaiApiCache
{
    /// <summary>Forgets the model page for <paramref name="modelId"/> (a Civitai model id).</summary>
    void InvalidateModel(int modelId);

    /// <summary>Forgets the version record for <paramref name="modelVersionId"/> (a Civitai version id).</summary>
    void InvalidateVersion(int modelVersionId);

    /// <summary>Forgets everything.</summary>
    void Clear();
}

/// <summary>
/// A small bounded store of Civitai responses, with single-flight so N concurrent callers asking
/// for the same model page make one request rather than N.
/// </summary>
/// <remarks>
/// In-memory and process-lifetime on purpose. A disk cache would have to answer questions about
/// staleness that <c>ModelSyncState</c> already answers for the long term; what is missing is
/// only the short window in which several surfaces ask for the same page within seconds — a
/// download's persist and its completion sync, an update check and the detail panel the user
/// then opens.
/// </remarks>
public sealed class CivitaiResponseCache : ICivitaiApiCache
{
    private sealed record Entry(object? Value, long ExpiresAt, long InsertedAt);

    private readonly int _capacity;
    private readonly Func<long> _clock;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<object?>> _inFlight = new(StringComparer.Ordinal);

    /// <param name="capacity">Maximum entries before the oldest-inserted are evicted.</param>
    /// <param name="clock">Monotonic millisecond clock. Test seam.</param>
    public CivitaiResponseCache(int capacity = 1000, Func<long>? clock = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public static string ModelKey(int modelId) => $"model:{modelId}";
    public static string VersionKey(int modelVersionId) => $"version:{modelVersionId}";
    public static string HashKey(string hash) => $"hash:{hash.ToUpperInvariant()}";
    public static string SearchKey(string queryString) => $"search:{queryString}";

    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, or awaits <paramref name="factory"/>
    /// once and caches its result. A <c>null</c> result IS cached — a 404 is an answer. An
    /// exception is not: a transient failure must not become a fifteen-minute one.
    /// </summary>
    public async Task<T?> GetOrAddAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> factory)
        where T : class
    {
        if (TryGet(key, out var cached)) return (T?)cached;

        // GetOrAdd's factory can run more than once under contention, so the work is deferred into
        // a Lazy-like Task the losers simply await. Whoever's task ends up in the dictionary is the
        // one that runs; everybody gets its result.
        var task = _inFlight.GetOrAdd(key, _ => RunAsync(key, ttl, factory));
        try
        {
            return (T?)await task.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Task<object?>>(key, task));
        }
    }

    private async Task<object?> RunAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> factory)
        where T : class
    {
        var value = await factory().ConfigureAwait(false);

        var now = _clock();
        _entries[key] = new Entry(value, now + (long)ttl.TotalMilliseconds, now);
        Trim();
        return value;
    }

    private bool TryGet(string key, out object? value)
    {
        value = null;
        if (!_entries.TryGetValue(key, out var entry)) return false;
        if (_clock() >= entry.ExpiresAt)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        value = entry.Value;
        return true;
    }

    /// <summary>
    /// Oldest-inserted eviction rather than least-recently-used: entries expire on their own
    /// within minutes, so recency buys nothing an LRU's extra bookkeeping would pay for.
    /// </summary>
    private void Trim()
    {
        if (_entries.Count <= _capacity) return;

        foreach (var key in _entries
                     .OrderBy(kv => kv.Value.InsertedAt)
                     .Take(_entries.Count - _capacity)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            _entries.TryRemove(key, out _);
        }
    }

    /// <inheritdoc />
    public void InvalidateModel(int modelId) => _entries.TryRemove(ModelKey(modelId), out _);

    /// <inheritdoc />
    public void InvalidateVersion(int modelVersionId) => _entries.TryRemove(VersionKey(modelVersionId), out _);

    /// <inheritdoc />
    public void Clear() => _entries.Clear();
}
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiResponseCache"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(civitai): bounded TTL response cache with single-flight

Concurrent callers asking for the same model page now make one request. 404s
are cached (they are answers); exceptions are not.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: `CivitaiApiGateway`

**Files:**
- Create: `DiffusionNexus.Civitai/CivitaiApiGateway.cs`
- Test: `DiffusionNexus.Tests/Civitai/CivitaiApiGatewayTests.cs`

**Interfaces:**
- Consumes: `ICivitaiRequestPacer` (Task 1), `CivitaiRateLimitCooldown` (Task 3), `CivitaiResponseCache` / `ICivitaiApiCache` (Task 4).
- Produces: `enum CivitaiCallLane { Interactive, Background }`; `CivitaiApiGateway : ICivitaiClient, ICivitaiApiCache` with ctor `(ICivitaiClient inner, ICivitaiRequestPacer pacer, CivitaiRateLimitCooldown cooldown, CivitaiResponseCache cache, CivitaiCallLane lane = CivitaiCallLane.Interactive)`.

- [ ] **Step 1: Write the failing tests**

`DiffusionNexus.Tests/Civitai/CivitaiApiGatewayTests.cs`:

```csharp
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

public class CivitaiApiGatewayTests
{
    private sealed class CountingClient : ICivitaiClient
    {
        public int ModelCalls;
        public int VersionCalls;
        public int HashCalls;
        public int SearchCalls;
        public List<string?> ApiKeysSeen { get; } = [];
        public Func<int, CivitaiModel?>? ModelResponder;

        public Task<CivitaiPagedResponse<CivitaiModel>> GetModelsAsync(
            CivitaiModelsQuery? query = null, string? apiKey = null, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            ApiKeysSeen.Add(apiKey);
            return Task.FromResult(new CivitaiPagedResponse<CivitaiModel>());
        }

        public Task<CivitaiModel?> GetModelAsync(
            int modelId, string? apiKey = null, CancellationToken cancellationToken = default)
        {
            ModelCalls++;
            ApiKeysSeen.Add(apiKey);
            var model = ModelResponder is not null
                ? ModelResponder(modelId)
                : new CivitaiModel { Id = modelId, Name = $"model-{modelId}" };
            return Task.FromResult(model);
        }

        public Task<CivitaiModelVersion?> GetModelVersionAsync(
            int modelVersionId, string? apiKey = null, CancellationToken cancellationToken = default)
        {
            VersionCalls++;
            return Task.FromResult<CivitaiModelVersion?>(new CivitaiModelVersion { Id = modelVersionId });
        }

        public Task<CivitaiModelVersion?> GetModelVersionByHashAsync(
            string hash, string? apiKey = null, CancellationToken cancellationToken = default)
        {
            HashCalls++;
            return Task.FromResult<CivitaiModelVersion?>(new CivitaiModelVersion { Id = 1 });
        }

        public Task<CivitaiPagedResponse<CivitaiModelImage>> GetImagesAsync(
            CivitaiImagesQuery? query = null, string? apiKey = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CivitaiPagedResponse<CivitaiModelImage>());

        public Task<CivitaiPagedResponse<CivitaiTag>> GetTagsAsync(
            int? limit = null, int? page = null, string? query = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CivitaiPagedResponse<CivitaiTag>());

        public Task<CivitaiPagedResponse<CivitaiCreatorInfo>> GetCreatorsAsync(
            int? limit = null, int? page = null, string? query = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CivitaiPagedResponse<CivitaiCreatorInfo>());
    }

    private long _now;
    private readonly List<TimeSpan> _waits = [];

    private (CivitaiApiGateway interactive, CivitaiApiGateway background, CountingClient inner,
             CivitaiRateLimitCooldown cooldown) CreateBoth()
    {
        var inner = new CountingClient();
        Task Delay(TimeSpan d, CancellationToken _) { _waits.Add(d); _now += (long)d.TotalMilliseconds; return Task.CompletedTask; }
        var pacer = new CivitaiRequestPacer(clock: () => _now, delay: Delay);
        var cooldown = new CivitaiRateLimitCooldown(clock: () => _now, delay: Delay);
        var cache = new CivitaiResponseCache(clock: () => _now);

        return (new CivitaiApiGateway(inner, pacer, cooldown, cache, CivitaiCallLane.Interactive),
                new CivitaiApiGateway(inner, pacer, cooldown, cache, CivitaiCallLane.Background),
                inner, cooldown);
    }

    [Fact]
    public async Task GetModelAsync_SecondCallForTheSameId_IsServedFromCache()
    {
        var (interactive, _, inner, _) = CreateBoth();

        await interactive.GetModelAsync(42);
        await interactive.GetModelAsync(42);

        inner.ModelCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetModelAsync_CacheIsSharedBetweenLanes()
    {
        var (interactive, background, inner, _) = CreateBoth();

        await interactive.GetModelAsync(42);
        await background.GetModelAsync(42);

        inner.ModelCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetModelAsync_A404IsCached()
    {
        var (interactive, _, inner, _) = CreateBoth();
        inner.ModelResponder = _ => null;

        (await interactive.GetModelAsync(42)).Should().BeNull();
        (await interactive.GetModelAsync(42)).Should().BeNull();

        inner.ModelCalls.Should().Be(1);
    }

    [Fact]
    public async Task InteractiveLane_SpacesRequestsBy750ms()
    {
        var (interactive, _, _, _) = CreateBoth();

        await interactive.GetModelAsync(1);
        await interactive.GetModelAsync(2);

        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(750));
    }

    [Fact]
    public async Task BackgroundLane_SpacesRequestsBy1500ms()
    {
        var (_, background, _, _) = CreateBoth();

        await background.GetModelAsync(1);
        await background.GetModelAsync(2);

        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task BackgroundLane_YieldsToAnInteractiveCall_ViaTheSharedTimestamp()
    {
        var (interactive, background, _, _) = CreateBoth();

        await background.GetModelAsync(1);
        await interactive.GetModelAsync(2);

        // The interactive caller waits only its own 750 ms, not the background 1500 ms.
        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(750));
    }

    [Fact]
    public async Task ARateLimitSeenByOneLane_DelaysTheNextCallOnTheOther()
    {
        var (interactive, background, _, cooldown) = CreateBoth();
        await interactive.GetModelAsync(1);
        _waits.Clear();

        cooldown.OnRateLimited(TimeSpan.FromSeconds(20));
        await background.GetModelAsync(2);

        _waits.Should().Contain(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task RateLimitMultiplier_WidensTheLaneInterval()
    {
        var (interactive, _, _, cooldown) = CreateBoth();
        cooldown.OnRateLimited(TimeSpan.Zero);   // multiplier 2, no cooldown wait
        await interactive.GetModelAsync(1);
        _waits.Clear();

        await interactive.GetModelAsync(2);

        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task InvalidateModel_ForcesARefetch()
    {
        var (interactive, _, inner, _) = CreateBoth();

        await interactive.GetModelAsync(42);
        interactive.InvalidateModel(42);
        await interactive.GetModelAsync(42);

        inner.ModelCalls.Should().Be(2);
    }

    [Fact]
    public async Task ChangingTheApiKey_ClearsTheCache()
    {
        var (interactive, _, inner, _) = CreateBoth();

        await interactive.GetModelAsync(42, apiKey: null);
        await interactive.GetModelAsync(42, apiKey: "key-a");

        inner.ModelCalls.Should().Be(2);
        inner.ApiKeysSeen.Should().Equal(null, "key-a");
    }

    [Fact]
    public async Task SameApiKeyTwice_StillHitsTheCache()
    {
        var (interactive, _, inner, _) = CreateBoth();

        await interactive.GetModelAsync(42, apiKey: "key-a");
        await interactive.GetModelAsync(42, apiKey: "key-a");

        inner.ModelCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetModelsAsync_CachesBySerializedQuery()
    {
        var (interactive, _, inner, _) = CreateBoth();
        var query = new CivitaiModelsQuery { Limit = 50, Query = "anime" };

        await interactive.GetModelsAsync(query);
        await interactive.GetModelsAsync(new CivitaiModelsQuery { Limit = 50, Query = "anime" });
        await interactive.GetModelsAsync(new CivitaiModelsQuery { Limit = 50, Query = "realistic" });

        inner.SearchCalls.Should().Be(2);
    }

    [Fact]
    public async Task GetModelVersionByHashAsync_IsCachedCaseInsensitively()
    {
        var (interactive, _, inner, _) = CreateBoth();

        await interactive.GetModelVersionByHashAsync("abcDEF");
        await interactive.GetModelVersionByHashAsync("ABCdef");

        inner.HashCalls.Should().Be(1);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiApiGateway"
```
Expected: FAIL — `CivitaiApiGateway` and `CivitaiCallLane` do not exist.

- [ ] **Step 3: Implement**

`DiffusionNexus.Civitai/CivitaiApiGateway.cs`:

```csharp
namespace DiffusionNexus.Civitai;

/// <summary>
/// Which spacing a caller gets. Chosen by which registration you resolve, not by an ambient
/// context — the lane is a property of the surface making the call, and surfaces are wired once.
/// </summary>
public enum CivitaiCallLane
{
    /// <summary>A user is waiting: the browser, a detail panel, a dialog, a download.</summary>
    Interactive,

    /// <summary>Nobody is waiting: library sync, the visible-tile update sweep.</summary>
    Background
}

/// <summary>
/// The one door to the Civitai API: paces every request, waits out a rate limit anybody drew,
/// and serves repeats from a short-lived cache.
/// </summary>
/// <remarks>
/// <para>
/// Registered <i>as</i> <see cref="ICivitaiClient"/>, so the seventeen call sites across the app
/// get all of this without knowing it exists. That is the point: pacing used to live at four
/// hand-picked call sites inside the sync pipeline, which meant every surface added since —
/// the browser, the update checker, the detail panel, the waitlist, the sorter, the download
/// path — hammered Civitai unpaced and discovered the 429 on its own.
/// </para>
/// <para>
/// Two instances share one pacer, one cooldown and one cache; they differ only in lane. A
/// background sync therefore cannot outrun a user, and a user never waits behind a sync's
/// longer interval.
/// </para>
/// </remarks>
public sealed class CivitaiApiGateway : ICivitaiClient, ICivitaiApiCache
{
    /// <summary>Spacing for a call a user is waiting on.</summary>
    public static readonly TimeSpan InteractiveInterval = TimeSpan.FromMilliseconds(750);

    /// <summary>Spacing for background work.</summary>
    public static readonly TimeSpan BackgroundInterval = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan ModelTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan VersionTtl = TimeSpan.FromMinutes(15);

    /// <summary>A hash maps to a version forever; only the record it points at can change.</summary>
    private static readonly TimeSpan HashTtl = TimeSpan.FromMinutes(60);

    /// <summary>Long enough to absorb a filter toggled off and on, short enough to feel live.</summary>
    private static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(2);

    private readonly ICivitaiClient _inner;
    private readonly ICivitaiRequestPacer _pacer;
    private readonly CivitaiRateLimitCooldown _cooldown;
    private readonly CivitaiResponseCache _cache;
    private readonly CivitaiCallLane _lane;

    private readonly object _apiKeyLock = new();
    private string? _lastApiKey;
    private bool _apiKeySeen;

    public CivitaiApiGateway(
        ICivitaiClient inner,
        ICivitaiRequestPacer pacer,
        CivitaiRateLimitCooldown cooldown,
        CivitaiResponseCache cache,
        CivitaiCallLane lane = CivitaiCallLane.Interactive)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pacer = pacer ?? throw new ArgumentNullException(nameof(pacer));
        _cooldown = cooldown ?? throw new ArgumentNullException(nameof(cooldown));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _lane = lane;
    }

    private TimeSpan Interval =>
        (_lane == CivitaiCallLane.Background ? BackgroundInterval : InteractiveInterval)
        * _cooldown.IntervalMultiplier;

    /// <summary>
    /// Cache keys deliberately omit the API key — an authenticated and an anonymous request for
    /// the same public model return the same page, and keying by secret would halve the hit rate
    /// for nothing. What must not happen is an anonymous answer being served to a caller that has
    /// since supplied a key (gated models answer differently), so a change of key empties the store.
    /// </summary>
    private void NoteApiKey(string? apiKey)
    {
        lock (_apiKeyLock)
        {
            if (_apiKeySeen && string.Equals(_lastApiKey, apiKey, StringComparison.Ordinal)) return;
            if (_apiKeySeen) _cache.Clear();
            _lastApiKey = apiKey;
            _apiKeySeen = true;
        }
    }

    /// <summary>Cooldown first, then spacing: no point pacing into a wall.</summary>
    private async Task<T?> SendAsync<T>(string cacheKey, TimeSpan ttl, string? apiKey,
        Func<CancellationToken, Task<T?>> call, CancellationToken ct)
        where T : class
    {
        NoteApiKey(apiKey);

        return await _cache.GetOrAddAsync(cacheKey, ttl, async () =>
        {
            await _cooldown.WaitAsync(ct).ConfigureAwait(false);
            await _pacer.WaitAsync(Interval, ct).ConfigureAwait(false);
            return await call(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<CivitaiModel?> GetModelAsync(int modelId, string? apiKey = null, CancellationToken cancellationToken = default)
        => SendAsync(CivitaiResponseCache.ModelKey(modelId), ModelTtl, apiKey,
            ct => _inner.GetModelAsync(modelId, apiKey, ct), cancellationToken);

    /// <inheritdoc />
    public Task<CivitaiModelVersion?> GetModelVersionAsync(int modelVersionId, string? apiKey = null, CancellationToken cancellationToken = default)
        => SendAsync(CivitaiResponseCache.VersionKey(modelVersionId), VersionTtl, apiKey,
            ct => _inner.GetModelVersionAsync(modelVersionId, apiKey, ct), cancellationToken);

    /// <inheritdoc />
    public Task<CivitaiModelVersion?> GetModelVersionByHashAsync(string hash, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        return SendAsync(CivitaiResponseCache.HashKey(hash), HashTtl, apiKey,
            ct => _inner.GetModelVersionByHashAsync(hash, apiKey, ct), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CivitaiPagedResponse<CivitaiModel>> GetModelsAsync(
        CivitaiModelsQuery? query = null, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        var key = CivitaiResponseCache.SearchKey(query?.ToQueryString() ?? string.Empty);
        var page = await SendAsync(key, SearchTtl, apiKey,
            ct => _inner.GetModelsAsync(query, apiKey, ct)!, cancellationToken).ConfigureAwait(false);
        return page ?? new CivitaiPagedResponse<CivitaiModel>();
    }

    // Not cached: unused in production, and an images/tags/creators listing is a browse of a
    // moving target rather than a record we would want to hold on to.

    /// <inheritdoc />
    public async Task<CivitaiPagedResponse<CivitaiModelImage>> GetImagesAsync(
        CivitaiImagesQuery? query = null, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        NoteApiKey(apiKey);
        await _cooldown.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _pacer.WaitAsync(Interval, cancellationToken).ConfigureAwait(false);
        return await _inner.GetImagesAsync(query, apiKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CivitaiPagedResponse<CivitaiTag>> GetTagsAsync(
        int? limit = null, int? page = null, string? query = null, CancellationToken cancellationToken = default)
    {
        await _cooldown.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _pacer.WaitAsync(Interval, cancellationToken).ConfigureAwait(false);
        return await _inner.GetTagsAsync(limit, page, query, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CivitaiPagedResponse<CivitaiCreatorInfo>> GetCreatorsAsync(
        int? limit = null, int? page = null, string? query = null, CancellationToken cancellationToken = default)
    {
        await _cooldown.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _pacer.WaitAsync(Interval, cancellationToken).ConfigureAwait(false);
        return await _inner.GetCreatorsAsync(limit, page, query, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void InvalidateModel(int modelId) => _cache.InvalidateModel(modelId);

    /// <inheritdoc />
    public void InvalidateVersion(int modelVersionId) => _cache.InvalidateVersion(modelVersionId);

    /// <inheritdoc />
    public void Clear() => _cache.Clear();
}
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiApiGateway"
```
Expected: PASS. If `RateLimitMultiplier_WidensTheLaneInterval` fails because `OnRateLimited(TimeSpan.Zero)` still produces a cooldown wait of zero, that is fine — assert on the pacing wait only, which is what the test does.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(civitai): one gateway for every Civitai API call

CivitaiApiGateway decorates ICivitaiClient with two-lane pacing, the shared
429 cooldown and the TTL cache. Interactive and background callers share one
timestamp, so background work spaces itself behind the user rather than
alongside them.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: Wire the gateway into DI and delete the now-duplicated pacing

The gateway is inert until it is what `ICivitaiClient` resolves to. This task also removes the four hand-placed `_pacer.WaitAsync` calls (which would otherwise double-pace the sync pipeline to 3 s) and `LoraUpdateChecker`'s private 60 s cooldown.

**Files:**
- Modify: `DiffusionNexus.UI/App.axaml.cs:858-859`
- Modify: `DiffusionNexus.Service/Services/Sync/SyncServiceCollectionExtensions.cs:26-31`
- Modify: `DiffusionNexus.Service/Services/Sync/CivitaiMetadataApplier.cs` (ctor + lines 70, 267, 307)
- Modify: `DiffusionNexus.Service/Services/Sync/Steps/IdentifyModelStep.cs` (ctor + line 179)
- Modify: `DiffusionNexus.UI/Services/LoraUpdateChecker.cs`
- Test: `DiffusionNexus.Tests/Sync/Service/Steps/IdentifyModelStepTests.cs`, `FetchTagsStepTests.cs`, `FetchImagesStepTests.cs`, `LibrarySyncServiceTests.cs` (drop pacer arguments)

**Interfaces:**
- Consumes: `CivitaiApiGateway`, `CivitaiCallLane`, `CivitaiRateLimitCooldown`, `CivitaiResponseCache`, `ICivitaiApiCache`, `CivitaiRequestPacer`.
- Produces: `ICivitaiClient` (default, interactive lane); keyed `ICivitaiClient` under the string key `"background"`; `ICivitaiApiCache` (singleton, the shared cache); `CivitaiMetadataApplier` ctor is now `(ICivitaiClient client, IUnifiedLogger? logger = null)`; `IdentifyModelStep` ctor loses its `ICivitaiRequestPacer` parameter.

- [ ] **Step 1: Register the gateway**

In `DiffusionNexus.UI/App.axaml.cs`, replace lines 858-859:

```csharp
        // The one door to the Civitai API. Everything below is a singleton because the pacing
        // timestamp, the 429 cooldown and the cache are the process's single opinion about
        // Civitai — a second copy of any of them would pace, cool down and cache nothing.
        services.AddSingleton<Civitai.CivitaiRateLimitCooldown>();
        services.AddSingleton<Civitai.CivitaiResponseCache>();
        services.AddSingleton<Civitai.ICivitaiApiCache>(sp => sp.GetRequiredService<Civitai.CivitaiResponseCache>());
        services.AddSingleton<Civitai.ICivitaiRequestPacer>(_ => new Civitai.CivitaiRequestPacer());

        // The raw client, told to report every 429 to the shared cooldown the moment it sees one.
        services.AddSingleton(sp => new Civitai.CivitaiClient(
            new HttpClient(),
            disposeHttpClient: true,
            rateLimitObserver: sp.GetRequiredService<Civitai.CivitaiRateLimitCooldown>()));

        // Default lane: a user is waiting. Resolved by the browser, the detail panel, the
        // dialogs, the download path, the waitlist, the sorter and the pipeline installer.
        services.AddSingleton<Civitai.ICivitaiClient>(sp => new Civitai.CivitaiApiGateway(
            sp.GetRequiredService<Civitai.CivitaiClient>(),
            sp.GetRequiredService<Civitai.ICivitaiRequestPacer>(),
            sp.GetRequiredService<Civitai.CivitaiRateLimitCooldown>(),
            sp.GetRequiredService<Civitai.CivitaiResponseCache>(),
            Civitai.CivitaiCallLane.Interactive));

        // Background lane: nobody is waiting. Library sync and the visible-tile update sweep.
        services.AddKeyedSingleton<Civitai.ICivitaiClient>("background", (sp, _) => new Civitai.CivitaiApiGateway(
            sp.GetRequiredService<Civitai.CivitaiClient>(),
            sp.GetRequiredService<Civitai.ICivitaiRequestPacer>(),
            sp.GetRequiredService<Civitai.CivitaiRateLimitCooldown>(),
            sp.GetRequiredService<Civitai.CivitaiResponseCache>(),
            Civitai.CivitaiCallLane.Background));
```

- [ ] **Step 2: Point sync and the update checker at the background lane**

In `DiffusionNexus.Service/Services/Sync/SyncServiceCollectionExtensions.cs`, delete the pacer registration (lines 27-31) and its comment, and register the two consumers that need the background client explicitly. Replace the `AddTransient<CivitaiMetadataApplier>()` line with:

```csharp
        // Nobody is waiting on a sync, so it takes the background lane: 1.5 s spacing, yielding
        // to any interactive call. The pacing itself now lives in the gateway — this pipeline used
        // to be the only thing in the app that paced at all.
        services.AddTransient(sp => new CivitaiMetadataApplier(
            sp.GetRequiredKeyedService<ICivitaiClient>("background"),
            sp.GetService<IUnifiedLogger>()));
```

`IdentifyModelStep` is the only step that takes an `ICivitaiClient` directly (`FetchTagsStep`, `FetchImagesStep` and `ThumbnailsStep` go through the applier or the CDN client, so they need no change). Replace its registration:

```csharp
        // The only step holding a client of its own — the by-hash lookup. Background lane, and
        // without the pacer parameter the gateway has made redundant.
        services.AddTransient<ISyncStep>(sp => new IdentifyModelStep(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredKeyedService<ICivitaiClient>("background"),
            sp.GetRequiredService<CivitaiMetadataApplier>(),
            sp.GetRequiredService<SidecarMetadataApplier>(),
            sp.GetService<IUnifiedLogger>()));
```

Add to the usings: `using DiffusionNexus.Civitai;`.

In `DiffusionNexus.UI/App.axaml.cs`, replace line 948:

```csharp
        services.AddSingleton<ILoraUpdateChecker>(sp => new LoraUpdateChecker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredKeyedService<Civitai.ICivitaiClient>("background"),
            sp.GetRequiredService<IAppSettingsService>(),
            sp.GetService<Domain.Services.UnifiedLogging.IUnifiedLogger>()));
```

- [ ] **Step 3: Remove the four hand-placed pacer calls**

In `DiffusionNexus.Service/Services/Sync/CivitaiMetadataApplier.cs`:
- Delete the `_pacer` field, the `pacer` constructor parameter and its `<param>` doc block; the constructor becomes `public CivitaiMetadataApplier(ICivitaiClient client, IUnifiedLogger? logger = null)`.
- Delete the three `await _pacer.WaitAsync(ct);` lines (at 70, 267 and 307).
- Replace the class-level `<remarks>` sentence about pacing with:

```csharp
/// Pacing is not this class's business any more: every request it makes goes through the
/// Civitai gateway, which paces, cools down after a 429 and caches. It used to wait here
/// because this was the only code in the app that waited at all.
```

In `DiffusionNexus.Service/Services/Sync/Steps/IdentifyModelStep.cs`, delete the `_pacer` field, the constructor parameter and the `await _pacer.WaitAsync(ct).ConfigureAwait(false);` at line 179.

In `DiffusionNexus.UI/Services/LoraUpdateChecker.cs`, delete: the `RateLimitBackoff` field, `_rateLimitedUntilUtc`, `_rateLimitLock`, `IsRateLimited()`, `TriggerBackoff()`, the two `if (IsRateLimited())` guards (lines 62-67 and 130-135), and change the 429 catch block to:

```csharp
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // The gateway's shared cooldown already paused every surface; nothing to do here but
            // say so. This class used to keep a private 60 s backoff that only it obeyed.
            _logger?.Warn(LogCategory.Network, "LoraUpdateChecker",
                $"Civitai rate-limited update check for '{tileName}' (trigger={source}, civitaiId={civitaiModelId})");
        }
```

- [ ] **Step 4: Fix the tests that pass a pacer**

Build, then remove the pacer arguments the compiler flags in `DiffusionNexus.Tests/Sync/Service/Steps/IdentifyModelStepTests.cs`, `FetchTagsStepTests.cs`, `FetchImagesStepTests.cs` and `LibrarySyncServiceTests.cs`. Where a test asserted that the pacer was awaited, delete that assertion — the behaviour moved to `CivitaiApiGatewayTests`.

```bash
dotnet build DiffusionNexus.sln
```
Expected: succeeds after the argument removals.

- [ ] **Step 5: Run the whole suite**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj
```
Expected: PASS. Investigate any failure before continuing — a green suite here is what says the decorator is transparent to seventeen call sites.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(civitai): route every call through the gateway

ICivitaiClient now resolves to the interactive gateway; library sync and the
update sweep take the keyed background lane. The four hand-placed pacer waits
and LoraUpdateChecker's private 60s backoff are deleted — the gateway does
both, for everybody.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: Invalidate the cache when the user asks for fresh data

A cached model page is wrong exactly when the user has pressed something that means "go and look again".

**Files:**
- Modify: `DiffusionNexus.Service/Services/Sync/LibrarySyncService.cs`
- Test: `DiffusionNexus.Tests/Sync/Service/LibrarySyncServiceTests.cs`

**Interfaces:**
- Consumes: `ICivitaiApiCache` (Task 4); `ILibrarySyncService.ExecuteAsync(SyncPlan plan, IProgress<LibrarySyncProgress>?, CancellationToken)` (existing); `SyncPlan.Options` of type `SyncOptions` with `ForceIdentify` / `ForceTags` / `ForceImages` / `ForceThumbnails` (existing, `DiffusionNexus.Domain/Services/Sync/SyncOptions.cs`).
- Produces: `LibrarySyncService` constructor gains a trailing optional `ICivitaiApiCache? apiCache = null` parameter.

Note: the LoRA Viewer's per-tile "Download Metadata" already passes `ForceIdentify: true, ForceThumbnails: true` (`LoraViewerViewModel.cs:2057-2066`), so it is covered by this hook with no change of its own.

- [ ] **Step 1: Write the failing test**

Append to `DiffusionNexus.Tests/Sync/Service/LibrarySyncServiceTests.cs` (mirroring the construction the neighbouring tests use, plus the new argument):

```csharp
    [Fact]
    public async Task ExecuteAsync_WithAForceOption_ClearsTheCivitaiCacheFirst()
    {
        var cache = new Mock<ICivitaiApiCache>();
        var service = CreateService(apiCache: cache.Object);
        var options = new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.FetchTags }, ForceTags: true);
        var plan = await service.PlanAsync(SyncScope.Library, options, CancellationToken.None);

        await service.ExecuteAsync(plan, null, CancellationToken.None);

        cache.Verify(c => c.Clear(), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutAForceOption_LeavesTheCacheAlone()
    {
        var cache = new Mock<ICivitaiApiCache>();
        var service = CreateService(apiCache: cache.Object);
        var options = new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.FetchTags });
        var plan = await service.PlanAsync(SyncScope.Library, options, CancellationToken.None);

        await service.ExecuteAsync(plan, null, CancellationToken.None);

        cache.Verify(c => c.Clear(), Times.Never);
    }
```

Add an optional `ICivitaiApiCache? apiCache = null` parameter to the file's existing `CreateService` helper and pass it through to the `LibrarySyncService` constructor.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~LibrarySyncServiceTests"
```
Expected: FAIL — the constructor has no `apiCache` parameter.

- [ ] **Step 3: Implement**

In `DiffusionNexus.Service/Services/Sync/LibrarySyncService.cs`, add the field and constructor parameter (last, optional, so no existing caller breaks):

```csharp
    private readonly ICivitaiApiCache? _apiCache;
```

and in the constructor body:

```csharp
        _apiCache = apiCache;
```

At the top of `ExecuteAsync`, before any step runs:

```csharp
        // "Force" means the user has told us the local answer is wrong. A cached response is a
        // local answer, so it goes too — otherwise a forced re-sync would replay the very page
        // that produced the state they are trying to correct. This also covers the LoRA Viewer's
        // per-tile "Download Metadata", which forces identify and thumbnails.
        var options = plan.Options;
        if (options.ForceIdentify || options.ForceTags || options.ForceImages || options.ForceThumbnails)
        {
            _apiCache?.Clear();
        }
```

If `ExecuteAsync` already declares a local named `options` from `plan.Options`, reuse it rather than shadowing.

In `DiffusionNexus.Service/Services/Sync/SyncServiceCollectionExtensions.cs`, add the new argument to the `LibrarySyncService` factory:

```csharp
        services.AddSingleton<ILibrarySyncService>(sp => new LibrarySyncService(
            sp.GetRequiredService<IEnumerable<ISyncStep>>(),
            sp.GetRequiredService<SyncStateInitializer>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<IUnifiedLogger>(),
            sp.GetService<ICivitaiApiCache>()));
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~LibrarySyncServiceTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(sync): a forced re-sync drops the Civitai response cache

Force means the user is telling us the local answer is wrong; a cached
response is a local answer.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: Read the sidecar before asking Civitai (F1)

`IdentifyModelStep` hashes the file, calls `model-versions/by-hash`, and only reads the `.civitai.info` sidecar when that 404s. Every file that shipped with a sidecar therefore costs two API calls to learn what was already sitting next to it on disk. `SorterMetadataResolver` already reads the sidecar first; this makes sync agree.

**Files:**
- Modify: `DiffusionNexus.Service/Services/Sync/Steps/IdentifyModelStep.cs:175-199`
- Test: `DiffusionNexus.Tests/Sync/Service/Steps/IdentifyModelStepTests.cs`

**Interfaces:**
- Consumes: `SidecarMetadataApplier.ApplyAsync(IUnitOfWork uow, int modelId, string localPath, CancellationToken ct)` returning a result with `Applied` and `Signature` (existing).
- Produces: no new public surface.

- [ ] **Step 1: Write the failing test**

Append to `DiffusionNexus.Tests/Sync/Service/Steps/IdentifyModelStepTests.cs`, following the arrangement the neighbouring tests use for a candidate with a sidecar on disk:

```csharp
    [Fact]
    public async Task ExecuteOneAsync_WithASidecarPresent_NeverCallsCivitai()
    {
        // A .civitai.info next to the file already answers the question the hash lookup would ask.
        var candidate = CreateCandidateWithSidecar();

        var result = await Step.ExecuteOneAsync(new SyncItem(candidate.ModelId, candidate.Name, candidate),
            apiKey: null, CancellationToken.None);

        result.Should().Be(SyncItemResult.Success);
        Client.Verify(c => c.GetModelVersionByHashAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteOneAsync_WithNoSidecar_StillAsksCivitaiByHash()
    {
        var candidate = CreateCandidateWithoutSidecar();

        await Step.ExecuteOneAsync(new SyncItem(candidate.ModelId, candidate.Name, candidate),
            apiKey: null, CancellationToken.None);

        Client.Verify(c => c.GetModelVersionByHashAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
```

Add `CreateCandidateWithSidecar` / `CreateCandidateWithoutSidecar` helpers to the fixture. Both create a temp `.safetensors` using whatever temp-file plumbing the existing tests in this file already use (reuse its cleanup — do not add a second temp-directory scheme). The first additionally writes a sidecar next to it, in the shape `SidecarMetadataApplier.Find` expects:

```csharp
    private static void WriteSidecar(string modelFilePath)
    {
        var sidecar = Path.ChangeExtension(modelFilePath, ".civitai.info");
        File.WriteAllText(sidecar, """
        {
          "id": 4242,
          "modelId": 900,
          "name": "v1.0",
          "baseModel": "SDXL 1.0",
          "trainedWords": ["trigger"],
          "files": [{ "name": "model.safetensors", "hashes": { "SHA256": "ABC123" } }]
        }
        """);
    }
```

Confirm the property names against `SidecarMetadataApplier` before running — if its reader expects a different shape, use that shape; the point of the test is only that a readable sidecar suppresses the API call.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~IdentifyModelStepTests"
```
Expected: FAIL — the sidecar test still sees a by-hash call.

- [ ] **Step 3: Implement**

In `IdentifyModelStep.ExecuteOneAsync`, replace the block from `var hash = await ResolveHashAsync(...)` down to the end of the 404 sidecar fallback with:

```csharp
            // Sidecar first. It is the answer Civitai would give us, already on disk, and reading
            // it costs no request. This used to run only after the hash lookup 404'd, which meant
            // every sidecar-bearing file paid two API calls to be told what was sitting next to it.
            // SorterMetadataResolver has always done it in this order.
            var sidecar = await _sidecar.ApplyAsync(uow, candidate.ModelId, candidate.LocalPath, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (sidecar.Applied)
            {
                await StampAsync(uow, candidate.ModelId, SyncOutcome.Sidecar, now, sidecar.Signature, error: null, headerCheckedAt: null, ct).ConfigureAwait(false);
                return SyncItemResult.Success;
            }

            var hash = await ResolveHashAsync(uow, candidate, ct).ConfigureAwait(false);

            var version = await _client.GetModelVersionByHashAsync(hash, apiKey, ct).ConfigureAwait(false);
            if (version is not null)
            {
                var applied = await _civitai.ApplyAsync(uow, candidate.ModelId, candidate.FileId, version, apiKey, ct).ConfigureAwait(false);
                return await RecordMatchAsync(uow, candidate, version, applied, now, ct).ConfigureAwait(false);
            }

            // Not on Civitai and no sidecar — read the file's own header, then guess from its name.
```

Leave the header/heuristic tail below that comment exactly as it is.

- [ ] **Step 4: Run the tests**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~IdentifyModelStep"
```
Expected: PASS, including the pre-existing tests for the header and heuristic rungs.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "perf(sync): read the .civitai.info sidecar before asking Civitai

A sidecar-bearing file cost two API calls to learn what was already on disk
beside it. The sorter has always read the sidecar first; sync now agrees.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 9: One model call per model in `FetchImagesStep` (F2)

The step already groups its candidates per model, then makes one `model-versions/{id}` call per version inside the group. `models/{id}` returns every version with its images, so a six-version model costs six calls where one would do. The gateway's cache cannot collapse these — they are six distinct version ids.

**Files:**
- Modify: `DiffusionNexus.Service/Services/Sync/CivitaiMetadataApplier.cs` (add `ApplyImagesFromModelAsync`)
- Modify: `DiffusionNexus.Service/Services/Sync/Steps/FetchImagesStep.cs:92-120`
- Test: `DiffusionNexus.Tests/Sync/Service/Steps/FetchImagesStepTests.cs`

**Interfaces:**
- Consumes: `CivitaiMetadataApplier.ApplyImagesAsync(IUnitOfWork, int modelId, int versionId, int civitaiVersionId, string? apiKey, CancellationToken)` returning `int?` (existing — kept as the per-version fallback).
- Produces: `CivitaiMetadataApplier.ApplyImagesFromModelAsync(IUnitOfWork uow, int civitaiModelId, IReadOnlyList<(int ModelId, int VersionId, int CivitaiVersionId)> versions, string? apiKey, CancellationToken ct)` returning `IReadOnlyDictionary<int, int?>` keyed by `CivitaiVersionId` — the count added per version, or `null` for a version the model page did not describe.

- [ ] **Step 1: Write the failing tests**

Append to `DiffusionNexus.Tests/Sync/Service/Steps/FetchImagesStepTests.cs`:

```csharp
    [Fact]
    public async Task ExecuteOneAsync_ThreeVersionsOfOneModel_MakesOneModelCall()
    {
        var item = CreateItemWithVersions(civitaiModelId: 900, civitaiVersionIds: [10, 11, 12]);

        await Step.ExecuteOneAsync(item, apiKey: null, CancellationToken.None);

        Client.Verify(c => c.GetModelAsync(900, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Client.Verify(c => c.GetModelVersionAsync(
            It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteOneAsync_VersionMissingFromTheModelPage_FallsBackToTheVersionCall()
    {
        // The model page describes 10 and 11 but not 12 — that one still needs its own call
        // rather than being silently recorded as "no images".
        var item = CreateItemWithVersions(civitaiModelId: 900, civitaiVersionIds: [10, 11, 12]);
        SetModelPageVersions(900, [10, 11]);

        await Step.ExecuteOneAsync(item, apiKey: null, CancellationToken.None);

        Client.Verify(c => c.GetModelAsync(900, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Client.Verify(c => c.GetModelVersionAsync(12, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Client.Verify(c => c.GetModelVersionAsync(10, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
```

Add the `CreateItemWithVersions` and `SetModelPageVersions` helpers to the fixture, following the existing tests' arrangement of `ImageCandidate` payloads and mocked client responses.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~FetchImagesStepTests"
```
Expected: FAIL — three version calls, no model call.

- [ ] **Step 3: Add the applier method**

In `DiffusionNexus.Service/Services/Sync/CivitaiMetadataApplier.cs`, add below `ApplyImagesAsync`:

```csharp
    /// <summary>
    /// Images for every version of one model, from a single model-page request. Returns the number
    /// of images added per Civitai version id; a version the page does not describe maps to
    /// <c>null</c> so the caller can ask for that one specifically.
    /// </summary>
    /// <remarks>
    /// <c>models/{id}</c> carries every version with its images, so asking per version cost a
    /// six-version model six requests for one page of data. The per-version
    /// <see cref="ApplyImagesAsync"/> remains for the versions a model page omits — Civitai's two
    /// endpoints do not always agree, and recording "no images" for a version we simply were not
    /// told about would stamp it as checked and never look again.
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, int?>> ApplyImagesFromModelAsync(
        IUnitOfWork uow,
        int civitaiModelId,
        IReadOnlyList<(int ModelId, int VersionId, int CivitaiVersionId)> versions,
        string? apiKey,
        CancellationToken ct = default)
    {
        var results = new Dictionary<int, int?>();
        var civitaiModel = await _client.GetModelAsync(civitaiModelId, apiKey, ct);

        foreach (var (modelId, versionId, civitaiVersionId) in versions)
        {
            ct.ThrowIfCancellationRequested();

            var civitaiVersion = civitaiModel?.ModelVersions?.FirstOrDefault(v => v.Id == civitaiVersionId);
            if (civitaiVersion is null)
            {
                results[civitaiVersionId] = null;
                continue;
            }

            var dbModel = await uow.Models.GetByIdWithIncludesAsync(modelId, ct);
            var dbVersion = dbModel?.Versions.FirstOrDefault(v => v.Id == versionId);
            if (dbVersion is null)
            {
                results[civitaiVersionId] = 0;
                continue;
            }

            var added = AppendImages(dbVersion, civitaiVersion.Images);
            if (added > 0) await uow.SaveChangesAsync(ct);
            results[civitaiVersionId] = added;
        }

        return results;
    }
```

- [ ] **Step 4: Use it from the step**

In `DiffusionNexus.Service/Services/Sync/Steps/FetchImagesStep.cs`, replace the `foreach (var candidate in candidates)` loop body (lines 101-120) with:

```csharp
            // One request for the whole model, then one per version the page did not describe.
            var civitaiModelId = candidates[0].CivitaiModelId;
            var byModel = await _civitai
                .ApplyImagesFromModelAsync(
                    uow,
                    civitaiModelId,
                    candidates.Select(c => (c.ModelId, c.VersionId, c.CivitaiVersionId)).ToList(),
                    apiKey,
                    ct)
                .ConfigureAwait(false);

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();

                // Which version the refusal handler names in its one warning line.
                inFlight = candidate;

                var added = byModel.TryGetValue(candidate.CivitaiVersionId, out var fromPage) ? fromPage : null;
                added ??= await _civitai
                    .ApplyImagesAsync(uow, candidate.ModelId, candidate.VersionId, candidate.CivitaiVersionId, apiKey, ct)
                    .ConfigureAwait(false);

                if (added is null)
                {
                    versionsGone++;
                    continue;
                }

                versionsAnswered++;
                imagesAdded += added.Value;
            }
```

If `ImageCandidate` has no `CivitaiModelId`, add one: it is populated in `SelectAsync` from the same `Model.CivitaiId` / `CivitaiModelPageId` the tags step uses (see `FetchTagsStep`'s candidate selection), and the grouping at line 66 already guarantees one model id per item.

- [ ] **Step 5: Run the tests**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~FetchImagesStep"
```
Expected: PASS, including the existing stamping and refusal tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "perf(sync): fetch a model's images in one request, not one per version

models/{id} carries every version with its images. Versions the page omits
still fall back to their own call rather than being stamped as imageless.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 10: Stop the browser searching before anyone has looked at it (F3)

`CivitaiBrowserViewModel`'s constructor runs a search, and the LoRA Viewer builds that VM when it opens — so opening the Installed tab searches Civitai, up to ten paginated calls, for a tab the user may never visit. Separately, only the text box is debounced: every sort, period and model-type change fires a search immediately.

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiBrowserViewModel.cs:102-104, 523-526`
- Modify: `DiffusionNexus.UI/Views/CivitaiBrowser/CivitaiBrowserView.axaml.cs`
- Test: `DiffusionNexus.Tests/Viewer/CivitaiBrowserViewModelBaseModelFilterTests.cs`

**Interfaces:**
- Consumes: `CivitaiBrowserViewModel.SearchAsync()` (existing private), `DebouncedSearchAsync()` (existing private).
- Produces: `public Task EnsureLoadedAsync()` on `CivitaiBrowserViewModel` — idempotent; runs the installed-set refresh and the first search once.

- [ ] **Step 1: Write the failing test**

Append to `DiffusionNexus.Tests/Viewer/CivitaiBrowserViewModelBaseModelFilterTests.cs` (using the same mocked `ICivitaiClient` construction its existing tests use):

```csharp
    [Fact]
    public void Constructor_DoesNotSearchCivitai()
    {
        var client = new Mock<ICivitaiClient>();

        _ = CreateViewModel(client.Object);

        client.Verify(c => c.GetModelsAsync(
            It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureLoadedAsync_SearchesOnce_HoweverOftenItIsCalled()
    {
        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelsAsync(
                It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModel>());
        var vm = CreateViewModel(client.Object);

        await vm.EnsureLoadedAsync();
        await vm.EnsureLoadedAsync();

        client.Verify(c => c.GetModelsAsync(
            It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
```

Add a `CreateViewModel(ICivitaiClient client)` helper to the fixture if one is not already there, mirroring the construction at `LoraViewerViewModel.cs:499`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiBrowserViewModel"
```
Expected: FAIL — the constructor searches, and `EnsureLoadedAsync` does not exist.

- [ ] **Step 3: Implement the lazy first search**

In `CivitaiBrowserViewModel`, replace lines 100-104 with:

```csharp
        // Enable search-on-filter-change now that the initial property cascade is done. The first
        // search waits for EnsureLoadedAsync: this VM is constructed when the LoRA Viewer opens,
        // so searching here meant opening the Installed tab spent up to ten paginated Civitai
        // requests on a tab the user may never look at.
        _initialized = true;
```

Add, next to `RefreshAsync`:

```csharp
    private int _loaded;

    /// <summary>
    /// Runs the first search, once, however many times the tab is shown. Called when the Browse
    /// Civitai view is first attached to the visual tree.
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (Interlocked.Exchange(ref _loaded, 1) == 1) return;

        await RefreshInstalledSetAsync();
        await SearchAsync();
    }
```

- [ ] **Step 4: Debounce the filter changes**

Replace lines 524-526:

```csharp
    // Debounced like the text box: a user comparing two sorts or three periods used to spend a
    // full paginated search on each intermediate choice.
    partial void OnSelectedSortChanged(string? value) { if (_initialized) _ = DebouncedSearchAsync(); }
    partial void OnSelectedPeriodChanged(CivitaiPeriod value) { if (_initialized) _ = DebouncedSearchAsync(); }
    partial void OnSelectedModelTypeChanged(ModelTypeOption? value) { if (_initialized) _ = DebouncedSearchAsync(); }
```

- [ ] **Step 5: Trigger the first search from the view**

In `DiffusionNexus.UI/Views/CivitaiBrowser/CivitaiBrowserView.axaml.cs`, add to the class:

```csharp
    protected override void OnAttachedToVisualTree(VisualTreeAttachedEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // The TabControl realises this view only when its tab is first selected, which is exactly
        // the moment the user has asked to see Civitai results.
        if (DataContext is CivitaiBrowserViewModel vm) _ = vm.EnsureLoadedAsync();
    }
```

Add `using Avalonia.Controls;` and `using DiffusionNexus.UI.ViewModels.CivitaiBrowser;` if they are not already present.

- [ ] **Step 6: Run the tests**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiBrowser"
```
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "perf(browser): search Civitai when the tab is opened, not when it is built

Opening the LoRA Viewer used to spend up to ten paginated searches on a tab
the user may never visit. Sort/period/type changes are debounced like the
text box now.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 11: Send the API key on the queue's rehydrate call (F4)

After a restart the download queue re-fetches each job's version anonymously. An authenticated request gets a friendlier quota and can see gated versions.

**Files:**
- Modify: `DiffusionNexus.UI/Services/CivitaiBrowser/CivitaiDownloadQueue.cs:638-644`
- Test: `DiffusionNexus.Tests/Viewer/CivitaiDownloadQueueStartResumeTests.cs`

**Interfaces:**
- Consumes: `ICivitaiApiKeyProvider.GetApiKeyAsync(CancellationToken)` (existing, `DiffusionNexus.Domain.Services`).
- Produces: no new public surface. If `CivitaiDownloadQueue` has no `ICivitaiApiKeyProvider`, add one as a trailing optional constructor parameter `ICivitaiApiKeyProvider? apiKeyProvider = null` and pass `sp.GetRequiredService<ICivitaiApiKeyProvider>()` at its construction site (`LoraViewerViewModel.cs:497`).

- [ ] **Step 1: Write the failing test**

In the queue's test fixture:

```csharp
    [Fact]
    public async Task RestoredJob_RehydratesTheVersion_WithTheApiKey()
    {
        var apiKeyProvider = new Mock<ICivitaiApiKeyProvider>();
        apiKeyProvider.Setup(p => p.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("key-a");
        var queue = CreateQueue(apiKeyProvider: apiKeyProvider.Object);

        await queue.StartAsync(CreateRestoredJob(versionId: 600), CancellationToken.None);

        Client.Verify(c => c.GetModelVersionAsync(600, "key-a", It.IsAny<CancellationToken>()), Times.Once);
    }
```

Adapt `CreateQueue` / `CreateRestoredJob` / the run entry point to whatever the existing fixture in that file already uses to drive a job through `RunJobAsync`.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiDownloadQueue|FullyQualifiedName~CivitaiWaitlist"
```
Expected: FAIL — the call goes out with a null key.

- [ ] **Step 3: Implement**

In `CivitaiDownloadQueue.cs`, replace lines 638-644:

```csharp
            var civVersion = job.CivitaiVersion;
            if (civVersion is null && _civitaiClient is not null)
            {
                // Authenticated: this is the one call in the queue that used to go out anonymously,
                // which both spent the stricter quota and hid gated versions from a restored job.
                var apiKey = _apiKeyProvider is null ? null : await _apiKeyProvider.GetApiKeyAsync(ct);
                civVersion = await _civitaiClient.GetModelVersionAsync(job.VersionId, apiKey, ct);
                job.CivitaiVersion = civVersion;
            }
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiDownloadQueue|FullyQualifiedName~CivitaiWaitlist"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "fix(queue): rehydrate a restored job's version with the API key

The one queue call that went out anonymously — it spent the stricter quota
and could not see gated versions.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 12: Reuse HTTP connections (F5)

Six sites across five files construct `new HttpClient()` per operation. That does not change the request count, but it throws away the connection pool and TLS session for every card image and every download, adding socket churn to a host we are already asking to be patient with us. `ModelDetailViewModel.cs:78` already has the right pattern.

**Files:**
- Modify: `DiffusionNexus.UI/Services/LoraDownloadService.cs:97`, `DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiResultViewModel.cs:348, 465`, `DiffusionNexus.UI/ViewModels/DownloadLoraDialogViewModel.cs:327`, `DiffusionNexus.UI/Views/Dialogs/AssignCivitaiIdsDialog.axaml.cs:274`, `DiffusionNexus.UI/Views/Dialogs/CivitaiTokenDialog.axaml.cs:105`
- Test: none — this is a lifetime change with no observable behaviour to assert. The existing suites for these types are the regression net.

**Interfaces:**
- Consumes: nothing.
- Produces: nothing public.

- [ ] **Step 1: Replace each per-operation client with a shared static**

For each of the six sites, hoist the client to a `private static readonly HttpClient` field on the type and delete the `using var` / local construction. Where the local client set a timeout (`LoraDownloadService.cs` uses 2 hours for the file transfer), keep that on the static field. Follow the shape at `ModelDetailViewModel.cs:78`:

```csharp
    /// <summary>
    /// One client for the lifetime of the process. A per-operation HttpClient discards the
    /// connection pool and the TLS session every time, which is socket churn against a host we
    /// are already asking to be patient with us.
    /// </summary>
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromHours(2) };
```

Do not add a `Dispose` for these — a static client is meant to outlive every request.

- [ ] **Step 2: Build and run the full suite**

```bash
dotnet build DiffusionNexus.sln
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj
```
Expected: build succeeds, suite passes.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "perf(http): share HttpClient instances instead of one per operation

Six sites built a client per call, discarding the connection pool and TLS
session each time. ModelDetailViewModel already had the right pattern.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 13: Verify end to end

**Files:** none modified unless a failure is found.

**Interfaces:**
- Consumes: everything above.
- Produces: a verified branch ready for a PR.

- [ ] **Step 1: Full build and test**

```bash
cd e:/Repos/DiffusionNexus
dotnet build DiffusionNexus.sln -c Release
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj
```
Expected: Release build clean; the whole suite green. Record the test count — it should be at or above the pre-change count plus the roughly 30 tests this plan adds.

- [ ] **Step 2: Confirm no call site bypasses the gateway**

```bash
git grep -n "new CivitaiClient(" -- "*.cs" ":!DiffusionNexus.Tests"
```
Expected: exactly one hit, the DI registration in `App.axaml.cs`. Any other production hit is a surface that would still be unpaced.

- [ ] **Step 3: Manual smoke — the actual bug**

Run the app (`dotnet run --project DiffusionNexus.UI`) and:
1. Open the LoRA Viewer on the Installed tab. Confirm the Unified Console shows **no** Civitai search request until the Browse Civitai tab is selected.
2. On Browse Civitai, queue five or more downloads.
3. While they run, trigger a library sync from the Installed tab.
4. Watch the Unified Console (Network category) for the whole run.

Expected: no `429` warnings; downloads complete; the browser stays responsive while the sync runs; each completed download shows **one** `models/{id}` request, not two.

- [ ] **Step 4: Manual smoke — forced refresh still refreshes**

Right-click a tile → Download Metadata, then open its detail panel. Expected: the panel shows the freshly fetched data, not a stale cached page.

- [ ] **Step 5: Commit any fixes, then open the PR**

```bash
git push -u origin feature/civitai-api-gateway
gh pr create --base develop --title "Civitai API gateway: stop the browser and downloads tripping 429" --body "$(cat <<'EOF'
## Summary
Puts every Civitai API call behind one gateway that paces requests on two
lanes, shares a 429 cooldown across all surfaces, and caches responses for a
short TTL — then removes the redundant calls the gateway cannot remove itself.

Design: `docs/superpowers/specs/2026-08-28-civitai-api-gateway-design.md`

## Test plan
- Full suite green (`dotnet test DiffusionNexus.Tests`)
- Manual: five concurrent downloads during a library sync — no 429, one
  `models/{id}` per download instead of two
- Manual: opening the LoRA Viewer no longer searches Civitai
- Manual: Download Metadata still returns fresh data

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Notes for the executor

- **The gateway must stay transparent.** If a test outside `DiffusionNexus.Tests/Civitai/` starts failing after Task 6, that is a real finding about the decorator, not a test to be adjusted — cache lifetime and pacing are the usual suspects.
- **Do not add pacing back at a call site.** If a surface seems to need different spacing, it needs a lane, not a `Task.Delay`.
- **`ImageCandidate.CivitaiModelId` (Task 9)** may not exist yet. Adding it is in scope; inventing a second way to look up a model id is not — take it from the same place `FetchTagsStep` takes its `civitaiModelId`.
