"""Извлечение текста PDF с номерами страниц и поиском заголовков.

Используется :mod:`pypdf`, потому что:
    * имеет чистый Python-API без внешних бинарников;
    * отдаёт текст постранично (``Page.extract_text``);
    * корректно работает с PDF 1.x — 2.0.

Если текста нет (сканы без OCR), инструмент падает с понятной ошибкой.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import List, Optional

from . import text_utils


_HEADING_PATTERNS = [
    # Жирные фрагменты в начале строки (heuristic).
    re.compile(r"^([А-ЯA-Z][^\n]{2,80})$"),
]


@dataclass
class PdfPage:
    """Текст одной страницы с метаданными."""

    page_number: int  # 1-based
    text: str
    is_heading: bool = False
    heading_level: int = 0


@dataclass
class PdfDoc:
    """Результат разбора PDF."""

    path: Path
    pages: List[PdfPage] = field(default_factory=list)
    raw_char_count: int = 0
    title_hint: Optional[str] = None

    def total_words(self) -> int:
        return sum(text_utils.count_words(p.text) for p in self.pages)

    def headings(self) -> List[PdfPage]:
        return [p for p in self.pages if p.is_heading]


def _looks_like_heading(line: str) -> Optional[int]:
    """Грубая эвристика: строка — заголовок?

    Возвращает предполагаемый уровень (1–3) или ``None``.
    """

    s = line.strip()
    if not s or len(s) > 120:
        return None
    # Числовой заголовок "1.", "1.2", "Глава 3".
    if re.match(r"^(?:Глава|Часть|Chapter|Раздел)\s+\d+", s, re.IGNORECASE):
        return 1
    if re.match(r"^\d+(?:\.\d+){0,2}\.?\s+\S", s):
        depth = s.split(".", 1)[0].count(".")
        return min(depth + 1, 3)
    # Заголовок не заканчивается точкой — это почти всегда предложение.
    if s.endswith("."):
        return None
    # Короткая строка, начинается с заглавной буквы, без точки в конце.
    if s[0].isupper() and len(s) <= 80:
        # Только если в строке мало знаков препинания — это «шапка».
        if sum(1 for c in s if c in ",;:") <= 1:
            return 2
    return None


def extract_pdf(path: Path) -> PdfDoc:
    """Распарсить PDF и вернуть страницы с заголовками.

    Бросает :class:`RuntimeError`, если ни одна страница не содержит
    текста — это сигнал, что требуется OCR.
    """

    try:
        from pypdf import PdfReader  # type: ignore[import-not-found]
    except ImportError as e:  # pragma: no cover - зависимость объявлена в requirements
        raise RuntimeError(
            "pypdf is required for PDF parsing. Install via: py -m pip install pypdf"
        ) from e

    reader = PdfReader(str(path))
    pages: List[PdfPage] = []
    raw_chars = 0

    for idx, page in enumerate(reader.pages, start=1):
        try:
            text = page.extract_text() or ""
        except Exception as exc:  # pypdf иногда падает на шифрованых страницах
            text = ""
            last_error = exc
        else:
            last_error = None
        text = text_utils.normalize_whitespace(text)
        raw_chars += len(text)
        level = _looks_like_heading(text.split("\n", 1)[0]) if text else None
        is_heading = level is not None
        # Если текст маленький — это может быть шапка/футер без контента.
        # Сохраняем, но не считаем заголовком, если не сработала эвристика.
        pages.append(
            PdfPage(
                page_number=idx,
                text=text,
                is_heading=is_heading,
                heading_level=level or 0,
            )
        )

    if raw_chars == 0:
        raise RuntimeError(
            "PDF text extraction returned 0 characters. "
            "File is likely a scan without OCR. "
            "Run OCR (e.g. `ocrmypdf`) and retry."
        )

    title_hint = None
    for p in pages[:3]:
        if p.text:
            title_hint = p.text.split("\n", 1)[0].strip()[:200]
            break

    if all(not p.text for p in pages):
        raise RuntimeError(
            "PDF parsed but every page is empty. OCR is required."
        )

    return PdfDoc(path=path, pages=pages, raw_char_count=raw_chars, title_hint=title_hint)


__all__ = ["PdfDoc", "PdfPage", "extract_pdf"]
