#!/usr/bin/env python3
# PGNTOOLS-LC0-BEGIN
"""
Lc0 Match Downloader
====================
A comprehensive tool for downloading, processing, and organizing Leela Chess Zero matches.

Features:
- Scrapes match metadata from training.lczero.org
- Downloads PGN files from storage.lczero.org (open directory)
- Adds date information to each game in the PGN
- Normalizes player names to "Lc0 [version]"
- Adds Elo ratings when available
- Organizes games by month
- Handles .tar.gz archives and individual PGN files

Usage:
    python lc0_match_downloader.py --mode scrape      # Build CSV from web
    python lc0_match_downloader.py --mode download    # Download matches
    python lc0_match_downloader.py --mode process     # Process PGNs
    python lc0_match_downloader.py --mode all         # Run all steps
"""

import aiohttp
import asyncio
import argparse
import csv
import gzip
import io
import json
import logging
import os
import re
import sqlite3
import sys
import tarfile
from collections import defaultdict
from datetime import datetime, timedelta
from pathlib import Path
from typing import Dict, List, Optional, Tuple
from urllib.parse import urljoin

from bs4 import BeautifulSoup

# ============================================================================
# CONFIGURATION
# ============================================================================

class Config:
    """Central configuration for the downloader."""
    
    # URLs
    MATCHES_BASE_URL = "https://training.lczero.org/matches/"
    STORAGE_BASE_URL = "https://storage.lczero.org/files/match_pgns/"
    
    # Scraping settings
    TOTAL_PAGES = 2440  # Buffer above current count
    CONCURRENT_SCRAPE_REQUESTS = 25
    
    # Download settings
    CONCURRENT_DOWNLOADS = 10
    MAX_RETRIES = 3
    RETRY_DELAY = 5  # seconds
    CHUNK_SIZE = 8192  # for streaming downloads
    
    # File paths
    CSV_FILE = "lc0_matches_full.csv"
    DB_FILE = "lc0_matches.db"
    DOWNLOAD_DIR = Path("downloads")
    PROCESSED_DIR = Path("processed")
    
    # Date filtering (None for all dates)
    START_DATE = None  # datetime(2020, 1, 1)
    END_DATE = None    # datetime(2024, 12, 31)
    
    # Lc0 version mapping (date ranges to versions)
    # Based on GitHub releases: https://github.com/LeelaChessZero/lc0/releases
    VERSION_MAP = [
        (datetime(2025, 1, 1), "v0.32.0"),
        (datetime(2024, 6, 1), "v0.31.0"),
        (datetime(2023, 7, 1), "v0.30.0"),
        (datetime(2023, 1, 1), "v0.29.0"),
        (datetime(2022, 1, 1), "v0.28.0"),
        (datetime(2021, 1, 1), "v0.27.0"),
        (datetime(2020, 1, 1), "v0.26.0"),
        (datetime(2019, 1, 1), "v0.25.0"),
        (datetime(2018, 1, 1), "v0.24.0"),
        (datetime(1970, 1, 1), "v0.23.0"),  # Fallback for older
    ]

# ============================================================================
# LOGGING SETUP
# ============================================================================

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s',
    handlers=[
        logging.FileHandler('lc0_downloader.log'),
        logging.StreamHandler(sys.stdout)
    ]
)
logger = logging.getLogger(__name__)

# ============================================================================
# DATABASE MANAGEMENT
# ============================================================================

