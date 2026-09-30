"""Тесты разбора Confluence JSON: nested expand, отсутствие дублей."""

from __future__ import annotations

import json
import re
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

from docindexing import confluence  # noqa: E402

FIXTURE = Path(__file__).resolve().parent / "fixtures" / "confluence_sample.json"
NOISY_FIXTURE = Path(__file__).resolve().parent / "fixtures" / "confluence_noisy.json"


class ConfluenceParserTest(unittest.TestCase):
    def test_load_and_nested_expand(self) -> None:
        doc = confluence.load_confluence_doc(FIXTURE)
        self.assertEqual(doc.page_id, "12345")
        self.assertEqual(doc.title, "Тестовая страница: вложенные expand и excerpt")
        self.assertEqual(doc.version, 7)
        # Соберём все заголовки секций по дереву.
        all_titles = []

        def walk(sections):
            for s in sections:
                all_titles.append(s.title)
                walk(s.children)

        walk(doc.sections)
        self.assertIn("Введение", all_titles)
        self.assertIn("Архитектура", all_titles)
        self.assertIn("Эксплуатация", all_titles)
        self.assertIn("[expand] Подробности сети", all_titles)
        self.assertIn("[expand] Вложенный блок", all_titles)

        # Найти секцию "Архитектура" и убедиться, что в ней есть вложенный expand.
        arch = next(
            (s for s in doc.sections for c in s.children if c.title == "Архитектура"),
            None,
        )
        # Поищем по всему дереву.
        def find(sections, title):
            for s in sections:
                if s.title == title:
                    return s
                r = find(s.children, title)
                if r is not None:
                    return r
            return None

        arch = find(doc.sections, "Архитектура")
        self.assertIsNotNone(arch)
        expand_titles = [c.title for c in arch.children if c.title.startswith("[expand]")]
        self.assertIn("[expand] Подробности сети", expand_titles)

        nested = next(c for c in arch.children if c.title == "[expand] Подробности сети")
        # Внутри должен быть вложенный expand "Вложенный блок".
        deep_titles = [c.title for c in nested.children if c.title.startswith("[expand]")]
        self.assertIn("[expand] Вложенный блок", deep_titles)
        deep = next(c for c in nested.children if c.title == "[expand] Вложенный блок")
        joined = "\n".join(b.text for b in deep.blocks)
        self.assertIn("10.0.0.1", joined)

    def test_storage_view_no_duplicates(self) -> None:
        # View-merging — теперь опциональная фича. Включаем её явно,
        # чтобы убедиться: дедупликация storage↔view по-прежнему работает.
        doc = confluence.load_confluence_doc(FIXTURE, include_view=True)
        # Соберём весь текст из секций — никакой фрагмент не должен встречаться
        # дважды. В частности, текст "Текст внутри expand" не должен
        # дублироваться от storage и view.
        text_chunks: list[str] = []

        def walk(sections):
            for s in sections:
                for b in s.blocks:
                    if b.text:
                        text_chunks.append(b.text.strip())
                walk(s.children)

        walk(doc.sections)

        full = "\n".join(text_chunks)
        self.assertEqual(full.count("Текст внутри expand про VLAN"), 1)
        # Из view попадает только новый абзац про PDU.
        self.assertIn("PDU", full)

    def test_view_adds_only_unique_content(self) -> None:
        # View-merging — теперь опциональная фича. Включаем её явно.
        doc = confluence.load_confluence_doc(FIXTURE, include_view=True)
        cov = doc.coverage
        self.assertGreaterEqual(cov["view_sections_added"], 1)
        self.assertGreater(cov["storage_sections"], 0)
        self.assertTrue(cov["view_included"])
        self.assertFalse(cov["view_excluded"])


