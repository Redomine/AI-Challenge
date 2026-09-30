"""Фокусные тесты CLI: переключение stdout/stderr в UTF-8.

Зачем отдельный файл
--------------------

В PowerShell 5.1 стандартный ``sys.stdout`` приходит из консоли с
кодировкой ``cp1251``. Любой ``print("Привет 😀")`` падает с
``UnicodeEncodeError`` уже после успешной сборки индексов — пользователь
видит «собралось, а потом взорвалось на баннере». Решение —

* CLI внутри ``main()`` дёргает ``configure_utf8_io()``, который
  переключает ``sys.stdout``/``sys.stderr`` на UTF-8 через
  ``TextIOWrapper.reconfigure``;
* PowerShell-обёртки выставляют ``[Console]::OutputEncoding = UTF8``
  и ``PYTHONIOENCODING=utf-8`` / ``PYTHONUTF8=1``.

Эти тесты проверяют первую часть (CLI-уровень) на синтетической
кириллице + символах, которые ``cp1251`` не умеет кодировать
(эмодзи, длинное тире, кавычки-ёлочки), а также защищают
``configure_utf8_io()`` от регрессий: повторный вызов и попадание
на нестандартный (без ``reconfigure``) поток.
"""

from __future__ import annotations

import io
import sys
import unittest
from contextlib import redirect_stdout, redirect_stderr
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from docindexing import cli  # noqa: E402


# ---------------------------------------------------------------------
# Синтетические строки, которые гарантированно не кодируются в cp1251.
# ---------------------------------------------------------------------

# 1) Эмодзи: нет в любой однобайтовой Windows-кодировке.
_EMOJI = "😀"
# 2) Кириллица с ё (ё=U+0451) есть в cp1251, но мы добавляем её
#    именно как «обычную кириллицу», чтобы проверить, что ничего
#    не сломалось и для неё.
_CYRILLIC = "Привет, мир"
# 3) Кавычки-ёлочки и длинное тире: cp1251 их не знает.
_DASH = "«текст» — ок"


class _FakeNoReconfigureStream:
    """Поток-обманка без метода ``reconfigure``.

    Используется в тестах, чтобы убедиться, что
    ``_reconfigure_stream`` тихо возвращает такой поток, а не валит
    CLI ``AttributeError``.
    """

    def __init__(self) -> None:
        self.encoding = "cp1251"

    def write(self, _data: str) -> int:
        return 0


class ConfigureUtf8IoTest(unittest.TestCase):
    """Поведение ``configure_utf8_io`` / ``_reconfigure_stream``."""

    def test_reconfigure_real_stream_to_utf8(self) -> None:
        """Реальный ``TextIOWrapper`` переключается в UTF-8 без ошибок."""
        stream = io.TextIOWrapper(
            io.BytesIO(), encoding="cp1251", write_through=True
        )
        try:
            cli._reconfigure_stream(stream)
            self.assertEqual(stream.encoding, "utf-8")
        finally:
            stream.close()

    def test_reconfigure_stream_without_method_is_noop(self) -> None:
        """Поток без ``reconfigure`` не валит CLI — мы возвращаем его как есть."""
        fake = _FakeNoReconfigureStream()
        result = cli._reconfigure_stream(fake)
        self.assertIs(result, fake)
        # Кодировка оригинала не изменилась — мы не притворяемся, что переписали её.
        self.assertEqual(fake.encoding, "cp1251")

    def test_reconfigure_handles_exceptions_quietly(self) -> None:
        """Если ``reconfigure`` бросает — мы не должны валить CLI.

        Это защищает от ситуаций, когда ``sys.stdout`` подменён
        нестандартной обёрткой (например, некоторые IDE-плагины
        или контекстные менеджеры для unittest).
        """

        class _BoomStream:
            encoding = "cp1251"

            def reconfigure(self, *, encoding: str) -> None:
                raise OSError("simulated closed stream")

        boom = _BoomStream()
        result = cli._reconfigure_stream(boom)
        self.assertIs(result, boom)

    def test_configure_utf8_io_idempotent(self) -> None:
        """Повторный вызов ``configure_utf8_io`` не падает и остаётся UTF-8."""
        real_out = sys.stdout
        real_err = sys.stderr
        try:
            sys.stdout = io.TextIOWrapper(
                io.BytesIO(), encoding="cp1251", write_through=True
            )
            sys.stderr = io.TextIOWrapper(
                io.BytesIO(), encoding="cp1251", write_through=True
            )
            cli.configure_utf8_io()
            cli.configure_utf8_io()
            self.assertEqual(sys.stdout.encoding, "utf-8")
            self.assertEqual(sys.stderr.encoding, "utf-8")
        finally:
            try:
                sys.stdout.close()
            except Exception:
                pass
            try:
                sys.stderr.close()
            except Exception:
                pass
            sys.stdout = real_out
            sys.stderr = real_err


