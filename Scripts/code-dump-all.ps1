[CmdletBinding()]
param(
    # Full-codebase context dump for a whole-app review.
    # Pair the resulting file with ARCHITECTURE.md (load ARCHITECTURE.md into the
    # round-up tool's "Context" slot, and this file into the "Codebase" slot).
    [string]$OutputPath = "..\Context\Full_Context.txt"
)

$ErrorActionPreference = "Stop"

$ScriptLocation = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $ScriptLocation ".."))
Write-Host "Repo Root detected as: $RepoRoot" -ForegroundColor Cyan

# Root-level docs/config to include explicitly (ARCHITECTURE.md is intentionally NOT
# here — it's the separate "Context" document that goes alongside this dump).
$manualFiles = @(
    "README.md",
    "build.cmd",
    ".github\workflows\build.yml",
    ".github\workflows\release.yml"
)

# Source trees to sweep recursively.
$includeRoots = @(
    "PgnTools.Wpf",
    "PgnTools\Services",
    "PgnTools\ViewModels",
    "PgnTools\Models",
    "PgnTools\Helpers",
    "PgnTools.SmokeTests"
)
$extensions = @(".cs", ".xaml", ".csproj")
$excludePattern = '\\(bin|obj|Build)\\'

$autoFiles = foreach ($root in $includeRoots) {
    $full = Join-Path $RepoRoot $root
    if (Test-Path $full) {
        Get-ChildItem -Path $full -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $extensions -contains $_.Extension -and $_.FullName -notmatch $excludePattern } |
            ForEach-Object { [System.IO.Path]::GetRelativePath($RepoRoot, $_.FullName) }
    }
}

$targetFiles = @($manualFiles + $autoFiles) |
    Where-Object { Test-Path (Join-Path $RepoRoot $_) } |
    Sort-Object -Unique

# Prepare output (UTF-8 without BOM), creating the Context folder if needed.
$OutputFullPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $ScriptLocation $OutputPath }
$OutputFullPath = [System.IO.Path]::GetFullPath($OutputFullPath)
$OutDir = Split-Path -Parent $OutputFullPath
if ($OutDir -and -not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }

$utf8 = [System.Text.UTF8Encoding]::new($false)
$writer = [System.IO.StreamWriter]::new($OutputFullPath, $false, $utf8)

try {
    $writer.WriteLine("# Context Dump: PGN Tools (full codebase)")
    $writer.WriteLine("# Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $writer.WriteLine("# File Count: $($targetFiles.Count)")
    $writer.WriteLine("# Pair this with ARCHITECTURE.md (load that into the tool's Context slot).")
    $writer.WriteLine()

    foreach ($relativePath in $targetFiles) {
        $fullPath = Join-Path $RepoRoot $relativePath
        Write-Host "Processing: $relativePath" -ForegroundColor Green

        $writer.WriteLine("===== BEGIN: $relativePath =====")
        $content = [System.IO.File]::ReadAllText($fullPath, $utf8)
        $writer.Write($content)
        if (-not $content.EndsWith("`n")) { $writer.WriteLine() }
        $writer.WriteLine("===== END: $relativePath =====")
        $writer.WriteLine()
    }
}
finally {
    $writer.Dispose()
}

Write-Host "Done. Output saved to: $OutputFullPath" -ForegroundColor Cyan
Write-Host "Total files: $($targetFiles.Count)" -ForegroundColor Cyan
