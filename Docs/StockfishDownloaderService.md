# StockfishDownloaderService.md

## Service Specification: StockfishDownloaderService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Not applicable (external resource management)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Download, verify, and configure Stockfish chess engine binaries from official sources while handling platform-specific variants (Windows/Linux/macOS), architecture optimizations (BMI2, AVX2, POPCNT), and version selection. Operations must execute with robust error recovery, cryptographic signature verification, and automatic integration into the engine registry. The service must support latest version detection, explicit version pinning, hardware capability detection, and sandboxed execution environment setup.

## 2. Input Contract

```csharp
public record StockfishDownloadRequest(
    StockfishVersionSpecifier Version,  // Latest | Specific | Development
    string DestinationDirectory,        // Target directory for engine binary
    StockfishDownloadOptions Options = null // Configuration parameters (defaults in Section 2.2)
);

public record StockfishVersionSpecifier(
    StockfishVersionMode Mode,          // LatestStable | SpecificVersion | DevelopmentBuild | CustomUrl
    string? VersionString = null,       // Required for SpecificVersion mode (e.g., "16.1")
    string? CustomDownloadUrl = null    // Required for CustomUrl mode
);

public enum StockfishVersionMode
{
    LatestStable,       // Most recent official release (e.g., 16.1)
    SpecificVersion,    // Explicit version number (e.g., "15", "14.1")
    DevelopmentBuild,   // Latest commit from official GitHub (unstable)
    CustomUrl           // User-provided direct download URL
}

public record StockfishDownloadOptions(
    bool VerifySignature = true,                // Validate PGP signature against official key
    ArchitectureOptimization Optimization = ArchitectureOptimization.AutoDetect, // AutoDetect | BMI2 | AVX2 | POPCNT | Basic
    OperatingSystem TargetOs = OperatingSystem.AutoDetect, // AutoDetect | Windows | Linux | MacOS
    bool IntegrateWithApplication = true,       // Register engine in PgnTools engine registry
    bool SetExecutablePermissions = true,       // chmod +x on Unix-like systems
    TimeSpan Timeout = default,                 // Per-request timeout (default: 30 seconds)
    int MaxRetries = 3,                         // Retry count on transient failures
    IReadOnlyList<string>? CustomMirrors = null // Override default GitHub release mirrors
);
```

### 2.1 Default Options
```csharp
public static readonly StockfishDownloadOptions Default = new(
    VerifySignature: true,
    Optimization: ArchitectureOptimization.AutoDetect,
    TargetOs: OperatingSystem.AutoDetect,
    IntegrateWithApplication: true,
    SetExecutablePermissions: true,
    Timeout: TimeSpan.FromSeconds(30),
    MaxRetries: 3,
    CustomMirrors: null
);
```

### 2.2 Architecture Optimization Mapping
| Optimization Level | CPU Requirements | Performance Gain | Binary Suffix |
|--------------------|------------------|------------------|---------------|
| `Basic` | SSE2 (all x86_64) | Baseline | `-basic` |
| `POPCNT` | POPCNT instruction (Intel Nehalem+, AMD Barcelona+) | +8% | `-modern` |
| `AVX2` | AVX2 instruction set (Intel Haswell+, AMD Excavator+) | +15% | `-avx2` |
| `BMI2` | BMI2 instruction set (Intel Haswell+, AMD Zen+) | +18% | `-bmi2` |
| `AVX512` | AVX-512 (Intel Skylake-X+, future AMD) | +22% (theoretical) | `-avx512` |

**Critical Detection Algorithm:**
```csharp
private ArchitectureOptimization DetectOptimalOptimization()
{
    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
    {
        // Use CPUID intrinsics via P/Invoke or runtime feature detection
        if (IsAvx512Supported()) return ArchitectureOptimization.AVX512;
        if (IsBmi2Supported()) return ArchitectureOptimization.BMI2;
        if (IsAvx2Supported()) return ArchitectureOptimization.AVX2;
        if (IsPopcntSupported()) return ArchitectureOptimization.POPCNT;
    }
    
    return ArchitectureOptimization.Basic;
}
```

## 3. Stockfish Distribution Architecture

### 3.1 Official Distribution Channels
| Channel | URL Pattern | Verification Method | Update Frequency |
|---------|-------------|---------------------|------------------|
| GitHub Releases | `https://github.com/official-stockfish/Stockfish/releases/download/sf_{version}/stockfish-{os}-{arch}.zip` | PGP signature + SHA256 | Stable: Quarterly<br>Dev: Daily |
| Lichess Binaries | `https://stockfish.chess.com/stockfish-{version}-{os}-{arch}.zip` | SHA256 only | Matches GitHub releases |
| IPFS Mirror | `https://ipfs.io/ipfs/{hash}/stockfish-{version}.zip` | Content hash | Immutable snapshots |

