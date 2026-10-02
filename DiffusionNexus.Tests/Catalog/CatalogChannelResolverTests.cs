using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.UI.Services.Catalog;
using FluentAssertions;

namespace DiffusionNexus.Tests.Catalog;

/// <summary>
/// The main app shares %LocalAppData%\DiffusionNexus\catalog with the 3.x installer, so it must
/// follow the channel the installer saved. Following a different one would make each app's
/// startup apply over the other's content.
/// </summary>
public class CatalogChannelResolverTests
{
    [Theory]
    [InlineData("preview", "Stable", CatalogChannel.Preview)]  // env wins
    [InlineData(null, "Preview", CatalogChannel.Preview)]      // saved setting
    [InlineData(null, "1", CatalogChannel.Stable)]             // a digit is not a channel
    [InlineData(null, null, CatalogChannel.Stable)]            // default
    [InlineData("  STABLE ", "Preview", CatalogChannel.Stable)] // trimmed, any case
    [InlineData("nightly", "Preview", CatalogChannel.Preview)] // an unknown env word falls through
    public void Resolve_picks_env_then_setting_then_stable(string? env, string? saved, CatalogChannel expected)
        => CatalogChannelResolver.Resolve(env, saved).Channel.Should().Be(expected);

    [Theory]
    [InlineData("preview", null, CatalogChannelSource.Environment)]
    [InlineData(null, "Preview", CatalogChannelSource.Setting)]
    [InlineData("0", "garbage", CatalogChannelSource.Default)]
    public void Resolve_reports_where_the_channel_came_from(string? env, string? saved, CatalogChannelSource expected)
        => CatalogChannelResolver.Resolve(env, saved).Source.Should().Be(expected);
}
