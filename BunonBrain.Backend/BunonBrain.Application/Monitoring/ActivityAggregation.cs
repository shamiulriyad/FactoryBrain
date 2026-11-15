namespace BunonBrain.Application.Monitoring;

// Turns stored activity events into the monitoring room's activity picture.
// Pure (no database): the room service loads the rows, this computes.
//
// * A track is identified by (camera, track id): tracks of different cameras
//   are never merged, and nothing here links a track to a worker.
// * A track's activity at any moment is the one of its latest activity event
//   (ACTIVITY_UNCERTAIN means UNKNOWN). PERSON_LOST ends it.
// * Observed time: from one of a track's events to its next, capped at
//   MaxSegment so a silent track doesn't accumulate time; the last segment of
//   a still-current track runs to "as of", also capped.
// * Nothing here is a productivity or performance figure: it is how long
//   temporary tracks were *predicted* to be in each posture.

/// <summary>A stored event, reduced to what the aggregation needs.</summary>
public sealed record ActivityEventRow(
    Guid Id, string EventType, Guid CameraId, string TrackId, string? LineId, DateTimeOffset OccurredAt, string? Activity, string? Reason,
    string? Candidate, double? Confidence, string? ModelName, string? ModelVersion, string SourceType, Guid? WorkerId, string? Evaluation = null);

public sealed record ActivityTrackSummary(
    Guid CameraId, string TrackId, string? LineId, string Activity, double? Confidence, string? Reason, string? Candidate, DateTimeOffset Since,
    DateTimeOffset LastAt, bool Current, bool Ended, int Changes, string? ModelName, string? ModelVersion, string SourceType, Guid? WorkerId,
    Guid LastEventId);

public sealed class ActivityTotals
{
    public Dictionary<string, double> Seconds { get; } = new();
    public Dictionary<string, int> Predictions { get; } = new();
    public HashSet<string> Tracks { get; } = new();
    public int Uncertain { get; set; }
    public int PredictionCount => Predictions.Values.Sum();
}

public sealed record ActivityPicture(
    IReadOnlyList<ActivityTrackSummary> Tracks, Dictionary<Guid, ActivityTotals> ByCamera, Dictionary<string, ActivityTotals> ByLine,
    int Classified, int StateChanges, int Uncertain);

public static class ActivityAggregation
{
    public static readonly TimeSpan MaxSegment = TimeSpan.FromMinutes(2);
    public const int DefaultActiveSeconds = 300;

    public static ActivityPicture Build(IEnumerable<ActivityEventRow> rows, DateTimeOffset asOf, TimeSpan active, ActivityFamily? family = null)
    {
        family ??= ActivityFamily.Generic;
        var tracks = new List<ActivityTrackSummary>();
        var byCamera = new Dictionary<Guid, ActivityTotals>();
        var byLine = new Dictionary<string, ActivityTotals>();
        int classified = 0, changes = 0, uncertain = 0;

        foreach (var g in rows.GroupBy(r => (r.CameraId, r.TrackId)))
        {
            var list = g.OrderBy(r => r.OccurredAt).ThenBy(r => r.Id).ToList();
            ActivityEventRow? last = null;          // latest activity event
            DateTimeOffset since = default;
            string? state = null;
            var ended = false;
            var trackChanges = 0;
            var key = $"{g.Key.CameraId:N}/{g.Key.TrackId}";
            for (var i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e.EventType == MonitoringEventTypes.PersonLost)
                {
                    if (last is not null) ended = true;     // the track keeps its last predicted activity
                    continue;
                }
                var activity = e.EventType == family.Uncertain ? ActivityLabels.Unknown : e.Activity ?? ActivityLabels.Unknown;
                if (e.EventType == family.Classified) classified++;
                else if (e.EventType == family.Changed) { changes++; trackChanges++; }
                else if (e.EventType == family.Uncertain) { uncertain++; trackChanges++; }
                else continue;                                   // another family's event: not part of this picture
                if (activity != state) since = e.OccurredAt;
                state = activity;
                ended = false;
                last = e;

                var nextAt = i + 1 < list.Count ? list[i + 1].OccurredAt : (DateTimeOffset?)null;
                var end = nextAt ?? (asOf - e.OccurredAt <= active ? asOf : e.OccurredAt);
                var seconds = Math.Max(0, Math.Min((end - e.OccurredAt).TotalSeconds, MaxSegment.TotalSeconds));
                foreach (var totals in Totals(byCamera, byLine, e))
                {
                    totals.Seconds[activity] = totals.Seconds.GetValueOrDefault(activity) + seconds;
                    totals.Predictions[activity] = totals.Predictions.GetValueOrDefault(activity) + 1;
                    totals.Tracks.Add(key);
                    if (activity == ActivityLabels.Unknown) totals.Uncertain++;
                }
            }
            if (last is null) continue;
            var current = !ended && asOf - last.OccurredAt <= active;
            tracks.Add(new ActivityTrackSummary(g.Key.CameraId, g.Key.TrackId, last.LineId, state ?? ActivityLabels.Unknown, last.Confidence,
                last.EventType == family.Uncertain ? last.Reason : null,
                last.EventType == family.Uncertain ? last.Candidate : null,
                since, last.OccurredAt, current, ended, trackChanges, last.ModelName, last.ModelVersion, last.SourceType, last.WorkerId, last.Id));
        }
        return new ActivityPicture(tracks.OrderByDescending(t => t.Current).ThenByDescending(t => t.LastAt).ToList(), byCamera, byLine,
            classified, changes, uncertain);
    }

    private static IEnumerable<ActivityTotals> Totals(Dictionary<Guid, ActivityTotals> byCamera, Dictionary<string, ActivityTotals> byLine, ActivityEventRow e)
    {
        if (!byCamera.TryGetValue(e.CameraId, out var c)) byCamera[e.CameraId] = c = new ActivityTotals();
        var list = new List<ActivityTotals> { c };
        if (e.LineId is { } line)
        {
            if (!byLine.TryGetValue(line, out var l)) byLine[line] = l = new ActivityTotals();
            list.Add(l);
        }
        return list;
    }
}
