using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Data point for the score trend chart.
/// </summary>
/// <param name="Score">Composite score (0–100).</param>
/// <param name="DateLabel">Short date label for the x-axis.</param>
/// <param name="Tooltip">Hover text (unused for now, future-proof).</param>
public record ScoreTrendDataPoint(double Score, string DateLabel, string Tooltip);

/// <summary>
/// A line chart showing composite score over time with color-coded background
/// bands (red/orange/yellow/green) representing quality zones.
/// </summary>
public class ScoreTrendChart : Control
{
    /// <summary>
    /// Ordered list of <see cref="ScoreTrendDataPoint"/> (oldest first).
    /// </summary>
    public static readonly StyledProperty<IList?> DataPointsProperty =
        AvaloniaProperty.Register<ScoreTrendChart, IList?>(nameof(DataPoints));

    public IList? DataPoints
    {
        get => GetValue(DataPointsProperty);
        set => SetValue(DataPointsProperty, value);
    }

    static ScoreTrendChart()
    {
        AffectsRender<ScoreTrendChart>(DataPointsProperty);
    }

    private const double PaddingLeft = 36;
    private const double PaddingRight = 12;
    private const double PaddingTop = 12;
    private const double PaddingBottom = 28;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var items = ExtractDataPoints();
        if (items.Count < 2)
            return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        var chartW = w - PaddingLeft - PaddingRight;
        var chartH = h - PaddingTop - PaddingBottom;
        if (chartW < 40 || chartH < 40) return;

        // Draw background quality zone bands
        DrawZoneBands(context, chartW, chartH);

        // Draw horizontal grid lines at 25, 50, 75
        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#2A2A2A")), 1);
        foreach (var v in new[] { 25.0, 50.0, 75.0 })
        {
            var y = PaddingTop + chartH * (1 - v / 100.0);
            context.DrawLine(gridPen, new Point(PaddingLeft, y), new Point(PaddingLeft + chartW, y));

            // Y-axis label
            var label = new FormattedText(
                v.ToString("F0"),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 9, new SolidColorBrush(Color.Parse("#666")));
            context.DrawText(label, new Point(PaddingLeft - label.Width - 4, y - label.Height / 2));
        }

        // Calculate point positions
        var points = new Point[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var x = PaddingLeft + (items.Count == 1 ? chartW / 2 : chartW * i / (items.Count - 1));
            var y = PaddingTop + chartH * (1 - Math.Clamp(items[i].Score, 0, 100) / 100.0);
            points[i] = new Point(x, y);
        }

        // Draw the polyline
        var linePen = new Pen(new SolidColorBrush(Color.Parse("#81D4FA")), 2);
        for (var i = 1; i < points.Length; i++)
        {
            context.DrawLine(linePen, points[i - 1], points[i]);
        }

        // Draw dots and score labels
        var dotBrush = new SolidColorBrush(Colors.White);
        var dotPen = new Pen(new SolidColorBrush(Color.Parse("#81D4FA")), 2);
        for (var i = 0; i < points.Length; i++)
        {
            context.DrawEllipse(dotBrush, dotPen, points[i], 4, 4);

            // Score label above the dot
            var scoreText = new FormattedText(
                items[i].Score.ToString("F0"),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), 10,
                new SolidColorBrush(Color.Parse(GetScoreColor(items[i].Score))));
            context.DrawText(scoreText, new Point(points[i].X - scoreText.Width / 2, points[i].Y - scoreText.Height - 4));
        }

        // X-axis date labels (show first, last, and a few in between)
        DrawXLabels(context, items, points, chartH);
    }

    private void DrawZoneBands(DrawingContext context, double chartW, double chartH)
    {
        // Zones: 0-40 red, 40-65 orange, 65-80 yellow, 80-100 green (bottom to top)
        var zones = new (double Low, double High, string Color)[]
        {
            (0, 40, "#15FF6B6B"),
            (40, 65, "#15FFA726"),
            (65, 80, "#15FFEB3B"),
            (80, 100, "#154CAF50")
        };

        foreach (var (low, high, color) in zones)
        {
            var y1 = PaddingTop + chartH * (1 - high / 100.0);
            var y2 = PaddingTop + chartH * (1 - low / 100.0);
            var rect = new Rect(PaddingLeft, y1, chartW, y2 - y1);
            context.FillRectangle(new SolidColorBrush(Color.Parse(color)), rect);
        }
    }

    private void DrawXLabels(DrawingContext context, List<(double Score, string DateLabel)> items, Point[] points, double chartH)
    {
        // Show up to ~8 labels evenly spaced
        var maxLabels = Math.Min(items.Count, 8);
        var step = items.Count <= maxLabels ? 1 : (double)(items.Count - 1) / (maxLabels - 1);

        for (var li = 0; li < maxLabels; li++)
        {
            var idx = (int)Math.Round(li * step);
            idx = Math.Clamp(idx, 0, items.Count - 1);

            var label = new FormattedText(
                items[idx].DateLabel,
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 9, new SolidColorBrush(Color.Parse("#666")));
            context.DrawText(label, new Point(points[idx].X - label.Width / 2, PaddingTop + chartH + 6));
        }
    }

    private static string GetScoreColor(double score) => score switch
    {
        >= 80 => "#4CAF50",
        >= 65 => "#8BC34A",
        >= 40 => "#FFA726",
        _ => "#FF6B6B"
    };

    private List<(double Score, string DateLabel)> ExtractDataPoints()
    {
        var result = new List<(double, string)>();
        if (DataPoints is null) return result;

        foreach (var item in DataPoints)
        {
            if (item is ScoreTrendDataPoint dp)
            {
                result.Add((dp.Score, dp.DateLabel));
            }
        }

        return result;
    }
}
