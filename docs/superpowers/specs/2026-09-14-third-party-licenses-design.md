# Third-party licences: generated notices + an About screen

Date: 2026-09-14
Issue: #511 (audit + attribution)
Branch: `feature/third-party-licenses`

## Problem

DiffusionNexus ships no attribution of any kind. There is no notices file in the
repo, nothing in the release zip, and no screen in the app. Every dependency that
requires its licence text to accompany the binary — LGPL, OFL, Apache-2.0, the
BSD family — is currently shipped without it.

The sibling Electron installer solved this: a PowerShell generator reads the
restore graph, emits `THIRD-PARTY-NOTICES.txt`, embeds it, shows it on a
`/licenses` page, and CI fails when it drifts. This design ports that machinery
to the main app and puts the result behind a button in the left navigation pane.

Two licence problems surfaced while scoping the work and are fixed here rather
than filed for later, because both are cheap and both are live today:

- `FluentAssertions 8.10.0` (tests) is the Xceed licence — paid for commercial
  use with **no** revenue threshold. Test-only does not exempt it; it is a *use*
  licence, not a distribution licence.
- `Xabe.FFmpeg 6.0.2` is CC BY-NC-SA 3.0 — non-commercial only, with no
  open-source carve-out, and it is **embedded into the shipped single-file exe**.

## Non-goals

- The remainder of issue #511 (full remediation) — this delivers the attribution
  half only.
- LGPL user-replaceability of the bundled native libraries.
- Remediating the LGPL/OFL components themselves — attribution discharges what
  this design is responsible for.

---

## 1. Generation

### 1.1 `Scripts/Generate-ThirdPartyNotices.ps1`

Ported from `DiffusionNexus.Installer`'s generator, minus the npm half (no
Electron here) and minus `-AllowLocalSdk` — this repo has no
`Directory.Build.targets` SDK redirect, so the local and CI restore graphs
already match.

Reads `DiffusionNexus.UI/obj/project.assets.json`: the project `publish.ps1`
actually ships. For every `type: "package"` entry it resolves the package folder
in the NuGet cache (both path segments lower-cased, as NuGet's global-packages
layout does) and reads from the nuspec: licence expression or licence file,
copyright, authors, projectUrl.

**Fail-closed rules, both retained from the original:**

- A package that declares no licence **throws**. It must gain a hand-authored
  `supplements.json` entry before the build can proceed.
- A package whose nuspec declares `<license type="file">` has no SPDX text to
  print, so it **throws** unless `supplements.json` carries a `bundledNotices`
  entry reproducing that file verbatim. Without this rule such a package
  silently contributes nothing.

**Runtime packs.** `publish.ps1` publishes self-contained single-file
(`--self-contained true -p:PublishSingleFile=true`), so the .NET runtime packs
are embedded in the artifact and each ships its own `THIRD-PARTY-NOTICES.TXT`
(zlib, Brotli, ICU, Unicode data). The generator walks `downloadDependencies` in
the restore graph and reproduces every notices file it finds.

> This is the corrected behaviour. The Electron generator ships without it —
> it is unfixed finding (b) of
> `docs/superpowers/reviews/2026-09-05-pr-4-third-party-notices.md` in that repo.
> Do not port the shipped version.

### 1.2 `Scripts/license-data/`

- `supplements.json` — `excludePrefixes: ["DiffusionNexus."]` (first-party
  packages carry no third-party obligation), `bundledNotices`, and hand-authored
  `supplements` entries for material the graph cannot express.
- `texts/` — SPDX texts this graph needs: MIT, Apache-2.0, BSD-3-Clause,
  LGPL-2.1, OFL-1.1 (Inter, via `Avalonia.Fonts.Inter`), the Six Labors Split
  Licence, and the .NET runtime MIT text.

### 1.3 Outputs

Both files are produced from one in-memory model in a single pass, so they
cannot drift from each other.

| File | Purpose |
|---|---|
| `THIRD-PARTY-NOTICES.txt` | The flat legal document. Ships beside the exe. |
| `third-party-notices.json` | UI index: `{id, version, license, copyright, authors, projectUrl, licenseText}` per component. |

### 1.4 Floating versions

