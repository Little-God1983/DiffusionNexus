using DiffusionNexus.DataAccess.Entities;
using DiffusionNexus.DataAccess.Infrastructure.Serialization;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.DataAccess.Infrastructure;

public class SerializerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTrip_Works(bool useJson)
    {
        ISerializer serializer = useJson ? new JsonSerializerAdapter() : new XmlSerializerAdapter();
        var original = new AppSetting { Key = "Theme", Value = "Dark" };
        string payload = serializer.Serialize(original);
        var copy = serializer.Deserialize<AppSetting>(payload);
        copy.Key.Should().Be(original.Key);
        copy.Value.Should().Be(original.Value);
    }
}
