"""Конфигурация CLI docindexing.

Все пути и параметры CLI собираются в одной структуре :class:`RunConfig`,
чтобы избежать рассеянных ``argparse``-аргументов и обеспечить согласованное
использование между сборкой индекса, запросами и сравнительным отчётом.
"""

from __future__ import annotations

import argparse
import dataclasses
from pathlib import Path
from typing import Optional

DEFAULT_CONFLUENCE_JSON = Path("input/confluence.json")
# Дополнительный путь к Confluence HTML-выгрузке (``view``, сохранённой
# через браузер). Этот источник опционален: если файла нет, ingest
# просто пропускает этот шаг, и итоговый корпус строится из
# ``confluence.json`` + ``document.pdf`` без потерь.
DEFAULT_CONFLUENCE_HTML = Path("input/confluence.html")
DEFAULT_PDF_PATH = Path("input/document.pdf")
DEFAULT_OUTPUT_DIR = Path("index_out")
DEFAULT_OLLAMA_URL = "http://127.0.0.1:11434"
DEFAULT_EMBED_MODEL = "qwen3-embedding:0.6b"

# Фиксированные размеры окна и перекрытия для стратегии fixed.
DEFAULT_FIXED_TOKENS = 256
DEFAULT_FIXED_OVERLAP = 32

# Целевой размер чанка для стратегии structural (в символах).
DEFAULT_STRUCT_MAX_CHARS = 1600
DEFAULT_STRUCT_MIN_CHARS = 200


@dataclasses.dataclass(frozen=True)
class RunConfig:
    """Полный снимок параметров одного запуска CLI."""

    confluence_json: Path
    pdf_path: Path
    output_dir: Path
    ollama_url: str
    embed_model: str
    fixed_tokens: int
    fixed_overlap: int
    struct_max_chars: int
    struct_min_chars: int
    embed_batch_size: int
    embed_max_retries: int
    embed_retry_base_delay: float
    embed_timeout: float
    # Опциональная HTML-выгрузка Confluence. ``None`` означает, что
    # этот источник не используется (обратная совместимость со
    # старыми скриптами и тестами, которые не передают
    # ``--confluence-html``).
    confluence_html: Optional[Path] = None

    def fixed_db(self) -> Path:
        """Путь к SQLite-индексу стратегии fixed."""

        return self.output_dir / "fixed.sqlite3"

    def structural_db(self) -> Path:
        """Путь к SQLite-индексу стратегии structural."""

        return self.output_dir / "structural.sqlite3"

    def report_path(self) -> Path:
        """Путь к JSON-отчёту сборки индекса."""

        return self.output_dir / "build_report.json"

    def corpus_path(self) -> Path:
        """Путь к JSON-дампу нормализованного корпуса."""

        return self.output_dir / "corpus.json"


def build_config_from_args(args: argparse.Namespace) -> RunConfig:
    """Собрать :class:`RunConfig` из произвольного набора CLI-аргументов.

    Аргументы читаются по принципу «есть значение — берём его, иначе дефолт».
    Это позволяет переиспользовать одну и ту же фабрику для команд
    ``build``, ``query`` и ``compare``.

    Поле ``confluence_html`` опционально: ``None``/пустая строка →
    HTML-выгрузка не используется, что сохраняет обратную
    совместимость со старыми скриптами и тестами, которые не
    передают ``--confluence-html``.
    """

    def _opt(name: str, default):
        return getattr(args, name, default)

    confluence_html = _opt("confluence_html", None)
    if isinstance(confluence_html, str):
        confluence_html = confluence_html.strip() or None
    return RunConfig(
        confluence_json=Path(_opt("confluence_json", DEFAULT_CONFLUENCE_JSON)),
        confluence_html=Path(confluence_html) if confluence_html else None,
        pdf_path=Path(_opt("pdf_path", DEFAULT_PDF_PATH)),
        output_dir=Path(_opt("output_dir", DEFAULT_OUTPUT_DIR)),
        ollama_url=_opt("ollama_url", DEFAULT_OLLAMA_URL),
        embed_model=_opt("embed_model", DEFAULT_EMBED_MODEL),
        fixed_tokens=int(_opt("fixed_tokens", DEFAULT_FIXED_TOKENS)),
        fixed_overlap=int(_opt("fixed_overlap", DEFAULT_FIXED_OVERLAP)),
        struct_max_chars=int(_opt("struct_max_chars", DEFAULT_STRUCT_MAX_CHARS)),
        struct_min_chars=int(_opt("struct_min_chars", DEFAULT_STRUCT_MIN_CHARS)),
        embed_batch_size=int(_opt("embed_batch_size", 16)),
        embed_max_retries=int(_opt("embed_max_retries", 4)),
        embed_retry_base_delay=float(_opt("embed_retry_base_delay", 0.5)),
        embed_timeout=float(_opt("embed_timeout", 60.0)),
    )


