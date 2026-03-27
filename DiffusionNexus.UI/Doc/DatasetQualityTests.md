# Dataset Quality & Bucket Analysis — Test Documentation

## Purpose

This document describes the full test suite for the **Dataset Quality** and **Bucket Analysis** features of DiffusionNexus. These features help LoRA trainers catch common captioning mistakes, dataset composition problems, and image bucketing issues *before* they start a multi-hour training run.

The tests serve two goals:

1. **Regression safety** — every bug-prone edge case (empty captions, moved files, extreme aspect ratios, Unicode, booru vs NL formatting) has a dedicated test so future changes can't silently break existing behavior.
2. **Living specification** — reading the tests tells you exactly what the code considers "wrong" with a dataset, at what severity, and what auto-fix it proposes.

All tests live in `DiffusionNexus.Tests/DatasetQuality/` and use **xUnit**, **FluentAssertions**, and **Moq**.

---

## Architecture of the Test Suite

The suite is split into three tiers that mirror the production code:

```
┌───────────────────────────────────────────────────────────┐
│ Tier 1 — Infrastructure                                   │
│ ImageHeaderReaderTests  CaptionLoaderTests                │
│ TextHelpersTests        DictionaryTests                   │
│                                                           │
│ Purpose: verify the low-level building blocks (read       │
│ pixels without loading a bitmap, load captions from disk, │
│ detect caption style, maintain dictionary invariants).     │
│ If these break, every higher-level check breaks too.      │
├───────────────────────────────────────────────────────────┤
│ Tier 2 — Individual Checks                                │
│ FormatConsistencyCheckTests   TriggerWordCheckTests       │
│ SynonymConsistencyCheckTests  FeatureConsistencyCheckTests│
│ TypeSpecificCheckTests                                    │
│                                                           │
│ Purpose: each IDatasetCheck implementation has its own    │
│ test class covering detection thresholds, severity        │
│ levels, affected-file reporting, and auto-fix generation. │
├───────────────────────────────────────────────────────────┤
│ Tier 3 — Orchestration & Side Effects                     │
│ AnalysisPipelineTests    FixApplierTests                  │
│ BucketAnalyzerTests                                       │
│                                                           │
│ Purpose: the pipeline wires checks together in order and  │
│ sorts results; FixApplier writes edits to disk; Bucket    │
│ Analyzer runs the full kohya_ss-style analysis end-to-end.│
└───────────────────────────────────────────────────────────┘
```

---

## 1. BucketAnalyzerTests

**What it tests:** `BucketAnalyzer` — the engine that replicates kohya_ss-style resolution bucketing. During LoRA training, images are grouped into fixed-resolution "buckets" and resized to fit. If the dataset has wildly different aspect ratios, tiny images that need heavy upscaling, or a skewed distribution across buckets, training quality suffers. This analyzer catches those problems before you spend GPU hours.

**Test strategy:** All tests use in-memory `ImageFileInfo` records (no files on disk). A `ConfigWith(...)` helper lets each test tweak exactly the parameters it cares about while keeping everything else at production defaults.

### 1.1 Bucket Generation

The first thing the analyzer does is build the set of allowed resolutions. These tests make sure the generator respects every constraint in `BucketConfig` — because if the bucket grid itself is wrong, every downstream assignment and metric will be wrong too.

| Test | Purpose |
|---|---|
| `WhenDefaultConfigThenGeneratesExpectedBucketCount` | **Smoke test.** Production defaults (base=1024, step=64, min=256, max=2048, ratio≤2.0) should produce a non-empty set of buckets. If this fails, the algorithm is fundamentally broken. |
| `WhenDefaultConfigThenAllDimensionsAreMultiplesOfStepSize` | kohya_ss requires every bucket dimension to be a multiple of the step size (typically 64). Training crashes or degrades with non-aligned dimensions. This iterates *every* generated bucket and checks both width and height. |
| `WhenDefaultConfigThenAllDimensionsWithinBounds` | Ensures no bucket dimension drops below `MinDimension` (256) or exceeds `MaxDimension` (2048). Out-of-bound buckets waste VRAM or produce artifacts. |
| `WhenDefaultConfigThenAspectRatiosWithinLimit` | For each bucket, computes `max(w,h)/min(w,h)` and asserts it stays within `MaxAspectRatio` (2.0). Extreme-AR buckets cause severe cropping and are excluded by kohya_ss. |
| `WhenDefaultConfigThenBothOrientationsPresent` | A valid bucket set must include landscape (w>h), portrait (h>w), and at least one square (w==h) bucket. If any orientation is missing, images of that type have no good match and get heavily cropped. |
| `WhenDefaultConfigThenBucketsAreSorted` | Buckets must be sorted (by `CompareTo`) so that `FindBestBucket` can work with deterministic tie-breaking. Tests that index `[i-1]` ≤ index `[i]` for all consecutive pairs. |
| `WhenDefaultConfigThenNoDuplicates` | Duplicate bucket entries would inflate the distribution count and confuse metrics. Asserts `Distinct().Count == Count`. |
| `WhenRatioIsOneThenOnlySquareBucket` | **Boundary test.** With `MaxAspectRatio = 1.0`, the only legal bucket is a single square at the base resolution. This validates the edge of the ratio logic: exactly one bucket, and it's square. |
| `WhenSmallBaseResolutionThenFewerBuckets` | When training at 512px instead of 1024px, fewer distinct resolutions fit in the `[min, max]` range, so the bucket count should be ≤ the 1024px count. Ensures the generator scales down correctly. |
| `WhenLargerStepThenFewerBuckets` | Step=128 skips every other resolution that step=64 would include. Validates that coarser steps produce fewer (but still valid) buckets. |
| `WhenNullConfigThenThrows` | **Guard clause.** Passing `null` to `GenerateBuckets` must throw `ArgumentNullException` immediately, not produce a confusing `NullReferenceException` deep in the algorithm. |

### 1.2 Image Assignment

Once buckets are generated, each image must be assigned to the bucket that best matches its aspect ratio. These tests verify that orientation is respected and that degenerate inputs are handled.

| Test | Purpose |
|---|---|
| `WhenSquareImageThenAssignedToSquareBucket` | A 1024×1024 image should be matched to a bucket where `Width == Height`. If this goes wrong, the image gets needlessly cropped despite being a perfect square. |
| `WhenLandscapeImageThenAssignedToLandscapeBucket` | A 1920×1080 (roughly 16:9) image must land in a bucket where `Width > Height`. Misassignment to a portrait bucket would crop ~50% of the image. |
| `WhenPortraitImageThenAssignedToPortraitBucket` | Same logic as above but for tall images (768×1024). |
| `WhenExactBucketMatchThenReturnsExactBucket` | Takes the first bucket from the generated set and feeds it back as an image. The assignment must return that exact bucket, not a "close-enough" neighbor. This tests zero-distance matching. |
| `WhenVariousAspectRatiosThenClosestBucketIsChosen` | **Parameterized test** (Theory) with 16:9, 9:16, and 1:1 inputs. Validates that the AR-distance metric picks the correct orientation in each case. |
| `WhenEmptyBucketListThenThrows` | `FindBestBucket` with an empty list has no valid answer — it should throw `ArgumentException` rather than returning garbage. |
| `WhenNullBucketListThenThrows` | Same as above but for `null`. Tests that the guard fires before any logic runs. |

### 1.3 Fit Metrics

After assignment, the analyzer calculates how much each image must be scaled and how much is cropped to fit its bucket. These metrics drive the issue detection.

