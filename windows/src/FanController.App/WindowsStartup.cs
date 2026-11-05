using Microsoft.Win32;

namespace FanController.App;

/// <summary>Gère le lancement de l'application à l'ouverture de session de l'utilisateur courant.</summary>
internal static class WindowsStartup
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FanController";

    private static string Command => $"\"{Environment.ProcessPath ?? throw new IOException("Chemin de l’application indisponible.")}\" --startup";

    /// <summary>Vérifie que la clé de démarrage correspond à l'exécutable courant avec --startup.</summary>
    /// <returns>True si la commande attendue est enregistrée.</returns>
    /// <exception cref="IOException">Le chemin de l'exécutable n'est pas disponible.</exception>
    public static bool Enabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Active ou supprime le lancement automatique et vérifie le résultat.</summary>
    /// <param name="enabled">True pour enregistrer le démarrage, false pour le supprimer.</param>
    /// <exception cref="IOException">La clé ou le chemin est inaccessible, ou la modification n'est pas confirmée.</exception>
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new IOException("Configuration du démarrage Windows inaccessible.");
        if (enabled)
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);

        if (Enabled() != enabled)
            throw new IOException("La modification du démarrage Windows n’a pas été confirmée.");
    }
}
