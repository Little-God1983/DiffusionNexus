using DiffusionNexus.LoraSort.Service.Services;
using System.IO;
using System.Linq;
using Xunit;

namespace DiffusionNexus.LoraSort.Service.Tests;

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

            var result = JsonInfoFileReaderService.GroupFilesByPrefix(tempDir);
            Assert.Equal(2, result.Count);
            var first = result.First(m => m.ModelName == "model1");
            Assert.Equal(2, first.AssociatedFilesInfo.Count);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
