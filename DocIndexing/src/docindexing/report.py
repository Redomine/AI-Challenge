"""Сравнительный отчёт двух стратегий по набору вопросов с ground-truth.

Старая версия считала Jaccard по ``chunk_id`` между стратегиями — это было
бессмысленно, потому что ``chunk_id`` детерминирован от стратегии
(``fixed-<sha>``/``structural-<sha>``) и пересечение по сути случайно либо
нулевое. Здесь вместо этого:

* вопрос — либо объект ``{question, expected_source, expected_section_contains,
  expected_pdf_page?optional}``, либо обычная строка;
* для каждого вопроса берётся top-k из обеих стратегий (реальные чанки с
  метаданными ``source``, ``section``, ``pdf_page``);
* для объектов-вопросов считается hit@k (1, 3, 5 и т.д.) — был ли
  хотя бы один подходящий чанк в первых k результатах;
* для строковых вопросов мы НЕ выдумываем метки релевантности и просто
  возвращаем списки top-k без оценок качества;
* дополнительно считаются метрики корпуса из build_report (если есть) и
  напрямую из SQLite (количество чанков, размер БД) — это даёт контекст
  для сравнения стратегий.

Метрики hit@k осмыслены только если в файле вопросов есть ``expected_*``.
Если ни один вопрос не имеет ground-truth, отчёт всё равно пишется, но
``quality_claims=False`` и ``aggregated`` пустой.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, List, Mapping, Optional, Sequence

from .config import RunConfig
from .embeddings import OllamaClient
from .index_store import IndexStore
from .query import query_index


# Поля ground-truth, которые распознаём в вопросах-объектах.
_GT_FIELDS = ("expected_source", "expected_section_contains", "expected_pdf_page")


@dataclass
class NormalizedQuestion:
    """Вопрос после нормализации: либо помечен как ground-truth, либо нет."""

    question: str
    has_ground_truth: bool
    expected_source: Optional[str] = None
    expected_section_contains: Optional[str] = None
    expected_pdf_page: Optional[int] = None


def _normalize_question(raw: Any) -> NormalizedQuestion:
    """Превратить один элемент списка вопросов в ``NormalizedQuestion``.

    Поддерживаем два формата:

    * строка — без ground-truth (``has_ground_truth=False``);
    * объект с ключом ``question`` (и опциональными ``expected_*``) — с ground-truth.
    """

    if isinstance(raw, str):
        return NormalizedQuestion(question=raw, has_ground_truth=False)
    if isinstance(raw, Mapping):
        if "question" not in raw:
            raise ValueError(
                "question object must contain 'question' field, "
                f"got keys: {sorted(raw.keys())}"
            )
        question = str(raw["question"])
        expected_source = raw.get("expected_source")
        expected_section_contains = raw.get("expected_section_contains")
        expected_pdf_page = raw.get("expected_pdf_page")
        if expected_pdf_page is not None:
            try:
                expected_pdf_page = int(expected_pdf_page)
            except (TypeError, ValueError) as e:
                raise ValueError(
                    f"expected_pdf_page must be int, got {expected_pdf_page!r}"
                ) from e
        # Ground-truth считается заданным, если есть хотя бы один фильтр.
        has_gt = any(
            raw.get(k) is not None for k in _GT_FIELDS
        )
        return NormalizedQuestion(
            question=question,
            has_ground_truth=has_gt,
            expected_source=(
                str(expected_source) if expected_source is not None else None
            ),
            expected_section_contains=(
                str(expected_section_contains)
                if expected_section_contains is not None
                else None
            ),
            expected_pdf_page=expected_pdf_page,
        )
    raise ValueError(
        f"unsupported question item type: {type(raw).__name__}; "
        "must be str or mapping with 'question'"
    )


def _read_questions(path: Path) -> List[NormalizedQuestion]:
    """Прочитать JSON со списком вопросов.

    Допустимые формы:

    * ``["вопрос 1", ...]``;
    * ``{"questions": [...]}``;
    * объекты-вопросы с полем ``question`` и опциональными ``expected_*``.

    Объекты без ``expected_*`` валидны и будут интерпретированы как plain-строки
    (без оценок качества).
    """

    data = json.loads(path.read_text(encoding="utf-8"))
    items: Iterable[Any]
    if isinstance(data, list):
        items = data
    elif isinstance(data, dict) and "questions" in data:
        items = data["questions"]
    else:
        raise ValueError(
            "questions.json must be a list or {'questions': [...]}"
        )
    return [_normalize_question(it) for it in items]


# ---------------------------------------------------------------------
# Метрики и оценка
# ---------------------------------------------------------------------


def _hit_for_hit(
    hit: Mapping[str, Any],
    *,
    expected_source: Optional[str],
    expected_section_contains: Optional[str],
    expected_pdf_page: Optional[int],
) -> bool:
    """Подходит ли чанк под ground-truth.

    Все непустые фильтры должны сойтись. Поле ``section`` сравнивается
    как подстрока (case-insensitive) — оно хранит ``section_path`` через
    ``/``. ``source`` сравнивается точно (``confluence``/``pdf``).
    ``pdf_page`` сравнивается точно по числу.
    """

    if expected_source is not None and hit.get("source") != expected_source:
        return False
    if expected_pdf_page is not None and hit.get("pdf_page") != expected_pdf_page:
        return False
    if expected_section_contains is not None:
        section = (hit.get("section") or "").lower()
        needle = expected_section_contains.lower()
        if needle not in section:
            return False
    return True


def _first_relevant_rank(
    hits: Sequence[Mapping[str, Any]],
    *,
    expected_source: Optional[str],
    expected_section_contains: Optional[str],
    expected_pdf_page: Optional[int],
) -> Optional[int]:
    """Позиция первого подходящего чанка (1-based) или ``None``."""

    for i, h in enumerate(hits, start=1):
        if _hit_for_hit(
            h,
            expected_source=expected_source,
            expected_section_contains=expected_section_contains,
            expected_pdf_page=expected_pdf_page,
        ):
            return i
    return None


def _hit_at_k_for_field(
    per_question: Sequence[Mapping[str, Any]],
    *,
    field: str,
    k: int,
) -> Optional[float]:
    """hit@k для указанной стратегии (``field`` = ``first_relevant_rank_fixed``/``_structural``).

    Учитываются только вопросы с ground-truth. Возвращает ``None``, если
    таких вопросов нет.
    """

    eligible = [q for q in per_question if q.get("has_ground_truth")]
    if not eligible:
        return None
    hits = 0
    for q in eligible:
        rank = q.get(field)
        if rank is not None and rank <= k:
            hits += 1
    return hits / len(eligible)


# ---------------------------------------------------------------------
# Метрики хранилища
# ---------------------------------------------------------------------


def _db_size_bytes(path: Path) -> Optional[int]:
    """Размер ``<db>`` + ``<db>-wal``, если файлы существуют."""

    if not path.exists():
        return None
    total = path.stat().st_size
    wal = path.with_name(path.name + "-wal")
    if wal.exists():
        total += wal.stat().st_size
    return total


def _index_summary(store: IndexStore, db_path: Path) -> dict:
    """Базовая сводка по одной SQLite-БД (без обращения к эмбеддингам)."""

    chunk_count = store.count_chunks()
    missing = store.chunks_without_embedding()
    embed_dim = store.stored_embed_dim()
    embed_model = store.get_meta("embed_model")
    size_bytes = _db_size_bytes(db_path)
    return {
        "db_path": str(db_path),
        "exists": db_path.exists(),
        "size_bytes": size_bytes,
        "chunk_count": chunk_count,
        "chunks_without_embedding": missing,
        "embed_dim": embed_dim,
        "embed_model": embed_model,
    }


def _load_build_report(output_dir: Path) -> Optional[dict]:
    """Подтянуть ``build_report.json``, если он есть рядом с индексами."""

    path = output_dir / "build_report.json"
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


# ---------------------------------------------------------------------
# Главная точка входа
# ---------------------------------------------------------------------


def run_comparison(
    cfg: RunConfig,
    *,
    questions: Sequence[NormalizedQuestion],
    top_k: int,
    ollama_client: OllamaClient,
) -> dict:
    """Сравнить две стратегии на одном наборе вопросов.

    ``questions`` принимает уже нормализованные вопросы — это позволяет
    тестам не ходить в файловую систему. Для CLI чтение файла делает
    :func:`run_compare_cli`.
    """

    fixed_db = cfg.fixed_db()
    struct_db = cfg.structural_db()
    fixed_store = IndexStore(fixed_db)
    struct_store = IndexStore(struct_db)
    try:
        index_info = {
            "fixed": _index_summary(fixed_store, fixed_db),
            "structural": _index_summary(struct_store, struct_db),
        }
        build_report = _load_build_report(cfg.output_dir)

        per_question: List[dict] = []
        for q in questions:
            f_hits = query_index(
                cfg,
                question=q.question,
                strategy="fixed",
                top_k=top_k,
                ollama_client=ollama_client,
                store=fixed_store,
            )
            s_hits = query_index(
                cfg,
                question=q.question,
                strategy="structural",
                top_k=top_k,
                ollama_client=ollama_client,
                store=struct_store,
            )

            entry: dict = {
                "question": q.question,
                "has_ground_truth": q.has_ground_truth,
                "fixed": f_hits,
                "structural": s_hits,
            }
            if q.has_ground_truth:
                entry["expected"] = {
                    "source": q.expected_source,
                    "section_contains": q.expected_section_contains,
                    "pdf_page": q.expected_pdf_page,
                }
                rank_f = _first_relevant_rank(
                    f_hits,
                    expected_source=q.expected_source,
                    expected_section_contains=q.expected_section_contains,
                    expected_pdf_page=q.expected_pdf_page,
                )
                rank_s = _first_relevant_rank(
                    s_hits,
                    expected_source=q.expected_source,
                    expected_section_contains=q.expected_section_contains,
                    expected_pdf_page=q.expected_pdf_page,
                )
                entry["first_relevant_rank_fixed"] = rank_f
                entry["first_relevant_rank_structural"] = rank_s
            per_question.append(entry)

        quality_claims = any(q.has_ground_truth for q in questions)
        summary: dict = {
            "questions": len(questions),
            "questions_with_ground_truth": sum(
                1 for q in questions if q.has_ground_truth
            ),
            "top_k": top_k,
            "model": cfg.embed_model,
            "output_dir": str(cfg.output_dir),
            "index_info": index_info,
            "build_report_present": build_report is not None,
            "quality_claims": quality_claims,
            "per_question": per_question,
        }

        # Сводка по hit@k — есть только если есть ground-truth.
        if quality_claims:
            # Подбираем набор k: 1, 3, 5 (если влезает в top_k) и сам top_k.
            ks_set: set[int] = {1, top_k}
            if top_k >= 3:
                ks_set.add(3)
            if top_k >= 5:
                ks_set.add(5)
            ks = sorted(k for k in ks_set if k >= 1)
            aggregated: dict = {}
            for k in ks:
                aggregated[f"hit_at_{k}_fixed"] = _hit_at_k_for_field(
                    per_question, field="first_relevant_rank_fixed", k=k
                )
                aggregated[f"hit_at_{k}_structural"] = _hit_at_k_for_field(
                    per_question, field="first_relevant_rank_structural", k=k
                )
            summary["aggregated"] = aggregated
            summary["notes"] = (
                "hit@k считается только по вопросам с ground-truth. "
                "Ранг — позиция первого подходящего чанка в top-k (1-based). "
                "Если подходящих чанков в top-k не оказалось, ранга и hit'a нет "
                "(None). Для строковых вопросов без ground-truth оценки "
                "качества не вычисляются."
            )
        else:
            summary["aggregated"] = {}
            summary["notes"] = (
                "В файле вопросов нет ground-truth. Возвращены только списки "
                "top-k из обеих стратегий; оценки качества не вычисляются."
            )

        # Размерные метрики из build_report, если получилось его прочитать.
        if build_report is not None:
            chunks = build_report.get("chunks", {})
            summary["build_report_summary"] = {
                "chunks_fixed_count": chunks.get("fixed", {}).get("count"),
                "chunks_structural_count": chunks.get("structural", {}).get("count"),
                "model": build_report.get("model"),
            }
        return summary
    finally:
        fixed_store.close()
        struct_store.close()


def run_compare_cli(args) -> dict:
    """Реализация команды ``compare`` CLI."""

    from .config import build_config_from_args

    cfg = build_config_from_args(args)
    questions_path = Path(args.questions)
    if not questions_path.exists():
        raise FileNotFoundError(f"Questions file not found: {questions_path}")
    questions = _read_questions(questions_path)

    client = OllamaClient(
        cfg.ollama_url,
        timeout=cfg.embed_timeout,
        max_retries=cfg.embed_max_retries,
        retry_base_delay=cfg.embed_retry_base_delay,
    )
    if not client.health():
        raise RuntimeError(f"Ollama is not reachable at {cfg.ollama_url}")
    summary = run_comparison(
        cfg, questions=questions, top_k=args.top_k, ollama_client=client
    )
    out_path = Path(args.output)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(
        json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"Comparison written: {out_path}")
    return summary


__all__ = [
    "NormalizedQuestion",
    "run_comparison",
    "run_compare_cli",
    "_read_questions",
    "_normalize_question",
    "_hit_for_hit",
    "_first_relevant_rank",
]
