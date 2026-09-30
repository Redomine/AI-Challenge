"""Оркестратор сборки индекса."""

from __future__ import annotations

import json
import logging
import os
import time
from pathlib import Path
from typing import Dict, List, Mapping, Tuple

from . import chunking, corpus, embeddings, hashing
from .config import RunConfig
from .confluence import load_confluence_doc
from .index_store import IndexStore
from .pdf_text import extract_pdf

LOG = logging.getLogger("docindexing.build")

# Суффикс для «черновика» БД, который собирается рядом с боевым файлом и
# заменяет его атомарно через ``os.replace`` только после успешной валидации.
STAGING_SUFFIX = ".new"


def _staging_path(db_path: Path) -> Path:
    """Путь к промежуточной БД, в которую идёт сборка текущего запуска."""

    return db_path.with_name(db_path.name + STAGING_SUFFIX)


def _finalize_db(staging: Path, final: Path) -> None:
    """Атомарно заменить ``final`` на ``staging``.

    Перед переносом удаляются WAL/SHM-файлы от staging — их быть не должно
    после закрытия соединения. Также подчищаются возможные остаточные
    side-файлы прежнего ``final`` (от прошлого запуска), чтобы Windows
    не упиралась в занятые дескрипторы. Если ``final`` отсутствует,
    ``os.replace`` создаст его.
    """

    for path in (staging, final):
        for side in ("-wal", "-shm", "-journal"):
            side_path = Path(str(path) + side)
            if side_path.exists():
                try:
                    side_path.unlink()
                except OSError:
                    LOG.warning("Could not remove side file: %s", side_path)
    os.replace(staging, final)


def _cleanup_staging(staging: Path) -> None:
    """Убрать staging и связанные side-файлы, если что-то пошло не так."""

    for side in ("", "-wal", "-shm", "-journal"):
        p = Path(str(staging) + side)
        if p.exists():
            try:
                p.unlink()
            except OSError:
                LOG.warning("Could not remove staging file: %s", p)


def _validate_post_build(store: IndexStore, *, expected_count: int, expected_dim: int) -> None:
    """Проверить свежесобранный индекс перед публикацией.

    Бросает ``RuntimeError`` при любом расхождении. На ошибке файл
    остаётся в staging-зоне и не заменяет боевую БД.
    """

    actual = store.count_chunks()
    if actual != expected_count:
        raise RuntimeError(
            f"Staged index has {actual} chunks, expected {expected_count}"
        )
    missing = store.chunks_without_embedding()
    if missing:
        raise RuntimeError(
            f"Staged index has {missing} chunks without embeddings"
        )
    stored_dim = store.stored_embed_dim()
    if stored_dim is None:
        if expected_dim > 0:
            raise RuntimeError(
                f"Staged index has no embed_dim recorded, expected {expected_dim}"
            )
    elif stored_dim != expected_dim:
        raise RuntimeError(
            f"Staged index embed_dim={stored_dim}, expected {expected_dim}"
        )


