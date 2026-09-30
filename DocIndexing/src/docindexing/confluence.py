"""Разбор Confluence HTML и storage-формата.

Источники:
    body.storage — XHTML с макросами в виде ``<ac:structured-macro ...>``.
    body.view    — уже отрисованный HTML, который резолвит excerpt/include.

Требования:
    * Сохраняем иерархию заголовков (``h1``–``h6``) и текст вложенных
      expand-макросов вместе с их заголовком.
    * Избегаем дублей между storage и view: всё, что попало из storage,
      не берём из view (используем нормализованные SHA-256 «сигнатуры»).
    * Удаляем типовой шум: ToC, breadcrumbs, мини-статусы.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from html.parser import HTMLParser
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Tuple

from . import hashing, text_utils


# Классы CSS, которые Confluence и темы добавляют к «служебным» блокам.
# Сопоставление идёт по целому слову (token), а не по подстроке, чтобы
# ``my-toc-macro-panel`` не подавлял содержимое. ``columnLayout`` и
# прочие layout-обёртки НЕ входят в список: это контейнеры для
# реального контента, иначе теряются все вложенные expand/таблицы.
NOISE_CLASS_TOKENS = frozenset({
    "toc-macro",
    "breadcrumb",
    "page-metadata",
    "footer-content",
    "navmenu",
})

# Имена макросов, которые мы считаем шумом.
NOISE_MACRO_NAMES = {"status", "excerpt-include-metadata"}

# Теги, в которых подавление сохраняется по парным открывающим/закрывающим.
# Для макросов и шумовых ``div`` хранится стек.
VOID_HTML_TAGS = {"br", "hr", "img", "meta", "link"}


@dataclass
class _BlockNode:
    """Внутреннее представление узла дерева блоков при парсинге."""

    block: Optional[Block]
    parent: Optional["_BlockNode"] = None
    children: List["_BlockNode"] = None  # type: ignore[assignment]

    def __post_init__(self) -> None:
        if self.children is None:
            self.children = []


@dataclass
class Block:
    """Минимальная структурная единица документа.

    ``level`` — глубина вложенности (для заголовков и expand). 0 для
    обычного текста/списка. ``title`` заполняется для заголовков и expand.
    """

    kind: str  # "heading" | "expand" | "paragraph" | "list" | "table" | "code"
    level: int = 0
    title: Optional[str] = None
    text: str = ""

    def signature(self) -> str:
        """Стабильная сигнатура для дедупликации."""

        return f"{self.kind}|{self.level}|{self.title or ''}|{text_utils.normalize_whitespace(self.text)}"


@dataclass
class Section:
    """Иерархическая секция с заголовком и вложенными блоками/секциями."""

    title: str
    level: int
    blocks: List[Block] = field(default_factory=list)
    children: List["Section"] = field(default_factory=list)

    def walk(self) -> Iterable["Section"]:
        yield self
        for child in self.children:
            yield from child.walk()


class _ConfluenceHTMLParser(HTMLParser):
    """Парсер Confluence HTML в плоскую последовательность блоков.

    Поддерживает:
        * заголовки h1-h6 с учётом уровня;
        * expand-макросы (``ac:macro`` с именем ``expand``) и их тело;
        * параграфы, списки, таблицы, code/pre;
        * excerpt и excerpt-include как маркеры «внешний источник»;
        * удаление шумовых блоков.

    Дерево блоков строится in-memory и в конце ``flatten``-ится
    в плоский список в порядке появления (DFS). Это сохраняет
    естественную вложенность expand.
    """

    VOID_TAGS = set(VOID_HTML_TAGS)

    def __init__(self, *, source_kind: str) -> None:
        super().__init__(convert_charrefs=True)
        self.source_kind = source_kind
        self.blocks: List[Block] = []
        self._stack: List[Dict[str, object]] = []
        # Список сигнатур, которые уже учтены из другого источника.
        self._seen_signatures: set[str] = set()
        # Стек «подавленных» тегов: каждый кадр хранит имя тега,
        # который открыл подавление. Когда закрывается тот же тег,
        # кадр снимается. Это гарантирует, что подавление не «утекает»
        # за пределы шумового блока, в том числе для тегов, у которых
        # нет парного закрытия в ``HTMLParser`` (``ac:structured-macro``,
        # ``ac:macro``), и для вложенных шумовых контейнеров.
        self._suppress_stack: List[str] = []
        # Стек уровней заголовков (h1..h6), чтобы вложенные expand
        # знали, на каком уровне они находятся.
        self._heading_stack: List[int] = []
        # Стек глубин expand — для вложенных expand-макросов.
        self._expand_depth: List[int] = []
        # Корневой узел дерева блоков.
        self._root_node = _BlockNode(None)
        # Узел, в который сейчас добавляются дети (== верхний блок или root).
        self._current_node = self._root_node

    # --- helpers ---------------------------------------------------

    def _suppressed(self) -> bool:
        return bool(self._suppress_stack)

    def _begin_suppression(self, tag: str) -> None:
        """Пометить, что внутри ``tag`` нужно игнорировать содержимое."""

        self._suppress_stack.append(tag)

    def _end_suppression(self, tag: str) -> bool:
        """Снять подавление для ``tag``.

        Возвращает ``True``, если кадр был снят (т.е. тег совпал с
        верхним элементом стека). ``ac:macro`` и ``ac:structured-macro``
        трактуются как эквивалентные: разные представления одного и того
        же макроса встречаются в разных версиях Confluence HTML.
        """

        if not self._suppress_stack:
            return False
        canonical_close = self._canonical_macro_tag(tag)
        # Сначала пробуем снять верхний кадр (нормальный случай).
        if self._canonical_macro_tag(self._suppress_stack[-1]) == canonical_close:
            self._suppress_stack.pop()
            return True
        # Тег не совпал с вершиной: ищем соответствующий кадр
        # глубже (вложенное подавление) и снимаем всё, что выше.
        for i in range(len(self._suppress_stack) - 1, -1, -1):
            if self._canonical_macro_tag(self._suppress_stack[i]) == canonical_close:
                del self._suppress_stack[i:]
                return True
        return False

    @staticmethod
    def _canonical_macro_tag(tag: str) -> str:
        if tag in {"ac:macro", "ac:structured-macro"}:
            return "ac:macro"
        return tag

    def _has_class(self, classes: List[str], token: str) -> bool:
        return token in classes

    def _open_block(self, block: Block) -> None:
        if self._suppressed():
            # Внутри шумового блока новые блоки не открываем. Это
            # сохраняет баланс стека блоков и предотвращает попадание
            # «пустых» параграфов в выдачу.
            return
        node = _BlockNode(block, parent=self._current_node)
        self._current_node.children.append(node)
        self._stack.append({"block": block, "node": node, "buf": []})
        self._current_node = node

    def _close_block(self) -> Optional[Block]:
        if not self._stack:
            return None
        frame = self._stack.pop()
        block: Block = frame["block"]  # type: ignore[assignment]
        node: _BlockNode = frame["node"]  # type: ignore[assignment]
        buf: List[str] = frame["buf"]  # type: ignore[assignment]
        text = text_utils.normalize_whitespace("".join(buf))
        if text:
            if block.kind == "heading":
                block.text = text
            elif block.kind in {"paragraph", "list", "table", "code", "expand"}:
                block.text = (block.text + "\n" + text).strip() if block.text else text
        # Поднимаем current_node обратно к родителю.
        self._current_node = node.parent or self._root_node
        # Регистрируем сигнатуру независимо от подавления: пустые
        # блоки внутри шумовых регионов всё равно могут занимать
        # место в дереве и влиять на вложенность, но мы их не
        # добавляем в ``self.blocks`` ниже через DFS-фильтр.
        return block

    def _append_text(self, text: str) -> None:
        if not text:
            return
        if self._suppressed():
            return
        if self._stack:
            self._stack[-1]["buf"].append(text)  # type: ignore[index]

    # --- tag callbacks --------------------------------------------

    def handle_starttag(self, tag: str, attrs):
        a = {k.lower(): v for k, v in attrs}
        classes = [c.lower() for c in (a.get("class") or "").split() if c]

        # Подавление шумовых блоков по классу (точное совпадение токена).
        if any(self._has_class(classes, p) for p in NOISE_CLASS_TOKENS):
            self._begin_suppression(tag)
            return

        # Expand-макрос: и в виде <ac:macro name="expand">, и в виде
        # <ac:structured-macro name="expand"> (используется в Confluence storage).
        if tag in {"ac:macro", "ac:structured-macro"}:
            name = (a.get("ac:name") or "").lower()
            if name == "expand":
                base = self._heading_stack[-1] if self._heading_stack else 0
                expand_offset = len(self._expand_depth)
                level = max(1, base + 1 + expand_offset)
                self._expand_depth.append(level)
                block = Block(kind="expand", level=level)
                self._open_block(block)
                # Не подавляем тело expand: внутри может быть вложенный
                # expand, заголовки и текст.
                return
            if name in NOISE_MACRO_NAMES:
                self._begin_suppression(tag)
                return
            if name in {"excerpt", "excerpt-include"}:
                # Excerpt/include трактуем как источник «внешнего» контента.
                # Контент обрабатываем, но помечаем, чтобы избежать дублей.
                return
            # Прочие structured-макросы пропускаем как есть.
            return

        # Параметр expand с заголовком.
        if tag == "ac:parameter" and (a.get("ac:name") or "").lower() == "title":
            if not self._stack:
                return
            self._stack[-1]["awaiting_expand_title"] = True  # type: ignore[index]
            return

        if tag == "ac:rich-text-body":
            # Тело expand: содержимое будет парситься как обычный HTML.
            return

        if tag in {"h1", "h2", "h3", "h4", "h5", "h6"}:
            level = int(tag[1])
            # Поддерживаем стек: пока текущий верхний уровень >= нового,
            # закрываем его.
            while self._heading_stack and self._heading_stack[-1] >= level:
                self._heading_stack.pop()
            self._heading_stack.append(level)
            block = Block(kind="heading", level=level)
            self._open_block(block)
            return

        if tag == "p":
            self._open_block(Block(kind="paragraph"))
            return

        if tag in {"ul", "ol"}:
            self._open_block(Block(kind="list"))
            return

        if tag == "table":
            self._open_block(Block(kind="table"))
            return

        if tag in {"pre", "code"}:
            block = Block(kind="code")
            self._open_block(block)
            return

        if tag in {"div", "section", "article"}:
            # Контейнеры: продолжаем без нового блока.
            return

        if tag == "li":
            # Просто текст внутри ul/ol — буферизуем.
            return

        if tag == "br":
            self._append_text("\n")
            return

    def handle_endtag(self, tag: str):
        # Снимаем подавление, если этот тег его открывал.
        # Делаем это ДО обработки обычных закрытий, чтобы баланс
        # шумовых блоков не «утекал» дальше по документу.
        self._end_suppression(tag)

        # Подавление могло остаться активным, если закрылся не тот тег
        # (например, вложенный шумовой макрос).
        if self._suppressed():
            return

        if tag in {"h1", "h2", "h3", "h4", "h5", "h6"}:
            self._close_block()
            return

        if tag == "p":
            self._close_block()
            return

        if tag in {"ul", "ol"}:
            self._close_block()
            return

        if tag == "table":
            self._close_block()
            return

        if tag in {"pre", "code"}:
            self._close_block()
            return

        if tag in {"ac:macro", "ac:structured-macro"}:
            # Если макрос был expand — закрываем блок expand.
            if self._stack and isinstance(self._stack[-1].get("block"), Block):
                top_block: Block = self._stack[-1]["block"]  # type: ignore[assignment]
                if top_block.kind == "expand":
                    self._close_block()
                    if self._expand_depth:
                        self._expand_depth.pop()
            return

    def handle_data(self, data: str):
        if self._suppressed():
            return
        # Если ждём заголовок expand-параметра.
        if self._stack and self._stack[-1].get("awaiting_expand_title"):
            top = self._stack[-1]
            block: Block = top["block"]  # type: ignore[assignment]
            title = text_utils.normalize_whitespace(data)
            if title:
                block.title = title
            top["awaiting_expand_title"] = False
            return
        self._append_text(data)

    def _depth(self) -> int:
        # Глубина expand (для level).
        return sum(1 for f in self._stack if isinstance(f, dict) and isinstance(f.get("block"), Block) and f["block"].kind == "expand")  # type: ignore[arg-type]

    def close(self) -> None:  # type: ignore[override]
        super().close()
        # Перед закрытием аккуратно свернём оставшиеся открытые блоки.
        while self._stack:
            self._close_block()
        # Соберём плоский список блоков в DFS-порядке, чтобы expand
        # появлялся перед вложенными блоками.
        #
        # ВАЖНО: дедупликация по сигнатуре здесь НЕ выполняется. Иначе
        # повторяющиеся expand-блоки с одинаковым заголовком/текстом
        # (что легитимно в storage — например, два сиблинга с одним
        # ``ac:name="title"``) вместе со всем своим поддеревом отбрасывались
        # бы после первого вхождения. Кросс-представленческая (storage vs
        # view) дедупликация выполняется на уровне ``load_confluence_doc``
        # через сигнатуры секций/блоков, без потери легитимных повторов
        # в одном источнике.
        out: List[Block] = []

        def visit(node: _BlockNode) -> None:
            for child in node.children:
                if child.block is None:
                    # Корневой узел — пропускаем его самого, обходим детей.
                    visit(child)
                    continue
                out.append(child.block)
                visit(child)

        visit(self._root_node)
        self.blocks = out


def parse_html_to_blocks(html: str, *, source_kind: str) -> List[Block]:
    """Распарсить HTML (storage или view) в плоский список блоков."""

    parser = _ConfluenceHTMLParser(source_kind=source_kind)
    parser.feed(html or "")
    parser.close()
    return parser.blocks


def build_sections_from_blocks(blocks: Iterable[Block]) -> List[Section]:
    """Собрать дерево секций из плоского списка блоков.

    Заголовки формируют новую секцию. Expand-блоки вкладываются как
    дочерние секции с ``title`` из макроса. Прочие блоки (параграфы,
    списки, таблицы, код) накапливаются в текущей открытой секции.
    """

    root = Section(title="__root__", level=0)
    stack: List[Section] = [root]

    def open_section(title: str, level: int) -> Section:
        section = Section(title=title, level=level)
        # Помещаем секцию в ближайшего родителя с меньшим уровнем.
        while stack and stack[-1].level >= level and stack[-1] is not root:
            stack.pop()
        parent = stack[-1]
        parent.children.append(section)
        stack.append(section)
        return section

    for block in blocks:
        if block.kind == "heading":
            level = max(1, block.level)
            title = block.text.strip() or "(без названия)"
            open_section(title=title, level=level)
        elif block.kind == "expand":
            title = (block.title or "Раскрывающийся блок").strip()
            level = block.level + 1  # expand на уровень глубже
            section = open_section(title=f"[expand] {title}", level=level)
            # Вложенный текст expand хранится в block.text — добавим его
            # как параграф в эту секцию, если есть.
            if block.text:
                section.blocks.append(Block(kind="paragraph", text=block.text))
        else:
            stack[-1].blocks.append(block)

    return root.children


def filter_noise_sections(sections: List[Section]) -> List[Section]:
    """Удалить секции, которые содержат только «шумовой» текст."""

    noise_words = ("table of contents", "содержание", "навигация")

    def is_noise(section: Section) -> bool:
        if section.title.strip().lower() in noise_words:
            return True
        joined = text_utils.normalize_whitespace(
            " ".join(b.text for b in section.blocks)
        ).lower()
        if not joined and not section.children:
            return True
        if joined and any(w in joined for w in noise_words) and len(joined) < 60:
            return True
        return False

    def walk(items: List[Section]) -> List[Section]:
        out: List[Section] = []
        for s in items:
            s.children = walk(s.children)
            if not is_noise(s):
                out.append(s)
        return out

    return walk(sections)


@dataclass
class ConfluenceDoc:
    """Результат разбора одной страницы Confluence."""

    page_id: str
    title: str
    version: Optional[int]
    storage_hash: str
    view_hash: str
    storage_chars: int
    view_chars: int
    sections: List[Section]
    coverage: Dict[str, int]
    # Сколько ``ac:structured-macro ac:name="excerpt-include"`` пришло из
    # ``body.storage``. Такие макросы тянут контент С ДРУГИХ страниц;
    # мы их НЕ резолвим и НЕ добавляем в корпус (cross-page merge
    # запрещён по умолчанию). Значение попадает в ``coverage`` под ключом
    # ``unresolved_excerpt_includes`` и в отчёт сборки.
    unresolved_excerpt_includes: int = 0

    def total_words(self) -> int:
        """Подсчитать слова во всех секциях дерева, включая вложенные.

        Использует :meth:`Section.walk`, чтобы корректно обходить
        вложенные секции (expand-ы). Заголовок секции тоже учитывается.
        """

        total = 0
        for s in self.sections:
            for s_walk in s.walk():
                for b in s_walk.blocks:
                    total += text_utils.count_words(b.text)
                if s_walk.title:
                    total += text_utils.count_words(s_walk.title)
        return total


def _count_excerpt_includes(html: str) -> int:
    """Подсчитать количество ``excerpt-include`` макросов в HTML.

    ``excerpt-include`` тянет контент С ДРУГОЙ страницы Confluence, что
    делает невозможным безопасный «merge» в один корпус: это уже не текст
    текущей страницы, а внешняя зависимость. Мы её НЕ резолвим, только
    считаем, чтобы coverage честно сообщал о неразрешённых ссылках.
    """

    if not html:
        return 0
    return len(re.findall(
        r'ac:(?:structured-)?macro[^>]*ac:name="excerpt-include"',
        html,
        flags=re.IGNORECASE,
    ))


def load_confluence_doc(
    path: Path,
    *,
    include_view: bool = False,
) -> ConfluenceDoc:
    """Загрузить JSON Confluence REST API и разобрать storage.

    По умолчанию используется только :data:`body.storage`. ``body.view``
    содержит уже отрисованный HTML, в котором:

    * динамически резолвятся ``excerpt-include`` (контент с ДРУГИХ
      страниц) — это cross-page merge, который мы НЕ делаем;
    * дублируется контент ``expand`` в виде обычных блоков — задвоение
      до 497 точных совпадений на одной странице;
    * попадает шум, не устранённый на стороне Confluence.

    Поэтому ``include_view=False`` по умолчанию. Если кто-то всё же
    хочет включить view, он ОБЯЗАН явно передать ``include_view=True``
    и принять, что в корпус может попасть cross-page контент.

    Параметры:
        path: путь к JSON-выгрузке Confluence REST API
            (``GET /rest/api/content/{id}?expand=body.storage,body.view``).
        include_view: разрешить «довкладывать» в корпус уникальные секции
            из ``body.view``. По умолчанию ``False``. Даже при
            ``include_view=True`` мы НЕ добавляем секции, в которых
            встретился ``excerpt-include`` (cross-page) — это ловится
            на этапе парсинга view и режектится.

    Все «служебные» поля игнорируются. ``body.storage`` и ``body.view``
    могут приходить как строкой, так и объектом
    ``{"value": ..., "representation": ...}``.
    """

    raw = path.read_text(encoding="utf-8")
    data = json.loads(raw)
    body = data.get("body") or {}

    def _extract(value) -> str:
        if value is None:
            return ""
        if isinstance(value, str):
            return value
        if isinstance(value, dict):
            inner = value.get("value")
            if isinstance(inner, str):
                return inner
            return ""
        return str(value)

    storage_html = _extract(body.get("storage"))
    view_html = _extract(body.get("view"))

    storage_hash = hashing.dict_sha256({"src": "storage", "v": storage_html})
    view_hash = hashing.dict_sha256({"src": "view", "v": view_html})

    # Считаем excerpt-include в storage: эти макросы тащат контент
    # С ДРУГОЙ страницы и потому не могут быть резолвлены в текущем
    # корпусе. Значение попадает в ``coverage`` и в отчёт сборки.
    unresolved_excerpt_includes = _count_excerpt_includes(storage_html)

    storage_blocks = parse_html_to_blocks(storage_html, source_kind="storage")
    storage_sections = build_sections_from_blocks(storage_blocks)
    storage_sections = filter_noise_sections(storage_sections)

    def collect_all(items: List[Section]) -> Iterable[Section]:
        for s in items:
            yield s
            yield from collect_all(s.children)

    def collect_all_blocks(items: Iterable[Section]) -> Iterable[Block]:
        for s in items:
            for b in s.blocks:
                yield b
            yield from collect_all_blocks(s.children)

    def section_signature(sec: Section) -> str:
        return text_utils.normalize_whitespace(
            " ".join(b.text for b in sec.blocks) + " " + sec.title
        )

    def content_signature(sec: Section) -> str:
        return text_utils.normalize_whitespace(
            " ".join(b.text for b in sec.blocks)
        )

    storage_signatures = {
        section_signature(s) for s in collect_all(storage_sections)
    }
    storage_content_sigs = {
        content_signature(s) for s in collect_all(storage_sections) if content_signature(s)
    }
    # Собираем сигнатуры отдельных блоков — для более тонкой дедупликации,
    # когда view «развернул» expand в обычные параграфы.
    storage_block_sigs = {
        text_utils.normalize_whitespace(b.text)
        for b in collect_all_blocks(storage_sections)
        if text_utils.normalize_whitespace(b.text)
    }

    filtered_view: List[Section] = []
    view_blocks_total = 0
    view_excerpt_rejected = 0
    if include_view:
        view_blocks = parse_html_to_blocks(view_html, source_kind="view")
        view_blocks_total = len(view_blocks)
        view_sections = build_sections_from_blocks(view_blocks)
        view_sections = filter_noise_sections(view_sections)

        # Удаляем из view те секции, чей нормализованный текст уже есть
        # в storage. Уровни дедупликации:
        #  1) точная сигнатура секции (title + blocks);
        #  2) сигнатура только содержимого секции;
        #  3) если все блоки секции уже есть в storage — секция дублирована;
        #  4) внутри «уникальной» view-секции удаляем блоки, которые уже
        #     присутствуют в storage (включая вложенные подсекции), чтобы
        #     совпадающий контент не задвоился.
        def collect_unique(items: List[Section], out: List[Section]) -> None:
            for s in items:
                sig = section_signature(s)
                content_sig = content_signature(s)
                non_empty_blocks = [
                    b for b in s.blocks if text_utils.normalize_whitespace(b.text)
                ]
                blocks_all_seen = bool(non_empty_blocks) and all(
                    text_utils.normalize_whitespace(b.text) in storage_block_sigs
                    for b in non_empty_blocks
                )
                is_dup = False
                if sig and sig in storage_signatures:
                    is_dup = True
                elif content_sig and content_sig in storage_content_sigs:
                    is_dup = True
                elif blocks_all_seen:
                    is_dup = True
                if is_dup:
                    collect_unique(s.children, out)
                    continue

                # Оставляем секцию, но удаляем уже известные блоки/подсекции.
                seen_blocks = [
                    b for b in s.blocks
                    if text_utils.normalize_whitespace(b.text) not in storage_block_sigs
                ]
                unique_children: List[Section] = []
                collect_unique(s.children, unique_children)

                if not seen_blocks and not unique_children:
                    # Секция полностью дублировалась на уровне блоков и
                    # подсекций — пропускаем.
                    continue

                deduped = Section(
                    title=s.title,
                    level=s.level,
                    blocks=seen_blocks,
                    children=unique_children,
                )
                out.append(deduped)

        collect_unique(view_sections, filtered_view)
    else:
        # Даже когда view отключён по умолчанию, считаем excerpt-include
        # в нём: это даёт coverage полное представление о cross-page
        # зависимостях, которые НЕ были разрешены.
        view_excerpt_rejected = _count_excerpt_includes(view_html)

    coverage = {
        "storage_blocks": len(storage_blocks),
        "view_blocks": view_blocks_total,
        "storage_sections": len(storage_sections),
        "view_sections_added": len(filtered_view),
        "unresolved_excerpt_includes": unresolved_excerpt_includes,
        "view_excerpt_includes_unresolved": view_excerpt_rejected,
        "view_excluded": not include_view,
        "view_included": include_view,
    }

    title = data.get("title") or ""
    version_info = (data.get("version") or {}).get("number")
    return ConfluenceDoc(
        page_id=str(data.get("id") or ""),
        title=title,
        version=version_info,
        storage_hash=storage_hash,
        view_hash=view_hash,
        storage_chars=len(storage_html),
        view_chars=len(view_html),
        sections=storage_sections + filtered_view,
        coverage=coverage,
        unresolved_excerpt_includes=unresolved_excerpt_includes,
    )


__all__ = [
    "Block",
    "Section",
    "ConfluenceDoc",
    "load_confluence_doc",
    "parse_html_to_blocks",
    "build_sections_from_blocks",
    "filter_noise_sections",
]


# Внутренние утилиты (не экспортируются, но помогают в тестах).


def _flatten_sections_for_text(sections: List[Section]) -> List[Tuple[str, int, str]]:
    """Плоский список (title, level, text) по всем секциям."""

    out: List[Tuple[str, int, str]] = []
    for s in sections:
        out.append((s.title, s.level, "\n".join(b.text for b in s.blocks)))
        out.extend(_flatten_sections_for_text(s.children))
    return out
