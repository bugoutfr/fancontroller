using System.Globalization;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.Resources;

namespace FanController.App.Localization;

public static class Localizer
{
    private static readonly Lazy<ResourceManager> Manager = new(() => new(
        Path.Combine(AppContext.BaseDirectory, "FanController.App.pri")));
    private static ResourceContext? _context;

    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(Localizer), new PropertyMetadata(null));

    public static string GetKey(DependencyObject element) => element.GetValue(KeyProperty) as string ?? "";
    public static void SetKey(DependencyObject element, string value) => element.SetValue(KeyProperty, value);

    public static void SetLanguage(string language)
    {
        var tag = language == "fr" ? "fr-FR" : "en-US";
        var manager = Manager.Value;
        var context = manager.CreateResourceContext();
        context.QualifierValues["Language"] = tag;
        _context = context;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(tag);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(tag);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture;
    }

    public static string Get(string key, string fallback = "")
    {
        try
        {
            var context = _context ?? Manager.Value.CreateResourceContext();
            var lookup = key.Replace('.', '/');
            return Manager.Value.MainResourceMap.GetSubtree("Resources").GetValue(lookup, context).ValueAsString;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    public static string Format(string key, string fallback, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key, fallback), args);

    public static void Apply(DependencyObject root)
    {
        ApplyToElement(root);
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            Apply(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
    }

    private static void ApplyToElement(DependencyObject element)
    {
        var key = GetKey(element);
        if (string.IsNullOrEmpty(key)) return;
        foreach (var propertyName in new[] { "Text", "Header", "Content", "PlaceholderText", "OnContent", "OffContent" })
        {
            var property = element.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property is null || !property.CanWrite || !property.CanRead) continue;
            var resourceKey = propertyName switch
            {
                "OnContent" => $"{key}.On",
                "OffContent" => $"{key}.Off",
                _ => $"{key}.{propertyName}"
            };
            var value = TryGet(resourceKey);
            if (value is not null) property.SetValue(element, value);
        }
        if (element is FrameworkElement frameworkElement)
        {
            var automationName = TryGet($"{key}.AutomationProperties.Name");
            if (automationName is not null) Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(frameworkElement, automationName);
            var toolTip = TryGet($"{key}.ToolTipService.ToolTip");
            if (toolTip is not null) Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(frameworkElement, toolTip);
        }
    }

    private static string? TryGet(string key)
    {
        var fallback = "\u0001";
        var value = Get(key, fallback);
        return value == fallback ? null : value;
    }
}
