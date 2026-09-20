"""C7: Outgoing Telegram voice-call bridge (flag-gated, default OFF).

Thin, faithful port of the PROVEN spike engine (`spike_telegram_call.py`,
C6 PASSED live: ring -> answer -> tone -> connect_p2p -> RX PCM). Both crash
fixes discovered during the spike are baked in here:

  * CRASH FIX #1 (loop binding): ntgcalls.NTgCalls() MUST be constructed
    inside the running event loop (not in __init__). Building it outside the
    loop causes "Future attached to a different event loop". We build it
    lazily in `run()`.
  * CRASH FIX #2 (native attr): the native `RTCServer` returned by
    ntgcalls exposes no Python attributes, so `servers[0].id` raises
    AttributeError. The connection id needed by connect_p2p comes from the
    Telethon TL object: `phone_call.connections[0].id`.

This module is a self-contained slice: it brings its own TelegramClient from
the saved DPAPI session (same as the spike) so it does NOT disturb the
backend controller's own client lifecycle. It is gated by `enabled()`, which
reads ULTRON_TELEGRAM_CALL_ENABLED (env) or the DPAPI config.dat key
"telegramcallenabled". Default is OFF at import time.

C8 (Gemini<->phone audio bridge) will swap the FILE-stream-source media side
for the Gemini mic queue; this module keeps the proven FILE-tone + RX PCM
path so the state machine is real now and media swaps later without touching
signaling. Hangup mirrors the spike (DiscardCallRequest + wrtc.stop + event).
"""

from __future__ import annotations

import asyncio
import base64
import ctypes
import json
import math
import os
import random
import sys
import tempfile
import time
import traceback
import wave
from pathlib import Path
from typing import (
    Any,
    Awaitable,
    Callable,
    List,
    Optional,
    Sequence,
    Tuple,
    Union,
)

APP_DIR = os.path.join(
    os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "Ultron"
)
CONFIG_PATH = os.path.join(APP_DIR, "config.dat")
SESSION_FILE = os.path.join(APP_DIR, "telegram.session")
TONE_SR = 48000

# Optional NumPy speeds up tone synthesis; pure-stdlib fallback provided.
try:
    import numpy as np  # type: ignore

    _HAS_NP = True
except Exception:  # pragma: no cover
    _HAS_NP = False


# ---------------------------------------------------------------------------
# Small shared bits (DPAPI + env creds) -- DO NOT duplicate; import from
# telegram_client when available, otherwise fall back to local copies so this
# module is testable standalone (spike convention).
# ---------------------------------------------------------------------------

try:  # reuse the production helpers when importable
    from telegram_client import (  # noqa: F401
        dpapi_decrypt as _dpapi_decrypt,
        load_session as _load_session,
        save_session as _save_session,
        clear_session as _clear_session,
    )

    _HAS_CONTROLLER = True
except Exception:  # pragma: no cover
    _HAS_CONTROLLER = False

    def _dpapi_decrypt(b64: str) -> bytes:
        raise RuntimeError("DPAPI stubs unavailable")  # never hit in prod

    _load_session = _save_session = _clear_session = None  # type: ignore


def _log(msg: str) -> None:
    print(f"[call_bridge] {msg}", flush=True)


def _redact(text: str) -> str:
    text = str(text)
    api_hash = os.environ.get("ULTRON_TELEGRAM_API_HASH", "")
    if api_hash:
        text = text.replace(api_hash, "[api_hash]")
    return text


# ---------------------------------------------------------------------------
# Flag / gate
# ---------------------------------------------------------------------------


def _config_value(key: str, default: Any = None) -> Any:
    """Read a case-insensitive key from the DPAPI JSON config.dat."""
    try:
        if not os.path.exists(CONFIG_PATH):
            return default
        try:
            with open(CONFIG_PATH, "rb") as f:
                blob = f.read()
            if _HAS_CONTROLLER:
                raw = _dpapi_decrypt(blob.decode("ascii", "replace"))
            else:
                raw = blob.decode("utf-8", "replace")
            data = json.loads(raw)
        except Exception:
            return default
        lowered = {str(k).lower(): v for k, v in data.items()}
        return lowered.get(key.lower(), default)
    except Exception:
        return default


def enabled() -> bool:
    """C7 gate: ULTRON_TELEGRAM_CALL_ENABLED env, else config.dat. OFF default."""
    env = os.environ.get("ULTRON_TELEGRAM_CALL_ENABLED", "").strip().lower()
    if env:
        return env in ("1", "true", "yes", "on")
    val = _config_value("telegramcallenabled", None)
    if isinstance(val, bool):
        return val
    if isinstance(val, str):
        return val.strip().lower() in ("1", "true", "yes", "on")
    if isinstance(val, (int, float)):
        return val != 0
    return False


