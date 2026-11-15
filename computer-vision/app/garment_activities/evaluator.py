"""Evaluates a trained garment model on the held-out test split.

Reports, after the UNKNOWN decision rules (what the live system would say)
and for the raw arg-max (what the network alone says):

* accuracy (UNKNOWN counts as wrong), accuracy over decided predictions,
  precision, recall and F1 per class, macro and weighted F1, the confusion
  matrix (with an UNKNOWN column) and the most frequent class-wise errors;
* the UNKNOWN rate, the low-confidence rate and UNKNOWN reasons;
* per-camera results where the data allows (≥ ``min_camera_clips`` clips);
* how often the prediction matches *any* label of a multi-label clip;
* model latency per clip, feature-extraction latency (when recorded) and
  processing throughput.

Without a labelled test split it returns status PENDING and never a score.
"""
from __future__ import annotations

import json
import time
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

from app.activity_recognition.evaluation import report
from app.garment_activities.activity_labels import UNKNOWN
from app.garment_activities.dataset_loader import FeatureSet
from app.garment_activities.inference import GarmentClassifier
from app.garment_activities.model_factory import METADATA_FILE

PENDING_NOTE = ("Garment-specific accuracy is PENDING: there is no labelled held-out test set of garment-factory footage. "
                "No accuracy is reported without one.")


def evaluate(clf: GarmentClassifier, test: FeatureSet, qualities: list[dict] | None = None, min_camera_clips: int = 20,
             split: str = "test", data_source: str = "factory-labelled") -> dict:
    if len(test) == 0:
        return {"status": "PENDING", "note": PENDING_NOTE, "split": split}
    qualities = qualities or [{} for _ in test.clips]
    q = []
    for qual, row in zip(qualities, test.mask):
        person = float(row.mean()) if len(row) else 0.0
        q.append({"personRatio": qual.get("personRatio", person), "poseRatio": qual.get("poseRatio", person)})
    windows = [(c.start_s, c.end_s) for c in test.clips]
    started = time.perf_counter()
    probs = clf.probabilities(test.x, test.mask)
    model_ms = (time.perf_counter() - started) * 1000 / len(test)
    from app.garment_activities.inference import decide

    preds = [decide(p, clf.labels, clf.decision, qq, w, clf.uses_pose) for p, qq, w in zip(probs, q, windows)]
    truth = [c.primary_label for c in test.clips]
    eval_labels = [lab for lab in clf.labels if lab != UNKNOWN]
    decided = [p.label for p in preds]
    raw = [clf.labels[int(i)] for i in probs.argmax(1)]
    errors = Counter((t, p) for t, p in zip(truth, decided) if t != p)
    extraction = [qq.get("extractionMs") for qq in qualities if qq.get("extractionMs") is not None]
    frames = test.x.shape[1]
    out = {
        "status": "EVALUATED", "split": split, "dataSource": data_source, "evaluatedAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "model": {"name": clf.name, "version": clf.version, "datasetVersion": clf.dataset_version}, "clips": len(test),
        "decision": {"minConfidence": clf.decision.min_confidence, "minMargin": clf.decision.min_margin, "lowConfidence": clf.decision.low_confidence},
        "afterUncertainty": report(truth, decided, eval_labels),
        "rawArgmax": report(truth, raw, eval_labels),
        "unknownRate": round(sum(p.label == UNKNOWN for p in preds) / len(preds), 4),
        "lowConfidenceRate": round(sum(p.confidence < clf.decision.low_confidence for p in preds) / len(preds), 4),
        "unknownReasons": dict(Counter(p.reason for p in preds if p.reason)),
        "classErrors": [{"truth": t, "predicted": p, "clips": n} for (t, p), n in errors.most_common(15)],
        "matchesAnyLabel": round(sum(p.label in c.labels for p, c in zip(preds, test.clips)) / len(preds), 4),
        "latency": {"modelMsPerClip": round(model_ms, 3), "featureExtractionMsPerClip": round(float(np.mean(extraction)), 1) if extraction else None,
                    "clipsPerSecond": round(1000 / model_ms, 1) if model_ms else None,
                    "framesPerSecond": round(1000 * frames / model_ms, 1) if model_ms else None},
        "perCamera": {},
    }
    by_cam: dict[str, list[int]] = {}
    for i, c in enumerate(test.clips):
        by_cam.setdefault(c.camera_id or "unknown", []).append(i)
    for cam, idx in sorted(by_cam.items()):
        if len(idx) < min_camera_clips:
            out["perCamera"][cam] = {"clips": len(idx), "note": f"fewer than {min_camera_clips} clips: not scored"}
            continue
        r = report([truth[i] for i in idx], [decided[i] for i in idx], eval_labels)
        out["perCamera"][cam] = {"clips": len(idx), "accuracy": r["accuracy"], "macroF1": r["macro"]["f1"], "unknownRate": r["unknownRate"]}
    return out


def record(model_dir: Path, result: dict) -> None:
    """Store the evaluation summary in the model's metadata (status EVALUATED makes it eligible for live use)."""
    meta_path = Path(model_dir) / METADATA_FILE
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    if result.get("status") == "EVALUATED":
        a = result["afterUncertainty"]
        meta["evaluation"] = {"status": "EVALUATED", "split": result["split"], "evaluatedAt": result["evaluatedAt"], "clips": result["clips"],
                              "dataSource": result.get("dataSource"), "accuracy": a["accuracy"], "macroF1": a["macro"]["f1"],
                              "weightedF1": a["weighted"]["f1"], "unknownRate": result["unknownRate"], "datasetVersion": meta.get("datasetVersion")}
    else:
        meta["evaluation"] = {"status": "PENDING", "note": result.get("note", PENDING_NOTE)}
    meta_path.write_text(json.dumps(meta, indent=2), encoding="utf-8")


def to_markdown(result: dict) -> str:
    if result.get("status") != "EVALUATED":
        return f"# Garment activity evaluation\n\n**PENDING.** {result.get('note', PENDING_NOTE)}\n"
    a, m = result["afterUncertainty"], result["model"]
    lines = [f"# Garment activity evaluation: {m['name']} {m['version']}", "",
             f"Split **{result['split']}** ({result['clips']} clips, data source: {result['dataSource']}), dataset {m['datasetVersion']}, "
             f"evaluated {result['evaluatedAt']}.", "",
             f"| Metric | After UNKNOWN rules | Raw arg-max |", "|---|---|---|",
             f"| Accuracy | {a['accuracy']} | {result['rawArgmax']['accuracy']} |",
             f"| Macro F1 | {a['macro']['f1']} | {result['rawArgmax']['macro']['f1']} |",
             f"| Weighted F1 | {a['weighted']['f1']} | {result['rawArgmax']['weighted']['f1']} |",
             f"| UNKNOWN rate | {result['unknownRate']} | — |", f"| Low-confidence rate | {result['lowConfidenceRate']} | — |", "",
             "| Class | Precision | Recall | F1 | Support | UNKNOWN |", "|---|---|---|---|---|---|"]
    for lab, c in a["perClass"].items():
        lines.append(f"| {lab} | {c['precision']} | {c['recall']} | {c['f1']} | {c['support']} | {c['unknown']} |")
    lines += ["", "Most frequent errors: " + (", ".join(f"{e['truth']}→{e['predicted']} ({e['clips']})" for e in result["classErrors"]) or "none"),
              "", f"Latency: {result['latency']}", "", f"Per camera: {json.dumps(result['perCamera'])}"]
    return "\n".join(lines) + "\n"
