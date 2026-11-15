"""Measure the pipeline on a recorded video with activity recognition off and on (real models, nothing published).

    python scripts/benchmark_activity.py                                  # data/samples/people_walkthrough.mp4
    python scripts/benchmark_activity.py data/my_clip.mp4 --process-fps 8 --activity-fps 4

Plays the video at its own speed (like a live camera), so the numbers are
what one camera costs in the running service: processing FPS achieved,
per-frame latency, pose inference time, and the activity events produced.
It reports throughput, not accuracy (see scripts/evaluate_activity.py).
"""
from __future__ import annotations

import argparse
import sys
import time
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


def main() -> int:
    from app.activity_recognition.activity_config import ActivityConfig
    from app.activity_recognition.pose_estimator import YoloPoseEstimator
    from app.camera.manager import SessionManager
    from app.camera.sources import OpenCvSource
    from app.core.config import ROOT, Settings
    from app.detection.detector import YoloPersonDetector
    from app.events.schemas import ACTIVITY_EVENT_TYPES
    from tests.conftest import FakeDirectory, MemoryPublisher, camera

    ap = argparse.ArgumentParser()
    ap.add_argument("video", nargs="?", default=str(ROOT / "data" / "samples" / "people_walkthrough.mp4"))
    ap.add_argument("--process-fps", type=float, default=8.0)
    ap.add_argument("--activity-fps", type=float, default=4.0)
    ap.add_argument("--device", default="cpu")
    a = ap.parse_args()

    s = Settings(_env_file=None, api_key="x" * 24, process_fps=a.process_fps, publish_events=False)
    det = YoloPersonDetector(s.model_path, a.device, s.confidence, s.image_size)
    pose = YoloPoseEstimator(s.activity_model_path, a.device, s.activity_pose_confidence, s.activity_image_size)
    for m in (det.load(), pose.load()):
        if not m.loaded:
            print("Model not loaded:", m.error)
            return 1
    for activity in (False, True):
        cam = camera()
        pub = MemoryPublisher()
        mgr = SessionManager(s, FakeDirectory({cam.id: cam}), det, pub, a.device, pose_estimator=pose,
                             activity_config=ActivityConfig(enabled=activity, process_fps=a.activity_fps),
                             source_factory=lambda cfg, opts: OpenCvSource(a.video, "file", "benchmark video", realtime=True))
        t0 = time.perf_counter()
        mgr.start(cam.id)
        while mgr.get(cam.id).state.value not in ("FINISHED", "FAILED", "STOPPED"):
            time.sleep(0.1)
        wall = time.perf_counter() - t0
        sess = mgr.get(cam.id)
        snap = sess.metrics.snapshot()
        print(f"\n=== activity recognition {'ON' if activity else 'OFF'} ({Path(a.video).name}, {a.device}) ===")
        print(f"wall {wall:.1f} s | frames read {snap['frames_read']} processed {snap['frames_processed']} skipped {snap['frames_skipped']}")
        print(f"processing fps {snap['frames_processed'] / wall:.2f} (target {a.process_fps}) | per-frame latency avg {snap['avg_latency_ms']} ms")
        print(f"detector avg inference {det.info.avg_inference_ms} ms")
        if activity:
            am = sess.activity.metrics.snapshot()
            print(f"pose inferences {am['pose_inferences']} avg {am['avg_pose_ms']} ms | activity step avg {am['avg_step_ms']} ms "
                  f"| activity rate {am['pose_inferences'] / wall:.2f}/s (target {a.activity_fps})")
            print(f"observations {am['observations']} with pose {am['observations_with_pose']} without {am['observations_without_pose']}")
            print(f"window predictions {am['predictions']} | unknown rate {am['unknown_rate']} | reasons {am['unknown_reasons']}")
            acts = [e for e in pub.events if e.event_type in ACTIVITY_EVENT_TYPES]
            print(f"activity events {Counter(e.event_type for e in acts)}")
            print("state changes:", [(e.track_id, e.payload.get('previousActivity'), e.payload['activity'], e.confidence)
                                     for e in acts if e.event_type != "ACTIVITY_CLASSIFIED"])
        mgr.shutdown(5)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
