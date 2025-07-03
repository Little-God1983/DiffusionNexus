using DiffusionNexus.Service.Services;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace DiffusionNexus.Tests.LoraSort.Services;
public class DuplicateScannerTests
{
    [Fact]
    public async Task ScanAsync_FindsDuplicates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dup_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.safetensors");
            var b = Path.Combine(dir, "b.safetensors");
            var c = Path.Combine(dir, "c.safetensors");
            File.WriteAllText(a, "hello");
            File.Copy(a, b);
            File.WriteAllText(c, "other");

            var scanner = new DuplicateScanner();
            var result = await scanner.ScanAsync(dir, null);
            Assert.Single(result);
            var set = result.First();
            var paths = new[] { set.FileA.FullName, set.FileB.FullName };
            Assert.Contains(a, paths);
            Assert.Contains(b, paths);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
