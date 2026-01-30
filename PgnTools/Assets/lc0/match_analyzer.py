#!/usr/bin/env python3
"""
Lc0 Match Analyzer
==================
Utility script for analyzing downloaded and processed Lc0 matches.

Provides statistics and insights about the match database.
"""

import sqlite3
import sys
from collections import defaultdict
from datetime import datetime
from pathlib import Path
from typing import Dict, List

class MatchAnalyzer:
    """Analyzer for Lc0 match database and processed files."""
    
    def __init__(self, db_path: str = "lc0_matches.db"):
        self.db_path = db_path
        if not Path(db_path).exists():
            print(f"Error: Database {db_path} not found.")
            print("Run scraping first: python lc0_match_downloader.py --mode scrape")
            sys.exit(1)
        
        self.conn = sqlite3.connect(db_path)
    
    def print_header(self, text: str):
        """Print formatted section header."""
        print("\n" + "=" * 70)
        print(f"  {text}")
        print("=" * 70 + "\n")
    
    def overview(self):
        """Print overview statistics."""
        self.print_header("DATABASE OVERVIEW")
        
        cursor = self.conn.cursor()
        
        # Total matches
        cursor.execute("SELECT COUNT(*) FROM matches")
        total = cursor.fetchone()[0]
        print(f"Total matches in database: {total:,}")
        
        # Date range
        cursor.execute("SELECT MIN(date), MAX(date) FROM matches")
        min_date, max_date = cursor.fetchone()
        print(f"Date range: {min_date} to {max_date}")
        
        # Status breakdown
        cursor.execute("""
            SELECT download_status, COUNT(*) 
            FROM matches 
            GROUP BY download_status
            ORDER BY COUNT(*) DESC
        """)
        
        print("\nDownload Status:")
        for status, count in cursor.fetchall():
            percentage = (count / total * 100) if total > 0 else 0
            print(f"  {status:15s}: {count:8,} ({percentage:5.1f}%)")
        
        # Processing status
        cursor.execute("SELECT COUNT(*) FROM matches WHERE processed = 1")
        processed = cursor.fetchone()[0]
        percentage = (processed / total * 100) if total > 0 else 0
        print(f"\nProcessed: {processed:,} ({percentage:.1f}%)")
    
    def monthly_breakdown(self):
        """Show matches by month."""
        self.print_header("MONTHLY BREAKDOWN")
        
        cursor = self.conn.cursor()
        cursor.execute("""
            SELECT 
                strftime('%Y-%m', date) as month,
                COUNT(*) as total,
                SUM(CASE WHEN download_status = 'success' THEN 1 ELSE 0 END) as downloaded,
                SUM(CASE WHEN processed = 1 THEN 1 ELSE 0 END) as processed
            FROM matches
            GROUP BY month
            ORDER BY month DESC
            LIMIT 24
        """)
        
        print(f"{'Month':<10} {'Total':>8} {'Downloaded':>12} {'Processed':>11}")
        print("-" * 45)
        
        for month, total, downloaded, processed in cursor.fetchall():
            print(f"{month:<10} {total:>8,} {downloaded:>12,} {processed:>11,}")
    
    def failure_analysis(self):
        """Analyze download failures."""
        self.print_header("FAILURE ANALYSIS")
        
        cursor = self.conn.cursor()
        
        # Failed matches
        cursor.execute("""
            SELECT COUNT(*) FROM matches WHERE download_status = 'failed'
        """)
        failed_count = cursor.fetchone()[0]
        print(f"Total failed downloads: {failed_count:,}")
        
        if failed_count == 0:
            print("No failures to analyze.")
            return
        
        # Recent failures
        cursor.execute("""
            SELECT m.match_id, m.date, l.message
            FROM matches m
            JOIN download_log l ON m.match_id = l.match_id
            WHERE m.download_status = 'failed'
            AND l.status = 'failed'
            ORDER BY l.timestamp DESC
            LIMIT 10
        """)
        
        print("\nRecent failures:")
        print(f"{'Match ID':<12} {'Date':<20} {'Reason':<40}")
        print("-" * 75)
        
        for match_id, date, message in cursor.fetchall():
            message = message[:37] + "..." if len(message) > 40 else message
            print(f"{match_id:<12} {date[:19]:<20} {message:<40}")
        
        # Retry candidates
        cursor.execute("""
            SELECT COUNT(*) 
            FROM matches 
            WHERE download_status = 'failed' 
            AND download_attempts < 3
        """)
        retry_count = cursor.fetchone()[0]
        print(f"\nMatches eligible for retry: {retry_count:,}")
    
    def file_analysis(self):
        """Analyze downloaded and processed files."""
        self.print_header("FILE ANALYSIS")
        
        download_dir = Path("downloads")
        processed_dir = Path("processed")
        
        # Downloaded files
        if download_dir.exists():
            downloaded_files = list(download_dir.glob("*.tar.gz")) + list(download_dir.glob("*.pgn"))
            total_size = sum(f.stat().st_size for f in downloaded_files)
            print(f"Downloaded files: {len(downloaded_files):,}")
            print(f"Total size: {total_size / (1024**3):.2f} GB")
        else:
            print("Downloads directory not found")
        
        # Processed files
        if processed_dir.exists():
            pgn_files = list(processed_dir.rglob("*.pgn"))
            print(f"\nProcessed PGN files: {len(pgn_files):,}")
            
            # Count games in processed files
            total_games = 0
            for pgn_file in pgn_files:
                try:
                    content = pgn_file.read_text(encoding='utf-8', errors='ignore')
                    # Count [Event tags as proxy for game count
                    total_games += content.count('[Event ')
                except:
                    pass
            
            print(f"Estimated total games: {total_games:,}")
            
            # Size breakdown by year
            year_sizes = defaultdict(int)
            for pgn_file in pgn_files:
                year = pgn_file.parts[-3] if len(pgn_file.parts) >= 3 else "unknown"
                year_sizes[year] += pgn_file.stat().st_size
            
            print("\nProcessed files by year:")
            for year in sorted(year_sizes.keys(), reverse=True):
                size_mb = year_sizes[year] / (1024**2)
                print(f"  {year}: {size_mb:>8.1f} MB")
        else:
            print("\nProcessed directory not found")
    
    def download_rate_analysis(self):
        """Analyze download success rates over time."""
        self.print_header("DOWNLOAD SUCCESS RATE OVER TIME")
        
        cursor = self.conn.cursor()
        cursor.execute("""
            SELECT 
                strftime('%Y-%m', date) as month,
                COUNT(*) as total,
                SUM(CASE WHEN download_status = 'success' THEN 1 ELSE 0 END) as success,
                CAST(SUM(CASE WHEN download_status = 'success' THEN 1 ELSE 0 END) AS FLOAT) / COUNT(*) * 100 as success_rate
            FROM matches
            GROUP BY month
            HAVING total > 10
            ORDER BY month DESC
            LIMIT 12
        """)
        
        print(f"{'Month':<10} {'Total':>8} {'Success':>9} {'Rate':>8}")
        print("-" * 40)
        
        for month, total, success, rate in cursor.fetchall():
            print(f"{month:<10} {total:>8,} {success:>9,} {rate:>7.1f}%")
    
    def pending_work(self):
        """Show pending work summary."""
        self.print_header("PENDING WORK")
        
        cursor = self.conn.cursor()
        
        # Pending downloads
        cursor.execute("""
            SELECT COUNT(*) FROM matches 
            WHERE download_status = 'pending' AND download_attempts < 3
        """)
        pending_downloads = cursor.fetchone()[0]
        
        # Downloaded but not processed
        cursor.execute("""
            SELECT COUNT(*) FROM matches 
            WHERE download_status = 'success' AND processed = 0
        """)
        pending_processing = cursor.fetchone()[0]
        
        print(f"Pending downloads: {pending_downloads:,}")
        print(f"Downloaded but not processed: {pending_processing:,}")
        
        if pending_downloads > 0:
            print(f"\nTo download: python lc0_match_downloader.py --mode download")
        
        if pending_processing > 0:
            print(f"To process: python lc0_match_downloader.py --mode process")
    
    def search_matches(self, query: str):
        """Search for specific matches."""
        self.print_header(f"SEARCH: {query}")
        
        cursor = self.conn.cursor()
        
        # Try to parse as match ID
        try:
            match_id = int(query)
            cursor.execute("""
                SELECT match_id, date, pgn_filename, download_status, processed
                FROM matches
                WHERE match_id = ?
            """, (match_id,))
        except ValueError:
            # Try as date pattern
            cursor.execute("""
                SELECT match_id, date, pgn_filename, download_status, processed
                FROM matches
                WHERE date LIKE ?
                LIMIT 20
            """, (f"%{query}%",))
        
        results = cursor.fetchall()
        
        if not results:
            print("No matches found.")
            return
        
        print(f"{'Match ID':<12} {'Date':<20} {'Status':<12} {'Processed':<10}")
        print("-" * 60)
        
        for match_id, date, filename, status, processed in results:
            processed_str = "Yes" if processed else "No"
            print(f"{match_id:<12} {date[:19]:<20} {status:<12} {processed_str:<10}")
    
    def export_report(self, filename: str = "match_analysis_report.txt"):
        """Export complete analysis to file."""
        import sys
        from io import StringIO
        
        # Redirect stdout to capture all output
        old_stdout = sys.stdout
        sys.stdout = buffer = StringIO()
        
        try:
            self.overview()
            self.monthly_breakdown()
            self.failure_analysis()
            self.file_analysis()
            self.download_rate_analysis()
            self.pending_work()
            
            # Get captured output
            report = buffer.getvalue()
        finally:
            sys.stdout = old_stdout
        
        # Write to file
        Path(filename).write_text(report)
        print(f"\nReport exported to: {filename}")
    
    def close(self):
        """Close database connection."""
        if self.conn:
            self.conn.close()

