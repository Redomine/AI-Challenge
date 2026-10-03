"""Разбор Confluence HTML-выгрузки (view) и извлечение табличных связей.

Файл ``.html`` в этой папке — это сохранённая через браузер страница Confluence
(``body.view`` после рендеринга). Внутри неё лежит одна большая таблица с
маппингом «проект ↔ сервер Revit» по годам (Проекты 2022 / 2024 и т.п.) и
с заголовками колонок ``revit-201`` / ``revit-702`` / ``revit-703`` / …

Задача модуля — извлечь каждую конкретную связь
``(раздел, год, имя сервера, код проекта)`` как структурный блок, который
затем превращается в :class:`~docindexing.corpus.CorpusUnit` с
метаданными ``source="confluence_html"`` и понятным ``section_path``.

Важные инварианты:

* Никакого вывода Revit-версии из строки «Проекты 2022»: серверы
  ``revit-201`` / ``revit-702`` / ``revit-703`` / ``revit-704`` — это
  уникальные имена серверов, а не версии Revit. Правило только эмитит
  связь (server, project) и сохраняет год в метаданных как
  «подраздел», а не как «версию».
* Каждая запись в ячейке (``STLB-OK1`` и т.п.) — это отдельная связь,
  а не пакетная строка; в одной ячейке через ``\\n`` перечислено
  несколько кодов, каждый из которых идёт в свой юнит.
* Иерархия заголовков (h2 / h3) перед таблицей даёт
  ``section_path`` для эмитированного юнита.
* Используется ТОЛЬКО ``html.parser`` из stdlib — никаких внешних
  зависимостей (lxml / beautifulsoup4 не добавляются).
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from html.parser import HTMLParser
from pathlib import Path
from typing import List, Optional, Tuple


# ----------------------------------------------------------------------
# Минимальная константа: ожидаемые классы у основной таблицы.
# ----------------------------------------------------------------------

# Confluence размечает свои таблицы сочетанием классов ``relative-table``
# и ``wrapped confluenceTable``. На всякий случай держим и просто
# ``confluenceTable`` — если шаблон темы изменится, мы всё равно поймаем.
_CONFLUENCE_TABLE_CLASS_RE = re.compile(
    r"confluenceTable|relative-table", re.IGNORECASE
)

# Маркеры строк «без данных»: одиночная ячейка с colspan (H3 sub-header),
# пустые строки или текст ``&nbsp;``.
_PLACEHOLDER_RE = re.compile(r"^\s*(?:&nbsp;|-)\s*$", re.IGNORECASE)

# Эталонные имена серверов: revit-XXX (где XXX — три цифры).
_SERVER_NAME_RE = re.compile(r"^revit-(\d+)$", re.IGNORECASE)

# Эталонные «годовые» метки. Только латиница + цифры; кириллица
# допускается как UTF-8, потому что в источнике метки выглядят как
# «Проекты 2022», «Проекты 2024», …
_YEAR_LABEL_RE = re.compile(r"^\s*Проекты?\s+(\d{4})\s*$", re.IGNORECASE)


@dataclass(frozen=True)
class ConfluenceHtmlRelation:
    """Одна табличная связь между экспортом и сервером.

    Поля:

    * ``section_path`` — путь заголовков h2 / h3 от корня до таблицы
      (например ``[Где находится мой проект?,
      Бюро комплексного проектирования социальных объектов]``);
    * ``row_label`` — метка строки таблицы (``Проекты 2022``,
      ``Проекты 2024``, или пользовательская);
    * ``column_header`` — имя сервера (``revit-702`` и т.п.);
    * ``project_code`` — конкретный код проекта в ячейке
      (``STLB-OK1``);
    * ``page_id`` — стабильный идентификатор страницы из URL Confluence
      (извлекается из ``saved from url=`` или из ссылки ``viewpage``).
    """

    page_id: str
    section_path: Tuple[str, ...]
    row_label: str
    column_header: str
    project_code: str
    extra_text: str = ""

    def unit_id(self, *, occurrence: int) -> str:
        """Стабильный идентификатор юнита с ординалом вхождения.

        Повторяющиеся проекты в одной и той же колонке встречаются в
        разных таблицах/разделах — добавляем детерминированный
        ординал, как это делает
        :func:`docindexing.corpus._confluence_unit_id`.
        """

        safe_path = "/".join(s.strip().replace(" ", "_") for s in self.section_path if s)
        base = (
            f"cfhtml:{self.page_id}:{safe_path}:{self.row_label}:"
            f"{self.column_header}:{self.project_code}"
        )
        return f"{base}#{occurrence}"

    def text(self) -> str:
        """Текст юнита для индексации: явная связь «проект ↔ сервер»."""

        parts: List[str] = []
        if self.section_path:
            parts.append(" / ".join(self.section_path))
        parts.append(f"Сервер: {self.column_header}")
        parts.append(f"Проект: {self.project_code}")
        parts.append(f"Год: {self.row_label}")
        if self.extra_text:
            parts.append(f"Контекст: {self.extra_text}")
        return ". ".join(parts)


@dataclass
class ConfluenceHtmlDoc:
    """Результат разбора одной HTML-выгрузки Confluence."""

    page_id: str
    title: str
    source_path: Path
    relations: List[ConfluenceHtmlRelation] = field(default_factory=list)
    # Сколько таблиц самого (Наименование сервера / revit-XXX) обработано.
    tables_parsed: int = 0
    # Сколько строк отброшено как «шумовые» (одиночные ячейки с h3).
    section_header_rows: int = 0

    def total_words(self) -> int:
        """Слова по всем связям (для совместимости с ConfluenceDoc)."""

        total = 0
        for r in self.relations:
            total += len(r.text().split())
        return total


# ----------------------------------------------------------------------
# Парсер таблиц
# ----------------------------------------------------------------------


class _CellCollector(HTMLParser):
    """Собрать плоский текст из HTML внутри одной ячейки / строки."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self._buf: List[str] = []

    def handle_starttag(self, tag: str, attrs) -> None:
        if tag == "br":
            self._buf.append("\n")
        elif tag in {"p", "li"}:
            self._buf.append("\n")

    def handle_endtag(self, tag: str) -> None:
        if tag in {"p", "li"}:
            self._buf.append("\n")

    def handle_data(self, data: str) -> None:
        if data:
            self._buf.append(data)

    def text(self) -> str:
        # Нормализуем: схлопнем последовательные \n и пробелы в \n.
        raw = "".join(self._buf)
        raw = re.sub(r"[ \t]+", " ", raw)
        raw = re.sub(r"\n[ \t]*", "\n", raw)
        raw = re.sub(r"\n{2,}", "\n", raw)
        return raw.strip()


