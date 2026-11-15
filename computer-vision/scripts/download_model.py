"""Download pretrained YOLO weights into computer-vision/models/ (kept out of Git).

    python scripts/download_model.py              # yolo11n.pt (5 MB, fastest; good on CPU) → models/
    python scripts/download_model.py yolo11s.pt   # more accurate, slower
    python scripts/download_model.py --pose       # yolo11n-pose.pt (6 MB, COCO-17 keypoints) → models/pose/
    python scripts/download_model.py --pose yolo11s-pose.pt

Weights come from Ultralytics' GitHub release assets (COCO-pretrained; class 0 = person).
"""
from __future__ import annotations

import sys
from pathlib import Path

MODELS = Path(__file__).resolve().parents[1] / "models"


def main() -> int:
    from ultralytics.utils.downloads import attempt_download_asset

    args = [a for a in sys.argv[1:] if a != "--pose"]
    pose = "--pose" in sys.argv[1:]
    name = args[0] if args else ("yolo11n-pose.pt" if pose else "yolo11n.pt")
    folder = MODELS / "pose" if pose else MODELS
    folder.mkdir(parents=True, exist_ok=True)
    target = folder / name
    if target.is_file():
        print(f"Already present: {target} ({target.stat().st_size / 2**20:.1f} MB)")
        return 0
    path = Path(attempt_download_asset(str(target)))
    if not path.is_file():
        print(f"Download failed for {name}.")
        return 1
    setting = "CV_ACTIVITY_MODEL_PATH" if pose else "CV_MODEL_PATH"
    print(f"Downloaded {path} ({path.stat().st_size / 2**20:.1f} MB). Set {setting}={path.relative_to(MODELS.parent).as_posix()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