**Critical Security Requirement:** Always verify PGP signatures against the official Stockfish release key (Fingerprint: `C696 3351 7D3C 3AF0 5B04  7E32 538C 5F9D 5C16 2CCD`).

### 3.2 Binary Naming Convention
| Platform | Architecture | Filename Pattern | Executable Name |
|----------|--------------|------------------|-----------------|
| Windows | x86-64 BMI2 | `stockfish-windows-x86-64-bmi2.zip` | `stockfish.exe` |
| Windows | x86-64 AVX2 | `stockfish-windows-x86-64-avx2.zip` | `stockfish.exe` |
| Linux | x86-64 BMI2 | `stockfish-linux-x86-64-bmi2.zip` | `stockfish` |
| macOS | Apple Silicon | `stockfish-macos-arm64.zip` | `stockfish` |
| macOS | Intel | `stockfish-macos-x86-64-bmi2.zip` | `stockfish` |

## 4. Algorithm Specification

### 4.1 Version Resolution & Latest Detection
```csharp
private async Task<StockfishRelease> ResolveVersionAsync(
    StockfishVersionSpecifier specifier,
    StockfishDownloadOptions options,
    CancellationToken ct)
{
    return specifier.Mode switch
    {
        StockfishVersionMode.LatestStable => await DetectLatestStableAsync(options, ct),
        StockfishVersionMode.SpecificVersion => await ResolveSpecificVersionAsync(specifier.VersionString!, options, ct),
        StockfishVersionMode.DevelopmentBuild => await ResolveDevelopmentBuildAsync(options, ct),
        StockfishVersionMode.CustomUrl => new StockfishRelease(
            Version: "custom",
            DownloadUrl: specifier.CustomDownloadUrl!,
            SignatureUrl: null, // Custom URLs typically lack signatures
            Sha256Checksum: null,
            ReleaseDate: DateTime.UtcNow
        ),
        _ => throw new NotSupportedException($"Version mode {specifier.Mode} not supported")
    };
}
```

#### Latest Stable Detection via GitHub API
```csharp
private async Task<StockfishRelease> DetectLatestStableAsync(
    StockfishDownloadOptions options,
    CancellationToken ct)
{
    // Query GitHub Releases API
    var response = await _httpClient.GetAsync(
        "https://api.github.com/repos/official-stockfish/Stockfish/releases/latest",
        ct
    );
    
    response.EnsureSuccessStatusCode();
    var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(ct);
    
    // Extract version number from tag_name (e.g., "sf_16_1" → "16.1")
    string version = ExtractVersionFromTag(release.TagName);
    
    // Select optimal binary asset based on OS + architecture
    var asset = SelectOptimalAsset(release.Assets, options.TargetOs, options.Optimization);
    
    return new StockfishRelease(
        Version: version,
        DownloadUrl: asset.BrowserDownloadUrl,
        SignatureUrl: asset.BrowserDownloadUrl + ".sig",
        Sha256Checksum: asset.Sha256Checksum, // From release body or companion file
        ReleaseDate: release.PublishedAt
    );
}
```

### 4.2 Secure Download Pipeline with Signature Verification
```csharp
private async Task<StockfishInstallationResult> DownloadAndVerifyAsync(
    StockfishRelease release,
    string destinationDirectory,
    StockfishDownloadOptions options,
    CancellationToken ct)
{
    Directory.CreateDirectory(destinationDirectory);
    
    // Step 1: Download binary archive
    string archivePath = Path.Combine(destinationDirectory, $"stockfish-{release.Version}.zip");
    await DownloadFileWithRetriesAsync(release.DownloadUrl, archivePath, options, ct);
    
    // Step 2: Download PGP signature if verification enabled
    if (options.VerifySignature && !string.IsNullOrEmpty(release.SignatureUrl))
    {
        string sigPath = archivePath + ".sig";
        await DownloadFileWithRetriesAsync(release.SignatureUrl, sigPath, options, ct);
        
        // Verify signature against official Stockfish key
        if (!await VerifyPgpSignatureAsync(archivePath, sigPath, StockfishOfficialPublicKey, ct))
        {
            if (options.CustomMirrors != null)
            {
                // Retry with alternate mirror before failing
                throw new SignatureVerificationException(
                    $"PGP signature verification failed for {release.DownloadUrl}. " +
                    "Trying alternate mirrors...");
            }
            else
            {
                throw new SignatureVerificationException(
                    $"PGP signature verification failed and no alternate mirrors configured. " +
                    "Download aborted for security reasons.");
            }
        }
    }
    
    // Step 3: Verify SHA256 checksum if available
    if (!string.IsNullOrEmpty(release.Sha256Checksum))
    {
        if (!await VerifySha256Async(archivePath, release.Sha256Checksum, ct))
        {
            throw new ChecksumVerificationException($"SHA256 checksum mismatch for {archivePath}");
        }
    }
    
    // Step 4: Extract archive
    string executablePath = await ExtractStockfishArchiveAsync(archivePath, destinationDirectory, ct);
    
    // Step 5: Set executable permissions on Unix-like systems
    if (options.SetExecutablePermissions && !OperatingSystem.IsWindows())
    {
        await SetExecutablePermissionAsync(executablePath, ct);
    }
    
    // Step 6: Validate engine actually works
    if (!await ValidateEngineFunctionalityAsync(executablePath, ct))
    {
        throw new EngineValidationException($"Downloaded Stockfish binary failed basic functionality test");
    }
    
    return new StockfishInstallationResult(
        ExecutablePath: executablePath,
        Version: release.Version,
        Optimization: options.Optimization,
        Verified: options.VerifySignature
    );
}
```

