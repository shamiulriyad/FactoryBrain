"""Video import, metadata, frame extraction, clip export and duplicate detection.

* Import copies a video into ``raw/<label hint or unsorted>/<video id>.<ext>``
  and records its metadata in ``videos.jsonl``. The id comes from the file's
  SHA-256, so the same file is never imported twice.
* A perceptual signature (difference hashes of 8 evenly spaced frames) finds
  near-duplicates: re-encodes, trims and copies of the same recording, which
  must never end up in different splits.
* Frames and clips are read with OpenCV by timestamp, so a clip is the same
  whatever the source frame rate.
"""
from __future__ import annotations

import hashlib
import shutil
from datetime import datetime, timezone
from pathlib import Path

import cv2
import numpy as np

from app.garment_activities.config import DatasetPaths
from app.garment_activities.schemas import VideoRecord, load_videos, write_jsonl

VIDEO_EXTENSIONS = {".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v"}
SIGNATURE_FRAMES = 8


class VideoError(ValueError):
    pass


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def probe(path: Path) -> dict:
    """fps, frame count, duration, size and codec of a video (OpenCV / FFmpeg)."""
    cap = cv2.VideoCapture(str(path))
    if not cap.isOpened():
        raise VideoError(f"{path.name}: not a readable video.")
    try:
        fps = float(cap.get(cv2.CAP_PROP_FPS) or 0.0)
        count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
        w, h = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0), int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
        fourcc = int(cap.get(cv2.CAP_PROP_FOURCC) or 0)
        codec = "".join(chr((fourcc >> (8 * i)) & 0xFF) for i in range(4)).strip("\x00 ") or "unknown"
        ok, frame = cap.read()
    finally:
        cap.release()
    if not ok or frame is None or fps <= 0 or w <= 0:
        raise VideoError(f"{path.name}: no decodable frames.")
    return {"fps": round(fps, 3), "frame_count": count, "duration_s": round(count / fps, 3) if count else 0.0, "width": w, "height": h,
            "codec": codec}


def dhash(frame: np.ndarray, size: int = 8) -> int:
    g = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY) if frame.ndim == 3 else frame
    small = cv2.resize(g, (size + 1, size), interpolation=cv2.INTER_AREA)
    bits = (small[:, 1:] > small[:, :-1]).flatten()
    return int("".join("1" if b else "0" for b in bits), 2)


def signature(path: Path, frames: int = SIGNATURE_FRAMES) -> list[int]:
    cap = cv2.VideoCapture(str(path))
    try:
        n = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
        out = []
        for i in range(frames):
            cap.set(cv2.CAP_PROP_POS_FRAMES, int((i + 0.5) * n / frames) if n else 0)
            ok, f = cap.read()
            if ok and f is not None:
                out.append(dhash(f))
        return out
    finally:
        cap.release()


def signature_distance(a: list[int], b: list[int]) -> float | None:
    """Mean Hamming distance (0–64) between two signatures; None when they can't be compared."""
    n = min(len(a), len(b))
    if n == 0:
        return None
    return sum(bin(x ^ y).count("1") for x, y in zip(a[:n], b[:n])) / n


def near_duplicates(videos: dict[str, VideoRecord], threshold: float = 6.0) -> list[tuple[str, str, float]]:
    ids = sorted(videos)
    out = []
    for i, a in enumerate(ids):
        for b in ids[i + 1:]:
            d = signature_distance(videos[a].signature, videos[b].signature)
            if d is not None and d <= threshold:
                out.append((a, b, round(d, 2)))
    return out


