using FanController.Core;
using FanController.App.Localization;
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
using System.Globalization;

namespace FanController.App.Controls;

/// <summary>Trace des séries avec ruptures, info-bulles et édition des points d'un profil.</summary>
public sealed class LineChart : Grid
{
    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this);

    private sealed class ChartAutomationPeer(LineChart owner) : FrameworkElementAutomationPeer(owner)
    {
        /// <inheritdoc />
        protected override string GetClassNameCore() => nameof(LineChart);

        /// <inheritdoc />
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
    }

    /// <summary>Identifie la propriété de dépendance des séries affichées.</summary>
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<PlotSeries>), typeof(LineChart),
        new PropertyMetadata(null, (obj, _) => ((LineChart)obj).Render()));

    /// <summary>Identifie la propriété de dépendance du mode d'axe temporel.</summary>
    public static readonly DependencyProperty IsTimeAxisProperty = DependencyProperty.Register(
        nameof(IsTimeAxis), typeof(bool), typeof(LineChart),
        new PropertyMetadata(false, (obj, _) => ((LineChart)obj).Render()));

    /// <summary>Identifie la propriété de dépendance de l'éditeur de profil.</summary>
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(
        nameof(Editor), typeof(ProfileEditor), typeof(LineChart),
        new PropertyMetadata(null, (obj, args) => ((LineChart)obj)._toolbar.DataContext = args.NewValue));

    /// <summary>Identifie la propriété de dépendance du mode d'édition.</summary>
    public static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(
        nameof(IsEditing), typeof(bool), typeof(LineChart),
        new PropertyMetadata(false, (obj, _) => ((LineChart)obj).EditingChanged()));

    /// <summary>Éditeur recevant les déplacements et commandes de points.</summary>
    public ProfileEditor? Editor
    {
        get => (ProfileEditor?)GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    /// <summary>Indique si les points d'un profil peuvent être déplacés.</summary>
    public bool IsEditing
    {
        get => (bool)GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    /// <summary>Séries à tracer dans l'ordre fourni, ou null pour un graphique vide.</summary>
    public IReadOnlyList<PlotSeries>? Series
    {
        get => (IReadOnlyList<PlotSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    /// <summary>Indique si les abscisses représentent des secondes Unix plutôt que des températures.</summary>
    public bool IsTimeAxis
    {
        get => (bool)GetValue(IsTimeAxisProperty);
        set => SetValue(IsTimeAxisProperty, value);
    }

    private readonly Canvas _canvas = new();
    private readonly StackPanel _toolbar = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 0, 12, 0)
    };

    private readonly Border _inputSurface = new()
    {
        Background = new SolidColorBrush(Colors.Transparent),
        IsHitTestVisible = true,
        ManipulationMode = ManipulationModes.None
    };

    private readonly Border _pointToolTip = new()
    {
        Background = Brush("#26364B"),
        BorderBrush = Brush("#5DD9C1"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 7, 10, 7),
        MaxWidth = 280,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };

    private readonly List<(string Name, PlotPoint Point, Point Location)> _hitPoints = [];
    private int _hoveredPointIndex = -1;
    private PointEditor? _dragPoint;
    private uint? _dragPointer;
    private double _minX, _maxX, _plotWidth, _plotHeight;
    private readonly Button _addButton;
    private readonly Button _deleteButton;

    /// <summary>Crée la surface de tracé, la barre d'édition et les interactions de pointeur.</summary>
    public LineChart()
    {
        Height = 250;
        Children.Add(_canvas);
        Children.Add(_inputSurface);
        Children.Add(_pointToolTip);

        _addButton = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE710",
                FontSize = 16
            }
        };
        _addButton.SetBinding(Button.CommandProperty, new Binding { Path = new PropertyPath("AddCommand") });
        _deleteButton = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE74D",
                FontSize = 16
            }
        };
        _deleteButton.SetBinding(Button.CommandProperty, new Binding { Path = new PropertyPath("SelectedPoint.RemoveCommand") });
        _deleteButton.SetBinding(Button.IsEnabledProperty, new Binding { Path = new PropertyPath("HasSelectedPoint") });

        RefreshLocalization();
        _toolbar.Children.Add(_addButton);
        _toolbar.Children.Add(_deleteButton);
        Children.Add(_toolbar);

        _inputSurface.PointerPressed += BeginDrag;
        _inputSurface.PointerMoved += Drag;
        _inputSurface.PointerExited += (_, _) => ClearPointToolTip();
        _inputSurface.PointerReleased += EndDrag;
        _inputSurface.PointerCanceled += EndDrag;
        _inputSurface.PointerCaptureLost += EndDrag;
        PointerMoved += (_, args) =>
        {
            if (_dragPoint is null)
                UpdatePointToolTip(args.GetCurrentPoint(_canvas).Position);
        };
        PointerExited += (_, _) => ClearPointToolTip();
        Unloaded += (_, _) => CancelDrag();
        SizeChanged += (_, _) => Render();
    }

    private void EditingChanged()
    {
        if (!IsEditing)
            CancelDrag();
        _inputSurface.IsHitTestVisible = true;
        _toolbar.Visibility = IsEditing && !IsTimeAxis ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    /// <summary>Actualise les libellés accessibles et redessine avec la langue courante.</summary>
    public void RefreshLocalization()
    {
        var addText = Localizer.Get("Chart.AddPoint", "Add point to curve");
        ToolTipService.SetToolTip(_addButton, addText);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_addButton, addText);

        var deleteText = Localizer.Get("Chart.SelectedPointDelete", "Delete selected point");
        ToolTipService.SetToolTip(_deleteButton, deleteText);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_deleteButton, deleteText);

        Render();
    }

    private void BeginDrag(object sender, PointerRoutedEventArgs args)
    {
        if (!IsEditing || IsTimeAxis || Editor is null || _dragPoint is not null ||
            !args.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed || _plotWidth <= 0 || _plotHeight <= 0)
            return;
        if (Editor.Curve.Count == 0)
            return;
        var position = args.GetCurrentPoint(_canvas).Position;
        var nearest = Editor.Points.Select(point =>
        {
            var value = point.Read();
            var x = 50 + (value.Temperature - _minX) / (_maxX - _minX) * _plotWidth;
            var y = 10 + (100 - value.Duty) / 100 * _plotHeight;
            return (Point: point, Distance: Math.Pow(position.X - x, 2) + Math.Pow(position.Y - y, 2));
        }).OrderBy(item => item.Distance).FirstOrDefault();
        if (nearest.Point is null || nearest.Distance > 18 * 18 || !_inputSurface.CapturePointer(args.Pointer))
            return;
        _dragPoint = nearest.Point;
        Editor.SelectedPoint = nearest.Point;
        _dragPointer = args.Pointer.PointerId;
        args.Handled = true;
    }

    private void Drag(object sender, PointerRoutedEventArgs args)
    {
        if (_dragPoint is null)
            return;
        if (args.Pointer.PointerId != _dragPointer || Editor is null)
            return;
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

    private void UpdatePointToolTip(Point position)
    {
        var nearest = _hitPoints.Select((point, index) => (Point: point, Index: index,
                Distance: Math.Pow(position.X - point.Location.X, 2) + Math.Pow(position.Y - point.Location.Y, 2)))
            .OrderBy(item => item.Distance).FirstOrDefault();
        if (nearest.Point.Point is null || nearest.Distance > 14 * 14)
        {
            ClearPointToolTip();
            return;
        }
        if (_hoveredPointIndex == nearest.Index)
            return;
        _hoveredPointIndex = nearest.Index;

        var point = nearest.Point.Point;
        var xLabel = Localizer.Get(IsTimeAxis ? "Chart.TimeAxis" : "Chart.TemperatureAxis",
            IsTimeAxis ? "Time" : "Temperature");
        var yLabel = Localizer.Get(IsTimeAxis ? "Chart.ValueAxis" : "Chart.PwmAxis",
            IsTimeAxis ? "Value" : "PWM");
        var xValue = IsTimeAxis
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(point.X * 1000)).ToLocalTime().ToString("G", CultureInfo.CurrentCulture)
            : point.X.ToString("0.#", CultureInfo.CurrentCulture) + " °C";
        var yValue = point.Y!.Value.ToString("0.#", CultureInfo.CurrentCulture) + (IsTimeAxis ? "" : " %");
        _pointToolTip.Child = new TextBlock
        {
            Text = Localizer.Format("Chart.PointToolTip", "{0}\n{1}: {2}\n{3}: {4}",
                nearest.Point.Name, xLabel, xValue, yLabel, yValue),
            TextWrapping = TextWrapping.Wrap
        };
        var tipWidth = _pointToolTip.ActualWidth > 0 ? _pointToolTip.ActualWidth : 240;
        var tipHeight = _pointToolTip.ActualHeight > 0 ? _pointToolTip.ActualHeight : 72;
        var left = position.X + 14;
        var top = position.Y + 14;
        if (left + tipWidth > ActualWidth)
            left = Math.Max(0, position.X - tipWidth - 14);
        if (top + tipHeight > ActualHeight)
            top = Math.Max(0, position.Y - tipHeight - 14);
        _pointToolTip.Margin = new Thickness(left, top, 0, 0);
        _pointToolTip.Visibility = Visibility.Visible;
    }

    private void ClearPointToolTip()
    {
        if (_hoveredPointIndex < 0)
            return;
        _hoveredPointIndex = -1;
        _pointToolTip.Visibility = Visibility.Collapsed;
    }

    private void EndDrag(object sender, PointerRoutedEventArgs args)
    {
        if (_dragPointer != args.Pointer.PointerId)
            return;
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
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = brush ?? Brush("#8999AF")
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        _canvas.Children.Add(label);
    }

    private void Render()
    {
        _canvas.Children.Clear();
        _hitPoints.Clear();
        ClearPointToolTip();

        var width = ActualWidth - 80;
        var height = ActualHeight - 80;
        _plotWidth = width;
        _plotHeight = height;
        if (width <= 0 || height <= 0)
            return;

        var series = Series ?? [];
        var points = series.SelectMany(s => s.Points).Where(p => p.Y is { } y && double.IsFinite(y)).ToArray();
        if (points.Length == 0)
        {
            Label(IsTimeAxis ? Localizer.Get("History.NoData", "No data for this period.") :
                Localizer.Get("Chart.Invalid", "Curve unavailable: check values and point order."), 18, 60);
            return;
        }

        var minX = IsEditing && !IsTimeAxis ? -20 : points.Min(p => p.X);
        var maxX = IsEditing && !IsTimeAxis ? 120 : points.Max(p => p.X);
        if (maxX <= minX)
            maxX = minX + (IsTimeAxis ? 60 : 1);
        _minX = minX;
        _maxX = maxX;
        _plotWidth = width;
        _plotHeight = height;
        var minY = IsTimeAxis ? Math.Min(0, points.Min(p => p.Y!.Value)) : 0;
        var maxY = IsTimeAxis ? Math.Max(1, Math.Ceiling(points.Max(p => p.Y!.Value) * 1.1)) : 100;

        DrawAxes(width, height, minX, maxX, minY, maxY);
        DrawSeries(series, width, height, minX, maxX, minY, maxY);
    }

    private void DrawAxes(double width, double height, double minX, double maxX, double minY, double maxY)
    {
        for (var tick = 0; tick <= 4; tick++)
        {
            var x = 50 + width * tick / 4;
            var y = 10 + height * tick / 4;
            _canvas.Children.Add(new Line
            {
                X1 = 50,
                Y1 = y,
                X2 = width + 50,
                Y2 = y,
                Stroke = Brush("#34445C"),
                StrokeThickness = 1
            });
            Label((maxY - (maxY - minY) * tick / 4).ToString("0.#"), 2, y - 7);

            var value = minX + (maxX - minX) * tick / 4;
            var text = IsTimeAxis ? DateTimeOffset.FromUnixTimeSeconds((long)value).ToLocalTime().ToString("dd/MM HH:mm")
                : value.ToString("0.#") + " °C";
            Label(text, Math.Max(0, x - 28), height + 24);
        }
    }

    private void DrawSeries(IReadOnlyList<PlotSeries> series, double width, double height,
        double minX, double maxX, double minY, double maxY)
    {
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
                    line = new Polyline
                    {
                        Stroke = color,
                        StrokeThickness = 2,
                        StrokeLineJoin = PenLineJoin.Round
                    };
                    _canvas.Children.Add(line);
                }

                segment = point.Segment;
                var location = new Point(50 + (point.X - minX) / (maxX - minX) * width,
                    10 + (maxY - value) / (maxY - minY) * height);
                _hitPoints.Add((item.Name, point, location));
                line.Points.Add(location);

                if (!IsTimeAxis || line.Points.Count == 1)
                {
                    var size = IsEditing ? 12 : 5;
                    var selected = IsEditing && Editor?.SelectedPoint is { } selection &&
                        selection.Read() == new CurvePoint(point.X, value);
                    var dot = new Ellipse
                    {
                        Width = size,
                        Height = size,
                        Fill = color,
                        Stroke = selected ? new SolidColorBrush(Colors.White) : null,
                        StrokeThickness = 2
                    };
                    Canvas.SetLeft(dot, location.X - size / 2.0);
                    Canvas.SetTop(dot, location.Y - size / 2.0);
                    _canvas.Children.Add(dot);
                }
            }
        }
    }
}
