using DiffusionNexus.DataAccess.Entities;
using DiffusionNexus.DataAccess.Infrastructure.Serialization;
using DiffusionNexus.DataAccess.Infrastructure;
using System.IO;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.DataAccess.Infrastructure;

public class FileConfigStoreTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveAndLoad_Works(bool useJson)
    {
        ISerializer serializer = useJson ? new JsonSerializerAdapter() : new XmlSerializerAdapter();
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var store = new FileConfigStore(dir, serializer);
            var setting = new AppSetting { Key = "Language", Value = "EN" };
            store.Save("settings", setting);
            var loaded = store.Load<AppSetting>("settings");
            loaded.Value.Should().Be(setting.Value);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
