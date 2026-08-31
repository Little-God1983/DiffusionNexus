using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Views.Dialogs;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the toolbar "Download Lora" path's gated-version handling: the dialog result
/// must map into the same preflight subject the detail panel uses, so all three download
/// surfaces (browser, detail panel, URL dialog) share one EA/paywall rule.
/// </summary>
public class LoraViewerGatedDownloadTests
{
    private static readonly DateTimeOffset Now =
        new(DateTime.UtcNow.Date.AddHours(10), TimeSpan.Zero);

    private static DownloadLoraResult Result(CivitaiModelVersion version) => new()
    {
        Confirmed = true,
        ModelName = "Dialog LoRA",
        ModelId = 555,
        Category = "Style",
        IsNsfw = true,
        Version = version,
        DownloadUrl = version.DownloadUrl,
        FileName = "dialog.safetensors",
        TargetFolder = @"C:\Loras"
    };

    [Fact]
    public void GatedSubjectFrom_MapsTheDialogResultIntoThePreflightSubject()
    {
        var version = new CivitaiModelVersion
        {
            Id = 9,
            ModelId = 123,
            Name = "v9",
            BaseModel = "Krea 2",
            EarlyAccessDeadline = Now.AddDays(7)
        };

        var subject = LoraViewerViewModel.GatedSubjectFrom(Result(version));

        subject.ModelId.Should().Be(123, "the version DTO names its own model");
        subject.ModelName.Should().Be("Dialog LoRA");
        subject.VersionLabel.Should().Be("v9");
        subject.Version.Should().BeSameAs(version);
        subject.Category.Should().Be("Style");
        subject.IsNsfw.Should().BeTrue();
    }

    [Fact]
    public void GatedSubjectFrom_FallsBackToTheDialogsModelIdAndBaseModelLabel()
    {
        // Older payloads can omit modelId on the version, and a version may carry no name.
        var version = new CivitaiModelVersion
        {
            Id = 10,
            ModelId = 0,
            Name = "",
            BaseModel = "Krea 2",
            EarlyAccessDeadline = Now.AddDays(7)
        };

        var subject = LoraViewerViewModel.GatedSubjectFrom(Result(version));

        subject.ModelId.Should().Be(555, "the dialog resolved the model and its id survives in the result");
        subject.VersionLabel.Should().Be("Krea 2", "the tab-label rule: version name, else base model");
    }
}
