<#
.SYNOPSIS
    Запросить top-k из конкретного индекса docindexing.

.DESCRIPTION
    Запускать из каталога DocIndexing.
    Использует .\.venv\Scripts\python.exe и PYTHONPATH=src.
    Параметр -OutputDir задаёт каталог, где лежат fixed.sqlite3 /
    structural.sqlite3 (по умолчанию 'index_out' относительно DocIndexing).
    Результат: JSON с найденными чанками в stdout.
    Требуется запущенный Ollama с моделью qwen3-embedding:0.6b.

    На старте принудительно включает UTF-8 для консоли
    ([Console]::OutputEncoding = [System.Text.Encoding]::UTF8) и
    устанавливает PYTHONIOENCODING=utf-8 / PYTHONUTF8=1, чтобы
    кириллица в JSON-ответе доходила до пользователя без
    UnicodeEncodeError (иначе PowerShell 5.1 держит cp1251 и
    падает на первом не-cp1251 символе).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Question,
    [ValidateSet('fixed', 'structural')] [string] $Strategy = 'structural',
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
    Write-Warning "[query_index] Could not set Console.OutputEncoding: $_"
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
    '-m', 'docindexing', 'query',
    '--question', $Question,
    '--strategy', $Strategy,
    '--top-k', "$TopK",
    '--output-dir', $OutputDir,
    '--ollama-url', $OllamaUrl,
    '--embed-model', $EmbedModel
)

Write-Host "[query_index] Working directory: $repoDir"
Write-Host "[query_index] Output dir: $OutputDir"
& $venvPy @argsList
