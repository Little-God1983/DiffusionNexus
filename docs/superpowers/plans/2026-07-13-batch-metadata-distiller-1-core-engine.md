# Batch Metadata Distiller — Part 1: Core Engine — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the pure, UI-free metadata engine that recovers ComfyUI generation parameters (prompt, sampler, CFG, LoRAs incl. rgthree Power Lora Loader / Lora Loader Stack), applies delete/replace rule sets, formats an A1111 `parameters` string, optionally hashes resources, and writes clean/stripped PNG copies.

**Architecture:** Six focused, `internal` classes in `DiffusionNexus.UI/Services/Distiller/` plus targeted edits to two existing readers/writers. Every class is a pure function or a thin service with no Avalonia dependency, so all of it is unit-tested from `DiffusionNexus.Tests` (which already has `InternalsVisibleTo` access to `DiffusionNexus.UI`). Part 2 (gallery + run screen) consumes these.

**Tech Stack:** C# / .NET 10, `System.Text.Json`, `System.IO.Compression.ZLibStream`, xUnit + FluentAssertions + Moq.

## Global Constraints

- Target framework: `net10.0`; `Nullable` enabled; `ImplicitUsings` enabled (both `DiffusionNexus.UI` and `DiffusionNexus.Tests`).
- New engine classes are `internal` and live in namespace `DiffusionNexus.UI.Services.Distiller`. `DiffusionNexus.UI/Properties/AssemblyInfo.cs` already has `[assembly: InternalsVisibleTo("DiffusionNexus.Tests")]` — do not duplicate it.
- Tests: xUnit (`[Fact]`/`[Theory]`), FluentAssertions (`.Should()`), Moq. Test project already has global `using Xunit;`.
- Reference algorithm: `comfyui-metadata-extraction-for-csharp.md` (repo root) — cited by section (§) throughout.
- Reuse the existing models `DiffusionNexus.UI.Models.ImageGenerationData` and `LoraInfo`; do not create parallel result types.
- Work on branch `feature/batch-metadata-distiller` (already created). Commit after every task.
- Build with `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj` and test with `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` from `e:\Repos\DiffusionNexus`.

---

### Task 1: Add `Source` to `LoraInfo`

