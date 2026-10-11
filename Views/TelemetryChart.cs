// Views/TelemetryChart.cs
//
// Small rolling line chart for the Live Telemetry window: one or more series over
// the last WindowSeconds, auto-scaled, with title, unit, current values and the
// min/max of the visible window. Drawn directly with Avalonia's DrawingContext,
// so no charting package is needed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace InfraDroneDesktop.Views;

public sealed class TelemetryChart : Control
{
    private sealed class Series
    {
        public string Name = "";
        public IBrush Brush = Brushes.White;
        public readonly List<(DateTime t, double v)> Points = new();
    }

    private readonly List<Series> _series = new();
    public string Title { get; set; } = "";
    public string Unit { get; set; } = "";
    public double WindowSeconds { get; set; } = 120;
    /// <summary>Always include zero in the vertical range (useful for speed, climb).</summary>
    public bool IncludeZero { get; set; } = true;

    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#0b1017"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#1e2b3a")), 1);
    private static readonly IPen ZeroPen = new Pen(new SolidColorBrush(Color.Parse("#334155")), 1);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#7f93a8"));
    private static readonly IBrush TitleBrush = new SolidColorBrush(Color.Parse("#e2e8f0"));
    private static readonly Typeface Face = new(FontFamily.Default);

    public TelemetryChart() { Height = 130; ClipToBounds = true; }

    public int AddSeries(string name, string color)
    {
        _series.Add(new Series { Name = name, Brush = new SolidColorBrush(Color.Parse(color)) });
        return _series.Count - 1;
    }

    public void Push(int seriesIndex, double value)
    {
        if (seriesIndex < 0 || seriesIndex >= _series.Count || double.IsNaN(value)) return;
        var s = _series[seriesIndex];
        var now = DateTime.UtcNow;
        s.Points.Add((now, value));
        var cutoff = now.AddSeconds(-WindowSeconds - 2);
        while (s.Points.Count > 0 && s.Points[0].t < cutoff) s.Points.RemoveAt(0);
    }

    public void Clear()
    {
        foreach (var s in _series) s.Points.Clear();
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width; var h = Bounds.Height;
        if (w < 40 || h < 40) return;
        ctx.FillRectangle(Bg, new Rect(0, 0, w, h), 6);

        const double padL = 44, padR = 8, padT = 22, padB = 16;
        var plot = new Rect(padL, padT, Math.Max(1, w - padL - padR), Math.Max(1, h - padT - padB));
        var inv = CultureInfo.InvariantCulture;

        // Title + current values
        DrawText(ctx, Title + (Unit.Length > 0 ? $" ({Unit})" : ""), new Point(8, 4), 12, TitleBrush);
        double x = w - 8;
        for (int i = _series.Count - 1; i >= 0; i--)
        {
            var s = _series[i];
            var txt = s.Points.Count > 0 ? $"{s.Name} {s.Points[^1].v.ToString("F1", inv)}" : $"{s.Name} -";
            var ft = Make(txt, 11, s.Brush);
            x -= ft.Width;
            ctx.DrawText(ft, new Point(x, 5));
            x -= 12;
        }

        var now = DateTime.UtcNow;
        var all = _series.SelectMany(s => s.Points.Where(p => (now - p.t).TotalSeconds <= WindowSeconds))
                         .Select(p => p.v).ToList();
        if (all.Count == 0)
        {
            DrawText(ctx, "waiting for data...", new Point(plot.X + 8, plot.Y + plot.Height / 2 - 8), 11, LabelBrush);
            return;
        }
        double min = all.Min(), max = all.Max();
        if (IncludeZero) { min = Math.Min(min, 0); max = Math.Max(max, 0); }
        if (max - min < 1) { var mid = (max + min) / 2; min = mid - 0.5; max = mid + 0.5; }
        var pad = (max - min) * 0.1; min -= pad; max += pad;

        double Y(double v) => plot.Bottom - (v - min) / (max - min) * plot.Height;
        double X(DateTime t) => plot.Right - (now - t).TotalSeconds / WindowSeconds * plot.Width;

        // Grid: 4 horizontal lines with labels
        for (int i = 0; i <= 4; i++)
        {
            var v = min + (max - min) * i / 4;
            var y = Y(v);
            ctx.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(ctx, v.ToString(Math.Abs(max - min) < 10 ? "F1" : "F0", inv), new Point(4, y - 7), 10, LabelBrush);
        }
        if (min < 0 && max > 0) ctx.DrawLine(ZeroPen, new Point(plot.Left, Y(0)), new Point(plot.Right, Y(0)));
        DrawText(ctx, $"-{WindowSeconds:F0}s", new Point(plot.Left, plot.Bottom + 1), 9, LabelBrush);
        DrawText(ctx, "now", new Point(plot.Right - 18, plot.Bottom + 1), 9, LabelBrush);

        // Series
        foreach (var s in _series)
        {
            var pts = s.Points.Where(p => (now - p.t).TotalSeconds <= WindowSeconds).ToList();
            if (pts.Count < 2) continue;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(X(pts[0].t), Y(pts[0].v)), false);
                for (int i = 1; i < pts.Count; i++) g.LineTo(new Point(X(pts[i].t), Y(pts[i].v)));
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(s.Brush, 2), geo);
        }
    }

    private static FormattedText Make(string text, double size, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush);

    private static void DrawText(DrawingContext ctx, string text, Point p, double size, IBrush brush) =>
        ctx.DrawText(Make(text, size, brush), p);
}
