using System.Globalization;
using System.Text;

namespace FanController.Core;

/// <summary>Raw edge measurements and both RPM estimators for a fan.</summary>
/// <param name="Fan">One-based fan identity.</param>
/// <param name="WindowUs">Time between counter snapshots.</param>
/// <param name="Pulses">All falling edges in the window.</param>
/// <param name="Intervals">Complete intervals ending in the window.</param>
/// <param name="MinUs">Shortest interval, or zero when absent.</param>
/// <param name="MaxUs">Longest interval, or zero when absent.</param>
/// <param name="SpanUs">Sum of complete intervals.</param>
/// <param name="RpmCount">RPM from the microsecond counting window.</param>
/// <param name="RpmPeriod">RPM from complete periods, or null when unavailable.</param>
/// <param name="RpmDisplay">RPM used by the dashboard; corrected starting in firmware 1.4.3.</param>
/// <param name="PwmPct">Applied PWM duty.</param>
/// <param name="TraceUs">Four preceding intervals, the short trigger interval, and four following intervals.</param>
/// <param name="TraceTriggerUs">Microsecond timestamp of the triggering edge, wrapping at 32 bits.</param>
/// <param name="TraceReferenceUs">Interval immediately preceding the trigger.</param>
/// <param name="RpmRawDisplay">Original millisecond counting estimate, absent in older firmware.</param>
/// <param name="RpmCorrected">Average over reconstructed periods, null when unavailable.</param>
/// <param name="CorrectedIntervals">Number of resolved periods in this update.</param>
/// <param name="CorrectedSpanUs">Sum of resolved period durations.</param>
/// <param name="MergedPairs">Number of confirmed pairs merged in this update.</param>
public sealed record TachFanReading(int Fan, uint WindowUs, uint Pulses, uint Intervals,
    uint MinUs, uint MaxUs, ulong SpanUs, double? RpmCount, double? RpmPeriod,
    double RpmDisplay, double PwmPct, uint[]? TraceUs = null,
    uint? TraceTriggerUs = null, uint? TraceReferenceUs = null,
    double? RpmRawDisplay = null, double? RpmCorrected = null,
    uint? CorrectedIntervals = null, ulong? CorrectedSpanUs = null, uint? MergedPairs = null);

/// <summary>A numbered snapshot of all four fan counters.</summary>
/// <param name="Sequence">Monotonically increasing window identity since boot.</param>
/// <param name="UptimeMs">Firmware uptime at the update.</param>
/// <param name="Fans">The four fan measurements.</param>
public sealed record TachWindow(ulong Sequence, uint UptimeMs, TachFanReading[] Fans);

/// <summary>A cursor response; no window means that acquisition has not advanced.</summary>
/// <param name="LatestSequence">Newest window identity on the controller.</param>
/// <param name="Dropped">The cursor is no longer in the retained history or the controller restarted.</param>
/// <param name="Window">The next window, if available.</param>
public sealed record TachReply(ulong LatestSequence, bool Dropped, TachWindow? Window);

