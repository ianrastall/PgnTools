$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Discover every per-tool dump script (and code-dump-all) automatically.
$scripts = Get-ChildItem -Path $ScriptDir -Filter "code-dump-*.ps1" | Sort-Object Name
if ($scripts.Count -eq 0) {
    Write-Host "No code-dump-*.ps1 scripts found in $ScriptDir" -ForegroundColor Yellow
    return
}

Write-Host ""
Write-Host "  Per-tool context dumps" -ForegroundColor Cyan
Write-Host "  ----------------------"
for ($i = 0; $i -lt $scripts.Count; $i++) {
    $name = $scripts[$i].BaseName -replace '^code-dump-', ''
    "{0,3}. {1}" -f ($i + 1), $name | Write-Host
}
Write-Host ""

$choice = Read-Host "Enter a number to dump that tool (blank to cancel)"
if ([string]::IsNullOrWhiteSpace($choice)) {
    Write-Host "Cancelled."
    return
}

$idx = 0
if (-not [int]::TryParse($choice, [ref]$idx) -or $idx -lt 1 -or $idx -gt $scripts.Count) {
    Write-Host "Invalid choice: '$choice'" -ForegroundColor Yellow
    return
}

$chosen = $scripts[$idx - 1]
Write-Host ""
Write-Host "Running $($chosen.Name)..." -ForegroundColor Green
& $chosen.FullName
