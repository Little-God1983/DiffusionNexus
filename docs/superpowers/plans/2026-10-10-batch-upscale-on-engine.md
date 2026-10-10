# Batch Upscale on the Diffusion Nexus Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Batch Upscale (normal and Vision Auto-Prompt) runs on the Diffusion Nexus Engine; Vision describes every image first with Qwen3-VL kept loaded, then upscales.

**Architecture:** Two catalog workloads (normal, Vision) map to two Engine Features rows. The Batch Upscale tab takes a per-run client from `IComfyUiClientProvider` (Engine or own URL, per Settings). Vision runs a new describe-only workflow per image through a small `ImageDescriber` (step 1), then the normal upscale workflow with each image's description as its prompt (step 2). A step banner tells the user which step runs and why no upscaled image has appeared yet.

**Tech Stack:** .NET 10, Avalonia 11, CommunityToolkit.Mvvm, xUnit + FluentAssertions + Moq, ComfyUI API workflows (JSON), the DiffusionNexus.Catalog repo (`dn-catalog`).

**Spec:** `docs/superpowers/specs/2026-10-10-batch-upscale-on-engine-design.md`

## Global Constraints

- Repo `E:\Repos\DiffusionNexus`, branch `feature/batch-upscale-on-engine` (already created, spec committed). One PR to `develop`, `Closes #608`. Commit and push after each task (`gh auth switch --user Little-God1983` before pushing).
- Catalog repo `E:\Repos\DiffusionNexus.Catalog`: commit straight to `main`, never a branch or PR; `gh auth switch --user Into-The-Latent` before pushing.
- Describer node: `SimpleQwenVLggufV2` (KLL535 pack `ComfyUI_Simple_Qwen3-VL-gguf`); modes used: `keep_vram` (every image but the last), `direct_clean` (the last, and the free-up job). Seed is 32-bit: `seed & 0xFFFFFFFFL`. Config: model + projector paths, `chat_handler: "qwen3"`, `ctx: 8192`, `output_max_tokens: 400`, `temperature: 0.3`, `repeat_penalty: 1.1`.
- Catalog model names (shared with Outpaint Vision): `Qwen3-VL-8B-Abliterated-Caption-it` and `Qwen3-VL-8B-Abliterated-Caption-it mmproj`.
- New catalog workload id: `FE2E7606-AC36-470E-A7C5-7F8CC23ECC98`, name `Upscaling-Z-Image-Turbo Vision`. Existing: `B853EB7C-0A0E-48A6-985E-E32B2F8848F5` `Upscaling-Z-Image-Turbo`.
- UI copy, verbatim: `Step 1 of 2 · Describing images with Qwen3-VL`, `Step 2 of 2 · Upscaling`, explanation `Upscaled images appear in step 2. Qwen3-VL describes every image first so it loads only once; then it is unloaded and the upscaler gets the VRAM.`
- Every new step logs to the Unified Console (`IUnifiedLogger`, category `General`, source `Batch Upscale`) — standing rule.
- UI checks on the **BenQ** monitor only (`BNQ7F05`, non-primary, X<0), never the LG.
- Bash tool: write long scripts with the Write tool; never `sed -i` on CRLF files (it strips CR); compare `git diff --numstat` with `git diff -w --numstat` before pushing.
- Test command: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~<Name>"`; full suite at the end of every task.

## Review Focus

- **A Vision batch of one image:** the only image is also the last, so it must run `direct_clean` and never leave Qwen3-VL loaded → test in Task 6.
- **Cancel before the first upload finishes:** nothing is loaded, so no free-up job may be queued (an extra job on an idle Engine would load Qwen3-VL for nothing) → test in Task 6.
- **The last description fails:** its job may have died before unloading, so a free-up job follows before step 2 → test in Task 6.
- **Model paths unknown** (readiness not run yet, or files elsewhere): re-check once, then stop before uploading with the install hint, not a node error halfway → test in Task 6.
- **Engine not installed / fails to start:** the status shows the provider's message (`ComfyUiUnavailableException`), not `Error: …` → test in Task 5.

---

### Task 1: Catalog — slim the upscale workload, add the Vision workload

**Files:**
- Modify: `E:\Repos\DiffusionNexus.Catalog\workloads\upscaling-z-image-turbo\workload.json`
- Create: `E:\Repos\DiffusionNexus.Catalog\workloads\upscaling-z-image-turbo-vision\workload.json`, `...\upscaling-z-image-turbo-vision\thumbnail.webp` (copy of the upscale one)
- Script: `<scratchpad>\catalog_608.py`

**Interfaces:**
- Produces: catalog workload `FE2E7606-AC36-470E-A7C5-7F8CC23ECC98` with models named exactly `Qwen3-VL-8B-Abliterated-Caption-it` / `… mmproj` (Task 7 reads their paths from readiness).

- [ ] **Step 1: Pull and write the edit script**

```bash
git -C /e/Repos/DiffusionNexus.Catalog pull -q
```

Write `catalog_608.py` (Write tool, scratchpad):

```python
import json, uuid, shutil, os
root = r"E:\Repos\DiffusionNexus.Catalog\workloads"
src = os.path.join(root, "upscaling-z-image-turbo", "workload.json")
outp = os.path.join(root, "outpainting-qwen-2512", "workload.json")
ULTIMATE = "40ee7409-7c27-48fc-8192-a9ef5e9d9fbb"
KLL535 = "26d661b8-8cf9-4c48-9b28-09dd6854d6d6"
SCRIPTS = "b6fd8a6e-ccbb-4a5c-a9df-912d0407e4aa"
CU128_WHEEL = "f119f4d4-ef71-484f-8213-0abc49f26900"

def load(p): return json.load(open(p, encoding="utf-8"))
def save(p, w): open(p, "w", encoding="utf-8", newline="\n").write(json.dumps(w, indent=2, ensure_ascii=False) + "\n")
def fresh_ids(model):
    m = json.loads(json.dumps(model)); m["id"] = str(uuid.uuid4())
    for link in m["downloadLinks"]: link["id"] = str(uuid.uuid4())
    return m

# 1. The normal workload: Ultimate SD Upscale only (Qwen3-VL-Instruct and rgthree go).
w = load(src)
w["gitRepositories"] = [ULTIMATE]
w["subVersion"] += 1
w["description"] = w["description"].replace(
    "Includes the `Z-Image-Turbo-Upscale` workflow.",
    "Includes the `Z-Image-Turbo-Upscale` workflow. Vision Auto-Prompt lives in *Upscaling-Z-Image-Turbo Vision*.")
save(src, w)

# 2. The Vision workload: the same set plus the Qwen3-VL GGUF describer (models copied from Outpainting).
qwen = [m for m in load(outp)["modelDownloads"] if m["name"].startswith("Qwen3-VL-8B-Abliterated-Caption-it")]
assert len(qwen) == 2, [m["name"] for m in qwen]
v = json.loads(json.dumps(w))
v["id"] = "fe2e7606-ac36-470e-a7c5-7f8cc23ecc98"
v["name"] = "Upscaling-Z-Image-Turbo Vision"
v["version"], v["subVersion"] = 1, 0
v["description"] = ("**Z-Image-Turbo** with UltimateSDUpscale, plus Vision Auto-Prompt: the Simple Qwen3-VL GGUF node "
                    "(llama.cpp) describes each image with Qwen3-VL 8B Abliterated, and the description becomes that "
                    "image's upscale prompt. Runs as a Diffusion Nexus Core capability.")
v["gitRepositories"] = [ULTIMATE, KLL535, SCRIPTS]
v["installLamaCpp"] = True
v["selectedLamaCppWheelId"] = CU128_WHEEL
v["vram"] = {"vramProfiles": "8,12,16,24,32"}
v["modelDownloads"] = [fresh_ids(m) for m in w["modelDownloads"]] + [fresh_ids(m) for m in qwen]
dst_dir = os.path.join(root, "upscaling-z-image-turbo-vision")
os.makedirs(dst_dir, exist_ok=True)
save(os.path.join(dst_dir, "workload.json"), v)
shutil.copyfile(os.path.join(root, "upscaling-z-image-turbo", "thumbnail.webp"), os.path.join(dst_dir, "thumbnail.webp"))
print("ok", w["subVersion"], len(v["modelDownloads"]))
```

- [ ] **Step 2: Run it and validate**

```bash
python "<scratchpad>/catalog_608.py"
cd /e/Repos/DiffusionNexus.Catalog && dn-catalog validate . && dn-catalog validate . --online
```
Expected: `ok 7 6`; both validations report no errors (warnings about unchanged versions must not name these two workloads).

- [ ] **Step 3: Check the diff is only these two workloads**

```bash
git -C /e/Repos/DiffusionNexus.Catalog status --short
```
Expected: ` M workloads/upscaling-z-image-turbo/workload.json`, `?? workloads/upscaling-z-image-turbo-vision/`.

- [ ] **Step 4: Commit to main and push**

```bash
cd /e/Repos/DiffusionNexus.Catalog && git add workloads && git commit -q -m "feat(workloads): #608 Upscaling-Z-Image-Turbo Vision (Qwen3-VL GGUF); the normal upscale workload needs only UltimateSDUpscale

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" && gh auth switch --user Into-The-Latent && git push -q
```
Then `gh run list -R Into-The-Latent/DiffusionNexus.Catalog -L 1` until the run is `completed success` (Preview now carries both).

- [ ] **Step 5: Tell the owner**

Report: catalog `main` carries the new workload; Task 7 needs a Stable **v6** release (owner, Toolbox) so the embedded seed can be refreshed. Continue with Tasks 2–6 meanwhile.

---

### Task 2: One Qwen3-VL GGUF config helper (Outpaint moves onto it)

