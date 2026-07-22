using DiffusionNexus.DataAccess.Infrastructure.Serialization;
using FluentAssertions;

namespace DiffusionNexus.Tests.DataAccess.Serialization;

/// <summary>
/// First coverage of the previously-untested
/// <c>DiffusionNexus.DataAccess.Infrastructure</c> serializer adapters. Both are
/// thin wrappers around the framework serializers; these tests pin the
/// round-trip contract and the culture-invariant number formatting (this suite
/// runs on a German-locale machine, so a locale-dependent decimal separator
/// would surface here).
/// </summary>
public class SerializerAdapterTests
{
    public class SampleDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public double Ratio { get; set; }
    }

    // ---------------------------------------------------------------------
    // JSON
    // ---------------------------------------------------------------------

    [Fact]
    public void Json_Serialize_ProducesIndentedPayload()
    {
        ISerializer sut = new JsonSerializerAdapter();

        var json = sut.Serialize(new SampleDto { Id = 1, Name = "x", Ratio = 1.5 });

        // WriteIndented = true -> multi-line output.
        json.Should().Contain("\n");
        json.Should().Contain("\"Id\"").And.Contain("\"Name\"");
    }

    [Fact]
    public void Json_Serialize_UsesInvariantDecimalPoint()
    {
        ISerializer sut = new JsonSerializerAdapter();

        var json = sut.Serialize(new SampleDto { Ratio = 1.5 });

        json.Should().Contain("1.5").And.NotContain("1,5");
    }

    [Fact]
    public void Json_RoundTrip_PreservesValues()
    {
        ISerializer sut = new JsonSerializerAdapter();
        var original = new SampleDto { Id = 7, Name = "round-trip", Ratio = 3.25 };

        var restored = sut.Deserialize<SampleDto>(sut.Serialize(original));

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Json_Deserialize_ReadsExternalPayload()
    {
        ISerializer sut = new JsonSerializerAdapter();

        var dto = sut.Deserialize<SampleDto>("""{ "Id": 5, "Name": "hi", "Ratio": 2.5 }""");

        dto.Id.Should().Be(5);
        dto.Name.Should().Be("hi");
        dto.Ratio.Should().Be(2.5);
    }

    // ---------------------------------------------------------------------
    // XML
    // ---------------------------------------------------------------------

    [Fact]
    public void Xml_Serialize_ContainsElementAndValues()
    {
        ISerializer sut = new XmlSerializerAdapter();

        var xml = sut.Serialize(new SampleDto { Id = 9, Name = "node", Ratio = 1.5 });

        xml.Should().Contain("<SampleDto");
        xml.Should().Contain("<Name>node</Name>");
        // XmlConvert is invariant -> decimal point, never a German comma.
        xml.Should().Contain("<Ratio>1.5</Ratio>");
    }

    [Fact]
    public void Xml_RoundTrip_PreservesValues()
    {
        ISerializer sut = new XmlSerializerAdapter();
        var original = new SampleDto { Id = 11, Name = "xml round-trip", Ratio = 4.75 };

        var restored = sut.Deserialize<SampleDto>(sut.Serialize(original));

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Adapters_ImplementISerializer()
    {
        new JsonSerializerAdapter().Should().BeAssignableTo<ISerializer>();
        new XmlSerializerAdapter().Should().BeAssignableTo<ISerializer>();
    }
}
