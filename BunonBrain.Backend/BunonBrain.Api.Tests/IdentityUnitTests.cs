using BunonBrain.Application.Identity;
using BunonBrain.Infrastructure.Identity;

namespace BunonBrain.Api.Tests;

/// <summary>
/// The identity association rules without a database: what makes a camera
/// track VERIFIED as a worker, and every way the evidence falls short
/// (AMBIGUOUS, UNVERIFIED, PENDING, MANUAL_REVIEW).
/// </summary>
public sealed class IdentityUnitTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Worker = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly Guid Ws = Guid.NewGuid();
    private static readonly Guid OtherWs = Guid.NewGuid();
    private static readonly Guid CamA = Guid.NewGuid();
    private static readonly Guid CamB = Guid.NewGuid();
    private static readonly Guid Zone = Guid.NewGuid();
    private static readonly IdentityOptions O = new();

    private static TrackFact Track(string id, int enteredSecondsBefore = 60, int? leftSecondsAfter = null, bool sim = false, Guid? verifiedWorker = null) =>
        new(id, sim, T.AddSeconds(-enteredSecondsBefore), leftSecondsAfter is { } l ? T.AddSeconds(l) : null, Guid.NewGuid().ToString(), verifiedWorker,
            verifiedWorker is null ? null : Guid.NewGuid());

    private static CameraZoneFact ZoneOf(Guid cam, params TrackFact[] tracks) => new(cam, cam == CamA ? "CAM-001" : "CAM-002", Zone, "WS T-025", "L1", tracks);

    private static ReadFacts Facts(string precision = MappingPrecisions.Workstation, bool mapped = true, Guid? assignedWs = null, bool assigned = true,
        int otherAssigned = 0, OtherReadFact[]? otherReads = null, CameraZoneFact[]? zones = null, WorkerVerifiedFact[]? verified = null, bool sim = false) =>
        new(1, Worker, sim, T,
            mapped ? new MappingFact(Guid.NewGuid(), precision, MappingPrecisions.WorkstationLevel.Contains(precision) ? Ws : null, "T-025", null) : null,
            assigned ? new AssignmentFact(Guid.NewGuid(), assignedWs ?? Ws, assignedWs == OtherWs ? "T-030" : "T-025") : null,
            otherAssigned, otherReads ?? Array.Empty<OtherReadFact>(), zones ?? new[] { ZoneOf(CamA, Track("s1-t7")) }, verified ?? Array.Empty<WorkerVerifiedFact>());

    private static ReadDecision Eval(ReadFacts f, bool final = false, IdentityOptions? o = null) => AssociationRuleEngine.Evaluate(f, o ?? O, final);

    // ─── Verified only when everything agrees ──────────────────────────────

    [Fact]
    public void An_active_card_at_the_assigned_workstation_with_one_track_in_its_zone_is_verified()
    {
        var d = Eval(Facts());
        Assert.Equal(AssociationStatuses.Verified, d.Evaluation);
        var t = Assert.Single(d.Tracks);
        Assert.Equal(("s1-t7", AssociationStatuses.Verified, AssociationReasons.AllEvidenceAgrees), (t.TrackId, t.Status, t.Reason));
        Assert.Equal(new[] { Worker }, t.CandidateWorkers);
        var kinds = t.Evidence.Select(e => e.Kind).ToHashSet();
        foreach (var k in new[] { EvidenceKinds.RfidRead, EvidenceKinds.CardOwnership, EvidenceKinds.ReaderMapping, EvidenceKinds.Assignment,
                                  EvidenceKinds.CameraZone, EvidenceKinds.TrackObservation })
            Assert.Contains(k, kinds);
        Assert.DoesNotContain(t.Evidence, e => e.Effect == EvidenceEffects.Contradicts);
    }

    [Fact]
    public void A_zone_mapping_of_the_workstation_counts_like_a_workstation_mapping() =>
        Assert.Equal(AssociationStatuses.Verified, Eval(Facts(MappingPrecisions.Zone)).Evaluation);

    // ─── Location evidence ────────────────────────────────────────────────

    [Fact]
    public void A_reader_without_a_mapping_cannot_place_anyone() =>
        Assert.Equal((AssociationStatuses.Unverified, AssociationReasons.NoReaderMapping), (Eval(Facts(mapped: false)).Evaluation, Eval(Facts(mapped: false)).Reason));

    [Theory]
    [InlineData(MappingPrecisions.Line)]
    [InlineData(MappingPrecisions.Floor)]
    [InlineData(MappingPrecisions.Area)]
    public void A_broad_reader_is_never_workstation_level_proof(string precision)
    {
        var d = Eval(Facts(precision));
        Assert.Equal((AssociationStatuses.Unverified, AssociationReasons.MappingNotWorkstation), (d.Evaluation, d.Reason));
        Assert.Empty(d.Tracks);
    }

    // ─── Assignment evidence ──────────────────────────────────────────────

    [Fact]
    public void Without_an_active_assignment_the_read_is_unverified()
    {
        var d = Eval(Facts(assigned: false));
        Assert.Equal((AssociationStatuses.Unverified, AssociationReasons.NoActiveAssignment), (d.Evaluation, d.Reason));
    }

    [Fact]
    public void A_card_read_at_a_workstation_the_worker_is_not_assigned_to_is_unverified()
    {
        var d = Eval(Facts(assignedWs: OtherWs));
        Assert.Equal((AssociationStatuses.Unverified, AssociationReasons.AssignmentMismatch), (d.Evaluation, d.Reason));
        Assert.Contains(d.ReadEvidence, e => e.Kind == EvidenceKinds.Assignment && e.Effect == EvidenceEffects.Contradicts);
    }

    [Fact]
    public void A_shared_workstation_is_ambiguous_unless_explicitly_allowed()
    {
        var d = Eval(Facts(otherAssigned: 1));
        Assert.Equal((AssociationStatuses.Ambiguous, AssociationReasons.SharedWorkstation), (d.Evaluation, d.Tracks[0].Reason));
        Assert.Equal(AssociationStatuses.Verified, Eval(Facts(otherAssigned: 1), o: new IdentityOptions { AllowSharedWorkstation = true }).Evaluation);
    }

    // ─── Several people ───────────────────────────────────────────────────

    [Fact]
    public void Several_workers_read_at_one_reader_are_ambiguous_and_both_are_candidates()
    {
        var d = Eval(Facts(otherReads: new[] { new OtherReadFact(2, Other, T.AddSeconds(3)) }));
        Assert.Equal(AssociationStatuses.Ambiguous, d.Evaluation);
        var t = Assert.Single(d.Tracks);
        Assert.Equal(AssociationReasons.MultipleWorkersAtReader, t.Reason);
        Assert.Equal(new[] { Worker, Other }.OrderBy(x => x), t.CandidateWorkers.OrderBy(x => x));
    }

    [Fact]
    public void Several_tracks_in_the_zone_are_never_matched_by_position()
    {
        var d = Eval(Facts(zones: new[] { ZoneOf(CamA, Track("s1-t7", 30), Track("s1-t9", 5)) }));
        Assert.Equal(AssociationStatuses.Ambiguous, d.Evaluation);
        Assert.Equal(2, d.Tracks.Count);
        Assert.All(d.Tracks, t =>
        {
            Assert.Equal(AssociationStatuses.Ambiguous, t.Status);
            Assert.Equal(AssociationReasons.MultipleTracksInZone, t.Reason);
            Assert.Equal(new[] { "s1-t7", "s1-t9" }, t.CandidateTracks);
        });
    }

    // ─── Time window ──────────────────────────────────────────────────────

    [Fact]
    public void No_track_yet_waits_and_becomes_unverified_when_the_window_closes()
    {
        var empty = Facts(zones: new[] { ZoneOf(CamA) });
        Assert.Equal((AssociationStatuses.Pending, AssociationReasons.WaitingForTrack), (Eval(empty).Evaluation, Eval(empty).Reason));
        Assert.Equal((AssociationStatuses.Unverified, AssociationReasons.NoTrackInZone), (Eval(empty, final: true).Evaluation, Eval(empty, final: true).Reason));
    }

    [Theory]
    [InlineData(600, -300, false)]      // left five minutes before the read: outside the 2-minute window
    [InlineData(600, -60, true)]        // left a minute before: inside
    [InlineData(-60, null, true)]       // entered a minute after the read (tapped, then sat down): inside
    [InlineData(-300, null, false)]     // entered five minutes after: outside
    public void Only_tracks_present_within_the_window_count(int enteredSecondsBefore, int? leftSecondsAfter, bool counts)
    {
        var d = Eval(Facts(zones: new[] { ZoneOf(CamA, Track("s1-t7", enteredSecondsBefore, leftSecondsAfter)) }), final: true);
        Assert.Equal(counts ? AssociationStatuses.Verified : AssociationStatuses.Unverified, d.Evaluation);
    }

    [Fact]
    public void A_workstation_without_a_camera_zone_is_unverified() =>
        Assert.Equal(AssociationReasons.NoWorkstationZone, Eval(Facts(zones: Array.Empty<CameraZoneFact>())).Reason);

    // ─── Contradictions ───────────────────────────────────────────────────

    [Fact]
    public void A_track_already_verified_as_someone_else_goes_to_review_and_the_old_association_is_suspended()
    {
        var other = Track("s1-t7", verifiedWorker: Other);
        var d = Eval(Facts(zones: new[] { ZoneOf(CamA, other) }));
        Assert.Equal(AssociationStatuses.ManualReview, d.Evaluation);
        Assert.Equal(AssociationReasons.TrackVerifiedAsOther, d.Tracks[0].Reason);
        Assert.Contains(d.Suspend, s => s.AssociationId == other.VerifiedAssociationId && s.Reason == AssociationReasons.TrackVerifiedAsOther);
        Assert.Equal(new[] { Worker, Other }, d.Tracks[0].CandidateWorkers);
    }

    [Fact]
    public void A_worker_already_verified_on_another_track_of_the_camera_goes_to_review()
    {
        var existing = new WorkerVerifiedFact(Guid.NewGuid(), CamA, "s1-t3", Ws);
        var d = Eval(Facts(verified: new[] { existing }));
        Assert.Equal((AssociationStatuses.ManualReview, AssociationReasons.WorkerOnOtherTrack), (d.Evaluation, d.Tracks[0].Reason));
        Assert.Contains(d.Suspend, s => s.AssociationId == existing.AssociationId);
    }

    [Fact]
    public void A_new_read_of_the_same_worker_on_the_same_track_refreshes_it()
    {
        var mine = Track("s1-t7", verifiedWorker: Worker);
        var d = Eval(Facts(zones: new[] { ZoneOf(CamA, mine) }));
        var t = Assert.Single(d.Tracks);
        Assert.Equal((AssociationStatuses.Verified, AssociationReasons.Refreshed, mine.VerifiedAssociationId), (t.Status, t.Reason, t.RefreshAssociationId));
        Assert.Empty(d.Suspend);
    }

    [Fact]
    public void A_read_elsewhere_while_verified_suspends_the_old_association_and_verifies_nothing()
    {
        var elsewhere = new WorkerVerifiedFact(Guid.NewGuid(), CamB, "s2-t4", OtherWs);
        var d = Eval(Facts(verified: new[] { elsewhere }));
        Assert.Contains(d.Suspend, s => s.AssociationId == elsewhere.AssociationId && s.Reason == AssociationReasons.ConflictingLocation);
        Assert.Equal((AssociationStatuses.ManualReview, AssociationReasons.ConflictingLocation), (d.Evaluation, d.Tracks[0].Reason));
        // Even when this read's own evidence is incomplete, the contradiction is still flagged.
        var mismatch = Eval(Facts(assignedWs: OtherWs, verified: new[] { elsewhere }));
        Assert.Contains(mismatch.Suspend, s => s.AssociationId == elsewhere.AssociationId);
    }

    // ─── Separation ───────────────────────────────────────────────────────

    [Fact]
    public void Simulated_reads_and_real_tracks_never_combine()
    {
        var realTrack = Facts(sim: true, zones: new[] { ZoneOf(CamA, Track("s1-t7", sim: false)) });
        var d = Eval(realTrack, final: true);
        Assert.Equal(AssociationStatuses.Unverified, d.Evaluation);
        Assert.Equal(AssociationStatuses.Verified, Eval(Facts(sim: true, zones: new[] { ZoneOf(CamA, Track("sim-t1", sim: true)) })).Evaluation);
        Assert.Equal(AssociationStatuses.Unverified, Eval(Facts(sim: false, zones: new[] { ZoneOf(CamA, Track("sim-t1", sim: true)) }), final: true).Evaluation);
    }

    [Fact]
    public void Each_camera_is_judged_on_its_own_track_never_by_linking_cameras()
    {
        var d = Eval(Facts(zones: new[] { ZoneOf(CamA, Track("s1-t7")), ZoneOf(CamB, Track("s2-t2"), Track("s2-t5")) }));
        Assert.Equal(AssociationStatuses.Verified, d.Tracks.Single(t => t.CameraId == CamA).Status);
        Assert.All(d.Tracks.Where(t => t.CameraId == CamB), t => Assert.Equal(AssociationStatuses.Ambiguous, t.Status));
    }

    // ─── Card identifiers, review decisions, options ──────────────────────

    [Fact]
    public void Raw_card_uids_are_never_kept_only_a_hash_and_the_last_four_characters()
    {
        var hash = CardIdentifiers.Hash("04A1B2C3D4");
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain("04A1B2C3D4", hash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(hash, CardIdentifiers.Hash("04A1B2C3D4"));
        Assert.NotEqual(hash, CardIdentifiers.Hash("04A1B2C3D5"));
        Assert.Equal("…C3D4", CardIdentifiers.Masked(CardIdentifiers.Suffix("04A1B2C3D4")));
    }

    [Fact]
    public void Review_decisions_depend_on_the_status_and_closed_cases_stay_closed()
    {
        Assert.Equal(new[] { ReviewDecisions.Revoke, ReviewDecisions.Ambiguous }, IdentityAssociationService.AllowedDecisions(AssociationStatuses.Verified));
        Assert.Contains(ReviewDecisions.Confirm, IdentityAssociationService.AllowedDecisions(AssociationStatuses.Ambiguous));
        Assert.Contains(ReviewDecisions.Confirm, IdentityAssociationService.AllowedDecisions(AssociationStatuses.ManualReview));
        foreach (var closed in new[] { AssociationStatuses.Expired, AssociationStatuses.Revoked, AssociationStatuses.Unverified })
            Assert.Empty(IdentityAssociationService.AllowedDecisions(closed));
    }

    [Fact]
    public void Windows_and_validity_are_configurable_and_bounded()
    {
        var o = new IdentityOptions { WindowBeforeSeconds = 1, WindowAfterSeconds = 99999, ValidityMinutes = 0 };
        Assert.Equal(TimeSpan.FromSeconds(5), o.Before);
        Assert.Equal(TimeSpan.FromHours(1), o.After);
        Assert.Equal(TimeSpan.FromMinutes(1), o.Validity);
        Assert.Equal(TimeSpan.FromMinutes(240), new IdentityOptions().Validity);
        var narrow = new IdentityOptions { WindowBeforeSeconds = 10, WindowAfterSeconds = 10 };
        Assert.Equal(AssociationStatuses.Unverified, Eval(Facts(zones: new[] { ZoneOf(CamA, Track("s1-t7", 600, -60)) }), final: true, o: narrow).Evaluation);
    }
}
