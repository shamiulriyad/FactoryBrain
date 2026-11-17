"""Orchestrates steps 7-11 for one question, scoped to one Knowledge Base's Qdrant
collection (spec section 13: every retrieval query MUST filter by the current
Knowledge Base - a dedicated collection per KB makes that the default, not
something that can be forgotten in a WHERE clause)."""

import re
from datetime import datetime, timezone
from typing import Dict, List, Optional

from rag import answer_cache, index_meta
from rag.step06_vector_store import collection_exists, get_vector_store
from rag.step08_query_embedding import embed_question
from rag.step09_retrieve import retrieve
from rag.step10_generate import generate_answer

# Gemini tags claims with [S1], [S2]... (sometimes back-to-back) - strip them from the
# prose; the same information is returned structured in `sources`.
_MARKER_RE = re.compile(r"[ \t]*(?:\[S\d+\])+")
_CITED_RE = re.compile(r"\[S(\d+)\]")


class KnowledgeBaseNotIndexedError(ValueError):
    """Raised when a Knowledge Base has no ingested documents yet."""


def _iso(epoch) -> Optional[str]:
    if not isinstance(epoch, (int, float)):
        return None
    return datetime.fromtimestamp(epoch, tz=timezone.utc).isoformat()


def strip_markers(text: str) -> str:
    return _MARKER_RE.sub("", text).strip()


def answer_question(
    *, collection_name: str, question: str, top_k: int,
    similarity_threshold: Optional[float], embeddings, llm, use_cache: bool = True,
) -> Dict:
    """Answer one question. With `use_cache`, an earlier answer to the same question is
    returned without calling the LLM (see rag/answer_cache.py); the fresh answer is
    stored for next time either way."""
    index_meta.check_before_query(collection_name)                       # model still matches the index?

    scored_docs = []
    query_vector = None
    # A Knowledge Base with no documents yet can still chat (greetings etc.); the model is
    # told nothing was found and says so for real questions.
    if collection_exists(collection_name):
        query_vector = embed_question(embeddings, question)             # 8
        if use_cache:
            hit = answer_cache.lookup(collection_name, question, query_vector)
            if hit is not None:
                return {
                    "answer": hit["answer"],
                    "sources": hit.get("sources") or [],
                    "cached": True,
                    "cached_question": hit.get("question"),
                    "cached_at": _iso(hit.get("created_at")),
                }
        store = get_vector_store(embeddings, collection_name)
        scored_docs = retrieve(store, query_vector, top_k)              # 9

        if similarity_threshold is not None:
            scored_docs = [(doc, score) for doc, score in scored_docs if score >= similarity_threshold]

    raw_answer = generate_answer(question, scored_docs, llm=llm)         # 10
    answer = strip_markers(raw_answer)

    # Only the chunks the model actually cited are sources - a greeting or a "not covered"
    # reply cites nothing, so no unrelated passages get attached to it.
    cited = {int(n) for n in _CITED_RE.findall(raw_answer)}
    used_docs = [(doc, score) for i, (doc, score) in enumerate(scored_docs, start=1) if i in cited]

    sources: List[Dict] = [                                             # 11
        {
            "document_id": doc.metadata.get("document_id"),
            "chunk_id": str(doc.metadata.get("chunk_id")) if doc.metadata.get("chunk_id") is not None else None,
            "page": doc.metadata.get("page_number"),
            "source": doc.metadata.get("source_file", "document"),
            "score": float(score),
            "excerpt": doc.page_content[:280],
        }
        for doc, score in used_docs
    ]
    if query_vector is not None:
        answer_cache.store(collection_name, question, query_vector, answer, sources)
    return {"answer": answer, "sources": sources, "cached": False}
