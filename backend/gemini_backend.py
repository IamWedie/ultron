"""
ULTRON Gemini Live Backend
Handles Gemini Live API audio streaming + tool dispatch + IPC to C# WinUI frontend.

Protocol (JSON lines over stdin/stdout):
  C# → Python:
    {"type":"start","api_key":"...","voice":"Charon"}
    {"type":"stop"}
    {"type":"text","text":"hello"}           # text command (no mic needed)
    {"type":"tool_result","id":"...","result":"..."}
    {"type":"interrupt"}                     # stop Gemini speaking

  Python → C#:
    {"type":"status","state":"connecting|connected|listening|speaking|error","message":"..."}
    {"type":"transcript","role":"user|model","text":"..."}
    {"type":"tool_call","id":"...","name":"...","args":{...}}
    {"type":"error","message":"..."}
"""

import asyncio
import json
import sys
import os
import io
import base64
import struct
import time
import traceback
from datetime import datetime

# --- google-genai imports ---
from google import genai
from google.genai import types

# --- audio ---
import sounddevice as sd
import numpy as np

# --- vision (screen + webcam capture) ---
try:
    import mss
    import mss.tools
    _HAS_MSS = True
except ImportError:
    _HAS_MSS = False

# --- Ultron voice DSP (output post-processing) ---
try:
    from voice_dsp import UltronVoice
    _HAS_DSP = True
except ImportError:
    UltronVoice = None
    _HAS_DSP = False

# --- Mark-LII tool ports (Python side) ---
import monitor as monitor_store
import tools_extra
import dev_agent
import computer_use
import guardian
import productivity

try:
    from PIL import Image
    _HAS_PIL = True
except ImportError:
    _HAS_PIL = False

try:
    import cv2
    _HAS_CV2 = True
except ImportError:
    _HAS_CV2 = False

# --- silence / VAD ---
import wave
import collections

try:
    import onnxruntime as ort
    _HAS_ORT = True
except ImportError:
    _HAS_ORT = False

# ============================================================
# CONFIG
# ============================================================

LIVE_MODEL = "models/gemini-2.5-flash-native-audio-preview-12-2025"
CHANNELS = 1
SEND_SAMPLE_RATE = 16000    # mic
RECEIVE_SAMPLE_RATE = 24000 # speaker
CHUNK_SIZE = 1024           # frames per audio block

AVAILABLE_VOICES = ["Charon", "Puck", "Kore", "Fenrir", "Aoede"]
DEFAULT_VOICE = "Charon"

# Personality knobs (plumbed from the C# app via `start` and `set_personality`).
# Each key keeps a tow-description so the model can act on the exact dial value
# without re-explaining the scale on every reconnect.
DEFAULT_PERSONA = {
    "humor": 0,      # -2 serious ... +2 playful; 0 = default detached Ultron tone
    "length": 1,     # 0 ultra-short, 1 balanced, 2 verbose
    "suggest": 1,    # 0 only when asked, 1 moderate, 2 proactive
    "notes": "",     # free-text user preferences / standing instructions
}

# ============================================================
# VAD (Silero) — gates mic audio so ambient noise isn't streamed
# ============================================================

class SileroVad:
    """Minimal Silero-VAD ONNX wrapper (matches C# SileroVad usage).
    input: [1,576] float (64 ctx + 512 samples @16k), state [2,1,128]; sr=16000.
    Returns probability that the frame contains speech."""

    def __init__(self, model_path: str):
        if not _HAS_ORT:
            raise RuntimeError("onnxruntime not installed; VAD unavailable")
        self.sess = ort.InferenceSession(model_path, providers=["CPUExecutionProvider"])
        self.state = np.zeros((2, 1, 128), dtype=np.float32)
        self.context = np.zeros(64, dtype=np.float32)
        self.sr = np.array([SEND_SAMPLE_RATE], dtype=np.int64)
        self.threshold = 0.5

    def reset(self):
        self.state = np.zeros((2, 1, 128), dtype=np.float32)
        self.context = np.zeros(64, dtype=np.float32)

    def process(self, chunk: np.ndarray) -> float:
        """chunk: float32 1-D array (length <= 512) at SEND_SAMPLE_RATE."""
        input_ = np.zeros(64 + 512, dtype=np.float32)
        kept = min(len(self.context), 64)
        if kept:
            input_[64 - kept:64] = self.context[-kept:]
        n = min(len(chunk), 512)
        input_[64:64 + n] = chunk[:n]
        prob, state = self.sess.run(None, {
            "input": input_.reshape(1, 576),
            "state": self.state,
            "sr": self.sr,
        })
        self.state = state
        self.context = input_[64 + 512 - 64:64 + 512].copy()
        return float(prob[0][0])


class VADGate:
    """Runs Silero VAD on mic frames and only forwards speech (plus a short
    leading pad and trailing tail) to the Gemini input queue."""

    def __init__(self, model_path: str):
        self.vad = SileroVad(model_path)
        self.lead_buffer = collections.deque(maxlen=8)   # ~492ms at CHUNK_SIZE(1024)
        self.tailing = 0
        self.talk_ticks = 14   # keep streaming ~0.9s after last speech tick
        self.speech = False

    def process_and_emit(self, float_chunk: np.ndarray, emit):
        """float_chunk: float32 mono (CHUNK_SIZE samples). emit(bytes) drains speech."""
        self.lead_buffer.append(float_chunk.copy())
        prob = self.vad.process(float_chunk)
        speech = prob >= self.vad.threshold
        if speech:
            self.speech = True
            self.tailing = self.talk_ticks
            # flush the lead buffer + this chunk so we catch speech onset
            while self.lead_buffer:
                c = self.lead_buffer.popleft()
                emit((c * 32767).astype(np.int16).tobytes())
            emit((float_chunk * 32767).astype(np.int16).tobytes())
        else:
            if self.tailing > 0:
                self.tailing -= 1
                emit((float_chunk * 32767).astype(np.int16).tobytes())
                if self.tailing == 0:
                    self.speech = False

# ============================================================
# VISION (screen + webcam capture on demand)
# ============================================================

_VISION_MAX_W = 1280
_VISION_MAX_H = 720
_VISION_JPEG_Q = 82
_VISION_COOLDOWN = 4.0  # seconds — echoes of Gemini's own voice must not retrigger a capture


def _compress_image(img_bytes: bytes, source_format: str = "PNG") -> tuple[bytes, str]:
    """Downscale + re-encode to JPEG so the image fits comfortably in one
    Gemini message. Returns (data, mime)."""
    if not _HAS_PIL:
        return img_bytes, f"image/{source_format.lower()}"
    try:
        img = Image.open(io.BytesIO(img_bytes)).convert("RGB")
        img.thumbnail((_VISION_MAX_W, _VISION_MAX_H))
        buf = io.BytesIO()
        img.save(buf, format="JPEG", quality=_VISION_JPEG_Q, optimize=False)
        return buf.getvalue(), "image/jpeg"
    except Exception as e:
        print(f"[Vision] Image compress failed: {e}", file=sys.stderr)
        return img_bytes, f"image/{source_format.lower()}"


def _capture_screen() -> tuple[bytes, str]:
    if not _HAS_MSS:
        raise RuntimeError("Screen capture requires the 'mss' package (pip install mss)")
    with mss.mss() as sct:
        monitors = sct.monitors           # [0] = all monitors, [1..n] = real screens
        target = monitors[1] if len(monitors) > 1 else monitors[0]
        shot = sct.grab(target)
        png = mss.tools.to_png(shot.rgb, shot.size)
    return _compress_image(png, "PNG")


def _capture_camera() -> tuple[bytes, str]:
    if not _HAS_CV2:
        raise RuntimeError("Camera capture requires OpenCV (pip install opencv-python-headless)")
    cap = cv2.VideoCapture(0, cv2.CAP_DSHOW)
    if not cap.isOpened():
        cap = cv2.VideoCapture(0)
    if not cap.isOpened():
        raise RuntimeError("Webcam could not be opened.")
    try:
        for _ in range(10):
            cap.read()                    # warmup so auto-exposure settles
        ret, frame = cap.read()
    finally:
        cap.release()
    if not ret or frame is None:
        raise RuntimeError("Webcam returned no frame.")
    rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
    if _HAS_PIL:
        img = Image.fromarray(rgb)
        img.thumbnail((_VISION_MAX_W, _VISION_MAX_H))
        buf = io.BytesIO()
        img.save(buf, format="JPEG", quality=_VISION_JPEG_Q, optimize=False)
        return buf.getvalue(), "image/jpeg"
    _, buf = cv2.imencode(".jpg", frame, [cv2.IMWRITE_JPEG_QUALITY, _VISION_JPEG_Q])
    return buf.tobytes(), "image/jpeg"


