using System.Globalization;
using FanController.App.ViewModels;
using FanController.Core;
using Xunit;

namespace FanController.Tests;

public sealed class EditorTests
{
    [Fact]
    public void RepeatedAdvertisementsKeepDeviceItemsAndSelectionStable()
    {
        var index = new AdvertisementIndex();
        var initial = index.Add(42, "", true, -50)!;
        var device = new BleDeviceDisplay(initial);
        var devices = new System.Collections.ObjectModel.ObservableCollection<BleDeviceDisplay> { device };
        var selected = devices[0];
        var collectionChanges = 0;
        var changes = new List<string?>();
        devices.CollectionChanged += (_, _) => collectionChanges++;
        device.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        for (var i = 0; i < 100; i++)
            device.Update(index.Add(42, "", true, -50)!);
        Assert.Empty(changes);
        device.Update(index.Add(42, "ESP32-FanController", false, -50)!);
        Assert.Contains("ESP32-FanController", device.Label);
        device.Update(index.Add(42, "", true, -51)!);
        Assert.Contains("-51 dBm", device.Label);
        Assert.Equal(new[] { "Label", "Label" }, changes);
        Assert.Same(selected, devices[0]);
        Assert.Equal(0, collectionChanges);
        Assert.Throws<ArgumentException>(() => device.Update(new(43, "Autre", -50)));
    }

    [Fact]
    public void SimpleDashboardPreservesSafetyAndCanExposeTechnicalDetailsWithoutReplacingCards()
    {
        var sensor = new SensorDisplay("Temperature 1");
        sensor.Update("Indisponible", "3200 mV, CIRCUIT OUVERT, sécurité ACTIVE", "CIRCUIT OUVERT, sécurité ACTIVE");
        Assert.Equal("CIRCUIT OUVERT, sécurité ACTIVE", sensor.DisplayDetails);
        sensor.Advanced = true;
        Assert.Contains("3200 mV", sensor.DisplayDetails);
        var fan = new FanDisplay("V1");
        fan.Update("1200 tr/min", "cible 40 %, fixe 30 %, boost inactif", "Automatique, PWM 40 %");
        Assert.Equal("Automatique, PWM 40 %", fan.DisplayDetails);
        fan.Advanced = true;
        Assert.Contains("cible", fan.DisplayDetails);
    }

