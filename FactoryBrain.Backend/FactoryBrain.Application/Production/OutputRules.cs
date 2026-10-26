using FactoryBrain.Domain.Entities;

namespace FactoryBrain.Application.Production;

/// <summary>One output record's values, before it is stored.</summary>
public sealed record OutputValues(
    DateTime? IntervalStart, DateTime? IntervalEnd, int? Quantity, int? ReworkQuantity, int? RejectQuantity,
    int? OperatorsPresent, double? DowntimeMinutes, string? DowntimeReason, string? Notes);

/// <summary>The checked record: UTC interval, its production date and shift.</summary>
public sealed record CheckedOutput(DateTime StartUtc, DateTime EndUtc, DateOnly ProductionDate, string? ShiftCode,
    int Quantity, int Rework, int Reject, int? OperatorsPresent, double? DowntimeMinutes, string? DowntimeReason, string? Notes);

/// <summary>
/// Validation of output records. Instants without an offset are taken as
/// UTC. An interval must end after it starts, be at most
/// <c>PRODUCTION_MAX_INTERVAL_HOURS</c> long, not end in the future (5 min
/// clock tolerance), not start before the backfill window, and not span two
/// shifts (split it at the shift boundary). Downtime can't exceed the
/// interval. The production date and shift come from the interval start.
/// </summary>
public static class OutputRules
{
    public const int MaxQuantity = 100_000;
    /// <summary>A record stored this long after its interval ended is reported as late-arriving.</summary>
    public static readonly TimeSpan LateThreshold = TimeSpan.FromHours(12);

    public static CheckedOutput? Check(OutputValues v, DateTime nowUtc, ProductionOptions options, ProductionCalendar calendar, Dictionary<string, string[]> errors)
    {
        void Add(string k, string m) => errors[k] = errors.TryGetValue(k, out var e) ? e.Append(m).ToArray() : new[] { m };

        if (v.IntervalStart is null) Add("intervalStart", "intervalStart is required (ISO 8601, UTC or with an offset).");
        if (v.IntervalEnd is null) Add("intervalEnd", "intervalEnd is required (ISO 8601, UTC or with an offset).");
        if (v.Quantity is null) Add("quantity", "quantity (good pieces) is required; use 0 when nothing was produced.");
        foreach (var (key, value) in new[] { ("quantity", v.Quantity), ("reworkQuantity", v.ReworkQuantity), ("rejectQuantity", v.RejectQuantity) })
            if (value is < 0 or > MaxQuantity) Add(key, $"{key} must be from 0 to {MaxQuantity}.");
        if (v.OperatorsPresent is { } ops && ops is < 1 or > 500) Add("operatorsPresent", "operatorsPresent must be from 1 to 500.");
        if (v.DowntimeMinutes is { } dt && (double.IsNaN(dt) || dt < 0)) Add("downtimeMinutes", "downtimeMinutes can't be negative.");
        if (v.DowntimeReason is { Length: > 200 }) Add("downtimeReason", "downtimeReason is limited to 200 characters.");
        if (v.Notes is { Length: > 500 }) Add("notes", "notes are limited to 500 characters.");
        if (v.IntervalStart is null || v.IntervalEnd is null) return null;

        var start = Truncate(ProductionCalendar.AsUtc(v.IntervalStart.Value));
        var end = Truncate(ProductionCalendar.AsUtc(v.IntervalEnd.Value));
        var minutes = (end - start).TotalMinutes;
        if (end <= start) Add("intervalEnd", "intervalEnd must be after intervalStart.");
        else if (minutes > options.MaxIntervalHours * 60) Add("intervalEnd", $"An interval is at most {options.MaxIntervalHours} hours long.");
        if (end > nowUtc + ProductionOptions.FutureTolerance) Add("intervalEnd", "Output can't be recorded for an interval that hasn't ended yet.");
        if (start < nowUtc.AddDays(-options.MaxBackfillDays)) Add("intervalStart", $"Output older than {options.MaxBackfillDays} days can't be recorded or corrected.");
        if (v.DowntimeMinutes is { } down && minutes > 0 && down > minutes + 1e-9) Add("downtimeMinutes", "Downtime can't be longer than the interval.");
        if (errors.Count > 0) return null;

        var (date, shift) = calendar.Classify(start);
        var (_, endShift) = calendar.Classify(end.AddTicks(-1));
        if (shift is not null && endShift is not null && shift != endShift)
        {
            Add("intervalEnd", $"The interval spans shifts {shift} and {endShift}. Record each shift's part separately.");
            return null;
        }
        return new CheckedOutput(start, end, date, shift, v.Quantity!.Value, v.ReworkQuantity ?? 0, v.RejectQuantity ?? 0,
            v.OperatorsPresent, v.DowntimeMinutes is { } d ? Math.Round(d, 2) : null, Trim(v.DowntimeReason), Trim(v.Notes));
    }

    /// <summary>True when two intervals share any time (touching ends do not overlap).</summary>
    public static bool Overlaps(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd) => aStart < bEnd && bStart < aEnd;

    /// <summary>Same line, order, stage, interval and counts: a repeat of the same record.</summary>
    public static bool SameRecord(ProductionOutputEvent e, CheckedOutput c) =>
        e.IntervalStartUtc == c.StartUtc && e.IntervalEndUtc == c.EndUtc && e.Quantity == c.Quantity && e.ReworkQuantity == c.Rework
        && e.RejectQuantity == c.Reject && e.OperatorsPresent == c.OperatorsPresent && Nullable.Equals(e.DowntimeMinutes, c.DowntimeMinutes);

    // Stored at whole seconds, so a repeated submission compares equal.
    private static DateTime Truncate(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
