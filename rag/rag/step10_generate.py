"""STEP 10 - Gemini.

The retrieved chunks become the context. For document questions the prompt forces
the model to answer from that context only, and to tag every claim with the [S1]..[Sn] marker of
the chunk it came from - that is what makes step 11's sources trustworthy.
"""

from typing import List, Tuple

from langchain_core.documents import Document
from langchain_core.output_parsers import StrOutputParser
from langchain_core.prompts import ChatPromptTemplate
from langchain_google_genai import ChatGoogleGenerativeAI

import config

SYSTEM_PROMPT = """You are a friendly assistant that helps people with the documents in their
knowledge base. Each message is either conversation or a question about the documents -
use your judgement.

Conversation (greetings, thanks, goodbyes, "how are you", "what can you do", asking who
you are, and similar):
- Reply naturally and briefly, like a helpful person would. You may mention that you can
  answer questions about their uploaded documents.
- Do NOT use the context and do NOT add [S] markers.

Questions about the documents:
- Use ONLY the context below. Do not add outside knowledge.
- After each claim, put the marker of the chunk it came from, like [S1] or [S2].
- If the context does not contain the answer, say clearly that the documents do not cover
  it. Do not guess. If the context says no passages were found, tell the user that no
  relevant information (or no indexed documents) was found and suggest uploading a PDF
  or rephrasing.

Language - very important:
- Reply in the language of the USER'S MESSAGE, never in the language of the context.
  The documents may be written in a different language than the question; read them,
  then answer in the user's language (translate quotes and terms as needed).
- The language of the context (for example Hungarian) must NEVER influence the language
  of your reply. Only the words of the user's message decide it.
- If the message is a language written in Latin letters (for example Bengali or Hindi in
  English letters, like "motivation ki?"), reply in that same language in the same
  style. If you cannot tell which language it is, reply in English.

Be direct - no preamble."""

PROMPT = ChatPromptTemplate.from_messages([
    ("system", SYSTEM_PROMPT),
    ("human", "Context:\n{context}\n\nQuestion: {question}\n\n"
              "(Reply in the language of the question above - or in English if unsure - "
              "not in the language of the context.)"),
])


def get_llm() -> ChatGoogleGenerativeAI:
    config.require_api_key()
    return ChatGoogleGenerativeAI(
        model=config.LLM_MODEL,
        google_api_key=config.GOOGLE_API_KEY,
        temperature=config.TEMPERATURE,
    )


def format_context(scored_docs: List[Tuple[Document, float]]) -> str:
    blocks = []
    for i, (doc, _score) in enumerate(scored_docs, start=1):
        page = doc.metadata.get("page_number", "?")
        source = doc.metadata.get("source_file", "document")
        blocks.append(f"[S{i}] ({source}, page {page})\n{doc.page_content}")
    return "\n\n---\n\n".join(blocks)


def generate_answer(question: str, scored_docs: List[Tuple[Document, float]], llm=None) -> str:
    # Even with nothing retrieved we still ask the model: it can chat back to a greeting,
    # or explain that nothing relevant was found, in the user's own language.
    chain = PROMPT | (llm or get_llm()) | StrOutputParser()
    answer = chain.invoke({
        "context": format_context(scored_docs) if scored_docs else "(no relevant passages were found)",
        "question": question,
    })

    print(f"[10] Gemini    : {config.LLM_MODEL} answered")
    return answer.strip()
