"""Тесты для сравнительного отчёта стратегий (hit@k по ground-truth)."""

from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any, Mapping

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from docindexing.report import (  # noqa: E402
    NormalizedQuestion,
    _first_relevant_rank,
    _hit_for_hit,
    _normalize_question,
    _read_questions,
)


# ---------------------------------------------------------------------
# Разбор входа
# ---------------------------------------------------------------------


class ReadQuestionsTest(unittest.TestCase):
    def test_list_of_strings(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "q.json"
            p.write_text('["вопрос 1", "вопрос 2"]', encoding="utf-8")
            qs = _read_questions(p)
            self.assertEqual(len(qs), 2)
            self.assertEqual(
                qs,
                [
                    NormalizedQuestion(question="вопрос 1", has_ground_truth=False),
                    NormalizedQuestion(question="вопрос 2", has_ground_truth=False),
                ],
            )

    def test_dict_with_questions_list(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "q.json"
            p.write_text('{"questions": ["a"]}', encoding="utf-8")
            qs = _read_questions(p)
            self.assertEqual(len(qs), 1)
            self.assertEqual(qs[0].question, "a")
            self.assertFalse(qs[0].has_ground_truth)

    def test_ground_truth_object(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "q.json"
            p.write_text(
                '{"questions": [{"question": "q", "expected_source": "pdf", '
                '"expected_pdf_page": 3}]}',
                encoding="utf-8",
            )
            qs = _read_questions(p)
            self.assertEqual(len(qs), 1)
            self.assertTrue(qs[0].has_ground_truth)
            self.assertEqual(qs[0].expected_source, "pdf")
            self.assertEqual(qs[0].expected_pdf_page, 3)

    def test_object_without_expected_is_plain(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "q.json"
            p.write_text(
                '{"questions": [{"question": "просто текст"}]}',
                encoding="utf-8",
            )
            qs = _read_questions(p)
            self.assertEqual(len(qs), 1)
            self.assertFalse(qs[0].has_ground_truth)
            self.assertEqual(qs[0].question, "просто текст")

    def test_bad_shape_raises(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "q.json"
            p.write_text('{"foo": "bar"}', encoding="utf-8")
            with self.assertRaises(ValueError):
                _read_questions(p)

    def test_object_missing_question_raises(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "q.json"
            p.write_text(
                '{"questions": [{"expected_source": "pdf"}]}',
                encoding="utf-8",
            )
            with self.assertRaises(ValueError):
                _read_questions(p)

    def test_pdf_page_must_be_int(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "q.json"
            p.write_text(
                '{"questions": [{"question": "x", "expected_pdf_page": "два"}]}',
                encoding="utf-8",
            )
            with self.assertRaises(ValueError):
                _read_questions(p)


class NormalizeQuestionTest(unittest.TestCase):
    def test_string(self) -> None:
        n = _normalize_question("hello")
        self.assertEqual(n.question, "hello")
        self.assertFalse(n.has_ground_truth)

    def test_object_with_section_only(self) -> None:
        n = _normalize_question(
            {"question": "q", "expected_section_contains": "PDU"}
        )
        self.assertTrue(n.has_ground_truth)
        self.assertIsNone(n.expected_source)
        self.assertIsNone(n.expected_pdf_page)
        self.assertEqual(n.expected_section_contains, "PDU")

    def test_unsupported_type(self) -> None:
        with self.assertRaises(ValueError):
            _normalize_question(123)


# ---------------------------------------------------------------------
# Сопоставление чанка с ground-truth
# ---------------------------------------------------------------------


class HitForHitTest(unittest.TestCase):
    def test_source_filter(self) -> None:
        hit = {"source": "pdf", "section": "PDU", "pdf_page": 1}
        self.assertTrue(
            _hit_for_hit(hit, expected_source="pdf",
                         expected_section_contains=None,
                         expected_pdf_page=None)
        )
        self.assertFalse(
            _hit_for_hit(hit, expected_source="confluence",
                         expected_section_contains=None,
                         expected_pdf_page=None)
        )

    def test_section_substring_case_insensitive(self) -> None:
        hit = {"source": "confluence", "section": "Серверы / PDU / Сброс",
               "pdf_page": None}
        self.assertTrue(
            _hit_for_hit(hit, expected_source=None,
                         expected_section_contains="pdu",
                         expected_pdf_page=None)
        )
        self.assertFalse(
            _hit_for_hit(hit, expected_source=None,
                         expected_section_contains="ups",
                         expected_pdf_page=None)
        )

    def test_pdf_page_exact(self) -> None:
        hit = {"source": "pdf", "section": "X", "pdf_page": 7}
        self.assertTrue(
            _hit_for_hit(hit, expected_source=None,
                         expected_section_contains=None,
                         expected_pdf_page=7)
        )
        self.assertFalse(
            _hit_for_hit(hit, expected_source=None,
                         expected_section_contains=None,
                         expected_pdf_page=8)
        )

    def test_all_filters_combined(self) -> None:
        hit = {"source": "pdf", "section": "Стойка / Охлаждение",
               "pdf_page": 5}
        self.assertTrue(
            _hit_for_hit(
                hit, expected_source="pdf",
                expected_section_contains="охлаждение",
                expected_pdf_page=5,
            )
        )
        self.assertFalse(
            _hit_for_hit(
                hit, expected_source="pdf",
                expected_section_contains="охлаждение",
                expected_pdf_page=6,
            )
        )


# ---------------------------------------------------------------------
# Ранг первого релевантного
# ---------------------------------------------------------------------


class FirstRelevantRankTest(unittest.TestCase):
    def _hits(self) -> list[Mapping[str, Any]]:
        return [
            {"source": "pdf", "section": "A", "pdf_page": 1},
            {"source": "pdf", "section": "B / PDU", "pdf_page": 2},
            {"source": "confluence", "section": "C", "pdf_page": None},
        ]

    def test_first_match_position(self) -> None:
        rank = _first_relevant_rank(
            self._hits(),
            expected_source="pdf",
            expected_section_contains="PDU",
            expected_pdf_page=None,
        )
        self.assertEqual(rank, 2)

    def test_no_match_returns_none(self) -> None:
        rank = _first_relevant_rank(
            self._hits(),
            expected_source="pdf",
            expected_section_contains="ZZZ",
            expected_pdf_page=None,
        )
        self.assertIsNone(rank)


# ---------------------------------------------------------------------
# Сквозной сценарий run_comparison (с фейковыми hits)
# ---------------------------------------------------------------------


class RunComparisonSmokeTest(unittest.TestCase):
    def _make_hit(self, source: str, section: str, pdf_page):
        return {
            "chunk_id": f"{source}-{section}",
            "score": 0.9,
            "source": source,
            "title": section,
            "section": section,
            "text": "...",
            "pdf_page": pdf_page,
        }

    def test_hit_at_k_per_strategy(self) -> None:
        """Прямой прогон run_comparison на минимальной конфигурации.

        Подменяем ``query_index`` и ``IndexStore``, чтобы не зависеть от
        реальной Ollama и файлов.
        """

        import dataclasses
        from unittest import mock

        from docindexing import report as report_mod
        from docindexing.config import RunConfig

        # Две стратегии: у fixed 2-й вопрос верен, у structural — 1-й.
        def fake_query(cfg, *, question, strategy, top_k,
                       ollama_client, store):
            if "PDU" in question:
                return [self._make_hit("pdf", "PDU", 3)]
            if "охлаждение" in question:
                if strategy == "fixed":
                    return [
                        self._make_hit("pdf", "Общее", 1),
                        self._make_hit("pdf", "PDU", 2),
                    ]
                return [self._make_hit("pdf", "Охлаждение", 4)]
            return []

        # Заглушка IndexStore, чтобы report.py не открывал настоящие БД.
        class FakeStore:
            def __init__(self, *_args, **_kwargs) -> None:
                pass

            def count_chunks(self) -> int:
                return 10

            def chunks_without_embedding(self) -> int:
                return 0

            def stored_embed_dim(self):
                return 4

            def get_meta(self, key):
                return {"embed_model": "fake"}.get(key)

            def close(self) -> None:
                pass

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
        cfg = dataclasses.replace(
            cfg, output_dir=Path(tempfile.gettempdir())
        )

        questions = [
            NormalizedQuestion(
                question="пароль PDU",
                has_ground_truth=True,
                expected_source="pdf",
                expected_section_contains="PDU",
            ),
            NormalizedQuestion(
                question="охлаждение стойки",
                has_ground_truth=True,
                expected_source="pdf",
                expected_section_contains="Охлаждение",
            ),
        ]

        with mock.patch.object(report_mod, "IndexStore", FakeStore):
            with mock.patch.object(report_mod, "query_index",
                                   side_effect=fake_query):
                summary = report_mod.run_comparison(
                    cfg, questions=questions, top_k=5,
                    ollama_client=mock.Mock(),
                )

        self.assertTrue(summary["quality_claims"])
        self.assertEqual(summary["questions_with_ground_truth"], 2)

        # hit@1 для fixed: только второй вопрос попадает в первую позицию.
        self.assertAlmostEqual(summary["aggregated"]["hit_at_1_fixed"], 0.5)
        # hit@1 для structural: первый вопрос в первой позиции,
        # второй — тоже в первой (раздел "Охлаждение").
        self.assertAlmostEqual(
            summary["aggregated"]["hit_at_1_structural"], 1.0
        )
        # При top_k=5 в aggregated попадают hit@1, hit@3, hit@5.
        self.assertIn("hit_at_3_fixed", summary["aggregated"])
        self.assertIn("hit_at_5_fixed", summary["aggregated"])
        self.assertIn("hit_at_3_structural", summary["aggregated"])
        self.assertIn("hit_at_5_structural", summary["aggregated"])
        # Проверяем, что в per_question есть и фиксированный, и структурный
        # список для каждого вопроса.
        for entry in summary["per_question"]:
            self.assertIn("fixed", entry)
            self.assertIn("structural", entry)
            if entry["has_ground_truth"]:
                self.assertIn("first_relevant_rank_fixed", entry)
                self.assertIn("first_relevant_rank_structural", entry)
                self.assertIn("expected", entry)

    def test_plain_string_questions_skip_quality_metrics(self) -> None:
        """Строковые вопросы без ground-truth → нет hit@k, есть только top-k."""

        import dataclasses
        from unittest import mock

        from docindexing import report as report_mod
        from docindexing.config import RunConfig

        def fake_query(cfg, *, question, strategy, top_k,
                       ollama_client, store):
            return [{"chunk_id": "x", "score": 0.5, "source": "pdf",
                     "title": "t", "section": "s", "text": "...",
                     "pdf_page": 1}]

        class FakeStore:
            def __init__(self, *_a, **_kw) -> None:
                pass

            def count_chunks(self) -> int:
                return 1

            def chunks_without_embedding(self) -> int:
                return 0

            def stored_embed_dim(self):
                return 4

            def get_meta(self, key):
                return {"embed_model": "fake"}.get(key)

            def close(self) -> None:
                pass

        cfg = dataclasses.replace(
            RunConfig(
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
            ),
            output_dir=Path(tempfile.gettempdir()),
        )

        questions = [NormalizedQuestion(question="просто строка",
                                        has_ground_truth=False)]

        with mock.patch.object(report_mod, "IndexStore", FakeStore):
            with mock.patch.object(report_mod, "query_index",
                                   side_effect=fake_query):
                summary = report_mod.run_comparison(
                    cfg, questions=questions, top_k=3,
                    ollama_client=mock.Mock(),
                )

        self.assertFalse(summary["quality_claims"])
        self.assertEqual(summary["questions_with_ground_truth"], 0)
        self.assertEqual(summary["aggregated"], {})
        self.assertEqual(len(summary["per_question"]), 1)
        self.assertNotIn("first_relevant_rank_fixed",
                         summary["per_question"][0])
        self.assertNotIn("first_relevant_rank_structural",
                         summary["per_question"][0])
        self.assertNotIn("expected", summary["per_question"][0])

    def test_top_k_smaller_than_three_drops_extra_keys(self) -> None:
        """При top_k=2 aggregated содержит только hit_at_1 и hit_at_2."""

        import dataclasses
        from unittest import mock

        from docindexing import report as report_mod
        from docindexing.config import RunConfig

        def fake_query(cfg, *, question, strategy, top_k,
                       ollama_client, store):
            return []

        class FakeStore:
            def __init__(self, *_a, **_kw) -> None:
                pass

            def count_chunks(self) -> int:
                return 0

            def chunks_without_embedding(self) -> int:
                return 0

            def stored_embed_dim(self):
                return None

            def get_meta(self, key):
                return None

            def close(self) -> None:
                pass

        cfg = dataclasses.replace(
            RunConfig(
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
            ),
            output_dir=Path(tempfile.gettempdir()),
        )

        questions = [NormalizedQuestion(question="q",
                                        has_ground_truth=True,
                                        expected_source="pdf")]

        with mock.patch.object(report_mod, "IndexStore", FakeStore):
            with mock.patch.object(report_mod, "query_index",
                                   side_effect=fake_query):
                summary = report_mod.run_comparison(
                    cfg, questions=questions, top_k=2,
                    ollama_client=mock.Mock(),
                )

        # При top_k=2: hit_at_1 и hit_at_2, но не hit_at_3/5.
        self.assertIn("hit_at_1_fixed", summary["aggregated"])
        self.assertIn("hit_at_2_fixed", summary["aggregated"])
        self.assertNotIn("hit_at_3_fixed", summary["aggregated"])
        self.assertNotIn("hit_at_5_fixed", summary["aggregated"])


if __name__ == "__main__":
    unittest.main()
