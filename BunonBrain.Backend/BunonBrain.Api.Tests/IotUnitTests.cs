using System.Text;
using System.Text.Json;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Monitoring;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Machine monitoring and IoT rules without a database: the device message
/// contract (MQTT topics and payloads), machine state derivation, alert
/// conditions, production counting, shift attribution, metrics, device keys
/// and the per-device rate limit.
/// </summary>
public sealed class IotUnitTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly MachineRules Power = MachineRules.Defaults(MachineKinds.Sewing) with { RunningPowerWatts = 150, IdlePowerWatts = 20, MaxTemperatureC = 70, MaxVibrationMmS = 12 };

    private static IotTopic Topic(string channel = "telemetry", string subject = "device") =>
        IotTopics.Parse($"factory/MAIN/{subject}/{(subject == "device" ? "S-01" : "M-0101")}/{channel}", null)!;

    private static IotParseResult Parse(string json, IotTopic? topic = null) => IotMessageParser.Parse(json, topic ?? Topic());

    private static MachineSnapshot Snap(string state = MachineStates.Running, DateTimeOffset? since = null, DateTimeOffset? lastEvent = null,
        DateTimeOffset? lastTelemetry = null, bool maintenance = false, params string[] conditions) =>
        new(state, since ?? T.AddMinutes(-30), lastEvent, lastTelemetry ?? T.AddMinutes(-1), maintenance, conditions);

    private static TelemetryReading Reading(string? state = null, double? power = null, double? temp = null, double? vib = null, string? error = null, DateTimeOffset? at = null) =>
        new(at ?? T, state, power, temp, vib, error);

    // ─── Topics ────────────────────────────────────────────────────────────

    [Fact]
    public void Topics_follow_the_documented_structure_with_an_optional_prefix()
    {
        var t = IotTopics.Parse("factory/MAIN/device/RF-01/telemetry", null)!;
        Assert.Equal(("MAIN", IotTopic.DeviceSubject, "RF-01", "telemetry"), (t.FactoryRef, t.Subject, t.SubjectRef, t.Channel));
        var m = IotTopics.Parse("bunonbrain/factory/MAIN/machine/M-0101/production", "bunonbrain")!;
        Assert.Equal(IotTopic.MachineSubject, m.Subject);
        Assert.Equal("M-0101", m.SubjectRef);
        Assert.Null(IotTopics.Parse("factory/MAIN/machine/M-0101/telemetry", null));      // machines only publish production
        Assert.Null(IotTopics.Parse("factory/MAIN/device/RF-01/production", null));       // counts go on the machine topic
        Assert.Null(IotTopics.Parse("factory/+/device/RF-01/telemetry", null));           // wildcards are never a message topic
        Assert.Null(IotTopics.Parse("factory/MAIN/device//telemetry", null));
        Assert.Null(IotTopics.Parse("other/factory/MAIN/device/RF-01/telemetry", "bunonbrain"));
        Assert.Equal(4, IotTopics.Subscriptions("bb").Count);
        Assert.All(IotTopics.Subscriptions("bb"), s => Assert.StartsWith("bb/factory/+/", s));
        Assert.Equal("factory/MAIN/device/S-01/status", IotTopics.Device(null, "MAIN", "S-01", IotTopics.Status));
    }

    // ─── Payload parsing ───────────────────────────────────────────────────

    [Fact]
    public void A_valid_telemetry_message_is_parsed_with_the_topic_device_and_factory()
    {
        var r = Parse("""{"schema":"bunonbrain.iot.v1","messageId":"m-1","occurredAt":"2026-11-15T08:00:00Z","state":"running","powerWatts":312.5,"temperatureC":41.2,"vibrationMmS":3.1,"metrics":{"rpm":3500},"note":"x"}""");
        Assert.True(r.Ok, r.Error);
        var m = r.Message!;
        Assert.Equal(IotMessageTypes.Telemetry, m.Type);
        Assert.Equal("S-01", m.DeviceRef);
        Assert.Equal("MAIN", m.FactoryRef);
        Assert.Equal(MachineStates.Running, m.State);
        Assert.Equal(312.5, m.PowerWatts);
        Assert.Equal(3500, m.Metrics!["rpm"]);
        Assert.Equal(T, m.OccurredAt);
        Assert.Equal(new[] { "note" }, m.IgnoredFields);
    }

    [Theory]
    [InlineData("""{"state":"RUNNING"}""", "message_id_required")]
    [InlineData("""{"messageId":"m 1","state":"RUNNING"}""", "message_id_required")]
    [InlineData("""{"messageId":"m-1","state":"FLYING"}""", "state_invalid")]
    [InlineData("""{"messageId":"m-1","state":"OFFLINE"}""", "state_invalid")]
    [InlineData("""{"messageId":"m-1","powerWatts":-5}""", "reading_invalid")]
    [InlineData("""{"messageId":"m-1","powerWatts":"a lot"}""", "reading_invalid")]
    [InlineData("""{"messageId":"m-1","temperatureC":900}""", "reading_invalid")]
    [InlineData("""{"messageId":"m-1"}""", "telemetry_empty")]
    [InlineData("""{"messageId":"m-1","type":"production","count":1}""", "type_topic_mismatch")]
    [InlineData("""{"messageId":"m-1","deviceId":"S-02","state":"RUNNING"}""", "device_mismatch")]
    [InlineData("""{"messageId":"m-1","factoryId":"OTHER","state":"RUNNING"}""", "factory_mismatch")]
    [InlineData("""{"messageId":"m-1","schema":"acme.v9","state":"RUNNING"}""", "schema_unsupported")]
    [InlineData("""{"messageId":"m-1","occurredAt":"yesterday","state":"RUNNING"}""", "occurred_at_invalid")]
    [InlineData("""{"messageId":"m-1","metrics":{"rpm":"fast"}}""", "metrics_invalid")]
    [InlineData("""{"messageId":"m-1","errorCode":"bad code!"}""", "error_code_invalid")]
    [InlineData("""[1,2]""", "payload_not_object")]
    [InlineData("""{"messageId":""", "payload_not_json")]
    public void Invalid_payloads_are_rejected_with_a_reason(string json, string code)
    {
        var r = Parse(json);
        Assert.False(r.Ok);
        Assert.Equal(code, r.Code);
    }

    [Fact]
    public void Oversized_and_empty_messages_are_rejected()
    {
        var big = "{\"messageId\":\"m-1\",\"message\":\"" + new string('x', 9000) + "\"}";
        Assert.Equal("payload_too_large", IotMessageParser.Parse(Encoding.UTF8.GetBytes(big), Topic()).Code);
        Assert.Equal("payload_empty", IotMessageParser.Parse(Array.Empty<byte>(), Topic()).Code);
    }

    [Fact]
    public void Status_and_heartbeat_share_the_status_topic()
    {
        Assert.Equal(IotMessageTypes.Status, Parse("""{"messageId":"s-1","status":"offline"}""", Topic("status")).Message!.Type);
        Assert.Equal(IotDeviceStatuses.Offline, Parse("""{"messageId":"s-1","status":"offline"}""", Topic("status")).Message!.Status);
        Assert.Equal(IotMessageTypes.Heartbeat, Parse("""{"messageId":"h-1","type":"heartbeat","metrics":{"rssi":-60}}""", Topic("status")).Message!.Type);
        Assert.Equal("status_required", Parse("""{"messageId":"s-1"}""", Topic("status")).Code);
        Assert.Equal(IotMessageTypes.Heartbeat, Parse("""{"messageId":"h-2"}""", Topic("heartbeat")).Message!.Type);
    }

    [Fact]
    public void Counter_messages_accept_three_forms_and_check_their_values()
    {
        var topic = Topic("production", "machine");
        var obj = Parse("""{"messageId":"c-1","deviceId":"PC-01","counter":{"mode":"absolute","value":1520}}""", topic).Message!;
        Assert.Equal(new IotCounterReading(CounterModes.Absolute, 1520), obj.Counter);
        Assert.Equal("M-0101", obj.MachineRef);
        Assert.Equal("PC-01", obj.DeviceRef);
        Assert.Equal(new IotCounterReading(CounterModes.Increment, 2), Parse("""{"messageId":"c-2","deviceId":"PC-01","count":2}""", topic).Message!.Counter);
        Assert.Equal(new IotCounterReading(CounterModes.Absolute, 7), Parse("""{"messageId":"c-3","deviceId":"PC-01","counterValue":7}""", topic).Message!.Counter);
        Assert.Equal(new IotCounterReading(CounterModes.Reset, 0), Parse("""{"messageId":"c-4","deviceId":"PC-01","counter":{"mode":"RESET"}}""", topic).Message!.Counter);
        Assert.Equal("device_required", Parse("""{"messageId":"c-5","count":1}""", topic).Code);
        Assert.Equal("counter_invalid", Parse("""{"messageId":"c-6","deviceId":"PC-01","count":0}""", topic).Code);
        Assert.Equal("counter_invalid", Parse("""{"messageId":"c-7","deviceId":"PC-01","count":1.5}""", topic).Code);
        Assert.Equal("counter_invalid", Parse("""{"messageId":"c-8","deviceId":"PC-01","counter":{"mode":"absolute","value":-1}}""", topic).Code);
        Assert.Equal("counter_invalid", Parse("""{"messageId":"c-9","deviceId":"PC-01"}""", topic).Code);
        Assert.Equal("machine_mismatch", Parse("""{"messageId":"c-10","deviceId":"PC-01","machineId":"M-9","count":1}""", topic).Code);
    }

    [Fact]
    public void Http_posts_take_the_type_from_the_route_and_the_device_from_the_key()
    {
        using var doc = JsonDocument.Parse("""{"messageId":"m-1","state":"IDLE"}""");
        var r = IotMessageParser.Parse(doc.RootElement, null, IotTopics.Telemetry, "6a4f2c3e-0000-4000-8000-000000000001");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("6a4f2c3e-0000-4000-8000-000000000001", r.Message!.DeviceRef);
        using var wrong = JsonDocument.Parse("""{"messageId":"m-1","type":"production","count":1}""");
        Assert.Equal("type_topic_mismatch", IotMessageParser.Parse(wrong.RootElement, null, IotTopics.Telemetry, "x").Code);
        using var noType = JsonDocument.Parse("""{"messageId":"m-1","deviceId":"S-01","state":"IDLE"}""");
        Assert.Equal("type_required", IotMessageParser.Parse(noType.RootElement, null).Code);
    }

    // ─── Machine state ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(320.0, MachineStates.Running)]
    [InlineData(150.0, MachineStates.Running)]
    [InlineData(45.0, MachineStates.Idle)]
    [InlineData(3.0, MachineStates.Stopped)]
    public void Power_thresholds_decide_the_state_of_a_power_only_sensor(double watts, string expected)
    {
        var (change, late) = MachineStateEngine.OnTelemetry(Snap(MachineStates.Unknown), Power, Reading(power: watts));
        Assert.False(late);
        Assert.Equal(expected, change?.ToState ?? MachineStates.Unknown);
    }

    [Fact]
    public void Reported_states_and_error_codes_win_over_power()
    {
        Assert.Equal(MachineStates.Error, MachineStateEngine.OnTelemetry(Snap(), Power, Reading(power: 320, error: "THREAD_BREAK")).Change!.ToState);
        Assert.Equal(MachineStates.Stopped, MachineStateEngine.OnTelemetry(Snap(), Power, Reading(MachineStates.Stopped, power: 320)).Change!.ToState);
        Assert.Null(MachineStateEngine.OnTelemetry(Snap(), Power, Reading(MachineStates.Running)).Change); // same state: no transition
        // Without power thresholds a power reading says nothing about the state.
        Assert.Null(MachineStateEngine.OnTelemetry(Snap(), MachineRules.Defaults(MachineKinds.Sewing), Reading(power: 3)).Change);
    }

    [Fact]
    public void Late_telemetry_is_stored_but_never_moves_the_state_back()
    {
        var s = Snap(MachineStates.Running, lastEvent: T);
        var (change, late) = MachineStateEngine.OnTelemetry(s, Power, Reading(MachineStates.Stopped, at: T.AddMinutes(-5)));
        Assert.True(late);
        Assert.Null(change);
    }

    [Fact]
    public void Maintenance_is_a_human_decision_that_telemetry_cannot_override()
    {
        var (change, late) = MachineStateEngine.OnTelemetry(Snap(MachineStates.Maintenance, maintenance: true), Power, Reading(MachineStates.Running));
        Assert.False(late);
        Assert.Null(change);
        Assert.Null(MachineStateEngine.OnHealth(Snap(MachineStates.Maintenance, maintenance: true), Power, new HealthFacts(1, 0), T));
    }

    [Fact]
    public void A_missing_heartbeat_is_offline_or_unknown_never_stopped()
    {
        var offline = MachineStateEngine.OnHealth(Snap(MachineStates.Running), Power, new HealthFacts(1, 0), T)!;
        Assert.Equal(MachineStates.Offline, offline.ToState);
        Assert.Equal(StateTriggers.DeviceOffline, offline.Trigger);
        Assert.Contains("not read as a stopped machine", offline.Reason);
        var back = MachineStateEngine.OnHealth(Snap(MachineStates.Offline), Power, new HealthFacts(1, 1), T)!;
        Assert.Equal(MachineStates.Unknown, back.ToState);
        var stale = MachineStateEngine.OnHealth(Snap(MachineStates.Running, lastTelemetry: T.AddMinutes(-20)), Power, new HealthFacts(1, 1), T)!;
        Assert.Equal(MachineStates.Unknown, stale.ToState);
        Assert.Equal(StateTriggers.NoTelemetry, stale.Trigger);
        Assert.Null(MachineStateEngine.OnHealth(Snap(MachineStates.Running, lastTelemetry: T.AddMinutes(-20)), Power with { StaleAfterSeconds = null }, new HealthFacts(1, 1), T));
        Assert.Null(MachineStateEngine.OnHealth(Snap(MachineStates.Running), Power, new HealthFacts(0, 0), T)); // no sensor: nothing to derive
        Assert.All(new[] { offline, back, stale }, c => Assert.NotEqual(MachineStates.Stopped, c.ToState));
    }

    [Fact]
    public void A_sensor_that_talks_again_without_a_state_ends_offline_as_unknown()
    {
        var c = MachineStateEngine.OnTelemetry(Snap(MachineStates.Offline), MachineRules.Defaults(MachineKinds.Sewing), Reading(temp: 40)).Change!;
        Assert.Equal(MachineStates.Unknown, c.ToState);
    }

    // ─── Conditions ────────────────────────────────────────────────────────

    [Fact]
    public void Temperature_and_vibration_alerts_raise_and_clear_with_hysteresis()
    {
        var hot = MachineStateEngine.ReadingConditions(Snap(), Power, Reading(temp: 75)).Single();
        Assert.Equal((MachineConditions.AbnormalTemperature, true), (hot.Condition, hot.Active));
        // Just below the limit is still hot (2 °C hysteresis), well below clears.
        Assert.Empty(MachineStateEngine.ReadingConditions(Snap(conditions: MachineConditions.AbnormalTemperature), Power, Reading(temp: 69)));
        Assert.False(MachineStateEngine.ReadingConditions(Snap(conditions: MachineConditions.AbnormalTemperature), Power, Reading(temp: 67.5)).Single().Active);
        Assert.True(MachineStateEngine.ReadingConditions(Snap(), Power, Reading(vib: 15)).Single().Active);
        Assert.Empty(MachineStateEngine.ReadingConditions(Snap(conditions: MachineConditions.HighVibration), Power, Reading(vib: 11)));
        Assert.False(MachineStateEngine.ReadingConditions(Snap(conditions: MachineConditions.HighVibration), Power, Reading(vib: 10)).Single().Active);
        // No limit configured: no alert.
        Assert.Empty(MachineStateEngine.ReadingConditions(Snap(), MachineRules.Defaults(MachineKinds.Sewing), Reading(temp: 300)));
        // Alerts switched off clear what was raised.
        Assert.False(MachineStateEngine.ReadingConditions(Snap(conditions: MachineConditions.AbnormalTemperature), Power with { AlertsEnabled = false }, Reading(temp: 90)).Single().Active);
    }

    [Fact]
    public void A_stoppage_is_unexpected_only_during_a_scheduled_shift()
    {
        var stopped = Snap(MachineStates.Stopped, since: T.AddMinutes(-15));
        var inShift = new TimedFacts(true, false, false, null, 1);
        var offShift = inShift with { InWorkingShift = false };
        Assert.Contains(MachineStateEngine.TimedConditions(stopped, Power, inShift, T), c => c is { Condition: MachineConditions.UnexpectedStop, Active: true });
        Assert.DoesNotContain(MachineStateEngine.TimedConditions(stopped, Power, offShift, T), c => c.Condition == MachineConditions.UnexpectedStop);
        Assert.DoesNotContain(MachineStateEngine.TimedConditions(Snap(MachineStates.Stopped, since: T.AddMinutes(-2)), Power, inShift, T),
            c => c.Condition == MachineConditions.UnexpectedStop);
        var cleared = MachineStateEngine.TimedConditions(Snap(MachineStates.Running, conditions: MachineConditions.UnexpectedStop), Power, inShift, T);
        Assert.Contains(cleared, c => c is { Condition: MachineConditions.UnexpectedStop, Active: false });
    }

    [Fact]
    public void Long_idle_counter_silence_and_flapping_are_detected()
    {
        var idle = MachineStateEngine.TimedConditions(Snap(MachineStates.Idle, since: T.AddMinutes(-25)), Power, new TimedFacts(true, false, false, null, 1), T);
        Assert.Contains(idle, c => c is { Condition: MachineConditions.LongIdle, Active: true });

        var running = Snap(MachineStates.Running, since: T.AddMinutes(-40));
        var silent = MachineStateEngine.TimedConditions(running, Power, new TimedFacts(true, true, true, T.AddMinutes(-20), 1), T);
        Assert.Contains(silent, c => c is { Condition: MachineConditions.CounterDisconnected, Active: true });
        var counterOffline = MachineStateEngine.TimedConditions(Snap(MachineStates.Stopped), Power, new TimedFacts(true, true, false, T, 1), T);
        Assert.Contains(counterOffline, c => c is { Condition: MachineConditions.CounterDisconnected, Active: true });
        var counting = MachineStateEngine.TimedConditions(Snap(MachineStates.Running, since: T.AddMinutes(-40), conditions: MachineConditions.CounterDisconnected), Power,
            new TimedFacts(true, true, true, T.AddMinutes(-1), 1), T);
        Assert.Contains(counting, c => c is { Condition: MachineConditions.CounterDisconnected, Active: false });
        // A machine without a counter never raises it.
        Assert.DoesNotContain(MachineStateEngine.TimedConditions(running, Power, new TimedFacts(true, false, false, null, 1), T), c => c.Condition == MachineConditions.CounterDisconnected);

        var flapping = MachineStateEngine.TimedConditions(Snap(), Power, new TimedFacts(true, false, false, null, 14), T);
        Assert.Contains(flapping, c => c is { Condition: MachineConditions.UnusualTransition, Active: true });
        Assert.Contains(MachineStateEngine.TimedConditions(Snap(conditions: MachineConditions.UnusualTransition), Power, new TimedFacts(true, false, false, null, 2), T),
            c => c is { Condition: MachineConditions.UnusualTransition, Active: false });
    }

    // ─── Production counting ───────────────────────────────────────────────

    private static readonly CountingRule Piece = new(CountingModes.Piece, 1);

    [Fact]
    public void Increments_count_even_when_late_and_pulses_per_piece_carries_the_remainder()
    {
        var d = ProductionCounting.Apply(new CounterState(null, T, 0), Piece, new IotCounterReading(CounterModes.Increment, 3), T.AddMinutes(-10));
        Assert.Equal((CountStatuses.Counted, 3L, 3), (d.Status, d.Pulses, d.Pieces));
        Assert.Contains("late", d.Note);
        var rule = new CountingRule(CountingModes.PulsesPerPiece, 2);
        var a = ProductionCounting.Apply(new CounterState(null, null, 0), rule, new IotCounterReading(CounterModes.Increment, 3), T);
        Assert.Equal((1, 1), (a.Pieces, a.NewRemainder));
        var b = ProductionCounting.Apply(new CounterState(null, null, a.NewRemainder), rule, new IotCounterReading(CounterModes.Increment, 1), T);
        Assert.Equal((1, 0), (b.Pieces, b.NewRemainder));
        var cycles = ProductionCounting.Apply(new CounterState(null, null, 0), new CountingRule(CountingModes.CycleNotPiece, 1), new IotCounterReading(CounterModes.Increment, 40), T);
        Assert.Equal((40L, 0), (cycles.Pulses, cycles.Pieces));
    }

    [Fact]
    public void Absolute_readings_count_differences_and_handle_restarts_and_out_of_order_readings()
    {
        var baseline = ProductionCounting.Apply(new CounterState(null, null, 0), Piece, new IotCounterReading(CounterModes.Absolute, 1000), T);
        Assert.Equal((CountStatuses.Baseline, 0, (long?)1000), (baseline.Status, baseline.Pieces, baseline.NewLastValue));
        var next = ProductionCounting.Apply(new CounterState(1000, T, 0), Piece, new IotCounterReading(CounterModes.Absolute, 1012), T.AddMinutes(1));
        Assert.Equal((CountStatuses.Counted, 12), (next.Status, next.Pieces));
        var old = ProductionCounting.Apply(new CounterState(1012, T.AddMinutes(1), 0), Piece, new IotCounterReading(CounterModes.Absolute, 1005), T.AddSeconds(30));
        Assert.Equal((CountStatuses.OutOfOrder, 0, (long?)1012), (old.Status, old.Pieces, old.NewLastValue));
        var restart = ProductionCounting.Apply(new CounterState(1012, T.AddMinutes(1), 0), Piece, new IotCounterReading(CounterModes.Absolute, 4), T.AddMinutes(2));
        Assert.Equal((CountStatuses.Counted, 4, true), (restart.Status, restart.Pieces, restart.ResetDetected));
        var jump = ProductionCounting.Apply(new CounterState(10, T, 0), Piece, new IotCounterReading(CounterModes.Absolute, 10 + ProductionCounting.MaxPlausibleJump + 1), T.AddMinutes(1));
        Assert.Equal((CountStatuses.SuspectJump, 0), (jump.Status, jump.Pieces));
        var reset = ProductionCounting.Apply(new CounterState(1012, T, 1), Piece, new IotCounterReading(CounterModes.Reset, 0), T.AddMinutes(3));
        Assert.Equal((CountStatuses.Reset, 0, (long?)0, 0), (reset.Status, reset.Pieces, reset.NewLastValue, reset.NewRemainder));
    }

    // ─── Shift calendar ────────────────────────────────────────────────────

    private static readonly TimeZoneInfo Dhaka = TimeZoneInfo.CreateCustomTimeZone("BD", TimeSpan.FromHours(6), "BD", "BD");
    private static readonly Guid Day = Guid.NewGuid();
    private static readonly Guid Night = Guid.NewGuid();
    private static readonly ShiftCalendar Calendar = new(new[]
    {
        new ShiftWindow(Day, "DAY", "Day", new TimeOnly(8, 0), new TimeOnly(17, 0), 0b0111111, new[] { ((TimeOnly?)new TimeOnly(13, 0), 60) }),
        new ShiftWindow(Night, "NIGHT", "Night", new TimeOnly(20, 0), new TimeOnly(4, 0), 0b0111111, Array.Empty<(TimeOnly?, int)>()),
    }, Dhaka);

    [Fact]
    public void Counts_are_attributed_to_the_shift_and_production_day_they_happened_in()
    {
        // 2026-11-15 is a Sunday; local time = UTC + 6.
        var morning = Calendar.Locate(new DateTimeOffset(2026, 11, 15, 4, 0, 0, TimeSpan.Zero));      // 10:00 local
        Assert.Equal((Day, "DAY", new DateOnly(2026, 11, 15), false), (morning.ShiftId!.Value, morning.ShiftCode, morning.ProductionDate, morning.InBreak));
        var lunch = Calendar.Locate(new DateTimeOffset(2026, 11, 15, 7, 30, 0, TimeSpan.Zero));        // 13:30 local
        Assert.True(lunch.InBreak);
        var lateNight = Calendar.Locate(new DateTimeOffset(2026, 11, 15, 20, 30, 0, TimeSpan.Zero));   // 02:30 local on the 16th
        Assert.Equal((Night, new DateOnly(2026, 11, 15)), (lateNight.ShiftId!.Value, lateNight.ProductionDate));
        var evening = Calendar.Locate(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));      // 18:00 local
        Assert.Null(evening.ShiftId);
        Assert.Equal(new DateOnly(2026, 11, 15), evening.ProductionDate);
        // Saturday (bit 6) is not a working day here.
        Assert.Null(Calendar.Locate(new DateTimeOffset(2026, 11, 21, 4, 0, 0, TimeSpan.Zero)).ShiftId);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 18, 0, 0, TimeSpan.Zero), Calendar.DayStartUtc(new DateOnly(2026, 11, 15)));
    }

    // ─── Metrics ───────────────────────────────────────────────────────────

    [Fact]
    public void State_durations_are_clipped_to_the_period_and_unknown_before_any_data()
    {
        var from = T;
        var to = T.AddHours(1);
        var totals = IotMetrics.Durations(MachineStates.Running, new[]
        {
            (T.AddMinutes(20), MachineStates.Idle), (T.AddMinutes(30), MachineStates.Stopped), (T.AddMinutes(40), MachineStates.Running),
            (T.AddMinutes(-5), MachineStates.Error),  // before the period: ignored
            (T.AddHours(2), MachineStates.Error),     // after it: ignored
        }, from, to);
        Assert.Equal(40 * 60, totals.Running);
        Assert.Equal(10 * 60, totals.Idle);
        Assert.Equal(10 * 60, totals.Downtime);
        Assert.Equal(3600, totals.CoveredSeconds);
        var none = IotMetrics.Durations(null, Array.Empty<(DateTimeOffset, string)>(), from, to);
        Assert.Equal(3600, none.Of(MachineStates.Unknown));
        Assert.Equal(60.0, IotMetrics.RatePerRunningHour(40, 40 * 60));
        Assert.Null(IotMetrics.RatePerRunningHour(5, 30));
    }

    // ─── Alerts ────────────────────────────────────────────────────────────

    private static AlertContext Machine(string type, string? to, string? from = null, string? condition = null) =>
        new(type, false, to, from, null, null, null, null, null, null, null, null, null, null, null, Guid.NewGuid(), "M-0101", condition);

    [Fact]
    public void Machine_events_raise_and_recover_machine_alerts_through_the_existing_lifecycle()
    {
        var offline = MonitoringRules.AlertFor(Machine(MonitoringEventTypes.MachineStateChanged, "OFFLINE"))!;
        Assert.Equal(AlertTypes.MachineOffline, offline.AlertType);
        Assert.Contains("does not mean it stopped", offline.Description);
        Assert.Equal(AlertSeverities.High, MonitoringRules.AlertFor(Machine(MonitoringEventTypes.MachineStateChanged, "ERROR"))!.Severity);
        Assert.Null(MonitoringRules.AlertFor(Machine(MonitoringEventTypes.MachineStateChanged, "STOPPED")));
        Assert.Null(MonitoringRules.AlertFor(Machine(MonitoringEventTypes.MachineStateChanged, "IDLE")));
        var ctx = Machine(MonitoringEventTypes.MachineStateChanged, "RUNNING", "OFFLINE");
        Assert.Equal($"{AlertTypes.MachineOffline}:{ctx.MachineId}", MonitoringRules.RecoveryDedupKey(ctx));
        foreach (var c in MachineConditions.All)
        {
            var spec = MonitoringRules.AlertFor(Machine(MonitoringEventTypes.MachineCondition, "ACTIVE", condition: c))!;
            Assert.Contains(spec.AlertType, AlertTypes.Machine);
            Assert.Contains("says nothing about the operator", spec.Description);
            var clear = Machine(MonitoringEventTypes.MachineCondition, "CLEARED", condition: c);
            Assert.Equal($"{MonitoringRules.MachineConditionAlert(c)}:{clear.MachineId}", MonitoringRules.RecoveryDedupKey(clear));
        }
        Assert.All(AlertTypes.Machine, t => Assert.Equal(MonitoringCategories.System, AlertTypes.CategoryOf(t)));
        Assert.True(MonitoringRules.Requirements(MonitoringEventTypes.MachineStateChanged).Machine);
        Assert.False(MonitoringRules.Requirements(MonitoringEventTypes.MachineCondition).WorkerAllowed);
        Assert.Equal("Machine M-0101 is now OFFLINE (was RUNNING)", MonitoringRules.Summary(MonitoringEventTypes.MachineStateChanged, false, null, null, null, null, null,
            "OFFLINE", null, null, false, machineCode: "M-0101", fromStatus: "RUNNING"));
        Assert.StartsWith("SIMULATED", MonitoringRules.Summary(MonitoringEventTypes.MachineCondition, true, null, null, null, null, null, "ACTIVE", null, null, false,
            reason: "hot", machineCode: "M-1", condition: MachineConditions.AbnormalTemperature));
    }

    // ─── Devices ───────────────────────────────────────────────────────────

    [Fact]
    public void Device_types_map_onto_the_existing_device_categories()
    {
        Assert.Equal(DeviceCategories.ProductionCounter, IotDeviceTypes.Category(IotDeviceTypes.PieceCounter));
        Assert.Equal(DeviceCategories.MachineSensor, IotDeviceTypes.Category(IotDeviceTypes.PressingMachineSensor));
        Assert.True(IotDeviceTypes.FitsCategory(IotDeviceTypes.GenericSensor, DeviceCategories.MachineSensor));
        Assert.False(IotDeviceTypes.FitsCategory(IotDeviceTypes.PieceCounter, DeviceCategories.MachineSensor));
        Assert.Equal(IotDeviceTypes.PieceCounter, IotDeviceTypes.Default(DeviceCategories.ProductionCounter));
        Assert.DoesNotContain(IotDeviceTypes.PieceCounter, IotDeviceTypes.MachineState);
        Assert.Equal(11, IotDeviceTypes.All.Count);
        Assert.Equal(MachineKinds.Sewing, MachineKinds.Guess("Single needle lockstitch"));
        Assert.Equal(MachineKinds.Pressing, MachineKinds.Guess("Steam iron table"));
        Assert.Equal(MachineKinds.Cutting, MachineKinds.Guess("Straight knife cutter"));
    }

    [Fact]
    public void Device_keys_are_random_shown_once_and_stored_only_as_a_hash()
    {
        var key = DeviceKeys.New();
        Assert.StartsWith(DeviceKeys.Prefix, key);
        Assert.True(key.Length >= 40);
        var hash = DeviceKeys.Hash(key);
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain(key, hash);
        Assert.True(DeviceKeys.Matches(key, hash));
        Assert.False(DeviceKeys.Matches(DeviceKeys.New(), hash));
        Assert.False(DeviceKeys.Matches("not-a-key", hash));
        Assert.False(DeviceKeys.Matches(key, null));
        Assert.NotEqual(key, DeviceKeys.New());
        Assert.EndsWith("…", DeviceKeys.Display(key));
    }

    [Fact]
    public void A_device_flooding_the_pipeline_is_limited_per_second_and_per_minute()
    {
        var limiter = new DeviceRateLimiter(perSecond: 3, perMinute: 5);
        var dev = Guid.NewGuid();
        Assert.True(Enumerable.Range(0, 3).All(_ => limiter.TryAcquire(dev, T)));
        Assert.False(limiter.TryAcquire(dev, T));                        // 4th in the same second
        Assert.True(limiter.TryAcquire(Guid.NewGuid(), T));             // other devices unaffected
        Assert.True(limiter.TryAcquire(dev, T.AddSeconds(2)));
        Assert.True(limiter.TryAcquire(dev, T.AddSeconds(3)));
        Assert.False(limiter.TryAcquire(dev, T.AddSeconds(4)));         // 5 within the minute
        Assert.True(limiter.TryAcquire(dev, T.AddSeconds(61)));
    }

    [Fact]
    public void Options_are_clamped_and_mqtt_is_off_by_default()
    {
        var o = new IotOptions { TelemetryRetentionDays = 0, MessagesPerSecondPerDevice = 100_000, MonitorIntervalSeconds = 1 };
        Assert.Equal(1, o.TelemetryRetentionDays);
        Assert.Equal(1000, o.MessagesPerSecondPerDevice);
        Assert.Equal(5, o.MonitorIntervalSeconds);
        Assert.False(o.Mqtt.Enabled);
        Assert.Null(o.Mqtt.Password);
    }
}
