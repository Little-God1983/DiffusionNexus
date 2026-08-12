using System.Security.Cryptography;
using System.Text;
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
/// Covers <see cref="ModelFileSyncService"/>'s handling of CONTESTED paths — several
/// ModelFile rows claiming one on-disk file. Generic Civitai file names
/// ("V1.safetensors") make this a real state: a colliding download overwrites the
/// neighbor's weights, the persist flow registers the new owner at the same path and
/// invalidates the old row — but verification used to re-validate ANY row whose path
/// existed, resurrecting the overwritten model's row, which then shadowed the real
/// owner in the Installed tab (user-reported: "downloaded, shows Installed in the
/// browser, but never appears in the Installed tab").
/// </summary>
public sealed class ModelFileSyncServiceContestedPathTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly string _root;

    public ModelFileSyncServiceContestedPathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dn-contested-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider
            .GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();
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

    private static string Sha256Of(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static Model NewModel(string name, string localPath, string? sha256,
        bool valid = true, DateTimeOffset? verifiedAt = null)
    {
        var model = new Model { Name = name, Type = ModelType.LORA, Source = DataSource.CivitaiApi };
        var version = new ModelVersion { Name = "v1", BaseModel = BaseModelType.Other, BaseModelRaw = "Krea 2", Model = model };
        version.Files.Add(new ModelFile
        {
            FileName = Path.GetFileName(localPath),
            LocalPath = localPath,
            HashSHA256 = sha256,
            IsPrimary = true,
            IsLocalFileValid = valid,
            LocalFileVerifiedAt = verifiedAt,
            ModelVersion = version,
        });
        model.Versions.Add(version);
        return model;
    }

    private async Task SeedAsync(params Model[] models)
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var model in models) await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();
    }

    private async Task<Dictionary<string, ModelFile>> FilesByModelNameAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var models = await uow.Models.GetModelsWithLocalFilesLightAsync();
        return models.ToDictionary(
            m => m.Name,
            m => m.Versions.Single().Files.Single());
    }

    [Fact]
    public async Task ContestedPath_ValidatesOnlyTheHashMatchingOwner()
    {
        // "Gimp" was overwritten on disk by "Light projection" — one path, two rows,
        // bytes belong to Light projection.
        var path = WriteFile("V1.safetensors", "light projection weights");
        await SeedAsync(
            NewModel("Gimp", path, Sha256Of("gimp weights")),
            NewModel("Light projection", path, Sha256Of("light projection weights")));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var sut = new ModelFileSyncService(uow, SettingsWithRoot());

        await sut.VerifyAndSyncFilesAsync();

        var files = await FilesByModelNameAsync();
        files["Light projection"].IsLocalFileValid.Should().BeTrue("its recorded SHA256 matches the bytes on disk");
        files["Gimp"].IsLocalFileValid.Should().BeFalse("its weights were overwritten — existence is not ownership");
    }

    [Fact]
    public async Task ContestedPath_NoHashMatches_InvalidatesAllClaimants()
    {
        var path = WriteFile("V1.safetensors", "some third model's weights");
        await SeedAsync(
            NewModel("Gimp", path, Sha256Of("gimp weights")),
            NewModel("Light projection", path, Sha256Of("light projection weights")));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var sut = new ModelFileSyncService(uow, SettingsWithRoot());

        await sut.VerifyAndSyncFilesAsync();

        var files = await FilesByModelNameAsync();
        files["Gimp"].IsLocalFileValid.Should().BeFalse();
        files["Light projection"].IsLocalFileValid.Should().BeFalse(
            "neither row's hash matches the actual bytes, so neither may claim them");
    }

    [Fact]
    public async Task UncontestedPath_IsStillValidatedByExistenceAlone()
    {
        // The fast path must not start hashing 200 MB files: a single claimant with a
        // wrong (or missing) recorded hash still validates by existence, as before.
        var path = WriteFile("solo.safetensors", "solo weights");
        await SeedAsync(NewModel("Solo", path, Sha256Of("something entirely different"), valid: false, verifiedAt: DateTimeOffset.UtcNow.AddDays(-1)));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var sut = new ModelFileSyncService(uow, SettingsWithRoot());

        await sut.VerifyAndSyncFilesAsync();

        var files = await FilesByModelNameAsync();
        files["Solo"].IsLocalFileValid.Should().BeTrue();
    }

    [Fact]
    public async Task LoadCachedFiles_PrefersTheMostRecentlyVerifiedRowForASharedPath()
    {
        // Transient state between a colliding download and the next verify pass:
        // two VALID rows share a path; the fresher row (the just-registered real
        // owner) must win the projection, not whichever model iterates first.
        var path = WriteFile("V1.safetensors", "light projection weights");
        await SeedAsync(
            NewModel("Gimp", path, Sha256Of("gimp weights"),
                valid: true, verifiedAt: DateTimeOffset.UtcNow.AddHours(-2)),
            NewModel("Light projection", path, Sha256Of("light projection weights"),
                valid: true, verifiedAt: DateTimeOffset.UtcNow));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var sut = new ModelFileSyncService(uow, SettingsWithRoot());

        var rows = await sut.LoadCachedFilesAsync();

        var entry = rows.Should().ContainSingle().Subject;
        entry.Model.Name.Should().Be("Light projection");
    }
}
