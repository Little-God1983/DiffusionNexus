using System.Security.Cryptography;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Service;

/// <summary>
/// #654: a LoRA the library knows that reappears at another path must keep its record, not get a
/// bare "???" one. Both moved-file checks compared a hash of the first 10 MB with the stored
/// whole-file SHA-256, so neither could match a real LoRA, and discovery only considered records
/// a verify pass had already flagged invalid. These tests use files just over 10 MB, the size the
/// older tests (a few bytes each) could never reach.
/// </summary>
public sealed class ModelFileSyncServiceMovedFileTests : IDisposable
{
    private const int OverTenMegabytes = 10 * 1024 * 1024 + 4096;

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly string _root;

    public ModelFileSyncServiceMovedFileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dn-moved-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>()
            .Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private IAppSettingsService SettingsWithRoot()
    {
        var mock = new Mock<IAppSettingsService>();
        mock.Setup(s => s.GetEnabledLoraSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([_root]);
        return mock.Object;
    }

    /// <summary>Weights over 10 MB whose bytes differ after the first 10 MB, so a hash of only
    /// the first 10 MB cannot stand in for the whole-file one.</summary>
    private static byte[] Weights(byte seed, int length = OverTenMegabytes)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i * 31 + seed);
        return bytes;
    }

    private string WriteFile(string relative, byte[] content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>A Civitai-identified record for <paramref name="content"/> at
    /// <paramref name="localPath"/>, with the whole-file SHA-256 Civitai records.</summary>
    private async Task<int> SeedRecordAsync(string name, string localPath, byte[] content, bool isValid)
    {
        var model = new Model { Name = name, Type = ModelType.LORA, Source = DataSource.CivitaiApi };
        var version = new ModelVersion { Name = "v1", BaseModelRaw = "SDXL 1.0", Model = model };
        var file = new ModelFile
        {
            FileName = Path.GetFileName(localPath),
            LocalPath = localPath,
            HashSHA256 = Convert.ToHexString(SHA256.HashData(content)),
            FileSizeBytes = content.Length,
            IsPrimary = true,
            IsLocalFileValid = isValid,
            LocalFileVerifiedAt = DateTimeOffset.UtcNow.AddDays(-1),
            ModelVersion = version,
        };
        version.Files.Add(file);
        model.Versions.Add(version);

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();
        return file.Id;
    }

    private async Task<IReadOnlyList<ModelFile>> AllFilesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await uow.ModelFiles.GetAllWithLocalPathAsync();
    }

    private async Task<DiscoveryResult> DiscoverAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var sut = new ModelFileSyncService(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), SettingsWithRoot());
        return await sut.DiscoverNewFilesAsync();
    }

    private async Task<FileSyncResult> VerifyAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var sut = new ModelFileSyncService(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), SettingsWithRoot());
        return await sut.VerifyAndSyncFilesAsync();
    }

    [Fact]
    public async Task Discovery_ReattachesAMovedFileOverTenMegabytes()
    {
        var weights = Weights(1);
        var recordId = await SeedRecordAsync("Alien Landscapes", Path.Combine(_root, "old", "a.safetensors"), weights, isValid: false);
        var newPath = WriteFile(@"new\a.safetensors", weights);

        var result = await DiscoverAsync();

        result.NewModels.Should().BeEmpty("the file is the known LoRA at a new path, not a new model");
        result.RepointedCount.Should().Be(1);
        (await AllFilesAsync()).Single(f => f.Id == recordId).LocalPath.Should().Be(newPath);
    }

    /// <summary>
    /// The test-lab case: the record's file vanished since the last verify, so the record is still
    /// flagged valid when discovery runs (discovery runs before verify at startup).
    /// </summary>
    [Fact]
    public async Task Discovery_ReattachesToARecordWhoseFileIsGoneButNotYetFlagged()
    {
        var weights = Weights(2);
        var recordId = await SeedRecordAsync("Abyssal Tech", Path.Combine(_root, "Target", "a.safetensors"), weights, isValid: true);
        var newPath = WriteFile(@"LAB\a.safetensors", weights);

        var result = await DiscoverAsync();

        result.NewModels.Should().BeEmpty();
        var file = (await AllFilesAsync()).Single(f => f.Id == recordId);
        file.LocalPath.Should().Be(newPath);
        file.IsLocalFileValid.Should().BeTrue();
    }

    /// <summary>A copy is not a move: the record whose file is still there keeps it, and the copy
    /// gets its own record (the library keeps one record chain per file).</summary>
    [Fact]
    public async Task Discovery_NeverTakesOverARecordWhoseFileStillExists()
    {
        var weights = Weights(3);
        var originalPath = WriteFile(@"store1\a.safetensors", weights);
        var recordId = await SeedRecordAsync("Original", originalPath, weights, isValid: true);
        WriteFile(@"store2\a.safetensors", weights);

        var result = await DiscoverAsync();

        result.NewModels.Should().ContainSingle("the copy is a second file on disk");
        result.RepointedCount.Should().Be(0);
        (await AllFilesAsync()).Single(f => f.Id == recordId).LocalPath.Should().Be(originalPath);
    }

    [Fact]
    public async Task Discovery_TwoIdenticalNewFilesDoNotBothClaimOneRecord()
    {
        var weights = Weights(4);
        var recordId = await SeedRecordAsync("Gone", Path.Combine(_root, "gone", "a.safetensors"), weights, isValid: false);
        var first = WriteFile(@"x\a.safetensors", weights);
        var second = WriteFile(@"y\a.safetensors", weights);

        var result = await DiscoverAsync();

        result.RepointedCount.Should().Be(1);
        result.NewModels.Should().ContainSingle();
        (await AllFilesAsync()).Single(f => f.Id == recordId).LocalPath.Should().BeOneOf(first, second);
    }

    [Fact]
    public async Task Verify_FindsAMovedFileOverTenMegabytes()
    {
        var weights = Weights(5);
        var recordId = await SeedRecordAsync("Moved", Path.Combine(_root, "old", "a.safetensors"), weights, isValid: true);
        var newPath = WriteFile(@"new\a.safetensors", weights);

        var result = await VerifyAsync();

        result.MovedCount.Should().Be(1);
        (await AllFilesAsync()).Single(f => f.Id == recordId).LocalPath.Should().Be(newPath);
    }

    /// <summary>
    /// Matching by whole-file hash finds copies as well as moves. A missing record must not be
    /// pointed at a file another record already owns: two records claiming one path is the
    /// "contested path" state the verify pass then has to arbitrate.
    /// </summary>
    [Fact]
    public async Task Verify_DoesNotMoveARecordOntoAFileAnotherRecordOwns()
    {
        var weights = Weights(6, length: 4096);
        var ownedPath = WriteFile(@"store\a.safetensors", weights);
        var ownerId = await SeedRecordAsync("Owner", ownedPath, weights, isValid: true);
        var missingId = await SeedRecordAsync("Missing copy", Path.Combine(_root, "gone", "a.safetensors"), weights, isValid: true);

        var result = await VerifyAsync();

        result.MovedCount.Should().Be(0);
        var files = await AllFilesAsync();
        files.Single(f => f.Id == ownerId).LocalPath.Should().Be(ownedPath);
        var missing = files.Single(f => f.Id == missingId);
        missing.LocalPath.Should().NotBe(ownedPath);
        missing.IsLocalFileValid.Should().BeFalse();
    }
}
