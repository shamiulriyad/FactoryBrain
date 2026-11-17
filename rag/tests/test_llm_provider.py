"""Choosing the answering LLM (PYRAG_LLM_PROVIDER). No network calls: models are only constructed."""

import pytest

import config
from rag import step10_generate


@pytest.fixture
def llm_config(monkeypatch):
    def apply(**values):
        for name, value in values.items():
            monkeypatch.setattr(config, name, value)

    apply(OPENAI_BASE_URL=None, TEMPERATURE=0.2)
    return apply


def test_openai_provider_builds_an_openai_chat_model(llm_config):
    llm_config(LLM_PROVIDER="openai", LLM_MODEL="gpt-4o-mini", OPENAI_API_KEY="sk-test")
    llm = step10_generate.get_llm()
    assert type(llm).__name__ == "ChatOpenAI"
    assert llm.model_name == "gpt-4o-mini"
    assert llm.temperature == 0.2


def test_google_provider_builds_a_gemini_chat_model(llm_config):
    llm_config(LLM_PROVIDER="google", LLM_MODEL="gemini-flash-lite-latest", GOOGLE_API_KEY="test-key")
    llm = step10_generate.get_llm()
    assert type(llm).__name__ == "ChatGoogleGenerativeAI"


def test_empty_temperature_is_not_sent(llm_config):
    llm_config(LLM_PROVIDER="openai", LLM_MODEL="gpt-4o-mini", OPENAI_API_KEY="sk-test", TEMPERATURE=None)
    llm = step10_generate.get_llm()
    assert llm.temperature is None


def test_openai_without_a_key_fails_with_a_clear_message(llm_config):
    llm_config(LLM_PROVIDER="openai", LLM_MODEL="gpt-4o-mini", OPENAI_API_KEY=None)
    with pytest.raises(RuntimeError, match="PYRAG_OPENAI_API_KEY"):
        step10_generate.get_llm()


def test_unknown_provider_is_rejected(llm_config):
    llm_config(LLM_PROVIDER="mistral")
    with pytest.raises(RuntimeError, match="PYRAG_LLM_PROVIDER='mistral'"):
        step10_generate.get_llm()
