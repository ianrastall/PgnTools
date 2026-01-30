# Lc0 Match Downloader

A comprehensive Python tool for downloading, processing, and organizing **Leela Chess Zero (Lc0)** match data from the distributed training infrastructure.

## Features

✅ **Web Scraping** - Automatically scrapes match metadata from `training.lczero.org`  
✅ **Smart Downloading** - Downloads PGN archives from `storage.lczero.org` open directory  
✅ **Date Injection** - Adds match dates to each game in the PGN database  
✅ **Player Normalization** - Standardizes player names to `Lc0 [version]` format  
✅ **Version Detection** - Automatically determines Lc0 version based on match date  
✅ **Monthly Organization** - Organizes processed games by year/month directory structure  
✅ **Archive Handling** - Handles both `.tar.gz` archives and individual `.pgn` files  
✅ **SQLite Tracking** - Maintains local database for download progress and status  
✅ **Concurrent Operations** - Async I/O for efficient scraping and downloading  
✅ **Robust Error Handling** - Retry logic, validation, and comprehensive logging

## Architecture

The downloader operates in three distinct phases:

### Phase 1: Scraping (`--mode scrape`)
- Scrapes all match pages from `training.lczero.org/matches/`
- Extracts match IDs, dates, and PGN filenames
- Saves to CSV and SQLite database
- Concurrent requests with configurable semaphore

### Phase 2: Downloading (`--mode download`)
- Fetches PGN archives from `storage.lczero.org/files/match_pgns/`
- Tries multiple URL patterns to locate files
- Validates downloaded content (not HTML error pages)
- Tracks status: pending, success, failed, retry
- Respects rate limits with connection pooling

### Phase 3: Processing (`--mode process`)
- Extracts PGNs from `.tar.gz` archives
- Injects `[Date "YYYY-MM-DD"]` header
- Normalizes `[White]` and `[Black]` to `Lc0 vX.XX.X`
- Organizes into monthly files: `processed/YYYY/MM/lc0_matches_YYYY_MM.pgn`
- Preserves original game metadata

## Installation

### Prerequisites
- Python 3.8 or higher
- Internet connection

### Setup

```bash
# Clone or download the repository
git clone <repository_url>
cd lc0-match-downloader

# Create virtual environment (recommended)
python -m venv venv
source venv/bin/activate  # On Windows: venv\Scripts\activate

# Install dependencies
pip install -r requirements.txt
```

## Usage

### Quick Start

```bash
# Download everything (scrape + download + process)
python lc0_match_downloader.py --mode all

# Or run phases separately:

# 1. Scrape match metadata
python lc0_match_downloader.py --mode scrape

# 2. Download PGN files (all pending)
python lc0_match_downloader.py --mode download

# 3. Process downloaded files
python lc0_match_downloader.py --mode process
```

### Advanced Usage

```bash
# Download only first 100 matches (for testing)
python lc0_match_downloader.py --mode download --limit 100

# View statistics
python lc0_match_downloader.py --mode stats

# Resume interrupted download
python lc0_match_downloader.py --mode download
# (Automatically skips already downloaded matches)
```

## Configuration

Edit the `Config` class in `lc0_match_downloader.py` to customize:

```python
class Config:
    # Scraping
    TOTAL_PAGES = 2440
    CONCURRENT_SCRAPE_REQUESTS = 25
    
    # Downloading
    CONCURRENT_DOWNLOADS = 10
    MAX_RETRIES = 3
    RETRY_DELAY = 5
    
    # File paths
    CSV_FILE = "lc0_matches_full.csv"
    DB_FILE = "lc0_matches.db"
    DOWNLOAD_DIR = Path("downloads")
    PROCESSED_DIR = Path("processed")
    
    # Date filtering (optional)
    START_DATE = datetime(2020, 1, 1)  # None for all
    END_DATE = datetime(2024, 12, 31)  # None for all
```

## Directory Structure

After running, your directory will look like:

```
lc0-match-downloader/
├── lc0_match_downloader.py    # Main script
├── requirements.txt            # Dependencies
├── lc0_matches_full.csv       # Scraped metadata
├── lc0_matches.db             # SQLite tracking database
├── lc0_downloader.log         # Execution log
├── downloads/                 # Downloaded archives
│   ├── match_100.pgn.tar.gz
│   ├── match_101.pgn.tar.gz
│   └── ...
└── processed/                 # Processed monthly PGNs
    ├── 2024/
    │   ├── 01/
    │   │   └── lc0_matches_2024_01.pgn
    │   ├── 02/
    │   │   └── lc0_matches_2024_02.pgn
    │   └── ...
    └── 2025/
        └── ...
```

## Data Format

### Input (Scraped)
From `training.lczero.org/matches/`:
```
Match_ID,Date,PGN_Filename
243886,2026-01-27 00:46:28 +00:00,match_243886.pgn.tar.gz
```

