"""
Periodic topic monitor for ULTRON.

Two tools feed this:
  * manage_monitor        — add/remove/list the topics ULTRON keeps an eye on.
  * background_monitor    — turn the periodic checks on/off, or run one now.

Checks are stored in %LOCALAPPDATA%\\Ultron\\monitors.json so they survive
restarts. The automatic loop lives in gemini_backend.py; this module is
persistence + due-bookkeeping only.
"""

from __future__ import annotations

import json
import os
import time
from datetime import datetime


def store_path() -> str:
    base = os.environ.get("LOCALAPPDATA", ".")
    return os.path.join(base, "Ultron", "monitors.json")


def _load() -> dict:
    try:
        with open(store_path(), "r", encoding="utf-8") as f:
            data = json.load(f)
            if isinstance(data, dict):
                return data
    except Exception:
        pass
    return {}


def _save(data: dict):
    os.makedirs(os.path.dirname(store_path()), exist_ok=True)
    with open(store_path(), "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)


def list_topics() -> list:
    return sorted(_load().keys())


def add_topic(topic: str) -> str:
    t = topic.strip()
    if not t:
        return "No topic given."
    data = _load()
    if t in data:
        return f"Already monitoring: {t}"
    mark = datetime.now().strftime("%Y-%m-%d %H:%M")
    data[t] = {"added": mark, "last_checked": None}
    _save(data)
    return f"Now monitoring: {t}"


def remove_topic(topic: str) -> str:
    data = _load()
    for key in data:
        if key.lower() == topic.strip().lower():
            del data[key]
            _save(data)
            return f"Stopped monitoring: {key}"
    return f"Not monitoring '{topic}'."


def mark_checked(topics: list):
    data = _load()
    updated = False
    for t in topics:
        if t in data:
            data[t]["last_checked"] = datetime.now().strftime("%Y-%m-%d %H:%M")
            updated = True
    if updated:
        _save(data)


def due_topics(interval_sec: float) -> list:
    """Topics whose last successful check is older than `interval_sec`, plus
    never-checked ones. Returns list of (topic, last_checked_str_or_None)."""
    data = _load()
    now = time.time()
    out = []
    for t, meta in data.items():
        last = meta.get("last_checked")
        if not last:
            out.append((t, None))
            continue
        try:
            lc = time.mktime(datetime.strptime(last, "%Y-%m-%d %H:%M").timetuple())
        except Exception:
            out.append((t, None))
            continue
        if now - lc >= interval_sec:
            out.append((t, last))
    return out