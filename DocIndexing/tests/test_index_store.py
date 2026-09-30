"""Тесты SQLite-индекса: идемпотентность, косинус-поиск, валидация."""

from __future__ import annotations

import math
import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from docindexing import chunking  # noqa: E402
from docindexing.index_store import IndexStore  # noqa: E402


def _vec(values: list[float]):
    return [float(v) for v in values]


class IndexStoreTest(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        self.db = self.dir / "test.sqlite3"
        self.store = IndexStore(self.db)

    def tearDown(self) -> None:
        self.store.close()
        self.tmp.cleanup()

    def _chunks(self):
        from docindexing.corpus import CorpusUnit
        units = [
            CorpusUnit(
                unit_id="u1",
                source="confluence",
                title="A",
                section_path=["A"],
                text="текст про серверы",
                pdf_page=None,
                heading_level=1,
            ),
            CorpusUnit(
                unit_id="u2",
                source="pdf",
                title="B",
                section_path=["B"],
                text="проектирование стойки",
                pdf_page=2,
                heading_level=2,
            ),
            CorpusUnit(
                unit_id="u3",
                source="pdf",
                title="C",
                section_path=["C"],
                text="что-то о сети",
                pdf_page=3,
                heading_level=1,
            ),
        ]
        return chunking.build_chunks(
            units,
            strategy="fixed",
            fixed_target_words=5,
            fixed_overlap_words=0,
            struct_max_chars=200,
            struct_min_chars=10,
        )

    def test_upsert_idempotent(self) -> None:
        chunks = self._chunks()
        vecs = [_vec([1.0, 0.0, 0.0]) for _ in chunks]
        self.store.upsert_chunks(chunks, embeddings=vecs, embed_model="m", embed_dim=3)
        self.assertEqual(self.store.count_chunks(), len(chunks))
        # Повторный upsert — количество не растёт.
        self.store.upsert_chunks(chunks, embeddings=vecs, embed_model="m", embed_dim=3)
        self.assertEqual(self.store.count_chunks(), len(chunks))

    def test_cosine_ranking(self) -> None:
        chunks = self._chunks()
        vecs = [
            _vec([1.0, 0.0, 0.0]),
            _vec([0.0, 1.0, 0.0]),
            _vec([0.0, 0.0, 1.0]),
        ]
        self.store.upsert_chunks(chunks, embeddings=vecs, embed_model="m", embed_dim=3)
        hits = self.store.cosine_search(_vec([1.0, 0.0, 0.0]), top_k=2)
        self.assertEqual(len(hits), 2)
        # Первый должен быть точный матч по [1,0,0].
        self.assertAlmostEqual(hits[0].score, 1.0, places=5)
        # Второй — ортогональный, score 0.
        self.assertAlmostEqual(hits[1].score, 0.0, places=5)
        # pdf_page сохранился только там, где был.
        for h in hits:
            if h.chunk_id == chunks[1].chunk_id:
                self.assertEqual(h.pdf_page, 2)

    def test_metadata_roundtrip(self) -> None:
        chunks = self._chunks()
        vecs = [_vec([0.1, 0.2, 0.3]) for _ in chunks]
        self.store.upsert_chunks(chunks, embeddings=vecs, embed_model="m", embed_dim=3)
        self.store.set_meta("strategy", "fixed")
        self.assertEqual(self.store.get_meta("strategy"), "fixed")
        self.store.add_provenance(
            source="pdf",
            file_path="x.pdf",
            file_sha256="abc",
            char_count=10,
            extra={"pages": 5},
        )
        self.store.close()
        # Открываем заново — данные должны сохраниться.
        store2 = IndexStore(self.db)
        try:
            self.assertEqual(store2.count_chunks(), len(chunks))
            self.assertEqual(store2.get_meta("strategy"), "fixed")
        finally:
            store2.close()

    # ------------------------------------------------------------------
    # Регрессии: file_path, валидация размерности/чисел, replace_corpus
    # ------------------------------------------------------------------

    def test_file_path_written_for_each_source(self) -> None:
        """upsert_chunks должен писать непустой file_path, если мэппинг задан."""

        chunks = self._chunks()
        vecs = [_vec([1.0, 0.0, 0.0]) for _ in chunks]
        mapping = {
            "confluence": "/data/confluence/page.json",
            "pdf": "/data/docs/book.pdf",
        }
        n = self.store.upsert_chunks(
            chunks,
            embeddings=vecs,
            embed_model="m",
            embed_dim=3,
            file_paths=mapping,
        )
        self.assertEqual(n, len(chunks))

        conn = sqlite3.connect(str(self.db))
        try:
            rows = conn.execute(
                "SELECT source, file_path FROM chunks ORDER BY source, chunk_index"
            ).fetchall()
        finally:
            conn.close()

        seen = {src: path for src, path in rows}
        # Каждый источник должен иметь ровно свой путь, без пустых строк.
        for src in ("confluence", "pdf"):
            self.assertIn(src, seen, msg=f"missing source {src}")
            self.assertTrue(seen[src], msg=f"empty file_path for {src}")
        # Конкретные источники:
        self.assertEqual(seen["confluence"], "/data/confluence/page.json")
        self.assertEqual(seen["pdf"], "/data/docs/book.pdf")

    def test_file_path_recomputed_on_reupsert(self) -> None:
        """Повторный upsert с новым file_path перезаписывает старый путь."""

        chunks = self._chunks()
        vecs = [_vec([1.0, 0.0, 0.0]) for _ in chunks]
        self.store.upsert_chunks(
            chunks,
            embeddings=vecs,
            embed_model="m",
            embed_dim=3,
            file_paths={"confluence": "/old/conf.json", "pdf": "/old/book.pdf"},
        )
        # Новые пути должны попасть в БД, даже если chunk_id те же.
        self.store.upsert_chunks(
            chunks,
            embeddings=vecs,
            embed_model="m",
            embed_dim=3,
            file_paths={"confluence": "/new/conf.json", "pdf": "/new/book.pdf"},
        )

        conn = sqlite3.connect(str(self.db))
        try:
            row = conn.execute(
                "SELECT file_path FROM chunks WHERE source='confluence' LIMIT 1"
            ).fetchone()
        finally:
            conn.close()
        self.assertEqual(row[0], "/new/conf.json")

    def test_upsert_rejects_mismatched_dim(self) -> None:
        """Вектор с другой размерностью → ValueError, ничего не записано."""

        chunks = self._chunks()
        vecs = [_vec([1.0, 0.0, 0.0]) for _ in chunks]
        bad = list(vecs)
        bad[1] = _vec([1.0, 0.0])  # другая размерность
        with self.assertRaises(ValueError) as cm:
            self.store.upsert_chunks(
                bad, embeddings=bad, embed_model="m", embed_dim=3
            )
        self.assertIn("dim", str(cm.exception).lower())
        self.assertEqual(self.store.count_chunks(), 0)

    def test_upsert_rejects_empty_vector(self) -> None:
        """Пустой вектор → ValueError."""

        chunks = self._chunks()
        vecs = [_vec([1.0, 0.0, 0.0]) for _ in chunks]
        vecs[0] = []  # пустой
        with self.assertRaises(ValueError):
            self.store.upsert_chunks(
                vecs, embeddings=vecs, embed_model="m", embed_dim=3
            )
        self.assertEqual(self.store.count_chunks(), 0)

    def test_upsert_rejects_nan(self) -> None:
        """NaN в эмбеддинге → ValueError, транзакция откатывается."""

        chunks = self._chunks()
        vecs = [_vec([1.0, 0.0, 0.0]) for _ in chunks]
        vecs[0] = [float("nan"), 0.0, 0.0]
        with self.assertRaises(ValueError):
            self.store.upsert_chunks(
                vecs, embeddings=vecs, embed_model="m", embed_dim=3
            )
        self.assertEqual(self.store.count_chunks(), 0)

    def test_upsert_rejects_inf(self) -> None:
        """Inf в эмбеддинге → ValueError, ничего не сохраняется."""

        chunks = self._chunks()
        vecs = [_vec([1.0, 0.0, 0.0]) for _ in chunks]
        vecs[0] = [float("inf"), 0.0, 0.0]
        with self.assertRaises(ValueError):
            self.store.upsert_chunks(
                vecs, embeddings=vecs, embed_model="m", embed_dim=3
            )
        self.assertEqual(self.store.count_chunks(), 0)

    def test_upsert_rejects_non_positive_embed_dim(self) -> None:
        """embed_dim <= 0 → ValueError до любых операций."""

        chunks = self._chunks()
        vecs = [_vec([1.0, 0.0, 0.0]) for _ in chunks]
        with self.assertRaises(ValueError):
            self.store.upsert_chunks(
                chunks, embeddings=vecs, embed_model="m", embed_dim=0
            )

    def test_replace_corpus_drops_stale_chunks_and_provenance(self) -> None:
        """replace_corpus удаляет старые чанки и provenance полностью."""

        chunks = self._chunks()
        vecs = [_vec([1.0, 0.0, 0.0]) for _ in chunks]
        # Сначала заливаем "прошлую" сборку с большим числом чанков и старой provenance.
        self.store.upsert_chunks(
            chunks,
            embeddings=vecs,
            embed_model="old-model",
            embed_dim=3,
            file_paths={"confluence": "/old/conf.json", "pdf": "/old/book.pdf"},
        )
        self.store.add_provenance(
            source="confluence",
            file_path="/old/conf.json",
            file_sha256="old-sha",
            char_count=1,
        )
        self.store.add_provenance(
            source="pdf",
            file_path="/old/book.pdf",
            file_sha256="old-sha",
            char_count=1,
        )

        # Новая сборка: меньше чанков (берём только первые два),
        # другая модель, другие пути, новая provenance.
        new_chunks = chunks[:2]
        new_vecs = vecs[:2]
        n = self.store.replace_corpus(
            strategy="fixed",
            chunks=new_chunks,
            embeddings=new_vecs,
            embed_model="new-model",
            embed_dim=3,
            file_paths={"confluence": "/new/conf.json", "pdf": "/new/book.pdf"},
            source_to_provenance={
                "confluence": (
                    "/new/conf.json",
                    "new-sha",
                    42,
                    {"page_id": "p1"},
                ),
            },
        )
        self.assertEqual(n, 2)

        conn = sqlite3.connect(str(self.db))
        try:
            chunk_count = conn.execute(
                "SELECT COUNT(*) FROM chunks"
            ).fetchone()[0]
            chunk_rows = conn.execute(
                "SELECT source, file_path, embed_model FROM chunks "
                "ORDER BY chunk_index"
            ).fetchall()
            prov_rows = conn.execute(
                "SELECT source, file_path, file_sha256, char_count "
                "FROM provenance ORDER BY id"
            ).fetchall()
        finally:
            conn.close()

        # Чанков ровно столько, сколько в новой сборке.
        self.assertEqual(chunk_count, 2)
        # Старый третий чанк исчез, старая модель больше не встречается.
        models = {row[2] for row in chunk_rows}
        self.assertEqual(models, {"new-model"})
        # Новые пути записаны.
        for src, path, _ in chunk_rows:
            self.assertTrue(path.startswith("/new/"), msg=f"path={path!r} for {src}")

        # Provenance — только новая запись, никаких следов старой.
        self.assertEqual(len(prov_rows), 1)
        self.assertEqual(prov_rows[0][0], "confluence")
        self.assertEqual(prov_rows[0][1], "/new/conf.json")
        self.assertEqual(prov_rows[0][2], "new-sha")
        self.assertEqual(prov_rows[0][3], 42)

    def test_replace_corpus_rollback_on_bad_embeddings(self) -> None:
        """Если эмбеддинги невалидны — replace_corpus не должен ничего менять."""

        chunks = self._chunks()
        vecs = [_vec([1.0, 0.0, 0.0]) for _ in chunks]
        # Сначала строим валидное состояние.
        self.store.replace_corpus(
            strategy="fixed",
            chunks=chunks,
            embeddings=vecs,
            embed_model="ok",
            embed_dim=3,
            file_paths={"confluence": "/a", "pdf": "/b"},
            source_to_provenance={
                "confluence": ("/a", "sha-a", 10, {}),
                "pdf": ("/b", "sha-b", 20, {}),
            },
        )
        before_chunks = self.store.count_chunks()
        before_prov = self.store._conn.execute(
            "SELECT COUNT(*) FROM provenance"
        ).fetchone()[0]
        self.assertEqual(before_chunks, len(chunks))
        self.assertEqual(before_prov, 2)

        # Теперь пытаемся перезаписать мусором — должна быть ошибка и
        # состояние БД должно остаться прежним.
        bad = list(vecs)
        bad[0] = [float("nan"), 0.0, 0.0]
        with self.assertRaises(ValueError):
            self.store.replace_corpus(
                strategy="fixed",
                chunks=chunks,
                embeddings=bad,
                embed_model="bad",
                embed_dim=3,
                file_paths={"confluence": "/x", "pdf": "/y"},
                source_to_provenance={
                    "confluence": ("/x", "sha-x", 99, {}),
                },
            )

        # Всё на месте: старые чанки и provenance, без следов «новой» сборки.
        self.assertEqual(self.store.count_chunks(), before_chunks)
        prov_after = self.store._conn.execute(
            "SELECT COUNT(*) FROM provenance"
        ).fetchone()[0]
        self.assertEqual(prov_after, before_prov)

        conn = sqlite3.connect(str(self.db))
        try:
            models = {
                row[0]
                for row in conn.execute(
                    "SELECT DISTINCT embed_model FROM chunks"
                ).fetchall()
            }
        finally:
            conn.close()
        self.assertEqual(models, {"ok"})

    def test_replace_corpus_empty_chunks_wipes_old(self) -> None:
        """Пустой новый корпус при replace полностью стирает старые данные."""

        chunks = self._chunks()
        vecs = [_vec([1.0, 0.0, 0.0]) for _ in chunks]
        self.store.replace_corpus(
            strategy="fixed",
            chunks=chunks,
            embeddings=vecs,
            embed_model="m",
            embed_dim=3,
            file_paths={"confluence": "/a", "pdf": "/b"},
            source_to_provenance={
                "confluence": ("/a", "sha-a", 1, {}),
            },
        )
        # Теперь «новый» запуск с пустым корпусом — никаких старых чанков.
        n = self.store.replace_corpus(
            strategy="fixed",
            chunks=[],
            embeddings=[],
            embed_model="m",
            embed_dim=3,
            file_paths={},
            source_to_provenance={},
        )
        self.assertEqual(n, 0)
        self.assertEqual(self.store.count_chunks(), 0)
        self.assertEqual(
            self.store._conn.execute(
                "SELECT COUNT(*) FROM provenance"
            ).fetchone()[0],
            0,
        )

    def test_stored_embed_dim(self) -> None:
        """stored_embed_dim возвращает размерность из БД."""

        chunks = self._chunks()
        vecs = [_vec([0.1] * 5) for _ in chunks]
        self.store.upsert_chunks(chunks, embeddings=vecs, embed_model="m", embed_dim=5)
        self.assertEqual(self.store.stored_embed_dim(), 5)


if __name__ == "__main__":
    unittest.main()