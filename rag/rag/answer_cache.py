"""Answer cache - reuse the answer to a question that was already asked.

Asking the same question again should not cost another LLM call. The question's
embedding (computed anyway for retrieval) is compared with earlier questions
stored in a side collection `<collection>__answers` in Qdrant, so the cache
survives restarts of this service.

Embeddings alone are not safe enough: in tests, "points for a 3 inch defect" vs
"... 9 inch defect" scored 0.915 and "pre-final inspection" vs "final
inspection" 0.92, while the same question in English vs Banglish scored 0.94.
So an earlier answer is reused only when ALL of these hold:
  * same language style (English / Banglish / Bengali script) - answers are
    written in the language of the question;
  * the same numbers;
  * and either the same wording after normalisation, or similarity >=
    PYRAG_ANSWER_CACHE_MIN_SIMILARITY (default 0.97), or similarity >= 0.93 with
    exactly the same content words (only word order / filler words differ).
A wrong reused answer is worse than a few tokens, so anything else is answered
fresh. The cache is cleared whenever the indexed documents change, and entries
older than PYRAG_ANSWER_CACHE_MAX_AGE_DAYS are ignored.

Every function here swallows its own errors: the cache must never stop a
question from being answered.
"""

from __future__ import annotations

import re
import time
import unicodedata
import uuid
from typing import Dict, List, Optional

import config

CACHE_SUFFIX = "__answers"
# Lower bound for any reuse; between this and MIN_SIMILARITY the content words must match exactly.
STRICT_SIMILARITY = 0.93

_BENGALI_SCRIPT = re.compile(r"[ঀ-৿]")
_TOKEN = re.compile(r"[0-9a-zঀ-৿]+")
_NUMBER = re.compile(r"\d+(?:[.,]\d+)?")
_BENGALI_DIGITS = str.maketrans("০১২৩৪৫৬৭৮৯", "0123456789")

# Function words that mark Bengali written in Latin letters ("kivabe kora hoy?").
_BANGLISH_MARKERS = {
    "ki", "kivabe", "kibhabe", "keno", "koto", "kon", "kothay", "kokhon", "kemon", "kora", "kore", "korte",
    "koro", "korbo", "korben", "hoy", "hobe", "holo", "hoye", "ache", "achhe", "nai", "naki", "theke",
    "jonno", "er", "ar", "ba", "na", "amar", "amader", "apnar", "tomar", "dao", "den", "bolo", "bolen",
    "chilo", "gula", "gulo", "ta", "ti", "te", "eta", "oita", "eita", "kivabe", "lagbe", "jabe",
}
# Words that carry no meaning of their own for matching purposes.
_STOPWORDS = _BANGLISH_MARKERS | {
    "a", "an", "the", "is", "are", "was", "were", "be", "to", "of", "in", "on", "for", "and", "or", "by",
    "with", "what", "which", "who", "whom", "how", "why", "when", "where", "do", "does", "did", "can",
    "could", "should", "would", "will", "please", "tell", "me", "about", "this", "that", "it", "its",
    "according", "manual", "document", "documents", "explain", "describe", "give", "i", "we", "our", "you",
}


# --------------------------------------------------------------------------- text features

def normalise(question: str) -> str:
    text = unicodedata.normalize("NFKC", question).lower().translate(_BENGALI_DIGITS)
    return " ".join(_TOKEN.findall(text))


def language(question: str) -> str:
    if _BENGALI_SCRIPT.search(question):
        return "bn"
    tokens = set(normalise(question).split())
    return "banglish" if tokens & _BANGLISH_MARKERS else "en"


def numbers(question: str) -> List[str]:
    text = unicodedata.normalize("NFKC", question).translate(_BENGALI_DIGITS)
    return sorted({n.replace(",", ".") for n in _NUMBER.findall(text)})


