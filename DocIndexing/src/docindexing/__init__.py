"""Локальный CLI для индексации документации Confluence + PDF.

Корневой пакет ``docindexing`` предоставляет модули:
    config        — настройки CLI и пути.
    hashing       — устойчивые хеши файлов.
    text_utils    — нормализация текста, подсчёт слов.
    confluence    — разбор Confluence HTML storage / view.
    pdf_text      — извлечение текста PDF с номерами страниц.
    corpus        — сборка единого корпуса из источников.
    chunking      — две стратегии чанкования (fixed и structural).
    embeddings    — клиент Ollama /api/embed.
    index_store   — изолированные SQLite-хранилища + поиск по косинусу.
    query         — CLI поиска top-k.
    report        — сравнительный отчёт двух стратегий.
"""

__all__ = [
    "config",
    "hashing",
    "text_utils",
    "confluence",
    "pdf_text",
    "corpus",
    "chunking",
    "embeddings",
    "index_store",
    "query",
    "report",
]
