# Extracting generation parameters from ComfyUI images (spec for a C#/.NET port)

**Goal for the target app:** drag-and-drop ComfyUI-generated images, recover the *real* generation
parameters (positive/negative prompt, sampler, scheduler, seed, steps, CFG, model, and **all LoRAs
— including rgthree Power Lora Loader and Lora Loader Stack**), then re-save each image into a new
folder **stripped of the embedded workflow** (clean pixels + optional A1111-style parameter text).

This document describes the exact algorithm already implemented and tested in
`ComfyUI-AI2Go-Utils/nodes/civitai_metadata/tracer.py`. Porting it is mostly mechanical because the
data ComfyUI embeds in the PNG is the same structure the tracer consumes.

---

## 1. The single most important fact

A ComfyUI PNG embeds **two** JSON documents as text chunks:

| PNG text keyword | Contents | Use it? |
|---|---|---|
| `prompt`   | The **API prompt** — a flat map `{ "<nodeId>": { "class_type": "...", "inputs": {...} } }`. Fully resolved. | ✅ **YES** |
| `workflow` | The **editor graph** — litegraph UI format (`nodes[]` with `widgets_values` arrays, `links[]`). Positional, harder, version-fragile. | ❌ avoid |

**Parse the `prompt` chunk.** It is exactly the structure the algorithm below walks. The `workflow`
chunk is the UI graph — don't parse it unless `prompt` is missing. (In the `workflow` format the
rgthree Power Lora Loader stores its LoRAs as opaque widget objects that differ between plugin
versions; in the `prompt` format they arrive as clean `inputs` dicts. This is why using `prompt` is
what makes Power Lora / Lora Stack detection tractable.)

> ComfyUI's `SaveImage` writes both by default (unless the server was started with
> `--disable-metadata`). Some third-party save nodes only write one, or none — handle absence
> gracefully.

### Where the JSON lives per format
- **PNG:** uncompressed `tEXt` chunk, keyword `prompt` (and `workflow`), Latin-1/ASCII bytes. ComfyUI
  uses `json.dumps(..., ensure_ascii=True)`, so non-ASCII prompt text is `\uXXXX`-escaped and fits in
  a `tEXt` chunk. Be tolerant of `iTXt`/`zTXt` (compressed) too — some custom nodes use them.
- **JPEG / WebP:** JSON is in EXIF, usually **UserComment (0x9286)** or **ImageDescription (0x010E)**,
  sometimes prefixed with `Prompt:` / `Workflow:`.

**Recommended C# reader:** `MetadataExtractor` (drewnoakes) NuGet handles PNG tEXt/iTXt/zTXt **and**
JPEG/WebP EXIF, so one library covers all inputs. `System.Drawing` does *not* expose PNG text chunks
— don't rely on it. A ~40-line manual PNG chunk walker also works if you want zero dependencies (read
the 8-byte signature, then loop `length[4] | type[4] | data | crc[4]`; for `tEXt`, data is
`keyword \0 text`).

---

## 2. Data model

Parse the `prompt` JSON into:

```csharp
// prompt = Dictionary<string, Node>
sealed class Node {
    public string ClassType { get; init; }               // "class_type"
    public Dictionary<string, object> Inputs { get; init; } // "inputs"
}
```

Two shapes appear as `inputs` values:

- **Literal**: a string / number / bool / dict (e.g. `"euler"`, `20`, `7.0`, or a Power-Lora dict).
- **Link**: a JSON array of exactly two elements `["<originNodeId>", <outputSlot:int>]`, e.g.
  `["8", 0]`. **`originNodeId` may deserialize as a number or string — always normalize to string.**

```csharp
static bool IsLink(object v) =>
    v is IList<object> a && a.Count == 2 && a[1] is int or long; // and a[0] is id
// helper: originId(v) => Convert.ToString(((IList<object>)v)[0]);   slot => Convert.ToInt32(a[1]);
```

Result container:

```csharp
sealed class TraceResult {
    public string Positive = "", Negative = "";
    public object Steps, Cfg, Seed, Denoise, ClipSkip;      // null if unresolved
    public string SamplerName, Scheduler;
    public string ModelName, ModelFile, ModelFolder;
    public List<LoraRef> Loras = new();                     // in load order
    public List<string> Unresolved = new();                 // field names we couldn't resolve
}
sealed class LoraRef { public string Name, File; public double? Strength; }
```

