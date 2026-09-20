# ULTRON — Telegram Real-Time Voice Call (Implementation Plan)

Scope: a dedicated ULTRON Telegram **user** account places a real Telegram voice call to the
user's phone. The phone rings, the user answers and speaks naturally, and ULTRON replies with
the **same native Gemini Live voice** already used in the app. No TTS/STT/voice-message layer.

Data flow: `[phone mic] -> Telegram VoIP (rTCP/WebRTC) -> ULTRON Windows -> PCM -> Gemini Live
(native voice) -> PCM -> Telegram VoIP -> [phone speaker]`.

- Voice AI engine stays **only** Gemini Live (`backend/gemini_backend.py`).
- Gemini input PCM: int16 16 kHz mono (`_mic_queue`). Gemini output PCM: int16 24 kHz mono
  (`_audio_out_queue`). Telegram VoIP PCM: int16 48 kHz mono. Resampling is a thin adapter
  (numpy linear), no new audio stack.

## Research verdict (cited)

- Telegram calls are secured by E2EE voice calls. Telegram clients 7.0+ use WebRTC media
  (`tgcalls`); `libtgvoip` is the deprecated pre-7.0 path: core.telegram.org/api/end-to-end/calls.
- Official MTProto call control: `phone.requestCall`, `phone.acceptCall`, `phone.confirmCall`,
  `phone.discardCall` — docs.core.telegram.org/method/phone.requestCall (and `_accepted_call`/
  `_receivedCall`). These are **user-hash** API calls; **bots cannot place calls**, hence the
  dedicated user account.
- TDLib (official) exposes call state objects (`call`, `callProtocol` with WebRTC
  `library_versions`, `createCall`/`acceptCall`/`callState*`:
  core.telegram.org/tdlib/docs/) but still hands media transport to the bundled `tgcalls`
  engine and has no maintained release Python binding on Windows with call media.
