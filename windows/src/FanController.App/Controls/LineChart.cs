using FanController.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace FanController.App.Controls;

public sealed class LineChart : Grid
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<PlotSeries>), typeof(LineChart),
        new PropertyMetadata(null, (obj, _) => ((LineChart)obj).Render()));
    public static readonly DependencyProperty IsTimeAxisProperty = DependencyProperty.Register(
        nameof(IsTimeAxis), typeof(bool), typeof(LineChart),
        new PropertyMetadata(false, (obj, _) => ((LineChart)obj).Render()));

    public IReadOnlyList<PlotSeries>? Series
    {
        get => (IReadOnlyList<PlotSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }
    public bool IsTimeAxis { get => (bool)GetValue(IsTimeAxisProperty); set => SetValue(IsTimeAxisProperty, value); }

    private readonly Canvas _canvas = new();
    public LineChart()
    {
        Height = 250;
        Children.Add(_canvas);
        SizeChanged += (_, _) => Render();
    }

    private static SolidColorBrush Brush(string hex) => new(ColorHelper.FromArgb(255,
        Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16)));

    private void Label(string text, double x, double y, SolidColorBrush? brush = null)
    {
        var label = new TextBlock { Text = text, FontSize = 12, Foreground = brush ?? Brush("#8999AF") };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        _canvas.Children.Add(label);
    }

    private void Render()
    {
        _canvas.Children.Clear();
        var width = ActualWidth - 80;
        var height = ActualHeight - 80;
        if (width <= 0 || height <= 0) return;
        var series = Series ?? [];
        var points = series.SelectMany(s => s.Points).Where(p => p.Y is { } y && double.IsFinite(y)).ToArray();
        if (points.Length == 0)
        {
            Label("Aucune donnée pour cette période.", 18, 60);
            return;
        }
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        if (maxX <= minX) maxX = minX + (IsTimeAxis ? 60 : 1);
        var minY = IsTimeAxis ? Math.Min(0, points.Min(p => p.Y!.Value)) : 0;
        var maxY = IsTimeAxis ? Math.Max(1, Math.Ceiling(points.Max(p => p.Y!.Value) * 1.1)) : 100;
        for (var tick = 0; tick <= 4; tick++)
        {
            var x = 50 + width * tick / 4;
            var y = 10 + height * tick / 4;
            _canvas.Children.Add(new Line { X1 = 50, Y1 = y, X2 = width + 50, Y2 = y,
                Stroke = Brush("#34445C"), StrokeThickness = 1 });
            Label((maxY - (maxY - minY) * tick / 4).ToString("0.#"), 2, y - 7);
            var value = minX + (maxX - minX) * tick / 4;
            var text = IsTimeAxis ? DateTimeOffset.FromUnixTimeSeconds((long)value).ToLocalTime().ToString("dd/MM HH:mm")
                : value.ToString("0.#") + " °C";
            Label(text, Math.Max(0, x - 28), height + 24);
        }
        var legendX = 12.0;
        foreach (var item in series)
        {
            var color = Brush(item.Color);
            Label(item.Name, legendX, height + 53, color);
            legendX += 145;
            Polyline? line = null;
            string? segment = null;
            foreach (var point in item.Points)
            {
                if (point.Y is not { } value || !double.IsFinite(value))
                {
                    line = null;
                    continue;
                }
                if (line is null || point.Segment != segment)
                {
                    line = new Polyline { Stroke = color, StrokeThickness = 2,
                        StrokeLineJoin = PenLineJoin.Round };
                    _canvas.Children.Add(line);
                }
                segment = point.Segment;
                var location = new Point(50 + (point.X - minX) / (maxX - minX) * width,
                    10 + (maxY - value) / (maxY - minY) * height);
                line.Points.Add(location);
                if (!IsTimeAxis || line.Points.Count == 1)
                {
                    var dot = new Ellipse { Width = 5, Height = 5, Fill = color };
                    Canvas.SetLeft(dot, location.X - 2.5);
                    Canvas.SetTop(dot, location.Y - 2.5);
                    _canvas.Children.Add(dot);
                }
            }
        }
    }
}
