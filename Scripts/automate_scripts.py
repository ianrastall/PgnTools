import subprocess
import time
from pathlib import Path
import sys
import shutil

def main():
    if len(sys.argv) > 1:
        script_dir = Path(sys.argv[1]).resolve()
    else:
        script_dir = Path(__file__).parent.resolve()

    # Find the targeted snapshot scripts
    dump_scripts = list(script_dir.glob("code-dump-*.ps1"))
    export_scripts = list(script_dir.glob("Export-*.ps1"))
    
    # Combine and sort alphabetically
    target_scripts = sorted(dump_scripts + export_scripts)

    if not target_scripts:
        print(f"No context generation scripts found in {script_dir}.")
        return

    print(f"Found {len(target_scripts)} context generation scripts.")
    print("Starting batch execution...\n")

    # CRITICAL FIX: Prioritize PowerShell 7 (pwsh) over legacy Windows PowerShell (powershell.exe)
    ps_exe = "pwsh" if shutil.which("pwsh") else "powershell.exe"
    
    start_time = time.time()
    success_count = 0

    for script in target_scripts:
        print(f"Running: {script.name} ... ", end="", flush=True)
        
        cmd = [
            ps_exe,
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", str(script)
        ]
        
        try:
            result = subprocess.run(cmd, capture_output=True, text=True, check=True)
            print("Done ✓")
            success_count += 1
        except subprocess.CalledProcessError as e:
            print("Failed ✗")
            print("--- Error Details ---")
            error_output = e.stderr.strip() if e.stderr else e.stdout.strip()
            print(error_output)
            print("---------------------\n")

    elapsed = time.time() - start_time
    print(f"\nBatch execution complete in {elapsed:.2f} seconds.")
    print(f"Successfully generated {success_count} out of {len(target_scripts)} context snapshots.")

if __name__ == "__main__":
    main()