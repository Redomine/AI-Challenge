param(
    [Parameter(Mandatory = $true)] [string] $PromptPath,
    [Parameter(Mandatory = $true)] [string] $OutputPath,
    [string] $WorkDir = (Get-Location).Path,
    [string] $ContextPaths = '',
    [string] $Title = 'MiniMax task',
    [string] $Model = 'opencode-go/minimax-m3',
    [switch] $AutoApprove
)

$ErrorActionPreference = 'Stop'
$cli = Join-Path $env:LOCALAPPDATA 'Programs\@opencodedesktop\resources\opencode-cli.exe'
$guidancePath = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'OPENCODE_MINIMAX_DELEGATION.md'
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw "OpenCode CLI not found: $cli" }
if (-not (Test-Path -LiteralPath $PromptPath -PathType Leaf)) { throw "Prompt not found: $PromptPath" }
if (-not (Test-Path -LiteralPath $guidancePath -PathType Leaf)) { throw "Guidance not found: $guidancePath" }
if (-not (Test-Path -LiteralPath $WorkDir -PathType Container)) { throw "Work directory not found: $WorkDir" }

$prompt = (Get-Content -LiteralPath $guidancePath -Encoding UTF8 -Raw) +
    "`n`n--- TASK ---`n" + (Get-Content -LiteralPath $PromptPath -Encoding UTF8 -Raw)
if ([Text.Encoding]::UTF8.GetByteCount($prompt) -gt 16000) {
    throw 'Prompt and guidance exceed the 16 KB command-line limit. Attach context files instead.'
}

$arguments = @('run', '--standalone', '--model', $Model, '--format', 'json', '--title', $Title)
if ($AutoApprove) { $arguments += '--auto' }
foreach ($path in ($ContextPaths -split ';' | Where-Object { $_ })) {
    $resolved = (Resolve-Path -LiteralPath $path -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Context is not a file: $resolved" }
    if ([IO.Path]::GetExtension($resolved) -match '^\.(rvt|rfa|rte|log|jsonl)$') {
        throw "This context file type is not allowed: $resolved"
    }
    $arguments += @('--file', $resolved)
}
$arguments += $prompt

Push-Location -LiteralPath $WorkDir
try {
    $lines = & $cli @arguments 2>&1
    $exitCode = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($exitCode -ne 0) { throw "OpenCode failed with exit code $exitCode. $($lines | Select-Object -Last 1)" }

$parts = foreach ($line in $lines) {
    try { $event = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
    if ($event.type -eq 'text' -and $event.part.type -eq 'text') { $event.part.text }
}
$reply = ($parts -join "`n").Trim()
if (-not $reply) { throw 'OpenCode returned no assistant text.' }
$target = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
[IO.File]::WriteAllText($target, $reply, (New-Object Text.UTF8Encoding($false)))
Write-Output "Model: $Model"
Write-Output "Output: $target"