**Files:**
- Create: `DiffusionNexus.UI/Services/Vision/QwenVlGguf.cs`
- Modify: `DiffusionNexus.UI/ViewModels/OutpaintingViewModel.cs` (remove `VisionModelName`, `VisionProjectorName`, `BuildVisionConfig`; use the helper)
- Modify: `DiffusionNexus.Tests/ViewModels/OutpaintVisionDescriptionTests.cs` (references)
- Test: `DiffusionNexus.Tests/Services/Vision/QwenVlGgufTests.cs`

**Interfaces:**
- Produces:
  - `public static class QwenVlGguf` in namespace `DiffusionNexus.UI.Services.Vision`
  - `public const string ModelName = "Qwen3-VL-8B-Abliterated-Caption-it";`
  - `public const string ProjectorName = "Qwen3-VL-8B-Abliterated-Caption-it mmproj";`
  - `public static bool TryGetPaths(IReadOnlyDictionary<string, string> modelPaths, out string modelPath, out string projectorPath)`
  - `public static string BuildConfig(string modelPath, string projectorPath)`
  - `public static long Seed(long seed)` → `seed & 0xFFFFFFFFL`

- [ ] **Step 1: Write the failing tests**

`DiffusionNexus.Tests/Services/Vision/QwenVlGgufTests.cs`:

```csharp
using System.Text.Json.Nodes;
using DiffusionNexus.UI.Services.Vision;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services.Vision;

public class QwenVlGgufTests
{
    [Fact]
    public void BuildConfig_IsJsonWithEscapedWindowsPaths_AndABoundedAnswer()
    {
        var config = JsonNode.Parse(QwenVlGguf.BuildConfig(@"C:\m\model.gguf", @"C:\m\mmproj.gguf"))!;

        config["model_path"]!.GetValue<string>().Should().Be(@"C:\m\model.gguf");
        config["mmproj_path"]!.GetValue<string>().Should().Be(@"C:\m\mmproj.gguf");
        config["chat_handler"]!.GetValue<string>().Should().Be("qwen3");
        config["output_max_tokens"]!.GetValue<int>().Should().Be(400);
        config["temperature"]!.GetValue<double>().Should().Be(0.3);
    }

    [Fact]
    public void TryGetPaths_NeedsBothFiles()
    {
        var both = new Dictionary<string, string> { [QwenVlGguf.ModelName] = "m.gguf", [QwenVlGguf.ProjectorName] = "p.gguf" };
        QwenVlGguf.TryGetPaths(both, out var m, out var p).Should().BeTrue();
        (m, p).Should().Be(("m.gguf", "p.gguf"));

        QwenVlGguf.TryGetPaths(new Dictionary<string, string> { [QwenVlGguf.ModelName] = "m.gguf" }, out _, out _)
            .Should().BeFalse("the projector is missing");
    }

    [Theory]
    [InlineData(5L, 5L)]
    [InlineData(0x1_0000_0005L, 5L)]
    [InlineData(long.MaxValue, 0xFFFFFFFFL)]
    public void Seed_FitsTheNodes32BitInput(long seed, long expected) =>
        QwenVlGguf.Seed(seed).Should().Be(expected);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test … --filter "FullyQualifiedName~QwenVlGgufTests"` — Expected: build error, `QwenVlGguf` does not exist.

- [ ] **Step 3: Implement the helper**

`DiffusionNexus.UI/Services/Vision/QwenVlGguf.cs`:

```csharp
using System.Text.Json;

namespace DiffusionNexus.UI.Services.Vision;

/// <summary>
/// Qwen3-VL through KLL535's <c>SimpleQwenVLggufV2</c> node (llama.cpp): the catalog model names whose
/// paths readiness reports, and the node's <c>config_override</c>. Shared by Outpaint Vision (#607) and
/// Batch Upscale Vision (#608).
/// </summary>
public static class QwenVlGguf
{
    /// <summary>Catalog name of the GGUF model (VRAM-tiered quantizations).</summary>
    public const string ModelName = "Qwen3-VL-8B-Abliterated-Caption-it";

    /// <summary>Catalog name of its vision projector.</summary>
    public const string ProjectorName = "Qwen3-VL-8B-Abliterated-Caption-it mmproj";

    /// <summary>Both file paths from a readiness check's <c>ModelPaths</c>; false when either is missing.</summary>
    public static bool TryGetPaths(IReadOnlyDictionary<string, string> modelPaths, out string modelPath, out string projectorPath)
    {
        modelPath = projectorPath = "";
        if (!modelPaths.TryGetValue(ModelName, out var model) || !modelPaths.TryGetValue(ProjectorName, out var projector))
            return false;
        (modelPath, projectorPath) = (model, projector);
        return true;
    }

    /// <summary>
    /// The node's config: model and projector paths, a bounded answer (a looping description once ran
    /// to 2048 tokens and became the prompt), low temperature.
    /// </summary>
    public static string BuildConfig(string modelPath, string projectorPath) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["model_path"] = modelPath,
            ["mmproj_path"] = projectorPath,
            ["chat_handler"] = "qwen3",
            ["ctx"] = 8192,
            ["output_max_tokens"] = 400,
            ["temperature"] = 0.3,
            ["repeat_penalty"] = 1.1,
        });

    /// <summary>The node's seed input tops out at 0xFFFFFFFF; a KSampler's 63-bit seed is rejected.</summary>
    public static long Seed(long seed) => seed & 0xFFFFFFFFL;
}
```

- [ ] **Step 4: Move Outpaint onto it**

In `OutpaintingViewModel.cs`:
- delete `internal const string VisionModelName = …;`, `internal const string VisionProjectorName = …;` and the whole `internal static string BuildVisionConfig(...)` member (with its doc comment);
- add `using DiffusionNexus.UI.Services.Vision;`;
- replace the two `ContainsKey(VisionModelName)` / `ContainsKey(VisionProjectorName)` checks and the two `TryGetValue` calls with:

```csharp
                if (!QwenVlGguf.TryGetPaths(VisionReadiness.ModelPaths, out _, out _))
                    await VisionReadiness.CheckReadinessAsync();
                if (!QwenVlGguf.TryGetPaths(VisionReadiness.ModelPaths, out var modelPath, out var projectorPath))
```
- `BuildVisionConfig(modelPath, projectorPath)` → `QwenVlGguf.BuildConfig(modelPath, projectorPath)`;
- `node["inputs"]!["seed"] = seed & 0xFFFFFFFFL;` → `node["inputs"]!["seed"] = QwenVlGguf.Seed(seed);`.

In `OutpaintVisionDescriptionTests.cs`: `OutpaintingViewModel.VisionModelName` → `QwenVlGguf.ModelName`, `OutpaintingViewModel.VisionProjectorName` → `QwenVlGguf.ProjectorName`; delete the test `BuildVisionConfig_IsJsonWithEscapedWindowsPaths` (now in `QwenVlGgufTests`); add `using DiffusionNexus.UI.Services.Vision;`. Grep the test project for any other `VisionModelName|BuildVisionConfig` use and update it the same way.

- [ ] **Step 5: Run the tests**

Run: `--filter "FullyQualifiedName~QwenVlGgufTests|FullyQualifiedName~OutpaintVisionDescriptionTests|FullyQualifiedName~EditorEngineGenerateTests"` — Expected: PASS. Then the full suite: 0 failed.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/Services/Vision/QwenVlGguf.cs DiffusionNexus.UI/ViewModels/OutpaintingViewModel.cs DiffusionNexus.Tests
git commit -m "refactor(vision): #608 one Qwen3-VL GGUF config helper, used by Outpaint Vision

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 3: Workflows — no rgthree, a describe-only workflow, the Vision upscale file goes

**Files:**
- Modify: `DiffusionNexus.Service/Assets/Workflows/Z-Image-Turbo-Upscale.json`
- Create: `DiffusionNexus.Service/Assets/Workflows/Qwen3-VL-Describe.json`
- Delete: `DiffusionNexus.Service/Assets/Workflows/Vision-Z-Image-Turbo-Upscale.json`
- Modify: `DiffusionNexus.Service/Services/FeatureRegistry.cs` (the comment that names the deleted file)
- Test: `DiffusionNexus.Tests/Workflows/UpscaleWorkflowTests.cs`

**Interfaces:**
- Produces: `Assets/Workflows/Qwen3-VL-Describe.json` with node ids `"1"` LoadImage, `"2"` ImageScaleToMaxDimension, `"3"` SimpleQwenVLggufV2, `"4"` ShowText|pysssss (Task 4 overrides `"1"` and `"3"`).

- [ ] **Step 1: Write the failing tests**

