using BunonBrain.Application.Monitoring;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Activity events without a database: the label catalog and what each
/// producer may claim, the per-type rules, summaries that say "predicted",
/// and the aggregation behind GET /api/monitoring/activity.
/// </summary>
public sealed class ActivityUnitTests
{
    private const string Cv = MonitoringProducers.CvService;
    private const string Api = MonitoringProducers.Api;

    [Fact]
    public void The_activity_types_are_vision_events_that_need_a_track_and_an_activity()
    {
        foreach (var t in MonitoringEventTypes.Activity)
        {
            Assert.Equal(MonitoringCategories.Vision, MonitoringEventTypes.CategoryOf(t));
            var r = MonitoringRules.Requirements(t);
            Assert.True(r.Camera && r.Track && r.Activity);
        }
        Assert.True(MonitoringRules.Requirements(MonitoringEventTypes.ActivityStateChanged).Confidence);
        // "Can't tell" carries no required confidence and can never name a worker.
        var uncertain = MonitoringRules.Requirements(MonitoringEventTypes.ActivityUncertain);
        Assert.False(uncertain.Confidence);
        Assert.False(uncertain.WorkerAllowed);
        Assert.Equal(MonitoringEventTypes.ActivityStateChanged, MonitoringEventTypes.Parse("activity-state-changed"));
    }

    [Theory]
    [InlineData(MonitoringEventTypes.ActivityStateChanged, "WALKING", "STANDING", Cv, null)]
    [InlineData(MonitoringEventTypes.ActivityStateChanged, "walking", null, Cv, null)]
    [InlineData(MonitoringEventTypes.ActivityClassified, "UNKNOWN", null, Cv, null)]
    [InlineData(MonitoringEventTypes.ActivityUncertain, "UNKNOWN", "SITTING", Cv, null)]
    [InlineData(MonitoringEventTypes.ActivityStateChanged, "SEWING", null, Cv, "activity_invalid")]
    [InlineData(MonitoringEventTypes.ActivityStateChanged, "SEWING", null, Api, "activity_invalid")]
    [InlineData(MonitoringEventTypes.ActivityStateChanged, "SEWING_LIKE_MOTION", null, Cv, "activity_not_supported_by_service")]
    [InlineData(MonitoringEventTypes.ActivityStateChanged, "SEWING_LIKE_MOTION", null, Api, null)]
    [InlineData(MonitoringEventTypes.ActivityUncertain, "SITTING", null, Cv, "activity_must_be_unknown")]
    [InlineData(MonitoringEventTypes.ActivityStateChanged, "UNKNOWN", null, Cv, "activity_state_unknown")]
    [InlineData(MonitoringEventTypes.ActivityStateChanged, "SITTING", "NAPPING", Cv, "previous_activity_invalid")]
    public void Activities_come_from_the_catalog_and_reserved_ones_need_a_trained_model(string type, string activity, string? previous, string producer,
        string? expected)
    {
        Assert.Equal(expected, ActivityLabels.Check(type, activity, previous, producer)?.Code);
    }

    [Fact]
    public void Summaries_say_the_activity_is_predicted()
    {
        var changed = MonitoringRules.Summary(MonitoringEventTypes.ActivityStateChanged, false, "CAM-001", null, null, "t-12", 0.91, null, "WALKING",
            "pose-temporal-baseline", false, "STANDING");
        Assert.Equal("Predicted activity changed to WALKING from STANDING on CAM-001 (track t-12), confidence 0.91", changed);
        var unsure = MonitoringRules.Summary(MonitoringEventTypes.ActivityUncertain, false, "CAM-001", null, null, "t-12", null, null, "UNKNOWN",
            null, false, "SITTING", "LOW_CONFIDENCE");
        Assert.Equal("Activity uncertain (was SITTING) on CAM-001 (track t-12): low confidence", unsure);
    }

    [Fact]
    public void The_simulator_only_simulates_observable_activities_with_coherent_histories()
    {
        Assert.DoesNotContain("SEWING", MonitoringSimulation.SampleActivities);
        Assert.All(MonitoringSimulation.SampleActivities, a => Assert.Contains(a, ActivityLabels.Observable));
        var cam = new SimCamera(Guid.NewGuid(), "CAM-001", Array.Empty<SimZone>(), null);
        var allowed = new HashSet<string> { MonitoringEventTypes.PersonDetected, MonitoringEventTypes.ActivityStateChanged, MonitoringEventTypes.ActivityUncertain,
            MonitoringEventTypes.ActivityClassified };
        var state = new SimState();
        var last = new Dictionary<string, string>();
        for (var i = 0; i < 400; i++)
        {
            var e = MonitoringSimulation.Next(new Random(i), new[] { cam }, Array.Empty<SimDevice>(), allowed, state)!;
            if (!MonitoringEventTypes.Activity.Contains(e.EventType)) continue;
            var activity = (string)e.Payload["activity"]!;
            Assert.Null(ActivityLabels.Check(e.EventType, activity, e.Payload.GetValueOrDefault("previousActivity") as string, MonitoringProducers.Simulator));
            if (e.EventType == MonitoringEventTypes.ActivityClassified) Assert.Equal(last[e.TrackId!], activity);    // re-confirms the current state
            if (e.Payload.GetValueOrDefault("previousActivity") is string prev) Assert.Equal(last[e.TrackId!], prev);
            last[e.TrackId!] = activity;
        }
        Assert.NotEmpty(last);
    }

