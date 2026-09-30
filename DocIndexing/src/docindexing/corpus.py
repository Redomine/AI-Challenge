"""Сборка единого корпуса из Confluence + PDF.

Корпус — это сериализуемая структура, которая потом превращается в чанки.
Каждой «единице» соответствует заголовок, текст и метаданные.
"""

from __future__ import annotations

import json
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Dict, List, Optional

from .confluence import ConfluenceDoc, Section
from .pdf_text import PdfDoc


@dataclass
class CorpusUnit:
    """Одна логическая единица текста в корпусе."""

    unit_id: str  # стабильный идентификатор unit внутри источника
    source: str  # "confluence" | "pdf"
    title: str
    section_path: List[str]  # путь из заголовков от корня
    text: str
    pdf_page: Optional[int] = None
    heading_level: int = 0


@dataclass
class Corpus:
    """Готовый корпус, который подаётся в чанкер."""

    sources: Dict[str, dict] = field(default_factory=dict)
    units: List[CorpusUnit] = field(default_factory=list)

    def total_words(self) -> int:
        from . import text_utils

        return sum(text_utils.count_words(u.text) for u in self.units)

    def total_chars(self) -> int:
        return sum(len(u.text) for u in self.units)


def _confluence_unit_id(
    page_id: str, section_path: List[str], *, occurrence: int
) -> str:
    """Стабильный идентификатор юнита Confluence с ординалом вхождения.

    Повторяющиеся expand-сиблинги с одинаковым ``section_path`` (например,
    ``[Heading, [expand] Подробности]`` дважды подряд) дают одинаковый
    «логический» путь. Если бы идентификатор строился только из пути, оба
    юнита перезаписали бы друг друга в SQLite (``INSERT OR REPLACE`` по
    ``chunk_id``). Чтобы этого не происходило, добавляем детерминированный
    ординал вхождения: ``cf:<page_id>:<path>#<n>``.

    ``occurrence`` нумерация начинается с 1. Для первого юнита с данным
    ``section_path`` это ``#1``, для второго — ``#2`` и т.д. Номер
    определяется порядком обхода DFS дерева секций, который детерминирован.
    """

    safe = "/".join(s.strip().replace(" ", "_") for s in section_path if s)
    base = f"cf:{page_id}:{safe or 'root'}"
    return f"{base}#{occurrence}"


def _confluence_units(doc: ConfluenceDoc) -> List[CorpusUnit]:
    out: List[CorpusUnit] = []
    # Счётчик числа юнитов с тем же section_path, чтобы добавить ординал
    # в идентификатор. Это критично для repeated expand-сиблингов с
    # одинаковым путём: без ординала они перезаписывали бы друг друга
    # в SQLite, потому что ``chunk_id`` деривится из ``unit_id``.
    path_counts: Dict[tuple, int] = {}

    def walk(sections: List[Section], path: List[str]) -> None:
        for s in sections:
            section_path = path + [s.title]
            text = "\n".join(b.text for b in s.blocks if b.text).strip()
            if text:
                key = tuple(section_path)
                path_counts[key] = path_counts.get(key, 0) + 1
                out.append(
                    CorpusUnit(
                        unit_id=_confluence_unit_id(
                            doc.page_id, section_path,
                            occurrence=path_counts[key],
                        ),
                        source="confluence",
                        title=s.title,
                        section_path=section_path,
                        text=text,
                        heading_level=s.level,
                    )
                )
            walk(s.children, section_path)

    walk(doc.sections, [])
    return out


def _pdf_units(doc: PdfDoc, *, source_title: str) -> List[CorpusUnit]:
    out: List[CorpusUnit] = []
    for page in doc.pages:
        if not page.text.strip():
            continue
        title = source_title
        path = [source_title, f"Страница {page.page_number}"]
        if page.is_heading:
            # Если есть заголовок — используем его как заголовок секции.
            heading_text = page.text.split("\n", 1)[0].strip()
            title = heading_text
            path = [source_title, f"Страница {page.page_number}", heading_text]
        out.append(
            CorpusUnit(
                unit_id=f"pdf:{doc.path.name}:p{page.page_number}",
                source="pdf",
                title=title,
                section_path=path,
                text=page.text,
                pdf_page=page.page_number,
                heading_level=page.heading_level or 0,
            )
        )
    return out


def build_corpus(
    *,
    confluence_doc: ConfluenceDoc,
    pdf_doc: PdfDoc,
    pdf_title: str,
) -> Corpus:
    """Собрать корпус из обоих источников."""

    units = _confluence_units(confluence_doc) + _pdf_units(
        pdf_doc, source_title=pdf_title or pdf_doc.path.stem
    )
    sources = {
        "confluence": {
            "page_id": confluence_doc.page_id,
            "title": confluence_doc.title,
            "version": confluence_doc.version,
            "storage_hash": confluence_doc.storage_hash,
            "view_hash": confluence_doc.view_hash,
            "storage_chars": confluence_doc.storage_chars,
            "view_chars": confluence_doc.view_chars,
            "coverage": confluence_doc.coverage,
        },
        "pdf": {
            "path": str(pdf_doc.path),
            "pages": len(pdf_doc.pages),
            "raw_char_count": pdf_doc.raw_char_count,
            "title_hint": pdf_doc.title_hint,
        },
    }
    return Corpus(sources=sources, units=units)


def save_corpus(corpus: Corpus, path: Path) -> None:
    """Сохранить корпус в JSON (без эмбеддингов, только текст)."""

    payload = {
        "sources": corpus.sources,
        "units": [asdict(u) for u in corpus.units],
    }
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")


def load_corpus(path: Path) -> Corpus:
    """Загрузить корпус из JSON (для отладки/перезапуска)."""

    data = json.loads(path.read_text(encoding="utf-8"))
    units = [CorpusUnit(**u) for u in data["units"]]
    return Corpus(sources=data["sources"], units=units)


__all__ = ["Corpus", "CorpusUnit", "build_corpus", "save_corpus", "load_corpus"]
