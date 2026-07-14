using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using Moq;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Pins the observable behavior of <see cref="DatasetBackupService.AnalyzeBackupAsync"/>
/// across the Task.Run/Core refactor (a threading-only change — counts and success/failure
/// shape must stay identical).
/// </summary>
public class DatasetBackupAnalysisTests
{
    [Fact]
    public async Task AnalyzeBackupAsync_CountsEntries_OfASmallZip()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var contentDir = Directory.CreateTempSubdirectory();
            try
            {
                await File.WriteAllTextAsync(Path.Combine(contentDir.FullName, "a.txt"), "alpha");
                await File.WriteAllTextAsync(Path.Combine(contentDir.FullName, "b.txt"), "beta");
                var zipPath = Path.Combine(dir.FullName, "backup.zip");
                System.IO.Compression.ZipFile.CreateFromDirectory(contentDir.FullName, zipPath);

                var service = new DatasetBackupService(new Mock<IAppSettingsService>().Object);

                var result = await service.AnalyzeBackupAsync(zipPath);

                Assert.True(result.Success);
                Assert.Equal(2, result.CaptionCount); // a.txt + b.txt are ".txt" caption entries
            }
            finally
            {
                contentDir.Delete(recursive: true);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
