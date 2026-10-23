using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Panel state for the Canvas Extend tool: mutual exclusion on open, Apply gated on an
/// actual extension, typed sizes clamped to the image with the shrink hint, multipliers
/// and presets forwarded as requests, and no echo when the tool reports back.
/// </summary>
public class CanvasExtendViewModelTests
{
    private readonly List<string> _deactivated = [];
    private readonly CanvasExtendViewModel _sut;

    public CanvasExtendViewModelTests()
    {
        _sut = new CanvasExtendViewModel(() => true, () => 1024, () => 768, id => _deactivated.Add(id));
    }

    [Fact]
    public void Opening_DeactivatesOtherTools_AndRaisesToggleForItsOwnId()
    {
        (string ToolId, bool IsActive)? toggled = null;
        _sut.ToolToggled += (_, args) => toggled = args;
        var activated = 0;
        _sut.ToolActivated += (_, _) => activated++;

        _sut.IsPanelOpen = true;

        _deactivated.Should().ContainSingle().Which.Should().Be(ToolIds.CanvasExtend);
        toggled.Should().Be((ToolIds.CanvasExtend, true));
        activated.Should().Be(1);
    }

    [Fact]
    public void Apply_IsDisabledUntilThereIsAnExtension()
    {
        _sut.IsPanelOpen = true;
        _sut.ApplyCommand.CanExecute(null).Should().BeFalse();

        _sut.UpdateResolution(2048, 768, hasExtension: true);

        _sut.ApplyCommand.CanExecute(null).Should().BeTrue();
        _sut.ResolutionText.Should().Be("2048 x 768");
        _sut.OriginalSizeText.Should().Be("from 1024 x 768");
    }

    [Fact]
    public void TypedWidth_AtOrAboveImage_RaisesTargetSizeRequested()
    {
        _sut.IsPanelOpen = true;
        _sut.UpdateResolution(1024, 768, hasExtension: false);
        (int Width, int Height)? requested = null;
        _sut.TargetSizeRequested += (_, args) => requested = args;

        _sut.TargetWidth = 1500;

        requested.Should().Be((1500, 768));
        _sut.IsShrinkHintVisible.Should().BeFalse();
    }

    [Fact]
    public void TypedWidth_BelowImage_ClampsShowsHint_AndRaisesNothingWhenNothingChanges()
    {
        _sut.IsPanelOpen = true;
        _sut.UpdateResolution(1024, 768, hasExtension: false);
        var requests = 0;
        _sut.TargetSizeRequested += (_, _) => requests++;

        _sut.TargetWidth = 800;

        _sut.TargetWidth.Should().Be(1024);
        _sut.IsShrinkHintVisible.Should().BeTrue();
        requests.Should().Be(0, "the axis already sits at the image size");
    }

    [Fact]
    public void TypedHeight_BelowImage_OnAnExtendedAxis_ClampsAndPushesTheClampToTheTool()
    {
        _sut.IsPanelOpen = true;
        _sut.UpdateResolution(1024, 1500, hasExtension: true); // height already extended
        (int Width, int Height)? requested = null;
        _sut.TargetSizeRequested += (_, args) => requested = args;

        _sut.TargetHeight = 500;

        _sut.TargetHeight.Should().Be(768);
        _sut.IsShrinkHintVisible.Should().BeTrue();
        requested.Should().Be((1024, 768), "the frame must follow the field, or the panel contradicts the canvas");
    }

    [Fact]
    public void Multiplier_UsesTheImageDimension_AndKeepsTheOtherTarget()
    {
        _sut.IsPanelOpen = true;
        _sut.UpdateResolution(1024, 900, hasExtension: true); // height already extended to 900
        (int Width, int Height)? requested = null;
        _sut.TargetSizeRequested += (_, args) => requested = args;

        _sut.MultiplyCommand.Execute("2xW");
        requested.Should().Be((2048, 900));

        _sut.MultiplyCommand.Execute("3xH");
        requested.Should().Be((1024, 2304)); // width target is still the reported 1024
    }

    [Fact]
    public void Multiplier_OneX_ReturnsThatAxisToTheImageSize_WithoutTheShrinkHint()
    {
        _sut.IsPanelOpen = true;
        _sut.UpdateResolution(2048, 2304, hasExtension: true); // 2x W and 3x H applied
        (int Width, int Height)? requested = null;
        _sut.TargetSizeRequested += (_, args) => requested = args;

        _sut.MultiplyCommand.Execute("1xW");
        requested.Should().Be((1024, 2304));

        _sut.MultiplyCommand.Execute("1xH");
        requested.Should().Be((2048, 768)); // width target is still the reported 2048
        _sut.IsShrinkHintVisible.Should().BeFalse("the image size itself is not a shrink");
    }

