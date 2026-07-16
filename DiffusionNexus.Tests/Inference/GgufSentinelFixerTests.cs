using System.Reflection;
using System.Text;
using DiffusionNexus.Inference.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.Inference;

/// <summary>
/// Tests the in-place repair of stale unsloth Qwen-Image GGUFs whose zero-element sentinel tensor
/// (<c>__index_timestep_zero__</c>) was written with a data-offset of 0. Builds tiny synthetic GGUF v3
/// files so the binary parsing/patching logic is exercised without a multi-GB model.
/// </summary>
public class GgufSentinelFixerTests : IDisposable
{
    private static readonly MethodInfo EnsureLoadable =
        typeof(ModelDescriptor).Assembly
            .GetType("DiffusionNexus.Inference.StableDiffusionCpp.GgufSentinelFixer", throwOnError: true)!
            .GetMethod("EnsureLoadable", BindingFlags.Public | BindingFlags.Static)!;

    private readonly string _path = Path.Combine(Path.GetTempPath(), "dn-gguf-" + Guid.NewGuid().ToString("N") + ".gguf");

    public void Dispose() { try { File.Delete(_path); } catch { } }

    private static void WriteStr(BinaryWriter bw, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        bw.Write((ulong)bytes.Length);
        bw.Write(bytes);
    }

    /// <summary>Writes a 2-tensor GGUF v3: one real F32 tensor "w" and a zero-element "__index_timestep_zero__"
    /// sentinel whose declared offset is <paramref name="sentinelOffset"/>. The data section is one
    /// alignment block (32 bytes) so the correct sentinel offset is exactly 32.</summary>
    private void WriteGguf(ulong sentinelOffset)
    {
        const int alignment = 32;
        using var fs = new FileStream(_path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write(0x46554747u);  // "GGUF"
        bw.Write(3u);           // version
        bw.Write(2UL);          // tensor count
        bw.Write(1UL);          // kv count

        // general.alignment = 32 (UINT32 = type 4)
        WriteStr(bw, "general.alignment");
        bw.Write(4u);
        bw.Write((uint)alignment);

        // real tensor "w": 1 dim [4], F32 (type 0), offset 0
        WriteStr(bw, "w");
        bw.Write(1u);           // n_dims
        bw.Write(4UL);          // dims[0]
        bw.Write(0u);           // type F32
        bw.Write(0UL);          // offset

        // sentinel: 1 dim [0] (zero elements), F32, offset = sentinelOffset
        WriteStr(bw, "__index_timestep_zero__");
        bw.Write(1u);           // n_dims
        bw.Write(0UL);          // dims[0] = 0  -> zero elements
        bw.Write(0u);           // type F32
        bw.Write(sentinelOffset);

        // pad to alignment, then one 32-byte data block (16 bytes of "w" + 16 pad)
        var pad = (int)(((fs.Position + alignment - 1) / alignment * alignment) - fs.Position);
        bw.Write(new byte[pad]);
        bw.Write(new byte[alignment]);
    }

    private (ulong wOffset, ulong sentinelOffset) ReadOffsets()
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);
        br.ReadUInt32(); br.ReadUInt32();              // magic, version
        var tc = br.ReadUInt64(); var kv = br.ReadUInt64();
        for (ulong i = 0; i < kv; i++) { var n = br.ReadUInt64(); br.ReadBytes((int)n); br.ReadUInt32(); br.ReadUInt32(); } // alignment kv (uint32)
        ulong w = 0, s = 0;
        for (ulong i = 0; i < tc; i++)
        {
            var n = br.ReadUInt64(); var name = Encoding.UTF8.GetString(br.ReadBytes((int)n));
            var nd = br.ReadUInt32(); for (uint d = 0; d < nd; d++) br.ReadUInt64();
            br.ReadUInt32(); var off = br.ReadUInt64();
            if (name == "w") w = off; else s = off;
        }
        return (w, s);
    }

    [Fact]
    public void EnsureLoadable_BrokenZeroOffsetSentinel_PatchedToDataEnd()
    {
        WriteGguf(sentinelOffset: 0);   // broken

        EnsureLoadable.Invoke(null, new object[] { _path });

        var (w, sentinel) = ReadOffsets();
        w.Should().Be(0UL, "the real tensor's offset must never be touched");
        sentinel.Should().Be(32UL, "the zero-size sentinel must be moved to the end of the 32-byte data section");
    }

    [Fact]
    public void EnsureLoadable_AlreadyValidSentinel_LeftUnchanged()
    {
        WriteGguf(sentinelOffset: 32);  // already correct

        EnsureLoadable.Invoke(null, new object[] { _path });

        var (_, sentinel) = ReadOffsets();
        sentinel.Should().Be(32UL);
    }

    [Fact]
    public void EnsureLoadable_NonGgufFile_NoThrow()
    {
        File.WriteAllText(_path, "not a gguf");
        var act = () => EnsureLoadable.Invoke(null, new object[] { _path });
        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureLoadable_MissingFile_NoThrow()
    {
        var act = () => EnsureLoadable.Invoke(null, new object?[] { "Z:\\does\\not\\exist.gguf" });
        act.Should().NotThrow();
    }
}
