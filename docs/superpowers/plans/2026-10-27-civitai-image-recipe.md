# Civitai Image Recipe View (#628) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pasting a Civitai image link (`civitai.com|civitai.red/images/<id>`) into Browse Civitai shows a recipe view (image, SFW/NSFW rating, generation data, resources with weights) and queues the missing LoRAs in one click.

**Architecture:** A new `Services/CivitaiRecipe` folder in `DiffusionNexus.UI` holds pure readers (JPEG EXIF, file texts, ComfyUI LoRA scan, recipe reader, resource matcher) and one service that calls the Civitai images + model-version + model endpoints, downloads the original file into a cache and assembles a `CivitaiImageRecipe`. `CivitaiBrowserViewModel` routes image links to it and shows a `CivitaiRecipeViewModel` in place of the card grid; its rows wrap ordinary `CivitaiResultViewModel` cards so the queue, preflight dialog, waitlist and installed badges are reused unchanged.

**Tech Stack:** .NET 10, Avalonia 11.3, CommunityToolkit.Mvvm, System.Text.Json, xUnit + FluentAssertions + Moq.

**Spec:** `docs/superpowers/specs/2026-10-27-civitai-image-recipe-design.md` (= issue #628 body, plus the SFW/NSFW rule added 2026-10-27).

## Global Constraints

- Branch `feature/civitai-image-recipe-628` (already checked out, based on `develop` 0f511e67). One PR for the whole issue into `develop`. Commit **and push** after every task. `gh auth switch --user Little-God1983` before pushing.
- Do **not** change `ComfyUiPromptTracer` behaviour (Distiller / Batch Upscale output stays as it is).
- A bare number keeps its #630 meaning; only links are image links.
- Never show a default as if it came from the image: unknown values read "weight unknown" / "unknown", never `1` or `0`.
- Rating: `rating = image.BrowsingLevel ?? image.NsfwLevel`; SFW ⇔ `NsfwPolicy.IsSafe(rating)`; label `NsfwPolicy.RatingLabel((NsfwLevel)(rating ?? 0))`. Open on Civitai host: `civitai.red` for NSFW, `civitai.com` for SFW.
- Images call always sends `imageId=<id>&nsfw=X` (without `nsfw=X` an NSFW image comes back as an empty list).
- File format comes from magic bytes, never from the URL extension (the example's `….jpeg` URL serves a PNG).
- Logging: every step goes to the Unified Console (`IUnifiedLogger`, source `"CivitaiRecipe"`, `LogCategory.Network` for API/file, `LogCategory.Download` for the queue).
- Windows only; no cross-platform branches.
- Copy (exact strings):
  - Notice: `Showing the image from your link and the resources it was made with. Filters are ignored. Clear the search box to go back to your filtered search.`
  - Source notes: `Read from the original image file (ComfyUI workflow embedded)` / `Read from the original image file (settings text)` / `Read from the original image file (settings text; LoRA weights from the ComfyUI workflow)`
  - No data: `This image's file carries no generation data, and Civitai does not publish it either. The prompt, settings and LoRA weights are unknown. The list of resources below still comes from Civitai, so the LoRAs can be downloaded.`
  - Send to ComfyUI tooltip: `Coming in a later version`
  - Unlinked row note: `not linked on Civitai`
- Test command: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~<Name>"`.

## Review Focus

1. **JPEG UserComment in UTF-16 big-endian inside a little-endian (`II`) TIFF.** Every sampled Civitai JPEG (2026-10-27) has `UNICODE\0` + UTF-16BE text while the TIFF header is `II`. Decoding by TIFF byte order yields garbage. → Task 3 test `UserComment_Utf16BigEndian_InLittleEndianTiff_IsRead`.
2. **NSFW image pasted.** Without `nsfw=X` Civitai returns no item and the user would read "no such image". → Task 6 test `LoadAsync_AsksForEveryRating`.
3. **Civitai's file name differs from the local one** (`AIM-KREA-V1 (1).safetensors` on Civitai vs `Krea2\Style\AIM-KREA-V1.safetensors` in the graph) and the file carries no hash for it. → Task 5 test `NameMatch_IgnoresFoldersAndCopySuffix`.
4. **Original served as PNG under a `.jpeg` URL** (and the reverse). → Task 3 test `Png_NamedJpeg_IsReadAsPng`, Task 6 caches by detected type.
5. **A weight that cannot be read** (`<lora:x:abc>`, a graph strength wired as a link) must read "weight unknown", not `1`. → Task 4 tests `A1111Tag_UnreadableWeight_IsUnknown`, `GraphStrengthAsLink_IsUnknown`.

---

### Task 1: Civitai API surface for single images

**Files:**
- Modify: `DiffusionNexus.Civitai/CivitaiQueryParameters.cs` (`CivitaiImagesQuery`, ~98-143)
- Modify: `DiffusionNexus.Civitai/Models/CivitaiModelImage.cs` (`CivitaiModelImage`)
- Modify: `DiffusionNexus.Civitai/Models/CivitaiModelFile.cs` (`CivitaiFileHashes`, ~77)
- Modify: `DiffusionNexus.Civitai/CivitaiApiGateway.cs:183` (stale comment "unused in production")
- Test: `DiffusionNexus.Tests/Civitai/CivitaiQueryParametersTests.cs`, `DiffusionNexus.Tests/Civitai/CivitaiClientTests.cs`, new `DiffusionNexus.Tests/Civitai/CivitaiFileHashesTests.cs`

**Interfaces:**
- Produces: `CivitaiImagesQuery.ImageId` (`long?`, emits `imageId=`), `CivitaiImagesQuery.NsfwLevel` (`string?`, emits `nsfw=<value>`; when set, the `Nsfw` bool is not emitted); `CivitaiModelImage.BrowsingLevel` (`int?`), `.BaseModel` (`string?`), `.ModelVersionIds` (`IReadOnlyList<int>?`); `CivitaiModelImage.NsfwLevel` now reads a word as null; `CivitaiFileHashes.AutoV3`, `.SHA256_12`, and `IEnumerable<string> AllValues()` (every non-empty hash value).

- [ ] **Step 1: Write the failing tests**

```csharp
// CivitaiQueryParametersTests
[Fact]
public void ImagesQuery_ImageIdAndNsfwLevel_AreSent()
{
    new CivitaiImagesQuery { ImageId = 143992601, NsfwLevel = "X", Nsfw = true }.ToQueryString()
        .Should().Be("imageId=143992601&nsfw=X");
}

// CivitaiClientTests (stub handler, same style as the file's existing tests) — payload copied from the
// live images?imageId=143992601&nsfw=X answer (2026-10-27), where nsfwLevel is a word:
[Fact]
public async Task GetImagesAsync_SingleImagePayload_ReadsRatingAndResources()
{
    // json: {"items":[{"id":143992601,"url":"https://image.civitai.com/x/original=true/a.jpeg","width":1256,"height":1672,
    //   "nsfwLevel":"None","browsingLevel":1,"type":"image","nsfw":false,"postId":31277568,"meta":null,
    //   "username":"purplelady","baseModel":"Krea 2 Turbo","modelVersionIds":[3064297,3072964,3110766,3113770,3343424,3357558]}],"metadata":{}}
    var page = await client.GetImagesAsync(new CivitaiImagesQuery { ImageId = 143992601, NsfwLevel = "X" });
    var image = page.Items.Should().ContainSingle().Subject;
    image.NsfwLevel.Should().BeNull("this endpoint sends a word, the rating is browsingLevel");
    image.BrowsingLevel.Should().Be(1);
    image.BaseModel.Should().Be("Krea 2 Turbo");
    image.ModelVersionIds.Should().Equal(3064297, 3072964, 3110766, 3113770, 3343424, 3357558);
    handler.LastRequestUri!.Query.Should().Be("?imageId=143992601&nsfw=X");
}

// CivitaiFileHashesTests
[Fact]
public void AllValues_ListsEveryHashKindIncludingAutoV3AndSha256_12()
{
    var hashes = JsonSerializer.Deserialize<CivitaiFileHashes>(
        """{"AutoV1":"F19D324D","AutoV2":"990D0DF641","SHA256":"990D0DF6418709EF","CRC32":"FC9A8795","BLAKE3":"D514","AutoV3":"47A98D0C95BE","SHA256_12":"990D0DF64187"}""")!;
    hashes.AllValues().Should().BeEquivalentTo("F19D324D", "990D0DF641", "990D0DF6418709EF", "FC9A8795", "D514", "47A98D0C95BE", "990D0DF64187");
}
```

- [ ] **Step 2: Run them, expect FAIL** (compile errors on the new members).

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CivitaiQueryParametersTests|FullyQualifiedName~CivitaiClientTests|FullyQualifiedName~CivitaiFileHashesTests"`

- [ ] **Step 3: Implement.** `NsfwLevel` on `CivitaiModelImage` gets `[JsonConverter(typeof(TolerantNullableInt32JsonConverter))]` (numbers from the model endpoints still read; a word reads null instead of failing the page). `BrowsingLevel` uses the same converter. Keep the existing XML-doc tone: say *why* (`nsfwLevel` is a word on `/images`). Update the gateway comment: images is now used by the recipe view, still uncached.

- [ ] **Step 4: Run the tests, expect PASS;** also run the whole `Civitai` folder: `--filter "FullyQualifiedName~DiffusionNexus.Tests.Civitai"`.

- [ ] **Step 5: Commit + push** — `feat(civitai): single-image query, image rating and resource ids, AutoV3/SHA256_12 hashes (#628)`

---

### Task 2: Image links in the search box

**Files:**
- Modify: `DiffusionNexus.UI/Services/CivitaiBrowser/CivitaiLinkQuery.cs`
- Test: `DiffusionNexus.Tests/Viewer/CivitaiLinkQueryTests.cs`

**Interfaces:**
- Produces: `CivitaiLinkQuery.ImageId` (`long?`), `CivitaiLinkQuery.IsImageLink` (`ImageId is not null`); `IsLinkWithoutIds` is false for an image link; `ToString()` → `"image link (image 143992601)"`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("https://civitai.red/images/143992601")]
[InlineData("civitai.com/images/143992601")]
[InlineData("https://www.civitai.com/images/143992601?postId=31277568")]
public void ImageLink_YieldsTheImageId(string text)
{
    CivitaiLinkQuery.TryParse(text, out var q).Should().BeTrue();
    q!.ImageId.Should().Be(143992601);
    q.IsImageLink.Should().BeTrue();
    q.IsLinkWithoutIds.Should().BeFalse();
    q.ModelId.Should().BeNull();
    q.VersionId.Should().BeNull();
}

[Fact]
public void BareNumber_IsNeverAnImage()
{
    CivitaiLinkQuery.TryParse("143992601", out var q).Should().BeTrue();
    q!.IsImageLink.Should().BeFalse();
}

[Fact]
public void UserPageLink_StaysALinkWithoutIds()
{
    CivitaiLinkQuery.TryParse("https://civitai.com/user/purplelady", out var q).Should().BeTrue();
    q!.IsLinkWithoutIds.Should().BeTrue();
}
```

- [ ] **Step 2: Run, expect FAIL.** `--filter "FullyQualifiedName~CivitaiLinkQueryTests"`
- [ ] **Step 3: Implement** — after the host check, match the path with `^/images/(\d+)/?$` (case-insensitive) and set `ImageId` via `long.TryParse`; ids ≤ 0 are not image links. Update the class doc (a third kind of link, #628).
- [ ] **Step 4: Run, expect PASS** (the existing #630 tests in the file must still pass, and `CivitaiLinkLookupTests`).
- [ ] **Step 5: Commit + push** — `feat(browser): recognise Civitai image links (#628)`

---

### Task 3: Read the original file's texts (PNG chunks, JPEG EXIF)

**Files:**
- Create: `DiffusionNexus.UI/Services/CivitaiRecipe/JpegExifReader.cs`
- Create: `DiffusionNexus.UI/Services/CivitaiRecipe/RecipeFileReader.cs`
- Test: `DiffusionNexus.Tests/Viewer/CivitaiRecipe/JpegExifReaderTests.cs`, `DiffusionNexus.Tests/Viewer/CivitaiRecipe/RecipeFileReaderTests.cs`

**Interfaces:**
- Produces:
  - `internal static class JpegExifReader { public static string? ReadUserComment(string filePath); }`
  - `public enum RecipeFileFormat { Unknown, Png, Jpeg, WebP, Video }`
  - `internal sealed record RecipeFileText(RecipeFileFormat Format, IReadOnlyDictionary<string, string> Texts);` — keys `"prompt"`, `"workflow"`, `"parameters"` as found.
  - `internal static class RecipeFileReader { public static RecipeFileFormat DetectFormat(ReadOnlySpan<byte> head); public static RecipeFileText Read(string filePath); }`

Format by magic bytes: PNG `89 50 4E 47 0D 0A 1A 0A`; JPEG `FF D8`; WebP `RIFF....WEBP`; MP4 (`....ftyp`) / WebM (`1A 45 DF A3`) → `Video`. PNG → `PngChunkReader.ReadTextChunks`. JPEG → `{"parameters": ReadUserComment}` when non-empty. WebP / Video / Unknown → empty texts (WebP is optional per spec; not built).

EXIF walk (the algorithm the tests do not determine): find the APP1 segment (`FF E1`) whose payload starts `Exif\0\0`; TIFF header `II`/`MM` sets the byte order for **offsets only**; IFD0 → tag `0x8769` (Exif IFD pointer) → tag `0x9286` (UserComment, type UNDEFINED, count = bytes, value offset relative to the TIFF header). Payload = 8-byte charset prefix + text:
- `UNICODE\0`: UTF-16. Endianness from the text, not the TIFF header: a BOM wins; else if byte[0] == 0 and byte[1] != 0 → big-endian, else little-endian.
- `ASCII\0\0\0` or 8 zero bytes: UTF-8.
Trim trailing `\0`. Any malformed offset → null (never throws).

- [ ] **Step 1: Write the failing tests.** Build JPEGs in the test with a small helper `static byte[] JpegWithUserComment(bool bigEndianTiff, byte[] prefix, byte[] text)` writing SOI, one APP1 Exif segment (IFD0 with one entry 0x8769 → Exif IFD with one entry 0x9286), EOI.

```csharp
[Fact]
public void UserComment_Utf16BigEndian_InLittleEndianTiff_IsRead() // Review Focus 1
{
    var path = Write(JpegWithUserComment(bigEndianTiff: false, "UNICODE\0"u8.ToArray(),
        Encoding.BigEndianUnicode.GetBytes("masterpiece\nSteps: 20, Sampler: Euler a, Seed: 1")));
    JpegExifReader.ReadUserComment(path).Should().Be("masterpiece\nSteps: 20, Sampler: Euler a, Seed: 1");
}

[Fact] public void UserComment_Utf16LittleEndian_IsRead() { /* Encoding.Unicode bytes, II TIFF → same text */ }
[Fact] public void UserComment_InBigEndianTiff_IsRead() { /* MM TIFF + UTF-16BE → same text */ }
[Fact] public void UserComment_AsciiPrefix_IsRead() { /* "ASCII\0\0\0" + UTF-8 bytes */ }
[Fact] public void Jpeg_WithoutExif_ReturnsNull() { /* FF D8 FF D9 only */ }
[Fact] public void Jpeg_WithTruncatedExif_ReturnsNullAndDoesNotThrow() { /* cut the segment after the IFD0 count */ }

// RecipeFileReaderTests
[Fact]
public void Png_NamedJpeg_IsReadAsPng() // Review Focus 4
{
    // PNG bytes (signature + IHDR + tEXt "parameters" + IEND) written to "x.jpeg"
    var result = RecipeFileReader.Read(pathNamedJpeg);
    result.Format.Should().Be(RecipeFileFormat.Png);
    result.Texts["parameters"].Should().StartWith("a cat");
}

[Fact] public void Jpeg_PutsUserCommentUnderParameters() { /* Texts["parameters"] == comment */ }

[Theory]
[InlineData(new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70 }, RecipeFileFormat.Video)]
[InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, RecipeFileFormat.WebP)]
[InlineData(new byte[] { 0x47, 0x49, 0x46 }, RecipeFileFormat.Unknown)]
public void DetectFormat_ByMagicBytes(byte[] head, RecipeFileFormat expected)
    => RecipeFileReader.DetectFormat(head).Should().Be(expected);
```

- [ ] **Step 2: Run, expect FAIL.** `--filter "FullyQualifiedName~CivitaiRecipe"`
- [ ] **Step 3: Implement** both classes (namespace `DiffusionNexus.UI.Services.CivitaiRecipe`). `PngChunkReader` is reused as is.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit + push** — `feat(recipe): read generation text from PNG chunks and JPEG EXIF UserComment (#628)`

---

### Task 4: Recipe reader (graph trace, settings text, LoRA scan)

**Files:**
- Create: `DiffusionNexus.UI/Services/CivitaiRecipe/ImageRecipeData.cs` (records + enum below)
- Create: `DiffusionNexus.UI/Services/CivitaiRecipe/ComfyLoraScan.cs`
- Create: `DiffusionNexus.UI/Services/CivitaiRecipe/ImageRecipeReader.cs`
- Modify: `DiffusionNexus.UI/Services/ImageMetadataParser.cs` — make `ParseA1111Parameters` `internal static` (signature unchanged), add `internal static IReadOnlyDictionary<string, string> ReadA1111Settings(string parameters)`; move the prompt-block / settings-line split into one private `SplitA1111(string parameters) → (string PromptBlock, string SettingsLine)` that both use. No behaviour change for existing callers.
- Modify: `DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` — `<None Include="Viewer\TestData\CivitaiRecipe\**" CopyToOutputDirectory="PreserveNewest" />`
- Fixtures (already committed with this plan): `DiffusionNexus.Tests/Viewer/TestData/CivitaiRecipe/143992601-prompt.json` (the example's `prompt` chunk: Power Lora Loader with 47 entries, 5 `on`, `ClownsharKSampler_Beta`), `143992601-parameters.txt` (its `parameters` chunk).
- Test: `DiffusionNexus.Tests/Viewer/CivitaiRecipe/ComfyLoraScanTests.cs`, `ImageRecipeReaderTests.cs`; existing `ImageMetadataParser` tests must still pass.

**Interfaces:**
- Produces (in `ImageRecipeData.cs`):
```csharp
public sealed record RecipeLoraUse(string Name, string? FileName, double? Weight, string? Hash);
// Name: file stem without folders and extension ("AIM-KREA-V1"); FileName: as written in the file or null.
public enum RecipeDataSource { None, ComfyUiWorkflow, SettingsText }
public sealed record ImageRecipeData
{
    public RecipeFileFormat Format { get; init; }
    public RecipeDataSource Source { get; init; }
    public string? SourceNote { get; init; }        // exact copy from Global Constraints; null when Source == None
    public string? Prompt { get; init; }
    public string? NegativePrompt { get; init; }
    public string? Checkpoint { get; init; }
    public string? Vae { get; init; }
    public string? Sampler { get; init; }
    public string? Scheduler { get; init; }
    public int? Steps { get; init; }
    public double? Cfg { get; init; }
    public long? Seed { get; init; }
    public IReadOnlyList<RecipeLoraUse> Loras { get; init; } = [];
    public string? ReadError { get; init; }         // set when a chunk was present but unreadable
    public static ImageRecipeData Empty(RecipeFileFormat format, string? readError = null);
}
```
- `internal sealed record ComfyGraphFacts(IReadOnlyList<RecipeLoraUse> Loras, string? Checkpoint, string? Vae);`
- `internal static class ComfyLoraScan { public static ComfyGraphFacts Scan(IReadOnlyDictionary<string, JsonElement> graph); }`
- `internal static class ImageRecipeReader { public static ImageRecipeData Read(string filePath); public static ImageRecipeData FromTexts(RecipeFileText file); }`

Scan rules (spec §Behaviour 2): every node, any position. `LoraLoader` / `LoraLoaderModelOnly`: `lora_name`, weight `strength_model`. Any `class_type` containing `Power Lora Loader`: every input whose value is an object with `lora` — skip unless `"on": true`; weight `strength`. A strength that is not a JSON number (a link array) → `Weight = null`. `Checkpoint` = the file of the only `CheckpointLoaderSimple`/`CheckpointLoader`/`UNETLoader`/`UnetLoaderGGUF` node when exactly one exists, else null; `Vae` likewise from the only `VAELoader`. Name = `Path.GetFileNameWithoutExtension` after normalising `/` and `\`.

Reader rules:
1. `graph` = `Texts["prompt"]` parsed as `Dictionary<string, JsonElement>`; bad JSON → `ReadError`, continue with the text.
2. `trace = ComfyUiPromptTracer.Trace(graph, null, 0, 0)`; if `trace.HasData` → `Source = ComfyUiWorkflow`, prompt/settings from the trace, `Vae = facts.Vae ?? text VAE`.
3. Else if the `parameters` text parses with data → `Source = SettingsText`: prompt/negative/sampler/scheduler/steps/cfg/seed/checkpoint from `ParseA1111Parameters`, `Vae` from `ReadA1111Settings(...)["VAE"]`, `Checkpoint ??= facts.Checkpoint`.
4. Else if the graph scan found LoRAs or a checkpoint → `Source = ComfyUiWorkflow` with only those.
5. LoRAs: graph scan when it found any (graph wins); else the text's `<lora:name:w>` tags read by the reader itself (weight = first `:`-segment after the name parsed with InvariantCulture, else null — do **not** use `LoraInfo.StrengthModel`, which defaults to 1.0). Each use gets `Hash` by case-insensitive name from the settings `Lora hashes` value (`"name: hash, name: hash"`) or, failing that, the `Hashes` JSON key `lora:<name>`.
6. SourceNote: ComfyUiWorkflow → "…(ComfyUI workflow embedded)"; SettingsText with graph LoRAs → "…(settings text; LoRA weights from the ComfyUI workflow)"; SettingsText otherwise → "…(settings text)".

- [ ] **Step 1: Write the failing tests**

```csharp
// ComfyLoraScanTests
[Fact]
public void ExampleGraph_FindsTheFiveSwitchedOnLoras()
{
    var facts = ComfyLoraScan.Scan(LoadGraph("143992601-prompt.json"));
    facts.Loras.Select(l => (l.Name, l.Weight)).Should().BeEquivalentTo(new (string, double?)[]
    {
        ("Purple_Graphics_KR2", 1), ("Purple_Grainy_Kr2_AM", 0.5), ("ass_krea2_loraholic", 3),
        ("AIM-KREA-V1", 0.65), ("godago_4ho", 1),
    });
    facts.Checkpoint.Should().Be(@"Krea-2\krea2TurboOfficialComfy_krea2TurboFp8.safetensors");
    facts.Vae.Should().Be("qwen_image_vae.safetensors");
}
[Fact] public void CoreLoraLoader_UsesStrengthModel() { /* LoraLoader strength_model 0.8, strength_clip 0.3 → 0.8 */ }
[Fact] public void GraphStrengthAsLink_IsUnknown() { /* strength_model: ["12", 0] → Weight null */ } // Review Focus 5
[Fact] public void TwoCheckpointLoaders_GiveNoCheckpoint() { }

// ImageRecipeReaderTests
[Fact]
public void ExampleFile_SettingsFromText_WeightsFromGraph_HashesFromText()
{
    var data = ImageRecipeReader.FromTexts(new RecipeFileText(RecipeFileFormat.Png, new Dictionary<string, string>
    {
        ["prompt"] = Fixture("143992601-prompt.json"), ["parameters"] = Fixture("143992601-parameters.txt"),
    }));
    data.Source.Should().Be(RecipeDataSource.SettingsText); // ClownsharKSampler: the shared tracer finds no core sampler
    data.SourceNote.Should().Be("Read from the original image file (settings text; LoRA weights from the ComfyUI workflow)");
    data.Prompt.Should().StartWith("Photograph of a solitary cybernetic sniper");
    data.Steps.Should().Be(8);
    data.Cfg.Should().Be(1.0);
    data.Seed.Should().Be(832350830334358);
    data.Sampler.Should().Be("exponential/ddim_beta57");
    data.Checkpoint.Should().Be(@"Krea-2\krea2TurboOfficialComfy_krea2TurboFp8.safetensors");
    data.Vae.Should().Be("qwen_image_vae.safetensors");
    data.Loras.Single(l => l.Name == "Purple_Graphics_KR2").Hash.Should().Be("990d0df641");
    data.Loras.Single(l => l.Name == "ass_krea2_loraholic").Weight.Should().Be(3);
}
[Fact] public void TextOnly_TakesWeightsFromLoraTags() { /* "a cat <lora:foo:0.7>\nSteps: 20, Seed: 5" → foo 0.7, Source SettingsText, note "(settings text)" */ }
[Fact] public void A1111Tag_UnreadableWeight_IsUnknown() { /* "<lora:foo:abc>" → Weight null */ } // Review Focus 5
[Fact] public void CoreKSamplerGraph_UsesTheTrace() { /* minimal graph KSampler+CheckpointLoaderSimple+CLIPTextEncode → Source ComfyUiWorkflow, note "(ComfyUI workflow embedded)" */ }
[Fact] public void BrokenPromptJson_FallsBackToTextAndReportsTheError() { }
[Fact] public void NoTexts_GivesNone() { /* Source None, SourceNote null, Loras empty */ }
```

- [ ] **Step 2: Run, expect FAIL.** `--filter "FullyQualifiedName~CivitaiRecipe|FullyQualifiedName~ImageMetadataParser"`
- [ ] **Step 3: Implement** the records, the scan, the reader and the `ImageMetadataParser` refactor.
- [ ] **Step 4: Run, expect PASS** — including every existing `ImageMetadataParser` / `Distiller` test (`--filter "FullyQualifiedName~Distiller|FullyQualifiedName~ImageMetadata"`).
- [ ] **Step 5: Commit + push** (include the csproj line) — `feat(recipe): recipe reader with graph LoRA scan and settings-text fallback (#628)`

---

### Task 5: Match weights to Civitai resources

**Files:**
- Create: `DiffusionNexus.UI/Services/CivitaiRecipe/RecipeResourceMatcher.cs`
- Test: `DiffusionNexus.Tests/Viewer/CivitaiRecipe/RecipeResourceMatcherTests.cs`

**Interfaces:**
- Consumes: `RecipeLoraUse` (Task 4), `CivitaiFileHashes.AllValues()` (Task 1).
- Produces:
```csharp
internal sealed record RecipeMatchResult(IReadOnlyDictionary<int, RecipeLoraUse> ByVersionId, IReadOnlyList<RecipeLoraUse> Unlinked);
internal static class RecipeResourceMatcher
{
    public static RecipeMatchResult Match(IReadOnlyList<CivitaiModelVersion> versions, IReadOnlyList<RecipeLoraUse> uses);
}
```
Pass 1, per use with a `Hash`: the version having any file whose `AllValues()` contains it (case-insensitive). Pass 2, remaining uses: file-stem equality (case-insensitive) between `use.Name` and each file name's stem with a trailing ` (n)` removed (regex `\s\(\d+\)$`). A version takes at most one use; a use at most one version. Uses left over → `Unlinked`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void HashMatch_OnAutoV2() { /* use Hash "990d0df641" ↔ file AutoV2 "990D0DF641" */ }
[Fact] public void HashMatch_OnAutoV3AndSha256_12() { /* "47a98d0c95be" ↔ AutoV3; "990d0df64187" ↔ SHA256_12 */ }
[Fact]
public void NameMatch_IgnoresFoldersAndCopySuffix() // Review Focus 3
{
    var version = Version(3343424, file: "AIM-KREA-V1 (1).safetensors");
    var use = new RecipeLoraUse("AIM-KREA-V1", @"Krea2\Style\AIM-KREA-V1.safetensors", 0.65, null);
    RecipeResourceMatcher.Match([version], [use]).ByVersionId[3343424].Weight.Should().Be(0.65);
}
[Fact] public void HashBeatsName() { /* use named like version A but hashed like version B → B */ }
[Fact] public void UnmatchedUse_IsUnlinked_AndUnmatchedVersionHasNoEntry() { }
```

- [ ] **Step 2: Run, expect FAIL.**  - [ ] **Step 3: Implement.**  - [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit + push** — `feat(recipe): match LoRA weights to Civitai resources by hash, then file name (#628)`

---

### Task 6: Recipe service (API calls, original-file cache, assembly)

**Files:**
- Create: `DiffusionNexus.UI/Services/CivitaiRecipe/CivitaiImageRecipe.cs`
- Create: `DiffusionNexus.UI/Services/CivitaiRecipe/CivitaiImageRecipeService.cs`
- Test: `DiffusionNexus.Tests/Viewer/CivitaiRecipe/CivitaiImageRecipeServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 3, 4, 5.
- Produces:
```csharp
public sealed record CivitaiRecipeResource(int VersionId, CivitaiModelVersion? Version, CivitaiModel? Model, RecipeLoraUse? Use, string? LoadError);
public sealed record CivitaiImageRecipe(
    long ImageId, CivitaiModelImage Image, int? Rating, string? OriginalFilePath, ImageRecipeData Data,
    IReadOnlyList<CivitaiRecipeResource> Resources, IReadOnlyList<RecipeLoraUse> UnlinkedLoras)
{
    public bool IsSafe => NsfwPolicy.IsSafe(Rating);
    public string RatingLabel => NsfwPolicy.RatingLabel((NsfwLevel)(Rating ?? 0));
    public string CivitaiUrl => $"https://{(IsSafe ? "civitai.com" : "civitai.red")}/images/{ImageId}";
}
public sealed record CivitaiImageRecipeLoadResult(CivitaiImageRecipe? Recipe, string? Message);
public sealed class CivitaiImageRecipeService
{
    public CivitaiImageRecipeService(ICivitaiClient client, HttpClient http, string cacheDirectory, IUnifiedLogger? logger);
    public static string DefaultCacheDirectory { get; } // %LocalAppData%\DiffusionNexus\Cache\civitai-recipes
    public Task<CivitaiImageRecipeLoadResult> LoadAsync(long imageId, string? apiKey, CancellationToken ct);
}
```
Flow: `GetImagesAsync(new() { ImageId = id, NsfwLevel = "X" })`; no item → `Recipe = null`, `Message = "Civitai has no public image with ID {id}."`. `Rating = image.BrowsingLevel ?? image.NsfwLevel`. Resources: for every `ModelVersionIds` entry in order, in parallel: `GetModelVersionAsync` → null gives `LoadError = "not found on Civitai"`; then `GetModelAsync(version.ModelId)` → null or `Mode != null` gives `LoadError = "model no longer available on Civitai"`; an exception gives `LoadError = ex.Message` (one failure never fails the others; cancellation still propagates). Row `Version` = the model's own entry with that id when listed, else the standalone version. Original: skipped when `image.Type == "video"` (Data = Empty(Video)); cache hit = a non-empty `<cacheDir>/<id>.*` file; otherwise GET `image.Url`, detect format from the first bytes, save as `<id>.png|.jpg|.webp|.bin`. Download failure → `Data = ImageRecipeData.Empty(Unknown, "Could not download the original image file ({message}).")`, resources still returned. Then `ImageRecipeReader.Read`, then `RecipeResourceMatcher.Match` over the loaded versions. Log: link/image found (rating, type, n resource ids), each API call with ms, cache hit/download size + detected format, source chosen, "{matched} of {n} weights matched, {unlinked} not linked on Civitai".

- [ ] **Step 1: Write the failing tests** (Moq `ICivitaiClient` as in `CivitaiLinkLookupTests`; a stub `HttpMessageHandler` serving a small PNG built with the Task 3 helper; temp cache dir per test)

```csharp
[Fact]
public async Task LoadAsync_AsksForEveryRating() // Review Focus 2
{
    await Service().LoadAsync(147036988, null, default);
    _client.Verify(c => c.GetImagesAsync(It.Is<CivitaiImagesQuery>(q => q.ImageId == 147036988 && q.NsfwLevel == "X"),
        It.IsAny<string?>(), It.IsAny<CancellationToken>()));
}
[Fact] public async Task UnknownImage_GivesMessageAndNoRecipe() { /* Message "Civitai has no public image with ID 5." */ }
[Fact] public async Task NsfwImage_IsNsfwWithCivitaiRedLink() { /* BrowsingLevel 8 → IsSafe false, RatingLabel "X", CivitaiUrl "https://civitai.red/images/7" */ }
[Fact] public async Task SafeImage_IsSfwWithCivitaiComLink() { /* BrowsingLevel 2 → IsSafe true, "PG-13", civitai.com */ }
[Fact] public async Task OneResourceFailing_KeepsTheOthers() { /* 2 ids, one GetModelVersionAsync throws HttpRequestException("429") → that row LoadError "429", other row has Model */ }
[Fact] public async Task VideoPost_IsNotDownloaded_AndHasNoData() { /* handler never called; Data.Source None; Data.Format Video */ }
[Fact] public async Task PngServedUnderJpegUrl_IsCachedAsPng_AndReusedNextTime() { /* file <id>.png; second LoadAsync makes no HTTP call */ }
[Fact] public async Task DownloadFailure_StillListsResources() { /* handler 500 → Data.ReadError starts "Could not download the original image file", Resources.Count == 2 */ }
[Fact] public async Task WeightsAreMatchedOntoResources() { /* PNG with parameters "<lora:foo:0.7>" + Lora hashes "foo: abc1234567"; version file AutoV2 ABC1234567 → Resources[0].Use.Weight 0.7 */ }
```

- [ ] **Step 2: Run, expect FAIL.**  - [ ] **Step 3: Implement.**  - [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit + push** — `feat(recipe): load an image recipe from Civitai and the original file (#628)`

---

### Task 7: Recipe view model

**Files:**
- Create: `DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiRecipeViewModel.cs`
- Create: `DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiRecipeResourceRowViewModel.cs`
- Test: `DiffusionNexus.Tests/Viewer/CivitaiRecipeViewModelTests.cs`

**Interfaces:**
- Consumes: `CivitaiImageRecipe` (Task 6), `CivitaiResultViewModel`, `CivitaiVersionPickItemViewModel`, `CivitaiInstalledIndex`, `IClipboardService`.
- Produces:
```csharp
public partial class CivitaiRecipeViewModel : ObservableObject
{
    public CivitaiRecipeViewModel(CivitaiImageRecipe recipe, CivitaiInstalledIndex installed, bool showNsfw,
        Func<CivitaiModel, CivitaiResultViewModel> cardFactory, IClipboardService? clipboard = null);
    public CivitaiImageRecipe Recipe { get; }
    public ObservableCollection<CivitaiRecipeResourceRowViewModel> Rows { get; } // Civitai order, then unlinked
    public string Title { get; }            // "Image 143992601 by purplelady"
    public string Details { get; }          // "1256 × 1672 · Krea 2 Turbo"
    public string RatingBadge { get; }      // "SFW · PG" / "NSFW · X" / "NSFW · unrated"
    public bool IsSafe { get; }
    [ObservableProperty] bool IsPreviewHidden;   // !showNsfw && !IsSafe
    [ObservableProperty] Bitmap? PreviewImage;
    public bool HasGenerationData { get; }  // Data.Source != None
    public string? SourceNote { get; }
    public string NoDataWarning { get; }    // exact copy from Global Constraints
    public string SamplerLine { get; }      // "exponential/ddim_beta57" or "sampler · scheduler"; "unknown" when both null
    public string StepsCfgSeedLine { get; } // "8 · 1 · 832350830334358", each unknown part "unknown"
    [ObservableProperty] bool IsPromptExpanded;
    public int SelectedCount { get; }
    public string FooterText { get; }       // "4 selected · 668 MB"
    public string AddToQueueText { get; }   // "Add 4 to queue"
    public bool CanSendToComfyUi => false;
    public string SendToComfyUiTip => "Coming in a later version";
    public Action<List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)>>? EnqueueHandler { get; set; }
    public Action<string>? UrlOpener { get; set; }
    public List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> SelectedPairs();
    public void ApplyInstalledIndex(CivitaiInstalledIndex index); // updates rows; redoes default ticks while untouched
    public void ApplyNsfwPreference(bool showNsfw);
    public Task LoadPreviewAsync(CancellationToken ct);           // Bitmap.DecodeToWidth(original, 600) off the UI thread
    [RelayCommand] Task CopyPromptAsync(); [RelayCommand] void TogglePrompt(); [RelayCommand] void OpenOnCivitai();
    [RelayCommand] void AddToQueue(); // EnqueueHandler(SelectedPairs())
}
public partial class CivitaiRecipeResourceRowViewModel : ObservableObject
{
    public string Name { get; } public string VersionLabel { get; }  // "v1.0 · LoRA"
    public string BaseModel { get; } public string SizeDisplay { get; }
    public string WeightDisplay { get; }   // "3", "0.5", "0.65" (InvariantCulture "0.###"); LoRA w/o use "weight unknown"; non-LoRA "—"
    public string? TypeBadge { get; }      // non-LoRA type upper-cased ("CHECKPOINT", "VAE"); null for LoRA/LoCon/DoRA
    public string? Note { get; }           // "not linked on Civitai" / LoadError
    public bool CanTick { get; }           // LoRA/LoCon/DoRA && Card != null && Pick != null
    [ObservableProperty] bool IsSelected; [ObservableProperty] bool IsInstalled;
    public bool ShowEarlyAccessBadge { get; } public bool IsPermanentlyPaid { get; }
    public CivitaiResultViewModel? Card { get; } public CivitaiVersionPickItemViewModel? Pick { get; }
    public string? CivitaiUrl { get; }     // model page with ?modelVersionId=
}
```
Row source: a resource with `Model` → `Card = cardFactory(Model)`, `Pick = Card.Versions.FirstOrDefault(v => v.Version.Id == VersionId)` (`Pick == null` → `CanTick = false`, `Note = "version not listed on the model page"`). LoRA types: `CivitaiModelType.LORA`, `LoCon`, `DoRA`. Default tick: `CanTick && !IsInstalled`.

- [ ] **Step 1: Write the failing tests** (build `CivitaiImageRecipe` by hand; `cardFactory = m => new CivitaiResultViewModel(m, true)`; models without images so no preview fetch starts)

```csharp
[Fact]
public void ExampleShape_TicksMissingLoras_NotCheckpointNorInstalled()
{
    // 4 LoRA resources (one installed via CivitaiInstalledIndex([3343424], [])) + 1 Checkpoint, weights 3/0.5/1/0.65
    vm.Rows.Where(r => r.IsSelected).Select(r => r.Name).Should().Equal("Ass and Thighs", "Purple Grainy", "Purple Graphics");
    vm.Rows.Single(r => r.TypeBadge == "CHECKPOINT").CanTick.Should().BeFalse();
    vm.Rows.Single(r => r.TypeBadge == "CHECKPOINT").WeightDisplay.Should().Be("—");
    vm.Rows.Select(r => r.WeightDisplay).Should().StartWith(new[] { "3", "0.5", "1", "0.65" });
    vm.AddToQueueText.Should().Be("Add 3 to queue");
    vm.CanSendToComfyUi.Should().BeFalse();
}
[Fact] public void LoraWithoutUse_ShowsWeightUnknown() { }
[Fact] public void UnlinkedLora_IsAnUntickableRowWithNote() { /* Note "not linked on Civitai", WeightDisplay "0.8" */ }
[Fact] public void FailedResource_IsAnUntickableRowWithItsError() { }
[Fact] public void NsfwRecipe_HidesPreviewUntilShowNsfw() { /* Rating 8, showNsfw false → IsPreviewHidden; ApplyNsfwPreference(true) → false; RatingBadge "NSFW · X" */ }
[Fact] public void SfwRecipe_BadgeAndPreviewShown() { /* Rating 1 → "SFW · PG", IsPreviewHidden false with showNsfw false */ }
[Fact] public void NoData_ShowsWarningAndUnknownLines() { /* HasGenerationData false, StepsCfgSeedLine "unknown · unknown · unknown" */ }
[Fact] public void AddToQueue_PassesSelectedPairs() { /* EnqueueHandler receives (card, pick) for ticked rows only */ }
[Fact] public void InstalledSnapshotArrivingLater_UnticksUntouchedRows_KeepsUserTicks() { }
[Fact] public void FooterCountsAndSize() { /* "2 selected · 236.6 MB" from pick SizeBytes using CivitaiVersionPickItemViewModel.FormatSize */ }
```

- [ ] **Step 2: Run, expect FAIL.**  - [ ] **Step 3: Implement.**  - [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit + push** — `feat(recipe): recipe view model with resource rows, rating and queue selection (#628)`

---

### Task 8: Browser integration and the recipe view

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiBrowserViewModel.cs` (`SearchAsync` ~284, `LoadLinkAsync` ~341, `RefreshInstalledSetAsync` ~1750, `OnShowNsfwChanged` ~960)
- Create: `DiffusionNexus.UI/Views/CivitaiBrowser/CivitaiRecipeView.axaml` (+ `.axaml.cs`)
- Modify: `DiffusionNexus.UI/Views/CivitaiBrowser/CivitaiBrowserView.axaml` (results area ~325-350)
- Test: `DiffusionNexus.Tests/Viewer/CivitaiBrowserRecipeTests.cs`

**Interfaces:**
- Consumes: Tasks 2, 6, 7; existing `EnqueueWithPreflightPromptAsync`, `CreateResultCard`, `GetApiKeyAsync`.
- Produces on `CivitaiBrowserViewModel`: `[ObservableProperty] CivitaiRecipeViewModel? Recipe`, `bool IsRecipeView => Recipe is not null` (notify on change), `internal Func<CivitaiImageRecipeService>? RecipeServiceFactory` (test seam; default builds the service from `_civitaiClient`, a static `HttpClient`, `DefaultCacheDirectory`, `_logger`).

Behaviour: `SearchAsync` clears `Recipe` with the results; `link.IsImageLink` → `LoadImageRecipeAsync(link.ImageId!.Value, ct)` (sets `IsLinkLookup = true`, `StatusMessage = "Loading the image recipe from Civitai..."`, then on success `Recipe = new CivitaiRecipeViewModel(recipe, _installed, ShowNsfw, CreateResultCard, AvaloniaClipboardService.Instance) { EnqueueHandler = pairs => _ = EnqueueWithPreflightPromptAsync(pairs), UrlOpener = OpenUrl }`, `LinkLookupNotice` = the notice copy, `StatusMessage = result.Message`, starts `LoadPreviewAsync(ct)`; same cancellation/exception handling as `LoadLinkAsync`). `RefreshInstalledSetAsync` and `OnShowNsfwChanged` forward to `Recipe`. The enqueue log line: `"Recipe {imageId}: {n} LoRA(s) sent to the queue"`.

View (match the mockup `docs/superpowers/specs/assets/2026-10-27-civitai-image-recipe/1-recipe-view.png` and `2-no-metadata.png`): left column 280 px — image (or a "NSFW image hidden. Turn on Show NSFW content to see it." placeholder when `IsPreviewHidden`), rating badge (green SFW / red NSFW), title, details, *Open on Civitai*. Right: "Generation data" — source note (green ✓) or the amber warning box; prompt (2 lines collapsed, *Show all* / *Copy*), negative prompt, checkpoint, VAE, sampler line, steps·CFG·seed line. "Resources used": one grid row per `Rows` item (tick box bound to `IsSelected`/`CanTick`, thumbnail `Card.PreviewImage`, name + `VersionLabel`, `TypeBadge`, INSTALLED / EA / PAID badges as on the cards, base model, weight, size, note; rows without a tick box show an *Open on Civitai* link to `CivitaiUrl`, as the checkpoint row in the mockup). Footer: `FooterText`, disabled *Send to ComfyUI* with `ToolTip.ShowOnDisabled="True"` and the tip, accent *Add N to queue* (disabled when `SelectedCount == 0`). In `CivitaiBrowserView.axaml` the card `ItemsControl` gets `IsVisible="{Binding !IsRecipeView}"` and `<cb:CivitaiRecipeView DataContext="{Binding Recipe}" IsVisible="{Binding IsRecipeView}"/>` sits under the notice. Check `DiffusionNexus.UI/REUSABLES.md` for badge / info-box pieces first and reuse them; add a row there only if you create something reusable.

- [ ] **Step 1: Write the failing tests** (VM built as in `DownloadPreflightChoiceTests.Create()`, `RecipeServiceFactory` returning a service over a Moq client + stub handler from Task 6)

```csharp
[Fact] public async Task ImageLink_ShowsRecipe_AndNotice() { /* SearchText "https://civitai.red/images/7"; await SearchCommand → IsRecipeView, Results empty, LinkLookupNotice == notice copy */ }
[Fact] public async Task NameSearchAfterRecipe_ClearsIt() { /* SearchText "anime" → Recipe null */ }
[Fact] public async Task UnknownImage_ShowsMessage_NoRecipe() { /* StatusMessage "Civitai has no public image with ID 7." */ }
[Fact] public async Task RecipeAddToQueue_UsesTheQueue() { /* destination custom folder set; AddToQueueCommand → queue.Jobs has the ticked version ids */ }
[Fact] public async Task ShowNsfwToggle_ReachesTheRecipe() { /* NSFW recipe; ShowNsfw false → Recipe.IsPreviewHidden true */ }
```

- [ ] **Step 2: Run, expect FAIL.**  - [ ] **Step 3: Implement VM wiring, then the XAML.**
- [ ] **Step 4: Run the new tests and the whole Viewer folder, expect PASS:** `--filter "FullyQualifiedName~DiffusionNexus.Tests.Viewer"`; then `dotnet build DiffusionNexus.sln -c Debug` with no new warnings in the touched files.
- [ ] **Step 5: GUI check on the BenQ** (global rule: never the LG; select the test build by exe path, the owner's app may be open). Launch the Debug build, move it to the BenQ rectangle, LoRA Viewer → Browse Civitai, paste `https://civitai.red/images/143992601`. Expected: prompt starting "Photograph of a solitary cybernetic sniper", sampler `exponential/ddim_beta57`, `8 · 1 · 832350830334358`, five LoRA rows with weights 3 / 0.5 / 1 / 0.65 / 1 (in Civitai's order: Ass and Thighs 3, Purple Grainy 0.5, Purple Graphics 1, AG: Aim 0.65, godago 1), checkpoint row with CHECKPOINT and no tick box, badge "SFW · PG", greyed *Send to ComfyUI* with its tooltip. Paste an NSFW image link (pick one from `images?nsfw=X&sort=Newest` with `browsingLevel` 8) with Show NSFW off: preview hidden, badge "NSFW · X". Unified Console shows each step. Screenshot both.
- [ ] **Step 6: Commit + push** — `feat(browser): Civitai image link opens the recipe view (#628)`

---

### Task 9: PR

- [ ] **Step 1:** Mirror the spec's new SFW/NSFW paragraph and the corrected `<lora:…>` sentence into the issue #628 body (`gh issue edit 628 --body-file`, keeping the image links absolute as they are now).
- [ ] **Step 2:** Full test run `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` — record pass/fail counts; compare `git diff --numstat develop` with `-w` for CRLF flips before the final push.
- [ ] **Step 3:** Open the PR into `develop` titled `Civitai image link → recipe view (#628)`, body: what it does, the API facts found (nsfw=X, word `nsfwLevel`, UTF-16BE EXIF, PNG under `.jpeg`), the two screenshots, test counts, "Closes #628". Do not merge.