def content_words(question: str) -> List[str]:
    words = set()
    for token in normalise(question).split():
        if token in _STOPWORDS or token.isdigit():
            continue
        if len(token) > 4 and token.endswith("s") and not token.endswith("ss"):
            token = token[:-1]  # "defects" ~ "defect"
        words.add(token)
    return sorted(words)


def is_reusable(question: str, cached: Dict, similarity: float) -> bool:
    """Whether the cached answer to `cached["question"]` may answer `question`."""
    if cached.get("normalised") == normalise(question):
        return True
    if cached.get("language") != language(question):
        return False
    if list(cached.get("numbers") or []) != numbers(question):
        return False
    if similarity >= config.ANSWER_CACHE_MIN_SIMILARITY:
        return True
    return similarity >= STRICT_SIMILARITY and list(cached.get("words") or []) == content_words(question)


# --------------------------------------------------------------------------- storage

def cache_collection(collection_name: str) -> str:
    return f"{collection_name}{CACHE_SUFFIX}"


def _client():
    from rag.step06_vector_store import get_client  # local import: step06 also imports this module
    return get_client()


def lookup(collection_name: str, question: str, vector: List[float]) -> Optional[Dict]:
    """The reusable cached entry for this question, or None."""
    if not config.ANSWER_CACHE_ENABLED or not vector:
        return None
    try:
        from qdrant_client.http.models import FieldCondition, Filter, Range

        client = _client()
        name = cache_collection(collection_name)
        if not client.collection_exists(name):
            return None
        cutoff = time.time() - config.ANSWER_CACHE_MAX_AGE_DAYS * 86400
        hits = client.query_points(
            collection_name=name,
            query=vector,
            limit=5,
            score_threshold=STRICT_SIMILARITY,
            with_payload=True,
            query_filter=Filter(must=[FieldCondition(key="created_at", range=Range(gte=cutoff))]),
        ).points
        for hit in hits:
            payload = hit.payload or {}
            if is_reusable(question, payload, float(hit.score)):
                print(f"[cache] reused the answer to {payload.get('question')!r} (similarity {hit.score:.3f})")
                return {**payload, "similarity": float(hit.score)}
    except Exception as exc:  # noqa: BLE001 - a cache problem must never block an answer
        print(f"[cache] lookup skipped: {type(exc).__name__}: {exc}")
    return None


def store(collection_name: str, question: str, vector: List[float], answer: str, sources: List[Dict]) -> None:
    """Remember a fresh answer. Only answers that cite the documents are kept."""
    if not config.ANSWER_CACHE_ENABLED or not vector or not sources or not answer.strip():
        return
    try:
        from qdrant_client.http.models import Distance, PayloadSchemaType, PointStruct, VectorParams

        client = _client()
        name = cache_collection(collection_name)
        if not client.collection_exists(name):
            client.create_collection(name, vectors_config=VectorParams(size=len(vector), distance=Distance.COSINE))
            client.create_payload_index(name, field_name="created_at", field_schema=PayloadSchemaType.FLOAT)
        norm = normalise(question)
        lang = language(question)
        point_id = str(uuid.uuid5(uuid.NAMESPACE_URL, f"{collection_name}|{lang}|{norm}"))  # same question → same entry
        client.upsert(name, points=[PointStruct(id=point_id, vector=vector, payload={
            "question": question,
            "normalised": norm,
            "language": lang,
            "numbers": numbers(question),
            "words": content_words(question),
            "answer": answer,
            "sources": sources,
            "created_at": time.time(),
        })])
    except Exception as exc:  # noqa: BLE001
        print(f"[cache] store skipped: {type(exc).__name__}: {exc}")


def clear(collection_name: str) -> None:
    """Forget every cached answer of a Knowledge Base (its documents changed)."""
    try:
        client = _client()
        name = cache_collection(collection_name)
        if client.collection_exists(name):
            client.delete_collection(name)
            print(f"[cache] cleared cached answers for '{collection_name}'")
    except Exception as exc:  # noqa: BLE001
        print(f"[cache] clear skipped: {type(exc).__name__}: {exc}")
