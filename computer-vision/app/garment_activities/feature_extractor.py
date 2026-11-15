"""Per-frame features of one tracked person, identical for training clips and live inference.

Each sampled frame of a person becomes one vector of four parts:

* ``appearance`` — a frozen ImageNet CNN (MobileNetV3-Small by default)
  embedding of the person crop (box enlarged by ``crop_padding`` so hands,
  tools and fabric around the person are included): what the person handles;
* ``pose`` — the existing pose features (angles, proportions), their
  presence mask, box-relative keypoints and keypoint visibility: how the body
  is arranged;
* ``motion`` — feet-point velocity and box-scale change (body heights per
  second) and both wrists' velocity relative to the body: how the body and
  the hands move (sewing, cutting and folding differ mostly in hand motion);
* ``presence`` — 1 when the person was seen in that frame.

Missing parts are zeros with their presence flags at 0, never guesses. The
layout is versioned (``FEATURE_VERSION``) and stored with every model.
"""
from __future__ import annotations

import threading
import time
from dataclasses import dataclass
from typing import Protocol

import cv2
import numpy as np

from app.activity_recognition.feature_extractor import extract_features
from app.activity_recognition.pose_preprocessor import InvalidPose, normalize_pose
from app.activity_recognition.schemas import FEATURE_NAMES, KP, NUM_KEYPOINTS, PoseDetection
from app.detection.types import BBox
from app.garment_activities.config import FEATURE_VERSION, FeatureConfig

POSE_DIM = len(FEATURE_NAMES) * 2 + NUM_KEYPOINTS * 3      # features + mask + keypoints xy + visibility = 81
MOTION_DIM = 7
BACKBONE_DIMS = {"mobilenet_v3_small": 576, "resnet18": 512, "none": 0}
_MEAN = np.array([0.485, 0.456, 0.406], np.float32)
_STD = np.array([0.229, 0.224, 0.225], np.float32)


def layout(backbone: str) -> dict[str, tuple[int, int]]:
    """Slices of each part in the frame vector."""
    if backbone not in BACKBONE_DIMS:
        raise ValueError(f"Unknown backbone '{backbone}' ({', '.join(BACKBONE_DIMS)}).")
    a = BACKBONE_DIMS[backbone]
    return {"appearance": (0, a), "pose": (a, a + POSE_DIM), "motion": (a + POSE_DIM, a + POSE_DIM + MOTION_DIM),
            "presence": (a + POSE_DIM + MOTION_DIM, a + POSE_DIM + MOTION_DIM + 1)}


def feature_dim(backbone: str) -> int:
    return layout(backbone)["presence"][1]


# ─── Appearance ───────────────────────────────────────────────────────────

class Embedder(Protocol):
    name: str
    dim: int

    def embed(self, crops: list[np.ndarray]) -> np.ndarray: ...


class NullEmbedder:
    """No appearance features (backbone "none")."""

    name, dim = "none", 0

    def embed(self, crops: list[np.ndarray]) -> np.ndarray:
        return np.zeros((len(crops), 0), np.float32)


class CnnEmbedder:
    """A frozen torchvision ImageNet CNN, global-average-pooled. Loaded once, shared, inference serialised."""

    def __init__(self, backbone: str = "mobilenet_v3_small", device: str = "cpu") -> None:
        import torch
        from torchvision import models

        if backbone == "mobilenet_v3_small":
            net = models.mobilenet_v3_small(weights=models.MobileNet_V3_Small_Weights.IMAGENET1K_V1)
            body = torch.nn.Sequential(net.features, net.avgpool, torch.nn.Flatten())
        elif backbone == "resnet18":
            net = models.resnet18(weights=models.ResNet18_Weights.IMAGENET1K_V1)
            body = torch.nn.Sequential(*list(net.children())[:-1], torch.nn.Flatten())
        else:
            raise ValueError(f"Unknown CNN backbone '{backbone}'.")
        self._torch = torch
        self._net = body.eval().to(device)
        for p in self._net.parameters():
            p.requires_grad_(False)
        self._device = device
        self._lock = threading.Lock()
        self.name, self.dim = backbone, BACKBONE_DIMS[backbone]
        self.calls, self.avg_ms = 0, 0.0

    def embed(self, crops: list[np.ndarray]) -> np.ndarray:
        if not crops:
            return np.zeros((0, self.dim), np.float32)
        x = np.stack([(c.astype(np.float32) / 255.0 - _MEAN) / _STD for c in crops]).transpose(0, 3, 1, 2)
        started = time.perf_counter()
        with self._lock, self._torch.no_grad():
            out = self._net(self._torch.from_numpy(np.ascontiguousarray(x)).to(self._device)).cpu().numpy()
        self.calls += 1
        self.avg_ms = round(self.avg_ms + ((time.perf_counter() - started) * 1000 - self.avg_ms) / min(self.calls, 50), 2)
        return out.astype(np.float32)