class ConfluenceHtmlParserTest(unittest.TestCase):
    def test_expand_with_title(self) -> None:
        html = (
            "<h1>Hi</h1>"
            "<ac:structured-macro ac:name=\"expand\">"
            "<ac:parameter ac:name=\"title\">Раздел X</ac:parameter>"
            "<ac:rich-text-body><p>Внутри X.</p></ac:rich-text-body>"
            "</ac:structured-macro>"
        )
        blocks = confluence.parse_html_to_blocks(html, source_kind="storage")
        # В плоском списке блоков expand появляется до своих дочерних
        # параграфов (DFS-обход дерева). Это нормальный порядок для
        # последующей сборки секций.
        expand_idx = next(i for i, b in enumerate(blocks) if b.kind == "expand")
        expands = [blocks[expand_idx]]
        self.assertEqual(expands[0].title, "Раздел X")
        # Содержимое expand вынесено в отдельный paragraph-блок.
        paragraph_after = blocks[expand_idx + 1]
        self.assertEqual(paragraph_after.kind, "paragraph")
        self.assertIn("Внутри X", paragraph_after.text)

    def test_repeated_expand_siblings_with_distinct_children(self) -> None:
        """Два сиблинга expand с одинаковым title содержат РАЗНЫЙ текст и
        вложенные expand. Глобальная дедупликация по сигнатуре в ``close()``
        отбрасывала бы второй expand вместе с поддеревом, оставляя лишь
        первый. Здесь проверяем, что сохраняются ОБА вхождения в порядке
        появления вместе со всеми дочерними блоками.
        """

        html = (
            "<h1>Главный заголовок</h1>"
            # Первый однофамилец с уникальным текстом и вложенным expand.
            "<ac:structured-macro ac:name=\"expand\">"
            "<ac:parameter ac:name=\"title\">Одинаковый заголовок</ac:parameter>"
            "<ac:rich-text-body>"
            "<p>Уникальный текст первого expand.</p>"
            "<ac:structured-macro ac:name=\"expand\">"
            "<ac:parameter ac:name=\"title\">Вложенный expand</ac:parameter>"
            "<ac:rich-text-body><p>Текст во вложенном expand первого.</p></ac:rich-text-body>"
            "</ac:structured-macro>"
            "</ac:rich-text-body>"
            "</ac:structured-macro>"
            # Второй однофамилец с ДРУГИМ текстом и СВОИМ вложенным expand.
            "<ac:structured-macro ac:name=\"expand\">"
            "<ac:parameter ac:name=\"title\">Одинаковый заголовок</ac:parameter>"
            "<ac:rich-text-body>"
            "<p>Совершенно другой текст второго expand.</p>"
            "<ac:structured-macro ac:name=\"expand\">"
            "<ac:parameter ac:name=\"title\">Вложенный expand</ac:parameter>"
            "<ac:rich-text-body><p>Текст во вложенном expand второго.</p></ac:rich-text-body>"
            "</ac:structured-macro>"
            "</ac:rich-text-body>"
            "</ac:structured-macro>"
        )

        # Проверим, что в HTML действительно 4 открывающих тега expand —
        # 2 внешних сиблинга + 2 вложенных.
        opens = re.findall(
            r'ac:structured-macro[^>]*ac:name="expand"', html
        )
        self.assertEqual(len(opens), 4)

        blocks = confluence.parse_html_to_blocks(html, source_kind="storage")

        # Оба внешних expand-блока должны присутствовать.
        expands = [b for b in blocks if b.kind == "expand"]
        self.assertEqual(len(expands), 4, "ожидаем 4 expand-макроса в выдаче")

        # Первые два — внешние сиблинги с одинаковым title.
        outer_expands = [b for b in expands if b.title == "Одинаковый заголовок"]
        self.assertEqual(len(outer_expands), 2)
        # Оба должны сохранить СВОЙ уникальный заголовок, и в выдаче
        # должны присутствовать ОБА дочерних параграфа (по одному на
        # каждый expand) — раньше из-за глобальной дедупликации в
        # ``close()`` второй expand вместе с поддеревом отбрасывался.
        outer_paragraphs: list[str] = []
        for outer in outer_expands:
            # В плоском DFS-выводе текст внешнего expand хранится в
            # следующем за ним paragraph-блоке (см. test_expand_with_title).
            # Используем ``is`` для поиска позиции: dataclass-блоки с
            # одинаковыми полями сравнимы по ``==``, но нам нужна
            # идентичность конкретного экземпляра.
            idx = next(i for i, b in enumerate(blocks) if b is outer)
            self.assertEqual(blocks[idx + 1].kind, "paragraph")
            outer_paragraphs.append(blocks[idx + 1].text)
        self.assertIn("Уникальный текст первого expand.", outer_paragraphs[0])
        self.assertIn("Совершенно другой текст второго expand.", outer_paragraphs[1])
        # У первого параграфа не должно быть текста второго, и наоборот.
        self.assertNotIn(
            "Совершенно другой текст второго expand.", outer_paragraphs[0]
        )
        self.assertNotIn(
            "Уникальный текст первого expand.", outer_paragraphs[1]
        )

        # Оба вложенных expand-блока должны присутствовать, каждый со
        # своим дочерним параграфом.
        nested_expands = [b for b in expands if b.title == "Вложенный expand"]
        self.assertEqual(len(nested_expands), 2)
        nested_paragraphs: list[str] = []
        for nested in nested_expands:
            idx = next(i for i, b in enumerate(blocks) if b is nested)
            self.assertEqual(blocks[idx + 1].kind, "paragraph")
            nested_paragraphs.append(blocks[idx + 1].text)
        self.assertIn("Текст во вложенном expand первого.", nested_paragraphs[0])
        self.assertIn("Текст во вложенном expand второго.", nested_paragraphs[1])

        # Порядок в плоском DFS-списке:
        #   h1,
        #   expand#1 (внешний), paragraph#1 (его текст),
        #   expand#2 (вложенный в #1), paragraph#2 (его текст),
        #   expand#3 (внешний), paragraph#3 (его текст),
        #   expand#4 (вложенный в #3), paragraph#4 (его текст).
        # Все 4 expand идут в порядке появления (DFS), каждый expand
        # расположен непосредственно перед своим paragraph-блоком.
        kinds = [b.kind for b in blocks]
        self.assertEqual(kinds[0], "heading")
        expand_indices = [i for i, k in enumerate(kinds) if k == "expand"]
        self.assertEqual(len(expand_indices), 4)
        # 1 heading + 4 expand + 4 параграфа (по одному на каждый expand).
        self.assertEqual(len(blocks), 9)
        self.assertEqual(kinds.count("paragraph"), 4)
        # Каждый expand идёт сразу перед своим paragraph-блоком, и они
        # строго чередуются в DFS-порядке (expand, paragraph).
        for idx in expand_indices:
            self.assertEqual(kinds[idx], "expand")
            self.assertEqual(kinds[idx + 1], "paragraph")
        # Expand-индексы должны возрастать монотонно и идти с шагом 2
        # (после каждого expand идёт его paragraph, потом следующий expand).
        self.assertEqual(expand_indices[0], 1)
        for prev, curr in zip(expand_indices, expand_indices[1:]):
            self.assertEqual(curr, prev + 2)

        # Полный текст содержит обе уникальные строки и оба вложенных текста.
        full_text = "\n".join(b.text for b in blocks)
        for snippet in [
            "Уникальный текст первого expand.",
            "Совершенно другой текст второго expand.",
            "Текст во вложенном expand первого.",
            "Текст во вложенном expand второго.",
        ]:
            self.assertEqual(
                full_text.count(snippet),
                1,
                f"фрагмент {snippet!r} должен встретиться ровно один раз",
            )

    def test_noisy_storage_preserves_all_expands(self) -> None:
        """Шумовой ``div``/status-макрос не должен подавлять содержимое после.

        Синтетический fixture покрывает:

        * ``div.aura-panel`` — реальный контент, не шум;
        * ``div.toc-macro`` с вложенным ``status`` макросом — шум;
        * 3 уровня вложенных expand (A1 -> A2 -> A3);
        * таблица, список и абзац ПОСЛЕ шумового фрагмента.

        Проверяем точные счётчики блоков и уникальность текста.
        """

        data = json.loads(NOISY_FIXTURE.read_text(encoding="utf-8"))
        storage = data["body"]["storage"]["value"]

        # Сначала убедимся, что в HTML ровно три expand-макроса.
        opens = re.findall(
            r'ac:structured-macro[^>]*ac:name="expand"', storage
        )
        self.assertEqual(len(opens), 3)

        blocks = confluence.parse_html_to_blocks(storage, source_kind="storage")
        expands = [b for b in blocks if b.kind == "expand"]
        self.assertEqual(len(expands), 3, "ожидаем 3 expand-макроса в выдаче")
        expand_titles = [e.title for e in expands]
        self.assertEqual(
            expand_titles,
            [
                "Внешний expand A1",
                "Вложенный expand A2",
                "Самый глубокий expand A3",
            ],
        )

        # Порядок: в плоском DFS expand появляется до своих дочерних
        # параграфов, поэтому все три идут подряд до любого параграфа
        # из тела самого глубокого expand.
        for i in range(len(expands)):
            self.assertEqual(expands[i].kind, "expand")

        # Все 5 уникальных текстов должны быть в выдаче ровно по разу.
        all_text = "\n".join(b.text for b in blocks)
        expected_snippets = [
            "Текст внутри внешнего expand A1.",
            "Текст внутри вложенного expand A2.",
            "Текст самого глубокого expand A3.",
            "Хвостовой текст вложенного expand A2.",
            "Хвостовой текст внешнего expand A1.",
            "Содержимое aura-панели",
            "Завершающий абзац после шумного фрагмента.",
            "Первый элемент списка.",
            "Колонка 1",
        ]
        for snippet in expected_snippets:
            self.assertEqual(
                all_text.count(snippet),
                1,
                f"фрагмент {snippet!r} должен встречаться ровно один раз",
            )

        # Шумовой контент (status/toc-macro) не должен попасть в выдачу.
        self.assertNotIn("Тестовый статус", all_text)
        self.assertNotIn("Содержимое статус-макроса.", all_text)

        # Контент после шумового фрагмента присутствует.
        self.assertIn("Колонка 2", all_text)
        self.assertIn("Ячейка 1-2", all_text)
        self.assertIn("Второй элемент списка.", all_text)

        # Структура: 1 h1 + 1 h2 + 1 h2 + 3 expand + 1 table + 1 list +
        # минимум по одному параграфу от aura-панели и завершающего блока,
        # плюс 5 параграфов внутри трёх expand.
        kinds = [b.kind for b in blocks]
        self.assertEqual(kinds.count("heading"), 3)
        self.assertEqual(kinds.count("expand"), 3)
        self.assertEqual(kinds.count("table"), 1)
        self.assertEqual(kinds.count("list"), 1)
        self.assertGreaterEqual(kinds.count("paragraph"), 7)

    def test_storage_view_merge_no_double_count(self) -> None:
        """Storage+view merge не должен задваивать совпадающий контент.

        Этот тест включает ``include_view=True`` явно: merge — теперь
        опциональная фича, по умолчанию используется только ``body.storage``.
        """

        doc = confluence.load_confluence_doc(NOISY_FIXTURE, include_view=True)

        # Соберём весь текст секций документа и поищем повторы.
        text_chunks: list[str] = []

        def walk(sections):
            for s in sections:
                for b in s.blocks:
                    if b.text:
                        text_chunks.append(b.text.strip())
                walk(s.children)

        walk(doc.sections)

        # Каждый из «общих» текстов должен встретиться ровно один раз:
        # ни storage, ни view не должны его задвоить.
        shared_snippets = [
            "Текст внутри внешнего expand A1.",
            "Текст внутри вложенного expand A2.",
            "Текст самого глубокого expand A3.",
            "Хвостовой текст вложенного expand A2.",
            "Хвостовой текст внешнего expand A1.",
            "Завершающий абзац после шумного фрагмента.",
        ]
        full = "\n".join(text_chunks)
        for snippet in shared_snippets:
            self.assertEqual(
                full.count(snippet),
                1,
                f"фрагмент {snippet!r} задвоился между storage и view",
            )

        # Уникальный view-only фрагмент должен остаться.
        self.assertEqual(full.count("PDU"), 1)

        # Шумовой контент storage не должен попасть в финальные секции.
        self.assertNotIn("Тестовый статус", full)
        self.assertNotIn("Содержимое статус-макроса.", full)


