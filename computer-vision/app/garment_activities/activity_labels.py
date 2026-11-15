"""The garment activity label registry: one place that says which classes exist.

Loaded from ``configs/garment_activity_labels.json`` (or another file), so a
factory can add its own classes without code changes. UNKNOWN is always part
of the registry and always a valid output. Label names are upper snake case,
2–40 characters, the same rule the backend applies.
"""
from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path

UNKNOWN = "UNKNOWN"
LABEL_PATTERN = re.compile(r"^[A-Z][A-Z0-9_]{1,39}$")
KINDS = ("task", "movement", "posture", "unknown")
DEFAULT_FILE = Path(__file__).resolve().parents[2] / "configs" / "garment_activity_labels.json"


class LabelError(ValueError):
    pass


@dataclass(frozen=True)
class ActivityLabelDef:
    name: str
    kind: str
    description: str = ""
    evidence: str = ""


@dataclass(frozen=True)
class LabelRegistry:
    version: str
    labels: tuple[ActivityLabelDef, ...]

    @classmethod
    def load(cls, path: Path | None = None) -> "LabelRegistry":
        p = Path(path) if path else DEFAULT_FILE
        data = json.loads(p.read_text(encoding="utf-8"))
        return cls.from_dict(data)

    @classmethod
    def from_dict(cls, data: dict) -> "LabelRegistry":
        defs = []
        seen = set()
        for item in data.get("labels", []):
            name = str(item.get("name", "")).strip().upper()
            kind = str(item.get("kind", "task")).strip().lower()
            if not LABEL_PATTERN.match(name):
                raise LabelError(f"Label '{name}': 2–40 characters, upper case letters, digits and '_', starting with a letter.")
            if kind not in KINDS:
                raise LabelError(f"Label '{name}': kind must be one of {', '.join(KINDS)}.")
            if name in seen:
                raise LabelError(f"Label '{name}' is defined twice.")
            seen.add(name)
            defs.append(ActivityLabelDef(name, kind, item.get("description", ""), item.get("evidence", "")))
        if UNKNOWN not in seen:
            defs.append(ActivityLabelDef(UNKNOWN, "unknown", "Insufficient evidence or uncertain activity."))
        if not any(d.kind != "unknown" for d in defs):
            raise LabelError("The registry needs at least one class besides UNKNOWN.")
        return cls(str(data.get("version", "1.0")), tuple(defs))

    @property
    def names(self) -> tuple[str, ...]:
        return tuple(d.name for d in self.labels)

    @property
    def trainable(self) -> tuple[str, ...]:
        """Classes a model learns. UNKNOWN is an outcome of the thresholds, not a class to learn (unless configured)."""
        return tuple(d.name for d in self.labels if d.kind != "unknown")

    def get(self, name: str) -> ActivityLabelDef | None:
        return next((d for d in self.labels if d.name == name), None)

    def normalise(self, name: str) -> str:
        """Upper-case ``name`` and check it is registered; raises :class:`LabelError` otherwise."""
        n = (name or "").strip().upper().replace("-", "_").replace(" ", "_")
        if n not in self.names:
            raise LabelError(f"'{name}' is not a registered garment activity ({', '.join(self.names)}).")
        return n

    def is_valid(self, name: str) -> bool:
        try:
            self.normalise(name)
            return True
        except LabelError:
            return False
