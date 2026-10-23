"""Auth and error-mapping tests for the RAG HTTP service. They never load an embedding model or
talk to Qdrant: every request here is answered (or refused) before any heavy work starts.

    pip install -r requirements-dev.txt
    pytest tests/test_service_security.py
"""

import sys
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import config  # noqa: E402
import main  # noqa: E402
from api.ingest import ScannedPdfError, UnreadablePdfError, ingest_document  # noqa: E402

KEY = "test-key-0123456789"


@pytest.fixture()
def client(monkeypatch):
    monkeypatch.setattr(config, "RAG_API_KEY", KEY)
    # No `with` block: that would run the lifespan hook, which loads the embedding model.
    return TestClient(main.app)


# A collection name with a space is rejected with 400 by the route itself, before any model or
# Qdrant work - so "400" proves the request got past authentication, "401" proves it did not.
PASSES_AUTH_PATH = "/api/kb/bad%20name/query"


def test_health_is_open_without_a_key(client):
    # /health talks to Qdrant, so only assert it is not an auth failure.
    assert client.get("/health").status_code != 401


@pytest.mark.parametrize("method,path", [
    ("POST", "/api/kb/kb_x/query"),
    ("POST", "/api/kb/kb_x/ingest?document_id=d"),
    ("DELETE", "/api/kb/kb_x"),
    ("DELETE", "/api/kb/kb_x/documents/d1"),
])
def test_every_data_route_refuses_requests_without_the_key(client, method, path):
    r = client.request(method, path, json={"question": "hi"} if method == "POST" else None)
    assert r.status_code == 401
    assert "api key" in r.json()["detail"].lower()


def test_wrong_key_is_refused(client):
    r = client.post(PASSES_AUTH_PATH, json={"question": "hi"}, headers={"X-Rag-Api-Key": KEY + "x"})
    assert r.status_code == 401


def test_key_of_different_length_is_refused_without_error(client):
    r = client.post(PASSES_AUTH_PATH, json={"question": "hi"}, headers={"X-Rag-Api-Key": "x"})
    assert r.status_code == 401


def test_right_key_gets_past_authentication(client):
    r = client.post(PASSES_AUTH_PATH, json={"question": "hi"}, headers={"X-Rag-Api-Key": KEY})
    assert r.status_code == 400  # reached the route and failed its own validation


def test_without_a_configured_key_the_service_stays_open(monkeypatch):
    monkeypatch.setattr(config, "RAG_API_KEY", None)
    r = TestClient(main.app).post(PASSES_AUTH_PATH, json={"question": "hi"})
    assert r.status_code == 400  # not 401: auth is off, which is what the startup warning is about


def test_key_is_not_accepted_in_the_query_string(client):
    r = client.post(PASSES_AUTH_PATH + f"?x_rag_api_key={KEY}", json={"question": "hi"})
    assert r.status_code == 401


# ---------------------------------------------------------------- PDF error classification

def _ingest(path: Path):
    return ingest_document(
        collection_name="kb_test", document_id="d1", pdf_path=path, filename=path.name,
        chunk_size=500, chunk_overlap=50, embeddings=None,
    )


def test_corrupt_pdf_is_reported_as_unreadable_not_scanned(tmp_path):
    bad = tmp_path / "corrupt.pdf"
    bad.write_bytes(b"%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF")
    with pytest.raises(UnreadablePdfError) as exc:
        _ingest(bad)
    assert "could not be read" in str(exc.value) or "no readable pages" in str(exc.value)
    assert "scanned" not in str(exc.value).lower()


def test_garbage_bytes_are_unreadable(tmp_path):
    bad = tmp_path / "garbage.pdf"
    bad.write_bytes(b"this is not a pdf at all " * 40)
    with pytest.raises(UnreadablePdfError):
        _ingest(bad)


def test_image_only_pdf_is_reported_as_scanned(tmp_path):
    pymupdf = pytest.importorskip("pymupdf")
    doc = pymupdf.open()
    doc.new_page()  # a real page with no text layer
    blank = tmp_path / "blank.pdf"
    doc.save(blank)
    with pytest.raises(ScannedPdfError) as exc:
        _ingest(blank)
    assert "scanned" in str(exc.value).lower()


def test_both_pdf_errors_map_to_4xx_so_the_caller_does_not_retry():
    # Callers should retry only 5xx; a deterministic rejection must be a 4xx.
    # main.ingest maps both exception types to 422.
    import inspect
    src = inspect.getsource(main.ingest)
    assert "except (ScannedPdfError, UnreadablePdfError)" in src
    assert "status_code=422" in src
