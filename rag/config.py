"""All tunable settings in one place. Read from the repo-root .env, with sane defaults.

That one .env also configures the Next.js frontend and the .NET API, so every
key here except GOOGLE_API_KEY carries a PYRAG_ prefix (the API has its own
LLM_MODEL, for example).
"""

import os
from pathlib import Path

from dotenv import load_dotenv

BASE_DIR = Path(__file__).resolve().parent

load_dotenv(BASE_DIR.parent / ".env")


def _env(name: str, default=None):
    return os.getenv(f"PYRAG_{name}", default)


def _resolve(value, default: Path) -> Path:
    """Turn a path from .env into an absolute one.

    Relative values are anchored to the project folder, not the shell's current
    directory, so `python ingest.py` works no matter where it is run from.
    """
    if not value:
        return default
    path = Path(value).expanduser()
    return path if path.is_absolute() else (BASE_DIR / path)


# --- Step 1: PDF ---------------------------------------------------------
PDF_PATH = _resolve(_env("PDF_PATH"), BASE_DIR / "data" / "sample.pdf")

# --- Step 4: Chunking ----------------------------------------------------
CHUNK_SIZE = int(_env("CHUNK_SIZE", "1000"))
CHUNK_OVERLAP = int(_env("CHUNK_OVERLAP", "150"))

# --- Step 5: Embedding ---------------------------------------------------
GOOGLE_API_KEY = os.getenv("GOOGLE_API_KEY")

# Which embedding backend to use. Set this in .env - it is never hard-coded.
#   google      : Gemini embeddings (recommended). Needs GOOGLE_API_KEY with
#                 embedding quota. Best for large PDFs on a CPU-only machine.
#   huggingface : a local sentence-transformers model. No API key, no quota,
#                 no cost, but CPU-bound.
EMBEDDING_PROVIDER = _env("EMBEDDING_PROVIDER", "google").strip().lower()

KNOWN_PROVIDERS = ("google", "huggingface")

# Used only when PYRAG_EMBEDDING_MODEL is left unset in .env.
_DEFAULT_MODEL = {
    "google": "gemini-embedding-001",
    "huggingface": "sentence-transformers/all-MiniLM-L6-v2",
}
EMBEDDING_MODEL = (
    _env("EMBEDDING_MODEL", "").strip()
    or _DEFAULT_MODEL.get(EMBEDDING_PROVIDER, _DEFAULT_MODEL["google"])
)

# Gemini embedding models the kit knows about (with or without the "models/"
# prefix the API expects). Anything else is rejected early with a clear message.
KNOWN_GOOGLE_EMBEDDING_MODELS = (
    "gemini-embedding-001",
    "text-embedding-004",
    "embedding-001",
)

# None => detect the real dimension at runtime instead of hard-coding it.
EMBEDDING_DIM = int(_env("EMBEDDING_DIM") or 0) or None
EMBED_BATCH_SIZE = int(_env("EMBED_BATCH_SIZE", "90"))
# Gemini free tier allows ~100 embedded texts / minute; pause between batches so
# a large PDF does not trip a 429. Not needed for a local model.
EMBED_SLEEP = float(_env("EMBED_SLEEP", "60" if EMBEDDING_PROVIDER == "google" else "0"))

# --- Step 6: Qdrant ------------------------------------------------------
# Normal mode: connect to a Qdrant SERVER over HTTP, e.g.
# `docker run -p 6333:6333 qdrant/qdrant`.
QDRANT_URL = _env("QDRANT_URL", "http://localhost:6333")
QDRANT_API_KEY = _env("QDRANT_API_KEY") or None
_qdrant_path = _env("QDRANT_PATH")
# Optional fallback: embedded on-disk store (no server). Only the `python
# ingest.py` / `ask.py` CLIs can use it - the HTTP service (main.py) needs
# server mode. Leave PYRAG_QDRANT_PATH empty for normal mode.
QDRANT_PATH = str(_resolve(_qdrant_path, BASE_DIR / "qdrant_data")) if _qdrant_path else None
COLLECTION_NAME = _env("COLLECTION_NAME", "pdf_rag")

# --- Service authentication ----------------------------------------------
# Shared secret callers send as `X-Rag-Api-Key`. When set, every route
# except GET /health requires it; when empty the service is open to anyone who can
# reach the port (fine for a throwaway local trial, never for a deployment).
# Generate one with: python -c "import secrets; print(secrets.token_urlsafe(32))"
RAG_API_KEY = _env("API_KEY") or None

# --- Upload (POST /api/kb/{collection}/ingest) ----------------------------
# Sensible configurable ceiling - not unlimited.
MAX_UPLOAD_MB = int(_env("MAX_UPLOAD_MB", "200"))
# A PDF whose pages yield less text than this (after cleaning) is treated as
# scanned/image-based and rejected with a clear message instead of being indexed.
MIN_TEXT_CHARS = int(_env("MIN_TEXT_CHARS", "200"))

# --- Step 9: Retrieval ---------------------------------------------------
TOP_K = int(_env("TOP_K", "4"))

# --- Step 10: Gemini LLM -------------------------------------------------
LLM_MODEL = _env("LLM_MODEL", "gemini-2.5-flash")
TEMPERATURE = float(_env("TEMPERATURE", "0.2"))


def require_api_key() -> None:
    if not GOOGLE_API_KEY:
        raise RuntimeError(
            "GOOGLE_API_KEY is not set. Put your Gemini API key in the repo-root "
            ".env (https://aistudio.google.com/app/apikey)."
        )


def google_embedding_model_name() -> str:
    """The model id the Google API expects, i.e. with a 'models/' prefix."""
    name = EMBEDDING_MODEL
    return name if name.startswith("models/") else f"models/{name}"


def validate_embedding_config() -> None:
    """Fail early, with a clear message, on a bad embedding setup in .env.

    Called at the start of step 5 so a typo in PYRAG_EMBEDDING_PROVIDER / PYRAG_EMBEDDING_MODEL
    surfaces here instead of as a deep library traceback later.
    """
    if EMBEDDING_PROVIDER not in KNOWN_PROVIDERS:
        raise RuntimeError(
            f"PYRAG_EMBEDDING_PROVIDER='{EMBEDDING_PROVIDER}' is not valid. "
            f"Use one of: {', '.join(KNOWN_PROVIDERS)} (set it in .env)."
        )

    if EMBEDDING_PROVIDER == "google":
        require_api_key()
        bare = EMBEDDING_MODEL
        if bare.startswith("models/"):
            bare = bare[len("models/"):]
        if bare not in KNOWN_GOOGLE_EMBEDDING_MODELS:
            raise RuntimeError(
                f"PYRAG_EMBEDDING_MODEL='{EMBEDDING_MODEL}' is not a known Gemini "
                f"embedding model. Try one of: "
                f"{', '.join(KNOWN_GOOGLE_EMBEDDING_MODELS)}. "
                "Recommended: gemini-embedding-001."
            )
