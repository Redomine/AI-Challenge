# DocIndexing

Локальный CLI для индексирования Confluence JSON и PDF-руководств в
SQLite-индексы с эмбеддингами Ollama. Используется как «внешняя»
поисковая база для BIM-помощника.

Все примеры ниже запускаются из корня репозитория; первая команда переходит
в `DocIndexing` и создаёт результаты только в указанных локальных путях.

> **Замечание для Windows.** В этом README ниже есть две формы команд:
> прямые вызовы `.\.venv\Scripts\python.exe -m docindexing …` (основные)
> и обёртки `.\scripts\*.ps1` (опциональные). Прямые команды работают
> всегда, пока есть локальный `.venv\Scripts\python.exe`. Обёртки
> `.ps1` могут быть заблокированы системной политикой
> PowerShell (`PSSecurityException` при `…\scripts\*.ps1`), если у
> пользователя не разрешено выполнение локальных скриптов. В этом
> README **не** предлагается менять `ExecutionPolicy` и **не**
> предлагается запускать скрипты с `-ExecutionPolicy Bypass` — вместо
> этого используйте прямые команды ниже.

## Структура

- `src/docindexing/` — пакет Python (3.10+). Точка входа — `python -m docindexing`.
- `scripts/` — обёртки PowerShell 5.1 для типовых сценариев
  (опциональные; см. замечание выше).
- `tests/` — `unittest`-тесты и фикстуры (`tests/fixtures/*.json`).

## Подготовка

Создаёт локальный `.venv` и ставит туда зависимости из `requirements.txt`.
Python 3.10+ должен быть в PATH (по умолчанию скрипт ищет `py -3.13`).

Прямой вариант (не зависит от политики выполнения PowerShell):

```powershell
Set-Location -LiteralPath '.\DocIndexing'
py -3.13 -m venv .\.venv
.\.venv\Scripts\python.exe -m pip install --upgrade pip
.\.venv\Scripts\python.exe -m pip install -r .\requirements.txt
```

Рабочий каталог: корень `DocIndexing`. Результат: `.venv\Scripts\python.exe`
готов к использованию.

Опциональная обёртка (если политика выполнения PowerShell разрешает
запуск локальных скриптов):

```powershell
Set-Location -LiteralPath '.\DocIndexing'
.\scripts\setup_venv.ps1
```

> `.\scripts\setup_venv.ps1` делает ровно то же, что и прямые команды
> выше: `py -3.13 -m venv .\.venv`, затем `pip install --upgrade pip`
> и `pip install -r requirements.txt`. Если при запуске обёртки вы
> получаете `…cannot be loaded because running scripts is disabled on
> this system…` — используйте прямые команды, политику менять не нужно.

## Тесты

