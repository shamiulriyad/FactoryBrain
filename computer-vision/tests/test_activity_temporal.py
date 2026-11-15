"""Temporal buffer and activity state: isolation per camera and track, gaps, expiry, smoothing and transitions."""
from app.activity_recognition.activity_classifier import BaselineTemporalClassifier
from app.activity_recognition.schemas import ActivityLabel, ActivityPrediction, UncertainReason, WindowQuality
from app.activity_recognition.state_manager import ActivityStateManager
from app.activity_recognition.temporal_buffer import TemporalBuffer
from tests.activity_helpers import CFG, observe

Q = WindowQuality(10, 10, 2.5, 0.9, 1.0, 0.0, 1.0)


def pred(label: str, conf: float = 0.9, t: float = 0.0) -> ActivityPrediction:
    lab = ActivityLabel(label)
    return ActivityPrediction(lab, conf, None if lab == ActivityLabel.UNKNOWN else lab, {}, Q, t - 2.5, t,
                              UncertainReason.LOW_CONFIDENCE if lab == ActivityLabel.UNKNOWN else None)


# ─── Buffer ───────────────────────────────────────────────────────────────

def test_the_buffer_keeps_cameras_and_tracks_apart():
    buf = TemporalBuffer(window_seconds=3)
    for o in observe("STANDING", camera="cam-A", track=5) + observe("SITTING", camera="cam-B", track=5) + observe("BENDING", camera="cam-A", track=6):
        buf.add(o)
    assert len(buf) == 3
    a5, b5 = buf.window("cam-A", 5), buf.window("cam-B", 5)
    assert all(o.camera_id == "cam-A" and o.track_id == 5 for o in a5)
    assert all(o.camera_id == "cam-B" for o in b5)
    clf = BaselineTemporalClassifier(CFG)
    # Same track number on two cameras: two different people, two different answers.
    assert clf.classify(a5).label == ActivityLabel.STANDING and clf.classify(b5).label == ActivityLabel.SITTING


def test_the_window_is_time_based():
    buf = TemporalBuffer(window_seconds=2.0)
    for o in observe("STANDING", n=20, fps=4):                    # 5 s of observations
        buf.add(o)
    w = buf.window("cam-1", 1)
    assert w[-1].timestamp - w[0].timestamp <= 2.0 and len(w) == 9


def test_a_long_gap_restarts_the_window_and_out_of_order_frames_are_ignored():
    buf = TemporalBuffer(window_seconds=3, max_gap_seconds=1.0)
    first = observe("STANDING", n=6, t0=1000.0)
    later = observe("STANDING", n=3, t0=1010.0)                    # 8 s later: motion across the gap can't be trusted
    for o in first + later:
        buf.add(o)
    assert [o.timestamp for o in buf.window("cam-1", 1)] == [o.timestamp for o in later]
    assert buf.stats.gap_resets == 1
    buf.add(first[0])                                              # older than what's there
    assert buf.stats.rejected == 1 and len(buf.window("cam-1", 1)) == 3


def test_tracks_unseen_for_a_while_are_dropped():
    buf = TemporalBuffer(reset_after_seconds=3)
    for o in observe("STANDING", n=4, t0=1000.0, track=1) + observe("STANDING", n=4, t0=1004.0, track=2):
        buf.add(o)
    gone = buf.expire(now=1006.0)
    assert gone == [("cam-1", 1)] and buf.keys() == [("cam-1", 2)]


# ─── State manager ────────────────────────────────────────────────────────

def run(sm: ActivityStateManager, labels: list[str], dt: float = 0.25, t0: float = 0.0, camera: str = "cam-1", track: int = 1):
    out = []
    for i, lab in enumerate(labels):
        out += sm.update(camera, track, pred(lab, t=t0 + i * dt), t0 + i * dt)
    return out


