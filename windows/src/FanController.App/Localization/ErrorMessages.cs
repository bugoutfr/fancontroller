using FanController.Core;

namespace FanController.App.Localization;

internal static class ErrorMessages
{
    internal static string Message(Exception exception) =>
        exception.Data[Validation.ResourceKey] is string key
            ? Localizer.Get(key, exception.Message)
            : exception.Message;

    internal static string Details(Exception exception)
    {
        var details = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = Message(current);
            details.Add(string.IsNullOrWhiteSpace(message)
                ? current.GetType().Name
                : $"{current.GetType().Name}: {message}");
        }

        return string.Join(Environment.NewLine, details.Distinct(StringComparer.Ordinal));
    }
}
