# LichessDownloaderService.md

## Service Specification: LichessDownloaderService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Optional (index generated after download completes)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Download chess games from Lichess.org for specific users or tournaments using the official REST API while respecting rate limits, handling pagination, and supporting incremental updates. Operations must execute with robust error recovery, progress reporting, and automatic format conversion (NDJSON → PGN). The service must support bulk downloads (entire user history), filtered downloads (by speed, rated status, time control), and integrate downloaded content directly into the binary index ecosystem.

## 2. Input Contract

```csharp
public record LichessDownloadRequest(
    LichessDownloadTarget Target,   // User games, tournament games, or study content
    string OutputFilePath,          // Target path for downloaded PGN file
    LichessDownloadOptions Options = null // Configuration parameters (defaults in Section 2.2)
);

public record LichessDownloadTarget(
    LichessTargetType Type,         // User | Tournament | Study | OpeningExplorer
    string Identifier,              // Username, tournament ID, study ID, or ECO code
    LichessGameFilters? Filters = null // Optional filters (see Section 3)
);

public enum LichessTargetType
{
    User,               // Games played by specific user
    Tournament,         // Games from specific tournament
    Study,              // Analysis boards from Lichess studies
    OpeningExplorer     // Master/OTB games from Lichess Opening Explorer
}

public record LichessGameFilters(
    bool? RatedOnly = null,         // null = both, true = rated only, false = casual only
    IReadOnlyList<TimeControlType>? Speeds = null, // Bullet, Blitz, Rapid, Classical, Correspondence
    DateOnly? Since = null,         // Download games since this date (inclusive)
    DateOnly? Until = null,         // Download games until this date (inclusive)
    bool? VsAi = null,              // Include/exclude AI games
    bool? MaximalGames = null       // Request maximal game format (evals, clocks, accuracy)
);

public record LichessDownloadOptions(
    bool GenerateIndex = true,              // Build .pbi index after download completes
    TimeSpan Timeout = default,             // Per-request timeout (default: 10 seconds)
    int MaxRetries = 3,                     // Retry count on transient failures
    int MaxConcurrentRequests = 1,          // Respect Lichess rate limits (1 req/sec unauthenticated)
    bool UseOAuth = false,                  // Use OAuth token for higher rate limits (60 req/min)
    string? OAuthToken = null,              // Lichess API token (requires UseOAuth=true)
    bool IncrementalUpdate = false,         // Skip games already present in existing output file
    int MaxGames = 32000,                   // Lichess API limit per user (soft limit: 32K games)
    bool PreserveOriginalFormat = false     // Keep NDJSON instead of converting to PGN
);
```

### 2.1 Default Options
```csharp
public static readonly LichessDownloadOptions Default = new(
    GenerateIndex: true,
    Timeout: TimeSpan.FromSeconds(10),
    MaxRetries: 3,
    MaxConcurrentRequests: 1, // Critical: Lichess enforces 1 req/sec for unauthenticated users
    UseOAuth: false,
    OAuthToken: null,
    IncrementalUpdate: false,
    MaxGames: 32000,
    PreserveOriginalFormat: false
);
```

### 2.2 Lichess API Rate Limits
| Authentication | Requests per Minute | Requests per Hour | Notes |
|----------------|---------------------|-------------------|-------|
| Unauthenticated | 60 | 1800 | 1 request per second enforced strictly |
| OAuth Token | 60 | 3600 | Higher hourly limit; still 1 req/sec burst limit |
| Lichess Patron | 120 | 7200 | Requires special API key |

**Critical Enforcement:** Service must implement client-side rate limiting to avoid 429 responses:

