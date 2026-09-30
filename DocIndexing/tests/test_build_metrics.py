"""Тесты согласованности метрик ``_compute_metrics`` в ``build.py``.

Основная аудиторская проверка:

* ``corpus.words`` должно быть строго равно ``confluence.words + pdf.words``.
* ``sections_total`` должен учитывать вложенные expand-секции рекурсивно,
  плюс отдельно показывать число корневых секций для прозрачности.
* Сырые цифры парсеров должны быть доступны отдельно (``raw_words``),
  чтобы можно было сравнить «что попало в корпус» с «что пришло из источника».

Здесь НЕ проверяются chunking / embeddings / Ollama — это тонкая,
изолированная проверка только блока метрик.
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path
from typing import List

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from docindexing import build as build_mod  # noqa: E402
from docindexing import corpus as corpus_mod  # noqa: E402
from docindexing import text_utils as tu  # noqa: E402
from docindexing.confluence import (  # noqa: E402
    Block,
    ConfluenceDoc,
    Section,
)
from docindexing.corpus import Corpus, CorpusUnit  # noqa: E402
from docindexing.pdf_text import PdfDoc, PdfPage  # noqa: E402


def _block(text: str) -> Block:
    """Минимальная обёртка вокруг текстового блока секции."""

    return Block(kind="paragraph", text=text, level=0)


def _section(title: str, blocks: List[str], children: List[Section] = None) -> Section:
    """Собрать секцию с заданными блоками и дочерними секциями."""

    return Section(
        title=title,
        level=2,
        blocks=[_block(t) for t in blocks],
        children=list(children or []),
    )


def _make_cf_doc() -> ConfluenceDoc:
    """ConfluenceDoc с вложенными expand-секциями.

    Структура:
        * Серверы (2 блока по 3 слова = 6 слов в тексте + 1 слово в заголовке)
            * Развёрнутое описание (1 блок из 4 слов + 3 слова в заголовке)
            * Развёрнутое описание (1 блок из 5 слов + 3 слова в заголовке)
        * Сеть (1 блок из 2 слов + 1 слово в заголовке)

    Итого по ``cf_doc.total_words()``:
        1 (Серверы) + 6 (блоки) + 3 (Развёрнутое описание) + 4 + 3 + 5 + 1 (Сеть) + 2 = 25

    В ``CorpusUnit.text`` попадают ТОЛЬКО тексты блоков (без заголовков секций),
    потому что ``_confluence_units`` собирает текст только из ``s.blocks``.
    """
    nested_a = _section(
        "Развёрнутое описание",
        ["альфа один два три четыре"],
    )
    nested_b = _section(
        "Развёрнутое описание",
        ["пять шесть семь восемь десять"],
    )
    top_a = _section(
        "Серверы",
        ["первый блок один два", "второй блок три четыре"],
        children=[nested_a, nested_b],
    )
    top_b = _section(
        "Сеть",
        ["коммутатор uplink"],
    )
    return ConfluenceDoc(
        page_id="SYNTH",
        title="Synthetic",
        version=1,
        storage_hash="x" * 64,
        view_hash="y" * 64,
        storage_chars=0,
        view_chars=0,
        sections=[top_a, top_b],
        coverage={},
        unresolved_excerpt_includes=0,
    )


def _make_corpus(cf_doc: ConfluenceDoc) -> Corpus:
    """Корпус строго по тому, что реально попадает в индексируемые юниты.

    Здесь мы НЕ используем ``build_corpus``, потому что хотим полностью
    контролировать содержимое ``CorpusUnit.text`` для синтетического
    сценария. Структура совпадает с тем, что собрал бы
    ``corpus._confluence_units`` для описанного выше ``cf_doc``.
    """

    units: List[CorpusUnit] = [
        # top_a "Серверы" — два блока.
        CorpusUnit(
            unit_id="cf:SYNTH:Servers#1",
            source="confluence",
            title="Серверы",
            section_path=["Серверы"],
            text="первый блок один два",
        ),
        CorpusUnit(
            unit_id="cf:SYNTH:Servers#2",
            source="confluence",
            title="Серверы",
            section_path=["Серверы"],
            text="второй блок три четыре",
        ),
        # nested_a "Серверы / Развёрнутое описание".
        CorpusUnit(
            unit_id="cf:SYNTH:Servers/Detail#1",
            source="confluence",
            title="Развёрнутое описание",
            section_path=["Серверы", "Развёрнутое описание"],
            text="альфа один два три четыре",
        ),
        # nested_b (тот же section_path, второй occurrence).
        CorpusUnit(
            unit_id="cf:SYNTH:Servers/Detail#2",
            source="confluence",
            title="Развёрнутое описание",
            section_path=["Серверы", "Развёрнутое описание"],
            text="пять шесть семь восемь десять",
        ),
        # top_b "Сеть".
        CorpusUnit(
            unit_id="cf:SYNTH:Network#1",
            source="confluence",
            title="Сеть",
            section_path=["Сеть"],
            text="коммутатор uplink",
        ),
        # PDF-юниты (синтетические).
        CorpusUnit(
            unit_id="pdf:s.pdf:p1",
            source="pdf",
            title="s",
            section_path=["s", "Страница 1"],
            text="PDF страница один кратко",
        ),
        CorpusUnit(
            unit_id="pdf:s.pdf:p2",
            source="pdf",
            title="s",
            section_path=["s", "Страница 2"],
            text="PDF страница два тоже есть",
        ),
    ]
    return Corpus(
        sources={"confluence": {"page_id": "SYNTH"}, "pdf": {}},
        units=units,
    )


def _make_pdf_doc() -> PdfDoc:
    """PdfDoc, в котором одна страница пустая — её не должно быть в корпусе."""

    return PdfDoc(
        path=Path("s.pdf"),
        pages=[
            PdfPage(page_number=1, text="PDF страница один кратко"),
            PdfPage(page_number=2, text="PDF страница два тоже есть"),
            PdfPage(page_number=3, text=""),  # пустая страница
        ],
        raw_char_count=0,
        title_hint=None,
    )


class PerSourceWordsSumTest(unittest.TestCase):
    """``confluence.words + pdf.words`` должно строго равняться ``corpus.words``."""

    def test_per_source_sum_equals_corpus(self) -> None:
        cf_doc = _make_cf_doc()
        corpus_obj = _make_corpus(cf_doc)
        pdf_doc = _make_pdf_doc()

        # Sanity-check: «сырой» счётчик Confluence включает заголовки
        # секций (которых нет в CorpusUnit.text) и завышает цифру
        # относительно индексированной части.
        raw_cf_words = cf_doc.total_words()
        raw_pdf_words = pdf_doc.total_words()
        corpus_words = corpus_obj.total_words()
        indexed_cf_words = sum(
            tu.count_words(u.text) for u in corpus_obj.units if u.source == "confluence"
        )
        indexed_pdf_words = sum(
            tu.count_words(u.text) for u in corpus_obj.units if u.source == "pdf"
        )

        self.assertGreater(
            raw_cf_words,
            indexed_cf_words,
            "Сырой счётчик Confluence должен включать заголовки секций, "
            "которых нет в CorpusUnit.text — иначе тест не покрывает регрессию.",
        )
        # ``raw_pdf_words`` НЕ обязан превышать ``indexed_pdf_words`` в
        # общем случае (PDF обычно падает 1-в-1 на непустые страницы).
        # Но он не может быть меньше — иначе корпус бы посчитал слова,
        # которых нет в PDF. Это неравенство служит нижней границей.
        self.assertGreaterEqual(raw_pdf_words, indexed_pdf_words)

        # Получаем метрики и проверяем инвариант.
        from docindexing.config import RunConfig

        cfg = RunConfig(
            confluence_json=Path("unused.json"),
            pdf_path=Path("unused.pdf"),
            output_dir=Path("unused_out"),
            ollama_url="http://unused",
            embed_model="fake",
            fixed_tokens=10,
            fixed_overlap=2,
            struct_max_chars=200,
            struct_min_chars=20,
            embed_batch_size=4,
            embed_max_retries=1,
            embed_retry_base_delay=0.0,
            embed_timeout=1.0,
        )

        metrics = build_mod._compute_metrics(
            cfg=cfg,
            corpus_obj=corpus_obj,
            cf_doc=cf_doc,
            pdf_doc=pdf_doc,
            hashes={"confluence_json_sha256": "z" * 64, "pdf_sha256": "z" * 64},
            fixed_chunks=[],
            struct_chunks=[],
            fixed_dim=0,
            struct_dim=0,
        )

        self.assertEqual(
            metrics["corpus"]["words"],
            corpus_words,
            "corpus.words должен совпадать с суммой слов по CorpusUnit.text",
        )
        self.assertEqual(
            metrics["confluence"]["words"] + metrics["pdf"]["words"],
            metrics["corpus"]["words"],
            "confluence.words + pdf.words должно строго равняться corpus.words",
        )
        # Сырые значения сохранены отдельно для аудита.
        self.assertEqual(metrics["confluence"]["raw_words"], raw_cf_words)
        self.assertEqual(metrics["pdf"]["raw_words"], raw_pdf_words)
        # Метрики индексированной части совпадают с прямым подсчётом.
        self.assertEqual(metrics["confluence"]["words"], indexed_cf_words)
        self.assertEqual(metrics["pdf"]["words"], indexed_pdf_words)


class NestedSectionsCountTest(unittest.TestCase):
    """``sections_total`` учитывает вложенные expand-ы, плюс top-level отдельно."""

    def test_sections_total_is_recursive(self) -> None:
        cf_doc = _make_cf_doc()
        corpus_obj = _make_corpus(cf_doc)
        pdf_doc = _make_pdf_doc()

        from docindexing.config import RunConfig

        cfg = RunConfig(
            confluence_json=Path("unused.json"),
            pdf_path=Path("unused.pdf"),
            output_dir=Path("unused_out"),
            ollama_url="http://unused",
            embed_model="fake",
            fixed_tokens=10,
            fixed_overlap=2,
            struct_max_chars=200,
            struct_min_chars=20,
            embed_batch_size=4,
            embed_max_retries=1,
            embed_retry_base_delay=0.0,
            embed_timeout=1.0,
        )

        metrics = build_mod._compute_metrics(
            cfg=cfg,
            corpus_obj=corpus_obj,
            cf_doc=cf_doc,
            pdf_doc=pdf_doc,
            hashes={"confluence_json_sha256": "z" * 64, "pdf_sha256": "z" * 64},
            fixed_chunks=[],
            struct_chunks=[],
            fixed_dim=0,
            struct_dim=0,
        )

        # В фикстуре: Серверы + Сеть на верхнем уровне (2),
        # плюс 2 вложенных «Развёрнутое описание» под «Серверы».
        self.assertEqual(metrics["confluence"]["sections_top_level"], 2)
        self.assertEqual(metrics["confluence"]["sections_total"], 4)

        # Top-level не может превышать рекурсивное число.
        self.assertLessEqual(
            metrics["confluence"]["sections_top_level"],
            metrics["confluence"]["sections_total"],
        )


class RawWordsPreservedTest(unittest.TestCase):
    """Сырые цифры парсеров остаются в отчёте отдельно."""

    def test_raw_words_present(self) -> None:
        cf_doc = _make_cf_doc()
        corpus_obj = _make_corpus(cf_doc)
        pdf_doc = _make_pdf_doc()

        from docindexing.config import RunConfig

        cfg = RunConfig(
            confluence_json=Path("unused.json"),
            pdf_path=Path("unused.pdf"),
            output_dir=Path("unused_out"),
            ollama_url="http://unused",
            embed_model="fake",
            fixed_tokens=10,
            fixed_overlap=2,
            struct_max_chars=200,
            struct_min_chars=20,
            embed_batch_size=4,
            embed_max_retries=1,
            embed_retry_base_delay=0.0,
            embed_timeout=1.0,
        )

        metrics = build_mod._compute_metrics(
            cfg=cfg,
            corpus_obj=corpus_obj,
            cf_doc=cf_doc,
            pdf_doc=pdf_doc,
            hashes={"confluence_json_sha256": "z" * 64, "pdf_sha256": "z" * 64},
            fixed_chunks=[],
            struct_chunks=[],
            fixed_dim=0,
            struct_dim=0,
        )

        # Не должно быть «магии»: оба значения должны присутствовать.
        self.assertIn("raw_words", metrics["confluence"])
        self.assertIn("raw_words", metrics["pdf"])
        self.assertGreaterEqual(
            metrics["confluence"]["raw_words"], metrics["confluence"]["words"]
        )
        self.assertGreaterEqual(
            metrics["pdf"]["raw_words"], metrics["pdf"]["words"]
        )


if __name__ == "__main__":
    unittest.main()