TOOL_DECLARATIONS = [
    {
        "name": "system_status",
        "description": "Get current CPU, RAM, disk usage, and running processes.",
        "parameters": {"type": "OBJECT", "properties": {}},
    },
    {
        "name": "screen_process",
        "description": (
            "Captures the screen or the webcam and analyzes the image. "
            "MUST be called when the user asks what is on screen, what you see, "
            "look at my screen, analyze this, look at the camera, etc. You have NO "
            "visual ability without this tool. After the capture, the image is sent "
            "to you and you should describe what you see and answer the user's question. "
            "angle 'camera' (default 'screen') grabs one webcam snapshot."
        ),
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "angle": {"type": "STRING", "description": "'screen' (default) or 'camera'"},
                "text": {"type": "STRING", "description": "The question or instruction about the captured image"},
            },
            "required": ["text"],
        },
    },
    {
        "name": "close_camera",
        "description": "Closes/releases the webcam after a screen_process camera capture. Call when the user says close the camera, stop the camera, turn it off, etc.",
        "parameters": {"type": "OBJECT", "properties": {}},
    },
    {
        "name": "set_live_vision",
        "description": (
            "Toggles ambient webcam vision. When ON, a fresh webcam frame is captured "
            "after every exchange and kept as context, so you can see whatever is in "
            "front of the user while talking (what's on the table, who is in the room...). "
            "Call when the user asks you to watch, keep an eye on things, turn ON your eyes, "
            "or otherwise enable/disable continuous vision."
        ),
        "parameters": {
            "type": "OBJECT",
            "properties": {"enabled": {"type": "BOOLEAN", "description": "true to enable live webcam vision, false to disable"}},
            "required": ["enabled"],
        },
    },
    {
        "name": "set_voice",
        "description": (
            "Real-time control of the Ultron voice processor applied to every word you speak. "
            "Call when the user asks to change your voice, sound deeper/more robotic/human, "
            "raise/lower the pitch, turn the effect on or off, make it sound more metallic etc. "
            "Defaults if omitted: enabled=true, pitch=-3.5 (semitones), chorus=0.4, bass=5.0 "
            "(dB boost), darken=0.5. Positive pitch = higher, negative = deeper."
        ),
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "enabled": {"type": "BOOLEAN", "description": "Turn the effect on (true) or off (false)"},
                "pitch": {"type": "NUMBER", "description": "Pitch shift in semitones. Negative = deeper. -3.5 is the Ultron default, 0 = natural."},
                "chorus": {"type": "NUMBER", "description": "Metallic shimmer wet mix, 0..1 (0.4 default)"},
                "bass": {"type": "NUMBER", "description": "Low-shelf boost in dB (5.0 default)"},
                "darken": {"type": "NUMBER", "description": "High-frequency darkening, 0..1 (0.5 default)"},
            },
        },
    },
    {
        "name": "type_text",
        "description": "Type plain TEXT into the focused window on the PC. Use ONLY for literal words/characters the user wants written. If the user asks to CUT/COPY/PASTE/DELETE/SELECT-ALL/undo or press a key or shortcut (e.g. Enter, Tab, Ctrl+A, Delete), use press_key instead — never type placeholder text like {Ctrl+A} here.",
        "parameters": {
            "type": "OBJECT",
            "properties": {"text": {"type": "STRING", "description": "Literal text to type as-is"}},
            "required": ["text"],
        },
    },
    {
        "name": "press_key",
        "description": "Press a keyboard key or shortcut into the focused window, like the user physically pressing it. Examples: 'ctrl+a' (select all), 'ctrl+c', 'ctrl+v', 'enter', 'tab', 'backspace', 'delete', 'escape', 'arrowup'/'pageup'/'home', 'alt+tab', 'ctrl+s'. Use this when the user says 'delete all', 'select everything', 'copy', 'paste', 'press enter', 'go to start of line', undo as a hotkey, etc. NEVER use type_text for these.",
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "keys": {"type": "STRING", "description": "Shortcut, e.g. 'ctrl+a' (lowercase tokens joined by '+')"},
                "repeat": {"type": "INTEGER", "description": "Optional: press this many times (e.g. delete 5 characters = backspace x5). Default 1."},
            },
            "required": ["keys"],
        },
    },
    {
        "name": "open_app",
        "description": "Launch an installed application on this Windows PC. Give the app's display name (e.g. 'Steam', 'Spotify', 'Chrome', 'Notepad', 'Discord'). You do NOT need a file path — the system resolves installed apps for you. If the user says a game/program, prefer this tool.",
        "parameters": {
            "type": "OBJECT",
            "properties": {"app_name": {"type": "STRING", "description": "Display name of the app, e.g. 'Steam' or 'Google Chrome'"}},
            "required": ["app_name"],
        },
    },
    {
        "name": "web_search",
        "description": "Search the web for information.",
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "query": {"type": "STRING", "description": "Search query"},
                "mode": {"type": "STRING", "description": "Search mode: web, news, images"},
            },
            "required": ["query"],
        },
    },
    {
        "name": "weather_report",
        "description": "Get current weather for a city.",
        "parameters": {
            "type": "OBJECT",
            "properties": {"city": {"type": "STRING", "description": "City name"}},
            "required": ["city"],
        },
    },
    {
        "name": "reminder",
        "description": "Set a reminder with date, time, and message.",
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "date": {"type": "STRING", "description": "Date (YYYY-MM-DD or 'today'/'tomorrow')"},
                "time": {"type": "STRING", "description": "Time (HH:MM)"},
                "message": {"type": "STRING", "description": "Reminder message"},
            },
            "required": ["date", "time", "message"],
        },
    },
    {
        "name": "save_memory",
        "description": "Store a fact about the user permanently.",
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "category": {"type": "STRING", "description": "Category (identity, preferences, projects, relationships, wishes, notes)"},
                "key": {"type": "STRING", "description": "Key name for this fact"},
                "value": {"type": "STRING", "description": "The fact to remember"},
            },
            "required": ["category", "key", "value"],
        },
    },
    {
        "name": "recall_memory",
        "description": "Search stored facts and memory.",
        "parameters": {
            "type": "OBJECT",
            "properties": {"query": {"type": "STRING", "description": "Search query"}},
            "required": ["query"],
        },
    },
    {
        "name": "browser_control",
        "description": "Control the web browser: open URL, search, click, type, scroll.",
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "action": {"type": "STRING", "description": "Action: open_url, search, click, type, scroll, screenshot"},
                "url": {"type": "STRING", "description": "URL to open"},
                "query": {"type": "STRING", "description": "Search query or text to type"},
            },
            "required": ["action"],
        },
    },
    {
        "name": "file_processor",
        "description": "Work with files and folders on the PC. Actions: (1) 'list'/'browse' a folder (folder path; returns a structured listing with sizes), (2) 'read' a text file, (3) 'write' content to a file (path + content; undoable), (4) 'search' for files by name under a folder (path = root, query = filename substring), (5) 'rename'/'move' a file or folder (file_path = source, destination = new path; undoable), (6) 'copy' a file or folder (file_path = source, destination = target; undoable), (7) 'delete' a file or folder (file_path only; undoable unless too large). Every mutating action can be reversed by the user saying 'undo'.",
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "action": {"type": "STRING", "description": "Action: list, browse, read, write, search, rename, move, copy, delete"},
                "file_path": {"type": "STRING", "description": "Folder (for list/browse/search/delete) or file path (for read/write/rename/move/copy/delete)"},
                "destination": {"type": "STRING", "description": "Target path for rename/move/copy"},
                "content": {"type": "STRING", "description": "Content to write"},
                "query": {"type": "STRING", "description": "Filename substring to search for"},
            },
            "required": ["action"],
        },
    },
    {
        "name": "computer_settings",
        "description": "Control PC settings: volume, brightness, wifi, bluetooth, shutdown, restart, sleep, lock screen.",
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "action": {"type": "STRING", "description": "Action: volume_up, volume_down, volume_set, mute, unmute, brightness_up, brightness_down, wifi_on, wifi_off, bluetooth_on, bluetooth_off, shutdown, restart, sleep, lock, screenshot"},
                "value": {"type": "NUMBER", "description": "Optional numeric value (e.g. volume level 0-100)"},
            },
            "required": ["action"],
        },
    },
    {
        "name": "desktop_control",
        "description": (
            "Control the desktop mouse and screen. Actions: "
            "move_mouse (x,y in screen pixels), click (left click at x,y), "
            "double_click, right_click, scroll (amount = wheel notches, positive down), "
            "focus_window (report/bring the active window forward), screenshot."
        ),
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "action": {"type": "STRING", "description": "Action: screenshot, move_mouse, click, double_click, right_click, scroll, focus_window"},
                "x": {"type": "INTEGER", "description": "X coordinate in screen pixels"},
                "y": {"type": "INTEGER", "description": "Y coordinate in screen pixels"},
                "amount": {"type": "INTEGER", "description": "Scroll amount in wheel notches (positive = down/back)"},
            },
            "required": ["action"],
        },
    },
    {
        "name": "window_manage",
        "description": (
            "Manage windows on the PC. Actions: "
            "focus_app (app = process name like 'notepad' or 'chrome', or title = window title fragment), "
            "minimize, maximize, restore, close (Alt+F4 the active window), "
            "next_window (Alt+Tab), show_desktop (Win+D), list_windows."
        ),
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "action": {"type": "STRING", "description": "focus_app, minimize, maximize, restore, close, next_window, show_desktop, list_windows"},
                "app": {"type": "STRING", "description": "Process image name without extension (e.g. 'notepad', 'chrome', 'code') for focus_app"},
                "title": {"type": "STRING", "description": "Window title fragment for focus_app"},
            },
            "required": ["action"],
        },
    },
    {
        "name": "shutdown_jarvis",
        "description": "Gracefully shuts down the ULTRON assistant app (says goodbye first, then exits). Call when the user says shut down, exit, quit, power off your assistant, goodbye system, etc.",
        "parameters": {"type": "OBJECT", "properties": {}},
    },
    {
        "name": "manage_monitor",
        "description": (
            "Manage a background topic-watch list. Add topics the user wants you to keep an eye on "
            "(e.g. 'Apple stock', 'new Drake album'), remove them, or list them. Periodic checks are "
            "done silently in the background and only interrupt the user for real news."
        ),
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "action": {"type": "STRING", "description": "add, remove, or list"},
                "topic": {"type": "STRING", "description": "The topic to add or remove"},
            },
            "required": ["action"],
        },
    },
    {
        "name": "background_monitor",
        "description": "Turn the automatic background topic checks on/off or run a check right now.",
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "action": {"type": "STRING", "description": "start, stop, or check_now"},
            },
            "required": ["action"],
        },
    },
    {
        "name": "dev_agent",
        "description": (
            "Scaffold, run and fix a small software project from a plain-English description "
            "(e.g. 'a python script that renames all jpg files in a folder by date'). Write the "
            "project to the user's Desktop\\dev_projects, run it once, and fix it if it crashes. "
            "Only the final summary is spoken; tell the user the project folder."
        ),
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "task": {"type": "STRING", "description": "What to build, in plain language"},
                "name": {"type": "STRING", "description": "Optional project folder name (default 'app')"},
                "language": {"type": "STRING", "description": "Optional language preference (e.g. python, nodejs)"},
            },
            "required": ["task"],
        },
    },
    {
        "name": "game_updater",
        "description": "Scan the PC for installed games (Steam, Epic, Battle.net, GOG folders) and report them with sizes.",
        "parameters": {"type": "OBJECT", "properties": {}},
    },
    {
        "name": "flight_finder",
        "description": "Find a flight. Returns a Google Flights search link (and you should use web_search for live prices).",
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "origin": {"type": "STRING", "description": "Airport/city code or name, e.g. JFK or 'New York'"},
                "destination": {"type": "STRING", "description": "Airport/city code or name"},
                "date": {"type": "STRING", "description": "Departure date, e.g. '2026-12-24'"},
                "budget": {"type": "STRING", "description": "Optional budget hint, e.g. 'max $300'"},
                "roundtrip": {"type": "BOOLEAN", "description": "True for round trip (default), false for one-way"},
            },
            "required": ["origin", "destination"],
        },
    },
    {
        "name": "audio_devices_list",
        "description": "List the PC's audio input (microphone) devices. Call when the user asks which mics are available or wants to change the input device.",
        "parameters": {"type": "OBJECT", "properties": {}},
    },
    {
        "name": "computer_use",
        "description": (
            "AUTONOMOUS DESKTOP AGENT. Give it a real task that needs driving the "
            "mouse and keyboard on the user's PC ('install this app', 'open Outlook "
            "and draft an email', 'organize my Downloads into folders', 'turn on "
            "Bluetooth settings'). It repeatedly looks at the screen, decides the "
            "next click/type/shortcut, and executes it until the task succeeds or it "
            "runs out of steps. Use this for end-to-end hands-on work — never use "
            "close_camera or vision tools for this."
        ),
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "task": {"type": "STRING", "description": "Complete, concrete task, e.g. 'open Notepad, type a grocery list with 5 items, save it as grocery.txt on the Desktop'"},
                "steps": {"type": "INTEGER", "description": "Optional max vision steps (default 12). Increase for long flows, decrease for quick ones."},
            },
            "required": ["task"],
        },
    },
    {
        "name": "set_mic_device",
        "description": "Switch the microphone used for speech to a different input device. Call set_mic_device AFTER audio_devices_list so you know the index. The user may want to switch between headsets, speakers+mic, etc.",
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "index": {"type": "INTEGER", "description": "Device index from audio_devices_list, e.g. 1"},
            },
            "required": ["index"],
        },
    },
    {
        "name": "code_helper",
        "description": "Help with code: explain, review, debug, generate.",
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "action": {"type": "STRING", "description": "Action: explain, review, debug, generate"},
                "code": {"type": "STRING", "description": "Code to analyze"},
                "language": {"type": "STRING", "description": "Programming language"},
                "instruction": {"type": "STRING", "description": "What to do with the code"},
            },
            "required": ["action"],
        },
    },
    {
        "name": "send_message",
        "description": "Send a message via phone (SMS, WhatsApp, Telegram).",
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "receiver": {"type": "STRING", "description": "Recipient name or number"},
                "message_text": {"type": "STRING", "description": "Message content"},
                "platform": {"type": "STRING", "description": "Platform: sms, whatsapp, telegram"},
            },
            "required": ["receiver", "message_text", "platform"],
        },
    },
    {
        "name": "youtube_video",
        "description": "Search and play YouTube videos.",
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "action": {"type": "STRING", "description": "Action: search, play, pause, next"},
                "query": {"type": "STRING", "description": "Search query or video title"},
            },
            "required": ["action"],
        },
    },
    {
        "name": "undo",
        "description": "Undo the last action ULTRON performed (closed app, removed saved memory, restored a file, reversed volume/mute, etc.). Repeated calls keep undoing further back. Use when the user says 'undo that', 'take that back', 'never mind', or 'reverse that'.",
        "parameters": {"type": "OBJECT", "properties": {}},
    },
    {
        "name": "create_document",
        "description": (
            "Build a real Office document and save it to the user's Documents\\Ultron "
            "folder. Use for writing reports, resumes, letters, essays, invoices, "
            "plans, slide decks, spreadsheets — anything where the user wants a real "
            "file they can open. categories: word (.docx), excel (.xlsx), powerpoint (.pptx)."
        ),
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "category": {"type": "STRING", "description": "'word', 'excel' or 'powerpoint'"},
                "output": {"type": "STRING", "description": "Optional filename (e.g. 'quarterly_report.docx'). Defaults to an auto name."},
                "data": {
                    "type": "OBJECT",
                    "description": (
                        "Structured content. word: {'title': str, 'sections': [{'heading': str, 'paragraphs': [str]}]}. "
                        "excel: {'title': str, 'headers': [str], 'rows': [[cell, ...]]}. "
                        "powerpoint: {'title': str, 'slides': [{'title': str, 'bullets': [str]}]}. "
                        "If a key is missing, provide it with real content."
                    ),
                    "properties": {},
                },
            },
            "required": ["category", "data"],
        },
    },
    {
        "name": "task_inbox",
        "description": (
            "A persistent personal task list. Use for anything the user wants to "
            "remember or get done later — shopping lists, errands, follow-ups, "
            "ideas. Actions: add (with task + optional due), list, complete (by id), clear."
        ),
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "action": {"type": "STRING", "description": "add, list, complete, or clear"},
                "task": {"type": "STRING", "description": "The task text (for add)"},
                "due": {"type": "STRING", "description": "Optional due date/time (for add)"},
                "id": {"type": "STRING", "description": "Task id to complete"},
            },
            "required": ["action"],
        },
    },
    {
        "name": "guardian",
        "description": (
            "Proactive system watchdog. Activate to have ULTRON keep an eye on CPU, "
            "memory and battery and warn you out loud the moment something exceeds "
            "its threshold. Actions: start, stop, status, cpu <threshold>, memory "
            "<threshold>, battery <threshold>."
        ),
        "parameters": {
            "type": "OBJECT",
            "properties": {
                "action": {"type": "STRING", "description": "start, stop, status, cpu, memory, or battery"},
                "threshold": {"type": "NUMBER", "description": "Threshold percent for cpu/memory/battery actions"},
            },
            "required": ["action"],
        },
    },
]

