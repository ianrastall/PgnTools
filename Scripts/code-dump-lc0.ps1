[CmdletBinding()]
param(
    [string]$OutputPath = "..\Context\Lc0_Context.txt",
    [string]$ExtraManifestPath = "",
    [string[]]$Tags = @("LC0"),
    [switch]$TaggedOnly,
    [switch]$IncludeUntagged
)

$ErrorActionPreference = "Stop"

# Strip inline base64 data-image blobs from output to keep dumps text-only.
function Remove-Base64ImageLines {
    param(
        [string[]]$Lines
    )

    $output = New-Object System.Collections.Generic.List[string]
    foreach ($line in $Lines) {
        if ($line -match 'data:image\/[a-zA-Z0-9.+-]+;base64,') {
            $output.Add('[image omitted: data URI removed]') | Out-Null
            continue
        }
        $output.Add($line) | Out-Null
    }
    return $output.ToArray()
}

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
    "Docs\Lc0DownloaderService.md",
    "PgnTools\Services\Lc0DownloaderService.cs",
    "PgnTools\ViewModels\Tools\Lc0DownloaderViewModel.cs",
    "PgnTools\Views\Tools\Lc0DownloaderPage.xaml",
    "PgnTools\Views\Tools\Lc0DownloaderPage.xaml.cs",
    "PgnTools\Helpers\FileValidationHelper.cs",
    "PgnTools\Helpers\FilePickerHelper.cs",
    "PgnTools\Helpers\PgnHeaderExtensions.cs"
)

# Auto-discover all Lc0-named files in the primary folders
$autoPatterns = @(
    "PgnTools\Services\*Lc0*.cs",
    "PgnTools\ViewModels\Tools\*Lc0*.cs",
    "PgnTools\Views\Tools\*Lc0*.xaml",
    "PgnTools\Views\Tools\*Lc0*.xaml.cs",
    "PgnTools\Assets\lc0\*",
    "PgnTools\Assets\lc0\old\*"
)

$autoFiles = foreach ($pattern in $autoPatterns) {
    Get-ChildItem -Path (Join-Path $RepoRoot $pattern) -File -ErrorAction SilentlyContinue |
        ForEach-Object { [System.IO.Path]::GetRelativePath($RepoRoot, $_.FullName) }
}

# Optional extra manifest file (one relative path per line, '#' for comments)
if ([string]::IsNullOrWhiteSpace($ExtraManifestPath)) {
    $ExtraManifestPath = Join-Path $ScriptLocation "code-dump-lc0.extra.txt"
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
    $writer.WriteLine("# Context Dump: Lc0")
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
                    $filtered = Remove-Base64ImageLines -Lines $captured
                    $writer.WriteLine($filtered -join "`n")
                }
                elseif ($IncludeUntagged) {
                    $contentLines = [System.IO.File]::ReadAllLines($fullPath, $utf8)
                    $filtered = Remove-Base64ImageLines -Lines $contentLines
                    $writer.Write($filtered -join "`n")
                    $writer.WriteLine()
                }
                else {
                    Write-Warning "No tagged sections found in: $relativePath"
                }
            }
            else {
                # Read and Write Content
                $contentLines = [System.IO.File]::ReadAllLines($fullPath, $utf8)
                $filtered = Remove-Base64ImageLines -Lines $contentLines
                $writer.Write($filtered -join "`n")
                $writer.WriteLine()
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
