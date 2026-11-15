# Trained garment activity models

Empty on purpose: no garment activity model has been trained, because no
labelled garment-factory footage exists yet. Accuracy is **PENDING**.

`python scripts/garment.py train` writes `<model name>/<version>/` here with
`model.pt`, `metadata.json` (labels, dataset version, input and training
configuration, validation metrics, training date, evaluation status) and
`history.csv`. A model is used live only after
`python scripts/garment.py evaluate <dir>` has scored it on the held-out test
split (`evaluation.status` = EVALUATED). Weights are never committed.
See docs/GARMENT_ACTIVITIES.md.
