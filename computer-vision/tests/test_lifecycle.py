"""Track lifecycle: confirmation, updates, loss after disappearance, zone changes with debounce."""
from app.detection.types import BBox
from app.tracking.base import TrackedObject
from app.tracking.lifecycle import TrackLifecycle
from app.zones.types import Zone

Z1 = Zone("z1", "T-025", "Workstation", ((0, 0), (0.5, 0), (0.5, 1), (0, 1)))
Z2 = Zone("z2", "Walkway", "Walkway", ((0.5, 0), (1, 0), (1, 1), (0.5, 1)))


def obj(tid: int = 7) -> TrackedObject:
    return TrackedObject(tid, BBox(10, 10, 50, 100), 0.9, 0)


def kinds(transitions):
    return [t.kind for t in transitions]


def test_a_track_is_reported_after_min_hits_and_lost_after_disappearing():
    lc = TrackLifecycle(min_hits=2, lost_after_seconds=1.0, update_seconds=100)
    assert kinds(lc.step([(obj(), None)], 0.0)) == []                  # one-frame flicker: nothing yet
    assert kinds(lc.step([(obj(), None)], 0.2)) == ["DETECTED"]
    assert kinds(lc.step([], 0.8)) == []                               # short gap: still the same person
    assert kinds(lc.step([(obj(), None)], 0.9)) == []
    lost = lc.step([], 2.0)
    assert kinds(lost) == ["LOST"]
    assert lost[0].duration_seconds == 0.9
    assert lc.active == 0


def test_a_flicker_that_never_confirms_is_never_reported():
    lc = TrackLifecycle(min_hits=3, lost_after_seconds=0.5)
    lc.step([(obj(1), None)], 0.0)
    assert kinds(lc.step([], 1.0)) == []


def test_updates_are_rate_limited():
    lc = TrackLifecycle(min_hits=1, update_seconds=5)
    out = []
    for t in range(0, 21):
        out += kinds(lc.step([(obj(), None)], float(t)))
    assert out.count("DETECTED") == 1
    assert out.count("UPDATED") == 4


def test_zone_changes_need_consecutive_frames_and_report_exit_then_entry():
    lc = TrackLifecycle(min_hits=1, zone_debounce_frames=2, update_seconds=100)
    assert kinds(lc.step([(obj(), Z1)], 0.0)) == ["DETECTED", "ZONE_ENTERED"]
    assert kinds(lc.step([(obj(), Z2)], 0.1)) == []                    # one frame elsewhere: debounced
    assert kinds(lc.step([(obj(), Z1)], 0.2)) == []
    assert kinds(lc.step([(obj(), Z2)], 0.3)) == []
    moved = lc.step([(obj(), Z2)], 0.4)
    assert kinds(moved) == ["ZONE_EXITED", "ZONE_ENTERED"]
    assert [t.zone.id for t in moved] == ["z1", "z2"]
    assert kinds(lc.step([(obj(), None)], 0.5) + lc.step([(obj(), None)], 0.6)) == ["ZONE_EXITED"]


def test_losing_a_track_inside_a_zone_exits_it_and_flush_reports_everything():
    lc = TrackLifecycle(min_hits=1, lost_after_seconds=0.5)
    lc.step([(obj(1), Z1), (obj(2), None)], 0.0)
    assert kinds(lc.step([(obj(2), None)], 1.0)) == ["ZONE_EXITED", "LOST"]
    assert kinds(lc.flush()) == ["LOST"]
    assert lc.tracks == {}