### Output (Processed PGN)
Each game in monthly files includes:
```pgn
[Event "Lc0 match 243886"]
[Date "2026-01-27"]
[White "Lc0 v0.32.0"]
[Black "Lc0 v0.32.0"]
[Result "1-0"]
...moves...
```

## Version Detection

The script automatically maps match dates to Lc0 versions based on GitHub releases:

| Date Range        | Lc0 Version |
|-------------------|-------------|
| 2025-01-01+       | v0.32.0     |
| 2024-06-01+       | v0.31.0     |
| 2023-07-01+       | v0.30.0     |
| 2023-01-01+       | v0.29.0     |
| 2022-01-01+       | v0.28.0     |
| Earlier           | v0.27.0 and below |

Update `Config.VERSION_MAP` to refine version boundaries.

## Database Schema

The SQLite database tracks download progress:

### `matches` table
```sql
CREATE TABLE matches (
    match_id INTEGER PRIMARY KEY,
    date TEXT NOT NULL,
    pgn_filename TEXT NOT NULL,
    run_id INTEGER,
    download_status TEXT DEFAULT 'pending',  -- pending|success|failed|retry
    download_attempts INTEGER DEFAULT 0,
    processed BOOLEAN DEFAULT 0,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
)
```

### `download_log` table
```sql
CREATE TABLE download_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    match_id INTEGER,
    status TEXT,
    message TEXT,
    timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP
)
```

## Error Handling

The script handles various failure scenarios:

1. **Network Errors** - Retries with exponential backoff
2. **404 Not Found** - Tries alternative URL patterns, marks as failed if none work
3. **Invalid Content** - Validates PGN structure, rejects HTML error pages
4. **Corrupted Archives** - Logs errors, skips to next match
5. **Rate Limiting** - Respects concurrent connection limits

## Logging

All operations are logged to:
- `lc0_downloader.log` (file)
- Console output

Log levels:
- `INFO` - Progress updates, statistics
- `WARNING` - Missing files, validation failures
- `ERROR` - Download failures, processing errors
- `DEBUG` - Detailed parsing information

## Performance

Expected performance (varies by network):

- **Scraping**: ~2440 pages in 5-10 minutes
- **Downloading**: ~10-50 matches/minute (depends on file size)
- **Processing**: ~100+ matches/minute

Adjust `CONCURRENT_SCRAPE_REQUESTS` and `CONCURRENT_DOWNLOADS` based on your system.

## Troubleshooting

### "No pending downloads"
- Run `--mode scrape` first to populate the database
- Check `lc0_matches.db` exists

### "All URL patterns failed"
- Some matches may not have uploaded PGNs (client failures)
- Check `download_log` table for specific errors
- Matches marked as 'failed' won't retry automatically

### "Module not found"
```bash
pip install -r requirements.txt
```

### "Database locked"
- Close other processes accessing the database
- On Windows, ensure no antivirus is scanning the database

## Extending

### Adding Elo Ratings

To add Elo ratings (if available in match metadata):

1. Modify `MatchScraper._parse_html()` to extract rating information
2. Add `white_elo` and `black_elo` columns to database
3. Update `PGNProcessor._process_pgn_content()` to inject `[WhiteElo]` and `[BlackElo]` headers

### Filtering by Run

To download only specific training runs (T60, T70, etc.):

1. Enhance `MatchDownloader._extract_run_id()` to parse run from match page
2. Add `run_id` filter in `MatchDatabase.get_pending_downloads()`
3. Update `Config` with desired run IDs

### Custom Processing

Subclass `PGNProcessor` and override `_process_pgn_content()`:

```python
class CustomProcessor(PGNProcessor):
    def _process_pgn_content(self, pgn_text, match_date, match_id):
        # Your custom logic here
        processed = super()._process_pgn_content(pgn_text, match_date, match_id)
        # Add more modifications
        return processed
```

## Data Sources

- **Match Metadata**: https://training.lczero.org/matches/
- **PGN Files**: https://storage.lczero.org/files/match_pgns/
- **Lc0 Releases**: https://github.com/LeelaChessZero/lc0/releases

## License

This tool is provided for research and analysis of Lc0 training data. Respect the Lc0 project's infrastructure and rate limits.

## Contributing

Improvements welcome:
- Better run ID detection
- Elo extraction from match pages
- Progress bars (tqdm integration)
- Configuration file support
- Resume from interruption

## References

- [Lc0 Documentation](https://lczero.org/dev/wiki/)
- [Training Infrastructure](https://training.lczero.org/)
- [Lc0 GitHub](https://github.com/LeelaChessZero/lc0)

## Support

For issues:
1. Check `lc0_downloader.log`
2. Review database with: `sqlite3 lc0_matches.db "SELECT * FROM matches LIMIT 10;"`
3. Open an issue with log excerpts
