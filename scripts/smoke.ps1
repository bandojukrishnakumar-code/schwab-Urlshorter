param([int]$Port = 3137)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectDirectory = Join-Path $root "src\UrlShortener"
$application = Join-Path $projectDirectory "bin\Debug\net10.0\UrlShortener.exe"
$temporaryData = Join-Path ([IO.Path]::GetTempPath()) ("url-shortener-smoke-" + [guid]::NewGuid())
$base = "http://127.0.0.1:$Port"
$env:ASPNETCORE_URLS = $base
$env:BASE_URL = $base
$env:DATA_DIRECTORY = $temporaryData
$server = $null

try {
    if (-not (Test-Path -LiteralPath $application)) {
        throw "Build the solution before running the smoke script: dotnet build .\UrlShortener.sln"
    }
    $server = Start-Process -FilePath $application -WorkingDirectory $projectDirectory `
        -PassThru -WindowStyle Hidden
    $healthy = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $health = Invoke-RestMethod "$base/health" -TimeoutSec 1
            if ($health.status -eq "ok") { $healthy = $true; break }
        } catch {
            Start-Sleep -Milliseconds 150
        }
    }
    if (-not $healthy) { throw "API did not become healthy." }

    $link = Invoke-RestMethod -Method Post -Uri "$base/api/urls" -ContentType "application/json" `
        -Body '{"url":"https://example.com/article","alias":"smoke1"}'
    if ($link.code -ne "smoke1") { throw "Unexpected short code." }

    $redirect = Invoke-WebRequest -Uri "$base/smoke1" -MaximumRedirection 0 `
        -SkipHttpErrorCheck -ErrorAction SilentlyContinue
    if ([int]$redirect.StatusCode -ne 302 -or $redirect.Headers.Location -ne "https://example.com/article") {
        throw "Short URL did not redirect to the original destination."
    }
    $stats = Invoke-RestMethod "$base/api/urls/smoke1"
    if ($stats.clicks -ne 1) { throw "Expected one tracked click." }

    $run = Invoke-RestMethod -Method Post -Uri "$base/api/runs" -ContentType "application/json" `
        -Body '{"scenarioId":"greenfield"}'
    if ($run.status -ne "waiting_approval") { throw "Release was not blocked for review." }
    $stopped = Invoke-RestMethod -Method Post -Uri "$base/api/runs/$($run.id)/approval" `
        -ContentType "application/json" `
        -Body '{"approved":false,"actor":"smoke-test","reason":"Validate safe stop."}'
    if ($stopped.status -ne "safe_stopped") { throw "Rejected approval did not safe-stop." }
    Write-Host "API smoke test passed: create, redirect, analytics, human approval, and safe-stop."
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id
        $server.WaitForExit()
    }
    Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue
    Remove-Item Env:BASE_URL -ErrorAction SilentlyContinue
    Remove-Item Env:DATA_DIRECTORY -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $temporaryData) {
        Remove-Item -LiteralPath $temporaryData -Recurse -Force
    }
}