def make_embedder(cfg: FeatureConfig, device: str = "cpu") -> Embedder:
    return NullEmbedder() if cfg.backbone == "none" else CnnEmbedder(cfg.backbone, device)


def crop_person(frame: np.ndarray, box: BBox, padding: float, size: int) -> np.ndarray:
    """A square RGB crop around the box (enlarged by ``padding``), resized to ``size`` × ``size``; outside the frame is black."""
    h, w = frame.shape[:2]
    cx, cy = (box.x1 + box.x2) / 2, (box.y1 + box.y2) / 2
    side = max(box.width, box.height) * (1 + 2 * padding)
    x1, y1 = int(round(cx - side / 2)), int(round(cy - side / 2))
    x2, y2 = x1 + int(round(side)), y1 + int(round(side))
    canvas = np.zeros((max(1, y2 - y1), max(1, x2 - x1), 3), np.uint8)
    sx1, sy1, sx2, sy2 = max(0, x1), max(0, y1), min(w, x2), min(h, y2)
    if sx2 > sx1 and sy2 > sy1:
        canvas[sy1 - y1:sy2 - y1, sx1 - x1:sx2 - x1] = frame[sy1:sy2, sx1:sx2]
    return cv2.cvtColor(cv2.resize(canvas, (size, size), interpolation=cv2.INTER_AREA), cv2.COLOR_BGR2RGB)


# ─── Pose and motion ──────────────────────────────────────────────────────

@dataclass
class FrameDescription:
    """One sampled frame of one person, before the appearance embedding is added."""

    t: float
    present: bool
    has_pose: bool
    pose: np.ndarray                  # (POSE_DIM,)
    crop: np.ndarray | None
    anchor: tuple[float, float] | None = None
    height: float | None = None
    wrists: tuple[np.ndarray | None, np.ndarray | None] = (None, None)
    motion: np.ndarray | None = None  # filled by add_motion
    embedding: np.ndarray | None = None   # appearance, when already computed (live: once per observation)


def describe(frame: np.ndarray | None, t: float, box: BBox | None, pose: PoseDetection | None, cfg: FeatureConfig,
             need_crop: bool = True) -> FrameDescription:
    if frame is None or box is None:
        return FrameDescription(t, False, False, np.zeros(POSE_DIM, np.float32), None)
    vec = np.zeros(POSE_DIM, np.float32)
    has_pose = False
    wrists: tuple = (None, None)
    if pose is not None:
        try:
            norm = normalize_pose(pose, box, cfg.keypoint_confidence)
            if norm.quality.usable:
                f = extract_features(norm).as_vector()
                n = len(FEATURE_NAMES)
                vec[:n] = np.nan_to_num(f, nan=0.0)
                vec[n:2 * n] = (~np.isnan(f)).astype(np.float32)
                rel = np.nan_to_num(norm.box_relative(), nan=0.0).flatten()
                vec[2 * n:2 * n + NUM_KEYPOINTS * 2] = rel
                vec[2 * n + NUM_KEYPOINTS * 2:] = norm.visible.astype(np.float32)
                has_pose = True
                wrists = (norm.point("left_wrist"), norm.point("right_wrist"))
        except InvalidPose:
            pass
    crop = crop_person(frame, box, cfg.crop_padding, cfg.input_size) if need_crop else None
    return FrameDescription(t, True, has_pose, vec, crop, box.bottom_centre(), max(1.0, box.height), wrists)


def add_motion(frames: list[FrameDescription]) -> None:
    """Velocities between consecutive frames in which the person was present (0 where unknown)."""
    prev: FrameDescription | None = None
    for d in frames:
        m = np.zeros(MOTION_DIM, np.float32)
        if d.present and prev is not None and prev.present and d.t > prev.t:
            dt = d.t - prev.t
            h = (d.height + prev.height) / 2
            m[0] = (d.anchor[0] - prev.anchor[0]) / h / dt
            m[1] = (d.anchor[1] - prev.anchor[1]) / h / dt
            m[2] = (d.height / prev.height - 1.0) / dt
            for i, (now, before) in enumerate(zip(d.wrists, prev.wrists)):
                if now is not None and before is not None:
                    m[3 + 2 * i: 5 + 2 * i] = (now - before) / dt
        d.motion = np.clip(m, -10, 10)
        if d.present:
            prev = d


def assemble(frames: list[FrameDescription], embeddings: np.ndarray, backbone: str) -> np.ndarray:
    """(T, D) matrix of a sequence; ``embeddings`` has one row per frame with a crop, in order."""
    lay = layout(backbone)
    out = np.zeros((len(frames), lay["presence"][1]), np.float32)
    k = 0
    for i, d in enumerate(frames):
        if d.present:
            a0, a1 = lay["appearance"]
            if a1 > a0 and d.embedding is not None:
                out[i, a0:a1] = d.embedding
            elif a1 > a0 and d.crop is not None:
                out[i, a0:a1] = embeddings[k]
                k += 1
            out[i, slice(*lay["pose"])] = d.pose
            out[i, slice(*lay["motion"])] = d.motion if d.motion is not None else 0.0
            out[i, lay["presence"][0]] = 1.0
    return out


