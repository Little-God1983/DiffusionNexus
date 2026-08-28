# Civitai API Gateway — design

**Date:** 2026-08-28
**Branch:** `feature/civitai-api-gateway`
**Problem:** Downloading from Civitai in the LoRA Viewer / Civitai Browser trips HTTP 429.

## Why we get rate limited

Seventeen production call sites share one `ICivitaiClient`, and only four of
them — all inside the library-sync pipeline — go through the 1.5 s
`CivitaiRequestPacer`. Everything else fires unpaced and concurrently:

| Surface | Calls | Pacing |
|---|---|---|
| Browser search (`CivitaiBrowserViewModel`) | up to 10 sequential `GET models` per action + tag fallback | none; only typed input is debounced |
| Update checker (`LoraUpdateChecker`) | up to 200 `GET models/{id}`, 4 concurrent, on *every* filter/sort pass | none; private 60 s 429 cooldown |
| Detail panel (`ModelDetailViewModel`) | 1 `GET models/{id}` on **every** open | none; per-load field only |
| Download persist (`LoraDownloadService`) | `GET models/{id}` per download | none |
| Download completion sync (`CivitaiMetadataApplier`) | **the same** `GET models/{id}` again, seconds later | paced |
| Waitlist, sorter, pipeline installer, URL dialogs | 1–2 calls each | none |

So one download costs `GET models/{id}` **twice**, two downloads run
concurrently, and a background sync plus an update-check sweep can be in
flight at the same moment. When one of them draws a 429, only that caller
learns about it; the rest keep firing, and the client's own backoff sleeps
10/20/40 s **inside the call**, holding a download slot the whole time.

Three structural gaps cause this:

1. **No shared throttle.** Pacing lives at four hand-picked call sites, not
   at the client.
2. **No shared knowledge of a 429.** Each surface discovers the limit
   independently and keeps digging.
3. **No shared cache.** The same model page is fetched repeatedly by
   different surfaces within seconds.

## Approach

One decorator around `ICivitaiClient` — `CivitaiApiGateway` — registered *as*
`ICivitaiClient`, so all seventeen call sites become paced, cached, and
429-aware without touching their code. Then a short list of call-site fixes
the gateway cannot do for them.

Considered and rejected:

- **Per-call-site fixes only.** Fixes today's redundancies; the next surface
  added reintroduces the problem. Doesn't solve gaps 1 or 2.
- **A single global 1.5 s lane for everything.** Simplest, but a background
  sync would then make browser search feel broken. Two lanes cost ~10 lines.
- **Disk / ETag caching.** Conditional requests still count against the
  quota, and `ModelSyncState` already serves as the long-term cache. Not
  worth the complexity now.

## Component 1 — `CivitaiApiGateway`

Lives in `DiffusionNexus.Civitai` (a dependency-free project; the gateway
adds no references). Implements `ICivitaiClient` by delegating to the inner
`CivitaiClient`, and adds three things.

### Throttle — two lanes, one timestamp

`CivitaiRequestPacer` **moves** from `DiffusionNexus.Service.Services.Sync`
into `DiffusionNexus.Civitai`, gains a per-call interval parameter, and
becomes the gateway's private collaborator.

```
CivitaiCallLane { Interactive, Background }
  Interactive → min 750 ms since the previous request (any lane)
  Background  → min 1500 ms since the previous request (any lane)
```

One `_lastCall` timestamp shared by both lanes, so background work
automatically spaces itself behind interactive work. A user-facing search
never waits behind a sync's 1.5 s interval; a sync never bursts ahead of a
user.

The lane comes from **which gateway instance you resolve**, not from an
ambient context:

- `services.AddSingleton<ICivitaiClient>` → interactive gateway (default).
- `services.AddKeyedSingleton<ICivitaiClient>("background")` → background
  gateway, sharing the *same* pacer, cache and cooldown singletons.

Background consumers are exactly two: the library-sync pipeline
(`AddDiffusionNexusSync`) and `LoraUpdateChecker`. Everything else keeps the
default registration and needs no change.

