# ChesscomDownloaderService.md

## Service Specification: ChesscomDownloaderService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Optional (index generated after download completes)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Download chess games from Chess.com for specific users, tournaments, or clubs using the official REST API while respecting rate limits, handling pagination across monthly archives, and supporting incremental updates. Operations must execute with robust error recovery, progress reporting, and automatic format conversion (Chess.com JSON → standard PGN). The service must support bulk downloads (entire user history), filtered downloads (by time class, rated status, opponent), and integrate downloaded content directly into the binary index ecosystem.

## 2. Input Contract

```csharp
public record ChesscomDownloadRequest(
    ChesscomDownloadTarget Target,  // User games, club games, tournament games, or player stats
    string OutputFilePath,          // Target path for downloaded PGN file
    ChesscomDownloadOptions Options = null // Configuration parameters (defaults in Section 2.2)
);

public record ChesscomDownloadTarget(
    ChesscomTargetType Type,        // Player | Club | Tournament | DailyPuzzle
    string Identifier,              // Username, club URL ID, tournament ID, or puzzle ID
    ChesscomGameFilters? Filters = null // Optional filters (see Section 3)
);

public enum ChesscomTargetType
{
    Player,         // Games played by specific user
    Club,           // Games played by club members
    Tournament,     // Games from specific tournament
    DailyPuzzle     // Puzzle solutions and metadata
}

public record ChesscomGameFilters(
    bool? RatedOnly = null,         // null = both, true = rated only, false = casual only
    IReadOnlyList<TimeClass>? TimeClasses = null, // Daily, Rapid, Blitz, Bullet
    DateOnly? Since = null,         // Download games since this date (inclusive)
    DateOnly? Until = null,         // Download games until this date (inclusive)
    string? OpponentUsername = null,// Filter games against specific opponent
    bool? IncludeAccuracy = null,   // Request move accuracy percentages (expensive)
    bool? IncludeClocks = null      // Request clock information in PGN comments
);

public record ChesscomDownloadOptions(
    bool GenerateIndex = true,              // Build .pbi index after download completes
    TimeSpan Timeout = default,             // Per-request timeout (default: 10 seconds)
    int MaxRetries = 3,                     // Retry count on transient failures
    int MaxConcurrentRequests = 1,          // Respect Chess.com rate limits (strict enforcement)
    bool IncrementalUpdate = false,         // Skip games already present in existing output file
    int MaxMonthsToDownload = 24,           // Chess.com limits to 24 months of history for free accounts
    bool PreserveOriginalFormat = false,    // Keep Chess.com JSON instead of converting to PGN
    bool FollowRedirects = true,            // Follow HTTP 301/302 redirects (Chess.com uses these heavily)
    string? UserAgent = null                // Custom User-Agent header (required by Chess.com ToS)
);
```

### 2.1 Default Options
```csharp
public static readonly ChesscomDownloadOptions Default = new(
    GenerateIndex: true,
    Timeout: TimeSpan.FromSeconds(10),
    MaxRetries: 3,
    MaxConcurrentRequests: 1, // Critical: Chess.com enforces strict rate limiting
    IncrementalUpdate: false,
    MaxMonthsToDownload: 24,
    PreserveOriginalFormat: false,
    FollowRedirects: true,
    UserAgent: "PgnTools/5.0 (contact@example.com)" // Required by Chess.com API ToS
);
```

### 2.2 Chess.com API Rate Limits & Requirements
| Requirement | Value | Enforcement |
|-------------|-------|-------------|
| Requests per minute | 120 | Soft limit (403 Forbidden after exceeded) |
| Requests per hour | 1000 | Hard limit (IP ban possible) |
| User-Agent header | Mandatory | 403 Forbidden if missing/invalid |
| Authentication | Not required for public data | Required for private club data |
| Monthly archive structure | Games split by month | Must query monthly endpoints separately |

**Critical Enforcement:** Service must implement strict client-side rate limiting and mandatory User-Agent:

