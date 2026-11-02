namespace FanController.Core;

public sealed record PlotPoint(double X, double? Y, string? Segment = null);
public sealed record PlotSeries(string Name, string Color, IReadOnlyList<PlotPoint> Points);
