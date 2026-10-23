using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using DiffusionNexus.Tests.Sync.Service.Identity;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Service;

/// <summary>
/// Covers the KIND half of <see cref="ModelFileSyncService.DiscoverNewFilesAsync"/> (#527):
/// discovery used to stamp <c>Type = ModelType.LORA</c> on every file it found, so a VAE sitting in
/// a LoRA folder was indistinguishable from a LoRA everywhere downstream — the Viewer, the sorter,
/// and the "could not be identified" count all read that field. These drive real files through the
/// actual discovery loop and a real SQLite round trip, because the bug was never in
/// <c>AssetKindResolver</c> itself (that has its own unit tests) — it was that discovery never
/// called it.
/// </summary>
public sealed class ModelFileSyncServiceDiscoveryKindTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly IServiceScope _scope;
    private readonly string _sourceFolder;
    private readonly ModelFileSyncService _service;

    public ModelFileSyncServiceDiscoveryKindTests()
    {
        _sourceFolder = Path.Combine(Path.GetTempPath(), "dn-kind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceFolder);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using (var initScope = _serviceProvider.CreateScope())
        {
            var context = initScope.ServiceProvider
                .GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>();
            context.Database.EnsureCreated();
        }

        // Held for the life of the test (not a per-call `using`, unlike the repoint suite this is
        // mirrored from) because the brief's test bodies call `_service` directly as a field — the
        // backing IUnitOfWork/DbContext has to outlive the single DiscoverNewFilesAsync call each
        // test makes.
        _scope = _serviceProvider.CreateScope();
        var uow = _scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        _service = new ModelFileSyncService(uow, SettingsWithRoot());
    }

    public void Dispose()
    {
        _scope.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_sourceFolder, recursive: true); } catch { /* best effort */ }
    }

    private IAppSettingsService SettingsWithRoot()
    {
        var mock = new Mock<IAppSettingsService>();
        mock.Setup(s => s.GetEnabledLoraSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([_sourceFolder]);
        return mock.Object;
    }

    /// <summary>
    /// Re-reads the one discovered model through a brand-new scope and <c>AsNoTracking</c> query,
    /// so the assertion proves what round-tripped through the real <c>Type</c>-as-string SQLite
    /// column — not merely what the in-memory instance still holds after <c>SaveChangesAsync</c>
    /// returned. Same technique as <c>ModelFileSyncServiceDiscoveryRepointTests</c>'s verify scope.
    /// </summary>
    private async Task<ModelType> ReadPersistedTypeAsync()
    {
        using var verifyScope = _serviceProvider.CreateScope();
        var verifyUow = verifyScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var models = await verifyUow.Models.GetModelsWithLocalFilesLightAsync();
        return models.Single().Type;
    }

    /// <summary>
    /// Discovery used to stamp Type = LORA on literally every file it found, which is the root of
    /// #527: a VAE was indistinguishable from a LoRA everywhere downstream because the row said it
    /// was one.
    /// </summary>
    [Fact]
    public async Task ADiscoveredVaeIsRecordedAsAVae()
    {
        var path = Path.Combine(_sourceFolder, "Wan2_2_VAE_bf16.safetensors");
        await File.WriteAllBytesAsync(path, SafetensorsFixture.Safetensors(
            SafetensorsFixture.Tensors("post_quant_conv.weight", "encoder.down.0.block.0.norm1.weight")));

        var result = await _service.DiscoverNewFilesAsync();

        result.NewModels.Should().ContainSingle()
            .Which.Type.Should().Be(ModelType.VAE);
        (await ReadPersistedTypeAsync()).Should().Be(ModelType.VAE,
            "the row the Viewer and sorter actually read must record it too, not just the in-memory instance");
    }

    /// <summary>
    /// The weights outrank the name here too, not only in the resolver's own unit tests — this is
    /// the call site where a mistake would physically mislabel a user's row.
    /// </summary>
    [Fact]
    public async Task ALoraNamedLikeAVaeIsStillALora()
    {
        var path = Path.Combine(_sourceFolder, "vae_finetune_lora.safetensors");
        await File.WriteAllBytesAsync(path, SafetensorsFixture.Safetensors(
            SafetensorsFixture.Tensors("lora_unet_blocks_0.lora_up.weight")));

        var result = await _service.DiscoverNewFilesAsync();

        result.NewModels.Should().ContainSingle().Which.Type.Should().Be(ModelType.LORA);
        (await ReadPersistedTypeAsync()).Should().Be(ModelType.LORA,
            "the row the Viewer and sorter actually read must record it too, not just the in-memory instance");
    }

    [Fact]
    public async Task AnUpscalerPickleIsRecordedFromItsName()
    {
        var path = Path.Combine(_sourceFolder, "4x-UltraSharp.pth");
        await File.WriteAllBytesAsync(path, new byte[64]);

        var result = await _service.DiscoverNewFilesAsync();

        result.NewModels.Should().ContainSingle().Which.Type.Should().Be(ModelType.Upscaler);
        (await ReadPersistedTypeAsync()).Should().Be(ModelType.Upscaler,
            "the row the Viewer and sorter actually read must record it too, not just the in-memory instance");
    }

    [Fact]
    public async Task AnOrdinaryLoraIsStillALora()
    {
        var path = Path.Combine(_sourceFolder, "MyChar_Pony_v2.safetensors");
        await File.WriteAllBytesAsync(path, new byte[64]);

        var result = await _service.DiscoverNewFilesAsync();

        result.NewModels.Should().ContainSingle().Which.Type.Should().Be(ModelType.LORA);
        (await ReadPersistedTypeAsync()).Should().Be(ModelType.LORA,
            "the row the Viewer and sorter actually read must record it too, not just the in-memory instance");
    }
}
