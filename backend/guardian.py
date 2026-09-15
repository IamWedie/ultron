"""Proactive guardian for ULTRON: watches system health and raises alerts.

Pure-python helper; the decision loop lives in gemini_backend.py. Config is a
small JSON with thresholds; when exceeded, the backend asks Gemini to speak a
brief alert through the Live session.
"""

from __future__ import annotations

import json
import os
from datetime import datetime

try:
    import psutil
    _HAS_PSUTIL = True
except Exception:
    _HAS_PSUTIL = False


def _defaults() -> dict:
    return {
        "enabled": False,
        "interval": 60,        # seconds between checks
        "cpu_threshold": 90.0, # alert when CPU% this high
        "memory_threshold": 90.0,
        "battery_low": 20.0,   # alert when battery < this and unplugged
        "cooldown": 360.0,     # seconds before the same alert can fire again
        "reminders_enabled": True,  # also speak overdue task-inbox items
    }


def config_path() -> str:
    base = os.environ.get("LOCALAPPDATA", ".")
    return os.path.join(base, "Ultron", "guardian.json")


def load_config() -> dict:
    cfg = _defaults()
    try:
        with open(config_path(), "r", encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, dict):
            cfg.update({k: v for k, v in data.items() if k in cfg})
    except Exception:
        pass
    return cfg


def save_config(cfg: dict) -> None:
    try:
        os.makedirs(os.path.dirname(config_path()), exist_ok=True)
        with open(config_path(), "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2)
    except Exception:
        pass


def set_config(**kw) -> dict:
    cfg = load_config()
    for k, v in kw.items():
        if k in cfg:
            try:
                cfg[k] = float(v) if not isinstance(v, bool) else bool(v)
            except (TypeError, ValueError):
                cfg[k] = v
    save_config(cfg)
    return cfg


def collect_stats() -> dict:
    """Current system snapshot: cpu %, memory %, battery %, plugged."""
    stats: dict = {}
    if not _HAS_PSUTIL:
        return stats
    try:
        stats["cpu"] = round(psutil.cpu_percent(interval=0.3), 1)
    except Exception:
        pass
    try:
        stats["memory"] = round(psutil.virtual_memory().percent, 1)
    except Exception:
        pass
    try:
        b = psutil.sensors_battery()
        if b is not None and b.percent is not None:
            stats["battery"] = round(float(b.percent), 1)
            stats["plugged"] = bool(b.power_plugged)
    except Exception:
        pass
    return stats


def evaluate(config: dict, stats: dict) -> list[str]:
    """Return human-readable alerts for thresholds that are currently breached."""
    alerts: list[str] = []
    cpu = stats.get("cpu")
    if cpu is not None and cpu >= float(config["cpu_threshold"]):
        alerts.append(f"CPU is running hot at {cpu:.0f}% — above the {config['cpu_threshold']:.0f}% guard.")
    mem = stats.get("memory")
    if mem is not None and mem >= float(config["memory_threshold"]):
        alerts.append(f"Memory usage is at {mem:.0f}% — above the {config['memory_threshold']:.0f}% guard.")
    bat = stats.get("battery")
    if bat is not None and not stats.get("plugged", False) \
            and bat <= float(config["battery_low"]):
        alerts.append(f"Battery is down to {bat:.0f}% and not charging — consider plugging in.")
    return alerts


def describe() -> str:
    cfg = load_config()
    st = collect_stats()
    pieces = [f"CPU {st.get('cpu', '?')}%",
              f"RAM {st.get('memory', '?')}%"]
    if "battery" in st:
        pieces.append(f"Battery {st['battery']:.0f}%{' (plugged)' if st.get('plugged') else ' (on battery)'}")
    return (f"Guardian is {'ON' if cfg['enabled'] else 'OFF'}. "
            + f"Thresholds: CPU>{cfg['cpu_threshold']:.0f}%, RAM>{cfg['memory_threshold']:.0f}%, "
            + f"battery<{cfg['battery_low']:.0f}%. Now: {', '.join(pieces)}.")