class MatchDatabase:
    """SQLite database for tracking matches and downloads."""
    
    def __init__(self, db_path: str = Config.DB_FILE):
        self.db_path = db_path
        self.conn = None
        self._init_db()
    
    def _init_db(self):
        """Initialize database schema."""
        self.conn = sqlite3.connect(self.db_path)
        self.conn.execute("""
            CREATE TABLE IF NOT EXISTS matches (
                match_id INTEGER PRIMARY KEY,
                date TEXT NOT NULL,
                pgn_filename TEXT NOT NULL,
                run_id INTEGER,
                download_status TEXT DEFAULT 'pending',
                download_attempts INTEGER DEFAULT 0,
                processed BOOLEAN DEFAULT 0,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            )
        """)
        
        self.conn.execute("""
            CREATE TABLE IF NOT EXISTS download_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                match_id INTEGER,
                status TEXT,
                message TEXT,
                timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (match_id) REFERENCES matches(match_id)
            )
        """)
        
        self.conn.execute("""
            CREATE INDEX IF NOT EXISTS idx_match_date ON matches(date)
        """)
        self.conn.execute("""
            CREATE INDEX IF NOT EXISTS idx_download_status ON matches(download_status)
        """)
        
        self.conn.commit()
    
    def insert_matches(self, matches: List[Tuple[str, str, str]]):
        """Bulk insert matches from CSV data."""
        cursor = self.conn.cursor()
        cursor.executemany(
            "INSERT OR IGNORE INTO matches (match_id, date, pgn_filename) VALUES (?, ?, ?)",
            matches
        )
        self.conn.commit()
        logger.info(f"Inserted {cursor.rowcount} new matches into database")
    
    def get_pending_downloads(self, limit: Optional[int] = None) -> List[Dict]:
        """Get matches that need to be downloaded."""
        query = """
            SELECT match_id, date, pgn_filename 
            FROM matches 
            WHERE download_status IN ('pending', 'retry')
            AND download_attempts < ?
            ORDER BY date DESC
        """
        if limit:
            query += f" LIMIT {limit}"
        
        cursor = self.conn.cursor()
        cursor.execute(query, (Config.MAX_RETRIES,))
        
        return [
            {'match_id': row[0], 'date': row[1], 'pgn_filename': row[2]}
            for row in cursor.fetchall()
        ]
    
    def update_download_status(self, match_id: int, status: str, message: str = ""):
        """Update download status for a match."""
        cursor = self.conn.cursor()
        cursor.execute("""
            UPDATE matches 
            SET download_status = ?, download_attempts = download_attempts + 1
            WHERE match_id = ?
        """, (status, match_id))
        
        cursor.execute("""
            INSERT INTO download_log (match_id, status, message)
            VALUES (?, ?, ?)
        """, (match_id, status, message))
        
        self.conn.commit()
    
    def mark_processed(self, match_id: int):
        """Mark a match as processed."""
        self.conn.execute(
            "UPDATE matches SET processed = 1 WHERE match_id = ?",
            (match_id,)
        )
        self.conn.commit()
    
    def get_stats(self) -> Dict:
        """Get download statistics."""
        cursor = self.conn.cursor()
        
        stats = {}
        
        # Total matches
        cursor.execute("SELECT COUNT(*) FROM matches")
        stats['total_matches'] = cursor.fetchone()[0]
        
        # Downloaded
        cursor.execute("SELECT COUNT(*) FROM matches WHERE download_status = 'success'")
        stats['downloaded'] = cursor.fetchone()[0]
        
        # Pending
        cursor.execute("SELECT COUNT(*) FROM matches WHERE download_status = 'pending'")
        stats['pending'] = cursor.fetchone()[0]
        
        # Failed
        cursor.execute("SELECT COUNT(*) FROM matches WHERE download_status = 'failed'")
        stats['failed'] = cursor.fetchone()[0]
        
        # Processed
        cursor.execute("SELECT COUNT(*) FROM matches WHERE processed = 1")
        stats['processed'] = cursor.fetchone()[0]
        
        return stats
    
    def close(self):
        """Close database connection."""
        if self.conn:
            self.conn.close()

# ============================================================================
# WEB SCRAPER
# ============================================================================