def import_video(src: Path, paths: DatasetPaths, camera_id: str | None = None, recording_group: str | None = None,
                 label_hint: str | None = None, move: bool = False) -> tuple[VideoRecord, str]:
    """Copy (or move) ``src`` into the dataset and register it. Returns (record, outcome: imported | duplicate)."""
    src = Path(src)
    if src.suffix.lower() not in VIDEO_EXTENSIONS:
        raise VideoError(f"{src.name}: unsupported extension (use one of {', '.join(sorted(VIDEO_EXTENSIONS))}).")
    meta = probe(src)
    digest = sha256_of(src)
    videos = load_videos(paths.videos_manifest)
    for v in videos.values():
        if v.sha256 == digest:
            return v, "duplicate"
    video_id = "v-" + digest[:12]
    folder = (label_hint or "unsorted").strip().lower().replace(" ", "_")
    dest = paths.raw / folder / f"{video_id}{src.suffix.lower()}"
    dest.parent.mkdir(parents=True, exist_ok=True)
    (shutil.move if move else shutil.copy2)(str(src), str(dest))
    rec = VideoRecord(video_id=video_id, file=dest.relative_to(paths.root).as_posix(), original_name=src.name, sha256=digest,
                      size_bytes=dest.stat().st_size, camera_id=camera_id, recording_group=recording_group, label_hint=label_hint,
                      imported_at=datetime.now(timezone.utc).isoformat(timespec="seconds"), signature=signature(dest), **meta)
    videos[video_id] = rec
    write_jsonl(paths.videos_manifest, videos.values())
    return rec, "imported"


def read_frames(path: Path, start_s: float, end_s: float, sample_fps: float) -> list[tuple[float, np.ndarray]]:
    """Frames at ``start_s, start_s + 1/sample_fps, …`` (< end_s), each the source frame nearest that time."""
    cap = cv2.VideoCapture(str(path))
    if not cap.isOpened():
        raise VideoError(f"{path}: can't open.")
    try:
        fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
        wanted = []
        t = start_s
        while t < end_s - 1e-6:
            wanted.append(t)
            t += 1.0 / sample_fps
        out: list[tuple[float, np.ndarray]] = []
        if not wanted:
            return out
        cap.set(cv2.CAP_PROP_POS_FRAMES, max(0, int(round(start_s * fps))))
        index = max(0, int(round(start_s * fps)))
        wi = 0
        while wi < len(wanted):
            ok, frame = cap.read()
            if not ok or frame is None:
                break
            ts = index / fps
            index += 1
            while wi < len(wanted) and ts + 0.5 / fps >= wanted[wi]:
                out.append((round(wanted[wi], 3), frame))
                wi += 1
        return out
    finally:
        cap.release()


def extract_frames(path: Path, out_dir: Path, every_s: float = 1.0, max_frames: int = 500) -> int:
    """Save JPEG frames every ``every_s`` seconds (annotation previews). Returns how many were written."""
    meta = probe(path)
    out_dir.mkdir(parents=True, exist_ok=True)
    n = 0
    for ts, frame in read_frames(path, 0.0, meta["duration_s"] or 1e9, 1.0 / every_s):
        if n >= max_frames:
            break
        cv2.imwrite(str(out_dir / f"{path.stem}_{ts:08.2f}s.jpg"), frame, [cv2.IMWRITE_JPEG_QUALITY, 85])
        n += 1
    return n


def export_clip(path: Path, start_s: float, end_s: float, dest: Path) -> Path:
    """Write the [start_s, end_s) part of a video to ``dest`` (MP4) for annotators."""
    cap = cv2.VideoCapture(str(path))
    if not cap.isOpened():
        raise VideoError(f"{path}: can't open.")
    try:
        fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
        w, h = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)), int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
        dest.parent.mkdir(parents=True, exist_ok=True)
        writer = cv2.VideoWriter(str(dest), cv2.VideoWriter_fourcc(*"mp4v"), fps, (w, h))
        cap.set(cv2.CAP_PROP_POS_FRAMES, int(round(start_s * fps)))
        for _ in range(max(1, int(round((end_s - start_s) * fps)))):
            ok, frame = cap.read()
            if not ok:
                break
            writer.write(frame)
        writer.release()
    finally:
        cap.release()
    return dest
