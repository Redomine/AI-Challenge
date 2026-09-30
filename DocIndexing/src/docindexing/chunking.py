"""Две стратегии чанкования.

* ``fixed`` — по количеству слов с перекрытием.
* ``structural`` — по секциям: разбиваем крупные секции на окна,
  склеиваем мелкие в один чанк, но не превышаем лимит.

Каждый чанк получает стабильный ``chunk_id`` на основе SHA-256 от
нормализованного текста и метаданных. Это позволяет идемпотентно
перезапускать индексацию.

Fixed strategy: объединяем тексты последовательных юнитов одного
источника в общий поток и режем его скользящим окном
``target_words`` с перекрытием ``overlap_words``. Для Confluence
можно пересекать границы секций (это «настоящее» фиксированное
чанкование поверх корпуса). Для PDF границы страниц сохраняются
непересечёнными, чтобы ``pdf_page`` оставался валидной ground-truth
меткой; чанкование PDF идёт постранично. Метаданные чанка
(``section_path``) собирают все метки секций, через которые прошло
окно — детерминированно и без дубликатов.
"""

from __future__ import annotations

import hashlib
import re
from dataclasses import dataclass
from typing import List, Optional, Sequence, Tuple

from . import text_utils
from .corpus import CorpusUnit


@dataclass
class Chunk:
    """Единица индекса."""

    chunk_id: str
    strategy: str  # "fixed" | "structural"
    source: str  # "confluence" | "pdf"
    title: str
    section_path: List[str]
    text: str
    pdf_page: Optional[int] = None
    heading_level: int = 0
    word_count: int = 0
    char_count: int = 0


def _words(text: str) -> List[str]:
    """Разбить текст на слова (Unicode-aware)."""

    return re.findall(r"\S+", text)


def _make_chunk_id(strategy: str, source: str, title: str, idx: int, text: str) -> str:
    payload = f"{strategy}|{source}|{title}|{idx}|{text_utils.normalize_whitespace(text)}"
    h = hashlib.sha256(payload.encode("utf-8")).hexdigest()
    return f"{strategy}-{h[:16]}"


# ----------------------------------------------------------------------
# Fixed strategy
# ----------------------------------------------------------------------


def _concat_units_text(units: Sequence[CorpusUnit]) -> Tuple[str, List[Tuple[int, int, CorpusUnit]]]:
    """Склеить тексты юнитов в один поток.

    Между текстами соседних юнитов вставляется одиночный пробел, чтобы
    «крайние» слова юнита и начала следующего юнита не склеились в
    одно «слово». Возвращает пару ``(big_text, unit_spans)`` где
    ``unit_spans`` — список ``(start_word_idx, end_word_idx, unit)``
    с полуоткрытыми индексами слов в общем потоке.
    """

    big_parts: List[str] = []
    spans: List[Tuple[int, int, CorpusUnit]] = []
    total_words = 0
    for u in units:
        words = _words(u.text)
        if not words:
            # Пустые юниты не добавляют ни слов, ни секций — пропускаем.
            continue
        start = total_words
        big_parts.append(u.text)
        total_words += len(words)
        end = total_words
        spans.append((start, end, u))
    big_text = " ".join(big_parts)
    return big_text, spans


def _section_paths_for_window(
    unit_spans: Sequence[Tuple[int, int, CorpusUnit]],
    win_start: int,
    win_end: int,
) -> Tuple[List[str], str, int]:
    """Собрать метаданные для окна ``[win_start, win_end)``.

    Возвращает ``(section_path, title, heading_level)``:

    * ``section_path`` — детерминированная последовательность меток
      секций, через которые окно прошло. Сохраняет порядок появления,
      не содержит дубликатов, начинается с общей «корневой» части
      (самая длинная общая приставка первого и последнего юнита окна)
      и дополняется «хвостовыми» метками последующих юнитов.
    * ``title`` — заголовок первого юнита окна.
    * ``heading_level`` — уровень заголовка первого юнита окна.
    """

    overlapping = [u for s, e, u in unit_spans if not (e <= win_start or s >= win_end)]
    if not overlapping:
        return [], "", 0

    first = overlapping[0]
    last = overlapping[-1]

    # Находим общий префикс первого и последнего юнита.
    prefix: List[str] = []
    for a, b in zip(first.section_path, last.section_path):
        if a == b:
            prefix.append(a)
        else:
            break

    # Собираем хвостовые метки от каждого юнита, который пересёкся.
    collected: List[str] = []
    for u in overlapping:
        tail = u.section_path[len(prefix):]
        for label in tail:
            if not collected or collected[-1] != label:
                collected.append(label)

    section_path = prefix + collected
    return section_path, first.title, first.heading_level