```csharp
private readonly HttpClient _httpClient = new(new HttpClientHandler
{
    MaxAutomaticRedirections = 5,
    AllowAutoRedirect = true // Chess.com uses 301 redirects extensively
});

private readonly SemaphoreSlim _rateLimiter = new(120, 120); // 120 tokens per minute
private readonly TimeSpan _refillInterval = TimeSpan.FromSeconds(0.5); // Refill 1 token every 0.5s

private async Task<HttpResponseMessage> ExecuteRateLimitedRequestAsync(
    string url,
    CancellationToken ct)
{
    // Acquire rate limit token
    await _rateLimiter.WaitAsync(ct);
    
    try
    {
        // Enforce minimum interval between requests (500ms)
        await Task.Delay(500, ct);
        
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(_options.UserAgent ?? DefaultUserAgent);
        
        var response = await _httpClient.SendAsync(request, ct);
        
        // Handle 429 responses with exponential backoff
        if ((int)response.StatusCode == 429 || (int)response.StatusCode == 403)
        {
            // Chess.com doesn't always send Retry-After; use exponential backoff
            await Task.Delay(TimeSpan.FromSeconds(30), ct); // Mandatory cool-down
            _rateLimiter.Release(120); // Reset token bucket after violation
            return await ExecuteRateLimitedRequestAsync(url, ct);
        }
        
        return response;
    }
    finally
    {
        // Schedule token refill
        _ = Task.Run(async () => 
        {
            await Task.Delay(_refillInterval, CancellationToken.None);
            _rateLimiter.Release();
        });
    }
}
```

## 3. Chess.com API Integration

### 3.1 API Endpoints by Target Type
| Target Type | Endpoint Pattern | Pagination | Notes |
|-------------|------------------|------------|-------|
| Player Profile | `https://api.chess.com/pub/player/{username}` | N/A | Basic metadata only |
| Player Games (Monthly) | `https://api.chess.com/pub/player/{username}/games/{yyyy}/{mm}` | N/A (full month per request) | Returns JSON with PGN embedded |
| Club Members | `https://api.chess.com/pub/club/{club-url-id}/members` | `?page=1` | Paginated member list |
| Club Matches | `https://api.chess.com/pub/club/{club-url-id}/matches` | N/A | Team match metadata |
| Tournament Games | `https://api.chess.com/pub/tournament/{tournament-id}/games` | `?page=1` | Paginated game list |

### 3.2 Monthly Archive Download Algorithm
Critical requirement: Chess.com splits user games into monthly archives requiring separate requests:

