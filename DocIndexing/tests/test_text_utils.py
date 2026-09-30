"""Тесты утилит текста и подсчёта страниц."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from docindexing import text_utils  # noqa: E402


class TextUtilsTest(unittest.TestCase):
    def test_count_words_unicode(self) -> None:
        self.assertEqual(text_utils.count_words(""), 0)
        self.assertEqual(text_utils.count_words("один два три"), 3)
        self.assertEqual(text_utils.count_words("Русский и English words 123"), 5)

    def test_estimate_pages(self) -> None:
        # ceil(700 / 350) = 2
        self.assertEqual(text_utils.estimate_pages_from_words(700), 2)
        self.assertEqual(text_utils.estimate_pages_from_words(0), 0)
        self.assertEqual(text_utils.estimate_pages_from_words(351), 2)
        self.assertEqual(text_utils.estimate_pages_from_words(350), 1)

    def test_normalize_whitespace(self) -> None:
        self.assertEqual(text_utils.normalize_whitespace("  a  \n\n\n  b\tc "), "a\n\nb c")

    def test_split_sentences(self) -> None:
        parts = text_utils.split_sentences("Один. Два! Три?")
        self.assertEqual(len(parts), 3)


if __name__ == "__main__":
    unittest.main()
