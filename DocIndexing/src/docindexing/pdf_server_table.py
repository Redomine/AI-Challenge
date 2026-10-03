"""Удаление дублирующих таблиц «сервер ↔ проект» из текста PDF.

Когда HTML-выгрузка Confluence (структурный источник ``confluence_html``)
поставляется вместе с PDF, последний часто содержит те же таблицы
сопоставления «проект ↔ сервер» — но в виде плоского текста, в котором
заголовки колонок и коды проектов идут в линейном порядке. Это приводит
к неоднозначным чанкам: например, ``STLB-OK1`` оказывается в одном
чанке с ``revit-702 revit-703 revit-704``, и индекс «не знает», к
какому из серверов относится проект.

Этот модуль решает задачу консервативно: он детектирует в тексте PDF
«блоки серверных таблиц» и удаляет только те из них, чей набор колонок
(``revit-XXX``) уже покрыт HTML-выгрузкой. Остальной PDF-контент
(вступления, инструкции, RSN.ini, «Что делать, если…» и т.п.) не
трогается.

Детекция блока
==============

Блок серверной таблицы — это непрерывный диапазон строк, удовлетворяющий
всем условиям:

1. **Стартовая строка**: либо строка с двумя и более токенами
   ``revit-NNN`` (трёхзначный цифровой суффикс), либо строка
   ``Наименование`` / ``Сервера revit-XXX …``, идущая сразу перед
   такой строкой. Это позволяет корректно ловить «разорванные»
   заголовки вида ``Сервера revit-702 revit-703 revit-704`` (когда
   ``Сервера`` идёт в одной строке, а сами заголовки — в следующей).
2. **Содержимое**: внутри блока есть хотя бы одна «годовая» строка
   вида ``Проекты YYYY`` (опционально — с суффиксом, например,
   ``Проекты 2024 -`` или ``Проекты 2024 LAGL-DS1``).
3. **Конец**: блок заканчивается на одной из границ:

   * следующая стартовая строка (новая таблица);
   * строка заголовка раздела (``Что делать…``, ``Введение``,
     ``Где находится мой проект?``, ``Общие серверы`` и т.п.);
   * конец страницы.

Блок удаляется, только если множество его ``revit-XXX`` колонок —
подмножество колонок HTML-выгрузки. Это сохраняет «уникальные» таблицы
PDF (если такие когда-нибудь появятся) и не трогает «контекстные»
упоминания одного сервера вне таблиц.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Iterable, List, Sequence, Set, Tuple


# ----------------------------------------------------------------------
# Регулярные выражения
# ----------------------------------------------------------------------

# Один токен ``revit-NNN`` (NNN — три цифры).
_REVIT_TOKEN_RE = re.compile(r"\brevit-(\d{3})\b", re.IGNORECASE)

# Годовая строка: «Проекты 2022», «Проекты 2024», «Проекты 2024 -»,
# «Проекты 2024 LAGL-DS1» и т.п. Число года обязательно; остальное —
# это либо суффикс, либо первая запись строки, идущая после пробела.
_YEAR_ROW_RE = re.compile(r"^\s*Проекты?\s+\d{4}\b", re.IGNORECASE)

# Префикс таблицы: «Наименование» / «Сервера revit-XXX ...» / одна
# строка с двумя и более revit-токенами. Используется ТОЛЬКО как
# маркер начала блока.
_TABLE_TITLE_RE = re.compile(
    r"^\s*(?:Наименование|Сервер[аы]?|Название)\b",
    re.IGNORECASE,
)

# Заголовки разделов, прерывающие таблицу. Список намеренно
# консервативный: это реальные заголовки из выгрузки. Если в
# PDF-странице встретится неизвестный заголовок — блок не
# «отрезается» раньше времени, и может остаться «лишний» текст.
_SECTION_HEADING_RE = re.compile(
    r"^(?:"  # non-capturing alternation
    r"Введение|"
    r"Где\s+находится\s+мой\s+проект\??|"
    r"Общие\s+серверы|"
    r"Проектный\s+институт|"
    r"Бюро\s+комплексного\s+проектирования(?:\s+социальных\s+объектов)?|"
    r"Что\s+делать,?\s+если\s+|"
    r"Заключение|"
    r"Создал\(|"
    r"СОД\.|"
    r"https?://|"
    r"\d{2}\.\d{2}\.\d{4},?\s+\d{2}:\d{2}"  # footer «30.09.2026, 09:52 …»
    r")\b",
    re.IGNORECASE,
)


# ----------------------------------------------------------------------
# Контракт результата
# ----------------------------------------------------------------------


@dataclass(frozen=True)
class StrippedBlock:
    """Информация об одном удалённом блоке серверной таблицы."""

    page_number: int
    start_line: int  # inclusive (0-based)
    end_line: int  # exclusive
    columns: Tuple[str, ...]  # revit-XXX в исходном порядке
    matched_html: bool  # True, если блок удалён (== subset HTML)


@dataclass(frozen=True)
class StripResult:
    """Сводка пост-обработки одной страницы (или набора страниц)."""

    cleaned_text: str
    stripped_blocks: Tuple[StrippedBlock, ...]
    kept_lines: int
    stripped_lines: int


# ----------------------------------------------------------------------
# Детекция
# ----------------------------------------------------------------------


def _count_revits(line: str) -> int:
    """Число revit-токенов в строке."""

    return len(_REVIT_TOKEN_RE.findall(line))


def _revit_columns(line: str) -> List[str]:
    """Список revit-XXX в строке (в порядке появления)."""

    seen: Set[str] = []
    for m in _REVIT_TOKEN_RE.finditer(line):
        tok = "revit-" + m.group(1)
        if tok not in seen:
            seen.append(tok)
    return seen


def _is_year_row(line: str) -> bool:
    """«Проекты YYYY ...» — это маркер содержимого таблицы."""

    return bool(_YEAR_ROW_RE.match(line))


def _is_table_title_line(line: str) -> bool:
    """Стартовая строка блока (одна из форм «Наименование» / «Сервера …»)."""

    return bool(_TABLE_TITLE_RE.match(line))


def _is_column_header_line(line: str) -> bool:
    """Заголовок колонок: строка с двумя и более ``revit-NNN``."""

    return _count_revits(line) >= 2


def _is_section_heading_line(line: str) -> bool:
    """Заголовок раздела: «Введение», «Что делать, если…» и т.п."""

    return bool(_SECTION_HEADING_RE.match(line))


def _find_block_boundaries(lines: Sequence[str]) -> List[Tuple[int, int, List[str]]]:
    """Найти все серверные таблицы в наборе строк.

    Возвращает список кортежей ``(start, end, columns)``, где
    ``[start, end)`` — полуоткрытый диапазон строк, а ``columns`` —
    список revit-XXX-колонок таблицы.

    Стартовая строка блока — это строка заголовка таблицы:
    либо ``Наименование``/``Сервера`` (одна или две строки подряд),
    за которыми идёт строка с ``revit-XXX``; либо строка с двумя и
    более ``revit-XXX`` сама по себе. Блок должен также содержать
    хотя бы одну годовую строку ``Проекты YYYY`` — иначе это не
    таблица, а «случайное» перечисление revit-XXX (например, в
    инструкции).
    """

    blocks: List[Tuple[int, int, List[str]]] = []
    n = len(lines)
    i = 0
    while i < n:
        line = lines[i]
        # Ищем кандидата в стартовые строки блока.
        is_title = _is_table_title_line(line) and not _is_section_heading_line(line)
        is_multi_col = _is_column_header_line(line)
        if not (is_title or is_multi_col):
            i += 1
            continue
        # Если это «Наименование» / «Сервера», проверим, что в
        # ближайших 1–3 строках есть revit-XXX (это и есть заголовок
        # колонок). Иначе это «голое» слово «Наименование» без
        # таблицы (например, в инструкции).
        start = i
        header_end = i  # последняя строка, в которой искали revit-XXX
        if is_title and not is_multi_col:
            revit_idx: int = -1
            for k in range(i + 1, min(i + 4, n)):
                if _REVIT_TOKEN_RE.search(lines[k]):
                    revit_idx = k
                    break
            if revit_idx < 0:
                i += 1
                continue
            header_end = revit_idx
        else:
            # Мультиколоночная стартовая строка: revit-XXX в ней самой.
            header_end = i
        # Собираем revit-колонки из диапазона ``[start, header_end+1)``,
        # который охватывает «Наименование» + «сервера» + строку с
        # revit-XXX. Для мультиколоночной стартовой строки
        # ``header_end == i``, и колонки берутся из самой строки.
        columns: List[str] = []
        for k in range(start, header_end + 1):
            for c in _revit_columns(lines[k]):
                if c not in columns:
                    columns.append(c)
        # Ищем конец блока: следующая стартовая строка, заголовок
        # раздела, или конец ввода.
        end = header_end + 1
        saw_year = False
        k = header_end + 1
        while k < n:
            cur = lines[k].strip()
            if not cur:
                # Пустая строка — продолжаем, но не считаем «хвост».
                end = k + 1
                k += 1
                continue
            # Следующая стартовая строка (новая таблица).
            if _is_column_header_line(lines[k]):
                break
            if _is_table_title_line(lines[k]) and not _is_section_heading_line(lines[k]):
                # «Наименование» / «Сервера» следующей таблицы.
                break
            if _is_section_heading_line(lines[k]):
                break
            if _is_year_row(lines[k]):
                saw_year = True
            end = k + 1
            k += 1
        # Блок должен содержать хотя бы одну годовую строку.
        if saw_year and columns:
            # Дополнительно собираем revit-токены со всего блока
            # (вдруг одна колонка оказалась ниже «головной» строки —
            # это бывает, когда в одной таблице выводится «случайно
            # затесавшийся» ещё один revit-XXX; такой revit-XXX всё
            # равно покрывается блоком).
            for k2 in range(start, end):
                for c in _revit_columns(lines[k2]):
                    if c not in columns:
                        columns.append(c)
            blocks.append((start, end, columns))
            i = end
            continue
        # Не серверная таблица — пропускаем одну строку.
        i += 1
    return blocks


def strip_server_table_blocks(
    text: str,
    *,
    html_columns: Iterable[str],
) -> StripResult:
    """Удалить серверные таблицы, чьи колонки есть в ``html_columns``.

    Возвращает :class:`StripResult` со «чистым» текстом и списком
    удалённых блоков. Если ``html_columns`` пуст — текст возвращается
    без изменений.
    """

    if not text:
        return StripResult(
            cleaned_text=text or "",
            stripped_blocks=(),
            kept_lines=0,
            stripped_lines=0,
        )
    html_set = {c for c in html_columns if c}
    if not html_set:
        return StripResult(
            cleaned_text=text,
            stripped_blocks=(),
            kept_lines=len(text.split("\n")),
            stripped_lines=0,
        )
    lines = text.split("\n")
    blocks = _find_block_boundaries(lines)
    # Оставляем только блоки, чьи колонки — подмножество HTML.
    keep_lines: List[str] = [line for line in lines]
    stripped: List[StrippedBlock] = []
    for start, end, columns in blocks:
        if not all(c in html_set for c in columns):
            continue
        page_no = 0  # filled by caller; this function is page-agnostic
        stripped.append(
            StrippedBlock(
                page_number=page_no,
                start_line=start,
                end_line=end,
                columns=tuple(columns),
                matched_html=True,
            )
        )
        for k in range(start, end):
            # Заменяем строку на пустую, чтобы сохранить нумерацию.
            keep_lines[k] = ""
    # Схлопываем подряд идущие пустые строки в одну.
    cleaned_lines: List[str] = []
    prev_empty = False
    for line in keep_lines:
        if not line.strip():
            if prev_empty:
                continue
            cleaned_lines.append("")
            prev_empty = True
        else:
            cleaned_lines.append(line)
            prev_empty = False
    # Убираем ведущие/завершающие пустые строки.
    while cleaned_lines and not cleaned_lines[0].strip():
        cleaned_lines.pop(0)
    while cleaned_lines and not cleaned_lines[-1].strip():
        cleaned_lines.pop()
    stripped_count = sum(
        max(0, b.end_line - b.start_line) for b in stripped
    )
    return StripResult(
        cleaned_text="\n".join(cleaned_lines),
        stripped_blocks=tuple(stripped),
        kept_lines=sum(1 for line in cleaned_lines if line.strip()),
        stripped_lines=stripped_count,
    )


def strip_server_table_blocks_in_pages(
    pages: Sequence[Tuple[int, str]],
    *,
    html_columns: Iterable[str],
) -> Tuple[List[Tuple[int, str]], List[StrippedBlock]]:
    """Применить :func:`strip_server_table_blocks` к набору страниц PDF.

    ``pages`` — список ``(page_number, text)``. Возвращает пару:
    ``(очищенные_страницы, удалённые_блоки)``.
    """

    cleaned: List[Tuple[int, str]] = []
    all_blocks: List[StrippedBlock] = []
    for page_number, text in pages:
        result = strip_server_table_blocks(text, html_columns=html_columns)
        cleaned.append((page_number, result.cleaned_text))
        for b in result.stripped_blocks:
            all_blocks.append(
                StrippedBlock(
                    page_number=page_number,
                    start_line=b.start_line,
                    end_line=b.end_line,
                    columns=b.columns,
                    matched_html=b.matched_html,
                )
            )
    return cleaned, all_blocks


__all__ = [
    "StripResult",
    "StrippedBlock",
    "strip_server_table_blocks",
    "strip_server_table_blocks_in_pages",
]