This repo floats `Microsoft.EntityFrameworkCore 10.0.*` and
`Microsoft.Extensions.* 10.0.*`. A naive freshness gate turns CI red on an
unrelated PR the day EF ships a patch — the Electron repo hit exactly this with
`Microsoft.AspNetCore.App.Internal.Assets`.

Therefore:

- `-Check` compares with **version strings normalised out**. A new package, a
  dropped package or a changed licence fails; a patch bump does not. Attribution
  obligations attach to the component and its licence, not its patch number.
- The comparison is otherwise exact **including case** — use `-cne`, not `-ne`.
  PowerShell's `-ne` is case-insensitive and the Electron drift gate misses
  case-only changes because of it.
- To keep shipped versions exact anyway, `publish.ps1` **regenerates both output
  files before `dotnet publish`** rather than shipping the committed copies. The
  committed files guard content through CI; the published build carries true
  versions in both the embedded JSON and the text file. Regenerating *after*
  publish would be wrong: the JSON is embedded at build time, so the screen would
  show patch-stale versions while the text file beside it showed exact ones.

Build-only auto-referenced SDK assets (`autoReferenced: true` in the graph) are
skipped for the same reason: their version follows the installed SDK.

---

## 2. Shipping

**Embedding.** `DiffusionNexus.UI.csproj` gains:

```xml
<EmbeddedResource Include="..\third-party-notices.json"
                  LogicalName="THIRD-PARTY-NOTICES.json" />
```

An embedded resource survives single-file publish with no
`IncludeAllContentForSelfExtract` interaction, so the screen works offline and
always matches the binaries inside that exe.

**Release artifact.** `publish.ps1` gains two steps, in this order:

1. **Before `dotnet publish`** (and after the restore it implies): regenerate
   both outputs, so the JSON embedded into the exe carries exact resolved
   versions — see §1.4.
2. **After publish, before `Compress-Archive`**: copy the freshly generated
   `THIRD-PARTY-NOTICES.txt` into `$OutputDir`, so it lands in
   `diffusion_nexus.V<ver>.zip` beside the exe.

If the generator fails, publish fails — a release that cannot produce its
notices is not a release.

**CI gate.** `.github/workflows/dotnet.yml` gains one step between *Restore* and
*Build*:

```yaml
- name: Check third-party notices are current
  shell: pwsh
  run: pwsh Scripts/Generate-ThirdPartyNotices.ps1 -Check
```

On every PR, not release-only. A release-time gate fails at the worst possible
moment; the check belongs at the start of the process.

---

## 3. The UI

### 3.1 Navigation entry

A fourth 196×48 button in the bottom `StackPanel` of the pane in
`DiffusionNexusMainWindow.axaml`, **below Civitai**, bound to `OpenAboutCommand`
— which already exists on the ViewModel and is bound in no XAML today. This
revives dead code rather than adding a command. Icon is an emoji in a `Border`,
matching how the Feedback button avoids needing a new asset.

**Insulation from Installer issue #6.** That issue makes the YouTube / Patreon /
Civitai list data-driven from a gist or catalog JSON, and its sample payload
already grows the list to five entries. The three social buttons are therefore
wrapped in their own container **now**, while still hardcoded, with About placed
as a sibling *after* that container. When #6 lands, the container's contents
become an `ItemsControl` and About is untouched — remote data can never reorder
or displace an app-owned screen. Without the grouping, About is just another
child of the same panel and whoever implements #6 has to notice it.

**`OpenAbout()` teardown bug.** It currently sets `CurrentModuleView` but,
unlike `NavigateToModule`, never clears `IsSelected` across `Modules` and never
calls `OnThumbnailDeactivated` on the outgoing module. About would render with
another module still highlighted and its thumbnail pipeline still running. Both
`OpenAbout` and `OpenSettings` are routed through one shared "show an app-level
view" helper that performs the teardown. This is a real defect in code the
change touches, not unrelated refactoring.

Known limitation, not fixed here: the bottom stack does not scroll. Six gist
links plus About at 48px each can overflow a short window.

### 3.2 The About screen

