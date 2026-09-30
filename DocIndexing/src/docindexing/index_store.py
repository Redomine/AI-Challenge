"""SQLite-хранилище для индексов.

Один файл = одна стратегия. Схема стабильная и пригодна для идемпотентной
перезаписи (``INSERT OR REPLACE`` по ``chunk_id``). Вектор хранится как
BLOB с прямым little-endian float32.
"""

from __future__ import annotations

import array
import json
import math
import sqlite3
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, List, Mapping, Optional, Sequence, Tuple

from .chunking import Chunk

# Версия схемы. Увеличивается при несовместимых изменениях.
SCHEMA_VERSION = 1

# SQL DDL: один файл — одна стратегия. Вектор хранится как BLOB.
_SCHEMA = """
CREATE TABLE IF NOT EXISTS chunks (
    chunk_id     TEXT PRIMARY KEY,
    strategy     TEXT NOT NULL,
    source       TEXT NOT NULL,
    source_title TEXT NOT NULL,
    file_path    TEXT NOT NULL,
    section      TEXT NOT NULL,
    chunk_index  INTEGER NOT NULL,
    pdf_page     INTEGER,
    heading_level INTEGER NOT NULL DEFAULT 0,
    word_count   INTEGER NOT NULL,
    char_count   INTEGER NOT NULL,
    text         TEXT NOT NULL,
    embedding    BLOB,
    embed_model  TEXT,
    embed_dim    INTEGER,
    created_at   TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_chunks_strategy ON chunks(strategy);
CREATE INDEX IF NOT EXISTS idx_chunks_source ON chunks(source);
CREATE INDEX IF NOT EXISTS idx_chunks_pdf_page ON chunks(pdf_page);

CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS provenance (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    source      TEXT NOT NULL,
    file_path   TEXT NOT NULL,
    file_sha256 TEXT NOT NULL,
    char_count  INTEGER NOT NULL,
    extra_json  TEXT,
    created_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_provenance_source ON provenance(source);
"""


def _vector_to_blob(vec: Sequence[float]) -> bytes:
    return array.array("f", vec).tobytes()


def _blob_to_vector(blob: bytes) -> List[float]:
    arr = array.array("f")
    arr.frombytes(blob)
    return list(arr)


def _validate_vector(vec: Sequence[float], *, expected_dim: int, position: int) -> int:
    """Проверить один вектор: непустой, финитный, нужной размерности.

    Возвращает фактическую длину. Бросает ``ValueError`` с понятным
    сообщением, указывающим индекс проблемного вектора.
    """

    if not isinstance(vec, (list, tuple)) or len(vec) == 0:
        raise ValueError(
            f"embedding at index {position} is empty or not a sequence"
        )
    if len(vec) != expected_dim:
        raise ValueError(
            f"embedding at index {position} has dim {len(vec)}, "
            f"expected {expected_dim}"
        )
    for j, x in enumerate(vec):
        try:
            fx = float(x)
        except (TypeError, ValueError) as e:
            raise ValueError(
                f"embedding at index {position} position {j} is not numeric: {x!r}"
            ) from e
        if not math.isfinite(fx):
            raise ValueError(
                f"embedding at index {position} position {j} is not finite: {fx}"
            )
    return len(vec)


def _validate_embeddings(
    embeddings: Sequence[Sequence[float]], *, embed_dim: int
) -> None:
    """Проверить весь список эмбеддингов перед записью."""

    if embed_dim <= 0:
        raise ValueError(f"embed_dim must be positive, got {embed_dim}")
    if not embeddings:
        # Пустой корпус — допустимо, но размерность всё равно проверяем.
        return
    for i, vec in enumerate(embeddings):
        _validate_vector(vec, expected_dim=embed_dim, position=i)


@dataclass
class SearchHit:
    chunk_id: str
    score: float
    source: str
    title: str
    section: str
    text: str
    pdf_page: Optional[int]


