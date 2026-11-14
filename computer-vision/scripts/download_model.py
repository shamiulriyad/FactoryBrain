"""Download pretrained YOLO weights into computer-vision/models/ (kept out of Git).

    python scripts/download_model.py              # yolo11n.pt (5 MB, fastest; good on CPU)
    python scripts/download_model.py yolo11s.pt   # more accurate, slower

Weights come from Ultralytics' GitHub release assets (COCO-pretrained; class 0 = person).
"""
from __future__ import annotations

import sys
from pathlib import Path

MODELS = Path(__file__).resolve().parents[1] / "models"


def main() -> int:
    from ultralytics.utils.downloads import attempt_download_asset

    name = sys.argv[1] if len(sys.argv) > 1 else "yolo11n.pt"
    MODELS.mkdir(parents=True, exist_ok=True)
    target = MODELS / name
    if target.is_file():
        print(f"Already present: {target} ({target.stat().st_size / 2**20:.1f} MB)")
        return 0
    path = Path(attempt_download_asset(str(target)))
    if not path.is_file():
        print(f"Download failed for {name}.")
        return 1
    print(f"Downloaded {path} ({path.stat().st_size / 2**20:.1f} MB). Set CV_MODEL_PATH=models/{name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
