# U L T R O N

**A Windows desktop AI assistant — spoken, seen, and in control of your PC.**

ULTRON is a WinUI 3 desktop app with a Python **Gemini Live** backend for
real-time voice, plus local fallback STT/VAD/TTS when no API key is set. It
lives in your tray, listens for your voice, watches your screen and system,
runs your tools, and never stops quietly reconnecting if the world blinks.

---

## Core features

### Voice & audio
- **Real-time Gemini Live voice** — hands-free conversation with audio streaming, not text-in-a-box.
- **Wake word** ("Hey Ultron" / "Yo Ultron" / configurable phrases) with auto-sleep when idle.
- **Push-to-talk**, global mute, and wake via hotkeys — configurable, conflict-checked at startup.
- **Local fallback pipeline** — Whisper STT + Silero VAD (downloaded on first run, no cloud needed); voice replies are always Gemini Live voices.
- **Voice-ID owner enrollment** — optional owner-gated access.
- **"Ultron" voice DSP** — pitch / chorus / bass / darken processing of everything it says, tuned by voice command ("deeper", "more robotic").

### Control & automation (tool suite)
ULTRON exposes 30+ tools to the model so it can actually operate the machine:

| Area | Tools |
| --- | --- |
| System | `system_status` (CPU/RAM/GPU/disk), `computer_settings` (volume, brightness, Wi-Fi, BT, shutdown/restart/sleep/lock) |
| Screen | `screen_process` (screen/camera capture + describe), `set_live_vision` (ambient webcam context), `close_camera` |
| Desktop | `desktop_control` (mouse move/click/scroll), `window_manage` (focus/min/max/close/tab), `type_text`, `press_key` |
| Apps & web | `open_app`, `browser_control`, `web_search`, `youtube_video`, `weather_report`, `flight_finder` |
| Drive | `computer_use` — autonomous agent: looks at the screen, clicks/types until the task is done |
| Keyboard in code | `code_helper` (explain/review/debug/generate), `dev_agent` (scaffold + run + fix a mini project) |
| Files & docs | `file_processor` (list/read/write/search/rename/copy/delete, undoable), `create_document` (Word/Excel/PowerPoint) |
| Memory | `save_memory`, `recall_memory` — permanent local memory (SQLite) |
| Tasks & proactivity | `task_inbox` (persistent to-do list), `reminder`, `manage_monitor` / `background_monitor` (silent topic watch), `guardian` (spoken CPU/mem/battery alerts) |
| Phone | `send_message` (SMS/WhatsApp/Telegram), `audio_devices_list`, `set_mic_device` |
| Misc | `undo` (reverse last actions), `game_updater` (scan installed games), `shutdown_jarvis` (graceful exit), `set_voice` (live DSP control) |

### System traits
- **System tray companion** — minimize to tray, right-click menu, balloons, tooltips; tray icon uses a fixed GUID identity so it never ghosts after restarts.
- **Global hotkeys** — PTT / mute / wake, validated against Windows conflicts on every launch.
- **Crash-recovery watchdog** — heartbeat + guardian process auto-relaunches the app (bounded retries) and keeps breadcrumbs for diagnosis.
- **Web deck** — optional LAN dashboard with QR code for remote control (default port 8123).
- **Secret hygiene** — API keys and tokens are redacted (`***REDACTED***`) before anything touches disk or logs.
- **Persistent memory** — opt-in conversation log + facts in `%LOCALAPPDATA%\Ultron\memory.db`, with a one-click purge.

---

## In progress / partial

Honest status of the rougher edges:

| Feature | Status |
| --- | --- |
| **Personality & tone control** | Backend protocol and `GeminiBackend.SetPersona` plumbing are in (humor, reply length, suggestability, custom notes feed the system prompt). **No UI or voice commands yet** — defaults preserve the current detached tone. |
| **Phone messaging** (`send_message`) | Scaffolded for SMS/WhatsApp/Telegram; requires optional `adb` + `phone_addr:port` pairing to actually deliver. |
| **Local STT/VAD models** | Auto-downloaded on first run; if the VAD model is missing the backend falls back to "streaming raw mic" (noise-un-gated). |
| **Voice-ID enrollment** | Enrollment and owner gating exist in Settings; treated as experimental hardening, not a guarantee. |
| **Web deck** | Functional but opt-in (`WebDashboardEnabled`) and still evolving. |
| **`computer_use` / `dev_agent`** | Working but deliberately step-capped; treat as experimental autonomy — confirm before mutating actions. |
| **Legacy `ZenApiKey`** | Kept only for migration; superseded by `GeminiApiKey`. |

---

## Prerequisites

- **Windows 10/11** + .NET 10 SDK.
- **Python 3.11–3.13** with backend dependencies: `google-genai`, `sounddevice`, `numpy`.
  ULTRON auto-locates `%LOCALAPPDATA%\Programs\Python\Python312\python.exe` (and 311/313); the CPython version is embedded in the output folder name.
- **Gemini Live API key** — paste in Settings, or set `GEMINI_API_KEY`.
- **Optional:** `adb` on `PATH` for phone tools (`phone_addr:port` pairing).

## Model files (local mode only)

When no Gemini key is set, ULTRON downloads the Whisper (STT) and Silero-VAD
models on first run to `%LOCALAPPDATA%\Ultron\models`. The VAD model is also
downloaded in Gemini mode — it gates mic audio so noise and echo never reach
Gemini. Voice **output** is always Gemini Live (no local TTS).

| File | Source |
| ---- | ------ |
| whisper-encoder/decoder/tokenizer | onnx-community/whisper-tiny.en (HuggingFace) |
| silero-vad.onnx | snakers4/silero-vad (GitHub) |

At startup the app runs non-blocking first-run checks and surfaces any missing
prerequisite (Python, backend script, models, adb) in the chat window.

## Build & test

```powershell
dotnet restore
dotnet build --no-restore      # app (single-file WinExe, x64)
dotnet test tools/Tests/Ultron.Tests.csproj --no-restore
```

The test project compiles the real service sources (`MemoryStore`, `AppLog`,
`GeminiBackend`, `UltronOptions`) and runs unit tests plus an integration
test that spawns the Python backend and verifies the `waiting → start → error →
exit` protocol with an invalid API key (auto-skips when Python/deps are absent).

Roslyn analyzers run in build (see `Directory.Build.props`); the CI workflow
(`.github/workflows/ci.yml`) restores, tests, and builds on `windows-latest`.

## Layout

- `MainWindow.xaml.cs` — composition root + UI; wires all services.
- `Services/` — brain, memory (SQLite), backend IPC, STT/VAD/TTS, volume, adb, logging.
- `backend/gemini_backend.py` — Gemini Live: audio streaming, tool dispatch, IPC.
- `tools/Tests/` — unit + backend-protocol integration tests (xunit).
- `tools/Smoke/` — console smoke harness for the same service sources.

## Privacy

- Conversations are written to `%LOCALAPPDATA%\Ultron\memory.db` only while
  *Memory Logging* is enabled (Settings). A one-click purge button erases all
  stored conversations and facts.
- API keys and long tokens are redacted (`***REDACTED***`) before any text is
  stored. Set *Redact secrets before store* in Settings (default on).
- Local-mode speech (STT/VAD/TTS) runs fully offline; voice/session data to
  Gemini Live goes only when *you* talk to it.