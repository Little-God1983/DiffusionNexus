# LoRA Naming & Merging Reference

This folder documents the domain concepts that drive LoRA variant classification and merge behavior inside DiffusionNexus.

## Terminology

- **High / Low variants** – The canonical labels surfaced to the user interface. These originate from filenames, model version names, or metadata tags that include one of the common aliases below.
- **HighNoise / LowNoise** – Verbose spellings used by many WAN 2.2 releases. They collapse into the High/Low labels above.
- **H / L aliases** – Short suffixes such as `_H`, `_L`, `_HN`, `_LN`, `HighNoise`, `LowNoise`, and even embedded segments like `wan22cshotHighNoise`. Every alias is normalized in a case-insensitive manner.
- **WAN 2.2 naming quirks** – WAN releases frequently mix spaces, dashes, underscores, and epoch counts. The classifier tolerates these differences by tokenizing on punctuation, trimming numeric prefixes/suffixes, and ignoring version tokens like `v1`, `epoch20`, or `e100`.

## Classification Pipeline

1. **Inputs** – The classifier inspects the `SafeTensorFileName` first and falls back to `ModelVersionName` if either the variant label or normalized key is missing.
2. **Parsing** – Each source string is trimmed, known extensions are removed, and the value is tokenized on spaces, underscores, dashes, dots, and bracket characters.
3. **Normalization** – Variant aliases are stripped, numeric padding is removed, and version tokens (`v1`, `epoch10`, `ver2`) are ignored. Remaining tokens are concatenated into a lowercase normalized key used for grouping.
4. **Variant detection** – Tokens and raw substrings are scanned for High/Low aliases. Embedded matches (for example `HighNoise` in the middle of a word) are only accepted when surrounded by non-letter characters.
5. **Final label** – The resulting `LoraVariantClassification` exposes the normalized key and the preferred variant label (High, Low, or `null` when nothing is detected).

## Merge Rules

- **Grouping** – Two seeds are merged when they share the same normalized key *and* the same `ModelId` and diffusion base model. Only High/Low variants participate in grouping.
- **Primary selection** – The first seed encountered wins the card folder/tree paths. Variant ordering always places `High` before `Low`, then falls back to alphabetical order for any other labels.
- **Priority** – High variants take precedence when selecting the default model for the card. If only one variant is detected, the card renders as a standalone entry.

## Edge Cases & Precedence

- **Missing variant** – Seeds without a recognized High/Low alias never merge, even if other seeds share the same normalized key.
- **Ambiguous names** – Numeric-only tokens are ignored unless a later token proves the entry represents a mixed alphanumeric name. This prevents `14B` or `_30` suffixes from polluting the normalized key.
- **Case sensitivity** – All matching is case-insensitive, and the normalized key is always emitted in lowercase for consistency.
- **Noise keywords** – Standalone `noise` tokens are ignored so that filenames like `wan2.2 high noise` still normalize to the same key as `wan2.2_high_noise`.

Refer to the XML documentation inside `DiffusionNexus.UI/Domain/Loras` for method-level behavior and additional implementation details.