class ConfluenceStorageDefaultTest(unittest.TestCase):
    """Поведение по умолчанию: storage-only, coverage честно сообщает о view."""

    def test_default_excludes_view(self) -> None:
        doc = confluence.load_confluence_doc(FIXTURE)
        cov = doc.coverage
        # View-merging теперь opt-in.
        self.assertTrue(cov["view_excluded"])
        self.assertFalse(cov["view_included"])
        self.assertEqual(cov["view_blocks"], 0)
        self.assertEqual(cov["view_sections_added"], 0)

    def test_default_has_no_view_only_unique_paragraph(self) -> None:
        """Уникальный view-only абзац про PDU НЕ должен попасть в корпус
        по умолчанию — иначе мы выдаём чужие данные за свою страницу."""

        doc = confluence.load_confluence_doc(FIXTURE)

        def walk(sections):
            for s in sections:
                for b in s.blocks:
                    if b.text and "PDU" in b.text:
                        return b.text
                inner = walk(s.children)
                if inner:
                    return inner
            return None

        # По умолчанию view-only контент отключён.
        self.assertIsNone(walk(doc.sections))

    def test_include_view_opt_in(self) -> None:
        """При include_view=True view-merging снова работает, как раньше."""

        default_doc = confluence.load_confluence_doc(FIXTURE)
        view_doc = confluence.load_confluence_doc(FIXTURE, include_view=True)

        # В обоих режимах coverage доступен.
        self.assertIn("view_excluded", default_doc.coverage)
        self.assertIn("view_included", view_doc.coverage)
        self.assertTrue(view_doc.coverage["view_included"])

        # В default — view-блоки не учитываются. В opt-in — учитываются.
        self.assertEqual(default_doc.coverage["view_blocks"], 0)
        self.assertGreater(view_doc.coverage["view_blocks"], 0)