**Files:**
- Modify: `DiffusionNexus.UI/Models/LoraInfo.cs`
- Test: `DiffusionNexus.Tests/Distiller/LoraInfoTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `LoraInfo.Source` (`string?`, default `null`) — the loader that emitted the LoRA ("Power Lora", "Lora Stack", "LoraLoader"), consumed by the tracer (Task 3) and the run-screen VM (Part 2).

- [ ] **Step 1: Read the current record**

Open `DiffusionNexus.UI/Models/LoraInfo.cs` and confirm it is a `record` with `Name`, `StrengthModel = 1.0`, `StrengthClip = 1.0`.

- [ ] **Step 2: Write the failing test**

Create `DiffusionNexus.Tests/Distiller/LoraInfoTests.cs`:

```csharp
using DiffusionNexus.UI.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class LoraInfoTests
{
    [Fact]
    public void Source_defaults_to_null_and_is_settable()
    {
        var a = new LoraInfo { Name = "x" };
        a.Source.Should().BeNull();

        var b = new LoraInfo { Name = "x", Source = "Power Lora" };
        b.Source.Should().Be("Power Lora");
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~LoraInfoTests"`
Expected: FAIL to compile — `'LoraInfo' does not contain a definition for 'Source'`.

- [ ] **Step 4: Add the property**

In `DiffusionNexus.UI/Models/LoraInfo.cs`, add inside the record (additive, nullable, default null):

```csharp
    /// <summary>The loader that emitted this LoRA ("Power Lora", "Lora Stack", "LoraLoader"), or null.</summary>
    public string? Source { get; init; }
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~LoraInfoTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/Models/LoraInfo.cs DiffusionNexus.Tests/Distiller/LoraInfoTests.cs
git commit -m "feat(distiller): add optional Source to LoraInfo"
```

---

### Task 2: `PngChunkReader` — read compressed `zTXt` chunks

**Files:**
- Modify: `DiffusionNexus.UI/Services/PngChunkReader.cs`
- Test: `DiffusionNexus.Tests/Distiller/PngChunkReaderZTxtTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `PngChunkReader.ReadTextChunks` now also returns entries stored in `zTXt` (zlib-compressed) chunks. Signature unchanged: `static Dictionary<string,string> ReadTextChunks(string filePath)`.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Tests/Distiller/PngChunkReaderZTxtTests.cs`:

```csharp
using System.IO;
using System.IO.Compression;
using System.Text;
using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class PngChunkReaderZTxtTests
{
    [Fact]
    public void ReadTextChunks_decompresses_zTXt_chunk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ztxt_{System.Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, BuildPngWithZTxt("prompt", "hello ztxt"));

            var chunks = PngChunkReader.ReadTextChunks(path);

            chunks.Should().ContainKey("prompt");
            chunks["prompt"].Should().Be("hello ztxt");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Minimal PNG: signature + IHDR(13 bytes, dummy) + zTXt + IEND. CRCs are dummy (reader skips them).
    private static byte[] BuildPngWithZTxt(string keyword, string text)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]); // PNG signature

        WriteChunk(ms, "IHDR", new byte[13]); // 13 zero bytes is enough for the reader (it skips IHDR)

        // zTXt data = keyword + 0x00 + compressionMethod(0) + zlib(text)
        using var comp = new MemoryStream();
        comp.Write(Encoding.Latin1.GetBytes(keyword));
        comp.WriteByte(0x00);
        comp.WriteByte(0x00); // compression method: 0 = zlib/deflate
        var raw = Encoding.Latin1.GetBytes(text);
        using (var z = new ZLibStream(comp, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);
        WriteChunk(ms, "zTXt", comp.ToArray());

        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = System.BitConverter.GetBytes(data.Length);
        if (System.BitConverter.IsLittleEndian) System.Array.Reverse(len);
        s.Write(len);
        s.Write(Encoding.ASCII.GetBytes(type));
        s.Write(data);
        s.Write(new byte[4]); // dummy CRC — PngChunkReader does not verify it
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~PngChunkReaderZTxtTests"`
Expected: FAIL — `chunks` does not contain key "prompt" (zTXt currently skipped).

- [ ] **Step 3: Implement zTXt support**

In `DiffusionNexus.UI/Services/PngChunkReader.cs`, change the chunk-type gate in `ReadTextChunks` from:

```csharp
            if (length > 0 && type is "tEXt" or "iTXt")
```

to:

```csharp
            if (length > 0 && type is "tEXt" or "iTXt" or "zTXt")
```

Then add a `zTXt` branch at the end of `ParseTextChunk` (before the closing brace of the method):

```csharp
        else if (type == "zTXt")
        {
            int nullIdx = Array.IndexOf(data, (byte)0);
            if (nullIdx < 0) return;

            var key = Encoding.Latin1.GetString(data, 0, nullIdx);

            // data[nullIdx+1] = compression method (0 = zlib). Compressed stream follows.
            int pos = nullIdx + 2;
            if (pos >= data.Length) return;

            try
            {
                using var compressed = new MemoryStream(data, pos, data.Length - pos);
                using var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                zlib.CopyTo(outMs);
                result[key] = Encoding.Latin1.GetString(outMs.ToArray());
            }
            catch
            {
                // Corrupt/unsupported compression — skip this chunk, keep parsing the rest.
            }
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~PngChunkReaderZTxtTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/PngChunkReader.cs DiffusionNexus.Tests/Distiller/PngChunkReaderZTxtTests.cs
git commit -m "feat(distiller): read compressed zTXt PNG chunks"
```

---

### Task 3: `ComfyUiPromptTracer` — port of tracer.py

**Files:**
- Create: `DiffusionNexus.UI/Services/Distiller/ComfyUiPromptTracer.cs`
- Test: `DiffusionNexus.Tests/Distiller/ComfyUiPromptTracerTests.cs`

**Interfaces:**
- Consumes: `LoraInfo.Source` (Task 1); models `ImageGenerationData`, `LoraInfo`.
- Produces: `static ImageGenerationData ComfyUiPromptTracer.Trace(Dictionary<string, JsonElement> graph, string? fileName, int width, int height)` — traces one image's parameters. Returns `HasData = false` when no sampler is found. LoRAs are returned in **load order** (checkpoint-nearest first).

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/Distiller/ComfyUiPromptTracerTests.cs`:

```csharp
using System.Collections.Generic;
using System.Text.Json;
using DiffusionNexus.UI.Services.Distiller;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class ComfyUiPromptTracerTests
{
    private static Dictionary<string, JsonElement> Graph(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Plain_checkpoint_no_lora()
    {
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{
                 "model":["4",0],"positive":["6",0],"negative":["7",0],
                 "steps":20,"cfg":7.0,"seed":123,"sampler_name":"euler","scheduler":"normal","denoise":1.0}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"sub/sd_xl.safetensors"}},
          "6": {"class_type":"CLIPTextEncode","inputs":{"text":"a cat"}},
          "7": {"class_type":"CLIPTextEncode","inputs":{"text":"blurry"}},
          "9": {"class_type":"SaveImage","inputs":{"images":["3",0]}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, "x.png", 512, 768);

        r.HasData.Should().BeTrue();
        r.Checkpoint.Should().Be("sd_xl");
        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().Be("blurry");
        r.Steps.Should().Be(20);
        r.Cfg.Should().Be(7.0);
        r.Seed.Should().Be(123);
        r.SamplerName.Should().Be("euler");
        r.Scheduler.Should().Be("normal");
        r.Loras.Should().BeEmpty();
    }

    [Fact]
    public void LoraLoader_chain_is_in_load_order()
    {
        // sampler <- A <- B <- checkpoint  => load order [B, A] (B nearest checkpoint)
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{"model":["10",0],"positive":["6",0],"negative":["7",0]}},
          "10":{"class_type":"LoraLoader","inputs":{"lora_name":"A.safetensors","strength_model":0.8,"model":["11",0]}},
          "11":{"class_type":"LoraLoader","inputs":{"lora_name":"B.safetensors","strength_model":0.6,"model":["4",0]}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}},
          "6": {"class_type":"CLIPTextEncode","inputs":{"text":"p"}},
          "7": {"class_type":"CLIPTextEncode","inputs":{"text":"n"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.Loras.Select(l => l.Name).Should().Equal("B", "A");
        r.Loras[1].StrengthModel.Should().Be(0.8);
        r.Loras[0].Source.Should().Be("LoraLoader");
        r.Checkpoint.Should().Be("base");
    }

    [Fact]
    public void PowerLoraLoader_skips_disabled_and_none()
    {
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{"model":["20",0]}},
          "20":{"class_type":"Power Lora Loader (rgthree)","inputs":{
                 "lora_1":{"on":true,"lora":"style.safetensors","strength":0.7},
                 "lora_2":{"on":false,"lora":"off.safetensors","strength":1.0},
                 "lora_3":{"on":true,"lora":"None","strength":1.0},
                 "model":["4",0]}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.Loras.Select(l => l.Name).Should().Equal("style");
        r.Loras[0].StrengthModel.Should().Be(0.7);
        r.Loras[0].Source.Should().Be("Power Lora");
    }

    [Fact]
    public void LoraLoaderStack_skips_none_and_zero_strength()
    {
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{"model":["30",0]}},
          "30":{"class_type":"Lora Loader Stack (rgthree)","inputs":{
                 "lora_01":"a.safetensors","strength_01":0.5,
                 "lora_02":"None","strength_02":1.0,
                 "lora_03":"b.safetensors","strength_03":0,
                 "model":["4",0]}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.Loras.Select(l => l.Name).Should().Equal("a");
        r.Loras[0].Source.Should().Be("Lora Stack");
    }

    [Fact]
    public void Mixed_power_lora_and_stock_loader_load_order()
    {
        // sampler <- PowerLora(A,B) <- LoraLoader(C) <- checkpoint  => [C, A, B]
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{"model":["20",0]}},
          "20":{"class_type":"Power Lora Loader (rgthree)","inputs":{
                 "lora_1":{"on":true,"lora":"A.safetensors","strength":0.5},
                 "lora_2":{"on":true,"lora":"B.safetensors","strength":0.6},
                 "model":["21",0]}},
          "21":{"class_type":"LoraLoader","inputs":{"lora_name":"C.safetensors","strength_model":0.7,"model":["4",0]}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.Loras.Select(l => l.Name).Should().Equal("C", "A", "B");
    }

    [Fact]
    public void KSamplerAdvanced_uses_noise_seed()
    {
        var g = Graph("""
        {
          "3": {"class_type":"KSamplerAdvanced","inputs":{"model":["4",0],"noise_seed":999,"steps":30,"cfg":5.0}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.Seed.Should().Be(999);
        r.Steps.Should().Be(30);
    }

    [Fact]
    public void Linked_text_resolves_through_primitive_string_node()
    {
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{"model":["4",0],"positive":["6",0]}},
          "6": {"class_type":"CLIPTextEncode","inputs":{"text":["8",0]}},
          "8": {"class_type":"PrimitiveNode","inputs":{"value":"resolved text"}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.PositivePrompt.Should().Be("resolved text");
    }

    [Fact]
    public void UNETLoader_diffusion_model_is_recognized()
    {
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{"model":["40",0]}},
          "40":{"class_type":"UNETLoader","inputs":{"unet_name":"flux-klein.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.Checkpoint.Should().Be("flux-klein");
    }

    [Fact]
    public void No_sampler_returns_no_data()
    {
        var g = Graph("""{ "1": {"class_type":"LoadImage","inputs":{"image":"x.png"}} }""");

        var r = ComfyUiPromptTracer.Trace(g, "x.png", 10, 10);

        r.HasData.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ComfyUiPromptTracerTests"`
Expected: FAIL to compile — `ComfyUiPromptTracer` does not exist.

- [ ] **Step 3: Implement the tracer**

Create `DiffusionNexus.UI/Services/Distiller/ComfyUiPromptTracer.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.Services.Distiller;

/// <summary>
/// Recovers generation parameters from a ComfyUI "prompt"-chunk graph (the resolved API prompt,
/// NOT the editor "workflow" graph). Faithful C# port of
/// ComfyUI-AI2Go-Utils/nodes/civitai_metadata/tracer.py; see comfyui-metadata-extraction-for-csharp.md.
/// Using the API prompt is what makes rgthree Power Lora / Lora Stack detection tractable.
/// </summary>
internal static class ComfyUiPromptTracer
{
    private static readonly HashSet<string> SamplerClasses = new(StringComparer.OrdinalIgnoreCase)
    { "KSampler", "KSamplerAdvanced", "SamplerCustom", "SamplerCustomAdvanced" };

    private static readonly HashSet<string> SaveClasses = new(StringComparer.OrdinalIgnoreCase)
    { "SaveImage", "PreviewImage", "SaveImageWebsocket" };

    // class_type -> (widget name holding the file, models subfolder)
    private static readonly Dictionary<string, (string Widget, string Folder)> ModelSources =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["CheckpointLoaderSimple"] = ("ckpt_name", "checkpoints"),
        ["CheckpointLoader"]       = ("ckpt_name", "checkpoints"),
        ["unCLIPCheckpointLoader"] = ("ckpt_name", "checkpoints"),
        ["UNETLoader"]             = ("unet_name", "diffusion_models"),
        ["UnetLoaderGGUF"]         = ("unet_name", "diffusion_models"),
        ["UnetLoaderGGUFAdvanced"] = ("unet_name", "diffusion_models"),
    };

    public static ImageGenerationData Trace(
        Dictionary<string, JsonElement> graph, string? fileName, int width, int height)
    {
        var startId = PickSamplerId(graph);
        if (startId is null || !TryNode(graph, startId, out _, out var s))
            return new ImageGenerationData { FileName = fileName, Width = width, Height = height, HasData = false };

        int? steps = ReadInt(s, "steps");
        double? cfg = ReadDouble(s, "cfg");
        long? seed = ReadLong(s, "seed") ?? ReadLong(s, "noise_seed");
        string? samplerName = ReadString(s, "sampler_name");
        string? scheduler = ReadString(s, "scheduler");
        double? denoise = ReadDouble(s, "denoise");

        string? positive = ResolvePrompt(graph, s, "positive");
        string? negative = ResolvePrompt(graph, s, "negative");

        var loras = new List<LoraInfo>();
        string? checkpoint = WalkModelChain(graph, s, loras);
        loras.Reverse(); // collected sampler-first; load order is checkpoint-first

        var hasData = positive is not null || negative is not null || checkpoint is not null
                      || samplerName is not null || loras.Count > 0;

        return new ImageGenerationData
        {
            FileName = fileName, Width = width, Height = height,
            PositivePrompt = positive, NegativePrompt = negative, Checkpoint = checkpoint,
            Loras = loras, SamplerName = samplerName, Scheduler = scheduler,
            Steps = steps, Seed = seed, Cfg = cfg, Denoise = denoise, HasData = hasData,
        };
    }

    // ── sampler selection (§3.1) ────────────────────────────────────────────────
    private static string? PickSamplerId(Dictionary<string, JsonElement> graph)
    {
        var samplerIds = graph.Where(kv => IsClass(kv.Value, SamplerClasses)).Select(kv => kv.Key).ToList();
        if (samplerIds.Count == 0) return null;

        // Prefer the sampler reachable from a save node's images input.
        foreach (var (_, node) in graph)
        {
            if (!IsClass(node, SaveClasses)) continue;
            if (!node.TryGetProperty("inputs", out var ins)) continue;
            if (!ins.TryGetProperty("images", out var imgs) || !IsLink(imgs)) continue;
            var found = BfsBackward(graph, OriginId(imgs), cls => SamplerClasses.Contains(cls));
            if (found is not null) return found;
        }

        // Single-sampler fallback; otherwise best-effort first sampler.
        return samplerIds[0];
    }

    // BFS over input links; checks the start node itself first.
    private static string? BfsBackward(Dictionary<string, JsonElement> graph, string startId, Func<string, bool> match)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(startId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            if (!TryNode(graph, id, out var cls, out var ins)) continue;
            if (match(cls)) return id;
            foreach (var prop in ins.EnumerateObject())
                if (IsLink(prop.Value)) queue.Enqueue(OriginId(prop.Value));
        }
        return null;
    }

    // ── prompts (§3.3) ──────────────────────────────────────────────────────────
    private static string? ResolvePrompt(Dictionary<string, JsonElement> graph, JsonElement sampler, string key)
    {
        if (!sampler.TryGetProperty(key, out var v) || !IsLink(v)) return null;

        var encId = BfsBackward(graph, OriginId(v),
            cls => cls.Contains("CLIPTextEncode", StringComparison.OrdinalIgnoreCase));
        if (encId is null || !TryNode(graph, encId, out _, out var enc)) return null;

        if (!enc.TryGetProperty("text", out var text)) return null;
        if (text.ValueKind == JsonValueKind.String) return text.GetString();

        if (IsLink(text) && TryNode(graph, OriginId(text), out _, out var src))
        {
            foreach (var prop in src.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
        }
        return null;
    }

    // ── model chain + LoRAs (§3.4–3.5) ──────────────────────────────────────────
    private static string? WalkModelChain(Dictionary<string, JsonElement> graph, JsonElement sampler, List<LoraInfo> loras)
    {
        if (!sampler.TryGetProperty("model", out var link) || !IsLink(link)) return null;

        string? current = OriginId(link);
        var seen = new HashSet<string>();
        while (current is not null && seen.Add(current))
        {
            if (!TryNode(graph, current, out var cls, out var ins)) break;

            if (cls.Equals("LoraLoader", StringComparison.OrdinalIgnoreCase) ||
                cls.Equals("LoraLoaderModelOnly", StringComparison.OrdinalIgnoreCase))
            {
                var name = ReadString(ins, "lora_name");
                if (name is { Length: > 0 } && name != "None")
                {
                    var str = ReadDouble(ins, "strength_model") ?? ReadDouble(ins, "strength") ?? 1.0;
                    loras.Add(new LoraInfo { Name = Stem(name), StrengthModel = str, StrengthClip = str, Source = "LoraLoader" });
                }
                current = NextModel(ins); continue;
            }

            if (cls.Equals("Power Lora Loader (rgthree)", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var e in Enumerable.Reverse(PowerLoraEntries(ins))) loras.Add(e);
                current = NextModel(ins); continue;
            }

            if (cls.Equals("Lora Loader Stack (rgthree)", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var e in Enumerable.Reverse(LoraStackEntries(ins))) loras.Add(e);
                current = NextModel(ins); continue;
            }

            if (ModelSources.TryGetValue(cls, out var srcDef))
            {
                var name = ReadString(ins, srcDef.Widget);
                return name is { Length: > 0 } ? Stem(name) : null;
            }

            current = NextModel(ins); // unknown pass-through node
        }
        return null;
    }

    private static string? NextModel(JsonElement ins) =>
        ins.TryGetProperty("model", out var m) && IsLink(m) ? OriginId(m) : null;

    private static List<LoraInfo> PowerLoraEntries(JsonElement ins)
    {
        var list = new List<LoraInfo>();
        foreach (var prop in ins.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            if (!prop.Name.StartsWith("lora_", StringComparison.OrdinalIgnoreCase)) continue;

            var obj = prop.Value;
            if (obj.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.False) continue;

            var name = obj.TryGetProperty("lora", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            if (string.IsNullOrEmpty(name) || name == "None") continue;

            double? str = obj.TryGetProperty("strength", out var srE) && srE.ValueKind == JsonValueKind.Number ? srE.GetDouble() : null;
            list.Add(new LoraInfo { Name = Stem(name), StrengthModel = str ?? 1.0, StrengthClip = str ?? 1.0, Source = "Power Lora" });
        }
        return list;
    }

    private static List<LoraInfo> LoraStackEntries(JsonElement ins)
    {
        var nums = new List<string>();
        foreach (var prop in ins.EnumerateObject())
            if (prop.Name.StartsWith("lora_", StringComparison.OrdinalIgnoreCase)
                && prop.Name.Length > 5 && prop.Name[5..].All(char.IsDigit))
                nums.Add(prop.Name[5..]);

        var list = new List<LoraInfo>();
        foreach (var num in nums.OrderBy(int.Parse))
        {
            var name = ReadString(ins, "lora_" + num);
            if (string.IsNullOrEmpty(name) || name == "None") continue;

            double? str = ReadDouble(ins, "strength_" + num);
            if (str is 0) continue; // zero strength: rgthree won't load it

            list.Add(new LoraInfo { Name = Stem(name), StrengthModel = str ?? 1.0, StrengthClip = str ?? 1.0, Source = "Lora Stack" });
        }
        return list;
    }

    // ── low-level helpers (§2) ──────────────────────────────────────────────────
    private static bool IsLink(JsonElement v) =>
        v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2 && v[1].ValueKind == JsonValueKind.Number;

    private static string OriginId(JsonElement link) => link[0].ToString();

    private static bool IsClass(JsonElement node, HashSet<string> set) =>
        node.TryGetProperty("class_type", out var c) && c.ValueKind == JsonValueKind.String && set.Contains(c.GetString()!);

    private static bool TryNode(Dictionary<string, JsonElement> graph, string id, out string classType, out JsonElement inputs)
    {
        classType = ""; inputs = default;
        if (!graph.TryGetValue(id, out var node)) return false;
        if (!node.TryGetProperty("class_type", out var c) || c.ValueKind != JsonValueKind.String) return false;
        classType = c.GetString() ?? "";
        return node.TryGetProperty("inputs", out inputs);
    }

    private static string? ReadString(JsonElement ins, string key) =>
        ins.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? ReadInt(JsonElement ins, string key) =>
        ins.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? ReadLong(JsonElement ins, string key) =>
        ins.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static double? ReadDouble(JsonElement ins, string key) =>
        ins.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static string Stem(string name)
    {
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) name = name[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ComfyUiPromptTracerTests"`
Expected: PASS (all 9 facts).

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/Distiller/ComfyUiPromptTracer.cs DiffusionNexus.Tests/Distiller/ComfyUiPromptTracerTests.cs
git commit -m "feat(distiller): ComfyUI prompt-chunk tracer with rgthree lora support"
```

---

### Task 4: Route `ImageMetadataParser` through the tracer

**Files:**
- Modify: `DiffusionNexus.UI/Services/ImageMetadataParser.cs`
- Test: (covered by Task 3 + a build check; no new test file)

**Interfaces:**
- Consumes: `ComfyUiPromptTracer.Trace` (Task 3).
- Produces: `ImageMetadataParser.Parse` now uses the robust tracer for ComfyUI images. Public signature unchanged.

- [ ] **Step 1: Replace the ComfyUI branch body**

In `DiffusionNexus.UI/Services/ImageMetadataParser.cs`, replace the entire `ParseComfyUiGraph` method body so it delegates to the tracer:

```csharp
    private static ImageGenerationData ParseComfyUiGraph(
        Dictionary<string, JsonElement> graph,
        string fileName,
        int width,
        int height)
        => DiffusionNexus.UI.Services.Distiller.ComfyUiPromptTracer.Trace(graph, fileName, width, height);
```

- [ ] **Step 2: Delete the now-dead ComfyUI helpers**

Delete these members from `ImageMetadataParser.cs` (they are only used by the old ComfyUI walk; the A1111 path does not touch them):
- fields `SamplerNodeTypes`, `CheckpointNodeTypes`, `LoraNodeTypes`, `MaxTraceDepth`
- methods `IsSamplerNode`, `ExtractSamplerData`, `TraceText`, `TraceModel`

Keep everything under the `#region A1111 / Forge parameter parsing` and the `LoraTagRegex`, `ParseA1111Parameters`, `ParseA1111SettingsLine`, `FindNextSettingSeparator`, `ExtractLoraTagsFromPrompt` members — the A1111 path still uses them.

- [ ] **Step 3: Build to verify no dead-code / unused-usings errors**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -warnaserror:CS0169,CS8321`
Expected: Build succeeded, 0 errors. (If `System.Text.Json` usings are now only used by the delegation line, they remain used — no removal needed.)

- [ ] **Step 4: Run the full tracer + existing suite to confirm no regressions**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~Distiller"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/ImageMetadataParser.cs
git commit -m "refactor(distiller): route ImageMetadataParser ComfyUI path through tracer"
```

---

### Task 5: `PromptRuleEngine` — delete/replace with protected LoRA tokens

**Files:**
- Create: `DiffusionNexus.UI/Services/Distiller/PromptRuleEngine.cs`
- Create: `DiffusionNexus.UI/Models/Distiller/PromptRuleSet.cs`
- Test: `DiffusionNexus.Tests/Distiller/PromptRuleEngineTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `enum RuleKind { Delete, Replace }`
  - `sealed class PromptRuleSet { string Name; RuleKind Kind; bool Enabled = true; IReadOnlyList<string> DeleteWords = []; IReadOnlyList<ReplacePair> ReplacePairs = []; }`
  - `readonly record struct ReplacePair(string From, string To)`
  - `static string PromptRuleEngine.Apply(string prompt, IReadOnlyList<PromptRuleSet> sets)` — applies enabled sets in order; `<lora:...>` tokens are removed before matching and re-appended after, so rules never mangle LoRA names. Whole-word, case-insensitive.

- [ ] **Step 1: Write the model**

Create `DiffusionNexus.UI/Models/Distiller/PromptRuleSet.cs`:

```csharp
using System.Collections.Generic;

namespace DiffusionNexus.UI.Models.Distiller;

/// <summary>Whether a rule set deletes words or replaces them.</summary>
public enum RuleKind { Delete, Replace }

/// <summary>A single word replacement (case-insensitive match on <see cref="From"/>).</summary>
public readonly record struct ReplacePair(string From, string To);

/// <summary>
/// A named, toggleable set of prompt-cleanup rules applied batch-wide by the Batch Metadata
/// Distiller. A set is EITHER a delete list OR a replace list, per <see cref="Kind"/>.
/// </summary>
public sealed class PromptRuleSet
{
    public string Name { get; set; } = "New rule set";
    public RuleKind Kind { get; set; } = RuleKind.Delete;
    public bool Enabled { get; set; } = true;

    /// <summary>Words removed when <see cref="Kind"/> is <see cref="RuleKind.Delete"/>.</summary>
    public IReadOnlyList<string> DeleteWords { get; set; } = [];

    /// <summary>Substitutions applied when <see cref="Kind"/> is <see cref="RuleKind.Replace"/>.</summary>
    public IReadOnlyList<ReplacePair> ReplacePairs { get; set; } = [];
}
```

- [ ] **Step 2: Write the failing tests**

Create `DiffusionNexus.Tests/Distiller/PromptRuleEngineTests.cs`:

```csharp
using System.Collections.Generic;
using DiffusionNexus.UI.Models.Distiller;
using DiffusionNexus.UI.Services.Distiller;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class PromptRuleEngineTests
{
    [Fact]
    public void Delete_removes_whole_words_case_insensitively_and_tidies_commas()
    {
        var set = new PromptRuleSet { Kind = RuleKind.Delete, DeleteWords = ["masterpiece", "4k"] };

        var result = PromptRuleEngine.Apply("Masterpiece, a cat, 4k, detailed", [set]);

        result.Should().Be("a cat, detailed");
    }

    [Fact]
    public void Delete_does_not_remove_substrings_inside_other_words()
    {
        var set = new PromptRuleSet { Kind = RuleKind.Delete, DeleteWords = ["art"] };

        var result = PromptRuleEngine.Apply("art, cartoon, artist", [set]);

        result.Should().Be("cartoon, artist");
    }

    [Fact]
    public void Replace_substitutes_pairs_case_insensitively()
    {
        var set = new PromptRuleSet { Kind = RuleKind.Replace, ReplacePairs = [new("1girl", "woman"), new("1boy", "man")] };

        var result = PromptRuleEngine.Apply("1girl and 1BOY", [set]);

        result.Should().Be("woman and man");
    }

    [Fact]
    public void Lora_tokens_survive_delete_and_replace_and_are_reappended()
    {
        var del = new PromptRuleSet { Kind = RuleKind.Delete, DeleteWords = ["style"] };
        var rep = new PromptRuleSet { Kind = RuleKind.Replace, ReplacePairs = [new("cat", "dog")] };

        var result = PromptRuleEngine.Apply("a cat, style <lora:styleB:0.8>", [del, rep]);

        result.Should().Be("a dog <lora:styleB:0.8>");
    }

    [Fact]
    public void Disabled_sets_are_ignored()
    {
        var set = new PromptRuleSet { Kind = RuleKind.Delete, Enabled = false, DeleteWords = ["cat"] };

        var result = PromptRuleEngine.Apply("a cat", [set]);

        result.Should().Be("a cat");
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~PromptRuleEngineTests"`
Expected: FAIL to compile — `PromptRuleEngine` does not exist.

- [ ] **Step 4: Implement the engine**

Create `DiffusionNexus.UI/Services/Distiller/PromptRuleEngine.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using DiffusionNexus.UI.Models.Distiller;

namespace DiffusionNexus.UI.Services.Distiller;

/// <summary>
/// Applies delete/replace <see cref="PromptRuleSet"/>s to a prompt. LoRA tokens (&lt;lora:...&gt;) are
/// extracted before rules run and re-appended after, so a blacklist can never corrupt a LoRA name.
/// Matching is whole-word and case-insensitive.
/// </summary>
internal static class PromptRuleEngine
{
    private static readonly Regex LoraToken = new(@"<lora:[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Apply(string prompt, IReadOnlyList<PromptRuleSet> sets)
    {
        if (string.IsNullOrEmpty(prompt) || sets is null || sets.Count == 0)
            return prompt ?? string.Empty;

        // 1. Pull LoRA tokens out so rules can't touch them.
        var tokens = new List<string>();
        var body = LoraToken.Replace(prompt, m => { tokens.Add(m.Value); return ""; });

        // 2. Apply enabled sets in order.
        foreach (var set in sets)
        {
            if (!set.Enabled) continue;
            body = set.Kind switch
            {
                RuleKind.Delete => ApplyDelete(body, set.DeleteWords),
                RuleKind.Replace => ApplyReplace(body, set.ReplacePairs),
                _ => body,
            };
        }

        // 3. Tidy separators, then re-append the tokens.
        body = Tidy(body.Replace("", " ").Trim());
        if (tokens.Count == 0) return body;

        var sb = new StringBuilder(body);
        foreach (var t in tokens)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
    }

    private static string ApplyDelete(string text, IReadOnlyList<string> words)
    {
        foreach (var w in words)
        {
            if (string.IsNullOrWhiteSpace(w)) continue;
            text = Regex.Replace(text, $@"\b{Regex.Escape(w.Trim())}\b", "", RegexOptions.IgnoreCase);
        }
        return text;
    }

    private static string ApplyReplace(string text, IReadOnlyList<ReplacePair> pairs)
    {
        foreach (var p in pairs)
        {
            if (string.IsNullOrWhiteSpace(p.From)) continue;
            text = Regex.Replace(text, $@"\b{Regex.Escape(p.From.Trim())}\b", p.To ?? "", RegexOptions.IgnoreCase);
        }
        return text;
    }

    // Collapse the whitespace/commas left behind by deletions: "a , , b" -> "a, b".
    private static string Tidy(string text)
    {
        text = Regex.Replace(text, @"\s+", " ");
        text = Regex.Replace(text, @"\s*,\s*", ", ");
        text = Regex.Replace(text, @"(,\s*){2,}", ", ");
        text = text.Trim().Trim(',').Trim();
        return text;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~PromptRuleEngineTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/Models/Distiller/PromptRuleSet.cs DiffusionNexus.UI/Services/Distiller/PromptRuleEngine.cs DiffusionNexus.Tests/Distiller/PromptRuleEngineTests.cs
git commit -m "feat(distiller): prompt rule engine with protected lora tokens"
```

---

### Task 6: `A1111MetadataFormatter` — build the CivitAI-readable parameters string

**Files:**
- Create: `DiffusionNexus.UI/Services/Distiller/A1111MetadataFormatter.cs`
- Test: `DiffusionNexus.Tests/Distiller/A1111MetadataFormatterTests.cs`

**Interfaces:**
- Consumes: `ImageGenerationData`, `LoraInfo`.
- Produces:
  - `readonly record struct ResourceHashes(string? ModelHash, IReadOnlyDictionary<string,string> LoraHashes)`
  - `static string A1111MetadataFormatter.Build(ImageGenerationData data, string positive, string? negative, IReadOnlyList<LoraInfo> loras, ResourceHashes? hashes)` — emits the A1111 `parameters` text: positive with `<lora:Name:Strength>` tokens appended, `Negative prompt:` line, and the settings line (Steps/Sampler/Schedule type/CFG scale/Seed/Size/Model, plus `Model hash:` and a `Hashes:` JSON block when hashes are supplied).

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/Distiller/A1111MetadataFormatterTests.cs`:

```csharp
using System.Collections.Generic;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services.Distiller;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class A1111MetadataFormatterTests
{
    private static ImageGenerationData Data() => new()
    {
        Width = 1024, Height = 1536, Checkpoint = "sd_xl_base",
        SamplerName = "dpmpp_2m", Scheduler = "karras", Steps = 28, Cfg = 4.5, Seed = 88213105, HasData = true
    };

    [Fact]
    public void Build_appends_lora_tokens_and_maps_sampler_name()
    {
        var loras = new List<LoraInfo> { new() { Name = "styleB", StrengthModel = 0.8 } };

        var s = A1111MetadataFormatter.Build(Data(), "cinematic portrait", "blurry", loras, hashes: null);

        s.Should().StartWith("cinematic portrait <lora:styleB:0.8>");
        s.Should().Contain("Negative prompt: blurry");
        s.Should().Contain("Sampler: DPM++ 2M Karras");
        s.Should().Contain("Steps: 28");
        s.Should().Contain("CFG scale: 4.5");
        s.Should().Contain("Size: 1024x1536");
        s.Should().Contain("Model: sd_xl_base");
        s.Should().NotContain("Hashes:");
        s.Should().NotContain("Model hash:");
    }

    [Fact]
    public void Build_emits_hashes_block_when_supplied()
    {
        var loras = new List<LoraInfo> { new() { Name = "styleB", StrengthModel = 0.8 } };
        var hashes = new ResourceHashes("abc1234567",
            new Dictionary<string, string> { ["styleB"] = "def8901234" });

        var s = A1111MetadataFormatter.Build(Data(), "p", null, loras, hashes);

        s.Should().Contain("Model hash: abc1234567");
        s.Should().Contain("Hashes: {");
        s.Should().Contain("\"model\":\"abc1234567\"");
        s.Should().Contain("\"lora:styleB\":\"def8901234\"");
    }

    [Fact]
    public void Build_omits_negative_line_when_negative_is_empty()
    {
        var s = A1111MetadataFormatter.Build(Data(), "p", null, [], hashes: null);

        s.Should().NotContain("Negative prompt:");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~A1111MetadataFormatterTests"`
Expected: FAIL to compile — `A1111MetadataFormatter` does not exist.

- [ ] **Step 3: Implement the formatter**

Create `DiffusionNexus.UI/Services/Distiller/A1111MetadataFormatter.cs`:

```csharp
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.Services.Distiller;

/// <summary>AutoV2 hashes for the checkpoint and LoRAs, keyed by LoRA stem.</summary>
public readonly record struct ResourceHashes(string? ModelHash, IReadOnlyDictionary<string, string> LoraHashes);

/// <summary>
/// Formats an <see cref="ImageGenerationData"/> trace as an Automatic1111 <c>parameters</c> string
/// (the format CivitAI's image parser reads). See comfyui-metadata-extraction-for-csharp.md §4.2.
/// </summary>
internal static class A1111MetadataFormatter
{
    // ComfyUI (sampler_name, scheduler) -> A1111 combined sampler name. Unmapped names pass through.
    private static readonly Dictionary<string, string> SamplerMap = new()
    {
        ["euler|normal"] = "Euler",
        ["euler|karras"] = "Euler Karras",
        ["euler_ancestral|normal"] = "Euler a",
        ["dpmpp_2m|normal"] = "DPM++ 2M",
        ["dpmpp_2m|karras"] = "DPM++ 2M Karras",
        ["dpmpp_2m_sde|karras"] = "DPM++ 2M SDE Karras",
        ["dpmpp_sde|karras"] = "DPM++ SDE Karras",
        ["ddim|normal"] = "DDIM",
    };

    public static string Build(
        ImageGenerationData data,
        string positive,
        string? negative,
        IReadOnlyList<LoraInfo> loras,
        ResourceHashes? hashes)
    {
        var sb = new StringBuilder();

        sb.Append(positive?.TrimEnd() ?? string.Empty);
        foreach (var lora in loras)
        {
            var strength = lora.StrengthModel.ToString("0.###", CultureInfo.InvariantCulture);
            sb.Append(" <lora:").Append(lora.Name).Append(':').Append(strength).Append('>');
        }

        if (!string.IsNullOrWhiteSpace(negative))
            sb.Append("\nNegative prompt: ").Append(negative.Trim());

        var settings = new List<string>();
        if (data.Steps is { } steps) settings.Add($"Steps: {steps}");
        settings.Add($"Sampler: {MapSampler(data.SamplerName, data.Scheduler)}");
        if (!string.IsNullOrWhiteSpace(data.Scheduler)) settings.Add($"Schedule type: {data.Scheduler}");
        if (data.Cfg is { } cfg) settings.Add($"CFG scale: {cfg.ToString("0.###", CultureInfo.InvariantCulture)}");
        if (data.Seed is { } seed) settings.Add($"Seed: {seed}");
        if (data.Width > 0 && data.Height > 0) settings.Add($"Size: {data.Width}x{data.Height}");
        if (!string.IsNullOrWhiteSpace(data.Checkpoint)) settings.Add($"Model: {data.Checkpoint}");

        if (hashes is { } h)
        {
            if (!string.IsNullOrEmpty(h.ModelHash)) settings.Add($"Model hash: {h.ModelHash}");
            settings.Add($"Hashes: {BuildHashesJson(h)}");
        }

        sb.Append('\n').Append(string.Join(", ", settings));
        return sb.ToString();
    }

    private static string MapSampler(string? samplerName, string? scheduler)
    {
        if (string.IsNullOrWhiteSpace(samplerName)) return "Euler";
        var key = $"{samplerName.ToLowerInvariant()}|{(scheduler ?? "normal").ToLowerInvariant()}";
        return SamplerMap.TryGetValue(key, out var mapped) ? mapped : samplerName;
    }

    private static string BuildHashesJson(ResourceHashes h)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(h.ModelHash)) parts.Add($"\"model\":\"{h.ModelHash}\"");
        foreach (var kv in h.LoraHashes.OrderBy(k => k.Key))
            parts.Add($"\"lora:{kv.Key}\":\"{kv.Value}\"");
        return "{" + string.Join(",", parts) + "}";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~A1111MetadataFormatterTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/Distiller/A1111MetadataFormatter.cs DiffusionNexus.Tests/Distiller/A1111MetadataFormatterTests.cs
git commit -m "feat(distiller): A1111 parameters formatter with lora tokens + hashes"
```

---

### Task 7: `PngMetadataWriter` — optional strip + zTXt removal

**Files:**
- Modify: `DiffusionNexus.UI/Services/PngMetadataWriter.cs`
- Test: `DiffusionNexus.Tests/Distiller/PngMetadataWriterStripTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: overload `static void PngMetadataWriter.CopyWithMetadata(string sourcePath, string destPath, Dictionary<string,string> metadata, bool stripExisting)`.
  - `stripExisting: true` — drop ALL existing `tEXt`/`iTXt`/`zTXt` chunks (incl. `prompt`/`workflow`), write the new metadata. (The existing 3-arg method keeps its behaviour and now forwards to this with `stripExisting: true`.)
  - `stripExisting: false` — keep existing chunks, but drop any existing chunk whose keyword collides with a key in `metadata` (so `parameters` is replaced, `prompt`/`workflow` are preserved).

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/Distiller/PngMetadataWriterStripTests.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class PngMetadataWriterStripTests
{
    private static string TempPng(Dictionary<string, string> chunks)
    {
        // Build a valid-enough PNG by writing chunks via the writer itself from a bare base.
        var basePath = Path.Combine(Path.GetTempPath(), $"base_{System.Guid.NewGuid():N}.png");
        File.WriteAllBytes(basePath, MinimalPng());
        var outPath = Path.Combine(Path.GetTempPath(), $"src_{System.Guid.NewGuid():N}.png");
        PngMetadataWriter.CopyWithMetadata(basePath, outPath, chunks); // seeds the chunks
        File.Delete(basePath);
        return outPath;
    }

    // 8-byte signature + IHDR(13) + IEND, real CRCs not required by the reader; writer needs a valid IHDR-first PNG.
    private static byte[] MinimalPng()
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(ms, "IHDR", new byte[13]);
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = System.BitConverter.GetBytes(data.Length);
        if (System.BitConverter.IsLittleEndian) System.Array.Reverse(len);
        s.Write(len);
        s.Write(System.Text.Encoding.ASCII.GetBytes(type));
        s.Write(data);
        s.Write(new byte[4]);
    }

    [Fact]
    public void Strip_true_removes_prompt_and_workflow_and_keeps_only_new_parameters()
    {
        var src = TempPng(new() { ["prompt"] = "{...}", ["workflow"] = "{...}" });
        var dst = Path.Combine(Path.GetTempPath(), $"dst_{System.Guid.NewGuid():N}.png");
        try
        {
            PngMetadataWriter.CopyWithMetadata(src, dst, new() { ["parameters"] = "clean" }, stripExisting: true);

            var chunks = PngChunkReader.ReadTextChunks(dst);
            chunks.Should().ContainKey("parameters");
            chunks["parameters"].Should().Be("clean");
            chunks.Should().NotContainKey("prompt");
            chunks.Should().NotContainKey("workflow");
        }
        finally { foreach (var p in new[] { src, dst }) if (File.Exists(p)) File.Delete(p); }
    }

    [Fact]
    public void Strip_false_keeps_prompt_and_workflow_but_replaces_parameters()
    {
        var src = TempPng(new() { ["prompt"] = "{keepme}", ["workflow"] = "{keepme}", ["parameters"] = "old" });
        var dst = Path.Combine(Path.GetTempPath(), $"dst_{System.Guid.NewGuid():N}.png");
        try
        {
            PngMetadataWriter.CopyWithMetadata(src, dst, new() { ["parameters"] = "new" }, stripExisting: false);

            var chunks = PngChunkReader.ReadTextChunks(dst);
            chunks["prompt"].Should().Be("{keepme}");
            chunks["workflow"].Should().Be("{keepme}");
            chunks["parameters"].Should().Be("new"); // replaced, not duplicated
        }
        finally { foreach (var p in new[] { src, dst }) if (File.Exists(p)) File.Delete(p); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~PngMetadataWriterStripTests"`
Expected: FAIL to compile — no 4-arg `CopyWithMetadata` overload.

- [ ] **Step 3: Implement the overload**

In `DiffusionNexus.UI/Services/PngMetadataWriter.cs`, change the existing 3-arg method to forward, and add the 4-arg overload. Replace the current method signature line:

```csharp
    public static void CopyWithMetadata(string sourcePath, string destPath, Dictionary<string, string> metadata)
```

with:

```csharp
    public static void CopyWithMetadata(string sourcePath, string destPath, Dictionary<string, string> metadata)
        => CopyWithMetadata(sourcePath, destPath, metadata, stripExisting: true);

    /// <summary>
    /// Copies a PNG, inserting <paramref name="metadata"/> as tEXt chunks. When
    /// <paramref name="stripExisting"/> is true, ALL existing tEXt/iTXt/zTXt chunks are dropped
    /// (removing the embedded ComfyUI prompt/workflow). When false, existing chunks are preserved
    /// except any whose keyword collides with a key in <paramref name="metadata"/> (so a
    /// "parameters" chunk is replaced rather than duplicated). Non-PNG files are copied verbatim.
    /// </summary>
    public static void CopyWithMetadata(string sourcePath, string destPath, Dictionary<string, string> metadata, bool stripExisting)
```

Then, inside the method body, update the chunk-copy loop's skip condition. Replace this block:

```csharp
            if (type is "tEXt" or "iTXt")
            {
                // Skip old metadata chunk (data + CRC)
                if (input.Position + length + 4 > input.Length) break;
                input.Seek(length + 4, SeekOrigin.Current);
                continue;
            }
```

with:

```csharp
            if (type is "tEXt" or "iTXt" or "zTXt")
            {
                bool drop = stripExisting;
                if (!drop)
                {
                    // Keep the chunk unless its keyword collides with a new key we're writing.
                    var data = reader.ReadBytes(length);
                    var keyword = ReadChunkKeyword(type, data);
                    if (keyword is not null && metadata.ContainsKey(keyword))
                    {
                        input.Seek(4, SeekOrigin.Current); // skip CRC of the dropped chunk
                        continue;
                    }
                    // Not colliding — re-emit the chunk verbatim.
                    writer.Write(lengthBytes);
                    writer.Write(typeBytes);
                    writer.Write(data);
                    writer.Write(reader.ReadBytes(4)); // CRC
                    continue;
                }

                if (input.Position + length + 4 > input.Length) break;
                input.Seek(length + 4, SeekOrigin.Current);
                continue;
            }
```

Add this private helper at the end of the class (before the final closing brace):

```csharp
    /// <summary>Reads the keyword (text up to the first NUL) from a tEXt/iTXt/zTXt chunk's data.</summary>
    private static string? ReadChunkKeyword(string type, byte[] data)
    {
        int nul = Array.IndexOf(data, (byte)0);
        if (nul < 0) return null;
        var enc = type == "iTXt" ? Encoding.UTF8 : Encoding.Latin1;
        return enc.GetString(data, 0, nul);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~PngMetadataWriterStripTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/PngMetadataWriter.cs DiffusionNexus.Tests/Distiller/PngMetadataWriterStripTests.cs
git commit -m "feat(distiller): optional strip flag + zTXt removal in PngMetadataWriter"
```

---

### Task 8: `ImageResourceHasher` — AutoV2 hashes for LoRAs & checkpoints

**Files:**
- Create: `DiffusionNexus.UI/Services/Distiller/ImageResourceHasher.cs`
- Test: `DiffusionNexus.Tests/Distiller/ImageResourceHasherTests.cs`

**Interfaces:**
- Consumes: `DiffusionNexus.UI.Services.Lora.ILoraCatalog` (`GetInstalledLorasAsync(baseModelFilter, ct) → IReadOnlyList<AvailableLora>`, where `AvailableLora.FilePath` is the on-disk path); `DiffusionNexus.UI.Models.LoraInfo`.
- Produces:
  - `sealed class ImageResourceHasher(ILoraCatalog loraCatalog, Func<CancellationToken, Task<string?>> resolveModelsRoot)`
  - `Task<ResourceHashes> ComputeAsync(string? checkpointStem, IReadOnlyList<LoraInfo> loras, CancellationToken ct)` — resolves each name against the local catalogs, returns AutoV2 (first 10 lowercase hex of SHA-256) for those found; unresolved names are omitted.
  - `static string? ComputeAutoV2(string filePath)` — testable file hash helper.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/Distiller/ImageResourceHasherTests.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services.Distiller;
using DiffusionNexus.UI.Services.Lora;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Distiller;

public class ImageResourceHasherTests
{
    [Fact]
    public void ComputeAutoV2_is_first_10_hex_of_sha256()
    {
        var path = Path.Combine(Path.GetTempPath(), $"h_{System.Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllText(path, "content");
            var expected = System.Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                .ToLowerInvariant()[..10];

            ImageResourceHasher.ComputeAutoV2(path).Should().Be(expected);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ComputeAsync_hashes_lora_found_in_catalog()
    {
        // The file's STEM must equal the trace's LoRA name ("styleB"); use a unique dir, real name.
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"lora_{System.Guid.NewGuid():N}")).FullName;
        var loraFile = Path.Combine(dir, "styleB.safetensors");
        File.WriteAllText(loraFile, "weights");
        try
        {
            var catalog = new Mock<ILoraCatalog>();
            catalog.Setup(c => c.GetInstalledLorasAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<AvailableLora> { new("styleB", loraFile, null, null) });

            var hasher = new ImageResourceHasher(catalog.Object, _ => Task.FromResult<string?>(null));

            var loras = new List<LoraInfo> { new() { Name = "styleB" } };
            var result = await hasher.ComputeAsync(checkpointStem: null, loras, CancellationToken.None);

            result.LoraHashes.Should().ContainKey("styleB");
            result.LoraHashes["styleB"].Should().Be(ImageResourceHasher.ComputeAutoV2(loraFile));
            result.ModelHash.Should().BeNull();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ImageResourceHasherTests"`
Expected: FAIL to compile — `ImageResourceHasher` does not exist.

- [ ] **Step 3: Implement the hasher**

Create `DiffusionNexus.UI/Services/Distiller/ImageResourceHasher.cs`:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services.Lora;

namespace DiffusionNexus.UI.Services.Distiller;

/// <summary>
/// Computes AutoV2 (first 10 hex of SHA-256) hashes for a trace's checkpoint and LoRAs by resolving
/// their stems against the local catalogs. Names not found locally are omitted (name-only fallback).
/// Hashes are cached per (path,size,mtime) so a batch hashes each file once.
/// </summary>
internal sealed class ImageResourceHasher
{
    private static readonly string[] ModelExtensions = [".safetensors", ".ckpt", ".gguf", ".pt", ".sft", ".bin"];
    private static readonly string[] CheckpointSubfolders = ["checkpoints", "diffusion_models", "unet"];

    private readonly ILoraCatalog _loraCatalog;
    private readonly Func<CancellationToken, Task<string?>> _resolveModelsRoot;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageResourceHasher(ILoraCatalog loraCatalog, Func<CancellationToken, Task<string?>> resolveModelsRoot)
    {
        _loraCatalog = loraCatalog;
        _resolveModelsRoot = resolveModelsRoot;
    }

    public async Task<ResourceHashes> ComputeAsync(string? checkpointStem, IReadOnlyList<LoraInfo> loras, CancellationToken ct)
    {
        var installed = await _loraCatalog.GetInstalledLorasAsync(null, ct).ConfigureAwait(false);
        var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in installed)
        {
            var stem = Path.GetFileNameWithoutExtension(l.FilePath);
            byStem.TryAdd(stem, l.FilePath);
        }

        var loraHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lora in loras)
        {
            ct.ThrowIfCancellationRequested();
            if (byStem.TryGetValue(lora.Name, out var path) && HashCached(path) is { } h)
                loraHashes[lora.Name] = h;
        }

        string? modelHash = null;
        if (!string.IsNullOrWhiteSpace(checkpointStem))
        {
            var root = await _resolveModelsRoot(ct).ConfigureAwait(false);
            var file = FindModelFile(root, checkpointStem);
            if (file is not null) modelHash = HashCached(file);
        }

        return new ResourceHashes(modelHash, loraHashes);
    }

    private string? HashCached(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);
            var key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            return _cache.GetOrAdd(key, _ => ComputeAutoV2(filePath));
        }
        catch { return null; }
    }

    private static string? FindModelFile(string? modelsRoot, string stem)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot) || !Directory.Exists(modelsRoot)) return null;
        foreach (var sub in CheckpointSubfolders)
        {
            var dir = Path.Combine(modelsRoot, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (!ModelExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                if (Path.GetFileNameWithoutExtension(file).Equals(stem, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }
        return null;
    }

    /// <summary>AutoV2 = first 10 lowercase hex chars of the file's full SHA-256. Null on I/O error.</summary>
    public static string? ComputeAutoV2(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant()[..10];
        }
        catch { return null; }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ImageResourceHasherTests"`
Expected: PASS.

- [ ] **Step 5: Run the entire Distiller suite + build the UI project**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj` then `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~Distiller"`
Expected: Build succeeded; all Distiller tests PASS.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/Services/Distiller/ImageResourceHasher.cs DiffusionNexus.Tests/Distiller/ImageResourceHasherTests.cs
git commit -m "feat(distiller): AutoV2 resource hasher for loras and checkpoints"
```

---

### Task 9: Read AI2Go / A1111 output cleanly (normalize embedded LoRA tokens)

**Files:**
- Modify: `DiffusionNexus.UI/Services/ImageMetadataParser.cs`
- Test: `DiffusionNexus.Tests/Distiller/A1111LoraNormalizationTests.cs`

**Why:** The AI2Go "Save Metadata (Civitai)" ComfyUI node writes a standard A1111 `parameters`
tEXt chunk with LoRAs embedded in the prompt as `<lora:name:strength>` tokens (and a
`Lora hashes: "..."` field + `Version: ComfyUI`), stripping `prompt`/`workflow` by default. The
existing A1111 path reads these, but `ExtractLoraTagsFromPrompt` collects the tokens into the LoRA
list **without removing them from the stored prompt**. That means an A1111/AI2Go-sourced image's
`PositivePrompt` still contains `<lora:...>`, so when the distiller re-appends LoRA tokens on output
it **duplicates** them. This task normalizes the A1111 read path so `PositivePrompt`/`NegativePrompt`
never contain LoRA tokens (matching the ComfyUI trace path, where prompts are already clean).

**Interfaces:**
- Consumes: `A1111MetadataFormatter.Build` (Task 6) for the round-trip test; `PngMetadataWriter`,
  `PngChunkReader`, `ImageMetadataParser`, `ImageGenerationData`, `LoraInfo`.
- Produces: A1111-parsed `ImageGenerationData` whose prompts are LoRA-token-free; LoRAs remain in
  `Loras`. Public `Parse` signature unchanged.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/Distiller/A1111LoraNormalizationTests.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Distiller;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class A1111LoraNormalizationTests
{
    // The exact golden string the AI2Go "Save Metadata (Civitai)" node writes (tests/test_a1111.py).
    private const string AI2GoGolden =
        "a red fox, 8k <lora:styleLora:0.8>\n" +
        "Negative prompt: blurry\n" +
        "Steps: 30, Sampler: DPM++ 2M Karras, CFG scale: 6.5, Seed: 12345, Size: 1024x1024, " +
        "Model hash: a1b2c3d4e5, Model: myCkpt, Lora hashes: \"styleLora: 1122aabbcc\", Version: ComfyUI";

    private static string WritePngWithParameters(string parameters)
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"seed_{System.Guid.NewGuid():N}.png");
        File.WriteAllBytes(basePath, MinimalPng());
        var outPath = Path.Combine(Path.GetTempPath(), $"a1111_{System.Guid.NewGuid():N}.png");
        PngMetadataWriter.CopyWithMetadata(basePath, outPath, new() { ["parameters"] = parameters });
        File.Delete(basePath);
        return outPath;
    }

    private static byte[] MinimalPng()
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(ms, "IHDR", new byte[13]);
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = System.BitConverter.GetBytes(data.Length);
        if (System.BitConverter.IsLittleEndian) System.Array.Reverse(len);
        s.Write(len); s.Write(System.Text.Encoding.ASCII.GetBytes(type)); s.Write(data); s.Write(new byte[4]);
    }

    [Fact]
    public void Parses_ai2go_golden_with_clean_prompt_and_extracted_lora()
    {
        var path = WritePngWithParameters(AI2GoGolden);
        try
        {
            var data = new ImageMetadataParser().Parse(path);

            data.HasData.Should().BeTrue();
            data.PositivePrompt.Should().Be("a red fox, 8k");     // <lora:...> removed
            data.PositivePrompt.Should().NotContain("<lora:");
            data.NegativePrompt.Should().Be("blurry");
            data.Loras.Select(l => (l.Name, l.StrengthModel)).Should().Equal(("styleLora", 0.8));
            data.Steps.Should().Be(30);
            data.Cfg.Should().Be(6.5);
            data.Seed.Should().Be(12345);
            data.SamplerName.Should().Be("DPM++ 2M Karras");
            data.Checkpoint.Should().Be("myCkpt");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Round_trip_through_formatter_does_not_duplicate_lora_tokens()
    {
        var path = WritePngWithParameters(AI2GoGolden);
        try
        {
            var data = new ImageMetadataParser().Parse(path);

            var reformatted = A1111MetadataFormatter.Build(
                data, data.PositivePrompt ?? "", data.NegativePrompt, data.Loras, hashes: null);

            // Exactly one <lora:styleLora:...> token, not two.
            System.Text.RegularExpressions.Regex.Matches(reformatted, @"<lora:styleLora:").Count.Should().Be(1);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~A1111LoraNormalizationTests"`
Expected: FAIL — the first test's `PositivePrompt` still contains the `<lora:styleLora:0.8>` token; the round-trip test finds two tokens.

- [ ] **Step 3: Strip LoRA tokens from the A1111 prompt after extraction**

In `DiffusionNexus.UI/Services/ImageMetadataParser.cs`, change `ExtractLoraTagsFromPrompt` to RETURN the prompt with the tokens removed and whitespace tidied, and update its two call sites in `ParseA1111Parameters`.

Change the two call sites from:

```csharp
        // Extract LoRA tags from prompts before returning them
        var loras = new List<LoraInfo>();
        if (positivePrompt is not null)
        {
            ExtractLoraTagsFromPrompt(positivePrompt, loras);
        }

        if (negativePrompt is not null)
        {
            ExtractLoraTagsFromPrompt(negativePrompt, loras);
        }
```

to:

```csharp
        // Extract LoRA tags into the list AND strip them from the stored prompt text, so a
        // re-formatted A1111/AI2Go image does not double-append LoRA tokens (matches the ComfyUI
        // trace path, whose prompts are already token-free).
        var loras = new List<LoraInfo>();
        if (positivePrompt is not null)
        {
            positivePrompt = ExtractLoraTagsFromPrompt(positivePrompt, loras);
        }

        if (negativePrompt is not null)
        {
            negativePrompt = ExtractLoraTagsFromPrompt(negativePrompt, loras);
        }
```

Then change the `ExtractLoraTagsFromPrompt` method signature and body from:

```csharp
    private static void ExtractLoraTagsFromPrompt(string prompt, List<LoraInfo> loras)
    {
        foreach (var match in LoraTagRegex().EnumerateMatches(prompt))
        {
```

to return the cleaned prompt (keep the existing tag-collection loop unchanged; only change the signature line and add the strip + tidy + return at the end):

```csharp
    private static string ExtractLoraTagsFromPrompt(string prompt, List<LoraInfo> loras)
    {
        foreach (var match in LoraTagRegex().EnumerateMatches(prompt))
        {
```

and at the very end of the method (replace its closing brace with the strip/tidy/return):

```csharp
        // Remove the tokens from the prompt text and tidy the separators/whitespace left behind.
        var cleaned = LoraTagRegex().Replace(prompt, "");
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        cleaned = Regex.Replace(cleaned, @"\s*,\s*", ", ");
        cleaned = Regex.Replace(cleaned, @"(,\s*){2,}", ", ");
        return cleaned.Trim().Trim(',').Trim();
    }
```

(`Regex` is already imported — the file uses `System.Text.RegularExpressions`.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~A1111LoraNormalizationTests"`
Expected: PASS (both facts).

- [ ] **Step 5: Guard against regressions in the existing A1111 path**

Run the whole Distiller test group to confirm nothing else broke:
Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~Distiller"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/Services/ImageMetadataParser.cs DiffusionNexus.Tests/Distiller/A1111LoraNormalizationTests.cs
git commit -m "feat(distiller): read AI2Go/A1111 output cleanly (strip embedded lora tokens)"
```

---

## Part 1 done — engine complete

At this point the entire metadata engine is implemented and unit-tested with no UI. Part 2 wires it into a gallery tile and run screen. The public surface Part 2 relies on:

- `ComfyUiPromptTracer.Trace(graph, fileName, w, h) → ImageGenerationData`
- `PromptRuleEngine.Apply(prompt, IReadOnlyList<PromptRuleSet>) → string`
- `A1111MetadataFormatter.Build(data, positive, negative, loras, ResourceHashes?) → string`
- `ImageResourceHasher.ComputeAsync(checkpointStem, loras, ct) → ResourceHashes`
- `PngMetadataWriter.CopyWithMetadata(src, dst, metadata, bool stripExisting)`
- Models: `PromptRuleSet`, `RuleKind`, `ReplacePair`, `ResourceHashes`, `LoraInfo.Source`
