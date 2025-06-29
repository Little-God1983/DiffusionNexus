using DiffusionNexus.DataAccess.Entities;
using DiffusionNexus.DataAccess.Infrastructure.Serialization;
using Xunit;

namespace DiffusionNexus.DataAccess.Infrastructure.Tests;

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
        Assert.Equal(original.Key, copy.Key);
        Assert.Equal(original.Value, copy.Value);
    }
}
