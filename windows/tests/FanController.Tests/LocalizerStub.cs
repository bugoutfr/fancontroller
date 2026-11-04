namespace FanController.App.Localization;

internal static class Localizer
{
    public static string Get(string key, string fallback = "") => fallback;
    public static string Format(string key, string fallback, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, fallback, args);
}

