"""Activity state per tracked person: current activity, smoothing, transitions and history.

Rules (all configurable):
* a different known activity must be predicted continuously for
  ``min_state_seconds`` (and at least twice) before the state changes, so a
  single odd window never flips the state;
* UNKNOWN must persist for ``unknown_after_seconds`` before it replaces a known
  state: a short occlusion keeps the last activity;
* a confirmed state is re-reported (CLASSIFIED) at most every
  ``report_seconds`` while it stays the same;
* the confidence of a stable state is smoothed (exponential average);
* a track that disappears briefly keeps its state; one gone for
  ``reset_after_seconds`` (or reported lost) is forgotten.

States are keyed by ``(camera_id, track_id)``: tracks of different cameras are
never merged, and nothing here links a track to a worker.
"""
from __future__ import annotations

from dataclasses import dataclass, field

from app.activity_recognition.schemas import ActivityLabel, ActivityPrediction, ActivityTransition, ActivityUpdate, TrackActivityState
from app.detection.types import BBox

_EMA = 0.4


@dataclass
class ActivityStateManager:
    min_state_seconds: float = 1.5
    unknown_after_seconds: float = 3.0
    report_seconds: float = 15.0
    reset_after_seconds: float = 3.0
    history_size: int = 50
    states: dict[tuple[str, int], TrackActivityState] = field(default_factory=dict)
    transitions: int = 0

    def update(self, camera_id: str, track_id: int, pred: ActivityPrediction, now: float, bbox: BBox | None = None) -> list[ActivityUpdate]:
        key = (camera_id, track_id)
        st = self.states.get(key)
        if st is None:
            st = self.states[key] = TrackActivityState(camera_id, track_id)
        st.last_seen = now
        st.last_prediction = pred
        label = pred.label

        if label == st.current:
            st.pending, st.pending_count = None, 0
            st.confidence = round((1 - _EMA) * st.confidence + _EMA * pred.confidence, 3) if label != ActivityLabel.UNKNOWN else pred.confidence
            if label != ActivityLabel.UNKNOWN and now - st.last_report >= self.report_seconds:
                st.last_report = now
                return [self._update("CLASSIFIED", st, pred, bbox)]
            return []

        if label == st.pending:
            st.pending_count += 1
        else:
            st.pending, st.pending_since, st.pending_count = label, now, 1
        hold = self.unknown_after_seconds if label == ActivityLabel.UNKNOWN else self.min_state_seconds
        if now - st.pending_since < hold or st.pending_count < 2:
            return []
        return [self._change(st, label, pred, now, bbox)]

    def _change(self, st: TrackActivityState, label: ActivityLabel, pred: ActivityPrediction, now: float, bbox: BBox | None) -> ActivityUpdate:
        previous = st.current
        previous_duration = (st.pending_since - st.since) if previous is not None else None
        st.current, st.since, st.confidence = label, st.pending_since, pred.confidence
        st.pending, st.pending_count = None, 0
        st.last_report = now
        st.history.append(ActivityTransition(st.since, previous, label, pred.confidence))
        if len(st.history) > self.history_size:
            del st.history[: len(st.history) - self.history_size]
        self.transitions += 1
        kind = "UNCERTAIN" if label == ActivityLabel.UNKNOWN else "STATE_CHANGED"
        return ActivityUpdate(kind, st.camera_id, st.track_id, label, previous, pred.confidence, pred, st.since,
                              None if previous_duration is None else max(0.0, previous_duration), bbox)

    @staticmethod
    def _update(kind: str, st: TrackActivityState, pred: ActivityPrediction, bbox: BBox | None) -> ActivityUpdate:
        return ActivityUpdate(kind, st.camera_id, st.track_id, st.current, None, st.confidence, pred, st.since, None, bbox)  # type: ignore[arg-type]

    def get(self, camera_id: str, track_id: int) -> TrackActivityState | None:
        return self.states.get((camera_id, track_id))

    def end(self, camera_id: str, track_id: int) -> TrackActivityState | None:
        """The track is gone (lost or stopped): forget its state; returns it for the record."""
        return self.states.pop((camera_id, track_id), None)

    def expire(self, now: float) -> list[TrackActivityState]:
        gone = [k for k, s in self.states.items() if now - s.last_seen >= self.reset_after_seconds]
        return [self.states.pop(k) for k in gone]

    def clear(self) -> None:
        self.states.clear()
