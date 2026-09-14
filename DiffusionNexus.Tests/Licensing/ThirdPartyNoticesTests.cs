using System.Linq;
using DiffusionNexus.UI.Services.Licensing;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Licensing;

/// <summary>
/// The notices are a legal document, so these tests guard the two ways they fail silently:
/// the embedded resource going missing after a rename, and a component arriving with no
/// licence text (blank attribution reads as attribution but is not).
/// </summary>
public class ThirdPartyNoticesTests
{
    [Fact]
    public void WhenLoadedThenEmbeddedResourceIsPresentAndParses()
    {
        var components = ThirdPartyNotices.Load();

        components.Should().NotBeEmpty(
            "the generated index is embedded at build time and must be readable at runtime");
    }

    [Fact]
    public void WhenLoadedThenEveryComponentHasLicenseText()
    {
        var blank = ThirdPartyNotices.Load()
            .Where(c => string.IsNullOrWhiteSpace(c.LicenseText))
            .Select(c => c.Id)
            .ToList();

        blank.Should().BeEmpty("blank attribution is not attribution");
    }

    [Fact]
    public void WhenLoadedThenEveryComponentHasAnIdAndLicense()
    {
        foreach (var component in ThirdPartyNotices.Load())
        {
            component.Id.Should().NotBeNullOrWhiteSpace();
            component.License.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData("LibVLCSharp")]
    [InlineData("VideoLAN.LibVLC.Windows")]
    [InlineData("HPPH.SkiaSharp")]
    [InlineData("SixLabors.ImageSharp")]
    [InlineData("Avalonia.Fonts.Inter")]
    [InlineData("SkiaSharp.NativeAssets.Win32")]
    [InlineData("HarfBuzzSharp.NativeAssets.Win32")]
    public void WhenLoadedThenComponentsCarryingObligationsArePresent(string packageId)
    {
        // A copyleft/OFL canary. If a generator change ever drops the components that carry
        // real obligations, this fails instead of a lawyer noticing.
        ThirdPartyNotices.Load()
            .Should().Contain(c => c.Id == packageId);
    }

    [Theory]
    [InlineData("LibVLCSharp", "LGPL")]
    [InlineData("LibVLCSharp.Avalonia", "LGPL")]
    [InlineData("VideoLAN.LibVLC.Windows", "LGPL")]
    [InlineData("HPPH.SkiaSharp", "LGPL")]
    [InlineData("Inter typeface (bundled by Avalonia.Fonts.Inter)", "OFL")]
    public void WhenLoadedThenObligationBearingComponentsReportTheExpectedLicense(
        string id, string expectedLicenseFragment)
    {
        // Presence alone is not the property that matters: a component can be listed under the
        // WRONG licence, which reads as attribution while asserting something false. That is
        // exactly what happened to the Inter typeface - Avalonia.Fonts.Inter is MIT, but the six
        // Inter TTFs it embeds and .WithInterFont() renders with are SIL OFL 1.1, so the MIT line
        // was the wrapper's licence standing in for the font data's. Nothing failed; a human had
        // to notice. This asserts the licence, so the next one fails here instead.
        var component = ThirdPartyNotices.Load().SingleOrDefault(c => c.Id == id);

        component.Should().NotBeNull($"'{id}' carries a real licence obligation and must be listed");
        component!.License.Should().Contain(
            expectedLicenseFragment,
            $"'{id}' is licensed under {expectedLicenseFragment}, and a different id here would be a false claim");
    }

    [Fact]
    public void WhenNativeSkiaIsLoadedThenItsBundledNoticeTravelsWithIt()
    {
        // SkiaSharp's nuspec says MIT, which covers the binding only: libSkiaSharp.dll is Skia
        // compiled together with ANGLE, FreeType, libpng and others, and the package ships their
        // combined ~2,700-line notice, which self-contained publishing embeds into our exe. The
        // generator reproduced runtime-pack notices but not package-carried ones, so this was
        // silently absent - and the -Check freshness gate could never see it, because a gap in
        // the generator is missing from both sides of its comparison.
        var component = ThirdPartyNotices.Load()
            .Single(c => c.Id == "SkiaSharp.NativeAssets.Win32");

        component.LicenseText.Should().Contain("FreeType", "the bundled native notice must be reproduced, not just the binding's MIT");
        component.LicenseText.Should().Contain("libpng");
    }

    [Fact]
    public void WhenOnnxRuntimeIsLoadedThenItsBundledNoticeTravelsWithIt()
    {
        // Same class of gap as SkiaSharp, reopened by a filename spelling: the generator's notice
        // probe matched only 'THIRD-PARTY-NOTICES*', and Microsoft ships ONNX Runtime's notice as
        // 'ThirdPartyNotices.txt'. 6,121 lines covering Intel MKL, Eigen, protobuf and onnx were
        // therefore invisible to the probe AND to the -Check freshness gate, while
        // IncludeNativeLibrariesForSelfExtract embedded onnxruntime.dll into the shipped exe.
        var component = ThirdPartyNotices.Load()
            .Single(c => c.Id == "Microsoft.ML.OnnxRuntime.DirectML");

        component.LicenseText.Should().Contain(
            "Eigen", "the notice compiled into onnxruntime.dll must be reproduced, not just the package LICENSE");
        component.LicenseText.Should().Contain("Intel Math Kernel Library");
        component.LicenseText.Should().Contain("protobuf");
    }

    [Fact]
    public void WhenXabeIsSearchedForThenItIsAbsent()
    {
        // Xabe.FFmpeg was removed for being CC BY-NC-SA. If it returns, the notices will
        // advertise a non-commercial dependency inside an MIT product.
        ThirdPartyNotices.Load()
            .Should().NotContain(c => c.Id.StartsWith("Xabe", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WhenFirstPartyPackagesAreSearchedForThenTheyAreExcluded()
    {
        ThirdPartyNotices.Load()
            .Should().NotContain(c => c.Id.StartsWith("DiffusionNexus.", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WhenParsingJsonThenFieldsMapToTheRecord()
    {
        const string json = """
        [
          {
            "id": "Some.Package",
            "version": "1.2.3",
            "license": "MIT",
            "copyright": "Copyright (c) Someone",
            "authors": "Someone",
            "projectUrl": "https://example.com",
            "licenseText": "MIT License ..."
          }
        ]
        """;

        var components = ThirdPartyNotices.Parse(json);

        components.Should().ContainSingle();
        var component = components[0];
        component.Id.Should().Be("Some.Package");
        component.Version.Should().Be("1.2.3");
        component.License.Should().Be("MIT");
        component.Copyright.Should().Be("Copyright (c) Someone");
        component.Authors.Should().Be("Someone");
        component.ProjectUrl.Should().Be("https://example.com");
        component.LicenseText.Should().Be("MIT License ...");
    }

    [Fact]
    public void WhenParsingJsonWithMissingFieldsThenStringsAreEmptyNotNull()
    {
        var components = ThirdPartyNotices.Parse("""[{ "id": "X", "license": "MIT" }]""");

        components[0].Copyright.Should().BeEmpty();
        components[0].ProjectUrl.Should().BeEmpty();
        components[0].LicenseText.Should().BeEmpty();
    }
}
