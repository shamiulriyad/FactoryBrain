namespace BunonBrain.Application.Iot;

// Machine state is derived, never reported blindly:
//
//   * telemetry decides RUNNING / STOPPED / IDLE / ERROR (a reported state,
//     an error code, or power thresholds when the sensor only measures power);
//   * device health decides OFFLINE (no machine sensor reachable) and UNKNOWN
//     (reachable but silent, or just back online). A missing heartbeat is
//     never read as STOPPED;
//   * a person decides MAINTENANCE, which telemetry can't override;
//   * a message older than the newest applied one is stored but never moves
//     the state backwards (out-of-order delivery after a gateway reconnects).
//
// Conditions (temperature, vibration, unexpected stoppage, long idle, counter
// silence, flapping) are raised and cleared with hysteresis so a reading
// hovering at a threshold doesn't produce a storm of alerts.

/// <summary>The rules of one machine (from its monitoring profile).</summary>
public sealed record MachineRules(
    string CountingMode,
    int PulsesPerPiece,
    double? RunningPowerWatts,
    double? IdlePowerWatts,
    int? StaleAfterSeconds,
    int? StopAlertAfterSeconds,
    int? LongIdleAlertSeconds,
    double? MaxTemperatureC,
    double? MaxVibrationMmS,
    int? CounterSilenceAlertSeconds,
    int? FlapTransitionsPer5Min,
    bool AlertsEnabled)
{
    public const double TemperatureHysteresisC = 2.0;
    public const double VibrationClearRatio = 0.9;

    /// <summary>Defaults for a kind of machine. Thresholds that depend on the machine model (power, temperature, vibration) start off.</summary>
    public static MachineRules Defaults(string kind) => kind switch
    {
        MachineKinds.Pressing => new(CountingModes.Piece, 1, null, null, 900, 900, 1800, null, null, 1800, 12, true),
        MachineKinds.Cutting => new(CountingModes.Piece, 1, null, null, 900, 900, 1800, null, null, null, 12, true),
        _ => new(CountingModes.Piece, 1, null, null, 900, 600, 1200, null, null, 900, 12, true),
    };
}

/// <summary>What the engine needs to know about the machine now.</summary>
public sealed record MachineSnapshot(
    string State,
    DateTimeOffset Since,
    DateTimeOffset? LastStateEventAt,
    DateTimeOffset? LastTelemetryAt,
    bool InMaintenance,
    IReadOnlyCollection<string> ActiveConditions);

public sealed record TelemetryReading(DateTimeOffset OccurredAt, string? State, double? PowerWatts, double? TemperatureC, double? VibrationMmS, string? ErrorCode);

/// <param name="StateDevices">Devices of the machine whose telemetry decides its state.</param>
/// <param name="StateDevicesOnline">Of those, how many are effectively online now.</param>
public sealed record HealthFacts(int StateDevices, int StateDevicesOnline);

public sealed record TimedFacts(bool InWorkingShift, bool HasCounter, bool CounterOnline, DateTimeOffset? LastCountAt, int TransitionsLast5Min);

public sealed record StateChange(string ToState, string Reason, string Trigger);

public sealed record ConditionChange(string Condition, bool Active, string Detail, double? Value = null);

public static class MachineStateEngine
{
    /// <summary>The state telemetry implies, or null when it says nothing about the state.</summary>
    public static (string State, string Reason)? Implied(MachineRules r, TelemetryReading t)
    {
        if (t.ErrorCode is not null) return (MachineStates.Error, $"Sensor reported error {t.ErrorCode}");
        if (t.State is not null) return (t.State, $"Sensor reported {t.State}");
        if (t.PowerWatts is { } w && r.RunningPowerWatts is { } run)
        {
            if (w >= run) return (MachineStates.Running, $"Power {w:0.#} W ≥ running threshold {run:0.#} W");
            if (r.IdlePowerWatts is { } idle && w >= idle) return (MachineStates.Idle, $"Power {w:0.#} W between idle ({idle:0.#} W) and running ({run:0.#} W)");
            return (MachineStates.Stopped, $"Power {w:0.#} W below {(r.IdlePowerWatts ?? run):0.#} W");
        }
        return null;
    }

    /// <summary>Apply one telemetry message. <c>Late</c>: older than the newest applied message, so stored only.</summary>
    public static (StateChange? Change, bool Late) OnTelemetry(MachineSnapshot s, MachineRules r, TelemetryReading t)
    {
        if (s.LastStateEventAt is { } last && t.OccurredAt < last) return (null, true);
        if (s.InMaintenance) return (null, false);
        var implied = Implied(r, t);
        if (implied is null)
        {
            // The sensor is talking but says nothing about the state: no longer offline, not yet known.
            return s.State == MachineStates.Offline
                ? (new StateChange(MachineStates.Unknown, "Sensor reachable again; its telemetry carries no state", StateTriggers.Telemetry), false)
                : (null, false);
        }
        return implied.Value.State == s.State ? (null, false) : (new StateChange(implied.Value.State, implied.Value.Reason, StateTriggers.Telemetry), false);
    }

