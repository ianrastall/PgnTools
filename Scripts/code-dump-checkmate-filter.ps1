[CmdletBinding()]
param(
    [string]$OutputPath = "..\Context\CheckmateFilter_Context.txt",
    [string]$ExtraManifestPath = "",
    [string[]]$Tags = @("CHECKMATEFILTER"),
    [switch]$TaggedOnly,
    [switch]$IncludeUntagged
)

$ErrorActionPreference = "Stop"

$ScriptLocation = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Join-Path $ScriptLocation ".."
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)

Write-Host "Repo Root detected as: $RepoRoot" -ForegroundColor Cyan

$manualFiles = @(
    "Docs\CheckmateFilterService.md"
    "PgnTools\Services\CheckmateFilterService.cs"
    "PgnTools\ViewModels\Tools\CheckmateFilterViewModel.cs"
    "PgnTools\Views\Tools\CheckmateFilterPage.xaml"
    "PgnTools\Views\Tools\CheckmateFilterPage.xaml.cs"
    "PgnTools\Helpers\FileReplacementHelper.cs"
    "PgnTools\Helpers\FilePickerHelper.cs"
    "PgnTools\Helpers\FileValidationHelper.cs"
    "PgnTools\Helpers\PgnHeaderExtensions.cs"
)

$autoPatterns = @(
    "PgnTools\Services\*CheckmateFilter*.cs"
    "PgnTools\ViewModels\Tools\*CheckmateFilter*.cs"
    "PgnTools\Views\Tools\*CheckmateFilter*.xaml"
    "PgnTools\Views\Tools\*CheckmateFilter*.xaml.cs"
)

$autoFiles = foreach ($pattern in $autoPatterns) {
    Get-ChildItem -Path (Join-Path $RepoRoot $pattern) -File -ErrorAction SilentlyContinue |
        ForEach-Object { [System.IO.Path]::GetRelativePath($RepoRoot, $_.FullName) }
}

# Optional extra manifest file (one relative path per line, '#' for comments)
if ([string]::IsNullOrWhiteSpace($ExtraManifestPath)) {
    $ExtraManifestPath = Join-Path $ScriptLocation "code-dump-checkmate-filter.extra.txt"
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

$OutputFullPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $ScriptLocation $OutputPath }
$utf8 = [System.Text.UTF8Encoding]::new($false) # UTF-8 without BOM

$writer = [System.IO.StreamWriter]::new($OutputFullPath, $false, $utf8)

try {
    $writer.WriteLine("# Context Dump: Checkmate Filter")
    $writer.WriteLine("# Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $writer.WriteLine("# File Count: $($targetFiles.Count)")
    $writer.WriteLine()

    foreach ($relativePath in $targetFiles) {
        $fullPath = Join-Path $RepoRoot $relativePath

        if (Test-Path $fullPath) {
            Write-Host "Processing: $relativePath" -ForegroundColor Green

            $writer.WriteLine("===== BEGIN: $relativePath =====")

            if ($TaggedOnly) {
                $lines = [System.IO.File]::ReadAllLines($fullPath, $utf8)
                $openTags = [System.Collections.Generic.HashSet[string]]::new()
                $captured = [System.Collections.Generic.List[string]]::new()

                foreach ($line in $lines) {
                    $lineHasMarker = $false
                    foreach ($tag in $Tags) {
                        $beginMarker = "PGNTOOLS-$tag-BEGIN"
                        $endMarker = "PGNTOOLS-$tag-END"

                        if ($line.IndexOf($beginMarker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                            $openTags.Add($tag) | Out-Null
                            $lineHasMarker = $true
                        }

                        if ($line.IndexOf($endMarker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                            $openTags.Remove($tag) | Out-Null
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
                $content = [System.IO.File]::ReadAllText($fullPath, $utf8)
                $writer.Write($content)

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

