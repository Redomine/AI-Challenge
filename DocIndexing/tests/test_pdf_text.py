"""Тесты извлечения PDF-текста с номерами страниц и заголовками."""

from __future__ import annotations

import io
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from docindexing import pdf_text  # noqa: E402


def _build_sample_pdf(path: Path, pages: list[str]) -> None:
    """Собрать минимальный PDF с заданными страницами (без внешних библиотек).

    Используем :mod:`pypdf.PdfWriter`, который умеет добавлять пустые
    страницы. Текст пишем через ``add_blank_page`` + ``merge_page`` с
    предварительно сгенерированной страницей-донором.
    Для простоты здесь используем трюк: создаём PDF, где каждая страница
    уже содержит наш текст, через :class:`pypdf.PageObject` +
    ``/Contents`` со ссылкой на поток. Но pypdf не умеет «рисовать» текст
    на странице без reportlab, поэтому для тестов достаточно положиться
    на ``extract_text`` после ручной сборки PDF через минимальный writer
    с заранее подготовленными страницами.
    """

    from pypdf import PdfReader, PdfWriter
    from pypdf.generic import (
        ArrayObject,
        ContentStream,
        DecodedStreamObject,
        DictionaryObject,
        FloatObject,
        NameObject,
        NumberObject,
        TextStringObject,
    )

    writer = PdfWriter()
    for text in pages:
        page = writer.add_blank_page(width=612, height=792)

        # Собираем содержимое страницы с одной строкой текста через Tj.
        # Используем шрифт по имени F1; добавим его как ресурс страницы.
        font_dict = DictionaryObject({
            NameObject("/Type"): NameObject("/Font"),
            NameObject("/Subtype"): NameObject("/Type1"),
            NameObject("/BaseFont"): NameObject("/Helvetica"),
        })
        resources = page.get("/Resources")
        if resources is None:
            resources = DictionaryObject()
            page[NameObject("/Resources")] = resources
        resources[NameObject("/Font")] = DictionaryObject({
            NameObject("/F1"): font_dict,
        })

        # Подготовим простую строку с экранированием скобок.
        escaped = text.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
        content = f"BT /F1 18 Tf 72 720 Td ({escaped}) Tj ET"
        content_bytes = content.encode("latin-1", errors="replace")

        stream = DecodedStreamObject()
        stream.set_data(content_bytes)
        content_stream = ContentStream(stream, page)

        page[NameObject("/Contents")] = content_stream

    with open(path, "wb") as fh:
        writer.write(fh)


class PdfExtractionTest(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        self.pdf = self.dir / "sample.pdf"
        _build_sample_pdf(
            self.pdf,
            [
                "Vvedenie v servery",
                "Glava 1. Arkhitektura stoyki",
                "Prostoy tekst stranitsy dlya testov.",
            ],
        )

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def test_pages_and_headings(self) -> None:
        doc = pdf_text.extract_pdf(self.pdf)
        self.assertEqual(len(doc.pages), 3)
        # Заголовок детектируется на странице 1 и 2.
        self.assertTrue(doc.pages[0].is_heading)
        self.assertTrue(doc.pages[1].is_heading)
        self.assertFalse(doc.pages[2].is_heading)
        self.assertEqual(doc.pages[0].text, "Vvedenie v servery")
        self.assertGreater(doc.raw_char_count, 0)
        self.assertGreater(doc.total_words(), 0)

    def test_fail_when_empty(self) -> None:
        # Сгенерируем PDF без текста.
        from pypdf import PdfWriter
        empty = self.dir / "empty.pdf"
        writer = PdfWriter()
        writer.add_blank_page(width=612, height=792)
        with open(empty, "wb") as fh:
            writer.write(fh)
        with self.assertRaises(RuntimeError):
            pdf_text.extract_pdf(empty)


if __name__ == "__main__":
    unittest.main()