def test_a_state_is_accepted_only_after_the_minimum_duration():
    sm = ActivityStateManager(min_state_seconds=1.0, report_seconds=100)
    ups = run(sm, ["STANDING"] * 4)                               # 0.75 s
    assert ups == []
    ups = run(sm, ["STANDING"] * 2, t0=1.0)
    assert [u.kind for u in ups] == ["STATE_CHANGED"] and ups[0].activity == ActivityLabel.STANDING and ups[0].previous is None
    assert sm.get("cam-1", 1).since == 0.0                        # the state began when it was first seen


def test_single_odd_predictions_do_not_flip_the_state():
    sm = ActivityStateManager(min_state_seconds=1.0, report_seconds=100)
    run(sm, ["STANDING"] * 6)
    ups = run(sm, ["STANDING", "SITTING", "STANDING", "BENDING", "STANDING", "STANDING"], t0=1.5)
    assert ups == [] and sm.get("cam-1", 1).current == ActivityLabel.STANDING


def test_transitions_are_recorded_with_the_previous_activity_and_its_duration():
    sm = ActivityStateManager(min_state_seconds=1.0, report_seconds=100)
    ups = run(sm, ["STANDING"] * 8 + ["WALKING"] * 8 + ["STANDING"] * 8)
    changes = [(u.previous and u.previous.value, u.activity.value) for u in ups if u.kind == "STATE_CHANGED"]
    assert changes == [(None, "STANDING"), ("STANDING", "WALKING"), ("WALKING", "STANDING")]
    walking = [u for u in ups if u.activity == ActivityLabel.WALKING][0]
    assert walking.previous_duration == 2.0                       # standing from 0.0 to 2.0
    hist = sm.get("cam-1", 1).history
    assert [h.to_activity.value for h in hist] == ["STANDING", "WALKING", "STANDING"]


def test_unknown_must_last_before_it_replaces_a_known_state():
    sm = ActivityStateManager(min_state_seconds=1.0, unknown_after_seconds=2.0, report_seconds=100)
    run(sm, ["SITTING"] * 6)
    brief = run(sm, ["UNKNOWN"] * 6 + ["SITTING"] * 4, t0=1.5)    # 1.25 s of occlusion: still sitting
    assert brief == [] and sm.get("cam-1", 1).current == ActivityLabel.SITTING
    long = run(sm, ["UNKNOWN"] * 10, t0=5.0)                       # 2.25 s: now uncertain
    assert [u.kind for u in long] == ["UNCERTAIN"] and long[0].previous == ActivityLabel.SITTING
    back = run(sm, ["SITTING"] * 6, t0=8.0)
    assert [(u.kind, u.previous.value, u.activity.value) for u in back] == [("STATE_CHANGED", "UNKNOWN", "SITTING")]


def test_a_stable_state_is_reported_periodically_with_smoothed_confidence():
    sm = ActivityStateManager(min_state_seconds=0.5, report_seconds=2.0)
    ups = run(sm, ["STANDING"] * 21)                               # 5 s
    kinds = [u.kind for u in ups]
    assert kinds[0] == "STATE_CHANGED" and kinds.count("CLASSIFIED") == 2


def test_states_are_per_camera_and_per_track():
    sm = ActivityStateManager(min_state_seconds=0.5, report_seconds=100)
    run(sm, ["SITTING"] * 6, camera="cam-A", track=4)
    run(sm, ["WALKING"] * 6, camera="cam-B", track=4)
    assert sm.get("cam-A", 4).current == ActivityLabel.SITTING and sm.get("cam-B", 4).current == ActivityLabel.WALKING


def test_brief_tracking_loss_keeps_the_state_and_long_loss_forgets_it():
    sm = ActivityStateManager(min_state_seconds=0.5, report_seconds=100, reset_after_seconds=3.0)
    run(sm, ["SITTING"] * 6)
    assert sm.expire(now=1.25 + 2.0) == []                        # 2 s unseen
    assert run(sm, ["SITTING"] * 2, t0=3.5) == [] and sm.get("cam-1", 1).current == ActivityLabel.SITTING
    gone = sm.expire(now=3.75 + 3.0)
    assert [g.track_id for g in gone] == [1] and sm.get("cam-1", 1) is None
