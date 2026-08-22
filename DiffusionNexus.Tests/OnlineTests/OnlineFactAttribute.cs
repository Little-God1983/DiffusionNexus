namespace DiffusionNexus.Tests.OnlineTests;

/// <summary>
/// A fact that runs only when <c>DIFFUSIONNEXUS_SDK_ONLINE_TESTS=1</c> is set,
/// for canary tests that hit external registries.
/// </summary>
/// <remarks>
/// Copied verbatim from <c>DiffusionNexus.Installer.SDK.Tests.Integration.OnlineFactAttribute</c>
/// (SDK repo). The "SDK" in the env var name is deliberate, not a copy-paste slip: it is a
/// single cross-repo switch, so one <c>DIFFUSIONNEXUS_SDK_ONLINE_TESTS=1</c> flips opt-in
/// online canaries in both the Installer SDK and this repo at once. Do not rename it here.
/// </remarks>
public sealed class OnlineFactAttribute : FactAttribute
{
    public OnlineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DIFFUSIONNEXUS_SDK_ONLINE_TESTS") != "1")
        {
            Skip = "Online canary tests are opt-in: set DIFFUSIONNEXUS_SDK_ONLINE_TESTS=1.";
        }
    }
}