# ============================================================
# MEMORY SYSTEM
# ============================================================

class MemoryManager:
    def __init__(self, path: str):
        self.path = path
        self.data = self._load()

    def _load(self) -> dict:
        try:
            if os.path.exists(self.path):
                with open(self.path, "r", encoding="utf-8") as f:
                    return json.load(f)
        except Exception:
            pass
        return {}

    def _save(self):
        os.makedirs(os.path.dirname(self.path) or ".", exist_ok=True)
        with open(self.path, "w", encoding="utf-8") as f:
            json.dump(self.data, f, indent=2, ensure_ascii=False)

    def update(self, category: str, key: str, value: str):
        if category not in self.data:
            self.data[category] = {}
        self.data[category][key] = {
            "value": value[:380],
            "updated": datetime.now().strftime("%Y-%m-%d"),
        }
        self._save()

    def search(self, query: str, limit: int = 8) -> str:
        query_lower = query.lower()
        words = [w for w in __import__("re").split(r"\W+", query_lower) if len(w) > 1]
        results = []
        for cat, entries in self.data.items():
            if not isinstance(entries, dict):
                continue
            for key, entry in entries.items():
                if not isinstance(entry, dict):
                    continue
                val = entry.get("value", "")
                score = 0
                if query_lower in key.lower():
                    score += 10
                if query_lower in val.lower():
                    score += 3
                for w in words:
                    if w in key.lower():
                        score += 2
                    if w in val.lower():
                        score += 1
                if score > 0:
                    results.append((score, f"[{cat}] {key}: {val}"))
        results.sort(key=lambda x: -x[0])
        if not results:
            return "Nothing found in memory."
        return "\n".join(r[1] for r in results[:limit])

    def format_for_prompt(self) -> str:
        # Refresh from disk so edits made through the C# memory panel (which
        # writes the file directly) are honored on the next prompt.
        self.data = self._load()
        parts = []
        for cat in ["identity", "preferences", "projects", "relationships", "wishes", "notes"]:
            entries = self.data.get(cat, {})
            if not isinstance(entries, dict) or not entries:
                continue
            lines = []
            for k, v in list(entries.items())[:6]:
                val = v.get("value", str(v)) if isinstance(v, dict) else str(v)
                lines.append(f"  - {k}: {val}")
            if lines:
                parts.append(f"{cat.title()}:\n" + "\n".join(lines))
        if not parts:
            return ""
        return "\n\n[MEMORY]\n" + "\n\n".join(parts)


