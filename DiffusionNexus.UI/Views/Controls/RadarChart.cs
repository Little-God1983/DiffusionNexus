using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// A radar (spider) chart that renders category scores as a filled polygon
/// over concentric guide rings. Designed for 3–8 axes.
/// </summary>
public class RadarChart : Control
{
    /// <summary>
    /// Items source containing objects with <c>CategoryName</c> (string),
    /// <c>Score</c> (double 0–100), and <c>ScoreColor</c> (string hex) properties.
    /// Typically bound to <see cref="ViewModels.Tabs.TestRunViewModel.CategoryScores"/>.
    /// </summary>
    public static readonly StyledProperty<IList?> DataPointsProperty =
        AvaloniaProperty.Register<RadarChart, IList?>(nameof(DataPoints));

    public IList? DataPoints
    {
        get => GetValue(DataPointsProperty);
        set => SetValue(DataPointsProperty, value);
    }

    static RadarChart()
    {
        AffectsRender<RadarChart>(DataPointsProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var items = ExtractDataPoints();
        if (items.Count < 3)
            return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        var cx = w / 2;
        var cy = h / 2;
        var radius = Math.Min(cx, cy) - 30; // leave room for labels
        if (radius < 20) return;

        var count = items.Count;
        var angleStep = 2 * Math.PI / count;
        // Start from top (-π/2)
        const double startAngle = -Math.PI / 2;

        // Draw concentric guide rings at 25, 50, 75, 100
        var ringPen = new Pen(new SolidColorBrush(Color.Parse("#333")), 1);
        foreach (var pct in new[] { 0.25, 0.50, 0.75, 1.0 })
        {
            var r = radius * pct;
            context.DrawEllipse(null, ringPen, new Point(cx, cy), r, r);
        }

        // Draw axis lines
        var axisPen = new Pen(new SolidColorBrush(Color.Parse("#444")), 1);
        for (var i = 0; i < count; i++)
        {
            var angle = startAngle + i * angleStep;
            var ex = cx + radius * Math.Cos(angle);
            var ey = cy + radius * Math.Sin(angle);
            context.DrawLine(axisPen, new Point(cx, cy), new Point(ex, ey));
        }

        // Build the data polygon
        var points = new Point[count];
        for (var i = 0; i < count; i++)
        {
            var angle = startAngle + i * angleStep;
            var normalizedScore = Math.Clamp(items[i].Score, 0, 100) / 100.0;
            var r = radius * normalizedScore;
            points[i] = new Point(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
        }

        // Fill polygon
        var fillGeometry = BuildPolygonGeometry(points);
        var fillBrush = new SolidColorBrush(Color.Parse("#4081D4FA")); // semi-transparent blue
        var strokePen = new Pen(new SolidColorBrush(Color.Parse("#81D4FA")), 2);
        context.DrawGeometry(fillBrush, strokePen, fillGeometry);

        // Draw data points and labels
        var dotBrush = new SolidColorBrush(Color.Parse("#81D4FA"));
        for (var i = 0; i < count; i++)
        {
            // Data dot
            context.DrawEllipse(dotBrush, null, points[i], 4, 4);

            // Category label
            var angle = startAngle + i * angleStep;
            var labelR = radius + 16;
            var lx = cx + labelR * Math.Cos(angle);
            var ly = cy + labelR * Math.Sin(angle);

            var text = new FormattedText(
                items[i].CategoryName,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Normal),
                11,
                new SolidColorBrush(Color.Parse("#AAA")));

            // Center the label around the point
            var tx = lx - text.Width / 2;
            var ty = ly - text.Height / 2;

            context.DrawText(text, new Point(tx, ty));
        }
    }

    private static StreamGeometry BuildPolygonGeometry(Point[] points)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(points[0], true);
        for (var i = 1; i < points.Length; i++)
            ctx.LineTo(points[i]);
        ctx.EndFigure(true);
        return geometry;
    }

    private record struct DataItem(string CategoryName, double Score);

    private List<DataItem> ExtractDataPoints()
    {
        var result = new List<DataItem>();
        if (DataPoints is null) return result;

        foreach (var item in DataPoints)
        {
            if (item is null) continue;
            var type = item.GetType();
            var nameProp = type.GetProperty("CategoryName");
            var scoreProp = type.GetProperty("Score");
            if (nameProp is null || scoreProp is null) continue;

            var name = nameProp.GetValue(item) as string ?? string.Empty;
            var score = scoreProp.GetValue(item) is double d ? d : 0;
            result.Add(new DataItem(name, score));
        }

        return result;
    }
}