def _build_one_strategy(
    *,
    cfg: RunConfig,
    strategy: str,
    chunks: List[chunking.Chunk],
    embeddings_list: List[List[float]],
    cf_doc,
    pdf_doc,
    hashes: Mapping[str, str],
    file_paths: Mapping[str, str],
    provenance_records: Mapping[str, Tuple[str, str, int, dict]],
) -> None:
    """Собрать и опубликовать индекс для одной стратегии.

    Сборка ведётся в staging-файле ``<db>.new``. Только после полной
    валидации staging переименовывается поверх боевого файла. При любой
    ошибке staging удаляется, а боевой индекс не меняется — пользователь
    продолжает видеть результат предыдущей успешной сборки.
    """

    db_final = cfg.fixed_db() if strategy == "fixed" else cfg.structural_db()
    db_staging = _staging_path(db_final)

    # Удаляем возможные остатки от прерванного прошлого запуска.
    _cleanup_staging(db_staging)

    if chunks:
        embed_dim = len(embeddings_list[0])
    else:
        embed_dim = 0

    store = IndexStore(db_staging)
    try:
        store.set_meta("schema_version", str(1))
        store.set_meta("strategy", strategy)
        store.set_meta("embed_model", cfg.embed_model)
        store.set_meta("embed_dim", str(embed_dim))
        store.set_meta("created_at", time.strftime("%Y-%m-%dT%H:%M:%S"))

        store.replace_corpus(
            strategy=strategy,
            chunks=chunks,
            embeddings=embeddings_list,
            embed_model=cfg.embed_model,
            embed_dim=embed_dim,
            file_paths=file_paths,
            source_to_provenance=provenance_records,
        )

        _validate_post_build(
            store, expected_count=len(chunks), expected_dim=embed_dim
        )
    except Exception:
        try:
            store.close()
        except Exception:
            pass
        _cleanup_staging(db_staging)
        raise

    store.close()
    # Боевой файл перезаписывается только после успешной сборки и валидации.
    _finalize_db(db_staging, db_final)
    LOG.info("Published %s index: %s", strategy, db_final)


