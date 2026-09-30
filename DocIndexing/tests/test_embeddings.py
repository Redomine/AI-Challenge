"""Тесты клиента Ollama: ретраи, размерность, отсутствие фейков."""

from __future__ import annotations

import http.server
import json
import socket
import sys
import threading
import time
import unittest
import warnings
from contextlib import contextmanager
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from docindexing import embeddings  # noqa: E402

# Python 3.13 шумит ResourceWarning на закрытом сокете внутри
# http.client.HTTPResponse → email.feedparser / selectors. Это известный
# stdlib-артефакт, который не зависит от нашего кода. Глушим локально,
# чтобы вывод unittest был чистым.
warnings.filterwarnings("ignore", category=ResourceWarning)


def _free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


@contextmanager
def _fake_ollama(handler_cls):
    port = _free_port()
    # SO_REUSEADDR позволяет сразу переиспользовать порт.
    httpd = http.server.HTTPServer(
        ("127.0.0.1", port), handler_cls,
        bind_and_activate=False,
    )
    httpd.allow_reuse_address = True
    httpd.server_bind()
    httpd.server_activate()
    th = threading.Thread(target=httpd.serve_forever, daemon=True)
    th.start()
    try:
        yield f"http://127.0.0.1:{port}"
    finally:
        httpd.shutdown()
        httpd.server_close()


class _EmbedHandler(http.server.BaseHTTPRequestHandler):
    dim = 4
    fail_first = 0
    fail_body = b""

    def log_message(self, *a, **k):
        return

    def handle_one_request(self):  # type: ignore[override]
        # Закрываем соединение после каждого запроса, чтобы не утекал сокет.
        try:
            super().handle_one_request()
        finally:
            try:
                self.connection.shutdown(2)  # SHUT_RDWR
            except OSError:
                pass
            self.connection.close()

    def do_GET(self):  # noqa: N802
        if self.path == "/api/tags":
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b"{}")
            return
        self.send_response(404)
        self.end_headers()

    def do_POST(self):  # noqa: N802
        length = int(self.headers.get("Content-Length", "0"))
        _ = self.rfile.read(length)
        if _EmbedHandler.fail_first > 0:
            _EmbedHandler.fail_first -= 1
            self.send_response(503)
            self.send_header("Content-Type", "text/plain")
            self.end_headers()
            self.wfile.write(_EmbedHandler.fail_body or b"unavailable")
            return
        body = {
            "model": "fake",
            "embeddings": [[0.1 * i, 0.2 * i, 0.3 * i, 0.4 * i] for i in range(1, 3)],
        }
        data = json.dumps(body).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


class OllamaClientTest(unittest.TestCase):
    def test_basic_embed(self) -> None:
        _EmbedHandler.fail_first = 0
        with _fake_ollama(_EmbedHandler) as url:
            client = embeddings.OllamaClient(url, timeout=5.0, max_retries=0, retry_base_delay=0.01)
            res = client.embed(["one", "two"], model="fake")
            self.assertEqual(res.dim, 4)
            self.assertEqual(len(res.vectors), 2)
            # Длина каждого вектора == dim.
            for v in res.vectors:
                self.assertEqual(len(v), 4)

    def test_retries_transient(self) -> None:
        _EmbedHandler.fail_first = 2  # первые 2 раза вернём 503
        with _fake_ollama(_EmbedHandler) as url:
            client = embeddings.OllamaClient(
                url, timeout=5.0, max_retries=3, retry_base_delay=0.01
            )
            t0 = time.time()
            res = client.embed(["one", "two"], model="fake")
            self.assertEqual(len(res.vectors), 2)
            # Минимум две задержки 0.01, 0.02 → время должно быть > 0.
            self.assertGreater(time.time() - t0, 0.02)

    def test_no_fake_embeddings_on_empty(self) -> None:
        class Bad(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a, **k):
                return

            def do_GET(self):  # noqa: N802
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b"{}")

            def do_POST(self):  # noqa: N802
                length = int(self.headers.get("Content-Length", "0"))
                _ = self.rfile.read(length)
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(b'{"embeddings": []}')

        with _fake_ollama(Bad) as url:
            client = embeddings.OllamaClient(url, timeout=5.0, max_retries=0, retry_base_delay=0.01)
            with self.assertRaises(embeddings.EmbeddingError):
                client.embed(["x"], model="fake")


if __name__ == "__main__":
    unittest.main()
