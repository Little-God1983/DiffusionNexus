using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Diffusion;
using DiffusionNexus.UI.Services.Pipelines;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Regression tests for the "Installer Manager → Diffusion Nexus Core → workload does not
/// download its LoRAs" bug: Civitai LoRA downloads never wrote a <c>.civitai.info</c> sidecar,
/// so the readiness check (which matches LoRAs primarily by sidecar modelId) reported them
/// missing forever, while re-installing early-returned on the existing weights file without
/// downloading or healing anything.
/// </summary>
public sealed class PipelineAssetInstallerTests : IDisposable
{
    private readonly string _root;
    private readonly Mock<IDownloadCoordinator> _coordinator = new();
    private readonly Mock<ICivitaiClient> _civitai = new();
    private readonly Mock<IAppSettingsService> _settings = new();

    public PipelineAssetInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dn-pipeline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "loras"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private PipelineAssetInstaller CreateInstaller()
    {
        return new PipelineAssetInstaller(
            _coordinator.Object,
            _civitai.Object,
            new LoraDownloadService(null, null, null),
            _settings.Object,
            new LocalDiffusionBackendProvider(new Mock<IServiceProvider>().Object));
    }

    private static PipelineManifest ManifestWithLora(int modelId, string expectedFileName, string name = "Test LoRA")
        => new()
        {
            Id = "test-pipeline",
            Title = "Test Pipeline",
            Assets =
            [
                new PipelineAsset
                {
                    Name = name,
                    Kind = PipelineAssetKind.Lora,
                    TargetSubfolder = "loras",
                    ExpectedFileName = expectedFileName,
                    CivitaiModelId = modelId,
                },
            ],
        };

    private static CivitaiModel CivitaiModelWithFile(int versionId, string fileName, int modelId = 0)
        => new()
        {
            Id = 1934100,
            Name = "Anime2Real",
            ModelVersions =
            [
                new CivitaiModelVersion
                {
                    Id = versionId,
                    // Deliberately 0 by default: nested versions in /api/v1/models/{id}
                    // responses do not always carry the parent modelId.
                    ModelId = modelId,
                    Name = "Klein9B",
                    Files =
                    [
                        new CivitaiModelFile
                        {
                            Id = 1,
                            Name = fileName,
                            Primary = true,
                            DownloadUrl = $"https://civitai.com/api/download/models/{versionId}",
                        },
                    ],
                },
            ],
        };

    // ── Bug precondition (characterization): why the sidecar is required ──

    [Fact]
    public void BuildReadiness_DoesNotDetectLora_WhenWeightsExistWithoutSidecar()
    {
        // The real Civitai filename shares no substring with the manifest hint
        // (e.g. hint "anime2real" vs file "A2R_Klein_Standard.safetensors").
        File.WriteAllBytes(Path.Combine(_root, "loras", "A2R_Klein_Standard.safetensors"), [0x1]);

        var readiness = PipelineAssetInstaller.BuildReadiness(
            ManifestWithLora(1934100, "anime2real"), [_root]);

        readiness.Assets.Should().ContainSingle().Which.IsPresent.Should().BeFalse(
            "without a sidecar, neither the modelId scan nor the filename hint can match the file");
    }

    // ── The fix: sidecar written on download makes the LoRA detectable ──

    [Fact]
    public void WriteCivitaiInfoSidecar_MakesLoraDetectable_ByReadiness()
    {
        var weights = Path.Combine(_root, "loras", "A2R_Klein_Standard.safetensors");
        File.WriteAllBytes(weights, [0x1]);
        var version = CivitaiModelWithFile(2674717, "A2R_Klein_Standard.safetensors").ModelVersions[0];

        PipelineAssetInstaller.WriteCivitaiInfoSidecar(weights, version, 1934100);

        var readiness = PipelineAssetInstaller.BuildReadiness(
            ManifestWithLora(1934100, "anime2real"), [_root]);

        readiness.IsComplete.Should().BeTrue();
        readiness.Assets[0].ResolvedFileName.Should().Be("A2R_Klein_Standard.safetensors");
    }

    [Fact]
    public void WriteCivitaiInfoSidecar_WritesManifestModelId_WhenVersionLacksModelId()
    {
        var weights = Path.Combine(_root, "loras", "A2R_Klein_Standard.safetensors");
        File.WriteAllBytes(weights, [0x1]);
        var version = CivitaiModelWithFile(2674717, "A2R_Klein_Standard.safetensors", modelId: 0).ModelVersions[0];

        PipelineAssetInstaller.WriteCivitaiInfoSidecar(weights, version, 1934100);

        var sidecar = Path.Combine(_root, "loras", "A2R_Klein_Standard.civitai.info");
        File.Exists(sidecar).Should().BeTrue();
        File.ReadAllText(sidecar).Should().Contain("\"modelId\": 1934100");
    }

    [Fact]
    public void BuildReadiness_ReportsLoraMissing_WhenSidecarExistsButWeightsWereDeleted()
    {
        var weights = Path.Combine(_root, "loras", "A2R_Klein_Standard.safetensors");
        File.WriteAllBytes(weights, [0x1]);
        var version = CivitaiModelWithFile(2674717, "A2R_Klein_Standard.safetensors").ModelVersions[0];
        PipelineAssetInstaller.WriteCivitaiInfoSidecar(weights, version, 1934100);
        File.Delete(weights);

        var readiness = PipelineAssetInstaller.BuildReadiness(
            ManifestWithLora(1934100, "anime2real"), [_root]);

        readiness.Assets.Should().ContainSingle().Which.IsPresent.Should().BeFalse(
            "a sidecar without its weights file must not count as installed");
    }

    // ── Self-heal: re-installing over existing weights writes the missing sidecar ──