The four manual `_pacer.WaitAsync(ct)` calls in `CivitaiMetadataApplier`
(×3) and `IdentifyModelStep` (×1) are **removed** along with their optional
`pacer` constructor parameters — leaving them would double-pace to 3 s.
Pacing moves from four scattered sites to one.

### 429 cooldown — shared, adaptive

State on the singleton, consulted before every request in either lane:

- On 429: set `_cooldownUntil = now + Retry-After` (supporting both the
  delta *and* the HTTP-date form — the date form is silently ignored today),
  falling back to 30 s. Double `_intervalMultiplier`, capped at 4×.
- Every call waits out an active cooldown before proceeding, so one surface's
  429 pauses *all* of them.
- `_intervalMultiplier` decays back to 1 after 5 minutes without a 429.

**How the gateway learns.** The 429 response is visible only inside
`CivitaiClient`, which today converts it into a bare `HttpRequestException`
carrying nothing but the status. Two additions close that gap:

- `CivitaiClient` takes an optional `ICivitaiRateLimitObserver` and calls it
  the moment *any* 429 arrives — including one its own retry then recovers
  from. So the first 429 pauses every other surface immediately, rather than
  only after the unlucky caller has exhausted its retries.
- When it does give up, it throws `CivitaiRateLimitedException`, which
  derives from `HttpRequestException` with `StatusCode = TooManyRequests`
  and adds `RetryAfter`. Every existing `catch (HttpRequestException ex)
  when (ex.StatusCode == TooManyRequests)` handler keeps working unchanged.

`Retry-After` is parsed once, in the client, accepting **both** the delta
form and the HTTP-date form (only the delta form is understood today).

The inner client's 429 retry budget drops from 3 to 1. Three retries meant a
single logical call could sleep 10 + 20 + 40 s *inside* the call while
holding a download slot; with a shared cooldown now doing that job properly,
one immediate retry is the right amount of local optimism. The 5xx/transient
budget stays at 3.

`LoraUpdateChecker`'s private 60 s cooldown is deleted — the gateway's is
strictly better.

### Cache — in-memory, bounded, TTL

| Method | Key | TTL | Notes |
|---|---|---|---|
| `GetModelAsync` | model id | 15 min | the hot path; subsumes most redundancy |
| `GetModelVersionAsync` | version id | 15 min | |
| `GetModelVersionByHashAsync` | hash (upper-cased) | 60 min | immutable mapping; caches 404-as-null too |
| `GetModelsAsync` | full query string | 2 min | kills filter-toggle re-searches |
| `GetTagsAsync`, `GetCreatorsAsync`, `GetImagesAsync` | — | not cached | unused in production |

- A single bounded store, 1000 entries, evicting oldest-inserted first. One
  cap is enough — per-kind caps would be three knobs measuring the same
  memory.
- **Single-flight**: concurrent callers for the same key await one request
  rather than issuing N. This alone collapses the two-concurrent-download
  case when both hit the same model page.
- Cached entries are keyed **without** the API key. The gateway remembers
  the last key it was called with; when a call arrives carrying a different
  one (including null → set, or set → null), it clears the cache before
  proceeding, so a key change never serves anonymous results to an
  authenticated caller.
- Exceptions are never cached; a `null` (404) is.

The cache is what removes the download path's duplicate `GET models/{id}`,
the applier's per-file repeat for multi-version models, the detail-panel
refetch after an update check, and the waitlist's double-fetch — no call-site
edits needed for any of them.

### Escape hatch

`ICivitaiApiCache` (implemented by the gateway) exposes
`InvalidateModel(int id)`, `InvalidateVersion(int id)` and `Clear()`.
User-initiated "give me fresh data" paths call it before fetching:
`LibrarySyncService` when any `Force*` option is set, and the LoRA Viewer's
per-tile "Download Metadata".

## Component 2 — call-site fixes the gateway cannot make

