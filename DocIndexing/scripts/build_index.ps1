<#
.SYNOPSIS
    Собрать два SQLite-индекса docindexing.

.DESCRIPTION
    Запускать из каталога DocIndexing.
    Использует .\.venv\Scripts\python.exe и PYTHONPATH=src.
    Результат: файлы index_out\fixed.sqlite3, index_out\structural.sqlite3,
    index_out\corpus.json, index_out\build_report.json.
    Требуется запущенный Ollama с моделью qwen3-embedding:0.6b.

    На старте принудительно включает UTF-8 для консоли
    ([Console]::OutputEncoding = [System.Text.Encoding]::UTF8) и
    устанавливает переменные окружения PYTHONIOENCODING / PYTHONUTF8=1,
    чтобы дочерний python.exe унаследовал UTF-8 и для stdout/stderr,
    и для открытия файлов. Без этого PowerShell 5.1 держит cp1251
    в консоли, и любая кириллица в логах/JSON роняет процесс с
    UnicodeEncodeError уже после сборки файлов.
#>
[CmdletBinding()]
param(
    [string] $ConfluenceJson = $null,
    [string] $ConfluenceHtml = $null,
    [string] $PdfPath = $null,
    [string] $OutputDir = $null,
    [string] $OllamaUrl = 'http://127.0.0.1:11434',
    [string] $EmbedModel = 'qwen3-embedding:0.6b'
)

$ErrorActionPreference = 'Stop'

# UTF-8 для консоли PowerShell + переменные окружения для дочернего python.exe.
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
} catch {
    Write-Warning "[build_index] Could not set Console.OutputEncoding: $_"
}
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUTF8 = '1'

$repoDir = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')
$venvPy = Join-Path $repoDir '.venv\Scripts\python.exe'

if (-not (Test-Path -LiteralPath $venvPy)) {
    throw "venv not found at $venvPy. Run scripts\setup_venv.ps1 first."
}

$env:PYTHONPATH = (Join-Path $repoDir 'src')

$argsList = @('-m', 'docindexing', 'build')
if ($ConfluenceJson) { $argsList += @('--confluence-json', $ConfluenceJson) }
if ($ConfluenceHtml) { $argsList += @('--confluence-html', $ConfluenceHtml) }
if ($PdfPath) { $argsList += @('--pdf', $PdfPath) }
if ($OutputDir) { $argsList += @('--output-dir', $OutputDir) }
$argsList += @('--ollama-url', $OllamaUrl)
$argsList += @('--embed-model', $EmbedModel)

Write-Host "[build_index] Working directory: $repoDir"
$effectiveOutputDir = if ($OutputDir) { $OutputDir } else { 'index_out' }
Write-Host "[build_index] Output dir: $effectiveOutputDir"
& $venvPy @argsList