Прогоняет все `unittest`-тесты в каталоге `tests\` через локальный venv.
Подробный отчёт выводится в stdout. Запускать из корня `DocIndexing`.

Прямой вариант:

```powershell
Set-Location -LiteralPath '.\DocIndexing'
$env:PYTHONPATH = '.\src'
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
```

Рабочий каталог: корень `DocIndexing`. Результат: подробный отчёт
`unittest` в stdout (`Ran N tests …` — итог).

Опциональная обёртка (если политика выполнения PowerShell разрешает
запуск локальных скриптов):

```powershell
Set-Location -LiteralPath '.\DocIndexing'
.\scripts\run_tests.ps1
```

Рабочий каталог: корень `DocIndexing`. Текущий набор (`test_chunking`,
`test_confluence`, `test_corpus`, `test_embeddings`, `test_end_to_end`,
`test_hashing`, `test_index_store`, `test_pdf_text`, `test_report`,
`test_text_utils`) — точное число может меняться при добавлении
новых тестов, поэтому за истиной — в строку `Ran N tests …` из
вывода `unittest`. Раньше в этом абзаце стояла жёстко прошитая
цифра; она устаревала после каждого нового теста, поэтому
теперь её нет.

## Где лежат индексы и входные данные

По умолчанию CLI ожидает:

- входную выгрузку Confluence — JSON-файл (по умолчанию путь задан в
  `config.DEFAULT_CONFLUENCE_JSON`, его можно переопределить через
  `-ConfluenceJson` / `--confluence-json`);
- входной PDF — путь по умолчанию задан в `config.DEFAULT_PDF_PATH`,
  переопределяется через `-PdfPath` / `--pdf`;
- каталог результатов — `index_out` относительно текущей рабочей
  папки (`DEFAULT_OUTPUT_DIR`). Внутри будут лежать
  `fixed.sqlite3`, `structural.sqlite3`, `corpus.json`,
  `build_report.json`. Всё, что лежит вне `index_out`, нужно явно
  указывать через `-OutputDir` / `--output-dir` — никаких скрытых
  обращений к произвольным путям из коробки нет.

> Дефолтные пути в `config.py` используют `input/confluence.json` и
> `input/document.pdf` относительно текущей папки. Если их нет, `build` упадёт с понятной ошибкой
> ещё до обращения к Ollama. Чтобы взять индексы из произвольного
> места, передавайте `-OutputDir` явно в `build_index.ps1`,
> `query_index.ps1`, `compare_index.ps1` или `--output-dir` в
> соответствующих командах `python -m docindexing …`.

## Сборка индексов

Собирает два SQLite-индекса: `fixed` и `structural`, плюс
`corpus.json` и `build_report.json`. Использует Ollama
(`qwen3-embedding:0.6b`) для эмбеддингов. По умолчанию пишет в
`index_out\` внутри текущей рабочей папки.

Прямой вариант (рекомендуется; работает даже при запрете запуска
`.ps1`-обёрток):

```powershell
Set-Location -LiteralPath '.\DocIndexing'
$env:PYTHONPATH = '.\src'
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUTF8 = '1'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
.\.venv\Scripts\python.exe -m docindexing build `
    --confluence-json 'C:\путь\к\confluence.json' `
    --output-dir 'C:\путь\к\результату'
```

Параметры CLI:

- `--confluence-json` — путь к JSON-дампу Confluence (опционально;
  CLI-имя для `-ConfluenceJson`).
- `--pdf` — путь к PDF-руководству (опционально; CLI-имя для `-PdfPath`).
- `--output-dir` — каталог для результатов; по умолчанию `index_out`
  (относительно текущей папки).
- `--ollama-url` — адрес Ollama (по умолчанию `http://127.0.0.1:11434`).
- `--embed-model` — имя модели (по умолчанию `qwen3-embedding:0.6b`).

Рабочий каталог: корень `DocIndexing`. Результат: файлы
`fixed.sqlite3`, `structural.sqlite3`, `corpus.json`, `build_report.json`
в `--output-dir`. Переменные `PYTHONIOENCODING=utf-8` и `PYTHONUTF8=1`
нужны, чтобы PowerShell 5.1 не ронял процесс на кириллице в логах и
JSON; при прямом вызове их должен выставить пользователь (обёртка
`build_index.ps1` делает это сама).

Опциональная обёртка (если политика выполнения PowerShell разрешает
запуск локальных скриптов):

```powershell
Set-Location -LiteralPath '.\DocIndexing'
.\scripts\build_index.ps1 `
    -ConfluenceJson 'C:\путь\к\confluence.json' `
    -OutputDir 'C:\путь\к\результату'
```

Параметры обёртки совпадают с CLI, только в стиле PowerShell:
`-ConfluenceJson`, `-PdfPath`, `-OutputDir`, `-OllamaUrl`,
`-EmbedModel`.

Рабочий каталог: корень `DocIndexing` (PowerShell-скрипт вычисляет его
сам по `$PSScriptRoot`). Результат: те же файлы в `-OutputDir`.

## Запрос к индексу

Top-k из конкретного индекса. Нужен запущенный Ollama и собранный индекс.

Прямой вариант (рекомендуется; работает даже при запрете запуска
`.ps1`-обёрток):