def sequence(frames: list[FrameDescription], embedder: Embedder) -> np.ndarray:
    add_motion(frames)
    crops = [d.crop for d in frames if d.present and d.crop is not None and d.embedding is None] if embedder.dim else []
    emb = embedder.embed(crops) if crops else np.zeros((0, embedder.dim), np.float32)
    return assemble(frames, emb, embedder.name)


def resample(frames: list[FrameDescription], start: float, n: int, step: float) -> list[FrameDescription]:
    """Exactly ``n`` frames at start, start+step, … taking the nearest description within half a step (absent otherwise)."""
    out = []
    for i in range(n):
        t = start + i * step
        best = min(frames, key=lambda d: abs(d.t - t), default=None)
        if best is not None and abs(best.t - t) <= step / 2 + 1e-6:
            out.append(best)
        else:
            out.append(FrameDescription(t, False, False, np.zeros(POSE_DIM, np.float32), None))
    return out


def quality(frames: list[FrameDescription]) -> dict:
    n = max(1, len(frames))
    return {"frames": len(frames), "personRatio": round(sum(d.present for d in frames) / n, 3),
            "poseRatio": round(sum(d.has_pose for d in frames) / n, 3), "featureVersion": FEATURE_VERSION}


__all__ = ["KP", "POSE_DIM", "MOTION_DIM", "layout", "feature_dim", "describe", "sequence", "resample", "quality", "crop_person",
           "CnnEmbedder", "NullEmbedder", "make_embedder", "FrameDescription"]


# ─── Clips from recorded video (dataset preparation) ──────────────────────

class ClipFeatureExtractor:
    """Features of the annotated person in a clip, with the service's own detector, ByteTrack and pose model.

    The subject is the track overlapping the clip's ``subject_roi`` most in
    the first frames it appears in (or, without an ROI, the track seen in the
    most frames). Frames where that track is missing stay absent.
    """

    def __init__(self, detector, pose, embedder: Embedder, cfg: FeatureConfig, sample_fps: float, lead_in_s: float = 1.0) -> None:  # noqa: ANN001
        self.detector, self.pose, self.embedder, self.cfg = detector, pose, embedder, cfg
        self.sample_fps, self.lead_in_s = sample_fps, lead_in_s

    def extract(self, video_path, start_s: float, end_s: float, roi: dict | None = None) -> tuple[np.ndarray, dict]:  # noqa: ANN001
        from app.activity_recognition.pose_estimator import associate_poses, iou
        from app.garment_activities.video_preprocessor import read_frames
        from app.tracking.bytetrack import ByteTrackTracker

        tracker = ByteTrackTracker(frame_rate=self.sample_fps, track_buffer_seconds=2.0)
        lead = max(0.0, start_s - self.lead_in_s)          # a short lead-in lets ByteTrack settle before the clip starts
        per_frame: list[tuple[float, np.ndarray, dict]] = []
        for t, frame in read_frames(video_path, lead, end_s, self.sample_fps):
            h, w = frame.shape[:2]
            tracks = tracker.update(self.detector.detect(frame, "dataset", t), w, h)
            if t + 1e-6 < start_s:
                continue
            poses = associate_poses(tracks, self.pose.estimate(frame), 0.3) if tracks else {}
            per_frame.append((t, frame, {tr.track_id: (tr.bbox, poses.get(tr.track_id)) for tr in tracks}))
        n = max(2, int(round((end_s - start_s) * self.sample_fps)))
        subject = self._subject(per_frame, roi, iou)
        frames = []
        for t, frame, by_track in per_frame:
            box, pose = by_track.get(subject, (None, None)) if subject is not None else (None, None)
            frames.append(describe(frame if box is not None else None, t, box, pose, self.cfg, need_crop=self.embedder.dim > 0))
        frames = resample(frames, start_s, n, 1.0 / self.sample_fps)
        q = quality(frames) | {"subjectTrack": subject, "subjectFound": subject is not None, "roi": roi is not None}
        return sequence(frames, self.embedder), q

    @staticmethod
    def _subject(per_frame, roi, iou):  # noqa: ANN001, ANN205
        counts: dict[int, int] = {}
        for _, _, by_track in per_frame:
            for tid in by_track:
                counts[tid] = counts.get(tid, 0) + 1
        if not counts:
            return None
        if roi is None:
            return max(counts, key=lambda k: (counts[k], -k))
        best, best_iou = None, 0.0
        for _, frame, by_track in per_frame[:3]:
            h, w = frame.shape[:2]
            r = BBox(float(roi["x"]) * w, float(roi["y"]) * h, (float(roi["x"]) + float(roi["w"])) * w, (float(roi["y"]) + float(roi["h"])) * h)
            for tid, (box, _) in by_track.items():
                v = iou(box, r)
                if v > best_iou:
                    best, best_iou = tid, v
        return best if best_iou >= 0.2 else None