| Test | Purpose |
|---|---|
| `WhenExactMatchThenScaleIsOneAndCropIsZero` | **Ideal case baseline.** A 1024×1024 image in a 1024×1024 bucket needs no scaling and no cropping. Scale should be ≈1.0, crop ≈0. This tests the zero-error path. |
| `WhenImageSmallerThanBucketThenScaleGreaterThanOne` | A 512×512 image in a 1024×1024 bucket must be upscaled 2×. The scale factor should be > 1.0. Upscaling introduces blurriness, which is why the issue detector flags it. |
| `WhenImageLargerThanBucketThenScaleLessThanOne` | A 1024×1024 image in a 512×512 bucket is downscaled (scale < 1.0). This is usually fine for training but the metric must be correct. |
| `WhenAspectRatioMismatchThenCropIsPositive` | A 16:9 image (1920×1080) forced into a 1:1 bucket (1024×1024) requires significant cropping. The crop percentage must be > 0 — this is the signal that data is being lost. |
| `WhenAspectRatioMatchesThenCropIsNearZero` | A 2048×1536 image (4:3) into a 1024×768 bucket (also 4:3) requires only scaling, no cropping. Crop should be < 1.0 (essentially zero, within floating-point tolerance). |
| `WhenVerySmallImageThenUpscaleFactorIsHigh` | A 400×300 image into a 1024×768 bucket requires ≥2× upscaling. This is the threshold at which the issue detector raises a Critical, so the metric must be accurate. |
| `WhenZeroDimensionsThenReturnsZeros` | **Defensive edge case.** A 0×0 image (from a corrupt or unreadable file) should produce scale=0, crop=0 rather than a division-by-zero crash. |

### 1.4 Issue Detection

The analyzer scans the full assignment for problems that would hurt training quality. Each test creates a specific problematic dataset and checks that the right issue is raised at the right severity.

| Test | Purpose |
|---|---|
| `WhenImageRequiresHeavyUpscalingThenCriticalIssue` | A 400×300 image in a 1024px training setup needs heavy upscaling, introducing blur and artifacts. The analyzer must flag this as **Critical** (the most severe level), because training on blurry data teaches the model to generate blurry output. |
| `WhenImageHasHighCropThenCriticalIssue` | An ultra-wide 3000×500 image has AR=6.0, far beyond the bucket limit of 2.0. It will lose most of its content to cropping. Must be flagged as **Critical** with a message about "cropping". |
| `WhenImageHasModerateCropThenWarningOrCriticalIssue` | A 2400×600 image (AR=4.0) is bad but not as extreme. Tests that the analyzer still catches it with a crop-related issue, even if the exact severity depends on the threshold. |
| `WhenSingleImageBucketsWithBatchSizeGreaterThanOneThenWarning` | When batch_size > 1 (e.g., 4) but a bucket contains only 1 image, that image is repeated to fill the batch, overfitting on it. This test uses two wildly different ARs + batch=4, expecting a **Warning** about "1 image" per bucket. |
| `WhenDominantBucketThenWarning` | 10 square images + 1 landscape = one bucket contains >90% of all images. Training will see square images far more often, creating a composition bias. Must be flagged as **Warning** about "skewed" distribution. |
| `WhenResolutionVarianceIsHighThenWarning` | A 256×256 + 4096×4096 mix means some images are upscaled 4× while others are downscaled 4×. The wide variance is a sign the dataset wasn't curated. Expects a **Warning** about resolution "varies". |
| `WhenTooManyBucketsThenWarning` | 4 images spread across 4+ different buckets means each bucket has 1 image. With small batch sizes this is wasteful and unbalanced. Asserts the distribution has > 0 entries (sanity check). |
| `WhenCleanDatasetThenNoIssues` | **Positive control.** Five identical 1024×1024 images produce zero Critical issues. If this test starts failing, the issue detector is generating false positives. |

### 1.5 Distribution Score

The distribution score uses Shannon Evenness (0–100) to quantify how balanced the bucket distribution is. 0 = all images in one bucket or empty, 100 = perfectly even.

| Test | Purpose |
|---|---|
| `WhenSingleBucketThenScoreIsZero` | With only one bucket, there is no distribution to measure — score is 0. This is mathematically correct for Shannon Evenness (log₂(1) = 0). |
| `WhenEvenDistributionThenScoreIsHundred` | Three buckets with exactly 10 images each = maximum evenness = 100. This is the theoretical ceiling. |
| `WhenModerateSkewThenScoreIsMidRange` | An 8:2 split across 2 buckets produces a score between 0 and 100. The test doesn't check the exact value — it just validates the range, because the precise score depends on the Shannon formula. |
| `WhenEmptyDistributionThenScoreIsZero` | An empty list (no images assigned at all) should return 0, not crash. |
| `WhenNullDistributionThenThrows` | `null` input must throw `ArgumentNullException`, not produce a misleading score. |

### 1.6 Full Analysis (End-to-End)

These tests call the top-level `BucketAnalyzer.Analyze(images, config)` and verify the complete pipeline: generate buckets → assign → compute metrics → detect issues → score.

| Test | Purpose |
|---|---|
| `WhenEmptyImageListThenReturnsEmptyResult` | An empty folder is a valid (if boring) input. The result must have buckets generated but zero assignments, zero distribution entries, and zero issues. Ensures the pipeline doesn't crash on empty input. |
| `WhenSingleImageThenAssignedToOneBucket` | One image → exactly 1 assignment and 1 distribution entry. Tests the minimal non-empty case. |
| `WhenMixedImagesThenCorrectlyDistributed` | Four images with different ARs (square, square, landscape, portrait) should spread across ≥2 buckets. Verifies that diverse inputs produce a non-trivial distribution. |
| `WhenSameAspectRatioThenSingleBucket` | Three 1:1 images at different sizes (512, 1024, 2048) should all land in the same square bucket. The bucket selector matches by AR, not by resolution. |
| `WhenAssignmentsThenSortedByFileName` | Assignments must preserve insertion order (input order). If a user passes `["z_image.png", "a_image.png"]`, the output should be in that same order, not alphabetically sorted. |
| `WhenZeroDimensionImageThenSkipped` | A 0×0 image (unreadable file) is silently skipped rather than crashing the analysis. The valid image beside it is still assigned. |

---

## 2. ImageHeaderReaderTests

**What it tests:** `ImageHeaderReader` — reads image width and height by parsing raw file headers (PNG IHDR, JPEG SOF0, BMP BITMAPINFOHEADER, WebP VP8X, GIF logical screen descriptor) without loading the full bitmap into memory.

**Why it matters:** The bucket analyzer needs dimensions for thousands of images. Loading each as a full `Bitmap` would be slow and memory-heavy. The header reader is ~100× faster because it reads only the first few dozen bytes. But parsing raw binary formats is error-prone, so each format has dedicated tests.

**Test strategy:** Each test creates a minimal valid (or deliberately corrupt) binary file in a temp directory using `BinaryWriter`. This ensures tests don't depend on external sample images and can run anywhere. The test class implements `IDisposable` to clean up temp files.

### 2.1 PNG

| Test | Purpose |
|---|---|
| `WhenValidPngThenReadsDimensions` | Creates a minimal PNG with the 8-byte signature + IHDR chunk encoding 1920×1080 in big-endian. Verifies the reader correctly parses the IHDR width/height fields from the expected byte offsets. |
| `WhenInvalidPngThenReturnsInvalid` | Writes 3 random bytes with a `.png` extension. The reader must detect the invalid magic bytes and return `IsValid = false` rather than reading garbage dimensions. |

### 2.2 JPEG

| Test | Purpose |
|---|---|
| `WhenValidJpegThenReadsDimensions` | Creates a JPEG with SOI marker (0xFFD8) + SOF0 marker (0xFFC0) containing 1280×720. JPEG stores height before width in SOF0, so this also validates the field ordering. |
| `WhenInvalidJpegThenReturnsInvalid` | A 3-byte file with `.jpg` extension has no valid SOI marker. Must return `IsValid = false`. |

### 2.3 BMP