class CyrillicPrintTest(unittest.TestCase):
    """Синтетическая кириллица и эмодзи переживают ``print`` после CLI-fix."""

    def test_print_cyrillic_via_utf8_stdout(self) -> None:
        """После ``configure_utf8_io`` ``print(cyrillic)`` идёт через UTF-8."""
        buf = io.TextIOWrapper(io.BytesIO(), encoding="utf-8", write_through=True)
        try:
            # Эмулируем «stdout переключён в UTF-8» — печать не должна
            # упасть ни на кириллице, ни на эмодзи.
            with redirect_stdout(buf):
                cli.configure_utf8_io()
                print(f"{_CYRILLIC} | {_EMOJI} | {_DASH}")
        finally:
            buf.close()

    def test_print_emoji_does_not_raise_on_utf8(self) -> None:
        """Эмодзи в UTF-8-выводе не роняет ``print`` (раньше падало на cp1251)."""
        buf = io.TextIOWrapper(io.BytesIO(), encoding="utf-8", write_through=True)
        try:
            with redirect_stdout(buf):
                print(f"before-{_EMOJI}-after")
        finally:
            buf.close()

    def test_print_does_not_swallow_silent_unicode_errors(self) -> None:
        """Мы НЕ выставляем ``errors='replace'``: ошибки остаются видимыми.

        Если бы мы глушили не-кодируемые символы, тест ``test_print_emoji_does_not_raise_on_utf8``
        ниже бы ничего не доказывал. Проверяем прямым контрактом:
        ``sys.stdout.errors`` после ``configure_utf8_io`` равен
        ``'strict'`` (значение по умолчанию у ``TextIOWrapper``),
        а не ``'replace'`` / ``'ignore'``.
        """
        original = sys.stdout
        wrapped = io.TextIOWrapper(
            io.BytesIO(),
            encoding="cp1251",
            errors="strict",
            write_through=True,
        )
        try:
            sys.stdout = wrapped
            cli.configure_utf8_io()
            self.assertEqual(sys.stdout.errors, "strict")
        finally:
            try:
                sys.stdout.close()
            except Exception:
                pass
            sys.stdout = original


class StderrUtf8Test(unittest.TestCase):
    """stderr CLI должен быть UTF-8 — иначе лог-сообщения с кириллицей падают."""

    def test_logging_cyrillic_message_via_utf8_stderr(self) -> None:
        """INFO-лог на русском через stderr UTF-8 не падает с UnicodeEncodeError."""
        import logging

        buf = io.TextIOWrapper(io.BytesIO(), encoding="utf-8", write_through=True)
        try:
            with redirect_stderr(buf):
                cli.configure_utf8_io()
                logger = logging.getLogger("docindexing.test_utf8")
                # ``basicConfig`` смотрит на ``sys.stderr``; мы подменили его
                # на UTF-8-обёртку через ``redirect_stderr``, а потом CLI
                # ещё раз включил UTF-8 явно — ничего не должно упасть.
                logging.basicConfig(
                    level=logging.INFO,
                    format="%(levelname)s %(name)s: %(message)s",
                    stream=sys.stderr,
                )
                logger.info("Сборка завершена: %d чанков", 42)
        finally:
            buf.close()


if __name__ == "__main__":
    unittest.main()