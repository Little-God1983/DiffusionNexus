using DiffusionNexus.UI.Services.Startup;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.Tests.ViewModels;

public class StartupOverlayViewModelTests
{
    [Fact]
    public void Rows_MirrorServiceChecks_InOrder()
    {
        var svc = new StartupProgressService(StartupProgressService.BuildDefaultChecks(false));
        var vm = new StartupOverlayViewModel(svc);
        Assert.Equal(svc.Checks.Select(c => c.DisplayName), vm.Rows.Select(r => r.DisplayName));
        Assert.All(vm.Rows, r => Assert.True(r.IsPending));
    }

    [Fact]
    public void CheckChanged_UpdatesTheMatchingRow_AndRaisesPropertyChanged()
    {
        var svc = new StartupProgressService(StartupProgressService.BuildDefaultChecks(false));
        var vm = new StartupOverlayViewModel(svc);
        var row = vm.Rows.Single(r => r.DisplayName == "Database");
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        svc.Begin("database");
        Assert.True(row.IsRunning);
        svc.Complete("database");
        Assert.True(row.IsDone);
        Assert.Contains(nameof(StartupCheckRowViewModel.IsRunning), raised);
        Assert.Contains(nameof(StartupCheckRowViewModel.IsDone), raised);
    }

    [Fact]
    public void FailedCheck_ExposesError()
    {
        var svc = new StartupProgressService(StartupProgressService.BuildDefaultChecks(false));
        var vm = new StartupOverlayViewModel(svc);
        svc.Begin("settings");
        svc.Fail("settings", "boom");
        var row = vm.Rows.Single(r => r.DisplayName == "Settings");
        Assert.True(row.IsFailed);
        Assert.Equal("boom", row.Error);
    }
}
