[CmdletBinding()]
param(
    [string]$OutputPath = "..\Context\Tablebases_Context.txt"
)

$ErrorActionPreference = "Stop"

# 1. Setup Root Context
# Assuming this script is inside a subfolder (e.g., /Scripts/), we go up one level to the Repo Root.
# If you place this in the root, change ".." to "."
$ScriptLocation = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Join-Path $ScriptLocation ".."
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)

Write-Host "Repo Root detected as: $RepoRoot" -ForegroundColor Cyan

# 2. Hardcoded File List (The "Vertical Slice")
# These paths are relative to the Repo Root
$targetFiles = @(
    "Docs\TablebaseDownloaderService.md",
    "PgnTools\Services\TablebaseConstants.cs",
    "PgnTools\Services\TablebaseDownloaderService.cs",
    "PgnTools\ViewModels\Tools\TablebaseDownloaderViewModel.cs",
    "PgnTools\Views\Tools\TablebaseDownloaderPage.xaml",
    "PgnTools\Views\Tools\TablebaseDownloaderPage.xaml.cs",
	"PgnTools\Assets\Tablebases\download.txt"
)

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
            
            # Read and Write Content
            $content = [System.IO.File]::ReadAllText($fullPath, $utf8)
            $writer.Write($content)
            
            # Ensure newline at end of file
            if (-not $content.EndsWith("`n")) {
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