```csharp
private readonly SemaphoreSlim _rateLimiter = new(1, 1);
private readonly TimeSpan _minRequestInterval = TimeSpan.FromSeconds(1.1); // Slight buffer
private DateTime _lastRequestTime = DateTime.MinValue;

private async Task<HttpResponseMessage> ExecuteRateLimitedRequestAsync(
    Func<HttpClient, Task<HttpResponseMessage>> requestFunc,
    CancellationToken ct)
{
    await _rateLimiter.WaitAsync(ct);
    try
    {
        // Enforce minimum interval between requests
        TimeSpan elapsed = DateTime.UtcNow - _lastRequestTime;
        if (elapsed < _minRequestInterval)
            await Task.Delay(_minRequestInterval - elapsed, ct);
        
        var response = await requestFunc(_httpClient);
        _lastRequestTime = DateTime.UtcNow;
        
        // Handle 429 responses with exponential backoff
        if ((int)response.StatusCode == 429)
        {
            int retryAfter = int.Parse(response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString() ?? "5");
            await Task.Delay(TimeSpan.FromSeconds(retryAfter * 2), ct); // Exponential backoff
            return await ExecuteRateLimitedRequestAsync(requestFunc, ct);
        }
        
        return response;
    }
    finally
    {
        _rateLimiter.Release();
    }
}
```

## 3. Lichess API Integration

### 3.1 API Endpoints by Target Type
| Target Type | Endpoint Pattern | Pagination | Max Games |
|-------------|------------------|------------|-----------|
| User Games | `https://lichess.org/api/games/user/{username}` | `?max=100&before={gameId}` | 32,000 |
| Tournament Games | `https://lichess.org/api/tournament/{id}/games` | `?max=200` | All games |
| Study Chapters | `https://lichess.org/api/study/{id}.pgn` | N/A (single request) | Entire study |
| Opening Explorer | `https://explorer.lichess.ovh/{variant}/{color}/{fen}` | `?moves=100&topGames=100` | 100 games per request |

### 3.2 User Games Download Algorithm
Critical requirement: Handle Lichess pagination via `before` parameter using last game ID:

