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
/// Covers <see cref="ModelFileSyncService.CountExcludedSupportAssetsAsync"/> (#527, Task 12 re-plan):
/// the number the Viewer uses to explain why a legacy library's VAEs, ControlNets, upscalers and
/// text encoders vanish from the grid the moment Tasks 6-8 give them a real <c>Model.Type</c>.
/// <see cref="ModelFileSyncService.LoadCachedFilesAsync"/> already drops every model failing
/// <c>IsLoraFamily</c> — a deliberate, pre-existing rule this task does not change — so the count
/// exists purely to name what that rule is hiding. Mirrors
/// <see cref="ModelFileSyncServiceBackfillTests"/>'s fixture shape (shared kept-open SQLite
/// connection, <c>AddDataAccessLayer</c> with <c>UseSqlite</c>, <c>EnsureCreated</c>, a scope held
/// for the test's life, disposed before the provider).
/// </summary>
public sealed class ModelFileSyncServiceSupportAssetCountTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly IServiceScope _scope;
    private readonly ModelFileSyncService _service;

    /// <summary>The one enabled LoRA source the mocked settings hand back.</summary>
    private readonly string _enabledRoot =
        Path.Combine(Path.GetTempPath(), "dn-support-asset-count-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A folder the mocked settings never name — stands in for a source the user has disabled (or
    /// removed). No test reads its contents; only the LocalPath string needs to sit outside
    /// <see cref="_enabledRoot"/>.
    /// </summary>
    private readonly string _disabledRoot =
        Path.Combine(Path.GetTempPath(), "dn-support-asset-count-disabled-" + Guid.NewGuid().ToString("N"));

    public ModelFileSyncServiceSupportAssetCountTests()
    {
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

        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.GetEnabledLoraSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([_enabledRoot]);

        // Held for the life of the test, not a per-call `using` — same reasoning as
        // ModelFileSyncServiceBackfillTests: the backing IUnitOfWork/DbContext has to outlive a
        // single CountExcludedSupportAssetsAsync call and every test here calls _service directly.
        _scope = _serviceProvider.CreateScope();
        var uow = _scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        _service = new ModelFileSyncService(uow, settings.Object);
    }

    public void Dispose()
    {
        _scope.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Inserts a minimal local-file row — one Model, one Version, one File — whose LocalPath sits
    /// under the mocked enabled source (or under an unrelated, never-enabled temp folder when
    /// <paramref name="underEnabledSource"/> is false), with <c>IsLocalFileValid = true</c>. No
    /// bytes ever touch disk: every path the code under test checks is a string comparison against
    /// the normalized roots, never a filesystem read.
    /// </summary>
    private async Task<int> GivenModelAsync(string name, ModelType type, bool underEnabledSource = true)
    {
        var root = underEnabledSource ? _enabledRoot : _disabledRoot;
        var localPath = Path.Combine(root, $"{name}.safetensors");

        var model = new Model { Name = name, Type = type, Source = DataSource.LocalFile };
        var version = new ModelVersion { Name = "v1", BaseModelRaw = "???", Model = model };
        version.Files.Add(new ModelFile
        {
            FileName = Path.GetFileName(localPath),
            LocalPath = localPath,
            IsPrimary = true,
            IsLocalFileValid = true,
            ModelVersion = version,
        });
        model.Versions.Add(version);

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();
        return model.Id;
    }

    /// <summary>
    /// On a legacy library these files vanish from a grid the user has watched for months,
    /// because LoadCachedFilesAsync drops everything outside IsLoraFamily. The count is what
    /// turns a silent disappearance into an explained one.
    /// </summary>
    [Fact]
    public async Task CountsTheSupportAssetsTheGridIsHiding()
    {
        await GivenModelAsync("Wan2_2_VAE_bf16", ModelType.VAE);
        await GivenModelAsync("clip_g_hidream", ModelType.TextEncoder);
        await GivenModelAsync("MyChar_Pony_v2", ModelType.LORA);

        (await _service.CountExcludedSupportAssetsAsync()).Should().Be(2);
    }

    /// <summary>
    /// Only the support kinds count. A checkpoint is also excluded from the grid, but it was
    /// never a LoRA the user expected to see there, and #527 is not about checkpoints — saying
    /// "3 support assets hidden" when one of them is a checkpoint would be a wrong explanation.
    /// </summary>
    [Fact]
    public async Task DoesNotCountOtherNonLoraTypes()
    {
        await GivenModelAsync("SomeCheckpoint", ModelType.Checkpoint);
        await GivenModelAsync("SomeEmbedding", ModelType.TextualInversion);
        await GivenModelAsync("SD3-VAE", ModelType.VAE);

        (await _service.CountExcludedSupportAssetsAsync()).Should().Be(1);
    }

    /// <summary>
    /// The count describes the grid, so it must honour the same enabled-source-folder rule the
    /// grid does — a VAE under a source the user disabled is not being hidden by this feature.
    /// </summary>
    [Fact]
    public async Task IgnoresFilesOutsideTheEnabledSources()
    {
        await GivenModelAsync("Wan2_2_VAE_bf16", ModelType.VAE, underEnabledSource: false);

        (await _service.CountExcludedSupportAssetsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task IsZeroWhenTheLibraryHoldsOnlyLoras()
    {
        await GivenModelAsync("MyChar_Pony_v2", ModelType.LORA);

        (await _service.CountExcludedSupportAssetsAsync()).Should().Be(0);
    }
}
