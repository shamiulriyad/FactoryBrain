namespace BunonBrain.Application.Monitoring;

// The monitoring simulator's event generator. Pure: given a random source,
// the run's cameras (with their zones) and the run's state, it decides the
// next demo event. The service sends that event through the same ingestion
// pipeline real producers use. Generated events never carry a worker id:
// tracks are temporary camera-local numbers, labelled "sim-…".

public sealed record SimZone(Guid Id, string Name, string ZoneType);

public sealed record SimCamera(Guid Id, string Code, IReadOnlyList<SimZone> Zones, Guid? SimulatedHealthDeviceId);

public sealed record SimDevice(Guid Id, string Code);

/// <summary>One live simulated track on a camera.</summary>
public sealed class SimTrack
{
    public SimTrack(string id) { Id = id; }
    public string Id { get; }
    public Guid? ZoneId { get; set; }
    /// <summary>The track's current simulated activity (null until one is "predicted").</summary>
    public string? Activity { get; set; }
}

/// <summary>What the generator remembers between events of one run.</summary>
public sealed class SimState
{
    public Dictionary<Guid, List<SimTrack>> Tracks { get; } = new();
    public HashSet<Guid> OfflineCameras { get; } = new();
    public HashSet<Guid> OfflineDevices { get; } = new();
    public int NextTrack { get; set; } = 1;
}

/// <summary>
/// The next demo event. <see cref="DeviceId"/> + <see cref="DeviceKind"/> mean
/// "drive this simulated device through the device simulator" (its pipeline
/// then emits the status events); otherwise the event is ingested directly.
/// </summary>
public sealed record SimEvent(
    string EventType, Guid? CameraId, Guid? ZoneId, string? TrackId, double? Confidence,
    IReadOnlyDictionary<string, object?> Payload, Guid? DeviceId = null, string? DeviceKind = null);

public static class MonitoringSimulation
{
    public const int MaxTracksPerCamera = 6;
    public const int MinEventsPerMinute = 1;
    public const int MaxEventsPerMinute = 120;
    public const int MinDurationMinutes = 1;
    public const int MaxDurationMinutes = 120;
    public const int MaxCameras = 20;
    public const int MaxEventsPerRun = 5000;

    /// <summary>Simulated activities: the observable labels a pose-based model could predict (never garment operations).</summary>
    public static readonly string[] SampleActivities =
        { ActivityLabels.Standing, ActivityLabels.Sitting, ActivityLabels.Walking, ActivityLabels.Bending, ActivityLabels.Reaching };

    /// <summary>Event types the simulator can generate (all of the contract except model status).</summary>
    public static readonly string[] Generatable =
    {
        MonitoringEventTypes.PersonDetected, MonitoringEventTypes.TrackUpdated, MonitoringEventTypes.PersonLost,
        MonitoringEventTypes.ZoneEntered, MonitoringEventTypes.ZoneExited, MonitoringEventTypes.ActivityClassified,
        MonitoringEventTypes.ActivityStateChanged, MonitoringEventTypes.ActivityUncertain, MonitoringEventTypes.UncertainDetection, MonitoringEventTypes.CameraStatusChanged, MonitoringEventTypes.DeviceStatusChanged,
        MonitoringEventTypes.ModelStatusChanged,
    };

    private static Dictionary<string, object?> Base(Dictionary<string, object?>? extra = null)
    {
        var p = new Dictionary<string, object?> { ["simulated"] = true, ["note"] = MonitoringRules.SimulatedNote };
        if (extra is not null) foreach (var (k, v) in extra) p[k] = v;
        return p;
    }

    private static double Between(Random rng, double lo, double hi) => Math.Round(lo + rng.NextDouble() * (hi - lo), 2);

    private static Dictionary<string, object?> Box(Random rng) => new()
    {
        ["x"] = Between(rng, 0.05, 0.7), ["y"] = Between(rng, 0.05, 0.6), ["w"] = Between(rng, 0.08, 0.2), ["h"] = Between(rng, 0.2, 0.35),
    };