    [Fact]
    public void AspectPreset_IsForwarded()
    {
        _sut.IsPanelOpen = true;
        (float W, float H)? requested = null;
        _sut.SetAspectRatioRequested += (_, args) => requested = args;

        _sut.SetAspectRatioCommand.Execute("16:9");

        requested.Should().Be((16f, 9f));
    }

    [Fact]
    public void UpdateResolution_DoesNotEchoATargetSizeRequest()
    {
        _sut.IsPanelOpen = true;
        var requests = 0;
        _sut.TargetSizeRequested += (_, _) => requests++;

        _sut.UpdateResolution(1300, 768, hasExtension: true);

        _sut.TargetWidth.Should().Be(1300);
        requests.Should().Be(0);
    }

    [Fact]
    public void ShrinkHint_ClearsWhenTheCanvasGrows_AndOnApplied()
    {
        _sut.IsPanelOpen = true;
        _sut.UpdateResolution(1024, 768, hasExtension: false);
        _sut.OnShrinkAttempted();
        _sut.IsShrinkHintVisible.Should().BeTrue();

        _sut.UpdateResolution(1100, 768, hasExtension: true);
        _sut.IsShrinkHintVisible.Should().BeFalse();

        _sut.OnShrinkAttempted();
        _sut.OnApplied(1100, 768);

        _sut.IsShrinkHintVisible.Should().BeFalse();
        _sut.IsPanelOpen.Should().BeFalse();
    }

    [Fact]
    public void TypedWidth_WhileThePanelIsClosed_IsStoredWithoutRequestOrHint()
    {
        var requests = 0;
        _sut.TargetSizeRequested += (_, _) => requests++;

        _sut.TargetWidth = 800;

        _sut.TargetWidth.Should().Be(800);
        requests.Should().Be(0);
        _sut.IsShrinkHintVisible.Should().BeFalse();
    }

    [Fact]
    public void OnApplyFailed_LeavesThePanelOpen_SoTheUserCanTryASmallerSize()
    {
        _sut.IsPanelOpen = true;
        _sut.UpdateResolution(4096, 768, hasExtension: true);

        _sut.OnApplyFailed();

        _sut.IsPanelOpen.Should().BeTrue();
        _sut.HasExtension.Should().BeTrue();
        _sut.ResolutionText.Should().Be("4096 x 768");
    }

    [Fact]
    public void OpenCrop_RaisesRequest()
    {
        _sut.IsPanelOpen = true;
        var raised = 0;
        _sut.OpenCropRequested += (_, _) => raised++;

        _sut.OpenCropCommand.Execute(null);

        raised.Should().Be(1);
    }

    [Fact]
    public void Placement_StartsTopLeft_AndAGridClick_SelectsAndRaisesARequest()
    {
        _sut.SelectedAnchor.Should().Be(CanvasAnchor.TopLeft);
        _sut.IsPanelOpen = true;
        CanvasAnchor? requested = null;
        _sut.AnchorRequested += (_, anchor) => requested = anchor;

        _sut.SetAnchorCommand.Execute(CanvasAnchor.BottomRight);

        _sut.SelectedAnchor.Should().Be(CanvasAnchor.BottomRight);
        requested.Should().Be(CanvasAnchor.BottomRight);
    }

    [Fact]
    public void Placement_IsDisabledWhileThePanelIsClosed_AndNeverRequestsCustom()
    {
        _sut.SetAnchorCommand.CanExecute(CanvasAnchor.Center).Should().BeFalse();

        _sut.IsPanelOpen = true;
        var requests = 0;
        _sut.AnchorRequested += (_, _) => requests++;

        _sut.SetAnchorCommand.Execute(CanvasAnchor.Custom);

        requests.Should().Be(0);
        _sut.SelectedAnchor.Should().Be(CanvasAnchor.TopLeft);
    }

    [Fact]
    public void UpdateAnchor_FromTheTool_ClearsTheGridForCustom_WithoutEchoingARequest()
    {
        _sut.IsPanelOpen = true;
        var requests = 0;
        _sut.AnchorRequested += (_, _) => requests++;

        _sut.UpdateAnchor(CanvasAnchor.Custom);
        _sut.SelectedAnchor.Should().BeNull();

        _sut.UpdateAnchor(CanvasAnchor.Center);
        _sut.SelectedAnchor.Should().Be(CanvasAnchor.Center);

        requests.Should().Be(0);
    }

    [Fact]
    public void ClosePanel_RaisesDeactivated_WithoutTouchingOtherTools()
    {
        _sut.IsPanelOpen = true;
        _deactivated.Clear();
        var deactivatedEvents = 0;
        _sut.ToolDeactivated += (_, _) => deactivatedEvents++;

        _sut.ClosePanel();

        _sut.IsPanelOpen.Should().BeFalse();
        deactivatedEvents.Should().Be(1);
        _deactivated.Should().BeEmpty();
    }
}
