using Microsoft.Win32;

namespace FanController.App;

internal static class WindowsStartup
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FanController";
    private static string Command => $"\"{Environment.ProcessPath ?? throw new IOException("Chemin de l’application indisponible.")}\" --startup";

    public static bool Enabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new IOException("Configuration du démarrage Windows inaccessible.");
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        if (Enabled() != enabled)
            throw new IOException("La modification du démarrage Windows n’a pas été confirmée.");
    }
}