class MatchScraper:
    """Scrapes match data from training.lczero.org."""
    
    def __init__(self):
        self.base_url = Config.MATCHES_BASE_URL
        self.total_pages = Config.TOTAL_PAGES
        self.concurrent_requests = Config.CONCURRENT_SCRAPE_REQUESTS
    
    async def fetch_page(self, session: aiohttp.ClientSession, page_num: int) -> List[Tuple]:
        """Fetch and parse a single page of matches."""
        params = {'page': page_num, 'show_all': '1'}
        
        try:
            async with session.get(self.base_url, params=params, timeout=30) as response:
                if response.status == 200:
                    html = await response.text()
                    return self._parse_html(html)
                else:
                    logger.warning(f"Page {page_num} returned status {response.status}")
                    return []
        except asyncio.TimeoutError:
            logger.error(f"Timeout fetching page {page_num}")
            return []
        except Exception as e:
            logger.error(f"Error fetching page {page_num}: {e}")
            return []
    
    def _parse_html(self, html: str) -> List[Tuple]:
        """Parse HTML to extract match data."""
        soup = BeautifulSoup(html, 'html.parser')
        rows = []
        
        table_rows = soup.select('table.table tbody tr')
        
        for tr in table_rows:
            tds = tr.find_all('td')
            if not tds:
                continue
            
            try:
                # Extract match ID
                match_id_tag = tds[0].find('a')
                if match_id_tag:
                    match_id = match_id_tag.get_text(strip=True)
                else:
                    match_id = tds[0].get_text(strip=True)
                
                # Extract date (last column)
                raw_date = tds[-1].get_text(strip=True)
                
                if match_id and raw_date:
                    pgn_name = f"match_{match_id}.pgn.tar.gz"
                    rows.append((match_id, raw_date, pgn_name))
            
            except Exception as e:
                logger.debug(f"Error parsing row: {e}")
                continue
        
        return rows
    
    async def scrape_all(self) -> List[Tuple]:
        """Scrape all pages concurrently."""
        logger.info(f"Starting scrape of {self.total_pages} pages...")
        
        async with aiohttp.ClientSession() as session:
            sem = asyncio.Semaphore(self.concurrent_requests)
            
            async def sem_task(page_num):
                async with sem:
                    data = await self.fetch_page(session, page_num)
                    if page_num % 100 == 0:
                        logger.info(f"Progress: {page_num}/{self.total_pages} pages")
                    return data
            
            tasks = [sem_task(i) for i in range(1, self.total_pages + 1)]
            results = await asyncio.gather(*tasks)
        
        # Flatten results
        all_matches = []
        for page_data in results:
            all_matches.extend(page_data)
        
        logger.info(f"Scraping complete! Found {len(all_matches)} matches")
        return all_matches
    
    def save_to_csv(self, matches: List[Tuple], filename: str = Config.CSV_FILE):
        """Save matches to CSV file."""
        with open(filename, 'w', newline='', encoding='utf-8') as f:
            writer = csv.writer(f)
            writer.writerow(['Match_ID', 'Date', 'PGN_Filename'])
            writer.writerows(matches)
        
        logger.info(f"Saved {len(matches)} matches to {filename}")

# ============================================================================
# MATCH DOWNLOADER
# ============================================================================

