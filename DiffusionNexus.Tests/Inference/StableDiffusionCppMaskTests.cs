using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.Models;
using DiffusionNexus.Inference.StableDiffusionCpp;
using FluentAssertions;

namespace DiffusionNexus.Tests.Inference;

/// <summary>
/// The local backend's inpaint rules (#595). The native call itself needs a GPU; these pin what reaches it.
/// </summary>
public class StableDiffusionCppMaskTests
{
    private static readonly ModelDescriptor Descriptor = new()
    {
        Key = "z-image-turbo",
        DisplayName = "Z-Image Turbo",
        Kind = ModelKind.ZImageTurbo,
        DimensionAlignment = 64,
    };

    private static DiffusionRequest Request(float? initStrength, bool mask) => new()
    {
        ModelKey = Descriptor.Key,
        Prompt = "a lighthouse",
        Width = 512,
        Height = 512,
        InitImage = initStrength is { } strength ? new DiffusionReferenceImage(@"C:\scratch\region.png", strength) : null,
        MaskImage = mask ? new DiffusionReferenceImage(@"C:\scratch\mask.png") : null,
    };

    [Fact]
    public void InpaintingIsNoLongerReportedAsUnsupported()
    {
        StableDiffusionCppBackend.LocalCapabilities.Supports(BackendFeature.Inpainting).Should().BeTrue();
    }

    [Fact]
    public void AMaskedRunIsCappedJustBelowFullStrength()
    {
        // At 1.0 stable-diffusion.cpp skips encoding the init image, leaving nothing outside the mask to keep.
        StableDiffusionCppBackend.InitStrengthFor(Request(1.0f, mask: true))
            .Should().Be(StableDiffusionCppBackend.MaxMaskedStrength).And.BeLessThan(1.0f);
    }

    [Fact]
    public void AMaskedRunBelowTheCapKeepsItsStrength()
    {
        StableDiffusionCppBackend.InitStrengthFor(Request(0.6f, mask: true)).Should().Be(0.6f);
    }

    [Fact]
    public void AnUnmaskedRunKeepsFullStrength()
    {
        StableDiffusionCppBackend.InitStrengthFor(Request(1.0f, mask: false)).Should().Be(1.0f);
    }

    [Fact]
    public void AMaskWithoutAnInitImageIsRefused()
    {
        var act = () => StableDiffusionCppBackend.ValidateRequest(Request(initStrength: null, mask: true), Descriptor);

        act.Should().Throw<ArgumentException>().WithMessage("*mask*image to image*");
    }

    [Fact]
    public void AMaskWithAnInitImageIsAccepted()
    {
        var act = () => StableDiffusionCppBackend.ValidateRequest(Request(0.75f, mask: true), Descriptor);

        act.Should().NotThrow();
    }
}
