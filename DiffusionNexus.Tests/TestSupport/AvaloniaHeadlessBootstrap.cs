using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using DiffusionNexus.UI;

namespace DiffusionNexus.Tests.TestSupport;

/// <summary>
/// One-time Avalonia headless platform bootstrap for this test assembly.
///
/// Some tests exercise app code that resolves platform services from Avalonia's service
/// locator - e.g. <see cref="Avalonia.Platform.AssetLoader"/>, used by
/// <c>PipelineManifestProvider</c> to read <c>avares://</c> pipeline manifest JSON. Without
/// an <c>AppBuilder.Setup()</c> call somewhere in the process, <c>IAssetLoader</c> is never
/// registered and <c>AssetLoader.Exists</c>/<c>Open</c> throw
/// <see cref="System.InvalidOperationException"/> ("Unable to locate 'Avalonia.Platform.IAssetLoader'"),
/// which <c>PipelineManifestProvider</c> catches and logs as a warning - so the symptom is a
/// silently-empty manifest list, not a visible test-runner error.
///
/// <see cref="ModuleInitializerAttribute"/> guarantees this runs exactly once, before any
/// test in the assembly, regardless of xunit's discovery/parallelization order. Mirrors
/// <c>DiffusionNexus.IntegrationTests.TestAppHost.EnsureAvalonia()</c>, but only performs the
/// lightweight <c>Initialize()</c> step (XAML load + exception-handler wiring) - it does not
/// invoke <c>OnFrameworkInitializationCompleted</c>, so no database/service bootstrapping runs.
/// </summary>
internal static class AvaloniaHeadlessBootstrap
{
    [ModuleInitializer]
    public static void Initialize()
    {
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();
    }
}
