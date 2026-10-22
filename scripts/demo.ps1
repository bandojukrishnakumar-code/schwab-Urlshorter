param(
    [switch]$Approve,
    [string]$BaseUrl = "http://localhost:3000"
)

$ErrorActionPreference = "Stop"
$base = $BaseUrl.TrimEnd("/")
$scenarios = @("greenfield", "brownfield", "ambiguous")

foreach ($scenario in $scenarios) {
    $run = Invoke-RestMethod -Method Post -Uri "$base/api/runs" `
        -ContentType "application/json" -Body (@{ scenarioId = $scenario } | ConvertTo-Json)
    Write-Host "`n$($run.scenarioTitle): $($run.status)"
    Write-Host "Tests=$($run.nodes.tests.status); docs=$($run.nodes.documentation.status); retries=$($run.metrics.retries)"
    $decision = @{
        approved = [bool]$Approve
        actor = "demo-operator"
        reason = if ($Approve) { "Explicit demo approval." } else { "Demo safe-stop." }
    } | ConvertTo-Json
    $run = Invoke-RestMethod -Method Post -Uri "$base/api/runs/$($run.id)/approval" `
        -ContentType "application/json" -Body $decision
    Write-Host "Explicit decision resulted in: $($run.status)"
}