| # | Change | Saving |
|---|---|---|
| F1 | `IdentifyModelStep`: read the `.civitai.info` sidecar **before** the by-hash call, not as a 404 fallback (the Sorter already does it in this order) | 2 calls per sidecar-bearing file on first sync |
| F2 | `FetchImagesStep`: one `GET models/{id}` (which returns every version with its images) instead of one `GET model-versions/{id}` per version | M→1 per multi-version model |
| F3 | `CivitaiBrowserViewModel`: don't run a search from the constructor — the LoRA Viewer builds this VM on open, so opening the viewer searches Civitai even if the browser tab is never shown. Search on first activation. Also debounce filter/sort changes, which today bypass the 400 ms debounce that only guards typing | up to 10 calls per viewer open |
| F4 | `CivitaiDownloadQueue:561`: pass the API key to `GetModelVersionAsync` (currently anonymous) | authenticated quota, fewer gated-model retries |
| F5 | Replace the five `new HttpClient()`-per-operation sites with shared static/injected clients | connection reuse, not call count |

F1–F3 are the ones that change request *counts*; F4–F5 are cheap hygiene
that ride along.

## Data flow after the change

**A download** (`GET models/{id}` ×2 → ×1):
`LoraDownloadService` fetches the model page (interactive lane, cached);
the completion sync asks for the same id seconds later and is served from
cache. Net API cost: the file GET plus one model page.

**Browsing:** search results cached 2 min; back-and-forth filter toggles and
re-opening the viewer are free. Pagination is paced at 750 ms.

**Background sync / update sweep:** one request per 1.5 s, yielding to any
interactive call, pausing entirely for the cooldown after a 429.

## Error handling

- A 429 that survives the inner client's retries still throws
  `HttpRequestException` with `StatusCode = TooManyRequests`; the gateway
  records the cooldown and rethrows. Callers' existing handling is unchanged.
- Cooldown waiting honours the caller's `CancellationToken`, so a cancelled
  download or a closed dialog doesn't sit in a 30 s sleep.
- The gateway never swallows an exception and never caches one.

## Testing

Unit tests against a fake inner `ICivitaiClient` (counting calls) in
`DiffusionNexus.Tests/Civitai/`:

- Two calls for the same model id → one inner call; after TTL → two.
- Concurrent calls for the same key → one inner call (single-flight).
- A 404 (`null`) is cached; an exception is not.
- Interactive and background lanes observe their respective minimum
  intervals against an injected clock/delay (the pacer's existing test seams).
- A 429 recorded by one lane delays the next call in the other.
- `Retry-After` as an HTTP-date is honoured, not ignored.
- `InvalidateModel` forces a refetch.
- Changing the API key clears the cache.

Existing tests that construct `CivitaiMetadataApplier` / `IdentifyModelStep`
with a pacer argument need updating for the removed parameter. Step-level
tests assert the new call shapes for F1 and F2.

Manual smoke (owed, cannot be automated): queue 5+ downloads in the browser
while a library sync runs and confirm no 429 surfaces and the UI stays
responsive.

## Known limitations

The two lanes equalise the request **interval**, not queue **priority**.
`CivitaiRequestPacer.WaitAsync` acquires a single `SemaphoreSlim(1,1)` and
sleeps inside it, so waiters of both lanes are served FIFO — whoever calls
`WaitAsync` first is released first, regardless of lane. The "a user-facing
search never waits behind a sync's 1.5 s interval" claim above is true of the
*interval* (an interactive call never has to wait out a background-length
gap) but not of the *queue*: a burst of background callers already queued
ahead of an interactive one still goes first. Worst realistic case — the
update checker's 4 concurrent calls, the waitlist's 3, plus a sync's 1 — means
an interactive call can queue behind roughly 8 requests and wait around 10 s
before its own leaves. Bounded, and still far better than the 429 storms this
design fixes, but worth naming plainly. Future fix: a two-queue pacer that
drains interactive waiters ahead of background ones.

## Out of scope

Disk-backed or ETag caching; making the download-file endpoint itself
(`api/download/models/{id}`, which is not `ICivitaiClient`) go through the
gateway; user-facing settings for the intervals (constants, tunable in one
place).
