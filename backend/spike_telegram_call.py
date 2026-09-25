"""Phase 3 (C6) media spike: prove a private Telegram VoIP call works on Windows.

Standalone script - does NOT touch the running backend. Reuses the DPAPI session
and env credentials from telegram_client.py, then drives the raw ntgcalls
P2P-call flow against Telethon MTProto:

    create_p2p_call -> get_protocol -> messages.getDhConfig
    -> init_exchange (g_a_hash) -> phone.requestCall (rings)
    -> (update) PhoneCallAccepted -> exchange_keys -> phone.confirmCall
    -> connect_p2p (RTC) -> set_stream_sources (media) -> audio both ways

Usage:
    python spike_telegram_call.py <target> [--seconds N] [--timeout N]

  <target>   Telegram username (with or without @) / phone / id to call.
  --seconds  Auto-hangup after N seconds (default 30).
  --timeout  Give up ringing after N seconds (default 120).

Acceptance: answer on the phone -> hear a 440Hz tone; speak -> RX PCM logged;
connection reaches CONNECTED; clean hangup both sides.
"""

from __future__ import annotations

import argparse
import asyncio
import os
import random
import sys
import time
import traceback

ADJ = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, ADJ)

import numpy as np  # noqa: E402
from telethon import TelegramClient, events  # noqa: E402
from telethon.sessions import StringSession  # noqa: E402
from telethon.tl import functions as f, types as t  # noqa: E402

import ctypes  # noqa: E402
import json  # noqa: E402

import ntgcalls  # noqa: E402
from telegram_client import load_session  # noqa: E402

TONE_SR = 48000
TONE_PATH = os.path.join(os.environ.get(
    "TEMP", "C:\\Users\\wadia\\AppData\\Local\\Temp"), "opencode", "spike_tone.pcm")
CONFIG_PATH = os.path.join(
    os.environ.get("LOCALAPPDATA", os.path.expanduser("~")),
    "Ultron", "config.dat")


class _DATA_BLOB(ctypes.Structure):
    _fields_ = [("cbData", ctypes.c_ulong), ("pbData", ctypes.c_void_p)]


def dpapi_unprotect_raw(raw: bytes) -> bytes:
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


def _load_creds():
    """(api_id, api_hash, target) from env, else the app's config.dat."""
    api_id = os.environ.get("ULTRON_TELEGRAM_API_ID", "").strip()
    api_hash = os.environ.get("ULTRON_TELEGRAM_API_HASH", "").strip()
    target = os.environ.get("ULTRON_TELEGRAM_TARGET", "").strip() or "@HerA9el"
    if api_id and api_hash:
        return api_id, api_hash, target
    try:
        if sys.platform == "win32" and os.path.exists(CONFIG_PATH):
            with open(CONFIG_PATH, "rb") as f:
                data = json.loads(dpapi_unprotect_raw(f.read()))
            s = {k.lower(): v for k, v in data.items()}
            api_id = str(s.get("telegramapiid", "")).strip()
            api_hash = str(s.get("telegramapihash", "")).strip()
            target = str(s.get("telegramcalltarget", "")).strip() or target
            if api_id and api_hash:
                log("creds loaded from config.dat")
                return api_id, api_hash, target
    except Exception as e:
        log(f"config.dat fallback failed: {type(e).__name__}")
    return api_id, api_hash, target


def log(msg: str) -> None:
    print(f"[spike] {msg}", flush=True)


def make_tone(path: str, seconds: int = 30, freq: float = 440.0) -> None:
    if os.path.exists(path) and os.path.getsize(path) >= seconds * TONE_SR * 2:
        return
    os.makedirs(os.path.dirname(path), exist_ok=True)
    n = seconds * TONE_SR
    data = (0.3 * np.sin(2 * np.pi * freq * np.arange(n) / TONE_SR)
            * 32767).astype(np.int16)
    data.tofile(path)
    log(f"Wrote tone PCM ({seconds}s, {freq}Hz, {TONE_SR}Hz mono): {path}")


def ntg_protocol_to_telethon(proto) -> "t.PhoneCallProtocol":
    return t.PhoneCallProtocol(
        min_layer=proto.min_layer,
        max_layer=proto.max_layer,
        library_versions=list(proto.library_versions),
        udp_p2p=bool(proto.udp_p2p),
        udp_reflector=bool(proto.udp_reflector),
    )


def rtc_servers_from_phone_call(phone_call) -> list:
    servers = []
    for conn in phone_call.connections:
        if isinstance(conn, t.PhoneConnectionWebrtc):
            servers.append(ntgcalls.RTCServer(
                id=conn.id,
                ipv4=conn.ip,
                ipv6=conn.ipv6,
                port=conn.port,
                username=conn.username,
                password=conn.password,
                turn=bool(conn.turn or False),
                stun=bool(conn.stun or False),
                tcp=False,
                peer_tag=None,
            ))
        elif isinstance(conn, t.PhoneConnection):
            servers.append(ntgcalls.RTCServer(
                id=conn.id,
                ipv4=conn.ip,
                ipv6=conn.ipv6,
                port=conn.port,
                username=None,
                password=None,
                turn=False,
                stun=False,
                tcp=bool(conn.tcp or False),
                peer_tag=conn.peer_tag,
            ))
        else:
            log(f"  unknown connection type: {type(conn).__name__}")
    return servers


