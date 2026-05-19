using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.Markup;

/// <summary>
/// XAML markup extension that resolves an avares:// URI through
/// <see cref="SafeAssetBitmap"/>. A failed load returns <c>null</c>
/// (Image renders blank) instead of throwing — see issue #351.
/// </summary>
public sealed class SafeAssetExtension : MarkupExtension
{
    public SafeAssetExtension() { }

    public SafeAssetExtension(string uri)
    {
        Uri = uri;
    }

    public string Uri { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => SafeAssetBitmap.Load(Uri)!;
}
