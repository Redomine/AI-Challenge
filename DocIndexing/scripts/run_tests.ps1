<#
.SYNOPSIS
    Прогнать unittest-тесты docindexing в локальном venv.

.DESCRIPTION
    Запускать из каталога DocIndexing. Использует .\.venv\Scripts\python.exe.
    PYTHONPATH=src, чтобы пакет docindexing был доступен без установки.
    Результат: подробный отчёт unittest в stdout.

    На старте принудительно включает UTF-8 для консоли
    ([Console]::OutputEncoding = [System.Text.Encoding]::UTF8) и
    устанавливает PYTHONIOENCODING=utf-8 / PYTHONUTF8=1, чтобы имена
    тестов и ассерты с кириллицей доходили до пользователя без
    UnicodeEncodeError на PowerShell 5.1 (cp1251).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# UTF-8 для консоли PowerShell + переменные окружения для дочернего python.exe.
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
} catch {
    Write-Warning "[run_tests] Could not set Console.OutputEncoding: $_"
}
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUTF8 = '1'

$repoDir = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')
$venvPy = Join-Path $repoDir '.venv\Scripts\python.exe'

if (-not (Test-Path -LiteralPath $venvPy)) {
    throw "venv not found at $venvPy. Run scripts\setup_venv.ps1 first."
}

Write-Host "[run_tests] Working directory: $repoDir"
$env:PYTHONPATH = (Join-Path $repoDir 'src')

& $venvPy -m unittest discover -s (Join-Path $repoDir 'tests') -v
