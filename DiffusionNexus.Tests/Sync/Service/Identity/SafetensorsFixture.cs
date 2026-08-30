using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using DiffusionNexus.Civitai;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service.Identity;

/// <summary>
/// Shared byte-layout builders for every suite that exercises the safetensors identity chain —
/// <c>SafetensorsHeaderReaderTests</c>, <c>BaseModelHeaderMapTests</c>,
/// <c>FilenameBaseModelHeuristicTests</c>, and <c>IdentifyModelStepTests</c> — plus the reflection
/// accessor onto <see cref="CivitaiBaseModelCatalog"/>'s bundled snapshot two of them use. The
/// safetensors byte layout is exactly the thing that must not drift between suites, so it is built
/// in one place instead of copy-pasted into four.
/// </summary>
public static class SafetensorsFixture
{
    /// <summary>
    /// Builds a minimal real safetensors byte layout: an 8-byte little-endian header length, the
    /// header JSON itself, then a few trailing bytes standing in for tensor data.
    /// </summary>
    public static byte[] Safetensors(string headerJson, int trailingTensorBytes = 16)
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
    public static string Meta(params (string Key, string Value)[] pairs) =>
        "{\"__metadata__\":{" + string.Join(",", pairs.Select(p => $"\"{p.Key}\":\"{p.Value}\"")) +
        "},\"tensor.weight\":{\"dtype\":\"F16\",\"shape\":[4],\"data_offsets\":[0,8]}}";

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

    /// <summary>
    /// <see cref="CivitaiBaseModelCatalog"/>'s private bundled label snapshot, reflected once here
    /// instead of duplicated per suite — backs the "every output is a real Civitai label" assertion
    /// in <c>BaseModelHeaderMapTests</c> and <c>FilenameBaseModelHeuristicTests</c>.
    /// </summary>
    public static IReadOnlyList<string> CatalogLabels
    {
        get
        {
            var bundledSnapshotField = typeof(CivitaiBaseModelCatalog)
                .GetField("BundledSnapshot", BindingFlags.NonPublic | BindingFlags.Static);
            bundledSnapshotField.Should().NotBeNull(
                "CivitaiBaseModelCatalog.BundledSnapshot must exist for this check to mean anything");

            return (IReadOnlyList<string>)bundledSnapshotField!.GetValue(null)!;
        }
    }
}