    [Fact]
    public void ProfileGraphUsesTheSameLinearPointsAndInvalidatesOnEdit()
    {
        var editor = new ProfileEditor(1, [new(30, 40), new(40, 60)]);
        var curveChanges = 0;
        editor.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ProfileEditor.Curve)) curveChanges++; };
        var series = Assert.Single(editor.Curve);
        Assert.Equal(new[] { new PlotPoint(30, 40), new PlotPoint(40, 60) }, series.Points);
        editor.Points[1].Temperature = "20";
        Assert.Empty(editor.Curve);
        Assert.Equal(1, curveChanges);
    }

    [Fact]
    public void DraggingUpdatesTheDraftAndPreservesProfileConstraints()
    {
        var profile = new ProfileEditor(1, [new(20, 20), new(40, 50), new(60, 80)]) { IsEditing = true };
        var middle = profile.Points[1];
        var validNotifications = 0;
        profile.Edited += () => { Validation.Profile(profile.Read()); validNotifications++; };
        profile.MovePoint(middle, 35.26, 45.26);
        Assert.Equal(new CurvePoint(35.3, 45.3), middle.Read());
        profile.MovePoint(middle, -100, -100);
        Assert.Equal(new CurvePoint(20.1, 20), middle.Read());
        profile.MovePoint(middle, 200, 200);
        Assert.Equal(new CurvePoint(59.9, 80), middle.Read());
        profile.MovePoint(profile.Points[0], -100, -100);
        Assert.Equal(new CurvePoint(-20, 0), profile.Points[0].Read());
        profile.MovePoint(profile.Points[^1], 200, 200);
        Assert.Equal(new CurvePoint(120, 100), profile.Points[^1].Read());
        Assert.True(validNotifications > 0);
        profile.IsEditing = false;
        Assert.Throws<InvalidOperationException>(() => profile.MovePoint(middle, 30, 30));
    }

    [Fact]
    public void DragRejectsInvalidInputsAndHandlesCloselySpacedPoints()
    {
        var profile = new ProfileEditor(1, [new(20, 20), new(20.001, 20), new(20.002, 20)]) { IsEditing = true };
        profile.MovePoint(profile.Points[1], 0, 100);
        Validation.Profile(profile.Read());
        Assert.Equal(new CurvePoint(20.001, 20), profile.Points[1].Read());
        Assert.Throws<ArgumentException>(() => profile.MovePoint(profile.Points[0], double.NaN, 0));
        Assert.Throws<ArgumentException>(() => profile.MovePoint(profile.Points[0], 20, double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => profile.MovePoint(new(new(30, 40)), 30, 40));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TrashRemovesAnyPointAndKeepsAtLeastTwo(int index)
    {
        var profile = new ProfileEditor(1, [new(20, 20), new(40, 50), new(60, 80)]);
        var deleted = profile.Points[index];
        var remaining = profile.Points.Where(p => p != deleted).ToArray();
        var edits = 0;
        profile.Edited += () => edits++;
        Assert.True(deleted.RemoveCommand!.CanExecute(null));
        deleted.RemoveCommand.Execute(null);
        Assert.Equal(remaining, profile.Points);
        Assert.Equal(1, edits);
        Assert.All(profile.Points, point => Assert.False(point.RemoveCommand!.CanExecute(null)));
        Assert.False(deleted.RemoveCommand.CanExecute(null));
        deleted.Temperature = "21";
        Assert.Equal(1, edits);
        Validation.Profile(profile.Read());
    }

    [Fact]
    public void TelemetryCardsUpdatePropertiesWithoutReplacingItemsOrResettingCollections()
    {
        var sensor = new SensorDisplay("Temperature 1");
        var fan = new FanDisplay("Ventilateur 1 • Temperature 1");
        var sensors = new System.Collections.ObjectModel.ObservableCollection<SensorDisplay> { sensor };
        var fans = new System.Collections.ObjectModel.ObservableCollection<FanDisplay> { fan };
        var collectionChanges = 0;
        sensors.CollectionChanged += (_, _) => collectionChanges++;
        fans.CollectionChanged += (_, _) => collectionChanges++;
        var sensorChanges = new List<string?>();
        var fanChanges = new List<string?>();
        sensor.PropertyChanged += (_, args) => sensorChanges.Add(args.PropertyName);
        fan.PropertyChanged += (_, args) => fanChanges.Add(args.PropertyName);

        Assert.Equal("Waiting", sensor.Temperature);
        Assert.Equal("Waiting", fan.Rpm);
        sensor.Update("25,0 °C", "aucun défaut");
        fan.Update("1 200 tr/min", "PWM 40 %");
        sensor.Update("Indisponible", "CIRCUIT OUVERT");
        fan.Update("0 tr/min", "PWM 100 %");

        Assert.Same(sensor, sensors[0]);
        Assert.Same(fan, fans[0]);
        Assert.Equal(0, collectionChanges);
        Assert.Equal(new[] { "Temperature", "Details", "DisplayDetails", "Temperature", "Details", "DisplayDetails" }, sensorChanges);
        Assert.Equal(new[] { "Rpm", "Details", "DisplayDetails", "Rpm", "Details", "DisplayDetails" }, fanChanges);
        Assert.Equal("Indisponible", sensor.Temperature);
        Assert.Equal("0 tr/min", fan.Rpm);
        sensor.Update("Indisponible", "CIRCUIT OUVERT");
        fan.Update("0 tr/min", "PWM 100 %");
        Assert.Equal(6, sensorChanges.Count);
        Assert.Equal(6, fanChanges.Count);
    }

    [Fact]
    public void FrenchDecimalCommaForTemperatureAndDutyIsValidated()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var editor = new PointEditor(new(25.5, 40));
            Assert.Equal("25,5", editor.Temperature);
            Assert.Equal(new CurvePoint(25.5, 40), editor.Read());
            editor.Duty = "40,5";
            Assert.Equal(new CurvePoint(25.5, 40.5), editor.Read());
            editor.Duty = "invalide";
            Assert.Throws<ArgumentException>(() => editor.Read());
            editor.Temperature = "invalide";
            Assert.Throws<ArgumentException>(() => editor.Read());
        }
        finally { CultureInfo.CurrentCulture = old; }
    }
    [Fact]
    public void ProfileEditorAllowsOnlyTwoToEightPointsAndNotifiesChanges()
    {
        var profile = new ProfileEditor(1, [new(20, 20), new(60, 100)]);
        var edits = 0;
        profile.Edited += () => edits++;
        Assert.False(profile.RemoveCommand.CanExecute(null));
        for (var i = 0; i < 6; i++)
            profile.AddCommand.Execute(null);
        Assert.Equal(8, profile.Points.Count);
        Assert.False(profile.AddCommand.CanExecute(null));
        Assert.True(profile.RemoveCommand.CanExecute(null));
        profile.Points[0].Temperature = "21";
        Assert.Equal(7, edits);
        Validation.Profile(profile.Read());
        for (var i = 0; i < 6; i++)
            profile.RemoveCommand.Execute(null);
        Assert.Equal(2, profile.Points.Count);
        Assert.False(profile.RemoveCommand.CanExecute(null));
        Assert.Equal(2, profile.Read().Length);
    }
    [Fact]
    public void AddingInsertsOnTheWidestSegmentAndSelectsWithoutChangingTheCurveShape()
    {
        var profile = new ProfileEditor(1, [new(20, 20), new(40, 40), new(100, 100)]);
        profile.AddCommand.Execute(null);
        Assert.Equal(new CurvePoint(70, 70), profile.Points[2].Read());
        Assert.Same(profile.Points[2], profile.SelectedPoint);
        Assert.NotEmpty(profile.Curve);
        Validation.Profile(profile.Read());
        profile.SelectedPoint!.RemoveCommand!.Execute(null);
        Assert.Null(profile.SelectedPoint);
        Assert.Equal(3, profile.Points.Count);
        profile.Points[0].Temperature = "invalide";
        Assert.False(profile.AddCommand.CanExecute(null));
    }
    [Fact]
    public void FixedModeValidationAndAutoIgnoreInactiveDuty()
    {
        var fan = new FanEditor(3, new("fixed", 55));
        Assert.Equal(1, fan.ModeIndex);
        Assert.Equal(new FanSetting("fixed", 55), fan.Read());
        fan.Duty = "101";
        Assert.Throws<ArgumentException>(() => fan.Read());
        fan.ModeIndex = 0;
        fan.Duty = "ignoré";
        Assert.Equal(new FanSetting("auto", 0), fan.Read());
        fan.ModeIndex = -1;
        Assert.Throws<ArgumentException>(() => fan.Read());
    }
}
