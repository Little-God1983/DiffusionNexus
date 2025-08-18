using DiffusionNexus.Service.Services;
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

            var result = JsonInfoFileReaderService.GroupFilesByPrefix(tempDir);
            result.Count.Should().Be(2);
            var first = result.First(m => m.SafeTensorFileName == "model1");
            first.AssociatedFilesInfo.Count.Should().Be(2);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SameModelNameInDifferentDirectories_IsNotMerged()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var dirA = Path.Combine(tempDir, "a");
        var dirB = Path.Combine(tempDir, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        try
        {
            File.WriteAllText(Path.Combine(dirA, "model.safetensors"), string.Empty);
            File.WriteAllText(Path.Combine(dirB, "model.safetensors"), string.Empty);

            var result = JsonInfoFileReaderService.GroupFilesByPrefix(tempDir);
            result.Should().HaveCount(2);
            result.Select(m => m.SafeTensorFileName).Should().AllBe("model");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
