param(
    [Parameter(Mandatory = $true)]
    [string] $PromptPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [string] $ContextPaths = '',

    [int] $MaxTokens = 6000
)

$ErrorActionPreference = 'Stop'
$key = $env:GIGACHAT_AUTH_KEY
if ([string]::IsNullOrWhiteSpace($key)) {
    throw 'GIGACHAT_AUTH_KEY is not set.'
}
if ($key.StartsWith('Basic ', [StringComparison]::OrdinalIgnoreCase)) {
    $key = $key.Substring(6).Trim()
}

$guidancePath = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'GIGACHAT_ULTRA_DELEGATION.md'
$guidance = Get-Content -LiteralPath $guidancePath -Encoding UTF8 -Raw
$prompt = $guidance + "`n`n--- TASK ---`n" + (Get-Content -LiteralPath $PromptPath -Encoding UTF8 -Raw)
foreach ($contextPath in ($ContextPaths -split ';' | Where-Object { $_ })) {
    $context = Get-Content -LiteralPath $contextPath -Encoding UTF8 -Raw
    $prompt += "`n`n--- FILE: $contextPath ---`n$context"
}
$token = Invoke-RestMethod -Uri 'https://ngw.devices.sberbank.ru:9443/api/v2/oauth' `
    -Method Post `
    -Headers @{ Authorization = "Basic $key"; RqUID = [guid]::NewGuid().ToString(); Accept = 'application/json' } `
    -Body @{ scope = 'GIGACHAT_API_PERS' } `
    -TimeoutSec 60

$body = @{
    model = 'GigaChat-3-Ultra'
    messages = @(
        @{ role = 'system'; content = 'You are a senior C# and Revit API coding agent. Follow the supplied repository instructions. Return only the requested artifact. Never invent test results.' }
        @{ role = 'user'; content = $prompt }
    )
    max_tokens = $MaxTokens
} | ConvertTo-Json -Depth 12

$response = Invoke-RestMethod -Uri 'https://api.giga.chat/v1/chat/completions' `
    -Method Post `
    -Headers @{ Authorization = "Bearer $($token.access_token)" } `
    -ContentType 'application/json; charset=utf-8' `
    -Body ([Text.Encoding]::UTF8.GetBytes($body)) `
    -TimeoutSec 180

$content = $response.choices[0].message.content
if ([string]::IsNullOrWhiteSpace($content)) {
    throw 'GigaChat Ultra returned no text.'
}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $content, (New-Object Text.UTF8Encoding($false)))
Write-Output "Model: $($response.model)"
Write-Output "Tokens: $($response.usage.total_tokens)"
Write-Output "Output: $OutputPath"
