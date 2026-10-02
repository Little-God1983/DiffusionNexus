using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Monochrome glyph for a community link's <c>icon</c> key (<c>youtube</c>, <c>patreon</c>,
/// <c>civitai</c>, <c>globe</c>, <c>mail</c>, <c>linktree</c>). The keys come from an
/// operator-edited document, so a key nobody drew yet renders the neutral link glyph instead of
/// an empty box: a new row can ship before the app knows its icon.
/// <para>
/// The glyphs are the 3.x installer's (<c>CommunityLinkIcon.razor</c>) on the same 24-unit box,
/// so both products show the same icons. Drawn in <see cref="Foreground"/>, which inherits like
/// text, so the icon follows the button it sits in. Set <c>Width</c>/<c>Height</c>; the glyph
/// scales to the smaller of the two and is centred.
/// </para>
/// </summary>
public class CommunityLinkIcon : Control
{
    /// <summary>The neutral glyph every unknown or missing key falls back to.</summary>
    public const string FallbackKey = "link";

    public static readonly StyledProperty<string?> IconKeyProperty =
        AvaloniaProperty.Register<CommunityLinkIcon, string?>(nameof(IconKey));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<CommunityLinkIcon>();

    /// <summary>Glyph key from the links document; matched case-insensitively.</summary>
    public string? IconKey
    {
        get => GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private const double GlyphBox = 24;
    private const double StrokeThickness = 2;

    /// <summary>
    /// Path data per key on a 24-unit box. Stroked glyphs are outlines drawn with a round 2-unit
    /// pen; the rest are filled. Written out with explicit separators and absolute shapes (the
    /// SVG originals use circles, rects and packed numbers like <c>7.54.54</c>).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Data, bool Stroked)> Glyphs =
        new Dictionary<string, (string, bool)>(StringComparer.Ordinal)
        {
            ["youtube"] = ("M21.6,7.2 A2.5,2.5 0 0 0 19.8,5.4 C18.2,5 12,5 12,5 S5.8,5 4.2,5.4 A2.5,2.5 0 0 0 2.4,7.2 " +
                           "A26,26 0 0 0 2,12 A26,26 0 0 0 2.4,16.8 A2.5,2.5 0 0 0 4.2,18.6 C5.8,19 12,19 12,19 " +
                           "S18.2,19 19.8,18.6 A2.5,2.5 0 0 0 21.6,16.8 A26,26 0 0 0 22,12 A26,26 0 0 0 21.6,7.2 Z " +
                           "M10,15 V9 L15.2,12 Z", false),
            ["patreon"] = ("M9,9 A6,6 0 1 1 21,9 A6,6 0 1 1 9,9 Z M3,3 H6.6 V21 H3 Z", false),
            ["civitai"] = ("F0 M12,1.5 L21.1,6.75 V17.25 L12,22.5 L2.9,17.25 V6.75 Z " +
                           "M12,4.96 L5.9,8.48 V15.52 L12,19.04 L18.1,15.52 V13.22 H15.1 V13.79 L12,15.15 " +
                           "L8.9,13.36 V10.64 L12,8.85 L15.1,10.64 V11.2 H18.1 V8.48 Z", false),
            ["globe"] = ("M3,12 A9,9 0 1 1 21,12 A9,9 0 1 1 3,12 Z M3,12 H21 " +
                         "M12,3 A15.3,15.3 0 0 1 16,12 A15.3,15.3 0 0 1 12,21 A15.3,15.3 0 0 1 8,12 A15.3,15.3 0 0 1 12,3 Z", true),
            ["mail"] = ("M5,5 H19 A2,2 0 0 1 21,7 V17 A2,2 0 0 1 19,19 H5 A2,2 0 0 1 3,17 V7 A2,2 0 0 1 5,5 Z " +
                        "M3,7 L12,13 L21,7", true),
            ["linktree"] = ("M11,2 H13 V7.3 L16.8,3.5 L18.2,4.9 L14.5,8.6 H20 V10.6 H14.5 L18.2,14.3 L16.8,15.7 L13,12 " +
                            "V22 H11 V12 L7.2,15.7 L5.8,14.3 L9.5,10.6 H4 V8.6 H9.5 L5.8,4.9 L7.2,3.5 L11,7.3 Z", false),
            [FallbackKey] = ("M10,13 A5,5 0 0 0 17.54,13.54 L20.54,10.54 A5,5 0 0 0 13.47,3.47 L11.75,5.18 " +
                             "M14,11 A5,5 0 0 0 6.46,10.46 L3.46,13.46 A5,5 0 0 0 10.53,20.53 L12.24,18.82", true),
        };

    // Parsed on first render (geometry needs the platform's render interface). Rendering runs on
    // the UI thread only, so a plain dictionary is enough.
    private static readonly Dictionary<string, Geometry> ParsedGlyphs = new(StringComparer.Ordinal);

    static CommunityLinkIcon()
    {
        AffectsRender<CommunityLinkIcon>(IconKeyProperty, ForegroundProperty);
    }

    /// <summary>The glyph key <paramref name="key"/> renders as: itself when drawn, else <see cref="FallbackKey"/>.</summary>
    public static string ResolveKey(string? key)
    {
        var normalized = key?.Trim().ToLowerInvariant();
        return normalized is not null && Glyphs.ContainsKey(normalized) ? normalized : FallbackKey;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var brush = Foreground;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (brush is null || size <= 0)
            return;

        var key = ResolveKey(IconKey);
        var (data, stroked) = Glyphs[key];
        if (!ParsedGlyphs.TryGetValue(key, out var geometry))
        {
            geometry = Geometry.Parse(data);
            ParsedGlyphs[key] = geometry;
        }

        // Scale the 24-unit box to the control, pen included, so stroked and filled glyphs keep
        // the same proportions at any size.
        var scale = size / GlyphBox;
        var transform = Matrix.CreateScale(scale, scale)
            * Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);

        using (context.PushTransform(transform))
        {
            if (stroked)
            {
                var pen = new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                context.DrawGeometry(null, pen, geometry);
            }
            else
            {
                context.DrawGeometry(brush, null, geometry);
            }
        }
    }
}
