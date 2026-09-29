[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $SourcePath
)

$ErrorActionPreference = 'Stop'
$preview = [bool] $WhatIfPreference
$WhatIfPreference = $false

if (-not $SourcePath) {
    $developmentRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path
    $SourcePath = Join-Path $developmentRoot 'rvt-mcp-custom\src\plugin-r22\bin\Release\net48\RvtMcp.Plugin.dll'
}

$source = (Resolve-Path -LiteralPath $SourcePath -ErrorAction Stop).Path
$target = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2022\RvtMcp\RvtMcp.Plugin.dll'
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
    throw "Installed Revit 2022 plugin not found: $target"
}
if ((Get-Item -LiteralPath $source).Length -eq 0) {
    throw "Source DLL is empty: $source"
}

$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
if ($sourceHash -eq $targetHash) {
    Write-Output "Already installed: $target ($targetHash)"
    return
}

$runningR22 = Get-CimInstance Win32_Process -Filter "name='Revit.exe'" |
    Where-Object { $_.ExecutablePath -match '\\Revit 2022\\Revit\.exe$' -or
        $_.CommandLine -match '\\Revit 2022\\Revit\.exe' }
if ($runningR22 -and -not $preview) {
    throw "Revit 2022 is running (PID: $($runningR22.ProcessId -join ', ')). Close it before replacing the DLL."
}

$WhatIfPreference = $preview
if (-not $PSCmdlet.ShouldProcess($target, "Back up current DLL and install $source")) {
    return
}
$WhatIfPreference = $false

$backupRoot = Join-Path $env:LOCALAPPDATA 'RvtMcp\backups'
$backupName = 'plugin-r22-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$backupDir = Join-Path $backupRoot $backupName
[IO.Directory]::CreateDirectory($backupDir) | Out-Null
$backup = Join-Path $backupDir 'RvtMcp.Plugin.dll'
Copy-Item -LiteralPath $target -Destination $backup -ErrorAction Stop
if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $targetHash) {
    throw "Backup verification failed: $backup"
}

try {
    Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $sourceHash) {
        throw "Installed DLL hash differs from source: $target"
    }
} catch {
    Copy-Item -LiteralPath $backup -Destination $target -Force -ErrorAction Stop
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $targetHash) {
        throw "Installation failed and rollback hash differs; backup is at $backup"
    }
    throw
}

Write-Output "Installed: $target"
Write-Output "SHA256: $sourceHash"
Write-Output "Backup: $backup"
