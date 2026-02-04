# Lc0DownloaderService.md

## Service Specification: Lc0DownloaderService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Not applicable (external resource management)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Download, verify, and configure Leela Chess Zero (Lc0) neural network weights and engine binaries from official sources while handling GPU/backend compatibility (CUDA, OpenCL, Vulkan), network formats (ONNX, Lczero), and rating list selection. Operations must execute with robust error recovery, cryptographic hash verification, and automatic integration into the engine registry. The service must support network selection by rating/performance, hardware capability detection, and sandboxed execution environment setup.

## 2. Input Contract

```csharp
public record Lc0DownloadRequest(
    Lc0NetworkSpecifier Network,   // Network selection criteria (see Section 3)
    string DestinationDirectory,    // Target directory for weights + engine binary
    Lc0DownloadOptions Options = null // Configuration parameters (defaults in Section 2.2)
);

public record Lc0NetworkSpecifier(
    Lc0NetworkSelectionMode Mode,   // BestAvailable | SpecificId | RatingThreshold | HardwareOptimized
    string? NetworkId = null,       // Required for SpecificId mode (e.g., "799788")
    double? MinRating = null,       // Required for RatingThreshold mode (e.g., 3600.0)
    HardwareBackend? PreferredBackend = null // Optional backend hint (CUDA/OpenCL/Vulkan)
);

public enum Lc0NetworkSelectionMode
{
    BestAvailable,          // Highest-rated network compatible with hardware
    SpecificId,             // Explicit network ID from lczero.org
    RatingThreshold,        // First network meeting minimum Elo rating
    HardwareOptimized       // Network optimized for specific GPU architecture
}

public record Lc0DownloadOptions(
    bool VerifyHash = true,                 // Validate SHA256 hash against lczero.org database
    HardwareBackend Backend = HardwareBackend.AutoDetect, // AutoDetect | CUDA | OpenCL | Vulkan | Eigen
    bool DownloadEngineBinary = true,       // Also download lc0.exe/lc0 binary matching network format
    bool IntegrateWithApplication = true,   // Register engine + network in PgnTools engine registry
    bool SetExecutablePermissions = true,   // chmod +x on Unix-like systems
    TimeSpan Timeout = default,             // Per-request timeout (default: 60 seconds - networks are large)
    int MaxRetries = 3,                     // Retry count on transient failures
    bool UseTorrent = true,                 // Prefer BitTorrent for networks >50MB (faster, resilient)
    IReadOnlyList<string>? CustomMirrors = null // Override default lczero.org mirrors
);
```

### 2.1 Default Options
```csharp
public static readonly Lc0DownloadOptions Default = new(
    VerifyHash: true,
    Backend: HardwareBackend.AutoDetect,
    DownloadEngineBinary: true,
    IntegrateWithApplication: true,
    SetExecutablePermissions: true,
    Timeout: TimeSpan.FromSeconds(60),
    MaxRetries: 3,
    UseTorrent: true, // Critical for large networks
    CustomMirrors: null
);
```

### 2.2 Hardware Backend Compatibility Matrix
| Backend | GPU Requirements | Performance | Network Format | Windows | Linux | macOS |
|---------|------------------|-------------|----------------|---------|-------|-------|
| `CUDA` | NVIDIA GPU (Compute Capability ≥ 5.0) | ★★★★★ | `.pb.gz` (Lczero) | ✓ | ✓ | ✗ |
| `OpenCL` | NVIDIA/AMD/Intel GPU with OpenCL 1.2+ | ★★★☆☆ | `.pb.gz` | ✓ | ✓ | ✓ |
| `Vulkan` | Vulkan 1.1+ capable GPU | ★★★★☆ | `.pb.gz` | ✓ | ✓ | ✓ |
| `Eigen` | CPU-only (AVX2 recommended) | ★★☆☆☆ | `.pb.gz` | ✓ | ✓ | ✓ |
| `ONNX` | Any (via ONNX Runtime) | ★★★☆☆ | `.onnx` | ✓ | ✓ | ✓ |

