"""Сквозной тест: парсинг + корпус + чанки + индекс + запрос."""

from __future__ import annotations

import http.server
import json
import shutil
import socket
import sys
import tempfile
import threading
import unittest
from contextlib import contextmanager
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from docindexing import build as build_mod  # noqa: E402
from docindexing import query as query_mod  # noqa: E402
from docindexing.config import RunConfig  # noqa: E402
from docindexing.embeddings import EmbeddingResult  # noqa: E402
from docindexing.pdf_text import PdfPage, PdfDoc  # noqa: E402


def _free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


@contextmanager
def _fake_ollama(vectors_factory=None, model_name="fake", dim=4):
    """Поднять локальный HTTP-сервер, отвечающий на /api/embed.

    ``vectors_factory`` — функция ``(count, request_payload) -> list[list[float]]``.
    Если не задана, используется детерминированная фабрика, индексирующая
    позиции в батче.
    """

    counter = {"n": 0}
    lock = threading.Lock()

    def _default_factory(count, _payload):
        return [
            [0.01 * (i + 1), 0.02 * (i + 1), 0.03 * (i + 1), 0.04 * (i + 1)]
            for i in range(count)
        ]

    factory = vectors_factory or _default_factory

    class H(http.server.BaseHTTPRequestHandler):
        def log_message(self, *a, **k):
            return

        def handle_one_request(self):  # type: ignore[override]
            try:
                super().handle_one_request()
            finally:
                try:
                    self.connection.shutdown(2)
                except OSError:
                    pass
                self.connection.close()

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
            with lock:
                counter["n"] += 1
            vecs = factory(len(inputs), payload)
            body = {"model": model_name, "embeddings": vecs}
            data = json.dumps(body).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

    port = _free_port()
    httpd = http.server.HTTPServer(
        ("127.0.0.1", port), H,
        bind_and_activate=False,
    )
    httpd.allow_reuse_address = True
    httpd.server_bind()
    httpd.server_activate()
    th = threading.Thread(target=httpd.serve_forever, daemon=True)
    th.start()
    try:
        yield f"http://127.0.0.1:{port}", counter
    finally:
        httpd.shutdown()
        httpd.server_close()


def _stub_pdf_extractor(monkey_attr: dict, doc: PdfDoc) -> None:
    """Подменить extract_pdf через monkey-patch модуля."""

    def fake(path):
        return doc

    monkey_attr["fake"] = fake


def _make_cfg(*, dir_, cf, pdf_path, url, embed_model="fake") -> RunConfig:
    return RunConfig(
        confluence_json=cf,
        pdf_path=pdf_path,
        output_dir=dir_ / "index_out",
        ollama_url=url,
        embed_model=embed_model,
        fixed_tokens=10,
        fixed_overlap=2,
        struct_max_chars=200,
        struct_min_chars=20,
        embed_batch_size=4,
        embed_max_retries=2,
        embed_retry_base_delay=0.01,
        embed_timeout=10.0,
    )


