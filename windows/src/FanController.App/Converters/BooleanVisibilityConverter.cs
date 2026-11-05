using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace FanController.App.Converters;

/// <summary>Keeps WinUI visibility conversion in the view rather than in presentation models.</summary>
public sealed class BooleanVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}
