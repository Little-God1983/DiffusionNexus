using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Bundle of services a <see cref="ModelTileViewModel"/> needs, injected through
/// its constructor and the static factory methods (#438). Previously each of these
/// was fetched lazily from the <c>App.Services</c> static locator inside the tile's
/// method bodies, which made the tile untestable. The parent view model builds one
/// bundle from DI and threads it through <c>TileGroupingHelper</c> / the factories so
/// every tile is constructed with real services; tests construct tiles with a bundle
/// of fakes (or none, in which case the tile degrades exactly as the null-conditional
/// locator calls used to).
/// <para>
/// All members are nullable and default to <c>null</c> so construction sites that
/// cannot reach DI (design-time, demo data, grouping-logic unit tests) keep compiling
/// and behaving as before. <see cref="ScopeFactory"/> is a singleton whose
/// <c>CreateScope()</c> is still called per operation inside the tile — it is never
/// captured as a scoped instance.
/// </para>
/// </summary>
public sealed record ModelTileDependencies(
    IUnifiedLogger? Logger = null,
    IServiceScopeFactory? ScopeFactory = null,
    IDialogService? DialogService = null,
    IVideoThumbnailService? VideoThumbnailService = null,
    IClipboardService? Clipboard = null,
    IUiScheduler? UiScheduler = null);
