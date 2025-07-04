using DiffusionNexus.Service.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace DiffusionNexus.Tests.Service;

public class DuplicateScannerTests : IDisposable
{
    private readonly string _testFolderPath;

    public DuplicateScannerTests()
    {
        _testFolderPath = Path.Combine(Path.GetTempPath(), "DuplicateScannerTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testFolderPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testFolderPath))
        {
            Directory.Delete(_testFolderPath, true);
        }
    }

    private async Task<FileInfo> CreateTestFile(string name, string content)
    {
        var filePath = Path.Combine(_testFolderPath, name);
        await File.WriteAllTextAsync(filePath, content);
        return new FileInfo(filePath);
    }

    [Fact]
    public async Task ScanAsync_WhenNoDuplicates_ReturnsEmptyList()
    {
        // Arrange
        await CreateTestFile("file1.safetensors", "unique content 1");
        await CreateTestFile("file2.safetensors", "unique content 2");
        var scanner = new DuplicateScanner();

        // Act
        var result = await scanner.ScanAsync(_testFolderPath);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ScanAsync_WhenOneDuplicatePair_ReturnsOneSet()
    {
        // Arrange
        await CreateTestFile("file1.safetensors", "duplicate content");
        await CreateTestFile("file2.safetensors", "duplicate content");
        await CreateTestFile("file3.safetensors", "unique content");
        var scanner = new DuplicateScanner();

        // Act
        var result = await scanner.ScanAsync(_testFolderPath);

        // Assert
        Assert.Single(result);
        Assert.Equal("file1.safetensors", result[0].FileA.Name);
        Assert.Equal("file2.safetensors", result[0].FileB.Name);
    }

    [Fact]
    public async Task ScanAsync_WithSubdirectories_FindsDuplicates()
    {
        // Arrange
        await CreateTestFile("file1.safetensors", "duplicate content");
        var subfolderPath = Path.Combine(_testFolderPath, "sub");
        Directory.CreateDirectory(subfolderPath);
        await File.WriteAllTextAsync(Path.Combine(subfolderPath, "file2.safetensors"), "duplicate content");
        var scanner = new DuplicateScanner();

        // Act
        var result = await scanner.ScanAsync(_testFolderPath);

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task ScanAsync_WhenFilesHaveSameSizeButDifferentContent_ReturnsEmptyList()
    {
        // Arrange
        await CreateTestFile("file1.safetensors", "contentA");
        await CreateTestFile("file2.safetensors", "contentB");
        var scanner = new DuplicateScanner();

        // Act
        var result = await scanner.ScanAsync(_testFolderPath);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ScanAsync_WhenNoSafetensorsFiles_ReturnsEmptyList()
    {
        // Arrange
        await CreateTestFile("file1.txt", "some content");
        await CreateTestFile("file2.txt", "some content");
        var scanner = new DuplicateScanner();

        // Act
        var result = await scanner.ScanAsync(_testFolderPath);

        // Assert
        Assert.Empty(result);
    }
}