def _build_fixed_chunks_for_stream(
    *,
    source: str,
    units: Sequence[CorpusUnit],
    target_words: int,
    overlap_words: int,
    base_index: int,
    keep_section_boundaries: bool,
) -> Tuple[List[Chunk], int]:
    """Собрать чанки фиксированного размера по потоку слов.

    ``keep_section_boundaries=True`` — каждый юнит чанкуется отдельно
    (используется для PDF, чтобы не пересекать границы страниц).
    ``keep_section_boundaries=False`` — тексты юнитов склеиваются и
    чанкуются скользящим окном по всему потоку.
    """

    if target_words <= 0:
        raise ValueError("target_words must be positive")
    if overlap_words < 0 or overlap_words >= target_words:
        raise ValueError("overlap_words must be in [0, target_words)")

    chunks: List[Chunk] = []
    idx = 0

    if keep_section_boundaries:
        # PDF: каждая страница — отдельный юнит, границы не пересекаем.
        for u in units:
            words = _words(u.text)
            if not words:
                continue
            unit_spans: List[Tuple[int, int, CorpusUnit]] = [
                (0, len(words), u)
            ]
            step = target_words - overlap_words
            pos = 0
            while pos < len(words):
                win_start, win_end = pos, min(pos + target_words, len(words))
                window = words[win_start:win_end]
                if not window:
                    break
                text = " ".join(window)
                section_path, title, level = _section_paths_for_window(
                    unit_spans, win_start, win_end
                )
                # PDF-страница — единственная ground-truth метка,
                # поэтому путь всегда начинается с этой страницы.
                cid = _make_chunk_id("fixed", source, title, base_index + idx, text)
                chunks.append(
                    Chunk(
                        chunk_id=cid,
                        strategy="fixed",
                        source=source,
                        title=title,
                        section_path=section_path,
                        text=text,
                        pdf_page=u.pdf_page,
                        heading_level=level,
                        word_count=len(window),
                        char_count=len(text),
                    )
                )
                idx += 1
                if win_end >= len(words):
                    break
                pos += step
        return chunks, idx

    # Confluence (или любой не-PDF источник): склеиваем поток и режем.
    big_text, unit_spans = _concat_units_text(units)
    if not unit_spans:
        return chunks, idx
    all_words = _words(big_text)
    if not all_words:
        return chunks, idx

    step = target_words - overlap_words
    pos = 0
    n_words = len(all_words)
    while pos < n_words:
        win_start = pos
        win_end = min(pos + target_words, n_words)
        window = all_words[win_start:win_end]
        if not window:
            break
        text = " ".join(window)
        section_path, title, level = _section_paths_for_window(
            unit_spans, win_start, win_end
        )
        cid = _make_chunk_id("fixed", source, title, base_index + idx, text)
        chunks.append(
            Chunk(
                chunk_id=cid,
                strategy="fixed",
                source=source,
                title=title,
                section_path=section_path,
                text=text,
                pdf_page=None,
                heading_level=level,
                word_count=len(window),
                char_count=len(text),
            )
        )
        idx += 1
        if win_end >= n_words:
            break
        pos += step
    return chunks, idx


def chunk_fixed(
    unit: CorpusUnit,
    *,
    target_words: int,
    overlap_words: int,
    base_index: int,
) -> List[Chunk]:
    """Разбить один юнит на чанки фиксированного размера с перекрытием.

    Сохранён для обратной совместимости со старыми тестами, которые
    вызывают ``chunk_fixed`` напрямую с одним юнитом. Внутри
    использует общий код скользящего окна. Границы юнита
    рассматриваются как «непересекаемые» (для PDF-страниц это
    естественно, для вызовов со стороны тестов — тоже корректно).
    """

    if target_words <= 0:
        raise ValueError("target_words must be positive")
    if overlap_words < 0 or overlap_words >= target_words:
        raise ValueError("overlap_words must be in [0, target_words)")

    if unit.source == "pdf":
        chunks, _ = _build_fixed_chunks_for_stream(
            source=unit.source,
            units=[unit],
            target_words=target_words,
            overlap_words=overlap_words,
            base_index=base_index,
            keep_section_boundaries=True,
        )
        return chunks

    # Confluence или иной источник: склеиваем один юнит (== весь поток).
    chunks, _ = _build_fixed_chunks_for_stream(
        source=unit.source,
        units=[unit],
        target_words=target_words,
        overlap_words=overlap_words,
        base_index=base_index,
        keep_section_boundaries=False,
    )
    return chunks


# ----------------------------------------------------------------------
# Structural strategy
# ----------------------------------------------------------------------


def _section_text(unit: CorpusUnit) -> str:
    """Собрать «полный» текст секции для structural-чанкера."""

    parts = []
    if unit.title:
        parts.append(unit.title)
    if unit.text:
        parts.append(unit.text)
    return text_utils.normalize_whitespace("\n".join(parts))


