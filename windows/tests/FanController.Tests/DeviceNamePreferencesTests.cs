using FanController.App.Services;
using Xunit;

namespace FanController.Tests;

public sealed class DeviceNamePreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FanController-device-names-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SavesAndLoadsLabelsForEveryDeviceAndRejectsCorruptData()
    {
        var path = Path.Combine(_directory, "device-names.json");
        var preferences = new DeviceNamePreferences(path);
        var empty = preferences.Load();
        Assert.Equal(["", ""], empty.Sensors);
        Assert.Equal(["", "", "", ""], empty.Fans);

        var names = new DeviceNames(["CPU", ""], ["Front", "Top", "Rear", ""]);
        preferences.Save(names);

        var loaded = new DeviceNamePreferences(path).Load();
        Assert.Equal(names.Sensors, loaded.Sensors);
        Assert.Equal(names.Fans, loaded.Fans);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Throws<ArgumentException>(() => preferences.Save(new(["Probe"], ["Fan"])));
        loaded = preferences.Load();
        Assert.Equal(names.Sensors, loaded.Sensors);
        Assert.Equal(names.Fans, loaded.Fans);

        File.WriteAllText(path, """{"Sensors":["A","B"],"Fans":["1","2"]}""");
        Assert.Throws<InvalidDataException>(() => preferences.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
        var temporary = Path.Combine(_directory, "device-names.json.tmp");
        if (File.Exists(temporary))
            File.Delete(temporary);
    }
}
