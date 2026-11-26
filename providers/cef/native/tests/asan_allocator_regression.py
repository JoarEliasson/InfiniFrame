"""Standalone address-sanitizer adapter checks; never loads CEF."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument("--controls", type=Path, required=True)
parser.add_argument("--logs", type=Path)
options = parser.parse_args()
logs = options.logs or Path(tempfile.mkdtemp(prefix="if-cef-asan-controls-"))
logs.mkdir(parents=True, exist_ok=True)
environment = os.environ.copy()
for key in ("ASAN_OPTIONS", "LSAN_OPTIONS", "UBSAN_OPTIONS", "LD_PRELOAD"):
    environment.pop(key, None)
expected = {
    "positive": None,
    "oob": "heap-buffer-overflow",
    "oob-calloc": "heap-buffer-overflow",
    "oob-realloc": "heap-buffer-overflow",
    "uaf": "heap-use-after-free",
    "doublefree": "double-free",
    "size-invalid": "malloc_usable_size",
    "size-interior": "malloc_usable_size",
    "size-freed": "malloc_usable_size",
}
results = []
for name, diagnostic in expected.items():
    with (logs / (name + ".stdout.log")).open("w") as stdout, (logs / (name + ".stderr.log")).open("w") as stderr:
        result = subprocess.run([str(options.controls.resolve()), name], env=environment,
                                stdout=stdout, stderr=stderr, timeout=15)
    output = (logs / (name + ".stdout.log")).read_text(errors="replace")
    errors = (logs / (name + ".stderr.log")).read_text(errors="replace")
    passed = (result.returncode == 0 and "PASS allocator families" in output) if diagnostic is None else (
        result.returncode != 0 and "AddressSanitizer" in errors and diagnostic in errors
        and "FAIL intended ASan violation" not in errors)
    row = {"case": name, "exit": result.returncode, "expectedDiagnostic": diagnostic, "passed": passed}
    results.append(row)
    print(json.dumps(row), flush=True)
    (logs / "results.json").write_text(json.dumps(results, indent=2) + "\n")
assert all(row["passed"] for row in results), results
