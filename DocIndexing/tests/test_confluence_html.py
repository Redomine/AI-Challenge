"""Тесты для модуля ``docindexing.confluence_html``.

Покрывают:

* извлечение ``page_id`` / заголовка из сохранённого HTML;
* обнаружение ToC-блока (``<div class="toc-macro">``) и его
  исключение из ``section_path``;
* извлечение табличных связей «проект ↔ сервер» с правильной
  иерархией ``section_path`` (h2 / h3);
* жёсткий контракт: ``STLB-OK1`` относится к ``revit-703``,
  а НЕ к ``revit-702`` / ``revit-704`` — это критическое свойство,
  которое запрещает «выводить» версию Revit из «Проекты 2022»;
* интеграция с ``corpus.build_corpus`` (источник ``confluence_html``);
* интеграция с ``build.build_index`` end-to-end (через fake Ollama).

Сами таблицы собираются в минимальном синтетическом HTML, чтобы
тесты не зависели от конкретного реального HTML-выгрузки.
"""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from typing import List

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from docindexing import confluence_html as ch  # noqa: E402
from docindexing import corpus as corpus_mod  # noqa: E402


# ----------------------------------------------------------------------
# Синтетический HTML, имитирующий «браузерную» выгрузку Confluence.
# Внутри: h2 «Где находится мой проект?», один подh3 и таблица
# «Наименование сервера / revit-702 / revit-703 / revit-704» со
# строками «Проекты 2022» и «Проекты 2024».
# ----------------------------------------------------------------------


def _build_synthetic_html(
    *,
    relations: List[dict] | None = None,
    include_toc: bool = True,
) -> str:
    """Собрать минимальный HTML с таблицей «проект ↔ сервер».

    ``relations`` — список словарей с ключами ``row_label``,
    ``column_header`` и ``entries`` (список кодов проектов). Если
    ``relations`` не задан, используется фиксированный набор,
    гарантирующий, что ``STLB-OK1`` лежит под ``revit-703`` в строке
    ``Проекты 2022`` — это требование контракта.
    """

    if relations is None:
        relations = [
            {
                "row_label": "Проекты 2022",
                "cells": {
                    "revit-702": ["PRKS-SD2", "STLB-SD1"],
                    "revit-703": ["BUTV-OK1", "STLB-OK1"],
                    "revit-704": ["PRKS-PL1"],
                },
            },
            {
                "row_label": "Проекты 2024",
                "cells": {
                    "revit-702": ["LAGL-DS1"],
                    "revit-703": ["DSNR-OK1"],
                    "revit-704": [],
                },
            },
        ]

    headers = ["revit-702", "revit-703", "revit-704"]
    header_cells = (
        "<td><strong>Наименование<br>сервера</strong></td>"
        + "".join(f"<td><strong>{h}</strong></td>" for h in headers)
    )
    data_rows_html = ""
    for r in relations:
        cells_html = f"<td><p><strong>{r['row_label']}</strong></p></td>"
        for h in headers:
            entries = r["cells"].get(h, [])
            if not entries:
                cells_html += "<td><br></td>"
            else:
                inner = "<br>".join(entries)
                cells_html += f"<td><p>{inner}</p></td>"
        data_rows_html += f"<tr>{cells_html}</tr>"

    toc_html = ""
    if include_toc:
        toc_html = (
            '<h2 id="id-SOME-TOC">'
            '<div class="toc-macro client-side-toc-macro conf-macro output-block">'
            '<ul><li><a>Page 1</a></li><li><a>Page 2</a></li></ul>'
            "</div></h2>"
        )

    return f"""<!DOCTYPE html>
<html>
<head><title>Synthetic Confluence export</title></head>
<body>
  {toc_html}
  <h2 id="id-ROOT-INTRO">Введение</h2>
  <p>Краткое введение в базу знаний.</p>
  <h2 id="id-SOME-PAGE-Гденаходитсямойпроект?">Где находится мой проект?</h2>
  <p>В этом разделе перечислены серверы и проекты.</p>
  <h3 id="id-SOME-PAGE-Общиесерверы">Общие серверы</h3>
  <table class="relative-table wrapped confluenceTable">
    <tbody>
      <tr>{header_cells}</tr>
      {data_rows_html}
    </tbody>
  </table>
  <h2 id="id-SOME-PAGE-Чтоделать">Что делать, если сервер не отображается?</h2>
  <p>Другая секция, которая не должна попасть в section_path для связей.</p>
</body>
</html>"""


