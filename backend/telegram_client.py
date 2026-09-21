"""Telegram account client for ULTRON.

Phase 1 scope: user-account authentication and a securely stored session for
the real-time voice call feature. Call signaling and media arrive in later
phases; this module deliberately contains no call code yet.

Security model:
  - The Telethon session is stored as a StringSession blob DPAPI-encrypted at
    rest (CryptProtectData, CurrentUser scope) - the same protection the C#
    side uses for config.dat. No plaintext session ever touches disk.
  - api_hash / session / login codes are never written to logs or events.
"""

from __future__ import annotations

import asyncio
import base64
import ctypes
import os
import random
import sys
import time
import traceback
from typing import Awaitable, Callable, Optional

APP_DIR = os.path.join(
    os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "Ultron"
)
SESSION_FILE = os.path.join(APP_DIR, "telegram.session")

# ---------------------------------------------------------------------------
# Windows DPAPI helpers (no pywin32 dependency)
# ---------------------------------------------------------------------------


class _DATA_BLOB(ctypes.Structure):
    _fields_ = [("cbData", ctypes.c_ulong), ("pbData", ctypes.c_void_p)]


def dpapi_encrypt(data: bytes) -> str:
    """Encrypt bytes with CryptProtectData (CurrentUser), return base64 str."""
    buf = ctypes.create_string_buffer(data)
    blob_in = _DATA_BLOB(len(data), ctypes.cast(buf, ctypes.c_void_p))
    blob_out = _DATA_BLOB()
    if not ctypes.windll.crypt32.CryptProtectData(
        ctypes.byref(blob_in), None, None, None, None, 0, ctypes.byref(blob_out)
    ):
        raise OSError(ctypes.WinError().winerror, "CryptProtectData failed")
    try:
        out = ctypes.string_at(blob_out.pbData, blob_out.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(ctypes.c_void_p(blob_out.pbData))
    return base64.b64encode(out).decode("ascii")


def dpapi_decrypt(b64: str) -> bytes:
    """Decrypt a base64 CryptProtectData blob (CurrentUser), return bytes."""
    raw = base64.b64decode(b64)
    buf = ctypes.create_string_buffer(raw)
    blob_in = _DATA_BLOB(len(raw), ctypes.cast(buf, ctypes.c_void_p))
    blob_out = _DATA_BLOB()
    if not ctypes.windll.crypt32.CryptUnprotectData(
        ctypes.byref(blob_in), None, None, None, None, 0, ctypes.byref(blob_out)
    ):
        raise OSError(ctypes.WinError().winerror, "CryptUnprotectData failed")
    try:
        return ctypes.string_at(blob_out.pbData, blob_out.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(ctypes.c_void_p(blob_out.pbData))


class FakeDPAPI:
    """XOR-obfuscation stand-in so the client is importable/testable off Windows."""

    @staticmethod
    def encrypt(data: bytes) -> str:
        return base64.b64encode(bytes(b ^ 0x5A for b in data)).decode("ascii")

    @staticmethod
    def decrypt(b64: str) -> bytes:
        raw = base64.b64decode(b64)
        return bytes(b ^ 0x5A for b in raw)


if sys.platform == "win32":
    _protect = dpapi_encrypt
    _unprotect = dpapi_decrypt
else:
    _protect = FakeDPAPI.encrypt
    _unprotect = FakeDPAPI.decrypt


def _redact(text: str) -> str:
    text = str(text)
    api_hash = os.environ.get("ULTRON_TELEGRAM_API_HASH", "")
    if api_hash:
        text = text.replace(api_hash, "[api_hash]")
    return text


# ---------------------------------------------------------------------------
# Sessions (DPAPI-encrypted StringSession at rest)
# ---------------------------------------------------------------------------


def load_session() -> Optional[str]:
    """Return the DPAPI-decrypted Telethon session string, or None."""
    try:
        if not os.path.exists(SESSION_FILE):
            return None
        with open(SESSION_FILE, "r", encoding="ascii") as f:
            return _unprotect(f.read().strip()).decode("utf-8") or None
    except Exception:
        return None


def save_session(session_string: str) -> None:
    os.makedirs(APP_DIR, exist_ok=True)
    blob = _protect(session_string.encode("utf-8"))
    with open(SESSION_FILE, "w", encoding="ascii") as f:
        f.write(blob)


def clear_session() -> None:
    try:
        os.remove(SESSION_FILE)
    except OSError:
        pass


# ---------------------------------------------------------------------------
# Controller
# ---------------------------------------------------------------------------


class TelegramController:
    """Owns the Telethon client lifecycle under the running backend loop."""

    def __init__(self, emit: Callable[[dict], None]):
        self._emit_fn = emit
        self._phase = "off"
        self._message = ""
        self._api_id: Optional[int] = None
        self._api_hash = ""
        self._phone = ""
        self._api_id = None  # reset on every explicit login
        self._api_hash = ""
        self._phone = ""
        self._pending_code_hash = None
        self._session_string: Optional[str] = load_session()
        self._checking = asyncio.Lock()
        self._client: Optional[object] = None
        self._target_user_id: Optional[str] = None
        self._target_username = ""
        self._target_name = ""

    # -- events -------------------------------------------------------------

    def _emit(self, **payload: object) -> None:
        self._emit_fn(dict(payload))

    def _status(self, phase: str, message: str) -> None:
        self._phase = phase
        self._message = message
        self._emit(type="telegram_status", phase=phase,
                   message=_redact(message), available=phase == "connected")

    # -- env/config ---------------------------------------------------------

    def _creds_from_env(self):
        api_id = os.environ.get("ULTRON_TELEGRAM_API_ID", "").strip()
        api_hash = os.environ.get("ULTRON_TELEGRAM_API_HASH", "").strip()
        return api_id, api_hash

    # -- client construction ------------------------------------------------

    def _build(self) -> "object":
        import telethon  # noqa: F401  (deliberately importable-only on demand)
        from telethon import TelegramClient
        from telethon.sessions import StringSession
        if self._session_string:
            client = TelegramClient(StringSession(self._session_string),
                                    self._api_id, self._api_hash)
        else:
            client = TelegramClient(StringSession(),
                                    self._api_id, self._api_hash)
        return client

    @property
    def phase(self) -> str:
        return self._phase

    async def run(self) -> None:
        """Background entry: silently validate a stored session at startup."""
        api_id, api_hash = self._creds_from_env()
        if not api_id or not api_hash:
            self._status("off", "Telegram call account not configured.")
            return
        try:
            import telethon  # noqa: F401
        except ImportError:
            self._status("unavailable", "Telethon is not installed.")
            return
        if not self._session_string:
            self._status("off", "Telegram call account not logged in.")
            return
        self._api_id = int(api_id)
        self._api_hash = api_hash
        self._status("handshaking", "Validating Telegram session...")
        try:
            if await self._valid_session():
                self._status("connected",
                             "Telegram call account CONNECTED (session restored).")
            else:
                self._session_string = None
                clear_session()
                self._status("off", "Saved Telegram session is no longer valid.")
        except Exception as e:
            self._status("off", f"Telegram session check failed: {type(e).__name__}")

    async def _valid_session(self) -> bool:
        client = self._build()
        try:
            await client.connect()
            if not await client.is_user_authorized():
                return False
            me = await client.get_me()
            self._phone = me.phone if me and getattr(me, "phone", None) else self._phone
            return True
        finally:
            try:
                await client.disconnect()
            except Exception:
                pass

    # -- IPC handlers -------------------------------------------------------

    async def login(self, msg: dict) -> None:
        """Start or resume login. Carries api_id/api_hash/phone when provided."""
        api_id = str(msg.get("api_id") or os.environ.get("ULTRON_TELEGRAM_API_ID", "")).strip()
        api_hash = str(msg.get("api_hash") or os.environ.get("ULTRON_TELEGRAM_API_HASH", "")).strip()
        phone = str(msg.get("phone") or "").strip()
        if not api_id or not api_hash:
            self._status("off", "Telegram api_id/api_hash missing.")
            return
        try:
            self._api_id = int(api_id)
        except (TypeError, ValueError):
            self._status("off", "Telegram api_id is invalid.")
            return
        self._api_hash = api_hash
        if phone:
            self._phone = phone

        self._status("handshaking", "Connecting to Telegram...")
        if self._client is not None:
            try:
                await self._client.disconnect()
            except Exception:
                pass
            self._client = None
        client = self._build()
        self._client = client
        try:
            await client.connect()
            if await client.is_user_authorized():
                await self._finish_authorized(client)
                return
            if not self._phone:
                await client.disconnect()
                self._status("awaiting_code", "Telegram phone number required.")
                return
            self._session_string = client.session.save()
            await self._request_code(client)
        except Exception as e:
            try:
                await client.disconnect()
            except Exception:
                pass
            self._client = None
            self._status("off", f"Telegram connect failed: {type(e).__name__}")

    async def _request_code(self, client: "object") -> None:
        try:
            self._pending_code_hash = None
            sent = await client.send_code_request(self._phone)
            self._pending_code_hash = getattr(sent, "phone_code_hash", None)
            if not isinstance(self._pending_code_hash, str):
                self._status("off", "Telegram did not return a code hash.")
                return
            self._status("awaiting_code", f"Code requested for {self._phone}.")
            self._emit(type="telegram_code_required",
                       phone=self._phone, hint=f"Enter the login code for {self._phone}.")
        except Exception as e:
            self._status("off", f"Could not send code: {type(e).__name__}")

    async def submit_code(self, msg: dict) -> None:
        """Complete login with the SMS/Telegram code (or 2FA password)."""
        code = str(msg.get("code") or "").strip()
        password = str(msg.get("password") or "").strip()
        if not code and not password:
            self._emit(type="telegram_code_result", ok=False,
                       message="No code provided.")
            return
        if self._api_id is None or self._api_hash == "":
            self._emit(type="telegram_code_result", ok=False,
                       message="Start a login first (telegram_login).")
            return
        if password:
            pass
        elif not self._pending_code_hash:
            # The phone_code_hash is per-code-request; without an active request
            # Telethon cannot finish the sign-in. Ask the user to reconnect.
            self._emit(type="telegram_code_result", ok=False,
                       message="Login expired — press Connect again to get a new code.")
            return

        client = self._client if self._client is not None else self._build()
        self._client = client
        try:
            if not client.is_connected():
                await client.connect()
            if password:
                await client.sign_in(password=password)
            else:
                await client.sign_in(phone=self._phone, code=code,
                                     phone_code_hash=self._pending_code_hash)
            await self._finish_authorized(client)
        except Exception as e:
            try:
                await client.disconnect()
            except Exception:
                pass
            self._client = None
            traceback.print_exc()
            self._emit(type="telegram_code_result", ok=False,
                       message=f"Login failed: {type(e).__name__}: "
                               f"{_redact(str(e))}")

    async def _finish_authorized(self, client: "object") -> None:
        self._session_string = client.session.save()
        save_session(self._session_string)
        self._pending_code_hash = None
        me = None
        try:
            me = await client.get_me()
        except Exception:
            pass
        name = getattr(me, "first_name", "") or self._phone
        self._phone = getattr(me, "phone", "") or self._phone
        try:
            await client.disconnect()
        except Exception:
            pass
        self._client = None
        self._status("connected", f"Telegram call account CONNECTED ({name}).")
        self._emit(type="telegram_code_result", ok=True,
                   message="Logged in successfully.")

    async def status(self) -> None:
        """Report current phase; validate a stored session if we have creds."""
        async with self._checking:
            api_id, api_hash = self._creds_from_env()
            if self._phase == "connected":
                self._status("connected", self._message)
                return
            if self._session_string and api_id and api_hash:
                self._api_id = int(api_id)
                self._api_hash = api_hash
                self._status("handshaking", "Validating Telegram session...")
                try:
                    if await self._valid_session():
                        self._status("connected",
                                     "Telegram call account CONNECTED.")
                    else:
                        self._session_string = None
                        clear_session()
                        self._status("off", "Saved Telegram session is invalid.")
                except Exception as e:
                    self._status("off",
                                 f"Telegram session check failed: {type(e).__name__}")
            else:
                self._status(self._phase, self._message)

    async def logout(self) -> None:
        """Log out on Telegram's side and drop the local session."""
        self._status("handshaking", "Logging out of Telegram...")
        client = self._client
        try:
            if client is None and self._session_string and self._api_id is not None:
                client = self._build()
            if client is not None:
                if not client.is_connected():
                    await client.connect()
                await client.log_out()
        except Exception:
            pass
        finally:
            if client is not None:
                try:
                    await client.disconnect()
                except Exception:
                    pass
        self._client = None
        self._session_string = None
        self._pending_code_hash = None
        clear_session()
        self._status("off", "Telegram call account logged out.")

    # -- call target resolution (Phase 2) -----------------------------------

    async def resolve_target(self, msg: dict) -> None:
        """Resolve the configured call target to a Telegram account.

        Accepts a username ("@user"), a phone number, or a numeric peer id.
        Validates the resolved entity is a callable user (not a bot/chat) and
        caches the user id for the outgoing-call phases. Never logs the raw
        target if it could be sensitive, and never stores it in source.
        """
        target = str(msg.get("target") or "").strip()
        if not target:
            self._emit(type="telegram_target_result", ok=False,
                       message="No call target provided.")
            return
        if self._api_id is None or self._api_hash == "":
            self._emit(type="telegram_target_result", ok=False,
                       message="Telegram account not configured.")
            return

        client = self._client
        try:
            if client is None or not client.is_connected():
                client = self._build()
                await client.connect()
            if not await client.is_user_authorized():
                self._emit(type="telegram_target_result", ok=False,
                           message="Connect the Telegram account first.")
                return

            from telethon.tl.types import User
            entity = await client.get_entity(target)
            if not isinstance(entity, User):
                self._emit(type="telegram_target_result", ok=False,
                           message="Target is not a Telegram user "
                                   "(resolved to a chat or channel).")
                return
            if entity.bot:
                self._emit(type="telegram_target_result", ok=False,
                           message="Target is a bot; Telegram calls need a person.")
                return

            self._target_user_id = str(entity.id)
            self._target_username = getattr(entity, "username", None) or ""
            self._target_name = getattr(entity, "first_name", "") or ""
            name_part = "" if entity.bot else f" ({self._target_name})".strip()
            self._emit(type="telegram_target_result", ok=True,
                       target=self._target_username or target,
                       user_id=self._target_user_id,
                       name=self._target_name,
                       callable=True,
                       message=f"Target resolved: {self._target_username or '<user>'}{name_part} "
                               f"[{self._target_user_id}]")
        except Exception as e:
            self._emit(type="telegram_target_result", ok=False,
                       message=f"Could not resolve target: "
                               f"{type(e).__name__}: {_redact(str(e))}")

    # -- call state machine (Phase C7) ----------------------------------------

    def _creds_from_config(self) -> tuple[str, str, str]:
        """Load api_id, api_hash, target from env or config.dat (case-insensitive)."""
        api_id = os.environ.get("ULTRON_TELEGRAM_API_ID", "").strip()
        api_hash = os.environ.get("ULTRON_TELEGRAM_API_HASH", "").strip()
        target = os.environ.get("ULTRON_TELEGRAM_TARGET", "").strip() or "@HerA9el"
        if api_id and api_hash:
            return api_id, api_hash, target
        try:
            if sys.platform == "win32":
                import json
                config_path = os.path.join(APP_DIR, "config.dat")
                if os.path.exists(config_path):
                    with open(config_path, "rb") as f:
                        data = json.loads(dpapi_decrypt(f.read()).decode("utf-8"))
                    s = {k.lower(): v for k, v in data.items()}
                    api_id = str(s.get("telegramapiid", "")).strip()
                    api_hash = str(s.get("telegramapihash", "")).strip()
                    target = str(s.get("telegramcalltarget", "")).strip() or target
                    if api_id and api_hash:
                        return api_id, api_hash, target
        except Exception:
            pass
        return api_id, api_hash, target

    def _make_tone(self, path: str, seconds: int = 30, freq: float = 440.0) -> None:
        """Generate a 440Hz sine tone PCM file (48kHz mono). Pure-Python fallback."""
        import math
        import wave
        n = seconds * 48000
        try:
            import numpy as np  # type: ignore
            data = (0.3 * np.sin(2 * np.pi * freq * np.arange(n) / 48000)
                    * 32767).astype(np.int16)
            data.tofile(path)
        except Exception:
            samples = [int(0.3 * 32767 * math.sin(2 * math.pi * freq * i / 48000))
                       for i in range(n)]
            with wave.open(path, "wb") as w:
                w.setnchannels(1)
                w.setsampwidth(2)
                w.setframerate(48000)
                w.writeframes(b"".join(int(s).to_bytes(2, "little", signed=True) for s in samples))

    async def _call_emit_state(self, state: str, message: str = "") -> None:
        self._emit(type="telegram_call_state", state=state, message=message)

    async def call_start(self, msg: dict, mic_queue: asyncio.Queue = None, audio_out_queue: asyncio.Queue = None) -> None:
        """Start an outgoing Telegram voice call (C7/C8 state machine with fallback)."""
        # Gate: TelegramCallEnabled must be True (from env or settings)
        enabled = os.environ.get("ULTRON_TELEGRAM_ENABLED", "").strip().lower()
        if enabled not in ("1", "true", "yes", "on"):
            # Also check config.dat for the flag
            try:
                if sys.platform == "win32":
                    import json
                    config_path = os.path.join(APP_DIR, "config.dat")
                    if os.path.exists(config_path):
                        with open(config_path, "rb") as f:
                            data = json.loads(dpapi_decrypt(f.read()).decode("utf-8"))
                        val = data.get("TelegramCallEnabled", False)
                        if not val:
                            self._emit(type="telegram_call_result", ok=False,
                                       message="Telegram calling is disabled (TelegramCallEnabled=false).")
                            return
            except Exception:
                pass
            self._emit(type="telegram_call_result", ok=False,
                       message="Telegram calling is disabled (TelegramCallEnabled=false).")
            return

        # C10: Concurrent call prevention
        if hasattr(self, "_call_task") and self._call_task and not self._call_task.done():
            self._emit(type="telegram_call_result", ok=False,
                       message="Call already in progress. Stop current call first.")
            return

        # Must have a resolved target
        if not self._target_user_id:
            self._emit(type="telegram_call_result", ok=False,
                       message="No call target resolved. Use telegram_resolve_target first.")
            return

        api_id, api_hash, target = self._creds_from_config()
        if not api_id or not api_hash:
            self._emit(type="telegram_call_result", ok=False,
                       message="Telegram api_id/api_hash not configured.")
            return

        session = load_session()
        if not session:
            self._emit(type="telegram_call_result", ok=False,
                       message="No saved Telegram session. Login first.")
            return

        # Extract payload (notification content for fallback)
        payload = msg.get("payload", {})
        if payload:
            self._call_payload = payload
        else:
            self._call_payload = {}

        # Store audio queues for the call task
        self._gemini_mic_queue = mic_queue
        self._gemini_audio_out_queue = audio_out_queue

        await self._call_emit_state("ringing", f"Calling {target}...")

        # Run the call in a background task so we can return immediately
        self._call_task = asyncio.create_task(self._run_call_task(api_id, api_hash, session, target))

    async def call_stop(self) -> None:
        """Stop/hangup the current outgoing call."""
        if hasattr(self, "_call_task") and self._call_task and not self._call_task.done():
            self._call_task.cancel()
            try:
                await self._call_task
            except asyncio.CancelledError:
                pass
            await self._call_emit_state("ended", "Call stopped by user.")
        else:
            self._emit(type="telegram_call_result", ok=False,
                       message="No active call to stop.")

    async def call_status(self) -> None:
        """Report current call phase (placeholder for future state tracking)."""
        phase = getattr(self, "_call_phase", "idle")
        self._emit(type="telegram_call_state", state=phase, message="")

    async def _run_call_task(self, api_id: str, api_hash: str, session: str, target: str) -> None:
        """Background task that runs the full call flow with fallback messaging."""
        import telethon
        from telethon import TelegramClient, events
        from telethon.sessions import StringSession
        from telethon.tl import functions as f, types as t

        import ntgcalls
        import numpy as np  # noqa: F401

        # Import the audio bridge
        from call_audio_bridge import InCallAudioBridge

        TELEGRAM_SR = 48000

        # Call timing state for fallback evaluation
        self._call_start_ts = time.time()
        self._call_answered_ts = None
        self._call_first_speech_ts = None
        self._call_first_real_speech_ts = None  # Track first NON-tone speech from remote

        self._call_phase = "ringing"
        self._call_task = asyncio.current_task()

        # Audio bridge (will be initialized after we have the Gemini session queues)
        self._audio_bridge: Optional[InCallAudioBridge] = None

        try:
            client = TelegramClient(StringSession(session), int(api_id), api_hash)
            wrtc = ntgcalls.NTgCalls()

            accepted = asyncio.Event()
            accepted_pc = None
            discarded = asyncio.Event()
            call_id = None
            call_access_hash = None
            last_connection_id = 0
            rx_bytes = 0

            # Connection quality tracking
            connection_quality = {"last_frame": time.time(), "frame_count": 0}

            def on_frames(chat_id, mode, device, frames):
                    nonlocal rx_bytes
                    mode_name = getattr(mode, "name", None) or str(mode)
                    for fr in frames:
                        data = getattr(fr, "data", None)
                        if data and mode_name == "PLAYBACK":
                            rx_bytes += len(data)
                            # Feed to audio bridge for resampling to Gemini
                            if self._audio_bridge:
                                self._audio_bridge.feed_telegram_rx(data)
                            # Track first real speech from remote
                            if self._call_first_speech_ts is None:
                                self._call_first_speech_ts = time.time()
                            # Update connection quality
                            connection_quality["last_frame"] = time.time()
                            connection_quality["frame_count"] += 1

            def on_conn_change(chat_id, state):
                pass  # silent

            wrtc.on_frames(on_frames)
            wrtc.on_connection_change(on_conn_change)

            async def on_update(update):
                nonlocal accepted_pc
                if not isinstance(update, t.UpdatePhoneCall):
                    return
                pc = update.phone_call
                if isinstance(pc, t.PhoneCallAccepted):
                    call_id = pc.id
                    call_access_hash = pc.access_hash
                    accepted_pc = pc
                    self._call_answered_ts = time.time()
                    accepted.set()
                elif isinstance(pc, t.PhoneCallDiscarded):
                    discarded.set()

            client.add_event_handler(on_update, events.Raw)

            await client.connect()
            if not await client.is_user_authorized():
                await self._call_emit_state("error", "Session not authorized")
                await client.disconnect()
                return

            # 1. create_p2p_call
            input_user = await client.get_input_entity(target)
            if not isinstance(input_user, t.InputPeerUser):
                await self._call_emit_state("error", "Target is not a user")
                await client.disconnect()
                return
            user_id = input_user.user_id
            await wrtc.create_p2p_call(user_id)

            # 2. get_protocol + getDhConfig
            proto = wrtc.get_protocol()
            from telethon.tl.types import PhoneCallProtocol
            protocol = PhoneCallProtocol(
                min_layer=proto.min_layer,
                max_layer=proto.max_layer,
                library_versions=list(proto.library_versions),
                udp_p2p=bool(getattr(proto, "udp_p2p", False)),
                udp_reflector=bool(getattr(proto, "udp_reflector", False)),
            )
            dh = await client(f.messages.GetDhConfigRequest(version=0, random_length=256))
            dh_config = ntgcalls.DhConfig(g=dh.g, p=bytes(dh.p), random=bytes(dh.random))
            g_a_hash = await wrtc.init_exchange(user_id, dh_config, None)

            # 3. RequestCall (rings the phone)
            random_id = random.randint(-2**31, 2**31 - 1)
            result = await client(f.phone.RequestCallRequest(
                user_id=t.InputUser(user_id=input_user.user_id, access_hash=input_user.access_hash),
                g_a_hash=g_a_hash,
                protocol=protocol,
                video=False,
                random_id=random_id,
            ))
            waiting = result.phone_call
            if not isinstance(waiting, t.PhoneCallWaiting):
                await self._call_emit_state("error", f"Unexpected RequestCall result: {type(waiting).__name__}")
                await client.disconnect()
                return
            call_id = waiting.id
            call_access_hash = waiting.access_hash
            await self._call_emit_state("ringing", f"Ringing {target}...")

            # 4. Wait for answer or timeout
            accepted_task = asyncio.create_task(accepted.wait())
            discarded_task = asyncio.create_task(discarded.wait())
            timeout_task = asyncio.create_task(asyncio.sleep(60))  # 60s ring timeout

            done, pending = await asyncio.wait(
                [accepted_task, discarded_task, timeout_task],
                return_when=asyncio.FIRST_COMPLETED
            )
            for task in pending:
                task.cancel()

            if discarded_task in done:
                await self._call_emit_state("ended", "Call declined by peer")
                await self._evaluate_and_send_fallback(client, "declined")
                await client.disconnect()
                return
            if timeout_task in done:
                await self._call_emit_state("ended", "Ring timeout")
                await self._evaluate_and_send_fallback(client, "timeout")
                await client.disconnect()
                return

            # 5. PhoneCallAccepted - exchange keys + ConfirmCall
            await self._call_emit_state("connecting", "Call accepted, setting up media...")
            auth = await wrtc.exchange_keys(user_id, bytes(accepted_pc.g_b), 0)
            g_a = bytes(auth.g_a_or_b)
            kw = getattr(auth, "key_fingerprint", None)

            protocol = PhoneCallProtocol(
                min_layer=proto.min_layer,
                max_layer=proto.max_layer,
                library_versions=list(proto.library_versions),
                udp_p2p=bool(getattr(proto, "udp_p2p", False)),
                udp_reflector=bool(getattr(proto, "udp_reflector", False)),
            )
            conn = await client(f.phone.ConfirmCallRequest(
                peer=t.InputPhoneCall(id=accepted_pc.id, access_hash=accepted_pc.access_hash),
                g_a=g_a,
                key_fingerprint=kw,
                protocol=protocol,
            ))
            phone_call = conn.phone_call
            if not isinstance(phone_call, t.PhoneCall):
                await self._call_emit_state("error", f"ConfirmCall failed: {type(phone_call).__name__}")
                await client.disconnect()
                return

            call_id = phone_call.id
            call_access_hash = phone_call.access_hash

            # Extract RTC servers + connection_id (from Telethon TL, NOT native RTCServer)
            servers = []
            conn_id = 0
            if phone_call.connections:
                conn_id = phone_call.connections[0].id
                for c in phone_call.connections:
                    if isinstance(c, t.PhoneConnectionWebrtc):
                        servers.append(ntgcalls.RTCServer(
                            id=c.id, ipv4=c.ip, ipv6=c.ipv6, port=c.port,
                            username=c.username, password=c.password,
                            turn=bool(c.turn or False), stun=bool(c.stun or False),
                            tcp=False, peer_tag=None))
                    elif isinstance(c, t.PhoneConnection):
                        servers.append(ntgcalls.RTCServer(
                            id=c.id, ipv4=c.ip, ipv6=c.ipv6, port=c.port,
                            username=None, password=None,
                            turn=False, stun=False,
                            tcp=bool(c.tcp or False), peer_tag=c.peer_tag))

            last_connection_id = conn_id
            versions = list(phone_call.protocol.library_versions)
            p2p_allowed = bool(phone_call.p2p_allowed or False)

            # 6. Initialize audio bridge with EXTERNAL source (named pipe)
            self._audio_bridge = InCallAudioBridge(
                mic_queue=self._gemini_mic_queue,
                audio_out_queue=self._gemini_audio_out_queue,
                on_remote_speech=lambda speaking: asyncio.create_task(
                    self._call_emit_state("remote_speech", "started" if speaking else "ended")
                )
            )
            
            # Configure ntgcalls to use EXTERNAL source (named pipe) for TX
            await wrtc.set_stream_sources(
                user_id,
                ntgcalls.StreamMode.CAPTURE,
                ntgcalls.MediaDescription(
                    microphone=ntgcalls.AudioDescription(
                        media_source=ntgcalls.MediaSource.EXTERNAL,
                        input=self._audio_bridge.get_pipe_name(),
                        sample_rate=TELEGRAM_SR,
                        channel_count=1,
                    ),
                ),
            )
            
            # Start audio bridge (creates named pipe, waits for ntgcalls to connect)
            await self._audio_bridge.start()
            
            await wrtc.connect_p2p(user_id, servers, versions, p2p_allowed)
            await self._call_emit_state("connected", f"Call live with {target}")

            # 7. Wait for hangup (no auto-timeout - conversation-driven duration)
            # Also monitor connection health
            self._call_phase = "connected"
            
            # C10: Connection health monitor task
            connection_lost = asyncio.Event()
            connection_quality = {"last_frame": time.time(), "frame_count": 0}
            
            async def connection_monitor():
                """Monitor connection health, trigger reconnect if needed."""
                while not connection_lost.is_set():
                    await asyncio.sleep(2.0)
                    if connection_lost.is_set():
                        break
                    # Check if we haven't received frames for > 10 seconds
                    if time.time() - connection_quality["last_frame"] > 10.0:
                        if connection_quality["frame_count"] > 0:
                            await self._call_emit_state("reconnecting", "Connection lost, attempting reconnect...")
                            connection_lost.set()
                            break
            
            monitor_task = asyncio.create_task(connection_monitor())
            
            # Update connection quality on each frame received
            original_on_frames = None
            
            # 7. Wait for hangup (no auto-timeout - conversation-driven duration)
            self._call_phase = "connected"
            # Wait indefinitely for discard (conversation ends when user hangs up)
            done, pending = await asyncio.wait(
                [asyncio.create_task(discarded.wait()), asyncio.create_task(connection_lost.wait())],
                return_when=asyncio.FIRST_COMPLETED
            )
            
            # Cancel pending tasks
            for task in pending:
                task.cancel()
                try:
                    await task
                except asyncio.CancelledError:
                    pass
            
            # Check if connection was lost (network issue)
            if connection_lost.is_set():
                await self._call_emit_state("reconnecting", "Network disconnected, call ended")
                # Send fallback if appropriate
                await self._evaluate_and_send_fallback(client, "network_disconnect")
                # Clean up
                await self._cleanup_call_resources(client, wrtc, user_id, call_id, call_access_hash, last_connection_id)
                return
            
            # Normal hangup - Clean up using shared method
            await self._cleanup_call_resources(client, wrtc, user_id, call_id, call_access_hash, last_connection_id)
            
            # 9. On discard - evaluate fallback
            await self._evaluate_and_send_fallback(client, "hangup")

        except asyncio.CancelledError:
            await self._call_emit_state("ended", "Call cancelled")
            # Don't send fallback on explicit cancel
        except Exception as e:
            await self._call_emit_state("error", f"Call failed: {type(e).__name__}: {_redact(str(e))}")
            # Clean up on error
            await self._cleanup_call_resources(
                client, wrtc, user_id, call_id, call_access_hash, last_connection_id
            )
        finally:
            self._call_phase = "idle"
            if hasattr(self, "_call_task"):
                self._call_task = None

    async def _cleanup_call_resources(
        self,
        client,
        wrtc,
        user_id: int,
        call_id: Optional[int],
        call_access_hash: Optional[int],
        connection_id: int,
    ) -> None:
        """Clean up all call resources safely."""
        try:
            if self._audio_bridge:
                await self._audio_bridge.stop()
        except Exception:
            pass
        try:
            await wrtc.stop(user_id)
        except Exception:
            pass
        if call_id and call_access_hash:
            try:
                await client(f.phone.DiscardCallRequest(
                    peer=t.InputPhoneCall(id=call_id, access_hash=call_access_hash),
                    duration=int(time.time() - self._call_start_ts),
                    reason=t.PhoneCallDiscardReasonHangup(),
                    connection_id=connection_id,
                    video=False,
                ))
            except Exception:
                pass

    async def _evaluate_and_send_fallback(self, client, reason: str) -> None:
        """Evaluate if fallback message should be sent and send it."""
        payload = getattr(self, "_call_payload", {})
        if not payload:
            return  # No payload = manual call, no fallback

        # Check if fallback is enabled (from env or config)
        fallback_enabled = os.environ.get("ULTRON_CALL_FALLBACK_ENABLED", "").strip().lower()
        if fallback_enabled not in ("1", "true", "yes", "on"):
            try:
                if sys.platform == "win32":
                    import json
                    config_path = os.path.join(APP_DIR, "config.dat")
                    if os.path.exists(config_path):
                        with open(config_path, "rb") as f:
                            data = json.loads(dpapi_decrypt(f.read()).decode("utf-8"))
                        val = data.get("CallFallbackEnabled", True)
                        if not val:
                            return
            except Exception:
                pass

        # Get fallback threshold (default 5 seconds)
        fallback_threshold = 5
        try:
            threshold_env = os.environ.get("ULTRON_CALL_FALLBACK_THRESHOLD", "").strip()
            if threshold_env:
                fallback_threshold = int(threshold_env)
            else:
                import json
                config_path = os.path.join(APP_DIR, "config.dat")
                if os.path.exists(config_path):
                    with open(config_path, "rb") as f:
                        data = json.loads(dpapi_decrypt(f.read()).decode("utf-8"))
                    val = data.get("CallFallbackThresholdSeconds", 5)
                    if isinstance(val, (int, float)) and val > 0:
                        fallback_threshold = int(val)
        except Exception:
            pass

        duration = time.time() - self._call_start_ts
        answered = self._call_answered_ts is not None
        spoke = self._call_first_speech_ts is not None

        # Conditions for fallback:
        # 1. Never answered (ring timeout or declined)
        # 2. Answered but hung up quickly (< threshold AND no real speech)
        should_fallback = False
        fallback_reason = ""
        
        if not answered:
            # Ring timeout or declined
            should_fallback = True
            fallback_reason = f"call {reason} (not answered)"
        elif duration < fallback_threshold and not spoke:
            # Answered but hung up immediately without real conversation
            should_fallback = True
            fallback_reason = f"call answered but ended quickly ({duration:.0f}s, no speech)"

        if should_fallback:
            await self._send_fallback_message(client, payload, fallback_reason)
        else:
            # Call had meaningful duration - no fallback needed
            pass

    async def _send_fallback_message(self, client, payload: dict, reason: str) -> None:
        """Send fallback Telegram message with the payload content."""
        try:
            # Build message text
            title = payload.get("title", "Ultron Notification")
            body = payload.get("body", "")
            source = payload.get("source", "ultron")
            priority = payload.get("priority", "normal")

            priority_emoji = {"high": "🔴", "normal": "📢", "low": "ℹ️"}.get(priority, "📢")
            
            text = f"{priority_emoji} **Call Fallback** ({reason})\n\n**{title}**\n{body}"

            # Send to the same target that was called
            target = self._target_username or self._target_user_id
            await client.send_message(target, text, parse_mode="md")
            
            self._emit(type="telegram_call_fallback_sent", ok=True, 
                       target=target, source=source, reason=reason)
        except Exception as e:
            self._emit(type="telegram_call_fallback_sent", ok=False, 
                       message=f"Failed to send fallback: {type(e).__name__}: {_redact(str(e))}")


def known_handles(client) -> list[str]:
    """Dedicated test hook for later phases (target resolution), not used yet."""
    return []


# ---------------------------------------------------------------------------
# Standalone sanity checks (no network). Run: python telegram_client.py
# ---------------------------------------------------------------------------


def run_self_test() -> int:
    import secrets
    import tempfile

    failures = 0

    def check(name: str, cond: bool) -> None:
        nonlocal failures
        print(f"  {'PASS' if cond else 'FAIL'}  {name}")
        if not cond:
            failures += 1

    blob = secrets.token_bytes(64)
    cipher = _protect(blob)
    check("dpapi encrypt/decrypt roundtrip", _unprotect(cipher) == blob)

    fake = FakeDPAPI()
    check("fake dpapi roundtrip", fake.decrypt(fake.encrypt(blob)) == blob)

    api_hash = "0123456789abcdef0123456789abcdef"
    os.environ["ULTRON_TELEGRAM_API_HASH"] = api_hash
    check("redact hides api_hash", api_hash not in _redact(f"hash={api_hash}"))
    check("redact leaves plain text", _redact("hello world") == "hello world")
    os.environ.pop("ULTRON_TELEGRAM_API_HASH", None)

    # Session file is DPAPI-encrypted (never plaintext).
    os.environ["ULTRON_TELEGRAM_API_HASH"] = ""
    with tempfile.TemporaryDirectory(dir=APP_DIR if os.path.isdir(APP_DIR) else None) as td:
        global SESSION_FILE
        saved = SESSION_FILE
        SESSION_FILE = os.path.join(td, "telegram.session")
        try:
            save_session("1sess-test123")
            with open(SESSION_FILE, "r", encoding="ascii") as f:
                on_disk = f.read()
            check("session persisted encrypted", "1sess-test123" not in on_disk)
            check("session load recovers value", load_session() == "1sess-test123")
            clear_session()
            check("session cleared", load_session() is None)
        finally:
            SESSION_FILE = saved

    async def _sm_test():
        events = []
        ctrl = TelegramController(lambda ev: events.append(ev))
        await ctrl.login({"api_id": "!bad", "api_hash": "hash"})
        check("bad api_id -> off status emitted",
              any(e.get("type") == "telegram_status" and e.get("phase") == "off"
                  for e in events))

    async def _submit_test():
        events = []
        ctrl = TelegramController(lambda ev: events.append(ev))
        ctrl._api_id = 1
        ctrl._api_hash = "x"
        await ctrl.submit_code({"code": "12345"})
        check("submit with no active code request -> clean error (no ValueError)",
              any(e.get("type") == "telegram_code_result" and not e.get("ok")
                  for e in events))

    async def _target_test():
        events = []
        ctrl = TelegramController(lambda ev: events.append(ev))
        await ctrl.resolve_target({})
        check("resolve with no target -> clean error",
              any(e.get("type") == "telegram_target_result" and not e.get("ok")
                  for e in events))
        events.clear()
        await ctrl.resolve_target({"target": "@owner"})
        check("resolve without configured creds -> clean error",
              any(e.get("type") == "telegram_target_result" and not e.get("ok")
                  for e in events))

    try:
        import asyncio
        asyncio.run(_sm_test())
        asyncio.run(_submit_test())
        asyncio.run(_target_test())
    except Exception as e:  # pragma: no cover
        check("state machine smoke", False)
        print(f"    exception: {e}")

    print(f"\n{'All telegram_client self-tests passed' if failures == 0 else f'{failures} FAILURES'}")
    return failures


if __name__ == "__main__":
    raise SystemExit(run_self_test())