using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

/// <summary>
/// Card-level NSFW behavior: the preview thumbnail must respect the browser's
/// "Show NSFW content" toggle (the API returns every gallery image unfiltered
/// because we request nsfw=true), and card NSFW-ness comes from the nsfwLevel
/// policy, not the narrow model-level nsfw boolean.
/// </summary>
public class CivitaiResultViewModelNsfwTests
{
    private const string AdultUrl = "https://cdn.example/adult.jpeg";
    private const string SafeUrl = "https://cdn.example/safe.jpeg";

    private static CivitaiModel ModelWithAdultFirstImage() => new()
    {
        Id = 2782544,
        Name = "unflagged model with XXX first image",
        Nsfw = false,
        NsfwLevel = 26,
        ModelVersions =
        [
            new CivitaiModelVersion
            {
                Images =
                [
                    new CivitaiModelImage { Url = AdultUrl, NsfwLevel = 16 },
                    new CivitaiModelImage { Url = SafeUrl, NsfwLevel = 2 }
                ]
            }
        ]
    };

    [Fact]
    public void NsfwOff_CardPicksSafePreview_NotFirstImage()
    {
        var vm = new CivitaiResultViewModel(ModelWithAdultFirstImage(), showNsfwPreviews: false);

        vm.PreviewUrl.Should().Be(SafeUrl);
    }

    [Fact]
    public void NsfwOn_CardPicksFirstImage()
    {
        var vm = new CivitaiResultViewModel(ModelWithAdultFirstImage(), showNsfwPreviews: true);

        vm.PreviewUrl.Should().Be(AdultUrl);
    }

    [Fact]
    public void ApplyNsfwPreference_TogglingOff_SwitchesToSafePreview()
    {
        var vm = new CivitaiResultViewModel(ModelWithAdultFirstImage(), showNsfwPreviews: true);

        vm.ApplyNsfwPreference(showNsfw: false);

        vm.PreviewUrl.Should().Be(SafeUrl);
    }

    [Fact]
    public void ApplyNsfwPreference_TogglingOn_SwitchesToFirstImage()
    {
        var vm = new CivitaiResultViewModel(ModelWithAdultFirstImage(), showNsfwPreviews: false);

        vm.ApplyNsfwPreference(showNsfw: true);

        vm.PreviewUrl.Should().Be(AdultUrl);
    }

    [Fact]
    public void IsNsfw_ComesFromPolicy_NotModelBoolean()
    {
        // nsfw=false but only adult imagery → card counts as NSFW.
        var onlyAdult = new CivitaiModel
        {
            Id = 2,
            Name = "adult-only gallery",
            Nsfw = false,
            NsfwLevel = 24,
            ModelVersions =
            [
                new CivitaiModelVersion
                {
                    Images = [new CivitaiModelImage { Url = AdultUrl, NsfwLevel = 16 }]
                }
            ]
        };

        var vm = new CivitaiResultViewModel(onlyAdult, showNsfwPreviews: true);

        vm.IsNsfw.Should().BeTrue();
    }
}
