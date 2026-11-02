using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FanController.App.Controls;

public sealed class TwoColumnPanel : Panel
{
    private const double Gap = 12;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 1000;
        var columnWidth = Math.Max(0, (width - Gap) / 2);
        var height = 0.0;
        for (var i = 0; i < Children.Count; i += 2)
        {
            var rowHeight = 0.0;
            for (var j = i; j < Math.Min(i + 2, Children.Count); j++)
            {
                Children[j].Measure(new Size(columnWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, Children[j].DesiredSize.Height);
            }
            height += rowHeight + (i == 0 ? 0 : Gap);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columnWidth = Math.Max(0, (finalSize.Width - Gap) / 2);
        var y = 0.0;
        for (var i = 0; i < Children.Count; i += 2)
        {
            var rowHeight = Children[i].DesiredSize.Height;
            if (i + 1 < Children.Count) rowHeight = Math.Max(rowHeight, Children[i + 1].DesiredSize.Height);
            for (var j = i; j < Math.Min(i + 2, Children.Count); j++)
                Children[j].Arrange(new Rect((j - i) * (columnWidth + Gap), y, columnWidth, rowHeight));
            y += rowHeight + Gap;
        }
        return finalSize;
    }
}
