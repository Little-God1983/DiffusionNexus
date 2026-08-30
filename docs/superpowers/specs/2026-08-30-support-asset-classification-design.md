# Support-asset classification — design

**Issue:** [#527](https://github.com/Little-God1983/DiffusionNexus/issues/527) — VAEs, CLIP encoders and upscalers are in the LoRA library and sort as LoRAs
**Date:** 2026-08-30
**Status:** approved, ready for planning

## The problem

The library scan enumerates by file extension, so a LoRA folder routinely also holds
the VAEs, text encoders, ControlNets and upscalers a workflow needs. On one real
library, 35 of 328 unidentified files were one of these — 15 VAEs, 12 CLIP / text
encoders, 3 decoders, 2 upscalers, 2 ControlNets, 1 set of LLM weights.

They are indistinguishable from LoRAs everywhere downstream because
`ModelFileSyncService.CreateModelFromFile` stamps `Type = ModelType.LORA` on every
file it discovers. Consequences:

- **Sorter** — they are filed into base-model folders, mixed in with real LoRAs.
- **LoRA Viewer** — they occupy tiles, draw thumbnail fetches and sync attempts.
- **Identification** — they permanently inflate the "could not be identified" count,
  so that number can never reach zero however good the identity chain gets.

## What already exists

A previous pass (`6b92f5d1`, `d8f6bc10`) built `SorterAssetKind` +
`SorterAssetKindClassifier` in `DiffusionNexus.UI.Services.Lora.Sorting`: a name-based
classifier whose marker tables were derived from real library file names, surfaced as
per-folder chips in the sorter's After Sorting tree.

That verdict never leaves the sorter preview. It is recomputed on every preview pass
and written nowhere, so nothing else in the application can act on it.

## Decisions

| Question (from the issue) | Decision |
|---|---|
| Exclude at discovery, or classify and keep? | **Classify and keep.** Discovery keeps finding them; the kind becomes a property of the row. |
| A bucket to sort into? | **Flat per-kind folders** — `<TargetRoot>\VAE\`, `\ControlNet\`, `\Text Encoder\`, `\Upscaler\` — siblings of the base-model folders. No base-model or category segment beneath them. |
| Cost of a false positive? | Reduced to near-nil for safetensors by reading the **weights** rather than the name. Residual risk confined to header-less `.pth` / `.ckpt`. |
| One vocabulary or two? | **One.** `ModelType` — the enum the app already uses, already carrying `VAE`, `Controlnet`, `Upscaler`. `SorterAssetKind` is deleted. |

Rejected alternatives: keeping `SorterAssetKind` and mapping it to `ModelType` at the
DB boundary (two enums meaning the same thing, plus a mapping function — the drift
`ModelFileExtensions`' own remarks were written to prevent); and adding a separate
`Model.AssetKind` column (a migration, plus two columns answering one question that
can disagree).

## §1 Vocabulary

`ModelType` gains one member, `TextEncoder`, **appended** to the enum.
`ModelConfiguration.cs:24` persists the property with `HasConversion<string>()`, so the
new member needs no migration and no member may be reordered.

`SorterAssetKind` is deleted. `SortCandidate.AssetKind` becomes `ModelType`, as does
`SortPreviewNodeViewModel`'s kind set.

Two additions in Domain, each the single definition of its rule:

- `IsSupportAsset(ModelType)` — true for `VAE`, `Controlnet`, `Upscaler`,
  `TextEncoder`. Every "is this a LoRA" test in the app reads this rather than
  restating the set.
- A display-name map covering every kind the classifier can return — `LoRA`, `VAE`,
  `ControlNet`, `Text Encoder`, `Upscaler`. For the four support kinds the **same
  string** is the sorter chip's text, the destination folder's name, and the Viewer's
  badge, so a folder and the chip on its row cannot drift apart. `LoRA` has a display
  name but no folder name: a LoRA's folder is its base model, which is a different
  question.

## §2 Detection — proof first, guess second

### The header already holds the answer

`SafetensorsHeaderReader.TryReadAsync` parses the entire header JSON and discards
everything but three `__metadata__` fields. The root object's remaining properties are
the **tensor key names**, which state what the file is far more reliably than its name.

`SafetensorsHeaderInfo` gains a bounded sample of those keys (the first 64 root
properties other than `__metadata__`). No extra I/O and no extra parse: the keys are
already in the `JsonDocument`. The cap bounds memory for a file with thousands of
tensors; 64 is ample because a container's keys are homogeneous.

### `AssetKindHeaderMap`

New, beside `BaseModelHeaderMap` in `Service/Services/Sync/Identity/`. Maps a parsed
header to a `ModelType`, or null when the keys say nothing recognizable.

| Evidence in the tensor keys | Verdict |
|---|---|
| `lora_up`, `lora_down`, `lora_A`, `lora_B`, `lora_unet`, `lora_te`, a key ending `.alpha` | `LORA` |
| `post_quant_conv`, `quant_conv`, `encoder.down.`, `decoder.up.` | `VAE` |
| `text_model.encoder.layers`, `logit_scale`, `token_embedding`, `shared.weight` | `TextEncoder` |
| `control_model.`, `controlnet_cond_embedding`, `input_hint_block` | `Controlnet` |
| none of the above | null |

There is deliberately no upscaler row: ESRGAN-family upscalers ship as `.pth` pickles
with no readable header, so claiming header detection for them would be a rule that
never fires.

### `AssetKindClassifier`

`SorterAssetKindClassifier` moves to
`Service/Services/Sync/Identity/AssetKindClassifier.cs` and returns `ModelType`. Its
marker tables are carried over unchanged — they were derived from real library names
and re-deriving them would be motion, not improvement.

### Precedence

```
1. safetensors tensor keys   (a reading of the weights)
2. file name markers          (a guess about the name)
3. ModelType.LORA             (today's default)
```

**The header wins outright.** When the keys prove `LORA`, the name is never consulted —
this is what makes a LoRA called `vae_finetune_lora` safe, and it is the answer to the
issue's question 4. The name is reached only when there is no readable header, i.e.
`.pth` / `.ckpt` pickles, where the rules that can fire are the scale-factor /
`esrgan` / `ultrasharp` markers that do not occur in LoRA names.

The asymmetry the issue asks about is therefore resolved in our favour: a real LoRA can
only be misfiled if it is a pickle whose name carries an upscaler marker.

## §3 Where the verdict is written

**Discovery.** `ModelFileSyncService.CreateModelFromFile` stops hardcoding
`Type = ModelType.LORA` and classifies instead. The method becomes async: one
header read, capped at `SafetensorsHeaderReader.MaxHeaderBytes`, per *new* file — the
same order of I/O as the 10 MB partial hash the same loop already takes.

**Identify.** `IdentifyModelStep` already reads the header for every candidate. It
re-stamps `Type` from that same read, so a row classified by name alone is corrected
once its weights are read. A Civitai `Matched` answer is authoritative and always wins.

**Backfill.** Every row in an existing library says `LORA`. A one-shot, name-only pass
runs after discovery over rows that are all of:

- `Source == DataSource.LocalFile`
- `Type == ModelType.LORA`
- sync outcome `NotIdentified` or `None`

That is exactly the cohort Civitai has already failed to identify, which is where the
support assets live. A `Matched` LoRA is never touched. The pass is idempotent and
self-terminating: a row reclassified to `VAE` no longer satisfies `Type == LORA`.

Accepted residual: a self-trained LoRA named `my_vae_test.safetensors` is flipped by
name alone, and corrected the next time `IdentifyModelStep` reads its header, because
the header proves `LORA`. A bounded, self-closing window — not a permanent mislabel.
No new column is required to track it.

## §4 Sorter

- A support asset targets `<TargetRoot>\<KindFolderName>\`. No base-model segment and
  no category segment: both describe a LoRA's provenance and neither means anything for
  a VAE.
- The kind comes from the DB row's `Type` for library files, and from the header + name
  for browsed files — `SorterMetadataResolver.IdentifyFromFileAsync` already performs
  that header read, so it returns the kind alongside the base model rather than adding
  a second pass over the same bytes.
- `UpdateNameGuessHint` excludes support assets from "*N* LoRAs could not be
  identified". They are identified; they are simply not LoRAs. This is what finally
  lets that count reach zero.
- A support asset absorbs into `SortPreviewNodeViewModel` as
  `SortPreviewIdentity.Identified`. Without this the new `VAE` row would show ✗ purely
  because a VAE has no base model — the wrong question to ask of it.
- The chip rendering is unchanged; only its vocabulary changes.

## §5 Viewer

`ModelTypeDisplay` already binds `Model.Type` into the detail panel's `Type:` line
(`ModelDetailView.axaml:306`), so that line starts telling the truth with no view change.

Added:

- A kind badge on the tile for any support asset.
- A **Support assets** toggle in the same filter surface as the base-model filter,
  applied in `LoraViewerViewModel.ApplyFilters` alongside the existing predicates.

Default **hidden**, with the status line naming the count —
`Loaded 293 models (35 support assets hidden)` — so nothing disappears silently.

## §6 Testing

TDD throughout; every behavioural claim above gets a test.

- `AssetKindHeaderMap` over real header shapes, extending the existing
  `SafetensorsFixture` to emit tensor keys.
- Precedence: a header proving `LORA` beats a file name carrying `vae`.
- Fallback: a `.pth` with no readable header is classified from its name.
- Planner: a support asset targets `<root>\VAE\` with no base-model or category
  segment; a LoRA's target is unchanged.
- Discovery: a discovered VAE lands with `Type == ModelType.VAE`.
- Backfill: reclassifies a `NotIdentified` local row, leaves a `Matched` row alone, and
  is idempotent across two runs.
- Sorter: the name-guess hint excludes support assets; a support asset does not poison
  its folder's identity mark.
- Viewer: the filter hides and shows, and the count is reported.
- Reflection guard, mirroring the existing `AllLabels` guards: every `ModelType` the
  classifier can return has a display name, and every `ModelType` for which
  `IsSupportAsset` is true also has a folder name.

## Non-goals

- Adding `ModelType` members for the rest of the Civitai taxonomy.
- Automatically re-sorting libraries that have already been sorted.
- Removing support assets from the library, or excluding them at discovery.
- Header-based detection for `.pth` / `.ckpt` pickles.