def add_common_arguments(parser: argparse.ArgumentParser) -> None:
    """Подключить к под-парсеру набор общих аргументов."""

    parser.add_argument(
        "--confluence-json",
        default=str(DEFAULT_CONFLUENCE_JSON),
        help="Путь к JSON-выгрузке страницы Confluence REST API.",
    )
    parser.add_argument(
        "--confluence-html",
        dest="confluence_html",
        default=str(DEFAULT_CONFLUENCE_HTML),
        help=(
            "Путь к HTML-выгрузке Confluence (view, сохранённой "
            "через браузер). Опционально: если файла нет, этот "
            "источник пропускается без ошибок."
        ),
    )
    parser.add_argument(
        "--pdf",
        dest="pdf_path",
        default=str(DEFAULT_PDF_PATH),
        help="Путь к PDF-файлу базы знаний.",
    )
    parser.add_argument(
        "--output-dir",
        default=str(DEFAULT_OUTPUT_DIR),
        help="Каталог для индексов, корпуса и отчётов.",
    )
    parser.add_argument(
        "--ollama-url",
        default=DEFAULT_OLLAMA_URL,
        help="Базовый URL Ollama (например http://127.0.0.1:11434).",
    )
    parser.add_argument(
        "--embed-model",
        default=DEFAULT_EMBED_MODEL,
        help="Имя модели эмбеддингов Ollama.",
    )


def add_chunking_arguments(parser: argparse.ArgumentParser) -> None:
    """Подключить параметры чанкования и эмбеддингов."""

    parser.add_argument("--fixed-tokens", type=int, default=DEFAULT_FIXED_TOKENS)
    parser.add_argument("--fixed-overlap", type=int, default=DEFAULT_FIXED_OVERLAP)
    parser.add_argument(
        "--struct-max-chars", type=int, default=DEFAULT_STRUCT_MAX_CHARS
    )
    parser.add_argument(
        "--struct-min-chars", type=int, default=DEFAULT_STRUCT_MIN_CHARS
    )
    parser.add_argument("--embed-batch-size", type=int, default=16)
    parser.add_argument("--embed-max-retries", type=int, default=4)
    parser.add_argument("--embed-retry-base-delay", type=float, default=0.5)
    parser.add_argument("--embed-timeout", type=float, default=60.0)


__all__ = [
    "DEFAULT_CONFLUENCE_JSON",
    "DEFAULT_CONFLUENCE_HTML",
    "DEFAULT_PDF_PATH",
    "DEFAULT_OUTPUT_DIR",
    "DEFAULT_OLLAMA_URL",
    "DEFAULT_EMBED_MODEL",
    "DEFAULT_FIXED_TOKENS",
    "DEFAULT_FIXED_OVERLAP",
    "DEFAULT_STRUCT_MAX_CHARS",
    "DEFAULT_STRUCT_MIN_CHARS",
    "RunConfig",
    "build_config_from_args",
    "add_common_arguments",
    "add_chunking_arguments",
]


def parse_optional_path(value: Optional[str]) -> Optional[Path]:
    """Нормализовать путь из CLI: пустая строка → ``None``."""

    if value is None or value == "":
        return None
    return Path(value)