    // ─── Aggregation ───────────────────────────────────────────────────────

    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid CamA = Guid.NewGuid();
    private static readonly Guid CamB = Guid.NewGuid();

    private static ActivityEventRow Row(Guid cam, string track, int seconds, string type, string? activity = null, string? line = "L1", Guid? worker = null) =>
        new(Guid.NewGuid(), type, cam, track, line, T0.AddSeconds(seconds), activity, type == MonitoringEventTypes.ActivityUncertain ? "LOW_CONFIDENCE" : null,
            type == MonitoringEventTypes.ActivityUncertain ? "SITTING" : null, 0.9, "pose-temporal-baseline", "1.0", "REAL", worker);

    [Fact]
    public void Current_activity_time_per_activity_and_transitions_are_computed_per_camera_track()
    {
        var rows = new[]
        {
            Row(CamA, "s1-t1", 0, MonitoringEventTypes.ActivityStateChanged, "STANDING"),
            Row(CamA, "s1-t1", 30, MonitoringEventTypes.ActivityStateChanged, "WALKING"),
            Row(CamA, "s1-t1", 40, MonitoringEventTypes.ActivityClassified, "WALKING"),
            Row(CamA, "s1-t1", 50, MonitoringEventTypes.ActivityUncertain, "UNKNOWN"),
            // The same track number on another camera is another person.
            Row(CamB, "s1-t1", 0, MonitoringEventTypes.ActivityStateChanged, "SITTING", line: "L2"),
            Row(CamB, "s1-t1", 20, MonitoringEventTypes.PersonLost),
        };
        var p = ActivityAggregation.Build(rows, T0.AddSeconds(60), TimeSpan.FromMinutes(5));
        Assert.Equal(2, p.Tracks.Count);
        var a = p.Tracks.Single(t => t.CameraId == CamA);
        Assert.Equal("UNKNOWN", a.Activity);
        Assert.Equal("LOW_CONFIDENCE", a.Reason);
        Assert.Equal("SITTING", a.Candidate);
        Assert.True(a.Current);
        Assert.Equal(T0.AddSeconds(50), a.Since);
        Assert.Equal(3, a.Changes);
        var b = p.Tracks.Single(t => t.CameraId == CamB);
        Assert.True(b.Ended);
        Assert.False(b.Current);
        Assert.Equal("SITTING", b.Activity);

        var camA = p.ByCamera[CamA];
        Assert.Equal(30, camA.Seconds["STANDING"]);
        Assert.Equal(20, camA.Seconds["WALKING"]);         // 30→40 and 40→50
        Assert.Equal(10, camA.Seconds["UNKNOWN"]);         // 50→60 (still current)
        Assert.Equal(2, camA.Predictions["WALKING"]);
        Assert.Equal(1, camA.Uncertain);
        Assert.Equal(20, p.ByCamera[CamB].Seconds["SITTING"]);   // ends at PERSON_LOST
        Assert.Equal(60, p.ByLine["L1"].Seconds.Values.Sum());
        Assert.Equal((1, 3, 1), (p.Classified, p.StateChanges, p.Uncertain));
    }

    [Fact]
    public void Silent_tracks_stop_counting_and_long_gaps_are_capped()
    {
        var rows = new[]
        {
            Row(CamA, "t1", 0, MonitoringEventTypes.ActivityStateChanged, "SITTING"),
            Row(CamA, "t1", 600, MonitoringEventTypes.ActivityClassified, "SITTING"),     // 10 min gap: counted as 2 min at most
        };
        var p = ActivityAggregation.Build(rows, T0.AddSeconds(3600), TimeSpan.FromMinutes(5));
        var t = Assert.Single(p.Tracks);
        Assert.False(t.Current);                                 // nothing for 50 minutes
        Assert.Equal(ActivityAggregation.MaxSegment.TotalSeconds, p.ByCamera[CamA].Seconds["SITTING"]);
        Assert.Equal(T0, t.Since);                               // re-confirmations don't restart the state
    }

    [Fact]
    public void Tracks_without_activity_events_are_ignored()
    {
        var p = ActivityAggregation.Build(new[] { Row(CamA, "t9", 0, MonitoringEventTypes.PersonLost) }, T0.AddSeconds(10), TimeSpan.FromMinutes(5));
        Assert.Empty(p.Tracks);
        Assert.Empty(p.ByCamera);
    }

