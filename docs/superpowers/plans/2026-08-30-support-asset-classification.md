# Support-Asset Classification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make "this file is a VAE / text encoder / ControlNet / upscaler, not a LoRA" a real property of a library row — read from the weights where possible — so the sorter files it into its own folder, the Viewer can hide it, and it stops inflating the "could not be identified" count.

**Architecture:** One vocabulary (`ModelType`, gaining `TextEncoder`; `SorterAssetKind` is deleted). Detection is header-first: `SafetensorsHeaderReader` already parses the full header JSON and discards the tensor key names, which prove what a container is; a bounded sample of those keys is retained and mapped by `AssetKindHeaderMap`. The existing name-based classifier becomes the fallback for header-less `.pth`/`.ckpt` pickles. The verdict is written to `Model.Type` at discovery, corrected by `IdentifyModelStep`, and backfilled once for existing rows.

**Tech Stack:** .NET 10, C#, EF Core (SQLite), Avalonia (MVVM / CommunityToolkit.Mvvm), xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-08-30-support-asset-classification-design.md`

## Global Constraints

- Branch: `feature/support-asset-classification`. Never commit to `develop` or `main`.
- `ModelType` members are **appended only**. `ModelConfiguration.cs:24` persists the property with `HasConversion<string>()`, so no migration is needed and no member may be reordered or renamed.
- Support-asset kinds are exactly `VAE`, `Controlnet`, `Upscaler`, `TextEncoder`. Nothing outside `ModelTypeExtensions` may restate that set.
- Destination folder names, sorter chip text and Viewer badge text are the **same string**, from `ModelTypeExtensions.DisplayName`.
- Detection precedence is fixed: safetensors tensor keys → file-name markers → `ModelType.LORA`. A header verdict is never overruled by a name.
- Run tests with `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj`. The suite is ~4460 tests; use `--filter` while iterating and run the whole suite before the final commit.
- `DiffusionNexus.Service` has `InternalsVisibleTo("DiffusionNexus.Tests")`. `DiffusionNexus.Domain` does **not** — anything the tests must see in Domain has to be `public`.
- Every new public type gets a `<summary>` explaining *why* it exists, matching the density of the file it sits beside. This codebase documents reasoning, not restatement.

## File Structure

**Create:**
- `DiffusionNexus.Domain/Enums/ModelTypeExtensions.cs` — the support-asset set, display names, folder names.
- `DiffusionNexus.Service/Services/Sync/Identity/AssetKindHeaderMap.cs` — tensor keys → `ModelType?`.
- `DiffusionNexus.Service/Services/Sync/Identity/AssetKindClassifier.cs` — file name → `ModelType` (moved).
- `DiffusionNexus.Service/Services/Sync/Identity/AssetKindResolver.cs` — the precedence rule, the single seam all three consumers call.
- `DiffusionNexus.Tests/Domain/ModelTypeExtensionsTests.cs`
- `DiffusionNexus.Tests/Sync/Service/Identity/AssetKindHeaderMapTests.cs`
- `DiffusionNexus.Tests/Sync/Service/Identity/AssetKindClassifierTests.cs`
- `DiffusionNexus.Tests/Sync/Service/Identity/AssetKindResolverTests.cs`

**Delete:**
- `DiffusionNexus.UI/Services/Lora/Sorting/SorterAssetKind.cs`
- `DiffusionNexus.Tests/Sorter/SorterAssetKindClassifierTests.cs` (its cases move to `AssetKindClassifierTests`)

**Modify:**
- `DiffusionNexus.Domain/Enums/DomainEnums.cs:6-27` — append `TextEncoder`.
- `DiffusionNexus.Service/Services/Sync/Identity/SafetensorsHeaderReader.cs` — retain tensor keys.
- `DiffusionNexus.Service/Services/ModelFileSyncService.cs:617` — classify instead of hardcoding; add the backfill.
- `DiffusionNexus.Service/Services/Sync/Steps/IdentifyModelStep.cs:221` — re-stamp `Type`.
- `DiffusionNexus.Service/Services/Sync/Steps/DiscoverFilesStep.cs` — run the backfill, report its count.
- `DiffusionNexus.Service/Services/Lora/LoraPathBuilder.cs` — support-asset destination.
- `DiffusionNexus.Domain/Services/IModelSyncService.cs` — the backfill method.
- `DiffusionNexus.DataAccess/Repositories/Interfaces/IModelRepository.cs` + `ModelRepository.cs` — backfill candidates.
- `DiffusionNexus.UI/Services/Lora/Sorting/LoraSortModels.cs`, `LoraSortPlanner.cs`, `SorterMetadataResolver.cs`
- `DiffusionNexus.UI/ViewModels/SortPreviewNodeViewModel.cs`, `LoraSorterViewModel.cs`, `LoraViewerViewModel.cs`, `ModelTileViewModel.cs`
- `DiffusionNexus.Tests/Sync/Service/Identity/SafetensorsFixture.cs` — tensor-key builders.

---

### Task 1: The vocabulary — `ModelType.TextEncoder` and `ModelTypeExtensions`

**Files:**
- Modify: `DiffusionNexus.Domain/Enums/DomainEnums.cs:6-27`
- Create: `DiffusionNexus.Domain/Enums/ModelTypeExtensions.cs`
- Test: `DiffusionNexus.Tests/Domain/ModelTypeExtensionsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ModelType.TextEncoder`; `ModelTypeExtensions.SupportAssetKinds` (`IReadOnlyList<ModelType>`), `.IsSupportAsset(this ModelType)` → `bool`, `.DisplayName(this ModelType)` → `string`, `.SupportFolderName(this ModelType)` → `string?`.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Tests/Domain/ModelTypeExtensionsTests.cs`:

```csharp
using DiffusionNexus.Domain.Enums;
using FluentAssertions;

namespace DiffusionNexus.Tests.Domain;

/// <summary>
/// The support-asset set and its names are stated once, here, because three surfaces read them —
/// the sorter's destination folder, the chip on that folder's row, and the Viewer's badge — and a
/// folder whose name disagreed with the chip above it would be a bug nobody could see in a diff.
/// </summary>
public sealed class ModelTypeExtensionsTests
{
    [Theory]
    [InlineData(ModelType.VAE)]
    [InlineData(ModelType.Controlnet)]
    [InlineData(ModelType.Upscaler)]
    [InlineData(ModelType.TextEncoder)]
    public void SupportAssetsAreNotLoras(ModelType type)
        => type.IsSupportAsset().Should().BeTrue();

    [Theory]
    [InlineData(ModelType.LORA)]
    [InlineData(ModelType.Checkpoint)]
    [InlineData(ModelType.LoCon)]
    [InlineData(ModelType.DoRA)]
    [InlineData(ModelType.Unknown)]
    public void EverythingTheSorterIsForIsNotASupportAsset(ModelType type)
        => type.IsSupportAsset().Should().BeFalse();

    [Theory]
    [InlineData(ModelType.VAE, "VAE")]
    [InlineData(ModelType.Controlnet, "ControlNet")]
    [InlineData(ModelType.Upscaler, "Upscaler")]
    [InlineData(ModelType.TextEncoder, "Text Encoder")]
    [InlineData(ModelType.LORA, "LoRA")]
    public void DisplayNamesAreTheOnesUsersSee(ModelType type, string expected)
        => type.DisplayName().Should().Be(expected);

    /// <summary>
    /// The destination folder and the chip on its row must be the same string, or the preview
    /// would name a folder the sorter does not create.
    /// </summary>
    [Fact]
    public void EverySupportAssetFolderNameIsItsDisplayName()
    {
        foreach (var kind in ModelTypeExtensions.SupportAssetKinds)
            kind.SupportFolderName().Should().Be(kind.DisplayName());
    }

    /// <summary>
    /// A LoRA's folder is its base model, which is a different question — so it deliberately has a
    /// display name but no folder name, and a caller that asks gets null rather than a folder
    /// called "LoRA" appearing beside the base-model folders.
    /// </summary>
    [Fact]
    public void ALoraHasNoSupportFolder()
        => ModelType.LORA.SupportFolderName().Should().BeNull();