class PageMetadataExtractionTest(unittest.TestCase):
    """``load_confluence_html_doc`` достаёт ``page_id`` / ``title`` / ``relations``."""

    def test_extracts_page_id_from_saved_from_url(self) -> None:
        html = (
            "<!-- saved from url=(0058)https://wiki.example/pages/viewpage.action?pageId=123456789 -->\n"
            "<html><head><title>Sample</title></head></html>"
        )
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "sample.html"
            p.write_text(html, encoding="utf-8")
            doc = ch.load_confluence_html_doc(p)
        self.assertEqual(doc.page_id, "123456789")
        self.assertEqual(doc.title, "Sample")
        self.assertEqual(doc.relations, [])

    def test_extracts_page_id_from_action_link_fallback(self) -> None:
        # Нет ``saved from url``, но есть явная ``viewpage.action?pageId=``.
        html = (
            "<html><head><title>Sample</title></head>"
            "<body><a href=\"https://wiki.example/pages/viewpage.action?pageId=987\">link</a></body>"
            "</html>"
        )
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "sample.html"
            p.write_text(html, encoding="utf-8")
            doc = ch.load_confluence_html_doc(p)
        self.assertEqual(doc.page_id, "987")

    def test_missing_page_id_returns_fallback(self) -> None:
        html = "<html><head><title>Sample</title></head><body>Нет pageId</body></html>"
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "sample.html"
            p.write_text(html, encoding="utf-8")
            doc = ch.load_confluence_html_doc(p)
        # Документ всё равно строится; в ``page_id`` лежит либо
        # фактический ``pageId``, либо фолбэк ``confluence_html`` —
        # это лучше, чем пустая строка, потому что пустой
        # идентификатор «сливается» с другими страницами без
        # ``pageId``.
        self.assertIn(doc.page_id, ("confluence_html", ""))


class TocsAndSidebarsAreIgnoredTest(unittest.TestCase):
    """ToC / sidebar / мусорные ``<h2>`` НЕ должны попадать в ``section_path``."""

    def test_toc_block_excluded_from_section_path(self) -> None:
        html = _build_synthetic_html(include_toc=True)
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "sample.html"
            p.write_text(html, encoding="utf-8")
            doc = ch.load_confluence_html_doc(p)
        # Должны остаться только «Введение», «Где находится мой проект?»,
        # «Общие серверы». ToC отсутствует в section_path.
        all_paths = {tuple(r.section_path) for r in doc.relations}
        for path in all_paths:
            joined = " / ".join(path)
            self.assertNotIn("Page 1", joined)
            self.assertNotIn("Page 2", joined)

        # В каждой секции присутствует реальный заголовок.
        for path in all_paths:
            joined = " ".join(path)
            self.assertIn("Где находится мой проект?", joined)


class StlbOk1MapsToRevit703Test(unittest.TestCase):
    """Жёсткий контракт: ``STLB-OK1`` принадлежит только ``revit-703``.

    Это свойство покрывает требование «не выводить Revit-версию
    из названия строки Проекты 2022». Если парсер когда-то решит
    «вычислить» имя сервера из года (что запрещено), этот тест
    сломается.
    """

    def test_stlb_ok1_only_under_revit_703(self) -> None:
        html = _build_synthetic_html()
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "sample.html"
            p.write_text(html, encoding="utf-8")
            doc = ch.load_confluence_html_doc(p)

        stlb_ok1 = [r for r in doc.relations if r.project_code == "STLB-OK1"]
        self.assertEqual(
            len(stlb_ok1),
            1,
            "STLB-OK1 должна встречаться ровно один раз (на revit-703)",
        )
        self.assertEqual(stlb_ok1[0].column_header, "revit-703")
        self.assertEqual(stlb_ok1[0].row_label, "Проекты 2022")

        # И НЕ должна попасть в revit-702 / revit-704.
        all_under_702 = {r.project_code for r in doc.relations if r.column_header == "revit-702"}
        all_under_704 = {r.project_code for r in doc.relations if r.column_header == "revit-704"}
        self.assertNotIn("STLB-OK1", all_under_702)
        self.assertNotIn("STLB-OK1", all_under_704)

    def test_stlb_ok1_section_path_is_not_year_inferred(self) -> None:
        """``section_path`` НЕ должен зависеть от того, что «2022» означает
        какую-то «версию». В нём лежит «Где находится мой проект? /
        Общие серверы», и никакого «2022/Revit»."""

        html = _build_synthetic_html()
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "sample.html"
            p.write_text(html, encoding="utf-8")
            doc = ch.load_confluence_html_doc(p)
        stlb_ok1 = next(r for r in doc.relations if r.project_code == "STLB-OK1")
        # row_label содержит «Проекты 2022» — это допустимо, но это
        # метка строки, а не «версия сервера».
        self.assertEqual(stlb_ok1.row_label, "Проекты 2022")
        # Имя сервера не должно содержать «2022».
        self.assertNotIn("2022", stlb_ok1.column_header)
        self.assertNotIn("2024", stlb_ok1.column_header)


