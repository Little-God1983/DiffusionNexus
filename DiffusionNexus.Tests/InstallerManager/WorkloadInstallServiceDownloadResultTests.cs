using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Tests for <see cref="WorkloadInstallService.TallyDownload"/> and
/// <see cref="WorkloadInstallService.DescribeDownload"/>, the mapping from the SDK's
/// <see cref="FileDownloadResult"/> onto this service's success/fail counters and
/// progress text.
///
/// SDK 1.2.34 replaced <c>DownloadSingleFileAsync</c>'s <c>bool</c> return with
/// <see cref="FileDownloadResult"/>. The bool conflated two pairs of distinct events:
/// downloaded vs. already-present (both <c>true</c>), and failed vs. skipped-by-user
/// (both <c>false</c>). The second conflation was a live bug — pressing Skip reported
/// "Failed to download X" and incremented the failure count.
/// </summary>
public class WorkloadInstallServiceDownloadResultTests
{
    private static FileDownloadResult Result(
        FileDownloadOutcome outcome, string? error = null, string? warning = null)
        => new() { Outcome = outcome, ErrorMessage = error, Warning = warning };

    #region TallyDownload

    [Theory]
    [InlineData(FileDownloadOutcome.Downloaded)]
    [InlineData(FileDownloadOutcome.AlreadyPresent)]
    public void WhenFilePresentThenCountsAsSuccess(FileDownloadOutcome outcome)
    {
        WorkloadInstallService.TallyDownload(Result(outcome)).Should().Be((1, 0));
    }

    [Fact]
    public void WhenDownloadFailedThenCountsAsFailure()
    {
        WorkloadInstallService.TallyDownload(Result(FileDownloadOutcome.Failed))
            .Should().Be((0, 1));
    }

    /// <summary>
    /// The regression this fix exists for: a user skip is neither a success nor a
    /// failure. Under the old bool return this asserted (0, 1).
    /// </summary>
    [Fact]
    public void WhenUserSkippedThenCountsAsNeitherSuccessNorFailure()
    {
        WorkloadInstallService.TallyDownload(Result(FileDownloadOutcome.SkippedByUser))
            .Should().Be((0, 0));
    }

    #endregion

    #region DescribeDownload

    [Fact]
    public void WhenDownloadedThenMessageSaysDownloaded()
    {
        WorkloadInstallService.DescribeDownload("flux.safetensors", Result(FileDownloadOutcome.Downloaded))
            .Should().Be("Downloaded flux.safetensors");
    }

    [Fact]
    public void WhenAlreadyPresentThenMessageDistinguishesFromFreshDownload()
    {
        WorkloadInstallService.DescribeDownload("flux.safetensors", Result(FileDownloadOutcome.AlreadyPresent))
            .Should().Be("flux.safetensors already present");
    }

    [Fact]
    public void WhenUserSkippedThenMessageSaysSkippedNotFailed()
    {
        WorkloadInstallService.DescribeDownload("flux.safetensors", Result(FileDownloadOutcome.SkippedByUser))
            .Should().Be("Skipped flux.safetensors");
    }

    [Fact]
    public void WhenFailedWithoutErrorMessageThenMessageIsBare()
    {
        WorkloadInstallService.DescribeDownload("flux.safetensors", Result(FileDownloadOutcome.Failed))
            .Should().Be("Failed to download flux.safetensors");
    }

    [Fact]
    public void WhenFailedWithErrorMessageThenMessageIncludesIt()
    {
        var result = Result(FileDownloadOutcome.Failed, error: "404 Not Found");

        WorkloadInstallService.DescribeDownload("flux.safetensors", result)
            .Should().Be("Failed to download flux.safetensors: 404 Not Found");
    }

    /// <summary>
    /// A size-verification warning is advisory — it annotates the message but must not
    /// turn a completed download into a failure.
    /// </summary>
    [Fact]
    public void WhenDownloadCarriesWarningThenWarningIsAppendedToSuccessMessage()
    {
        var result = Result(FileDownloadOutcome.Downloaded, warning: "size unverifiable");

        WorkloadInstallService.DescribeDownload("flux.safetensors", result)
            .Should().Be("Downloaded flux.safetensors (size unverifiable)");
    }

    #endregion
}
