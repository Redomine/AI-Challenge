# Помощник проектировщика, день 10

Консольный агент на .NET 8 и GigaChat с тремя стратегиями управления контекстом
без summary. История и состояние стратегий сохраняются в SQLite.

## Стратегии

### Sliding Window

В модель передаются только последние N сообщений и новый вопрос. Полная история
остаётся в SQLite, но ранние детали исчезают из контекста модели.

### Sticky Facts

После каждого сообщения пользователя отдельный вызов модели обновляет JSON facts:
цели, ограничения, значения, предпочтения и решения. В основной запрос передаются
facts, последние N сообщений и новый вопрос.

### Branching

Checkpoint фиксирует общую точку истории. Новые ветки создаются из этого snapshot
и затем сохраняют независимые последовательности сообщений. Переключение ветки
восстанавливается после перезапуска.

## Запуск

Из папки `Week 2_Day 5\DesignerAssistant`:

```powershell
$env:GIGACHAT_AUTH_KEY = "ваш-ключ-авторизации"
$env:GIGACHAT_SCOPE = "GIGACHAT_API_PERS"
$env:GIGACHAT_MODEL = "GigaChat-2"
dotnet run --project '.\DesignerAssistant.csproj'
```

Дополнительные настройки:

```powershell
$env:DESIGN_ASSISTANT_RECENT_MESSAGES = "10"
$env:DESIGN_ASSISTANT_DB_PATH = ".\designer-assistant.db"
$env:DESIGN_ASSISTANT_MAX_OUTPUT_TOKENS = "1200"
$env:GIGACHAT_CONTEXT_LIMIT_TOKENS = "128000"
$env:GIGACHAT_PRICE_PER_MILLION_TOKENS = "65"
```

## Команды

```text
/strategy sliding|facts|branching  выбрать стратегию
/strategy status                   показать состояние
/checkpoint                        сохранить точку ветвления
/branch create <имя>               создать ветку от checkpoint
/branch switch <имя>               переключить ветку
/branch list                       показать ветки
/compare                           автоматически сравнить три стратегии
/clear                             очистить историю, facts и ветки
/exit                              завершить приложение
```

Пример ручной работы с ветками:

```text
/strategy branching
/checkpoint
/branch create low-ceiling
Прими максимальную высоту воздуховода 300 мм.
/branch create standard
Прими максимальную высоту воздуховода 400 мм.
/branch switch low-ceiling
Какое ограничение действует в этой ветке?
```

## Автоматическое сравнение

Команда `/compare` прогоняет 15 одинаковых связанных вопросов на Sliding Window,
Sticky Facts и Branching. Для Branching она создаёт две ветки от общего checkpoint
и отдельно проверяет их независимость. В конце выводятся:

- суммарный переданный контекст;
- максимальный контекст одного запроса;
- тарифицируемые токены и примерная стоимость;
- количество facts;
- ответы на контрольный вопрос №15 для сравнения качества и стабильности.

Автотест использует хранилища в памяти и не изменяет основную SQLite-базу. Он
делает не менее 62 генераций: Sticky Facts требует отдельного обновления памяти
после каждого пользовательского сообщения. Запуск может занять несколько минут
и расходует токены API.

Сценарий также доступен в `QUESTIONS_DAY10.md`.
