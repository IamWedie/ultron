#!/usr/bin/env python3
"""Python CI script for ULTRON backend.

Runs:
1. compileall - byte-compile all .py files (catches syntax errors)
2. pyflakes - static analysis (undefined names, unused imports, etc.)
3. Import test - verify all modules can be imported without errors
"""

import subprocess
import sys
import os
from pathlib import Path


BACKEND_DIR = Path(__file__).parent


def run_compileall() -> tuple[bool, str]:
    """Run python -m compileall on the backend directory."""
    result = subprocess.run(
        [sys.executable, "-m", "compileall", "-q", str(BACKEND_DIR)],
        capture_output=True,
        text=True,
    )
    return result.returncode == 0, result.stdout + result.stderr


def run_pyflakes() -> tuple[bool, str]:
    """Run pyflakes on the backend directory."""
    result = subprocess.run(
        [sys.executable, "-m", "pyflakes", str(BACKEND_DIR)],
        capture_output=True,
        text=True,
    )
    return result.returncode == 0, result.stdout + result.stderr


def run_import_test() -> tuple[bool, str]:
    """Verify all modules can be imported without errors."""
    modules = [
        "gemini_backend",
        "telegram_client",
        "voice_dsp",
        "computer_use",
        "guardian",
        "monitor",
        "productivity",
        "tools_extra",
    ]
    errors = []
    for mod in modules:
        try:
            __import__(mod)
        except Exception as e:
            errors.append(f"{mod}: {e}")
    return len(errors) == 0, "\n".join(errors)


def main():
    backend_dir = Path(__file__).parent
    os.chdir(backend_dir)

    print("=" * 60)
    print("ULTRON Python Backend CI")
    print("=" * 60)

    all_passed = True

    print("\n[1/3] Running compileall...")
    ok, output = run_compileall()
    if ok:
        print("  PASS")
    else:
        print("  FAIL")
        print(output)
        all_passed = False

    print("\n[2/3] Running pyflakes...")
    ok, output = run_pyflakes()
    if ok:
        print("  PASS")
    else:
        print("  FAIL")
        print(output)
        all_passed = False

    print("\n[3/3] Running import test...")
    ok, output = run_import_test()
    if ok:
        print("  PASS")
    else:
        print("  FAIL")
        print(output)
        all_passed = False

    print("\n" + "=" * 60)
    if all_passed:
        print("ALL CHECKS PASSED")
        return 0
    else:
        print("SOME CHECKS FAILED")
        return 1


if __name__ == "__main__":
    sys.exit(main())