    // ─── Garment activities ────────────────────────────────────────────────

    [Fact]
    public void Garment_events_are_a_separate_vision_family_that_names_its_model()
    {
        foreach (var t in MonitoringEventTypes.GarmentActivity)
        {
            Assert.Equal(MonitoringCategories.Vision, MonitoringEventTypes.CategoryOf(t));
            Assert.Contains(t, MonitoringProducers.CvEventTypes);
            var r = MonitoringRules.Requirements(t);
            Assert.True(r.Camera && r.Track && r.Activity && r.Model);
        }
        Assert.False(MonitoringRules.Requirements(MonitoringEventTypes.GarmentActivityUncertain).WorkerAllowed);
        Assert.False(MonitoringRules.Requirements(MonitoringEventTypes.GarmentActivityUncertain).Confidence);
        Assert.DoesNotContain(MonitoringEventTypes.GarmentActivityChanged, MonitoringEventTypes.Activity);   // never mixed with generic posture
        Assert.Equal(12, GarmentActivityLabels.Default.Length);
        Assert.Same(ActivityFamily.Garment, ActivityFamily.For("Garment"));
        Assert.Same(ActivityFamily.Generic, ActivityFamily.For(null));
    }

    [Theory]
    [InlineData(MonitoringEventTypes.GarmentActivityChanged, "SEWING", "SITTING", "0.1.0", null)]
    [InlineData(MonitoringEventTypes.GarmentActivityChanged, "button_attaching", null, "0.1.0", null)]      // factory-specific class
    [InlineData(MonitoringEventTypes.GarmentActivityClassified, "QUALITY_INSPECTION", null, "0.1.0", null)]
    [InlineData(MonitoringEventTypes.GarmentActivityUncertain, "UNKNOWN", "SEWING", "0.1.0", null)]
    [InlineData(MonitoringEventTypes.GarmentActivityChanged, "sew!ng", null, "0.1.0", "activity_invalid")]
    [InlineData(MonitoringEventTypes.GarmentActivityChanged, "UNKNOWN", null, "0.1.0", "activity_state_unknown")]
    [InlineData(MonitoringEventTypes.GarmentActivityUncertain, "SEWING", null, "0.1.0", "activity_must_be_unknown")]
    [InlineData(MonitoringEventTypes.GarmentActivityChanged, "SEWING", "?", "0.1.0", "previous_activity_invalid")]
    [InlineData(MonitoringEventTypes.GarmentActivityChanged, "SEWING", null, null, "model_version_required")]
    public void Garment_labels_and_model_rules(string type, string activity, string? previous, string? version, string? expected) =>
        Assert.Equal(expected, GarmentActivityLabels.Check(type, activity, previous, version)?.Code);

    [Fact]
    public void Garment_summaries_say_predicted_and_name_the_model()
    {
        var s = MonitoringRules.Summary(MonitoringEventTypes.GarmentActivityChanged, false, "CAM-001", null, null, "t-3", 0.82, null, "SEWING",
            "garment-cnn-gru", false, "SITTING");
        Assert.Equal("Predicted garment activity changed to SEWING from SITTING on CAM-001 (track t-3), confidence 0.82 (model garment-cnn-gru)", s);
    }

    [Fact]
    public void The_aggregation_only_counts_the_requested_family()
    {
        var rows = new[]
        {
            Row(CamA, "t1", 0, MonitoringEventTypes.ActivityStateChanged, "SITTING"),
            Row(CamA, "t1", 0, MonitoringEventTypes.GarmentActivityChanged, "SEWING"),
            Row(CamA, "t1", 20, MonitoringEventTypes.GarmentActivityUncertain, "UNKNOWN"),
            Row(CamA, "t1", 30, MonitoringEventTypes.GarmentActivityChanged, "BUTTON_ATTACHING"),
        };
        var garment = ActivityAggregation.Build(rows.Where(r => r.EventType.StartsWith("GARMENT")), T0.AddSeconds(40), TimeSpan.FromMinutes(5), ActivityFamily.Garment);
        var t = Assert.Single(garment.Tracks);
        Assert.Equal("BUTTON_ATTACHING", t.Activity);
        Assert.Equal((0, 2, 1), (garment.Classified, garment.StateChanges, garment.Uncertain));
        Assert.Equal(20, garment.ByCamera[CamA].Seconds["SEWING"]);
        Assert.Equal(10, garment.ByCamera[CamA].Seconds["UNKNOWN"]);
        // Mixed rows: each family ignores the other's events.
        var generic = ActivityAggregation.Build(rows, T0.AddSeconds(40), TimeSpan.FromMinutes(5));
        Assert.Equal("SITTING", Assert.Single(generic.Tracks).Activity);
        Assert.Equal(1, generic.StateChanges);
    }
}
