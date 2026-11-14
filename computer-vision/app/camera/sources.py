"""Video sources, resolved from the camera's configuration in the platform.

* RecordedVideo → the camera's uploaded video, downloaded from the backend to
  a temporary working copy (deleted when the session ends), played back at its
  own frame rate so time in events matches the recording.
* LocalWebcam   → a webcam on the machine running this service (the number in
  the camera's webcam label, else ``CV_WEBCAM_INDEX``).
* Rtsp / HttpStream → the stream URL from the backend (with its credentials,
  which are never logged or returned; descriptions are redacted).

A development override (``CV_ALLOW_SOURCE_OVERRIDE=true``) lets a start request
use a file under ``CV_DATA_DIR`` or a webcam number instead.
"""
from __future__ import annotations

import logging
import platform
import re
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

import cv2
import numpy as np

from app.core.errors import InvalidSource
from app.core.logging import redact
from app.integration.backend import CameraDirectory
from app.integration.models import CameraConfig

log = logging.getLogger(__name__)


class FrameSource(Protocol):
    kind: str             # file | webcam | stream
    description: str      # safe to log and return (no credentials)
    is_live: bool

    def open(self) -> None: ...
    def read(self) -> tuple[bool, np.ndarray | None]: ...
    def release(self) -> None: ...


class OpenCvSource:
    """OpenCV capture of a file, a webcam index or a stream URL."""

    def __init__(self, target: str | int, kind: str, description: str, loop: bool = False, realtime: bool = True,
                 cleanup: Path | None = None) -> None:
        self._target = target
        self.kind = kind
        self.description = description
        self.is_live = kind != "file"
        self._loop = loop
        self._realtime = realtime and kind == "file"
        self._cleanup = cleanup
        self._cap: cv2.VideoCapture | None = None
        self._fps = 25.0
        self._started = 0.0
        self._index = 0

    def open(self) -> None:
        if self.kind == "webcam":
            backend = cv2.CAP_DSHOW if platform.system() == "Windows" else cv2.CAP_ANY
            cap = cv2.VideoCapture(int(self._target), backend)
        elif self.kind == "stream":
            cap = cv2.VideoCapture(str(self._target), cv2.CAP_FFMPEG)
        else:
            cap = cv2.VideoCapture(str(self._target))
        if not cap.isOpened():
            cap.release()
            raise InvalidSource(f"Can't open {self.description}.")
        fps = cap.get(cv2.CAP_PROP_FPS)
        self._fps = fps if fps and 1 <= fps <= 120 else 25.0
        self._cap, self._started, self._index = cap, time.monotonic(), 0

    def read(self) -> tuple[bool, np.ndarray | None]:
        if self._cap is None:
            return False, None
        ok, frame = self._cap.read()
        if not ok and self.kind == "file" and self._loop:
            self._cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
            self._started, self._index = time.monotonic(), 0
            ok, frame = self._cap.read()
        if ok and self._realtime:
            # Play a recording at its own speed: frame n is due n / fps seconds after the start.
            self._index += 1
            delay = self._started + self._index / self._fps - time.monotonic()
            if delay > 0:
                time.sleep(min(delay, 1.0))
        return ok, frame if ok else None

    def release(self) -> None:
        if self._cap is not None:
            self._cap.release()
            self._cap = None
        if self._cleanup is not None:
            try:
                self._cleanup.unlink(missing_ok=True)
            except OSError:  # pragma: no cover - file in use on Windows
                log.warning("Couldn't delete the working copy %s.", self._cleanup.name)


@dataclass(frozen=True)
class SourceOverride:
    """Development only: ``file`` (a path under CV_DATA_DIR) or ``webcam`` (an index)."""

    type: str
    path: str | None = None
    index: int | None = None


_EXT = {"video/mp4": ".mp4", "video/webm": ".webm"}


def resolve_source(cfg: CameraConfig, directory: CameraDirectory, data_dir: Path, webcam_index: int, loop: bool,
                   override: SourceOverride | None = None, allow_override: bool = False) -> FrameSource:
    if override is not None:
        if not allow_override:
            raise InvalidSource("Source overrides are disabled (set CV_ALLOW_SOURCE_OVERRIDE=true for development).", "override_disabled")
        if override.type == "webcam":
            return OpenCvSource(override.index or 0, "webcam", f"local webcam {override.index or 0} (override)")
        if override.type == "file" and override.path:
            root = data_dir.resolve()
            path = (root / override.path).resolve() if not Path(override.path).is_absolute() else Path(override.path).resolve()
            if root not in path.parents:
                raise InvalidSource("Override files must be inside the data folder.", "override_outside_data_dir")
            if not path.is_file():
                raise InvalidSource(f"No such video file: {path.name}.")
            return OpenCvSource(str(path), "file", f"video file {path.name} (override)", loop=loop)
        raise InvalidSource("Override type must be 'file' (with path) or 'webcam' (with index).")

    st = cfg.source.source_type
    if st == "RecordedVideo":
        if not cfg.source.has_video:
            raise InvalidSource(f"{cfg.code} has no recorded video uploaded.")
        dest = data_dir / "cache" / f"{cfg.id}{_EXT.get(cfg.source.video_content_type or '', '.mp4')}"
        directory.download_video(cfg.id, dest)
        return OpenCvSource(str(dest), "file", f"recorded video {cfg.source.video_file_name or dest.name}", loop=loop, cleanup=dest)
    if st == "LocalWebcam":
        digits = re.search(r"\d+", cfg.source.webcam_label or "")
        index = int(digits.group()) if digits else webcam_index
        return OpenCvSource(index, "webcam", f"local webcam {index}")
    if st in ("Rtsp", "HttpStream"):
        if not cfg.source.stream_url:
            raise InvalidSource(f"{cfg.code} has no stream URL.")
        return OpenCvSource(cfg.source.stream_url, "stream", f"{st} stream {redact(cfg.source.stream_url)}")
    raise InvalidSource(f"{cfg.code} has no video source configured (source type {st}).", "no_source")
