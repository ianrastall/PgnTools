# Chesscom Monthly Downloader — Deep Dive

## Overview

The **Chesscom Monthly Downloader** is a .NET console application within the PgnTools solution that bulk-downloads PGN (Portable Game Notation) game archives from Chess.com's public API for a given player, organized by month.

---

## Architecture

```
ChesscomMonthlyDownloader/
├── Program.cs                  # Entry point & orchestration
├── ChesscomMonthlyDownloader.csproj  # Project file with dependencies
└── (references shared projects/packages in solution)
```

The tool is part of the larger **PgnTools** solution, which contains multiple chess-related utilities sharing common infrastructure.

---

## How It Works — Under the Hood

### 1. Entry Point (`Program.cs`)

The application starts in `Program.cs`. It:

1. **Parses configuration/arguments** — Determines which Chess.com username to target, and optionally a date range (year/month) to constrain downloads.
2. **Queries the Chess.com Archives endpoint** — Calls `https://api.chess.com/pub/player/{username}/games/archives` which returns a JSON array of monthly archive URLs the player has games in.
3. **Iterates over each monthly archive** — For each URL in the archives list (formatted as `https://api.chess.com/pub/player/{username}/games/{YYYY}/{MM}/pgn`), it downloads the PGN content.
4. **Writes PGN files to disk** — Each month's games are saved as a separate `.pgn` file, typically named with the year-month pattern (e.g., `username_2024_01.pgn`).

### 2. Chess.com Public API

The tool relies on Chess.com's **free, unauthenticated public API**:

| Endpoint | Purpose |
|----------|---------|
| `GET /pub/player/{username}/games/archives` | Returns JSON list of all monthly archive URLs |
| `GET /pub/player/{username}/games/{YYYY}/{MM}/pgn` | Returns raw PGN text of all games for that month |
| `GET /pub/player/{username}/games/{YYYY}/{MM}` | Returns JSON with detailed game objects (not typically used here) |

**Key API behaviors:**
- No authentication required for public game data
- Rate limiting applies (~100 requests/minute as of 2024; Chess.com asks for a `User-Agent` header)
- The `/pgn` suffix returns plain-text PGN, not JSON
- Archives list is sorted chronologically ascending

### 3. HTTP Client Usage

The application uses `HttpClient` to make requests. Important details:

- A custom `User-Agent` header should be set (Chess.com may block requests without one)
- The tool respects HTTP status codes — a `429 Too Many Requests` or `404 Not Found` should be handled gracefully
- PGN responses can be **large** (several MB for active players in a busy month), so streaming or buffered reads are used
- The `HttpClient` instance is typically reused (not disposed per-request) to avoid socket exhaustion

### 4. PGN File Structure

Each downloaded monthly PGN file contains concatenated games in standard PGN format:

```pgn
[Event "Live Chess"]
[Site "Chess.com"]
[Date "2024.01.15"]
[Round "-"]
[White "player1"]
[Black "player2"]
[Result "1-0"]
[WhiteElo "1500"]
[BlackElo "1450"]
[TimeControl "600"]
[Termination "player1 won by resignation"]

1. e4 e5 2. Nf3 Nc6 3. Bb5 a6 ... 1-0

[Event "Live Chess"]
...
```

Games are separated by blank lines. Each game has:
- **Tag pairs** (headers in `[Key "Value"]` format)
- **Movetext** (the actual moves in SAN notation)
- **Result token** (`1-0`, `0-1`, `1/2-1/2`, or `*`)

### 5. File Output Strategy

The downloader typically:
- Creates an output directory if it doesn't exist
- Names files predictably: `{username}_{YYYY}_{MM}.pgn`
- May skip months that already have files on disk (incremental download)
- Optionally merges all months into a single combined PGN file

### 6. Error Handling

| Scenario | Behavior |
|----------|----------|
| Player not found (404) | Logs error, exits gracefully |
| Month has no games | Skips (empty response or 404) |
| Network timeout | Retries with backoff or logs and continues |
| Rate limited (429) | Waits and retries |
| Disk write failure | Throws with descriptive message |

### 7. Dependencies & Shared Code

The project references shared code from the PgnTools solution:

- **PGN parsing utilities** — Shared libraries for reading/validating PGN format
- **HTTP helpers** — Common `HttpClient` configuration, retry policies
- **Configuration** — Shared settings patterns (appsettings, command-line args)
- **NuGet packages** — Likely includes `System.Text.Json` for API response parsing, possibly `Polly` for retry policies

### 8. Project File (`.csproj`)

The project targets a modern .NET version and declares:
- Target framework (e.g., `net8.0`)
- Output type: `Exe`
- Project references to shared libraries in the solution
- NuGet package references

---

## Data Flow Diagram

```
┌──────────────┐     HTTP GET /archives      ┌─────────────────┐
│   Program.cs │ ──────────────────────────►  │  Chess.com API  │
│              │ ◄──────────────────────────  │                 │
│              │     JSON: [url1, url2, ...]  │                 │
│              │                              │                 │
│  for each    │     HTTP GET /{Y}/{M}/pgn    │                 │
│  archive URL │ ──────────────────────────►  │                 │
│              │ ◄──────────────────────────  │                 │
│              │     Raw PGN text             │                 │
└──────┬───────┘                              └─────────────────┘
       │
       │  Write to disk
       ▼
┌──────────────┐
│  Output Dir  │
│  *.pgn files │
└──────────────┘
```

---

## Usage

```bash
# Typical invocation
dotnet run --project ChesscomMonthlyDownloader -- --username <chess.com-username>

# Or after building
ChesscomMonthlyDownloader.exe --username <chess.com-username> --output ./pgn-output
```

---

## Performance Considerations

- **Sequential downloads**: Months are typically downloaded one at a time to respect rate limits
- **File size**: Prolific players may have 10,000+ games per month; each PGN file could be 5-10 MB
- **Total volume**: A player active since 2010 could have 150+ monthly archives
- **Disk I/O**: Writing is fast relative to network; the bottleneck is always the API

---

## Chess.com API Quirks

1. **No pagination on monthly PGN** — You get ALL games for a month in one response
2. **PGN endpoint vs JSON endpoint** — The `/pgn` suffix gives raw text; without it you get JSON with additional metadata (accuracy scores, ECO codes, etc.)
3. **Archives can have gaps** — If a player didn't play in a month, that month won't appear in the archives list
4. **Time controls are embedded** — The `[TimeControl]` tag uses Chess.com's format (e.g., `"600"` for 10-minute, `"180+2"` for 3+2)
5. **Variants included** — Chess960, Crazyhouse, etc. are all in the same monthly archive unless filtered client-side