### 4.3 Engine Functionality Validation
Critical safety check to ensure binary is not corrupted and actually works:

```csharp
private async Task<bool> ValidateEngineFunctionalityAsync(string executablePath, CancellationToken ct)
{
    // Launch engine with timeout and verify UCI handshake
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        }
    };
    
    try
    {
        process.Start();
        
        // Send UCI command
        await process.StandardInput.WriteLineAsync("uci");
        await process.StandardInput.FlushAsync(ct);
        
        // Read output with timeout
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCt = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        
        string output = await ReadUntilReadyOkAsync(process.StandardOutput, linkedCt.Token);
        
        // Verify expected UCI responses
        bool hasId = output.Contains("id name Stockfish");
        bool hasReadyOk = output.Contains("readyok");
        bool hasOptions = output.Contains("option name");
        
        process.StandardInput.WriteLine("quit");
        await process.WaitForExitAsync(TimeSpan.FromSeconds(2));
        
        return hasId && hasReadyOk && hasOptions;
    }
    catch (Exception ex)
    {
        OnDiagnostic?.Invoke($"Engine validation failed: {ex.Message}");
        return false;
    }
}
```

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| GitHub API rate limit exceeded (403) | Fall back to Lichess mirror; log diagnostic with rate limit reset time |
| PGP signature verification fails | Abort download immediately; never execute unverified binary; suggest manual verification |
| Architecture optimization not supported on current CPU | Fall back to next-lower optimization level with diagnostic warning |
| Corrupted ZIP archive (CRC error) | Delete corrupted archive; retry download up to MaxRetries; switch mirror on persistent failure |
| Antivirus quarantine of downloaded binary | Detect via file disappearance after download; provide user guidance on exclusions |
| Multiple Stockfish versions in same directory | Preserve all versions with version-suffixed names; update registry to point to latest |
| Custom URL without signature/checksum | Require explicit `VerifySignature=false` flag; warn user about security implications |

## 6. Performance Characteristics

### 6.1 Download & Installation Benchmarks (100Mbps internet, NVMe SSD)
| Operation | Typical Duration | Notes |
|-----------|------------------|-------|
| Latest version detection | 400 ms - 1.2 s | GitHub API call |
| Binary download (5MB) | 0.4 s - 1.5 s | Depends on mirror proximity |
| Signature verification | 80 ms - 200 ms | PGP crypto operations |
| Archive extraction | 120 ms - 350 ms | Single 5MB binary |
| Engine validation | 800 ms - 2.5 s | UCI handshake + basic commands |

### 6.2 Resource Usage
| Scenario | Peak Memory | Disk I/O | CPU |
|----------|-------------|----------|-----|
| Download | < 16 MB | Sequential read/write | Minimal |
| Signature verification | < 32 MB | Sequential read | Moderate (crypto) |
| Engine validation | < 64 MB | Minimal | Moderate (engine startup) |

## 7. Engine Registry Integration

### 7.1 Automatic Registration Protocol
After successful installation, register engine in application-wide registry:

```csharp
private void RegisterEngineInApplication(
    StockfishInstallationResult result,
    StockfishDownloadOptions options)
{
    if (!options.IntegrateWithApplication) return;
    
    var registry = EngineRegistry.Load();
    
    // Create engine configuration
    var config = new EngineConfiguration(
        Id: Guid.NewGuid(),
        Name: $"Stockfish {result.Version} ({result.Optimization})",
        ExecutablePath: result.ExecutablePath,
        Type: EngineType.Stockfish,
        DefaultOptions: new Dictionary<string, string>
        {
            ["Threads"] = Environment.ProcessorCount.ToString(),
            ["Hash"] = "256", // MB
            ["MultiPV"] = "1",
            ["SyzygyPath"] = DetectTablebasePath() ?? "",
            ["SyzygyProbeLimit"] = "6"
        },
        IsDefaultEngine: registry.Engines.Count == 0 // First engine becomes default
    );
    
    registry.AddEngine(config);
    registry.Save();
    
    OnDiagnostic?.Invoke($"Registered Stockfish {result.Version} in engine registry");
}
```