def _collect_text_safe(html: str) -> str:
    """Собрать плоский текст из фрагмента HTML (закрыто, возвращает результат)."""

    c = _CellCollector()
    c.feed(html or "")
    c.close()
    return c.text()


def _parse_page_id_from_html(html: str) -> str:
    """Извлечь ``pageId`` из сохранённого HTML.

    Confluence сохраняет в комментарии ``<!-- saved from url=... -->``
    ``pageId``. Также Fallback ищется в ``viewpage.action?pageId=``.
    """

    m = re.search(
        r"saved from url=\(\d+\)(https?://[^>\s]+?viewpage\.action\?pageId=(\d+))",
        html,
    )
    if m:
        return m.group(2)
    m = re.search(r"viewpage\.action\?pageId=(\d+)", html)
    if m:
        return m.group(1)
    return ""


def _extract_title(html: str) -> str:
    m = re.search(r"<title>([^<]+)</title>", html)
    return m.group(1).strip() if m else ""


# ----------------------------------------------------------------------
# Основной парсер таблиц
# ----------------------------------------------------------------------


class _TableExtractor(HTMLParser):
    """Построчный обход главной ``confluenceTable``-таблицы.

    Сложность HTML-парсера stdlib в том, что он не даёт доступа к
    «текущей строке / ячейке» по позиции — обработчики ``handle_*tag``
    вызываются «плоско». Поэтому здесь поддерживается явный стек
    таблиц / строк / ячеек.
    """

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        # Стек открытых <table>.
        self._table_stack: List[dict] = []
        # Стек открытых <tr>.
        self._row_stack: List[dict] = []
        # Текущая открытая ячейка (если есть).
        self._cell_buf: List[str] = []
        # Все полностью собранные таблицы: list[list[str]] (cell texts).
        self.tables: List[List[List[str]]] = []
        # Текущая собираемая таблица.
        self._current_rows: List[List[str]] = []
        self._current_row_cells: List[str] = []

    # --- tag callbacks --------------------------------------------

    def handle_starttag(self, tag: str, attrs) -> None:
        a = {k.lower(): v for k, v in attrs}
        if tag == "table":
            classes = (a.get("class") or "").lower()
            is_main = bool(_CONFLUENCE_TABLE_CLASS_RE.search(classes))
            self._table_stack.append({
                "is_main": is_main,
                "classes": classes,
            })
            # Начинаем новую «черновую» таблицу для main-таблиц; для
            # вложенных таблиц мы игнорируем содержимое, но держим стек.
            self._current_rows = []
            self._current_row_cells = []
            return

        if not self._table_stack:
            return
        if not self._table_stack[-1]["is_main"]:
            return

        if tag == "tr":
            self._row_stack.append({
                "depth": len(self._table_stack),
            })
            self._current_row_cells = []
            return

        if not self._row_stack:
            return
        if tag in {"td", "th"}:
            # Начинаем сборку текста новой ячейки.
            self._cell_buf = []
            return
        if tag == "br":
            self._cell_buf.append("\n")
            return
        if tag in {"p", "li"}:
            self._cell_buf.append("\n")
            return

    def handle_endtag(self, tag: str) -> None:
        if tag == "table":
            if not self._table_stack:
                return
            top = self._table_stack.pop()
            if top["is_main"]:
                self.tables.append(self._current_rows)
                self._current_rows = []
                self._current_row_cells = []
            return

        if not self._table_stack:
            return
        if not self._table_stack[-1]["is_main"]:
            return

        if tag == "tr":
            if not self._row_stack:
                return
            self._row_stack.pop()
            # Сохраняем собранную строку в current_rows, если в ней
            # действительно были <td>/<th>.
            if self._current_row_cells:
                self._current_rows.append(self._current_row_cells)
                self._current_row_cells = []
            return

        if tag in {"td", "th"}:
            text = _collect_text_safe("".join(self._cell_buf))
            self._cell_buf = []
            self._current_row_cells.append(text)
            return
        if tag in {"p", "li"}:
            self._cell_buf.append("\n")
            return

    def handle_data(self, data: str) -> None:
        if not self._table_stack:
            return
        if not self._table_stack[-1]["is_main"]:
            return
        if not self._row_stack:
            return
        if self._cell_buf is not None:
            self._cell_buf.append(data)


