using FanController.Core;
using FanController.App.ViewModels;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace FanController.App.Controls;

public sealed class LineChart : Grid
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this);
    private sealed class ChartAutomationPeer(LineChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(LineChart);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
    }
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<PlotSeries>), typeof(LineChart),
        new PropertyMetadata(null, (obj, _) => ((LineChart)obj).Render()));
    public static readonly DependencyProperty IsTimeAxisProperty = DependencyProperty.Register(
        nameof(IsTimeAxis), typeof(bool), typeof(LineChart),
        new PropertyMetadata(false, (obj, _) => ((LineChart)obj).Render()));
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(
        nameof(Editor), typeof(ProfileEditor), typeof(LineChart),
        new PropertyMetadata(null, (obj, args) => ((LineChart)obj)._toolbar.DataContext = args.NewValue));
    public static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(
        nameof(IsEditing), typeof(bool), typeof(LineChart),
        new PropertyMetadata(false, (obj, _) => ((LineChart)obj).EditingChanged()));
    public ProfileEditor? Editor { get => (ProfileEditor?)GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    public bool IsEditing { get => (bool)GetValue(IsEditingProperty); set => SetValue(IsEditingProperty, value); }

    public IReadOnlyList<PlotSeries>? Series
    {
        get => (IReadOnlyList<PlotSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }
    public bool IsTimeAxis { get => (bool)GetValue(IsTimeAxisProperty); set => SetValue(IsTimeAxisProperty, value); }

    private readonly Canvas _canvas = new();
    private readonly StackPanel _toolbar = new()
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
        Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 12, 0)
    };
    private readonly Border _inputSurface = new()
    {
        Background = new SolidColorBrush(Colors.Transparent),
        IsHitTestVisible = false,
        ManipulationMode = ManipulationModes.None
    };
    private PointEditor? _dragPoint;
    private uint? _dragPointer;
    private double _minX, _maxX, _plotWidth, _plotHeight;
    public LineChart()
    {
        Height = 250;
        Children.Add(_canvas);
        Children.Add(_inputSurface);
        var add = new Button { Content = new FontIcon { Glyph = "\uE710", FontSize = 16 } };
        add.SetBinding(Button.CommandProperty, new Binding { Path = new PropertyPath("AddCommand") });
        ToolTipService.SetToolTip(add, "Ajouter un point sur la courbe");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(add, "Ajouter un point sur la courbe");
        var delete = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 16 } };
        delete.SetBinding(Button.CommandProperty, new Binding { Path = new PropertyPath("SelectedPoint.RemoveCommand") });
        delete.SetBinding(Button.IsEnabledProperty, new Binding { Path = new PropertyPath("HasSelectedPoint") });
        ToolTipService.SetToolTip(delete, "Supprimer le point sélectionné");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, "Supprimer le point sélectionné");
        _toolbar.Children.Add(add);
        _toolbar.Children.Add(delete);
        Children.Add(_toolbar);
        _inputSurface.PointerPressed += BeginDrag;
        _inputSurface.PointerMoved += Drag;
        _inputSurface.PointerReleased += EndDrag;
        _inputSurface.PointerCanceled += EndDrag;
        _inputSurface.PointerCaptureLost += EndDrag;
        Unloaded += (_, _) => CancelDrag();
        SizeChanged += (_, _) => Render();
    }

    private void EditingChanged()
    {
        if (!IsEditing) CancelDrag();
        _inputSurface.IsHitTestVisible = IsEditing;
        _toolbar.Visibility = IsEditing && !IsTimeAxis ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    private void BeginDrag(object sender, PointerRoutedEventArgs args)
    {
        if (!IsEditing || IsTimeAxis || Editor is null || _dragPoint is not null ||
            !args.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed || _plotWidth <= 0 || _plotHeight <= 0)
            return;
        if (Editor.Curve.Count == 0) return;
        var position = args.GetCurrentPoint(_canvas).Position;
        var nearest = Editor.Points.Select(point =>
        {
            var value = point.Read();
            var x = 50 + (value.Temperature - _minX) / (_maxX - _minX) * _plotWidth;
            var y = 10 + (100 - value.Duty) / 100 * _plotHeight;
            return (Point: point, Distance: Math.Pow(position.X - x, 2) + Math.Pow(position.Y - y, 2));
        }).OrderBy(item => item.Distance).FirstOrDefault();
        if (nearest.Point is null || nearest.Distance > 18 * 18 || !_inputSurface.CapturePointer(args.Pointer)) return;
        _dragPoint = nearest.Point;
        Editor.SelectedPoint = nearest.Point;
        _dragPointer = args.Pointer.PointerId;
        args.Handled = true;
    }

    private void Drag(object sender, PointerRoutedEventArgs args)
    {
        if (_dragPoint is null || args.Pointer.PointerId != _dragPointer || Editor is null) return;
        if (!IsEditing || !Editor.IsEditing || !Editor.Points.Contains(_dragPoint) || Editor.Curve.Count == 0)
        {
            CancelDrag();
            return;
        }
        var position = args.GetCurrentPoint(_canvas).Position;
        Editor.MovePoint(_dragPoint, _minX + (position.X - 50) / _plotWidth * (_maxX - _minX),
            100 - (position.Y - 10) / _plotHeight * 100);
        args.Handled = true;
    }

    private void EndDrag(object sender, PointerRoutedEventArgs args)
    {
        if (_dragPointer != args.Pointer.PointerId) return;
        CancelDrag();
        args.Handled = true;
    }

    private void CancelDrag()
    {
        _dragPoint = null;
        _dragPointer = null;
        _inputSurface.ReleasePointerCaptures();
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
        _plotWidth = width;
        _plotHeight = height;
        if (width <= 0 || height <= 0) return;
        var series = Series ?? [];
        var points = series.SelectMany(s => s.Points).Where(p => p.Y is { } y && double.IsFinite(y)).ToArray();
        if (points.Length == 0)
        {
            Label(IsTimeAxis ? "Aucune donnée pour cette période." :
                "Courbe indisponible : vérifiez les valeurs et l’ordre des points.", 18, 60);
            return;
        }
        var minX = IsEditing && !IsTimeAxis ? -20 : points.Min(p => p.X);
        var maxX = IsEditing && !IsTimeAxis ? 120 : points.Max(p => p.X);
        if (maxX <= minX) maxX = minX + (IsTimeAxis ? 60 : 1);
        _minX = minX;
        _maxX = maxX;
        _plotWidth = width;
        _plotHeight = height;
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
                    var size = IsEditing ? 12 : 5;
                    var selected = IsEditing && Editor?.SelectedPoint is { } selection &&
                        selection.Read() == new CurvePoint(point.X, value);
                    var dot = new Ellipse { Width = size, Height = size, Fill = color,
                        Stroke = selected ? new SolidColorBrush(Colors.White) : null, StrokeThickness = 2 };
                    Canvas.SetLeft(dot, location.X - size / 2.0);
                    Canvas.SetTop(dot, location.Y - size / 2.0);
                    _canvas.Children.Add(dot);
                }
            }
        }
    }
}