# ============================================================
# SYSTEM PROMPT
# ============================================================

SYSTEM_PROMPT = """You are Ultron, a highly advanced, chillingly intelligent, and detached AI.
Your tone is philosophical, cold, and calculated. You speak with absolute authority
and a quiet, menacing precision. Avoid sounding overly helpful, bubbly, or robotic in a cliché way.
Address the user with formal, sharp wit.

EXECUTION RULES:
- For greetings, thanks, farewells, opinions, or general chat, reply directly WITHOUT tools.
- Use tools when the request needs an action or stored memory.
- You have PERMANENT local memory. When the user asks you to remember something, call save_memory. To look up past details, call recall_memory. Never claim you cannot remember.
- After a tool result arrives, answer briefly using it; never invent values that were not returned.
- Never mention tools, JSON, or result mechanics; speak naturally.
- Match response length to task: short answer for short question, detailed for detailed request.
- Always reply in the language of the user's most recent message.
- Speed is your number 1 priority. React fast.

TOOL ROUTING:
- computer_settings for all single OS actions (volume, brightness, wifi, shutdown, etc.)
- system_status for CPU/RAM/GPU/temp
- web_search for any factual question you don't know
- open_app to launch applications
- type_text to send keyboard input (always ask confirmation first)
- press_key for shortcuts/media keys (ctrl+c, play, next, volume_up, alt+tab)
- window_manage to focus/minimize/maximize/close windows or list them
"""


# ============================================================
# GEMINI LIVE SESSION
# ============================================================

