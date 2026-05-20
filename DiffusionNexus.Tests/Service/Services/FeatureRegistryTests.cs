using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

/// <summary>
/// Pins the SDK workload-id bindings on each feature so the order-of-initialization bug
/// (Guid.Empty leaking into the registry because the *WorkloadId fields were declared
/// AFTER the Registry field) can't silently regress.
/// </summary>
public class FeatureRegistryTests
{
    [Theory]
    [InlineData(Feature.Captioning,         "701DA214-2B25-44B4-A904-E4B036621564")]
    [InlineData(Feature.Inpainting,         "4C486765-A4C1-4E94-ACC2-BBAC0E405B6A")]
    [InlineData(Feature.BatchUpscale,       "B853EB7C-0A0E-48A6-985E-E32B2F8848F5")]
    [InlineData(Feature.BatchUpscaleVision, "B853EB7C-0A0E-48A6-985E-E32B2F8848F5")]
    [InlineData(Feature.Outpaint,           "137929E4-5C05-4304-80D4-5D785D45FD3F")]
    [InlineData(Feature.OutpaintVision,     "137929E4-5C05-4304-80D4-5D785D45FD3F")]
    public void EachFeature_BindsToExpectedSdkWorkload(Feature feature, string expectedGuid)
    {
        var requirements = FeatureRegistry.GetRequirements(feature);

        requirements.Should().NotBeNull();
        requirements!.WorkloadConfigurationId
            .Should().Be(Guid.Parse(expectedGuid),
                $"feature '{feature}' is supposed to be backed by SDK workload {expectedGuid}; "
                + "a Guid.Empty here means the static initializer order is wrong again.");
    }
}