def main():
    """Main entry point."""
    import argparse
    
    parser = argparse.ArgumentParser(
        description="Analyze Lc0 match database and files"
    )
    parser.add_argument(
        '--report',
        choices=['overview', 'monthly', 'failures', 'files', 'rates', 'pending', 'all'],
        default='all',
        help='Type of report to generate'
    )
    parser.add_argument(
        '--search',
        type=str,
        help='Search for matches by ID or date'
    )
    parser.add_argument(
        '--export',
        type=str,
        help='Export report to file'
    )
    parser.add_argument(
        '--db',
        default='lc0_matches.db',
        help='Path to database file'
    )
    
    args = parser.parse_args()
    
    analyzer = MatchAnalyzer(args.db)
    
    try:
        if args.search:
            analyzer.search_matches(args.search)
        elif args.export:
            analyzer.export_report(args.export)
        elif args.report == 'overview':
            analyzer.overview()
        elif args.report == 'monthly':
            analyzer.monthly_breakdown()
        elif args.report == 'failures':
            analyzer.failure_analysis()
        elif args.report == 'files':
            analyzer.file_analysis()
        elif args.report == 'rates':
            analyzer.download_rate_analysis()
        elif args.report == 'pending':
            analyzer.pending_work()
        else:  # all
            analyzer.overview()
            analyzer.monthly_breakdown()
            analyzer.failure_analysis()
            analyzer.file_analysis()
            analyzer.download_rate_analysis()
            analyzer.pending_work()
    
    finally:
        analyzer.close()

if __name__ == "__main__":
    main()