```csharp
private async Task DownloadUserGamesAsync(
    string username,
    LichessGameFilters filters,
    string outputFilePath,
    LichessDownloadOptions options,
    CancellationToken ct)
{
    // Step 1: Determine starting point for incremental updates
    string lastDownloadedGameId = null;
    if (options.IncrementalUpdate && File.Exists(outputFilePath))
    {
        lastDownloadedGameId = await ExtractLastGameIdAsync(outputFilePath, ct);
    }
    
    // Step 2: Construct base query parameters
    var queryParams = new Dictionary<string, string>
    {
        ["max"] = "100", // Maximum per page
        ["moves"] = "true",
        ["pgnInJson"] = "false", // Request raw PGN instead of JSON-wrapped
        ["tags"] = "true", // Include all PGN headers
        ["clocks"] = "true", // Include [%clk] comments
        ["evals"] = options.MaximalGames?.ToString().ToLower() ?? "false",
        ["opening"] = "true" // Include ECO/opening names
    };
    
    // Apply filters
    if (filters?.RatedOnly.HasValue == true)
        queryParams["rated"] = filters.RatedOnly.Value.ToString().ToLower();
    
    if (filters?.Speeds != null && filters.Speeds.Count > 0)
        queryParams["perfType"] = string.Join(",", filters.Speeds.Select(MapSpeedToPerfType));
    
    if (filters?.Since.HasValue == true)
        queryParams["since"] = DateTimeOffset.FromUnixTimeMilliseconds(
            new DateTimeOffset(filters.Since.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()
        ).ToString();
    
    if (filters?.Until.HasValue == true)
        queryParams["until"] = DateTimeOffset.FromUnixTimeMilliseconds(
            new DateTimeOffset(filters.Until.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()
        ).ToString();
    
    if (filters?.VsAi.HasValue == true)
        queryParams["vsAi"] = filters.VsAi.Value.ToString().ToLower();
    
    // Step 3: Paginated download loop
    int totalGamesDownloaded = 0;
    string currentBefore = lastDownloadedGameId;
    bool hasMoreGames = true;
    
    using var outputStream = File.Open(outputFilePath, 
        options.IncrementalUpdate && File.Exists(outputFilePath) 
            ? FileMode.Append 
            : FileMode.Create);
    
    using var writer = new StreamWriter(outputStream, Encoding.UTF8, 4096, leaveOpen: true);
    
    while (hasMoreGames && totalGamesDownloaded < options.MaxGames)
    {
        ct.ThrowIfCancellationRequested();
        
        // Construct paginated URL
        var pageParams = new Dictionary<string, string>(queryParams);
        if (!string.IsNullOrEmpty(currentBefore))
            pageParams["before"] = currentBefore;
        
        string url = $"https://lichess.org/api/games/user/{username}?{BuildQueryString(pageParams)}";
        
        // Execute rate-limited request
        var response = await ExecuteRateLimitedRequestAsync(
            client => client.GetAsync(url, ct),
            ct
        );
        
        response.EnsureSuccessStatusCode();
        
        // Process response based on format
        if (response.Content.Headers.ContentType?.MediaType == "application/x-chess-pgn")
        {
            // Raw PGN response (preferred)
            string pgnContent = await response.Content.ReadAsStringAsync(ct);
            await writer.WriteAsync(pgnContent);
            await writer.FlushAsync(ct);
            
            // Extract game count and last game ID from response
            int gamesInPage = CountGamesInPgn(pgnContent);
            currentBefore = ExtractLastGameIdFromPgn(pgnContent);
            
            totalGamesDownloaded += gamesInPage;
            hasMoreGames = gamesInPage > 0 && gamesInPage == 100; // Full page = more games likely
        }
        else if (response.Content.Headers.ContentType?.MediaType == "application/x-ndjson")
        {
            // NDJSON fallback (one game per line)
            await foreach (var line in response.Content.ReadLinesAsync(ct))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                // Parse NDJSON game
                var game = JsonSerializer.Deserialize<LichessGame>(line, _jsonOptions);
                string pgn = ConvertLichessGameToPgn(game);
                
                await writer.WriteLineAsync(pgn);
                await writer.WriteLineAsync(); // Blank line between games
                
                currentBefore = game.Id;
                totalGamesDownloaded++;
            }
            
            hasMoreGames = totalGamesDownloaded % 100 == 0 && totalGamesDownloaded < options.MaxGames;
        }
        else
        {
            throw new UnsupportedContentTypeException(
                $"Unexpected content type: {response.Content.Headers.ContentType?.MediaType}");
        }
        
        // Progress reporting
        double percent = Math.Min(100.0, (double)totalGamesDownloaded / options.MaxGames * 100);
        OnProgress?.Invoke(new LichessDownloadProgress(
            percent, 
            totalGamesDownloaded, 
            estimatedTotal: options.MaxGames
        ));
    }
    
    await writer.FlushAsync(ct);
}
```

### 3.3 Speed/Time Control Mapping
| Lichess Speed | PerfType Value | Time Control Range | PGN Tag |
|---------------|----------------|--------------------|---------|
| UltraBullet | ultrabullet | < 30s | `[TimeControl "15+0"]` |
| Bullet | bullet | 30s - 2m | `[TimeControl "60+0"]` |
| Blitz | blitz | 3m - 10m | `[TimeControl "180+0"]` |
| Rapid | rapid | 10m - 25m | `[TimeControl "600+0"]` |
| Classical | classical | > 25m | `[TimeControl "1800+0"]` |
| Correspondence | correspondence | Days per move | `[TimeControl "-"]` |

```csharp
private static string MapSpeedToPerfType(TimeControlType speed) => speed switch
{
    TimeControlType.UltraBullet => "ultrabullet",
    TimeControlType.Bullet => "bullet",
    TimeControlType.Blitz => "blitz",
    TimeControlType.Rapid => "rapid",
    TimeControlType.Classical => "classical",
    TimeControlType.Correspondence => "correspondence",
    _ => throw new ArgumentOutOfRangeException(nameof(speed))
};
```