**Critical Detection Algorithm:**
```csharp
private HardwareBackend DetectOptimalBackend()
{
    if (IsNvidiaGpuPresent())
    {
        var cc = GetNvidiaComputeCapability();
        if (cc.Major >= 7) return HardwareBackend.CUDA; // Volta+ optimal for Lc0
        if (cc.Major >= 5) return HardwareBackend.CUDA; // Maxwell+ supported
    }
    
    if (IsAmdGpuPresent() || IsIntelGpuPresent())
    {
        if (IsVulkanSupported()) return HardwareBackend.Vulkan;
        if (IsOpenClSupported()) return HardwareBackend.OpenCL;
    }
    
    // Fallback to CPU
    return HardwareBackend.Eigen;
}
```

## 3. Lc0 Distribution Architecture

### 3.1 Official Distribution Channels
| Resource Type | URL Pattern | Verification Method | Size |
|---------------|-------------|---------------------|------|
| Networks (Lczero) | `https://networks.lczero.org/networks/bestnets.json` | SHA256 hash from DB | 30-150 MB |
| Networks (Torrent) | `magnet:?xt=urn:btih:{hash}&dn=lczero-net-{id}` | Torrent hash | 30-150 MB |
| Engine (GitHub) | `https://github.com/LeelaChessZero/lc0/releases/download/{tag}/lc0-{os}-{backend}.zip` | SHA256 checksum | 5-15 MB |
| Rating List | `https://lczero.org/api/v1/networks/?limit=100` | API signature | < 1 MB |

**Critical Security Requirement:** Always verify SHA256 hashes against the official lczero.org database before loading networks into engine.

### 3.2 Network Naming Convention & Metadata
Lc0 networks are identified by unique IDs with rich metadata:

```json
{
  "id": "799788",
  "name": "799788.pb.gz",
  "sha256": "a1b2c3d4...",
  "rating": 3650.2,
  "train_date": "2024-01-15",
  "blocks": 20,
  "filters": 256,
  "training_games": 842000000,
  "is_active": true,
  "compatible_backends": ["cuda", "vulkan", "opencl"],
  "description": "Best net as of Jan 2024"
}
```

## 4. Algorithm Specification

### 4.1 Network Selection & Resolution
```csharp
private async Task<Lc0Network> ResolveNetworkAsync(
    Lc0NetworkSpecifier specifier,
    Lc0DownloadOptions options,
    CancellationToken ct)
{
    // Fetch current best networks list from lczero.org API
    var networks = await FetchNetworksListAsync(ct);
    
    return specifier.Mode switch
    {
        Lc0NetworkSelectionMode.BestAvailable => 
            SelectBestAvailableNetwork(networks, options.Backend),
        
        Lc0NetworkSelectionMode.SpecificId => 
            networks.FirstOrDefault(n => n.Id == specifier.NetworkId) 
            ?? throw new NetworkNotFoundException($"Network ID {specifier.NetworkId} not found"),
        
        Lc0NetworkSelectionMode.RatingThreshold => 
            networks.FirstOrDefault(n => n.Rating >= specifier.MinRating) 
            ?? throw new NetworkNotFoundException($"No network found with rating ≥ {specifier.MinRating}"),
        
        Lc0NetworkSelectionMode.HardwareOptimized => 
            SelectHardwareOptimizedNetwork(networks, DetectGpuArchitecture()),
        
        _ => throw new NotSupportedException($"Network selection mode {specifier.Mode} not supported")
    };
}
```

