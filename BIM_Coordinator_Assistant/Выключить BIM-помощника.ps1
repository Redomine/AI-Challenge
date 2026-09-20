$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$webExecutable = Join-Path $projectRoot 'DesignerAssistant.Web\bin\Debug\net8.0\DesignerAssistant.Web.exe'
$webProject = Join-Path $projectRoot 'DesignerAssistant.Web\DesignerAssistant.Web.csproj'

$processes = Get-CimInstance Win32_Process | Where-Object {
    ($_.ExecutablePath -and $_.ExecutablePath.Equals($webExecutable, [StringComparison]::OrdinalIgnoreCase)) -or
    ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -and
        $_.CommandLine.IndexOf($webProject, [StringComparison]::OrdinalIgnoreCase) -ge 0) -or
    ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -and
        $_.CommandLine.IndexOf('DesignerAssistant.Web\DesignerAssistant.Web.csproj', [StringComparison]::OrdinalIgnoreCase) -ge 0)
}

if (-not $processes) {
    Write-Host 'BIM Assistant is already stopped.'
    Start-Sleep -Seconds 2
    exit 0
}

$processes |
    Sort-Object { if ($_.Name -eq 'dotnet.exe') { 1 } else { 0 } } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }

Write-Host 'BIM Assistant stopped.'
Start-Sleep -Seconds 2