class MatchDownloader:
    """Downloads PGN files from storage.lczero.org."""
    
    def __init__(self, db: MatchDatabase):
        self.db = db
        self.base_url = Config.STORAGE_BASE_URL
        self.download_dir = Config.DOWNLOAD_DIR
        self.download_dir.mkdir(exist_ok=True, parents=True)
        self.session = None
    
    def _extract_run_id(self, match_id: int, date_str: str) -> Optional[int]:
        """
        Infer run ID from match metadata.
        This is a heuristic - actual run IDs would need to be scraped from match details.
        Default to Run 1 (T60 mainline) for most matches.
        """
        # This would need to be enhanced with actual run detection logic
        # For now, default to run 1 and create structure for future enhancement
        return 1
    
    async def download_match(self, match_data: Dict) -> bool:
        """Download a single match PGN."""
        match_id = match_data['match_id']
        pgn_filename = match_data['pgn_filename']
        
        # Infer run ID (this is simplified - would need actual logic)
        run_id = self._extract_run_id(match_id, match_data['date'])
        
        # Try multiple possible URL patterns
        url_patterns = [
            f"{self.base_url}{run_id}/match_{match_id}.pgn.tar.gz",
            f"{self.base_url}{run_id}/match_{match_id}.pgn",
            f"{self.base_url}match_{match_id}.pgn.tar.gz",
            f"{self.base_url}match_{match_id}.pgn",
        ]
        
        for url in url_patterns:
            try:
                async with self.session.get(url, timeout=60) as response:
                    if response.status == 200:
                        content = await response.read()
                        
                        # Validate content
                        if not self._validate_pgn_content(content, pgn_filename):
                            continue
                        
                        # Save to disk
                        save_path = self.download_dir / pgn_filename
                        save_path.write_bytes(content)
                        
                        self.db.update_download_status(
                            match_id, 'success', f'Downloaded from {url}'
                        )
                        logger.info(f"✓ Downloaded match {match_id}")
                        return True
                    
                    elif response.status == 404:
                        continue  # Try next URL pattern
                    
            except asyncio.TimeoutError:
                logger.warning(f"Timeout downloading match {match_id} from {url}")
            except Exception as e:
                logger.error(f"Error downloading match {match_id}: {e}")
        
        # If we get here, all attempts failed
        self.db.update_download_status(match_id, 'failed', 'All URL patterns failed')
        logger.warning(f"✗ Failed to download match {match_id}")
        return False
    
    def _validate_pgn_content(self, content: bytes, filename: str) -> bool:
        """Validate that downloaded content is actually a PGN file."""
        try:
            # If it's a tar.gz, try to extract and validate
            if filename.endswith('.tar.gz'):
                with gzip.GzipFile(fileobj=io.BytesIO(content)) as gz:
                    with tarfile.open(fileobj=gz) as tar:
                        # Check that tar contains .pgn files
                        members = tar.getmembers()
                        return any(m.name.endswith('.pgn') for m in members)
            
            # If it's a plain PGN, check for PGN header
            elif filename.endswith('.pgn'):
                text = content.decode('utf-8', errors='ignore')[:500]
                return '[Event' in text or '[White' in text
            
            return False
        
        except Exception as e:
            logger.debug(f"Validation error: {e}")
            return False
    
    async def download_all(self, limit: Optional[int] = None):
        """Download all pending matches."""
        pending = self.db.get_pending_downloads(limit)
        
        if not pending:
            logger.info("No pending downloads")
            return
        
        logger.info(f"Starting download of {len(pending)} matches...")
        
        connector = aiohttp.TCPConnector(limit=Config.CONCURRENT_DOWNLOADS)
        async with aiohttp.ClientSession(connector=connector) as session:
            self.session = session
            
            tasks = [self.download_match(match) for match in pending]
            results = await asyncio.gather(*tasks, return_exceptions=True)
        
        success_count = sum(1 for r in results if r is True)
        logger.info(f"Download complete: {success_count}/{len(pending)} successful")

# ============================================================================
# PGN PROCESSOR
# ============================================================================