def build_index(cfg: RunConfig) -> dict:
    """Полная сборка двух индексов по конфигурации.

    Возвращает словарь с метриками, который попадает в JSON-отчёт.

    Семантика атомарности:

    * Сначала полностью собираются оба индекса в staging-файлы рядом с
      боевыми ``fixed.sqlite3.new`` и ``structural.sqlite3.new``.
    * Каждый staging валидируется; при ошибке staging удаляется, боевой
      файл не трогается.
    * Только после успешной валидации оба staging атомарно заменяют
      боевые файлы (``os.replace``). На любой неуспешной ветке
      пользователь продолжает видеть предыдущий валидный индекс.
    """

    LOG.info("Loading confluence JSON: %s", cfg.confluence_json)
    cf_doc = load_confluence_doc(cfg.confluence_json)
    LOG.info("Confluence: title=%r, sections=%d", cf_doc.title, len(cf_doc.sections))

    LOG.info("Extracting PDF: %s", cfg.pdf_path)
    pdf_doc = extract_pdf(cfg.pdf_path)
    LOG.info("PDF: pages=%d, chars=%d", len(pdf_doc.pages), pdf_doc.raw_char_count)

    LOG.info("Building corpus")
    built_corpus = corpus.build_corpus(
        confluence_doc=cf_doc, pdf_doc=pdf_doc, pdf_title=cfg.pdf_path.stem
    )
    LOG.info("Corpus units: %d", len(built_corpus.units))

    hashes = {
        "confluence_json_sha256": hashing.file_sha256(cfg.confluence_json),
        "pdf_sha256": hashing.file_sha256(cfg.pdf_path),
    }

    # Сохраняем корпус для воспроизводимости.
    cfg.output_dir.mkdir(parents=True, exist_ok=True)
    corpus.save_corpus(built_corpus, cfg.corpus_path())

    # Эмбеддинги. При сбое клиента — ошибка и остановка.
    LOG.info("Initializing Ollama client at %s", cfg.ollama_url)
    client = embeddings.OllamaClient(
        cfg.ollama_url,
        timeout=cfg.embed_timeout,
        max_retries=cfg.embed_max_retries,
        retry_base_delay=cfg.embed_retry_base_delay,
    )
    if not client.health():
        raise RuntimeError(
            f"Ollama is not reachable at {cfg.ollama_url}. "
            "Start `ollama serve` and verify the model is pulled."
        )

    # Считаем чанки для обеих стратегий.
    fixed_chunks = chunking.build_chunks(
        built_corpus.units,
        strategy="fixed",
        fixed_target_words=cfg.fixed_tokens,
        fixed_overlap_words=cfg.fixed_overlap,
        struct_max_chars=cfg.struct_max_chars,
        struct_min_chars=cfg.struct_min_chars,
    )
    structural_chunks = chunking.build_chunks(
        built_corpus.units,
        strategy="structural",
        fixed_target_words=cfg.fixed_tokens,
        fixed_overlap_words=cfg.fixed_overlap,
        struct_max_chars=cfg.struct_max_chars,
        struct_min_chars=cfg.struct_min_chars,
    )

    LOG.info("Chunks: fixed=%d, structural=%d", len(fixed_chunks), len(structural_chunks))

    file_paths = {
        "confluence": str(cfg.confluence_json),
        "pdf": str(cfg.pdf_path),
    }

    provenance_records = {
        "confluence": (
            str(cfg.confluence_json),
            hashes["confluence_json_sha256"],
            cf_doc.storage_chars + cf_doc.view_chars,
            {
                "page_id": cf_doc.page_id,
                "title": cf_doc.title,
                "version": cf_doc.version,
                "coverage": cf_doc.coverage,
            },
        ),
        "pdf": (
            str(cfg.pdf_path),
            hashes["pdf_sha256"],
            pdf_doc.raw_char_count,
            {"pages": len(pdf_doc.pages), "title_hint": pdf_doc.title_hint},
        ),
    }

    # Эмбеддинги батчами.
    fixed_vectors = _embed_chunks(client, fixed_chunks, cfg, label="fixed")
    struct_vectors = _embed_chunks(client, structural_chunks, cfg, label="structural")

    fixed_dim = len(fixed_vectors[0]) if fixed_vectors else 0
    struct_dim = len(struct_vectors[0]) if struct_vectors else 0

    # Сборка ведётся по одной стратегии за раз. Если вторая упадёт —
    # пользователь всё равно увидит первый валидный индекс; его staging
    # уже опубликован на этом этапе.
    _build_one_strategy(
        cfg=cfg,
        strategy="fixed",
        chunks=fixed_chunks,
        embeddings_list=fixed_vectors,
        cf_doc=cf_doc,
        pdf_doc=pdf_doc,
        hashes=hashes,
        file_paths=file_paths,
        provenance_records=provenance_records,
    )

    _build_one_strategy(
        cfg=cfg,
        strategy="structural",
        chunks=structural_chunks,
        embeddings_list=struct_vectors,
        cf_doc=cf_doc,
        pdf_doc=pdf_doc,
        hashes=hashes,
        file_paths=file_paths,
        provenance_records=provenance_records,
    )

    metrics = _compute_metrics(
        cfg=cfg,
        corpus_obj=built_corpus,
        cf_doc=cf_doc,
        pdf_doc=pdf_doc,
        hashes=hashes,
        fixed_chunks=fixed_chunks,
        struct_chunks=structural_chunks,
        fixed_dim=fixed_dim,
        struct_dim=struct_dim,
    )
    metrics["output"] = {
        "fixed_db": str(cfg.fixed_db()),
        "structural_db": str(cfg.structural_db()),
        "corpus_json": str(cfg.corpus_path()),
    }

    cfg.report_path().write_text(
        json.dumps(metrics, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    LOG.info("Report written: %s", cfg.report_path())
    return metrics


def _embed_chunks(
    client: embeddings.OllamaClient,
    chunks: List[chunking.Chunk],
    cfg: RunConfig,
    *,
    label: str,
) -> List[List[float]]:
    texts = [c.text for c in chunks]
    if not texts:
        return []
    LOG.info("Embedding %d chunks (%s) in batches of %d", len(texts), label, cfg.embed_batch_size)
    t0 = time.time()
    vectors = embeddings.embed_in_batches(
        client, texts, model=cfg.embed_model, batch_size=cfg.embed_batch_size
    )
    LOG.info("Embedded %d chunks (%s) in %.1fs", len(vectors), label, time.time() - t0)
    return vectors


def _compute_metrics(
    *,
    cfg: RunConfig,
    corpus_obj: corpus.Corpus,
    cf_doc,
    pdf_doc,
    hashes: Dict[str, str],
    fixed_chunks,
    struct_chunks,
    fixed_dim: int,
    struct_dim: int,
) -> dict:
    from . import text_utils as tu

    total_words_corpus = corpus_obj.total_words()

    # ``words`` для каждого источника считаем строго по индексированным
    # ``CorpusUnit.text``, сгруппированным по ``source``. Только так
    # выполняется равенство
    #     confluence.words + pdf.words == corpus.words.
    # Раньше использовались «сырые» ``cf_doc.total_words()`` и
    # ``pdf_doc.total_words()``, которые учитывали и заголовки секций,
    # и страницы PDF без текста; из-за этого сумма по источникам
    # превышала ``corpus.words`` (например, 10047 + 717 > 9799 за счёт
    # heading-ов Confluence, не вошедших в ``CorpusUnit.text``).
    raw_words_cf = cf_doc.total_words()
    raw_words_pdf = pdf_doc.total_words()

    indexed_words_cf = sum(
        tu.count_words(u.text) for u in corpus_obj.units if u.source == "confluence"
    )
    indexed_words_pdf = sum(
        tu.count_words(u.text) for u in corpus_obj.units if u.source == "pdf"
    )

    # Рекурсивный подсчёт секций (Section.walk обходит и вложенные
    # expand-ы), а также прямой top-level count для прозрачности —
    # прежнее число обозначало только корневые секции и было
    # заниженным (например, 2 при десятках expand-ов).
    sections_recursive = sum(1 for s in cf_doc.sections for _ in s.walk())
    sections_top_level = sum(1 for _ in cf_doc.sections)

    # Оценка страниц: ceil(words / 350) — формула в отчёте.
    # ``by_words_pdf_only`` теперь опирается на индексированную часть PDF,
    # чтобы согласоваться с ``pdf.words`` ниже.
    est_pages_word = tu.estimate_pages_from_words(total_words_corpus)
    est_pages_pdf_only = tu.estimate_pages_from_words(indexed_words_pdf)

    metrics = {
        "model": cfg.embed_model,
        "ollama_url": cfg.ollama_url,
        "hashes": hashes,
        "confluence": {
            "page_id": cf_doc.page_id,
            "title": cf_doc.title,
            "version": cf_doc.version,
            "storage_chars": cf_doc.storage_chars,
            "view_chars": cf_doc.view_chars,
            "storage_hash": cf_doc.storage_hash,
            "view_hash": cf_doc.view_hash,
            "coverage": cf_doc.coverage,
            "sections_total": sections_recursive,
            "sections_top_level": sections_top_level,
            "words": indexed_words_cf,
            "raw_words": raw_words_cf,
        },
        "pdf": {
            "path": str(cfg.pdf_path),
            "pages": len(pdf_doc.pages),
            "raw_char_count": pdf_doc.raw_char_count,
            "words": indexed_words_pdf,
            "raw_words": raw_words_pdf,
            "headings_detected": sum(1 for p in pdf_doc.pages if p.is_heading),
        },
        "corpus": {
            "units": len(corpus_obj.units),
            "words": total_words_corpus,
            "chars": corpus_obj.total_chars(),
        },
        "estimated_pages": {
            "formula": "ceil(words / 350)",
            "by_words_corpus": est_pages_word,
            "by_words_pdf_only": est_pages_pdf_only,
            "pdf_reported_pages": len(pdf_doc.pages),
        },
        "chunks": {
            "fixed": {
                "count": len(fixed_chunks),
                "avg_words": (
                    sum(c.word_count for c in fixed_chunks) / max(1, len(fixed_chunks))
                ),
                "min_words": min((c.word_count for c in fixed_chunks), default=0),
                "max_words": max((c.word_count for c in fixed_chunks), default=0),
                "dim": fixed_dim,
            },
            "structural": {
                "count": len(struct_chunks),
                "avg_chars": (
                    sum(c.char_count for c in struct_chunks) / max(1, len(struct_chunks))
                ),
                "min_chars": min((c.char_count for c in struct_chunks), default=0),
                "max_chars": max((c.char_count for c in struct_chunks), default=0),
                "dim": struct_dim,
            },
        },
    }
    return metrics


__all__ = ["build_index"]