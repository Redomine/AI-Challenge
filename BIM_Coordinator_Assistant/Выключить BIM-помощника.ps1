$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$webExecutable = Join-Path $projectRoot 'DesignerAssistant.Web\bin\Debug\net8.0\DesignerAssistant.Web.exe'
$webProject = Join-Path $projectRoot 'DesignerAssistant.Web\DesignerAssistant.Web.csproj'
$webProjectRelative = 'DesignerAssistant.Web\DesignerAssistant.Web.csproj'
$webProcessName = 'DesignerAssistant.Web.exe'
$webPort = 5199

function Test-AssistantProcess($process) {
    if ($process.ExecutablePath -and
        $process.ExecutablePath.Equals($webExecutable, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    if ($process.Name -eq $webProcessName -and $process.CommandLine -and
        $process.CommandLine.IndexOf($projectRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $true
    }

    return $process.CommandLine -and
        ($process.CommandLine.IndexOf($webProject, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
         $process.CommandLine.IndexOf($webProjectRelative, [StringComparison]::OrdinalIgnoreCase) -ge 0)
}

function Get-AssistantProcesses {
    $processes = @(Get-CimInstance Win32_Process | Where-Object { Test-AssistantProcess $_ })

    $portOwners = @(Get-NetTCPConnection -LocalPort $webPort -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)

    foreach ($processId in $portOwners) {
        $portProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$processId" -ErrorAction SilentlyContinue
        if ($portProcess -and (Test-AssistantProcess $portProcess)) {
            $processes += $portProcess
        }
    }

    return @($processes | Sort-Object ProcessId -Unique)
}

$processes = @(Get-AssistantProcesses)

if (-not $processes) {
    Write-Host 'BIM Assistant is already stopped.'
    Start-Sleep -Seconds 2
    exit 0
}

$processIds = @($processes | Select-Object -ExpandProperty ProcessId)
Write-Host ("Stopping BIM Assistant process(es): {0}" -f ($processIds -join ', '))

$processes |
    Sort-Object { if ($_.Name -eq $webProcessName) { 0 } else { 1 } } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }

$deadline = (Get-Date).AddSeconds(5)
do {
    Start-Sleep -Milliseconds 200
    $remaining = @(Get-AssistantProcesses)
} while ($remaining.Count -gt 0 -and (Get-Date) -lt $deadline)

if ($remaining.Count -gt 0) {
    $remaining | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    $remaining = @(Get-AssistantProcesses)
}

if ($remaining.Count -gt 0) {
    $remainingIds = @($remaining | Select-Object -ExpandProperty ProcessId)
    Write-Error ("Failed to stop BIM Assistant process(es): {0}" -f ($remainingIds -join ', '))
    exit 1
}

Write-Host 'BIM Assistant stopped.'
Start-Sleep -Seconds 2