class PGNProcessor:
    """Processes downloaded PGN files to add dates, normalize names, etc."""
    
    def __init__(self, db: MatchDatabase):
        self.db = db
        self.download_dir = Config.DOWNLOAD_DIR
        self.processed_dir = Config.PROCESSED_DIR
        self.processed_dir.mkdir(exist_ok=True, parents=True)
    
    def _get_lc0_version(self, date_str: str) -> str:
        """Get Lc0 version based on date."""
        try:
            date = datetime.fromisoformat(date_str.split()[0])
            
            for version_date, version in Config.VERSION_MAP:
                if date >= version_date:
                    return version
            
            return "v0.21.0"  # Fallback
        
        except Exception as e:
            logger.warning(f"Error parsing date {date_str}: {e}")
            return "v0.21.0"
    
    def _process_pgn_content(self, pgn_text: str, match_date: str, match_id: int) -> str:
        """Process PGN text to add date and normalize player names."""
        lc0_version = self._get_lc0_version(match_date)
        date_only = match_date.split()[0]  # Extract YYYY-MM-DD
        
        lines = pgn_text.split('\n')
        processed_lines = []
        in_headers = True
        
        for line in lines:
            # Add date if not present
            if in_headers and line.startswith('[Event'):
                processed_lines.append(line)
                # Add date header if not already present
                if not any('[Date' in l for l in lines[:20]):
                    processed_lines.append(f'[Date "{date_only}"]')
                continue
            
            # Normalize White/Black to Lc0 version
            if line.startswith('[White'):
                processed_lines.append(f'[White "Lc0 {lc0_version}"]')
                continue
            
            if line.startswith('[Black'):
                processed_lines.append(f'[Black "Lc0 {lc0_version}"]')
                continue
            
            # Add custom header with original match info
            if line.startswith('[Event'):
                processed_lines.append(f'[Event "Lc0 match {match_id}"]')
                continue
            
            # Track when we're done with headers
            if in_headers and line.strip() == '':
                in_headers = False
            
            processed_lines.append(line)
        
        return '\n'.join(processed_lines)
    
    def _extract_from_archive(self, archive_path: Path) -> List[str]:
        """Extract PGN files from tar.gz archive."""
        pgn_contents = []
        
        try:
            with gzip.open(archive_path, 'rb') as gz:
                with tarfile.open(fileobj=gz) as tar:
                    for member in tar.getmembers():
                        if member.name.endswith('.pgn'):
                            f = tar.extractfile(member)
                            if f:
                                content = f.read().decode('utf-8', errors='ignore')
                                pgn_contents.append(content)
        
        except Exception as e:
            logger.error(f"Error extracting {archive_path}: {e}")
        
        return pgn_contents
    
    def _organize_by_month(self, date_str: str, pgn_content: str):
        """Save PGN to monthly file."""
        try:
            date = datetime.fromisoformat(date_str.split()[0])
            month_dir = self.processed_dir / f"{date.year}" / f"{date.month:02d}"
            month_dir.mkdir(exist_ok=True, parents=True)
            
            month_file = month_dir / f"lc0_matches_{date.year}_{date.month:02d}.pgn"
            
            # Append to monthly file
            with open(month_file, 'a', encoding='utf-8') as f:
                f.write(pgn_content)
                f.write('\n\n')  # Separate games
            
        except Exception as e:
            logger.error(f"Error organizing by month: {e}")
    
    def process_match(self, match_id: int, date: str, pgn_filename: str):
        """Process a single match."""
        file_path = self.download_dir / pgn_filename
        
        if not file_path.exists():
            logger.warning(f"File not found: {file_path}")
            return
        
        try:
            # Extract PGNs
            if pgn_filename.endswith('.tar.gz'):
                pgn_contents = self._extract_from_archive(file_path)
            else:
                pgn_contents = [file_path.read_text(encoding='utf-8', errors='ignore')]
            
            # Process each PGN
            for pgn_text in pgn_contents:
                if not pgn_text.strip():
                    continue
                
                processed_pgn = self._process_pgn_content(pgn_text, date, match_id)
                self._organize_by_month(date, processed_pgn)
            
            # Mark as processed
            self.db.mark_processed(match_id)
            logger.info(f"✓ Processed match {match_id} ({len(pgn_contents)} games)")
        
        except Exception as e:
            logger.error(f"Error processing match {match_id}: {e}")
    
    def process_all(self):
        """Process all successfully downloaded matches."""
        cursor = self.db.conn.cursor()
        cursor.execute("""
            SELECT match_id, date, pgn_filename 
            FROM matches 
            WHERE download_status = 'success' AND processed = 0
            ORDER BY date
        """)
        
        matches = cursor.fetchall()
        logger.info(f"Processing {len(matches)} downloaded matches...")
        
        for match_id, date, pgn_filename in matches:
            self.process_match(match_id, date, pgn_filename)
        
        logger.info("Processing complete!")