/// <summary>Formats completed captures as locale-independent CSV.</summary>
public static class TachDiagnostics
{
    /// <summary>Checks cursor order and required measurement invariants.</summary>
    /// <param name="reply">The response received from firmware.</param>
    /// <param name="after">Previous window identity, or null for initialization.</param>
    /// <exception cref="InvalidDataException">Data is missing, invalid, or discontinuous.</exception>
    public static void Validate(TachReply reply, ulong? after)
    {
        if (after.HasValue && reply.LatestSequence < after.Value)
            throw new InvalidDataException($"TACH: controller sequence restarted (cursor {after.Value}, latest {reply.LatestSequence}).");
        if (reply.Dropped)
            throw new InvalidDataException($"TACH: diagnostic history overwritten (cursor {after}, latest {reply.LatestSequence}); BLE acquisition fell behind.");
        if (reply.Window is not { } window)
        {
            if (after.HasValue && reply.LatestSequence != after.Value)
                throw new InvalidDataException("TACH: missing diagnostic window.");
            return;
        }
        if (!after.HasValue || window.Sequence != after.Value + 1 || window.Sequence > reply.LatestSequence ||
            window.Fans is not { Length: 4 } ||
            !window.Fans.Select(f => f.Fan).Order().SequenceEqual(new[] { 1, 2, 3, 4 }))
            throw new InvalidDataException("TACH: invalid window sequence or fan identities.");
        foreach (var fan in window.Fans)
        {
            if (fan.WindowUs == 0 || fan.Intervals > fan.Pulses ||
                (fan.Intervals == 0 && (fan.MinUs != 0 || fan.MaxUs != 0 || fan.SpanUs != 0)) ||
                (fan.Intervals > 0 && (fan.MinUs > fan.MaxUs ||
                    fan.SpanUs < (ulong)fan.MinUs * fan.Intervals ||
                    fan.SpanUs > (ulong)fan.MaxUs * fan.Intervals)) ||
                fan.RpmCount is not { } count || !double.IsFinite(count) || count < 0 ||
                ((fan.Intervals != 0 && fan.SpanUs != 0) != fan.RpmPeriod.HasValue) ||
                (fan.RpmPeriod is { } period && (!double.IsFinite(period) || period < 0)) ||
                !double.IsFinite(fan.RpmDisplay) || fan.RpmDisplay < 0 ||
                !double.IsFinite(fan.PwmPct) || fan.PwmPct is < 0 or > 100)
                throw new InvalidDataException("TACH: invalid edge measurements or RPM.");
            if (fan.TraceUs is { } trace)
            {
                if (trace.Length != 9 || !fan.TraceTriggerUs.HasValue ||
                    fan.TraceReferenceUs is not { } reference || reference == 0 ||
                    trace[3] != reference || (ulong)trace[4] * 4 >= (ulong)reference * 3)
                    throw new InvalidDataException("TACH: invalid anomaly trace.");
            }
            else if (fan.TraceTriggerUs.HasValue || fan.TraceReferenceUs.HasValue)
                throw new InvalidDataException("TACH: missing anomaly trace.");
            if (fan.CorrectedIntervals is { } resolved)
            {
                if (fan.CorrectedSpanUs is not { } duration || fan.MergedPairs is not { } merged ||
                    merged > resolved || (resolved == 0 && duration != 0) ||
                    fan.RpmRawDisplay is not { } raw || !double.IsFinite(raw) || raw < 0 ||
                    ((duration != 0) != fan.RpmCorrected.HasValue) ||
                    (fan.RpmCorrected is { } rpm && (!double.IsFinite(rpm) || rpm < 0)))
                    throw new InvalidDataException("TACH: invalid corrected periods.");
            }
            else if (fan.CorrectedSpanUs.HasValue || fan.MergedPairs.HasValue ||
                fan.RpmRawDisplay.HasValue || fan.RpmCorrected.HasValue)
                throw new InvalidDataException("TACH: incomplete corrected periods.");
        }
    }

    /// <summary>Exports every window and fan without omitting absent period measurements.</summary>
    /// <param name="windows">Consecutive windows in acquisition order.</param>
    /// <returns>CSV with an invariant decimal point and explicit NA values.</returns>
    public static string ToCsv(IReadOnlyList<TachWindow> windows)
    {
        var csv = new StringBuilder("sequence,uptime_ms,fan,window_us,pulses,intervals,min_us,max_us,span_us,rpm_count,rpm_period,rpm_display,pwm_pct,trace_trigger_us,trace_reference_us,trace_us,rpm_raw_display,rpm_corrected,corrected_intervals,corrected_span_us,merged_pairs\r\n");
        foreach (var window in windows)
            foreach (var fan in window.Fans.OrderBy(f => f.Fan))
                csv.Append(CultureInfo.InvariantCulture,
                    $"{window.Sequence},{window.UptimeMs},{fan.Fan},{fan.WindowUs},{fan.Pulses},{fan.Intervals},{fan.MinUs},{fan.MaxUs},{fan.SpanUs},{Number(fan.RpmCount)},{Number(fan.RpmPeriod)},{fan.RpmDisplay:F2},{fan.PwmPct:F2},{fan.TraceTriggerUs?.ToString(CultureInfo.InvariantCulture) ?? "NA"},{fan.TraceReferenceUs?.ToString(CultureInfo.InvariantCulture) ?? "NA"},{Trace(fan.TraceUs)},{Number(fan.RpmRawDisplay)},{Number(fan.RpmCorrected)},{fan.CorrectedIntervals?.ToString(CultureInfo.InvariantCulture) ?? "NA"},{fan.CorrectedSpanUs?.ToString(CultureInfo.InvariantCulture) ?? "NA"},{fan.MergedPairs?.ToString(CultureInfo.InvariantCulture) ?? "NA"}\r\n");
        return csv.ToString();
    }

    private static string Number(double? value) => value?.ToString("F2", CultureInfo.InvariantCulture) ?? "NA";
    private static string Trace(uint[]? intervals) => intervals is null ? "NA" :
        string.Join(";", intervals.Select(value => value.ToString(CultureInfo.InvariantCulture)));
}