#### Best Network Selection Algorithm
```csharp
private Lc0Network SelectBestAvailableNetwork(
    IReadOnlyList<Lc0Network> networks, 
    HardwareBackend backend)
{
    // Filter networks compatible with requested backend
    var compatible = networks.Where(n => 
        n.CompatibleBackends.Contains(backend.ToString().ToLowerInvariant())
    ).ToList();
    
    if (compatible.Count == 0)
    {
        // Fallback: select any network (engine may convert format at runtime)
        compatible = networks.ToList();
        OnDiagnostic?.Invoke($"No networks compatible with {backend} backend; falling back to format conversion");
    }
    
    // Sort by rating descending, then training date descending
    compatible.Sort((a, b) => 
        b.Rating.CompareTo(a.Rating) != 0 
            ? b.Rating.CompareTo(a.Rating) 
            : b.TrainDate.CompareTo(a.TrainDate)
    );
    
    return compatible.First();
}
```

### 4.2 Secure Download Pipeline with Hash Verification
```csharp
private async Task<Lc0InstallationResult> DownloadAndVerifyAsync(
    Lc0Network network,
    string destinationDirectory,
    Lc0DownloadOptions options,
    CancellationToken ct)
{
    Directory.CreateDirectory(destinationDirectory);
    
    // Step 1: Determine optimal download method based on size
    bool useTorrent = options.UseTorrent && network.SizeBytes > 50_000_000; // >50MB
    
    string weightsPath = Path.Combine(destinationDirectory, $"{network.Id}.pb.gz");
    
    if (useTorrent)
    {
        await DownloadViaTorrentAsync(network.TorrentUri, weightsPath, options, ct);
    }
    else
    {
        await DownloadViaHttpAsync(network.DownloadUrl, weightsPath, options, ct);
    }
    
    // Step 2: Verify SHA256 hash
    if (options.VerifyHash)
    {
        if (!await VerifySha256Async(weightsPath, network.Sha256, ct))
        {
            throw new HashVerificationException(
                $"SHA256 hash mismatch for network {network.Id}. " +
                $"Expected: {network.Sha256.Substring(0, 16)}..., " +
                $"Actual: {ComputeSha256(weightsPath).Substring(0, 16)}...");
        }
    }
    
    // Step 3: Download matching engine binary if requested
    string enginePath = null;
    if (options.DownloadEngineBinary)
    {
        enginePath = await DownloadEngineBinaryAsync(
            network.RequiredEngineVersion, 
            options.Backend,
            destinationDirectory,
            ct
        );
    }
    
    // Step 4: Set executable permissions on Unix-like systems
    if (options.SetExecutablePermissions && enginePath != null && !OperatingSystem.IsWindows())
    {
        await SetExecutablePermissionAsync(enginePath, ct);
    }
    
    // Step 5: Validate network integrity (quick sanity check)
    if (!await ValidateNetworkIntegrityAsync(weightsPath, ct))
    {
        throw new NetworkValidationException($"Network {network.Id} failed integrity check");
    }
    
    return new Lc0InstallationResult(
        WeightsPath: weightsPath,
        EnginePath: enginePath,
        NetworkId: network.Id,
        Rating: network.Rating,
        Backend: options.Backend,
        Verified: options.VerifyHash
    );
}
```

### 4.3 Network Integrity Validation
Critical safety check to ensure weights file is not corrupted:

```csharp
private async Task<bool> ValidateNetworkIntegrityAsync(string weightsPath, CancellationToken ct)
{
    // Quick validation: Check file size within expected range
    var fileInfo = new FileInfo(weightsPath);
    if (fileInfo.Length < 25_000_000 || fileInfo.Length > 200_000_000)
        return false; // Outside 25MB-200MB range for standard nets
    
    // Check gzip magic bytes
    await using var fs = File.OpenRead(weightsPath);
    var header = new byte[2];
    await fs.ReadAsync(header, 0, 2, ct);
    if (header[0] != 0x1F || header[1] != 0x8B)
        return false; // Not a valid gzip file
    
    // Optional: Attempt partial decompression to verify gzip stream integrity
    try
    {
        using var gzip = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true);
        var buffer = new byte[65536];
        await gzip.ReadAsync(buffer, 0, buffer.Length, ct); // Read first 64KB
        return true;
    }
    catch
    {
        return false;
    }
}
```

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Network ID not found in database | Throw `NetworkNotFoundException` with suggestion to check lczero.org |
| GPU driver incompatibility (outdated CUDA) | Detect during engine validation; suggest driver update; fall back to CPU backend |
| Corrupted weights file (gzip error) | Delete corrupted file; retry download up to MaxRetries; switch mirror/torrent |
| Antivirus quarantine of downloaded binary | Detect via file disappearance; provide user guidance on exclusions for neural networks |
| Multiple networks in same directory | Preserve all networks with ID-suffixed names; registry tracks active network separately |
| Custom network URL without hash verification | Require explicit `VerifyHash=false` flag; warn user prominently about security risks |
| Network format mismatch (ONNX vs Lczero) | Auto-convert if engine supports it; otherwise download matching engine version |

