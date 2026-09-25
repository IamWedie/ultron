#!/usr/bin/env python3
"""Python CI script for ULTRON backend.

Runs:
1. compileall - byte-compile all .py files (catches syntax errors)
2. pyflakes - static analysis (undefined names, unused imports, etc.)
3. Isolated import test - verify all modules can be imported
4. Unit tests - run backend regression tests
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
        "json_store",
        "tools_extra",
        "call_audio_bridge",
        "spike_telegram_call",
        "ci",
    ]
    errors = []
    for mod in modules:
        result = subprocess.run(
            [sys.executable, "-c", f"import {mod}"],
            cwd=BACKEND_DIR,
            capture_output=True,
            text=True,
        )
        if result.returncode != 0:
            errors.append(f"{mod}: {result.stderr or result.stdout}")
    return len(errors) == 0, "\n".join(errors)


def run_unit_tests() -> tuple[bool, str]:
    result = subprocess.run(
        [
            sys.executable,
            "-m",
            "unittest",
            "discover",
            "-s",
            str(BACKEND_DIR / "tests"),
            "-p",
            "test_*.py",
        ],
        cwd=BACKEND_DIR,
        capture_output=True,
        text=True,
    )
    return result.returncode == 0, result.stdout + result.stderr


def main():
    backend_dir = Path(__file__).parent
    os.chdir(backend_dir)

    print("=" * 60)
    print("ULTRON Python Backend CI")
    print("=" * 60)

    all_passed = True

    print("\n[1/4] Running compileall...")
    ok, output = run_compileall()
    if ok:
        print("  PASS")
    else:
        print("  FAIL")
        print(output)
        all_passed = False

    print("\n[2/4] Running pyflakes...")
    ok, output = run_pyflakes()
    if ok:
        print("  PASS")
    else:
        print("  FAIL")
        print(output)
        all_passed = False

    print("\n[3/4] Running import test...")
    ok, output = run_import_test()
    if ok:
        print("  PASS")
    else:
        print("  FAIL")
        print(output)
        all_passed = False

    print("\n[4/4] Running unit tests...")
    ok, output = run_unit_tests()
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