- Media engine candidates for Windows+Python:
  - **PyTgCalls (`py-tgcalls`, v2.3.x — maintained fork, prebuilt Windows wheels, Telethon
    bridge, LGPL-3.0)** — private user-to-user outgoing/incoming calls; wraps NTgCalls
    (a build of Telegram's official WebRTC `tgcalls` stack, via tgcallsjs/pyservercall).
    PyPI: pypi.org/project/py-tgcalls.
  - MarshalX/tgcalls (PyPI `tgcalls`): official tgcalls engine binding; private-call PCM
    support lives on its dev branch, not the release wheel.
  - `pytgcalls`/NTgCalls original: group voice-chat service; private calls not in release.
- Conclusion: **Auth + signaling + state machine = Telethon** (pure-Python, official MTProto,
  installable on Windows, exposes `phone.*` raw calls — already implemented in `tg-auth`);
  **media = PyTgCalls (`py-tgcalls`) over the official tgcalls/WebRTC stack**, which must still
  be proven end-to-end in the Phase 3 spike before committing later phases.

## Chosen architecture

- **Transport plugin**: new `backend/telegram_client.py` wraps Telethon. Owns auth, encrypted
  session, contact resolution, call signaling, and a `TelegramCall` state machine.
- **IPC bridge** reuses the existing line-protocol: C# -> backend `telegram_*` commands;
  backend -> C# `telegram_*` events. Reuses `AppLog` redaction + `config.dat` DPAPI for secrets.
- **Delivery risk toggles**: `TelegramCallEnabled` (off by default — existing behavior untouched),
  plus `ULTRON_TELEGRAM_API_ID` / `ULTRON_TELEGRAM_API_HASH` / `ULTRON_TELEGRAM_PHONE` env overrides
  (same pattern as `ULTRON_TELEGRAM_TOKEN`).
- **Call audio routing**: when a call is active, an `InCallAudioBridge` injects resampled call PCM
  into `_mic_queue` (the mic source is bypassed) and re-queues Gemini 24 kHz responses into the
  call. Not in call => zero effect. (Phase 4+.)
- **Secure session**: Telethon `.session` bytes encrypted with Windows DPAPI (CryptProtectData via
  ctypes — matches the `config.dat` pattern, no pywin32 needed).

## Phases (each lands, tests green, relinkable)

- **T1 — Initial auth foundation (previous proactive task, done).** [not this plan]

### Phase 1 — Account login (DONE — plumbing; live login needs your creds)
- [x] **C1: Auth client.** `backend/telegram_client.py` — Telethon client, DPAPI-encrypted session
  in `%LOCALAPPDATA%\Ultron\telegram.session`, silent auto-reconnect, optional phone/QR.
  No call/media code. Never logs api_hash/session/code.
- [x] **C2: IPC.** backend handlers: `telegram_login` (flags), `telegram_code`, `telegram_logout`,
  `telegram_status`; events: `telegram_status {phase, message, available}`,
  `telegram_code_required`, `telegram_code_result`. Adds `telegram` to hello capabilities
  (not `call` — media isn't implemented yet, capability stays truthful).
- [x] **C3: C# pipeline.** `AppSettings` (+dpapi/env), `GeminiBackend` command/event methods,
  event model — no UI yet.
- [x] **C4: Tests.** python unit (dpapi stub, redaction, state machine) + C# parse tests. Build
  clean, all tests green (55/55).
- [ ] **C4b (you):** supply api_id/api_hash (my.telegram.org, like the Gemini key) and complete
  the live login so "CONNECTED" persists across backend restart.
- **Acceptance:** account "CONNECTED" survives backend restart; no plaintext session on disk.

### Phase 2 — Target resolution (C5)
- [ ] **C5:** Resolve call target (`@username`/phone -> `User`), validate `access_hash` + `can_call`,
  persist target id. Acceptance: target resolved, callable user, redacted logs.

### Phase 3 — Technical spike: media engine (C6) [risk-gate]
- [x] **C6: Prove PyTgCalls on Windows.** Spike (raw ntgcalls 2.2.5 + Telethon, no app changes):
  create_p2p_call -> getDhConfig -> init_exchange -> phone.requestCall (rings) ->
  PhoneCallAccepted -> exchange_keys -> ConfirmCall -> connect_p2p -> FILE tone out /
  PCM-in via on_frames. **PASSED LIVE 2026-09-20**: self-account called `@HerA9el`,
  user answered, heard 440Hz tone; RTCServer[0].id is NOT a Python attr (native object) —
  connection id comes from `phone_call.connections[0].id` (Telethon TL int).** REDACTED_SECRETS_STRIP
  to the existing Telethon client, and place a real **PCM test call** to the phone (ring →
  answer → streaming PCM I/O both ways). Validate call state notifications + media streaming.
- **Decision gate:** if media engine can't run on Windows/Python with live PCM I/O, **stop and
  revisit** (highest-risk item — user directive: prefer official/TDLib cleanly; do NOT invent a
  protocol). Do not burn later phases on a dead media path.

### Phase 4..N — after spike (only if gate passes)
- [x] **C7:** Outgoing call state machine via `phone.requestCall`/`accept`/`discard` + updates.
      - [x] IPC: `telegram_call_start`/`telegram_call_stop`/`telegram_call_status` → `TelegramController.call_start/stop/status`
      - [x] Events: `telegram_call_state` (ringing/connecting/connected/ended/error), `telegram_call_result`
      - [x] C# events: `TelegramCallStateChanged(state, message)`, `TelegramCallResult(ok, message)`
      - [x] C# send: `SendTelegramCallStartAsync/StopAsync/StatusAsync`
      - [x] Full flow: create_p2p_call → getDhConfig → init_exchange → RequestCall (rings) → PhoneCallAccepted → exchange_keys → ConfirmCall → connect_p2p (RTC) → FILE tone out / PCM-in via on_frames → DiscardCall
      - [x] Gate: `TelegramCallEnabled` (off by default), requires resolved target + valid session
- [ ] **C8:** InCallAudioBridge (48k<->16k/24k resample adapters) + mic bypass + barge-in/queue
  drain on remote speech.
- [ ] **C9:** Ring/accept/dial UI in MainWindow (status + "Test Call" + disconnect), tools stay live
  in-call, secure auth UX, logging.
- [ ] **C10:** Edge cases: hang-up, disconnect, concurrent calls, reconnect, timeouts, tests.

## Open questions for user
1. api_id/api_hash — create at my.telegram.org (like the Gemini key) and set via env override
   (no code secret). Provide when ready.
2. Auth UX preference: QR-scan login (recommended, no phone-code to relay) vs phone-number code?