---

## 3. The algorithm (port of `tracer.py`)

### 3.1 Pick the starting node

You are tracing one generated image, so you want the terminal sampler that produced it.

1. **If you can identify the save node** (class_type in `{"SaveImage","PreviewImage", …}`, or you know
   which node saved this file): read its `images` input. If it's a link, BFS backward from
   `originId(images)` to the nearest sampler.
2. **Fallback — single sampler:** if the graph contains exactly one sampler node, just use it. (Most
   single-image workflows hit this path; it's the pragmatic shortcut and it's what handles images
   whose save node you can't identify.)
3. If there are multiple samplers and no save node to disambiguate (refiner / upscale chains), prefer
   the sampler reachable from the save node; if unknown, the last-executed one is usually the one
   closest to the save node in the link graph.

```
SAMPLER_CLASSES = { KSampler, KSamplerAdvanced, SamplerCustom, SamplerCustomAdvanced }
```

**BFS backward** = breadth-first over *input links only*: from a node, enqueue `originId(v)` for every
input value `v` that `IsLink(v)`; stop when you reach a node whose `class_type ∈ SAMPLER_CLASSES`.
Track a `seen` set to avoid cycles.

### 3.2 Read sampler scalar fields

From the sampler node's `inputs`, take each value **only if it is a literal** (a linked value means
it's computed elsewhere — leave it null and add the field name to `Unresolved`):

```
steps        = literal("steps")
cfg          = literal("cfg")
seed         = literal("seed")  ?? literal("noise_seed")   // KSamplerAdvanced uses noise_seed
sampler_name = literal("sampler_name")
scheduler    = literal("scheduler")
denoise      = literal("denoise")
```
Mark `steps`, `cfg`, `sampler_name` as unresolved if null (they're the ones worth warning about).

### 3.3 Resolve positive / negative prompts

For each of `positive` and `negative` on the sampler:

1. It's a link to conditioning. BFS backward from it to the nearest `CLIPTextEncode`
   (walk through conditioning combiners like `ConditioningCombine`/`ConditioningConcat` — the BFS
   naturally passes through them since it follows every input link).
2. On the `CLIPTextEncode`, read `text`:
   - If `text` is a **string literal** → that's your prompt.
   - If `text` is a **link** → follow it to the origin node and take the first **string** input you
     find (covers `PrimitiveNode` / string / "Text" nodes feeding the encoder).
3. If nothing resolves, leave empty and add `"positive"` / `"negative"` to `Unresolved`.

> **Skip the AI2GoPromptBatch special case.** In this repo, a linked `text` can come from our batch
> node and is resolved by re-running `select_prompt(prompts_json, index)`. Your app doesn't have that
> node — just do step 2's "follow the link, take the first string." If the origin is a batch/list node
> you don't recognize and the text isn't a plain string, treat positive/negative as unresolved rather
> than guessing.

### 3.4 Walk the model chain (checkpoint + LoRAs) — the important part

Start from the sampler's `model` input link; walk backward node-by-node via each node's `model` input.
Collect LoRAs as you go, then **reverse** the collected list at the end (you collect
sampler→checkpoint; load order is checkpoint→sampler).

```
current = originId(sampler.inputs["model"])
seen = {}
loras = []            // collected sampler-first
while current not null and current not in seen:
    seen.add(current)
    node = prompt[current]; cls = node.class_type; ins = node.inputs

    if cls in { LoraLoader, LoraLoaderModelOnly }:
        name = ins["lora_name"]
        strength = ins["strength_model"] ?? ins["strength"]
        if name is string: loras.add({ stem(name), strength(if literal), name })
        current = originId(ins["model"]); continue

    if cls == "Power Lora Loader (rgthree)":
        for each entry from PowerLoraEntries(ins) in REVERSE:   // reverse within node
            loras.add(entry)
        current = originId(ins["model"]); continue

    if cls == "Lora Loader Stack (rgthree)":
        for each entry from LoraStackEntries(ins) in REVERSE:
            loras.add(entry)
        current = originId(ins["model"]); continue

    if cls in MODEL_SOURCES:                 // a checkpoint / diffusion-model loader
        (widget, folder) = MODEL_SOURCES[cls]
        name = ins[widget]
        if name is string:
            model_name = stem(name); model_file = name; model_folder = folder
        break                                // reached the source — done

    // unknown pass-through node (ModelSamplingDiscrete, FreeU, CFGGuider, etc.)
    current = originId(ins["model"]);        // if it has no model link, current = null → loop ends

loras.reverse()                              // now in load order
```

```
MODEL_SOURCES = {
    CheckpointLoaderSimple : ("ckpt_name",  "checkpoints"),
    CheckpointLoader       : ("ckpt_name",  "checkpoints"),
    unCLIPCheckpointLoader : ("ckpt_name",  "checkpoints"),
    UNETLoader             : ("unet_name",  "diffusion_models"),   // "Load Diffusion Model": Flux/SD3/Krea
    UnetLoaderGGUF         : ("unet_name",  "diffusion_models"),
    UnetLoaderGGUFAdvanced : ("unet_name",  "diffusion_models"),
}
```

`stem(name)` = filename without directory and without extension, tolerating `\` and `/`
(`sub/styleB.safetensors` → `styleB`). Keep both: `Name` (stem, for display / `<lora:...>` tokens)
and `File` (the raw value, e.g. for hashing against a models folder).

#### PowerLoraEntries(ins) — rgthree **Power Lora Loader**
Inputs contain `lora_1`, `lora_2`, … each a **dict** `{ on, lora, strength, strengthTwo }`
(plus a header widget and an "Add Lora" entry, which are filtered out by the shape check):

```
for (key, value) in ins:
    if not (value is dict AND key.ToUpper().StartsWith("LORA_")): continue   // excludes header/add-lora
    name = value["lora"]
    if name is not string OR name == "" OR name == "None" OR value["on"] == false: continue  // skip disabled/empty
    strength = value["strength"]                    // model strength; strengthTwo is the clip strength
    emit { stem(name), (strength if literal else null), name }
```
Preserve the natural key order (`lora_1`, `lora_2`, …). Emit them **reversed** into the collector so
the final global `reverse()` restores listed order.

#### LoraStackEntries(ins) — rgthree **Lora Loader Stack**
Flatter: paired `lora_01`/`strength_01` … `lora_04`/`strength_04`.

```
nums = [ suffix of key for key in ins if key startsWith "lora_" and suffix is all digits ]
for num in nums sorted numerically:
    name = ins["lora_" + num]
    if name is not string OR name == "" OR name == "None": continue        // empty slot
    strength = ins["strength_" + num]
    if strength is literal AND strength == 0: continue                      // zero-strength: rgthree won't load it
    emit { stem(name), (strength if literal else null), name }
```
Same reversal rule as above.

> **Why the skip rules matter:** disabled toggles, `"None"` slots, and zero-strength entries never
> touch the pixels. Recording them would put LoRAs in your metadata that weren't actually used. These
> rules mirror rgthree's own load behavior and are covered by tests
> (`tests/test_tracer_model.py::test_power_lora_loader_rgthree_multiple`,
> `::test_lora_loader_stack_rgthree`, `::test_power_lora_loader_mixed_with_standard_load_order`).

### 3.5 Ordering guarantee
Because every branch pushes entries **reversed within the node** and the loop collects
sampler→checkpoint, the single `loras.reverse()` at the end yields the true **load order**
(checkpoint-nearest LoRA first). A mixed graph
`sampler ← PowerLora(A,B) ← LoraLoader(C) ← checkpoint` correctly produces `[C, A, B]`.

---

## 4. Re-saving the image "clean" (workflow stripped)

Two independent steps:

### 4.1 Strip the embedded graph
- **Decode → re-encode** the pixels with a library that doesn't copy ancillary text chunks
  (ImageSharp `SixLabors.ImageSharp`, or a manual PNG rewrite that drops `tEXt`/`iTXt`/`zTXt` chunks
  whose keyword ∈ {`prompt`,`workflow`,`parameters`}). PNG is lossless, so pixels are byte-identical
  content-wise; you're only dropping metadata.
- If the input is JPEG/WebP, strip the EXIF UserComment/ImageDescription carrying the JSON.
- Write to the new output folder.

### 4.2 (Optional but recommended) write A1111-style parameters back
Your app already reads Automatic1111 `parameters`. You can emit an A1111 string from the trace so the
cleaned images round-trip through your existing reader:

```
<positive, with LoRAs appended as tokens: " <lora:Name:Strength>" for each lora>
Negative prompt: <negative>
Steps: <steps>, Sampler: <a1111SamplerName>, Schedule type: <scheduler>, CFG scale: <cfg>, Seed: <seed>, Size: <w>x<h>, Model: <ModelName>
```

Caveats when producing A1111 text:
- **Sampler name mapping differs.** ComfyUI `sampler_name` + `scheduler` → A1111's combined name, e.g.
  `euler`/`normal` → `Euler`; `dpmpp_2m`/`karras` → `DPM++ 2M Karras`. Keep a small lookup table; if
  unmapped, pass the raw ComfyUI name through.
- **LoRAs live in the prompt in A1111** as `<lora:name:strength>` — use the `Name` (stem) and the
  model strength. If strength was a link (unresolved), omit the number or default to `1`.
- **Size** isn't in the sampler; read it from the empty-latent node if you need it, or from the image
  dimensions themselves.
- Store the model file/hash if you compute it (hash the file in `ModelFolder` if you have the models
  locally); otherwise leave `Model:` as the stem.

If you'd rather not fake an A1111 string, just keep the `TraceResult` as your own structured sidecar
(JSON next to each output image). That's cleaner and lossless.

---

## 5. Gotchas / test checklist

1. **Use the `prompt` chunk, not `workflow`.** (§1) This is the whole reason rgthree detection is easy.
2. **Link ids come as number or string** — normalize to string before dict lookups. (§2)
3. **Literal-vs-link discipline:** only trust a field if it's a literal; a link means "computed
   elsewhere" → mark unresolved rather than storing the raw `[id,slot]` array.
4. **seed vs noise_seed** (KSamplerAdvanced). (§3.2)
5. **Skip disabled / `"None"` / zero-strength LoRAs** — match what actually generated the image. (§3.4)
6. **Reverse for load order** — collected sampler-first, emitted checkpoint-first. (§3.5)
7. **Pass-through nodes** (`ModelSamplingDiscrete`, `FreeU`, `CFGGuider`, LoRA *stacks* from other
   packs you don't special-case): follow the `model` input; if a node has no `model` link the walk
   ends and you keep whatever you found.
8. **Missing metadata:** images from other tools, or `--disable-metadata`, or re-saved elsewhere may
   have no `prompt` chunk. Fail soft — report "no ComfyUI metadata" and skip.
9. **Multi-sampler graphs** (refiner/upscale): disambiguate via the save node; the single-sampler
   fallback only applies when there's exactly one.
10. **Conditioning combiners / concat:** BFS through them reaches the `CLIPTextEncode`; deeply
    concatenated multi-node prompts may only surface the first string — acceptable for most images.

### Minimal fixtures to test against (mirror the Python tests)
- Plain checkpoint, no LoRA → model name only.
- `LoraLoader` chain A←B → load order `[A, B]`.
- `LoraLoaderModelOnly` behind a pass-through (Flux/UNETLoader wiring).
- **Power Lora Loader** with enabled + disabled + `"None"` slots → only the enabled real ones, in order.
- **Lora Loader Stack** with a `"None"` slot and a zero-strength slot → both skipped.
- Mixed **Power Lora + stock LoraLoader** → correct global load order.
- `UNETLoader` diffusion model → `model_folder == "diffusion_models"`.

These map 1:1 to `tests/test_tracer_model.py`, which you can read as executable reference behavior.

---

## 6. Reference files in the ComfyUI-AI2Go-Utils repo
- `nodes/civitai_metadata/tracer.py` — the authoritative algorithm (Python). Everything above is a
  faithful description of it.
- `web/js/save_civitai_metadata.js` — the same algorithm in JS, operating on the live editor graph
  (a second, independent reference implementation if you prefer reading JS).
- `tests/test_tracer_model.py` — behavioral spec / fixtures for the model+LoRA chain.
- `nodes/prompt_batch_core.py` — only relevant if you ever need the batch-prompt resolution; your app
  can ignore it.
