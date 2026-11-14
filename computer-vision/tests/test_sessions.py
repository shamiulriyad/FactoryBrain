"""Camera sessions end to end with a scripted detector: lifecycle, events, reconnects, limits, independence."""
import pytest

from app.camera.manager import SessionManager, StartOptions
from app.camera.sources import SourceOverride, resolve_source
from app.core.errors import InvalidSource, ModelNotReady, SessionAlreadyRunning, SessionNotRunning, TooManySessions
from tests.conftest import FakeDirectory, FakeSource, MemoryPublisher, ScriptedDetector, camera, rect_zone, wait_for

CAM1 = "11111111-1111-4111-8111-111111111111"
CAM2 = "22222222-2222-4222-8222-222222222222"


def walking(frames: range, x0: float = 40, step: float = 12):
    """One person walking right; feet at y = 300 (0.83 of 360)."""
    return {i: [(x0 + step * i, 120, x0 + step * i + 60, 300, 0.9)] for i in frames}


def manager(settings, detector, cams, sources, publisher=None):  # noqa: ANN001
    pub = publisher or MemoryPublisher()
    m = SessionManager(settings, FakeDirectory(cams), detector, pub, "cpu", source_factory=lambda cfg, opts: sources[cfg.id])
    return m, pub


def test_a_recorded_video_is_analysed_into_events_and_finishes(settings):
    zone = rect_zone("z-1", "T-025", 0.0, 0.5, 0.5, 1.0)               # left half, lower part (where feet are)
    cfg = camera(CAM1, zones=[zone])
    src = FakeSource(frames=40, delay=0.04)          # paced like a real recording
    m, pub = manager(settings, ScriptedDetector(walking(range(0, 30))), {CAM1: cfg}, {CAM1: src})
    status = m.start(CAM1)
    assert status["state"] in ("STARTING", "RUNNING")
    assert wait_for(lambda: m.get(CAM1).state.value == "FINISHED")
    types = pub.types()
    assert types[0] == "MODEL_STATUS_CHANGED" and types[1] == "CAMERA_STATUS_CHANGED"
    assert "PERSON_DETECTED" in types and "ZONE_ENTERED" in types and "ZONE_EXITED" in types and types[-1] == "PERSON_LOST"
    tracks = {e.track_id for e in pub.events if e.track_id}
    assert len(tracks) == 1                                           # one person, one temporary id
    assert all(e.source_type == "REAL" and e.camera_id == CAM1 for e in pub.events)
    assert src.released >= 1                                          # the source was released
    snap = m.get(CAM1).metrics.snapshot()
    # MODEL_STATUS_CHANGED is sent by the manager (once per factory), not by the camera session.
    assert snap["frames_processed"] > 0 and snap["events_emitted"] == len([e for e in pub.events if e.event_type != "MODEL_STATUS_CHANGED"])


def test_duplicate_start_stop_and_restart(settings):
    src = FakeSource(frames=10, live=True)
    m, pub = manager(settings, ScriptedDetector(), {CAM1: camera(CAM1)}, {CAM1: src})
    m.start(CAM1)
    assert wait_for(lambda: m.get(CAM1).state.value == "RUNNING")
    with pytest.raises(SessionAlreadyRunning):
        m.start(CAM1)
    stopped = m.stop(CAM1)
    assert stopped["state"] == "STOPPED" and src.released >= 1
    assert "OFFLINE" not in [e.payload.get("toStatus") for e in pub.events]   # stopping analysis isn't the camera going offline
    with pytest.raises(SessionNotRunning):
        m.stop(CAM1)
    m.start(CAM1)                                                         # a stopped camera can start again
    assert wait_for(lambda: m.get(CAM1).state.value == "RUNNING")
    m.shutdown(2)


def test_session_limit_inactive_cameras_and_missing_model(settings):
    cams = {CAM1: camera(CAM1), CAM2: camera(CAM2, code="CAM-002"), "33333333-3333-4333-8333-333333333333":
            camera("33333333-3333-4333-8333-333333333333", code="CAM-003"), "off": camera("off", code="CAM-OFF", status="Inactive")}
    sources = {k: FakeSource(frames=5, live=True) for k in cams}
    m, _ = manager(settings, ScriptedDetector(), cams, sources)
    with pytest.raises(InvalidSource) as e:
        m.start("off")
    assert e.value.code == "camera_not_active"
    m.start(CAM1)
    m.start(CAM2)
    with pytest.raises(TooManySessions):
        m.start("33333333-3333-4333-8333-333333333333")
    m.shutdown(2)
    m2, _ = manager(settings, ScriptedDetector(loaded=False), {CAM1: camera(CAM1)}, {CAM1: FakeSource(1)})
    with pytest.raises(ModelNotReady):
        m2.start(CAM1)


def test_an_unopenable_source_fails_visibly_and_reports_the_camera_offline(settings):
    m, pub = manager(settings, ScriptedDetector(), {CAM1: camera(CAM1)}, {CAM1: FakeSource(5, fail_open=True)})
    m.start(CAM1)
    assert wait_for(lambda: m.get(CAM1).state.value == "FAILED")
    assert "Can't open" in m.get(CAM1).error
    assert [e.payload.get("toStatus") for e in pub.events if e.event_type == "CAMERA_STATUS_CHANGED"] == ["OFFLINE"]


