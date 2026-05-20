using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.LoraSort.Services;

public class LoraDuplicateFinderTests : IDisposable
{
    private readonly string _root;

    public LoraDuplicateFinderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lora_dup_test_" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Empty_Library_Returns_No_Groups()
    {
        var finder = BuildFinder(Array.Empty<Model>());
        var result = await finder.FindAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Different_Content_Same_Size_Is_Not_Reported()
    {
        var a = WriteFile("a.safetensors", new string('A', 1024));
        var b = WriteFile("b.safetensors", new string('B', 1024));

        var models = new[]
        {
            BuildModel(1, "Model A", a),
            BuildModel(2, "Model B", b)
        };

        var finder = BuildFinder(models);
        var result = await finder.FindAsync();
        result.Should().BeEmpty("hash collision check must drop same-size, different-content files");
    }

    [Fact]
    public async Task Pair_Of_Identical_Files_Forms_One_Group()
    {
        var content = new string('X', 2048);
        var a = WriteFile("a.safetensors", content);
        var b = WriteFile("b.safetensors", content);

        var models = new[]
        {
            BuildModel(1, "Same Lora 1", a),
            BuildModel(2, "Same Lora 2", b)
        };

        var finder = BuildFinder(models);
        var result = await finder.FindAsync();

        result.Should().HaveCount(1);
        var group = result[0];
        group.Files.Should().HaveCount(2);
        group.Files.Select(f => f.FilePath).Should().BeEquivalentTo(new[] { a, b });
        group.SizeBytes.Should().Be(new FileInfo(a).Length);
    }

    [Fact]
    public async Task Three_Way_Identical_Group_Returns_All_Three()
    {
        var content = new string('Y', 4096);
        var a = WriteFile("a.safetensors", content);
        var b = WriteFile("b.safetensors", content);
        var c = WriteFile("c.safetensors", content);

        var models = new[]
        {
            BuildModel(1, "Triple 1", a),
            BuildModel(2, "Triple 2", b),
            BuildModel(3, "Triple 3", c)
        };

        var finder = BuildFinder(models);
        var result = await finder.FindAsync();

        result.Should().HaveCount(1);
        result[0].Files.Should().HaveCount(3);
    }

    [Fact]
    public async Task Two_Independent_Groups_Are_Reported_Separately()
    {
        var a1 = WriteFile("g1_a.safetensors", new string('1', 1024));
        var a2 = WriteFile("g1_b.safetensors", new string('1', 1024));
        var b1 = WriteFile("g2_a.safetensors", new string('2', 2048));
        var b2 = WriteFile("g2_b.safetensors", new string('2', 2048));

        var models = new[]
        {
            BuildModel(1, "g1", a1), BuildModel(2, "g1", a2),
            BuildModel(3, "g2", b1), BuildModel(4, "g2", b2)
        };

        var finder = BuildFinder(models);
        var result = await finder.FindAsync();

        result.Should().HaveCount(2);
        result.Sum(g => g.Files.Count).Should().Be(4);
        // Bigger group is listed first.
        result[0].SizeBytes.Should().Be(2048);
        result[1].SizeBytes.Should().Be(1024);
    }

    [Fact]
    public async Task Missing_Files_Are_Skipped()
    {
        var content = new string('Z', 1024);
        var a = WriteFile("present.safetensors", content);
        var b = Path.Combine(_root, "missing.safetensors"); // never created

        var models = new[]
        {
            BuildModel(1, "present", a),
            BuildModel(2, "missing", b)
        };

        var finder = BuildFinder(models);
        var result = await finder.FindAsync();
        result.Should().BeEmpty("missing local files cannot participate in a duplicate group");
    }

    [Fact]
    public async Task Cached_Sha_Is_Reused_Without_Touching_Disk()
    {
        var content = new string('Q', 2048);
        var a = WriteFile("cached_a.safetensors", content);
        var b = WriteFile("cached_b.safetensors", content);

        var models = new[]
        {
            BuildModel(1, "ca", a, cachedHash: ComputeSha256(a)),
            BuildModel(2, "cb", b, cachedHash: ComputeSha256(b))
        };

        var finder = BuildFinder(models);
        var result = await finder.FindAsync();

        result.Should().HaveCount(1);
        result[0].Files.Should().HaveCount(2);
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static Model BuildModel(int id, string name, string localPath, string? cachedHash = null)
    {
        var size = File.Exists(localPath) ? new FileInfo(localPath).Length : 0L;
        var file = new ModelFile
        {
            Id = id * 100,
            ModelVersionId = id * 10,
            FileName = Path.GetFileName(localPath),
            LocalPath = localPath,
            FileSizeBytes = size,
            IsLocalFileValid = true,
            HashSHA256 = cachedHash
        };
        var version = new ModelVersion
        {
            Id = id * 10,
            ModelId = id,
            Name = "v1",
            Files = new List<ModelFile> { file },
            Images = new List<ModelImage>()
        };
        return new Model
        {
            Id = id,
            Name = name,
            Versions = new List<ModelVersion> { version }
        };
    }

    private static string ComputeSha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        var bytes = sha.ComputeHash(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static LoraDuplicateFinder BuildFinder(IReadOnlyList<Model> models)
    {
        var syncMock = new Mock<IModelSyncService>();
        syncMock
            .Setup(s => s.LoadCachedModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(models);

        var fileRepoMock = new Mock<IModelFileRepository>();
        fileRepoMock
            .Setup(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelFile?)null);

        var uowMock = new Mock<IUnitOfWork>();
        uowMock.SetupGet(u => u.ModelFiles).Returns(fileRepoMock.Object);
        uowMock
            .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        return new LoraDuplicateFinder(syncMock.Object, uowMock.Object);
    }
}
