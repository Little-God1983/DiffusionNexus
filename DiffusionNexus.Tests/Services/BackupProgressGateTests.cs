using DiffusionNexus.Service.Services;

namespace DiffusionNexus.Tests.Services;

public class BackupProgressGateTests
{
    [Fact]
    public void FirstReport_AlwaysPasses()
    {
        var gate = new BackupProgressGate();
        Assert.True(gate.ShouldReport(5, "Creating backup"));
    }

    [Fact]
    public void SamePercentAndPhase_IsSuppressed()
    {
        var gate = new BackupProgressGate();
        gate.ShouldReport(42, "Creating backup");
        Assert.False(gate.ShouldReport(42, "Creating backup"));
    }

    [Fact]
    public void PercentChange_Passes()
    {
        var gate = new BackupProgressGate();
        gate.ShouldReport(42, "Creating backup");
        Assert.True(gate.ShouldReport(43, "Creating backup"));
    }

    [Fact]
    public void PhaseChange_PassesEvenAtSamePercent()
    {
        var gate = new BackupProgressGate();
        gate.ShouldReport(98, "Creating backup");
        Assert.True(gate.ShouldReport(98, "Cleaning up old backups"));
    }

    [Fact]
    public void ManySmallFileUpdates_CollapseToAtMostOnePerPercent()
    {
        var gate = new BackupProgressGate();
        var emitted = 0;
        for (var file = 1; file <= 100_000; file++)
        {
            var percent = 5 + (int)(90.0 * file / 100_000);
            if (gate.ShouldReport(percent, "Creating backup")) emitted++;
        }
        Assert.InRange(emitted, 1, 91);
    }
}
