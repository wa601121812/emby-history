#!/usr/bin/env python3
"""Run the linked production control-flow suite and retain evidence only in RetryTests/."""
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
DOTNET = os.environ.get("DOTNET", "dotnet")
SOURCES = [ROOT / "Plugin" / name for name in ["EntryPoint.cs", "HistoryWorkPlanner.cs", "HistoryStatusService.cs", "Configuration.cs", "PosterCache.cs"]] + sorted((ROOT / "Core").glob("*.cs"))
DEPS = sorted(p for p in list((ROOT / "sdk-current").glob("MediaBrowser.*.dll")) + list((HERE / "sdk-dependencies").glob("*.dll")) if p.exists())

def hashes(paths):
    return {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest() for path in paths}

before = hashes(SOURCES)
command = [DOTNET, "run", "--project", str(HERE / "RetryTests.csproj"), "-c", "Release"]
result = subprocess.run(command, cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
print(result.stdout, end="")
after = hashes(SOURCES)
(HERE / "run.log").write_text(result.stdout)
match = re.search(r"RESULT (\d+)/(\d+) passed; (\d+) failed", result.stdout)
evidence = {"utc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "command": command,
            "exit_code": result.returncode, "source_hashes": before, "sdk_hashes": hashes(DEPS),
            "linked_sources_stable_during_run": before == after,
            "passed": int(match[1]) if match else None, "total": int(match[2]) if match else None,
            "failed": int(match[3]) if match else None,
            "mode": "linked production EntryPoint/ledger; real SDK query types; mocked archive/index/access boundaries; no production API"}
(HERE / "verification.json").write_text(json.dumps(evidence, indent=2) + "\n")
if before != after:
    print("ERROR linked source changed while the test ran; rerun required")
    sys.exit(2)
sys.exit(result.returncode)
