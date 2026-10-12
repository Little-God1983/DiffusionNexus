using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public sealed class EngineFeatureRemovalTests : IDisposable
{
    private readonly string _root;
    private readonly Mock<ICatalog> _catalog = new();
    private readonly Mock<IConfigurationCheckerService> _checker = new();
    private readonly Mock<IDialogService> _dialog = new();
    private readonly Mock<IRemovalFileSystem> _fs = new();
    private readonly Mock<IUnifiedLogger> _log = new();
    private readonly Dictionary<Guid, ConfigurationCheckResult> _state = new();
    private readonly List<string> _userLibraries = [];
    private readonly string _gguf, _vae, _unet;

    public EngineFeatureRemovalTests()
    {
        _root = Directory.CreateTempSubdirectory("dn-feature-remove-").FullName;
        _gguf = Path.Combine(_root, "custom_nodes", "ComfyUI-GGUF");
        _vae = Path.Combine(_root, "models", "vae", "qwen_image_vae.safetensors");
        _unet = Path.Combine(_root, "models", "diffusion_models", "q.gguf");
        Directory.CreateDirectory(_gguf);
        Directory.CreateDirectory(Path.GetDirectoryName(_vae)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_unet)!);
        File.WriteAllText(_vae, "x");
        File.WriteAllText(_unet, "x");

        foreach (var id in EngineFeatureCatalog.AllWorkloadIds)
        {
            _catalog.Setup(c => c.GetWorkloadAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InstallationConfiguration { Id = id, Name = id.ToString() });
            _state[id] = Missing();
        }
        _checker.Setup(c => c.CheckConfigurationAsync(It.IsAny<InstallationConfiguration>(), _root,
                It.IsAny<ConfigurationCheckOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstallationConfiguration c, string _, ConfigurationCheckOptions? _, CancellationToken _) => _state[c.Id]);

        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result([_gguf], [_vae, _unet]);
    }

    public void Dispose()
    {
        foreach (var folder in _userLibraries.Append(_root))
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
        }
    }

    private static ConfigurationCheckResult Result(string[] nodes, string[] models) => new()
    {
        OverallStatus = ConfigurationStatus.Full, CustomNodesStatus = ConfigurationStatus.Full,
        ModelsStatus = ConfigurationStatus.Full, InstallationType = ComfyUIInstallationType.Manual,
        CustomNodeResults = nodes.Select(n => new CustomNodeCheckResult
            { Id = Guid.NewGuid(), Name = Path.GetFileName(n), Url = "u", IsInstalled = true, ExpectedPath = n }).ToList(),
        ModelResults = models.Select(m => new ModelCheckResult
            {
                Id = Guid.NewGuid(), Name = Path.GetFileName(m), IsInstalled = true, SearchedPaths = [], FoundAtPath = m, AllFoundPaths = [m],
                Category = Path.GetFileName(Path.GetDirectoryName(m)) // each file sits in its category folder
            }).ToList()
    };

    // Nothing on disk. Not Result([], []): a check with no items reads as Installed (nothing missing).
    private static ConfigurationCheckResult Missing() => Result([], []) with
    {
        ModelResults = [new ModelCheckResult { Id = Guid.NewGuid(), Name = "m", IsInstalled = false, SearchedPaths = [] }]
    };

    /// <summary>A model in one of the user's own folders, outside the Engine; deleted with the test.</summary>
    private string UserCopy()
    {
        var library = Directory.CreateTempSubdirectory("dn-user-lib-").FullName;
        _userLibraries.Add(library);
        var path = Path.Combine(library, "vae", "ae.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private EngineFeaturesViewModel Sut() =>
        new(_catalog.Object, _checker.Object, new Mock<IWorkloadInstallService>().Object, _root,
            unifiedLogger: _log.Object, freeSpaceProbe: _ => 0, dialogs: _dialog.Object,
            remover: new EngineRemover(null, null, fileSystem: _fs.Object));

    private void Answer(int index) =>
        _dialog.Setup(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>())).ReturnsAsync(index);

    private static EngineFeatureRowViewModel Row(EngineFeaturesViewModel vm, EngineFeature f) =>
        vm.Rows.Single(r => r.Definition.Feature == f);

    [Fact]
    public async Task Remove_DeletesItsNodePacksAndModels_AndRechecks()
    {
        Answer(1);
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);
        var row = Row(vm, EngineFeature.InpaintOutpaint);
        row.CanRemove.Should().BeTrue();
        _fs.Setup(f => f.DeleteFile(It.IsAny<string>()))
            .Callback(() => _state[EngineFeatureCatalog.InpaintingQwen2512] = Missing());

        await vm.RemoveFeatureCommand.ExecuteAsync(row);

        _fs.Verify(f => f.DeleteDirectory(_gguf), Times.Once);
        _fs.Verify(f => f.DeleteFile(_vae), Times.Once);
        _fs.Verify(f => f.DeleteFile(_unet), Times.Once);
        vm.DidRemove.Should().BeTrue();
        row.Status.Should().Be(EngineFeatureStatus.NotInstalled);
    }

    [Fact]
    public async Task Remove_KeepsWhatAnotherInstalledFeatureUses()
    {
        // Outpaint Vision has a model of its own, so it is installed on its own and needs the shared files.
        _state[EngineFeatureCatalog.OutpaintingQwen2512] =
            Result([_gguf], [_vae, Path.Combine(_root, "models", "text_encoders", "Qwen3-VL-8B.gguf")]);
        Answer(1);
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.RemoveFeatureCommand.ExecuteAsync(Row(vm, EngineFeature.InpaintOutpaint));

        _fs.Verify(f => f.DeleteDirectory(_gguf), Times.Never);
        _fs.Verify(f => f.DeleteFile(_vae), Times.Never);
        _fs.Verify(f => f.DeleteFile(_unet), Times.Once);
        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(),
            It.Is<string>(m => m.Contains("used by Outpaint Vision")), It.IsAny<string[]>()));
    }

    [Fact]
    public async Task Remove_EngineOnly_LeavesTheUsersCopy()
    {
        var userCopy = UserCopy();
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result([], [_unet, userCopy]);
        Answer(2); // "Delete only what's in the Diffusion Nexus Engine"
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.RemoveFeatureCommand.ExecuteAsync(Row(vm, EngineFeature.InpaintOutpaint));

        _fs.Verify(f => f.DeleteFile(_unet), Times.Once);
        _fs.Verify(f => f.DeleteFile(userCopy), Times.Never);
    }

    [Fact]
    public async Task Remove_Everything_AlsoDeletesTheUsersCopy()
    {
        var userCopy = UserCopy();
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result([], [_unet, userCopy]);
        Answer(1); // "Delete all models of this feature, wherever they are"
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.RemoveFeatureCommand.ExecuteAsync(Row(vm, EngineFeature.InpaintOutpaint));

        _fs.Verify(f => f.DeleteFile(userCopy), Times.Once);
    }

    // Minor: while the removal plan's check runs, the dialog says what it is doing.
    [Fact]
    public async Task WhilePlanning_TheProgressSaysTheFilesAreChecked()
    {
        Answer(0);
        EngineFeaturesViewModel? vm = null;
        string? seen = null;
        _checker.Setup(c => c.CheckConfigurationAsync(It.IsAny<InstallationConfiguration>(), _root,
                It.Is<ConfigurationCheckOptions?>(o => o != null && o.CollectAllMatches), It.IsAny<CancellationToken>()))
            .Callback(() => seen ??= vm?.ProgressText)
            .ReturnsAsync((InstallationConfiguration c, string _, ConfigurationCheckOptions? _, CancellationToken _) => _state[c.Id]);
        vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.RemoveFeatureCommand.ExecuteAsync(Row(vm, EngineFeature.InpaintOutpaint));

        seen.Should().Be("Checking Inpaint & Outpaint's files…");
    }

    [Fact]
    public async Task Cancel_DeletesNothing()
    {
        Answer(0);
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.RemoveFeatureCommand.ExecuteAsync(Row(vm, EngineFeature.InpaintOutpaint));

        _fs.VerifyNoOtherCalls();
        vm.DidRemove.Should().BeFalse();
    }

    // Review focus 5: one locked file is named; the rest still goes; the row re-checks to Partial.
    [Fact]
    public async Task ALockedFile_IsNamed_TheRestIsDeleted_AndTheRowIsPartial()
    {
        Answer(1);
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);
        _fs.Setup(f => f.DeleteFile(_vae)).Throws(new IOException("in use by another process"));
        _fs.Setup(f => f.DeleteFile(_unet))
            .Callback(() => _state[EngineFeatureCatalog.InpaintingQwen2512] = Result([], [_vae]) with
            {
                ModelResults = [new ModelCheckResult { Id = Guid.NewGuid(), Name = "q", IsInstalled = false, SearchedPaths = [] },
                                new ModelCheckResult { Id = Guid.NewGuid(), Name = "v", IsInstalled = true, SearchedPaths = [], FoundAtPath = _vae, AllFoundPaths = [_vae] }]
            });

        await vm.RemoveFeatureCommand.ExecuteAsync(Row(vm, EngineFeature.InpaintOutpaint));

        _fs.Verify(f => f.DeleteFile(_unet), Times.Once);
        _dialog.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains(_vae) && m.Contains("in use"))));
        Row(vm, EngineFeature.InpaintOutpaint).Status.Should().Be(EngineFeatureStatus.Partial);
    }

    [Fact]
    public async Task NotInstalledRow_CannotBeRemoved()
    {
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);

        var row = Row(vm, EngineFeature.BatchUpscale);
        row.CanRemove.Should().BeFalse();
        vm.RemoveFeatureCommand.CanExecute(row).Should().BeFalse();
    }

    // Runtime checks fall through, never block: a failing removal check changes nothing and says so.
    [Fact]
    public async Task AFailingRemovalCheck_ChangesNothing_AndSaysTheFilesCouldNotBeChecked()
    {
        Answer(1);
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);
        var row = Row(vm, EngineFeature.InpaintOutpaint);
        // Only the removal planner's check (every match collected) fails; the dialog's own re-check still works.
        _checker.Setup(c => c.CheckConfigurationAsync(It.IsAny<InstallationConfiguration>(), _root,
                It.Is<ConfigurationCheckOptions?>(o => o != null && o.CollectAllMatches), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("catalog is corrupt"));

        await vm.RemoveFeatureCommand.ExecuteAsync(row);

        _fs.VerifyNoOtherCalls();
        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>()), Times.Never);
        _dialog.Verify(d => d.ShowMessageAsync(EngineRemovalMessages.FeatureTitle,
            It.Is<string>(m => m.Contains("could not be checked") && m.Contains("catalog is corrupt"))));
        _log.Verify(l => l.Warn(LogCategory.Installation, "Diffusion Nexus Engine",
            It.Is<string>(m => m.Contains("could not be checked")), It.IsAny<string?>()));
        vm.DidRemove.Should().BeFalse();
        vm.IsRemoving.Should().BeFalse();
        row.Status.Should().Be(EngineFeatureStatus.Installed);
    }

    // A feature whose workload the catalog lacks has unknown files: it could share any of the target's,
    // so nothing is deleted and the user is told which feature and what to do.
    [Fact]
    public async Task AnotherFeaturesWorkloadMissingFromTheCatalog_DeletesNothing_AndNamesTheFeature()
    {
        Answer(1);
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);
        var row = Row(vm, EngineFeature.InpaintOutpaint);
        _catalog.Setup(c => c.GetWorkloadAsync(EngineFeatureCatalog.OutpaintingQwen2512, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstallationConfiguration?)null);

        await vm.RemoveFeatureCommand.ExecuteAsync(row);

        _fs.VerifyNoOtherCalls();
        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>()), Times.Never);
        _dialog.Verify(d => d.ShowMessageAsync(EngineRemovalMessages.FeatureTitle,
            "The catalog has no description of Outpaint Vision, so the app cannot tell which files Outpaint Vision shares. " +
            "Update the catalog or switch its channel, then try again. Uninstall Engine still works."));
        _log.Verify(l => l.Warn(LogCategory.Installation, "Diffusion Nexus Engine",
            It.Is<string>(m => m.Contains("Outpaint Vision") && m.Contains("catalog")), It.IsAny<string?>()));
        vm.DidRemove.Should().BeFalse();
    }

    // Only the user's own copies, and "only what's in the Engine": nothing to delete, the remover is not called.
    [Fact]
    public async Task EngineOnly_WithNothingInTheEngine_LeavesEverything_AndSaysSo()
    {
        var userCopy = UserCopy();
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result([], [userCopy]);
        Answer(2); // "Delete only what's in the Diffusion Nexus Engine"
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.RemoveFeatureCommand.ExecuteAsync(Row(vm, EngineFeature.InpaintOutpaint));

        _dialog.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.Is<string[]>(o => o.SequenceEqual(EngineRemovalMessages.FeatureOptions(true)))));
        _fs.VerifyNoOtherCalls();
        const string expected = "Nothing of Inpaint & Outpaint is in the Engine; your own files were left as they are.";
        vm.ProgressText.Should().Be(expected);
        _log.Verify(l => l.Info(LogCategory.Installation, "Diffusion Nexus Engine", expected, It.IsAny<string?>()));
        vm.DidRemove.Should().BeFalse();
    }
}