class EndToEndTest(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        # Копируем фикстуру Confluence в рабочую папку.
        self.cf = self.dir / "conf.json"
        shutil.copy(
            Path(__file__).resolve().parent / "fixtures" / "confluence_sample.json",
            self.cf,
        )

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def test_build_and_query(self) -> None:
        pdf_doc = PdfDoc(
            path=self.dir / "stub.pdf",
            pages=[
                PdfPage(page_number=1, text="Введение в серверы", is_heading=True, heading_level=1),
                PdfPage(page_number=2, text="Архитектура стойки и PDU", is_heading=True, heading_level=1),
                PdfPage(page_number=3, text="Мониторинг и эксплуатация серверов.", is_heading=False),
            ],
            raw_char_count=200,
            title_hint="Введение в серверы",
        )
        pdf_path = self.dir / "stub.pdf"
        pdf_path.write_bytes(b"%PDF-stub")

        with _fake_ollama() as (url, counter):
            cfg = RunConfig(
                confluence_json=self.cf,
                pdf_path=pdf_path,
                output_dir=self.dir / "index_out",
                ollama_url=url,
                embed_model="fake",
                fixed_tokens=10,
                fixed_overlap=2,
                struct_max_chars=200,
                struct_min_chars=20,
                embed_batch_size=4,
                embed_max_retries=2,
                embed_retry_base_delay=0.01,
                embed_timeout=10.0,
            )

            # Подменяем extract_pdf через временный патч модуля.
            # Патчим имя в самом build, потому что там сделан прямой импорт.
            from docindexing import build as build_pkg

            original_build_extract = build_pkg.extract_pdf
            build_pkg.extract_pdf = lambda p: pdf_doc
            try:
                metrics = build_mod.build_index(cfg)
            finally:
                build_pkg.extract_pdf = original_build_extract

            self.assertGreater(metrics["chunks"]["fixed"]["count"], 0)
            self.assertGreater(metrics["chunks"]["structural"]["count"], 0)
            self.assertEqual(metrics["chunks"]["fixed"]["dim"], 4)
            self.assertEqual(metrics["chunks"]["structural"]["dim"], 4)

            # Запрос
            hits_fixed = query_mod.query_index(
                cfg, question="что?", strategy="fixed", top_k=3
            )
            hits_struct = query_mod.query_index(
                cfg, question="что?", strategy="structural", top_k=3
            )
            self.assertGreater(len(hits_fixed), 0)
            self.assertGreater(len(hits_struct), 0)
            for hits in (hits_fixed, hits_struct):
                for h in hits:
                    self.assertIn("score", h)
                    self.assertIn("text", h)

            # Повторный build должен быть идемпотентным: чанков столько же.
            build_pkg.extract_pdf = lambda p: pdf_doc
            try:
                metrics2 = build_mod.build_index(cfg)
            finally:
                build_pkg.extract_pdf = original_build_extract
            self.assertEqual(
                metrics2["chunks"]["fixed"]["count"],
                metrics["chunks"]["fixed"]["count"],
            )
            self.assertEqual(
                metrics2["chunks"]["structural"]["count"],
                metrics["chunks"]["structural"]["count"],
            )


class ReplaceAndStagingTest(unittest.TestCase):
    """Проверяем: пересборка заменяет корпус целиком; сбой не ломает прошлый индекс."""

    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        self.cf = self.dir / "conf.json"
        shutil.copy(
            Path(__file__).resolve().parent / "fixtures" / "confluence_sample.json",
            self.cf,
        )

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def _pdf_doc(self) -> PdfDoc:
        return PdfDoc(
            path=self.dir / "stub.pdf",
            pages=[
                PdfPage(page_number=1, text="Введение в серверы", is_heading=True, heading_level=1),
                PdfPage(page_number=2, text="Архитектура стойки и PDU", is_heading=True, heading_level=1),
                PdfPage(page_number=3, text="Мониторинг и эксплуатация серверов.", is_heading=False),
            ],
            raw_char_count=200,
            title_hint="Введение в серверы",
        )

    def _patch_extract_pdf(self, doc: PdfDoc):
        from docindexing import build as build_pkg

        original = build_pkg.extract_pdf
        build_pkg.extract_pdf = lambda p: doc
        return original

    def _unpatch_extract_pdf(self, original) -> None:
        from docindexing import build as build_pkg

        build_pkg.extract_pdf = original

    def test_rerun_replaces_corpus_completely(self) -> None:
        """Вторая сборка с другими параметрами чанкования не оставляет старых чанков."""

        pdf_doc = self._pdf_doc()
        pdf_path = self.dir / "stub.pdf"
        pdf_path.write_bytes(b"%PDF-stub")

        with _fake_ollama() as (url, _):
            # Первый прогон: крупное окно → мало чанков.
            cfg1 = RunConfig(
                confluence_json=self.cf,
                pdf_path=pdf_path,
                output_dir=self.dir / "index_out",
                ollama_url=url,
                embed_model="fake",
                fixed_tokens=40,
                fixed_overlap=0,
                struct_max_chars=400,
                struct_min_chars=40,
                embed_batch_size=4,
                embed_max_retries=2,
                embed_retry_base_delay=0.01,
                embed_timeout=10.0,
            )
            original = self._patch_extract_pdf(pdf_doc)
            try:
                metrics1 = build_mod.build_index(cfg1)
            finally:
                self._unpatch_extract_pdf(original)

            # Второй прогон: маленькое окно → много чанков.
            cfg2 = RunConfig(
                confluence_json=self.cf,
                pdf_path=pdf_path,
                output_dir=self.dir / "index_out",
                ollama_url=url,
                embed_model="fake",
                fixed_tokens=4,
                fixed_overlap=0,
                struct_max_chars=80,
                struct_min_chars=10,
                embed_batch_size=4,
                embed_max_retries=2,
                embed_retry_base_delay=0.01,
                embed_timeout=10.0,
            )
            original = self._patch_extract_pdf(pdf_doc)
            try:
                metrics2 = build_mod.build_index(cfg2)
            finally:
                self._unpatch_extract_pdf(original)

        self.assertGreater(metrics2["chunks"]["fixed"]["count"],
                           metrics1["chunks"]["fixed"]["count"])
        self.assertGreater(metrics2["chunks"]["structural"]["count"],
                           metrics1["chunks"]["structural"]["count"])

        # Теперь из БД: чанков должно быть ровно столько, сколько во второй сборке,
        # ни одного «хвоста» от первой.
        from docindexing.index_store import IndexStore
        fixed = IndexStore(cfg2.fixed_db())
        try:
            n_fixed = fixed.count_chunks()
            prov_n = fixed._conn.execute(
                "SELECT COUNT(*) FROM provenance"
            ).fetchone()[0]
        finally:
            fixed.close()
        self.assertEqual(n_fixed, metrics2["chunks"]["fixed"]["count"])
        # Provenance в фиксированной БД должна соответствовать только второй сборке
        # (то есть ровно две записи: confluence и pdf).
        self.assertEqual(prov_n, 2)

        struct = IndexStore(cfg2.structural_db())
        try:
            n_struct = struct.count_chunks()
        finally:
            struct.close()
        self.assertEqual(n_struct, metrics2["chunks"]["structural"]["count"])

    def test_failed_build_leaves_previous_index_intact(self) -> None:
        """При сбое эмбеддингов прошлая валидная БД остаётся доступной."""

        pdf_doc = self._pdf_doc()
        pdf_path = self.dir / "stub.pdf"
        pdf_path.write_bytes(b"%PDF-stub")

        cfg = _make_cfg(dir_=self.dir, cf=self.cf, pdf_path=pdf_path, url="http://unused")

        # Первый, валидный прогон с фейковым Ollama.
        with _fake_ollama() as (url, _):
            cfg_ok = _make_cfg(dir_=self.dir, cf=self.cf, pdf_path=pdf_path, url=url)
            original = self._patch_extract_pdf(pdf_doc)
            try:
                metrics_ok = build_mod.build_index(cfg_ok)
            finally:
                self._unpatch_extract_pdf(original)

            fixed_db = cfg_ok.fixed_db()
            struct_db = cfg_ok.structural_db()
            self.assertTrue(fixed_db.exists())
            self.assertTrue(struct_db.exists())

            # Запоминаем валидные количества чанков.
            from docindexing.index_store import IndexStore

            store = IndexStore(fixed_db)
            try:
                ok_count = store.count_chunks()
            finally:
                store.close()

            # Второй прогон с заведомо сломанным провайдером — порт закрыт.
            cfg_bad = _make_cfg(dir_=self.dir, cf=self.cf, pdf_path=pdf_path,
                                url="http://127.0.0.1:1")  # мёртвый порт
            original = self._patch_extract_pdf(pdf_doc)
            try:
                with self.assertRaises(Exception):
                    build_mod.build_index(cfg_bad)
            finally:
                self._unpatch_extract_pdf(original)

        # Боевые БД не должны были пострадать.
        self.assertTrue(fixed_db.exists())
        self.assertTrue(struct_db.exists())
        # И не должно остаться staging-артефактов.
        self.assertFalse(fixed_db.with_name(fixed_db.name + ".new").exists())
        self.assertFalse(struct_db.with_name(struct_db.name + ".new").exists())

        store = IndexStore(fixed_db)
        try:
            self.assertEqual(store.count_chunks(), ok_count)
            self.assertEqual(store.count_chunks(), metrics_ok["chunks"]["fixed"]["count"])
        finally:
            store.close()

    def test_query_rejects_model_mismatch(self) -> None:
        """Запрос с другой embed_model относительно индекса → ошибка."""

        pdf_doc = self._pdf_doc()
        pdf_path = self.dir / "stub.pdf"
        pdf_path.write_bytes(b"%PDF-stub")

        with _fake_ollama(model_name="fake", dim=4) as (url, _):
            cfg = _make_cfg(dir_=self.dir, cf=self.cf, pdf_path=pdf_path, url=url,
                            embed_model="fake")
            original = self._patch_extract_pdf(pdf_doc)
            try:
                build_mod.build_index(cfg)
            finally:
                self._unpatch_extract_pdf(original)

            # Конфиг запрашивает другую модель — индекс был собран с "fake".
            from docindexing.embeddings import OllamaClient

            class StubClient:
                def embed(self, texts, *, model):
                    return EmbeddingResult(
                        vectors=[[0.1, 0.2, 0.3, 0.4]],
                        model="other-model",
                        dim=4,
                    )

            cfg_mismatch = _make_cfg(dir_=self.dir, cf=self.cf, pdf_path=pdf_path,
                                      url=url, embed_model="other-model")
            with self.assertRaises(RuntimeError) as cm:
                query_mod.query_index(
                    cfg_mismatch,
                    question="что?",
                    strategy="fixed",
                    top_k=3,
                    ollama_client=StubClient(),
                )
            self.assertIn("embed_model", str(cm.exception))

    def test_query_rejects_dim_mismatch_from_provider(self) -> None:
        """Если провайдер вернул вектор другой размерности — ошибка."""

        pdf_doc = self._pdf_doc()
        pdf_path = self.dir / "stub.pdf"
        pdf_path.write_bytes(b"%PDF-stub")

        with _fake_ollama() as (url, _):
            cfg = _make_cfg(dir_=self.dir, cf=self.cf, pdf_path=pdf_path, url=url)
            original = self._patch_extract_pdf(pdf_doc)
            try:
                build_mod.build_index(cfg)
            finally:
                self._unpatch_extract_pdf(original)

            from docindexing.embeddings import OllamaClient

            class WrongDimClient:
                def embed(self, texts, *, model):
                    return EmbeddingResult(
                        vectors=[[0.1, 0.2]],  # dim=2 вместо 4
                        model="fake",
                        dim=2,
                    )

            cfg2 = _make_cfg(dir_=self.dir, cf=self.cf, pdf_path=pdf_path, url=url)
            with self.assertRaises(RuntimeError) as cm:
                query_mod.query_index(
                    cfg2,
                    question="что?",
                    strategy="fixed",
                    top_k=3,
                    ollama_client=WrongDimClient(),
                )
            self.assertIn("dim", str(cm.exception).lower())

    def test_query_rejects_non_finite_query_vector(self) -> None:
        """Нефинитный вектор запроса → ValueError."""

        pdf_doc = self._pdf_doc()
        pdf_path = self.dir / "stub.pdf"
        pdf_path.write_bytes(b"%PDF-stub")

        with _fake_ollama() as (url, _):
            cfg = _make_cfg(dir_=self.dir, cf=self.cf, pdf_path=pdf_path, url=url)
            original = self._patch_extract_pdf(pdf_doc)
            try:
                build_mod.build_index(cfg)
            finally:
                self._unpatch_extract_pdf(original)

            from docindexing.embeddings import OllamaClient

            class NaNClient:
                def embed(self, texts, *, model):
                    return EmbeddingResult(
                        vectors=[[float("nan"), 0.2, 0.3, 0.4]],
                        model="fake",
                        dim=4,
                    )

            cfg2 = _make_cfg(dir_=self.dir, cf=self.cf, pdf_path=pdf_path, url=url)
            with self.assertRaises(ValueError) as cm:
                query_mod.query_index(
                    cfg2,
                    question="что?",
                    strategy="fixed",
                    top_k=3,
                    ollama_client=NaNClient(),
                )
            self.assertIn("finite", str(cm.exception).lower())


if __name__ == "__main__":
    unittest.main()