    /// <summary>
    /// Picks the next event among <paramref name="allowed"/> types that make
    /// sense now (a track must exist before it can move or leave). Null when
    /// nothing allowed is possible (e.g. no cameras).
    /// </summary>
    public static SimEvent? Next(Random rng, IReadOnlyList<SimCamera> cameras, IReadOnlyList<SimDevice> devices, IReadOnlySet<string> allowed, SimState state)
    {
        if (cameras.Count == 0 && devices.Count == 0) return null;
        var options = new List<(double Weight, Func<SimEvent> Make)>();

        foreach (var cam in cameras)
        {
            var tracks = state.Tracks.TryGetValue(cam.Id, out var t) ? t : state.Tracks[cam.Id] = new List<SimTrack>();
            var offline = state.OfflineCameras.Contains(cam.Id);
            if (allowed.Contains(MonitoringEventTypes.CameraStatusChanged))
                options.Add((offline ? 1.5 : 0.15, () => CameraStatus(cam, !offline, state)));
            if (offline) continue; // a disconnected camera reports nothing else

            if (allowed.Contains(MonitoringEventTypes.PersonDetected) && tracks.Count < MaxTracksPerCamera)
                options.Add((tracks.Count == 0 ? 4 : 2, () =>
                {
                    var track = new SimTrack($"sim-t{state.NextTrack++}");
                    tracks.Add(track);
                    return new SimEvent(MonitoringEventTypes.PersonDetected, cam.Id, null, track.Id, Between(rng, 0.75, 0.97),
                        Base(new() { ["bbox"] = Box(rng) }));
                }));
            if (tracks.Count == 0) continue;
            SimTrack Pick() => tracks[rng.Next(tracks.Count)];

            if (allowed.Contains(MonitoringEventTypes.TrackUpdated))
                options.Add((3, () => { var tr = Pick(); return new SimEvent(MonitoringEventTypes.TrackUpdated, cam.Id, tr.ZoneId, tr.Id, Between(rng, 0.7, 0.97), Base(new() { ["bbox"] = Box(rng) })); }));
            if (allowed.Contains(MonitoringEventTypes.ZoneEntered) && cam.Zones.Count > 0 && tracks.Any(x => x.ZoneId is null))
                options.Add((2, () =>
                {
                    var free = tracks.Where(x => x.ZoneId is null).ToList();
                    var tr = free[rng.Next(free.Count)];
                    // Restricted areas are entered rarely, so the demo isn't a wall of high alerts.
                    var zones = cam.Zones.Where(z => z.ZoneType != "Restricted" || rng.NextDouble() < 0.2).ToList();
                    var zone = zones.Count > 0 ? zones[rng.Next(zones.Count)] : cam.Zones[rng.Next(cam.Zones.Count)];
                    tr.ZoneId = zone.Id;
                    return new SimEvent(MonitoringEventTypes.ZoneEntered, cam.Id, zone.Id, tr.Id, Between(rng, 0.7, 0.95), Base(new() { ["zoneType"] = zone.ZoneType }));
                }));
            if (allowed.Contains(MonitoringEventTypes.ZoneExited) && tracks.Any(x => x.ZoneId is not null))
                options.Add((1.5, () =>
                {
                    var inside = tracks.Where(x => x.ZoneId is not null).ToList();
                    var tr = inside[rng.Next(inside.Count)];
                    var zone = tr.ZoneId;
                    tr.ZoneId = null;
                    return new SimEvent(MonitoringEventTypes.ZoneExited, cam.Id, zone, tr.Id, Between(rng, 0.7, 0.95), Base());
                }));
            if (allowed.Contains(MonitoringEventTypes.ActivityStateChanged))
                options.Add((1.5, () =>
                {
                    var tr = Pick();
                    var choices = SampleActivities.Where(a => a != tr.Activity).ToArray();
                    var activity = choices[rng.Next(choices.Length)];
                    var p = new Dictionary<string, object?> { ["activity"] = activity, ["sample"] = true };
                    if (tr.Activity is not null) p["previousActivity"] = tr.Activity;
                    tr.Activity = activity;
                    return new SimEvent(MonitoringEventTypes.ActivityStateChanged, cam.Id, tr.ZoneId, tr.Id, Between(rng, 0.6, 0.93), Base(p));
                }));
            if (allowed.Contains(MonitoringEventTypes.ActivityClassified) && tracks.Any(x => x.Activity is not null and not ActivityLabels.Unknown))
                options.Add((1.5, () =>
                {
                    var known = tracks.Where(x => x.Activity is not null and not ActivityLabels.Unknown).ToList();
                    var tr = known[rng.Next(known.Count)];
                    return new SimEvent(MonitoringEventTypes.ActivityClassified, cam.Id, tr.ZoneId, tr.Id, Between(rng, 0.6, 0.93),
                        Base(new() { ["activity"] = tr.Activity, ["sample"] = true }));
                }));
            if (allowed.Contains(MonitoringEventTypes.ActivityUncertain))
                options.Add((0.5, () =>
                {
                    var tr = Pick();
                    var p = new Dictionary<string, object?> { ["activity"] = ActivityLabels.Unknown, ["reason"] = "LOW_CONFIDENCE", ["sample"] = true };
                    if (tr.Activity is not null) p["previousActivity"] = tr.Activity;
                    tr.Activity = ActivityLabels.Unknown;
                    return new SimEvent(MonitoringEventTypes.ActivityUncertain, cam.Id, tr.ZoneId, tr.Id, Between(rng, 0.2, 0.5), Base(p));
                }));
            if (allowed.Contains(MonitoringEventTypes.PersonLost))
                options.Add((1, () =>
                {
                    var tr = Pick();
                    tracks.Remove(tr);
                    return new SimEvent(MonitoringEventTypes.PersonLost, cam.Id, tr.ZoneId, tr.Id, null, Base(new() { ["reason"] = "left the camera view" }));
                }));
            if (allowed.Contains(MonitoringEventTypes.UncertainDetection))
                options.Add((0.5, () => new SimEvent(MonitoringEventTypes.UncertainDetection, cam.Id, null, null, Between(rng, 0.2, 0.49),
                    Base(new() { ["bbox"] = Box(rng), ["reason"] = "partly occluded" }))));
        }

        if (allowed.Contains(MonitoringEventTypes.DeviceStatusChanged))
            foreach (var d in devices)
            {
                var offline = state.OfflineDevices.Contains(d.Id);
                options.Add((offline ? 1 : 0.2, () => DeviceStatus(d, !offline, state)));
            }

        if (allowed.Contains(MonitoringEventTypes.ModelStatusChanged))
            options.Add((0.05, () => new SimEvent(MonitoringEventTypes.ModelStatusChanged, null, null, null, null,
                Base(new() { ["model"] = "sim-person-detector", ["toStatus"] = rng.NextDouble() < 0.5 ? "DEGRADED" : "READY" }))));

        if (options.Count == 0) return null;
        var total = options.Sum(o => o.Weight);
        var roll = rng.NextDouble() * total;
        foreach (var (w, make) in options)
        {
            if (roll < w) return make();
            roll -= w;
        }
        return options[^1].Make();
    }

