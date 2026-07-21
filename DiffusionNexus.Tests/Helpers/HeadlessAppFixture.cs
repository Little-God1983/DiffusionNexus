using Avalonia;
using Avalonia.Headless;

namespace DiffusionNexus.Tests.Helpers;

/// <summary>
/// Boots a headless Avalonia platform once per test process. Only the small set
/// of tests that must construct a real Avalonia <c>Bitmap</c> (e.g. the
/// BatchUpscale thumbnail-marshalling seam test, whose decoder returns an Avalonia
/// bitmap) need this. <c>SetupWithoutStarting</c> registers the platform services
/// but starts no dispatcher loop, so it does not hijack any thread or interfere
/// with the rest of the suite. The static guard makes double-instantiation a no-op.
/// </summary>
public sealed class HeadlessAppFixture
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public HeadlessAppFixture()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            AppBuilder.Configure<Application>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();

            _initialized = true;
        }
    }
}

/// <summary>
/// xUnit collection that shares the single <see cref="HeadlessAppFixture"/>.
/// Tests in this collection run serially with respect to each other but in
/// parallel with the rest of the suite.
/// </summary>
[CollectionDefinition("Headless Avalonia")]
public sealed class HeadlessAvaloniaCollection : ICollectionFixture<HeadlessAppFixture>
{
}