## 6. Performance Characteristics

### 6.1 Download & Installation Benchmarks (100Mbps internet, NVMe SSD)
| Operation | Typical Duration | Notes |
|-----------|------------------|-------|
| Networks list fetch | 300 ms - 900 ms | lczero.org API call |
| Network download (80MB) | 6.5 s (HTTP) / 4.2 s (Torrent) | Torrent faster for large files |
| Hash verification | 220 ms - 650 ms | SHA256 on 80MB file |
| Engine binary download (10MB) | 0.8 s - 2.5 s | Smaller than networks |
| Network validation | 150 ms - 400 ms | Gzip header + partial decompress |

### 6.2 Resource Usage
| Scenario | Peak Memory | Disk I/O | GPU |
|----------|-------------|----------|-----|
| Network download | < 32 MB | Sequential read/write | None |
| Hash verification | < 16 MB | Sequential read | None |
| Network validation | < 64 MB | Random read (gzip seek) | None |
| Engine validation | < 256 MB | Minimal | Yes (brief initialization) |

## 7. Engine Registry Integration

### 7.1 Automatic Registration Protocol
After successful installation, register engine + network in application registry:

```csharp
private void RegisterEngineInApplication(
    Lc0InstallationResult result,
    Lc0DownloadOptions options)
{
    if (!options.IntegrateWithApplication) return;
    
    var registry = EngineRegistry.Load();
    
    // Create engine configuration with network reference
    var config = new EngineConfiguration(
        Id: Guid.NewGuid(),
        Name: $"Lc0 {result.NetworkId} ({result.Rating:F0} Elo)",
        ExecutablePath: result.EnginePath!,
        Type: EngineType.Lc0,
        WeightsPath: result.WeightsPath,
        DefaultOptions: new Dictionary<string, string>
        {
            ["Threads"] = "1", // Lc0 is GPU-bound; CPU threads minimal
            ["NNCacheSize"] = "2000000", // 2M positions
            ["MinibatchSize"] = DetectOptimalMinibatchSize(result.Backend),
            ["Backend"] = result.Backend.ToString().ToLowerInvariant(),
            ["BackendOptions"] = BuildBackendOptions(result.Backend)
        },
        IsDefaultEngine: registry.Engines.All(e => e.Type != EngineType.Lc0) // First Lc0 becomes default Lc0
    );
    
    registry.AddEngine(config);
    registry.Save();
    
    OnDiagnostic?.Invoke($"Registered Lc0 network {result.NetworkId} ({result.Rating:F0} Elo) in engine registry");
}
```

### 7.2 Backend-Specific Options
```csharp
private string BuildBackendOptions(HardwareBackend backend)
{
    return backend switch
    {
        HardwareBackend.CUDA => "cudnn=true,cudnn-fp16=true,threads=4",
        HardwareBackend.Vulkan => "device=0,threads=2",
        HardwareBackend.OpenCL => "device=0,threads=2",
        HardwareBackend.Eigen => "threads=4",
        _ => "threads=2"
    };
}
```

