"""Тесты SHA-256 файлов и словарей."""

from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from docindexing import hashing  # noqa: E402


class HashingTest(unittest.TestCase):
    def test_file_sha256_deterministic(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "a.txt"
            p.write_bytes(b"hello")
            h1 = hashing.file_sha256(p)
            h2 = hashing.file_sha256(p)
            self.assertEqual(h1, h2)
            self.assertEqual(len(h1), 64)

    def test_dict_sha256_canonical(self) -> None:
        a = hashing.dict_sha256({"b": 1, "a": 2})
        b = hashing.dict_sha256({"a": 2, "b": 1})
        self.assertEqual(a, b)


if __name__ == "__main__":
    unittest.main()
