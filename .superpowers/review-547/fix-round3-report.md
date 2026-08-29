# PR #547 round-3 fix wave

Three findings from the round-3 review, fixed and verified.

## Finding A — 429 episode guard misses whenever Retry-After equals the client's own sleep

**Root cause confirmed**: `CivitaiRateLimitCooldown.OnRateLimited`'s `sameEpisode` check
(`_everRateLimited && now < _cooldownUntil`) is purely time-based. `CivitaiClient.RateLimitDelay`
returns the server's `Retry-After` verbatim in production, so the client's actual sleep between a
call's two 429 reports equals the cooldown window that report just set — landing the second report
exactly at (or a hair past) the deadline, which the time-based check cannot tell apart from a
genuinely new episode.

**Fix chosen**: kept reporting every 429 (does not lose the deadline-extension guarantee), but gave
`ICivitaiRateLimitObserver.OnRateLimited` a new `isRetryOfReportedCall` parameter that
`CivitaiClient.GetAsync` sets from its own local per-call flag (`rateLimitAlreadyReportedThisCall`,
independent of the shared `attempt` counter, which also advances on transient 5xx/exception
retries). `CivitaiRateLimitCooldown` now computes
`sameEpisode = isRetryOfReportedCall || (_everRateLimited && now < _cooldownUntil)` — the call-level
signal is authoritative when present; the old timing check remains as a fallback for reports that
don't carry it (e.g. two independently concurrent calls).

Rejected the "report only the first 429 of a call" alternative: it would silently drop the
deadline-extension guarantee if the second 429 in a call ever carried a longer `Retry-After` than
the first — a narrow case given the retry budget is 1, but avoidable for free by keeping every
report and disambiguating with a flag instead.

**Pinned-bug test verdict**: `IntervalMultiplier_DoublesPerRateLimitEpisode_CappedAtFour` encoded
the bug, not correct behaviour. Its comment claimed each of its three reports "starts a genuinely
new episode" because it arrives after the previous report's own cooldown elapsed — but the exact
timing it used (report, +1001ms on a 1s Retry-After, report again) is mechanically identical to
`CivitaiClient`'s real single-call retry sequence, which finding A shows must NOT escalate on the
second report. Renamed/split it:
- `IntervalMultiplier_ASameCallRetry_DoesNotEscalate_EvenAfterItsOwnCooldownElapses` — same timing,
  second report marked `isRetryOfReportedCall: true`, multiplier stays at 2.
- `IntervalMultiplier_DoublesPerGenuinelySeparateEpisode_CappedAtFour` — same timing, but each
  report is a fresh call (flag left at its `false` default), still escalates 2→4→4. Kept
  deliberately identical timing to the first test so the flag — not the clock — is what the reader
  sees disambiguating the two scenarios.

Also updated `IntervalMultiplier_TwoReportsWithinTheSameCooldown_StaysAtTwo` (finding 10's original
regression test) to pass `isRetryOfReportedCall: true` on its second call, matching what
`CivitaiClient` actually sends — it already passed without the flag (no clock advance), but now
mirrors production rather than only the coincidentally-safe case.

**New tests driving the real sequence**, in `CivitaiClientTests.cs`, through the actual
`CivitaiClient` + `CivitaiRateLimitCooldown` pair (not the cooldown directly):
- `GetAsync_ReportsOnlyTheFirst429OfACall_AsNotARetryOfAnAlreadyReportedCall` — asserts the observer
  sees `[false, true]` across a call's two reports.
- `GetAsync_TwoRateLimitsInOneCall_EscalatesTheSharedCooldownMultiplierOnlyOnce` — wires a real
  `CivitaiRateLimitCooldown` as the observer, uses `RetryDelayOverride` to advance a shared fake
  clock by exactly the server's `Retry-After` (standing in for the real `Task.Delay`), and asserts
  `cooldown.IntervalMultiplier == 2` after the call throws `CivitaiRateLimitedException`.

## Finding B — check-then-act race in `CivitaiResponseCache`'s write

**Fix**: stamped the fetch's captured `generation` and `keyVersion` into `Entry` (new fields), and
`TryGet` now rejects (and removes) an entry whose stamped values don't match what's current at read
time — same treatment as an expired entry. This closes the window instead of narrowing it: the
existing pre-write check is still there (cheap, common-case filter) but is no longer the actual
guarantee. Updated the class's stale "answer is never written back" doc claims to describe the real
invariant (may be written, never served).

**Test**: `InvalidateModel_BetweenTheVersionCheckAndTheWrite_StillSuppressesTheStaleWrite`. The
check-then-write in `RunAsync` has no `await` between the two statements, so the race is a genuine
thread race, not reproducible via async ordering alone — added a minimal `internal` test-only hook
(`BeforeCacheWrite`, precedented by `CivitaiClient.RetryDelayOverride`) that fires exactly between
the check and the write, letting the test land `InvalidateModel` deterministically in that gap. The
test fails without the fix (stale value served, second fetch's factory never called) and passes
with it.

## Finding C — `ResolveHashAsync` doc comment overclaimed "previously-verified"

No migration/backfill/schema flag added — confirmed via `git branch -a --contains d9a1748c` that
the sidecar-first reorder is unique to this branch and never reached develop/main, so no released
build could have written a poisoned row. Rewrote `ResolveHashAsync`'s XML remarks to state plainly:
the short-circuit trusts `candidate.Sha256` by SHAPE only, is safe within one execution (re-hashes
regardless of what the sidecar branch wrote earlier in the same call), but does not and cannot
re-verify a value that reached the DB from an earlier execution — naming the exact commit range
(`d9a1748c`..`d552e398`) where such a row could have been written, and noting the mismatch warning
can therefore never fire for one. Also softened the "previously-verified" phrasing in
`ExecuteOneAsync`'s sidecar-branch comment to point at those remarks instead of asserting it
directly. Comment-only change; builds clean.

## Verification

- `dotnet build DiffusionNexus.sln -c Release -o <side path>` — 0 errors (side output path used
  throughout; no locked `bin/Release`).
- `dotnet test DiffusionNexus.Tests` — 4987 passed / 0 failed / 2 skipped (baseline 4983/0/2 + 4 new
  tests: 3 from finding A, 1 from finding B).
- `dotnet test DiffusionNexus.IntegrationTests` — 11 passed / 0 failed (matches baseline).
- `dotnet test --filter "FullyQualifiedName~CivitaiBrowser"` in isolation — 63 passed / 0 failed.
- The four known pre-existing order-dependent flakes (`CheckScoreAdapterTests`,
  `GenerationGalleryViewModelTests`, a Distiller temp-file-lock test,
  `JsonInfoFileReaderServiceTests`) were not touched and were not hit in the full-suite run above
  (it passed clean).

## Files touched

- `DiffusionNexus.Civitai/CivitaiRateLimitedException.cs`
- `DiffusionNexus.Civitai/CivitaiRateLimitCooldown.cs`
- `DiffusionNexus.Civitai/CivitaiClient.cs`
- `DiffusionNexus.Civitai/CivitaiResponseCache.cs`
- `DiffusionNexus.Service/Services/Sync/Steps/IdentifyModelStep.cs`
- `DiffusionNexus.Tests/Civitai/CivitaiRateLimitCooldownTests.cs`
- `DiffusionNexus.Tests/Civitai/CivitaiClientTests.cs`
- `DiffusionNexus.Tests/Civitai/CivitaiResponseCacheTests.cs`
