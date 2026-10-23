using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure.Services;
using DiffusionNexus.Service.Services.Sync;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Download;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// <see cref="CivitaiModelDownloader"/> against a REAL database (in-memory SQLite, same DI fixture
/// as <c>LoraDownloadServicePersistTests</c>) rather than a mocked unit of work — because the thing
/// under test here is a database side effect.
/// <para>
/// The transport persists the model row BEFORE the downloader's step-7 verification can reject it:
/// <c>LoraDownloadService.DownloadFileAsync</c> calls <c>PersistDownloadedModelAsync</c> and
/// <c>completed()</c> before it returns, so Model/ModelVersion/ModelFile already exist with
/// <c>IsLocalFileValid = true</c>. A truncated or tampered transfer therefore used to stay
/// permanently registered as a valid library entry — detection followed by leaving the evidence in
/// place. HashMismatch must clear that flag.
/// </para>
/// </summary>
public sealed class CivitaiModelDownloaderPersistTests : IDisposable
{
    private const string Url = "https://civitai.test/api/download/models/4242";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly string _tempDir;

    public CivitaiModelDownloaderPersistTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>().Database.EnsureCreated();

        _tempDir = Path.Combine(Path.GetTempPath(), "dn-downloader-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static string Sha256Of(string content)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, content);
            return FileHasher.Sha256Upper(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Transport that writes <paramref name="content"/> to the target and reports success.</summary>
    private static Mock<ILoraDownloadService> Transport(string content)
    {
        var transport = new Mock<ILoraDownloadService>();
        transport
            .Setup(t => t.DownloadFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CivitaiModelVersion>(), It.IsAny<string>(),
                It.IsAny<Action<double, string>?>(), It.IsAny<Action?>(), It.IsAny<Action?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<Action?>()))
            .Callback<string, string, CivitaiModelVersion, string, Action<double, string>?, Action?, Action?, int?, CancellationToken, bool, Action?>(
                (_, targetPath, _, _, _, completed, _, _, _, _, _) =>
                {
                    File.WriteAllText(targetPath, content);
                    completed?.Invoke();
                })
            .Returns(Task.CompletedTask);
        transport
            .Setup(t => t.PersistDownloadedModelAsync(It.IsAny<string>(), It.IsAny<CivitaiModelVersion>(), It.IsAny<int?>()))
            .ReturnsAsync(MetadataPersistOutcome.Complete);
        return transport;
    }

    private static CivitaiModelVersion Version(string sha256) => new()
    {
        Id = 4242,
        Name = "v1",
        Files =
        [
            new CivitaiModelFile
            {
                Id = 1,
                Name = "model.safetensors",
                Primary = true,
                DownloadUrl = Url,
                Hashes = new CivitaiFileHashes { SHA256 = sha256 },
            },
        ],
    };

    /// <summary>Writes the row the transport would have written just before verification runs.</summary>
    private async Task SeedFileRowAsync(string localPath)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var model = new Model { Name = "Seeded", Type = ModelType.LORA, Source = DataSource.CivitaiApi };
        var version = new ModelVersion { Name = "v1", BaseModelRaw = "SDXL 1.0" };
        version.Files.Add(new ModelFile
        {
            FileName = Path.GetFileName(localPath),
            LocalPath = localPath,
            IsPrimary = true,
            IsLocalFileValid = true,
        });
        model.Versions.Add(version);
        await unitOfWork.Models.AddAsync(model);
        await unitOfWork.SaveChangesAsync();
    }

    private async Task<ModelFile> ReadFileRowAsync(string localPath)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var rows = await unitOfWork.ModelFiles.GetByLocalPathAsync(localPath);
        return rows.Should().ContainSingle().Subject;
    }

    [Fact]
    public async Task HashMismatch_MarksTheAlreadyPersistedFileRowInvalid()
    {
        var target = Path.Combine(_tempDir, "model.safetensors");
        await SeedFileRowAsync(target);
        (await ReadFileRowAsync(target)).IsLocalFileValid.Should().BeTrue("the transport persisted it as valid");

        var downloader = new CivitaiModelDownloader(
            Transport("corrupt-bytes").Object,
            coordinator: null,
            librarySync: null,
            notifier: new LibraryChangeNotifier(),
            scopeFactory: _serviceProvider.GetRequiredService<IServiceScopeFactory>());

        var outcome = await downloader.DownloadAsync(
            new DownloadRequest(Version(Sha256Of("the-real-bytes")), _tempDir, DownloadTrigger.BrowseQueue));

        outcome.Status.Should().Be(DownloadStatus.HashMismatch);

        var row = await ReadFileRowAsync(target);
        row.IsLocalFileValid.Should().BeFalse(
            "a file whose bytes failed verification must not stay registered as a valid library entry");
        row.LocalFileVerifiedAt.Should().NotBeNull("the invalidation is a verification result, and is stamped as one");
    }

    [Fact]
    public async Task VerifiedTransfer_LeavesTheFileRowValid()
    {
        var target = Path.Combine(_tempDir, "model.safetensors");
        await SeedFileRowAsync(target);

        var downloader = new CivitaiModelDownloader(
            Transport("the-real-bytes").Object,
            coordinator: null,
            librarySync: null,
            notifier: new LibraryChangeNotifier(),
            scopeFactory: _serviceProvider.GetRequiredService<IServiceScopeFactory>());

        var outcome = await downloader.DownloadAsync(
            new DownloadRequest(Version(Sha256Of("the-real-bytes")), _tempDir, DownloadTrigger.BrowseQueue));

        outcome.Status.Should().Be(DownloadStatus.Completed);
        (await ReadFileRowAsync(target)).IsLocalFileValid.Should().BeTrue(
            "only a mismatch invalidates — a good download must not be collaterally marked bad");
    }
}