| Test | Purpose |
|---|---|
| `WhenValidBmpThenReadsDimensions` | Creates a BMP with the "BM" signature and a BITMAPINFOHEADER containing 800×600 (little-endian int32). |
| `WhenBmpNegativeHeightThenReadsAbsoluteValue` | In the BMP format, a negative height means the image is stored top-down instead of bottom-up. The actual pixel count is `|height|`. This test creates a BMP with height=-480 and verifies the reader returns 480, not -480. This is a real-world scenario — some BMP writers always use top-down format. |
| `WhenInvalidBmpThenReturnsInvalid` | A 2-byte file can't contain a valid BMP header. Must return `IsValid = false`. |

### 2.4 WebP

| Test | Purpose |
|---|---|
| `WhenValidWebPVP8XThenReadsDimensions` | Creates a WebP with RIFF container + VP8X extended chunk. VP8X stores (width-1) and (height-1) as 24-bit little-endian values — a quirk of the WebP spec. Tests that the reader adds 1 back to get 2560×1440. |
| `WhenInvalidWebPThenReturnsInvalid` | 4 random bytes with `.webp` extension. No valid RIFF header → `IsValid = false`. |

### 2.5 GIF

| Test | Purpose |
|---|---|
| `WhenValidGifThenReadsDimensions` | Creates a GIF89a with the 6-byte signature + logical screen descriptor (width and height as 16-bit LE). Reads 320×240. |

### 2.6 Edge Cases

These tests protect against common real-world scenarios that don't fit neatly into a format category.

| Test | Purpose |
|---|---|
| `WhenFileMissingThenReturnsInvalid` | The file path doesn't exist on disk. Must return `IsValid = false` rather than throwing — the caller (BucketAnalyzer) simply skips missing files. |
| `WhenEmptyFileThenReturnsInvalid` | A 0-byte `.png` file can't contain any header. Must return `IsValid = false`. |
| `WhenUnsupportedExtensionThenReturnsInvalid` | A `.tiff` file (even with valid TIFF magic bytes inside) is not a supported format. The reader dispatches by extension, so this should return `IsValid = false` immediately. |
| `WhenNullPathThenThrows` | Passing `null` is a programming error (not a user error), so it throws `ArgumentNullException` rather than returning `IsValid = false`. |

---

## 3. AnalysisPipelineTests

**What it tests:** `AnalysisPipeline` — the orchestrator that loads captions from a folder, runs each registered `IDatasetCheck` in priority order, collects their issues, sorts them by severity, and builds a summary report.

**Why it matters:** The pipeline is the single entry point for the entire caption quality system. If it runs checks in the wrong order, skips applicable checks, includes inapplicable ones, or miscounts fixable issues, the user gets a misleading or incomplete report.

**Test strategy:** Uses a real temp folder with stub `.txt` + `.png` files (so `CaptionLoader` works without mocking), but the actual `IDatasetCheck` implementations are Moq mocks. This isolates pipeline logic from check logic — if a check test fails, only the check's test class fails, not the pipeline tests.

| Test | Purpose |
|---|---|
| `WhenNoChecksRegisteredThenReturnsEmptyReport` | With no checks at all, the pipeline should still run successfully: load captions, produce an empty issue list, and report `ChecksRun = 0`. This is the baseline for the "everything works, nothing to report" path. The summary must still correctly count files (1 caption, 1 image). |
| `WhenCheckIsNotApplicableThenItIsSkipped` | A check registered for `LoraType.Style` should not be invoked when the user is analyzing a `Character` dataset. The test verifies the check's `Run` method is **never called** (`Times.Never`), and `ChecksRun` remains 0. This prevents false positives from inapplicable checks. |
| `WhenCheckIsApplicableThenItIsExecuted` | The inverse of the above: a `Character` check running against a `Character` dataset must be invoked. The mock returns a single Warning issue, and the test verifies it appears in the report with `ChecksRun = 1`. |
| `WhenMultipleChecksExistThenTheyRunInOrder` | Two checks registered out of order (order=10 first, order=5 second). The pipeline must sort by `Order` and run order=5 first. A callback records the call sequence, and the assertion checks `["Second", "First"]`. This matters because some checks depend on earlier checks having run (e.g., FormatCheck cleans up before TriggerCheck runs). |
| `WhenIssuesHaveDifferentSeveritiesThenTheyAreSortedCriticalFirst` | A single check returns Info, Critical, and Warning (in that random order). The pipeline's final issue list must be sorted Critical → Warning → Info. The user should see the most severe problems first. |
| `WhenConfigIsNullThenThrowsArgumentNullException` | Calling `Analyze(null)` is a programming error. The pipeline must fail fast with `ArgumentNullException` rather than crashing mid-analysis. |
| `WhenSummaryIsBuiltThenFixableCountIsCorrect` | The report summary includes `FixableIssueCount` — the number of issues that have at least one `FixSuggestion` with non-empty `Edits`. This test creates one fixable and one non-fixable issue and asserts the count is exactly 1. |

---

## 4. CaptionLoaderTests

**What it tests:** `CaptionLoader` — scans a dataset folder for caption files (`.txt`, `.caption`) and image files (`.png`, `.jpg`, `.webp`, etc.), pairs them by base filename, and returns structured `CaptionFile` records.

**Why it matters:** Every quality check operates on the `CaptionFile` list that `CaptionLoader` produces. If the loader fails to pair a caption with its image, ignores valid files, or crashes on edge cases, the entire quality report is wrong.

**Test strategy:** Real temp folder with real files. `IDisposable` cleanup.

### 4.1 Happy Path

| Test | Purpose |
|---|---|
| `WhenFolderContainsPairedCaptionAndImageThenBothArePaired` | The most basic scenario: `photo1.png` + `photo1.txt` in the same folder. Verifies `BaseName` = "photo1", `RawText` = file contents, `PairedImagePath` ends with "photo1.png", and `imageCount` = 1. |
| `WhenCaptionHasNoMatchingImageThenPairedImagePathIsNull` | A lone `orphan.txt` with no matching image. The caption must still be loaded (so checks can analyze it), but `PairedImagePath` must be `null`. Image count must be 0. |
| `WhenFolderContainsOnlyImagesThenNoCaptionsAreReturned` | Two images, zero caption files. The loader should return an empty caption list but correctly count 2 images. This is important so the summary can report "0 captions found for 2 images." |
| `WhenMultiplePairsExistThenAllAreLoaded` | Three pairs using different caption extensions (`.txt`, `.txt`, `.caption`). All 3 must be paired. Tests that the loader supports multiple caption extensions. |
| `WhenFolderIsEmptyThenReturnsEmptyResults` | An empty directory is valid input. Returns 0 captions, 0 images. Must not throw. |

### 4.2 Edge Cases & Validation

| Test | Purpose |
|---|---|
| `WhenFolderPathIsEmptyThenThrowsArgumentException` | Empty string is never a valid folder path. Must throw immediately. |
| `WhenFolderPathIsNullThenThrowsArgumentException` | Same as above, for `null`. |
| `WhenFolderDoesNotExistThenThrowsDirectoryNotFoundException` | A path that doesn't exist on disk. The error message should be clear — it's a configuration problem ("you pointed the tool at a folder that doesn't exist"). |
| `WhenNonMediaFilesExistThenTheyAreIgnored` | `.md` and `.json` files must not be counted as images. Only the `.png` counts. |

### 4.3 DetectCaptionStyle

These tests delegate to `TextHelpers.DetectCaptionStyle` but are included here because the loader sets `DetectedStyle` on each caption.

| Test | Purpose |
|---|---|
| `WhenTextIsNaturalLanguageThenReturnsNaturalLanguage` | A full sentence ("A woman with brown hair...") → `NaturalLanguage`. |
| `WhenTextIsBooruTagsThenReturnsBooruTags` | Comma-separated tags ("1girl, brown hair, blue dress") → `BooruTags`. |
| `WhenTextIsEmptyThenReturnsUnknown` | Empty string can't be classified. |
| `WhenTextIsWhitespaceThenReturnsUnknown` | Whitespace-only is effectively empty. |

---

## 5. FormatConsistencyCheckTests

