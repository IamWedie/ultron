"""
Small "quality of life" tool ports from Mark-LII that need no UI:
  * game_updater       — scan installed games (Steam / Epic / Battle.net dirs)
  * flight_finder      — build a Google Flights deep link; live fares go via web_search
  * audio_devices      — list available input devices; pick one via IPC
"""

from __future__ import annotations

import json
import os
import re
from pathlib import Path


# ────────────────────────────── game_updater ──────────────────────────────
def scan_games() -> str:
    roots = []
    pf = os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")
    pf64 = os.environ.get("ProgramFiles", r"C:\Program Files")

    steam = os.path.join(pf, "Steam", "steamapps", "common")
    roots.append(steam)
    libs = os.path.join(pf, "Steam", "steamapps", "libraryfolders.vdf")
    if os.path.exists(libs):
        for m in re.finditer(r'"path"\s+"([^"]+)"', _read(libs)):
            roots.append(str(Path(m.group(1)) / "steamapps" / "common"))

    for base in (pf64, pf):
        roots.append(str(Path(base) / "Epic Games"))
        roots.append(str(Path(base) / "Battle.net"))
        roots.append(str(Path(base) / "GOG Galaxy" / "Games"))

    found = []
    for root in roots:
        p = Path(root)
        if not p.is_dir():
            continue
        for child in sorted(p.iterdir(), key=lambda c: c.name.lower()):
            if child.is_dir() and not child.name.startswith("."):
                size = _dir_size(child)
                found.append(f"• {child.name}  ({_fmt(size)})")

    if not found:
        return "No installed games found in the usual Steam/Epic/Battle.net folders."
    # Cap the transcript length; Gemini can follow up for a specific title.
    list_str = "\n".join(found[:60])
    more = f"\n...and {len(found) - 60} more." if len(found) > 60 else ""
    return f"Installed games ({len(found)}):\n{list_str}{more}"


def _read(path):
    try:
        return Path(path).read_text(encoding="utf-8", errors="ignore")
    except Exception:
        return ""


def _dir_size(p: Path) -> int:
    total = 0
    for f in p.rglob("*"):
        try:
            if f.is_file():
                total += f.stat().st_size
        except Exception:
            pass
        if total > 100 * 2**30:   # don't scan forever
            break
    return total


def _fmt(n: int) -> str:
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024:
            return f"{n:.0f} {unit}"
        n /= 1024
    return f"{n:.1f} PB"


# ────────────────────────────── flight_finder ─────────────────────────────
def flight_url(origin: str, destination: str, date: str = "", budget: str = "",
               roundtrip: bool = True) -> str:
    import urllib.parse
    params = {
        "q": f"{origin} to {destination}",
        "curr": "USD",
    }
    if date:
        params["df"] = date
    if budget:
        params["budget"] = budget
    if not roundtrip:
        params["rt"] = "0"
    url = "https://www.google.com/travel/flights?" + urllib.parse.urlencode(params)
    return url


# ────────────────────────────── audio devices ─────────────────────────────
def list_input_devices() -> str:
    try:
        import sounddevice as sd
        defaults = sd.default
        lines = []
        for i, d in enumerate(sd.query_devices()):
            if d["max_input_channels"] > 0:
                mark = " *" if i == defaults.device[0] or (defaults.device[0] is None and i == sd.default.device) else ""
                name = str(d["name"])
                lines.append(f"[{i}] {name} ({d['max_input_channels']}ch) @{int(d['default_samplerate'])}{mark}")
        if not lines:
            return "No input devices found."
        return "Input devices:\n" + "\n".join(lines)
    except Exception as e:
        return f"Could not list audio devices: {e}"


if __name__ == "__main__":
    print(scan_games()[:500])