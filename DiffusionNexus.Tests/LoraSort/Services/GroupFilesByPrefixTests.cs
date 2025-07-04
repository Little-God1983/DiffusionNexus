using DiffusionNexus.Service.Services;
using DiffusionNexus.Service.Services.Metadata;
using System.Net.Http;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.LoraSort.Services;
public class GroupFilesByPrefixTests
{
    [Fact]
    public void GroupsFilesWithSamePrefixTogether()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "model1.safetensors"), string.Empty);
            File.WriteAllText(Path.Combine(tempDir, "model1.preview.png"), string.Empty);
            File.WriteAllText(Path.Combine(tempDir, "model2.ckpt"), string.Empty);

            var service = new ModelMetadataService(new CivitaiApiClient(new HttpClient()), string.Empty);
            var result = service.GroupFilesByPrefix(tempDir);
            result.Count.Should().Be(2);
            var first = result.First(m => m.SafeTensorFileName == "model1");
            first.AssociatedFilesInfo.Count.Should().Be(2);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