```powershell
Set-Location -LiteralPath '.\DocIndexing'
$env:PYTHONPATH = '.\src'
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUTF8 = '1'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
.\.venv\Scripts\python.exe -m docindexing query `
    --question 'Как сбросить пароль на PDU?' `
    --strategy 'structural' `
    --top-k 5 `
    --output-dir 'C:\путь\к\индексам'
```

Параметры CLI:

- `--question` — обязательный, поисковый запрос.
- `--strategy` — `fixed` или `structural` (по умолчанию `structural`).
- `--top-k` — количество результатов (по умолчанию `5`).
- `--output-dir` — каталог с `fixed.sqlite3`/`structural.sqlite3`
  (по умолчанию `index_out`).
- `--ollama-url`, `--embed-model` — адрес и модель Ollama.

Рабочий каталог: корень `DocIndexing`. Результат: JSON со списком
чанков (`chunk_id`, `title`, `section_path`, `text`, оценки близости)
в stdout. Переменные `PYTHONIOENCODING=utf-8` и `PYTHONUTF8=1` нужны,
чтобы PowerShell 5.1 не падал на кириллице в JSON-ответе.

Опциональная обёртка (если политика выполнения PowerShell разрешает
запуск локальных скриптов):

```powershell
Set-Location -LiteralPath '.\DocIndexing'
.\scripts\query_index.ps1 `
    -Question 'Как сбросить пароль на PDU?' `
    -Strategy 'structural' `
    -TopK 5 `
    -OutputDir 'C:\путь\к\индексам'
```

Параметры обёртки совпадают с CLI: `-Question`, `-Strategy`,
`-TopK`, `-OutputDir`, `-OllamaUrl`, `-EmbedModel`.

Рабочий каталог: корень `DocIndexing`. Результат: тот же JSON в stdout.

## Сравнение стратегий

Прогоняет одинаковые вопросы через две стратегии. В отчёте:

* реальные top-k из обеих стратегий с метаданными (`source`,
  `section`, `pdf_page`, `score`);
* сводка по SQLite-индексам (количество чанков, размер файла,
  размерность эмбеддингов, модель);
* если в файле вопросов есть ground-truth (см. ниже) — hit@1,
  hit@3, hit@5 по обеим стратегиям и позиция первого релевантного
  чанка для каждого вопроса;
* если `build_report.json` лежит рядом с индексами — к отчёту
  подтягиваются счётчики чанков из него.

Прямой вариант (рекомендуется; работает даже при запрете запуска
`.ps1`-обёрток):

```powershell
Set-Location -LiteralPath '.\DocIndexing'
$env:PYTHONPATH = '.\src'
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUTF8 = '1'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
.\.venv\Scripts\python.exe -m docindexing compare `
    --questions 'C:\путь\к\questions.json' `
    --output 'C:\путь\к\compare_report.json' `
    --top-k 5 `
    --output-dir 'C:\путь\к\индексам'
```

Параметры CLI:

- `--questions` — обязательный, путь к JSON. Поддерживаются две формы:
  - список строк — `["вопрос 1", "вопрос 2"]`;
  - список объектов с ground-truth:

    ```json
    {
      "questions": [
        {
          "question": "Как сбросить пароль на PDU?",
          "expected_source": "confluence",
          "expected_section_contains": "PDU"
        },
        {
          "question": "Требования к охлаждению стойки",
          "expected_source": "pdf",
          "expected_pdf_page": 2
        }
      ]
    }
    ```

  Допустимые поля: `expected_source` (`"confluence"`/`"pdf"`),
  `expected_section_contains` (подстрока `section_path`, сравнение
  без учёта регистра), `expected_pdf_page` (целое число). Любой из
  них может быть опущен — тогда соответствующий фильтр не
  применяется. Если в объекте нет ни одного `expected_*`, он
  обрабатывается как обычный строковый вопрос.
- `--output` — обязательный, путь к JSON-отчёту.
- `--top-k` — количество результатов на вопрос.
- `--output-dir` — каталог с индексами (по умолчанию `index_out`).
- `--ollama-url`, `--embed-model` — параметры Ollama.