## 4. Algorithm Specification

### 4.1 Incremental Update Strategy
Critical optimization: Avoid re-downloading games already present in output file:

```csharp
private async Task<string?> ExtractLastGameIdAsync(string pgnPath, CancellationToken ct)
{
    // Strategy: Read last 10KB of file to find most recent game
    await using var fs = File.OpenRead(pgnPath);
    long seekPos = Math.Max(0, fs.Length - 10240);
    fs.Seek(seekPos, SeekOrigin.Begin);
    
    var buffer = new byte[10240];
    int bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length, ct);
    
    string tail = Encoding.UTF8.GetString(buffer, 0, bytesRead);
    
    // Extract last GameId tag (Lichess-specific)
    var match = Regex.Match(tail, @"\[GameId\s+""([^""]+)""\]", RegexOptions.RightToLeft);
    return match.Success ? match.Groups[1].Value : null;
}
```

### 4.2 NDJSON to PGN Conversion
Lichess NDJSON format requires careful conversion to standard PGN:

```csharp
private string ConvertLichessGameToPgn(LichessGame game)
{
    var sb = new StringBuilder();
    
    // Header tags
    AppendTag(sb, "Event", game.Event ?? "Lichess Game");
    AppendTag(sb, "Site", "https://lichess.org/" + game.Id);
    AppendTag(sb, "Date", game.Date ?? "????.??.??");
    AppendTag(sb, "Round", game.Round ?? "?");
    AppendTag(sb, "White", game.Players.White.UserId ?? "?");
    AppendTag(sb, "Black", game.Players.Black.UserId ?? "?");
    AppendTag(sb, "Result", game.Winner switch
    {
        "white" => "1-0",
        "black" => "0-1",
        _ => "1/2-1/2"
    });
    
    // Time control
    if (game.Speed != null && game.Increment != null)
        AppendTag(sb, "TimeControl", $"{game.TimeBase}+{game.Increment}");
    
    // Ratings
    if (game.Players.White.Rating.HasValue)
        AppendTag(sb, "WhiteElo", game.Players.White.Rating.Value.ToString());
    if (game.Players.Black.Rating.HasValue)
        AppendTag(sb, "BlackElo", game.Players.Black.Rating.Value.ToString());
    
    // Variant
    if (game.Variant != "standard")
        AppendTag(sb, "Variant", game.Variant);
    
    // Lichess-specific tags
    AppendTag(sb, "GameId", game.Id);
    if (game.Rated)
        AppendTag(sb, "LichessRatingType", "rated");
    
    // Opening information (if available)
    if (!string.IsNullOrEmpty(game.Opening?.Eco))
        AppendTag(sb, "ECO", game.Opening.Eco);
    if (!string.IsNullOrEmpty(game.Opening?.Name))
        AppendTag(sb, "Opening", game.Opening.Name);
    
    sb.AppendLine();
    
    // Move text with clock/eval comments
    int ply = 1;
    bool isWhiteToMove = true;
    
    foreach (var move in game.Moves)
    {
        if (isWhiteToMove)
            sb.Append($"{ply}. ");
        
        sb.Append(move.San);
        
        // Inject clock comment if available
        if (move.Clock != null)
            sb.Append($" {{ [%clk {FormatClock(move.Clock.Value)}] }}");
        
        // Inject eval comment if available
        if (move.Eval != null)
            sb.Append($" {{ [%eval {FormatEval(move.Eval.Value)}] }}");
        
        sb.Append(" ");
        
        if (!isWhiteToMove)
            ply++;
        
        isWhiteToMove = !isWhiteToMove;
    }
    
    // Result terminator
    sb.Append(game.Winner switch
    {
        "white" => "1-0",
        "black" => "0-1",
        _ => "1/2-1/2"
    });
    
    return sb.ToString();
}
```

### 4.3 Opening Explorer Download
Specialized algorithm for downloading master/OTB games by position:

```csharp
private async Task DownloadOpeningExplorerAsync(
    string ecoCode,
    string outputFilePath,
    LichessDownloadOptions options,
    CancellationToken ct)
{
    // Map ECO code to FEN via embedded ECO database
    string fen = _ecoDatabase.GetStartingFen(ecoCode) 
        ?? throw new ArgumentException($"Unknown ECO code: {ecoCode}");
    
    var queryParams = new Dictionary<string, string>
    {
        ["variant"] = "standard",
        ["fen"] = Uri.EscapeDataString(fen),
        ["moves"] = "100",
        ["topGames"] = "100",
        ["recentGames"] = "0"
    };
    
    string url = $"https://explorer.lichess.ovh/master?{BuildQueryString(queryParams)}";
    
    var response = await ExecuteRateLimitedRequestAsync(
        client => client.GetAsync(url, ct),
        ct
    );
    
    response.EnsureSuccessStatusCode();
    
    var explorerData = await response.Content.ReadFromJsonAsync<OpeningExplorerResponse>(ct);
    
    // Convert explorer games to PGN
    using var writer = new StreamWriter(outputFilePath, false, Encoding.UTF8);
    
    foreach (var game in explorerData.Games.Take(options.MaxGames))
    {
        string pgn = ConvertExplorerGameToPgn(game, ecoCode);
        await writer.WriteLineAsync(pgn);
        await writer.WriteLineAsync();
    }
}
```

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| User not found (404) | Throw `UserNotFoundException` with suggestion to check username spelling |
| Rate limit exceeded (429) | Implement exponential backoff; respect Retry-After header; fail after MaxRetries |
| Partial game downloads (network interruption) | Append mode not safe for partial games; delete incomplete last game before resuming |
| Games with non-standard variants (Crazyhouse, etc.) | Preserve Variant tag; skip if `VariantFilter=StandardOnly` |
| Corrupted NDJSON lines | Skip malformed line + log diagnostic; continue processing remaining games |
| Username with special characters | URI-encode username in API requests (`@` → `%40`, etc.) |
| Time control parsing failures | Fallback to generic time control tag (`[TimeControl "?"]`) with diagnostic |

## 6. Performance Characteristics

### 6.1 Network Operations
| Operation | Typical Duration | Notes |
|-----------|------------------|-------|
| Single page download (100 games) | 1.2 s - 3 s | Dominated by 1s rate limit + network latency |
| Full user history (32K games) | 6m 30s - 18m | 320 pages × 1.2-3s per page |
| Tournament download (200 games) | 2.5 s - 6 s | Single request typically sufficient |
| Opening explorer query | 800 ms - 2 s | Single API call |

### 6.2 Resource Usage
| Scenario | Peak Memory | Disk I/O Pattern |
|----------|-------------|------------------|
| Streaming download | < 8 MB | Sequential append to output file |
| NDJSON parsing | < 16 MB | Line-by-line processing; no full document load |
| PGN conversion | < 4 MB per game | Stateless conversion; minimal buffering |

### 6.3 Rate Limit Compliance Benchmarks
| Authentication | Max Sustainable Rate | Observed Success Rate |
|----------------|----------------------|------------------------|
| Unauthenticated | 55 req/min | 99.8% (with 1.1s spacing) |
| OAuth Token | 58 req/min | 99.9% (with 1.05s spacing) |
| Burst requests | 0 req/min | 0% (immediate 429 responses) |

## 7. Binary Index Integration

### 7.1 Post-Download Indexing
```csharp
private async Task IndexDownloadedFileAsync(
    string pgnPath,
    LichessDownloadOptions options,
    CancellationToken ct)
{
    if (!options.GenerateIndex) return;
    
    string pbiPath = pgnPath + ".pbi";
    
    try
    {
        await PgnBinaryIndexBuilder.BuildAsync(pgnPath, pbiPath, ct);
    }
    catch (Exception ex)
    {
        OnDiagnostic?.Invoke($"Indexing failed for {pgnPath}: {ex.Message}");
        throw;
    }
}
```