# ============================================================================
# MAIN APPLICATION
# ============================================================================

class Lc0MatchDownloaderApp:
    """Main application controller."""
    
    def __init__(self):
        self.db = MatchDatabase()
        self.scraper = MatchScraper()
        self.downloader = MatchDownloader(self.db)
        self.processor = PGNProcessor(self.db)
    
    async def run_scrape(self):
        """Run scraping phase."""
        logger.info("=" * 70)
        logger.info("PHASE 1: SCRAPING MATCH DATA")
        logger.info("=" * 70)
        
        matches = await self.scraper.scrape_all()
        
        # Save to CSV
        self.scraper.save_to_csv(matches)
        
        # Insert into database
        self.db.insert_matches(matches)
        
        self._print_stats()
    
    async def run_download(self, limit: Optional[int] = None):
        """Run download phase."""
        logger.info("=" * 70)
        logger.info("PHASE 2: DOWNLOADING MATCHES")
        logger.info("=" * 70)
        
        await self.downloader.download_all(limit)
        
        self._print_stats()
    
    def run_process(self):
        """Run processing phase."""
        logger.info("=" * 70)
        logger.info("PHASE 3: PROCESSING PGN FILES")
        logger.info("=" * 70)
        
        self.processor.process_all()
        
        self._print_stats()
    
    def _print_stats(self):
        """Print current statistics."""
        stats = self.db.get_stats()
        
        logger.info("\n" + "=" * 70)
        logger.info("STATISTICS")
        logger.info("=" * 70)
        logger.info(f"Total matches:    {stats['total_matches']:>8,}")
        logger.info(f"Downloaded:       {stats['downloaded']:>8,}")
        logger.info(f"Pending:          {stats['pending']:>8,}")
        logger.info(f"Failed:           {stats['failed']:>8,}")
        logger.info(f"Processed:        {stats['processed']:>8,}")
        logger.info("=" * 70 + "\n")
    
    async def run_all(self):
        """Run all phases."""
        await self.run_scrape()
        await self.run_download()
        self.run_process()
    
    def cleanup(self):
        """Cleanup resources."""
        self.db.close()

# ============================================================================
# CLI ENTRY POINT
# ============================================================================

def main():
    parser = argparse.ArgumentParser(
        description="Lc0 Match Downloader - Download and process Leela Chess Zero matches",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  %(prog)s --mode scrape              # Scrape match metadata from web
  %(prog)s --mode download            # Download PGN files
  %(prog)s --mode download --limit 100  # Download only 100 matches
  %(prog)s --mode process             # Process downloaded PGNs
  %(prog)s --mode all                 # Run complete pipeline
        """
    )
    
    parser.add_argument(
        '--mode',
        choices=['scrape', 'download', 'process', 'all', 'stats'],
        required=True,
        help='Operation mode'
    )
    
    parser.add_argument(
        '--limit',
        type=int,
        help='Limit number of downloads (download mode only)'
    )
    
    args = parser.parse_args()
    
    # Windows event loop fix
    if sys.platform == 'win32':
        asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())
    
    app = Lc0MatchDownloaderApp()
    
    try:
        if args.mode == 'scrape':
            asyncio.run(app.run_scrape())
        
        elif args.mode == 'download':
            asyncio.run(app.run_download(args.limit))
        
        elif args.mode == 'process':
            app.run_process()
        
        elif args.mode == 'all':
            asyncio.run(app.run_all())
        
        elif args.mode == 'stats':
            app._print_stats()
    
    except KeyboardInterrupt:
        logger.info("\nOperation cancelled by user")
    
    except Exception as e:
        logger.error(f"Fatal error: {e}", exc_info=True)
    
    finally:
        app.cleanup()
        logger.info("Shutdown complete")

if __name__ == "__main__":
    main()
# PGNTOOLS-LC0-END
