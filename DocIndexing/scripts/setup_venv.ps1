<#
.SYNOPSIS
    Создать локальный venv для docindexing и установить зависимости.

.DESCRIPTION
    Запускать из каталога DocIndexing. Создаёт .\.venv\, ставит туда
    pypdf из requirements.txt и инструменты разработки. Не трогает
    глобальный Python.
#>
[CmdletBinding()]
param(
    [string] $VenvDir = (Join-Path $PSScriptRoot '..\.venv')
)

$ErrorActionPreference = 'Stop'

$repoDir = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')
Write-Host "[setup_venv] Working directory: $repoDir"
Write-Host "[setup_venv] Creating venv at: $VenvDir"

py -3.13 -m venv $VenvDir

$py = Join-Path $VenvDir 'Scripts\python.exe'
& $py -m pip install --upgrade pip
& $py -m pip install -r (Join-Path $repoDir 'requirements.txt')

Write-Host "[setup_venv] Done. Активация: $VenvDir\Scripts\Activate.ps1"
