<#
.SYNOPSIS
    Exports labeled code blocks for the Chesscom Monthly Downloader tool for use as LLM context.

.DESCRIPTION
    Scans the PgnTools solution for all files relevant to the ChesscomMonthlyDownloader,
    wraps each in a labeled fenced code block with metadata (path, language, purpose),
    and writes the combined output to a single Markdown file.

    Right-click this script in Windows Explorer and select "Run with PowerShell" to execute.

.OUTPUTS
    A file named ChesscomDownloader-LLMContext.md in the script's directory.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve paths
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Split-Path -Parent $scriptDir
$outputFile = Join-Path $scriptDir 'ChesscomDownloader-LLMContext.md'

# Language map by extension
$langMap = @{
    '.cs'      = 'csharp'
    '.csproj'  = 'xml'
    '.json'    = 'json'
    '.sln'     = 'text'
    '.props'   = 'xml'
    '.targets' = 'xml'
    '.config'  = 'xml'
    '.md'      = 'markdown'
    '.ps1'     = 'powershell'
    '.txt'     = 'text'
}

function Get-Language([string]$filePath) {
    $ext = [System.IO.Path]::GetExtension($filePath).ToLower()
    if ($langMap.ContainsKey($ext)) { return $langMap[$ext] }
    return 'text'
}