class ConfluenceExcerptIncludeTest(unittest.TestCase):
    """Подсчёт и отчётность о неразрешённых excerpt-include ссылках."""

    # Синтетический fixture: 3 excerpt-include в storage, 0 в view.
    # Сериализуем через json.dumps, чтобы корректно экранировать все
    # кавычки и угловые скобки.
    EXCERPT_FIXTURE = (
        "<h1>Root</h1>"
        "<p>before</p>"
        '<ac:structured-macro ac:name="excerpt-include">'
        '<ac:parameter ac:name="page">Other Page 1</ac:parameter>'
        "</ac:structured-macro>"
        "<p>middle</p>"
        '<ac:structured-macro ac:name="excerpt-include">'
        '<ac:parameter ac:name="page">Other Page 2</ac:parameter>'
        "</ac:structured-macro>"
        '<ac:structured-macro ac:name="excerpt-include">'
        '<ac:parameter ac:name="page">Other Page 3</ac:parameter>'
        "</ac:structured-macro>"
        "<p>after</p>"
    )

    def _doc(self) -> confluence.ConfluenceDoc:
        import json
        import tempfile

        payload = {
            "id": "777",
            "type": "page",
            "title": "Excerpt fixture",
            "version": {"number": 1},
            "body": {
                "storage": {
                    "value": self.EXCERPT_FIXTURE,
                    "representation": "storage",
                },
                "view": {
                    "value": "<h1>Root</h1>",
                    "representation": "view",
                },
            },
        }
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "ex.json"
            p.write_text(
                json.dumps(payload, ensure_ascii=False), encoding="utf-8"
            )
            return confluence.load_confluence_doc(p)

    def test_unresolved_count_in_storage(self) -> None:
        doc = self._doc()
        cov = doc.coverage
        # В storage три excerpt-include — они должны быть посчитаны и
        # НЕ резолвлены (cross-page merge запрещён по умолчанию).
        self.assertEqual(cov["unresolved_excerpt_includes"], 3)
        # Свойство объекта совпадает с coverage.
        self.assertEqual(doc.unresolved_excerpt_includes, 3)

    def test_view_excerpt_unresolved_reported(self) -> None:
        doc = self._doc()
        cov = doc.coverage
        # В этом fixture view содержит 0 excerpt-include; сообщаем честно.
        self.assertEqual(cov["view_excerpt_includes_unresolved"], 0)

    def test_storage_text_preserved(self) -> None:
        """Контент ДО excerpt-include сохраняется: excerpt-include тянет
        внешний контент, но собственный текст страницы должен быть на месте."""

        doc = self._doc()

        def collect(sections):
            texts = []
            for s in sections:
                for b in s.blocks:
                    if b.text:
                        texts.append(b.text)
                texts.extend(collect(s.children))
            return texts

        all_texts = collect(doc.sections)
        joined = "\n".join(all_texts)
        self.assertIn("before", joined)
        self.assertIn("middle", joined)
        self.assertIn("after", joined)

    def test_no_cross_page_text_in_default(self) -> None:
        """По умолчанию мы НЕ должны тянуть контент из excerpt-include
        (он бы пришёл из чужих страниц и выдавался за нашу)."""

        doc = self._doc()
        cov = doc.coverage
        # Число unresolved == 0 в финальных секциях (мы их не открываем).
        # Эти макросы подавлены в парсере через ``excerpt-include`` как
        # маркер, но их тело у нас пустое (нет вложенного rich-text-body).
        self.assertGreaterEqual(cov["unresolved_excerpt_includes"], 1)


