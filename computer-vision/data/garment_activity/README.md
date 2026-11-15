# Garment activity dataset (convention only: no footage in Git)

    python scripts/garment.py init       # raw/<activity>/ annotated/ processed/ train/ validation/ test/ reports/ + annotations.csv

| Path | Contents |
|---|---|
| `raw/<activity or unsorted>/` | Imported videos (`<video id>.<ext>`), never modified. The folder is a hint, never a label |
| `videos.jsonl` | Video metadata: id, SHA-256, fps, duration, size, codec, camera, recording group, near-duplicate signature |
| `annotations.csv` or `.jsonl` | Time-segment annotations (templates: `annotations.template.*`) |
| `annotated/` | `cut` windows as MP4 for annotators, `<video>_template.csv`, preview frames |
| `processed/clips.jsonl`, `processed/features/` | Labelled fixed-length clips and their extracted features (`.npz`) |
| `splits.json`, `train/` `validation/` `test/` | The split (seed, groups, dataset version) and each split's clip list |
| `reports/` | Class distribution and evaluation reports |

Record only with consent; use pseudonymous `subject_group` ids (`P017`),
never names or employee codes. Everything here except this README and the
templates is ignored by Git and must stay on a controlled drive. Annotation
guidance per activity and the full workflow: docs/GARMENT_ACTIVITIES.md.
