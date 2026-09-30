"""Тесты сборки корпуса: стабильные ``unit_id`` для повторяющихся секций."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from docindexing import confluence, corpus  # noqa: E402


def _build_doc(
    page_id: str,
    *,
    storage_html: str,
    title: str = "Synthetic page",
    version: int = 1,
) -> confluence.ConfluenceDoc:
    """Собрать ConfluenceDoc из синтестического storage HTML.

    Не читает никаких реальных файлов и не зависит от JSON-фикстур:
    сериализуем payload через :mod:`json`, чтобы корректно экранировать
    вложенные кавычки и угловые скобки в HTML.
    """

    import hashlib
    import json

    payload = {
        "id": page_id,
        "type": "page",
        "title": title,
        "version": {"number": version},
        "body": {
            "storage": {"value": storage_html, "representation": "storage"},
            "view": {"value": "", "representation": "view"},
        },
    }
    raw = json.dumps(payload, ensure_ascii=False)
    data = json.loads(raw)
    storage_html_value = data["body"]["storage"]["value"]
    blocks = confluence.parse_html_to_blocks(
        storage_html_value, source_kind="storage"
    )
    sections = confluence.build_sections_from_blocks(blocks)
    sections = confluence.filter_noise_sections(sections)
    storage_hash = hashlib.sha256(storage_html_value.encode("utf-8")).hexdigest()
    return confluence.ConfluenceDoc(
        page_id=str(data["id"]),
        title=data["title"],
        version=data["version"]["number"],
        storage_hash=storage_hash,
        view_hash="",
        storage_chars=len(storage_html_value),
        view_chars=0,
        sections=sections,
        coverage={},
        unresolved_excerpt_includes=0,
    )


class CorpusUnitIdOccurrenceTest(unittest.TestCase):
    """Repeated expand-siblings с одинаковым ``section_path`` должны
    получать разные ``unit_id`` через детерминированный occurrence ordinal.

    Без ординала второй юнит «съедал» бы первый в SQLite, потому что
    ``chunk_id`` деривится из ``unit_id``. Это приводило бы к потере
    легитимных повторов (storage parser теперь видит все 207 expand, и
    однофамильцы там — нормальная ситуация).
    """

    def test_repeated_expand_siblings_get_distinct_unit_ids(self) -> None:
        html = (
            "<h1>Root</h1>"
            "<ac:structured-macro ac:name=\"expand\">"
            "<ac:parameter ac:name=\"title\">Detail</ac:parameter>"
            "<ac:rich-text-body><p>First unique body.</p></ac:rich-text-body>"
            "</ac:structured-macro>"
            "<ac:structured-macro ac:name=\"expand\">"
            "<ac:parameter ac:name=\"title\">Detail</ac:parameter>"
            "<ac:rich-text-body><p>Second unique body.</p></ac:rich-text-body>"
            "</ac:structured-macro>"
            "<ac:structured-macro ac:name=\"expand\">"
            "<ac:parameter ac:name=\"title\">Detail</ac:parameter>"
            "<ac:rich-text-body><p>Third unique body.</p></ac:rich-text-body>"
            "</ac:structured-macro>"
        )
        doc = _build_doc("9001", storage_html=html)
        units = corpus._confluence_units(doc)

        # Все три юнита имеют один и тот же section_path.
        paths = {tuple(u.section_path) for u in units}
        self.assertEqual(len(paths), 1)

        # Но unit_id у них РАЗНЫЕ — за счёт ординала.
        ids = [u.unit_id for u in units]
        self.assertEqual(len(ids), 3)
        self.assertEqual(len(set(ids)), 3, "unit_ids должны быть уникальны")

        # Каждый ординал встречается ровно один раз.
        ordinals = sorted(int(uid.rsplit("#", 1)[1]) for uid in ids)
        self.assertEqual(ordinals, [1, 2, 3])

        # И текст у каждого unit свой — без потерь от перезаписи.
        texts = sorted(u.text for u in units)
        self.assertEqual(
            texts,
            [
                "First unique body.",
                "Second unique body.",
                "Third unique body.",
            ],
        )

    def test_unit_id_is_stable_across_runs(self) -> None:
        html = (
            "<h1>Root</h1>"
            "<ac:structured-macro ac:name=\"expand\">"
            "<ac:parameter ac:name=\"title\">Same Title</ac:parameter>"
            "<ac:rich-text-body><p>A</p></ac:rich-text-body>"
            "</ac:structured-macro>"
            "<ac:structured-macro ac:name=\"expand\">"
            "<ac:parameter ac:name=\"title\">Same Title</ac:parameter>"
            "<ac:rich-text-body><p>B</p></ac:rich-text-body>"
            "</ac:structured-macro>"
        )
        doc_a = _build_doc("42", storage_html=html)
        doc_b = _build_doc("42", storage_html=html)
        ids_a = [u.unit_id for u in corpus._confluence_units(doc_a)]
        ids_b = [u.unit_id for u in corpus._confluence_units(doc_b)]
        self.assertEqual(ids_a, ids_b)

    def test_unit_id_includes_page_id(self) -> None:
        html = (
            "<h1>Root</h1>"
            "<ac:structured-macro ac:name=\"expand\">"
            "<ac:parameter ac:name=\"title\">Detail</ac:parameter>"
            "<ac:rich-text-body><p>Body.</p></ac:rich-text-body>"
            "</ac:structured-macro>"
        )
        doc = _build_doc("PAGE-X", storage_html=html)
        units = corpus._confluence_units(doc)
        self.assertEqual(len(units), 1)
        self.assertTrue(units[0].unit_id.startswith("cf:PAGE-X:"))


if __name__ == "__main__":
    unittest.main()