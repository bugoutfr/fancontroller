using FanController.App.Services;
using Xunit;

namespace FanController.Tests;

public sealed class LanguagePreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FanController-language-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReadsSavedLanguagesAndRejectsUnsupportedOrCorruptPreferences()
    {
        var path = Path.Combine(_directory, "language.txt");
        var preferences = new LanguagePreferences(path);
        Assert.Null(preferences.Load());
        preferences.Save("fr");
        Assert.Equal("fr", new LanguagePreferences(path).Load());
        preferences.Save("en");
        Assert.Equal("en", preferences.Load());
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Throws<ArgumentException>(() => preferences.Save("de"));
        Assert.Equal("en", preferences.Load());
        File.WriteAllText(path, "unsupported");
        Assert.Throws<InvalidDataException>(() => preferences.Load());
    }

    [Fact]
    public void DiskFailureIsNotReportedAsASuccessfulSave()
    {
        Directory.CreateDirectory(_directory);
        var error = Record.Exception(() => new LanguagePreferences(_directory).Save("fr"));
        Assert.True(error is IOException or UnauthorizedAccessException);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
        var temporary = _directory + ".tmp";
        if (File.Exists(temporary))
            File.Delete(temporary);
    }
}
