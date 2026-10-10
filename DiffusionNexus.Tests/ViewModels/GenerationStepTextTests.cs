using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

public class GenerationStepTextTests
{
    private TimeSpan _now = TimeSpan.FromMinutes(5);

    private GenerationStepText Create() => new(() => _now);

    [Theory]
    [InlineData("Preparing image and mask...", "Preparing the image")]
    [InlineData("Starting Diffusion Nexus Engine…", "Starting the Diffusion Nexus Engine")]
    [InlineData("Restarting Diffusion Nexus Engine…", "Restarting the Diffusion Nexus Engine")]
    [InlineData("Uploading image to ComfyUI...", "Uploading the image")]
    [InlineData("Checking available models...", "Checking the models")]
    [InlineData("Queuing inpainting workflow...", "Sending the job")]
    [InlineData("Generating (this may take a while)...", "Loading the models (slow on the first run)")]
    [InlineData("Executing node 12...", "Loading the models (slow on the first run)")]
    [InlineData("Progress: 2/4", "Generating · step 2 of 4")]
    [InlineData("Downloading result...", "Downloading the result")]
    [InlineData("Describing the surroundings with Qwen3-VL...", "Describing the surroundings (Qwen3-VL)")]
    public void Describe_MapsEachPhase(string status, string expected)
    {
        var sampled = false;
        GenerationStepText.Describe(status, ref sampled).Should().Be(expected);
    }

    [Fact]
    public void Describe_ExecutingAfterSampling_IsFinishing_NotLoading()
    {
        var sampled = false;
        GenerationStepText.Describe("Progress: 4/4", ref sampled);

        GenerationStepText.Describe("Executing node 3...", ref sampled).Should().Be("Finishing the image");
    }

    [Fact]
    public void Describe_UnknownStatus_KeepsTheCurrentStep()
    {
        var sampled = false;
        GenerationStepText.Describe("something else", ref sampled).Should().BeNull();
    }

    [Fact]
    public void Text_ShowsTheStepAndTheElapsedTime()
    {
        var sut = Create();
        sut.Start();
        sut.Update("Progress: 1/4");

        _now += TimeSpan.FromSeconds(83);

        sut.Text.Should().Be("Generating · step 1 of 4 · 1:23");
    }

    [Fact]
    public void Text_IsNullBeforeStart_AndAfterStop()
    {
        var sut = Create();
        sut.Update("Uploading image to ComfyUI...");
        sut.Text.Should().BeNull();

        sut.Start();
        sut.Update("Uploading image to ComfyUI...");
        sut.Text.Should().StartWith("Uploading the image");

        sut.Stop();
        sut.Text.Should().BeNull();
    }

    [Fact]
    public void Start_ResetsTheSamplingState()
    {
        var sut = Create();
        sut.Start();
        sut.Update("Progress: 4/4");
        sut.Stop();

        sut.Start();
        sut.Update("Executing node 3...");

        sut.Text.Should().StartWith("Loading the models");
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59, "0:59")]
    [InlineData(600, "10:00")]
    [InlineData(3725, "1:02:05")]
    public void FormatElapsed_MinutesAndSeconds(int seconds, string expected)
        => GenerationStepText.FormatElapsed(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
}
