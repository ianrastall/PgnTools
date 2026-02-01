[CmdletBinding()]
param(
    [string]$OutputPath = "codebase_pgn_tools.txt"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptRoot

$outputFullPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path $scriptRoot $OutputPath
}

$includeFiles = @(
    "Agents.md",
    "README.md",
    "LICENSE",
    ".gitattributes",
    ".gitignore",
    "generate_codebase_dump.ps1",
    "PgnTools/Assets/eco.pgn",
    "PgnTools/Assets/list.txt"
)

$includePrefixes = @(
    "PgnTools/",
    ".github/"
)

$excludePrefixes = @(
    "PgnTools/Assets/"
)

$excludePathSegments = @(
    ".git",
    ".vs",
    ".idea",
    "bin",
    "obj",
    "node_modules",
    "packages"
)

function Get-RepoFiles {
    try {
        $null = Get-Command git -ErrorAction Stop
        $raw = & git ls-files -z
        if ($LASTEXITCODE -ne 0) {
            throw "git ls-files failed"
        }
        return ($raw -split "`0") | Where-Object { $_ -and $_.Trim().Length -gt 0 }
    } catch {
        return Get-ChildItem -Path $scriptRoot -Recurse -File | ForEach-Object {
            $_.FullName.Substring($scriptRoot.Length + 1)
        }
    }
}

function Should-IncludeFile {
    param(
        [string]$RelativePath,
        [string]$FullPath
    )

    if ($RelativePath -eq $OutputPath -or $FullPath -eq $outputFullPath) {
        return $false
    }

    $normalized = $RelativePath -replace "\\", "/"

    $segments = $normalized -split "/"
    foreach ($segment in $segments) {
        if ($excludePathSegments -contains $segment) {
            return $false
        }
    }

    if ($includeFiles -contains $normalized) {
        return $true
    }

    foreach ($prefix in $excludePrefixes) {
        if ($normalized.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    foreach ($prefix in $includePrefixes) {
        if ($normalized.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

$files = Get-RepoFiles
$filesToInclude = New-Object System.Collections.Generic.List[string]

foreach ($file in $files) {
    if ([string]::IsNullOrWhiteSpace($file)) {
        continue
    }
    $fullPath = Join-Path $scriptRoot $file
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }
    if (-not (Should-IncludeFile -RelativePath $file -FullPath $fullPath)) {
        continue
    }
    $filesToInclude.Add($file) | Out-Null
}

$filesToInclude.Sort([System.StringComparer]::OrdinalIgnoreCase)

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$writer = New-Object System.IO.StreamWriter($outputFullPath, $false, $utf8NoBom)
try {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $writer.WriteLine("# PgnTools codebase snapshot")
    $writer.WriteLine("# Generated: $timestamp")
    $writer.WriteLine("# Root: $scriptRoot")
    $writer.WriteLine("# Scope: PgnTools source + selected text assets (eco.pgn, list.txt) + build/workflow files")
    $writer.WriteLine()

    foreach ($relativePath in $filesToInclude) {
        $fullPath = Join-Path $scriptRoot $relativePath
        $labelPath = $relativePath -replace "\\", "/"

        $writer.WriteLine("===== BEGIN: $labelPath =====")

        $content = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8
        if (-not [string]::IsNullOrEmpty($content)) {
            $writer.Write($content)
            if (-not $content.EndsWith("`n")) {
                $writer.WriteLine()
            }
        } else {
            $writer.WriteLine()
        }

        $writer.WriteLine("===== END: $labelPath =====")
        $writer.WriteLine()
    }
} finally {
    $writer.Flush()
    $writer.Dispose()
}

Write-Host "Wrote $($filesToInclude.Count) files to $outputFullPath"