    /// <summary>State from device health and silence: OFFLINE when no sensor is reachable, UNKNOWN when reachable but silent.</summary>
    public static StateChange? OnHealth(MachineSnapshot s, MachineRules r, HealthFacts h, DateTimeOffset now)
    {
        if (s.InMaintenance || h.StateDevices == 0) return null;
        if (h.StateDevicesOnline == 0)
            return s.State == MachineStates.Offline ? null
                : new StateChange(MachineStates.Offline, "No machine sensor is reachable (device offline). This is not read as a stopped machine.", StateTriggers.DeviceOffline);
        if (s.State == MachineStates.Offline)
            return new StateChange(MachineStates.Unknown, "Sensor back online; waiting for its telemetry", StateTriggers.DeviceOnline);
        if (r.StaleAfterSeconds is { } stale && s.State is not (MachineStates.Unknown or MachineStates.Maintenance)
            && (s.LastTelemetryAt is null || now - s.LastTelemetryAt.Value > TimeSpan.FromSeconds(stale)))
            return new StateChange(MachineStates.Unknown, $"No telemetry for more than {stale} s while the sensor is online", StateTriggers.NoTelemetry);
        return null;
    }

    /// <summary>Temperature and vibration conditions from one reading (with hysteresis).</summary>
    public static IReadOnlyList<ConditionChange> ReadingConditions(MachineSnapshot s, MachineRules r, TelemetryReading t)
    {
        var changes = new List<ConditionChange>();
        var active = s.ActiveConditions;
        if (t.TemperatureC is { } temp)
        {
            var on = active.Contains(MachineConditions.AbnormalTemperature);
            if (!r.AlertsEnabled || r.MaxTemperatureC is null) { if (on) changes.Add(new(MachineConditions.AbnormalTemperature, false, "Temperature rule switched off", temp)); }
            else if (!on && temp > r.MaxTemperatureC)
                changes.Add(new(MachineConditions.AbnormalTemperature, true, $"Temperature {temp:0.0} °C above the limit {r.MaxTemperatureC:0.0} °C", temp));
            else if (on && temp <= r.MaxTemperatureC - MachineRules.TemperatureHysteresisC)
                changes.Add(new(MachineConditions.AbnormalTemperature, false, $"Temperature back to {temp:0.0} °C", temp));
        }
        if (t.VibrationMmS is { } vib)
        {
            var on = active.Contains(MachineConditions.HighVibration);
            if (!r.AlertsEnabled || r.MaxVibrationMmS is null) { if (on) changes.Add(new(MachineConditions.HighVibration, false, "Vibration rule switched off", vib)); }
            else if (!on && vib > r.MaxVibrationMmS)
                changes.Add(new(MachineConditions.HighVibration, true, $"Vibration {vib:0.0} mm/s above the limit {r.MaxVibrationMmS:0.0} mm/s", vib));
            else if (on && vib <= r.MaxVibrationMmS * MachineRules.VibrationClearRatio)
                changes.Add(new(MachineConditions.HighVibration, false, $"Vibration back to {vib:0.0} mm/s", vib));
        }
        return changes;
    }

    /// <summary>Conditions that depend on how long something has lasted (checked by the machine monitor).</summary>
    public static IReadOnlyList<ConditionChange> TimedConditions(MachineSnapshot s, MachineRules r, TimedFacts f, DateTimeOffset now)
    {
        var changes = new List<ConditionChange>();
        var active = s.ActiveConditions;
        var lasted = now - s.Since;

        void Rule(string condition, bool shouldBeOn, bool shouldBeOff, string onDetail, string offDetail)
        {
            var on = active.Contains(condition);
            if (!r.AlertsEnabled) { if (on) changes.Add(new(condition, false, "Machine alerts switched off")); return; }
            if (!on && shouldBeOn) changes.Add(new(condition, true, onDetail));
            else if (on && shouldBeOff) changes.Add(new(condition, false, offDetail));
        }

        var stop = r.StopAlertAfterSeconds;
        Rule(MachineConditions.UnexpectedStop,
            stop is { } st && s.State == MachineStates.Stopped && f.InWorkingShift && lasted >= TimeSpan.FromSeconds(st),
            stop is null || s.State != MachineStates.Stopped,
            $"Stopped for {Minutes(lasted)} during a scheduled shift", $"No longer stopped ({s.State})");

        var idle = r.LongIdleAlertSeconds;
        Rule(MachineConditions.LongIdle,
            idle is { } it && s.State == MachineStates.Idle && lasted >= TimeSpan.FromSeconds(it),
            idle is null || s.State != MachineStates.Idle,
            $"Idle for {Minutes(lasted)}", $"No longer idle ({s.State})");

        var silence = r.CounterSilenceAlertSeconds;
        var reference = f.LastCountAt is { } lc && lc > s.Since ? lc : s.Since;
        var silent = s.State == MachineStates.Running && silence is { } si && now - reference >= TimeSpan.FromSeconds(si);
        Rule(MachineConditions.CounterDisconnected,
            f.HasCounter && silence is not null && (!f.CounterOnline || silent),
            !f.HasCounter || silence is null || (f.CounterOnline && !silent),
            !f.CounterOnline ? "The production counter is offline" : $"Running for {Minutes(now - reference)} without a production count",
            "The production counter is reporting again");

        var flap = r.FlapTransitionsPer5Min;
        Rule(MachineConditions.UnusualTransition,
            flap is { } fl && f.TransitionsLast5Min >= fl,
            flap is null || f.TransitionsLast5Min < Math.Max(1, flap.Value / 2),
            $"{f.TransitionsLast5Min} state changes within 5 minutes (sensor or machine flapping)", "State changes back to normal");

        return changes;
    }

    private static string Minutes(TimeSpan t) => t.TotalMinutes >= 1 ? $"{Math.Floor(t.TotalMinutes):0} min" : $"{Math.Floor(t.TotalSeconds):0} s";
}