## 8. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `NetworkNotFoundException` | Requested network ID not in database | Fail fast; suggest checking lczero.org for valid IDs |
| `HashVerificationException` | SHA256 mismatch after download | Delete corrupted file; retry download up to MaxRetries |
| `GpuDriverException` | Outdated/incompatible GPU drivers | Detect during validation; provide specific driver version requirements |
| `NetworkValidationException` | Weights file corrupted or invalid format | Delete file; retry download; suggest alternate network |
| `AntivirusInterferenceException` | File quarantined by AV | Detect via IOException; provide guidance on neural network exclusions |
| `BackendNotSupportedException` | Requested backend not available on system | Fall back to next-best backend with diagnostic warning |

## 9. Testability Requirements

### 9.1 Required Test Fixtures
- `lc0_mock_responses/` (recorded lczero.org API responses for offline testing)
- `lc0_corrupted_weights.pb.gz` (intentionally corrupted weights for error handling tests)
- `lc0_minimal_valid.pb.gz` (smallest valid network for integration tests)
- `lc0_rating_list_sample.json` (representative rating list for selection algorithm tests)
- `engine_binaries/` (sample lc0 binaries for different backends)

### 9.2 Assertion Examples
```csharp
// Verify best available network download
var result = await service.DownloadAsync(new Lc0DownloadRequest(
    new Lc0NetworkSpecifier(Lc0NetworkSelectionMode.BestAvailable),
    "C:/pgntools/engines/lc0"
), CancellationToken.None);

Assert.True(result.Success);
Assert.NotNull(result.WeightsPath);
Assert.True(File.Exists(result.WeightsPath));
Assert.True(result.Verified); // Hash verified
Assert.True(result.Rating > 3500); // Best nets are >3500 Elo

// Verify specific network download by ID
var specificResult = await service.DownloadAsync(new Lc0DownloadRequest(
    new Lc0NetworkSpecifier(Lc0NetworkSelectionMode.SpecificId, NetworkId: "799788"),
    "C:/pgntools/engines/lc0"
), CancellationToken.None);

Assert.Equal("799788", specificResult.NetworkId);

// Verify engine registry integration
var registry = EngineRegistry.Load();
var lc0 = registry.Engines.FirstOrDefault(e => e.Type == EngineType.Lc0);
Assert.NotNull(lc0);
Assert.Equal(result.WeightsPath, lc0.WeightsPath);
Assert.Contains("Lc0", lc0.Name);
Assert.True(lc0.Name.Contains(result.Rating.ToString("F0")));

// Verify backend fallback on unsupported hardware
var cudaResult = await service.DownloadAsync(new Lc0DownloadRequest(
    new Lc0NetworkSpecifier(Lc0NetworkSelectionMode.BestAvailable),
    "C:/pgntools/engines/lc0",
    new Lc0DownloadOptions(Backend: HardwareBackend.CUDA)
), CancellationToken.None);

// On non-NVIDIA system, should fall back to Vulkan/OpenCL/Eigen
Assert.NotEqual(HardwareBackend.CUDA, cudaResult.ActualBackend);
```

## 10. Versioning & Compatibility

- **Network Format Evolution:** Supports Lczero format v1-v3; rejects legacy formats
- **Engine Compatibility:** Requires lc0 v0.30+ for modern networks; service validates version compatibility
- **GPU Driver Requirements:** Documents minimum driver versions per backend (CUDA 11.0+, Vulkan 1.2+)
- **Rating List Stability:** Monitors lczero.org API for schema changes; maintains backward compatibility layer

## 11. Security Considerations

| Risk | Mitigation |
|------|------------|
| Malicious weights injection | Mandatory SHA256 verification against lczero.org database; reject unsigned networks |
| Supply chain attack (compromised binary) | Verify engine binaries via GitHub release checksums; prefer official releases |
| GPU memory exhaustion | Enforce NNCacheSize limits based on detected VRAM; prevent OOM crashes |
| Privacy leakage via network telemetry | Lc0 has no telemetry; verify binary with `strings` command during validation |
| Antivirus false positive (neural networks flagged as "suspicious") | Maintain whitelist of known AV products; provide SHA256 hash for manual verification |
| Format confusion attacks (malicious .pb.gz) | Validate gzip structure + protobuf header before engine loading |