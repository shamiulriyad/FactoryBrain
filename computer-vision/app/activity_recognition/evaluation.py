"""Evaluation metrics and dataset checks for activity recognition (pure functions, no model).

* :func:`report` — precision, recall, F1 per class, macro / weighted
  averages, the confusion matrix, and the UNKNOWN prediction rate. UNKNOWN is
  an abstention, not a class: it counts against recall of the true class and
  is reported separately; precision is over the predictions actually made.
* :func:`class_balance` — label counts and the imbalance ratio.
* :func:`group_split` / :func:`check_leakage` — split clips into train /
  validation / test by *recording group* (same recording, same session, same
  person), so near-identical frames never sit on both sides of a split.
"""
from __future__ import annotations

import hashlib
from collections import Counter
from dataclasses import dataclass

UNKNOWN = "UNKNOWN"


def report(y_true: list[str], y_pred: list[str], labels: list[str]) -> dict:
    if len(y_true) != len(y_pred):
        raise ValueError("y_true and y_pred differ in length")
    cols = labels + [UNKNOWN]
    matrix = {t: {p: 0 for p in cols} for t in labels}
    for t, p in zip(y_true, y_pred):
        if t not in matrix:
            continue                                   # ground truth outside the evaluated classes (e.g. OTHER) is skipped
        matrix[t][p if p in cols else UNKNOWN] += 1
    per_class = {}
    for lab in labels:
        tp = matrix[lab][lab]
        support = sum(matrix[lab].values())
        predicted = sum(matrix[t][lab] for t in labels)
        precision = tp / predicted if predicted else None
        recall = tp / support if support else None
        f1 = (2 * precision * recall / (precision + recall)) if precision and recall else (0.0 if support and predicted else None)
        per_class[lab] = {"precision": _r(precision), "recall": _r(recall), "f1": _r(f1), "support": support,
                          "unknown": matrix[lab][UNKNOWN]}
    total = sum(sum(r.values()) for r in matrix.values())
    unknown = sum(matrix[t][UNKNOWN] for t in labels)
    decided = total - unknown
    correct = sum(matrix[t][t] for t in labels)
    with_support = [lab for lab in labels if per_class[lab]["support"]]
    macro = {k: _r(_avg([per_class[lab][k] for lab in with_support])) for k in ("precision", "recall", "f1")}
    weighted = {k: _r(_wavg([(per_class[lab][k], per_class[lab]["support"]) for lab in with_support])) for k in ("precision", "recall", "f1")}
    return {
        "samples": total, "unknownPredictions": unknown, "unknownRate": _r(unknown / total if total else None),
        "accuracy": _r(correct / total if total else None),                         # UNKNOWN counts as wrong
        "accuracyWhenDecided": _r(correct / decided if decided else None),         # over non-UNKNOWN predictions only
        "perClass": per_class, "macro": macro, "weighted": weighted, "confusionMatrix": matrix, "labels": cols,
    }


def class_balance(labels: list[str]) -> dict:
    c = Counter(labels)
    if not c:
        return {"counts": {}, "imbalanceRatio": None, "minority": None, "majority": None}
    (major, nmax), (minor, nmin) = c.most_common()[0], c.most_common()[-1]
    return {"counts": dict(c), "imbalanceRatio": round(nmax / nmin, 2), "majority": major, "minority": minor,
            "shares": {k: round(v / sum(c.values()), 3) for k, v in c.items()}}


@dataclass(frozen=True)
class SplitRatios:
    train: float = 0.7
    validation: float = 0.15
    test: float = 0.15


def group_split(groups: list[str], ratios: SplitRatios = SplitRatios(), seed: str = "bunonbrain-activity-v1") -> dict[str, str]:
    """Assign each group (not each clip) to a split, deterministically from a hash of the group and the seed."""
    out = {}
    for g in sorted(set(groups)):
        h = int(hashlib.sha256(f"{seed}:{g}".encode()).hexdigest()[:8], 16) / 0xFFFFFFFF
        out[g] = "train" if h < ratios.train else "validation" if h < ratios.train + ratios.validation else "test"
    return out


def check_leakage(rows: list[dict], keys: tuple[str, ...] = ("recording_id", "subject_group")) -> list[str]:
    """Problems found: any recording or subject group present in more than one split."""
    problems = []
    for key in keys:
        seen: dict[str, set[str]] = {}
        for r in rows:
            v = (r.get(key) or "").strip()
            if v:
                seen.setdefault(v, set()).add(r.get("split", ""))
        for v, splits in sorted(seen.items()):
            if len(splits) > 1:
                problems.append(f"{key} '{v}' appears in {', '.join(sorted(splits))}")
    return problems


def _avg(vals: list[float | None]) -> float | None:
    v = [x for x in vals if x is not None]
    return sum(v) / len(v) if v else None


def _wavg(pairs: list[tuple[float | None, int]]) -> float | None:
    v = [(x, w) for x, w in pairs if x is not None]
    tw = sum(w for _, w in v)
    return sum(x * w for x, w in v) / tw if tw else None


def _r(x: float | None) -> float | None:
    return None if x is None else round(x, 4)