def _creds_from_env() -> Tuple[str, str, str]:
    api_id = os.environ.get("ULTRON_TELEGRAM_API_ID", "").strip()
    api_hash = os.environ.get("ULTRON_TELEGRAM_API_HASH", "").strip()
    target = os.environ.get("ULTRON_TELEGRAM_TARGET", "").strip() or "@HerA9el"
    if api_id and api_hash:
        return api_id, api_hash, target
    try:
        if sys.platform == "win32" and os.path.exists(CONFIG_PATH):
            data = _config_value("", None)
            api_id = str(_config_value("telegramapiid", "") or "").strip()
            api_hash = str(_config_value("telegramapihash", "") or "").strip()
            target = str(_config_value("telegramcalltarget", "") or "").strip() or target
            if api_id and api_hash:
                _log("creds loaded from config.dat")
                return api_id, api_hash, target
    except Exception as e:
        _log(f"config.dat fallback failed: {type(e).__name__}")
    return api_id, api_hash, target


# ---------------------------------------------------------------------------
# Tone synthesis (pure-stdlib fallback so the module runs without numpy)
# ---------------------------------------------------------------------------


def make_tone(path: str, seconds: int = 20, freq: float = 440.0) -> None:
    if Path(path).exists() and Path(path).stat().st_size >= seconds * TONE_SR * 2:
        return
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    n = seconds * TONE_SR
    if _HAS_NP:
        data = 0.3 * np.sin(2 * np.pi * freq * np.arange(n) / TONE_SR)
        samples = (data * 32767).astype(np.int16)
    else:
        samples = [int(0.3 * 32767 * math.sin(2 * math.pi * freq * i / TONE_SR))
                   for i in range(n)]
    buf = b"".join(int(s).to_bytes(2, "little", signed=True) for s in samples)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(TONE_SR)
        w.writeframes(buf)


# ---------------------------------------------------------------------------
# The call bridge (media engine port)
# ---------------------------------------------------------------------------


class CallBridge:
    """Owns an outgoing Telegram voice call on the running loop.

    Construct inside the running loop (crash fix #1). `wrtc` is created
    lazily in `run()` so the loop is live when the media stack binds.
    """

    def __init__(
        self,
        target: str,
        api_id: int,
        api_hash: str,
        session_string: str,
        seconds: int = 20,
        timeout: int = 90,
        tone_path: str = "",
        on_event: Optional[Callable[[str, dict], Awaitable[None]]] = None,
    ) -> None:
        self.target = target
        self.api_id = api_id
        self.api_hash = api_hash
        self.session_string = session_string
        self.seconds = seconds
        self.timeout = timeout
        self.tone_path = tone_path
        self._on_event = on_event
        self.wrtc = None  # ntgcalls.NTgCalls, built in run() (crash fix #1)
        self.call_id: Optional[int] = None
        self.call_access_hash: Optional[int] = None
        self.last_connection_id: int = 0
        self._user_id: Optional[int] = None
        self.rtc_servers: List[Any] = []
        self.start_ts: float = 0.0
        self.accepted = asyncio.Event()
        self.discarded = asyncio.Event()
        self.rx_bytes: int = 0

    async def _notify(self, kind: str, **payload) -> None:
        if self._on_event:
            await self._on_event(kind, payload)

    # Overridable for offline self-tests.
    def _build_wrtc(self):
        import ntgcalls

        return ntgcalls.NTgCalls()

    async def run(self) -> int:
        from telethon import TelegramClient
        from telethon.sessions import StringSession
        import telethon
        from telethon import functions as f
        from telethon import types as t

        # crash fix #1: build the media stack inside the live loop
        self.wrtc = self._build_wrtc()

        client = TelegramClient(
            StringSession(self.session_string), self.api_id, self.api_hash
        )
        try:
            await client.connect()
            if not await client.is_user_authorized():
                await self._notify("error", message="Telegram session not authorized.")
                return 3
            me = await client.get_me()
            await self._notify("status", phase="preparing",
                               message=f"Calling {self.target}...")
            await self._dial(client, me)
            return 0
        finally:
            try:
                await client.disconnect()
            except Exception:
                pass

    # -- media RX/PCM logging (FILE tone out, RX PCM logged) --------------

    async def _on_frames_cb(...):
        """on_frames callback: incoming PCM from the phone -> log volume."""
