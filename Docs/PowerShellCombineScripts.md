# PowerShell Combine Scripts (Context Dumps)

This document describes the standard pattern for creating the `code-dump-*.ps1` scripts that generate context dumps for LLM review.

**Purpose**
These scripts produce a single, plain‑text dump that stitches together a topic slice of the repo, including optional tag‑filtered sections and manifest‑driven extras.

**Naming and Location**
Use these conventions for new scripts:
- Script: `Scripts/code-dump-<topic>.ps1`
- Extra manifest: `Scripts/code-dump-<topic>.extra.txt`
- Default output: `Context/<Topic>_Context.txt`

**Required Parameters**
Every script should accept these parameters (matching the existing scripts):
- `OutputPath`: default output file path (relative to the script directory by default).
- `ExtraManifestPath`: optional list of extra files, one repo‑relative path per line.
- `Tags`: list of tag identifiers used with `-TaggedOnly`.
- `TaggedOnly`: switch to extract only tagged sections.
- `IncludeUntagged`: if `-TaggedOnly` is set and a file has no tagged sections, include the full file.

**Core Script Structure**
The standard flow is:
1. Resolve repo root from the script location.
2. Build the file list from manual files, auto patterns, and optional manifest.
3. Normalize and validate tags (if `-TaggedOnly`).
4. Emit a header with topic/date/count.
5. Write each file’s content between `BEGIN`/`END` markers.
6. Write missing file markers for absent paths.
7. Always dispose the writer in `finally`.

**Template**
Use this skeleton as a starting point:

```powershell
[CmdletBinding()]
param(
    [string]$OutputPath = "..\Context\<Topic>_Context.txt",
    [string]$ExtraManifestPath = "",
    [string[]]$Tags = @("<TOPICTAG>"),
    [switch]$TaggedOnly,
    [switch]$IncludeUntagged
)

$ErrorActionPreference = "Stop"

function Remove-Base64ImageLines {
    param([string[]]$Lines)
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

$ScriptLocation = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $ScriptLocation ".."))

$manualFiles = @(
    "Docs\<TopicDoc>.md",
    "PgnTools\Services\<TopicService>.cs"
)

$autoPatterns = @(
    "PgnTools\Services\*<TopicName>*.cs",
    "PgnTools\ViewModels\Tools\*<TopicName>*.cs",
    "PgnTools\Views\Tools\*<TopicName>*.xaml",
    "PgnTools\Views\Tools\*<TopicName>*.xaml.cs"
)

$autoFiles = foreach ($pattern in $autoPatterns) {
    Get-ChildItem -Path (Join-Path $RepoRoot $pattern) -File -ErrorAction SilentlyContinue |
        ForEach-Object { [System.IO.Path]::GetRelativePath($RepoRoot, $_.FullName) }
}

if ([string]::IsNullOrWhiteSpace($ExtraManifestPath)) {
    $ExtraManifestPath = Join-Path $ScriptLocation "code-dump-<topic>.extra.txt"
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
    if ($Tags.Count -eq 0) { throw "At least one tag is required when -TaggedOnly is specified." }
}

$OutputFullPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $ScriptLocation $OutputPath }
$utf8 = [System.Text.UTF8Encoding]::new($false)
$writer = [System.IO.StreamWriter]::new($OutputFullPath, $false, $utf8)

try {
    $writer.WriteLine("# Context Dump: <Topic Title>")
    $writer.WriteLine("# Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $writer.WriteLine("# File Count: $($targetFiles.Count)")
    $writer.WriteLine()

    foreach ($relativePath in $targetFiles) {
        $fullPath = Join-Path $RepoRoot $relativePath
        if (Test-Path $fullPath) {
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
```

**Tag Markers**
Tagged sections are delimited by markers inside source files:
- Begin: `PGNTOOLS-<TAG>-BEGIN`
- End: `PGNTOOLS-<TAG>-END`
Matching is case‑insensitive. Tags are normalized to uppercase in the script.

**Choosing Read Strategy**
Pick one of these approaches and keep it consistent per script:
- `ReadAllLines` + join with `` `n ``. Good when you need line‑level filtering or base64 stripping.
- `ReadAllText` + ensure a newline at the end. Simpler, but no per‑line filtering.

**Base64 Image Stripping**
Use `Remove-Base64ImageLines` when any input file can contain inline data‑URI images (common in Markdown or XAML). This avoids massive dumps and keeps output text‑only.

**Output Format**
Every file is wrapped like this:
- `===== BEGIN: <relative path> =====`
- File contents (or tagged sections)
- `===== END: <relative path> =====`
Missing files are recorded with:
- `!!!!! MISSING FILE: <relative path> !!!!!`

**Common Exceptions and Gotchas**
These are easy to miss when creating a new script:
- Always resolve `$RepoRoot` from the script location, not the current working directory.
- The `OutputPath` is interpreted relative to the script directory, not the repo root.
- Avoid including binary files. If a file is non‑text, add it to `.extra.txt` only if it is safe to render.
- If `-TaggedOnly` is used, include `-IncludeUntagged` when you want a full fallback for files without tags.
- Keep the output header consistent across scripts to make downstream tooling stable.

**Creation Checklist**
Use this list when adding a new combine script:
1. Pick a topic name and tag (e.g., `PGNMENTOR`).
2. Create `Scripts/code-dump-<topic>.ps1` using the template.
3. Add `Docs/<TopicDoc>.md` and core service/VM/view files to `$manualFiles`.
4. Add `*<TopicName>*` patterns for services, view models, and views.
5. Decide whether base64 stripping is needed and keep the read strategy consistent.
6. Create `Scripts/code-dump-<topic>.extra.txt` for edge files.
7. Confirm output path defaults to `Context/<Topic>_Context.txt`.
8. Run the script and verify a stable, text‑only dump.