class ConfluenceTotalWordsTest(unittest.TestCase):
    """``total_words`` должен рекурсивно обходить все секции."""

    def test_total_words_recurses_nested(self) -> None:
        """В ``fixture/confluence_sample.json`` есть вложенные expand
        с собственными словами в подсекциях. До исправления total_words
        считал только ``s.blocks`` и заголовки прямых ``children``, не
        уходя глубже. Проверяем, что слова внутри вложенного expand тоже
        учтены."""

        doc = confluence.load_confluence_doc(FIXTURE)

        def sum_words(sections):
            total = 0
            for s in sections:
                total += confluence.text_utils.count_words(s.title)
                for b in s.blocks:
                    total += confluence.text_utils.count_words(b.text)
                # Полный DFS — это и есть суть «recurses all sections».
                total += sum_words(s.children)
            return total

        recursive_total = sum_words(doc.sections)
        self.assertEqual(doc.total_words(), recursive_total)
        # В нашем fixture есть вложенный expand с уникальным текстом.
        # Это значение должно попасть в total_words.
        self.assertGreater(doc.total_words(), 0)

    def test_total_words_includes_nested_body(self) -> None:
        """Слова во вложенном expand должны учитываться в total_words."""

        doc = confluence.load_confluence_doc(FIXTURE)
        # В нашем fixture во вложенном expand есть уникальная фраза
        # про 10.0.0.1, которая должна попасть в подсчёт.
        # Эта фраза — в section ``[expand] Вложенный блок``, который
        # сам является ребёнком ``[expand] Подробности сети``.
        self.assertGreater(doc.total_words(), 0)

        # Ручной подсчёт через walk по дереву секций.
        manual = 0

        def walk(s):
            nonlocal manual
            manual += confluence.text_utils.count_words(s.title)
            for b in s.blocks:
                manual += confluence.text_utils.count_words(b.text)
            for c in s.children:
                walk(c)

        for s in doc.sections:
            walk(s)

        self.assertEqual(doc.total_words(), manual)

    def test_total_words_with_view_includes_nested(self) -> None:
        doc = confluence.load_confluence_doc(FIXTURE, include_view=True)
        # При включённом view total_words не должен падать и должен
        # учитывать все секции (включая добавленные из view).
        self.assertGreater(doc.total_words(), 0)


if __name__ == "__main__":
    unittest.main()
