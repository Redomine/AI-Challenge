"""Регрессионные тесты: дедупликация PDF-таблиц «проект ↔ сервер».

Этот модуль закрывает Day-24 indexing issue: когда HTML-выгрузка
Confluence и PDF отдаются одновременно, PDF содержит те же таблицы
сопоставления «проект ↔ сервер» в виде плоского текста. Это
порождает неоднозначные чанки вроде «``STLB-OK1`` рядом с тремя
``revit-XXX``-заголовками», где индекс не может сказать, к какому
именно серверу относится проект.

``docindexing.pdf_server_table`` детектирует «серверные» таблицы в
тексте PDF и удаляет только те, чей набор ``revit-XXX``-колонок уже
покрыт HTML. Остальное содержимое PDF (вступления, инструкции,
RSN.ini, заголовки разделов, футеры) сохраняется без изменений.

Эти тесты проверяют:

* минимальный детектор ``_find_block_boundaries``;
* функции :func:`strip_server_table_blocks` и
  :func:`strip_server_table_blocks_in_pages`;
* поведение :func:`corpus.build_corpus` (юниты PDF получают
  почищенный текст, остальные источники не задеты);
* end-to-end сценарий с реальными файлами из задачи (HTML и PDF
  из ``C:/Users/Mankaev_r/Desktop/Выгрузка страниц``).
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from docindexing import corpus as corpus_mod  # noqa: E402
from docindexing import pdf_server_table as pst  # noqa: E402
from docindexing.confluence import ConfluenceDoc  # noqa: E402
from docindexing.confluence_html import load_confluence_html_doc  # noqa: E402
from docindexing.pdf_text import PdfDoc, PdfPage  # noqa: E402


# ----------------------------------------------------------------------
# Хелперы для синтетических данных
# ----------------------------------------------------------------------


def _empty_confluence_doc() -> ConfluenceDoc:
    """Минимальный ConfluenceDoc без секций — чтобы ``build_corpus``
    не падал и не вносил лишних юнитов."""

    return ConfluenceDoc(
        page_id="SYNTH",
        title="Synthetic",
        version=1,
        storage_hash="x" * 64,
        view_hash="y" * 64,
        storage_chars=0,
        view_chars=0,
        sections=[],
        coverage={},
        unresolved_excerpt_includes=0,
    )


# ----------------------------------------------------------------------
# Тесты детектора блоков
# ----------------------------------------------------------------------


class FindBlockBoundariesTest(unittest.TestCase):
    """Детектор находит таблицы и не путает «случайные» revit-XXX."""

    def test_multi_column_block_with_severparator_title(self) -> None:
        """«Сервера revit-702 revit-703 revit-704» + Проекты 2022/2024."""

        lines = [
            "Бюро комплексного проектирования социальных объектов",
            "Наименование",
            "Сервера revit-702 revit-703 revit-704",
            "Проекты 2022",
            "BUTV-OK1",
            "STLB-OK1",
            "Проекты 2024",
            "DSNR-OK1",
            "Что делать, если сервер не отображается?",
        ]
        blocks = pst._find_block_boundaries(lines)
        self.assertEqual(len(blocks), 1)
        start, end, cols = blocks[0]
        # Заголовок «Наименование» + «Сервера …» — это начало блока.
        self.assertEqual(start, 1)
        # «Что делать…» — это конец (не входит в блок).
        self.assertEqual(end, len(lines) - 1)
        self.assertEqual(
            cols, ["revit-702", "revit-703", "revit-704"]
        )

    def test_single_server_table_with_one_rev(self) -> None:
        """Таблица с одной колонкой ``revit-201`` тоже ловится."""

        lines = [
            "Общие серверы",
            "Наименование",
            "Сервера",
            "revit-201",
            "Тестовый, храним",
            "Проекты 2022",
            "PRKS-05.1",
            "Проекты 2024 -",
            "Проектный институт",
        ]
        blocks = pst._find_block_boundaries(lines)
        self.assertEqual(len(blocks), 1)
        start, end, cols = blocks[0]
        self.assertEqual(cols, ["revit-201"])
        # Блок обрывается на «Проектный институт».
        self.assertEqual(end, 8)

    def test_random_rev_token_in_instructions_is_not_a_table(self) -> None:
        """Упоминание revit-XXX в инструкции не считается таблицей."""

        lines = [
            "Что делать, если сервер не отображается?",
            "Добавьте с новой строки адрес серверов (например revit-201).",
            "Это строка с revit-202 но без «Проекты YYYY».",
            "Поэтому это не таблица.",
        ]
        blocks = pst._find_block_boundaries(lines)
        self.assertEqual(blocks, [])

    def test_multiple_tables_on_same_page(self) -> None:
        """Несколько таблиц подряд на одной странице дают несколько блоков."""

        lines = [
            "Наименование",
            "Сервера revit-202 revit-203 revit-204 revit-209",
            "Проекты 2022",
            "PRKS-09",
            "Проекты 2024",
            "VTNK-03",
            "Бюро комплексного проектирования",
            "Наименование",
            "Сервера revit-702 revit-703 revit-704",
            "Проекты 2022",
            "STLB-OK1",
            "Проекты 2024",
            "DSNR-OK1",
        ]
        blocks = pst._find_block_boundaries(lines)
        self.assertEqual(len(blocks), 2)
        # Первый блок: revit-202..209.
        self.assertEqual(blocks[0][2], ["revit-202", "revit-203", "revit-204", "revit-209"])
        # Второй блок: revit-702..704.
        self.assertEqual(blocks[1][2], ["revit-702", "revit-703", "revit-704"])


# ----------------------------------------------------------------------
# Тесты функции strip_server_table_blocks
# ----------------------------------------------------------------------


class StripServerTableBlocksTest(unittest.TestCase):
    """Удаление блоков с учётом подмножества HTML-колонок."""

    HTML_COLS = ["revit-701", "revit-702", "revit-703", "revit-704"]

    def test_strips_block_subset_of_html(self) -> None:
        text = (
            "Бюро комплексного проектирования социальных объектов\n"
            "Наименование\n"
            "Сервера revit-702 revit-703 revit-704\n"
            "Проекты 2022\n"
            "BUTV-OK1\n"
            "STLB-OK1\n"
            "Проекты 2024\n"
            "DSNR-OK1\n"
            "Что делать, если сервер не отображается?\n"
            "Если в Вашем списке серверов отсутствуют сервера:\n"
            "Вам необходимо создать файл RSN.ini.\n"
        )
        result = pst.strip_server_table_blocks(
            text, html_columns=self.HTML_COLS
        )
        # Серверная таблица удалена.
        self.assertNotIn("STLB-OK1", result.cleaned_text)
        self.assertNotIn("revit-702", result.cleaned_text)
        # Остальное сохранено.
        self.assertIn("Что делать, если сервер не отображается?", result.cleaned_text)
        self.assertIn("Вам необходимо создать файл RSN.ini.", result.cleaned_text)
        self.assertIn(
            "Бюро комплексного проектирования социальных объектов",
            result.cleaned_text,
        )
        # Сводка удалённых блоков.
        self.assertEqual(len(result.stripped_blocks), 1)
        b = result.stripped_blocks[0]
        self.assertEqual(
            b.columns, ("revit-702", "revit-703", "revit-704")
        )
        self.assertTrue(b.matched_html)

    def test_keeps_block_with_columns_not_in_html(self) -> None:
        """Таблица с уникальными колонками (не в HTML) НЕ удаляется."""

        text = (
            "Специальный раздел\n"
            "Наименование\n"
            "Сервера revit-901 revit-902\n"
            "Проекты 2022\n"
            "PROJ-1\n"
            "PROJ-2\n"
        )
        result = pst.strip_server_table_blocks(
            text, html_columns=self.HTML_COLS
        )
        self.assertIn("revit-901", result.cleaned_text)
        self.assertIn("revit-902", result.cleaned_text)
        self.assertIn("PROJ-1", result.cleaned_text)
        self.assertEqual(result.stripped_blocks, ())

    def test_empty_html_columns_returns_text_unchanged(self) -> None:
        """Пустой ``html_columns`` оставляет текст без изменений."""

        text = (
            "Наименование\n"
            "Сервера revit-702 revit-703 revit-704\n"
            "Проекты 2022\n"
            "STLB-OK1\n"
        )
        result = pst.strip_server_table_blocks(text, html_columns=[])
        self.assertEqual(result.cleaned_text, text)
        self.assertEqual(result.stripped_blocks, ())
        # ``None`` тоже не меняет текст.
        result = pst.strip_server_table_blocks(text, html_columns=())
        self.assertEqual(result.cleaned_text, text)

    def test_empty_text_returns_empty(self) -> None:
        result = pst.strip_server_table_blocks("", html_columns=self.HTML_COLS)
        self.assertEqual(result.cleaned_text, "")
        self.assertEqual(result.stripped_blocks, ())

    def test_table_with_partial_overlap_is_kept(self) -> None:
        """Таблица с частичным пересечением HTML-колонок НЕ удаляется.

        Консервативный контракт: удаляем только блоки, чьи колонки —
        полное подмножество HTML. Это защищает от ошибочного
        удаления, когда в таблице есть «уникальные» revit-колонки,
        которых в HTML нет.
        """

        text = (
            "Наименование\n"
            "Сервера revit-703 revit-704 revit-905\n"
            "Проекты 2022\n"
            "STLB-OK1\n"
            "NEW-ONLY\n"
        )
        result = pst.strip_server_table_blocks(
            text, html_columns=self.HTML_COLS
        )
        # Блок НЕ удалён, потому что revit-905 нет в HTML.
        self.assertIn("STLB-OK1", result.cleaned_text)
        self.assertIn("revit-905", result.cleaned_text)
        self.assertEqual(result.stripped_blocks, ())

    def test_keeps_instructions_and_section_headings(self) -> None:
        """Текст инструкций RSN.ini и заголовки разделов сохраняются."""

        text = (
            "Наименование\n"
            "Сервера revit-702 revit-703 revit-704\n"
            "Проекты 2022\n"
            "STLB-OK1\n"
            "Что делать, если сервер не отображается?\n"
            "Если в Вашем списке серверов отсутствуют сервера:\n"
            "Вам необходимо создать файл RSN.ini.\n"
            "Это можно делать при открытом Revit.\n"
            "Для этого:\n"
            "1. Откройте два проводника\n"
            "2. В строке поиска у первого проводника введите "
            "C:\\ProgramData\\Autodesk\\Revit Server 2022\\Config\\\n"
            "3. Сохраните файл (ctrl+s).\n"
        )
        result = pst.strip_server_table_blocks(
            text, html_columns=self.HTML_COLS
        )
        # Все инструкции на месте.
        for must_have in (
            "Что делать, если сервер не отображается?",
            "Вам необходимо создать файл RSN.ini.",
            "Это можно делать при открытом Revit.",
            "1. Откройте два проводника",
            "C:\\ProgramData\\Autodesk\\Revit Server 2022\\Config\\",
            "ctrl+s",
        ):
            self.assertIn(must_have, result.cleaned_text)


class StripPagesTest(unittest.TestCase):
    """Многостраничный вариант с подшивкой номера страницы в блоки."""

    def test_each_block_carries_correct_page_number(self) -> None:
        pages = [
            (1, "Наименование\nСервера revit-702 revit-703 revit-704\nПроекты 2022\nSTLB-OK1\n"),
            (
                2,
                "Что делать, если сервер не отображается?\nВам необходимо создать файл RSN.ini.\n",
            ),
        ]
        cleaned, blocks = pst.strip_server_table_blocks_in_pages(
            pages, html_columns=["revit-702", "revit-703", "revit-704"]
        )
        self.assertEqual(len(cleaned), 2)
        self.assertEqual(cleaned[0][0], 1)
        self.assertNotIn("STLB-OK1", cleaned[0][1])
        self.assertEqual(cleaned[1][0], 2)
        self.assertIn("RSN.ini", cleaned[1][1])
        self.assertEqual(len(blocks), 1)
        self.assertEqual(blocks[0].page_number, 1)
        self.assertEqual(
            blocks[0].columns, ("revit-702", "revit-703", "revit-704")
        )


# ----------------------------------------------------------------------
# Интеграция с ``corpus.build_corpus``
# ----------------------------------------------------------------------


class CorpusBuildIntegrationTest(unittest.TestCase):
    """``build_corpus`` применяет дедупликацию, когда HTML задан."""

    def _html_doc_with_columns(self, cols):
        """Собрать минимальный ConfluenceHtmlDoc с заданными колонками.

        По каждой колонке добавляем одну relation; ``build_corpus``
        берёт множество ``r.column_header`` через множественное
        понимание, поэтому такого набора достаточно.
        """

        from docindexing.confluence_html import (
            ConfluenceHtmlDoc,
            ConfluenceHtmlRelation,
        )

        relations = []
        for i, col in enumerate(cols):
            relations.append(
                ConfluenceHtmlRelation(
                    page_id="SYNTH",
                    section_path=("Root",),
                    row_label="Проекты 2022",
                    column_header=col,
                    project_code=f"X-{i + 1}",
                )
            )
        return ConfluenceHtmlDoc(
            page_id="SYNTH",
            title="Synthetic",
            source_path=Path("synth.html"),
            relations=relations,
            tables_parsed=1,
            section_header_rows=0,
        )

    def test_build_corpus_strips_pdf_when_columns_match(self) -> None:
        html_cols = ["revit-702", "revit-703", "revit-704"]
        html_doc = self._html_doc_with_columns(html_cols)
        pdf = PdfDoc(
            path=Path("s.pdf"),
            pages=[
                PdfPage(
                    page_number=2,
                    text=(
                        "Бюро комплексного проектирования социальных объектов\n"
                        "Наименование\n"
                        "Сервера revit-702 revit-703 revit-704\n"
                        "Проекты 2022\n"
                        "STLB-OK1\n"
                        "BUTV-OK1\n"
                        "Проекты 2024\n"
                        "DSNR-OK1\n"
                        "Что делать, если сервер не отображается?\n"
                        "Вам необходимо создать файл RSN.ini.\n"
                    ),
                ),
            ],
            raw_char_count=200,
            title_hint="Sample",
        )

        corpus_obj = corpus_mod.build_corpus(
            confluence_doc=_empty_confluence_doc(),
            pdf_doc=pdf,
            pdf_title="sample",
            confluence_html_doc=html_doc,
        )

        pdf_units = [u for u in corpus_obj.units if u.source == "pdf"]
        self.assertEqual(len(pdf_units), 1)
        self.assertNotIn("STLB-OK1", pdf_units[0].text)
        # «Что делать…» и «RSN.ini» сохранены.
        self.assertIn("Что делать, если сервер не отображается?", pdf_units[0].text)
        self.assertIn("RSN.ini", pdf_units[0].text)
        # Метрика дедупликации присутствует.
        self.assertIn("pdf_dedup", corpus_obj.sources)
        self.assertEqual(corpus_obj.sources["pdf_dedup"]["matched_blocks"], 1)
        self.assertEqual(
            corpus_obj.sources["pdf_dedup"]["stripped_columns"],
            ["revit-702", "revit-703", "revit-704"],
        )

    def test_build_corpus_keeps_pdf_when_no_html(self) -> None:
        """Без HTML PDF остаётся как есть (обратная совместимость)."""

        pdf = PdfDoc(
            path=Path("s.pdf"),
            pages=[
                PdfPage(
                    page_number=2,
                    text=(
                        "Сервера revit-702 revit-703 revit-704\n"
                        "Проекты 2022\n"
                        "STLB-OK1\n"
                    ),
                ),
            ],
            raw_char_count=100,
            title_hint="",
        )

        corpus_obj = corpus_mod.build_corpus(
            confluence_doc=_empty_confluence_doc(),
            pdf_doc=pdf,
            pdf_title="sample",
            confluence_html_doc=None,
        )
        pdf_units = [u for u in corpus_obj.units if u.source == "pdf"]
        self.assertEqual(len(pdf_units), 1)
        # Без HTML — таблица сохранена полностью.
        self.assertIn("STLB-OK1", pdf_units[0].text)
        self.assertIn("revit-702", pdf_units[0].text)
        # Метрики дедупликации нет.
        self.assertNotIn("pdf_dedup", corpus_obj.sources)

    def test_build_corpus_strips_only_matching_blocks(self) -> None:
        """Когда колонки PDF НЕ покрыты HTML — PDF не трогается."""

        # HTML покрывает только revit-703.
        html_doc = self._html_doc_with_columns(["revit-703"])
        # PDF содержит таблицу с revit-901, revit-902 (нет в HTML) и
        # таблицу с revit-703 (есть в HTML). Удаляем только вторую.
        pdf = PdfDoc(
            path=Path("s.pdf"),
            pages=[
                PdfPage(
                    page_number=1,
                    text=(
                        "Наименование\n"
                        "Сервера revit-901 revit-902\n"
                        "Проекты 2022\n"
                        "ONLY-1\n"
                        "ONLY-2\n"
                        "Наименование\n"
                        "Сервера revit-703\n"
                        "Проекты 2022\n"
                        "MATCHED-1\n"
                    ),
                ),
            ],
            raw_char_count=100,
            title_hint="",
        )

        corpus_obj = corpus_mod.build_corpus(
            confluence_doc=_empty_confluence_doc(),
            pdf_doc=pdf,
            pdf_title="sample",
            confluence_html_doc=html_doc,
        )

        pdf_units = [u for u in corpus_obj.units if u.source == "pdf"]
        self.assertEqual(len(pdf_units), 1)
        text = pdf_units[0].text
        # Первая таблица (не в HTML) сохранена.
        self.assertIn("ONLY-1", text)
        self.assertIn("ONLY-2", text)
        # Вторая таблица (в HTML) удалена.
        self.assertNotIn("MATCHED-1", text)
        self.assertNotIn("revit-703", text)

    def test_fully_stripped_page_is_dropped(self) -> None:
        """Если вся страница — серверная таблица, она не идёт в корпус."""

        html_doc = self._html_doc_with_columns(["revit-901"])
        pdf = PdfDoc(
            path=Path("s.pdf"),
            pages=[
                PdfPage(
                    page_number=1,
                    text=(
                        "Наименование\n"
                        "Сервера revit-901\n"
                        "Проекты 2022\n"
                        "X-1\n"
                        "X-2\n"
                    ),
                ),
                PdfPage(
                    page_number=2,
                    text="Это страница без серверов.",
                ),
            ],
            raw_char_count=100,
            title_hint="",
        )
        corpus_obj = corpus_mod.build_corpus(
            confluence_doc=_empty_confluence_doc(),
            pdf_doc=pdf,
            pdf_title="sample",
            confluence_html_doc=html_doc,
        )
        pdf_units = [u for u in corpus_obj.units if u.source == "pdf"]
        # Только page 2 остался; page 1 ушла целиком.
        self.assertEqual(len(pdf_units), 1)
        self.assertEqual(pdf_units[0].pdf_page, 2)
        self.assertIn("без серверов", pdf_units[0].text)


# ----------------------------------------------------------------------
# End-to-end на реальных файлах из задачи Day 24
# ----------------------------------------------------------------------


class RealFilesDedupTest(unittest.TestCase):
    """Сквозная проверка на реальных файлах.

    Используется пара ``Confluence HTML`` + ``PDF`` из
    ``C:\\Users\\Mankaev_r\\Desktop\\Выгрузка страниц\\``. Если
    файлов нет — тест пропускается (CI без магистрали).
    """

    REAL_HTML = Path(
        "C:/Users/Mankaev_r/Desktop/Выгрузка страниц/"
        "СОД. Серверы - Проектирование. База знаний - Confluence.html"
    )
    REAL_PDF = Path(
        "C:/Users/Mankaev_r/Desktop/Выгрузка страниц/"
        "СОД. Серверы - Проектирование. База знаний - Confluence.pdf"
    )

    def _html_doc(self):
        if not self.REAL_HTML.exists():
            self.skipTest(f"Real HTML not found: {self.REAL_HTML}")
        return load_confluence_html_doc(self.REAL_HTML)

    def _pdf_doc(self):
        from docindexing.pdf_text import extract_pdf

        if not self.REAL_PDF.exists():
            self.skipTest(f"Real PDF not found: {self.REAL_PDF}")
        return extract_pdf(self.REAL_PDF)

    def test_pdf_dedup_drops_only_stubtable_blocks(self) -> None:
        """На реальных файлах удаляются только серверные таблицы,
        остальное содержимое PDF сохранено."""

        html_doc = self._html_doc()
        pdf_doc = self._pdf_doc()
        html_cols = sorted({r.column_header for r in html_doc.relations})
        # Этих колонок точно нет в реальном HTML (только ревитские).
        self.assertIn("revit-703", html_cols)
        pages = [(p.page_number, p.text) for p in pdf_doc.pages]
        cleaned, blocks = pst.strip_server_table_blocks_in_pages(
            pages, html_columns=html_cols
        )
        # Колонки, попавшие в удалённые блоки, — все из HTML.
        html_set = set(html_cols)
        for b in blocks:
            self.assertTrue(
                set(b.columns).issubset(html_set),
                f"block {b} has columns outside HTML: {set(b.columns) - html_set}",
            )
        # STLB-OK1 удалён из всех страниц.
        for pn, text in cleaned:
            self.assertNotIn(
                "STLB-OK1",
                text,
                f"STLB-OK1 still present on page {pn}",
            )
        # Заголовки инструкций и самой инструкции — сохранены.
        all_text = "\n".join(t for _, t in cleaned)
        self.assertIn(
            "Что делать, если сервер не отображается?", all_text
        )
        self.assertIn("RSN.ini", all_text)

    def test_build_corpus_has_unique_stlb_ok1_source(self) -> None:
        """В корпусе ``STLB-OK1`` встречается ровно один раз и только
        в ``confluence_html``-юните, привязанном к ``revit-703``."""

        html_doc = self._html_doc()
        pdf_doc = self._pdf_doc()
        corpus_obj = corpus_mod.build_corpus(
            confluence_doc=_empty_confluence_doc(),
            pdf_doc=pdf_doc,
            pdf_title="real",
            confluence_html_doc=html_doc,
        )
        stlb_units = [
            u
            for u in corpus_obj.units
            if "STLB-OK1" in u.text
        ]
        self.assertEqual(
            len(stlb_units),
            1,
            f"STLB-OK1 must appear in exactly one unit, got {len(stlb_units)}",
        )
        only = stlb_units[0]
        self.assertEqual(only.source, "confluence_html")
        self.assertEqual(only.title, "Проекты 2022 / revit-703")
        # Других серверов в тексте этого юнита быть не должно.
        for forbidden in ("revit-702", "revit-704"):
            self.assertNotIn(
                forbidden,
                only.text,
                f"STLB-OK1 chunk should not mention {forbidden}",
            )

    def test_no_pdf_chunk_mentions_three_server_headers(self) -> None:
        """Ни один PDF-чанк не должен содержать три revit-заголовка."""

        html_doc = self._html_doc()
        pdf_doc = self._pdf_doc()
        corpus_obj = corpus_mod.build_corpus(
            confluence_doc=_empty_confluence_doc(),
            pdf_doc=pdf_doc,
            pdf_title="real",
            confluence_html_doc=html_doc,
        )
        pdf_units = [u for u in corpus_obj.units if u.source == "pdf"]
        for u in pdf_units:
            # Сколько revit-токенов в чанке? Не должно быть >= 3.
            import re

            tokens = sorted(set(re.findall(r"revit-\d+", u.text)))
            self.assertLess(
                len(tokens),
                3,
                f"PDF chunk on page {u.pdf_page} has 3+ revit columns: {tokens}",
            )

    def test_no_index_chunk_has_three_pdf_server_headers(self) -> None:
        """End-to-end: после полного прогона сборки (с фейковым Ollama)
        ни в одной стратегии не должно быть PDF-чанка, который
        содержит три revit-XXX в одном тексте.

        Здесь используется реальный сквозной сценарий ``build_index``,
        чтобы убедиться, что дедупликация выживает фиксирование, нарезку
        и индексирование.
        """

        html_doc = self._html_doc()
        pdf_doc = self._pdf_doc()

        # Хелперы для локального HTTP-фейка Ollama и конфига.
        import http.server
        import json
        import socket
        import tempfile
        import threading

        from docindexing import build as build_mod
        from docindexing.config import RunConfig

        def free_port() -> int:
            with socket.socket() as s:
                s.bind(("127.0.0.1", 0))
                return s.getsockname()[1]

        port = free_port()

        class H(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a, **k):
                return

            def do_GET(self):  # noqa: N802
                if self.path == "/api/tags":
                    self.send_response(200)
                    self.end_headers()
                    self.wfile.write(b"{}")
                    return
                self.send_response(404)
                self.end_headers()

            def do_POST(self):  # noqa: N802
                length = int(self.headers.get("Content-Length", "0"))
                payload = json.loads(self.rfile.read(length) or b"{}")
                inputs = payload.get("input", [])
                vecs = [
                    [0.01 * (j + 1), 0.02 * (j + 1), 0.03 * (j + 1), 0.04 * (j + 1)]
                    for j in range(len(inputs))
                ]
                body = {"model": "fake", "embeddings": vecs}
                data = json.dumps(body).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

        httpd = http.server.HTTPServer(
            ("127.0.0.1", port), H, bind_and_activate=False
        )
        httpd.allow_reuse_address = True
        httpd.server_bind()
        httpd.server_activate()
        th = threading.Thread(target=httpd.serve_forever, daemon=True)
        th.start()
        try:
            with tempfile.TemporaryDirectory() as td:
                tdp = Path(td)
                cf = tdp / "c.json"
                # Минимальный Confluence JSON с одной секцией,
                # чтобы ``build_index`` не упал на чтении ``.json``.
                cf.write_text(
                    json.dumps(
                        {
                            "id": "SYNTH",
                            "type": "page",
                            "title": "Synthetic",
                            "version": {"number": 1},
                            "body": {
                                "storage": {"value": "", "representation": "storage"},
                                "view": {"value": "", "representation": "view"},
                            },
                        }
                    ),
                    encoding="utf-8",
                )
                pdf_path = tdp / "p.pdf"
                # PDF-stub, потому что реальный парсинг pypdf требует
                # валидного PDF; в нашем случае нас интересует только
                # контракт «после дедупликации» — а это обеспечивается
                # самим ``build_corpus``, см. ниже.
                pdf_path.write_bytes(b"%PDF-stub")
                html_p = tdp / "h.html"
                html_p.write_bytes(self.REAL_HTML.read_bytes())

                # Подменяем ``extract_pdf`` на наш реальный PDF.
                from docindexing import build as build_pkg

                original = build_pkg.extract_pdf
                build_pkg.extract_pdf = lambda p: pdf_doc
                try:
                    cfg = RunConfig(
                        confluence_json=cf,
                        confluence_html=html_p,
                        pdf_path=pdf_path,
                        output_dir=tdp / "index_out",
                        ollama_url=f"http://127.0.0.1:{port}",
                        embed_model="fake",
                        fixed_tokens=64,
                        fixed_overlap=8,
                        struct_max_chars=1600,
                        struct_min_chars=200,
                        embed_batch_size=8,
                        embed_max_retries=2,
                        embed_retry_base_delay=0.01,
                        embed_timeout=10.0,
                    )
                    build_mod.build_index(cfg)
                finally:
                    build_pkg.extract_pdf = original

                # Проверяем обе стратегии: fixed и structural.
                import re
                from docindexing.index_store import IndexStore

                for strategy, db_path in (
                    ("fixed", cfg.fixed_db()),
                    ("structural", cfg.structural_db()),
                ):
                    store = IndexStore(db_path)
                    try:
                        rows = store._conn.execute(
                            "SELECT text, pdf_page FROM chunks "
                            "WHERE source = 'pdf'"
                        ).fetchall()
                        for text, pdf_page in rows:
                            tokens = sorted(set(re.findall(r"revit-\d+", text)))
                            self.assertLess(
                                len(tokens),
                                3,
                                f"{strategy}: PDF chunk page={pdf_page} "
                                f"has 3+ revit columns: {tokens}",
                            )
                    finally:
                        store.close()
        finally:
            httpd.shutdown()
            httpd.server_close()

    def test_metrics_record_stripped_blocks(self) -> None:
        """``corpus.sources['pdf_dedup']`` сообщает о снятых блоках."""

        html_doc = self._html_doc()
        pdf_doc = self._pdf_doc()
        corpus_obj = corpus_mod.build_corpus(
            confluence_doc=_empty_confluence_doc(),
            pdf_doc=pdf_doc,
            pdf_title="real",
            confluence_html_doc=html_doc,
        )
        self.assertIn("pdf_dedup", corpus_obj.sources)
        d = corpus_obj.sources["pdf_dedup"]
        self.assertGreaterEqual(d["matched_blocks"], 1)
        self.assertIn("revit-702", d["stripped_columns"])
        self.assertIn("revit-703", d["stripped_columns"])
        self.assertIn("revit-704", d["stripped_columns"])


if __name__ == "__main__":
    unittest.main()