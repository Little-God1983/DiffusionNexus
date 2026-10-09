using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// #606 smoke: the panels showed only a fun line and a thin bar, so a long first run looked hung; and a
/// Generate the view could not prepare (no mask painted, export failed) left the panel busy for good.
/// </summary>
public class EditorGenerateProgressTests
{
    private static IComfyUiClientProvider Provider() => new Mock<IComfyUiClientProvider>().Object;

    private static IFeatureReadinessService Ready()
    {
        var readiness = new Mock<IFeatureReadinessService>();
        readiness.Setup(r => r.CheckAsync(It.IsAny<Feature>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Feature f, CancellationToken _) => new FeatureReadinessResult
            {
                Feature = f, Backend = BackendKind.Engine, ActiveBackendName = "Diffusion Nexus Engine",
                IsBackendOnline = true, IsReady = true, MissingRequirements = [], Warnings = []
            });
        return readiness.Object;
    }

    private static async Task<InpaintingViewModel> Inpaint()
    {
        var vm = new InpaintingViewModel(() => true, _ => { }, Provider(), eventAggregator: null, Ready())
        {
            PositivePrompt = "a cat",
        };
        vm.IsPanelOpen = true;
        await Task.Delay(50); // the open-panel check is fire-and-forget
        return vm;
    }

    private static async Task<OutpaintingViewModel> Outpaint()
    {
        var vm = new OutpaintingViewModel(() => true, () => 512, () => 512, _ => { }, Provider(), Ready())
        {
            PositivePrompt = "a beach",
        };
        vm.IsPanelOpen = true;
        await Task.Delay(50);
        return vm;
    }

    [Fact]
    public async Task Inpaint_Generate_ShowsThePreparingStepOnTheStatusLine()
    {
        var vm = await Inpaint();

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.ProgressStepText.Should().StartWith("Preparing the image · 0:0");
    }

    [Fact]
    public async Task Inpaint_EndWithoutRun_ClearsBusyAndTheStatusLine()
    {
        var vm = await Inpaint();
        await vm.GenerateCommand.ExecuteAsync(null);

        vm.EndWithoutRun();

        vm.IsBusy.Should().BeFalse();
        vm.ProgressStepText.Should().BeNull();
        vm.GenerateCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Outpaint_Generate_ShowsThePreparingStepOnTheStatusLine()
    {
        var vm = await Outpaint();

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.IsBusy.Should().BeTrue();
        vm.ProgressStepText.Should().StartWith("Preparing the image · 0:0");
    }

    [Fact]
    public async Task Outpaint_EndWithoutRun_ClearsBusyAndTheStatusLine()
    {
        var vm = await Outpaint();
        await vm.GenerateCommand.ExecuteAsync(null);

        vm.EndWithoutRun();

        vm.IsBusy.Should().BeFalse();
        vm.ProgressStepText.Should().BeNull();
    }
}
