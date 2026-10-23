using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Service.Services.Sync;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service;

/// <summary>
/// Covers <see cref="BaseModelWriter"/> directly — until now it was only exercised indirectly
/// through <c>IdentifyModelStepTests</c> and <c>SidecarMetadataApplierTests</c>.
/// </summary>
public class BaseModelWriterTests
{
    private static ModelVersion NewVersion(string? baseModelRaw, bool isUserEdited = false) =>
        new() { BaseModelRaw = baseModelRaw, IsUserEdited = isUserEdited };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    public void CanFill_TrueForAPlaceholderThatIsNotUserEdited(string? baseModelRaw)
    {
        BaseModelWriter.CanFill(NewVersion(baseModelRaw)).Should().BeTrue();
    }

    [Fact]
    public void CanFill_FalseWhenTheVersionIsUserEdited_EvenIfStillAPlaceholder()
    {
        BaseModelWriter.CanFill(NewVersion(null, isUserEdited: true)).Should().BeFalse();
    }

    [Fact]
    public void CanFill_FalseWhenARealBaseModelIsAlreadyStored()
    {
        BaseModelWriter.CanFill(NewVersion("SDXL 1.0")).Should().BeFalse();
    }

    /// <summary>
    /// B3. <c>CanFill</c> hand-wrote the same rule <see cref="SyncStateDeriver.IsPlaceholder"/>
    /// already states — this pins that the two are literally the same rule, not two copies that
    /// merely happen to agree today.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    [InlineData("SDXL 1.0")]
    public void CanFill_AgreesWithIsPlaceholder_ForAVersionThatIsNotUserEdited(string? baseModelRaw)
    {
        BaseModelWriter.CanFill(NewVersion(baseModelRaw)).Should().Be(SyncStateDeriver.IsPlaceholder(baseModelRaw));
    }

    [Fact]
    public void Write_BlankIsAMissingAnswer_NothingWritten()
    {
        var version = NewVersion("Pony");

        BaseModelWriter.Write(version, "  ").Should().BeFalse();

        version.BaseModelRaw.Should().Be("Pony");
    }

    [Fact]
    public void Write_WritesTheRawBaseModel()
    {
        var version = NewVersion(null);

        BaseModelWriter.Write(version, "SDXL 1.0").Should().BeTrue();

        version.BaseModelRaw.Should().Be("SDXL 1.0");
    }
}