    [Fact]
    public void SupportAssetKindsAndIsSupportAssetCannotDisagree()
    {
        foreach (var type in Enum.GetValues<ModelType>())
            type.IsSupportAsset().Should().Be(ModelTypeExtensions.SupportAssetKinds.Contains(type));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ModelTypeExtensionsTests"`
Expected: FAIL — compile error, `ModelType.TextEncoder` and `ModelTypeExtensions` do not exist.

- [ ] **Step 3: Append the enum member**

In `DiffusionNexus.Domain/Enums/DomainEnums.cs`, append to `ModelType` after `Motion` — do not reorder anything:

```csharp
    Motion,
    /// <summary>
    /// CLIP / T5 / LLM text encoders. Not a Civitai model type: Civitai has no such category, so
    /// this member only ever arrives from our own classification of a local file. Appended last —
    /// the value is persisted as a string (ModelConfiguration.cs:24), so appending is free and
    /// reordering would silently repoint every existing row.
    /// </summary>
    TextEncoder
```

- [ ] **Step 4: Write the extensions**

Create `DiffusionNexus.Domain/Enums/ModelTypeExtensions.cs`:

```csharp
namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// What the application means by "not a LoRA", and what it calls those things on screen.
/// </summary>
/// <remarks>
/// A LoRA library routinely also holds the VAEs, text encoders, ControlNets and upscalers a
/// workflow needs — 35 of 328 unidentified files on one real library (#527). Three surfaces have
/// to agree about them: the folder the sorter moves them into, the chip on that folder's row in
/// the preview, and the badge the Viewer shows. Those are one string each, defined here, because
/// a folder named differently from the chip advertising it is a defect no diff makes visible.
/// <para>
/// Public rather than internal: DiffusionNexus.Domain has no InternalsVisibleTo, and the guard
/// tests exist precisely to stop the set below drifting from its consumers.
/// </para>
/// </remarks>
public static class ModelTypeExtensions
{
    /// <summary>
    /// Everything a LoRA folder can hold that is not a LoRA. The one definition of the set —
    /// nothing else in the application may restate it.
    /// </summary>
    public static readonly IReadOnlyList<ModelType> SupportAssetKinds =
    [
        ModelType.VAE,
        ModelType.Controlnet,
        ModelType.Upscaler,
        ModelType.TextEncoder,
    ];

    /// <summary>Whether this is one of the things the sorter is NOT for.</summary>
    public static bool IsSupportAsset(this ModelType type) => type switch
    {
        ModelType.VAE or ModelType.Controlnet or ModelType.Upscaler or ModelType.TextEncoder => true,
        _ => false,
    };

    /// <summary>
    /// The label a user sees. Only the kinds our own classifier can produce are spelled out; the
    /// rest of the Civitai taxonomy falls back to the enum name, which is what every existing
    /// display path already showed.
    /// </summary>
    public static string DisplayName(this ModelType type) => type switch
    {
        ModelType.LORA => "LoRA",
        ModelType.VAE => "VAE",
        ModelType.Controlnet => "ControlNet",
        ModelType.Upscaler => "Upscaler",
        ModelType.TextEncoder => "Text Encoder",
        _ => type.ToString(),
    };

    /// <summary>
    /// The folder a support asset sorts into, or null for anything that is not one. A LoRA
    /// deliberately returns null: its folder is its base model, which is a different question,
    /// and returning "LoRA" here would put a folder of that name beside the base-model folders.
    /// </summary>
    public static string? SupportFolderName(this ModelType type)
        => type.IsSupportAsset() ? type.DisplayName() : null;
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ModelTypeExtensionsTests"`
Expected: PASS (all cases).

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Domain/Enums/DomainEnums.cs DiffusionNexus.Domain/Enums/ModelTypeExtensions.cs DiffusionNexus.Tests/Domain/ModelTypeExtensionsTests.cs
git commit -m "feat(domain): name the support-asset kinds once, in ModelType"
```

---

### Task 2: The header reader keeps the tensor keys it already parses

**Files:**
- Modify: `DiffusionNexus.Service/Services/Sync/Identity/SafetensorsHeaderReader.cs`
- Modify: `DiffusionNexus.Tests/Sync/Service/Identity/SafetensorsFixture.cs`
- Test: `DiffusionNexus.Tests/Sync/Service/Identity/SafetensorsHeaderReaderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `SafetensorsHeaderInfo` gains a 4th positional parameter `IReadOnlyList<string>? TensorKeys = null`; `SafetensorsHeaderReader.MaxSampledTensorKeys` (`const int` = 64); `SafetensorsFixture.Tensors(params string[] keys)` → `string`, `SafetensorsFixture.MetaAndTensors((string Key, string Value)[] pairs, params string[] keys)` → `string`.

The parameter defaults to `null` so the ~dozen existing three-argument construction sites in the test suite keep compiling unchanged.

- [ ] **Step 1: Add the fixture builders**

In `DiffusionNexus.Tests/Sync/Service/Identity/SafetensorsFixture.cs`, add beside `Meta`:

```csharp
    /// <summary>
    /// A header carrying only tensor entries — no <c>__metadata__</c> block at all, which is the
    /// normal shape for a VAE or a text encoder extracted from a checkpoint. Those files are
    /// exactly the ones the metadata rungs cannot answer for, so the fixture has to be able to
    /// build one without metadata rather than always emitting an empty block.
    /// </summary>
    public static string Tensors(params string[] tensorKeys) =>
        "{" + string.Join(",", tensorKeys.Select(TensorEntry)) + "}";

    /// <summary>A header with both a <c>__metadata__</c> block and named tensors.</summary>
    public static string MetaAndTensors((string Key, string Value)[] pairs, params string[] tensorKeys) =>
        "{\"__metadata__\":{" + string.Join(",", pairs.Select(p => $"\"{p.Key}\":\"{p.Value}\"")) + "}," +
        string.Join(",", tensorKeys.Select(TensorEntry)) + "}";

    private static string TensorEntry(string key) =>
        $"\"{key}\":{{\"dtype\":\"F16\",\"shape\":[4],\"data_offsets\":[0,8]}}";
```

- [ ] **Step 2: Write the failing test**

Append to `DiffusionNexus.Tests/Sync/Service/Identity/SafetensorsHeaderReaderTests.cs` (inside the existing test class; reuse whatever temp-file helper the class already uses — if it writes bytes inline, follow that pattern exactly):

```csharp
    /// <summary>
    /// The tensor key names are the only thing in a safetensors file that says what it actually is,
    /// and the reader was already parsing and discarding them. A VAE extracted from a checkpoint
    /// has no __metadata__ block at all, so the keys are the ONLY signal it carries.
    /// </summary>
    [Fact]
    public async Task ReadsTheTensorKeyNames()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.safetensors");
        await File.WriteAllBytesAsync(path, SafetensorsFixture.Safetensors(
            SafetensorsFixture.Tensors("encoder.down.0.block.0.norm1.weight", "post_quant_conv.weight")));

        try
        {
            var info = await SafetensorsHeaderReader.TryReadAsync(path);

            info.Should().NotBeNull();
            info!.Keys.Should().BeEquivalentTo(
                ["encoder.down.0.block.0.norm1.weight", "post_quant_conv.weight"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A checkpoint has thousands of tensors and the sample exists to bound memory, not to be
    /// complete: a container's keys are homogeneous, so the first 64 answer the same question the
    /// full set would.
    /// </summary>
    [Fact]
    public async Task SamplesTheTensorKeysRatherThanKeepingThemAll()
    {
        var keys = Enumerable.Range(0, SafetensorsHeaderReader.MaxSampledTensorKeys + 40)
            .Select(i => $"block.{i}.weight")
            .ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.safetensors");
        await File.WriteAllBytesAsync(path, SafetensorsFixture.Safetensors(SafetensorsFixture.Tensors(keys)));

        try
        {
            var info = await SafetensorsHeaderReader.TryReadAsync(path);

            info.Should().NotBeNull();
            info!.Keys.Should().HaveCount(SafetensorsHeaderReader.MaxSampledTensorKeys);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The metadata block is not a tensor and must never appear among the keys.</summary>
    [Fact]
    public async Task TheMetadataBlockIsNotATensorKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.safetensors");
        await File.WriteAllBytesAsync(path, SafetensorsFixture.Safetensors(
            SafetensorsFixture.MetaAndTensors([("modelspec.architecture", "flux-1-dev")], "lora_unet_0.lora_up.weight")));

        try
        {
            var info = await SafetensorsHeaderReader.TryReadAsync(path);

            info.Should().NotBeNull();
            info!.Keys.Should().ContainSingle().Which.Should().Be("lora_unet_0.lora_up.weight");
            info.Architecture.Should().Be("flux-1-dev", "the existing metadata rungs must keep working");
        }
        finally
        {
            File.Delete(path);
        }
    }
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~SafetensorsHeaderReaderTests"`
Expected: FAIL — `SafetensorsHeaderInfo` has no `TensorKeys`, `MaxSampledTensorKeys` undefined.

- [ ] **Step 4: Retain the keys in the reader**

In `SafetensorsHeaderReader.cs`, change the record and the parse. Replace the record declaration:

```csharp
/// <summary>The identity-relevant fields of a safetensors JSON header's __metadata__ block.</summary>
/// <param name="TensorKeys">
/// A bounded sample of the header's TENSOR NAMES — every root property other than
/// <c>__metadata__</c>, capped at <see cref="SafetensorsHeaderReader.MaxSampledTensorKeys"/>.
/// These say what the container actually is (<c>lora_up</c> vs <c>post_quant_conv</c> vs
/// <c>text_model.encoder.layers</c>) where the metadata block says only what it was trained
/// against — and a VAE or text encoder extracted from a checkpoint carries no metadata block at
/// all. Defaults to null so existing three-argument construction sites keep compiling; callers
/// read it through the non-null <see cref="Keys"/>.
/// </param>
public sealed record SafetensorsHeaderInfo(
    string? BaseModelVersion,   // __metadata__["ss_base_model_version"]
    string? Architecture,       // __metadata__["modelspec.architecture"]
    string? ModelNameHint,      // __metadata__["ss_sd_model_name"]
    IReadOnlyList<string>? TensorKeys = null)
{
    /// <summary>The sampled tensor names, never null.</summary>
    public IReadOnlyList<string> Keys => TensorKeys ?? [];
}
```

Add the cap beside `MaxHeaderBytes`:

```csharp
    /// <summary>
    /// How many tensor names are kept. A checkpoint has thousands and we need none of them
    /// individually — a container's keys are homogeneous, so a small prefix answers the same
    /// question the full set would, without holding the whole list.
    /// </summary>
    public const int MaxSampledTensorKeys = 64;
```

Inside `TryReadAsync`, after the existing `__metadata__` block and before the `return`, collect the keys:

```csharp
            var tensorKeys = new List<string>(MaxSampledTensorKeys);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (tensorKeys.Count == MaxSampledTensorKeys) break;
                if (property.NameEquals("__metadata__")) continue;
                tensorKeys.Add(property.Name);
            }

            return new SafetensorsHeaderInfo(baseModelVersion, architecture, modelNameHint, tensorKeys);
```

(delete the old three-argument `return`.)

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~SafetensorsHeaderReaderTests|FullyQualifiedName~BaseModelHeaderMapTests"`
Expected: PASS. `BaseModelHeaderMapTests` is included to prove the record change broke none of its construction sites.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Service/Services/Sync/Identity/SafetensorsHeaderReader.cs DiffusionNexus.Tests/Sync/Service/Identity/SafetensorsFixture.cs DiffusionNexus.Tests/Sync/Service/Identity/SafetensorsHeaderReaderTests.cs
git commit -m "feat(identity): keep the tensor names the header reader already parsed"
```

---

### Task 3: `AssetKindHeaderMap` — what the weights say

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/Identity/AssetKindHeaderMap.cs`
- Test: `DiffusionNexus.Tests/Sync/Service/Identity/AssetKindHeaderMapTests.cs`

**Interfaces:**
- Consumes: `SafetensorsHeaderInfo.Keys` (Task 2), `ModelType` (Task 1).
- Produces: `AssetKindHeaderMap.Map(SafetensorsHeaderInfo? info)` → `ModelType?`; `internal static IReadOnlyCollection<ModelType> AllKinds`.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Tests/Sync/Service/Identity/AssetKindHeaderMapTests.cs`:

```csharp
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Service.Services.Sync.Identity;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service.Identity;

/// <summary>
/// Every key pattern here is one a real container carries. This is the rung that makes the whole
/// feature safe to act on: the sorter physically MOVES files off this verdict, and a name-based
/// guess is not a good enough reason to move somebody's weights.
/// </summary>
public sealed class AssetKindHeaderMapTests
{
    private static SafetensorsHeaderInfo Header(params string[] keys) => new(null, null, null, keys);

    [Theory]
    [InlineData("lora_unet_single_blocks_0_linear1.lora_up.weight")]
    [InlineData("lora_unet_single_blocks_0_linear1.lora_down.weight")]
    [InlineData("lora_te_text_model_encoder_layers_0_mlp_fc1.lora_up.weight")]
    [InlineData("transformer.blocks.0.attn.to_q.lora_A.weight")]
    [InlineData("transformer.blocks.0.attn.to_q.lora_B.weight")]
    [InlineData("lora_unet_single_blocks_0_linear1.alpha")]
    public void LoraWeightsNameALora(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().Be(ModelType.LORA);

    [Theory]
    [InlineData("post_quant_conv.weight")]
    [InlineData("quant_conv.bias")]
    [InlineData("encoder.down.0.block.0.norm1.weight")]
    [InlineData("decoder.up.3.block.2.conv2.bias")]
    public void AutoencoderWeightsNameAVae(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().Be(ModelType.VAE);

    [Theory]
    [InlineData("text_model.encoder.layers.0.self_attn.q_proj.weight")]
    [InlineData("logit_scale")]
    [InlineData("text_model.embeddings.token_embedding.weight")]
    [InlineData("shared.weight")]
    public void EncoderWeightsNameATextEncoder(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().Be(ModelType.TextEncoder);

    [Theory]
    [InlineData("control_model.input_blocks.0.0.weight")]
    [InlineData("controlnet_cond_embedding.conv_in.weight")]
    [InlineData("input_hint_block.0.weight")]
    public void ControlWeightsNameAControlNet(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().Be(ModelType.Controlnet);

    /// <summary>
    /// A header whose keys match nothing must say nothing, so the caller falls through to the
    /// file name rather than being handed a confident wrong answer.
    /// </summary>
    [Theory]
    [InlineData("model.diffusion_model.input_blocks.0.0.weight")]
    [InlineData("some.opaque.tensor")]
    public void UnrecognizedWeightsSayNothing(string key)
        => AssetKindHeaderMap.Map(Header(key)).Should().BeNull();

    [Fact]
    public void ANullHeaderSaysNothing()
        => AssetKindHeaderMap.Map(null).Should().BeNull();

    [Fact]
    public void AHeaderWithNoTensorsSaysNothing()
        => AssetKindHeaderMap.Map(new SafetensorsHeaderInfo(null, null, null)).Should().BeNull();

    /// <summary>
    /// A LoRA trained on a text encoder carries BOTH "lora_te_" and "text_model.encoder.layers"
    /// shaped keys. It is a LoRA — that is what the file is — so the LoRA evidence has to be
    /// checked before the encoder evidence, not merely be present in the table.
    /// </summary>
    [Fact]
    public void ATextEncoderLoraIsALoraNotATextEncoder()
    {
        var header = Header(
            "lora_te_text_model_encoder_layers_0_mlp_fc1.lora_up.weight",
            "lora_te_text_model_encoder_layers_0_mlp_fc1.lora_down.weight");

        AssetKindHeaderMap.Map(header).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// Mirrors the AllLabels guards on BaseModelHeaderMap and FilenameBaseModelHeuristic: nothing
    /// may be returned from here that the rest of the app has no name for.
    /// </summary>
    [Fact]
    public void EveryKindItCanReturnIsOneTheAppCanName()
    {
        foreach (var kind in AssetKindHeaderMap.AllKinds)
        {
            kind.DisplayName().Should().NotBeNullOrWhiteSpace();
            if (kind != ModelType.LORA) kind.IsSupportAsset().Should().BeTrue();
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~AssetKindHeaderMapTests"`
Expected: FAIL — `AssetKindHeaderMap` does not exist.

- [ ] **Step 3: Write the map**

Create `DiffusionNexus.Service/Services/Sync/Identity/AssetKindHeaderMap.cs`:

```csharp
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Service.Services.Sync.Identity;

/// <summary>
/// Names what a safetensors container actually is from its TENSOR KEYS — a reading of the weights,
/// not a guess about the file name.
/// </summary>
/// <remarks>
/// This is the rung that makes #527 safe to act on. The sorter physically relocates files off this
/// verdict, and a LoRA called <c>vae_finetune_lora</c> must not be filed as a VAE because of how
/// its author named it. The keys cannot lie about this: a LoRA carries <c>lora_up</c>/<c>lora_A</c>
/// pairs, an autoencoder carries <c>post_quant_conv</c>, a text encoder carries
/// <c>text_model.encoder.layers</c>.
/// <para>
/// There is deliberately no upscaler rung: ESRGAN-family upscalers ship as <c>.pth</c> pickles with
/// no readable header at all, so a rule for them here would never fire. They are named by
/// <see cref="AssetKindClassifier"/> instead.
/// </para>
/// <para>
/// Order is load-bearing. A LoRA trained on the text encoder carries both LoRA markers and
/// encoder-shaped key names, so the LoRA evidence is checked first: what the file IS outranks what
/// it was trained on, exactly as <see cref="BaseModelHeaderMap"/> checks its name hint before the
/// architecture that every SDXL refinement shares.
/// </para>
/// </remarks>
public static class AssetKindHeaderMap
{
    // Rung 1 — LoRA. Checked first; see class remarks. ".alpha" is matched as a SUFFIX because it
    // is the per-module scale a LoRA writes beside each up/down pair, and as a substring it would
    // hit any tensor whose path merely contains the letters.
    private static readonly string[] LoraNeedles =
    {
        "lora_up", "lora_down", "lora_a.", "lora_b.", "lora_unet", "lora_te",
    };

    private const string LoraAlphaSuffix = ".alpha";

    // Rung 2 — autoencoder. "post_quant_conv"/"quant_conv" are unique to a VAE's latent bottleneck;
    // the down/up block paths are the encoder and decoder stacks either side of it.
    private static readonly string[] VaeNeedles =
    {
        "post_quant_conv", "quant_conv", "encoder.down.", "decoder.up.",
    };

    // Rung 3 — ControlNet. "control_model." is the prefix a bundled ControlNet carries;
    // "controlnet_cond_embedding" and "input_hint_block" are the hint-conditioning stem that only
    // a ControlNet has.
    private static readonly string[] ControlNetNeedles =
    {
        "control_model.", "controlnet_cond_embedding", "input_hint_block",
    };

    // Rung 4 — text encoder. "shared.weight" is T5's tied embedding table; "logit_scale" is CLIP's
    // learned temperature. Both are single, whole keys rather than path fragments, so they are
    // matched exactly — "shared.weight" as a substring would hit unrelated paths.
    private static readonly string[] TextEncoderNeedles =
    {
        "text_model.encoder.layers", "token_embedding",
    };

    private static readonly string[] TextEncoderExactKeys =
    {
        "logit_scale", "shared.weight",
    };

    /// <summary>
    /// Every kind this map can ever return. Test-only seam (DiffusionNexus.Tests,
    /// InternalsVisibleTo) — mirrors <see cref="BaseModelHeaderMap.AllLabels"/>.
    /// </summary>
    internal static IReadOnlyCollection<ModelType> AllKinds { get; } =
        [ModelType.LORA, ModelType.VAE, ModelType.Controlnet, ModelType.TextEncoder];

    /// <summary>What the tensor keys say this file is, or null when they say nothing usable.</summary>
    public static ModelType? Map(SafetensorsHeaderInfo? info)
    {
        if (info is null) return null;

        var keys = info.Keys;
        if (keys.Count == 0) return null;

        var lowered = new string[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            lowered[i] = keys[i].ToLowerInvariant();

        foreach (var key in lowered)
        {
            if (key.EndsWith(LoraAlphaSuffix, StringComparison.Ordinal)) return ModelType.LORA;
            if (ContainsAny(key, LoraNeedles)) return ModelType.LORA;
        }

        foreach (var key in lowered)
        {
            if (ContainsAny(key, VaeNeedles)) return ModelType.VAE;
        }

        foreach (var key in lowered)
        {
            if (ContainsAny(key, ControlNetNeedles)) return ModelType.Controlnet;
        }

        foreach (var key in lowered)
        {
            if (ContainsAny(key, TextEncoderNeedles)) return ModelType.TextEncoder;
            foreach (var exact in TextEncoderExactKeys)
            {
                if (string.Equals(key, exact, StringComparison.Ordinal)) return ModelType.TextEncoder;
            }
        }

        return null;
    }

    private static bool ContainsAny(string key, string[] needles)
    {
        foreach (var needle in needles)
        {
            if (key.Contains(needle, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~AssetKindHeaderMapTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.Service/Services/Sync/Identity/AssetKindHeaderMap.cs DiffusionNexus.Tests/Sync/Service/Identity/AssetKindHeaderMapTests.cs
git commit -m "feat(identity): name a container's kind from its tensor keys"
```

---

### Task 4: Move the name classifier down a layer, onto `ModelType`

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/Identity/AssetKindClassifier.cs`
- Delete: `DiffusionNexus.UI/Services/Lora/Sorting/SorterAssetKind.cs`
- Create: `DiffusionNexus.Tests/Sync/Service/Identity/AssetKindClassifierTests.cs`
- Delete: `DiffusionNexus.Tests/Sorter/SorterAssetKindClassifierTests.cs`
- Modify (compile fallout only): `DiffusionNexus.UI/Services/Lora/Sorting/LoraSortModels.cs`, `DiffusionNexus.UI/ViewModels/SortPreviewNodeViewModel.cs`, `DiffusionNexus.UI/ViewModels/LoraSorterViewModel.cs`

**Interfaces:**
- Consumes: `ModelType`, `ModelTypeExtensions` (Task 1).
- Produces: `AssetKindClassifier.Classify(string? fileName)` → `ModelType`; `internal static IReadOnlyCollection<ModelType> AllKinds`. `SorterAssetKind` and `SorterAssetKindClassifier` no longer exist.

`SortCandidate.AssetKind` and `SortPreviewNodeViewModel._kinds` change type to `ModelType` here purely so the solution compiles; their *behaviour* changes in Tasks 9 and 11.

- [ ] **Step 1: Port the tests**

Create `DiffusionNexus.Tests/Sync/Service/Identity/AssetKindClassifierTests.cs` with the full body of the existing `DiffusionNexus.Tests/Sorter/SorterAssetKindClassifierTests.cs`, changing only: the namespace to `DiffusionNexus.Tests.Sync.Service.Identity`, the usings to `DiffusionNexus.Domain.Enums` + `DiffusionNexus.Service.Services.Sync.Identity`, the class name to `AssetKindClassifierTests`, `SorterAssetKindClassifier` → `AssetKindClassifier`, and the expectations `SorterAssetKind.Vae` → `ModelType.VAE`, `.TextEncoder` → `ModelType.TextEncoder`, `.ControlNet` → `ModelType.Controlnet`, `.Upscaler` → `ModelType.Upscaler`, `.Lora` → `ModelType.LORA`.

**Do not drop or reword any case.** Every name in that file is a real one from the reference library, including the negative cases, which are what stop a marker firing on an ordinary LoRA name.

Then delete the old file:

```bash
git rm DiffusionNexus.Tests/Sorter/SorterAssetKindClassifierTests.cs
```

Add one new case at the end of the new class:

```csharp
    /// <summary>
    /// Mirrors the AllLabels guards elsewhere in this folder: nothing may come out of here that
    /// ModelTypeExtensions has no name for.
    /// </summary>
    [Fact]
    public void EveryKindItCanReturnIsOneTheAppCanName()
    {
        foreach (var kind in AssetKindClassifier.AllKinds)
        {
            kind.DisplayName().Should().NotBeNullOrWhiteSpace();
            if (kind != ModelType.LORA) kind.SupportFolderName().Should().NotBeNullOrWhiteSpace();
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~AssetKindClassifierTests"`
Expected: FAIL — `AssetKindClassifier` does not exist.

- [ ] **Step 3: Move the classifier**

Create `DiffusionNexus.Service/Services/Sync/Identity/AssetKindClassifier.cs` holding the `SorterAssetKindClassifier` body from `DiffusionNexus.UI/Services/Lora/Sorting/SorterAssetKind.cs`, **with every marker table and every explanatory comment carried over verbatim** — those tables were derived from a real library and the comments record why each marker did or did not earn its place. Change only:

- namespace → `DiffusionNexus.Service.Services.Sync.Identity`, `using DiffusionNexus.Domain.Enums;`
- class name → `AssetKindClassifier`
- return type → `ModelType`, and the returned members → `ModelType.VAE`, `ModelType.Controlnet`, `ModelType.TextEncoder`, `ModelType.Upscaler`, `ModelType.LORA`
- delete the `DisplayName` method — `ModelTypeExtensions.DisplayName` replaces it
- add the guard seam:

```csharp
    /// <summary>
    /// Every kind this classifier can ever return. Test-only seam (DiffusionNexus.Tests,
    /// InternalsVisibleTo) — mirrors <see cref="AssetKindHeaderMap.AllKinds"/>.
    /// </summary>
    internal static IReadOnlyCollection<ModelType> AllKinds { get; } =
        [ModelType.LORA, ModelType.VAE, ModelType.Controlnet, ModelType.TextEncoder, ModelType.Upscaler];
```

Update the class remarks: the old text said the kind "drives a label in the preview" and that "nothing here decides where a file goes". Both are about to stop being true. Replace that paragraph with:

```
/// Name-based and therefore fallible, which is why it is the SECOND rung: a safetensors file is
/// named by its tensor keys (<see cref="AssetKindHeaderMap"/>) and only a container with no
/// readable header — a .pth or .ckpt pickle — is named from here. That is what bounds the risk of
/// a verdict the sorter turns into a physical move.
```

Then delete the old file:

```bash
git rm DiffusionNexus.UI/Services/Lora/Sorting/SorterAssetKind.cs
```

- [ ] **Step 4: Fix the compile fallout**

Three call sites break. Change types only — no behaviour yet:

- `DiffusionNexus.UI/Services/Lora/Sorting/LoraSortModels.cs`: `SorterAssetKind AssetKind = SorterAssetKind.Lora` → `ModelType AssetKind = ModelType.LORA`. Add `using DiffusionNexus.Domain.Enums;`.
- `DiffusionNexus.UI/ViewModels/SortPreviewNodeViewModel.cs`: `SortedSet<SorterAssetKind> _kinds` → `SortedSet<ModelType>`; `SorterAssetKindClassifier.DisplayName(known)` → `known.DisplayName()`; `Absorb(SorterAssetKind kind, …)` → `Absorb(ModelType kind, …)`. Add `using DiffusionNexus.Domain.Enums;`.
- `DiffusionNexus.UI/ViewModels/LoraSorterViewModel.cs` (two sites, ~966 and ~1017): `SorterAssetKindClassifier.Classify(...)` → `AssetKindClassifier.Classify(...)`. Add `using DiffusionNexus.Service.Services.Sync.Identity;` if not already present.

- [ ] **Step 5: Run the tests**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~AssetKindClassifierTests|FullyQualifiedName~LoraSorterViewModelTests|FullyQualifiedName~SortPreviewNodeViewModelTests"`
Expected: PASS. The sorter suites must be green — this task changed types, not behaviour.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(identity): classify onto ModelType, drop the parallel SorterAssetKind enum"
```

---

### Task 5: The precedence rule — `AssetKindResolver`

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/Identity/AssetKindResolver.cs`
- Test: `DiffusionNexus.Tests/Sync/Service/Identity/AssetKindResolverTests.cs`

**Interfaces:**
- Consumes: `AssetKindHeaderMap.Map` (Task 3), `AssetKindClassifier.Classify` (Task 4).
- Produces: `AssetKindResolver.Resolve(SafetensorsHeaderInfo? header, string? fileName)` → `ModelType`; `AssetKindResolver.ResolveAsync(string filePath, CancellationToken ct = default)` → `Task<ModelType>`.

This is the single seam Tasks 6, 8 and 10 all call, so the precedence rule exists once.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Tests/Sync/Service/Identity/AssetKindResolverTests.cs`:

```csharp
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Service.Services.Sync.Identity;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service.Identity;

/// <summary>
/// The precedence rule, in one place because three callers depend on it: discovery, the identify
/// step, and the sorter's own resolver.
/// </summary>
public sealed class AssetKindResolverTests
{
    private static SafetensorsHeaderInfo Header(params string[] keys) => new(null, null, null, keys);

    /// <summary>
    /// The whole point of reading the weights. The issue named this exact hazard: a LoRA called
    /// "vae_finetune_lora" is a LoRA, and the name rung must never get to see it.
    /// </summary>
    [Fact]
    public void AHeaderProvingALoraBeatsAFileNameSayingVae()
        => AssetKindResolver.Resolve(
                Header("lora_unet_blocks_0.lora_up.weight"),
                "vae_finetune_lora.safetensors")
            .Should().Be(ModelType.LORA);

    [Fact]
    public void AHeaderProvingAVaeBeatsAnUninformativeName()
        => AssetKindResolver.Resolve(Header("post_quant_conv.weight"), "BRFHE7KV2VWXY8N3D4SXR4XCT0.safetensors")
            .Should().Be(ModelType.VAE);

    /// <summary>
    /// A .pth pickle has no readable header, so the name is all there is — which is why the name
    /// rung still exists and why every real upscaler in the reference library is a .pth.
    /// </summary>
    [Fact]
    public void WithNoReadableHeaderTheNameDecides()
        => AssetKindResolver.Resolve(header: null, "4x-UltraSharp.pth").Should().Be(ModelType.Upscaler);

    /// <summary>
    /// A header that parsed but recognizes nothing is not an answer — fall through to the name
    /// rather than treat "the keys said nothing" as "it is a LoRA".
    /// </summary>
    [Fact]
    public void AnUnrecognizedHeaderFallsThroughToTheName()
        => AssetKindResolver.Resolve(Header("model.diffusion_model.input_blocks.0.0.weight"),
                "Wan2_2_VAE_bf16.safetensors")
            .Should().Be(ModelType.VAE);

    [Fact]
    public void WhenNothingSaysAnythingItIsALora()
        => AssetKindResolver.Resolve(header: null, "MyChar_Pony_v2.safetensors").Should().Be(ModelType.LORA);

    [Fact]
    public async Task ResolveAsyncReadsARealFileHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.safetensors");
        await File.WriteAllBytesAsync(path, SafetensorsFixture.Safetensors(
            SafetensorsFixture.Tensors("post_quant_conv.weight")));

        try
        {
            (await AssetKindResolver.ResolveAsync(path)).Should().Be(ModelType.VAE);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A file that cannot be opened at all must not throw into a discovery loop — it is simply a
    /// file whose weights we could not read, which is what the name rung is for.
    /// </summary>
    [Fact]
    public async Task ResolveAsyncFallsBackToTheNameWhenTheFileIsUnreadable()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}", "4xLSDIRplus.pth");

        (await AssetKindResolver.ResolveAsync(missing)).Should().Be(ModelType.Upscaler);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~AssetKindResolverTests"`
Expected: FAIL — `AssetKindResolver` does not exist.

- [ ] **Step 3: Write the resolver**

Create `DiffusionNexus.Service/Services/Sync/Identity/AssetKindResolver.cs`:

```csharp
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Service.Services.Sync.Identity;

/// <summary>
/// What a file IS — a VAE, a text encoder, a ControlNet, an upscaler, or the LoRA the library is
/// actually for (#527).
/// </summary>
/// <remarks>
/// The precedence rule lives here, once, because three callers depend on it and they must not
/// drift: discovery (<c>ModelFileSyncService</c>), the identify step, and the sorter's own
/// <c>SorterMetadataResolver</c>.
/// <list type="number">
///   <item><description><see cref="AssetKindHeaderMap"/> — a reading of the weights.</description></item>
///   <item><description><see cref="AssetKindClassifier"/> — a guess about the file name.</description></item>
///   <item><description><see cref="ModelType.LORA"/> — the default, which is what discovery always assumed.</description></item>
/// </list>
/// The header wins outright and the name is not consulted when it answers. That is the whole
/// reason the header rung exists: the sorter turns this verdict into a physical move, and a LoRA
/// called <c>vae_finetune_lora</c> must not be filed as a VAE because of what its author called it.
/// Mirrors the same shape as the base-model chain (header, then
/// <see cref="FilenameBaseModelHeuristic"/>), for the same reason.
/// </remarks>
public static class AssetKindResolver
{
    /// <summary>The kind, from an already-parsed header and a file name.</summary>
    public static ModelType Resolve(SafetensorsHeaderInfo? header, string? fileName)
        => AssetKindHeaderMap.Map(header) ?? AssetKindClassifier.Classify(fileName);

    /// <summary>
    /// The kind for a file on disk. Reads the header when the file is a safetensors container and
    /// can be opened; falls back to the name otherwise. Never throws for an unreadable file —
    /// <see cref="SafetensorsHeaderReader.TryReadAsync"/> already answers null for that, and a
    /// file we could not read is exactly the case the name rung exists to cover.
    /// </summary>
    public static async Task<ModelType> ResolveAsync(string filePath, CancellationToken ct = default)
    {
        var header = await SafetensorsHeaderReader.TryReadAsync(filePath, ct).ConfigureAwait(false);
        return Resolve(header, Path.GetFileName(filePath));
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~AssetKindResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.Service/Services/Sync/Identity/AssetKindResolver.cs DiffusionNexus.Tests/Sync/Service/Identity/AssetKindResolverTests.cs
git commit -m "feat(identity): the weights outrank the file name, in one place"
```

---

### Task 6: Discovery stops calling every file a LoRA

**Files:**
- Modify: `DiffusionNexus.Service/Services/ModelFileSyncService.cs:284-320` (the discovery loop) and `:617-655` (`CreateModelFromFile`)
- Test: `DiffusionNexus.Tests/Service/ModelFileSyncServiceDiscoveryKindTests.cs` (create)

**Interfaces:**
- Consumes: `AssetKindResolver.ResolveAsync` (Task 5).
- Produces: `CreateModelFromFileAsync(string filePath, FileInfo fileInfo, CancellationToken ct)` → `Task<Model>` (private).

Follow the construction pattern in the existing `DiffusionNexus.Tests/Service/ModelFileSyncServiceDiscoveryRepointTests.cs` for building the service over an in-memory/SQLite unit of work and a settings service returning a temp source folder — read that file first and mirror it exactly rather than inventing a new harness.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Tests/Service/ModelFileSyncServiceDiscoveryKindTests.cs`. Mirror the fixture setup of `ModelFileSyncServiceDiscoveryRepointTests`, then:

```csharp
    /// <summary>
    /// Discovery used to stamp Type = LORA on literally every file it found, which is the root of
    /// #527: a VAE was indistinguishable from a LoRA everywhere downstream because the row said it
    /// was one.
    /// </summary>
    [Fact]
    public async Task ADiscoveredVaeIsRecordedAsAVae()
    {
        var path = Path.Combine(_sourceFolder, "Wan2_2_VAE_bf16.safetensors");
        await File.WriteAllBytesAsync(path, SafetensorsFixture.Safetensors(
            SafetensorsFixture.Tensors("post_quant_conv.weight", "encoder.down.0.block.0.norm1.weight")));

        var result = await _service.DiscoverNewFilesAsync();

        result.NewModels.Should().ContainSingle()
            .Which.Type.Should().Be(ModelType.VAE);
    }

    /// <summary>
    /// The weights outrank the name here too, not only in the resolver's own unit tests — this is
    /// the call site where a mistake would physically mislabel a user's row.
    /// </summary>
    [Fact]
    public async Task ALoraNamedLikeAVaeIsStillALora()
    {
        var path = Path.Combine(_sourceFolder, "vae_finetune_lora.safetensors");
        await File.WriteAllBytesAsync(path, SafetensorsFixture.Safetensors(
            SafetensorsFixture.Tensors("lora_unet_blocks_0.lora_up.weight")));

        var result = await _service.DiscoverNewFilesAsync();

        result.NewModels.Should().ContainSingle().Which.Type.Should().Be(ModelType.LORA);
    }

    [Fact]
    public async Task AnUpscalerPickleIsRecordedFromItsName()
    {
        var path = Path.Combine(_sourceFolder, "4x-UltraSharp.pth");
        await File.WriteAllBytesAsync(path, new byte[64]);

        var result = await _service.DiscoverNewFilesAsync();

        result.NewModels.Should().ContainSingle().Which.Type.Should().Be(ModelType.Upscaler);
    }

    [Fact]
    public async Task AnOrdinaryLoraIsStillALora()
    {
        var path = Path.Combine(_sourceFolder, "MyChar_Pony_v2.safetensors");
        await File.WriteAllBytesAsync(path, new byte[64]);

        var result = await _service.DiscoverNewFilesAsync();

        result.NewModels.Should().ContainSingle().Which.Type.Should().Be(ModelType.LORA);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ModelFileSyncServiceDiscoveryKindTests"`
Expected: FAIL — the VAE, upscaler and named-like-a-VAE cases all come back `LORA`.

- [ ] **Step 3: Classify at the point the row is created**

In `ModelFileSyncService.cs`, add `using DiffusionNexus.Service.Services.Sync.Identity;`, then change the signature and the `Type` assignment:

```csharp
    /// <summary>
    /// Builds the row for a newly discovered file. Async because the file's KIND is read from its
    /// safetensors header rather than assumed: this method used to stamp
    /// <c>Type = ModelType.LORA</c> unconditionally, which made every VAE, text encoder,
    /// ControlNet and upscaler in a LoRA folder indistinguishable from a LoRA everywhere
    /// downstream (#527). One bounded header read per NEW file — the same order of I/O as the
    /// 10 MB partial hash this loop already takes, and paid once per file ever.
    /// </summary>
    private static async Task<Model> CreateModelFromFileAsync(
        string filePath, FileInfo fileInfo, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var kind = await AssetKindResolver.ResolveAsync(filePath, cancellationToken).ConfigureAwait(false);

        var model = new Model
        {
            Name = fileName,
            Type = kind,
            Source = DataSource.LocalFile,
            CreatedAt = fileInfo.CreationTimeUtc
        };
```

(the rest of the method body is unchanged.)

At the call site (~line 313):

```csharp
                var model = await CreateModelFromFileAsync(filePath, fileInfo, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ModelFileSyncService"`
Expected: PASS, including the pre-existing discovery and contested-path suites.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.Service/Services/ModelFileSyncService.cs DiffusionNexus.Tests/Service/ModelFileSyncServiceDiscoveryKindTests.cs
git commit -m "fix(discovery): record what a discovered file is, not what we hoped"
```

---

### Task 7: Backfill the rows an existing library already has

**Files:**
- Modify: `DiffusionNexus.DataAccess/Repositories/Interfaces/IModelRepository.cs`, `DiffusionNexus.DataAccess/Repositories/ModelRepository.cs`
- Modify: `DiffusionNexus.Domain/Services/IModelSyncService.cs`, `DiffusionNexus.Service/Services/ModelFileSyncService.cs`
- Modify: `DiffusionNexus.Service/Services/Sync/Steps/DiscoverFilesStep.cs`
- Test: `DiffusionNexus.Tests/Service/ModelFileSyncServiceBackfillTests.cs` (create)

**Interfaces:**
- Consumes: `AssetKindClassifier.Classify` (Task 4), `ModelTypeExtensions.IsSupportAsset` (Task 1).
- Produces: `IModelRepository.GetSupportAssetBackfillCandidatesAsync(CancellationToken)` → `Task<IReadOnlyList<Model>>`; `IModelSyncService.ReclassifySupportAssetsAsync(CancellationToken)` → `Task<int>`; `DiscoverFilesStep.ReclassifiedCount` (`int`, get-only).

Name-only by design — see the spec's §3 "Accepted residual". A per-row header read over a 3 000-file library on a NAS is minutes of I/O for a one-time pass, and any row this rung gets wrong is corrected by Task 8 the next time its header is read.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Tests/Service/ModelFileSyncServiceBackfillTests.cs`, mirroring the harness in `ModelFileSyncServiceDiscoveryRepointTests`:

```csharp
    /// <summary>
    /// Every row in a library that predates #527 says LORA. The pass targets exactly the cohort
    /// Civitai has already failed on, which is where the support assets are.
    /// </summary>
    [Fact]
    public async Task ReclassifiesAnUnidentifiedLocalRow()
    {
        var id = await GivenModelAsync("Wan2_2_VAE_bf16", ModelType.LORA, DataSource.LocalFile, SyncOutcome.NotIdentified);

        var changed = await _service.ReclassifySupportAssetsAsync(CancellationToken.None);

        changed.Should().Be(1);
        (await LoadTypeAsync(id)).Should().Be(ModelType.VAE);
    }

    /// <summary>
    /// A model Civitai identified carries an authoritative type. Our name guess must never
    /// overrule it — that is the difference between filling a blank and overwriting an answer.
    /// </summary>
    [Fact]
    public async Task LeavesAMatchedRowAlone()
    {
        var id = await GivenModelAsync("vae_finetune_lora", ModelType.LORA, DataSource.LocalFile, SyncOutcome.Matched);

        var changed = await _service.ReclassifySupportAssetsAsync(CancellationToken.None);

        changed.Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
    }

    [Fact]
    public async Task LeavesAnOrdinaryLoraAlone()
    {
        var id = await GivenModelAsync("MyChar_Pony_v2", ModelType.LORA, DataSource.LocalFile, SyncOutcome.NotIdentified);

        var changed = await _service.ReclassifySupportAssetsAsync(CancellationToken.None);

        changed.Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// The pass runs on every discovery. It has to be free the second time: a row it reclassified
    /// no longer satisfies Type == LORA, so it is not a candidate again.
    /// </summary>
    [Fact]
    public async Task IsIdempotent()
    {
        await GivenModelAsync("SD3-VAE", ModelType.LORA, DataSource.LocalFile, SyncOutcome.NotIdentified);

        (await _service.ReclassifySupportAssetsAsync(CancellationToken.None)).Should().Be(1);
        (await _service.ReclassifySupportAssetsAsync(CancellationToken.None)).Should().Be(0);
    }
```

Write the two helpers `GivenModelAsync(string name, ModelType type, DataSource source, SyncOutcome outcome)` → `Task<int>` (inserts a `Model` with one `ModelVersion` + one `ModelFile` whose `FileName` is `$"{name}.safetensors"`, plus a `ModelSyncState` with `MetadataOutcome = outcome`) and `LoadTypeAsync(int id)` → `Task<ModelType>` against the same context the harness builds.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ModelFileSyncServiceBackfillTests"`
Expected: FAIL — `ReclassifySupportAssetsAsync` does not exist.

- [ ] **Step 3: Add the repository query**

In `IModelRepository.cs`:

```csharp
    /// <summary>
    /// Local rows still carrying discovery's old blanket <c>LORA</c> stamp that Civitai has never
    /// identified — the cohort a library's VAEs, text encoders, ControlNets and upscalers sit in
    /// (#527). Deliberately excludes <c>Matched</c> rows: those carry an authoritative Civitai
    /// type, and a name guess may fill a blank but never overwrite an answer.
    /// </summary>
    Task<IReadOnlyList<Model>> GetSupportAssetBackfillCandidatesAsync(CancellationToken cancellationToken = default);
```

In `ModelRepository.cs` — follow the file's existing query style (it uses `_context.Models` with `AsSplitQuery` where it includes collections; this one includes only the primary file, so keep it flat):

```csharp
    public async Task<IReadOnlyList<Model>> GetSupportAssetBackfillCandidatesAsync(
        CancellationToken cancellationToken = default)
        => await _context.Models
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Where(m => m.Type == ModelType.LORA
                        && m.Source == DataSource.LocalFile
                        && (m.SyncState == null
                            || m.SyncState.MetadataOutcome == SyncOutcome.NotIdentified
                            || m.SyncState.MetadataOutcome == SyncOutcome.None))
            .AsSplitQuery()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
```

- [ ] **Step 4: Add the service method**

In `DiffusionNexus.Domain/Services/IModelSyncService.cs`:

```csharp
    /// <summary>
    /// One-shot reclassification of rows that predate support-asset detection (#527), from the
    /// FILE NAME only. Returns how many rows changed.
    /// </summary>
    /// <remarks>
    /// Name-only on purpose: a header read per row would cost minutes on a large library over a
    /// NAS, and any row this gets wrong is corrected the next time <c>IdentifyModelStep</c> reads
    /// that file's weights. Idempotent and self-terminating — a row reclassified to VAE no longer
    /// satisfies the candidate query's <c>Type == LORA</c>.
    /// </remarks>
    Task<int> ReclassifySupportAssetsAsync(CancellationToken cancellationToken = default);
```

In `ModelFileSyncService.cs`:

```csharp
    /// <inheritdoc />
    public async Task<int> ReclassifySupportAssetsAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await _unitOfWork.Models
            .GetSupportAssetBackfillCandidatesAsync(cancellationToken)
            .ConfigureAwait(false);

        var changed = 0;
        foreach (var model in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The file name, not the model name: a user may have renamed the model in the app,
            // and it is the file on disk whose name carries the marker.
            var fileName = model.Versions
                .SelectMany(v => v.Files)
                .FirstOrDefault(f => f.IsPrimary)?.FileName
                ?? model.Versions.SelectMany(v => v.Files).FirstOrDefault()?.FileName;
            if (fileName is null) continue;

            var kind = AssetKindClassifier.Classify(fileName);
            if (!kind.IsSupportAsset()) continue;

            model.Type = kind;
            changed++;
        }

        if (changed > 0)
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return changed;
    }
```

- [ ] **Step 5: Run it from the discovery step**

In `DiscoverFilesStep.cs`, add the counter beside `RepointedCount`:

```csharp
    /// <summary>
    /// How many pre-existing rows the last scan reclassified as support assets (#527). Reported
    /// for the same reason RepointedCount is: on a library that predates the feature this is the
    /// only visible sign that 35 rows just stopped claiming to be LoRAs.
    /// </summary>
    public int ReclassifiedCount { get; private set; }
```

Reset it beside the others in `ExecuteOneAsync`, then after the `DiscoverNewFilesAsync` call:

```csharp
            ReclassifiedCount = await sync.ReclassifySupportAssetsAsync(ct).ConfigureAwait(false);

            _logger?.Info(LogCategory.FileSystem, LogSource,
                $"Discovered {DiscoveredCount} new model file(s), re-pointed {RepointedCount} moved, " +
                $"reclassified {ReclassifiedCount} as support assets");
```

(replacing the existing log line.)

- [ ] **Step 6: Run tests**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ModelFileSyncServiceBackfillTests|FullyQualifiedName~DiscoverFilesStepTests"`
Expected: PASS. Any fake `IModelSyncService` in `DiscoverFilesStepTests` needs the new member — add it returning `Task.FromResult(0)`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(sync): reclassify the support assets an existing library already holds"
```

---

### Task 8: `IdentifyModelStep` corrects the kind from the weights

**Files:**
- Modify: `DiffusionNexus.Service/Services/Sync/Steps/IdentifyModelStep.cs:220-230`
- Test: `DiffusionNexus.Tests/Sync/Service/Steps/IdentifyModelStepTests.cs`

**Interfaces:**
- Consumes: `AssetKindResolver.Resolve` (Task 5), `ModelTypeExtensions.IsSupportAsset` (Task 1).
- Produces: nothing new.

The step already reads the header at the "not on Civitai and no sidecar" branch. Reuse that same `header` value — do not open the file twice.

- [ ] **Step 1: Write the failing test**

Add to `DiffusionNexus.Tests/Sync/Service/Steps/IdentifyModelStepTests.cs`, following the existing arrangement helpers in that file:

```csharp
    /// <summary>
    /// The backfill (#527) classifies by name alone, which is fast but fallible. This is the rung
    /// that closes that window: the moment the step reads a file's weights, the row's kind is
    /// corrected from them.
    /// </summary>
    [Fact]
    public async Task CorrectsAMisnamedLoraFromItsWeights()
    {
        // A row the name-only backfill flipped to VAE, whose weights are a LoRA's.
        var candidate = await GivenLocalModelAsync(
            fileName: "vae_finetune_lora.safetensors",
            type: ModelType.VAE,
            headerJson: SafetensorsFixture.Tensors("lora_unet_blocks_0.lora_up.weight"));

        await WhenIdentifiedAsync(candidate);

        (await LoadTypeAsync(candidate.ModelId)).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// And the other direction: a VAE discovered before the feature existed, whose row still says
    /// LORA and whose name carries no marker, is named by its weights.
    /// </summary>
    [Fact]
    public async Task NamesASupportAssetFromItsWeights()
    {
        var candidate = await GivenLocalModelAsync(
            fileName: "opaque_name_nobody_can_read.safetensors",
            type: ModelType.LORA,
            headerJson: SafetensorsFixture.Tensors("post_quant_conv.weight"));

        await WhenIdentifiedAsync(candidate);

        (await LoadTypeAsync(candidate.ModelId)).Should().Be(ModelType.VAE);
    }
```

If `GivenLocalModelAsync` / `WhenIdentifiedAsync` / `LoadTypeAsync` do not already exist in that file under those or equivalent names, write them following the arrangement the file's existing tests use — read the file first and reuse its harness rather than adding a second one.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~IdentifyModelStepTests"`
Expected: FAIL — the type is never re-stamped.

- [ ] **Step 3: Re-stamp the kind from the header already read**

In `IdentifyModelStep.cs`, immediately after the existing header read (`var header = await SafetensorsHeaderReader.TryReadAsync(...)` and its `ct.ThrowIfCancellationRequested()`), insert:

```csharp
            // What the file IS, from the weights we just read (#527). Distinct from what it was
            // trained on, which is what the rungs below answer. Corrects rows the name-only
            // backfill guessed at, and names support assets discovered before the feature existed.
            //
            // Only ever moves BETWEEN our own verdicts: a model Civitai matched never reaches this
            // branch at all, so an authoritative type cannot be overwritten from here.
            var kind = AssetKindResolver.Resolve(header, Path.GetFileName(candidate.LocalPath));
            if (dbModel.Type != kind && (dbModel.Type == ModelType.LORA || dbModel.Type.IsSupportAsset()))
                dbModel.Type = kind;
```

Add `using DiffusionNexus.Service.Services.Sync.Identity;` if not present (it is — `SafetensorsHeaderReader` is already used) and `using DiffusionNexus.Domain.Enums;` (also already present).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~IdentifyModelStepTests"`
Expected: PASS, including the whole pre-existing suite.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.Service/Services/Sync/Steps/IdentifyModelStep.cs DiffusionNexus.Tests/Sync/Service/Steps/IdentifyModelStepTests.cs
git commit -m "feat(sync): correct a row's kind the moment its weights are read"
```

---

### Task 9: The sorter files a support asset into its own folder

**Files:**
- Modify: `DiffusionNexus.Service/Services/Lora/LoraPathBuilder.cs`
- Modify: `DiffusionNexus.UI/Services/Lora/Sorting/LoraSortPlanner.cs:81-84`
- Test: `DiffusionNexus.Tests/Sorter/LoraSortPlannerTests.cs` (there is no separate `LoraPathBuilderTests` — the path builder is covered through the planner)

**Interfaces:**
- Consumes: `ModelTypeExtensions.SupportFolderName` (Task 1), `SortCandidate.AssetKind` as `ModelType` (Task 4).
- Produces: `LoraPathBuilder.BuildSupportAssetDirectory(string targetRoot, ModelType kind)` → `string`.

- [ ] **Step 1: Write the failing test**

Add to the planner's test file:

```csharp
    /// <summary>
    /// #527: a VAE has no base model and no category — both describe a LoRA's provenance — so it
    /// gets a flat folder of its own beside the base-model folders rather than being filed under
    /// whichever base model its file name happened to suggest.
    /// </summary>
    [Fact]
    public void ASupportAssetGoesToItsOwnFlatFolder()
    {
        var candidate = Candidate("C:\\src\\Wan2_2_VAE_bf16.safetensors", baseModelRaw: "Wan Video",
            categoryFolderName: "Style") with { AssetKind = ModelType.VAE };

        var plan = Plan([candidate], includeCategory: true);

        plan.Moves.Single().TargetDirectory.Should().Be("C:\\dst\\VAE");
    }

    [Theory]
    [InlineData(ModelType.Controlnet, "C:\\dst\\ControlNet")]
    [InlineData(ModelType.TextEncoder, "C:\\dst\\Text Encoder")]
    [InlineData(ModelType.Upscaler, "C:\\dst\\Upscaler")]
    public void EveryKindGetsTheFolderItsChipNames(ModelType kind, string expected)
    {
        var candidate = Candidate("C:\\src\\thing.safetensors", baseModelRaw: "Qwen",
            categoryFolderName: "Style") with { AssetKind = kind };

        Plan([candidate], includeCategory: true).Moves.Single().TargetDirectory.Should().Be(expected);
    }

    /// <summary>The change must be invisible to the thing the sorter is actually for.</summary>
    [Fact]
    public void ALoraStillGoesToItsBaseModelAndCategory()
    {
        var candidate = Candidate("C:\\src\\MyChar.safetensors", baseModelRaw: "Pony",
            categoryFolderName: "Character");

        Plan([candidate], includeCategory: true).Moves.Single().TargetDirectory
            .Should().Be("C:\\dst\\Pony\\Character");
    }
```

Use the file's own existing `Candidate(...)` / `Plan(...)` helpers; if they have different names or signatures, adapt these three tests to them rather than adding new helpers.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~LoraSortPlannerTests"`
Expected: FAIL — the VAE lands in `C:\dst\Wan Video\Style`.

- [ ] **Step 3: Add the destination rule**

In `LoraPathBuilder.cs`:

```csharp
    /// <summary>
    /// Where a support asset goes: a flat, per-kind folder directly under the target root, beside
    /// the base-model folders (#527). No base-model segment and no category segment — both answer
    /// questions about a LoRA's provenance, and neither means anything for a VAE.
    /// </summary>
    /// <remarks>
    /// The folder name comes from <see cref="ModelTypeExtensions.SupportFolderName"/>, which is the
    /// same string the preview's chip shows, so the tree can never advertise a folder the sorter
    /// does not create. Throws for a non-support kind rather than inventing a folder: a LoRA's
    /// destination is its base model, and reaching here with one is a caller bug.
    /// </remarks>
    public static string BuildSupportAssetDirectory(string targetRoot, ModelType kind)
    {
        var folder = kind.SupportFolderName()
            ?? throw new ArgumentOutOfRangeException(nameof(kind), kind,
                "Only a support asset has a per-kind folder; a LoRA's folder is its base model.");
        return Path.Combine(targetRoot, SanitizeFolderName(folder));
    }
```

Add `using DiffusionNexus.Domain.Enums;` to the file.

In `LoraSortPlanner.BuildPlan`, replace the `targetDir` assignment:

```csharp
            var targetDir = candidate.AssetKind.IsSupportAsset()
                ? LoraPathBuilder.BuildSupportAssetDirectory(options.TargetRoot, candidate.AssetKind)
                : LoraPathBuilder.BuildTargetDirectory(
                    options.TargetRoot, candidate.BaseModelRaw, candidate.CategoryFolderName, options.IncludeCategory);
```

Add `using DiffusionNexus.Domain.Enums;` to the planner.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~Sorter"`
Expected: PASS — the whole sorter namespace, not just the planner. This task changes where files land, and `LoraSorterViewModelTests` asserts on destination folder names, so a routing regression has to surface here rather than two tasks later.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(sorter): file support assets into flat per-kind folders"
```

---

### Task 10: The sorter learns a browsed file's kind from the read it already does

**Files:**
- Modify: `DiffusionNexus.UI/Services/Lora/Sorting/SorterMetadataResolver.cs:11-41`, `:140-152`, `:215-241`
- Test: `DiffusionNexus.Tests/Sorter/SorterMetadataResolverTests.cs`

**Interfaces:**
- Consumes: `AssetKindResolver.Resolve` (Task 5).
- Produces: `FileIdentity` gains `ModelType AssetKind { get; init; } = ModelType.LORA;`; `ResolvedLoraMetadata` gains `ModelType AssetKind { get; init; } = ModelType.LORA;`.

`IdentifyFromFileAsync` already reads the header. Set the kind from that same `header` value — a second read of the same bytes would be the exact duplication the resolver's remarks argue against.

- [ ] **Step 1: Write the failing test**

Add to `DiffusionNexus.Tests/Sorter/SorterMetadataResolverTests.cs`. That file has **no** shared resolver field and no file-writing helper — every test constructs its own with `new SorterMetadataResolver(_client.Object, () => Task.FromResult<string?>(null), …)` and writes its own temp file (see the test at `:38` and `:216` for the two shapes). Follow that pattern: construct the resolver inline in each test below and write the bytes yourself. The assertions are the part that matters and must not change:

```csharp
    /// <summary>
    /// The "browse any folder" path has no DB row to read a kind from, so it has to come from the
    /// same header read the base-model rungs already perform — not a second pass over the bytes.
    /// </summary>
    [Fact]
    public async Task IdentifyFromFileNamesASupportAssetFromItsWeights()
    {
        var path = WriteFile("opaque_name.safetensors",
            SafetensorsFixture.Safetensors(SafetensorsFixture.Tensors("post_quant_conv.weight")));

        var identity = await _resolver.IdentifyFromFileAsync(path);

        identity.AssetKind.Should().Be(ModelType.VAE);
    }

    [Fact]
    public async Task IdentifyFromFileNamesAnUpscalerFromItsNameWhenThereIsNoHeader()
    {
        var path = WriteFile("4x-UltraSharp.pth", new byte[64]);

        var identity = await _resolver.IdentifyFromFileAsync(path);

        identity.AssetKind.Should().Be(ModelType.Upscaler);
    }

    [Fact]
    public async Task AnOrdinaryLoraIsStillALora()
    {
        var path = WriteFile("MyChar_Pony_v2.safetensors", new byte[64]);

        (await _resolver.IdentifyFromFileAsync(path)).AssetKind.Should().Be(ModelType.LORA);
    }

    /// <summary>ResolveAsync is the sorter's real entry point, so the kind has to survive it.</summary>
    [Fact]
    public async Task ResolveAsyncCarriesTheKind()
    {
        var path = WriteFile("Wan2_2_VAE_bf16.safetensors",
            SafetensorsFixture.Safetensors(SafetensorsFixture.Tensors("post_quant_conv.weight")));

        (await _resolver.ResolveAsync(path)).AssetKind.Should().Be(ModelType.VAE);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~SorterMetadataResolverTests"`
Expected: FAIL — `AssetKind` is not a member.

- [ ] **Step 3: Carry the kind through both records**

In `SorterMetadataResolver.cs`, add to `ResolvedLoraMetadata`:

```csharp
    /// <summary>
    /// What this file IS — a LoRA, or one of the support assets a LoRA folder also holds (#527).
    /// Read from the safetensors tensor keys where there are any, else guessed from the file name.
    /// The sorter files a support asset into its own folder, so this decides a destination and not
    /// merely a label.
    /// </summary>
    public ModelType AssetKind { get; init; } = ModelType.LORA;
```

Add the same property, with the same summary, to `FileIdentity`.

In `IdentifyFromFileAsync`, compute it from the header already read and attach it to every return:

```csharp
        var header = await SafetensorsHeaderReader.TryReadAsync(filePath, ct);

        // From the SAME header read — the weights answer both questions (what it was trained on,
        // and what it is), and opening the file twice for them would be the duplication this
        // class's own remarks argue against.
        var assetKind = AssetKindResolver.Resolve(header, fileName);

        var fromHeader = header is null ? null : BaseModelHeaderMap.Map(header);
        if (fromHeader is not null)
        {
            _logger?.Debug(LogCategory.FileSystem, LogSource,
                $"{fileName}: nothing on record knows this file; its own safetensors header says {fromHeader}.");
            return new FileIdentity(fromHeader, null) { AssetKind = assetKind };
        }
```

…and likewise on the remaining two exits of that method: `new FileIdentity(null, fromName) { AssetKind = assetKind }`, and in place of `FileIdentity.None` return `FileIdentity.None with { AssetKind = assetKind }`.

In `ResolveAsync`, carry it onto the returned metadata:

```csharp
        // The header is applied outright; the name is only offered. See FileIdentity.
        var withKind = resolved with { AssetKind = identity.AssetKind };
        return identity.FromHeader is not null
            ? withKind with { BaseModelRaw = identity.FromHeader }
            : withKind with { NameGuess = identity.FromName };
```

Add `using DiffusionNexus.Domain.Enums;` to the file.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~SorterMetadataResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/Lora/Sorting/SorterMetadataResolver.cs DiffusionNexus.Tests/Sorter/SorterMetadataResolverTests.cs
git commit -m "feat(sorter): carry a browsed file's kind out of the header read it already does"
```

---

### Task 11: The preview stops calling a VAE an unidentified LoRA

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/LoraSorterViewModel.cs:~966`, `:~1017`, `:1082-1097`
- Test: `DiffusionNexus.Tests/Sorter/LoraSorterViewModelTests.cs`

**Interfaces:**
- Consumes: `ResolvedLoraMetadata.AssetKind`, `FileIdentity.AssetKind` (Task 10); `Model.Type` (Task 6); `ModelTypeExtensions.IsSupportAsset` (Task 1).
- Produces: nothing new.

Two behaviour changes: the candidate's kind now comes from the row / the header instead of the file name alone, and a support asset counts as identified.

- [ ] **Step 1: Write the failing test**

Add to `DiffusionNexus.Tests/Sorter/LoraSorterViewModelTests.cs`, following its existing arrangement helpers:

```csharp
    /// <summary>
    /// #527: the count could never reach zero because ~35 files in a real library are not LoRAs at
    /// all. They are identified — we know exactly what they are — just not as LoRAs.
    /// </summary>
    [Fact]
    public async Task TheHintDoesNotCountSupportAssetsAsUnidentifiedLoras()
    {
        var vm = await GivenPreviewAsync(
            File("Wan2_2_VAE_bf16.safetensors", assetKind: ModelType.VAE),
            File("mystery_lora.safetensors", assetKind: ModelType.LORA));

        vm.NameGuessHint.Should().NotContain("2 LoRAs",
            "only the one file that is actually a LoRA can be an unidentified LoRA");
    }

    /// <summary>
    /// A VAE has no base model and never will. Marking its folder ✗ for that would ask the wrong
    /// question of it and leave the tree permanently unfinished.
    /// </summary>
    [Fact]
    public async Task ASupportAssetDoesNotPoisonItsFoldersMark()
    {
        var vm = await GivenPreviewAsync(File("Wan2_2_VAE_bf16.safetensors", assetKind: ModelType.VAE));

        var folder = vm.PreviewRoots.Single(n => n.Name == "VAE");
        folder.IsUnidentified.Should().BeFalse();
        folder.IsIdentified.Should().BeTrue();
        folder.AssetKinds.Should().ContainSingle().Which.Should().Be("VAE");
    }
```

`PreviewRoots` is the destination tree (`LoraSorterViewModel.cs:303`); `SourceRoots` is the other side. Adapt `GivenPreviewAsync` / `File(...)` to whatever the file already calls its arrangement helpers — read it first.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~LoraSorterViewModelTests"`
Expected: FAIL — the hint counts both files and the `VAE` folder shows ✗.

- [ ] **Step 3: Take the kind from the row and from the resolver**

At the DB-known candidate site (~966), replace the name-based classify with the row's own type, falling back to the header read that branch already performs for placeholder rows:

```csharp
                // The row's own type, which discovery and the identify step keep current (#527).
                // A placeholder row that had to be read from disk above gets the kind from that
                // same read instead — it is a better answer than a row nothing has classified yet.
                var assetKind = fileIdentityKind ?? f.Model.Type;

                candidates.Add(new SortCandidate(path, baseModelRaw, category,
                    f.Version.CivitaiId, f.File.HashSHA256, sizeBytes, SidecarLocator.FindSidecars(path), nameGuess,
                    assetKind));
```

…where `fileIdentityKind` is a `ModelType?` declared beside `nameGuess`, set inside the existing `if (LoraPathBuilder.IsPlaceholderBaseModel(baseModelRaw))` block:

```csharp
                    var fileIdentity = await _metadataResolver.IdentifyFromFileAsync(path, ct);
                    baseModelRaw = fileIdentity.FromHeader ?? baseModelRaw;
                    nameGuess = fileIdentity.FromName;
                    fileIdentityKind = fileIdentity.AssetKind;
```

At the unknown-file site (~1017), take it from the metadata:

```csharp
                candidates.Add(new SortCandidate(path, metadata.BaseModelRaw, category,
                    metadata.CivitaiVersionId, metadata.Sha256, sizeBytes, SidecarLocator.FindSidecars(path),
                    metadata.NameGuess, metadata.AssetKind));
```

- [ ] **Step 4: Exclude support assets from the hint**

In `UpdateNameGuessHint`, add the guard as the first line of the loop:

```csharp
        foreach (var candidate in candidates)
        {
            // A support asset is identified — we know exactly what it is — it simply is not a
            // LoRA, and it has no base model to be missing (#527). Counting one here is what kept
            // this number from ever reaching zero however good the identity chain got.
            if (candidate.AssetKind.IsSupportAsset()) continue;

            if (!LoraPathBuilder.IsPlaceholderBaseModel(candidate.BaseModelRaw)) continue;
```

- [ ] **Step 5: Absorb a support asset as identified**

Both `Absorb` call sites (`LoraSorterViewModel.cs:1878` and `:1886`) take the value from the single
`IdentityOf` helper at `:1103`, so the guard belongs there rather than at either call site. Replace
that method:

```csharp
    /// <summary>
    /// The mark a candidate contributes to its own row and to every folder above it.
    /// </summary>
    /// <remarks>
    /// A support asset is <see cref="SortPreviewIdentity.Identified"/> whatever its base model says
    /// (#527). The three marks answer "is this file's destination known", and a VAE's destination is
    /// its kind — it has no base model, never will, and asking it the base-model question would
    /// leave the new VAE\ folder permanently ✗. A wrong question answered honestly is still
    /// misleading.
    /// </remarks>
    private static SortPreviewIdentity IdentityOf(SortCandidate candidate)
        => candidate.AssetKind.IsSupportAsset() ? SortPreviewIdentity.Identified
            : LoraPathBuilder.IsPlaceholderBaseModel(candidate.BaseModelRaw) ? SortPreviewIdentity.Unidentified
            : candidate.BaseModelIsGuess ? SortPreviewIdentity.Guessed
            : SortPreviewIdentity.Identified;
```

Add `using DiffusionNexus.Domain.Enums;` if not present.

- [ ] **Step 6: Run tests**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~Sorter"`
Expected: PASS — the whole sorter namespace.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "fix(sorter): a support asset is identified, not an unidentified LoRA"
```

---

### Task 12: The Viewer badges support assets and can hide them

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/ModelTileViewModel.cs:377`
- Modify: `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs:2770-2833` (`ApplyFilters`), `:614`/`:629` (status line)
- Modify: `DiffusionNexus.UI/Views/LoraViewerView.axaml` (the filter flyout and the tile template)
- Test: `DiffusionNexus.Tests/Viewer/LoraViewerViewModelSupportAssetFilterTests.cs` (create). Mirror the harness in the existing `DiffusionNexus.Tests/Viewer/LoraViewerViewModelBaseModelFilterTests.cs` — it already builds a viewer over tiles and exercises `ApplyFilters`, which is exactly the shape needed here. Read it before writing anything.

**Interfaces:**
- Consumes: `Model.Type` (Task 6), `ModelTypeExtensions` (Task 1).
- Produces: `ModelTileViewModel.IsSupportAsset` (`bool`), `ModelTileViewModel.AssetKindLabel` (`string`); `LoraViewerViewModel.ShowSupportAssets` (`bool`, observable, default `false`), `LoraViewerViewModel.HiddenSupportAssetCount` (`int`).

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Tests/Viewer/LoraViewerViewModelSupportAssetFilterTests.cs`, reusing the construction helpers from `LoraViewerViewModelBaseModelFilterTests.cs`:

```csharp
    /// <summary>
    /// #527: 35 of 328 files in a real library are VAEs and text encoders. They occupy tiles, draw
    /// thumbnail fetches and retry Civitai forever. Hidden by default — but named in the status
    /// line, because a file silently missing from the grid is worse than one that is merely filtered.
    /// </summary>
    [Fact]
    public void SupportAssetsAreHiddenByDefault()
    {
        var vm = GivenViewerWith(
            Tile("MyChar_Pony_v2", ModelType.LORA),
            Tile("Wan2_2_VAE_bf16", ModelType.VAE),
            Tile("clip_g_hidream", ModelType.TextEncoder));

        vm.FilteredTiles.Should().ContainSingle().Which.DisplayName.Should().Be("MyChar_Pony_v2");
        vm.HiddenSupportAssetCount.Should().Be(2);
    }

    [Fact]
    public void TurningTheToggleOnShowsThem()
    {
        var vm = GivenViewerWith(
            Tile("MyChar_Pony_v2", ModelType.LORA),
            Tile("Wan2_2_VAE_bf16", ModelType.VAE));

        vm.ShowSupportAssets = true;

        vm.FilteredTiles.Should().HaveCount(2);
        vm.HiddenSupportAssetCount.Should().Be(0);
    }

    /// <summary>The badge names the kind with the same string the sorter's folder uses.</summary>
    [Fact]
    public void ASupportAssetTileCarriesItsKindLabel()
    {
        var tile = Tile("Wan2_2_VAE_bf16", ModelType.VAE);

        tile.IsSupportAsset.Should().BeTrue();
        tile.AssetKindLabel.Should().Be("VAE");
    }

    [Fact]
    public void ALoraTileCarriesNoBadge()
        => Tile("MyChar_Pony_v2", ModelType.LORA).IsSupportAsset.Should().BeFalse();
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~LoraViewerViewModelFilterTests"`
Expected: FAIL — none of the members exist.

- [ ] **Step 3: Add the tile members**

In `ModelTileViewModel.cs`, beside `ModelTypeDisplay` (line 377):

```csharp
    /// <summary>
    /// Whether this tile is one of the VAEs, text encoders, ControlNets or upscalers a LoRA folder
    /// also holds (#527) — the things that can never match on Civitai and are not what this grid
    /// is for.
    /// </summary>
    public bool IsSupportAsset => ModelEntity?.Type.IsSupportAsset() ?? false;

    /// <summary>
    /// The badge text — the same string the sorter names its destination folder with, so a user
    /// who sees "VAE" here finds the file in "VAE\" there. Empty for an ordinary LoRA.
    /// </summary>
    public string AssetKindLabel => IsSupportAsset ? ModelEntity!.Type.DisplayName() : string.Empty;
```

Raise both alongside `ModelTypeDisplay` at line ~1061:

```csharp
        OnPropertyChanged(nameof(IsSupportAsset));
        OnPropertyChanged(nameof(AssetKindLabel));
```

- [ ] **Step 4: Add the filter**

In `LoraViewerViewModel.cs`, beside the other filter state:

```csharp
    /// <summary>
    /// Whether the grid shows support assets — VAEs, text encoders, ControlNets, upscalers (#527).
    /// Off by default: they can never match on Civitai, so every one of them is a tile that draws
    /// a thumbnail fetch and a sync attempt for nothing. Never a silent disappearance — the status
    /// line names how many are hidden.
    /// </summary>
    [ObservableProperty]
    private bool _showSupportAssets;

    /// <summary>How many tiles the support-asset filter is currently hiding.</summary>
    [ObservableProperty]
    private int _hiddenSupportAssetCount;

    partial void OnShowSupportAssetsChanged(bool value) => ApplyFilters();
```

In `ApplyFilters`, after the NSFW predicate and before the base-model predicate:

```csharp
        if (!ShowSupportAssets)
        {
            query = query.Where(t => !t.IsSupportAsset);
        }
```

and — **above** the `if (filtered.SequenceEqual(FilteredTiles)) return;` early return, not after it, or a pass that changes no tiles would leave the count stale:

```csharp
        // Before the unchanged-result early return: this count describes what the filter is
        // holding back, which is true whether or not the visible set changed this pass.
        HiddenSupportAssetCount = ShowSupportAssets ? 0 : AllTiles.Count(t => t.IsSupportAsset);
```

Update both status-line assignments (lines ~614 and ~629) to name the hidden count when there is one — extract a helper so the two sites cannot drift:

```csharp
    /// <summary>
    /// "Loaded 293 models (312 tiles)", plus "· 35 support assets hidden" when the filter is
    /// holding some back. Naming them is the whole reason hiding them by default is acceptable.
    /// </summary>
    private string BuildLoadedStatus(int modelCount)
    {
        var status = $"Loaded {modelCount} models ({AllTiles.Count} tiles)";
        return HiddenSupportAssetCount > 0
            ? $"{status} · {HiddenSupportAssetCount} support assets hidden"
            : status;
    }
```

Call it in place of both interpolated strings, after `ReplaceTiles(...)` (which runs `ApplyFilters`, so the count is current).

Add `using DiffusionNexus.Domain.Enums;` to both files if not present.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~LoraViewer"`
Expected: PASS.

- [ ] **Step 6: Wire the view**

In `DiffusionNexus.UI/Views/LoraViewerView.axaml`:

- In the filter flyout that hosts the base-model filter, add a `CheckBox` bound to `ShowSupportAssets` with the content `Show support assets (VAE, ControlNet, …)`. Match the surrounding controls' styling exactly — do not introduce new brushes or spacing values.
- In the tile template, add a badge `Border` bound to `AssetKindLabel` with `IsVisible="{Binding IsSupportAsset}"`. Copy the chip styling from `LoraSorterView.axaml:123-127` (`Background="#2F2F33"`, `BorderBrush="#454549"`, `BorderThickness="1"`, `CornerRadius="3"`, `Padding="5,0,5,1"`, `FontSize="10"`, `Opacity="0.85"`) so the two surfaces read as the same label.

- [ ] **Step 7: Build the UI project**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -c Debug`
Expected: 0 errors, 0 warnings. XAML binding errors surface as build warnings here — treat any as a failure.

- [ ] **Step 8: Full suite and commit**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj`
Expected: all green except the two opt-in online canaries, which skip.

```bash
git add -A
git commit -m "feat(viewer): badge support assets and keep them out of the grid by default"
```

---

## Manual smoke (owed before the PR is merged)

Automated tests cannot see the two things this feature is actually judged on.

1. **Sorter preview.** Point the sorter at a real library holding VAEs and upscalers. The After Sorting tree must grow `VAE`, `ControlNet`, `Text Encoder` and `Upscaler` rows carrying their own chips and a ✓; `Unknown` must shrink by roughly the number of support assets; no base-model row may still show a non-LoRA chip. Then **run the sort** and confirm the files physically land in those folders.
2. **Viewer.** The grid loses those tiles, the status line names how many are hidden, the toggle brings them back, and opening one shows `Type: VAE` rather than `Type: LORA` in the detail panel.

Record the outcome on the PR — this repo's convention is that a smoke that was not run is stated as not run.
