using System.Text;
using System.Text.Json;
using FanController.Core;
using Xunit;

namespace FanController.Tests;

public sealed class FramingAndValidationTests
{
    [Fact]
    public void Utf8IsDecodedOnlyAfterLfAcrossEveryPossibleBoundary()
    {
        const string text = "{\"message\":\"Température élevée à 25 °C 🌡\"}";
        var bytes = Encoding.UTF8.GetBytes(text + "\r\n");
        for (var split = 1; split < bytes.Length; split++)
        {
            var framer = new LineFramer();
            Assert.Empty(framer.Push(bytes.AsSpan(0, split)));
            Assert.Equal(text, Assert.Single(framer.Push(bytes.AsSpan(split))));
        }
    }
    [Fact]
    public void MultipleLinesAndOneByteFragments()
    {
        var framer = new LineFramer();
        var output = new List<string>();
        foreach (var b in Encoding.UTF8.GetBytes("un\ndeux\n")) output.AddRange(framer.Push(new[] { b }));
        Assert.Equal(new[] { "un", "deux" }, output);
        Assert.Equal(new[] { "a", "b" }, framer.Push("a\nb\n"u8));
    }
    [Theory]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 10 })]
    [InlineData(new byte[] { 0xff, 10 })]
    public void CorruptFramesAreRejected(byte[] bytes) =>
        Assert.ThrowsAny<Exception>(() => new LineFramer().Push(bytes));

    [Fact]
    public void OversizedResponseIsRejected() =>
        Assert.Throws<InvalidDataException>(() => new LineFramer().Push(Enumerable.Repeat((byte)'x', 32769).ToArray()));

    [Fact]
    public void ValidProfilesAcceptBoundaryValuesAndFlatDuty()
    {
        Validation.Profile([new(-20, 0), new(120, 100)]);
        Validation.Profile(Enumerable.Range(0, 8).Select(i => new CurvePoint(i, 20)).ToArray());
    }
    public static IEnumerable<object[]> BadProfiles()
    {
        yield return [new CurvePoint[] { new(0, 0) }];
        yield return [Enumerable.Range(0, 9).Select(i => new CurvePoint(i, 0)).ToArray()];
        yield return [new CurvePoint[] { new(-21, 0), new(120, 100) }];
        yield return [new CurvePoint[] { new(0, 0), new(121, 100) }];
        yield return [new CurvePoint[] { new(0, -1), new(20, 100) }];
        yield return [new CurvePoint[] { new(0, 0), new(20, 101) }];
        yield return [new CurvePoint[] { new(10, 0), new(10, 100) }];
        yield return [new CurvePoint[] { new(10, 80), new(20, 70) }];
        yield return [new CurvePoint[] { new(double.NaN, 0), new(20, 100) }];
        yield return [new CurvePoint[] { new(0, 0), new(double.PositiveInfinity, 100) }];
        yield return [new CurvePoint[] { new(0, double.NaN), new(20, 100) }];
        yield return [new CurvePoint[] { new(0, 0), new(20, double.PositiveInfinity) }];
    }
    [Theory, MemberData(nameof(BadProfiles))]
    public void InvalidProfilesAreRejected(CurvePoint[] points) => Assert.Throws<ArgumentException>(() => Validation.Profile(points));

    [Theory]
    [InlineData(0, "auto", 0)]
    [InlineData(5, "fixed", 0)]
    [InlineData(1, "other", 0)]
    [InlineData(1, "fixed", 101)]
    public void InvalidFanIsRejected(int fan, string mode, int duty) =>
        Assert.Throws<ArgumentException>(() => Validation.Fan(fan, mode, duty));

    [Fact]
    public void NativeJsonContractUsesSnakeCase()
    {
        using var doc = JsonDocument.Parse("""{"fan":1,"sensor":1,"mode":"fixed","fixed_duty":45.5,"target_duty":66.25,"applied_duty":66.25,"rpm":2200.75,"pwm_ok":false,"boost":true}""");
        var fan = WireJson.Read<FanStatus>(doc.RootElement);
        Assert.Equal(45.5, fan.FixedDuty);
        Assert.Equal(66.25, fan.TargetDuty);
        Assert.Equal(66.25, fan.AppliedDuty);
        Assert.Equal(2200.75, fan.Rpm);
        Assert.False(fan.PwmOk);
        Assert.True(fan.Boost);
        Assert.Equal("""{"temperature":25.5,"duty":40}""", JsonSerializer.Serialize(new CurvePoint(25.5, 40), WireJson.Options));
    }

    [Fact]
    public void FractionalConfigurationAndMillivoltsAreReadWithoutRounding()
    {
        using var sensor = JsonDocument.Parse("""{"sensor":1,"millivolts":1649.0625,"temperature":25.0025,"fault":"none","safety":false}""");
        var value = WireJson.Read<SensorStatus>(sensor.RootElement);
        Assert.Equal(1649.0625, value.Millivolts);
        using var point = JsonDocument.Parse("""{"temperature":25.5,"duty":30.25}""");
        var curve = WireJson.Read<CurvePoint>(point.RootElement);
        Assert.Equal(30.25, curve.Duty);
        Validation.Profile([curve, new(65, 100)]);
        Validation.Fan(1, "fixed", 42.5);
        Assert.Throws<ArgumentException>(() => Validation.Fan(1, "fixed", double.NaN));
    }

    [Fact]
    public void AdvertisementAndScanResponseNamesAreMergedInBothOrders()
    {
        var index = new AdvertisementIndex();
        Assert.Contains("nom en attente", index.Add(42, "", true, -50)!.Name);
        Assert.Equal("ESP32-FanController", index.Add(42, "ESP32-FanController", false, -40)!.Name);
        Assert.Equal("ESP32-FanController", index.Add(42, "", true, -30)!.Name);
        Assert.Null(index.Add(43, "Autre appareil", false, -40));
        Assert.NotNull(index.Add(44, "ESP32-FanController", false, -40));
        Assert.Equal("ESP32-FanController", index.Add(44, "", true, -50)!.Name);
    }
}