    /// <summary>Camera disconnect / reconnect. Via its simulated health device when it has one, so the device pipeline reports it.</summary>
    public static SimEvent CameraStatus(SimCamera cam, bool goOffline, SimState state)
    {
        if (goOffline)
        {
            state.OfflineCameras.Add(cam.Id);
            state.Tracks.Remove(cam.Id);
        }
        else state.OfflineCameras.Remove(cam.Id);
        var to = goOffline ? "OFFLINE" : "ONLINE";
        var payload = Base(new() { ["toStatus"] = to, ["fromStatus"] = goOffline ? "ONLINE" : "OFFLINE", ["reason"] = goOffline ? "simulated disconnect" : "simulated reconnect" });
        return cam.SimulatedHealthDeviceId is { } dev
            ? new SimEvent(MonitoringEventTypes.CameraStatusChanged, cam.Id, null, null, null, payload, dev, goOffline ? "Offline" : "Online")
            : new SimEvent(MonitoringEventTypes.CameraStatusChanged, cam.Id, null, null, null, payload);
    }

    public static SimEvent DeviceStatus(SimDevice d, bool goOffline, SimState state)
    {
        if (goOffline) state.OfflineDevices.Add(d.Id); else state.OfflineDevices.Remove(d.Id);
        return new SimEvent(MonitoringEventTypes.DeviceStatusChanged, null, null, null, null,
            Base(new() { ["toStatus"] = goOffline ? "OFFLINE" : "ONLINE" }), d.Id, goOffline ? "Offline" : "Online");
    }
}
