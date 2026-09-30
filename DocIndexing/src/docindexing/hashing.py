"""SHA-256 и стабильные идентификаторы чанков.

Размер чанков в байтах небольшой, поэтому используем полноценный SHA-256,
а не короткий префикс. Это исключает коллизии внутри нашего корпуса.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any, Mapping


def file_sha256(path: Path, chunk_size: int = 1024 * 1024) -> str:
    """Посчитать SHA-256 файла потоково.

    Параметр ``chunk_size`` подобран так, чтобы один проход съедал файл
    Confluence JSON (~2 МБ) за два-три чтения.
    """

    h = hashlib.sha256()
    with open(path, "rb") as fh:
        while True:
            block = fh.read(chunk_size)
            if not block:
                break
            h.update(block)
    return h.hexdigest()


def dict_sha256(payload: Mapping[str, Any]) -> str:
    """SHA-256 словаря с канонической сериализацией ``json``."""

    encoded = json.dumps(
        payload, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    )
    return hashlib.sha256(encoded.encode("utf-8")).hexdigest()


__all__ = ["file_sha256", "dict_sha256"]
