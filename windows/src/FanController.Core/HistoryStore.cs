using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanController.Core;

public sealed record HistorySample(DateTimeOffset Timestamp, uint UptimeMs, double?[] Temperatures, double[] Rpm,
    string? SessionId = null)
{
    [JsonIgnore] public int Segment { get; init; }
    public double? Value(int series) => series < 2 ? Temperatures[series] : Rpm[series - 2];

    public static HistorySample FromTelemetry(DateTimeOffset timestamp, Telemetry telemetry)
    {
        Validation.Status(telemetry);
        return new(timestamp, telemetry.UptimeMs,
            telemetry.Sensors.OrderBy(s => s.Sensor).Select(s => s.Temperature).ToArray(),
            telemetry.Fans.OrderBy(f => f.Fan).Select(f => f.Rpm).ToArray());
    }

    public void Validate()
    {
        if (Temperatures is not { Length: 2 } || Rpm is not { Length: 4 } ||
            Temperatures.Any(t => t is { } value && !double.IsFinite(value)) ||
            Rpm.Any(r => !double.IsFinite(r) || r < 0))
            throw new InvalidDataException("Échantillon d’historique invalide.");
    }
}

/// <summary>One append-only file per device/day; streaming reads keep the extrema of each time bucket.</summary>
public sealed class HistoryStore(string directory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateOnly? _lastPrune;
    public const int RetentionDays = 30;

    public IReadOnlyList<ulong> Devices()
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is { Length: 23 } && name[12] == '-')
            .Select(name => ulong.TryParse(name.AsSpan(0, 12), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var address) ? (ulong?)address : null)
            .OfType<ulong>().Distinct().Order().ToArray();
    }

    public async Task AppendAsync(ulong device, HistorySample sample, CancellationToken ct = default)
    {
        sample.Validate();
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(directory);
            var today = DateOnly.FromDateTime(sample.Timestamp.UtcDateTime);
            Prune(today);
            var path = Path.Combine(directory, $"{device:X12}-{today:yyyy-MM-dd}.jsonl");
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(sample, WireJson.Options) + "\n", ct);
        }
        finally { _gate.Release(); }
    }

    public async Task PruneAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { if (Directory.Exists(directory)) Prune(DateOnly.FromDateTime(now.UtcDateTime)); }
        finally { _gate.Release(); }
    }

    private void Prune(DateOnly today)
    {
        if (_lastPrune != today)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length == 23 &&
                    ulong.TryParse(name.AsSpan(0, 12), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) &&
                    name[12] == '-' &&
                    DateOnly.TryParseExact(name.AsSpan(13), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var day) && day < today.AddDays(-RetentionDays))
                    File.Delete(file);
            }
            _lastPrune = today;
        }
    }

    public async Task<IReadOnlyList<HistorySample>> ReadAsync(ulong device, DateTimeOffset from,
        DateTimeOffset until, CancellationToken ct = default)
    {
        if (until <= from) throw new ArgumentOutOfRangeException(nameof(until));
        await _gate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(directory)) return [];
            var buckets = new Bucket[600];
            var retentionStart = until.AddDays(-RetentionDays);
            if (from < retentionStart) from = retentionStart;
            HistorySample? previous = null;
            var segment = 0;
            foreach (var file in Directory.EnumerateFiles(directory, $"{device:X12}-*.jsonl").Order())
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length != 23 || !DateOnly.TryParseExact(name.AsSpan(13), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var day) ||
                    day < DateOnly.FromDateTime(from.UtcDateTime) ||
                    day > DateOnly.FromDateTime(until.UtcDateTime)) continue;
                var lineNumber = 0;
                using var reader = File.OpenText(file);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    lineNumber++;
                    HistorySample sample;
                    try
                    {
                        sample = JsonSerializer.Deserialize<HistorySample>(line, WireJson.Options)
                            ?? throw new InvalidDataException("Échantillon vide.");
                        sample.Validate();
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException)
                    {
                        throw new InvalidDataException($"Historique endommagé : {Path.GetFileName(file)}, ligne {lineNumber}.", ex);
                    }
                    if (sample.Timestamp < from || sample.Timestamp > until) continue;
                    if (previous is not null && (previous.SessionId != sample.SessionId ||
                        sample.Timestamp - previous.Timestamp > TimeSpan.FromSeconds(30))) segment++;
                    sample = sample with { Segment = segment };
                    previous = sample;
                    var index = Math.Clamp((int)((sample.Timestamp - from).TotalSeconds /
                        (until - from).TotalSeconds * buckets.Length), 0, buckets.Length - 1);
                    (buckets[index] ??= new()).Add(sample);
                }
            }
            return buckets.Where(b => b is not null).SelectMany(b => b.Samples())
                .DistinctBy(s => s.Timestamp).OrderBy(s => s.Timestamp).ToArray();
        }
        finally { _gate.Release(); }
    }

    private sealed class Bucket
    {
        private HistorySample? _first, _last;
        private readonly HistorySample?[] _min = new HistorySample?[6], _max = new HistorySample?[6];
        private readonly HistorySample?[] _missing = new HistorySample?[6];
        private readonly List<HistorySample> _small = [];
        private readonly List<HistorySample> _boundaries = [];
        private bool _large;
        public void Add(HistorySample sample)
        {
            if (!_large)
            {
                if (_small.Count < 32) _small.Add(sample);
                else { _large = true; _small.Clear(); }
            }
            _first ??= sample;
            if (_last is not null && _last.Segment != sample.Segment && _boundaries.Count < 32)
            {
                _boundaries.Add(_last);
                _boundaries.Add(sample);
            }
            _last = sample;
            for (var i = 0; i < 6; i++)
                if (sample.Value(i) is { } value)
                {
                    if (_min[i] is null || value < _min[i]!.Value(i)) _min[i] = sample;
                    if (_max[i] is null || value > _max[i]!.Value(i)) _max[i] = sample;
                }
                else _missing[i] ??= sample;
        }
        public IEnumerable<HistorySample> Samples() =>
            !_large ? _small : _min.Concat(_max).Concat(_missing).Concat(_boundaries)
                .Append(_first).Append(_last).OfType<HistorySample>();
    }
}