def _split_cell_entries(cell_text: str) -> List[str]:
    """Разбить текст одной ячейки на отдельные записи.

    В исходном файле ячейки содержат несколько проектов, разделённых
    тегом ``<br>`` (в собранном тексте это ``\\n``). Также встречаются
    пометки вида ``STLB-04 (KR)`` — обёртка в скобках отбрасывается
    как «комментарий», чтобы индекс видел только «чистый» код.
    Пустые строки и ``&nbsp;``-placeholder'ы пропускаются.
    """

    if not cell_text:
        return []

    entries: List[str] = []
    for raw_line in cell_text.split("\n"):
        line = raw_line.strip()
        if not line:
            continue
        if _PLACEHOLDER_RE.match(line):
            continue
        # Делим по '<br>' если почему-то осталось в HTML.
        for sub in re.split(r"<br\s*/?>", line, flags=re.IGNORECASE):
            sub = sub.strip()
            if not sub or _PLACEHOLDER_RE.match(sub):
                continue
            # Отбрасываем комментарии в скобках: «(KR)», «(вып. данные)».
            cleaned = re.sub(r"\([^)]*\)", "", sub).strip()
            if not cleaned:
                # Если вся строка была комментарием — оставляем оригинал,
                # чтобы индекс не терял упоминание вроде «STLB-04».
                cleaned = sub
            entries.append(cleaned)
    return entries


def _is_year_label(text: str) -> Optional[str]:
    """Распознать «Проекты 2022», «Проекты 2024», «Проект 2025» и т.п.

    Возвращает нормализованную метку или ``None``, если строка не
    похожа на «проекты + год».
    """

    m = _YEAR_LABEL_RE.match(text.strip())
    return m.group(0).strip() if m else None


