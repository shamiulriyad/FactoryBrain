# Pose model weights

Not committed. Download the COCO-17-keypoint YOLO pose model with:

    python scripts/download_model.py --pose            # yolo11n-pose.pt, 6 MB
    python scripts/download_model.py --pose yolo11s-pose.pt   # more accurate, slower

`CV_ACTIVITY_MODEL_PATH` points at the file (default `models/pose/yolo11n-pose.pt`).
Ultralytics weights are AGPL-3.0 (see the README's Licensing section).
