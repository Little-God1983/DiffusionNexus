using System.Text;
using Serilog;

namespace DiffusionNexus.Inference.StableDiffusionCpp;

/// <summary>
/// Repairs a known defect in some stale unsloth Qwen-Image / Qwen-Image-Edit GGUF uploads: a
/// zero-element sentinel tensor (e.g. <c>__index_timestep_zero__</c>) written with a data-offset of
/// <c>0</c>. ggml's <c>gguf_init_from_file</c> validates tensor offsets while parsing and rejects the
/// offset-0 tensor ("tensor '…' has offset 0, expected N") <b>before</b> stable-diffusion.cpp gets a
/// chance to skip it via its ignore-list — so the file fails to load and upgrading the engine doesn't
/// help. unsloth re-uploaded corrected files that set the sentinel's offset to the true end of the
/// tensor-data section; this class applies the same 8-byte fix in place when (and only when) the exact
/// broken condition is detected, so an already-downloaded stale file becomes loadable without a
/// 20 GB re-download.
/// </summary>
internal static class GgufSentinelFixer
{
    private static readonly ILogger Logger = Log.ForContext(typeof(GgufSentinelFixer));

    private const uint GgufMagic = 0x46554747; // "GGUF" little-endian
    private const int DefaultAlignment = 32;

    /// <summary>
    /// Inspects <paramref name="ggufPath"/> and, if it carries a stale zero-offset sentinel tensor,
    /// patches that tensor's offset to the correct end-of-data value. No-op for non-GGUF files,
    /// already-correct files, or anything that doesn't match the precise broken signature. Never
    /// throws — any failure is logged and the original file is left untouched (the native loader will
    /// then surface its own error).
    /// </summary>
    public static void EnsureLoadable(string? ggufPath)
    {
        if (string.IsNullOrWhiteSpace(ggufPath)
            || !ggufPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(ggufPath))
        {
            return;
        }

        try
        {
            // Read-write but allow other readers; we only write 8 bytes far from any concurrent read region.
            using var fs = new FileStream(ggufPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            if (br.ReadUInt32() != GgufMagic)
                return;
            var version = br.ReadUInt32();
            if (version != 3)
                return; // only v3 is handled; other versions are left to the native loader

            var tensorCount = br.ReadUInt64();
            var kvCount = br.ReadUInt64();
            if (tensorCount == 0 || tensorCount > 1_000_000)
                return;

            var alignment = ReadAlignment(br, kvCount);
            if (alignment <= 0)
                alignment = DefaultAlignment;

            // Walk the tensor-info table, recording every zero-size sentinel that declares offset 0.
            var broken = new List<long>(); // byte positions of the offset fields to patch
            for (ulong i = 0; i < tensorCount; i++)
            {
                var name = ReadGgufString(br);
                var nDims = br.ReadUInt32();
                ulong elements = nDims == 0 ? 0UL : 1UL;
                for (uint d = 0; d < nDims; d++)
                    elements *= br.ReadUInt64();
                _ = br.ReadUInt32(); // ggml type
                var offsetFieldPos = fs.Position;
                var offset = br.ReadUInt64();

                if (offset == 0 && elements == 0 && IsSentinelName(name))
                    broken.Add(offsetFieldPos);
            }

            if (broken.Count == 0)
                return;

            // Data section begins at the next alignment boundary after the tensor-info table; the
            // sentinel's correct offset is the size of that data section (it sits at the very end).
            var dataStart = AlignUp(fs.Position, alignment);
            var correctOffset = fs.Length - dataStart;
            if (correctOffset <= 0)
                return; // sanity guard — never write a bogus value

            foreach (var pos in broken)
            {
                fs.Position = pos;
                fs.Write(BitConverter.GetBytes((ulong)correctOffset), 0, 8);
            }
            fs.Flush();

            Logger.Warning(
                "Repaired stale GGUF sentinel offset(s) in {Path}: set {Count} zero-offset sentinel tensor(s) to {Offset} " +
                "(known broken unsloth Qwen-Image upload; now loadable).",
                ggufPath, broken.Count, correctOffset);
        }
        catch (Exception ex)
        {
            // Best-effort repair: if anything goes wrong, leave the file as-is and let the native
            // loader report the original error.
            Logger.Warning(ex, "GGUF sentinel repair skipped for {Path} ({Reason}).", ggufPath, ex.Message);
        }
    }

    private static bool IsSentinelName(string name) =>
        name.StartsWith("__index", StringComparison.Ordinal);

    private static long AlignUp(long value, int alignment) =>
        (value + alignment - 1) / alignment * alignment;

    /// <summary>Reads the metadata KVs, returning <c>general.alignment</c> if present (else 0).</summary>
    private static int ReadAlignment(BinaryReader br, ulong kvCount)
    {
        var alignment = 0;
        for (ulong i = 0; i < kvCount; i++)
        {
            var key = ReadGgufString(br);
            var type = br.ReadUInt32();
            if (key == "general.alignment" && type == 4) // UINT32
            {
                alignment = (int)br.ReadUInt32();
            }
            else
            {
                SkipValue(br, type);
            }
        }
        return alignment;
    }

    private static string ReadGgufString(BinaryReader br)
    {
        var len = br.ReadUInt64();
        var bytes = br.ReadBytes((int)len);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void SkipValue(BinaryReader br, uint type)
    {
        switch (type)
        {
            case 0: case 1: case 7: br.ReadByte(); break;        // (u)int8 / bool
            case 2: case 3: br.ReadBytes(2); break;              // (u)int16
            case 4: case 5: case 6: br.ReadBytes(4); break;      // (u)int32 / float32
            case 10: case 11: case 12: br.ReadBytes(8); break;   // (u)int64 / float64
            case 8:                                              // string
                br.ReadBytes((int)br.ReadUInt64());
                break;
            case 9:                                              // array
                var elemType = br.ReadUInt32();
                var count = br.ReadUInt64();
                for (ulong i = 0; i < count; i++)
                    SkipValue(br, elemType);
                break;
            default:
                throw new InvalidDataException($"Unknown GGUF value type {type}.");
        }
    }
}