def _split_long_section(
    text: str,
    *,
    max_chars: int,
    min_chars: int,
) -> List[str]:
    """Разрезать длинный текст на куски ≤ ``max_chars``.

    Границы — абзацы, потом предложения. Если абзац длиннее ``max_chars``,
    режем по предложениям, потом по словам.
    """

    if len(text) <= max_chars:
        return [text]
    paragraphs = [p for p in text.split("\n\n") if p.strip()]
    parts: List[str] = []
    current = ""

    def flush() -> None:
        nonlocal current
        if current.strip():
            parts.append(current.strip())
        current = ""

    for para in paragraphs:
        if len(para) > max_chars:
            flush()
            sentences = text_utils.split_sentences(para)
            buf = ""
            for s in sentences:
                if len(buf) + len(s) + 1 > max_chars:
                    if buf.strip():
                        parts.append(buf.strip())
                    buf = s
                else:
                    buf = (buf + " " + s).strip()
            if buf.strip():
                parts.append(buf.strip())
            continue
        if len(current) + len(para) + 2 > max_chars:
            flush()
        current = (current + "\n\n" + para).strip()
    flush()

    # Если последний кусок слишком короткий и есть предыдущий — склеим.
    merged: List[str] = []
    buf = ""
    for part in parts:
        if len(part) < min_chars and buf:
            buf = (buf + "\n\n" + part).strip()
        elif len(part) < min_chars and not merged:
            buf = part
        else:
            if buf:
                merged.append(buf)
                buf = ""
            merged.append(part)
    if buf:
        merged.append(buf)

    return merged


def chunk_structural(
    units: Sequence[CorpusUnit],
    *,
    max_chars: int,
    min_chars: int,
) -> List[Chunk]:
    """Разбить секции по структуре (Confluence + PDF)."""

    chunks: List[Chunk] = []
    idx = 0
    for unit in units:
        text = _section_text(unit)
        if not text:
            continue
        pieces = _split_long_section(text, max_chars=max_chars, min_chars=min_chars)
        # Мелкие куски из разных юнитов можно склеить, но только если у них
        # один источник и совпадает первая часть section_path.
        # На практике корпус уже «секционный», так что просто эмитим чанки.
        for piece in pieces:
            cid = _make_chunk_id("structural", unit.source, unit.title, idx, piece)
            chunks.append(
                Chunk(
                    chunk_id=cid,
                    strategy="structural",
                    source=unit.source,
                    title=unit.title,
                    section_path=list(unit.section_path),
                    text=piece,
                    pdf_page=unit.pdf_page,
                    heading_level=unit.heading_level,
                    word_count=text_utils.count_words(piece),
                    char_count=len(piece),
                )
            )
            idx += 1
    return chunks


# ----------------------------------------------------------------------
# Public helpers
# ----------------------------------------------------------------------


def build_chunks(
    units: Sequence[CorpusUnit],
    *,
    strategy: str,
    fixed_target_words: int,
    fixed_overlap_words: int,
    struct_max_chars: int,
    struct_min_chars: int,
) -> List[Chunk]:
    """Универсальная фабрика чанков по имени стратегии.

    Для ``strategy == "fixed"``:
        * Confluence-юниты (и любые другие не-PDF) склеиваются в общий
          поток и чанкуются скользящим окном ``target_words`` с
          перекрытием ``overlap_words`` — это «настоящее»
          фиксированное чанкование поверх корпуса, где ``section_path``
          собирает все метки секций, через которые прошло окно.
        * PDF-юниты (каждый = одна страница) чанкуются отдельно, чтобы
          не пересекать границы страниц и сохранить ``pdf_page`` как
          валидную ground-truth метку. Чанки PDF идут после Confluence.

    Для ``strategy == "structural"`` поведение не меняется: каждый
    юнит превращается в один или несколько чанков фиксированного
    посимвольного размера.
    """

    if strategy == "fixed":
        out: List[Chunk] = []
        idx = 0

        # Сохраняем порядок юнитов из входа, но PDF-юниты чанкуем
        # постранично, чтобы не пересекать границы страниц и
        # сохранить ``pdf_page`` как валидную ground-truth метку.
        # Confluence-юниты склеиваются в общий поток и чанкуются
        # скользящим окном.
        cf_units = [u for u in units if u.source != "pdf"]
        if cf_units:
            cf_chunks, n = _build_fixed_chunks_for_stream(
                source=cf_units[0].source,
                units=cf_units,
                target_words=fixed_target_words,
                overlap_words=fixed_overlap_words,
                base_index=idx,
                keep_section_boundaries=False,
            )
            out.extend(cf_chunks)
            idx += n

        pdf_units = [u for u in units if u.source == "pdf"]
        if pdf_units:
            pdf_chunks, n = _build_fixed_chunks_for_stream(
                source="pdf",
                units=pdf_units,
                target_words=fixed_target_words,
                overlap_words=fixed_overlap_words,
                base_index=idx,
                keep_section_boundaries=True,
            )
            out.extend(pdf_chunks)
            idx += n

        return out
    if strategy == "structural":
        return chunk_structural(
            units, max_chars=struct_max_chars, min_chars=struct_min_chars
        )
    raise ValueError(f"Unknown strategy: {strategy!r}")


__all__ = [
    "Chunk",
    "chunk_fixed",
    "chunk_structural",
    "build_chunks",
    "_split_long_section",
]