def _is_server_column_header_row(first_cell: str) -> bool:
    """Определить, является ли строка «заголовком серверов».

    Строка считается таковой, если в первой ячейке лежит
    «Наименование сервера» (с переносами и пробелами). Это
    эвристика, но она устойчива к редакторскому форматированию
    Confluence (теги <br>, <span>, лишние пробелы).
    """

    if not first_cell:
        return False
    # Схлопываем переносы в одиночные пробелы.
    norm = re.sub(r"\s+", " ", first_cell).strip().lower()
    return norm.startswith("наименование сервера")


def _extract_section_headers(html: str, *, table_offset: int) -> List[str]:
    """Вернуть список заголовков секций (h2 / h3), идущих ПЕРЕД таблицей.

    Заголовки выдёргиваются в порядке появления. Возвращаем только
    те, что идут до ``table_offset``. Это позволяет установить
    правильный ``section_path`` для эмитированных юнитов.

    На странице Confluence, помимо настоящих заголовков, есть
    служебные:

    * ``<h2>`` со вложенным ``<div class="toc-macro">`` — это блок ToC;
    * «Страница навигации» (sidebar) — это очень короткий h2 без
      секционного смысла и без полезного контекста для таблицы.

    Все они отбрасываются: чтобы заголовок попал в
    ``section_path``, он должен либо иметь ``class="auto-cursor-target"``
    (тот шаблон, что Confluence использует для «настоящих» заголовков
    раздела), либо быть коротким и не иметь признаков ToC.
    """

    headers: List[Tuple[int, int, str]] = []  # (offset, level, text)
    for m in re.finditer(
        r"<h([23])[^>]*>([\s\S]*?)</h\1>",
        html[:table_offset],
        flags=re.IGNORECASE,
    ):
        text = _collect_text_safe(m.group(2))
        if not text:
            continue
        level = int(m.group(1))
        raw_html = m.group(0)
        is_toc_macro = "toc-macro" in raw_html
        headers.append((m.start(), level, text, is_toc_macro))

    # Heuristic: отбрасываем:
    #   1) ``<h2>`` с ToC-блоком (длинный список ссылок внутри);
    #   2) заголовки-«фантомы» боковой панели (sidebar обычно не
    #      имеет ``id="..."``; реальные заголовки Confluence всегда
    #      идут с ``id="id-..."``).
    cleaned: List[Tuple[int, int, str]] = []
    for m in re.finditer(
        r"<h([23])([^>]*)>([\s\S]*?)</h\1>",
        html[:table_offset],
        flags=re.IGNORECASE,
    ):
        attrs = m.group(2)
        raw_html = m.group(0)
        is_toc_macro = "toc-macro" in raw_html
        level = int(m.group(1))
        text = _collect_text_safe(m.group(3))
        if not text:
            continue
        # ToC-блок → пропускаем.
        if is_toc_macro:
            continue
        # Реальные секционные заголовки Confluence всегда имеют
        # ``id="id-..."``. Боковые панели и проч. шум — без id.
        has_id = re.search(r"\bid=\"id-", attrs) is not None
        if not has_id:
            continue
        cleaned.append((m.start(), level, text))

    if not cleaned:
        return []

    # Возвращаем в порядке появления: сначала ближайший h2, потом все
    # h3, идущие после последнего h2 и до следующего h2.
    out: List[str] = []
    last_h2_idx: Optional[int] = None
    for i, (off, lvl, txt) in enumerate(cleaned):
        if lvl == 2:
            last_h2_idx = i
            out.append(txt)
        else:
            # h3: добавляем ТОЛЬКО если он идёт после h2-а.
            if last_h2_idx is not None and i > last_h2_idx:
                out.append(txt)
    return out


def _find_main_table_offset(html: str) -> Optional[int]:
    """Найти смещение первой ``confluenceTable``-таблицы в HTML."""

    for m in re.finditer(r"<table\b[^>]*>", html, flags=re.IGNORECASE):
        # Берём кусок текста до закрывающей '>' открывающего тега,
        # чтобы сверить класс.
        end = html.find(">", m.start())
        if end < 0:
            continue
        snippet = html[m.start():end]
        if _CONFLUENCE_TABLE_CLASS_RE.search(snippet):
            return m.start()
    return None