function Format-CodeBlock([string]$filePath, [string]$label, [string]$content) {
    $lang = Get-Language $filePath
    $relativePath = $filePath.Replace($repoRoot, '').TrimStart('\', '/')
    $header = @"

---

## $label

| Property | Value |
|----------|-------|
| **File** | ``$relativePath`` |
| **Full Path** | ``$filePath`` |
| **Language** | $lang |
| **Type** | $([System.IO.Path]::GetExtension($filePath)) |

"@

    $block = @"
$header
``````$lang
// filepath: $filePath
$content
``````

"@
    return $block
}

# Start building output
$output = [System.Text.StringBuilder]::new()

[void]$output.AppendLine(@"
# Chesscom Monthly Downloader — Full Source Context for LLMs

> **Generated:** $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
> **Repository:** $repoRoot
> **Purpose:** Complete labeled source code for the ChesscomMonthlyDownloader tool from the PgnTools solution.
> **Usage:** Paste this entire document into an LLM chat for full codebase awareness.

---

## Table of Contents

(Auto-generated below based on discovered files)

"@)

$tocEntries = [System.Collections.ArrayList]::new()
$blocks = [System.Collections.ArrayList]::new()

# --- PRIMARY: ChesscomMonthlyDownloader project files ---
$primaryDir = Join-Path $repoRoot 'ChesscomMonthlyDownloader'
if (Test-Path $primaryDir) {
    $primaryFiles = Get-ChildItem -Path $primaryDir -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.csproj', '.json', '.config' } |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|\.vs)\\' } |
        Sort-Object FullName

    foreach ($file in $primaryFiles) {
        $content = Get-Content -Path $file.FullName -Raw -ErrorAction SilentlyContinue
        if (-not [string]::IsNullOrWhiteSpace($content)) {
            $label = "[PRIMARY] $($file.Name)"
            [void]$tocEntries.Add("- **$($file.Name)** — ``$($file.FullName.Replace($repoRoot, '').TrimStart('\'))``")
            [void]$blocks.Add((Format-CodeBlock $file.FullName $label $content))
        }
    }
}
else {
    Write-Warning "Primary directory not found: $primaryDir"
}

# --- SHARED: Look for shared/common projects referenced by the downloader ---
# Parse the .csproj for ProjectReference entries
$csprojPath = Join-Path $primaryDir 'ChesscomMonthlyDownloader.csproj'
$referencedProjects = @()
if (Test-Path $csprojPath) {
    $csprojContent = Get-Content $csprojPath -Raw
    $regex = [regex]'<ProjectReference\s+Include="([^"]+)"'
    $matches_ = $regex.Matches($csprojContent)
    foreach ($m in $matches_) {
        $refRelative = $m.Groups[1].Value.Replace('..', '').TrimStart('\', '/')
        # Resolve full path from csproj location
        $refFull = [System.IO.Path]::GetFullPath((Join-Path $primaryDir $m.Groups[1].Value))
        $refDir = Split-Path -Parent $refFull
        if (Test-Path $refDir) {
            $referencedProjects += $refDir
        }
    }
}

foreach ($projDir in $referencedProjects) {
    $sharedFiles = Get-ChildItem -Path $projDir -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.csproj', '.json' } |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|\.vs)\\' } |
        Sort-Object FullName

    foreach ($file in $sharedFiles) {
        $content = Get-Content -Path $file.FullName -Raw -ErrorAction SilentlyContinue
        if (-not [string]::IsNullOrWhiteSpace($content)) {
            $label = "[SHARED] $($file.Name)"
            [void]$tocEntries.Add("- **$($file.Name)** — ``$($file.FullName.Replace($repoRoot, '').TrimStart('\'))`` *(shared dependency)*")
            [void]$blocks.Add((Format-CodeBlock $file.FullName $label $content))
        }
    }
}

# --- SOLUTION-LEVEL: Solution file and Directory.Build.props if they exist ---
$slnFiles = Get-ChildItem -Path $repoRoot -Filter '*.sln' -File
foreach ($sln in $slnFiles) {
    $content = Get-Content -Path $sln.FullName -Raw -ErrorAction SilentlyContinue
    if (-not [string]::IsNullOrWhiteSpace($content)) {
        $label = "[SOLUTION] $($sln.Name)"
        [void]$tocEntries.Add("- **$($sln.Name)** — ``$($sln.Name)`` *(solution file)*")
        [void]$blocks.Add((Format-CodeBlock $sln.FullName $label $content))
    }
}

$buildProps = Join-Path $repoRoot 'Directory.Build.props'
if (Test-Path $buildProps) {
    $content = Get-Content -Path $buildProps -Raw
    $label = "[BUILD] Directory.Build.props"
    [void]$tocEntries.Add("- **Directory.Build.props** — ``Directory.Build.props`` *(build configuration)*")
    [void]$blocks.Add((Format-CodeBlock $buildProps $label $content))
}

$globalJson = Join-Path $repoRoot 'global.json'
if (Test-Path $globalJson) {
    $content = Get-Content -Path $globalJson -Raw
    $label = "[BUILD] global.json"
    [void]$tocEntries.Add("- **global.json** — ``global.json`` *(SDK configuration)*")
    [void]$blocks.Add((Format-CodeBlock $globalJson $label $content))
}

# --- Assemble final document ---
# Insert TOC
[void]$output.AppendLine("### Files Included")
[void]$output.AppendLine("")
foreach ($entry in $tocEntries) {
    [void]$output.AppendLine($entry)
}
[void]$output.AppendLine("")
[void]$output.AppendLine("---")

# Insert all blocks
foreach ($block in $blocks) {
    [void]$output.Append($block)
}

# Footer
[void]$output.AppendLine(@"

---

## Metadata

- **Total files exported:** $($blocks.Count)
- **Primary project files:** $(if (Test-Path $primaryDir) { (Get-ChildItem $primaryDir -Recurse -File | Where-Object { $_.Extension -in '.cs','.csproj' -and $_.FullName -notmatch '\\(bin|obj)\\' }).Count } else { 0 })
- **Referenced shared projects:** $($referencedProjects.Count)
- **Generated by:** Export-ChesscomDownloaderContext.ps1
- **Timestamp:** $(Get-Date -Format 'o')
"@)

# Write output
$output.ToString() | Out-File -FilePath $outputFile -Encoding utf8 -Force

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " Export Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Output file: $outputFile" -ForegroundColor Cyan
Write-Host "Total code blocks: $($blocks.Count)" -ForegroundColor Cyan
Write-Host ""
Write-Host "You can now paste the contents of this file into any browser-based LLM."
Write-Host ""

# Keep window open if run via right-click
if ($Host.Name -eq 'ConsoleHost') {
    Write-Host "Press any key to exit..." -ForegroundColor Yellow
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
}