Рабочий каталог: корень `DocIndexing`. Результат: файл `--output`
со сводкой и `per_question`-списком. Для строковых вопросов
`hit@k` и ранги не вычисляются (`quality_claims=false`) — мы не
выдумываем ground truth; в отчёте лежат только фактические top-k.
Переменные `PYTHONIOENCODING=utf-8` и `PYTHONUTF8=1` нужны, чтобы
PowerShell 5.1 не падал на кириллице в финальном
`"Comparison written: …"`.

Опциональная обёртка (если политика выполнения PowerShell разрешает
запуск локальных скриптов):

```powershell
Set-Location -LiteralPath '.\DocIndexing'
.\scripts\compare_index.ps1 `
    -Questions 'C:\путь\к\questions.json' `
    -Output 'C:\путь\к\compare_report.json' `
    -TopK 5 `
    -OutputDir 'C:\путь\к\индексам'
```

Параметры обёртки совпадают с CLI: `-Questions`, `-Output`,
`-TopK`, `-OutputDir`, `-OllamaUrl`, `-EmbedModel`.

Рабочий каталог: корень `DocIndexing`. Результат: тот же JSON-отчёт
по пути `-Output`.

## Ключевые детали реализации

- `confluence.parse_html_to_blocks` — парсит storage- и view-представления
  Confluence в плоский список блоков. Поддерживает заголовки h1–h6,
  expand-макросы (в том числе вложенные), таблицы, списки, code/pre.
  Подавление шумовых блоков (`toc-macro`, `breadcrumb`, `page-metadata`,
  `footer-content`, `navmenu`) идёт по точному совпадению CSS-токена и
  по стеку тегов, поэтому закрытие одного шумового макроса не «утекает»
  дальше по документу.
- `load_confluence_doc` — собирает дерево секций. По умолчанию
  (``include_view=False``) берётся только ``body.storage``: view
  резолвит cross-page ``excerpt-include``, которые мы не можем
  отрезолвить в текущем корпусе — такие ссылки считаются в
  ``coverage.unresolved_excerpt_includes`` и не выкидывают чанки,
  а лишь сигнализируют о потенциально потерянном контенте.
  Опциональное слияние со ``body.view`` (через явный
  ``include_view=True``) включает трёхуровневую дедупликацию:
  сигнатура секции, сигнатура содержимого, сигнатуры отдельных
  блоков. Совпадающие блоки/подсекции в view отбрасываются,
  чтобы финальный корпус не задвоился; секции view с
  ``excerpt-include`` (cross-page) всё равно режектятся на
  этапе парсинга view, даже если ``include_view=True``.
- `chunking.chunk_fixed` и `chunking.chunk_structural` — две стратегии
  чанкования с детерминированными SHA-256-идентификаторами, что
  позволяет идемпотентно перезапускать индексирование. В режиме
  `fixed` Confluence-юниты склеиваются в общий поток и режутся
  скользящим окном `target_words` с перекрытием `overlap_words`
  (разрешено пересекать границы секций); PDF-юниты чанкуются
  постранично, чтобы `pdf_page` оставался валидной ground-truth
  меткой. Метаданные окна (`section_path`) собирают все метки секций,
  через которые прошло окно, — детерминированно и без дубликатов.
- `report.run_comparison` — сравнивает стратегии по ground-truth. Раньше
  здесь считался Jaccard по `chunk_id` между стратегиями; это было
  бессмысленно, потому что `chunk_id` детерминирован от стратегии
  (`fixed-<sha>`/`structural-<sha>`) и пересечение всегда нулевое либо
  случайное. Сейчас метрика — hit@k по реальным метаданным чанков
  (`source`, `section`, `pdf_page`): в отчёте лежат `pdf_page`
  отдельных чанков, а не дословный текст PDF-страниц. Каких-то
  отдельных «многоточий» или «нераскрытых цитат» в
  `build_report.json` нет — это просто позиции чанков в исходных
  страницах, и при необходимости пользователь открывает PDF
  по этой странице сам.
