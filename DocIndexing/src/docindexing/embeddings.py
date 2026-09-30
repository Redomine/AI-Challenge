"""Клиент Ollama ``POST /api/embed``.

Особенности:
    * Батчинг: Ollama принимает массив ``input`` в одном запросе.
    * Повторы при сетевых ошибках и 5xx (transient).
    * Контроль размерности: первый успешный ответ фиксирует ``dim``,
      все последующие проверяются.
    * Никаких фейковых эмбеддингов — при сбое возвращается ошибка.
"""

from __future__ import annotations

import json
import logging
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Iterable, List, Sequence

LOG = logging.getLogger(__name__)


class EmbeddingError(RuntimeError):
    """Ошибка получения эмбеддингов."""


@dataclass
class EmbeddingResult:
    vectors: List[List[float]]
    model: str
    dim: int


class OllamaClient:
    """Минимальный HTTP-клиент Ollama без внешних зависимостей.

    Используем :mod:`urllib`, чтобы не добавлять ``requests`` в
    обязательные зависимости. Если ``requests`` установлен — допустимо,
    но инструмент работает на stdlib.
    """

    def __init__(
        self,
        base_url: str,
        *,
        timeout: float = 60.0,
        max_retries: int = 4,
        retry_base_delay: float = 0.5,
        user_agent: str = "docindexing/1.0",
    ) -> None:
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout
        self.max_retries = max_retries
        self.retry_base_delay = retry_base_delay
        self.user_agent = user_agent
        self._expected_dim: int | None = None

    def health(self) -> bool:
        """Проверить, что сервер Ollama отвечает."""

        try:
            with self._open("GET", "/api/tags") as resp:
                return resp.status == 200
        except Exception:
            return False

    def embed(self, texts: Sequence[str], *, model: str) -> EmbeddingResult:
        """Запросить эмбеддинги для пачки текстов.

        ``texts`` должен быть непустым. Возвращает ``EmbeddingResult``
        с плотными векторами одинаковой размерности.
        """

        if not texts:
            return EmbeddingResult(vectors=[], model=model, dim=self._expected_dim or 0)
        payload = json.dumps({"model": model, "input": list(texts)}).encode("utf-8")
        last_exc: Exception | None = None
        for attempt in range(self.max_retries + 1):
            try:
                response = self._post_json("/api/embed", payload)
                break
            except urllib.error.HTTPError as e:
                last_exc = e
                # 4xx (кроме 408/429) — нет смысла ретраить.
                if e.code not in {408, 429, 500, 502, 503, 504}:
                    raise EmbeddingError(
                        f"Ollama HTTP {e.code}: {e.read().decode('utf-8', 'replace')}"
                    ) from e
                self._sleep_backoff(attempt)
            except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
                last_exc = e
                self._sleep_backoff(attempt)
        else:  # pragma: no cover
            raise EmbeddingError(f"Ollama unavailable after retries: {last_exc}")

        body = json.loads(response.decode("utf-8"))
        vectors = body.get("embeddings")
        if not isinstance(vectors, list) or not vectors:
            raise EmbeddingError(
                f"Ollama returned no embeddings: {json.dumps(body)[:300]}"
            )
        for i, vec in enumerate(vectors):
            if not isinstance(vec, list) or not vec:
                raise EmbeddingError(
                    f"Ollama returned empty vector at index {i}; refusing to fake embedding"
                )
        dim = len(vectors[0])
        if self._expected_dim is None:
            self._expected_dim = dim
        elif self._expected_dim != dim:
            raise EmbeddingError(
                f"Embedding dim mismatch: expected {self._expected_dim}, got {dim}"
            )
        return EmbeddingResult(vectors=vectors, model=body.get("model", model), dim=dim)

    def _sleep_backoff(self, attempt: int) -> None:
        delay = self.retry_base_delay * (2**attempt)
        LOG.warning("Ollama retry %d in %.2fs", attempt + 1, delay)
        time.sleep(delay)

    def _post_json(self, path: str, body: bytes) -> bytes:
        url = f"{self.base_url}{path}"
        req = urllib.request.Request(
            url,
            data=body,
            headers={
                "Content-Type": "application/json",
                "User-Agent": self.user_agent,
            },
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=self.timeout) as resp:
            return resp.read()

    def _open(self, method: str, path: str):
        url = f"{self.base_url}{path}"
        req = urllib.request.Request(
            url,
            headers={"User-Agent": self.user_agent},
            method=method,
        )
        return urllib.request.urlopen(req, timeout=self.timeout)


def embed_in_batches(
    client: OllamaClient,
    texts: Sequence[str],
    *,
    model: str,
    batch_size: int = 16,
) -> List[List[float]]:
    """Удобный враппер: разбить вход на батчи и собрать плоский список."""

    if batch_size <= 0:
        raise ValueError("batch_size must be positive")
    out: List[List[float]] = []
    for i in range(0, len(texts), batch_size):
        batch = list(texts[i : i + batch_size])
        result = client.embed(batch, model=model)
        out.extend(result.vectors)
    return out


__all__ = [
    "OllamaClient",
    "EmbeddingError",
    "EmbeddingResult",
    "embed_in_batches",
]
