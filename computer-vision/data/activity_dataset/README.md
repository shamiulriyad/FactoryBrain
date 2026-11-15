# Activity dataset (convention only: no footage in Git)

    python scripts/activity_dataset.py init     # raw/ annotated/ processed/ train/ validation/ test/ reports/ + annotations.csv
    python scripts/activity_dataset.py split    # assign splits by recording / person group (no leakage)
    python scripts/activity_dataset.py check    # leakage, labels, missing files, class balance
    python scripts/evaluate_activity.py --split validation

* `raw/` original clips (never modified), `annotated/` finished annotation
  material, `processed/` derived pose / feature sequences, `train/`
  `validation/` `test/` split material, `reports/` evaluation reports.
* `annotations.csv`: one row per (clip, person, activity segment); columns in
  `annotations.template.csv`. Labels: STANDING, SITTING, WALKING, BENDING,
  REACHING, OTHER (skipped in scoring).
* Pseudonymous subject groups only (`P017`), never names or employee codes.
  Record only with consent. Everything here except this README and the
  template is ignored by Git and must stay on a controlled drive.

Annotation guidance, splitting rules and the evaluation workflow:
[docs/ACTIVITY_RECOGNITION.md](../../docs/ACTIVITY_RECOGNITION.md).
