"""Утилиты нормализации и подсчёта слов/символов.

Все метрики — детерминированные и локальные, без обращения к модели.
"""

from __future__ import annotations

import re
from typing import Iterable, List

# Unicode-классы для токенизации, эквивалентной \p{L}\p{N}.
_WORD_RE = re.compile(r"[0-9A-Za-z_]+|[^\W\d_]+", re.UNICODE)
# Паттерн для подсчёта слов «по-человечески»: буквенно-цифровые последовательности.
# Кириллица и латиница входят в \w при флаге UNICODE.
_HUMAN_WORD_RE = re.compile(r"\w+", re.UNICODE)


def normalize_whitespace(text: str) -> str:
    """Схлопнуть повторные пробелы/переносы в одиночные и обрезать края.

    Контракт:
        * Несколько пробелов → один пробел.
        * 3+ перевода строки → ровно 2 (``\\n\\n``).
        * Табы и спецпробелы → обычный пробел.
        * Края каждой строки обрезаются.
    """

    if not text:
        return ""
    # Заменяем все виды пробелов (включая NBSP) на обычный пробел.
    text = re.sub(r"[\u00A0\u202F\u2009\u2007\u2008]", " ", text)
    # Сводим любой управляющий пробельный символ к одиночному переводу строки.
    text = re.sub(r"[\r\t\f\v]+", " ", text)
    text = re.sub(r"\n{3,}", "\n\n", text)
    text = re.sub(r"[ \t]{2,}", " ", text)
    # Обрезаем края каждой строки.
    lines = [line.strip() for line in text.split("\n")]
    text = "\n".join(lines)
    return text.strip()


def count_words(text: str) -> int:
    """Подсчитать количество слов (Unicode-aware)."""

    if not text:
        return 0
    return len(_HUMAN_WORD_RE.findall(text))


def estimate_pages_from_words(word_count: int, words_per_page: int = 350) -> int:
    """Оценить число страниц по количеству слов.

    Формула фиксированная и описана в отчёте сборки:
    ``pages = ceil(words / words_per_page)``.
    """

    if word_count <= 0:
        return 0
    return (word_count + words_per_page - 1) // words_per_page


def estimate_pages_from_chars(char_count: int, chars_per_page: int = 1800) -> int:
    """Запасная оценка по символам, если слов нет (например, формулы)."""

    if char_count <= 0:
        return 0
    return (char_count + chars_per_page - 1) // chars_per_page


def split_sentences(text: str) -> List[str]:
    """Грубая сегментация по знакам препинания.

    Используется только в стратегии fixed как «безопасные границы», чтобы
    перекрытие не разрезало слова посередине.
    """

    if not text:
        return []
    # Сохраняем разделители, чтобы потом собрать обратно.
    parts = re.split(r"(?<=[.!?…])\s+|(?<=\n)\s*", text)
    return [p for p in parts if p and p.strip()]


def join_segments(segments: Iterable[str]) -> str:
    """Склеить сегменты одиночными переводами строк."""

    return "\n".join(s for s in segments if s)


__all__ = [
    "normalize_whitespace",
    "count_words",
    "estimate_pages_from_words",
    "estimate_pages_from_chars",
    "split_sentences",
    "join_segments",
]
