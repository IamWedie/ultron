# Spec: ULTRON Telegram Real-Time Voice Calling

## Objective

Ultron (a Windows 11 AI desktop assistant with Gemini Live real-time voice) will place real
Telegram voice calls to the owner's phone via a dedicated Telegram user account. The owner
answers and has a live, interruptible conversation with Ultron — same Gemini voice, over the
phone, with zero additional TTS/STT.

This spec does NOT apply to: Telegram Bot API messaging, voice messages, or simulated calls.

## Capability Map (Phase 0 — scope check)

| Module id | Responsibility | Depends on | Status |
|---|---|---|---|
| `tg-auth` | User-account login, encrypted session persistence (DPAPI), auto-reconnect | — | ✅ Done |
| `tg-target` | Resolve/call target (@user/phone → validated User), persist target | `tg-auth` | ✅ Done |
| `tg-voip` | Outgoing call signaling (requestCall/accept/discard), state machine, ring UI | `tg-auth`, `tg-target` | ✅ Done |
| `tg-audio` | Telegram WebRTC media bridge: PCM streaming, 48 k ↔ 16 k / 24 k resampling | `tg-voip` | 🔄 In progress (C8.6) |
| `tg-gemini` | Gemini Live ↔ Telegram audio bridge: mic bypass, streaming, barge-in | `tg-audio` + existing `gemini_backend.py` | ⏳ Pending |
| `tg-tools` | In-call tool access, conversation context, event-triggered auto-calls | `tg-gemini` + existing tool framework | ⏳ Pending |
| `tg-ui` | Settings panel, status, test-call button, configuration UX | `tg-auth`, `tg-target`, `tg-voip` | ✅ Done (C8.4) |

**Build order:** `tg-auth` → `tg-target` → `tg-voip` (spike/validate) → `tg-audio` →
`tg-gemini` → `tg-tools` + `tg-ui` (parallel).

Each module ships green before the next starts. Every module spec is independent and testable.

**Current phase:** C8.6 (InCallAudioBridge) — resampling adapters, mic bypass, barge-in/queue drain.

Each module ships green before the next starts. Every module spec is independent and testable.

## Tech Stack

- **Ultron core:** .NET 10 / WinUI 3 (`net10.0-windows10.0.19041.0`, win-x64, C#)
- **Python backend:** CPython 3.12, spawned as child process by `Services\GeminiBackend.cs`;
  IPC is JSON-lines over stdin/stdout (existing, unchanged).
- **Telegram auth + signaling:** Telethon (pure Python MTProto, already wired).
- **Telegram media engine:** `py-tgcalls` (PyTgCalls v2.3.x) wrapping NTgCalls/tgcallsjs;
  prebuilt Windows wheels; Telethon bridge available. Audio: PCM int16 48 kHz mono.
  LGPL-3.0 license.
- **AI voice engine:** Existing Gemini Live session (`gemini_backend.py`,
  `GeminiSession.start()`, config `response_modalities=["AUDIO"]`). Input: int16 16 kHz mono.
  Output: int16 24 kHz mono. No new TTS/STT.
- **Resampling:** thin numpy adapter at module boundary (48 k ↔ 16 k / 24 k).
- **Audio capture/playback:** NAudio (`AudioCapture.cs`, existing); outbound to Telegram
  via py-tgcalls media stream; inbound from Telegram to Gemini `_mic_queue`.
- **Securities:** DPAPI-encrypted session + config (`config.dat`, `telegram.session`);
  no secrets in source or logs; `Secrets.Redact` applied; `TelegramCallEnabled` off by default.

## Commands

- **Build (C#):** `dotnet build -c Debug` from `ultron_winui/`
- **C# tests:** `dotnet test tools/Tests/Ultron.Tests.csproj --nologo`
- **Python selftests:** `python backend/telegram_client.py` (from backend dir or absolute path)
- **Backend:** started automatically by `GeminiBackend.cs` (`StartAsync`)
- **Env overrides:** `ULTRON_TELEGRAM_API_ID`, `ULTRON_TELEGRAM_API_HASH`, `ULTRON_TELEGRAM_PHONE`, `ULTRON_TELEGRAM_ENABLED`

## Project Structure

```
ultron_winui/
├── backend/                 # Python backend
│   ├── gemini_backend.py    # main IPC loop, Gemini Live session, tool dispatch
│   ├── telegram_client.py   # Telegram auth + (future) call state machine
│   └── voice_dsp.py         # output DSP (phase vocoder, EQ)
├── Services/                # C# services
│   ├── GeminiBackend.cs     # child process manager, IPC, event model
│   ├── AppSettings.cs       # DPAPI-encrypted config.dat
│   └── ...                  # AudioCapture, SileroVad, WhisperStt, etc.
├── MainWindow.xaml[.cs]     # WinUI shell, settings, status, transcript
├── tools/Tests/             # xUnit tests (55 passing)
└── tasks/                   # plan + todo (this spec's implementation tracker)
```

## Code Style

- C#: nullable enabled, WinUI conventions, xUnit theories, no WinUI in test project.
- Python: type-hinted, `from __future__ import annotations`, `async` native, self-tests runnable standalone.
- IPC: `{type: "..."}` JSON objects; secrets never passed through events or logs.

## Testing Strategy

- **C#:** xUnit (Microsoft.NET.Test.Sdk, xunit 2.9.3); SUT compiled directly without WinUI;
  55 passing; `BackendIntegrationTests` spawns real Python backend.
- **Python:** standalone selftests (`python telegram_client.py`); 9 passing (dpapi, redaction,
  session, state-machine, submit-code guard).
- **End-to-end:** manual — "Call Me" button → phone rings → conversation.

## Boundaries

- **Always:** run tests before commits; DPAPI-only secret storage; redact all logs; TelegramCallEnabled off by default.
- **Ask first:** new Python package dependency (py-tgcalls); new C# NuGet package; UI layout changes; `config.dat` schema changes.
- **Never:** commit secrets/keys; hard-code call target; enable auto-calls by default; use Bot API for calls; bypass DPAPI for sessions.

## Open Questions for User

1. `api_id` / `api_hash` — create at my.telegram.org; provide when ready for live login.
2. Call target — your Telegram username or phone; set in settings; confirm when ready.
3. Auto-calls: keep OFF by default as spec says — confirm.
