using System.Text.Json;
using BunonBrain.Application.Access;
using BunonBrain.Application.Monitoring;
using BunonBrain.Infrastructure.Monitoring;
using P = BunonBrain.Application.Access.Permissions;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// The monitoring contract and rules without a database: event types and
/// their requirements, payload limits, identity rules, alert rules, the alert
/// lifecycle, real-time subscription plans, the simulator's generator and the
/// role matrix.
/// </summary>
public sealed class MonitoringUnitTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static IReadOnlyList<string> PermsOf(string role) => AccessCatalog.Role(role)!.Permissions;
    private static AccessContext As(string role, Guid[]? factories = null, string[]? lines = null) =>
        new(Guid.NewGuid(), $"{role}@x", new[] { role }, PermsOf(role), factories ?? Array.Empty<Guid>(), lines ?? Array.Empty<string>());

    // ─── Contract ──────────────────────────────────────────────────────────

    [Fact]
    public void Event_types_parse_and_split_into_vision_and_system()
    {
        // 17 + the camera AI's ACTIVITY_CHANGED, PPE_VIOLATION and DETECTION_UNAVAILABLE.
        Assert.Equal(20, MonitoringEventTypes.All.Length);
        // Machine monitoring adds two SYSTEM types, derived by the IoT pipeline.
        Assert.All(MonitoringEventTypes.Machine, t => Assert.Contains(t, MonitoringEventTypes.System));
        Assert.Equal(MonitoringEventTypes.PersonDetected, MonitoringEventTypes.Parse("person_detected"));
        Assert.Equal(MonitoringEventTypes.ZoneEntered, MonitoringEventTypes.Parse(" Zone-Entered "));
        Assert.Null(MonitoringEventTypes.Parse("WORKER_IDLE"));
        Assert.Null(MonitoringEventTypes.Parse(null));
        foreach (var t in MonitoringEventTypes.Vision) Assert.Equal(MonitoringCategories.Vision, MonitoringEventTypes.CategoryOf(t));
        foreach (var t in MonitoringEventTypes.System) Assert.Equal(MonitoringCategories.System, MonitoringEventTypes.CategoryOf(t));
        Assert.Equal(MonitoringSourceTypes.Real, MonitoringSourceTypes.Parse("real"));
        Assert.Null(MonitoringSourceTypes.Parse("LIVE"));
    }

    [Fact]
    public void Each_event_type_declares_what_it_needs_and_only_some_may_name_a_worker()
    {
        var zone = MonitoringRules.Requirements(MonitoringEventTypes.ZoneEntered);
        Assert.True(zone.Camera && zone.Zone && zone.Track && zone.WorkerAllowed);
        var uncertain = MonitoringRules.Requirements(MonitoringEventTypes.UncertainDetection);
        Assert.True(uncertain.Camera && uncertain.Confidence);
        Assert.False(uncertain.WorkerAllowed);
        foreach (var t in MonitoringEventTypes.System)
        {
            Assert.True(MonitoringRules.Requirements(t).ToStatus);
            Assert.False(MonitoringRules.Requirements(t).WorkerAllowed);
            Assert.NotEmpty(StatusValues.For(t));
        }
        Assert.True(MonitoringRules.Requirements(MonitoringEventTypes.ActivityClassified).Activity);
        Assert.True(MonitoringRules.Requirements(MonitoringEventTypes.ModelStatusChanged).Model);
    }

    [Theory]
    [InlineData("cam-1/2026-11-14T10:00:00Z/42", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData(" spaced key", false)]
    [InlineData("-leading-dash", false)]
    [InlineData("has space", false)]
    public void Idempotency_keys_are_short_printable_tokens(string key, bool ok) => Assert.Equal(ok, MonitoringRules.IsIdempotencyKey(key));

    [Fact]
    public void Track_ids_are_camera_local_tokens_with_a_length_limit()
    {
        Assert.True(MonitoringRules.IsTrackId("t-12"));
        Assert.True(MonitoringRules.IsTrackId("sim-t7"));
        Assert.False(MonitoringRules.IsTrackId(new string('x', 65)));
        Assert.False(MonitoringRules.IsTrackId("t 12"));
        Assert.False(MonitoringRules.IsTrackId(null));
    }

    // ─── Payload ───────────────────────────────────────────────────────────

    [Fact]
    public void A_payload_must_be_a_small_shallow_object_without_secrets_or_identities()
    {
        Assert.True(MonitoringRules.TryPayload(null, out var empty, out _));
        Assert.Equal("{}", empty);
        Assert.True(MonitoringRules.TryPayload(J("""{"bbox":{"x":0.1,"y":0.2,"w":0.1,"h":0.3},"toStatus":"OFFLINE"}"""), out var json, out _));
        Assert.Contains("bbox", json);

        Assert.False(MonitoringRules.TryPayload(J("[1,2]"), out _, out var notObject));
        Assert.Contains("object", notObject);
        Assert.False(MonitoringRules.TryPayload(J("""{"a":{"b":{"c":{"d":{"e":{"f":1}}}}}}"""), out _, out var deep));
        Assert.Contains("nested", deep);
        Assert.False(MonitoringRules.TryPayload(J($$"""{"blob":"{{new string('x', 9000)}}"}"""), out _, out var big));
        Assert.Contains("8 KB", big);
        Assert.False(MonitoringRules.TryPayload(J("""{"rtspPassword":"x"}"""), out _, out var secret));
        Assert.Contains("secret", secret);
        Assert.False(MonitoringRules.TryPayload(J("""{"camera":{"api_key":"x"}}"""), out _, out _));
        // A worker can only be named through the verified workerId of the event.
        foreach (var key in new[] { "workerId", "worker_id", "employeeCode", "faceEmbedding", "workerName" })
        {
            Assert.False(MonitoringRules.TryPayload(J($$"""{"{{key}}":"W-001"}"""), out _, out var identity));
            Assert.Contains("worker", identity);
        }
    }

    [Fact]
    public void Visual_identification_methods_are_never_verifiable()
    {
        Assert.Equal(new[] { IdentityMethods.RfidBadge }, IdentityMethods.Verifiable);
        foreach (var m in new[] { "face-recognition", "Visual Similarity", "REID", "appearance" })
        {
            var n = IdentityMethods.Normalise(m);
            Assert.DoesNotContain(n, IdentityMethods.Verifiable);
            Assert.Contains(n, IdentityMethods.Visual);
        }
    }

    // ─── Alert rules ───────────────────────────────────────────────────────

    private static AlertContext Ctx(string type, string? to = null, string? zoneType = null, bool sim = false, double? conf = null) =>
        new(type, sim, to, null, Guid.NewGuid(), "CAM-001", Guid.NewGuid(), "DEV-1", Guid.NewGuid(), "Store room", zoneType, "t-4", conf, "yolo-person", null);

    [Fact]
    public void Equipment_going_offline_raises_alerts_and_coming_back_annotates_them()
    {
        var cam = Ctx(MonitoringEventTypes.CameraStatusChanged, "OFFLINE");
        var spec = MonitoringRules.AlertFor(cam)!;
        Assert.Equal(AlertTypes.CameraOffline, spec.AlertType);
        Assert.Equal(AlertSeverities.High, spec.Severity);
        Assert.Equal($"{AlertTypes.CameraOffline}:{cam.CameraId}", spec.DedupKey);
        var back = cam with { ToStatus = "ONLINE" };
        Assert.Null(MonitoringRules.AlertFor(back));
        Assert.Equal(spec.DedupKey, MonitoringRules.RecoveryDedupKey(back));

        Assert.Equal(AlertTypes.DeviceOffline, MonitoringRules.AlertFor(Ctx(MonitoringEventTypes.DeviceStatusChanged, "OFFLINE"))!.AlertType);
        Assert.Equal(AlertTypes.DeviceError, MonitoringRules.AlertFor(Ctx(MonitoringEventTypes.DeviceStatusChanged, "ERROR"))!.AlertType);
        Assert.Null(MonitoringRules.AlertFor(Ctx(MonitoringEventTypes.DeviceStatusChanged, "MAINTENANCE")));
        Assert.Equal(AlertTypes.ModelUnavailable, MonitoringRules.AlertFor(Ctx(MonitoringEventTypes.ModelStatusChanged, "ERROR"))!.AlertType);
        Assert.Null(MonitoringRules.AlertFor(Ctx(MonitoringEventTypes.CameraStatusChanged, "MAINTENANCE")));
    }

    [Fact]
    public void Only_restricted_zones_and_uncertain_detections_raise_vision_alerts_and_none_judges_a_worker()
    {
        var restricted = MonitoringRules.AlertFor(Ctx(MonitoringEventTypes.ZoneEntered, zoneType: "Restricted"))!;
        Assert.Equal(AlertTypes.RestrictedZoneEntry, restricted.AlertType);
        Assert.Contains("not a worker identity", restricted.Description);
        Assert.Null(MonitoringRules.AlertFor(Ctx(MonitoringEventTypes.ZoneEntered, zoneType: "Workstation")));
        var uncertain = MonitoringRules.AlertFor(Ctx(MonitoringEventTypes.UncertainDetection, conf: 0.3))!;
        Assert.Equal(AlertSeverities.Low, uncertain.Severity);
        Assert.Contains("does not identify anyone", uncertain.Description);
        // Activity, tracks and detections never raise alerts: no productivity or idle judgement of people.
        foreach (var t in new[] { MonitoringEventTypes.ActivityClassified, MonitoringEventTypes.PersonDetected, MonitoringEventTypes.TrackUpdated,
                     MonitoringEventTypes.PersonLost, MonitoringEventTypes.ZoneExited })
            Assert.Null(MonitoringRules.AlertFor(Ctx(t)));
        foreach (var type in AlertTypes.All)
            Assert.DoesNotContain("salary", type, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Simulated_alerts_and_events_say_so()
    {
        var spec = MonitoringRules.AlertFor(Ctx(MonitoringEventTypes.CameraStatusChanged, "OFFLINE", sim: true))!;
        Assert.StartsWith("SIMULATED", spec.Title);
        Assert.Contains("SIMULATED data", spec.Description);
        Assert.StartsWith("SIMULATED", MonitoringRules.Summary(MonitoringEventTypes.PersonDetected, true, "CAM-001", null, null, "t-1", 0.9, null, null, null, false));
        Assert.DoesNotContain("SIMULATED", MonitoringRules.Summary(MonitoringEventTypes.PersonDetected, false, "CAM-001", null, null, "t-1", 0.9, null, null, null, false));
        Assert.Contains("needs human review", MonitoringRules.Summary(MonitoringEventTypes.UncertainDetection, false, "CAM-001", null, null, null, 0.3, null, null, null, false));
    }

    // ─── Alert lifecycle ───────────────────────────────────────────────────

    [Fact]
    public void Alerts_move_open_acknowledged_escalated_resolved_and_closed_alerts_never_change()
    {
        Assert.Equal(AlertStatuses.Acknowledged, MonitoringRules.Transition(AlertStatuses.Open, AlertActions.Acknowledge).ToStatus);
        Assert.Equal(AlertStatuses.Escalated, MonitoringRules.Transition(AlertStatuses.Acknowledged, AlertActions.Escalate).ToStatus);
        Assert.Equal(AlertStatuses.Escalated, MonitoringRules.Transition(AlertStatuses.Open, AlertActions.Escalate).ToStatus);
        Assert.Equal(AlertStatuses.Resolved, MonitoringRules.Transition(AlertStatuses.Escalated, AlertActions.Resolve).ToStatus);
        Assert.Equal(AlertStatuses.Resolved, MonitoringRules.Transition(AlertStatuses.Acknowledged, AlertActions.Resolve).ToStatus);
        Assert.Equal(AlertStatuses.Dismissed, MonitoringRules.Transition(AlertStatuses.Open, AlertActions.Dismiss).ToStatus);

        Assert.Equal("alert_not_acknowledged", MonitoringRules.Transition(AlertStatuses.Open, AlertActions.Resolve).Code);
        Assert.Equal("alert_already_acknowledged", MonitoringRules.Transition(AlertStatuses.Escalated, AlertActions.Acknowledge).Code);
        Assert.Equal("alert_escalation_limit", MonitoringRules.Transition(AlertStatuses.Escalated, AlertActions.Escalate, AlertEscalationTargets.MaxLevel).Code);
        foreach (var closed in new[] { AlertStatuses.Resolved, AlertStatuses.Dismissed })
            foreach (var action in new[] { AlertActions.Acknowledge, AlertActions.Escalate, AlertActions.Resolve, AlertActions.Dismiss })
                Assert.Equal("alert_closed", MonitoringRules.Transition(closed, action).Code);
    }

    // ─── Real-time subscriptions ───────────────────────────────────────────

    [Fact]
    public void Subscriptions_join_only_the_callers_factories_lines_and_categories()
    {
        Guid f1 = Guid.NewGuid(), f2 = Guid.NewGuid();

        var super = MonitoringSubscriptions.Plan(As(R.SuperAdmin), null);
        Assert.True(super.AllFactories);
        Assert.Equal(new[] { MonitoringGroups.Platform(MonitoringCategories.Vision), MonitoringGroups.Platform(MonitoringCategories.System) }, super.Groups);
        Assert.Equal(new[] { MonitoringGroups.Factory(f2, MonitoringCategories.Vision), MonitoringGroups.Factory(f2, MonitoringCategories.System) },
            MonitoringSubscriptions.Plan(As(R.SuperAdmin), f2).Groups);

        var operatorPlan = MonitoringSubscriptions.Plan(As(R.MonitoringOperator, new[] { f1 }), null);
        Assert.Null(operatorPlan.Error);
        Assert.Equal(new[] { MonitoringGroups.Factory(f1, MonitoringCategories.Vision), MonitoringGroups.Factory(f1, MonitoringCategories.System) }, operatorPlan.Groups);
        Assert.Equal("factory_out_of_scope", MonitoringSubscriptions.Plan(As(R.MonitoringOperator, new[] { f1 }), f2).Error);

        // IT / device administrators get camera / device / model status only.
        var it = MonitoringSubscriptions.Plan(As(R.DeviceAdmin, new[] { f1 }), null);
        Assert.Equal(new[] { MonitoringGroups.Factory(f1, MonitoringCategories.System) }, it.Groups);

        // Supervisors: their lines only, never the factory groups.
        var sup = MonitoringSubscriptions.Plan(As(R.ProductionSupervisor, new[] { f1 }, new[] { "line-1", "line-2" }), null);
        Assert.Equal(new[] { MonitoringGroups.Line("line-1"), MonitoringGroups.Line("line-2") }, sup.Groups);
        Assert.DoesNotContain(sup.Groups, g => g.StartsWith("mon:f:"));

        // HR and workers can't follow monitoring at all.
        Assert.Equal("monitoring_access_required", MonitoringSubscriptions.Plan(As(R.HrOfficer, new[] { f1 }), null).Error);
        Assert.Equal("monitoring_access_required", MonitoringSubscriptions.Plan(As(R.Worker, new[] { f1 }), null).Error);
    }

    [Fact]
    public void Items_are_pushed_to_their_factory_category_platform_and_line_groups_only()
    {
        var f = Guid.NewGuid();
        Assert.Equal(new[] { MonitoringGroups.Factory(f, "VISION"), MonitoringGroups.Platform("VISION"), MonitoringGroups.Line("line-1") },
            MonitoringSubscriptions.Targets(f, "line-1", "VISION"));
        Assert.Equal(2, MonitoringSubscriptions.Targets(f, null, "SYSTEM").Count);
    }

    [Fact]
    public void Visibility_follows_scope_and_the_vision_permission()
    {
        Guid f1 = Guid.NewGuid(), f2 = Guid.NewGuid();
        var op = As(R.MonitoringOperator, new[] { f1 });
        Assert.True(MonitoringQueries.CanSee(op, f1, null, MonitoringCategories.Vision));
        Assert.False(MonitoringQueries.CanSee(op, f2, null, MonitoringCategories.Vision));
        var it = As(R.DeviceAdmin, new[] { f1 });
        Assert.True(MonitoringQueries.CanSee(it, f1, null, MonitoringCategories.System));
        Assert.False(MonitoringQueries.CanSee(it, f1, null, MonitoringCategories.Vision));
        var sup = As(R.ProductionSupervisor, new[] { f1 }, new[] { "line-1" });
        Assert.True(MonitoringQueries.CanSee(sup, f1, "line-1", MonitoringCategories.Vision));
        Assert.False(MonitoringQueries.CanSee(sup, f1, "line-2", MonitoringCategories.Vision));
        Assert.False(MonitoringQueries.CanSee(sup, f1, null, MonitoringCategories.System));
    }

    // ─── Device pipeline mapping ───────────────────────────────────────────

    [Fact]
    public void Device_events_map_to_monitoring_statuses()
    {
        Assert.Equal("ONLINE", MonitoringSystemHooks.DeviceStatus("ConnectionOnline", "Online"));
        Assert.Equal("OFFLINE", MonitoringSystemHooks.DeviceStatus("ConnectionOffline", "Offline"));
        Assert.Equal("OFFLINE", MonitoringSystemHooks.DeviceStatus("HeartbeatMissed", "Offline"));
        Assert.Equal("ERROR", MonitoringSystemHooks.DeviceStatus("Error", null));
        Assert.Equal("MAINTENANCE", MonitoringSystemHooks.DeviceStatus("StatusChanged", "Maintenance"));
        Assert.Null(MonitoringSystemHooks.DeviceStatus("FirmwareChanged", "1.2"));
    }

    // ─── Simulator ─────────────────────────────────────────────────────────

    private static SimCamera Cam(bool withZones = true, Guid? healthDevice = null) => new(Guid.NewGuid(), "CAM-001",
        withZones ? new[] { new SimZone(Guid.NewGuid(), "T-025", "Workstation"), new SimZone(Guid.NewGuid(), "Store", "Restricted") } : Array.Empty<SimZone>(),
        healthDevice);

    [Fact]
    public void The_simulator_is_deterministic_for_a_seed_and_tracks_follow_a_story()
    {
        var cams = new[] { Cam() };
        var all = MonitoringSimulation.Generatable.ToHashSet();
        List<string> Run(int seed)
        {
            var state = new SimState();
            var types = new List<string>();
            for (var i = 0; i < 300; i++)
            {
                var e = MonitoringSimulation.Next(new Random(seed + i), cams, Array.Empty<SimDevice>(), all, state)!;
                types.Add($"{e.EventType}:{e.TrackId}");
            }
            return types;
        }
        Assert.Equal(Run(42), Run(42));

        // Tracks only move, enter zones or get lost after being detected.
        var st = new SimState();
        var seen = new HashSet<string>();
        for (var i = 0; i < 500; i++)
        {
            var e = MonitoringSimulation.Next(new Random(7 + i), cams, Array.Empty<SimDevice>(), all, st)!;
            if (e.EventType == MonitoringEventTypes.PersonDetected) seen.Add(e.TrackId!);
            else if (e.TrackId is not null) Assert.Contains(e.TrackId, seen);
            Assert.True(e.Payload["simulated"] is true);
            Assert.Equal(MonitoringRules.SimulatedNote, e.Payload["note"]);
            Assert.DoesNotContain(e.Payload.Keys, k => k.Contains("worker", StringComparison.OrdinalIgnoreCase));
            if (e.TrackId is not null) Assert.StartsWith("sim-", e.TrackId);
            if (e.Confidence is { } c) Assert.InRange(c, 0, 1);
            if (e.EventType == MonitoringEventTypes.UncertainDetection) { Assert.Null(e.TrackId); Assert.True(e.Confidence < 0.5); }
        }
    }

    [Fact]
    public void The_simulator_only_generates_allowed_types_and_a_disconnected_camera_stays_quiet()
    {
        var cam = Cam(withZones: false);
        var only = new HashSet<string> { MonitoringEventTypes.PersonDetected, MonitoringEventTypes.TrackUpdated };
        var state = new SimState();
        for (var i = 0; i < 100; i++)
            Assert.Contains(MonitoringSimulation.Next(new Random(i), new[] { cam }, Array.Empty<SimDevice>(), only, state)!.EventType, only);

        // Offline cameras only report reconnecting.
        var off = new SimState();
        MonitoringSimulation.CameraStatus(cam, goOffline: true, off);
        var all = MonitoringSimulation.Generatable.Where(t => t != MonitoringEventTypes.ModelStatusChanged).ToHashSet();
        var e = MonitoringSimulation.Next(new Random(1), new[] { cam }, Array.Empty<SimDevice>(), all, off)!;
        Assert.Equal(MonitoringEventTypes.CameraStatusChanged, e.EventType);
        Assert.Equal("ONLINE", e.Payload["toStatus"]);

        // No zones, no zone events; nothing allowed, nothing generated.
        Assert.Null(MonitoringSimulation.Next(new Random(1), new[] { cam }, Array.Empty<SimDevice>(), new HashSet<string> { MonitoringEventTypes.ZoneEntered }, new SimState()));
    }

    [Fact]
    public void Camera_and_device_disconnects_go_through_the_device_simulator_when_there_is_a_simulated_device()
    {
        var device = Guid.NewGuid();
        var viaDevice = MonitoringSimulation.CameraStatus(Cam(healthDevice: device), true, new SimState());
        Assert.Equal(device, viaDevice.DeviceId);
        Assert.Equal("Offline", viaDevice.DeviceKind);
        var direct = MonitoringSimulation.CameraStatus(Cam(), true, new SimState());
        Assert.Null(direct.DeviceId);
        var dev = MonitoringSimulation.DeviceStatus(new SimDevice(device, "GW-1"), false, new SimState());
        Assert.Equal("Online", dev.DeviceKind);
        Assert.Equal("ONLINE", dev.Payload["toStatus"]);
    }

    [Fact]
    public void A_simulation_run_acts_only_inside_its_factory_without_permissions_of_its_own()
    {
        var run = new Domain.Entities.MonitoringSimulationRun { FactoryId = Guid.NewGuid(), StartedByUserId = Guid.NewGuid(), StartedByEmail = "op@x" };
        var id = MonitoringSimulatorService.RunIdentity(run);
        Assert.Equal(RoleScope.Factory, id.Scope);
        Assert.True(id.CanAccessFactory(run.FactoryId));
        Assert.False(id.CanAccessFactory(Guid.NewGuid()));
        Assert.Empty(id.Permissions);
    }

    // ─── Roles ─────────────────────────────────────────────────────────────

    [Fact]
    public void Monitoring_permissions_follow_the_role_matrix()
    {
        var monitoring = new[] { P.MonitoringView, P.MonitoringAlertsReview, P.MonitoringAlertsEscalate, P.MonitoringAlertsDismiss,
            P.MonitoringEventsReview, P.MonitoringEventsIngest, P.MonitoringSimulate };
        foreach (var p in monitoring)
        {
            Assert.False(AccessCatalog.Permission(p)!.Reserved, $"{p} is used by endpoints and can't be reserved");
            Assert.Contains(p, PermsOf(R.SuperAdmin));
            Assert.Contains(p, PermsOf(R.FactoryAdmin));
        }
        // Monitoring operator: watch, review events, handle and escalate alerts, run the simulator. Not dismiss, not ingest.
        var op = PermsOf(R.MonitoringOperator);
        foreach (var p in new[] { P.MonitoringView, P.MonitoringAlertsReview, P.MonitoringAlertsEscalate, P.MonitoringEventsReview, P.MonitoringSimulate, P.CamerasView })
            Assert.Contains(p, op);
        Assert.DoesNotContain(P.MonitoringAlertsDismiss, op);
        Assert.DoesNotContain(P.MonitoringEventsIngest, op);
        // Factory manager may also dismiss; supervisors handle alerts on their lines but don't dismiss or simulate.
        Assert.Contains(P.MonitoringAlertsDismiss, PermsOf(R.FactoryManager));
        Assert.Contains(P.MonitoringAlertsReview, PermsOf(R.ProductionSupervisor));
        Assert.DoesNotContain(P.MonitoringAlertsDismiss, PermsOf(R.ProductionSupervisor));
        Assert.DoesNotContain(P.MonitoringSimulate, PermsOf(R.ProductionSupervisor));
        // IT / device admin: equipment status and the ingestion API, not people-tracking events.
        Assert.Contains(P.MonitoringEventsIngest, PermsOf(R.DeviceAdmin));
        Assert.Contains(P.DevicesView, PermsOf(R.DeviceAdmin));
        Assert.DoesNotContain(P.MonitoringView, PermsOf(R.DeviceAdmin));
        // HR and workers: no central monitoring.
        foreach (var role in new[] { R.HrOfficer, R.Worker })
            foreach (var p in monitoring)
                Assert.DoesNotContain(p, PermsOf(role));
    }
}
