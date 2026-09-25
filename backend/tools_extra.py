"""
Small "quality of life" tool ports from Mark-LII that need no UI:
  * audio_devices      — list available input devices; pick one via IPC
"""

from __future__ import annotations

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
    print(list_input_devices())