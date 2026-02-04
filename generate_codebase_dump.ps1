[CmdletBinding()]
param(
    [string]$OutputPath = "codebase_snapshot.txt"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptRoot

$outputFullPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path $scriptRoot $OutputPath
}
$outputFullPath = [System.IO.Path]::GetFullPath($outputFullPath)
$outputDirectory = Split-Path -Parent $outputFullPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory) -and -not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$outputRelativePath = $null
try {
    $outputRelativePath = [System.IO.Path]::GetRelativePath($scriptRoot, $outputFullPath)
} catch {
    $outputRelativePath = $null
}
$outputRelativePathNormalized = if ($outputRelativePath) { $outputRelativePath -replace "\\", "/" } else { $null }
$outputPathNormalized = $OutputPath -replace "\\", "/"

$includeExtensions = @(
    ".cs",
    ".xaml",
    ".csproj",
    ".sln",
    ".slnx",
    ".props",
    ".targets",
    ".config",
    ".json",
    ".yml",
    ".yaml",
    ".xml",
    ".resx",
    ".md",
    ".txt",
    ".ps1",
    ".psm1",
    ".psd1",
    ".cmd",
    ".bat",
    ".sh",
    ".editorconfig",
    ".gitignore",
    ".gitattributes",
    ".ruleset",
    ".csx",
    ".sql",
    ".ini"
)

$includeFileNames = @(
    "LICENSE",
    "LICENSE.txt",
    "README",
    "README.md",
    "NOTICE",
    "NOTICE.txt",
    ".editorconfig",
    ".gitignore",
    ".gitattributes"
)

$excludeDirNames = @(
    ".git",
    ".vs",
    ".idea",
    ".vscode",
    "bin",
    "obj",
    "node_modules",
    "packages",
    "dist",
    "build",
    "out",
    "artifacts",
    "TestResults"
)

$excludePathPrefixes = @(
    "Assets/",
    "PgnTools/Assets/",
    "Docs/Assets/"
)

$excludeFilePatterns = @(
    "codebase_*.txt",
    "*_codebase*.txt",
    "*snapshot*.txt"
)

$includeExtensionSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($ext in $includeExtensions) {
    if (-not [string]::IsNullOrWhiteSpace($ext)) {
        $includeExtensionSet.Add($ext) | Out-Null
    }
}

$includeFileNameSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in $includeFileNames) {
    if (-not [string]::IsNullOrWhiteSpace($name)) {
        $includeFileNameSet.Add($name) | Out-Null
    }
}

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

function Test-IsLikelyTextFile {
    param(
        [string]$FullPath
    )

    $stream = $null
    try {
        $stream = [System.IO.File]::Open($FullPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $buffer = New-Object byte[] 4096
        $read = $stream.Read($buffer, 0, $buffer.Length)
        for ($i = 0; $i -lt $read; $i++) {
            if ($buffer[$i] -eq 0) {
                return $false
            }
        }
        return $true
    } catch {
        return $false
    } finally {
        if ($stream) {
            $stream.Dispose()
        }
    }
}

function Read-TextFile {
    param(
        [string]$FullPath
    )

    $utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
    try {
        return [System.IO.File]::ReadAllText($FullPath, $utf8Strict)
    } catch [System.Text.DecoderFallbackException] {
        return [System.IO.File]::ReadAllText($FullPath, [System.Text.Encoding]::Default)
    }
}

function Matches-AnyPattern {
    param(
        [string]$Name,
        [string[]]$Patterns
    )

    foreach ($pattern in $Patterns) {
        if ($Name -like $pattern) {
            return $true
        }
    }
    return $false
}

function Should-IncludeFile {
    param(
        [string]$RelativePath,
        [string]$FullPath
    )

    $fullPathNormalized = [System.IO.Path]::GetFullPath($FullPath)
    if ($fullPathNormalized -eq $outputFullPath) {
        return $false
    }

    $normalized = $RelativePath -replace "\\", "/"
    if ($normalized -eq $outputPathNormalized) {
        return $false
    }
    if ($outputRelativePathNormalized -and $normalized -eq $outputRelativePathNormalized) {
        return $false
    }

    $segments = $normalized -split "/"
    foreach ($segment in $segments) {
        if ($excludeDirNames -contains $segment) {
            return $false
        }
    }

    foreach ($prefix in $excludePathPrefixes) {
        if ($normalized.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    $fileName = [System.IO.Path]::GetFileName($normalized)
    if (Matches-AnyPattern -Name $fileName -Patterns $excludeFilePatterns) {
        return $false
    }

    if ($includeFileNameSet.Contains($fileName)) {
        return $true
    }

    $extension = [System.IO.Path]::GetExtension($fileName)
    if ($includeExtensionSet.Contains($extension)) {
        return $true
    }

    return $false
}

$files = Get-RepoFiles
$filesToInclude = New-Object System.Collections.Generic.List[string]
$skippedBinary = 0

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
    if (-not (Test-IsLikelyTextFile -FullPath $fullPath)) {
        $skippedBinary++
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
    $writer.WriteLine("# Output: $outputFullPath")
    $writer.WriteLine("# Notes: Assets, binaries, build outputs, and non-text files are excluded.")
    $writer.WriteLine("# Included files: $($filesToInclude.Count)")
    if ($skippedBinary -gt 0) {
        $writer.WriteLine("# Skipped binary-like files: $skippedBinary")
    }
    $writer.WriteLine()

    foreach ($relativePath in $filesToInclude) {
        $fullPath = Join-Path $scriptRoot $relativePath
        $labelPath = $relativePath -replace "\\", "/"

        $writer.WriteLine("===== BEGIN: $labelPath =====")

        $content = Read-TextFile -FullPath $fullPath
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

Write-Host "Wrote $($filesToInclude.Count) files to $outputFullPath (skipped binary-like: $skippedBinary)"
