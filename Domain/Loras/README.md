# LoRA Variant Classification & Merging

This document explains how Diffusion Nexus interprets WAN-style LoRA downloads and merges related files into a single card with selectable variants.

## Terminology

- **High / Low** – Canonical variant labels surfaced in the UI. *High* corresponds to "High Noise" WAN training runs, while *Low* corresponds to "Low Noise". These are also sometimes abbreviated as `HN`/`LN` or simply `High`/`Low`.
- **LowNoise / HighNoise** – Common suffixes present in WAN 2.2 download names (`CassHamadaWan2.2LowNoise`). They collapse to the Low/High labels.
- **L / H** – WAN sometimes publishes files that end with `_L` and `_H`. They are treated as Low/High respectively when they appear as isolated tokens or suffixes attached to other fragments.
- **WAN 2.2 naming quirks** – The WAN team frequently mixes underscores, dashes, spaces and parenthesis while keeping the same semantic parts (e.g. `WAN-2.2-I2V-Name-HIGH 14B (1).safetensors`). Our parser treats all of these separators equivalently.
- **Noise level buckets** – The `High`/`Low` variant labels also double as noise level buckets. There are no additional noise tiers at the moment; any other tokens are ignored during normalization.

## Classification Pipeline

1. **Inputs** – Every `ModelClass` exposes a `SafeTensorFileName` and a `ModelVersionName`. Both are candidates for parsing.
2. **Normalization** – The parser trims whitespace, removes known file extensions (`.safetensors`, `.pt`, `.ckpt`, `.bin`) and splits the remaining text into tokens using separators (`_`, `-`, spaces, parentheses, etc.).
3. **Variant detection** – Tokens and substrings are scanned for the catalog of known variant aliases (`HighNoise`, `High`, `HN`, `LowNoise`, `Low`, `LN`). Longest aliases are evaluated first to avoid partial matches. Embedded occurrences such as `Wan2.2HighNoise` are also detected when surrounded by non-lowercase characters.
4. **Key construction** – Variant tokens, explicit `noise` fragments and WAN version identifiers (`v1`, `epoch10`, `ver2`) are removed. Remaining tokens are normalized (variant suffixes like `ModelHN` are stripped) and concatenated in lowercase to produce the `NormalizedKey`. This key is stable across High/Low downloads so both variants group together.
5. **Fallback merge** – If the safetensor file name does not yield a label or key the parser re-runs the pipeline against `ModelVersionName` and merges missing pieces (e.g. keep the key from the file name but borrow the label from the version string).
6. **Outputs** – The classification returns the `NormalizedKey` (used for merging) and the variant label (`High` or `Low`). If no variant is found the label is `null` and the model remains a standalone card.

## Merge Rules

The merger converts a collection of raw `LoraCardSeed` objects into `LoraCardEntry` instances displayed in the UI.

1. **Eligibility** – Only seeds classified as `High` or `Low` are considered for merging. The model must also expose a `ModelId`, a `DiffusionBaseModel`, and a non-empty `NormalizedKey`.
2. **Grouping key** – Seeds sharing the same `NormalizedKey`, `ModelId`, and `DiffusionBaseModel` are grouped together. This prevents unrelated WAN downloads with similar names from merging when they belong to different models or base checkpoints.
3. **Primary selection** – The grouped entry keeps the metadata (paths, folder context) from the first seed encountered. Variants are ordered with `High` before `Low`, and ties fall back to alphabetical ordering for deterministic results.
4. **Variants** – Each grouped entry exposes an immutable list of `LoraVariantDescriptor` items. Choosing a descriptor in the UI switches to the associated `ModelClass`.
5. **Non-variants** – Seeds without a recognized variant label remain standalone `LoraCardEntry` instances with an empty `Variants` collection.

## Priority & Edge Cases

- **Mixed separators** – The tokenizer ignores whether a separator is a dash, underscore, dot, or parenthesis; only the alphanumeric fragments matter.
- **Numeric tokens** – Leading numeric tokens (e.g. download counters) are discarded unless a later token contains alphabetic characters, mirroring WAN naming habits.
- **Embedded aliases** – Strings such as `ModelHighNoiseV1` or `Model_LN` are caught via embedded matching and suffix trimming even when they are not standalone tokens.
- **Fallback precedence** – When both sources supply information, the safetensor file name wins. The version name only fills in missing key or variant data.
- **Noise-only names** – If a download only includes noise descriptors (e.g., `Wan2.2HighNoise`) without any other tokens, the normalized key becomes empty and the entry does not merge with others, preventing false positives.

Use the unit tests under `DiffusionNexus.Tests/UI/ViewModels` as executable documentation—they contain canonical examples of every supported naming pattern.
