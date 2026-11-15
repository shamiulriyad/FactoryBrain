using System.Security.Cryptography;
using System.Text;

namespace BunonBrain.Application.Identity;

// Verified worker identity association: the rules, in one pure place.
//
// A camera track is associated with a worker only when independent evidence
// agrees and exactly one candidate remains:
//   1. an accepted RFID read of the worker's active card;
//   2. at a reader mapped precisely to one workstation (or to a camera zone of
//      one workstation) at the time of the read;
//   3. the worker actively assigned to that workstation at that time, and no
//      other worker's card read there within the window (and, by default,
//      no other worker assigned to it);
//   4. a camera zone of that workstation, with exactly one track of the same
//      source (REAL / SIMULATED) present in it within the time window;
//   5. no contradiction (the track already verified as someone else, the
//      worker verified on another track of the camera, the worker's card read
//      somewhere else).
// No score, threshold or appearance can turn weaker evidence into VERIFIED:
// anything less is AMBIGUOUS (several candidates), MANUAL_REVIEW (a
// contradiction), PENDING (waiting for the window) or UNVERIFIED.

public static class AssociationStatuses
{
    public const string Pending = "PENDING";
    public const string Verified = "VERIFIED";
    public const string Ambiguous = "AMBIGUOUS";
    public const string Unverified = "UNVERIFIED";
    public const string Expired = "EXPIRED";
    public const string Revoked = "REVOKED";
    public const string ManualReview = "MANUAL_REVIEW";
    public static readonly string[] All = { Pending, Verified, Ambiguous, Unverified, Expired, Revoked, ManualReview };
    /// <summary>Cases waiting for a person.</summary>
    public static readonly string[] NeedsReview = { Ambiguous, ManualReview };
    public static string? Parse(string? v) => All.FirstOrDefault(s => string.Equals(s, v?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class MappingPrecisions
{
    public const string Workstation = "WORKSTATION";
    public const string Zone = "ZONE";
    public const string Line = "LINE";
    public const string Floor = "FLOOR";
    public const string Area = "AREA";
    public static readonly string[] All = { Workstation, Zone, Line, Floor, Area };
    /// <summary>Precisions that can place a worker at one workstation.</summary>
    public static readonly string[] WorkstationLevel = { Workstation, Zone };
    public static string? Parse(string? v) => All.FirstOrDefault(s => string.Equals(s, v?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class RfidReadOutcomes
{
    public const string Accepted = "ACCEPTED";
    public const string Rejected = "REJECTED";
}

public static class RfidReadSources
{
    public const string Device = "Device";
    public const string Api = "Api";
    public const string Simulator = "Simulator";
}

public static class VerificationMethods
{
    public const string RfidWorkstation = "RFID_WORKSTATION";
    public const string ManualReview = "MANUAL_REVIEW";
}

public static class EvidenceKinds
{
    public const string RfidRead = "RFID_READ";
    public const string CardOwnership = "CARD_OWNERSHIP";
    public const string ReaderMapping = "READER_MAPPING";
    public const string Assignment = "ASSIGNMENT";
    public const string CameraZone = "CAMERA_ZONE";
    public const string TrackObservation = "TRACK_OBSERVATION";
    public const string OtherRead = "OTHER_READ";
    public const string Conflict = "CONFLICT";
    public const string Manual = "MANUAL";
}

public static class EvidenceEffects
{
    public const string Supports = "SUPPORTS";
    public const string Contradicts = "CONTRADICTS";
    public const string Context = "CONTEXT";
}

public static class ReviewDecisions
{
    public const string Confirm = "CONFIRM";
    public const string Reject = "REJECT";
    public const string Ambiguous = "AMBIGUOUS";
    public const string Revoke = "REVOKE";
    public static readonly string[] All = { Confirm, Reject, Ambiguous, Revoke };
    public static string? Parse(string? v) => All.FirstOrDefault(s => string.Equals(s, v?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Why a read or an association has its status (stable codes for the API and the dashboard).</summary>
public static class AssociationReasons
{
    public const string AllEvidenceAgrees = "ALL_EVIDENCE_AGREES";
    public const string NoReaderMapping = "NO_READER_MAPPING";
    public const string MappingNotWorkstation = "MAPPING_NOT_WORKSTATION";
    public const string NoActiveAssignment = "NO_ACTIVE_ASSIGNMENT";
    public const string AssignmentMismatch = "ASSIGNMENT_MISMATCH";
    public const string SharedWorkstation = "SHARED_WORKSTATION";
    public const string MultipleWorkersAtReader = "MULTIPLE_WORKERS_AT_READER";
    public const string NoWorkstationZone = "NO_WORKSTATION_ZONE";
    public const string WaitingForTrack = "WAITING_FOR_TRACK";
    public const string NoTrackInZone = "NO_TRACK_IN_ZONE";
    public const string MultipleTracksInZone = "MULTIPLE_TRACKS_IN_ZONE";
    public const string TrackVerifiedAsOther = "TRACK_VERIFIED_AS_OTHER";
    public const string WorkerOnOtherTrack = "WORKER_ON_OTHER_TRACK";
    public const string ConflictingLocation = "CONFLICTING_LOCATION";
    public const string Refreshed = "REFRESHED";
    public const string TrackLost = "TRACK_LOST";
    public const string ValidityElapsed = "VALIDITY_ELAPSED";
    public const string AssignmentEnded = "ASSIGNMENT_ENDED";
    public const string ManualConfirmed = "MANUAL_CONFIRMED";
    public const string ManualRejected = "MANUAL_REJECTED";
    public const string ManualAmbiguous = "MANUAL_AMBIGUOUS";
    public const string ManualRevoked = "MANUAL_REVOKED";

    public static string Describe(string code) => code switch
    {
        AllEvidenceAgrees => "Card, reader location, workstation assignment and exactly one track in the workstation zone agree.",
        NoReaderMapping => "The reader has no location mapping, so the read can't place the worker.",
        MappingNotWorkstation => "The reader covers a line, floor or area, not one workstation: not enough to place the worker at a workstation.",
        NoActiveAssignment => "The worker has no active workstation assignment at that time.",
        AssignmentMismatch => "The card was read at a workstation the worker isn't assigned to.",
        SharedWorkstation => "Several workers are assigned to this workstation; the read alone can't tell which track is whom.",
        MultipleWorkersAtReader => "Several workers' cards were read at this location within the time window.",
        NoWorkstationZone => "No active camera zone covers the workstation, so no track can be placed there.",
        WaitingForTrack => "No track in the workstation zone yet; waiting until the time window closes.",
        NoTrackInZone => "No track was in the workstation zone within the time window.",
        MultipleTracksInZone => "Several tracks were in the workstation zone: position alone doesn't say which is the worker.",
        TrackVerifiedAsOther => "The track is already verified as another worker: contradictory evidence.",
        WorkerOnOtherTrack => "The worker is already verified on another track of this camera: contradictory evidence.",
        ConflictingLocation => "The worker's card was read at another location while this association was valid.",
        Refreshed => "A new read confirmed the same worker on the same track.",
        TrackLost => "The camera lost the track.",
        ValidityElapsed => "The association's validity period ended.",
        AssignmentEnded => "The worker's workstation assignment it relied on ended or changed.",
        ManualConfirmed => "Confirmed by a reviewer.",
        ManualRejected => "Rejected by a reviewer.",
        ManualAmbiguous => "Marked ambiguous by a reviewer.",
        ManualRevoked => "Revoked by a reviewer.",
        _ => code,
    };
}

/// <summary>Configurable association policy (appsettings section <c>Identity</c>).</summary>
public sealed class IdentityOptions
{
    /// <summary>A track counts as present at the read if it was in the zone this long before it…</summary>
    public int WindowBeforeSeconds { get; set; } = 120;
    /// <summary>…or this long after it (a worker taps the card, then sits down).</summary>
    public int WindowAfterSeconds { get; set; } = 120;
    /// <summary>The longest an association stays valid without a new read; it ends earlier when the track is lost.</summary>
    public int ValidityMinutes { get; set; } = 240;
    /// <summary>Allow verifying at a workstation where several workers are assigned (off: such reads become ambiguous).</summary>
    public bool AllowSharedWorkstation { get; set; }
    /// <summary>Tracks with an event in this period count as currently visible (dashboard).</summary>
    public int RecentTrackMinutes { get; set; } = 5;

    public TimeSpan Before => TimeSpan.FromSeconds(Math.Clamp(WindowBeforeSeconds, 5, 3600));
    public TimeSpan After => TimeSpan.FromSeconds(Math.Clamp(WindowAfterSeconds, 5, 3600));
    public TimeSpan Validity => TimeSpan.FromMinutes(Math.Clamp(ValidityMinutes, 1, 24 * 60));
}

// ─── Facts the evaluator gathers, and the decision ─────────────────────────

public sealed record MappingFact(Guid Id, string Precision, Guid? WorkstationId, string? WorkstationCode, Guid? ZoneId);
public sealed record AssignmentFact(Guid Id, Guid WorkstationId, string WorkstationCode);
public sealed record OtherReadFact(long ReadId, Guid WorkerId, DateTimeOffset At);
public sealed record TrackFact(string TrackId, bool IsSimulated, DateTimeOffset EnteredAt, DateTimeOffset? LeftAt, string EnteredEventRef,
    Guid? VerifiedWorkerId = null, Guid? VerifiedAssociationId = null);
public sealed record CameraZoneFact(Guid CameraId, string CameraCode, Guid ZoneId, string ZoneName, string? LineId, IReadOnlyList<TrackFact> Tracks);
/// <summary>A currently valid VERIFIED association of the read's worker.</summary>
public sealed record WorkerVerifiedFact(Guid AssociationId, Guid CameraId, string TrackId, Guid? WorkstationId);

public sealed record ReadFacts(
    long ReadId, Guid WorkerId, bool IsSimulated, DateTimeOffset At, MappingFact? Mapping, AssignmentFact? Assignment,
    int OtherWorkersAssigned, IReadOnlyList<OtherReadFact> OtherReads, IReadOnlyList<CameraZoneFact> Zones,
    IReadOnlyList<WorkerVerifiedFact> WorkerVerified);

public sealed record EvidenceItem(string Kind, string Effect, string? RefType, string? RefId, string Detail, DateTimeOffset? ObservedAt);

/// <summary>What to do for one track of one camera.</summary>
public sealed record TrackDecision(Guid CameraId, Guid ZoneId, string? LineId, string TrackId, string Status, string Reason,
    IReadOnlyList<string> CandidateTracks, IReadOnlyList<Guid> CandidateWorkers, IReadOnlyList<EvidenceItem> Evidence,
    Guid? RefreshAssociationId = null);

public sealed record ReadDecision(string Evaluation, string Reason, IReadOnlyList<TrackDecision> Tracks, IReadOnlyList<(Guid AssociationId, string Reason)> Suspend,
    IReadOnlyList<EvidenceItem> ReadEvidence);

public static class AssociationRuleEngine
{
    /// <param name="final">The time window has closed: a missing track is final (UNVERIFIED) instead of PENDING.</param>
    public static ReadDecision Evaluate(ReadFacts f, IdentityOptions o, bool final)
    {
        var ev = new List<EvidenceItem>
        {
            new(EvidenceKinds.RfidRead, EvidenceEffects.Supports, "rfid_read", f.ReadId.ToString(), $"Card read at {f.At:u}{(f.IsSimulated ? " (SIMULATED)" : "")}.", f.At),
            new(EvidenceKinds.CardOwnership, EvidenceEffects.Supports, "worker", f.WorkerId.ToString(), "The card is an active card of this worker.", f.At),
        };
        ReadDecision Done(string evaluation, string reason, IReadOnlyList<TrackDecision>? tracks = null, IReadOnlyList<(Guid, string)>? suspend = null) =>
            new(evaluation, reason, tracks ?? Array.Empty<TrackDecision>(), suspend ?? Array.Empty<(Guid, string)>(), ev);

        // ── The read's location ──
        if (f.Mapping is null)
        {
            ev.Add(new(EvidenceKinds.ReaderMapping, EvidenceEffects.Contradicts, null, null, AssociationReasons.Describe(AssociationReasons.NoReaderMapping), f.At));
            return Done(AssociationStatuses.Unverified, AssociationReasons.NoReaderMapping);
        }
        if (!MappingPrecisions.WorkstationLevel.Contains(f.Mapping.Precision) || f.Mapping.WorkstationId is null)
        {
            ev.Add(new(EvidenceKinds.ReaderMapping, EvidenceEffects.Context, "rfid_reader_mapping", f.Mapping.Id.ToString(),
                $"Reader mapped at {f.Mapping.Precision} precision: {AssociationReasons.Describe(AssociationReasons.MappingNotWorkstation)}", f.At));
            return Done(AssociationStatuses.Unverified, AssociationReasons.MappingNotWorkstation);
        }
        var ws = f.Mapping.WorkstationId.Value;
        ev.Add(new(EvidenceKinds.ReaderMapping, EvidenceEffects.Supports, "rfid_reader_mapping", f.Mapping.Id.ToString(),
            $"Reader mapped to workstation {f.Mapping.WorkstationCode} ({f.Mapping.Precision}).", f.At));

        // ── Contradictions about where the worker is ──
        var elsewhere = f.WorkerVerified.Where(v => v.WorkstationId != ws).ToList();
        var suspend = elsewhere.Select(v => (v.AssociationId, AssociationReasons.ConflictingLocation)).ToList();

        // ── The worker's assignment ──
        if (f.Assignment is null)
        {
            ev.Add(new(EvidenceKinds.Assignment, EvidenceEffects.Contradicts, null, null, AssociationReasons.Describe(AssociationReasons.NoActiveAssignment), f.At));
            return Done(AssociationStatuses.Unverified, AssociationReasons.NoActiveAssignment, null, suspend);
        }
        if (f.Assignment.WorkstationId != ws)
        {
            ev.Add(new(EvidenceKinds.Assignment, EvidenceEffects.Contradicts, "workstation_assignment", f.Assignment.Id.ToString(),
                $"Assigned to {f.Assignment.WorkstationCode}, but the card was read at {f.Mapping.WorkstationCode}.", f.At));
            return Done(AssociationStatuses.Unverified, AssociationReasons.AssignmentMismatch, null, suspend);
        }
        ev.Add(new(EvidenceKinds.Assignment, EvidenceEffects.Supports, "workstation_assignment", f.Assignment.Id.ToString(),
            $"Actively assigned to {f.Assignment.WorkstationCode} (context, not visual proof).", f.At));

        // ── Other people at the same location ──
        var otherWorkers = f.OtherReads.Select(r => r.WorkerId).Where(w => w != f.WorkerId).Distinct().ToList();
        foreach (var r in f.OtherReads.Where(r => r.WorkerId != f.WorkerId))
            ev.Add(new(EvidenceKinds.OtherRead, EvidenceEffects.Contradicts, "rfid_read", r.ReadId.ToString(), "Another worker's card was read at this location within the window.", r.At));
        string? ambiguity = otherWorkers.Count > 0 ? AssociationReasons.MultipleWorkersAtReader
            : f.OtherWorkersAssigned > 0 && !o.AllowSharedWorkstation ? AssociationReasons.SharedWorkstation : null;
        if (ambiguity == AssociationReasons.SharedWorkstation)
            ev.Add(new(EvidenceKinds.Assignment, EvidenceEffects.Contradicts, null, null,
                $"{f.OtherWorkersAssigned} other worker(s) are assigned to this workstation.", f.At));

        // ── Camera zones of the workstation ──
        if (f.Zones.Count == 0)
        {
            ev.Add(new(EvidenceKinds.CameraZone, EvidenceEffects.Contradicts, null, null, AssociationReasons.Describe(AssociationReasons.NoWorkstationZone), f.At));
            return Done(ambiguity is null ? AssociationStatuses.Unverified : AssociationStatuses.Ambiguous, ambiguity ?? AssociationReasons.NoWorkstationZone, null, suspend);
        }

        var lo = f.At - o.Before;
        var hi = f.At + o.After;
        var decisions = new List<TrackDecision>();
        var anyPending = false;
        foreach (var z in f.Zones)
        {
            var zoneEv = new List<EvidenceItem>(ev)
            {
                new(EvidenceKinds.CameraZone, EvidenceEffects.Supports, "camera_zone", z.ZoneId.ToString(), $"Camera {z.CameraCode} zone {z.ZoneName} covers the workstation.", null),
            };
            // Simulated and real evidence never combine; position alone never picks a track.
            var present = z.Tracks.Where(t => t.IsSimulated == f.IsSimulated && t.EnteredAt <= hi && (t.LeftAt is null || t.LeftAt >= lo)).ToList();
            foreach (var t in z.Tracks.Where(t => t.IsSimulated != f.IsSimulated))
                zoneEv.Add(new(EvidenceKinds.TrackObservation, EvidenceEffects.Context, "monitoring_event", t.EnteredEventRef,
                    $"Track {t.TrackId} ignored: {(t.IsSimulated ? "SIMULATED" : "REAL")} observation vs {(f.IsSimulated ? "SIMULATED" : "REAL")} read.", t.EnteredAt));
            if (present.Count == 0)
            {
                if (!final) anyPending = true;
                continue;
            }
            foreach (var t in present)
                zoneEv.Add(new(EvidenceKinds.TrackObservation, EvidenceEffects.Supports, "monitoring_event", t.EnteredEventRef,
                    $"Track {t.TrackId} in zone {z.ZoneName} from {t.EnteredAt:u}{(t.LeftAt is { } l ? $" to {l:u}" : "")}.", t.EnteredAt));
            var candidates = present.Select(t => t.TrackId).ToList();
            var workers = new[] { f.WorkerId }.Concat(otherWorkers).Distinct().ToList();

            if (ambiguity is not null || present.Count > 1)
            {
                var reason = ambiguity ?? AssociationReasons.MultipleTracksInZone;
                decisions.AddRange(present.Select(t =>
                    new TrackDecision(z.CameraId, z.ZoneId, z.LineId, t.TrackId, AssociationStatuses.Ambiguous, reason, candidates, workers, zoneEv)));
                continue;
            }
            var track = present[0];
            if (track.VerifiedWorkerId is { } other && other != f.WorkerId)
            {
                zoneEv.Add(new(EvidenceKinds.Conflict, EvidenceEffects.Contradicts, "worker_track_association", track.VerifiedAssociationId.ToString(),
                    AssociationReasons.Describe(AssociationReasons.TrackVerifiedAsOther), f.At));
                suspend.Add((track.VerifiedAssociationId!.Value, AssociationReasons.TrackVerifiedAsOther));
                decisions.Add(new(z.CameraId, z.ZoneId, z.LineId, track.TrackId, AssociationStatuses.ManualReview, AssociationReasons.TrackVerifiedAsOther,
                    candidates, new[] { f.WorkerId, other }, zoneEv));
                continue;
            }
            var otherTrack = f.WorkerVerified.FirstOrDefault(v => v.CameraId == z.CameraId && v.TrackId != track.TrackId && v.WorkstationId == ws);
            if (otherTrack is not null)
            {
                zoneEv.Add(new(EvidenceKinds.Conflict, EvidenceEffects.Contradicts, "worker_track_association", otherTrack.AssociationId.ToString(),
                    AssociationReasons.Describe(AssociationReasons.WorkerOnOtherTrack), f.At));
                suspend.Add((otherTrack.AssociationId, AssociationReasons.WorkerOnOtherTrack));
                decisions.Add(new(z.CameraId, z.ZoneId, z.LineId, track.TrackId, AssociationStatuses.ManualReview, AssociationReasons.WorkerOnOtherTrack,
                    candidates, new[] { f.WorkerId }, zoneEv));
                continue;
            }
            if (track.VerifiedWorkerId == f.WorkerId)
            {
                decisions.Add(new(z.CameraId, z.ZoneId, z.LineId, track.TrackId, AssociationStatuses.Verified, AssociationReasons.Refreshed,
                    candidates, new[] { f.WorkerId }, zoneEv, track.VerifiedAssociationId));
                continue;
            }
            decisions.Add(new(z.CameraId, z.ZoneId, z.LineId, track.TrackId, AssociationStatuses.Verified, AssociationReasons.AllEvidenceAgrees,
                candidates, new[] { f.WorkerId }, zoneEv));
        }

        // A conflicting location elsewhere makes this read itself a contradiction: nothing is verified from it.
        if (suspend.Any(s => s.Item2 == AssociationReasons.ConflictingLocation))
            decisions = decisions.Select(d => d.Status == AssociationStatuses.Verified
                ? d with { Status = AssociationStatuses.ManualReview, Reason = AssociationReasons.ConflictingLocation, RefreshAssociationId = null } : d).ToList();

        string evaluation;
        if (decisions.Any(d => d.Status == AssociationStatuses.Verified)) evaluation = AssociationStatuses.Verified;
        else if (decisions.Any(d => d.Status == AssociationStatuses.ManualReview)) evaluation = AssociationStatuses.ManualReview;
        else if (decisions.Any(d => d.Status == AssociationStatuses.Ambiguous)) evaluation = AssociationStatuses.Ambiguous;
        else if (anyPending) evaluation = AssociationStatuses.Pending;
        else evaluation = ambiguity is null ? AssociationStatuses.Unverified : AssociationStatuses.Ambiguous;
        var readReason = decisions.Count > 0 ? decisions[0].Reason
            : anyPending ? AssociationReasons.WaitingForTrack : ambiguity ?? AssociationReasons.NoTrackInZone;
        return new ReadDecision(evaluation, readReason, decisions, suspend, ev);
    }
}

public static class CardIdentifiers
{
    /// <summary>SHA-256 (hex) of a normalised UID: recognises repeated unknown cards without keeping the UID.</summary>
    public static string Hash(string normalisedUid) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("bunonbrain-card:" + normalisedUid))).ToLowerInvariant();

    public static string Suffix(string normalisedUid) => normalisedUid.Length <= 4 ? normalisedUid : normalisedUid[^4..];

    /// <summary>What people see instead of a UID: "…C3D4".</summary>
    public static string Masked(string suffix) => "…" + suffix;
}