def test_a_dropped_live_stream_goes_offline_and_comes_back(settings):
    src = FakeSource(frames=30, live=True, drop_after=10)
    m, pub = manager(settings, ScriptedDetector(walking(range(0, 30))), {CAM1: camera(CAM1)}, {CAM1: src})
    m.start(CAM1)
    assert wait_for(lambda: [e.payload.get("toStatus") for e in pub.events if e.event_type == "CAMERA_STATUS_CHANGED"] == ["ONLINE", "OFFLINE", "ONLINE"])
    assert m.get(CAM1).metrics.reconnects == 1
    m.shutdown(2)


def test_a_stream_that_never_comes_back_fails_after_the_retries(settings):
    src = FakeSource(frames=30, live=True, drop_after=3, reopen_fails=True)
    m, pub = manager(settings, ScriptedDetector(), {CAM1: camera(CAM1)}, {CAM1: src})
    m.start(CAM1)
    assert wait_for(lambda: m.get(CAM1).state.value == "FAILED")
    assert "Gave up after 2" in m.get(CAM1).error


def test_two_cameras_are_processed_independently(settings):
    cams = {CAM1: camera(CAM1), CAM2: camera(CAM2, code="CAM-002")}
    sources = {CAM1: FakeSource(frames=40, delay=0.04), CAM2: FakeSource(frames=40, delay=0.04)}
    det = ScriptedDetector({**walking(range(0, 30)), **{}})
    m, pub = manager(settings, det, cams, sources)
    m.start(CAM1)
    m.start(CAM2)
    assert wait_for(lambda: all(m.get(c).state.value == "FINISHED" for c in cams))
    t1 = {e.track_id for e in pub.events if e.camera_id == CAM1 and e.track_id}
    t2 = {e.track_id for e in pub.events if e.camera_id == CAM2 and e.track_id}
    assert len(t1) == 1 and len(t2) == 1 and t1.isdisjoint(t2)          # same picture, separate tracks per camera
    assert m.get(CAM1).builder.ctx.session_tag != m.get(CAM2).builder.ctx.session_tag
    assert pub.types().count("MODEL_STATUS_CHANGED") == 1               # once per factory


def test_sources_are_resolved_from_configuration_and_overrides_are_guarded(settings, tmp_path):
    with pytest.raises(InvalidSource) as e:
        resolve_source(camera(CAM1, source={"sourceType": "None"}), FakeDirectory({}), tmp_path, 0, False)
    assert e.value.code == "no_source"
    with pytest.raises(InvalidSource):
        resolve_source(camera(CAM1, source={"sourceType": "RecordedVideo", "hasVideo": False}), FakeDirectory({}), tmp_path, 0, False)
    webcam = resolve_source(camera(CAM1, source={"sourceType": "LocalWebcam", "webcamLabel": "USB camera 2"}), FakeDirectory({}), tmp_path, 0, False)
    assert webcam.kind == "webcam" and webcam.description == "local webcam 2"
    stream = resolve_source(camera(CAM1, source={"sourceType": "Rtsp", "streamUrl": "rtsp://cam:secret@10.0.0.5/live"}), FakeDirectory({}), tmp_path, 0, False)
    assert "secret" not in stream.description and "***" in stream.description
    with pytest.raises(InvalidSource) as e:
        resolve_source(camera(CAM1), FakeDirectory({}), tmp_path, 0, False, SourceOverride("file", "x.mp4"), allow_override=False)
    assert e.value.code == "override_disabled"
    with pytest.raises(InvalidSource) as e:
        resolve_source(camera(CAM1), FakeDirectory({}), tmp_path, 0, False, SourceOverride("file", "../../etc/passwd"), allow_override=True)
    assert e.value.code == "override_outside_data_dir"
    bad = resolve_source(camera(CAM1), FakeDirectory({}), tmp_path, 0, False, SourceOverride("webcam", index=9), allow_override=True)
    (tmp_path / "broken.mp4").write_bytes(b"not a video")
    broken = resolve_source(camera(CAM1), FakeDirectory({}), tmp_path, 0, False, SourceOverride("file", "broken.mp4"), allow_override=True)
    with pytest.raises(InvalidSource):
        broken.open()
    assert bad.kind == "webcam"


def test_shutdown_stops_everything_and_refuses_new_work(settings):
    cams = {CAM1: camera(CAM1), CAM2: camera(CAM2, code="CAM-002")}
    sources = {CAM1: FakeSource(10, live=True), CAM2: FakeSource(10, live=True)}
    m, pub = manager(settings, ScriptedDetector(), cams, sources)
    m.start(CAM1)
    m.start(CAM2)
    assert wait_for(lambda: len(m.running()) == 2 and all(s.state.value == "RUNNING" for s in m.running()))
    m.shutdown(3)
    assert m.running() == [] and all(not s.alive for s in m.all())
    assert all(src.released >= 1 for src in sources.values()) and pub.closed
    with pytest.raises(InvalidSource):
        m.start(CAM1)
