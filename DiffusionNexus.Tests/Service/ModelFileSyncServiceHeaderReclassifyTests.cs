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
using static DiffusionNexus.Tests.Sync.Service.Identity.SafetensorsFixture;

namespace DiffusionNexus.Tests.Service;

/// <summary>
/// Covers the WEIGHTS arm of <see cref="ModelFileSyncService.ReclassifySupportAssetsAsync"/> (#527)
/// — the pass that names a legacy safetensors row from its own tensor keys.
/// </summary>
/// <remarks>
/// This arm exists because the correction it duplicates is unreachable for the rows that need it.
/// <c>IdentifyModelStep</c> fixes a row's kind whenever it reads that file's weights, but it only
/// ever sees rows a bulk run SELECTS, and <c>Matched</c> is terminal for the retry policy — so a
/// support asset Civitai happened to match is re-read by nothing and keeps its <c>LORA</c> stamp
/// forever. Three real text encoders were found in exactly that state on a live library
/// (<c>ministral-3-3b</c>, <c>ViT-L-14-…-TE-only-HF</c>, <c>qwen_3_4b</c>), every one of them
/// unambiguous from its first tensor key.
/// <para>
/// Real bytes on disk and a real SQLite round trip, not a mocked resolver: the defect was never in
/// <see cref="Sync.Service.Identity.AssetKindHeaderMapTests"/>'s territory — the map already
/// answered correctly for all three files — it was that nothing ever asked it.
/// </para>
/// </remarks>
public sealed class ModelFileSyncServiceHeaderReclassifyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly IServiceScope _scope;
    private readonly string _folder;
    private readonly ModelFileSyncService _service;

    /// <summary>The first tensor key of the real <c>qwen_3_4b.safetensors</c> / <c>ministral-3-3b.safetensors</c>.</summary>
    private const string LlmDecoderKey = "model.layers.0.mlp.gate_proj.weight";

    public ModelFileSyncServiceHeaderReclassifyTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "dn-hdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);

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

        // Held for the test's life, mirroring the sibling suites: bodies call `_service` as a field,
        // and IsReadOncePerFileEver calls it twice, so the backing IUnitOfWork has to outlive one call.
        _scope = _serviceProvider.CreateScope();
        var uow = _scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        _service = new ModelFileSyncService(uow, new Mock<IAppSettingsService>().Object);
    }

    public void Dispose()
    {
        _scope.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The live-library case, end to end: a text encoder Civitai matched to some model page, whose
    /// row therefore says LORA and whose weights say otherwise. Nothing but this pass reaches it.
    /// </summary>
    [Fact]
    public async Task NamesAMatchedCivitaiRowFromItsWeights()
    {
        var id = await GivenRowAsync("qwen_3_4b.safetensors", Tensors(LlmDecoderKey));

        (await _service.ReclassifySupportAssetsAsync()).Should().Be(1);
        (await LoadTypeAsync(id)).Should().Be(ModelType.TextEncoder);
    }

    /// <summary>
    /// The guard that made this arm necessary, stated from the other side: <c>Matched</c> and
    /// <c>Source = CivitaiApi</c> are not reasons to leave a row alone, because nothing in a Civitai
    /// payload has ever written <c>Model.Type</c> — a matched row's LORA is our own default, not an
    /// upstream verdict. (When Civitai's type does start being written, #550, that stops being true
    /// and this pass needs a signal saying so.)
    /// </summary>
    [Theory]
    [InlineData(DataSource.CivitaiApi, SyncOutcome.Matched)]
    [InlineData(DataSource.CivitaiApi, SyncOutcome.NotIdentified)]
    [InlineData(DataSource.LocalFile, SyncOutcome.Sidecar)]
    [InlineData(DataSource.LocalFile, SyncOutcome.None)]
    public async Task ReachesARowWhateverIdentifiedIt(DataSource source, SyncOutcome outcome)
    {
        var id = await GivenRowAsync("vae.safetensors", Tensors("post_quant_conv.weight"),
            source: source, outcome: outcome);

        (await _service.ReclassifySupportAssetsAsync()).Should().Be(1);
        (await LoadTypeAsync(id)).Should().Be(ModelType.VAE);
    }

    /// <summary>
    /// The termination property, and the reason the stamp is unconditional. A container whose keys
    /// match no rung is stamped anyway: the question has been asked, and asking it again would read
    /// the same bytes for the same silence. Without that, every unrecognised container in the
    /// library would be re-read on every discovery pass, forever.
    /// </summary>
    [Fact]
    public async Task IsReadOncePerFileEverEvenWhenTheWeightsSayNothing()
    {
        var id = await GivenRowAsync("mystery.safetensors", Tensors("some.opaque.tensor"));

        (await _service.ReclassifySupportAssetsAsync()).Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
        (await LoadHeaderCheckedAtAsync(id)).Should().NotBeNull("the header was read, and that is what bounds the pass");

        // Second pass: the candidate query no longer selects it. Proven by deleting the file — if
        // the row were still selected, the missing-file branch would be what saved us, not the stamp.
        File.Delete(Path.Combine(_folder, "mystery.safetensors"));
        (await _service.ReclassifySupportAssetsAsync()).Should().Be(0);
    }

    /// <summary>
    /// A container we could not OPEN is not evidence about anything, so it must not be stamped:
    /// a file still copying onto a NAS, or held by a trainer, would otherwise have its kind settled
    /// forever by a failure that had nothing to do with its contents. Same rule
    /// <c>AssetKindResolver.ContainerWasUnreadable</c> states for the callers that write a verdict.
    /// </summary>
    [Fact]
    public async Task DoesNotStampAContainerItCouldNotRead()
    {
        // A declared header length that runs past the end of the file — exactly what a half-copied
        // container looks like, and what SafetensorsHeaderReader answers null for.
        var truncated = new byte[] { 0x00, 0x04, 0, 0, 0, 0, 0, 0, 0x7B, 0x7D };
        var id = await GivenRowAsync("half-copied.safetensors", rawBytes: truncated);

        (await _service.ReclassifySupportAssetsAsync()).Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
        (await LoadHeaderCheckedAtAsync(id)).Should().BeNull("the next pass has to ask again once the copy finishes");
    }

    /// <summary>
    /// A genuine LoRA is what this whole library is made of — 1435 of the 1553 containers on the
    /// reference library — so the pass has to be silent on every one of them.
    /// </summary>
    [Fact]
    public async Task LeavesAGenuineLoraAlone()
    {
        var id = await GivenRowAsync("mychar_pony_v2.safetensors",
            Tensors("lora_unet_down_blocks_0_attn1_to_q.lora_down.weight",
                    "lora_unet_down_blocks_0_attn1_to_q.lora_up.weight"));

        (await _service.ReclassifySupportAssetsAsync()).Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// The regression this arm would otherwise have caused, end to end. A 2515-tensor SDXL
    /// checkpoint presents its conditioner block FIRST, so the sampled window is 64 keys of
    /// "conditioner.embedders.0.transformer.text_model.…" with no <c>first_stage_model.</c> or
    /// <c>model.diffusion_model.</c> key among them — the composite guard sees nothing to fire on.
    /// Before this pass existed nothing re-read such a row, so the free substring needle it hit was
    /// invisible; the moment the pass exists, two real 13.8 GB Pony checkpoints move into
    /// <c>Text Encoder\</c>. Anchoring the rung to the key ROOT is what stops it.
    /// </summary>
    [Fact]
    public async Task DoesNotMoveACheckpointSampledEntirelyAtItsConditionerBlock()
    {
        var id = await GivenRowAsync("cyberrealisticPony_v160.safetensors", Tensors(
            "conditioner.embedders.0.transformer.text_model.embeddings.token_embedding.weight",
            "conditioner.embedders.0.transformer.text_model.encoder.layers.0.layer_norm1.bias",
            "conditioner.embedders.0.transformer.text_model.encoder.layers.0.mlp.fc1.weight"));

        (await _service.ReclassifySupportAssetsAsync()).Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// A type the user set by hand is an answer, not a blank — the same refusal
    /// <c>IdentifyModelStep</c>'s <c>typeIsOurs</c> guard makes, and the two write sites have to
    /// agree about what a user's edit means.
    /// </summary>
    [Fact]
    public async Task NeverTouchesAUserEditedRow()
    {
        var id = await GivenRowAsync("hand-typed.safetensors", Tensors("post_quant_conv.weight"),
            isUserEdited: true);

        (await _service.ReclassifySupportAssetsAsync()).Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// A row that already carries a stamp has had its answer. This is what makes the read once per
    /// file EVER rather than once per pass — the whole cost argument depends on it.
    /// </summary>
    [Fact]
    public async Task SkipsARowWhoseHeaderHasAlreadyBeenRead()
    {
        var id = await GivenRowAsync("already-read.safetensors", Tensors("post_quant_conv.weight"),
            headerCheckedAt: DateTimeOffset.UtcNow.AddDays(-3));

        (await _service.ReclassifySupportAssetsAsync()).Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// A row with no <see cref="ModelSyncState"/> at all is left for <c>SyncStateInitializer</c>,
    /// which derives each one from the model's own history. Creating a bare row here instead would
    /// be <c>None</c>/unstamped — immediately due for a metadata check — which is precisely the
    /// first-run herd <c>SyncStateDeriver</c> exists to prevent. The initializer runs at the head of
    /// every sync plan, so such a row is simply picked up by the next pass.
    /// </summary>
    [Fact]
    public async Task LeavesARowWithNoSyncStateToTheInitializer()
    {
        var id = await GivenRowAsync("no-state.safetensors", Tensors("post_quant_conv.weight"),
            withSyncState: false);

        (await _service.ReclassifySupportAssetsAsync()).Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
        (await LoadStateAsync(id)).Should().BeNull("the pass must not create the row the initializer derives");
    }

    /// <summary>
    /// Rows <c>DiscoverNewFilesAsync</c> created in the SAME call were resolved from these very
    /// weights moments earlier by the same <c>AssetKindResolver</c>, so re-reading them here can
    /// only cost a second header read to reproduce an identical verdict.
    /// </summary>
    [Fact]
    public async Task SkipsTheModelIdsTheCallerJustClassified()
    {
        var id = await GivenRowAsync("just-discovered.safetensors", Tensors("post_quant_conv.weight"));

        (await _service.ReclassifySupportAssetsAsync(excludeModelIds: new HashSet<int> { id })).Should().Be(0);
        (await LoadTypeAsync(id)).Should().Be(ModelType.LORA);
        (await LoadHeaderCheckedAtAsync(id)).Should().BeNull("an excluded row was not examined, so it has not been asked");
    }

    /// <summary>
    /// Writes a real container to disk and seeds the row that points at it. <paramref name="rawBytes"/>
    /// overrides <paramref name="headerJson"/> for the malformed-file case.
    /// </summary>
    private async Task<int> GivenRowAsync(
        string fileName,
        string? headerJson = null,
        byte[]? rawBytes = null,
        DataSource source = DataSource.CivitaiApi,
        SyncOutcome outcome = SyncOutcome.Matched,
        bool withSyncState = true,
        bool isUserEdited = false,
        DateTimeOffset? headerCheckedAt = null)
    {
        var path = Path.Combine(_folder, fileName);
        await File.WriteAllBytesAsync(path, rawBytes ?? Safetensors(headerJson!));

        var model = new Model
        {
            Name = Path.GetFileNameWithoutExtension(fileName),
            Type = ModelType.LORA,
            Source = source,
            IsUserEdited = isUserEdited,
        };
        var version = new ModelVersion { Name = "v1", BaseModelRaw = "???", Model = model };
        version.Files.Add(new ModelFile
        {
            FileName = fileName,
            LocalPath = path,
            IsPrimary = true,
            ModelVersion = version,
        });
        model.Versions.Add(version);

        if (withSyncState)
            model.SyncState = new ModelSyncState { MetadataOutcome = outcome, HeaderCheckedAt = headerCheckedAt };

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();
        return model.Id;
    }

    /// <summary>
    /// Re-reads through a fresh scope, so assertions prove what round-tripped through the real
    /// Type-as-string column rather than what the tracked instance still holds.
    /// </summary>
    private async Task<ModelType> LoadTypeAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return (await uow.Models.GetByIdAsync(id))!.Type;
    }

    private async Task<ModelSyncState?> LoadStateAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await uow.SyncStates.GetByModelIdAsync(id);
    }

    private async Task<DateTimeOffset?> LoadHeaderCheckedAtAsync(int id)
        => (await LoadStateAsync(id))?.HeaderCheckedAt;
}
