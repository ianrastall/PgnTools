#!/usr/bin/env python3
"""
Quick Start Guide for Lc0 Match Downloader
===========================================

This script demonstrates basic usage patterns and provides
interactive examples for common tasks.
"""

import subprocess
import sys
from pathlib import Path

def print_header(text):
    """Print a formatted header."""
    print("\n" + "=" * 70)
    print(f"  {text}")
    print("=" * 70 + "\n")

def run_command(cmd, description):
    """Run a command and display output."""
    print(f"Running: {description}")
    print(f"Command: {' '.join(cmd)}\n")
    
    try:
        result = subprocess.run(cmd, capture_output=True, text=True)
        print(result.stdout)
        if result.stderr:
            print("Errors:", result.stderr)
        return result.returncode == 0
    except Exception as e:
        print(f"Error: {e}")
        return False

def check_setup():
    """Check if environment is set up correctly."""
    print_header("CHECKING SETUP")
    
    # Check Python version
    print(f"Python version: {sys.version}")
    
    # Check if main script exists
    main_script = Path("lc0_match_downloader.py")
    if main_script.exists():
        print("✓ Main script found")
    else:
        print("✗ Main script not found - please ensure lc0_match_downloader.py is in current directory")
        return False
    
    # Check dependencies
    try:
        import aiohttp
        import bs4
        print("✓ Required packages installed")
    except ImportError as e:
        print(f"✗ Missing dependency: {e}")
        print("  Run: pip install -r requirements.txt")
        return False
    
    print("\n✓ Setup complete!\n")
    return True

def demo_scrape_sample():
    """Demo: Scrape just first few pages."""
    print_header("DEMO 1: Scraping Sample Data")
    print("This will scrape the first 10 pages as a test.")
    print("(To scrape all pages, use the full --mode scrape command)")
    
    # Note: This is a simplified demo - actual implementation would need
    # to modify Config.TOTAL_PAGES or add a --pages argument
    print("\nFor a full scrape, run:")
    print("  python lc0_match_downloader.py --mode scrape")
    print("\nThis creates:")
    print("  - lc0_matches_full.csv (CSV export)")
    print("  - lc0_matches.db (SQLite database)")

def demo_download_limited():
    """Demo: Download limited number of matches."""
    print_header("DEMO 2: Download Limited Matches")
    print("This will download the first 10 matches as a test.")
    
    cmd = ["python", "lc0_match_downloader.py", "--mode", "download", "--limit", "10"]
    
    response = input("\nRun this demo? (y/n): ")
    if response.lower() == 'y':
        run_command(cmd, "Downloading 10 matches")
    else:
        print("Skipped.")

def demo_view_stats():
    """Demo: View current statistics."""
    print_header("DEMO 3: View Statistics")
    
    cmd = ["python", "lc0_match_downloader.py", "--mode", "stats"]
    
    response = input("\nView current stats? (y/n): ")
    if response.lower() == 'y':
        run_command(cmd, "Viewing statistics")
    else:
        print("Skipped.")

def demo_process():
    """Demo: Process downloaded matches."""
    print_header("DEMO 4: Process Downloaded Matches")
    print("This will process all successfully downloaded matches.")
    print("Output will be organized in: processed/YYYY/MM/")
    
    cmd = ["python", "lc0_match_downloader.py", "--mode", "process"]
    
    response = input("\nProcess downloaded matches? (y/n): ")
    if response.lower() == 'y':
        run_command(cmd, "Processing matches")
    else:
        print("Skipped.")

def show_common_workflows():
    """Show common workflow examples."""
    print_header("COMMON WORKFLOWS")
    
    workflows = [
        {
            "name": "Complete Pipeline",
            "description": "Scrape, download, and process everything",
            "command": "python lc0_match_downloader.py --mode all"
        },
        {
            "name": "Daily Update",
            "description": "Download new matches and process them",
            "commands": [
                "python lc0_match_downloader.py --mode scrape",
                "python lc0_match_downloader.py --mode download",
                "python lc0_match_downloader.py --mode process"
            ]
        },
        {
            "name": "Resume Interrupted Download",
            "description": "Continue downloading from where you left off",
            "command": "python lc0_match_downloader.py --mode download"
        },
        {
            "name": "Test Run",
            "description": "Test with a small sample before full download",
            "commands": [
                "# 1. Scrape metadata first",
                "python lc0_match_downloader.py --mode scrape",
                "# 2. Download just 100 matches",
                "python lc0_match_downloader.py --mode download --limit 100",
                "# 3. Process them",
                "python lc0_match_downloader.py --mode process"
            ]
        }
    ]
    
    for i, workflow in enumerate(workflows, 1):
        print(f"\n{i}. {workflow['name']}")
        print(f"   {workflow['description']}")
        print()
        
        if 'command' in workflow:
            print(f"   {workflow['command']}")
        else:
            for cmd in workflow['commands']:
                print(f"   {cmd}")
    
    print()

def show_database_queries():
    """Show useful SQLite queries."""
    print_header("USEFUL DATABASE QUERIES")
    
    queries = [
        {
            "description": "View first 10 matches",
            "query": "SELECT * FROM matches LIMIT 10;"
        },
        {
            "description": "Count by status",
            "query": "SELECT download_status, COUNT(*) FROM matches GROUP BY download_status;"
        },
        {
            "description": "Recent failures",
            "query": "SELECT match_id, message FROM download_log WHERE status = 'failed' ORDER BY timestamp DESC LIMIT 10;"
        },
        {
            "description": "Matches by month",
            "query": "SELECT strftime('%Y-%m', date) as month, COUNT(*) FROM matches GROUP BY month ORDER BY month;"
        },
        {
            "description": "Unprocessed downloads",
            "query": "SELECT COUNT(*) FROM matches WHERE download_status = 'success' AND processed = 0;"
        }
    ]
    
    print("Connect to database with:")
    print("  sqlite3 lc0_matches.db\n")
    
    for i, q in enumerate(queries, 1):
        print(f"\n{i}. {q['description']}")
        print(f"   {q['query']}")
    
    print()

def main():
    """Main interactive guide."""
    print("""
╔════════════════════════════════════════════════════════════════════╗
║                                                                    ║
║              Lc0 Match Downloader - Quick Start Guide             ║
║                                                                    ║
╚════════════════════════════════════════════════════════════════════╝
    """)
    
    if not check_setup():
        print("\nPlease fix setup issues before continuing.")
        return
    
    while True:
        print("\nWhat would you like to do?")
        print("  1. View common workflows")
        print("  2. Run demo: Download 10 matches")
        print("  3. View statistics")
        print("  4. Process downloaded matches")
        print("  5. Show database queries")
        print("  6. Exit")
        
        choice = input("\nEnter choice (1-6): ").strip()
        
        if choice == '1':
            show_common_workflows()
        elif choice == '2':
            demo_download_limited()
        elif choice == '3':
            demo_view_stats()
        elif choice == '4':
            demo_process()
        elif choice == '5':
            show_database_queries()
        elif choice == '6':
            print("\nGoodbye!\n")
            break
        else:
            print("\nInvalid choice. Please enter 1-6.")

if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\n\nExiting...\n")
    except Exception as e:
        print(f"\nError: {e}\n")