### 7.2 Registry Format (engines.json)
```json
{
  "engines": [
    {
      "id": "a1b2c3d4-...",
      "name": "Stockfish 16.1 (BMI2)",
      "executablePath": "C:/pgntools/engines/stockfish_16.1_bmi2.exe",
      "type": "Stockfish",
      "defaultOptions": {
        "Threads": "12",
        "Hash": "256",
        "SyzygyPath": "C:/tablebases/syzygy"
      },
      "isDefault": true,
      "lastValidated": "2024-02-04T14:30:22Z"
    }
  ]
}
```

## 8. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `SignatureVerificationException` | PGP signature invalid or unverifiable | Abort immediately; never execute binary; require user intervention |
| `ChecksumVerificationException` | SHA256 mismatch | Delete corrupted file; retry download up to MaxRetries |
| `ArchitectureNotSupportedException` | Requested optimization not available on CPU | Fall back to lower optimization with warning; abort if Basic fails |
| `EngineValidationException` | Binary fails UCI handshake | Delete binary; retry download; suggest alternate mirror |
| `AntivirusInterferenceException` | File quarantined/deleted by AV | Detect via IOException; provide user guidance on exclusions |
| `GitHubRateLimitException` | API rate limit exceeded | Fall back to Lichess mirror; respect X-RateLimit-Reset header |

## 9. Testability Requirements

### 9.1 Required Test Fixtures
- `stockfish_mock_responses/` (recorded GitHub API responses for offline testing)
- `stockfish_corrupted_binary.zip` (intentionally corrupted archive for error handling tests)
- `stockfish_invalid_signature.sig` (malformed PGP signature for security testing)
- `stockfish_minimal_valid.zip` (smallest valid Stockfish binary for integration tests)
- `pgp_keys/` (test PGP keys for signature verification unit tests)

### 9.2 Assertion Examples
```csharp
// Verify latest stable download with signature verification
var result = await service.DownloadAsync(new StockfishDownloadRequest(
    new StockfishVersionSpecifier(StockfishVersionMode.LatestStable),
    "C:/pgntools/engines"
), CancellationToken.None);

Assert.True(result.Success);
Assert.NotNull(result.ExecutablePath);
Assert.True(File.Exists(result.ExecutablePath));
Assert.True(result.Verified); // Signature verified

// Verify specific version download
var v15Result = await service.DownloadAsync(new StockfishDownloadRequest(
    new StockfishVersionSpecifier(StockfishVersionMode.SpecificVersion, VersionString: "15"),
    "C:/pgntools/engines"
), CancellationToken.None);

Assert.Equal("15", v15Result.Version);

// Verify engine registry integration
var registry = EngineRegistry.Load();
var stockfish = registry.Engines.FirstOrDefault(e => e.Name.Contains("Stockfish"));
Assert.NotNull(stockfish);
Assert.Equal(result.ExecutablePath, stockfish.ExecutablePath);

// Verify architecture fallback on unsupported optimization
var bmi2Result = await service.DownloadAsync(new StockfishDownloadRequest(
    new StockfishVersionSpecifier(StockfishVersionMode.LatestStable),
    "C:/pgntools/engines",
    new StockfishDownloadOptions(Optimization: ArchitectureOptimization.BMI2)
), CancellationToken.None);

// On non-BMI2 CPU, should fall back to AVX2/POPCNT/Basic
Assert.NotEqual(ArchitectureOptimization.BMI2, bmi2Result.ActualOptimization);
```

## 10. Versioning & Compatibility

- **Stockfish Versioning:** Follows semantic versioning (major.minor); service handles both formats (e.g., "16" vs "16.1")
- **UCI Protocol:** Requires UCI 1.0+ compliance; validates during engine validation phase
- **Platform Support:** Windows 10+, Linux kernel 3.0+, macOS 10.15+; older platforms require custom builds
- **PGP Key Rotation:** Monitors Stockfish project for key rotation announcements; maintains key revocation list

## 11. Security Considerations

| Risk | Mitigation |
|------|------------|
| Supply chain attack (compromised binary) | Mandatory PGP signature verification against official key; reject unsigned binaries by default |
| Man-in-the-middle attack | HTTPS-only downloads; certificate pinning for critical mirrors |
| Malicious custom URL | Require explicit opt-out of signature verification; warn user prominently |
| Antivirus false positive | Maintain whitelist of known AV products; provide SHA256 hash for manual verification |
| Engine execution sandboxing | Run engines with restricted permissions (no network access, limited filesystem access) |
| Privacy leakage via engine telemetry | Stockfish has no telemetry; verify binary with `strings` command during validation |