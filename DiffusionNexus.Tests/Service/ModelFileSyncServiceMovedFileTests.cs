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

    /// <summary>
    /// Review: a file still being copied in (a restore, the test-lab reset) has its final length
    /// already, so it matches a waiting record and gets hashed — and reading it fails. That threw
    /// out of the scan and lost every other repoint and new file of the pass. The locked file is
    /// left for the next scan instead: given a bare record now, it could never be re-attached.
    /// </summary>
    [Fact]
    public async Task Discovery_SkipsALockedFileAndKeepsTheRestOfThePass()
    {
        var weights = Weights(7);
        var recordId = await SeedRecordAsync("Being copied", Path.Combine(_root, "old", "a.safetensors"), weights, isValid: false);
        var locked = WriteFile(@"new\a.safetensors", weights);
        WriteFile(@"new\other.safetensors", Weights(8, length: 4096));

        DiscoveryResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await DiscoverAsync();

        result.NewModels.Should().ContainSingle("the unlocked new file is still discovered");
        result.RepointedCount.Should().Be(0);
        var files = await AllFilesAsync();
        files.Should().NotContain(f => f.LocalPath == locked, "no bare record for a file that could not be read");

        var retry = await DiscoverAsync();

        retry.RepointedCount.Should().Be(1, "the next scan re-attaches it once the copy is done");
        (await AllFilesAsync()).Single(f => f.Id == recordId).LocalPath.Should().Be(locked);
    }

    /// <summary>Review: the valid flag can also be stale the other way — a drive that was offline at
    /// the last verify is back. A record whose file exists is never taken over, flag or not.</summary>
    [Fact]
    public async Task Discovery_NeverTakesOverARecordFlaggedInvalidWhoseFileIsBack()
    {
        var weights = Weights(9);
        var originalPath = WriteFile(@"nas\a.safetensors", weights);
        var recordId = await SeedRecordAsync("Back online", originalPath, weights, isValid: false);
        WriteFile(@"local\a.safetensors", weights);

        var result = await DiscoverAsync();

        result.RepointedCount.Should().Be(0);
        result.NewModels.Should().ContainSingle("the second copy is a file of its own");
        (await AllFilesAsync()).Single(f => f.Id == recordId).LocalPath.Should().Be(originalPath);
    }

    /// <summary>Review: a file renamed while moved kept its old FileName on the record, which the
    /// UI shows and the next moved-file search looks for.</summary>
    [Fact]
    public async Task Discovery_TakesTheNewNameOfAFileRenamedWhileMoved()
    {
        var weights = Weights(10);
        var recordId = await SeedRecordAsync("Renamed", Path.Combine(_root, "old", "abc_v1.safetensors"), weights, isValid: false);
        WriteFile(@"new\abc.safetensors", weights);

        await DiscoverAsync();

        (await AllFilesAsync()).Single(f => f.Id == recordId).FileName.Should().Be("abc.safetensors");
    }

    [Fact]
    public async Task Verify_SkipsALockedCandidateAndFinishesThePass()
    {
        var weights = Weights(11);
        var missingId = await SeedRecordAsync("Missing", Path.Combine(_root, "old", "a.safetensors"), weights, isValid: true);
        var presentPath = WriteFile(@"present\b.safetensors", Weights(12, length: 4096));
        var presentId = await SeedRecordAsync("Present", presentPath, Weights(12, length: 4096), isValid: false);
        var locked = WriteFile(@"new\a.safetensors", weights);

        FileSyncResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await VerifyAsync();

        result.MovedCount.Should().Be(0);
        var files = await AllFilesAsync();
        files.Single(f => f.Id == missingId).IsLocalFileValid.Should().BeFalse("not found this pass; the next one retries");
        files.Single(f => f.Id == presentId).IsLocalFileValid.Should().BeTrue("the rest of the pass still ran and was saved");
    }

    /// <summary>
    /// Review round 2: a library the old bug already damaged — the Civitai record points at a dead
    /// path, and a bare record (no hash) holds the file at its new path. Verify moves the Civitai
    /// record there, and the contested-path arbitration then validates the record whose hash
    /// matches. A bare record has no hash to prove ownership, so it does not block the path.
    /// </summary>
    [Fact]
    public async Task Verify_ReattachesARecordToAFileHeldOnlyByABareRecord()
    {
        var weights = Weights(13);
        var newPath = WriteFile(@"LAB\a.safetensors", weights);
        var civitaiId = await SeedRecordAsync("Abyssal Tech", Path.Combine(_root, "Target", "a.safetensors"), weights, isValid: true);
        var bareId = await SeedBareRecordAsync(newPath, weights.Length);

        var result = await VerifyAsync();

        // Review round 3: settled in the same pass. Left to the next one, the bare row was stamped
        // valid later in this pass and won the viewer's "most recently verified" pick, and the two
        // rows then contested the path — a whole-file hash — on every verify after.
        result.MovedCount.Should().Be(1);
        var files = await AllFilesAsync();
        files.Single(f => f.Id == civitaiId).Should().Match<ModelFile>(f => f.LocalPath == newPath && f.IsLocalFileValid);
        var bare = await FileByIdAsync(bareId);
        bare.LocalPath.Should().BeNull("the record whose hash matches the bytes owns the path; the bare one lets go of it");
        bare.IsLocalFileValid.Should().BeFalse();
    }

    /// <summary>
    /// Review round 3: a record WITHOUT a hash cannot prove which file is its own. Moved onto a path
    /// another hashless record holds, the contested-path check found no owner and invalidated
    /// both, and the LoRA disappeared from the library.
    /// </summary>
    [Fact]
    public async Task Verify_NeverMovesAHashlessRecordOntoAPathAnotherRecordHolds()
    {
        var weights = Weights(15, length: 4096);
        var newPath = WriteFile(@"B\x.safetensors", weights);
        var missingId = await SeedBareRecordAsync(Path.Combine(_root, "A", "x.safetensors"), weights.Length);
        var holderId = await SeedBareRecordAsync(newPath, weights.Length);

        var result = await VerifyAsync();
        await VerifyAsync();

        result.MovedCount.Should().Be(0);
        var files = await AllFilesAsync();
        files.Single(f => f.Id == holderId).IsLocalFileValid.Should().BeTrue("the LoRA stays in the library");
        files.Single(f => f.Id == missingId).LocalPath.Should().NotBe(newPath);
    }

    private async Task<ModelFile> FileByIdAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>();
        return await context.Set<ModelFile>().AsNoTracking().SingleAsync(f => f.Id == id);
    }

    /// <summary>
    /// Review round 2: a record whose drive or share is not there at all is offline, not moved.
    /// Taking it over moved its metadata to a deliberate second copy, and the original got a bare
    /// record once the drive came back.
    /// </summary>
    [Fact]
    public async Task ARecordOnAnOfflineDriveIsNotTreatedAsMoved()
    {
        var offlineRoot = UnusedDriveRoot();
        if (offlineRoot is null) return; // every letter in use: nothing to simulate with
        var weights = Weights(14);
        var recordId = await SeedRecordAsync("On the external drive", Path.Combine(offlineRoot, "Loras", "a.safetensors"), weights, isValid: true);
        var copy = WriteFile(@"local\a.safetensors", weights);

        var verify = await VerifyAsync();
        var discovery = await DiscoverAsync();

        verify.MovedCount.Should().Be(0);
        discovery.RepointedCount.Should().Be(0);
        discovery.NewModels.Should().ContainSingle("the local copy is a file of its own");
        (await AllFilesAsync()).Single(f => f.Id == recordId).LocalPath.Should().NotBe(copy);
    }

    private static string? UnusedDriveRoot()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (var letter = 'Z'; letter >= 'H'; letter--)
            if (!used.Contains(letter)) return $"{letter}:\\";
        return null;
    }

    /// <summary>What the scan creates for a file it does not recognise: no hash, "???".</summary>
    private async Task<int> SeedBareRecordAsync(string localPath, long size)
    {
        var model = new Model { Name = Path.GetFileNameWithoutExtension(localPath), Type = ModelType.LORA, Source = DataSource.LocalFile };
        var version = new ModelVersion { Name = model.Name, BaseModelRaw = "???", Model = model };
        var file = new ModelFile
        {
            FileName = Path.GetFileName(localPath), LocalPath = localPath, FileSizeBytes = size,
            IsPrimary = true, IsLocalFileValid = true, ModelVersion = version,
        };
        version.Files.Add(file);
        model.Versions.Add(version);

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();
        return file.Id;
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