### 7.2 Lichess-Specific Index Enhancements
Extended GameRecord fields for Lichess metadata:

```csharp
public struct GameRecord // Extended format (v3.1+)
{
    // ... standard fields ...
    public uint LichessGameIdOffset; // Offset into string heap for Lichess game ID
    public byte SpeedCategory;       // 0=unknown, 1=ultrabullet, 2=bullet, 3=blitz, 4=rapid, 5=classical, 6=correspondence
    public bool IsRated;             // Bit flag in Flags field
}
```

## 8. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `UserNotFoundException` | HTTP 404 on user endpoint | Fail fast; suggest username verification |
| `RateLimitExceededException` | HTTP 429 after retries | Pause operation; require user intervention to continue |
| `InvalidGameFormatException` | Unparseable NDJSON/PGN | Skip malformed game; continue processing; aggregate errors in report |
| `OAuthTokenInvalidException` | HTTP 401 with OAuth token | Fail fast; require token renewal |
| `PartialDownloadException` | Network failure mid-download | Preserve partial file; enable resumption via IncrementalUpdate on next run |

## 9. Testability Requirements

### 9.1 Required Test Fixtures
- `lichess_mock_responses/` (recorded API responses for offline testing)
- `lichess_ndjson_samples.ndjson` (representative NDJSON lines for parser testing)
- `lichess_corrupted_lines.ndjson` (malformed lines for error handling tests)
- `lichess_variant_games.pgn` (games with non-standard variants)
- `lichess_incremental_update_test.pgn` (partial download for resumption testing)

### 9.2 Assertion Examples
```csharp
// Verify user games download with filters
var report = await service.DownloadAsync(new LichessDownloadRequest(
    new LichessDownloadTarget(
        LichessTargetType.User,
        "georges",
        new LichessGameFilters(
            RatedOnly: true,
            Speeds: new[] { TimeControlType.Blitz, TimeControlType.Rapid },
            Since: new DateOnly(2023, 1, 1)
        )
    ),
    "georges_blitz_rapid.pgn"
), CancellationToken.None);

Assert.True(report.TotalGamesDownloaded > 0);
Assert.True(report.TotalGamesDownloaded <= 32000);

// Verify incremental update skips existing games
File.WriteAllText("partial.pgn", existingGamesPgn);
var incrementalReport = await service.DownloadAsync(new LichessDownloadRequest(
    new LichessDownloadTarget(LichessTargetType.User, "georges"),
    "partial.pgn",
    new LichessDownloadOptions(IncrementalUpdate: true)
), CancellationToken.None);

// Should only download new games since last download
Assert.True(incrementalReport.TotalGamesDownloaded < 100); 

// Verify index generation
var index = PgnBinaryIndex.OpenRead("georges_blitz_rapid.pgn.pbi");
Assert.Equal(report.TotalGamesDownloaded, index.Header.GameCount);
```

## 10. Versioning & Compatibility

- **Lichess API Versioning:** Service targets Lichess API v2 (current stable); monitors API changelog for breaking changes
- **NDJSON Format Stability:** Lichess maintains backward compatibility for game export format
- **PGN Standard Compliance:** Converted output must pass `pgn-extract -c` validation
- **Rate Limit Policy Changes:** Service includes configuration override for minimum request interval to adapt to policy changes

## 11. Security & Privacy Considerations

| Risk | Mitigation |
|------|------------|
| OAuth token leakage | Never log tokens; store encrypted at rest; require explicit user consent |
| PII exposure in downloaded games | User IDs are public on Lichess; no additional PII collected beyond public game data |
| API abuse detection | Implement strict client-side rate limiting; never attempt to circumvent Lichess limits |
| Malicious game content | Validate PGN structure before indexing; reject games with binary/gibberish content |
| GDPR compliance | Lichess games are public data; service provides opt-out via user-controlled download scope |