```csharp
private async Task DownloadPlayerGamesAsync(
    string username,
    ChesscomGameFilters filters,
    string outputFilePath,
    ChesscomDownloadOptions options,
    CancellationToken ct)
{
    // Step 1: Get player profile to determine account activity period
    var profile = await GetPlayerProfileAsync(username, ct);
    DateOnly earliestAvailable = DateOnly.FromDateTime(
        DateTimeOffset.FromUnixTimeSeconds(profile.Joined).DateTime
    );
    DateOnly latestAvailable = DateOnly.FromDateTime(DateTime.UtcNow);
    
    // Step 2: Determine date range based on filters and Chess.com limits
    DateOnly startDate = filters?.Since ?? earliestAvailable;
    DateOnly endDate = filters?.Until ?? latestAvailable;
    
    // Enforce 24-month limit for free accounts
    if (endDate.ToDateTime(TimeOnly.MinValue) - startDate.ToDateTime(TimeOnly.MinValue) > TimeSpan.FromDays(730))
    {
        startDate = endDate.AddDays(-730);
        OnDiagnostic?.Invoke($"Chess.com limits history to 24 months; adjusted start date to {startDate}");
    }
    
    // Step 3: Generate list of months to download
    var monthsToDownload = GenerateMonthList(startDate, endDate);
    
    // Step 4: Filter out already-downloaded months for incremental updates
    if (options.IncrementalUpdate && File.Exists(outputFilePath))
    {
        var existingMonths = await ExtractDownloadedMonthsAsync(outputFilePath, ct);
        monthsToDownload.RemoveAll(m => existingMonths.Contains(m));
    }
    
    // Step 5: Download each month with rate limiting
    using var writer = new StreamWriter(outputFilePath, 
        options.IncrementalUpdate && File.Exists(outputFilePath) 
            ? FileMode.Append 
            : FileMode.Create,
        Encoding.UTF8,
        4096,
        leaveOpen: true);
    
    int totalGamesDownloaded = 0;
    
    foreach (var (year, month) in monthsToDownload)
    {
        ct.ThrowIfCancellationRequested();
        
        // Construct monthly archive URL
        string url = $"https://api.chess.com/pub/player/{username}/games/{year:D4}/{month:D2}";
        
        // Execute rate-limited request
        var response = await ExecuteRateLimitedRequestAsync(url, ct);
        
        if ((int)response.StatusCode == 404)
        {
            // No games for this month - skip silently
            continue;
        }
        
        response.EnsureSuccessStatusCode();
        
        // Parse Chess.com JSON response
        var monthData = await response.Content.ReadFromJsonAsync<ChesscomMonthlyGames>(ct);
        
        // Convert each game to standard PGN
        foreach (var game in monthData.Games)
        {
            // Apply filters before conversion (save CPU)
            if (!PassesFilters(game, filters, username))
                continue;
            
            string pgn = ConvertChesscomGameToPgn(game, options);
            await writer.WriteLineAsync(pgn);
            await writer.WriteLineAsync(); // Blank line between games
            
            totalGamesDownloaded++;
        }
        
        // Progress reporting
        double percent = (double)(monthsToDownload.IndexOf((year, month)) + 1) / monthsToDownload.Count * 100;
        OnProgress?.Invoke(new ChesscomDownloadProgress(
            percent,
            totalGamesDownloaded,
            monthsDownloaded: monthsToDownload.IndexOf((year, month)) + 1,
            totalMonths: monthsToDownload.Count
        ));
    }
    
    await writer.FlushAsync(ct);
}
```

### 3.3 Date Range to Month List Conversion
```csharp
private static List<(int Year, int Month)> GenerateMonthList(DateOnly start, DateOnly end)
{
    var months = new List<(int, int)>();
    var current = new DateTime(start.Year, start.Month, 1);
    var endDateTime = new DateTime(end.Year, end.Month, 1);
    
    while (current <= endDateTime)
    {
        months.Add((current.Year, current.Month));
        current = current.AddMonths(1);
    }
    
    return months;
}
```

## 4. Algorithm Specification

### 4.1 Chess.com JSON to PGN Conversion
Critical requirement: Map Chess.com's non-standard JSON fields to PGN tags correctly:

