using BunonBrain.Application.Attendance;

namespace BunonBrain.Application.Iot;

// Production counting. A counter message is turned into pulses, and pulses
// into pieces by the machine's counting rule: not every pulse is a garment.
//
//   INCREMENT n   n more pulses. Order doesn't matter, so a late increment
//                 is still counted (in the hour and shift it happened in).
//   ABSOLUTE v    the counter now reads v. The first reading is the baseline;
//                 afterwards v − last is counted. A reading older than the
//                 newest one is already included (OUT_OF_ORDER, 0). A lower
//                 reading means the counter restarted (power loss): v pulses
//                 since the restart are counted and the reset is noted. An
//                 implausible jump is stored as SUSPECT_JUMP and counts
//                 nothing until a person corrects it.
//   RESET v       the counter was reset to v (default 0): counts nothing.
//
// Duplicates never reach this code: the message is stored once per
// idempotency key.

public sealed record CounterState(long? LastValue, DateTimeOffset? LastOccurredAt, int PulseRemainder);

public sealed record CountingRule(string Mode, int PulsesPerPiece)
{
    public int Divisor => Mode == CountingModes.PulsesPerPiece ? Math.Max(1, PulsesPerPiece) : 1;
}

/// <param name="NewLastValue">The counter's last absolute value after this message (unchanged when it doesn't move it).</param>
/// <param name="NewLastOccurredAt">Time of the newest absolute reading after this message.</param>
public sealed record CountDecision(string Status, long Pulses, int Pieces, long? NewLastValue, DateTimeOffset? NewLastOccurredAt, int NewRemainder,
    bool ResetDetected, string? Note);

public static class ProductionCounting
{
    /// <summary>A jump larger than this in one absolute reading is not believed without a person's check.</summary>
    public const long MaxPlausibleJump = 50_000;

    public static CountDecision Apply(CounterState s, CountingRule rule, IotCounterReading reading, DateTimeOffset occurredAt)
    {
        switch (reading.Mode)
        {
            case CounterModes.Reset:
                return new CountDecision(CountStatuses.Reset, 0, 0, reading.Value, Newer(s.LastOccurredAt, occurredAt), 0, true,
                    $"Counter reset to {reading.Value}");

            case CounterModes.Increment:
            {
                var (pieces, remainder) = Pieces(rule, s.PulseRemainder, reading.Value);
                var late = s.LastOccurredAt is { } l && occurredAt < l;
                return new CountDecision(CountStatuses.Counted, reading.Value, pieces, s.LastValue, s.LastOccurredAt, remainder, false,
                    late ? "Arrived late; counted in the hour it happened" : null);
            }

            default: // ABSOLUTE
            {
                if (s.LastValue is null)
                    return new CountDecision(CountStatuses.Baseline, 0, 0, reading.Value, occurredAt, s.PulseRemainder, false,
                        $"First reading {reading.Value}: baseline, nothing counted");
                if (s.LastOccurredAt is { } last && occurredAt < last)
                    return new CountDecision(CountStatuses.OutOfOrder, 0, 0, s.LastValue, s.LastOccurredAt, s.PulseRemainder, false,
                        "Older than the newest reading; already included in it");
                var previous = s.LastValue.Value;
                long pulses;
                var reset = false;
                string? note = null;
                if (reading.Value >= previous) pulses = reading.Value - previous;
                else
                {
                    // The counter restarted (power loss / firmware reset): count from zero.
                    pulses = reading.Value;
                    reset = true;
                    note = $"Counter went back from {previous} to {reading.Value}: restart detected, {reading.Value} counted since the restart";
                }
                if (pulses > MaxPlausibleJump)
                    return new CountDecision(CountStatuses.SuspectJump, 0, 0, reading.Value, occurredAt, s.PulseRemainder, reset,
                        $"Jump of {pulses} pulses in one reading is not plausible; nothing counted, correct it manually if real");
                var (pieces, remainder) = Pieces(rule, s.PulseRemainder, pulses);
                return new CountDecision(CountStatuses.Counted, pulses, pieces, reading.Value, occurredAt, remainder, reset, note);
            }
        }
    }

    /// <summary>Pieces from pulses under a rule, carrying the remainder for PULSES_PER_PIECE.</summary>
    public static (int Pieces, int Remainder) Pieces(CountingRule rule, int remainder, long pulses)
    {
        if (rule.Mode == CountingModes.CycleNotPiece) return (0, 0);
        var total = remainder + pulses;
        var pieces = total / rule.Divisor;
        return ((int)Math.Min(int.MaxValue, pieces), (int)(total % rule.Divisor));
    }

    private static DateTimeOffset? Newer(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x > b ? x : b;
}

/// <summary>A factory shift as the calendar needs it.</summary>
public sealed record ShiftWindow(Guid Id, string Code, string Name, TimeOnly Start, TimeOnly End, int WorkingDays,
    IReadOnlyList<(TimeOnly? Start, int Minutes)> Breaks);

/// <param name="ShiftId">Null outside every shift (unscheduled / overtime).</param>
/// <param name="ProductionDate">Factory-local date the shift started (or the local date outside shifts).</param>
public sealed record ShiftSlot(Guid? ShiftId, string? ShiftCode, DateOnly ProductionDate, bool InBreak);

/// <summary>Which shift an instant falls in, in the factory's time zone. Overnight shifts belong to the day they start.</summary>
public sealed class ShiftCalendar
{
    private readonly IReadOnlyList<ShiftWindow> _shifts;
    private readonly TimeZoneInfo _tz;

    public ShiftCalendar(IReadOnlyList<ShiftWindow> shifts, TimeZoneInfo tz) { _shifts = shifts; _tz = tz; }

    public TimeZoneInfo TimeZone => _tz;

    public DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, _tz).DateTime;

    public ShiftSlot Locate(DateTimeOffset at)
    {
        var local = Local(at);
        var date = DateOnly.FromDateTime(local);
        var time = TimeOnly.FromDateTime(local);
        foreach (var s in _shifts.OrderBy(x => x.Start))
        {
            var overnight = s.End <= s.Start;
            DateOnly? started = null;
            if (!overnight)
            {
                if (time >= s.Start && time < s.End && AttendanceRules.WorksOn(s.WorkingDays, local.DayOfWeek)) started = date;
            }
            else if (time >= s.Start)
            {
                if (AttendanceRules.WorksOn(s.WorkingDays, local.DayOfWeek)) started = date;
            }
            else if (time < s.End)
            {
                var yesterday = date.AddDays(-1);
                if (AttendanceRules.WorksOn(s.WorkingDays, yesterday.DayOfWeek)) started = yesterday;
            }
            if (started is { } d)
                return new ShiftSlot(s.Id, s.Code, d, InBreak(s, d, local));
        }
        return new ShiftSlot(null, null, date, false);
    }

    private static bool InBreak(ShiftWindow s, DateOnly started, DateTime local)
    {
        foreach (var (start, minutes) in s.Breaks)
        {
            if (start is not { } bs || minutes <= 0) continue;
            // A break starting before the shift's start time belongs to the next calendar day of an overnight shift.
            var day = s.End <= s.Start && bs < s.Start ? started.AddDays(1) : started;
            var from = day.ToDateTime(bs);
            if (local >= from && local < from.AddMinutes(minutes)) return true;
        }
        return false;
    }

    /// <summary>UTC start of the factory-local day that contains <paramref name="date"/>.</summary>
    public DateTimeOffset DayStartUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, _tz.GetUtcOffset(local)).ToUniversalTime();
    }
}