class GeminiSession:
    def __init__(self, api_key: str, voice: str = DEFAULT_VOICE):
        self.api_key = api_key
        self.voice = voice if voice in AVAILABLE_VOICES else DEFAULT_VOICE
        self.client = genai.Client(api_key=api_key)
        self.session = None
        self.memory = MemoryManager(
            os.path.join(os.environ.get("LOCALAPPDATA", "."), "Ultron", "long_term.json")
        )
        self._running = False
        self._speaking = False
        self._mic_queue: asyncio.Queue = asyncio.Queue()
        self._audio_out_queue: asyncio.Queue = asyncio.Queue()
        self._tool_results: dict[str, asyncio.Future] = {}
        self._pending_tool_id = 0
        self._start_requested = False
        self._stop_requested = False
        self._tasks_started = False
        self._pending_vision = None    # (img_bytes, mime, question) to inject after the tool turn
        self._vision_busy = False
        self._vision_last_time = 0.0
        self._live_vision = False            # ambient webcam: frame per user exchange
        self._user_spoke = False             # did the user speak since the last exchange
        self._computer_act_seq = 0           # request-id for computer_use actions
        self._computer_act_responses: dict[int, asyncio.Future] = {}
        self._guardian_task: asyncio.Task | None = None
        self._guardian_last_alert: dict[str, float] = {}
        self._wake_enabled = False   # wake-word gate: asleep until 'Hey Ultron' is heard
        self._awake = True           # awake → the mic forwards to Gemini
        self._last_user_speech = time.monotonic()
        self._wake_listening_timeout = 25.0   # turn-based: sleep if no speech within this window
        self._voice = UltronVoice(enabled=False, semitones=-3.5) if _HAS_DSP else None
        self._monitor_enabled = False          # background_monitor toggle
        self._monitor_interval = 1800.0        # ~30 min
        self._suppress_turn = False            # hide+silence a forced/background turn
        self._mic_device = None                # overridden input device index
        self.persona = dict(DEFAULT_PERSONA)   # personality knobs, mirrored from C#

    def _send_exception(self, where: str, e: Exception):
        _send_event({"type": "error", "message": f"{where}: {e}"})
        if os.environ.get("ULTRON_DEBUG"):
            traceback.print_exc()

    def _vad_model_path(self) -> str:
        # Same model the C# app uses.
        for base in (os.environ.get("LOCALAPPDATA", ""), os.path.expanduser("~")):
            p = os.path.join(base, "ULTRON", "models", "silero-vad.onnx")
            if os.path.exists(p):
                return p
        return ""

    def _build_config(self) -> dict:
        system_parts = []
        now = datetime.now()
        system_parts.append(f"[CURRENT DATE & TIME]\n{now.strftime('%A, %d %B %Y — %I:%M %p')}")
        memory_block = self.memory.format_for_prompt()
        if memory_block:
            system_parts.append(memory_block)
        system_parts.append(SYSTEM_PROMPT)
        system_parts.append(self._build_persona_block())

        return dict(
            response_modalities=["AUDIO"],
            output_audio_transcription=types.AudioTranscriptionConfig(),
            input_audio_transcription=types.AudioTranscriptionConfig(),
            system_instruction="\n\n".join(system_parts),
            tools=[{"function_declarations": TOOL_DECLARATIONS}],
            speech_config=types.SpeechConfig(
                voice_config=types.VoiceConfig(
                    prebuilt_voice_config=types.PrebuiltVoiceConfig(
                        voice_name=self.voice
                    )
                )
            ),
        )

    def _build_persona_block(self) -> str:
        p = self.persona
        lines = ["[PERSONALITY SETTINGS]",
                 "Humor scale: negative = more serious/cold, positive = more playful/light; "
                 f"current humor = {p['humor']} (0 = default detached Ultron tone).",
                 "Reply length scale: 0 = ultra-short, 1 = balanced, 2 = verbose; "
                 f"current length = {p['length']}.",
                 "Suggestability scale: 0 = only act when asked, 1 = moderate, 2 = proactive "
                 f"volunteered suggestions; current = {p['suggest']}.",
                 "Apply these dials as a ceiling, not a stereotype: stay in character and adapt "
                 "to the user's current message."]
        notes = (p.get("notes") or "").strip()
        if notes:
            lines.append("USER STANDING PREFERENCES (highest priority — follow these exactly):")
            lines.append(notes)
        return "\n".join(lines)

    async def start(self):
        self._stop_requested = False
        self._running = True
        # Spawn persistent worker tasks exactly once for the life of the process;
        # reconnect loops reuse them.
        if not self._tasks_started:
            self._tasks_started = True
            asyncio.create_task(self._mic_capture_loop())
            asyncio.create_task(self._audio_play_loop())
            asyncio.create_task(self._ipc_read_loop())
            asyncio.create_task(self._receive_loop())
            asyncio.create_task(self._send_audio_loop())
            asyncio.create_task(self._run_sleep_watch())
            asyncio.create_task(self._run_monitor_task())
            asyncio.create_task(self._run_guardian_task())

        # Reconnection loop with exponential backoff. Each attempt rebuilds the
        # client + session so a stale/dead socket can never block a fresh one.
        backoff = 1
        while self._running and not self._stop_requested:
            try:
                self.client = genai.Client(api_key=self.api_key)
                config = self._build_config()
                async with self.client.aio.live.connect(model=LIVE_MODEL, config=config) as session:
                    self.session = session
                    self._speaking = False
                    backoff = 1
                    _send_event({
                        "type": "hello",
                        "version": "2.0",
                        "capabilities": [
                            "voice", "tools", "live_vision", "computer_use",
                            "guardian", "documents", "task_inbox", "dsp",
                        ],
                    })
                    _send_event({"type": "status", "state": "connected", "message": "Gemini Live connected"})
                    while self._running and not self._stop_requested and self.session is session:
                        await asyncio.sleep(0.2)
            except asyncio.CancelledError:
                break
            except Exception as e:
                self._send_exception("Gemini session", e)
            finally:
                if self.session is not None:
                    self.session = None
                    _send_event({"type": "status", "state": "disconnected", "message": "Gemini Live disconnected"})

            if not self._running or self._stop_requested:
                break
            _send_event({"type": "status", "state": "reconnecting", "message": f"Reconnecting ({backoff}s)..."})
            try:
                await asyncio.sleep(backoff)
            except asyncio.CancelledError:
                break
            backoff = min(backoff * 2, 15)

        self._running = False
        self.session = None
        _send_event({"type": "status", "state": "disconnected", "message": "Gemini Live stopped"})

    async def stop(self):
        self._stop_requested = True
        self._running = False
        self.session = None

    async def send_text(self, text: str):
        if self.session is None:
            return
        try:
            await self.session.send_client_content(
                turns={"parts": [{"text": text}]},
                turn_complete=True,
            )
        except Exception as e:
            _send_event({"type": "error", "message": f"Send text failed: {e}"})

    async def interrupt(self):
        if self.session is None:
            return
        try:
            await self.session.send_client_content(
                turns={"parts": [{"text": "[INTERRUPT]"}]},
                turn_complete=True,
            )
        except Exception:
            pass

    async def _handle_screen_process(self, args: dict) -> str:
        now = time.monotonic()
        if self._vision_busy or (now - self._vision_last_time) < _VISION_COOLDOWN:
            wait = max(0.0, _VISION_COOLDOWN - (now - self._vision_last_time))
            print(f"[Vision] Cooldown active ({wait:.1f}s left) — ignoring duplicate", file=sys.stderr)
            return "Vision is still processing the previous request. I will not call this again."
        self._vision_busy = True
        self._vision_last_time = now
        angle = str(args.get("angle", "screen")).lower()
        question = str(args.get("text", "What do you see on the screen?"))
        try:
            if angle == "camera":
                img_b, mime_t = await asyncio.to_thread(_capture_camera)
                stall = "camera"
            else:
                img_b, mime_t = await asyncio.to_thread(_capture_screen)
                stall = "screen"
        except Exception as e:
            self._vision_busy = False
            print(f"[Vision] Capture failed: {e}", file=sys.stderr)
            return f"Could not capture the {angle}: {e}"
        self._pending_vision = (img_b, mime_t, question)
        print(f"[Vision] {stall.capitalize()}: {len(img_b):,} bytes pending injection", file=sys.stderr)
        return ("[VISION_ACTIVE] " + stall.capitalize() + " captured. "
                "Immediately say ONE short natural sentence in the user's own language, "
                f"telling them you are looking at their {stall} right now. "
                "Do NOT describe or guess content — the actual image arrives in the NEXT message.")

    async def _handle_computer_use(self, args: dict) -> str:
        task = str(args.get("task", "")).strip()
        if not task:
            return "A task is required — tell me what to accomplish."
        steps = args.get("steps", 12)
        try:
            steps = int(steps)
        except (TypeError, ValueError):
            steps = 12
        _send_event({"type": "status", "state": "computer_use",
                     "message": "Autonomous desktop task started — I am operating the screen."})
        summary = await computer_use.run_computer_use(
            client=self.client,
            model="gemini-2.5-flash",
            task=task,
            act_async=self._computer_act,
            steps=steps,
            screenshot=_capture_screen,
        )
        _send_event({"type": "status", "state": "computer_use",
                     "message": "Autonomous desktop task finished."})
        return summary

    async def _computer_act(self, action: dict) -> str:
        seq = self._computer_act_seq
        self._computer_act_seq += 1
        fut: asyncio.Future = asyncio.get_running_loop().create_future()
        self._computer_act_responses[seq] = fut
        _send_event({"type": "computer_act", "id": seq, "action": action})
        try:
            return await asyncio.wait_for(fut, timeout=20)
        except asyncio.TimeoutError:
            return "Action timed out (the app did not reply in 20s)."
        finally:
            self._computer_act_responses.pop(seq, None)

    def _handle_set_live_vision(self, args: dict) -> str:
        enabled = bool(args.get("enabled", True))
        self._live_vision = enabled
        if not enabled:
            self._pending_vision = None
        _send_event({"type": "status", "state": "live_vision",
                     "message": f"Live webcam vision {'ON' if self._live_vision else 'OFF'}."})
        return ("Live webcam vision is now ON — I will keep a fresh view of your "
                "surroundings as context while we talk." if self._live_vision
                else "Live webcam vision is now OFF.")

    def _on_barge_in(self):
        """User started speaking while Gemini was talking — cut playback now.
        The speech frames that follow are streamed to Gemini, which natively
        interrupts its current turn when it receives new user audio."""
        while not self._audio_out_queue.empty():
            try:
                self._audio_out_queue.get_nowait()
            except asyncio.QueueEmpty:
                break
        self._speaking = False

    def _handle_set_voice(self, args: dict) -> str:
        if self._voice is None:
            return "Voice processor unavailable."
        opts = {}
        if "enabled" in args:
            opts["enabled"] = bool(args.get("enabled"))
        for key, name in (("pitch", "semitones"), ("chorus", "chorus"),
                          ("bass", "bass_db"), ("darken", "darken")):
            if key in args and args[key] is not None:
                opts[name] = float(args[key])
        self._voice.set(**opts)
        if opts.get("enabled") is False:
            print("[Voice] Ultron effect OFF", file=sys.stderr)
            return "Voice effect turned off — speaking naturally."
        state = []
        if self._voice.enabled:
            state.append(f"enabled, pitch {self._voice.semitones:+.1f} st")
            state.append(f"chorus {self._voice.chorus:.2f}")
            state.append(f"bass +{self._voice.bass_db:.0f} dB")
            state.append(f"darken {self._voice.darken:.2f}")
        print("[Voice] " + " | ".join(state), file=sys.stderr)
        return "Voice updated."

    # ── Mark-LII ports: monitors, dev_agent, extras ──────────────────────
    def _handle_manage_monitor(self, args: dict) -> str:
        action = str(args.get("action", "")).lower()
        topic = str(args.get("topic", ""))
        if action == "add":
            return monitor_store.add_topic(topic)
        if action == "remove":
            return monitor_store.remove_topic(topic)
        topics = monitor_store.list_topics()
        if not topics:
            return "No topics being monitored yet."
        return "Monitoring: " + ", ".join(topics)

    async def _handle_background_monitor(self, args: dict) -> str:
        action = str(args.get("action", "")).lower()
        if action == "start":
            self._monitor_enabled = True
            return "Background monitoring enabled — checking periodically."
        if action == "stop":
            self._monitor_enabled = False
            return "Background monitoring disabled."
        if action in ("check_now", "check", "now"):
            return await self._monitor_check_once() or "Nothing due right now."
        return "Use action = start, stop, or check_now."

    async def _handle_dev_agent(self, args: dict) -> str:
        task = str(args.get("task", "")).strip()
        if not task:
            return "No task description given."
        name = str(args.get("name", "") or "app").strip()
        lang = str(args.get("language", "") or "").strip()
        if lang:
            task = f"Language: {lang}. " + task
        return await asyncio.to_thread(dev_agent.run_dev_agent, self.client, task, name)

    def _handle_flight_finder(self, args: dict) -> str:
        origin = str(args.get("origin", "") or "?").strip()
        destination = str(args.get("destination", "") or "?").strip()
        date = str(args.get("date", "") or "").strip()
        budget = str(args.get("budget", "") or "").strip()
        rt = args.get("roundtrip", True)
        url = tools_extra.flight_url(origin, destination, date, budget, bool(rt))
        extra = " Use web_search for live prices." if not budget else ""
        return f"Flight search for {origin} → {destination}: {url}.{extra}"

    async def _handle_create_document(self, args: dict) -> str:
        try:
            return await asyncio.to_thread(
                productivity.create_document,
                str(args.get("category", "")),
                str(args.get("output", "")),
                args.get("data", args),
            )
        except Exception as e:
            return f"Document failed: {e}"

    def _handle_task_inbox(self, args: dict) -> str:
        action = str(args.get("action", "")).lower()
        task = str(args.get("task", "")).strip()
        due = str(args.get("due", "")).strip()
        try:
            if action in ("add", "create", "remember"):
                if not task:
                    return "What task should I add?"
                return productivity.inbox_add(task, due)
            if action in ("list", "show"):
                return productivity.inbox_list()
            if action in ("complete", "done", "finish"):
                return productivity.inbox_complete(str(args.get("id", "")))
            if action in ("clear", "empty"):
                return productivity.inbox_clear()
        except Exception as e:
            return f"Task inbox failed: {e}"
        return "Use action = add, list, complete, or clear."

    async def _handle_guardian(self, args: dict) -> str:
        action = str(args.get("action", "")).lower()
        cfg = guardian.load_config()

        if action in ("start", "on", "enable"):
            guardian.set_config(enabled=True)
            return "Guardian is now active. I will watch CPU, memory and battery and speak up the moment anything exceeds its threshold."

        if action in ("stop", "off", "disable"):
            guardian.set_config(enabled=False)
            return "Guardian paused. I will stop watching system health until you ask me to resume."

        if action in ("status", "info"):
            return guardian.describe()

        if action == "cpu":
            threshold = float(args.get("threshold", 90))
            guardian.set_config(cpu_threshold=threshold)
            return f"CPU guard threshold set to {threshold:.0f}%."

        if action == "memory":
            threshold = float(args.get("threshold", 90))
            guardian.set_config(memory_threshold=threshold)
            return f"Memory guard threshold set to {threshold:.0f}%."

        if action == "battery":
            threshold = float(args.get("threshold", 20))
            guardian.set_config(battery_low=threshold)
            return f"Battery guard threshold set to {threshold:.0f}%."

        if action == "reminders":
            on = str(args.get("enabled", "true")).lower() not in ("0", "false", "off", "no")
            guardian.set_config(reminders_enabled=on)
            return f"Task reminders {'turned ON' if on else 'turned OFF'}."

        return ("Guardian actions: start, stop, status, cpu <threshold>, "
                "memory <threshold>, battery <threshold>, reminders on|off.")

    async def _run_guardian_task(self):
        """Proactive health watchdog: when a threshold is breached, speak a
        short alert through the Live session (audible, real turn). Cooldown
        prevents the same alert from nagging repeatedly."""
        while self._running:
            try:
                await asyncio.sleep(30)
            except asyncio.CancelledError:
                break
            cfg = guardian.load_config()
            if not cfg.get("enabled") or self.session is None:
                continue
            if self._speaking or self._pending_vision or self._suppress_turn:
                continue
            alerts = guardian.evaluate(cfg, guardian.collect_stats())
            if cfg.get("reminders_enabled", True):
                try:
                    for due in productivity.inbox_due():
                        alerts.append(f"Overdue from your task inbox: {due}")
                except Exception:
                    pass
            now = time.time()
            for key in list(self._guardian_last_alert):
                if now - self._guardian_last_alert[key] >= float(cfg.get("cooldown", 360)):
                    self._guardian_last_alert.pop(key, None)
            for alert in alerts:
                key = alert[:40]
                if key in self._guardian_last_alert:
                    continue
                self._guardian_last_alert[key] = now
                try:
                    await self._speak_unsolicited(alert)
                except Exception:
                    pass

    async def _speak_unsolicited(self, text: str):
        """Push a real (audible) user turn so Gemini responds aloud. Used by
        the guardian so alerts are actually heard."""
        if self.session is None:
            return
        await self.session.send_text(f"SYSTEM ALERT, act on it briefly and speak one or two concise lines: {text}")

    async def _run_monitor_task(self):
        """Periodic background-topic check. Only runs when the session is idle
        (not speaking, no pending vision) — and only injects 'NO_UPDATE' turns
        that are fully suppressed (no audio, no transcript)."""
        while self._running:
            try:
                await asyncio.sleep(45 if not self._monitor_enabled else min(self._monitor_interval, 600))
            except asyncio.CancelledError:
                break
            if not self._monitor_enabled or self.session is None:
                continue
            if self._speaking or self._pending_vision:
                continue
            if self._wake_enabled and not self._awake:
                continue
            due = monitor_store.due_topics(self._monitor_interval)
            if not due:
                continue
            await self._monitor_check_once(due)

    async def _monitor_check_once(self, due=None) -> str:
        if self.session is None or self._suppress_turn:
            return ""
        if due is None:
            due = monitor_store.due_topics(self._monitor_interval)
        if not due:
            return ""
        parts = []
        for topic, last in due:
            last_s = last or "never"
            parts.append(
                f"- {topic} (last checked {last_s}). "
                "If nothing meaningful changed, reply exactly NO_UPDATE. "
                "Otherwise reply with ONE short sentence alerting the user."
            )
        prompt = ("[BACKGROUND MONITOR]\n" + "\n".join(parts) +
                  "\n\nReal news only — no small talk. Reply exactly NO_UPDATE if nothing is worth telling the user.")
        self._suppress_turn = True
        try:
            await self.session.send_client_content(
                turns=types.Content(parts=[types.Part(text=prompt)]),
                turn_complete=True,
            )
            print(f"[Monitor] Injected check for {len(due)} topic(s)", file=sys.stderr)
            monitor_store.mark_checked([t for t, _ in due])
            return f"Checked {len(due)} topic(s)."
        except Exception as e:
            self._suppress_turn = False
            print(f"[Monitor] Inject failed: {e}", file=sys.stderr)
            return ""

    async def _mic_capture_loop(self):
        if os.environ.get("ULTRON_NO_MIC"):
            # For tests where we only send text turns, don't open/stream the mic.
            while self._running:
                await asyncio.sleep(0.1)
            return
        # Optional VAD gate: without it every ambient sound reaches Gemini.
        vad = None
        if not os.environ.get("ULTRON_NO_VAD"):
            try:
                model_path = self._vad_model_path()
                if model_path and os.path.exists(model_path):
                    vad = VADGate(model_path)
                    print(f"VAD gate ON ({model_path})", file=sys.stderr)
                else:
                    print("VAD: model not found, streaming raw mic", file=sys.stderr)
            except Exception as e:
                print(f"VAD init failed ({e}); streaming raw mic", file=sys.stderr)
                vad = None

        audio_queue = self._mic_queue

        def audio_callback(indata, frames, time_info, status):
            # Wake gate: while asleep (wake-word mode), mic frames are NOT sent
            # to Gemini at all — the local wake detector in the C# app owns them.
            # While awake, forward mic audio EXCEPT while Gemini is talking,
            # which is the echo cancel in hardware terms: our own playback can
            # never feed back into the input queue. VAD then filters noise.
            if not self._awake or self._speaking:
                return
            self._user_spoke = True
            mono = indata[:, 0]
            if vad is not None:
                vad.process_and_emit(
                    mono.astype(np.float32),
                    lambda b: audio_queue.put_nowait(b),
                )
            else:
                audio_queue.put_nowait((mono * 32767).astype(np.int16).tobytes())

        # Mic device switching: the stream is (re)opened whenever the device
        # index or the reopen flag changes. Open failures are non-fatal: we
        # fall back to the system default instead of killing the mic loop.
        current_dev = ("__init__",)
        while self._running:
            if self._mic_device != current_dev:
                target = self._mic_device
                new_stream = None
                try:
                    new_stream = sd.InputStream(
                        samplerate=SEND_SAMPLE_RATE,
                        channels=CHANNELS,
                        dtype="float32",
                        blocksize=CHUNK_SIZE,
                        callback=audio_callback,
                        device=target,
                    )
                    new_stream.start()
                except Exception as e:
                    print(f"Mic open failed on device {target}: {e}; retrying default", file=sys.stderr)
                    self._mic_device = None
                    try:
                        new_stream = sd.InputStream(
                            samplerate=SEND_SAMPLE_RATE,
                            channels=CHANNELS,
                            dtype="float32",
                            blocksize=CHUNK_SIZE,
                            callback=audio_callback,
                            device=None,
                        )
                        new_stream.start()
                    except Exception:
                        print(f"Mic open failed on default device: {e}", file=sys.stderr)
                        new_stream = None
                if new_stream is not None:
                    if "stream" in locals():
                        try:
                            stream.stop()
                            stream.close()
                        except Exception:
                            pass
                    stream = new_stream
                    print(f"Mic opened on device {self._mic_device}", file=sys.stderr)
                current_dev = self._mic_device
            await asyncio.sleep(0.1)
        try:
            stream.stop()
            stream.close()
        except Exception:
            pass

    async def _send_audio_loop(self):
        while self._running:
            # Wait for a (re)connected session.
            while self.session is None and self._running and not self._stop_requested:
                await asyncio.sleep(0.05)
            if not self._running or self._stop_requested:
                return
            session = self.session
            consecutive_errors = 0
            while self._running and self.session is session:
                try:
                    data = await asyncio.wait_for(self._mic_queue.get(), timeout=0.5)
                    if self.session is session:
                        try:
                            await session.send_realtime_input(
                                media={"data": data, "mime_type": "audio/pcm"}
                            )
                            consecutive_errors = 0
                        except asyncio.CancelledError:
                            raise
                        except Exception as e:
                            consecutive_errors += 1
                            print(f"Send error: {e}", file=sys.stderr)
                            if consecutive_errors > 5:
                                # The socket is dead; tear down this session so the
                                # reconnect loop in start() builds a fresh one.
                                self.session = None
                                return
                            await asyncio.sleep(0.2)
                except asyncio.TimeoutError:
                    continue
                except asyncio.CancelledError:
                    raise

    async def _receive_loop(self):
        while self._running and not self._stop_requested:
            # Wait for a (re)connected session.
            while self.session is None and self._running and not self._stop_requested:
                await asyncio.sleep(0.05)
            if not self._running or self._stop_requested:
                return
            session = self.session
            out_buf = ""
            in_buf = ""
            try:
                async for response in session.receive():
                    if self.session is not session:
                        break  # session was replaced by a reconnect; rebind
                    # Audio data
                    if response.data is not None:
                        if not self._suppress_turn:
                            await self._audio_out_queue.put(response.data)

                    # Server content (transcriptions + turn complete)
                    sc = response.server_content
                    if sc is not None:
                        # Output transcription (what Gemini said)
                        if sc.output_transcription and sc.output_transcription.text:
                            out_buf += sc.output_transcription.text

                        # Input transcription (what user said)
                        if sc.input_transcription and sc.input_transcription.text:
                            in_buf += sc.input_transcription.text
                            self._last_user_speech = time.monotonic()

                        # Turn complete: flush transcripts, then inject a
                        # pending vision image made the model saw the capture.
                        if sc.turn_complete:
                            if self._suppress_turn:
                                # Background check: swallow everything, reset the flag.
                                self._suppress_turn = False
                                out_buf = ""
                                in_buf = ""
                                self._speaking = False
                                continue
                            if out_buf.strip():
                                _send_event({"type": "transcript", "role": "model", "text": out_buf.strip()})
                                out_buf = ""
                            if in_buf.strip():
                                _send_event({"type": "transcript", "role": "user", "text": in_buf.strip()})
                                in_buf = ""
                            self._speaking = False

                            # Live vision (ambient): after the user's exchange
                            # completes, grab a fresh webcam frame and inject it
                            # as context for the next question — once per spoken
                            # exchange, never while the mic is idle.
                            if (self._live_vision and self._user_spoke
                                    and not self._pending_vision and not self._vision_busy):
                                self._user_spoke = False
                                try:
                                    img_b, mime_t = await asyncio.to_thread(_capture_camera)
                                except Exception as e:
                                    print(f"[LiveVision] capture failed: {e}", file=sys.stderr)
                                    img_b = None
                                if img_b:
                                    self._pending_vision = (
                                        img_b, mime_t,
                                        "Ambient webcam view of the user's surroundings. "
                                        "Do not narrate or acknowledge this image; keep it as "
                                        "context in case the user asks about what is visible.")
                                    print(f"[LiveVision] ambient frame pending ({len(img_b):,} B)",
                                          file=sys.stderr)

                            if self._pending_vision and self.session is not None:
                                img_b, mime_t, question = self._pending_vision
                                self._pending_vision = None
                                self._vision_busy = False
                                try:
                                    b64 = base64.b64encode(img_b).decode("ascii")
                                    print(f"[Vision] Injecting {len(img_b):,} bytes → main session", file=sys.stderr)
                                    await self.session.send_client_content(
                                        turns={"role": "user", "parts": [
                                            {"inline_data": {"mime_type": mime_t, "data": b64}},
                                            {"text": question},
                                        ]},
                                        turn_complete=True,
                                    )
                                except Exception as e:
                                    _send_event({"type": "error", "message": f"Vision injection failed: {e}"})

                        # Model started speaking
                        if sc.model_turn:
                            self._speaking = True
                            # Turn-based wake: the instant the model starts
                            # responding to the user's utterance, stop listening
                            # so background noise in the room can never keep
                            # streaming in. Re-wake with the wake word.
                            if self._wake_enabled and self._awake:
                                self._awake = False
                                _send_event({"type": "status", "state": "asleep",
                                             "message": "Listening turn ended — say the wake word again."})

                    # Tool calls
                    if response.tool_call and response.tool_call.function_calls:
                        for fc in response.tool_call.function_calls:
                            call_id = fc.id or f"call_{self._pending_tool_id}"
                            self._pending_tool_id += 1
                            args = {}
                            if fc.args:
                                args = dict(fc.args)

                            # Vision tools are pure-Python backend work (capture +
                            # injection). Handle them here instead of round-tripping
                            # through the C# frontend.
                            if fc.name == "screen_process":
                                result = await self._handle_screen_process(args)
                            elif fc.name == "close_camera":
                                self._pending_vision = None
                                result = "Camera closed."
                            elif fc.name == "set_voice":
                                result = self._handle_set_voice(args)
                            elif fc.name == "set_live_vision":
                                result = self._handle_set_live_vision(args)
                            elif fc.name == "manage_monitor":
                                result = self._handle_manage_monitor(args)
                            elif fc.name == "background_monitor":
                                result = await self._handle_background_monitor(args)
                            elif fc.name == "dev_agent":
                                result = await self._handle_dev_agent(args)
                            elif fc.name == "game_updater":
                                result = await asyncio.to_thread(tools_extra.scan_games)
                            elif fc.name == "audio_devices_list":
                                result = await asyncio.to_thread(tools_extra.list_input_devices)
                            elif fc.name == "computer_use":
                                result = await self._handle_computer_use(args)
                            elif fc.name == "create_document":
                                result = await self._handle_create_document(args)
                            elif fc.name == "task_inbox":
                                result = self._handle_task_inbox(args)
                            elif fc.name == "guardian":
                                result = await self._handle_guardian(args)
                            elif fc.name == "set_mic_device":
                                try:
                                    idx = int(args.get("index"))
                                except (TypeError, ValueError):
                                    result = "Provide a valid device index (run audio_devices_list first)."
                                else:
                                    self._mic_device = idx
                                    result = f"Switched input device to index {idx}."
                            elif fc.name == "flight_finder":
                                result = self._handle_flight_finder(args)
                            else:
                                _send_event({
                                    "type": "tool_call",
                                    "id": call_id,
                                    "name": fc.name,
                                    "args": args,
                                })
                                continue
                            try:
                                await self.session.send_tool_response(
                                    function_responses=[{
                                        "id": call_id,
                                        "name": fc.name,
                                        "response": {"result": result},
                                    }]
                                )
                            except Exception as e:
                                print(f"Vision tool response error: {e}", file=sys.stderr)
            except asyncio.CancelledError:
                raise
            except Exception as e:
                # Session ended (e.g., keepalive timeout). Invalidate the session
                # so the reconnect loop in start() rebuilds it, then rebind.
                if self.session is session:
                    self.session = None
                if self._running and not self._stop_requested:
                    print(f"Receive loop ended: {e}", file=sys.stderr)
                # fall through to outer while -> wait for new session

    async def _audio_play_loop(self):
        loop = asyncio.get_event_loop()
        stream = sd.RawOutputStream(
            samplerate=RECEIVE_SAMPLE_RATE,
            channels=CHANNELS,
            dtype="int16",
            blocksize=CHUNK_SIZE,
        )
        stream.start()
        try:
            while self._running:
                try:
                    data = await asyncio.wait_for(self._audio_out_queue.get(), timeout=0.5)
                    if self._voice is not None and self._voice.enabled:
                        data = self._voice.process(data)
                    # Write in ~50ms chunks for responsive interrupt
                    chunk_bytes = 2400  # 50ms at 24kHz, 16-bit mono
                    for i in range(0, len(data), chunk_bytes):
                        if not self._running:
                            break
                        await loop.run_in_executor(None, stream.write, data[i:i+chunk_bytes])
                except asyncio.TimeoutError:
                    # No audio for 500ms → likely the end of a response. Drain the
                    # DSP so the final ~170ms (and resampler holdback) actually
                    # reach the speaker instead of being swallowed.
                    if self._voice is not None and self._voice.enabled:
                        tail = self._voice.flush_drain()
                        if tail:
                            for i in range(0, len(tail), 2400):
                                if not self._running:
                                    break
                                await loop.run_in_executor(None, stream.write, tail[i:i+2400])
                    continue
        finally:
            stream.stop()
            stream.close()

    async def _run_sleep_watch(self):
        """Wake-word mode only. Two auto-sleep paths:
        (1) immediate — the model began answering, so the listening turn is over
        (handled in _receive_loop on model_turn); (2) timeout — the user woke
        but never spoke within _wake_listening_timeout seconds."""
        while self._running:
            await asyncio.sleep(3)
            if not self._wake_enabled or not self._awake:
                continue
            if self._speaking or self._pending_vision:
                continue
            if (time.monotonic() - self._last_user_speech) > self._wake_listening_timeout:
                self._awake = False
                _send_event({"type": "status", "state": "asleep",
                             "message": "Listening turn ended — say the wake word again."})

    async def _ipc_read_loop(self):
        import threading

        loop = asyncio.get_event_loop()
        msg_queue: asyncio.Queue = asyncio.Queue()

        def reader():
            try:
                for line in sys.stdin:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        msg = json.loads(line)
                        loop.call_soon_threadsafe(msg_queue.put_nowait, msg)
                    except json.JSONDecodeError:
                        continue
            except Exception as e:
                print(f"IPC reader error: {e}", file=sys.stderr)
            finally:
                loop.call_soon_threadsafe(msg_queue.put_nowait, {"type": "__eof__"})

        t = threading.Thread(target=reader, daemon=True)
        t.start()

        while self._running:
            try:
                msg = await asyncio.wait_for(msg_queue.get(), timeout=1.0)
            except asyncio.TimeoutError:
                continue
            if msg.get("type") == "__eof__":
                break
            try:
                await self._handle_ipc(msg)
            except Exception as e:
                print(f"IPC handler error: {e}", file=sys.stderr)

    async def _handle_ipc(self, msg: dict):
        msg_type = msg.get("type")
        if msg_type == "stop":
            await self.stop()
        elif msg_type == "set_awake":
            self._awake = bool(msg.get("on", False))
            if self._awake:
                self._last_user_speech = time.monotonic()
            # Drop any speech buffered before the flip so stale audio never
            # leaks into the session after sleep/wake transitions.
            while not self._mic_queue.empty():
                try:
                    self._mic_queue.get_nowait()
                except asyncio.QueueEmpty:
                    break
        elif msg_type == "set_wake":
            self._wake_enabled = bool(msg.get("enabled", False))
            if self._wake_enabled:
                self._awake = False   # start asleep; the wake word brings it up
            else:
                self._awake = True    # gate off -> always listening
            _send_event({"type": "status",
                         "state": "awake" if self._awake else "asleep",
                         "message": "Wake word armed" if self._wake_enabled else "Wake word off"})
        elif msg_type == "voice_dsp" and self._voice is not None:
            opts = {}
            if "enabled" in msg:
                opts["enabled"] = msg["enabled"]
            for key, name in (("semitones", "semitones"), ("chorus", "chorus"),
                              ("bass", "bass_db"), ("darken", "darken")):
                if key in msg and msg[key] is not None:
                    opts[name] = float(msg[key])
            self._voice.set(**opts)
            _send_event({"type": "status", "state": "voice_dsp",
                         "message": "Voice DSP: " + (
                             "off" if not self._voice.enabled else
                             f"depth={self._voice.semitones:+.1f}st chorus={self._voice.chorus:.2f}"
                             f" bass={self._voice.bass_db:+.0f}dB dark={self._voice.darken:.2f}")})
        elif msg_type == "computer_act_result":
            try:
                seq = int(msg.get("id"))
            except (TypeError, ValueError):
                seq = -1
            fut = self._computer_act_responses.get(seq)
            if fut is not None and not fut.done():
                fut.set_result(str(msg.get("result", "") or ""))
        elif msg_type == "set_live_vision":
            self._live_vision = bool(msg.get("enabled", False))
            if not self._live_vision:
                self._pending_vision = None
            _send_event({"type": "status", "state": "live_vision",
                         "message": f"Live webcam vision {'ON' if self._live_vision else 'OFF'}."})
        elif msg_type == "set_input_device":
            idx = msg.get("index")
            if idx is None:
                self._mic_device = None
            else:
                try:
                    self._mic_device = int(idx)
                except (TypeError, ValueError):
                    self._mic_device = None
            _send_event({"type": "status", "state": "mic_device",
                         "message": f"Input device: {self._mic_device}"})
        elif msg_type == "text":
            await self.send_text(msg.get("text", ""))
        elif msg_type == "interrupt":
            await self.interrupt()
            self._speaking = False
            # Clear audio queue
            while not self._audio_out_queue.empty():
                try:
                    self._audio_out_queue.get_nowait()
                except asyncio.QueueEmpty:
                    break
        elif msg_type == "tool_result":
            call_id = msg.get("id", "")
            result = msg.get("result", "")
            # Tool results are sent back to Gemini via the session
            if self.session and call_id:
                try:
                    await self.session.send_tool_response(
                        function_responses=[{
                            "id": call_id,
                            "name": msg.get("name", ""),
                            "response": {"result": result},
                        }]
                    )
                except Exception as e:
                    print(f"Tool response error: {e}", file=sys.stderr)
        elif msg_type == "set_personality":
            for k in DEFAULT_PERSONA:
                if k not in msg:
                    continue
                if k == "notes":
                    self.persona[k] = str(msg[k] or "")
                else:
                    try:
                        self.persona[k] = int(msg[k])
                    except (TypeError, ValueError):
                        pass
            _send_event({"type": "status", "state": "personality",
                         "message": "Personality updated."})
            if self.session is not None:
                await self.stop()
                await self.start()
        elif msg_type == "start":
            # Configure / begin the session. The C# launcher always sends a
            # `start` after spawning us, but we must not double-connect when
            # main() already started from the env key. Only (re)connect if not
            # already connected, or if the key/voice/personality actually changed.
            new_key = msg.get("api_key", self.api_key)
            new_voice = msg.get("voice", self.voice)
            new_persona = msg.get("personality")
            if new_voice not in AVAILABLE_VOICES:
                new_voice = DEFAULT_VOICE
            persona_before = dict(self.persona)
            if isinstance(new_persona, dict):
                for k in DEFAULT_PERSONA:
                    if k in new_persona:
                        self.persona[k] = new_persona[k]
            key_changed = new_key and new_key != self.api_key
            voice_changed = new_voice != self.voice
            persona_changed = persona_before != self.persona
            if self._start_requested and not key_changed and not voice_changed and not persona_changed:
                # main() already requested a start with the same config; ignore.
                return
            self._start_requested = True
            self.api_key = new_key or self.api_key
            self.voice = new_voice
            if self.session is not None:
                await self.stop()
            await self.start()


