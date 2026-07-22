using System.Text.Json;
using DiffusionNexus.Civitai.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

/// <summary>
/// Pins <c>NextCursorJsonConverter</c> (internal) through the public
/// <see cref="CivitaiPaginationMetadata.NextCursor"/> property it decorates.
/// Civitai returns <c>nextCursor</c> as a JSON number on some endpoints and a
/// string on others; the converter normalizes both to a string and is culture
/// invariant (important: this suite runs on a German-locale machine, so a
/// locale-dependent decimal separator would be caught here).
/// </summary>
public class NextCursorJsonConverterTests
{
    private static string? ReadCursor(string cursorJson)
    {
        var json = $$"""{ "totalItems": 0, "nextCursor": {{cursorJson}} }""";
        return JsonSerializer.Deserialize<CivitaiPaginationMetadata>(json)!.NextCursor;
    }

    [Fact]
    public void Read_StringCursor_ReturnedVerbatim()
    {
        ReadCursor("\"abc-123|xyz\"").Should().Be("abc-123|xyz");
    }

    [Fact]
    public void Read_IntegerCursor_CoercedToString()
    {
        ReadCursor("482913").Should().Be("482913");
    }

    [Fact]
    public void Read_LargeLongCursor_CoercedToString()
    {
        // Exceeds Int32; must go through the Int64 path, not overflow.
        ReadCursor("9999999999").Should().Be("9999999999");
    }

    [Fact]
    public void Read_NonIntegerNumberCursor_UsesInvariantDecimalPoint()
    {
        // Falls through to the double branch (G17, invariant). A German locale
        // would render "1,5" — the converter must keep the invariant "1.5".
        ReadCursor("1.5").Should().Be("1.5");
    }

    [Fact]
    public void Read_NullCursor_ReturnsNull()
    {
        ReadCursor("null").Should().BeNull();
    }

    [Fact]
    public void Read_MissingCursor_DefaultsToNull()
    {
        var json = """{ "totalItems": 0 }""";
        JsonSerializer.Deserialize<CivitaiPaginationMetadata>(json)!.NextCursor.Should().BeNull();
    }

    [Fact]
    public void Write_NonNull_EmitsString()
    {
        var meta = new CivitaiPaginationMetadata { NextCursor = "42" };
        JsonSerializer.Serialize(meta).Should().Contain("\"nextCursor\":\"42\"");
    }

    [Fact]
    public void Write_Null_EmitsNull()
    {
        var meta = new CivitaiPaginationMetadata { NextCursor = null };
        JsonSerializer.Serialize(meta).Should().Contain("\"nextCursor\":null");
    }
}