class IndexStore:
    """Изолированное хранилище одной стратегии."""

    def __init__(self, db_path: Path) -> None:
        self.db_path = db_path
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        self._conn = sqlite3.connect(str(self.db_path))
        self._conn.execute("PRAGMA journal_mode=WAL")
        self._conn.execute("PRAGMA synchronous=NORMAL")
        self._conn.executescript(_SCHEMA)
        self._conn.commit()

    # --- meta -----------------------------------------------------

    def set_meta(self, key: str, value: str) -> None:
        self._conn.execute(
            "INSERT OR REPLACE INTO meta(key, value) VALUES (?, ?)",
            (key, value),
        )
        self._conn.commit()

    def get_meta(self, key: str) -> Optional[str]:
        row = self._conn.execute(
            "SELECT value FROM meta WHERE key = ?", (key,)
        ).fetchone()
        return row[0] if row else None

    # --- provenance ------------------------------------------------

    def add_provenance(
        self,
        *,
        source: str,
        file_path: str,
        file_sha256: str,
        char_count: int,
        extra: Optional[dict] = None,
    ) -> None:
        extra_json = json.dumps(extra or {}, ensure_ascii=False)
        self._conn.execute(
            "INSERT INTO provenance(source, file_path, file_sha256, char_count, extra_json) "
            "VALUES (?, ?, ?, ?, ?)",
            (source, file_path, file_sha256, char_count, extra_json),
        )
        self._conn.commit()

    # --- chunks ---------------------------------------------------

    def upsert_chunks(
        self,
        chunks: Iterable[Chunk],
        *,
        embeddings: Sequence[Sequence[float]],
        embed_model: str,
        embed_dim: int,
        file_paths: Optional[Mapping[str, str]] = None,
    ) -> int:
        """Вставить или обновить чанки вместе с эмбеддингами.

        Поведение:

        * ``file_paths`` — мэппинг ``source -> file_path``. Значение
          подставляется в колонку ``chunks.file_path`` напрямую при
          вставке. Если источника нет в мэппинге, в ``file_path``
          записывается пустая строка — но это явно нежелательное
          состояние, и в этом случае лучше передать пустую строку
          явно.
        * Идемпотентно по ``chunk_id``: повторный запуск с тем же
          ``chunk_id`` перезапишет текст, метаданные и эмбеддинг.
          ``file_path`` и ``embed_model`` тоже обновляются.
        * Все эмбеддинги валидируются: одинаковая размерность,
          отсутствие NaN/Inf, непустые. При нарушении — ``ValueError``,
          транзакция откатывается, ничего не сохраняется.
        """

        chunks_list = list(chunks)
        if len(chunks_list) != len(embeddings):
            raise ValueError(
                f"chunks/embeddings length mismatch: {len(chunks_list)} vs {len(embeddings)}"
            )
        if not embed_model:
            raise ValueError("embed_model must be a non-empty string")
        _validate_embeddings(embeddings, embed_dim=embed_dim)

        rows = []
        for i, (c, vec) in enumerate(zip(chunks_list, embeddings)):
            path = (file_paths or {}).get(c.source, "")
            rows.append(
                (
                    c.chunk_id,
                    c.strategy,
                    c.source,
                    c.title,
                    path,
                    " / ".join(c.section_path),
                    i,
                    c.pdf_page,
                    c.heading_level,
                    c.word_count,
                    c.char_count,
                    c.text,
                    _vector_to_blob(vec),
                    embed_model,
                    embed_dim,
                )
            )
        try:
            self._conn.execute("BEGIN")
            self._conn.executemany(
                """
                INSERT OR REPLACE INTO chunks(
                    chunk_id, strategy, source, source_title, file_path,
                    section, chunk_index, pdf_page, heading_level,
                    word_count, char_count, text, embedding, embed_model, embed_dim
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                rows,
            )
            self._conn.commit()
        except Exception:
            self._conn.rollback()
            raise
        return len(rows)

    def replace_corpus(
        self,
        *,
        strategy: str,
        chunks: Sequence[Chunk],
        embeddings: Sequence[Sequence[float]],
        embed_model: str,
        embed_dim: int,
        file_paths: Mapping[str, str],
        source_to_provenance: Mapping[str, Tuple[str, str, int, dict]],
    ) -> int:
        """Полная замена корпуса для стратегии в одной транзакции.

        Атомарно:

        * удаляет все чанки текущей ``strategy``;
        * удаляет всю provenance;
        * вставляет новые чанки с эмбеддингами;
        * вставляет свежую provenance.

        Это исключает остаточные данные от прошлых сборок: после
        успешного вызова состояние БД точно соответствует переданным
        ``chunks`` и ``source_to_provenance``.

        ``source_to_provenance`` — мэппинг ``source -> (file_path,
        file_sha256, char_count, extra_dict)``.
        """

        chunks_list = list(chunks)
        if len(chunks_list) != len(embeddings):
            raise ValueError(
                f"chunks/embeddings length mismatch: {len(chunks_list)} vs {len(embeddings)}"
            )
        if not embed_model:
            raise ValueError("embed_model must be a non-empty string")
        _validate_embeddings(embeddings, embed_dim=embed_dim)

        rows = []
        for i, (c, vec) in enumerate(zip(chunks_list, embeddings)):
            path = file_paths.get(c.source, "")
            rows.append(
                (
                    c.chunk_id,
                    c.strategy,
                    c.source,
                    c.title,
                    path,
                    " / ".join(c.section_path),
                    i,
                    c.pdf_page,
                    c.heading_level,
                    c.word_count,
                    c.char_count,
                    c.text,
                    _vector_to_blob(vec),
                    embed_model,
                    embed_dim,
                )
            )
        try:
            self._conn.execute("BEGIN")
            self._conn.execute("DELETE FROM chunks WHERE strategy = ?", (strategy,))
            self._conn.execute("DELETE FROM provenance")
            if rows:
                self._conn.executemany(
                    """
                    INSERT INTO chunks(
                        chunk_id, strategy, source, source_title, file_path,
                        section, chunk_index, pdf_page, heading_level,
                        word_count, char_count, text, embedding, embed_model, embed_dim
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    rows,
                )
            for source, (file_path, file_sha256, char_count, extra) in source_to_provenance.items():
                self._conn.execute(
                    "INSERT INTO provenance(source, file_path, file_sha256, char_count, extra_json) "
                    "VALUES (?, ?, ?, ?, ?)",
                    (
                        source,
                        file_path,
                        file_sha256,
                        char_count,
                        json.dumps(extra or {}, ensure_ascii=False),
                    ),
                )
            self._conn.commit()
        except Exception:
            self._conn.rollback()
            raise
        return len(rows)

    def set_file_paths(self, mapping: Mapping[str, str]) -> None:
        """Привязать путь файла к чанкам (по источнику)."""

        for source, path in mapping.items():
            self._conn.execute(
                "UPDATE chunks SET file_path = ? WHERE source = ?", (path, source)
            )
        self._conn.commit()

    def count_chunks(self) -> int:
        row = self._conn.execute("SELECT COUNT(*) FROM chunks").fetchone()
        return int(row[0])

    def chunks_without_embedding(self) -> int:
        row = self._conn.execute(
            "SELECT COUNT(*) FROM chunks WHERE embedding IS NULL"
        ).fetchone()
        return int(row[0])

    def stored_embed_dim(self) -> Optional[int]:
        """Размерность, записанная в колонке ``chunks.embed_dim`` (None если пусто)."""

        row = self._conn.execute(
            "SELECT embed_dim FROM chunks WHERE embed_dim IS NOT NULL LIMIT 1"
        ).fetchone()
        return int(row[0]) if row else None

    # --- search ---------------------------------------------------

    def cosine_search(
        self, query_vec: Sequence[float], *, top_k: int = 5
    ) -> List[SearchHit]:
        if top_k <= 0:
            return []
        rows = self._conn.execute(
            """
            SELECT chunk_id, source, source_title, section, text, pdf_page, embedding
            FROM chunks
            WHERE embedding IS NOT NULL
            """
        ).fetchall()
        scored: List[Tuple[float, sqlite3.Row]] = []
        qn = math.sqrt(sum(x * x for x in query_vec)) or 1.0
        for row in rows:
            vec = _blob_to_vector(row[6])
            # Косинус считаем прямо в Python: батчи маленькие, накладные расходы ОК.
            dot = 0.0
            nn = 0.0
            for a, b in zip(query_vec, vec):
                dot += a * b
                nn += b * b
            denom = qn * math.sqrt(nn) or 1.0
            score = dot / denom
            scored.append((score, row))
        scored.sort(key=lambda x: x[0], reverse=True)
        hits: List[SearchHit] = []
        for score, row in scored[:top_k]:
            hits.append(
                SearchHit(
                    chunk_id=row[0],
                    score=float(score),
                    source=row[1],
                    title=row[2],
                    section=row[3],
                    text=row[4],
                    pdf_page=row[5],
                )
            )
        return hits

    def close(self) -> None:
        self._conn.close()


__all__ = [
    "IndexStore",
    "SearchHit",
    "SCHEMA_VERSION",
    "_vector_to_blob",
    "_blob_to_vector",
]