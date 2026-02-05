[CmdletBinding()]
param(
    [string]$OutputPath = "..\Context\Tablebases_Context.txt",
    [string]$ExtraManifestPath = "",
    [string[]]$Tags = @("TABLEBASES"),
    [switch]$TaggedOnly,
    [switch]$IncludeUntagged
)

$ErrorActionPreference = "Stop"

# 1. Setup Root Context
# Assuming this script is inside a subfolder (e.g., /Scripts/), we go up one level to the Repo Root.
# If you place this in the root, change ".." to "."
$ScriptLocation = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Join-Path $ScriptLocation ".."
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)

Write-Host "Repo Root detected as: $RepoRoot" -ForegroundColor Cyan

# 2. Build File List (Topic Slice)
# These paths are relative to the Repo Root
$manualFiles = @(
    "Docs\TablebaseDownloaderService.md",
    "PgnTools\Services\TablebaseConstants.cs",
    "PgnTools\Services\TablebaseDownloaderService.cs",
    "PgnTools\Helpers\FileReplacementHelper.cs",
    "PgnTools\Helpers\FileValidationHelper.cs",
    "PgnTools\ViewModels\Tools\TablebaseDownloaderViewModel.cs",
    "PgnTools\Views\Tools\TablebaseDownloaderPage.xaml",
    "PgnTools\Views\Tools\TablebaseDownloaderPage.xaml.cs",
    "PgnTools\Assets\Tablebases\download.txt"
)

# Auto-discover all Tablebase-named files in the primary folders
$autoPatterns = @(
    "PgnTools\Services\*Tablebase*.cs",
    "PgnTools\ViewModels\Tools\*Tablebase*.cs",
    "PgnTools\Views\Tools\*Tablebase*.xaml",
    "PgnTools\Views\Tools\*Tablebase*.xaml.cs",
    "PgnTools\Assets\Tablebases\*"
)

$autoFiles = foreach ($pattern in $autoPatterns) {
    Get-ChildItem -Path (Join-Path $RepoRoot $pattern) -File -ErrorAction SilentlyContinue |
        ForEach-Object { [System.IO.Path]::GetRelativePath($RepoRoot, $_.FullName) }
}

# Optional extra manifest file (one relative path per line, '#' for comments)
if ([string]::IsNullOrWhiteSpace($ExtraManifestPath)) {
    $ExtraManifestPath = Join-Path $ScriptLocation "code-dump-tablebases.extra.txt"
}

$extraFiles = @()
if (Test-Path $ExtraManifestPath) {
    $extraFiles = Get-Content $ExtraManifestPath |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith("#") }
}

$targetFiles = @($manualFiles + $autoFiles + $extraFiles) | Sort-Object -Unique

if ($TaggedOnly) {
    $Tags = $Tags |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ } |
        ForEach-Object { $_.ToUpperInvariant() } |
        Sort-Object -Unique

    if ($Tags.Count -eq 0) {
        throw "At least one tag is required when -TaggedOnly is specified."
    }
}

# 3. Prepare Output
$OutputFullPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $ScriptLocation $OutputPath }
$utf8 = [System.Text.UTF8Encoding]::new($false) # UTF-8 without BOM

$writer = [System.IO.StreamWriter]::new($OutputFullPath, $false, $utf8)

try {
    # Write Header
    $writer.WriteLine("# Context Dump: Tablebases")
    $writer.WriteLine("# Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $writer.WriteLine("# File Count: $($targetFiles.Count)")
    $writer.WriteLine()

    foreach ($relativePath in $targetFiles) {
        $fullPath = Join-Path $RepoRoot $relativePath
        
        if (Test-Path $fullPath) {
            Write-Host "Processing: $relativePath" -ForegroundColor Green
            
            # File Header
            $writer.WriteLine("===== BEGIN: $relativePath =====")
            
            if ($TaggedOnly) {
                $lines = [System.IO.File]::ReadAllLines($fullPath, $utf8)
                $openTags = [System.Collections.Generic.HashSet[string]]::new()
                $captured = [System.Collections.Generic.List[string]]::new()
                $foundMarker = $false

                foreach ($line in $lines) {
                    $lineHasMarker = $false
                    foreach ($tag in $Tags) {
                        $beginMarker = "PGNTOOLS-$tag-BEGIN"
                        $endMarker = "PGNTOOLS-$tag-END"

                        if ($line.IndexOf($beginMarker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                            $openTags.Add($tag) | Out-Null
                            $foundMarker = $true
                            $lineHasMarker = $true
                        }

                        if ($line.IndexOf($endMarker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                            $openTags.Remove($tag) | Out-Null
                            $foundMarker = $true
                            $lineHasMarker = $true
                        }
                    }

                    if ($openTags.Count -gt 0 -and -not $lineHasMarker) {
                        $captured.Add($line) | Out-Null
                    }
                }

                if ($captured.Count -gt 0) {
                    $writer.WriteLine("----- TAGGED SECTIONS: $($Tags -join ", ") -----")
                    $writer.WriteLine($captured -join "`n")
                }
                elseif ($IncludeUntagged) {
                    $content = [System.IO.File]::ReadAllText($fullPath, $utf8)
                    $writer.Write($content)

                    if (-not $content.EndsWith("`n")) {
                        $writer.WriteLine()
                    }
                }
                else {
                    Write-Warning "No tagged sections found in: $relativePath"
                }
            }
            else {
                # Read and Write Content
                $content = [System.IO.File]::ReadAllText($fullPath, $utf8)
                $writer.Write($content)
                
                # Ensure newline at end of file
                if (-not $content.EndsWith("`n")) {
                    $writer.WriteLine()
                }
            }
            
            $writer.WriteLine("===== END: $relativePath =====")
            $writer.WriteLine()
        }
        else {
            Write-Warning "File not found (skipped): $relativePath"
            $writer.WriteLine("!!!!! MISSING FILE: $relativePath !!!!!")
            $writer.WriteLine()
        }
    }
}
finally {
    $writer.Dispose()
}

Write-Host "Done. Output saved to: $OutputFullPath" -ForegroundColor Cyan
