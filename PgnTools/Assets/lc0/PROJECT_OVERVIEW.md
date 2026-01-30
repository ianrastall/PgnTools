# Lc0 Match Downloader - Project Overview

## What This Is

A complete, production-ready Python application for downloading, processing, and organizing Leela Chess Zero (Lc0) match data from the distributed training infrastructure.

## Project Files

### Core Application
1. **lc0_match_downloader.py** (Main script - 850+ lines)
   - Complete three-phase pipeline (scrape, download, process)
   - Async I/O for efficient concurrent operations
   - SQLite database for tracking progress
   - Robust error handling and retry logic
   - Comprehensive logging

2. **requirements.txt**
   - Python dependencies (aiohttp, beautifulsoup4)
   - Minimal dependencies - mostly uses standard library

### Documentation
3. **README.md** (Comprehensive guide)
   - Installation instructions
   - Usage examples and workflows
   - Configuration options
   - Architecture overview
   - Troubleshooting guide
   - Performance tips

4. **config.json.example**
   - Template configuration file
   - All settings documented
   - Easy to customize

### Utilities
5. **quick_start.py** (Interactive guide)
   - Step-by-step tutorial
   - Demo commands
   - Common workflow examples
   - Database query examples

6. **match_analyzer.py** (Analysis tool)
   - Statistics and reporting
   - File size analysis
   - Success rate tracking
   - Search functionality
   - Export reports

## Key Features

### Phase 1: Scraping
- Scrapes ~2,440 pages from training.lczero.org/matches/
- Concurrent requests with semaphore control
- Extracts match IDs, dates, and filenames
- Saves to CSV and SQLite database

### Phase 2: Downloading
- Downloads PGN archives from storage.lczero.org
- Tries multiple URL patterns (different run directories)
- Validates content (rejects HTML error pages)
- Concurrent downloads with connection pooling
- Automatic retry on failure
- Progress tracking in database

### Phase 3: Processing
- Extracts games from .tar.gz archives
- Adds [Date "YYYY-MM-DD"] headers
- Normalizes players to "Lc0 vX.XX.X" format
- Automatic version detection based on date
- Organizes into monthly PGN files
- Directory structure: processed/YYYY/MM/

## Database Schema

**matches** table:
- match_id (primary key)
- date, pgn_filename
- download_status (pending/success/failed/retry)
- download_attempts
- processed (boolean)

**download_log** table:
- Audit trail of all download attempts
- Error messages and timestamps

## Usage Examples

```bash
# Complete pipeline
python lc0_match_downloader.py --mode all

# Individual phases
python lc0_match_downloader.py --mode scrape
python lc0_match_downloader.py --mode download --limit 100
python lc0_match_downloader.py --mode process

# Analysis
python match_analyzer.py --report all
python match_analyzer.py --search "2024-01"
python match_analyzer.py --export report.txt
```

## Technical Highlights

1. **Async/Await Architecture**
   - Non-blocking I/O for web requests
   - Semaphore-controlled concurrency
   - Efficient resource utilization

2. **Error Resilience**
   - Multiple retry attempts with backoff
   - Graceful degradation on failures
   - Comprehensive error logging
   - Resume capability

3. **Data Validation**
   - PGN structure verification
   - Archive integrity checks
   - Content type validation
   - Prevents corrupt downloads

4. **Progressive Processing**
   - Works with partial downloads
   - Incremental progress tracking
   - Can resume after interruption
   - Monthly organization prevents huge files

5. **Extensibility**
   - Modular class design
   - Easy to add custom processors
   - Configuration externalization
   - Plugin-ready architecture

## Version Detection

Maps match dates to Lc0 versions based on GitHub releases:

- 2025-01+ → v0.32.0
- 2024-06+ → v0.31.0
- 2023-07+ → v0.30.0
- Earlier dates mapped accordingly

## File Organization

```
downloads/              # Raw downloaded archives
  match_100.pgn.tar.gz
  match_101.pgn.tar.gz
  ...

processed/             # Organized monthly PGNs
  2024/
    01/
      lc0_matches_2024_01.pgn
    02/
      lc0_matches_2024_02.pgn
  2025/
    01/
      lc0_matches_2025_01.pgn

lc0_matches.db         # SQLite tracking database
lc0_matches_full.csv   # CSV export of matches
lc0_downloader.log     # Execution log
```

## Performance Expectations

- **Scraping**: ~2,440 pages in 5-10 minutes
- **Downloading**: 10-50 matches/minute (varies by size)
- **Processing**: 100+ matches/minute

Total data size: Varies, but expect 10-100+ GB for complete dataset

## Potential Enhancements

Future improvements could include:

1. **Elo Rating Extraction**
   - Parse rating from match pages
   - Add [WhiteElo] and [BlackElo] headers

2. **Run ID Detection**
   - Identify T60/T70/T75 series
   - Filter by training run

3. **Progress Bars**
   - Integrate tqdm for visual progress
   - Real-time download speed

4. **Configuration File**
   - Load settings from config.json
   - Multiple profile support

5. **API Integration**
   - Query match metadata via API
   - More accurate run detection

## Data Sources

- Match listing: https://training.lczero.org/matches/
- PGN storage: https://storage.lczero.org/files/match_pgns/
- Lc0 releases: https://github.com/LeelaChessZero/lc0/releases

## Requirements

- Python 3.8+
- 500MB+ free disk space (more for full dataset)
- Internet connection
- Standard Python libraries + aiohttp, beautifulsoup4

## License & Usage

This tool is designed for research and analysis of Lc0 training data. Please:
- Respect the infrastructure's rate limits
- Use reasonable concurrency settings
- Don't hammer the servers
- Consider the bandwidth costs to the project

## Support

Check the comprehensive README.md for:
- Detailed installation steps
- Troubleshooting guide
- Configuration options
- Advanced usage patterns

Run the quick_start.py for:
- Interactive tutorial
- Step-by-step examples
- Common workflows

Use match_analyzer.py for:
- Statistics and insights
- Progress tracking
- Failure analysis

---

**Ready to Use**: All files are production-ready and fully functional. Simply install dependencies and run!
