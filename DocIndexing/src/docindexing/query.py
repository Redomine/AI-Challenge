"""Запрос top-k из конкретного индекса."""

from __future__ import annotations

import json
import logging
import math
from dataclasses import asdict
from pathlib import Path
from typing import List, Sequence

from .config import RunConfig
from .embeddings import OllamaClient
from .index_store import IndexStore

LOG = logging.getLogger(__name__)


def _validate_query_vector(vec: Sequence[float], *, expected_dim: int) -> None:
    """Проверить вектор запроса перед косинусным поиском.

    Должен быть непустым, финитным, нужной размерности. Иначе — результат
    поиска был бы ложным, поэтому фейлим с понятным сообщением.
    """

    if not isinstance(vec, (list, tuple)) or len(vec) == 0:
        raise ValueError("query embedding is empty or not a sequence")
    if len(vec) != expected_dim:
        raise ValueError(
            f"query embedding dim={len(vec)} but index dim={expected_dim}"
        )
    for j, x in enumerate(vec):
        try:
            fx = float(x)
        except (TypeError, ValueError) as e:
            raise ValueError(
                f"query embedding position {j} is not numeric: {x!r}"
            ) from e
        if not math.isfinite(fx):
            raise ValueError(
                f"query embedding position {j} is not finite: {fx}"
            )


def _check_index_matches_model(store: IndexStore, cfg: RunConfig) -> int:
    """Сверить модель и размерность индекса с конфигом запроса.

    Возвращает ``embed_dim``, с которым нужно сравнивать вектор запроса.
    Бросает ``RuntimeError``, если индекс был собран другой моделью или
    имеет несовместимую размерность. Пустой индекс (без чанков)
    пропускается: размерность берётся из конфига провайдера, и ошибки
    проявятся уже на конкретных данных.
    """

    stored_model = store.get_meta("embed_model")
    if stored_model and stored_model != cfg.embed_model:
        raise RuntimeError(
            f"Index was built with embed_model={stored_model!r}, "
            f"but query asks for {cfg.embed_model!r}. "
            "Rebuild the index or pass matching --embed-model."
        )
    stored_dim_meta = store.get_meta("embed_dim")
    if stored_dim_meta:
        try:
            stored_dim_meta = int(stored_dim_meta)
        except ValueError as e:
            raise RuntimeError(
                f"Index has invalid embed_dim meta: {stored_dim_meta!r}"
            ) from e
    else:
        stored_dim_meta = store.stored_embed_dim()

    if stored_dim_meta and stored_dim_meta <= 0:
        raise RuntimeError(
            f"Index has non-positive embed_dim={stored_dim_meta}"
        )
    return stored_dim_meta if stored_dim_meta else 0


def query_index(
    cfg: RunConfig,
    *,
    question: str,
    strategy: str,
    top_k: int,
    ollama_client: OllamaClient | None = None,
    store: IndexStore | None = None,
) -> List[dict]:
    """Получить top-k по косинусу из нужного индекса.

    Возвращает список словарей с полями ``chunk_id``, ``score``, ``source``,
    ``title``, ``section``, ``text``, ``pdf_page``.

    Перед поиском проверяется, что индекс был собран той же моделью, что
    запрошена в ``cfg.embed_model``, и что размерность совпадает с тем,
    что вернул провайдер эмбеддингов. Любое расхождение — ошибка.
    """

    if strategy not in {"fixed", "structural"}:
        raise ValueError(f"Unknown strategy: {strategy!r}")
    own_store = store is None
    if own_store:
        db_path = cfg.fixed_db() if strategy == "fixed" else cfg.structural_db()
        if not db_path.exists():
            raise FileNotFoundError(f"Index not found: {db_path}")
        store = IndexStore(db_path)

    try:
        expected_dim = _check_index_matches_model(store, cfg)

        client_provided = ollama_client is not None
        client = ollama_client or OllamaClient(
            cfg.ollama_url,
            timeout=cfg.embed_timeout,
            max_retries=cfg.embed_max_retries,
            retry_base_delay=cfg.embed_retry_base_delay,
        )
        try:
            result = client.embed([question], model=cfg.embed_model)
        finally:
            if not client_provided:
                pass  # клиент держим открытым, HTTP stateless

        query_vec = result.vectors[0]
        # Размерность из провайдера — единственный источник истины для запроса.
        actual_dim = len(query_vec)
        if expected_dim and expected_dim != actual_dim:
            raise RuntimeError(
                f"Query provider returned dim={actual_dim}, but index dim={expected_dim}"
            )
        _validate_query_vector(query_vec, expected_dim=actual_dim)

        hits = store.cosine_search(query_vec, top_k=top_k)
        return [asdict(h) for h in hits]
    finally:
        if own_store:
            store.close()


def run_query_cli(args) -> dict:
    """Реализация команды ``query`` CLI.

    Возвращает dict со списком результатов и метаданными для вывода в stdout.
    """

    from .config import build_config_from_args

    cfg = build_config_from_args(args)
    hits = query_index(
        cfg,
        question=args.question,
        strategy=args.strategy,
        top_k=args.top_k,
    )
    payload = {
        "question": args.question,
        "strategy": args.strategy,
        "top_k": args.top_k,
        "model": cfg.embed_model,
        "hits": hits,
    }
    print(json.dumps(payload, ensure_ascii=False, indent=2))
    return payload


__all__ = ["query_index", "run_query_cli"]