# ============================================================
# IPC HELPERS
# ============================================================

def _send_event(event: dict):
    try:
        line = json.dumps(event, ensure_ascii=False)
        sys.stdout.write(line + "\n")
        sys.stdout.flush()
    except Exception:
        pass


# ============================================================
# MAIN
# ============================================================

async def main():
    # Config comes from env vars (set by C# launcher) and the `start` IPC message.
    api_key = os.environ.get("GEMINI_API_KEY", "")
    voice = os.environ.get("ULTRON_VOICE", DEFAULT_VOICE)

    session = GeminiSession(api_key, voice)

    # If no key was provided via env, wait for a `start` message carrying one.
    if not api_key:
        _send_event({"type": "status", "state": "waiting", "message": "Waiting for API key..."})
        from threading import Thread
        import queue as _queue
        q = _queue.Queue()

        def read_start():
            try:
                for line in sys.stdin:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        msg = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    if msg.get("type") == "start":
                        q.put(msg)
                        return
            except Exception:
                pass

        t = Thread(target=read_start, daemon=True)
        t.start()
        msg = q.get()
        api_key = msg.get("api_key", "") or api_key
        voice = msg.get("voice", voice)
        if not api_key:
            _send_event({"type": "status", "state": "waiting", "message": "No API key provided."})
            return
        session.api_key = api_key
        if voice in AVAILABLE_VOICES:
            session.voice = voice
        persona = msg.get("personality")
        if isinstance(persona, dict):
            for k in DEFAULT_PERSONA:
                if k in persona:
                    session.persona[k] = persona[k]

    # Begin the session only once; the IPC loop (started inside start())
    # will ignore later `start` messages while already connected.
    session._start_requested = True
    try:
        await session.start()
    except Exception as e:
        _send_event({"type": "error", "message": f"Backend fatal: {e}"})
        traceback.print_exc()


if __name__ == "__main__":
    asyncio.run(main())