    [Fact]
    public async Task InstallAssets_HealsMissingSidecar_WhenWeightsAlreadyExist()
    {
        // The user's stuck state: weights were downloaded previously (Civitai filename),
        // no sidecar → readiness says missing → Install runs again.
        var weights = Path.Combine(_root, "loras", "A2R_Klein_Standard.safetensors");
        File.WriteAllBytes(weights, [0x1]);

        _civitai.Setup(c => c.GetModelAsync(1934100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CivitaiModelWithFile(2674717, "A2R_Klein_Standard.safetensors"));

        var installer = CreateInstaller();
        var manifest = ManifestWithLora(1934100, "anime2real");

        var errors = await installer.InstallAssetsAsync(
            manifest, ["Test LoRA"], _root, vramGb: 0, hfToken: null, civitaiKey: null, CancellationToken.None);

        errors.Should().BeEmpty();
        _coordinator.Verify(
            c => c.EnqueueAsync(It.IsAny<string>(),
                It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "the weights already exist, so nothing should be re-downloaded");

        PipelineAssetInstaller.BuildReadiness(manifest, [_root]).IsComplete.Should().BeTrue(
            "re-installing must heal the missing sidecar so the asset stops showing as missing");
    }

    // ── Explicit download root (Base Model Folders) ──

    [Fact]
    public async Task InstallMissing_DownloadsIntoThePassedRoot_NotTheFirstSearchRoot()
    {
        // Two candidate roots exist; the caller (workload-window dropdown) picked the second.
        Directory.CreateDirectory(Path.Combine(_root, "first-root"));
        var chosenRoot = Path.Combine(_root, "chosen-root");

        _civitai.Setup(c => c.GetModelAsync(1934100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CivitaiModelWithFile(2674717, "A2R_Klein_Standard.safetensors", modelId: 1934100));
        _settings.Setup(s => s.GetHuggingfaceApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _settings.Setup(s => s.GetCivitaiApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        // Simulate a successful download without invoking the real download delegate.
        _coordinator.Setup(c => c.EnqueueAsync(It.IsAny<string>(),
                It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var installer = CreateInstaller();
        var manifest = ManifestWithLora(1934100, "anime2real");

        // New public contract: the caller chooses the download root explicitly —
        // no ComfyUI installation is required at all.
        await installer.InstallMissingAsync(manifest, vramGb: 0, chosenRoot, CancellationToken.None);

        // The sidecar written after the (mocked) download proves which root received the files.
        File.Exists(Path.Combine(chosenRoot, "loras", "A2R_Klein_Standard.civitai.info"))
            .Should().BeTrue("assets must land in the explicitly chosen download root");
        Directory.Exists(Path.Combine(_root, "first-root", "loras")).Should().BeFalse();
    }

    [Fact]
    public void BuildReadiness_FindsAssets_AcrossCatalogAndComfyRoots()
    {
        // Asset lives only in the second (ComfyUI) root — readiness must still see it.
        var catalogRoot = Path.Combine(_root, "catalog-root");
        Directory.CreateDirectory(Path.Combine(catalogRoot, "loras"));
        var comfyRoot = _root; // fixture root already contains loras/ from the constructor
        var weights = Path.Combine(comfyRoot, "loras", "A2R_Klein_Standard.safetensors");
        File.WriteAllBytes(weights, [0x1]);
        var version = CivitaiModelWithFile(2674717, "A2R_Klein_Standard.safetensors").ModelVersions[0];
        PipelineAssetInstaller.WriteCivitaiInfoSidecar(weights, version, 1934100);

        var readiness = PipelineAssetInstaller.BuildReadiness(
            ManifestWithLora(1934100, "anime2real"), [catalogRoot, comfyRoot]);

        readiness.IsComplete.Should().BeTrue();
    }

    // ── Per-asset isolation: one failing asset must not abort the rest ──

    [Fact]
    public async Task InstallAssets_ContinuesWithRemainingAssets_WhenOneDownloadFails()
    {
        var manifest = new PipelineManifest
        {
            Id = "test-pipeline",
            Title = "Test Pipeline",
            Assets =
            [
                new PipelineAsset
                {
                    Name = "Gated LoRA",
                    Kind = PipelineAssetKind.Lora,
                    TargetSubfolder = "loras",
                    ExpectedFileName = "gated",
                    CivitaiModelId = 2343188,
                },
                new PipelineAsset
                {
                    Name = "Public LoRA",
                    Kind = PipelineAssetKind.Lora,
                    TargetSubfolder = "loras",
                    ExpectedFileName = "public",
                    CivitaiModelId = 1934100,
                },
            ],
        };

        _civitai.Setup(c => c.GetModelAsync(2343188, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CivitaiModelWithFile(2635669, "Gated.safetensors"));
        _civitai.Setup(c => c.GetModelAsync(1934100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CivitaiModelWithFile(2674717, "Public.safetensors"));

        var enqueued = new List<string>();
        _coordinator.Setup(c => c.EnqueueAsync(It.IsAny<string>(),
                It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>, CancellationToken>(
                (name, _, _) => enqueued.Add(name))
            // Simulate the download failing (e.g. HTTP 401 because no Civitai API key is configured).
            .ReturnsAsync(false);

        var installer = CreateInstaller();

        var errors = await installer.InstallAssetsAsync(
            manifest, ["Gated LoRA", "Public LoRA"], _root, vramGb: 0, hfToken: null, civitaiKey: null,
            CancellationToken.None);

        enqueued.Should().HaveCount(2,
            "a failure on the first asset must not prevent the remaining assets from being attempted");
        errors.Should().HaveCount(2);
        errors[0].Should().StartWith("Gated LoRA:");
        errors[1].Should().StartWith("Public LoRA:");
    }
}
