using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Panel state for the Move / Transform tool: mutual exclusion on open, fields synced from the
/// tool without echoing a request back, typed values forwarded as requests, Apply gated on a
/// transform, and the amber hint for ineligible layers and refused commits.
/// </summary>
public class LayerTransformViewModelTests
{
    private readonly List<string> _deactivated = [];
    private readonly LayerTransformViewModel _sut;

    public LayerTransformViewModelTests()
    {
        _sut = new LayerTransformViewModel(() => true, id => _deactivated.Add(id));
    }

    [Fact]
    public void Opening_DeactivatesOtherTools_RaisesToggle_AndStatus()
    {
        (string ToolId, bool IsActive)? toggled = null;
        string? status = null;
        var activated = 0;
        _sut.ToolToggled += (_, a) => toggled = a;
        _sut.StatusMessageChanged += (_, s) => status = s;
        _sut.ToolActivated += (_, _) => activated++;

        _sut.IsPanelOpen = true;

        _deactivated.Should().ContainSingle().Which.Should().Be(ToolIds.LayerTransform);
        toggled.Should().Be((ToolIds.LayerTransform, true));
        status.Should().Be(LayerTransformViewModel.StatusOnOpen);
        activated.Should().Be(1);
    }

    [Fact]
    public void UpdateFromTool_SetsFields_WithoutRaisingRequests()
    {
        _sut.IsPanelOpen = true;
        var requests = 0;
        _sut.PositionRequested += (_, _) => requests++;
        _sut.SizeRequested += (_, _) => requests++;
        _sut.RotationRequested += (_, _) => requests++;

        _sut.UpdateFromTool("Background", 10.4f, 20.6f, 300f, 200f, 12.34f, hasTransform: true);

        _sut.LayerName.Should().Be("Background");
        _sut.X.Should().Be(10);
        _sut.Y.Should().Be(21);
        _sut.Width.Should().Be(300);
        _sut.Height.Should().Be(200);
        _sut.RotationDegrees.Should().BeApproximately(12.3f, 0.01f);
        _sut.HasTransform.Should().BeTrue();
        requests.Should().Be(0);
    }

    [Fact]
    public void TypedValues_RaiseRequests()
    {
        _sut.IsPanelOpen = true;
        (float X, float Y)? pos = null; (float W, float H)? size = null; float? rot = null; bool? aspect = null;
        _sut.PositionRequested += (_, p) => pos = p;
        _sut.SizeRequested += (_, s) => size = s;
        _sut.RotationRequested += (_, r) => rot = r;
        _sut.KeepAspectChanged += (_, k) => aspect = k;
        _sut.UpdateFromTool("L", 0, 0, 100, 50, 0, false);

        _sut.X = 5;
        _sut.Width = 200;
        _sut.RotationDegrees = 90f;
        _sut.KeepAspect = false;

        pos.Should().Be((5f, 0f));
        size.Should().Be((200f, 50f));
        rot.Should().Be(90f);
        aspect.Should().Be(false);
    }

    [Fact]
    public void Apply_IsGatedOnHasTransform_AndRaisesApplyRequested()
    {
        _sut.IsPanelOpen = true;
        var applied = 0;
        _sut.ApplyRequested += (_, _) => applied++;
        _sut.ApplyCommand.CanExecute(null).Should().BeFalse();

        _sut.UpdateFromTool("L", 0, 0, 1, 1, 0, hasTransform: true);
        _sut.ApplyCommand.CanExecute(null).Should().BeTrue();
        _sut.ApplyCommand.Execute(null);
        applied.Should().Be(1);
    }

    [Fact]
    public void FlipsAndReset_RaiseRequests()
    {
        _sut.IsPanelOpen = true;
        var flips = new List<bool>();
        var resets = 0;
        _sut.FlipRequested += (_, h) => flips.Add(h);
        _sut.ResetRequested += (_, _) => resets++;

        _sut.FlipHorizontalCommand.Execute(null);
        _sut.FlipVerticalCommand.Execute(null);
        _sut.ResetCommand.Execute(null);

        flips.Should().Equal(true, false);
        resets.Should().Be(1);
    }

    [Fact]
    public void Ineligible_ShowsHint_AndClearsOnEligible()
    {
        _sut.IsPanelOpen = true;
        _sut.OnIneligible(LayerTransformEligibility.InpaintMask);
        _sut.IsHintVisible.Should().BeTrue();
        _sut.HintText.Should().Be(LayerTransformViewModel.IneligibleHintText);

        _sut.OnIneligible(LayerTransformEligibility.Ok);
        _sut.IsHintVisible.Should().BeFalse();
    }

    [Fact]
    public void ApplyFailed_ShowsReasonHint_AndApplied_ClearsIt()
    {
        _sut.IsPanelOpen = true;
        _sut.OnApplyFailed(LayerTransformFailure.TooLarge);
        _sut.HintText.Should().Be(LayerTransformViewModel.TooLargeHintText);
        _sut.OnApplyFailed(LayerTransformFailure.Allocation);
        _sut.HintText.Should().Be(LayerTransformViewModel.AllocationHintText);

        _sut.OnApplied();
        _sut.IsHintVisible.Should().BeFalse();
        _sut.IsPanelOpen.Should().BeTrue(); // the tool stays open after Apply
    }

    [Fact]
    public void Cancel_ClosesPanel_AndRaisesDeactivated()
    {
        _sut.IsPanelOpen = true;
        var deactivated = 0;
        _sut.ToolDeactivated += (_, _) => deactivated++;
        _sut.CancelCommand.Execute(null);
        _sut.IsPanelOpen.Should().BeFalse();
        deactivated.Should().Be(1);
    }
}