`DiffusionNexus.Tests/Workflows/UpscaleWorkflowTests.cs` (workflows are copied to the test output by the Service project's `Content` item):

```csharp
using System.Text.Json.Nodes;
using FluentAssertions;

namespace DiffusionNexus.Tests.Workflows;

public class UpscaleWorkflowTests
{
    private static JsonObject Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Workflows", name)))!.AsObject();

    private static IEnumerable<string> Links(JsonObject workflow) =>
        workflow.SelectMany(n => n.Value!["inputs"]!.AsObject())
            .Where(i => i.Value is JsonArray { Count: 2 })
            .Select(i => i.Value![0]!.GetValue<string>());

    // #608: the rgthree loader only carried a disabled leftover LoRA, and the Engine never installs rgthree.
    [Fact]
    public void Upscale_HasNoRgthreeNode_AndEveryLinkPointsAtANodeThatExists()
    {
        var workflow = Load("Z-Image-Turbo-Upscale.json");

        workflow.Select(n => n.Value!["class_type"]!.GetValue<string>()).Should().NotContain(t => t.Contains("rgthree"));
        Links(workflow).Should().OnlyContain(id => workflow.ContainsKey(id));
        workflow["39"]!["inputs"]!["model"]!.ToJsonString().Should().Be("""["28",0]""");
        workflow["17"]!["inputs"]!["clip"]!.ToJsonString().Should().Be("""["27",0]""");
        workflow["35"]!["inputs"]!["clip"]!.ToJsonString().Should().Be("""["27",0]""");
    }

    [Fact]
    public void Describe_RunsTheGgufNodeOnA1280pxCopy_AndShowsTheText()
    {
        var workflow = Load("Qwen3-VL-Describe.json");

        workflow["1"]!["class_type"]!.GetValue<string>().Should().Be("LoadImage");
        workflow["2"]!["inputs"]!["largest_size"]!.GetValue<int>().Should().Be(1280);
        workflow["3"]!["class_type"]!.GetValue<string>().Should().Be("SimpleQwenVLggufV2");
        workflow["3"]!["inputs"]!["bypass"]!.GetValue<bool>().Should().BeFalse("the node rejects a prompt without it");
        workflow["3"]!["inputs"]!["image"]!.ToJsonString().Should().Be("""["2",0]""");
        workflow["4"]!["class_type"]!.GetValue<string>().Should().Be("ShowText|pysssss");
        Links(workflow).Should().OnlyContain(id => workflow.ContainsKey(id));
    }

    [Fact]
    public void TheVisionUpscaleWorkflow_IsGone() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Workflows", "Vision-Z-Image-Turbo-Upscale.json"))
            .Should().BeFalse("Vision runs the describe workflow, then the normal upscale workflow");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `--filter "FullyQualifiedName~UpscaleWorkflowTests"` — Expected: FAIL (rgthree present; describe file missing; Vision file present).

- [ ] **Step 3: Edit the workflows**

Write `<scratchpad>/workflows_608.py` and run it:

```python
import json, os
d = r"E:\Repos\DiffusionNexus\DiffusionNexus.Service\Assets\Workflows"
p = os.path.join(d, "Z-Image-Turbo-Upscale.json")
w = json.load(open(p, encoding="utf-8"))
del w["53"]                                   # Power Lora Loader (rgthree)
w["17"]["inputs"]["clip"] = ["27", 0]          # CLIPLoader
w["35"]["inputs"]["clip"] = ["27", 0]
w["39"]["inputs"]["model"] = ["28", 0]         # UNETLoader
open(p, "w", encoding="utf-8", newline="\n").write(json.dumps(w, indent=2, ensure_ascii=False) + "\n")

describe = {
  "1": {"inputs": {"image": "example.png"}, "class_type": "LoadImage", "_meta": {"title": "Load Image"}},
  "2": {"inputs": {"upscale_method": "area", "largest_size": 1280, "image": ["1", 0]},
        "class_type": "ImageScaleToMaxDimension", "_meta": {"title": "Image for the description (1280 px)"}},
  "3": {"inputs": {"model_preset": "None", "system_preset": "None",
                   "user_prompt": "Describe the image in about 100 detailed words. Write one paragraph of plain prose, no lists and no headings.",
                   "seed": 1, "unload_all_models": False, "mode": "direct_clean", "image": ["2", 0],
                   "config_override": "", "bypass": False},
        "class_type": "SimpleQwenVLggufV2", "_meta": {"title": "Qwen3-VL (GGUF)"}},
  "4": {"inputs": {"text": ["3", 0]}, "class_type": "ShowText|pysssss", "_meta": {"title": "Show Text"}},
}
open(os.path.join(d, "Qwen3-VL-Describe.json"), "w", encoding="utf-8", newline="\n").write(json.dumps(describe, indent=2) + "\n")
os.remove(os.path.join(d, "Vision-Z-Image-Turbo-Upscale.json"))
print("ok")
```

Check the original line endings first (`git show HEAD:DiffusionNexus.Service/Assets/Workflows/Z-Image-Turbo-Upscale.json | head -c 200 | od -c`); the file is LF, so `newline="\n"` keeps it.

In `FeatureRegistry.cs`, replace the comment above `[Feature.BatchUpscaleVision]` (the "Vision-Z-Image-Turbo-Upscale.json / Same workload as BatchUpscale…" lines) with `// Batch Upscale + Vision — Qwen3-VL-Describe.json, then Z-Image-Turbo-Upscale.json` (the id changes in Task 7).

- [ ] **Step 4: Run the tests**

Run: `--filter "FullyQualifiedName~UpscaleWorkflowTests"` — Expected: PASS. The full suite still compiles: `BatchUpscaleTabViewModel` names the deleted file only as a path string; its Vision run fails with "Workflow file not found" until Task 6, and no test runs it.

- [ ] **Step 5: Commit**

```bash
git add -A DiffusionNexus.Service/Assets/Workflows DiffusionNexus.Service/Services/FeatureRegistry.cs DiffusionNexus.Tests/Workflows
git commit -m "feat(workflows): #608 upscale without rgthree; Qwen3-VL describe-only workflow; the Vision upscale workflow goes

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 4: `ImageDescriber` — runs the describe workflow, frees the model

**Files:**
- Create: `DiffusionNexus.UI/Services/Vision/ImageDescriber.cs`
- Test: `DiffusionNexus.Tests/Services/Vision/ImageDescriberTests.cs`

**Interfaces:**
- Consumes: `QwenVlGguf.BuildConfig`, `QwenVlGguf.Seed` (Task 2); `Qwen3-VL-Describe.json` node ids `"1"`, `"3"` (Task 3); `IComfyUIWrapperService.QueueWorkflowAsync/WaitForCompletionAsync/GetResultAsync`; `ComfyUIResult.Texts`.
- Produces:
  - `public sealed class ImageDescriber` (namespace `DiffusionNexus.UI.Services.Vision`)
  - `public ImageDescriber(IComfyUIWrapperService client, string modelPath, string projectorPath, string? workflowPath = null)`
  - `public Task<string?> DescribeAsync(string uploadedImage, bool keepLoaded, CancellationToken ct)` — the trimmed description, or null when the node returned none
  - `public Task FreeAsync(string uploadedImage)` — never throws, ignores cancellation
  - `internal const string WorkflowRelativePath = "Assets/Workflows/Qwen3-VL-Describe.json"`, `LoadImageNodeId = "1"`, `DescribeNodeId = "3"`

- [ ] **Step 1: Write the failing tests**

`DiffusionNexus.Tests/Services/Vision/ImageDescriberTests.cs`:

```csharp
using System.Text.Json.Nodes;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services.Vision;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Services.Vision;

public class ImageDescriberTests
{
    private readonly Mock<IComfyUIWrapperService> _client = new();
    private readonly List<Dictionary<string, Action<JsonNode>>> _queued = [];

    public ImageDescriberTests()
    {
        _client.Setup(c => c.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Dictionary<string, Action<JsonNode>> o, CancellationToken _) => { _queued.Add(o); return $"p{_queued.Count}"; });
        _client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _client.Setup(c => c.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { var r = new ComfyUIResult(); r.Texts.Add("  A red fox in fresh snow.  "); return r; });
    }

    private ImageDescriber Sut() => new(_client.Object, @"D:\m\model.gguf", @"D:\m\mmproj.gguf");

    internal static (string Image, string Mode, long Seed, JsonNode Config) Applied(Dictionary<string, Action<JsonNode>> o)
    {
        var load = JsonNode.Parse("""{"inputs":{"image":""}}""")!;
        o["1"](load);
        var node = JsonNode.Parse("""{"inputs":{"mode":"","seed":0,"config_override":""}}""")!;
        o["3"](node);
        return (load["inputs"]!["image"]!.GetValue<string>(), node["inputs"]!["mode"]!.GetValue<string>(),
            node["inputs"]!["seed"]!.GetValue<long>(), JsonNode.Parse(node["inputs"]!["config_override"]!.GetValue<string>())!);
    }

    [Theory]
    [InlineData(true, "keep_vram")]
    [InlineData(false, "direct_clean")]
    public async Task Describe_SetsTheImage_TheMode_AndTheModelPaths(bool keepLoaded, string mode)
    {
        var text = await Sut().DescribeAsync("up-1.png", keepLoaded, CancellationToken.None);

        text.Should().Be("A red fox in fresh snow.");
        var applied = Applied(_queued.Single());
        applied.Image.Should().Be("up-1.png");
        applied.Mode.Should().Be(mode);
        applied.Seed.Should().BeInRange(0, uint.MaxValue);
        applied.Config["model_path"]!.GetValue<string>().Should().Be(@"D:\m\model.gguf");
        applied.Config["mmproj_path"]!.GetValue<string>().Should().Be(@"D:\m\mmproj.gguf");
    }

    [Fact]
    public async Task Describe_NoText_IsNull()
    {
        _client.Setup(c => c.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ComfyUIResult());

        (await Sut().DescribeAsync("up.png", keepLoaded: true, CancellationToken.None)).Should().BeNull();
    }

    // The free-up job reuses the loaded model (same config) and unloads it after answering.
    [Fact]
    public async Task Free_QueuesOneDirectCleanJob_WithTheSameConfig()
    {
        var sut = Sut();
        await sut.DescribeAsync("up-1.png", keepLoaded: true, CancellationToken.None);

        await sut.FreeAsync("up-1.png");

        _queued.Should().HaveCount(2);
        var free = Applied(_queued[1]);
        free.Mode.Should().Be("direct_clean");
        free.Config.ToJsonString().Should().Be(Applied(_queued[0]).Config.ToJsonString());
    }

    [Fact]
    public async Task Free_NeverThrows()
    {
        _client.Setup(c => c.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        await Sut().Invoking(s => s.FreeAsync("up.png")).Should().NotThrowAsync();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `--filter "FullyQualifiedName~ImageDescriberTests"` — Expected: build error, `ImageDescriber` does not exist.

- [ ] **Step 3: Implement**

`DiffusionNexus.UI/Services/Vision/ImageDescriber.cs`:

```csharp
using System.Text.Json.Nodes;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.UI.Services.Vision;

/// <summary>
/// Describes images with Qwen3-VL on a ComfyUI server through the describe-only workflow
/// (<c>Qwen3-VL-Describe.json</c>). A batch keeps the model loaded between images (<c>keep_vram</c>) and
/// frees it with the last one (<c>direct_clean</c>); <see cref="FreeAsync"/> frees it when a batch stops early.
/// </summary>
public sealed class ImageDescriber
{
    private static readonly ILogger Logger = Log.ForContext<ImageDescriber>();

    internal const string WorkflowRelativePath = "Assets/Workflows/Qwen3-VL-Describe.json";
    internal const string LoadImageNodeId = "1";
    internal const string DescribeNodeId = "3";

    private readonly IComfyUIWrapperService _client;
    private readonly string _workflowPath;
    private readonly string _config;
    private readonly Random _random = new();

    public ImageDescriber(IComfyUIWrapperService client, string modelPath, string projectorPath, string? workflowPath = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _workflowPath = workflowPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowRelativePath);
        _config = QwenVlGguf.BuildConfig(modelPath, projectorPath);
    }

    /// <summary>The description of an image already uploaded to the server, or null when the node returned none.</summary>
    public async Task<string?> DescribeAsync(string uploadedImage, bool keepLoaded, CancellationToken ct)
    {
        var promptId = await _client.QueueWorkflowAsync(_workflowPath, Overrides(uploadedImage, keepLoaded ? "keep_vram" : "direct_clean"), ct);
        await _client.WaitForCompletionAsync(promptId, progress: null, ct);
        var result = await _client.GetResultAsync(promptId, ct);
        return result.Texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim();
    }

    /// <summary>
    /// Frees a model a batch kept loaded: one <c>direct_clean</c> job with the same config reuses it and
    /// unloads it after answering. Runs to the end even when the batch was cancelled, and never throws.
    /// </summary>
    public async Task FreeAsync(string uploadedImage)
    {
        try
        {
            var promptId = await _client.QueueWorkflowAsync(_workflowPath, Overrides(uploadedImage, "direct_clean"), CancellationToken.None);
            await _client.WaitForCompletionAsync(promptId, progress: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not free Qwen3-VL on the ComfyUI server");
        }
    }

    private Dictionary<string, Action<JsonNode>> Overrides(string uploadedImage, string mode)
    {
        var seed = QwenVlGguf.Seed((long)(_random.NextDouble() * long.MaxValue));
        return new Dictionary<string, Action<JsonNode>>
        {
            [LoadImageNodeId] = node => node["inputs"]!["image"] = uploadedImage,
            [DescribeNodeId] = node =>
            {
                node["inputs"]!["mode"] = mode;
                node["inputs"]!["seed"] = seed;
                node["inputs"]!["config_override"] = _config;
            },
        };
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `--filter "FullyQualifiedName~ImageDescriberTests"` — Expected: PASS (6 tests). Full suite: 0 failed.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/Vision/ImageDescriber.cs DiffusionNexus.Tests/Services/Vision/ImageDescriberTests.cs
git commit -m "feat(vision): #608 ImageDescriber runs the describe workflow, keeps or frees Qwen3-VL

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 5: Batch Upscale tab on the client provider; readiness follows the tab; console logging

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/Tabs/BatchUpscaleTabViewModel.cs`
- Modify: `DiffusionNexus.UI/ViewModels/LoraDatasetHelperViewModel.cs:271` and the `SelectedTabIndex` setter (~line 112)
- Test: `DiffusionNexus.Tests/ViewModels/Tabs/BatchUpscaleEngineRunTests.cs` (new; Task 6 adds to it)

**Interfaces:**
- Consumes: `IComfyUiClientProvider.AcquireAsync`, `ComfyUiClientLease` (`Client`, `Mode`, `BaseUrl`); exceptions `ComfyUiUnavailableException`, `ComfyUIExecutionException`, `ComfyUIWorkflowRejectedException`, `HttpRequestException` with `StatusCode`; `IDatasetEventAggregator.SettingsSaved/EngineChanged`; `IUnifiedLogger`.
- Produces (Task 6 and 8 rely on these):
  - constructor `BatchUpscaleTabViewModel(IDatasetEventAggregator eventAggregator, IDatasetState state, IComfyUiClientProvider? clientProvider = null, IAppSettingsService? settingsService = null, IFeatureReadinessService? readinessService = null, IUiScheduler? uiScheduler = null, Func<string, int, Bitmap?>? thumbnailDecoder = null, IUnifiedLogger? unifiedLogger = null)`
  - `public void OnTabActivated()` / `public void OnTabDeactivated()`
  - private `void Info(string)`, `void Warn(string)`, `void Error(string, Exception?)` (Unified Console + Serilog, source `Batch Upscale`)
  - private `Dictionary<string, Action<JsonNode>> BuildNodeModifiers(string uploadedFilename, long seed, string positivePrompt)` — always sets node 17

- [ ] **Step 1: Write the failing tests**

`DiffusionNexus.Tests/ViewModels/Tabs/BatchUpscaleEngineRunTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Vision;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels.Tabs;

/// <summary>#608: Batch Upscale runs on the server chosen in Settings, through IComfyUiClientProvider.</summary>
public class BatchUpscaleEngineRunTests : IDisposable
{
    protected readonly Mock<IComfyUIWrapperService> Client = new();
    protected readonly List<(string Workflow, Dictionary<string, Action<JsonNode>> Overrides)> Queued = [];
    protected readonly List<(string Level, string Message)> Logged = [];
    protected readonly Mock<IFeatureReadinessService> Readiness = new();
    protected readonly DatasetEventAggregator Events = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("dn-upscale-").FullName;

    public BatchUpscaleEngineRunTests()
    {
        Client.Setup(c => c.UploadImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => "up-" + Path.GetFileName(path));
        Client.Setup(c => c.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string wf, Dictionary<string, Action<JsonNode>> o, CancellationToken _) =>
            {
                Queued.Add((Path.GetFileName(wf), o));
                return $"p{Queued.Count}";
            });
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Client.Setup(c => c.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => ResultFor(Queued[int.Parse(id[1..]) - 1]));
        Client.Setup(c => c.DownloadImageAsync(It.IsAny<ComfyUIImage>(), It.IsAny<CancellationToken>())).ReturnsAsync([1, 2, 3]);
        ReadinessWith(Paths());
    }

    /// <summary>A describe job answers "Description of {uploaded image}"; an upscale job returns one image.</summary>
    protected virtual ComfyUIResult ResultFor((string Workflow, Dictionary<string, Action<JsonNode>> Overrides) job)
    {
        var result = new ComfyUIResult();
        if (job.Workflow == "Qwen3-VL-Describe.json")
            result.Texts.Add($"Description of {ImageOf(job.Overrides, "1")}");
        else
            result.Images.Add(new ComfyUIImage("out.png", "", "output", "http://test/view"));
        return result;
    }

    protected static string ImageOf(Dictionary<string, Action<JsonNode>> o, string nodeId)
    {
        var node = JsonNode.Parse("""{"inputs":{"image":""}}""")!;
        o[nodeId](node);
        return node["inputs"]!["image"]!.GetValue<string>();
    }

    protected static string PromptOf(Dictionary<string, Action<JsonNode>> o)
    {
        var node = JsonNode.Parse("""{"inputs":{"text":"untouched"}}""")!;
        o["17"](node);
        return node["inputs"]!["text"]!.GetValue<string>();
    }

    protected static Dictionary<string, string> Paths() => new()
    {
        [QwenVlGguf.ModelName] = @"D:\m\model.gguf",
        [QwenVlGguf.ProjectorName] = @"D:\m\mmproj.gguf",
    };

    protected void ReadinessWith(IReadOnlyDictionary<string, string> paths) =>
        Readiness.Setup(r => r.CheckAsync(It.IsAny<Feature>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Feature f, CancellationToken _) => new FeatureReadinessResult
            {
                Feature = f, Backend = BackendKind.Engine, ActiveBackendName = "Diffusion Nexus Engine",
                IsBackendOnline = true, IsReady = true, MissingRequirements = [], Warnings = [], ModelPaths = paths
            });

    protected BatchUpscaleTabViewModel Sut(IComfyUiClientProvider? provider = null)
    {
        var logger = new Mock<IUnifiedLogger>();
        logger.Setup(l => l.Info(It.IsAny<LogCategory>(), "Batch Upscale", It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((LogCategory _, string _, string m, string? _) => Logged.Add(("Info", m)));
        logger.Setup(l => l.Warn(It.IsAny<LogCategory>(), "Batch Upscale", It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((LogCategory _, string _, string m, string? _) => Logged.Add(("Warn", m)));
        logger.Setup(l => l.Error(It.IsAny<LogCategory>(), "Batch Upscale", It.IsAny<string>(), It.IsAny<Exception?>()))
            .Callback((LogCategory _, string _, string m, Exception? _) => Logged.Add(("Error", m)));
        return new BatchUpscaleTabViewModel(Events, new Mock<IDatasetState>().Object,
            clientProvider: provider ?? InpaintingViewModelGGUFResolutionTests.Provider(Client.Object, ComfyUiServerMode.Engine),
            readinessService: Readiness.Object, uiScheduler: new ImmediateUiScheduler(),
            thumbnailDecoder: (_, _) => null, unifiedLogger: logger.Object);
    }

    protected string Image(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }

    protected async Task RunAsync(BatchUpscaleTabViewModel vm, UpscalePromptMode mode, params string[] names)
    {
        vm.IsSingleImageMode = true;
        foreach (var name in names) vm.SingleImagePaths.Add(Image(name));
        vm.PromptMode = mode;
        await vm.Readiness.CheckReadinessAsync();
        await vm.VisionReadiness.CheckReadinessAsync();
        await vm.StartUpscaleCommand.ExecuteAsync(null);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ManualPrompt_RunsOnTheLeasedClient_OneUpscalePerImage_WithTheTypedPrompt()
    {
        var vm = Sut();
        vm.PositivePrompt = "a sharp photo";

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png", "b.png");

        Queued.Select(q => q.Workflow).Should().Equal("Z-Image-Turbo-Upscale.json", "Z-Image-Turbo-Upscale.json");
        Queued.Select(q => PromptOf(q.Overrides)).Should().Equal("a sharp photo", "a sharp photo");
        Logged.Should().Contain(("Info", "Running on the Diffusion Nexus Engine at http://test."));
        vm.CurrentProcessingStatus.Should().StartWith("Done – 2/2");
    }

    [Fact]
    public async Task EngineUnavailable_ShowsTheProvidersMessage()
    {
        var provider = new Mock<IComfyUiClientProvider>();
        provider.Setup(p => p.AcquireAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ComfyUiUnavailableException("The Diffusion Nexus Engine is not installed."));
        var vm = Sut(provider.Object);
        vm.PositivePrompt = "x";

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png");

        vm.CurrentProcessingStatus.Should().Be("The Diffusion Nexus Engine is not installed.");
        Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task NodeFailure_NamesTheNode()
    {
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ComfyUIExecutionException("UltimateSDUpscale", "CUDA out of memory"));
        var vm = Sut();
        vm.PositivePrompt = "x";

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png");

        vm.CurrentProcessingStatus.Should().Be("Failed in the ComfyUI node UltimateSDUpscale – see the Unified Console");
        Logged.Should().Contain(e => e.Level == "Error" && e.Message.Contains("CUDA out of memory"));
    }

    [Fact]
    public async Task ServerAnswered400_SaysSo()
    {
        Client.Setup(c => c.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("bad", null, HttpStatusCode.BadRequest));
        var vm = Sut();
        vm.PositivePrompt = "x";

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png");

        vm.CurrentProcessingStatus.Should().Be("ComfyUI answered 400 – see the Unified Console");
    }

    // ── Readiness follows the tab (the tab never checked on its own; Start stayed greyed until "Check") ──

    [Fact]
    public void Activating_ChecksTheActiveMode_SwitchingModeAndEngineChangesCheckAgain_InactiveDoesNot()
    {
        var vm = Sut();
        vm.PromptMode = UpscalePromptMode.ManualPrompt;
        Events.PublishEngineChanged(new EngineChangedEventArgs());
        Readiness.Verify(r => r.CheckAsync(It.IsAny<Feature>(), It.IsAny<CancellationToken>()), Times.Never, "the tab is not active");

        vm.OnTabActivated();
        Readiness.Verify(r => r.CheckAsync(Feature.BatchUpscale, It.IsAny<CancellationToken>()), Times.Once);

        vm.PromptMode = UpscalePromptMode.VisionAutoPrompt;
        Readiness.Verify(r => r.CheckAsync(Feature.BatchUpscaleVision, It.IsAny<CancellationToken>()), Times.Once);

        Events.PublishEngineChanged(new EngineChangedEventArgs());
        Readiness.Verify(r => r.CheckAsync(Feature.BatchUpscaleVision, It.IsAny<CancellationToken>()), Times.Exactly(2));

        vm.OnTabDeactivated();
        Events.PublishSettingsSaved(new SettingsSavedEventArgs());
        Readiness.Verify(r => r.CheckAsync(It.IsAny<Feature>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }
}
```

Check before running: the exact names `PublishEngineChanged`/`PublishSettingsSaved` and their args types in `DatasetEventAggregator.cs` (lines ~600–700); if `SettingsSavedEventArgs` needs constructor values, pass what other tests pass (`grep -rn "new SettingsSavedEventArgs" DiffusionNexus.Tests`).

- [ ] **Step 2: Run to verify it fails**

Run: `--filter "FullyQualifiedName~BatchUpscaleEngineRunTests"` — Expected: build error (`clientProvider`, `unifiedLogger`, `OnTabActivated` do not exist).

- [ ] **Step 3: Constructor, fields, logging**

In `BatchUpscaleTabViewModel.cs`:
- add usings `DiffusionNexus.Domain.Services.UnifiedLogging`, `System.Net.Http`;
- replace `private readonly IComfyUIWrapperService? _comfyUiService;` with `private readonly IComfyUiClientProvider? _clientProvider;` and add `private readonly IUnifiedLogger? _unifiedLogger;`, `private bool _isActive;`, `private const string LogSource = "Batch Upscale";`;
- constructor: parameter `IComfyUIWrapperService? comfyUiService = null` → `IComfyUiClientProvider? clientProvider = null`; append `IUnifiedLogger? unifiedLogger = null` after `thumbnailDecoder`; assign both; update the `<param>` docs;
- pass the aggregator to both readiness view models (enables the panel's install link):

```csharp
        Readiness = new FeatureReadinessViewModel(readinessService, Feature.BatchUpscale, eventAggregator);
        VisionReadiness = new FeatureReadinessViewModel(readinessService, Feature.BatchUpscaleVision, eventAggregator);
```
- add the logging helpers in `#region Private Methods`:

```csharp
    private void Info(string message)
    {
        Logger.Information("Batch Upscale: {Message}", message);
        _unifiedLogger?.Info(LogCategory.General, LogSource, message);
    }

    private void Warn(string message)
    {
        Logger.Warning("Batch Upscale: {Message}", message);
        _unifiedLogger?.Warn(LogCategory.General, LogSource, message);
    }

    private void Error(string message, Exception? ex)
    {
        Logger.Error(ex, "Batch Upscale: {Message}", message);
        _unifiedLogger?.Error(LogCategory.General, LogSource, message, ex);
    }
```
- `CanStartUpscale`: `_comfyUiService is null` → `_clientProvider is null`; `StartUpscaleAsync`: the first guard becomes `if (_clientProvider is null) { CurrentProcessingStatus = "ComfyUI service not available."; return; }`.

- [ ] **Step 4: Readiness follows the tab**

Add after the readiness `PropertyChanged` subscriptions in the constructor:

```csharp
        // SettingsSaved (the Server mode) and EngineChanged (an Engine or Features install) change what
        // readiness reports; they can arrive on a thread-pool thread, and the check writes bound
        // properties. With no Avalonia application (unit tests) run inline.
        _eventAggregator.SettingsSaved += OnReadinessInputChanged;
        _eventAggregator.EngineChanged += OnReadinessInputChanged;
```

and the members:

```csharp
    /// <summary>The tab became visible: check the active mode's readiness so Start and the panel are current.</summary>
    public void OnTabActivated()
    {
        _isActive = true;
        _ = ActiveReadiness.CheckReadinessAsync();
    }

    /// <summary>The tab was left: Settings and Engine changes no longer trigger checks.</summary>
    public void OnTabDeactivated() => _isActive = false;

    private void OnReadinessInputChanged(object? sender, EventArgs e)
    {
        if (Avalonia.Application.Current is null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            RecheckIfActive();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(RecheckIfActive);
    }

    private void RecheckIfActive()
    {
        if (_isActive) _ = ActiveReadiness.CheckReadinessAsync();
    }
```

In the `PromptMode` setter, next to the existing `OnPropertyChanged(nameof(ActiveReadiness));`, add `RecheckIfActive();`. In `Dispose()`, add `_eventAggregator.SettingsSaved -= OnReadinessInputChanged;` and `_eventAggregator.EngineChanged -= OnReadinessInputChanged;`. (Check the event delegate types: if `SettingsSaved` is `EventHandler<SettingsSavedEventArgs>`, give `OnReadinessInputChanged` two overloads or use lambdas stored in fields so they can be unsubscribed.)

- [ ] **Step 5: Run the loop on a lease**

In `RunUpscaleLoopAsync`:
- `if (_comfyUiService is null) return;` → `if (_clientProvider is null) return;`
- declare `ComfyUiClientLease? lease = null;` before `try`; first lines inside `try`:

```csharp
            // Engine start-up text ("Starting Diffusion Nexus Engine…") lands on the status line.
            lease = await _clientProvider.AcquireAsync(new Progress<string>(msg => CurrentProcessingStatus = msg), ct);
            var comfy = lease.Client;
            Info($"Running on {(lease.Mode == ComfyUiServerMode.Engine ? "the Diffusion Nexus Engine" : "your own ComfyUI")} at {lease.BaseUrl}.");
            Info($"{TotalImageCount} image(s), prompt mode {PromptMode.GetDisplayName()}.");
```
- every `_comfyUiService.` inside the loop → `comfy.`;
- `BuildNodeModifiers(uploadedFilename, seed, isVision, imagePositivePrompt)` → `BuildNodeModifiers(uploadedFilename, seed, imagePositivePrompt)`; in `BuildNodeModifiers` drop the `isVision` parameter and the `if (!isVision)` guard so node 17 is always set (Task 6 makes Vision pass the description);
- after a successful save add `Info($"Upscaled {item.FileName} → {outputPath}.");` (keep the Serilog line or replace it with this);
- replace the catch blocks with:

```csharp
        catch (ComfyUiUnavailableException ex)
        {
            CurrentProcessingStatus = ex.Message;
            Warn(ex.Message);
        }
        catch (ComfyUIExecutionException ex)
        {
            CurrentProcessingStatus = $"Failed in the ComfyUI node {ex.NodeType} – see the Unified Console";
            Error($"Upscale failed in the ComfyUI node {ex.NodeType}: {ex.Detail}", ex);
        }
        catch (ComfyUIWorkflowRejectedException ex)
        {
            CurrentProcessingStatus = "ComfyUI rejected the workflow – see the Unified Console";
            Error($"ComfyUI rejected the upscale workflow: {ex.Reason}", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status)
        {
            CurrentProcessingStatus = $"ComfyUI answered {(int)status} – see the Unified Console";
            Error($"ComfyUI answered {(int)status}: {ex.Message}", ex);
        }
        catch (OperationCanceledException)
        {
            CurrentProcessingStatus = $"Cancelled after {CompletedCount}/{TotalImageCount} images.";
            Info(CurrentProcessingStatus);
        }
        catch (Exception ex)
        {
            CurrentProcessingStatus = lease?.Mode == ComfyUiServerMode.Engine
                ? $"Error: {ex.Message} – is the Diffusion Nexus Engine running?"
                : $"Error: {ex.Message}";
            Error($"Batch upscale failed at image {CompletedCount + 1}/{TotalImageCount}", ex);
        }
```
- in `finally`, add `lease?.Dispose();` before `IsProcessing = false;`;
- the success line: `CurrentProcessingStatus = $"Done – {CompletedCount}/{TotalImageCount} image(s) upscaled.";` then `Info(CurrentProcessingStatus);`.

- [ ] **Step 6: Wire the caller**

`LoraDatasetHelperViewModel.cs` line 271:

```csharp
        BatchUpscale = new BatchUpscaleTabViewModel(eventAggregator, state, comfyUiClientProvider, settingsService, readinessService,
            unifiedLogger: unifiedLogger);
```

In the `SelectedTabIndex` setter, after `NotifyActiveTab(value);` add:

```csharp
                // Batch Upscale (tab 4) checks its readiness when shown, so Start and the panel are current.
                if (value == 4) BatchUpscale.OnTabActivated();
                else BatchUpscale.OnTabDeactivated();
```

If `comfyUiService` is now unused in `LoraDatasetHelperViewModel`, leave the parameter (DI passes it) but update its `<param>` doc to say it is no longer used by Batch Upscale; do not change `App.axaml.cs` registrations. Update the `App.axaml.cs` comment above the `IComfyUIWrapperService` singleton: drop "Batch Upscale," from the list.

- [ ] **Step 7: Run the tests**

Run: `--filter "FullyQualifiedName~BatchUpscaleEngineRunTests|FullyQualifiedName~BatchUpscaleTabViewModelSchedulerTests"` — Expected: PASS. Full suite: 0 failed.

- [ ] **Step 8: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/Tabs/BatchUpscaleTabViewModel.cs DiffusionNexus.UI/ViewModels/LoraDatasetHelperViewModel.cs DiffusionNexus.UI/App.axaml.cs DiffusionNexus.Tests/ViewModels/Tabs/BatchUpscaleEngineRunTests.cs
git commit -m "feat(upscale): #608 Batch Upscale runs on the Settings server via IComfyUiClientProvider; readiness follows the tab; console logging

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 6: Vision Auto-Prompt in two steps

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/Tabs/BatchUpscaleTabViewModel.cs` (run loop, step properties, `UpscaleImageItemViewModel`)
- Test: `DiffusionNexus.Tests/ViewModels/Tabs/BatchUpscaleVisionTwoStepTests.cs`

**Interfaces:**
- Consumes: `ImageDescriber` (Task 4), `QwenVlGguf.TryGetPaths` (Task 2), the Task 5 lease loop and helpers, test base `BatchUpscaleEngineRunTests` (Task 5).
- Produces (Task 8 binds these):
  - `BatchUpscaleTabViewModel.StepText` (`string?`), `StepExplanation` (`string?`), `HasStep` (`bool`)
  - `UpscaleImageItemViewModel.Description` (`string?`), `IsDescribed` (`bool`), `HasDescription` (`bool`)
  - `internal const string StepOneText = "Step 1 of 2 · Describing images with Qwen3-VL";`, `StepTwoText = "Step 2 of 2 · Upscaling";`, `StepOneExplanation = "Upscaled images appear in step 2. Qwen3-VL describes every image first so it loads only once; then it is unloaded and the upscaler gets the VRAM.";`

- [ ] **Step 1: Write the failing tests**

`DiffusionNexus.Tests/ViewModels/Tabs/BatchUpscaleVisionTwoStepTests.cs` (derives from the Task 5 class to reuse its setup; xUnit runs the inherited facts again, which is harmless — if that is unwanted, move the shared setup into an `abstract class BatchUpscaleRunFixture` and derive both test classes from it):

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services.Vision;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels.Tabs;

/// <summary>#608: Vision describes every image with Qwen3-VL kept loaded, frees it, then upscales.</summary>
public class BatchUpscaleVisionTwoStepTests : BatchUpscaleEngineRunTests
{
    private static string ModeOf(Dictionary<string, Action<JsonNode>> o)
    {
        var node = JsonNode.Parse("""{"inputs":{"mode":"","seed":0,"config_override":""}}""")!;
        o["3"](node);
        return node["inputs"]!["mode"]!.GetValue<string>();
    }

    private IEnumerable<(string Workflow, Dictionary<string, Action<JsonNode>> Overrides)> Describes =>
        Queued.Where(q => q.Workflow == "Qwen3-VL-Describe.json");

    private IEnumerable<(string Workflow, Dictionary<string, Action<JsonNode>> Overrides)> Upscales =>
        Queued.Where(q => q.Workflow == "Z-Image-Turbo-Upscale.json");

    [Fact]
    public async Task DescribesEveryImageFirst_KeepingQwenLoaded_ThenUpscalesEachWithItsOwnDescription()
    {
        var vm = Sut();
        var steps = new List<string?>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.StepText)) steps.Add(vm.StepText); };

        await RunAsync(vm, UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png", "c.png");

        Queued.Select(q => q.Workflow).Should().Equal(
            "Qwen3-VL-Describe.json", "Qwen3-VL-Describe.json", "Qwen3-VL-Describe.json",
            "Z-Image-Turbo-Upscale.json", "Z-Image-Turbo-Upscale.json", "Z-Image-Turbo-Upscale.json");
        Describes.Select(d => ModeOf(d.Overrides)).Should().Equal("keep_vram", "keep_vram", "direct_clean");
        Upscales.Select(u => PromptOf(u.Overrides)).Should().Equal(
            "Description of up-a.png", "Description of up-b.png", "Description of up-c.png");
        Upscales.Select(u => ImageOf(u.Overrides, "50")).Should().Equal("up-a.png", "up-b.png", "up-c.png");
        Client.Verify(c => c.UploadImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3),
            "step 2 reuses the uploads of step 1");
        steps.Should().Equal(BatchUpscaleTabViewModel.StepOneText, BatchUpscaleTabViewModel.StepTwoText, null);
        vm.UpscaleItems.Select(i => i.Description).Should().Equal(
            "Description of up-a.png", "Description of up-b.png", "Description of up-c.png");
        Logged.Should().Contain(("Info", "Description of a.png: Description of up-a.png"));
    }

    [Fact]
    public async Task OneImage_IsDescribedAndFreedInOneJob()
    {
        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png");

        Describes.Select(d => ModeOf(d.Overrides)).Should().Equal("direct_clean");
    }

    [Fact]
    public async Task CancelDuringStepOne_FreesQwen_AndUpscalesNothing()
    {
        var waits = 0;
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++waits == 2 ? Task.FromCanceled(new CancellationToken(true)) : Task.CompletedTask);

        var vm = Sut();
        await RunAsync(vm, UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png", "c.png");

        Upscales.Should().BeEmpty();
        Describes.Select(d => ModeOf(d.Overrides)).Should().Equal("keep_vram", "keep_vram", "direct_clean");
        ImageOf(Describes.Last().Overrides, "1").Should().Be("up-b.png", "the free-up job reuses the last upload");
        vm.StepText.Should().BeNull();
        vm.CurrentProcessingStatus.Should().StartWith("Cancelled");
    }

    [Fact]
    public async Task CancelBeforeTheFirstUpload_QueuesNothing()
    {
        Client.Setup(c => c.UploadImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png");

        Queued.Should().BeEmpty("nothing was loaded, so there is nothing to free");
    }

    [Fact]
    public async Task AFailedDescription_UpscalesThatImageWithoutAPrompt_AndWarns()
    {
        var results = 0;
        Client.Setup(c => c.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
            {
                var job = Queued[int.Parse(id[1..]) - 1];
                if (job.Workflow == "Qwen3-VL-Describe.json" && ++results == 2)
                    throw new ComfyUIExecutionException("SimpleQwenVLggufV2", "context overflow");
                return ResultFor(job);
            });

        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png", "c.png");

        Upscales.Select(u => PromptOf(u.Overrides)).Should().Equal("Description of up-a.png", "", "Description of up-c.png");
        Logged.Should().Contain(e => e.Level == "Warn" && e.Message.Contains("b.png") && e.Message.Contains("context overflow"));
    }

    [Fact]
    public async Task TheLastDescriptionFails_QwenIsFreedBeforeStepTwo()
    {
        var results = 0;
        Client.Setup(c => c.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
            {
                var job = Queued[int.Parse(id[1..]) - 1];
                if (job.Workflow == "Qwen3-VL-Describe.json" && ++results == 2)
                    throw new ComfyUIExecutionException("SimpleQwenVLggufV2", "boom");
                return ResultFor(job);
            });

        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png");

        Queued.Select(q => q.Workflow).Should().Equal(
            "Qwen3-VL-Describe.json", "Qwen3-VL-Describe.json", "Qwen3-VL-Describe.json",
            "Z-Image-Turbo-Upscale.json", "Z-Image-Turbo-Upscale.json");
        ModeOf(Queued[2].Overrides).Should().Be("direct_clean");
    }

    [Fact]
    public async Task WithoutTheModelPaths_ChecksOnce_ThenStopsBeforeUploading_WithTheInstallHint()
    {
        ReadinessWith(new Dictionary<string, string>());
        var vm = Sut();

        await RunAsync(vm, UpscalePromptMode.VisionAutoPrompt, "a.png");

        Queued.Should().BeEmpty();
        Client.Verify(c => c.UploadImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.CurrentProcessingStatus.Should().Contain("Install Batch Upscale Vision");
    }

    [Fact]
    public async Task OtherPromptModes_RunOneStep_WithoutABanner()
    {
        var vm = Sut();
        vm.PositivePrompt = "x";
        var steps = new List<string?>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.StepText)) steps.Add(vm.StepText); };

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png");

        Describes.Should().BeEmpty();
        steps.Should().BeEmpty();
    }
}
```

`ResultFor` is `protected virtual` in the base class; the two failing-description tests call it directly.

- [ ] **Step 2: Run to verify it fails**

Run: `--filter "FullyQualifiedName~BatchUpscaleVisionTwoStepTests"` — Expected: build error (`StepText`, `StepOneText`, `Description` missing).

- [ ] **Step 3: Item and step properties**

In `UpscaleImageItemViewModel` (same file, ~line 102), add:

```csharp
    private string? _description;
    private bool _isDescribed;

    /// <summary>What Qwen3-VL wrote for this image in step 1 of a Vision run; its upscale prompt.</summary>
    public string? Description
    {
        get => _description;
        set
        {
            if (SetProperty(ref _description, value))
                OnPropertyChanged(nameof(HasDescription));
        }
    }

    /// <summary>Step 1 is done for this image (described, or tried and failed).</summary>
    public bool IsDescribed
    {
        get => _isDescribed;
        set => SetProperty(ref _isDescribed, value);
    }

    public bool HasDescription => !string.IsNullOrWhiteSpace(_description);
```

In `BatchUpscaleTabViewModel`, add next to the processing-state fields:

```csharp
    internal const string StepOneText = "Step 1 of 2 · Describing images with Qwen3-VL";
    internal const string StepTwoText = "Step 2 of 2 · Upscaling";
    internal const string StepOneExplanation =
        "Upscaled images appear in step 2. Qwen3-VL describes every image first so it loads only once; then it is unloaded and the upscaler gets the VRAM.";

    private string? _stepText;
    private string? _stepExplanation;

    /// <summary>The step banner of a Vision run; null when the run has one step.</summary>
    public string? StepText
    {
        get => _stepText;
        private set
        {
            if (SetProperty(ref _stepText, value))
                OnPropertyChanged(nameof(HasStep));
        }
    }

    /// <summary>Why no upscaled image has appeared yet (step 1 only).</summary>
    public string? StepExplanation
    {
        get => _stepExplanation;
        private set => SetProperty(ref _stepExplanation, value);
    }

    public bool HasStep => _stepText is not null;
```

- [ ] **Step 4: The two-step run**

In `RunUpscaleLoopAsync`:
- the workflow is always the upscale one: `var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ManualUpscaleWorkflowPath);` (keep the existence check); delete the `VisionUpscaleWorkflowPath` constant;
- after the workflow existence check and before building the item list, resolve the model paths:

```csharp
        string modelPath = "", projectorPath = "";
        if (isVision)
        {
            // The GGUF node takes the files as paths; readiness found them. Re-check once when unknown.
            if (!QwenVlGguf.TryGetPaths(VisionReadiness.ModelPaths, out _, out _))
                await VisionReadiness.CheckReadinessAsync();
            if (!QwenVlGguf.TryGetPaths(VisionReadiness.ModelPaths, out modelPath, out projectorPath))
            {
                CurrentProcessingStatus = "The Qwen3-VL GGUF model was not found. Install Batch Upscale Vision in " +
                                          "Installation Manager → Diffusion Nexus Engine → Features.";
                Warn(CurrentProcessingStatus);
                return;
            }
        }
```
- inside `try`, after the `Info(... prompt mode ...)` line:

```csharp
            var uploaded = new string?[UpscaleItems.Count];
            if (isVision)
            {
                await DescribeAllAsync(comfy, new ImageDescriber(comfy, modelPath, projectorPath), uploaded, ct);
                StepText = StepTwoText;
                StepExplanation = null;
                TotalProgress = 0;
                Info("Step 2 of 2: upscaling.");
            }
```
- in the per-image loop: `var uploadedFilename = uploaded[i] ??= await comfy.UploadImageAsync(item.OriginalPath, ct);` (replaces the upload line; keep the "Uploading…" status only when it actually uploads), and the prompt becomes `var imagePositivePrompt = isVision ? item.Description ?? string.Empty : ResolvePositivePrompt(item.OriginalPath);`;
- in `finally`: `StepText = null; StepExplanation = null;`.

Add the step-1 method:

```csharp
    /// <summary>
    /// Step 1 of a Vision run: uploads and describes every image with Qwen3-VL kept loaded, the last one
    /// freeing it. Stopping early (cancel, an error) frees the model with one extra job, so it does not
    /// stay in the server's VRAM. A description that fails for one image is a warning: that image is
    /// upscaled without a prompt.
    /// </summary>
    private async Task DescribeAllAsync(IComfyUIWrapperService comfy, ImageDescriber describer, string?[] uploaded, CancellationToken ct)
    {
        StepText = StepOneText;
        StepExplanation = StepOneExplanation;
        TotalProgress = 0;
        Info("Step 1 of 2: describing every image with Qwen3-VL.");

        string? lastUploaded = null;
        var lastFailed = false;
        try
        {
            for (var i = 0; i < UpscaleItems.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = UpscaleItems[i];
                item.IsProcessing = true;
                CurrentProcessingStatus = $"[{i + 1}/{TotalImageCount}] Describing {item.FileName}…";

                uploaded[i] = await comfy.UploadImageAsync(item.OriginalPath, ct);
                lastUploaded = uploaded[i];
                try
                {
                    item.Description = await describer.DescribeAsync(uploaded[i]!, keepLoaded: i < UpscaleItems.Count - 1, ct);
                    lastFailed = false;
                    if (item.Description is null)
                        Warn($"Qwen3-VL returned no description for {item.FileName}; it is upscaled without a prompt.");
                    else
                        Info($"Description of {item.FileName}: {item.Description}");
                }
                catch (ComfyUIExecutionException ex)
                {
                    lastFailed = true;
                    Warn($"Could not describe {item.FileName} (node {ex.NodeType}: {ex.Detail}); it is upscaled without a prompt.");
                }

                item.IsProcessing = false;
                item.IsDescribed = true;
                TotalProgress = (double)(i + 1) / TotalImageCount * 100;
            }

            // The last job frees the model itself; when it failed, it may have died before doing so.
            if (lastFailed && lastUploaded is not null)
                await describer.FreeAsync(lastUploaded);
        }
        catch (Exception) when (lastUploaded is not null)
        {
            Info("Freeing Qwen3-VL on the server after step 1 stopped early.");
            await describer.FreeAsync(lastUploaded);
            throw;
        }
    }
```

Add `using DiffusionNexus.UI.Services.Vision;`. Update `PromptModeDescription` for `VisionAutoPrompt` (and the enum doc comment on `VisionAutoPrompt`) to: `Qwen3-VL describes each image (step 1), then each image is upscaled with its description as the prompt (step 2).`

- [ ] **Step 5: Run the tests**

Run: `--filter "FullyQualifiedName~BatchUpscaleVisionTwoStepTests|FullyQualifiedName~BatchUpscaleEngineRunTests"` — Expected: PASS. Full suite: 0 failed.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/Tabs/BatchUpscaleTabViewModel.cs DiffusionNexus.Tests/ViewModels/Tabs/BatchUpscaleVisionTwoStepTests.cs
git commit -m "feat(upscale): #608 Vision Auto-Prompt in two steps — describe all with Qwen3-VL kept loaded, free it, then upscale

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 7: Features rows, readiness mapping, embedded seed

**Prerequisite:** the owner has released catalog **v6** (Task 1, Step 5). Check: `gh release view -R Into-The-Latent/DiffusionNexus.Catalog --json tagName -q .tagName` prints `v6` or later. If not, stop and ask.

**Files:**
- Modify: `DiffusionNexus.UI/Services/Engine/EngineFeatureCatalog.cs`
- Modify: `DiffusionNexus.Service/Services/FeatureRegistry.cs`
- Modify: `DiffusionNexus.Domain/Services/FeatureBackendRouter.cs` (`ServerModeFeatures`)
- Modify: `DiffusionNexus.UI/Assets/Catalog/catalog.zip`, `manifest.json` (via `Scripts/Update-CatalogSeed.ps1`)
- Test: `DiffusionNexus.Tests/Engine/EngineFeatureCatalogTests.cs`, `DiffusionNexus.Tests/Engine/EngineFeatureBackendTests.cs`, router/registry tests (find with `grep -rln "ServerModeFeatures\|FeatureRegistry.Get" DiffusionNexus.Tests`)

**Interfaces:**
- Produces: `EngineFeature.BatchUpscale`, `EngineFeature.BatchUpscaleVision`; `EngineFeatureCatalog.UpscalingZImageTurbo` (`B853EB7C-…`), `EngineFeatureCatalog.UpscalingZImageTurboVision` (`FE2E7606-…`); `ForAppFeature(Feature.BatchUpscale) == EngineFeature.BatchUpscale`, `ForAppFeature(Feature.BatchUpscaleVision) == EngineFeature.BatchUpscaleVision`.

- [ ] **Step 1: Write the failing tests**

In `EngineFeatureCatalogTests.cs`:
- the workload-id test: add

```csharp
        EngineFeatureCatalog.Get(EngineFeature.BatchUpscale).WorkloadIds
            .Should().Equal(EngineFeatureCatalog.UpscalingZImageTurbo);
        EngineFeatureCatalog.Get(EngineFeature.BatchUpscaleVision).WorkloadIds
            .Should().Equal(EngineFeatureCatalog.UpscalingZImageTurboVision);
```
and change `AllWorkloadIds.Should().HaveCount(3)` to `HaveCount(5)`;
- the display-name test: expected order `"Inpaint & Outpaint", "Outpaint Vision", "Batch Upscale", "Batch Upscale Vision", "Canvas · Krea 2 Turbo"`;
- `ForAppFeature_MapsEditorToolsToTheirRow`: add `[InlineData(Feature.BatchUpscale, EngineFeature.BatchUpscale)]` and `[InlineData(Feature.BatchUpscaleVision, EngineFeature.BatchUpscaleVision)]`; remove those two from `ForAppFeature_IsNull_ForFeaturesTheEngineDoesNotOfferYet` (keep `Captioning` or whatever else is there; if the theory is left empty, use `Feature.Captioning`).

In `EngineFeatureBackendTests.cs`: rename `BatchUpscale_IsNotOfferedOnTheEngineYet` to `Captioning_IsNotOfferedOnTheEngineYet`, using `Feature.Captioning` and the message `"Captioning is not available on the Diffusion Nexus Engine yet"`.

Registry/router: add a test (in the existing FeatureRegistry test file, or a new `DiffusionNexus.Tests/Service/Services/FeatureRegistryBatchUpscaleTests.cs`):

```csharp
    [Fact]
    public void BatchUpscaleVision_IsItsOwnWorkload_AndBothFollowTheServerSetting()
    {
        FeatureRegistry.Get(Feature.BatchUpscale)!.WorkloadConfigurationId.Should().Be(Guid.Parse("B853EB7C-0A0E-48A6-985E-E32B2F8848F5"));
        FeatureRegistry.Get(Feature.BatchUpscaleVision)!.WorkloadConfigurationId.Should().Be(Guid.Parse("FE2E7606-AC36-470E-A7C5-7F8CC23ECC98"));
        FeatureBackendRouter.ServerModeFeatures.Should().Contain([Feature.BatchUpscale, Feature.BatchUpscaleVision]);
    }
```
(Check the registry's lookup method name with `grep -n "public static" DiffusionNexus.Service/Services/FeatureRegistry.cs` and use it.)

- [ ] **Step 2: Run to verify it fails**

Run: `--filter "FullyQualifiedName~EngineFeatureCatalogTests|FullyQualifiedName~EngineFeatureBackendTests|FullyQualifiedName~FeatureRegistry"` — Expected: build error / FAIL.

- [ ] **Step 3: Implement**

`EngineFeatureCatalog.cs`:
- enum: add after `OutpaintVision`:

```csharp
    /// <summary>Batch Upscale with a typed, caption or metadata prompt: Z-Image Turbo + Ultimate SD Upscale.</summary>
    BatchUpscale,

    /// <summary>Batch Upscale with Vision Auto-Prompt: the upscale set plus the Qwen3-VL GGUF describer.</summary>
    BatchUpscaleVision,
```
- ids:

```csharp
    /// <summary>"Upscaling-Z-Image-Turbo": Z-Image Turbo bf16, qwen_3_4b, ae, 4x-UltraSharp + ComfyUI_UltimateSDUpscale.</summary>
    public static readonly Guid UpscalingZImageTurbo = Guid.Parse("B853EB7C-0A0E-48A6-985E-E32B2F8848F5");

    /// <summary>
    /// "Upscaling-Z-Image-Turbo Vision": the upscale set plus ComfyUI_Simple_Qwen3-VL-gguf, ComfyUI-Custom-Scripts
    /// and the Qwen3-VL 8B GGUF with its projector (same names as Outpaint Vision, so nothing downloads twice).
    /// </summary>
    public static readonly Guid UpscalingZImageTurboVision = Guid.Parse("FE2E7606-AC36-470E-A7C5-7F8CC23ECC98");
```
- rows, between Outpaint Vision and Canvas:

```csharp
        new(EngineFeature.BatchUpscale,
            "Batch Upscale",
            "Batch Upscale · Z-Image Turbo with Ultimate SD Upscale and 4x-UltraSharp",
            [UpscalingZImageTurbo]),
        new(EngineFeature.BatchUpscaleVision,
            "Batch Upscale Vision",
            "Batch Upscale · Qwen3-VL (GGUF, llama.cpp) describes each image and writes its upscale prompt",
            [UpscalingZImageTurboVision]),
```
- `ForAppFeature`: add `Feature.BatchUpscale => EngineFeature.BatchUpscale,` and `Feature.BatchUpscaleVision => EngineFeature.BatchUpscaleVision,`;
- class doc: drop "Batch Upscale arrives with #608."

`FeatureRegistry.cs`: add `private static readonly Guid BatchUpscaleVisionWorkloadId = new("FE2E7606-AC36-470E-A7C5-7F8CC23ECC98"); // Upscaling-Z-Image-Turbo Vision`, fix the `BatchUpscaleWorkloadId` comment (drop "covers Vision variant"), and point `[Feature.BatchUpscaleVision]` at `BatchUpscaleVisionWorkloadId`.

`FeatureBackendRouter.cs`: `ServerModeFeatures` = `{ Feature.Inpainting, Feature.Outpaint, Feature.OutpaintVision, Feature.BatchUpscale, Feature.BatchUpscaleVision }`; extend the doc comment: "Batch Upscale (#608) runs both its workflows through the same client."

- [ ] **Step 4: Refresh the embedded seed**

```powershell
pwsh E:\Repos\DiffusionNexus\Scripts\Update-CatalogSeed.ps1
```
Expected: "embedded v6" (or later). `CatalogSeedTests` now finds `FE2E7606-…`.

- [ ] **Step 5: Run the tests**

Run: `--filter "FullyQualifiedName~EngineFeatureCatalogTests|FullyQualifiedName~EngineFeatureBackendTests|FullyQualifiedName~FeatureRegistry|FullyQualifiedName~CatalogSeedTests|FullyQualifiedName~FeatureReadiness"` — Expected: PASS. Full suite: 0 failed.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/Services/Engine/EngineFeatureCatalog.cs DiffusionNexus.Service/Services/FeatureRegistry.cs DiffusionNexus.Domain/Services/FeatureBackendRouter.cs DiffusionNexus.UI/Assets/Catalog DiffusionNexus.Tests
git commit -m "feat(engine): #608 Batch Upscale and Batch Upscale Vision Features rows; both follow the Settings server; catalog seed v6

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 8: The tab's step banner and described thumbnails

**Files:**
- Modify: `DiffusionNexus.UI/Views/Tabs/BatchUpscaleTabView.axaml` (progress area ~line 183; thumbnail template ~line 230)

**Interfaces:**
- Consumes: `StepText`, `StepExplanation`, `HasStep` (VM); `Description`, `IsDescribed`, `HasDescription` (item) — Task 6.

- [ ] **Step 1: The banner above the progress bar**

Insert before `<!-- Progress -->`:

```xml
            <!-- Vision runs in two steps (#608): say which, and why no upscaled image has appeared yet -->
            <Border IsVisible="{Binding HasStep}" Background="#1F2A36" CornerRadius="4" Padding="10,8"
                    BorderBrush="#3A7BD5" BorderThickness="1">
              <StackPanel Spacing="4">
                <TextBlock Text="{Binding StepText}" FontWeight="SemiBold" FontSize="13"/>
                <TextBlock Text="{Binding StepExplanation}" FontSize="12" Opacity="0.8" TextWrapping="Wrap"
                           IsVisible="{Binding StepExplanation, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
              </StackPanel>
            </Border>
```

- [ ] **Step 2: Described thumbnails and the tooltip**

In the gallery `DataTemplate` border: add `Classes.described="{Binding IsDescribed}"` and `ToolTip.Tip="{Binding Description}"`, `ToolTip.IsEnabled="{Binding HasDescription}"`; add to its `Border.Styles` **between** the base style and `processing` (later selectors win, so processing/processed still override):

```xml
                    <Style Selector="Border.described">
                      <Setter Property="BorderBrush" Value="#3A7BD5"/>
                    </Style>
```

- [ ] **Step 3: Build and check for binding errors**

Run: `dotnet build E:\Repos\DiffusionNexus\DiffusionNexus.sln -c Debug` — Expected: 0 errors, no new AVLN warnings for this view (compiled bindings would flag a wrong property name).

- [ ] **Step 4: Commit**

```bash
git add DiffusionNexus.UI/Views/Tabs/BatchUpscaleTabView.axaml
git commit -m "feat(upscale): #608 step banner (Step 1 of 2 / Step 2 of 2) and described thumbnails with the description as tooltip

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 9: BenQ smoke on the Engine, PR

**Files:**
- Create: `.superpowers/sdd/2026-10-10-batch-upscale-on-engine/smoke-brief.md`, `smoke-report.md`, `smoke/*.png`
- PR body: `<scratchpad>/pr-608.md`

- [ ] **Step 1: Prepare**

- The app reads the published catalog at start; until v6 is applied, launch with `DIFFUSIONNEXUS_CATALOG_PATH=E:\Repos\DiffusionNexus.Catalog`.
- Engine at `E:\Installer\7\ComfyUI`; Settings → ComfyUI Server = Diffusion Nexus Engine.
- Three test images (e.g. `E:\Installer\7\ComfyUI\output\ComfyUI_0000{1..3}_.png`).

- [ ] **Step 2: Run the checklist (BenQ only, UI Automation, screenshots with SetThreadDpiAwarenessContext(-4))**

1. Features dialog lists **Batch Upscale** and **Batch Upscale Vision** with their descriptions; install both (models already on disk are found, not downloaded; the Vision row installs the llama-cpp-python wheel step as "already installed").
2. Batch Upscale tab: on opening it, the readiness panel shows "Running on Diffusion Nexus Engine" without pressing Check; with a row missing it shows the install link, which opens Features with that row ticked.
3. Manual prompt, three images → three upscaled results; Unified Console shows "Running on the Diffusion Nexus Engine…" and "Upscaled …" lines.
4. Vision Auto-Prompt, three images → banner "Step 1 of 2 · Describing images with Qwen3-VL" with the explanation; thumbnails turn blue with the description as tooltip; then "Step 2 of 2 · Upscaling"; three results; console shows each "Description of …"; record step-1 seconds per image.
5. Vision run cancelled during step 1 → status "Cancelled…", banner gone; the Engine's VRAM drops back (nvidia-smi before/after).

- [ ] **Step 3: Full suite, CRLF check, PR**

```bash
dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj
git -C /e/Repos/DiffusionNexus diff origin/develop --numstat | head -50
gh auth switch --user Little-God1983
gh pr create --base develop --head feature/batch-upscale-on-engine --title "Engine: Batch Upscale (normal + Vision) runs on the Diffusion Nexus Engine" --body-file "<scratchpad>/pr-608.md"
```
The PR body: what the user sees, why two steps, catalog changes (and the v6 dependency), the smoke table, known gaps, `Closes #608`, ending with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Do not merge.
