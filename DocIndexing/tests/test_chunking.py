"""Тесты чанкования: стабильные ID, границы, метаданные."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from docindexing import chunking  # noqa: E402
from docindexing.corpus import CorpusUnit


def _unit(
    text: str,
    *,
    source: str = "confluence",
    pdf_page=None,
    level: int = 1,
    section_path=None,
    title: str = "Title",
    unit_id: str | None = None,
) -> CorpusUnit:
    return CorpusUnit(
        unit_id=unit_id or f"{source}:{hash(text)}",
        source=source,
        title=title,
        section_path=list(section_path) if section_path is not None else [title],
        text=text,
        pdf_page=pdf_page,
        heading_level=level,
    )


class FixedChunkingTest(unittest.TestCase):
    def test_creates_overlapping_chunks(self) -> None:
        text = " ".join(f"word{i}" for i in range(500))
        u = _unit(text)
        chunks = chunking.chunk_fixed(
            u, target_words=100, overlap_words=20, base_index=0
        )
        self.assertGreater(len(chunks), 1)
        for c in chunks:
            self.assertLessEqual(c.word_count, 100)
            self.assertGreater(c.word_count, 0)
        # Перекрытие: первое слово 2-го чанка должно встретиться в 1-м.
        words = text.split()
        self.assertIn(words[0], chunks[0].text)
        self.assertIn(words[80], chunks[1].text)
        self.assertNotEqual(chunks[0].chunk_id, chunks[1].chunk_id)

    def test_stable_ids(self) -> None:
        u = _unit("Alpha Beta Gamma Delta")
        a = chunking.chunk_fixed(u, target_words=2, overlap_words=0, base_index=0)
        b = chunking.chunk_fixed(u, target_words=2, overlap_words=0, base_index=0)
        self.assertEqual([c.chunk_id for c in a], [c.chunk_id for c in b])
        self.assertEqual([c.text for c in a], [c.text for c in b])

    def test_metadata(self) -> None:
        u = _unit("one two three", source="pdf", pdf_page=3, level=2)
        chunks = chunking.chunk_fixed(u, target_words=2, overlap_words=0, base_index=0)
        self.assertEqual(chunks[0].source, "pdf")
        self.assertEqual(chunks[0].pdf_page, 3)
        self.assertEqual(chunks[0].heading_level, 2)
        self.assertEqual(chunks[0].strategy, "fixed")


class StructuralChunkingTest(unittest.TestCase):
    def test_splits_long_section(self) -> None:
        long_text = "\n\n".join(["абзац номер " + str(i) + " с текстом." for i in range(50)])
        u = _unit(long_text)
        chunks = chunking.build_chunks(
            [u],
            strategy="structural",
            fixed_target_words=10,
            fixed_overlap_words=2,
            struct_max_chars=120,
            struct_min_chars=20,
        )
        self.assertGreater(len(chunks), 1)
        for c in chunks:
            self.assertLessEqual(c.char_count, 120 + 50)  # допуск на склейку мелких
        self.assertGreaterEqual(chunks[0].char_count, 1)

    def test_pdf_page_preserved(self) -> None:
        u1 = _unit("короткий текст", source="pdf", pdf_page=4)
        u2 = _unit("ещё короткий текст страницы 5", source="pdf", pdf_page=5)
        chunks = chunking.build_chunks(
            [u1, u2],
            strategy="structural",
            fixed_target_words=10,
            fixed_overlap_words=0,
            struct_max_chars=400,
            struct_min_chars=20,
        )
        pdf_pages = {c.pdf_page for c in chunks}
        self.assertIn(4, pdf_pages)
        self.assertIn(5, pdf_pages)


# ----------------------------------------------------------------------
# Synthetic tests for the fixed-size per-source-document baseline.
# ----------------------------------------------------------------------


def _cf_unit(idx: int, text: str, *, section: list[str], level: int = 2) -> CorpusUnit:
    """Confluence-юнит с уникальным ``unit_id`` и явным section_path."""

    return CorpusUnit(
        unit_id=f"cf:test:{idx}",
        source="confluence",
        title=section[-1] if section else "Title",
        section_path=list(section),
        text=text,
        pdf_page=None,
        heading_level=level,
    )


def _pdf_unit(idx: int, page: int, text: str, *, title: str = "Doc") -> CorpusUnit:
    """PDF-юнит, привязанный к конкретной странице."""

    return CorpusUnit(
        unit_id=f"pdf:test:p{page}",
        source="pdf",
        title=title,
        section_path=[title, f"Страница {page}"],
        text=text,
        pdf_page=page,
        heading_level=1,
    )


class BuildFixedBaselineTest(unittest.TestCase):
    """Поведение нового baseline: фикс-чанкер по каждому источнику."""

    def test_many_short_confluence_units_merge_into_near_target_windows(self) -> None:
        """150 коротких Confluence-юнитов (по ~5 слов) дают чанки, близкие к target=256."""

        target, overlap = 256, 32
        units = []
        for i in range(150):
            section = ["Doc", f"Раздел {i // 10}", f"Подраздел {i}"]
            text = " ".join(f"слово{i}_{k}" for k in range(5))
            units.append(_cf_unit(i, text, section=section))
        # Sanity: ровно 150 юнитов и нужное число слов.
        self.assertEqual(len(units), 150)
        total_words = sum(len(u.text.split()) for u in units)
        self.assertEqual(total_words, 150 * 5)

        chunks = chunking.build_chunks(
            units,
            strategy="fixed",
            fixed_target_words=target,
            fixed_overlap_words=overlap,
            struct_max_chars=1000,
            struct_min_chars=20,
        )

        # Должно получиться существенно меньше 150 чанков: не «по чанку
        # на юнит», а скользящее окно по общему потоку.
        self.assertLess(len(chunks), 30)
        self.assertGreater(len(chunks), 0)

        # Все непустые и не превышают target по словам, кроме последнего
        # окна документа (это финальный остаток).
        for c in chunks[:-1]:
            self.assertEqual(c.source, "confluence")
            self.assertLessEqual(c.word_count, target)
            self.assertGreater(c.word_count, 0)
        # Последний чанк — финальный остаток, может быть короче target.
        self.assertGreater(chunks[-1].word_count, 0)

        # Склеенный текст всех чанков покрывает весь входной поток
        # (с учётом перекрытия каждое слово входит минимум в один чанк).
        joined = " ".join(c.text for c in chunks)
        # Каждое «слово i_k» должно встречаться хотя бы раз.
        for i in (0, 75, 149):
            for k in range(5):
                self.assertIn(f"слово{i}_{k}", joined)

    def test_overlap_is_actually_overlapping(self) -> None:
        """target=10, overlap=4 → соседние окна пересекаются по 6 словам."""

        units = [_cf_unit(i, f"unit{i} " + " ".join(f"w{i}_{k}" for k in range(20)),
                          section=["Doc", f"S{i}"]) for i in range(5)]
        chunks = chunking.build_chunks(
            units,
            strategy="fixed",
            fixed_target_words=10,
            fixed_overlap_words=4,
            struct_max_chars=1000,
            struct_min_chars=20,
        )
        self.assertGreater(len(chunks), 1)
        # step = target - overlap = 6.
        words_per_chunk = [c.text.split() for c in chunks]
        # Первые 4 слова каждого следующего чанка == последние 4 слова
        # предыдущего (это overlap).
        for prev, curr in zip(words_per_chunk, words_per_chunk[1:]):
            self.assertEqual(prev[-4:], curr[:4])

    def test_section_path_collects_all_overlapping_sections(self) -> None:
        """Окно, пересекающее несколько секций, собирает их метки
        детерминированно и без дубликатов."""

        # Строим 3 секции по 100 слов → 300 слов суммарно.
        # target=120, overlap=20 → step=100.
        # Окна:
        #   [0..119]   — внутри sectA + первые 20 слов sectB.
        #   [100..219] — последние 20 слов sectB + первые 20 слов sectC.
        #   [200..299] — последние 100 слов sectC.
        sect_a = ["Doc", "Раздел A"]
        sect_b = ["Doc", "Раздел B"]
        sect_c = ["Doc", "Раздел C"]
        units = [
            _cf_unit(0, " ".join(f"a{k}" for k in range(100)), section=sect_a),
            _cf_unit(1, " ".join(f"b{k}" for k in range(100)), section=sect_b),
            _cf_unit(2, " ".join(f"c{k}" for k in range(100)), section=sect_c),
        ]
        chunks = chunking.build_chunks(
            units,
            strategy="fixed",
            fixed_target_words=120,
            fixed_overlap_words=20,
            struct_max_chars=1000,
            struct_min_chars=20,
        )
        # 300 слов, step=100 → 3 окна.
        self.assertEqual(len(chunks), 3)

        # Первое окно: слова 0..119, пересекает sectA и sectB.
        self.assertIn("Раздел A", chunks[0].section_path)
        self.assertIn("Раздел B", chunks[0].section_path)
        self.assertNotIn("Раздел C", chunks[0].section_path)
        # Общий префикс "Doc" идёт первым, без дубликатов.
        self.assertEqual(chunks[0].section_path[0], "Doc")
        # Подсчёт: "Doc" + метки хвостов → длина 3.
        self.assertEqual(len(chunks[0].section_path), 3)

        # Второе окно: слова 100..219, пересекает хвост sectB и начало sectC.
        self.assertIn("Раздел B", chunks[1].section_path)
        self.assertIn("Раздел C", chunks[1].section_path)
        self.assertNotIn("Раздел A", chunks[1].section_path)
        self.assertEqual(chunks[1].section_path[0], "Doc")

        # Третье окно: слова 200..299 — полностью в sectC.
        self.assertIn("Раздел C", chunks[2].section_path)
        self.assertNotIn("Раздел A", chunks[2].section_path)
        self.assertNotIn("Раздел B", chunks[2].section_path)

    def test_section_path_deduped_and_deterministic(self) -> None:
        """Повторный прогон даёт тот же список меток секций в том же порядке."""

        units = [
            _cf_unit(0, " ".join(f"x{k}" for k in range(40)),
                     section=["Doc", "A", "deep1"]),
            _cf_unit(1, " ".join(f"y{k}" for k in range(40)),
                     section=["Doc", "A", "deep2"]),
            _cf_unit(2, " ".join(f"z{k}" for k in range(40)),
                     section=["Doc", "B"]),
        ]
        a = chunking.build_chunks(
            units, strategy="fixed",
            fixed_target_words=30, fixed_overlap_words=10,
            struct_max_chars=1000, struct_min_chars=20,
        )
        b = chunking.build_chunks(
            units, strategy="fixed",
            fixed_target_words=30, fixed_overlap_words=10,
            struct_max_chars=1000, struct_min_chars=20,
        )
        self.assertEqual(
            [c.section_path for c in a],
            [c.section_path for c in b],
        )
        # Без дубликатов подряд.
        for c in a:
            self.assertEqual(len(c.section_path), len(set(c.section_path)))

    def test_pdf_pages_kept_within_own_unit(self) -> None:
        """PDF границы страниц не пересекаются: каждый чанк принадлежит одной странице."""

        # 3 страницы, на каждой ~80 слов.
        units = [
            _pdf_unit(0, page=1, text=" ".join(f"p1_{k}" for k in range(80))),
            _pdf_unit(1, page=2, text=" ".join(f"p2_{k}" for k in range(80))),
            _pdf_unit(2, page=3, text=" ".join(f"p3_{k}" for k in range(80))),
        ]
        chunks = chunking.build_chunks(
            units,
            strategy="fixed",
            fixed_target_words=256,
            fixed_overlap_words=32,
            struct_max_chars=1000,
            struct_min_chars=20,
        )
        self.assertGreater(len(chunks), 0)
        # Каждый чанк имеет ровно одну страницу.
        for c in chunks:
            self.assertEqual(c.source, "pdf")
            self.assertIn(c.pdf_page, {1, 2, 3})
            # Никакого слова с чужой страницы внутри.
            page_token = f"p{c.pdf_page}_"
            self.assertIn(page_token, c.text)
            for other in ({1, 2, 3} - {c.pdf_page}):
                self.assertNotIn(f"p{other}_", c.text)

    def test_no_empty_chunks_and_no_duplicate_ids(self) -> None:
        """Никаких пустых чанков и повторов chunk_id в одной выдаче."""

        units = [
            _cf_unit(0, "", section=["Doc", "empty"]),  # пустой текст
            _cf_unit(1, " ".join(f"w{k}" for k in range(50)), section=["Doc", "real"]),
            _pdf_unit(2, page=1, text=" ".join(f"p{k}" for k in range(120))),
        ]
        chunks = chunking.build_chunks(
            units,
            strategy="fixed",
            fixed_target_words=64,
            fixed_overlap_words=8,
            struct_max_chars=1000,
            struct_min_chars=20,
        )
        for c in chunks:
            self.assertGreater(c.text.strip(), "")
            self.assertGreater(c.word_count, 0)
        ids = [c.chunk_id for c in chunks]
        self.assertEqual(len(ids), len(set(ids)))

    def test_ids_stable_across_reruns(self) -> None:
        """chunk_id детерминирован: два прогона с одинаковым входом дают одинаковые ID."""

        units = [
            _cf_unit(0, "alpha beta gamma", section=["Doc", "A"]),
            _cf_unit(1, "delta epsilon zeta", section=["Doc", "B"]),
            _pdf_unit(2, page=1, text="page one text"),
        ]
        a = chunking.build_chunks(
            units, strategy="fixed",
            fixed_target_words=10, fixed_overlap_words=2,
            struct_max_chars=1000, struct_min_chars=20,
        )
        b = chunking.build_chunks(
            units, strategy="fixed",
            fixed_target_words=10, fixed_overlap_words=2,
            struct_max_chars=1000, struct_min_chars=20,
        )
        self.assertEqual([c.chunk_id for c in a], [c.chunk_id for c in b])
        self.assertEqual([c.text for c in a], [c.text for c in b])
        self.assertEqual(
            [c.section_path for c in a],
            [c.section_path for c in b],
        )

    def test_input_text_fully_covered_by_chunks(self) -> None:
        """Каждое слово входа встречается хотя бы в одном чанке (после склейки)."""

        units = [
            _cf_unit(i, " ".join(f"u{i}_{k}" for k in range(30)),
                     section=["Doc", f"S{i}"]) for i in range(7)
        ]
        chunks = chunking.build_chunks(
            units, strategy="fixed",
            fixed_target_words=50, fixed_overlap_words=10,
            struct_max_chars=1000, struct_min_chars=20,
        )
        joined = " ".join(c.text for c in chunks)
        for i in range(7):
            self.assertIn(f"u{i}_0", joined)
            self.assertIn(f"u{i}_29", joined)

    def test_final_remainder_per_source_can_be_shorter_than_target(self) -> None:
        """Финальный чанк каждого источника — остаток, может быть < target."""

        # Конфлюенс: 300 слов → 3 чанка (256, 256 с overlap, остаток).
        cf_units = [
            _cf_unit(0, " ".join(f"w{k}" for k in range(150)),
                     section=["Doc", "A"]),
            _cf_unit(1, " ".join(f"w{k}" for k in range(150)),
                     section=["Doc", "B"]),
        ]
        cf_chunks = chunking.build_chunks(
            cf_units, strategy="fixed",
            fixed_target_words=256, fixed_overlap_words=32,
            struct_max_chars=1000, struct_min_chars=20,
        )
        self.assertGreaterEqual(len(cf_chunks), 2)
        # Последний остаток <= target (истина по построению).
        self.assertLessEqual(cf_chunks[-1].word_count, 256)
        # Суммарно покрыто 300 слов (с учётом overlap), но не меньше 300.
        # Минимум — каждое слово входит хотя бы в один чанк.
        joined_cf = " ".join(c.text for c in cf_chunks)
        for k in range(150):
            self.assertIn(f"w{k}", joined_cf)

    def test_structural_strategy_unchanged(self) -> None:
        """Structural-стратегия не должна зависеть от изменений fixed."""

        units = [
            _unit("длинный кусок " * 30, section_path=["Doc", "S1"]),
            _unit("короткий", section_path=["Doc", "S2"]),
        ]
        chunks = chunking.build_chunks(
            units,
            strategy="structural",
            fixed_target_words=256,
            fixed_overlap_words=32,
            struct_max_chars=120,
            struct_min_chars=20,
        )
        for c in chunks:
            self.assertEqual(c.strategy, "structural")
            # Каждый чанк привязан ровно к одному юниту и его section_path.
            self.assertGreaterEqual(len(c.section_path), 1)


if __name__ == "__main__":
    unittest.main()
