using System.Collections;
using System.Collections.Specialized;
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
/// A named series of trend data points rendered as a single line in the chart.
/// </summary>
/// <param name="Label">Series label (e.g. "V3").</param>
/// <param name="Color">Hex colour for the line and dots.</param>
/// <param name="Points">Ordered data points (oldest first).</param>
public record ScoreTrendSeries(string Label, string Color, IReadOnlyList<ScoreTrendDataPoint> Points);

/// <summary>
/// A line chart showing composite score over time with color-coded background
/// bands (red/orange/yellow/green) representing quality zones.
/// Supports rendering multiple series when <see cref="SeriesData"/> is set.
/// </summary>
public class ScoreTrendChart : Control
{
    /// <summary>
    /// Single-series data points (oldest first). Used when <see cref="SeriesData"/> is null.
    /// </summary>
    public static readonly StyledProperty<IList?> DataPointsProperty =
        AvaloniaProperty.Register<ScoreTrendChart, IList?>(nameof(DataPoints));

    /// <summary>
    /// Multi-series data. When set, overrides <see cref="DataPoints"/>.
    /// Each item should be a <see cref="ScoreTrendSeries"/>.
    /// </summary>
    public static readonly StyledProperty<IList?> SeriesDataProperty =
        AvaloniaProperty.Register<ScoreTrendChart, IList?>(nameof(SeriesData));

    public IList? DataPoints
    {
        get => GetValue(DataPointsProperty);
        set => SetValue(DataPointsProperty, value);
    }

    public IList? SeriesData
    {
        get => GetValue(SeriesDataProperty);
        set => SetValue(SeriesDataProperty, value);
    }

    static ScoreTrendChart()
    {
        AffectsRender<ScoreTrendChart>(DataPointsProperty, SeriesDataProperty);
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DataPointsProperty || change.Property == SeriesDataProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldNcc)
            {
                oldNcc.CollectionChanged -= OnBoundCollectionChanged;
            }

            if (change.NewValue is INotifyCollectionChanged newNcc)
            {
                newNcc.CollectionChanged += OnBoundCollectionChanged;
            }
        }
    }

    private void OnBoundCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private const double PaddingLeft = 36;
    private const double PaddingRight = 12;
    private const double PaddingTop = 12;
    private const double PaddingBottom = 28;
    private const double LegendHeight = 18;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var allSeries = BuildSeries();
        if (allSeries.Count == 0)
            return;

        var isMulti = allSeries.Count > 1;

        var w = Bounds.Width;
        var h = Bounds.Height;
        var bottomPad = PaddingBottom + (isMulti ? LegendHeight : 0);
        var chartW = w - PaddingLeft - PaddingRight;
        var chartH = h - PaddingTop - bottomPad;
        if (chartW < 40 || chartH < 40) return;

        // Draw background quality zone bands
        DrawZoneBands(context, chartW, chartH);

        // Draw horizontal grid lines at 25, 50, 75
        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#2A2A2A")), 1);
        foreach (var v in new[] { 25.0, 50.0, 75.0 })
        {
            var y = PaddingTop + chartH * (1 - v / 100.0);
            context.DrawLine(gridPen, new Point(PaddingLeft, y), new Point(PaddingLeft + chartW, y));

            var label = new FormattedText(
                v.ToString("F0"),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 9, new SolidColorBrush(Color.Parse("#666")));
            context.DrawText(label, new Point(PaddingLeft - label.Width - 4, y - label.Height / 2));
        }

        // Find the global time range across all series for consistent x-axis
        var maxPointCount = allSeries.Max(s => s.Points.Count);

        // Draw each series
        // Use the series with most points for x-axis labels
        Point[]? xLabelPoints = null;
        List<(double Score, string DateLabel)>? xLabelItems = null;

        foreach (var series in allSeries)
        {
            var items = series.Points;
            if (items.Count < 2) continue;

            var seriesColor = Color.Parse(series.Color);

            // Calculate point positions — each series is plotted across the full chart width
            var points = new Point[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                var x = PaddingLeft + chartW * i / (items.Count - 1);
                var y = PaddingTop + chartH * (1 - Math.Clamp(items[i].Score, 0, 100) / 100.0);
                points[i] = new Point(x, y);
            }

            // Draw the polyline
            var linePen = new Pen(new SolidColorBrush(seriesColor), 2);
            for (var i = 1; i < points.Length; i++)
                context.DrawLine(linePen, points[i - 1], points[i]);

            // Draw dots
            var dotBrush = new SolidColorBrush(Colors.White);
            var dotPen = new Pen(new SolidColorBrush(seriesColor), 2);
            for (var i = 0; i < points.Length; i++)
                context.DrawEllipse(dotBrush, dotPen, points[i], 3, 3);

            // Score labels only for single series (multi would be too cluttered)
            if (!isMulti)
            {
                for (var i = 0; i < points.Length; i++)
                {
                    var scoreText = new FormattedText(
                        items[i].Score.ToString("F0"),
                        CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), 10,
                        new SolidColorBrush(Color.Parse(GetScoreColor(items[i].Score))));
                    context.DrawText(scoreText,
                        new Point(points[i].X - scoreText.Width / 2, points[i].Y - scoreText.Height - 4));
                }
            }

            // Track the longest series for x-axis labels
            if (xLabelPoints is null || items.Count > xLabelItems!.Count)
            {
                xLabelPoints = points;
                xLabelItems = items.Select(p => (p.Score, p.DateLabel)).ToList();
            }
        }

        // X-axis date labels from the longest series
        if (xLabelPoints is not null && xLabelItems is not null)
            DrawXLabels(context, xLabelItems, xLabelPoints, chartH);

        // Legend for multi-series
        if (isMulti)
            DrawLegend(context, allSeries, chartH, bottomPad);
    }

    /// <summary>
    /// Builds the series list from either <see cref="SeriesData"/> or <see cref="DataPoints"/>.
    /// </summary>
    private List<ScoreTrendSeries> BuildSeries()
    {
        // Prefer multi-series if available
        if (SeriesData is { Count: > 0 })
        {
            var result = new List<ScoreTrendSeries>();
            foreach (var item in SeriesData)
            {
                if (item is ScoreTrendSeries s && s.Points.Count >= 2)
                    result.Add(s);
            }

            if (result.Count > 0)
                return result;
        }

        // Fall back to single series from DataPoints
        var points = ExtractDataPoints();
        if (points.Count < 2)
            return [];

        var dataPoints = points.Select(p => new ScoreTrendDataPoint(p.Score, p.DateLabel, string.Empty))
            .ToList();
        return [new ScoreTrendSeries("Current", "#81D4FA", dataPoints)];
    }

    private void DrawZoneBands(DrawingContext context, double chartW, double chartH)
    {
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

    private void DrawLegend(DrawingContext context, List<ScoreTrendSeries> series, double chartH, double bottomPad)
    {
        var legendY = PaddingTop + chartH + bottomPad - 8;
        var x = PaddingLeft;

        foreach (var s in series)
        {
            var color = new SolidColorBrush(Color.Parse(s.Color));

            // Color swatch
            context.FillRectangle(color, new Rect(x, legendY - 4, 10, 10));
            x += 14;

            // Label
            var text = new FormattedText(
                s.Label,
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, new SolidColorBrush(Color.Parse("#AAA")));
            context.DrawText(text, new Point(x, legendY - text.Height / 2 + 1));
            x += text.Width + 16;
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
                result.Add((dp.Score, dp.DateLabel));
        }

        return result;
    }
}
