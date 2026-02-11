using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Unit tests for <see cref="ColorToolsViewModel"/>.
/// Tests property state, slider clamping, range switching, and event raising.
/// </summary>
public class ColorToolsViewModelTests
{
    private readonly List<string> _deactivatedTools = [];
    private readonly ColorToolsViewModel _sut;

    public ColorToolsViewModelTests()
    {
        _sut = new ColorToolsViewModel(
            hasImage: () => true,
            deactivateOtherTools: tool => _deactivatedTools.Add(tool));
    }

    #region Constructor

    [Fact]
    public void WhenCreated_DefaultStateIsCorrect()
    {
        _sut.IsColorBalancePanelOpen.Should().BeFalse();
        _sut.IsBrightnessContrastPanelOpen.Should().BeFalse();
        _sut.Brightness.Should().Be(0);
        _sut.Contrast.Should().Be(0);
        _sut.ColorBalanceCyanRed.Should().Be(0);
        _sut.IsMidtonesSelected.Should().BeTrue();
        _sut.PreserveLuminosity.Should().BeTrue();
        _sut.HasColorBalanceAdjustments.Should().BeFalse();
        _sut.HasBrightnessContrastAdjustments.Should().BeFalse();
    }

    [Fact]
    public void WhenCreatedWithNullHasImage_ThrowsArgumentNullException()
    {
        var act = () => new ColorToolsViewModel(null!, _ => { });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenCreatedWithNullDeactivateOtherTools_ThrowsArgumentNullException()
    {
        var act = () => new ColorToolsViewModel(() => true, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Color Balance Panel

    [Fact]
    public void WhenColorBalancePanelOpened_DeactivateOtherToolsIsCalled()
    {
        _sut.IsColorBalancePanelOpen = true;

        _deactivatedTools.Should().Contain(nameof(ColorToolsViewModel.IsColorBalancePanelOpen));
    }

    [Fact]
    public void WhenColorBalancePanelClosed_CancelPreviewEventIsRaised()
    {
        _sut.IsColorBalancePanelOpen = true;

        var cancelRaised = false;
        _sut.CancelColorBalancePreviewRequested += (_, _) => cancelRaised = true;

        _sut.IsColorBalancePanelOpen = false;

        cancelRaised.Should().BeTrue();
    }

    [Fact]
    public void WhenColorBalancePanelClosed_SlidersAreReset()
    {
        _sut.IsColorBalancePanelOpen = true;
        _sut.ColorBalanceCyanRed = 50;

        _sut.IsColorBalancePanelOpen = false;

        _sut.ColorBalanceCyanRed.Should().Be(0);
    }

    #endregion

    #region Color Balance Range Selection

    [Fact]
    public void WhenShadowsSelected_ColorBalanceSlidersReflectShadows()
    {
        _sut.IsShadowsSelected = true;
        _sut.ColorBalanceCyanRed = 25;

        _sut.IsMidtonesSelected = true;
        _sut.ColorBalanceCyanRed.Should().Be(0);

        _sut.IsShadowsSelected = true;
        _sut.ColorBalanceCyanRed.Should().Be(25);
    }

    [Fact]
    public void WhenHighlightsSelected_ColorBalanceSlidersReflectHighlights()
    {
        _sut.IsHighlightsSelected = true;
        _sut.ColorBalanceMagentaGreen = -30;

        _sut.IsMidtonesSelected = true;
        _sut.ColorBalanceMagentaGreen.Should().Be(0);

        _sut.IsHighlightsSelected = true;
        _sut.ColorBalanceMagentaGreen.Should().Be(-30);
    }

    #endregion

    #region Color Balance Slider Clamping

    [Fact]
    public void WhenCyanRedSetAboveMax_ValueIsClampedTo100()
    {
        _sut.ColorBalanceCyanRed = 200;
        _sut.ColorBalanceCyanRed.Should().Be(100);
    }

    [Fact]
    public void WhenCyanRedSetBelowMin_ValueIsClampedToNegative100()
    {
        _sut.ColorBalanceCyanRed = -200;
        _sut.ColorBalanceCyanRed.Should().Be(-100);
    }

    [Fact]
    public void WhenYellowBlueSet_HasColorBalanceAdjustmentsIsTrue()
    {
        _sut.ColorBalanceYellowBlue = 10;
        _sut.HasColorBalanceAdjustments.Should().BeTrue();
    }

    #endregion

    #region Color Balance Preview

    [Fact]
    public void WhenSliderChangedWhilePanelOpen_PreviewEventIsRaised()
    {
        _sut.IsColorBalancePanelOpen = true;

        ColorBalanceSettings? received = null;
        _sut.ColorBalancePreviewRequested += (_, settings) => received = settings;

        _sut.ColorBalanceCyanRed = 10;

        received.Should().NotBeNull();
        received!.MidtonesCyanRed.Should().Be(10);
    }

    #endregion

    #region Color Balance Apply

    [Fact]
    public void WhenApplyColorBalanceExecuted_ApplyEventIsRaised()
    {
        _sut.IsColorBalancePanelOpen = true;
        _sut.ColorBalanceCyanRed = 20;

        ColorBalanceSettings? applied = null;
        _sut.ApplyColorBalanceRequested += (_, settings) => applied = settings;

        _sut.ApplyColorBalanceCommand.Execute(null);

        applied.Should().NotBeNull();
        applied!.MidtonesCyanRed.Should().Be(20);
    }

    [Fact]
    public void WhenNoAdjustments_ApplyColorBalanceCommandCannotExecute()
    {
        _sut.IsColorBalancePanelOpen = true;
        _sut.ApplyColorBalanceCommand.CanExecute(null).Should().BeFalse();
    }

    #endregion

    #region Color Balance Reset

    [Fact]
    public void WhenResetColorBalanceRangeExecuted_CurrentRangeIsReset()
    {
        _sut.IsColorBalancePanelOpen = true;
        _sut.ColorBalanceCyanRed = 30;
        _sut.ColorBalanceMagentaGreen = -20;

        _sut.ResetColorBalanceRangeCommand.Execute(null);

        _sut.ColorBalanceCyanRed.Should().Be(0);
        _sut.ColorBalanceMagentaGreen.Should().Be(0);
    }

    #endregion

    #region Brightness/Contrast Panel

    [Fact]
    public void WhenBrightnessContrastPanelOpened_DeactivateOtherToolsIsCalled()
    {
        _sut.IsBrightnessContrastPanelOpen = true;

        _deactivatedTools.Should().Contain(nameof(ColorToolsViewModel.IsBrightnessContrastPanelOpen));
    }

    [Fact]
    public void WhenBrightnessContrastPanelClosed_SlidersAreReset()
    {
        _sut.IsBrightnessContrastPanelOpen = true;
        _sut.Brightness = 50;
        _sut.Contrast = -30;

        _sut.IsBrightnessContrastPanelOpen = false;

        _sut.Brightness.Should().Be(0);
        _sut.Contrast.Should().Be(0);
    }

    #endregion

    #region Brightness/Contrast Slider Clamping

    [Fact]
    public void WhenBrightnessSetAboveMax_ValueIsClampedTo100()
    {
        _sut.Brightness = 200;
        _sut.Brightness.Should().Be(100);
    }

    [Fact]
    public void WhenContrastSetBelowMin_ValueIsClampedToNegative100()
    {
        _sut.Contrast = -200;
        _sut.Contrast.Should().Be(-100);
    }

    [Fact]
    public void WhenBrightnessIsNonZero_HasBrightnessContrastAdjustmentsIsTrue()
    {
        _sut.Brightness = 10;
        _sut.HasBrightnessContrastAdjustments.Should().BeTrue();
    }

    #endregion

    #region Brightness/Contrast Apply

    [Fact]
    public void WhenApplyBrightnessContrastExecuted_ApplyEventIsRaised()
    {
        _sut.IsBrightnessContrastPanelOpen = true;
        _sut.Brightness = 25;
        _sut.Contrast = -10;

        BrightnessContrastSettings? applied = null;
        _sut.ApplyBrightnessContrastRequested += (_, settings) => applied = settings;

        _sut.ApplyBrightnessContrastCommand.Execute(null);

        applied.Should().NotBeNull();
        applied!.Brightness.Should().Be(25);
        applied.Contrast.Should().Be(-10);
    }

    #endregion

    #region CloseAllPanels

    [Fact]
    public void WhenCloseAllPanels_AllPanelsAreClosed()
    {
        _sut.IsColorBalancePanelOpen = true;
        _sut.IsBrightnessContrastPanelOpen = true;

        _sut.CloseAllPanels();

        _sut.IsColorBalancePanelOpen.Should().BeFalse();
        _sut.IsBrightnessContrastPanelOpen.Should().BeFalse();
    }

    #endregion

    #region OnColorBalanceApplied / OnBrightnessContrastApplied

    [Fact]
    public void WhenOnColorBalanceApplied_PanelIsClosed()
    {
        _sut.IsColorBalancePanelOpen = true;

        _sut.OnColorBalanceApplied();

        _sut.IsColorBalancePanelOpen.Should().BeFalse();
    }

    [Fact]
    public void WhenOnBrightnessContrastApplied_PanelIsClosed()
    {
        _sut.IsBrightnessContrastPanelOpen = true;

        _sut.OnBrightnessContrastApplied();

        _sut.IsBrightnessContrastPanelOpen.Should().BeFalse();
    }

    #endregion

    #region CurrentSettings

    [Fact]
    public void WhenColorBalanceSettingsRequested_AllRangesAreIncluded()
    {
        _sut.IsShadowsSelected = true;
        _sut.ColorBalanceCyanRed = 10;

        _sut.IsMidtonesSelected = true;
        _sut.ColorBalanceMagentaGreen = 20;

        _sut.IsHighlightsSelected = true;
        _sut.ColorBalanceYellowBlue = 30;

        var settings = _sut.CurrentColorBalanceSettings;

        settings.ShadowsCyanRed.Should().Be(10);
        settings.MidtonesMagentaGreen.Should().Be(20);
        settings.HighlightsYellowBlue.Should().Be(30);
        settings.PreserveLuminosity.Should().BeTrue();
    }

    #endregion
}
