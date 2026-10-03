"""CLI ``docindexing``: команды ``build``, ``query``, ``compare``."""

from __future__ import annotations

import argparse
import json
import logging
import sys
from pathlib import Path
from typing import TextIO

from . import build, query, report
from .config import (
    add_chunking_arguments,
    add_common_arguments,
    build_config_from_args,
)


def _reconfigure_stream(stream: TextIO, *, encoding: str = "utf-8") -> TextIO:
    """Переключить текстовый поток на ``encoding`` (по умолчанию UTF-8).

    Используется для ``sys.stdout``/``sys.stderr`` в Windows PowerShell 5.1,
    где консоль по умолчанию сидит на ``cp1251``/``cp1252`` и
    ``print("Привет 😀")`` падает с ``UnicodeEncodeError``.

    Контракт:

    * Если поток поддерживает ``reconfigure`` (стандартный текстовый
      ``TextIOWrapper`` из ``sys``) — вызываем его и возвращаем
      обновлённый поток.
    * Если ``reconfigure`` нет (например, unittest подменяет stdout
      на свой объект) — тихо возвращаем тот же поток без изменений,
      чтобы CLI продолжал работать под тестами.
    * Любое исключение внутри ``reconfigure`` (например, ``OSError``
      на закрытом потоке) тоже проглатывается — лучше оставить
      оригинальную кодировку, чем валить CLI.
    """

    reconfigure = getattr(stream, "reconfigure", None)
    if reconfigure is None:
        return stream
    try:
        reconfigure(encoding=encoding)
    except (AttributeError, OSError, ValueError):
        # Не критично: тесты или редкие обёртки могут не поддерживать
        # переключение кодировки. CLI должен продолжить работу.
        return stream
    return stream


def configure_utf8_io() -> None:
    """Включить UTF-8 для ``sys.stdout``/``sys.stderr``.

    Должно вызываться как можно раньше — до любого ``print(...)`` или
    ``logging.basicConfig(...)``, иначе лог-сообщения с кириллицей
    могут упасть ещё до того, как пользователь увидит баннер CLI.

    Безопасно вызывать повторно: повторный ``reconfigure`` на ту же
    кодировку не приводит к ошибке.
    """

    _reconfigure_stream(sys.stdout)
    _reconfigure_stream(sys.stderr)


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="docindexing",
        description="Локальный CLI для индексации Confluence JSON + PDF через Ollama.",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    # build --------------------------------------------------------
    p_build = sub.add_parser("build", help="Собрать два SQLite-индекса.")
    add_common_arguments(p_build)
    add_chunking_arguments(p_build)
    p_build.add_argument("--skip-embeddings", action="store_true",
                        help="Не обращаться к Ollama; только чанковать и писать корпус.")
    p_build.set_defaults(func=_cmd_build)

    # query --------------------------------------------------------
    p_query = sub.add_parser("query", help="Запрос top-k из указанного индекса.")
    add_common_arguments(p_query)
    add_chunking_arguments(p_query)
    p_query.add_argument("--question", required=True, help="Текст запроса.")
    p_query.add_argument(
        "--strategy", choices=["fixed", "structural"], default="structural",
        help="Какой индекс использовать."
    )
    p_query.add_argument("--top-k", type=int, default=5)
    p_query.set_defaults(func=_cmd_query)

    # compare ------------------------------------------------------
    p_cmp = sub.add_parser(
        "compare", help="Сравнить две стратегии на одном наборе вопросов."
    )
    add_common_arguments(p_cmp)
    add_chunking_arguments(p_cmp)
    p_cmp.add_argument(
        "--questions", required=True,
        help="Путь к JSON-файлу со списком вопросов."
    )
    p_cmp.add_argument("--top-k", type=int, default=5)
    p_cmp.add_argument(
        "--output", required=True,
        help="Путь к JSON-файлу с отчётом сравнения."
    )
    p_cmp.set_defaults(func=_cmd_compare)

    return parser


def _cmd_build(args) -> int:
    cfg = build_config_from_args(args)
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    if args.skip_embeddings:
        # Только корпус без эмбеддингов — для отладки.
        from .confluence import load_confluence_doc
        from .confluence_html import load_confluence_html_doc
        from .pdf_text import extract_pdf
        from . import corpus as corpus_mod

        cf = load_confluence_doc(cfg.confluence_json)
        pdf = extract_pdf(cfg.pdf_path)
        cf_html = (
            load_confluence_html_doc(cfg.confluence_html)
            if cfg.confluence_html is not None
            and cfg.confluence_html.exists()
            else None
        )
        built = corpus_mod.build_corpus(
            confluence_doc=cf,
            pdf_doc=pdf,
            pdf_title=cfg.pdf_path.stem,
            confluence_html_doc=cf_html,
        )
        cfg.output_dir.mkdir(parents=True, exist_ok=True)
        corpus_mod.save_corpus(built, cfg.corpus_path())
        print(json.dumps(
            {
                "corpus_units": len(built.units),
                "corpus_path": str(cfg.corpus_path()),
                "confluence_sections": len(cf.sections),
                "pdf_pages": len(pdf.pages),
                "confluence_html_relations": (
                    len(cf_html.relations) if cf_html is not None else 0
                ),
            },
            ensure_ascii=False,
            indent=2,
        ))
        return 0
    metrics = build.build_index(cfg)
    print(json.dumps(metrics, ensure_ascii=False, indent=2))
    return 0


def _cmd_query(args) -> int:
    logging.basicConfig(level=logging.WARNING)
    query.run_query_cli(args)
    return 0


def _cmd_compare(args) -> int:
    logging.basicConfig(level=logging.WARNING)
    report.run_compare_cli(args)
    return 0


def main(argv: list[str] | None = None) -> int:
    # ВАЖНО: включаем UTF-8 ДО ``argparse.parse_args`` и ДО любого
    # ``print(...)``/``logging.basicConfig(...)`` в подкомандах —
    # иначе на Windows PowerShell 5.1 (cp1251) первая кириллическая
    # строка в логе/stdout роняет процесс с UnicodeEncodeError.
    configure_utf8_io()
    parser = _build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":  # pragma: no cover
    configure_utf8_io()
    sys.exit(main())