`AboutView` becomes a `ViewBase<AboutViewModel>` (the repo convention per
`DiffusionNexus.UI/REUSABLES.md`) with two regions.

**Identity** — unchanged in substance: "Diffusion Nexus", `AppVersion`, and the
red no-warranty paragraph, which today exists nowhere else.

**Third-party software** — a search box over a virtualising `ListBox` of
components on the left; the selected component's detail on the right: id,
version, SPDX licence id, copyright, a clickable project URL, and its licence
text in a `SelectableTextBlock`. One licence text at a time is a few KB, so the
~100 KB single-`TextBlock` layout problem never arises.

Search matches **id *and* licence id**, so typing `LGPL` answers "what here is
copyleft" — the question someone opens this screen to ask.

A footer line points at `THIRD-PARTY-NOTICES.txt` beside the exe with a button
to reveal it, shown only when the file exists (dev builds have no publish
folder).

**No `ScrollViewer.Padding` anywhere on this screen.** It makes the bottom
3× padding unreachable in every Avalonia 11.3.x build (PR #563). Padding goes on
the child's `Margin`.

### 3.3 ViewModel and service

`AboutViewModel` keeps `AppVersion` and gains `SearchText`, a filtered
`Components` view, `SelectedComponent`, `OpenProjectUrlCommand` and
`OpenNoticesFileCommand`.

`Services/Licensing/ThirdPartyNotices` deserialises the embedded JSON once
through a `Lazy<T>` into `IReadOnlyList<ThirdPartyComponent>`. Read-once matters:
the text is ~100 KB and must not be re-decoded per render. That record is the
seam — the ViewModel is testable without the resource.

---

## 4. Licence remediation folded into this branch

### 4.1 FluentAssertions → 7.2.2

`DiffusionNexus.Tests.csproj` pins `FluentAssertions` from `8.10.0` to `7.2.2`,
with the rationale inline at the `PackageReference`.

8.x is the Xceed licence: free for open-source/non-commercial, **paid for
commercial use with no revenue threshold**. 7.2.2 is verified Apache-2.0 (nuspec
licence expression `Apache-2.0`, no `requireLicenseAcceptance`, projectUrl back
to fluentassertions.com). The SDK and Installers repos already made this move and
measured it free: 0 compile errors, byte-identical test results across ~6350
`.Should()` sites. The `AssertionScope` → `AssertionChain` 8.0 break does not
apply — there are no custom FA extensions to port.

### 4.2 Xabe.FFmpeg → FFMpegCore

**Why.** `Xabe.FFmpeg 6.0.2` is CC BY-NC-SA 3.0 (verified at
`ffmpeg.xabe.net/license.html`: "You may use Software under
Attribution-NonCommercial-ShareAlike 3.0 Unported (CC BY-NC-SA 3.0) license for
non commercial projects"). The page has **no** open-source carve-out — the only
axis is commercial vs non-commercial, and NonCommercial restricts use and
distribution regardless of whether source is published.

The binding problem is the outbound grant. Publishing under any OSS licence
promises downstream users commercial use, but `publish.ps1` embeds
`Xabe.FFmpeg.dll` into the single-file exe and that may only be passed on under
NC. The promise and the restriction contradict each other; CC BY-NC-SA is not
OSI-approved and is incompatible with every OSS licence that would plausibly be
chosen. Their terms additionally bar distribution "as a standalone product; a
similar product" and bar sublicensing to third parties. Buying the €199/year
commercial seat fixes *use* but not this redistribution contradiction.

**Replacement.** `FFMpegCore` 5.4.0 — MIT, verified from the package LICENSE
("MIT License / Copyright (c) 2023 Vlad Jerca") — plus
`FFMpegCore.Extensions.Downloader` 5.0.0, also MIT, for the runtime fetch. The
`FFMpegDownloader.DownloadFFMpegSuite()` API lives in the extension package, not
the core one.

**Blast radius: one file**, `DiffusionNexus.Service/Services/VideoThumbnailService.cs`.

| Xabe | FFMpegCore |
|---|---|
| `FFmpeg.SetExecutablesPath(dir)` | `GlobalFFOptions.Configure(new FFOptions { BinaryFolder = dir })` |
| `FFmpeg.GetMediaInfo(path, ct)` | `FFProbe.AnalyseAsync(path, cancellationToken: ct)` |
| `mediaInfo.VideoStreams.FirstOrDefault()` | `analysis.PrimaryVideoStream` |
| `FFmpeg.Conversions.FromSnippet.Snapshot(...)` + `AddParameter` | `FFMpegArguments.FromFileInput(...).OutputToFile(..., o => o.WithCustomArgument(...))` |
| `FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, dir)` | `FFMpegDownloader.DownloadFFMpegSuite()` |

Two behaviour differences to handle, not paper over:

- **ffprobe.** FFMpegCore shells out to `ffprobe` for analysis as well as
  `ffmpeg`. The existing four-step discovery (app-local `ffmpeg/`, app base dir,
  system PATH, download) probes for `ffmpeg` only. Each probe step must verify
  **both** binaries, or a machine with only `ffmpeg` on PATH passes discovery and
  then fails at analysis.
- **Download source changes** from Xabe's own server to the ffbinaries.com API.
  This path needs a real network verification, not a unit test.

The ffmpeg **binaries** themselves are unaffected: they are fetched at runtime
and executed as a separate process under either library, so their own LGPL/GPL
terms reach no further than they do today.

---

## 5. Testing

xunit in `DiffusionNexus.Tests`, ViewModel-level. No headless render tests —
they hit the Avalonia font-manager quirk this repo has already been bitten by.

- `ThirdPartyNotices` loads the embedded resource under its exact `LogicalName`
  and parses it. Catches the classic silent break: someone renames the file, the
  resource vanishes, the screen quietly shows nothing.
- **Copyleft canary** — the parsed set contains `LibVLCSharp`,
  `VideoLAN.LibVLC.Windows`, `HPPH.SkiaSharp` and `SixLabors.ImageSharp`. If a
  generator change ever drops the components carrying real obligations, a test
  fails rather than a lawyer noticing. (`Xabe.FFmpeg` is deliberately *not* in
  this list — §4.2 removes it, and asserting its presence would fight the fix.)
- Every component has a non-empty `licenseText` — no blank attribution passing as
  attribution.
- `AboutViewModel` filtering: by id, by licence id, case-insensitive, empty
  search shows all, and selection behaviour when the filter excludes the selected
  item.
- `VideoThumbnailService` discovery: a directory holding only `ffmpeg` is
  **not** accepted as a valid binary folder (the ffprobe regression above).

### Verification before claiming completion

1. Run the generator for real; then run `-Check` and show it round-trips with
   zero diff.
2. Full `dotnet test` green, on 7.2.2.
3. Generate a real video thumbnail through the swapped service, including the
   download path on a machine with no ffmpeg.
4. Launch the app: the button appears below Civitai, the screen opens, search
   works, a licence text renders.
5. CRLF check before push — compare `git diff --numstat` against `-w` and
   restore line endings on any file flipped wholesale.

---

### 4.3 Repo `LICENSE`: MIT

`DiffusionNexus` is public with no licence, which in law means all rights
reserved — "public on GitHub" is not a licence grant. A `LICENSE` file is added:

```
MIT License

Copyright (c) 2025 Christian Wenzl
```

...followed by the standard MIT text, byte-identical in form to the file already
in `DiffusionNexus.Installers`, keeping the two repos consistent.

This is not housekeeping. The Six Labors Split Licence grants
`SixLabors.ImageSharp 3.1.12` under Apache-2.0 if "You are consuming the Work in
for use in software licensed under an Open Source or Source Available license" —
a clause that cannot be satisfied while no licence exists. Adding MIT closes the
ImageSharp question outright, with the `<$1M annual gross revenue` clause as a
second, independent escape. `README.md` gains a licence line pointing at the
file.

**Commit ordering is load-bearing.** The `LICENSE` commit must come *after* the
Xabe swap in §4.2. MIT promises downstream users the right to use the work
commercially; while `Xabe.FFmpeg` is embedded in the shipped exe under
CC BY-NC-SA, that is a promise the project does not hold the rights to make.
Swap first, licence second.

## Open decisions

None. The `LICENSE` choice — the last one — was settled as MIT above.