# ----------------------------------------------------------------------
# Публичные функции
# ----------------------------------------------------------------------


def load_confluence_html_doc(path: Path | str) -> ConfluenceHtmlDoc:
    """Загрузить Confluence HTML-выгрузку и извлечь табличные связи.

    Возвращает :class:`ConfluenceHtmlDoc` с массивом
    :class:`ConfluenceHtmlRelation`. Если в HTML нет подходящей
    таблицы — возвращает документ с пустым списком связей (но
    корректно заполненными ``title`` и ``page_id``).
    """

    src = Path(path)
    html = src.read_text(encoding="utf-8")

    page_id = _parse_page_id_from_html(html)
    title = _extract_title(html)

    # Заголовки секций, идущие до основной таблицы.
    table_offset = _find_main_table_offset(html) or len(html)
    section_headers = _extract_section_headers(html, table_offset=table_offset)

    # Извлекаем строки основной таблицы.
    extractor = _TableExtractor()
    extractor.feed(html)
    extractor.close()

    relations: List[ConfluenceHtmlRelation] = []
    tables_parsed = 0
    section_header_rows = 0

    # Confluence обычно кладёт ОДНУ «главную» таблицу на страницу;
    # защищаемся от двух одинаковых.
    main_tables = extractor.tables
    for table in main_tables:
        if not table:
            continue
        tables_parsed += 1

        # Текущий «контекст» секции: внешние h2/h3 + внутренние sub-headers.
        column_headers: List[str] = []  # заполняется из строки «Наименование сервера»
        current_sub_section: List[str] = list(section_headers)

        # Шаг 1: первый проход — собрать sub-headers (строки с одной ячейкой,
        # обычно содержащие h3).
        for row in table:
            if len(row) == 1:
                txt = row[0].strip()
                if txt and not _PLACEHOLDER_RE.match(txt):
                    current_sub_section = current_sub_section + [txt]
                    section_header_rows += 1

        # Шаг 2: второй проход — выделить column_headers и row_labels,
        # эмитнуть relations.
        current_sub_section = list(section_headers)
        for row in table:
            if not row:
                continue
            # row == ['text'] — colspan=6 секционный header.
            if len(row) == 1:
                txt = row[0].strip()
                if txt and not _PLACEHOLDER_RE.match(txt):
                    current_sub_section = current_sub_section + [txt]
                continue
            first = (row[0] or "").strip()
            if not first:
                continue
            # Строка-заголовков «Наименование сервера / revit-201 / …».
            if _is_server_column_header_row(first):
                # Обновляем column_headers; первая ячейка — это имя колонки.
                # Берём первое слово каждой ячейки; пустые ячейки
                # оставляем пустыми.
                column_headers = []
                for cell in row:
                    text = (cell or "").strip()
                    if not text:
                        column_headers.append("")
                    else:
                        column_headers.append(text.split(None, 1)[0])
                continue
            # Строка-«год»: «Проекты 2022», «Проекты 2024», и т.п.
            row_label = _is_year_label(first) or first
            # Извлекаем записи из каждой ячейки (кроме первой, где метка).
            for col_idx in range(1, len(row)):
                if col_idx >= len(column_headers):
                    continue
                col_header = column_headers[col_idx]
                if not col_header:
                    # Колонка без имени — пропускаем (нет ground-truth).
                    continue
                # НЕ выводим column_header из row_label: имя версий НЕ
                # вычисляется из «Проекты 2022». column_header берётся
                # ТОЛЬКО из заголовка колонки (``revit-703``).
                entries = _split_cell_entries(row[col_idx])
                for entry in entries:
                    relations.append(
                        ConfluenceHtmlRelation(
                            page_id=page_id or "confluence_html",
                            section_path=tuple(current_sub_section),
                            row_label=row_label,
                            column_header=col_header,
                            project_code=entry,
                        )
                    )

    return ConfluenceHtmlDoc(
        page_id=page_id or "confluence_html",
        title=title,
        source_path=src,
        relations=relations,
        tables_parsed=tables_parsed,
        section_header_rows=section_header_rows,
    )


__all__ = [
    "ConfluenceHtmlRelation",
    "ConfluenceHtmlDoc",
    "load_confluence_html_doc",
]