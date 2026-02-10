[CmdletBinding()]
param(
    [string]$InputFolder = "..",
    [string]$Pattern = "chesscom-*.pgn",
    [string]$OutputPath = "..\\chesscom-monthly-combined.pgn",
    [ValidateSet("Month", "Name")]
    [string]$Sort = "Month",
    [switch]$Recurse
)

$ErrorActionPreference = "Stop"

$ScriptLocation = Split-Path -Parent $MyInvocation.MyCommand.Path
$ScriptLocation = [System.IO.Path]::GetFullPath($ScriptLocation)

function Resolve-ScriptPath {
    param([string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }
    return [System.IO.Path]::GetFullPath((Join-Path $ScriptLocation $Path))
}

function TryParseMonthKey {
    param(
        [string]$Name,
        [ref]$Key
    )

    if ($Name -match "(?<year>\d{4})-(?<month>\d{2})") {
        $year = [int]$Matches["year"]
        $month = [int]$Matches["month"]
        if ($month -ge 1 -and $month -le 12) {
            $Key.Value = [datetime]::new($year, $month, 1)
            return $true
        }
    }

    return $false
}

$InputRoot = Resolve-ScriptPath $InputFolder
$OutputFullPath = Resolve-ScriptPath $OutputPath

Write-Host "Input folder: $InputRoot" -ForegroundColor Cyan
Write-Host "Pattern: $Pattern" -ForegroundColor Cyan
Write-Host "Output file: $OutputFullPath" -ForegroundColor Cyan

if (-not (Test-Path $InputRoot)) {
    throw "Input folder not found: $InputRoot"
}

if ($Recurse) {
    $files = Get-ChildItem -Path $InputRoot -Filter $Pattern -File -Recurse
}
else {
    $files = Get-ChildItem -Path $InputRoot -Filter $Pattern -File
}

$files = $files | Where-Object { $_.FullName -ne $OutputFullPath }

if (-not $files -or $files.Count -eq 0) {
    throw "No input files found in '$InputRoot' matching '$Pattern'."
}

$items = foreach ($file in $files) {
    $monthKey = $null
    $hasMonth = TryParseMonthKey -Name $file.Name -Key ([ref]$monthKey)
    [PSCustomObject]@{
        File = $file
        HasMonth = $hasMonth
        MonthKey = $monthKey
        Name = $file.Name
    }
}

if ($Sort -eq "Month") {
    $ordered = $items | Sort-Object `
        @{ Expression = { -not $_.HasMonth } }, `
        @{ Expression = { $_.MonthKey } }, `
        @{ Expression = { $_.Name } }
}
else {
    $ordered = $items | Sort-Object @{ Expression = { $_.Name } }
}

$orderedFiles = $ordered | ForEach-Object { $_.File }

$outputDirectory = Split-Path -Parent $OutputFullPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

$bufferSize = 65536
$utf8 = [System.Text.UTF8Encoding]::new($false)
$separatorBytes = $utf8.GetBytes("`r`n`r`n")

Write-Host "Combining $($orderedFiles.Count) file(s) into $OutputFullPath" -ForegroundColor Cyan

$outputStream = [System.IO.FileStream]::new(
    $OutputFullPath,
    [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::Read,
    $bufferSize,
    [System.IO.FileOptions]::SequentialScan)

try {
    $needsSeparator = $false
    $index = 0

    foreach ($file in $orderedFiles) {
        $index++
        Write-Host "[$index/$($orderedFiles.Count)] $($file.Name)" -ForegroundColor Green

        if ($file.Length -eq 0) {
            Write-Warning "Skipping empty file: $($file.FullName)"
            continue
        }

        if ($needsSeparator) {
            $outputStream.Write($separatorBytes, 0, $separatorBytes.Length)
        }

        $inputStream = [System.IO.FileStream]::new(
            $file.FullName,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read,
            $bufferSize,
            [System.IO.FileOptions]::SequentialScan)

        try {
            $prefixBuffer = New-Object byte[] 3
            $read = $inputStream.Read($prefixBuffer, 0, 3)
            $skip = 0

            if ($read -ge 3 -and $prefixBuffer[0] -eq 0xEF -and $prefixBuffer[1] -eq 0xBB -and $prefixBuffer[2] -eq 0xBF) {
                $skip = 3
            }

            if ($read -gt $skip) {
                $outputStream.Write($prefixBuffer, $skip, $read - $skip)
            }

            $inputStream.CopyTo($outputStream, $bufferSize)
        }
        finally {
            $inputStream.Dispose()
        }

        $needsSeparator = $true
    }
}
finally {
    $outputStream.Dispose()
}

Write-Host "Done. Output saved to: $OutputFullPath" -ForegroundColor Cyan