**What it tests:** `FormatConsistencyCheck` (pipeline order=1) — the first check to run. It catches three categories of formatting problems that make other checks unreliable: empty/near-empty captions, mixed captioning styles (booru tags vs natural language), and caption length outliers.

**Why it runs first:** If a caption is empty, the trigger word check will false-positive ("trigger missing!"). If styles are mixed, the synonym check's text parsing will misbehave. By catching format problems first, the user can fix the basics before looking at more nuanced issues.

### 5.1 Applicability & Metadata

| Test | Purpose |
|---|---|
| `WhenAnyLoraTypeThenIsApplicable` | Format consistency matters regardless of whether you're training a Character, Concept, or Style LoRA. Parameterized over all three types. |
| `WhenCheckMetadataInspectedThenOrderIsOne` | Confirms `Order = 1`, `Domain = Caption`, and that `Name`/`Description` are non-empty (they're shown in the UI report). |

### 5.2 Empty / Near-Empty Captions

These tests validate the most basic data quality gate: captions that contain no useful information.

| Test | Purpose |
|---|---|
| `WhenCaptionIsEmptyThenReportsCritical` | A completely empty string (`""`) is useless for training. The check must flag it as **Critical** (not Warning or Info) because training on it teaches the model nothing while consuming a training slot. |
| `WhenCaptionIsWhitespaceOnlyThenReportsCritical` | `"   \t  "` looks non-empty to a naive `string.Length` check but contains no information. Must be caught the same as a truly empty caption. |
| `WhenCaptionHasOneWordThenReportsCriticalNearEmpty` | `"woman"` — a single word provides almost no guidance to the model. Flagged as Critical with the word count shown relative to the `NearEmptyWordThreshold`. |
| `WhenCaptionHasTwoWordsThenReportsCriticalNearEmpty` | `"brown hair"` — still below threshold. |
| `WhenCaptionHasThreeWordsThenNoEmptyIssue` | `"a brown haired"` — at or above the 3-word threshold, this is considered "enough" content and should NOT trigger an empty/near-empty Critical. Tests the boundary. |
| `WhenMultipleEmptyCaptionsExistThenSingleIssueWithAllFiles` | Two empty captions + one normal. The check should consolidate into **one** issue (not two) with both empty files listed in `AffectedFiles`. This avoids flooding the report with duplicate issues. |

### 5.3 Mixed Styles

LoRA datasets should use a consistent captioning style. Mixing booru tags (`"1girl, brown hair, standing"`) with natural language (`"A woman standing in a park"`) confuses the model because the same concepts are represented differently.

| Test | Purpose |
|---|---|
| `WhenAllCaptionsAreNaturalLanguageThenNoMixedStyleIssue` | **Positive control.** Uniform NL style → no "Mixed" issue. |
| `WhenAllCaptionsAreBooruTagsThenNoMixedStyleIssue` | **Positive control.** Uniform booru style → no "Mixed" issue. |
| `WhenCaptionsAreMixedStyleThenReportsWarning` | 2 NL + 1 booru → **Warning** (not Critical, because it's fixable). The message must include counts ("2 natural language, 1 booru tags") so the user knows the split. |
| `WhenMixedStyleDetectedThenAffectedFilesAreMinority` | 3 NL + 1 booru → the *minority* style (booru) is flagged. The affected file is `d.txt` (the booru one), because that's the file the user should convert to match the majority. |
| `WhenUnknownAndMixedStylesExistThenTheyAreIgnoredForMixedCheck` | Captions detected as `Unknown` or `Mixed` style are ambiguous — they shouldn't trigger a mixed-style warning when there's only one definitive style present. |

### 5.4 Length Outliers

A caption that's 10× longer than all others is suspicious — it might be a batch-processing artifact or a copy/paste error.

| Test | Purpose |
|---|---|
| `WhenAllCaptionsAreSimilarLengthThenNoOutlierIssue` | Similar-length captions = no statistical outlier. |
| `WhenOneCaptionIsMuchLongerThenReportsWarning` | 8 normal captions + 1 that's 100 words long. The outlier detection uses a ≥2σ threshold. The test uses 8 normals (not 3) to ensure the single long caption is a true statistical outlier, not masked by a small sample. |
| `WhenFewerThanThreeCaptionsThenNoOutlierCheck` | With only 2 captions, standard deviation is unreliable. The check skips outlier analysis entirely. |
| `WhenOutlierIsAlsoNearEmptyThenItIsNotDoubleFlagged` | A 2-word caption among normal ones is both near-empty (Critical) AND a length outlier (Warning). But double-flagging the same file is confusing — the near-empty Critical is more actionable, so the outlier check must exclude already-flagged files. |

### 5.5 Edge Cases & CountWords Helper

| Test | Purpose |
|---|---|
| `WhenNoCaptionsThenReturnsEmpty` | Empty list → no issues (not a crash). |
| `WhenCaptionsIsNullThenThrowsArgumentNullException` | `null` input → fail fast. |
| `WhenConfigIsNullThenThrowsArgumentNullException` | `null` config → fail fast. |
| `WhenCountingWordsThenReturnsCorrectCount` | Parameterized test verifying `CountWords`: empty→0, whitespace→0, single word→1, two words→2, extra whitespace→3 (not 5), booru string with commas→5 (splits on whitespace, commas count). |

---

## 6. TriggerWordCheckTests

**What it tests:** `TriggerWordCheck` (pipeline order=2) — verifies that the configured trigger word (e.g., `"ohwx"`) appears in every caption, with correct casing, in a consistent position, and not duplicated.

**Why it matters:** The trigger word is what activates the LoRA at inference time. If it's missing from a training caption, that image trains the model without associating it with the trigger — causing the LoRA to "leak" into all generations. If the casing varies, the model learns multiple activation tokens instead of one.

### 6.1 Applicability

| Test | Purpose |
|---|---|
| `WhenCharacterOrConceptThenIsApplicable` | Character and Concept LoRAs require trigger words to activate. |
| `WhenStyleThenIsNotApplicable` | Style LoRAs typically don't use trigger words — the style is activated by other means. The check must **not** run for Style datasets. |
| `WhenCheckMetadataInspectedThenOrderIsTwo` | Runs after FormatConsistencyCheck (order=1) so empty captions are already flagged. |

### 6.2 Missing Trigger Word

| Test | Purpose |
|---|---|
| `WhenTriggerMissingFromCaptionThenReportsCritical` | A caption without the trigger word `"ohwx"` → **Critical**. This is the most damaging mistake in a LoRA dataset. |
| `WhenTriggerMissingThenFixSuggestionPrependsTrigger` | The auto-fix must prepend the trigger to the caption's start. The fix's `NewText` must begin with `"ohwx"`. |
| `WhenTriggerPresentThenNoMissingIssue` | **Positive control.** Caption already contains the trigger → no "missing" issue. |
| `WhenMultipleCaptionsMissingTriggerThenSingleIssueWithAllFiles` | 2 of 3 captions are missing the trigger. They're consolidated into 1 issue with 2 affected files and 2 fix edits (one per file). |
| `WhenTriggerMissingFromBooruCaptionThenFixPrependsBooruStyle` | For booru captions, the fix must use comma formatting: `"ohwx, 1girl, brown hair, blue eyes"` (not `"ohwx 1girl, brown hair, blue eyes"`). |
| `WhenTriggerMissingFromNlCaptionThenFixPrependsWithSpace` | For NL captions, the fix uses space formatting: `"ohwx A woman standing in a park."` (not `"ohwx, A woman..."`). |

### 6.3 Case Mismatch

| Test | Purpose |
|---|---|
| `WhenTriggerHasWrongCaseThenReportsCritical` | `"OHWX"` instead of `"ohwx"` → **Critical**. The model treats these as different tokens, so the LoRA won't activate reliably. |
| `WhenCaseMismatchThenFixCorrectsCasing` | The auto-fix replaces `"OHWX"` with the correct `"ohwx"` in the caption text. |
| `WhenCaseMismatchInBooruTagsThenFixCorrectsCasing` | Same fix but for booru-formatted captions — the tag must be replaced while preserving surrounding commas. |
| `WhenExactCaseMatchExistsThenNoCaseMismatchIssue` | **Positive control.** Correct casing → no issue. |
| `WhenBothExactAndWrongCaseExistThenNoMismatchForExactFile` | One file has `"ohwx"` (correct), another has `"OHWX"` (wrong). Only the wrong-cased file should be in `AffectedFiles`. The correct file must not be flagged. |

### 6.4 Position Inconsistency

Most LoRA trainers recommend putting the trigger word at the same position (usually first) in every caption, so the model consistently associates position with activation.

| Test | Purpose |
|---|---|
| `WhenTriggerAtSamePositionInAllCaptionsThenNoPositionIssue` | All captions start with `"ohwx"` → no position issue. |
| `WhenMostTriggerAtPosition0ButSomeElsewhereThenReportsInfo` | 3 captions have `"ohwx"` first, 1 has it mid-sentence. Flagged as **Info** (not Critical or Warning) because inconsistency is a mild concern, not a showstopper. |
| `WhenPositionInconsistentThenAffectedFilesAreMinority` | Only the minority file (the one with the trigger in an unusual position) is flagged. |
| `WhenBooruTriggerPositionVariesThenReportsInfo` | In booru tags, "position" means tag index (0th tag, 1st tag, etc.). 2 files have trigger as tag-0, 1 file has it as tag-1. Flagged as Info. |

### 6.5 Duplicate Trigger

Repeating the trigger word in a caption can overweight it during training, causing the model to require the trigger word to be said multiple times at inference.

| Test | Purpose |
|---|---|
| `WhenTriggerAppearsOnceThenNoDuplicateIssue` | Normal case: trigger appears exactly once. |
| `WhenTriggerAppearsTwiceThenReportsWarning` | `"ohwx a woman ohwx in a park."` — trigger appears twice. **Warning** severity. |
| `WhenDuplicateTriggerInBooruTagsThenReportsWarning` | `"ohwx, 1girl, ohwx, brown hair"` — same problem in booru format. |
| `WhenDuplicateWithMixedCaseThenAlsoCountsAsMultiple` | `"ohwx a woman OHWX in a park."` — `"ohwx"` and `"OHWX"` are counted as two occurrences of the trigger (case-insensitive matching). |

### 6.6 Edge Cases

| Test | Purpose |
|---|---|
| `WhenNoCaptionsThenReturnsEmpty` | Empty caption list → graceful empty result. |
| `WhenTriggerWordIsNullThenReturnsEmpty` | If the user hasn't configured a trigger word, the check should silently skip. |
| `WhenTriggerWordIsEmptyThenReturnsEmpty` | Same for empty string. |
| `WhenTriggerWordIsWhitespaceThenReturnsEmpty` | Same for whitespace-only. |
| `WhenCaptionsIsNullThenThrowsArgumentNullException` | Programming error → throw. |
| `WhenConfigIsNullThenThrowsArgumentNullException` | Programming error → throw. |
| `WhenCaptionIsEmptyStringThenTriggerIsMissing` | An empty caption definitely doesn't contain the trigger. Must report "missing". |
| `WhenTriggerIsSubstringOfWordThenNotCountedAsPresent` | `"xohwxyz"` should NOT match `"ohwx"`. The check uses word-boundary matching, not simple `string.Contains`. This prevents false negatives where the trigger happens to be a substring of another word. |
| `WhenAllCaptionsHaveTriggerCorrectlyThenNoIssues` | **Comprehensive positive control.** 3 captions, all correct → zero issues total (no missing, no casing, no position, no duplicate). |

### 6.7 Helper Methods

| Test | Purpose |
|---|---|
| `WhenTokenizingThenReturnsExpectedCount` | `Tokenize` must split NL by whitespace and booru by comma. Parameterized over 4 cases. |
| `WhenTokenizingBooruTagsThenSplitsByComma` | `"ohwx, 1girl, brown hair"` → `["ohwx", "1girl", "brown hair"]` (3 tokens, trimmed). |
| `WhenTokenizingNlTextThenSplitsByWhitespace` | `"ohwx a woman"` → `["ohwx", "a", "woman"]` (3 tokens). |
| `WhenPrependToBooruThenCommaFormatting` | Verifies the comma-separated prepend for booru captions. |
| `WhenPrependToNlThenSpaceFormatting` | Verifies the space-separated prepend for NL captions. |
| `WhenPrependToEmptyThenReturnsTriggerOnly` | Prepending to `""` returns just the trigger word. |
| `WhenReplacingCaseMismatchInNlThenCorrectsCasing` | `"OHWX a woman..."` → `"ohwx a woman..."`. |
| `WhenReplacingCaseMismatchInBooruThenCorrectsCasing` | Same for booru tags. |

---

## 7. SynonymConsistencyCheckTests

**What it tests:** `SynonymConsistencyCheck` (pipeline order=3) — detects when different captions use different words for the same concept (e.g., "car" vs "automobile" vs "vehicle"). This inconsistency confuses the model during training.

**Why it matters:** If half your captions say "woman" and half say "lady," the model learns two separate concepts instead of one. The LoRA becomes unreliable — sometimes it responds to "woman," sometimes to "lady," and the quality drops.

### 7.1 No Conflicts

| Test | Purpose |
|---|---|
| `WhenNoCaptionsThenReturnsEmpty` | Empty input → empty output, no crash. |
| `WhenCaptionsUseNoSynonymsThenReturnsEmpty` | Captions about sunsets and mountains contain no terms from any synonym group. No issues expected. |
| `WhenAllCaptionsUseSameSynonymThenNoConflict` | All 3 captions use "woman" — there's no conflict because only one synonym from the group is used. |

### 7.2 Synonym Conflicts Detected

| Test | Purpose |
|---|---|
| `WhenTwoSynonymsUsedThenReportsWarning` | "woman" in one caption, "lady" in another → **Warning** because they belong to the same synonym group. |
| `WhenThreeSynonymsUsedThenAllAppearInMessage` | "car" + "automobile" + "vehicle" → the warning message must list all three variants so the user knows the full scope of the inconsistency. |
| `WhenConflictDetectedThenAffectedFilesContainsAllInvolvedFiles` | Both the "car" file and the "automobile" file are listed in `AffectedFiles`. A third file (about the sky, no synonyms) is excluded. |
| `WhenConflictDetectedThenMostUsedTermIsListedFirst` | If "car" appears 3× and "automobile" 1×, the message shows "car" first. This tells the user which term is the majority (and therefore the one they probably want to keep). |
| `WhenBooruTagsHaveSynonymConflictThenDetected` | Same detection logic works for booru-style tags (`"1girl, car, blue sky"` vs `"1girl, automobile, sunset"`). |
| `WhenUsageCountsShownThenFormatIsCorrect` | The message format must be `"car"(2x)` and `"automobile"(1x)` — quoted term + usage count in parentheses. |

### 7.3 Fix Suggestions

Each synonym conflict produces N fix suggestions (one per synonym variant), so the user can choose which term to standardize on.

| Test | Purpose |
|---|---|
| `WhenTwoSynonymsUsedThenTwoFixSuggestionsGenerated` | "car" vs "automobile" → 2 fixes: "Standardize to car" and "Standardize to automobile". |
| `WhenFixTargetsCarThenAutomobileFileIsEdited` | Choosing "Standardize to car" means the file containing "automobile" gets edited → `b.txt` has its text replaced. |
| `WhenFixTargetsAutomobileThenCarFileIsEdited` | The reverse: choosing "automobile" edits the "car" file. |
| `WhenThreeSynonymsUsedThenThreeFixSuggestions` | 3 variants → 3 fix options. |
| `WhenFixAppliedToMajorityTermThenOnlyMinorityFilesHaveEdits` | If "car" appears 2× and "automobile" 1×, standardizing to "car" only edits the 1 automobile file. The 2 car files are untouched. |
| `WhenBooruFixAppliedThenTagsAreReplacedCorrectly` | In booru format, `"1girl, automobile, sunset"` → `"1girl, car, sunset"`. The tag is replaced exactly, not as a substring. |
| `WhenFixEditGeneratedThenOriginalTextMatchesRawCaption` | Every `FileEdit.OriginalText` must match the raw caption text verbatim. This is critical because `FixApplier` uses exact string matching — if the original text doesn't match the file content, the fix fails. |

### 7.4 Edge Cases & Helpers

| Test | Purpose |
|---|---|
| `WhenSameTermAppearsInMultipleCaptionsThenCountedCorrectly` | "car" in 3 captions → count = 3×, not 1×. |
| `WhenMultipleGroupsConflictThenMultipleIssuesReported` | "woman"/"lady" + "car"/"automobile" → 2 separate issues (one per synonym group). |
| `WhenOnlySingleTermFromGroupFoundThenNoConflict` | All files use "car" → no conflict (you need ≥2 different terms from the same group). |
| `WhenReplacingInNlTextThenUsesWordBoundary` | `ReplaceTerm` uses word boundaries so "car" in "scar" is not replaced. |
| `WhenReplacingInBooruTagsThenReplacesExactTag` | `"1girl, car, blue sky"` → `"1girl, automobile, blue sky"`. The comma-delimited tag is matched exactly. |
| `WhenGroupHasNoMatchesThenUsedTermsIsEmpty` | `AnalyzeGroup` with made-up terms that appear in no caption → empty result. |

---

## 8. FeatureConsistencyCheckTests

**What it tests:** `FeatureConsistencyCheck` (pipeline order=4) — detects known visual features (from a curated dictionary of hair colors, eye colors, poses, etc.) that appear in *almost* all captions but not quite all. It also discovers high-frequency n-grams that aren't in the dictionary.

**Why it matters:** If "blue eyes" appears in 4 out of 5 captions, the model will strongly associate the LoRA with blue eyes — but the 1 caption without it creates confusion during training. The user should either add "blue eyes" to the missing caption (consistency) or remove it from all captions (bake it into the trigger word instead).

### 8.1 Known Features — Near-Constant (80–99%)

The "danger zone": a feature present in most but not all captions.

| Test | Purpose |
|---|---|
| `WhenKnownFeatureIn80PercentThenReportsCritical` | "blue eyes" in 4/5 captions (80%) → **Critical**. The message includes "4/5" so the user sees the exact ratio. |
| `WhenKnownFeatureIn90PercentThenReportsCritical` | "brown hair" in 9/10 (90%) → same Critical severity. Tests that the threshold applies consistently across ratios. |
| `WhenNearConstantThenFixSuggestionToAddToMissing` | The "Add" fix suggests appending "blue eyes" to the one caption missing it (`e.txt`). The edit targets exactly that file. |
| `WhenNearConstantThenFixSuggestionToRemoveFromAll` | The "Remove" fix suggests deleting "blue eyes" from all 4 captions that have it. Produces 4 edit operations. |
| `WhenNearConstantThenAffectedFilesAreMissing` | `AffectedFiles` contains the files *missing* the feature — because those are the files the user needs to act on. |
| `WhenNearConstantThenMessageShowsCategory` | The message includes the feature category (`"eye_color"`) so the user understands what type of feature is causing the problem. |
| `WhenBooruNearConstantThenDetectedCorrectly` | The same detection works for booru tags (`"ohwx, 1girl, blue eyes, park"`) — the check uses style-aware feature matching. |

### 8.2 Known Features — Fully Covered (100%)

A feature in 100% of captions is not a "near-constant" problem — it's a "bake it into the trigger" opportunity.

| Test | Purpose |
|---|---|
| `WhenKnownFeatureIn100PercentThenReportsInfo` | "blue eyes" in 3/3 (100%) → **Info** severity (not Critical). It's not a training problem per se, but the user might want to remove it and bake it into the trigger word. |
| `WhenFullyCoveredThenFixSuggestionToRemove` | A single "Remove" fix with edits for all 3 files. |
| `WhenFullyCoveredThenMessageSuggestsBaking` | The message says "bake into trigger" — suggesting the user can add "blue eyes" to the trigger definition and remove it from all captions. |

### 8.3 Known Features — Not Flagged

| Test | Purpose |
|---|---|
| `WhenFeatureInLessThan80PercentThenNoIssue` | "blue eyes" in 2/5 (40%) is fine — it appears in a varied mix, which is good for training diversity. |
| `WhenFeatureInZeroPercentThenNoIssue` | Feature not present at all → nothing to report. |

### 8.4 Discovered N-grams

Besides the curated dictionary, the check also extracts bigrams and trigrams from the captions and flags any that appear in 80–99%.

| Test | Purpose |
|---|---|
| `WhenDiscoveredNgramIn80PercentThenReportsWarning` | "iron gate" appears in 4/5 captions — it's not a known feature, but it's suspiciously consistent. Flagged as **Warning** (not Critical) because it might be intentional. |
| `WhenNgramIsAlsoKnownFeatureThenNotDuplicateWarning` | If "blue eyes" is already flagged as a known-feature Critical, it should NOT also appear as an n-gram Warning. Only 1 issue per feature. |
| `WhenNgramInLessThan80PercentThenNoWarning` | "iron gate" in 2/5 (40%) → no warning. |
| `WhenNgramIn100PercentThenNotFlaggedAsWarning` | 100% is handled by the "fully covered" logic (Info), not the 80–99% n-gram logic (Warning). |
| `WhenDiscoveredNgramThenAffectedFilesAreMissingOnes` | Same as known features: affected files = the ones *missing* the n-gram. |

### 8.5 Edge Cases

| Test | Purpose |
|---|---|
| `WhenFewerThanMinCaptionsThenReturnsEmpty` | With only 2 captions, percentage-based analysis is unreliable. Skips entirely. |
| `WhenCaptionsAreEmptyStringsThenDoesNotThrow` | 3 empty captions → no crash (the format check already flagged them). |
| `WhenMultipleKnownFeaturesNearConstantThenMultipleIssues` | "blue eyes" and "brown hair" both at 80% → 2 separate Critical issues. |
| `WhenFindingContainingThenReturnsCorrectIndices` | `FindCaptionsContaining` helper: given 3 captions where 2 contain "blue eyes", returns indices `[0, 2]`. |

---

## 9. TypeSpecificCheckTests

**What it tests:** `TypeSpecificCheck` (pipeline order=5) — runs quality checks that only make sense for a specific LoRA type: clothing/pose diversity for Characters, viewpoint diversity for Concepts, and style-leak word detection for Styles.

**Why it matters:** Different LoRA types have different failure modes. A Character LoRA where every image shows the character in the same "standing" pose and "shirt" will produce a model that can only generate that exact pose/outfit. A Style LoRA that includes "masterpiece" or "anime" in captions will bake those quality/style tags into the LoRA, causing them to appear in every generation.

### 9.1 Guard Clauses

| Test | Purpose |
|---|---|
| `WhenAnyLoraTypeThenIsApplicable` | The check is applicable for all types — it internally dispatches to type-specific logic. |
| `WhenFewerThanMinCaptionsThenReturnsEmpty` | With < 3 captions, diversity analysis is meaningless. Returns empty. |

### 9.2 Character — Clothing Diversity

| Test | Purpose |
|---|---|
| `WhenCharacterHasSameClothingThenWarns` | Every caption mentions "shirt" and nothing else → **Warning** about clothing diversity. The model will learn "this character always wears a shirt" and struggle to generate other outfits. |
| `WhenCharacterHasVariedClothingThenNoWarning` | shirt/dress/hoodie/jacket → good diversity, no warning. |
| `WhenCharacterClothingWarningThenAffectedFilesIncludeAll` | All files are affected (they all contribute to the lack of diversity). |
| `WhenCharacterHasTwoClothingItemsThenNoWarning` | Even 2 different items (shirt + dress) provides some diversity — no warning. |
| `WhenCharacterBooruSameClothingThenWarns` | Same detection works for booru tags like `"1girl, shirt, park"`. |

### 9.3 Character — Pose Diversity

Same principle as clothing, applied to pose terms (standing, sitting, kneeling, etc.).

| Test | Purpose |
|---|---|
| `WhenCharacterHasSamePoseThenWarns` | All "standing" → Warning. |
| `WhenCharacterHasVariedPosesThenNoWarning` | standing/sitting/kneeling/walking → no warning. |
| `WhenCharacterHasBothClothingAndPoseIssuesThenBothWarned` | Same shirt + same standing in every caption → **two** separate Warnings (clothing and pose). They're independent problems. |
| `WhenCharacterHasNoPoseTermsThenNoWarning` | If no recognized pose terms appear, the check can't assess diversity and silently skips. |

### 9.4 Concept — Viewpoint Diversity

For Concept LoRAs (e.g., a magic circle, a specific building), variety of camera angles helps the model generalize.

| Test | Purpose |
|---|---|
| `WhenConceptHasSameAngleThenWarns` | All "close-up" → Warning about viewpoint diversity. |
| `WhenConceptHasVariedAnglesThenNoWarning` | close-up/wide/low angle/medium → no warning. |
| `WhenConceptHasNoAngleTermsThenNoWarning` | No angle terms found → skip. |
| `WhenConceptDoesNotCheckClothingThenNoClothingIssue` | Concept LoRAs don't care about clothing (there's no character wearing clothes). Even if "shirt" appears in every caption, no clothing warning is raised. |

### 9.5 Style — Style-Leak Words

Style LoRAs train the model to reproduce a visual style (oil painting, watercolor, anime, etc.). If captions contain quality/style tags like "masterpiece" or "anime," the LoRA bakes those into its weights. At inference time, every image it generates will carry those tags' effects, even when the user doesn't want them.

| Test | Purpose |
|---|---|
| `WhenStyleHasLeakWordThenCritical` | "masterpiece" in captions → **Critical** with "Style-leak" in the message. This is the most damaging issue for style LoRAs. |
| `WhenStyleHasLeakWordThenFixSuggestionRemoves` | The auto-fix removes "masterpiece" from all affected files. Verifies the fix has the correct number of edits and a "Remove" description. |
| `WhenStyleHasMultipleLeakWordsThenMultipleCriticals` | "masterpiece" + "illustration" → 2 separate Critical issues. Each leak word gets its own issue so the user can address them independently. |
| `WhenStyleHasNoLeakWordsThenNoCritical` | **Positive control.** Clean captions → no style-leak Critical. |
| `WhenStyleBooruHasLeakTagThenCritical` | Booru tags like `"1girl, cat ears, masterpiece, best quality"` → "masterpiece" detected. |
| `WhenStyleLeakWordThenAffectedFilesCorrect` | "watercolor" appears in files a.txt and c.txt but not b.txt → affected files are exactly [a.txt, c.txt]. |

### 9.6 Style — Content Diversity

Style LoRAs need diverse *subjects* (cats, dogs, landscapes, people) to teach the model that the style applies to anything, not just one subject.

| Test | Purpose |
|---|---|
| `WhenStyleHasLowContentDiversityThenWarns` | 5 identical captions ("a cat sitting on a table") → **Warning**. The model will learn "style = cats on tables" instead of a general style. |
| `WhenStyleHasHighContentDiversityThenNoWarning` | 5 captions about different subjects (cat, dog, bird, mountain, city) → good diversity, no warning. |

---

## 10. FixApplierTests

**What it tests:** `FixApplier` — the engine that applies auto-fix suggestions to disk by performing string replacements in caption files. This is the only part of the quality system that *writes* to the user's files, so it must be reliable and safe.

**Test strategy:** Uses a real temp folder with real files to test actual I/O. `IDisposable` cleanup.

### 10.1 Happy Path

| Test | Purpose |
|---|---|
| `WhenEditMatchesThenFileIsUpdated` | The simplest case: a file contains `"1girl, bad tag, blue eyes"`, the edit replaces `"bad tag, "` with `""`, and the file on disk now reads `"1girl, blue eyes"`. Verifies the complete round-trip (read → find → replace → write). |
| `WhenBackupEnabledThenBakFileIsCreated` | With `createBackup: true`, the original file content is saved to a `.bak` file before the edit is applied. Tests that the backup exists, is readable, and contains the original text. This is a safety net for users who want to undo fixes. |
| `WhenBackupDisabledThenNoBakFileIsCreated` | With `createBackup: false`, no `.bak` file is created. Verifies the flag actually controls behavior. |
| `WhenMultipleEditsExistThenAllAreApplied` | A single `FixSuggestion` can contain edits for multiple files. This test has 2 edits across 2 files and verifies both are updated. |

### 10.2 Error Cases

| Test | Purpose |
|---|---|
| `WhenFileDoesNotExistThenReturnsFailure` | The file was deleted between analysis and fix application. Returns `Success = false` with "not found" message rather than throwing. |
| `WhenOriginalTextNotFoundThenReturnsFailure` | The file was edited by the user (or another tool) between analysis and fix application, so the expected text no longer matches. Returns `Success = false` with "changed since analysis" — this prevents the fixer from blindly corrupting a manually-edited file. |
| `WhenSuggestionIsNullThenThrowsArgumentNullException` | Programming error → throw. |

### 10.3 ReplaceFirst Helper

| Test | Purpose |
|---|---|
| `WhenMultipleOccurrencesExistThenOnlyFirstIsReplaced` | `"cat, cat, dog"` with replace("cat", "bird") → `"bird, cat, dog"`. Only the first occurrence is replaced — this prevents unintended edits when the same text appears multiple times. |
| `WhenNoOccurrenceExistsThenSourceIsReturnedUnchanged` | No match → original string returned unchanged. |

---

## 11. TextHelpersTests

**What it tests:** `TextHelpers` — the shared utility functions used by every quality check: style detection, phrase matching, feature detection, tokenization, n-gram extraction, and phrase removal. These are the foundation that all checks build on.

### 11.1 DetectCaptionStyle

Determines whether a caption is written as natural language, booru-style tags, or something ambiguous.

| Test | Purpose |
|---|---|
| `WhenTextIsNaturalLanguageSentenceThenReturnsNaturalLanguage` | A full sentence with articles, verbs, and punctuation → `NaturalLanguage`. |
| `WhenTextIsBooruCommaSeparatedTagsThenReturnsBooruTags` | Short comma-separated terms ("1girl, brown hair, blue dress") → `BooruTags`. |
| `WhenTextContainsUnderscoreTokensThenReturnsBooruTags` | Underscore-joined tokens like `brown_hair` and `blue_eyes` are a strong signal of Danbooru-style tags. |
| `WhenTextIsEmptyThenReturnsUnknown` | Can't classify nothing. |
| `WhenTextIsWhitespaceThenReturnsUnknown` | Same as empty. |
| `WhenTextIsNullThenReturnsUnknown` | Defensive: `null` → Unknown, not a crash. |
| `WhenTextHasSentencesAndHighCommaDensityThenReturnsMixed` | A hybrid caption with both sentence structure and high comma density is ambiguous. The test accepts either `Mixed` or `NaturalLanguage` — the exact classification depends on heuristic thresholds. |

### 11.2 ContainsPhrase

Word-boundary-aware phrase matching. This is critical because naive `string.Contains("car")` would match "scar", "cargo", etc.

| Test | Purpose |
|---|---|
| `WhenPhraseExistsAsWholeWordThenReturnsTrue` | "car" in "a car is parked" → true. |
| `WhenPhraseIsSubstringOfAnotherWordThenReturnsFalse` | "car" in "a **scar** on her face" → false. The word boundary prevents false positives. |
| `WhenPhraseIsAtStartOfTextThenReturnsTrue` | "car" at position 0 → true (no preceding word boundary needed). |
| `WhenPhraseIsAtEndOfTextThenReturnsTrue` | "car" at the end → true. |
| `WhenPhraseIsCaseDifferentThenReturnsTrue` | "CAR" matches "car" (case-insensitive). |
| `WhenPhraseIsMultiWordThenMatchesExactly` | "brown hair" matched as a complete phrase. |
| `WhenTextIsEmptyThenReturnsFalse` | Empty text can't contain anything. |
| `WhenPhraseIsEmptyThenReturnsFalse` | Empty phrase matches nothing. |

### 11.3 ContainsFeature

Style-aware feature detection: uses exact tag matching for booru and word-boundary matching for NL.

| Test | Purpose |
|---|---|
| `WhenStyleIsBooruAndExactTagMatchesThenReturnsTrue` | In `"1girl, brown hair, blue eyes"`, the tag "brown hair" exists as an exact comma-delimited tag. |
| `WhenStyleIsBooruAndTagIsSubstringOfAnotherThenReturnsFalse` | In `"1girl, long brown hair, blue eyes"`, "brown hair" is NOT an exact tag — "long brown hair" is. This prevents false positives when a shorter tag is a suffix of a longer one. |
| `WhenStyleIsNaturalLanguageThenUsesWordBoundary` | For NL text, falls back to word-boundary matching. |
| `WhenStyleIsUnknownThenFallsBackToWordBoundary` | Unknown style → word boundary (safest default). |

### 11.4 ExtractTokens

Extracts meaningful content words from a caption, removing stop words, punctuation, and single-character tokens.

| Test | Purpose |
|---|---|
| `WhenCaptionHasContentWordsThenReturnsLowercaseTokens` | "Woman", "Standing", "Park" → all lowercased. |
| `WhenCaptionHasStopWordsThenTheyAreRemoved` | "a", "is", "in", "the" are removed — they add no discriminative value. |
| `WhenCaptionHasUnderscoreTokensThenTheyAreSplit` | `brown_hair` → ["brown", "hair"]. Booru-style underscores are split. |
| `WhenCaptionIsEmptyThenReturnsEmptyList` | No tokens from nothing. |
| `WhenCaptionHasPunctuationThenItIsStripped` | Commas, exclamation marks → removed before tokenizing. |
| `WhenTokensAreSingleCharacterThenTheyAreFiltered` | Single characters like "b", "c" (from initials or artifacts) are noise. |

### 11.5 ExtractBigrams & ExtractTrigrams

N-gram extraction for the discovered-feature analysis.

| Test | Purpose |
|---|---|
| `WhenTokensHaveThreeItemsThenReturnsTwoBigrams` | ["brown","hair","woman"] → ["brown hair","hair woman"]. |
| `WhenTokensHaveOneItemThenReturnsEmpty` | Can't make a bigram from 1 token. |
| `WhenTokensAreEmptyThenReturnsEmpty` | Empty → empty. |
| `WhenTokensHaveFourItemsThenReturnsTwoTrigrams` | ["long","brown","hair","woman"] → ["long brown hair","brown hair woman"]. |
| `WhenTokensHaveTwoItemsThenReturnsEmpty` | Can't make a trigram from 2 tokens. |

### 11.6 RemovePhrase

Style-aware phrase removal used by auto-fixes.

| Test | Purpose |
|---|---|
| `WhenStyleIsBooruThenRemovesExactTag` | `"1girl, bad tag, blue eyes"` → `"1girl, blue eyes"`. The tag and its surrounding comma/space are cleaned up. |
| `WhenStyleIsBooruAndTagNotFoundThenReturnsOriginal` | No match → original returned unchanged. |
| `WhenStyleIsNaturalLanguageThenRemovesWithWordBoundary` | Removes the phrase using word boundaries so surrounding text isn't corrupted. |

---

## 12. DictionaryTests

**What it tests:** The static data dictionaries (`StopWords`, `SynonymGroups`, `FeatureCategories`, `StyleLeakWords`) that the quality checks use for detection. These tests verify structural invariants, not specific content — so adding new terms to a dictionary doesn't break any tests.

**Why it matters:** A broken dictionary silently degrades every check that depends on it. A synonym group with only 1 term can never detect conflicts. A feature category with empty terms matches everything. A leak word missing from the main set but present in the critical subset causes inconsistent behavior.

### 12.1 StopWords

| Test | Purpose |
|---|---|
| `StopWordsSetIsNotEmpty` | An empty stop-words set means `ExtractTokens` would return every word, including noise, degrading n-gram analysis quality. |
| `StopWordsSetIsCaseInsensitive` | "the", "THE", "The" must all be found. If the set is case-sensitive, stop words in capitalized positions (start of sentence) slip through. |
| `StopWordsContainsCommonArticles` | "a", "an", "the" are the most basic stop words. Their absence would be a clear dictionary bug. |

### 12.2 SynonymGroups

| Test | Purpose |
|---|---|
| `SynonymGroupsIsNotEmpty` | The synonym check is useless without groups. |
| `EverySynonymGroupHasAtLeastTwoTerms` | A "synonym group" with 1 term can never produce a conflict. This invariant catches accidental single-term groups. |
| `SynonymGroupsHaveNoDuplicateTermsAcrossGroups` | If "car" appears in both the "vehicles" group and a "transport" group, the check would report the same conflict twice. |
| `TermToGroupIndexCoversAllTerms` | The reverse-lookup index (term → group index) must have an entry for every term in every group. Missing entries mean the check can't find certain synonyms. |
| `TermToGroupIndexPointsBackToCorrectGroup` | Every index must point back to a group that actually contains the term. A broken index causes incorrect conflict detection. |

### 12.3 FeatureCategories

| Test | Purpose |
|---|---|
| `FeatureCategoriesIsNotEmpty` | The feature check is useless without categories. |
| `EveryCategoryHasAtLeastOneTerms` | An empty category wastes processing time and produces confusing "empty category" messages. |
| `FeatureCategoriesHaveNoEmptyTerms` | A blank/whitespace term would match every caption, producing false positives everywhere. |
| `TermToCategoryCoversAllTerms` | Reverse-lookup index must cover all terms (same principle as SynonymGroups). |
| `TermToCategoryPointsBackToValidCategory` | Every mapping must reference a real category with the term in it. |
| `CategoryNamesMatchCategoriesKeys` | The `CategoryNames` convenience property must exactly mirror the `Categories` dictionary keys. |
| `WhenExpectedCategoryExistsThenItIsPresent` | Parameterized over essential categories: `hair_color`, `eye_color`, `pose`, `clothing_upper`, `setting_outdoor`, `lighting`. These are the minimum categories needed for the type-specific checks to work. |

### 12.4 StyleLeakWords

| Test | Purpose |
|---|---|
| `StyleLeakWordsSetIsNotEmpty` | The style-leak check is useless without terms. |
| `StyleLeakWordsSetIsCaseInsensitive` | "masterpiece" and "MASTERPIECE" must both match. Booru tags are often lowercase, but NL captions may capitalize. |
| `CriticalForStyleIsSubsetOfFullSet` | The `CriticalForStyle` subset (terms that are Critical for Style LoRAs specifically) must be a subset of the main `Set`. A term in Critical but not in the main set would be inconsistent. |
| `CriticalForStyleIsNotEmpty` | The critical subset must have at least some terms — otherwise the style-leak check has no Critical-severity detection. |
| `WhenKnownStyleTermThenItIsInSet` | Parameterized smoke test for 5 well-known leak words: "masterpiece", "anime", "digital art", "oil painting", "stable diffusion". If any are missing, something went wrong with the dictionary. |