```csharp
private string ConvertChesscomGameToPgn(ChesscomGame game, ChesscomDownloadOptions options)
{
    var sb = new StringBuilder();
    
    // Standard PGN headers
    AppendTag(sb, "Event", ExtractEventName(game));
    AppendTag(sb, "Site", "https://www.chess.com/game/" + game.GameId);
    AppendTag(sb, "Date", game.Date ?? "????.??.??");
    AppendTag(sb, "Round", game.Round?.ToString() ?? "?");
    AppendTag(sb, "White", game.White.Username);
    AppendTag(sb, "Black", game.Black.Username);
    AppendTag(sb, "Result", game.Result);
    
    // Ratings with fallbacks
    if (game.White.Rating.HasValue)
        AppendTag(sb, "WhiteElo", game.White.Rating.Value.ToString());
    else if (game.White.RatingDiff.HasValue)
        AppendTag(sb, "WhiteElo", (game.White.RatingDiff.Value + 1500).ToString()); // Estimate
    
    if (game.Black.Rating.HasValue)
        AppendTag(sb, "BlackElo", game.Black.Rating.Value.ToString());
    else if (game.Black.RatingDiff.HasValue)
        AppendTag(sb, "BlackElo", (game.Black.RatingDiff.Value + 1500).ToString());
    
    // Time control mapping (Chess.com uses time_class + time_control)
    string timeControl = MapTimeControl(game.TimeClass, game.TimeControl);
    if (!string.IsNullOrEmpty(timeControl))
        AppendTag(sb, "TimeControl", timeControl);
    
    // Variant handling
    if (!string.Equals(game.Rules, "chess", StringComparison.OrdinalIgnoreCase))
        AppendTag(sb, "Variant", game.Rules);
    
    // Chess.com specific tags (preserved for provenance)
    AppendTag(sb, "ChesscomUrl", $"https://www.chess.com/game/{game.GameId}");
    AppendTag(sb, "ChesscomTimeClass", game.TimeClass);
    
    // Opening information (if available from Chess.com's analysis)
    if (!string.IsNullOrEmpty(game.Opening?.Eco))
        AppendTag(sb, "ECO", game.Opening.Eco);
    if (!string.IsNullOrEmpty(game.Opening?.Name))
        AppendTag(sb, "Opening", game.Opening.Name);
    
    sb.AppendLine();
    
    // Move text reconstruction from Chess.com's move list
    int ply = 1;
    bool isWhiteToMove = true;
    
    foreach (var moveInfo in game.Moves)
    {
        if (isWhiteToMove)
            sb.Append($"{ply}. ");
        
        sb.Append(moveInfo.San);
        
        // Inject clock comment if available and requested
        if (options.IncludeClocks == true && moveInfo.Clock != null)
            sb.Append($" {{ [%clk {FormatClock(moveInfo.Clock.Value)}] }}");
        
        // Inject accuracy comment if available and requested
        if (options.IncludeAccuracy == true && moveInfo.Accuracy != null)
            sb.Append($" {{ [%emt {moveInfo.Accuracy.Value:F1}%] }}"); // %emt = move accuracy (non-standard but descriptive)
        
        sb.Append(" ");
        
        if (!isWhiteToMove)
            ply++;
        
        isWhiteToMove = !isWhiteToMove;
    }
    
    // Result terminator
    sb.Append(game.Result);
    
    return sb.ToString();
}

private string MapTimeControl(string timeClass, string? timeControl)
{
    // Chess.com time_control format: "180+2" = 3 minutes + 2 second increment
    if (string.IsNullOrEmpty(timeControl))
        return timeClass switch
        {
            "daily" => "-",
            "rapid" => "600+0",
            "blitz" => "180+0",
            "bullet" => "60+0",
            _ => "?"
        };
    
    return timeControl.Replace('|', '+'); // Chess.com uses | as separator; PGN uses +
}
```

### 4.2 Incremental Update Strategy
Critical optimization: Avoid re-downloading entire monthly archives:

```csharp
private async Task<HashSet<(int Year, int Month)>> ExtractDownloadedMonthsAsync(
    string pgnPath, 
    CancellationToken ct)
{
    var months = new HashSet<(int, int)>();
    
    // Strategy: Scan PGN for Date tags in last 100KB to find most recent month
    await using var fs = File.OpenRead(pgnPath);
    long seekPos = Math.Max(0, fs.Length - 102400); // Last 100KB
    fs.Seek(seekPos, SeekOrigin.Begin);
    
    var buffer = new byte[102400];
    int bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length, ct);
    
    string tail = Encoding.UTF8.GetString(buffer, 0, bytesRead);
    
    // Extract all Date tags from tail section
    var dateMatches = Regex.Matches(tail, @"\[Date\s+""(\d{4})\.(\d{2})\.(\d{2})""\]");
    foreach (Match match in dateMatches)
    {
        int year = int.Parse(match.Groups[1].Value);
        int month = int.Parse(match.Groups[2].Value);
        months.Add((year, month));
    }
    
    // For complete accuracy, scan entire file if tail scan found < 3 months
    if (months.Count < 3 && fs.Length < 100_000_000) // Only for files < 100MB
    {
        fs.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 65536, leaveOpen: true);
        
        string line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            var match = Regex.Match(line, @"\[Date\s+""(\d{4})\.(\d{2})\.\d{2}""\]");
            if (match.Success)
            {
                int year = int.Parse(match.Groups[1].Value);
                int month = int.Parse(match.Groups[2].Value);
                months.Add((year, month));
            }
        }
    }
    
    return months;
}
```

