<#
.SYNOPSIS
    Сравнить две стратегии индексирования на одном наборе вопросов.

.DESCRIPTION
    Запускать из каталога DocIndexing.
    Использует .\.venv\Scripts\python.exe и PYTHONPATH=src.
    Параметр -OutputDir задаёт каталог, где лежат fixed.sqlite3 /
    structural.sqlite3 (по умолчанию 'index_out' относительно DocIndexing).
    Вход: --questions <path-to-json>.
    Формат вопросов: список строк, либо список объектов
    {question, expected_source?, expected_section_contains?, expected_pdf_page?}.
    Если заданы expected_* — отчёт считает hit@k по обеим стратегиям и
    ранги первого релевантного чанка. Без ground-truth в отчёте только
    списки top-k, без оценок качества.
    Результат: JSON-отчёт по пути --output.

    На старте принудительно включает UTF-8 для консоли
    ([Console]::OutputEncoding = [System.Text.Encoding]::UTF8) и
    устанавливает PYTHONIOENCODING=utf-8 / PYTHONUTF8=1 — иначе
    PowerShell 5.1 сидит на cp1251 и кириллица в финальном
    "Comparison written: ..." роняет процесс с UnicodeEncodeError.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Questions,
    [Parameter(Mandatory = $true)] [string] $Output,
    [int] $TopK = 5,
    [string] $OutputDir = 'index_out',
    [string] $OllamaUrl = 'http://127.0.0.1:11434',
    [string] $EmbedModel = 'qwen3-embedding:0.6b'
)

$ErrorActionPreference = 'Stop'

# UTF-8 для консоли PowerShell + переменные окружения для дочернего python.exe.
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
} catch {
    Write-Warning "[compare_index] Could not set Console.OutputEncoding: $_"
}
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUTF8 = '1'

$repoDir = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')
$venvPy = Join-Path $repoDir '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $venvPy)) {
    throw "venv not found at $venvPy. Run scripts\setup_venv.ps1 first."
}

$env:PYTHONPATH = (Join-Path $repoDir 'src')

$argsList = @(
    '-m', 'docindexing', 'compare',
    '--questions', $Questions,
    '--output', $Output,
    '--top-k', "$TopK",
    '--output-dir', $OutputDir,
    '--ollama-url', $OllamaUrl,
    '--embed-model', $EmbedModel
)

Write-Host "[compare_index] Working directory: $repoDir"
Write-Host "[compare_index] Output dir: $OutputDir"
& $venvPy @argsList