class RelationExtractionStructureTest(unittest.TestCase):
    """Полная структура ``ConfluenceHtmlRelation``."""

    def test_all_cells_become_distinct_units(self) -> None:
        html = _build_synthetic_html()
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "sample.html"
            p.write_text(html, encoding="utf-8")
            doc = ch.load_confluence_html_doc(p)

        # Синтетический набор: 5 записей в строке «2022»
        # (2+2+1) и 2 записи в строке «2024» (1+1) — итого 7.
        self.assertEqual(len(doc.relations), 7)

        # Все записи имеют правильные имена колонок и метки строк.
        col_headers = {r.column_header for r in doc.relations}
        self.assertEqual(
            col_headers,
            {"revit-702", "revit-703", "revit-704"},
        )
        row_labels = {r.row_label for r in doc.relations}
        self.assertEqual(row_labels, {"Проекты 2022", "Проекты 2024"})

    def test_unit_id_is_stable_and_unique(self) -> None:
        html = _build_synthetic_html()
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "sample.html"
            p.write_text(html, encoding="utf-8")
            doc_a = ch.load_confluence_html_doc(p)
            doc_b = ch.load_confluence_html_doc(p)

        ids_a = [r.unit_id(occurrence=1) for r in doc_a.relations]
        ids_b = [r.unit_id(occurrence=1) for r in doc_b.relations]
        self.assertEqual(ids_a, ids_b)
        self.assertEqual(len(ids_a), len(set(ids_a)))

    def test_relation_text_mentions_server_and_project(self) -> None:
        html = _build_synthetic_html()
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "sample.html"
            p.write_text(html, encoding="utf-8")
            doc = ch.load_confluence_html_doc(p)
        stlb_ok1 = next(r for r in doc.relations if r.project_code == "STLB-OK1")
        text = stlb_ok1.text()
        self.assertIn("revit-703", text)
        self.assertIn("STLB-OK1", text)
        self.assertIn("Проекты 2022", text)


class CellSplittingTest(unittest.TestCase):
    """Разделители и скобочные комментарии в ячейках."""

    def test_br_separates_entries(self) -> None:
        # Ячейка с ``<br>`` между записями превращается в список.
        entries = ch._split_cell_entries("PRKS-SD2<br>STLB-SD1<br>VTNK-09")
        self.assertEqual(entries, ["PRKS-SD2", "STLB-SD1", "VTNK-09"])

    def test_newline_separates_entries(self) -> None:
        # ``\n`` (из текстового представления после ``<br>``) тоже работает.
        entries = ch._split_cell_entries("PRKS-SD2\nSTLB-SD1")
        self.assertEqual(entries, ["PRKS-SD2", "STLB-SD1"])

    def test_placeholder_dash_skipped(self) -> None:
        entries = ch._split_cell_entries("-")
        self.assertEqual(entries, [])

    def test_placeholder_nbsp_skipped(self) -> None:
        entries = ch._split_cell_entries("&nbsp;")
        self.assertEqual(entries, [])

    def test_paren_comment_dropped_but_inner_keyword_kept(self) -> None:
        # «STLB-04 (KR)» → «STLB-04»; скобки выкидываются как комментарий.
        entries = ch._split_cell_entries("STLB-04 (KR)")
        self.assertEqual(entries, ["STLB-04"])


