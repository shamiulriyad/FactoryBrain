"""Answer cache: when an earlier answer may be reused, and the store / lookup / clear cycle.
No network: Qdrant runs in memory and vectors are hand-made. The similarity values in the
reuse tests are the ones measured with gemini-embedding-001 for these question pairs."""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import config  # noqa: E402
from rag import answer_cache  # noqa: E402


def entry(question: str) -> dict:
    return {
        "question": question,
        "normalised": answer_cache.normalise(question),
        "language": answer_cache.language(question),
        "numbers": answer_cache.numbers(question),
        "words": answer_cache.content_words(question),
    }


def test_language_style_is_detected():
    assert answer_cache.language("How is fabric inspection done?") == "en"
    assert answer_cache.language("fabric inspection kivabe kora hoy?") == "banglish"
    assert answer_cache.language("ফেব্রিক ইন্সপেকশন কিভাবে হয়?") == "bn"


@pytest.mark.parametrize("new, old, similarity", [
    ("Fabric inspection kivabe kora hoy", "fabric inspection kivabe kora hoy?", 0.996),   # same words
    ("What is the acceptable range in 4 point system", "What is the acceptable range in the 4-point system?", 0.981),
    ("fabric inspection er process ki?", "fabric inspection kivabe kora hoy?", 0.971),      # paraphrase, same style
])
def test_equivalent_questions_reuse_the_answer(new, old, similarity):
    assert answer_cache.is_reusable(new, entry(old), similarity)


@pytest.mark.parametrize("new, old, similarity", [
    ("How many points for a 9 inch defect?", "How many points for a 3 inch defect?", 0.915),  # numbers differ
    ("Who does the final inspection?", "Who does the pre-final inspection?", 0.920),          # one word changes the meaning
    ("How is fabric inspection done?", "fabric inspection kivabe kora hoy?", 0.939),          # English vs Banglish
    ("AQL inspection kivabe kora hoy?", "AQL inspection procedure ki?", 0.919),               # below the bar
    ("What is the AQL for sewing?", "What is the AQL for cutting?", 0.844),
])
def test_different_questions_are_answered_fresh(new, old, similarity):
    assert not answer_cache.is_reusable(new, entry(old), similarity)


def test_strict_band_needs_the_same_content_words(monkeypatch):
    monkeypatch.setattr(config, "ANSWER_CACHE_MIN_SIMILARITY", 0.97)
    old = entry("What is the AQL for final inspection?")
    assert answer_cache.is_reusable("final inspection AQL - what is it?", old, 0.95)       # same words, other order
    assert not answer_cache.is_reusable("What is the AQL for line inspection?", old, 0.95)


@pytest.fixture()
def memory_qdrant(monkeypatch):
    from qdrant_client import QdrantClient

    client = QdrantClient(":memory:")
    monkeypatch.setattr(answer_cache, "_client", lambda: client)
    monkeypatch.setattr(config, "ANSWER_CACHE_ENABLED", True)
    return client


SOURCES = [{"source": "manual.pdf", "page": 5, "score": 0.8, "excerpt": "..."}]


def test_store_then_lookup_reuses_and_clear_forgets(memory_qdrant):
    q = "fabric inspection kivabe kora hoy?"
    answer_cache.store("kb", q, [1.0, 0.0, 0.0], "10% fabric inspect kora hoy.", SOURCES)

    hit = answer_cache.lookup("kb", "Fabric inspection kivabe kora hoy", [1.0, 0.0, 0.0])
    assert hit is not None and hit["answer"] == "10% fabric inspect kora hoy." and hit["sources"] == SOURCES

    # A different question pointing elsewhere is not reused.
    assert answer_cache.lookup("kb", "metal detection standard ki?", [0.0, 1.0, 0.0]) is None

    answer_cache.clear("kb")
    assert answer_cache.lookup("kb", q, [1.0, 0.0, 0.0]) is None


def test_answers_without_sources_are_not_cached(memory_qdrant):
    answer_cache.store("kb", "hello", [1.0, 0.0, 0.0], "Hi! Ask me about your documents.", [])
    assert answer_cache.lookup("kb", "hello", [1.0, 0.0, 0.0]) is None


def test_old_entries_are_ignored(memory_qdrant, monkeypatch):
    answer_cache.store("kb", "What is AQL?", [1.0, 0.0, 0.0], "Acceptable quality level.", SOURCES)
    monkeypatch.setattr(config, "ANSWER_CACHE_MAX_AGE_DAYS", -1)  # everything is "too old"
    assert answer_cache.lookup("kb", "What is AQL?", [1.0, 0.0, 0.0]) is None


def test_disabled_cache_never_reuses(memory_qdrant, monkeypatch):
    answer_cache.store("kb", "What is AQL?", [1.0, 0.0, 0.0], "Acceptable quality level.", SOURCES)
    monkeypatch.setattr(config, "ANSWER_CACHE_ENABLED", False)
    assert answer_cache.lookup("kb", "What is AQL?", [1.0, 0.0, 0.0]) is None


def test_cache_errors_never_raise(monkeypatch):
    def broken():
        raise ConnectionError("qdrant down")

    monkeypatch.setattr(answer_cache, "_client", broken)
    monkeypatch.setattr(config, "ANSWER_CACHE_ENABLED", True)
    assert answer_cache.lookup("kb", "x", [1.0]) is None
    answer_cache.store("kb", "x", [1.0], "y", SOURCES)
    answer_cache.clear("kb")
