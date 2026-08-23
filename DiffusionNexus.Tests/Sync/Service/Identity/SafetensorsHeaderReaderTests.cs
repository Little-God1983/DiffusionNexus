using System.Buffers.Binary;
using System.Text;
using DiffusionNexus.Service.Services.Sync.Identity;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service.Identity;

/// <summary>
/// Covers <see cref="SafetensorsHeaderReader"/> — the exact-path, never-throws safetensors
/// JSON-header probe later tasks use to identify a model's base model from the file itself.
/// </summary>
public class SafetensorsHeaderReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dn-shr-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] Safetensors(string headerJson, int trailingTensorBytes = 16)
    {
        var json = Encoding.UTF8.GetBytes(headerJson);
        var buffer = new byte[8 + json.Length + trailingTensorBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)json.Length);
        json.CopyTo(buffer, 8);
        return buffer;   // trailing zeros stand in for tensor data
    }

    // A raw-string interpolation of this shape (adjacent literal brace immediately touching the
    // hole delimiter, twice) cannot be made to compile at any $ count: the literal `{`/`}` next
    // to the hole always merges into one run that either collides with the delimiter count or
    // exceeds it (CS9007). Plain concatenation produces byte-identical JSON without the ambiguity.
    private static string Meta(params (string Key, string Value)[] pairs) =>
        "{\"__metadata__\":{" + string.Join(",", pairs.Select(p => $"\"{p.Key}\":\"{p.Value}\"")) +
        "},\"tensor.weight\":{\"dtype\":\"F16\",\"shape\":[4],\"data_offsets\":[0,8]}}";

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void TryRead_ExtractsTheThreeMetadataKeys()
    {
        var bytes = Safetensors(Meta(
            ("ss_base_model_version", "sdxl_base_v1-0"),
            ("modelspec.architecture", "stable-diffusion-xl-v1-base/lora"),
            ("ss_sd_model_name", "ponyDiffusionV6XL")));
        var path = WriteFile("model.safetensors", bytes);

        var info = SafetensorsHeaderReader.TryRead(path);

        info.Should().NotBeNull();
        info!.BaseModelVersion.Should().Be("sdxl_base_v1-0");
        info.Architecture.Should().Be("stable-diffusion-xl-v1-base/lora");
        info.ModelNameHint.Should().Be("ponyDiffusionV6XL");
    }

    [Fact]
    public void TryRead_HeaderWithoutMetadataBlockIsASuccessfulEmptyRead()
    {
        var bytes = Safetensors("""{"tensor.weight":{"dtype":"F16","shape":[4],"data_offsets":[0,8]}}""");
        var path = WriteFile("nometa.safetensors", bytes);

        var info = SafetensorsHeaderReader.TryRead(path);

        info.Should().NotBeNull();
        info!.BaseModelVersion.Should().BeNull();
        info.Architecture.Should().BeNull();
        info.ModelNameHint.Should().BeNull();
    }

    [Fact]
    public void TryRead_TruncatedFileReturnsNull()
    {
        var buffer = new byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, 500UL);
        var path = WriteFile("truncated.safetensors", buffer);

        SafetensorsHeaderReader.TryRead(path).Should().BeNull();
    }

    [Fact]
    public void TryRead_OversizedHeaderReturnsNull()
    {
        var buffer = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)(SafetensorsHeaderReader.MaxHeaderBytes + 1));
        var path = WriteFile("oversized.safetensors", buffer);

        SafetensorsHeaderReader.TryRead(path).Should().BeNull();
    }

    [Fact]
    public void TryRead_GarbageJsonReturnsNull()
    {
        var bytes = Safetensors("not json{{");
        var path = WriteFile("garbage.safetensors", bytes);

        SafetensorsHeaderReader.TryRead(path).Should().BeNull();
    }

    [Fact]
    public void TryRead_WrongExtensionReturnsNull()
    {
        var bytes = Safetensors(Meta(
            ("ss_base_model_version", "sdxl_base_v1-0"),
            ("modelspec.architecture", "stable-diffusion-xl-v1-base/lora"),
            ("ss_sd_model_name", "ponyDiffusionV6XL")));
        var path = WriteFile("model.ckpt", bytes);

        SafetensorsHeaderReader.TryRead(path).Should().BeNull();
    }

    [Fact]
    public void TryRead_MissingFileReturnsNull()
    {
        var path = Path.Combine(_dir, "gone.safetensors");

        SafetensorsHeaderReader.TryRead(path).Should().BeNull();
    }

    [Fact]
    public void TryRead_EmptyLengthReturnsNull()
    {
        var buffer = new byte[8];
        var path = WriteFile("empty.safetensors", buffer);

        SafetensorsHeaderReader.TryRead(path).Should().BeNull();
    }
}