class SpikeRunner:
    def __init__(self, target: str, seconds: int, timeout: int):
        self.target = target
        self.seconds = seconds
        self.timeout = timeout
        self.wrtc: "ntgcalls.NTgCalls | None" = None
        self.call_id: int | None = None
        self.call_access_hash: int | None = None
        self.last_connection_id = 0
        self.accepted = asyncio.Event()
        self.accepted_pc = None
        self.discarded = asyncio.Event()
        self.done = None
        self.rx_bytes = 0
        self.rx_last_log = 0.0
        self.start_ts = 0.0

    # -- callbacks ----------------------------------------------------------

    def on_frames_cb(self, chat_id, mode, device, frames):
        device_name = getattr(device, "name", None) or str(device)
        mode_name = getattr(mode, "name", None) or str(mode)
        for fr in frames:
            data = getattr(fr, "data", None)
            if not data:
                continue
            if mode.name == "PLAYBACK":
                self.rx_bytes += len(data)
        now = time.monotonic()
        if now - self.rx_last_log >= 2.0:
            self.rx_last_log = now
            log(f"  on_frames mode={mode_name} device={device_name} "
                f"cumulative RX {self.rx_bytes/1024:.1f} KB")

    def on_conn_change_cb(self, chat_id, state):
        log(f"  on_connection_change -> {state} ({getattr(state, 'name', '')})")

    # -- telethon update handler --------------------------------------------

    async def on_update(self, update):
        if not isinstance(update, t.UpdatePhoneCall):
            return
        pc = update.phone_call
        if isinstance(pc, t.PhoneCallAccepted):
            self.call_id = pc.id
            self.call_access_hash = pc.access_hash
            self.accepted_pc = pc
            log(f"PhoneCallAccepted id={pc.id} g_b={len(pc.g_b)} bytes")
            self.accepted.set()
        elif isinstance(pc, t.PhoneCallDiscarded):
            log(f"PhoneCallDiscarded reason={pc.reason!r}")
            self.discarded.set()

    # -- steps --------------------------------------------------------------

    async def place_call(self, client: TelegramClient):
        input_user = await self._input_user(client)
        user_id = input_user.user_id
        log(f"calling peer user_id={user_id}")
        await self.wrtc.create_p2p_call(user_id)
        protocol = ntg_protocol_to_telethon(self.wrtc.get_protocol())
        log(f"protocol -> min={protocol.min_layer} max={protocol.max_layer} "
            f"versions={protocol.library_versions}")

        dh = await client(f.messages.GetDhConfigRequest(
            version=0, random_length=256))
        dh_config = ntgcalls.DhConfig(g=dh.g, p=bytes(dh.p), random=bytes(dh.random))
        g_a_hash = await self.wrtc.init_exchange(user_id, dh_config, None)
        log(f"init_exchange -> g_a_hash {len(g_a_hash)} bytes")

        random_id = random.randint(-2 ** 31, 2 ** 31 - 1)
        log(f"RequestCall -> ringing {self.target} ...")
        result = await client(f.phone.RequestCallRequest(
            user_id=input_user,
            g_a_hash=g_a_hash,
            protocol=protocol,
            video=False,
            random_id=random_id,
        ))
        waiting = result.phone_call
        if not isinstance(waiting, t.PhoneCallWaiting):
            raise RuntimeError(f"Unexpected RequestCall result: {type(waiting).__name__}")
        self.call_id = waiting.id
        self.call_access_hash = waiting.access_hash
        log(f"RequestCall returned PhoneCallWaiting id={waiting.id}")
        return user_id

    async def _input_user(self, client: TelegramClient) -> "t.InputUser":
        ent = await client.get_input_entity(self.target)
        if isinstance(ent, t.InputPeerUser):
            return t.InputUser(user_id=ent.user_id, access_hash=ent.access_hash)
        raise RuntimeError(f"target is not a User: {type(ent).__name__}")

    async def on_accepted(self, client: TelegramClient, user_id: int):
        log("Accepted! exchanging keys ...")
        auth = await self.wrtc.exchange_keys(user_id, bytes(self.accepted_pc.g_b), 0)
        g_a = bytes(auth.g_a_or_b)
        kw = getattr(auth, "key_fingerprint", None)
        log(f"exchange_keys -> g_a_or_b {len(g_a)} bytes, "
            f"key_fingerprint={kw}")

        protocol = ntg_protocol_to_telethon(self.wrtc.get_protocol())
        conn = await client(f.phone.ConfirmCallRequest(
            peer=t.InputPhoneCall(id=self.accepted_pc.id,
                                  access_hash=self.accepted_pc.access_hash),
            g_a=g_a,
            key_fingerprint=kw,
            protocol=protocol,
        ))
        phone_call = conn.phone_call
        if not isinstance(phone_call, t.PhoneCall):
            raise RuntimeError(f"ConfirmCall returned {type(phone_call).__name__}")
        self.call_id = phone_call.id
        self.call_access_hash = phone_call.access_hash
        servers = rtc_servers_from_phone_call(phone_call)
        conn_id = 0
        if phone_call.connections:
            conn_id = phone_call.connections[0].id
        self.last_connection_id = conn_id
        versions = list(phone_call.protocol.library_versions)
        p2p_allowed = bool(phone_call.p2p_allowed or False)
        log(f"ConfirmCall -> {len(servers)} RTC servers, "
            f"versions={versions}, p2p_allowed={p2p_allowed}")

        await self.wrtc.set_stream_sources(
            user_id,
            ntgcalls.StreamMode.CAPTURE,
            ntgcalls.MediaDescription(
                microphone=ntgcalls.AudioDescription(
                    media_source=ntgcalls.MediaSource.FILE,
                    input=TONE_PATH,
                    sample_rate=TONE_SR,
                    channel_count=1,
                ),
            ),
        )
        log("set_stream_sources (FILE tone) done")
        await self.wrtc.connect_p2p(user_id, servers, versions, p2p_allowed)
        log("connect_p2p OK - call is live")

    async def hangup(self, client: TelegramClient, user_id: int, cleared_ok: bool = False):
        try:
            await self.wrtc.stop(user_id)
        except Exception:
            pass
        if self.call_id and self.call_access_hash:
            try:
                duration = int(time.time() - self.start_ts)
                await client(f.phone.DiscardCallRequest(
                    peer=t.InputPhoneCall(id=self.call_id,
                                          access_hash=self.call_access_hash),
                    duration=duration,
                    reason=t.PhoneCallDiscardReasonHangup(),
                    connection_id=self.last_connection_id,
                    video=False,
                ))
                log(f"DiscardCall sent (duration={duration}s)")
            except Exception as e:
                log(f"DiscardCall failed: {type(e).__name__} {e}")

    async def run(self) -> int:
        api_id, api_hash, cfg_target = _load_creds()
        session = load_session()
        if not api_id or not api_hash:
            log("ERROR: Telegram api_id/api_hash unavailable")
            return 2
        if not session:
            log("ERROR: no saved Telegram session (telegram.session)")
            return 2
        if not self.target:
            self.target = cfg_target
        if not self.target:
            log("ERROR: no call target (give username/phone or set it in Ultron)")
            return 2

        client = TelegramClient(StringSession(session), int(api_id), api_hash)
        self.wrtc = ntgcalls.NTgCalls()
        client.add_event_handler(self.on_update, events.Raw)
        self.wrtc.on_frames(self.on_frames_cb)
        self.wrtc.on_connection_change(self.on_conn_change_cb)
        self.done = asyncio.get_running_loop().create_future()

        user_id = None
        await client.connect()
        if not await client.is_user_authorized():
            log("ERROR: session not authorized")
            await client.disconnect()
            return 3
        try:
            self.start_ts = time.time()
            user_id = await self.place_call(client)

            timeout_task = asyncio.create_task(asyncio.sleep(self.timeout))

            while True:
                if self.accepted.is_set():
                    await self.on_accepted(client, user_id)
                    break
                if self.discarded.is_set():
                    log("Peer declined/hung up before connect")
                    await self.hangup(client, user_id)
                    return 4
                if timeout_task.done():
                    log(f"TIMEOUT: not answered within {self.timeout}s")
                    await self.hangup(client, user_id)
                    return 5
                await asyncio.sleep(0.2)

            log(f"Call live. Tone playing for up to {self.seconds}s, "
                f"speak so I can log RX PCM. Hangup on phone cancels.")
            sleep = asyncio.create_task(asyncio.sleep(self.seconds))
            while not sleep.done() and not self.discarded.is_set():
                await asyncio.sleep(0.2)
            if self.discarded.is_set():
                log(f"Peer ended the call (RX {self.rx_bytes/1024:.1f} KB)")
            else:
                log(f"Auto-hangup after {self.seconds}s (RX {self.rx_bytes/1024:.1f} KB)")
            await self.hangup(client, user_id)
            return 0
        finally:
            try:
                await client.disconnect()
            except Exception:
                pass
            alive = self.wrtc.calls()
            if isinstance(alive, dict) and alive:
                log(f"wrtc.calls() left over: {alive}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Phase 3 private-call spike")
    parser.add_argument("--target", help="Telegram username/phone/id to call "
                                         "(default: Ultron settings target)")
    parser.add_argument("--seconds", type=int, default=30)
    parser.add_argument("--timeout", type=int, default=120)
    args = parser.parse_args()
    make_tone(TONE_PATH, 30)
    return asyncio.run(
        SpikeRunner(args.target or "", args.seconds, args.timeout).run())


if __name__ == "__main__":
    try:
        sys.exit(main())
    except ntgcalls.RTCException as e:
        log(f"RTCException: {e}")
        sys.exit(10)
    except Exception:
        traceback.print_exc()
        sys.exit(1)