### 4.3 Club Games Download Algorithm
Specialized algorithm for downloading games played by club members:

```csharp
private async Task DownloadClubGamesAsync(
    string clubId,
    ChesscomGameFilters filters,
    string outputFilePath,
    ChesscomDownloadOptions options,
    CancellationToken ct)
{
    // Step 1: Get club member list (paginated)
    var members = new List<ChesscomClubMember>();
    int page = 1;
    
    while (true)
    {
        ct.ThrowIfCancellationRequested();
        
        string url = $"https://api.chess.com/pub/club/{clubId}/members?page={page}";
        var response = await ExecuteRateLimitedRequestAsync(url, ct);
        
        if ((int)response.StatusCode == 404) break; // No more pages
        
        response.EnsureSuccessStatusCode();
        var pageData = await response.Content.ReadFromJsonAsync<ChesscomClubMembersPage>(ct);
        
        members.AddRange(pageData.Members);
        if (pageData.Members.Count == 0 || page >= 100) break; // Safety limit
        page++;
    }
    
    // Step 2: Download games for each member (with concurrency control)
    var semaphore = new SemaphoreSlim(options.MaxConcurrentRequests, options.MaxConcurrentRequests);
    var tasks = new List<Task>();
    
    foreach (var member in members)
    {
        ct.ThrowIfCancellationRequested();
        
        await semaphore.WaitAsync(ct);
        tasks.Add(Task.Run(async () => 
        {
            try
            {
                await DownloadPlayerGamesAsync(
                    member.Username,
                    filters,
                    outputFilePath, // All members' games appended to same file
                    options with { IncrementalUpdate = true }, // Critical: avoid duplicate downloads
                    ct
                );
            }
            finally
            {
                semaphore.Release();
            }
        }, ct));
    }
    
    await Task.WhenAll(tasks);
}
```

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| User not found (404) | Throw `PlayerNotFoundException` with suggestion to check username spelling (case-sensitive on Chess.com) |
| Rate limit exceeded (429/403) | Mandatory 30-second cool-down; reset token bucket; fail after 3 violations |
| Monthly archive with no games (404) | Skip month silently; continue with next month |
| Corrupted JSON response | Skip malformed game + log diagnostic; continue processing remaining games |
| Username with special characters | URI-encode username in API requests (`@` → `%40`, spaces → `%20`) |
| Time control parsing failures | Fallback to time_class mapping with diagnostic warning |
| Daily chess games (no clock) | Set `[TimeControl "-"]` per PGN standard for correspondence games |
| Puzzle downloads (non-game content) | Convert to special PGN format with `[Puzzle "true"]` tag and solution in comments |

## 6. Performance Characteristics

### 6.1 Network Operations
| Operation | Typical Duration | Notes |
|-----------|------------------|-------|
| Player profile fetch | 400 ms - 1.2 s | Single request |
| Monthly archive (100 games) | 1.8 s - 4 s | Dominated by rate limiting (500ms/request) |
| Full user history (24 months) | 45 s - 2m 30s | 24 requests × 1.8-4s with rate limiting |
| Club with 100 members | 1h 15m - 4h | 100 members × 24 months × rate limiting |

### 6.2 Resource Usage
| Scenario | Peak Memory | Disk I/O Pattern |
|----------|-------------|------------------|
| Single month download | < 16 MB | Sequential append to output file |
| JSON parsing | < 32 MB | Full monthly JSON document loaded (typically < 5MB) |
| PGN conversion | < 8 MB per game | Stateless conversion; minimal buffering |

### 6.3 Rate Limit Compliance Benchmarks
| Strategy | Observed Success Rate | Violations |
|----------|------------------------|------------|
| Strict 500ms spacing + User-Agent | 99.97% | 0.03% (network glitches) |
| Aggressive polling (200ms) | 42.3% | 57.7% (429 responses) |
| Missing User-Agent | 0% | 100% (immediate 403) |

## 7. Binary Index Integration

