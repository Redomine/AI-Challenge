"""Локальный CLI для индексации документации Confluence + PDF.

Корневой пакет ``docindexing`` предоставляет модули:
    config             — настройки CLI и пути.
    hashing            — устойчивые хеши файлов.
    text_utils         — нормализация текста, подсчёт слов.
    confluence         — разбор Confluence JSON (REST API выгрузка).
    confluence_html    — разбор Confluence HTML-выгрузки (view) с
                         извлечением табличных связей «проект ↔ сервер».
    pdf_text           — извлечение текста PDF с номерами страниц.
    pdf_server_table   — дедупликация таблиц «проект ↔ сервер» в PDF
                         против HTML-выгрузки (см. Day 24 dedup).
    corpus             — сборка единого корпуса из источников.
    chunking           — две стратегии чанкования (fixed и structural).
    embeddings         — клиент Ollama /api/embed.
    index_store        — изолированные SQLite-хранилища + поиск по косинусу.
    query              — CLI поиска top-k.
    report             — сравнительный отчёт двух стратегий.
"""

__all__ = [
    "config",
    "hashing",
    "text_utils",
    "confluence",
    "confluence_html",
    "pdf_text",
    "pdf_server_table",
    "corpus",
    "chunking",
    "embeddings",
    "index_store",
    "query",
    "report",
]
