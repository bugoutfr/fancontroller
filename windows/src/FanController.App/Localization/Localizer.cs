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
        "Key", typeof(string), typeof(Localizer), new PropertyMetadata(null, OnKeyChanged));

    public static string GetKey(DependencyObject element) => element.GetValue(KeyProperty) as string ?? "";
    public static void SetKey(DependencyObject element, string value) => element.SetValue(KeyProperty, value);

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ApplyToElement(d);
        if (d is FrameworkElement fe)
        {
            fe.Loaded -= OnElementLoaded;
            fe.Loaded += OnElementLoaded;
        }
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject d)
            ApplyToElement(d);
    }

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
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        ApplyCore(root, visited);
    }

    private static void ApplyCore(DependencyObject element, HashSet<DependencyObject> visited)
    {
        if (element is null || !visited.Add(element)) return;

        ApplyToElement(element);

        // 1. Visual tree children
        var visualCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < visualCount; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i);
            ApplyCore(child, visited);
        }

        // 2. Logical / content tree children (ensures unrealized Pivot tabs and containers are localized immediately)
        if (element is Microsoft.UI.Xaml.Controls.ContentControl cc && cc.Content is DependencyObject ccChild)
            ApplyCore(ccChild, visited);

        if (element is Microsoft.UI.Xaml.Controls.ScrollViewer sv && sv.Content is DependencyObject svChild)
            ApplyCore(svChild, visited);

        if (element is Microsoft.UI.Xaml.Controls.Border border && border.Child is DependencyObject borderChild)
            ApplyCore(borderChild, visited);

        if (element is Microsoft.UI.Xaml.Controls.Panel panel)
        {
            foreach (var child in panel.Children)
                ApplyCore(child, visited);
        }

        if (element is Microsoft.UI.Xaml.Controls.ItemsControl ic)
        {
            foreach (var item in ic.Items)
                if (item is DependencyObject d) ApplyCore(d, visited);
        }

        if (element is Microsoft.UI.Xaml.Controls.Expander exp && exp.Content is DependencyObject expChild)
            ApplyCore(expChild, visited);

        if (element is Microsoft.UI.Xaml.Controls.UserControl uc && uc.Content is DependencyObject ucChild)
            ApplyCore(ucChild, visited);
    }

    private static void ApplyToElement(DependencyObject element)
    {
        if (element is Controls.LineChart lineChart)
            lineChart.RefreshLocalization();

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
