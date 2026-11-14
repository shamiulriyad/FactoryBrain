"""ByteTrack (the real algorithm) fed with scripted detections: ids across frames, occlusion, entry, independence per camera."""
from app.detection.types import BBox, Detection
from app.tracking.bytetrack import ByteTrackTracker


def det(x: float, y: float = 50, w: float = 60, h: float = 160, conf: float = 0.9) -> Detection:
    return Detection(BBox(x, y, x + w, y + h), conf, 0, "person", "cam", 0.0)


def run(tracker: ByteTrackTracker, frames: list[list[Detection]]):
    return [tracker.update(f, 640, 360) for f in frames]


def test_a_walking_person_keeps_one_track_id():
    t = ByteTrackTracker(frame_rate=10)
    out = run(t, [[det(20 + 8 * i)] for i in range(20)])
    ids = {o.track_id for frame in out[1:] for o in frame}
    assert len(ids) == 1
    assert all(len(f) == 1 for f in out[1:])
    # The box follows the person.
    assert out[-1][0].bbox.x1 > out[1][0].bbox.x1


def test_several_people_get_different_ids_and_a_new_person_a_new_one():
    t = ByteTrackTracker(frame_rate=10)
    frames = [[det(20 + 5 * i), det(400 - 5 * i, y=60)] for i in range(10)]
    frames += [[det(70 + 5 * i), det(350 - 5 * i, y=60), det(250, y=120, conf=0.92)] for i in range(10)]   # someone enters
    out = run(t, frames)
    early = {o.track_id for o in out[5]}
    late = {o.track_id for o in out[-1]}
    assert len(early) == 2
    assert len(late) == 3 and early < late


def test_a_short_occlusion_keeps_the_same_id():
    t = ByteTrackTracker(frame_rate=10, track_buffer_seconds=2.0)
    before = run(t, [[det(100 + 4 * i)] for i in range(8)])
    run(t, [[] for _ in range(5)])                       # hidden for 0.5 s
    after = run(t, [[det(152 + 4 * i)] for i in range(5)])
    assert {o.track_id for o in before[-1]} == {o.track_id for o in after[-1]}


def test_a_person_gone_longer_than_the_buffer_comes_back_with_a_new_id():
    t = ByteTrackTracker(frame_rate=10, track_buffer_seconds=0.3)
    first = run(t, [[det(100)] for _ in range(5)])
    run(t, [[] for _ in range(20)])                      # 2 s away
    second = run(t, [[det(100)] for _ in range(5)])
    assert first[-1][0].track_id != second[-1][0].track_id


def test_cameras_track_independently():
    a, b = ByteTrackTracker(frame_rate=10), ByteTrackTracker(frame_rate=10)
    out_a = run(a, [[det(20 + 5 * i)] for i in range(6)])
    out_b = run(b, [[det(300), det(500)] for _ in range(6)])
    assert len(out_a[-1]) == 1 and len(out_b[-1]) == 2
    a.reset()                                            # resetting one camera leaves the other alone
    assert run(a, [[]])[0] == []
    assert len(run(b, [[det(300), det(500)]])[0]) == 2
    assert {o.track_id for o in out_a[-1]}.isdisjoint({o.track_id for o in out_b[-1]})