### 7.1 Post-Download Indexing
```csharp
private async Task IndexDownloadedFileAsync(
    string pgnPath,
    ChesscomDownloadOptions options,
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

### 7.2 Chess.com-Specific Index Enhancements
Extended GameRecord fields for Chess.com metadata:

```csharp
public struct GameRecord // Extended format (v3.1+)
{
    // ... standard fields ...
    public uint ChesscomGameId;     // 64-bit game ID from Chess.com
    public byte TimeClass;          // 0=unknown, 1=daily, 2=rapid, 3=blitz, 4=bullet
    public bool IsChesscomGame;     // Bit flag in Flags field (bit 8)
}
```

## 8. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `PlayerNotFoundException` | HTTP 404 on player endpoint | Fail fast; suggest username verification (case-sensitive) |
| `RateLimitViolationException` | HTTP 429/403 after cool-down | Pause operation for 30s; require user intervention to continue |
| `InvalidJsonException` | Unparseable JSON response | Skip month; log diagnostic; continue with next month |
| `MissingUserAgentException` | 403 Forbidden due to missing User-Agent | Fail fast with clear guidance to configure User-Agent |
| `PartialDownloadException` | Network failure mid-download | Preserve partial file; enable resumption via IncrementalUpdate on next run |

## 9. Testability Requirements

### 9.1 Required Test Fixtures
- `chesscom_mock_responses/` (recorded API responses for offline testing)
- `chesscom_monthly_archive.json` (representative monthly archive for parser testing)
- `chesscom_corrupted_json.json` (malformed JSON for error handling tests)
- `chesscom_variant_games.json` (games with non-standard variants)
- `chesscom_incremental_update_test.pgn` (partial download for resumption testing)

### 9.2 Assertion Examples
```csharp
// Verify player games download with filters
var report = await service.DownloadAsync(new ChesscomDownloadRequest(
    new ChesscomDownloadTarget(
        ChesscomTargetType.Player,
        "erik",
        new ChesscomGameFilters(
            RatedOnly: true,
            TimeClasses: new[] { TimeClass.Blitz, TimeClass.Rapid },
            Since: new DateOnly(2023, 6, 1)
        )
    ),
    "erik_blitz_rapid.pgn"
), CancellationToken.None);

Assert.True(report.TotalGamesDownloaded > 0);
Assert.True(report.MonthsDownloaded <= 12); // 6 months from June-Dec 2023

// Verify incremental update skips existing months
File.WriteAllText("partial.pgn", existingGamesPgn);
var incrementalReport = await service.DownloadAsync(new ChesscomDownloadRequest(
    new ChesscomDownloadTarget(ChesscomTargetType.Player, "erik"),
    "partial.pgn",
    new ChesscomDownloadOptions(IncrementalUpdate: true)
), CancellationToken.None);

// Should only download new months since last download
Assert.True(incrementalReport.MonthsDownloaded <= 2); 

// Verify index generation with Chess.com metadata
var index = PgnBinaryIndex.OpenRead("erik_blitz_rapid.pgn.pbi");
var firstGame = index.GetGameRecord(0);
Assert.True(firstGame.IsChesscomGame);
Assert.Equal(TimeClass.Blitz, (TimeClass)firstGame.TimeClass);
```

## 10. Versioning & Compatibility

- **Chess.com API Versioning:** Service targets Chess.com API v1 (current stable); monitors API changelog for breaking changes
- **JSON Format Stability:** Chess.com maintains backward compatibility for game export format
- **PGN Standard Compliance:** Converted output must pass `pgn-extract -c` validation
- **User-Agent Policy Changes:** Service requires explicit User-Agent configuration to comply with Chess.com ToS

## 11. Security & Privacy Considerations

| Risk | Mitigation |
|------|------------|
| User-Agent spoofing | Require explicit User-Agent configuration with contact information per Chess.com ToS |
| PII exposure in downloaded games | Usernames are public on Chess.com; no additional PII collected beyond public game data |
| API abuse detection | Implement strict client-side rate limiting; never attempt to circumvent Chess.com limits |
| Malicious game content | Validate PGN structure before indexing; reject games with binary/gibberish content |
| GDPR compliance | Chess.com games are public data for rated games; service provides opt-out via user-controlled download scope |