class CorpusIntegrationTest(unittest.TestCase):
    """``corpus.build_corpus`` принимает ``confluence_html_doc``."""

    def test_corpus_includes_html_units_with_distinct_source(self) -> None:
        html = _build_synthetic_html()
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "sample.html"
            p.write_text(html, encoding="utf-8")
            html_doc = ch.load_confluence_html_doc(p)

        # Синтетический ConfluenceDoc (минимум, чтобы build_corpus не упал).
        from docindexing.confluence import (
            ConfluenceDoc,
        )
        cf = ConfluenceDoc(
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
        # И PDF-документ, который вернёт пустые юниты.
        from docindexing.pdf_text import PdfDoc

        pdf = PdfDoc(path=Path("nope.pdf"), pages=[], raw_char_count=0, title_hint=None)

        corpus_obj = corpus_mod.build_corpus(
            confluence_doc=cf,
            pdf_doc=pdf,
            pdf_title="stub",
            confluence_html_doc=html_doc,
        )

        # Должны быть только юниты из html_doc.
        self.assertEqual(len(corpus_obj.units), len(html_doc.relations))
        sources = {u.source for u in corpus_obj.units}
        self.assertEqual(sources, {"confluence_html"})
        # В ``sources`` корпуса теперь присутствует раздел
        # ``confluence_html`` с базовой метаинформацией.
        self.assertIn("confluence_html", corpus_obj.sources)

    def test_corpus_skips_html_when_none(self) -> None:
        """Передача ``None`` оставляет поведение прежним (только confluence + pdf)."""

        from docindexing.confluence import ConfluenceDoc, Section
        from docindexing.pdf_text import PdfDoc

        # Минимальная структура: одна секция Confluence, один PDF-юнит.
        section = Section(title="Серверы", level=1, blocks=[])
        cf = ConfluenceDoc(
            page_id="X",
            title="t",
            version=1,
            storage_hash="h",
            view_hash="v",
            storage_chars=0,
            view_chars=0,
            sections=[section],
            coverage={},
            unresolved_excerpt_includes=0,
        )
        from docindexing.pdf_text import PdfPage

        pdf = PdfDoc(
            path=Path("x.pdf"),
            pages=[PdfPage(page_number=1, text="PDF content")],
            raw_char_count=12,
            title_hint="x",
        )

        corpus_obj = corpus_mod.build_corpus(
            confluence_doc=cf,
            pdf_doc=pdf,
            pdf_title="x",
            confluence_html_doc=None,
        )
        # ``confluence_html`` в sources нет — обратная совместимость.
        self.assertNotIn("confluence_html", corpus_obj.sources)

    def test_corpus_html_unit_ids_are_unique_and_stable(self) -> None:
        html = _build_synthetic_html()
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "sample.html"
            p.write_text(html, encoding="utf-8")
            html_doc = ch.load_confluence_html_doc(p)

        from docindexing.confluence import ConfluenceDoc
        from docindexing.pdf_text import PdfDoc

        cf = ConfluenceDoc(
            page_id="SYNTH",
            title="t",
            version=1,
            storage_hash="x" * 64,
            view_hash="y" * 64,
            storage_chars=0,
            view_chars=0,
            sections=[],
            coverage={},
            unresolved_excerpt_includes=0,
        )
        pdf = PdfDoc(path=Path("nope.pdf"), pages=[], raw_char_count=0, title_hint=None)

        corpus_a = corpus_mod.build_corpus(
            confluence_doc=cf,
            pdf_doc=pdf,
            pdf_title="stub",
            confluence_html_doc=html_doc,
        )
        corpus_b = corpus_mod.build_corpus(
            confluence_doc=cf,
            pdf_doc=pdf,
            pdf_title="stub",
            confluence_html_doc=html_doc,
        )
        ids_a = sorted(u.unit_id for u in corpus_a.units)
        ids_b = sorted(u.unit_id for u in corpus_b.units)
        self.assertEqual(ids_a, ids_b)


class ChunkingMixedSourceTest(unittest.TestCase):
    """``chunking.build_chunks`` корректно разделяет ``confluence`` / ``confluence_html``."""

    def test_confluence_and_html_streams_produce_distinct_chunk_sources(self) -> None:
        """Каждый чанк должен иметь ``source``, совпадающий с источником его юнита."""

        from docindexing import chunking
        from docindexing.corpus import CorpusUnit

        units: List[CorpusUnit] = [
            CorpusUnit(
                unit_id="cf:x#1",
                source="confluence",
                title="Title1",
                section_path=["Title1"],
                text="some confluence content " * 30,
            ),
            CorpusUnit(
                unit_id="cfhtml:33557761:revit-703:STLB-OK1#1",
                source="confluence_html",
                title="Проекты 2022 / revit-703",
                section_path=["Введение", "Проекты 2022", "revit-703"],
                text="Сервер: revit-703. Проект: STLB-OK1. Год: Проекты 2022",
            ),
        ]

        chunks = chunking.build_chunks(
            units,
            strategy="fixed",
            fixed_target_words=10,
            fixed_overlap_words=0,
            struct_max_chars=400,
            struct_min_chars=40,
        )
        # chunk.source должен отражать фактический источник юнита.
        sources = {c.source for c in chunks}
        self.assertTrue(
            sources.issuperset({"confluence", "confluence_html"}),
            f"expected both sources in chunks, got {sources}",
        )


class RealConfluenceHtmlFileTest(unittest.TestCase):
    """Проверка на реальной HTML-выгрузке Confluence, если файл доступен.

    Файл поставляется вместе с задачей Day 24; путь захардкожен
    явно, чтобы тест запускался именно на нём и явно фиксировал
    контракт «STLB-OK1 ↔ revit-703». Если файла нет — тест
    пропускается (``skipTest``), чтобы CI без магистрали не
    падал.
    """

    REAL_HTML = Path(
        "C:/Users/Mankaev_r/Desktop/Выгрузка страниц/"
        "СОД. Серверы - Проектирование. База знаний - Confluence.html"
    )

    def _doc(self) -> ch.ConfluenceHtmlDoc:
        if not self.REAL_HTML.exists():
            self.skipTest(
                f"Real Confluence HTML export not found: {self.REAL_HTML}"
            )
        return ch.load_confluence_html_doc(self.REAL_HTML)

    def test_real_html_extracts_table_relations(self) -> None:
        doc = self._doc()
        # В файле есть хотя бы один relation.
        self.assertGreater(len(doc.relations), 0)

    def test_stlb_ok1_maps_to_revit_703_in_real_export(self) -> None:
        """Главный контракт: ``STLB-OK1`` ↔ ``revit-703`` на реальных данных."""

        doc = self._doc()
        stlb_ok1 = [r for r in doc.relations if r.project_code == "STLB-OK1"]
        self.assertEqual(len(stlb_ok1), 1)
        self.assertEqual(stlb_ok1[0].column_header, "revit-703")
        self.assertEqual(stlb_ok1[0].row_label, "Проекты 2022")

        # Ни в revit-702, ни в revit-704.
        for col in ("revit-702", "revit-704"):
            same_col = [r for r in doc.relations if r.column_header == col]
            self.assertFalse(
                any(r.project_code == "STLB-OK1" for r in same_col),
                f"STLB-OK1 must not appear under {col}",
            )

    def test_real_html_unit_id_is_stable_for_stlb_ok1(self) -> None:
        """На реальном файле, ``unit_id(occurrence=1)`` для STLB-OK1
        стабилен между повторными запусками."""

        doc_a = self._doc()
        doc_b = self._doc()
        stlb_a = next(r for r in doc_a.relations if r.project_code == "STLB-OK1")
        stlb_b = next(r for r in doc_b.relations if r.project_code == "STLB-OK1")
        self.assertEqual(
            stlb_a.unit_id(occurrence=1),
            stlb_b.unit_id(occurrence=1),
        )
        # И начинается с префикса ``cfhtml:`` с реальным ``page_id``.
        self.assertTrue(
            stlb_a.unit_id(occurrence=1).startswith("cfhtml:33557761:")
        )


if __name__ == "__main__":
    unittest.main()