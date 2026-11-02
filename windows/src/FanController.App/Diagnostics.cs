namespace FanController.App;

internal static class Diagnostics
{
    public static void Record(Exception exception)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanController");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "erreurs.log"), $"{DateTimeOffset.Now:O} {exception}\n");
            for (var current = exception; current is not null; current = current.InnerException)
                foreach (System.Collections.DictionaryEntry item in current.Data)
                    File.AppendAllText(Path.Combine(folder, "erreurs.log"), $"{item.Key}: {item.Value}\n");
        }
        catch { /* Le diagnostic ne doit pas provoquer une seconde exception. */ }
    }
}
