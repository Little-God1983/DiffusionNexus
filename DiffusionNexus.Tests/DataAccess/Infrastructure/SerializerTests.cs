using DiffusionNexus.DataAccess.Infrastructure.Serialization;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.DataAccess.Infrastructure;

public class SerializerTests
{
    /// <summary>
    /// Simple test class for serialization tests.
    /// </summary>
    public class TestConfig
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTrip_Works(bool useJson)
    {
        ISerializer serializer = useJson ? new JsonSerializerAdapter() : new XmlSerializerAdapter();
        var original = new TestConfig { Key = "Theme", Value = "Dark" };
        string payload = serializer.Serialize(original);
        var copy = serializer.Deserialize<TestConfig>(payload);
        copy.Key.Should().Be(original.Key);
        copy.Value.Should().Be(original.Value);
    }
}
