# U L T R O N

**A Windows desktop AI assistant — spoken, seen, and in control of your PC.**

ULTRON is a WinUI 3 desktop app with a Python **Gemini Live** backend for
real-time voice. Speech in and speech out both go through the Gemini Live
session; there is no local speech-recognition or speech-synthesis stack. It
lives in your tray, listens for your voice, watches your screen and system,
runs your tools, and never stops quietly reconnecting if the world blinks.

---

## Core features

### Voice & audio
- **Real-time Gemini Live voice** — hands-free conversation with audio streaming, not text-in-a-box. Speech recognition and speech synthesis are both Gemini's.
- **Wake gate** — the model stays silent until you say "Hey Ultron" (or "Yo Ultron" / "Morning Ultron"). It is enforced by an instruction in the Gemini system prompt, not by matching audio on this machine.
- **Continuous conversation** — once you are in, you stay in. ULTRON no longer stops listening after one reply; it sleeps when you tell it to, when you close the mic, or after two minutes of silence.
- **Mic toggle** — the Wake Toggle button is a real on/off switch for microphone audio: painted red when audio is streaming, grey when it is shut. The UI state always mirrors the actual gate.
- **Local VAD** — Silero VAD gates mic audio so room noise and echo never reach Gemini. It is a signal gate only; it does no recognition.
- **"Ultron" voice DSP** — pitch / chorus / bass / darken processing of everything it says, tuned by voice command ("deeper", "more robotic").

### Control & automation (tool suite)
ULTRON exposes 30+ tools to the model so it can actually operate the machine:

| Area | Tools |
| --- | --- |
| System | `system_status` (CPU/RAM/GPU/disk), `computer_settings` (volume, brightness, Wi-Fi, BT, shutdown/restart/sleep/lock) |
| Screen | `screen_process` (screen/camera capture + describe), `set_live_vision` (ambient webcam context), `close_camera` |
| Desktop | `desktop_control` (mouse move/click/scroll), `window_manage` (focus/min/max/close/tab), `type_text`, `press_key` |
| Apps & web | `open_app`, `browser_control`, `web_search`, `youtube_video`, `weather_report` |
| Drive | `computer_use` — autonomous agent: looks at the screen, clicks/types until the task is done |
| Keyboard in code | `code_helper` (explain/review/debug/generate) |
| Files & docs | `file_processor` (list/read/write/search/rename/copy/delete, undoable), `create_document` (Word/Excel/PowerPoint) |
| Memory | `save_memory`, `recall_memory` — permanent local memory (`long_term.json`) |
| Tasks & proactivity | `task_inbox` (persistent to-do list), `reminder`, `manage_monitor` / `background_monitor` (silent topic watch), `guardian` (spoken CPU/mem/battery alerts) |
| Phone | `send_message` (SMS/WhatsApp/Telegram), `audio_devices_list`, `set_mic_device` |
| Misc | `undo` (reverse last actions), `shutdown_jarvis` (graceful exit), `set_voice` (live DSP control) |

### System traits
- **System tray companion** — minimize to tray, right-click menu, balloons, tooltips; tray icon uses a fixed GUID identity so it never ghosts after restarts.
- **Crash-recovery watchdog** — heartbeat + guardian process auto-relaunches the app (bounded retries) and keeps breadcrumbs for diagnosis.
- **Web deck** — optional LAN dashboard with QR code for remote control (default port 8123).
- **Secret hygiene** — API keys and tokens are redacted (`***REDACTED***`) before anything touches disk or logs.
- **One memory store** — facts live in a single JSON file; SQLite holds only the conversation transcript. A one-click purge erases both.

---

## In progress / partial

Honest status of the rougher edges:

| Feature | Status |
| --- | --- |
| **Personality & tone control** | Backend protocol and `GeminiBackend.SetPersona` plumbing are in (humor, reply length, suggestability, custom notes feed the system prompt). **No UI or voice commands yet** — defaults preserve the current detached tone. |
| **Phone messaging** (`send_message`) | Scaffolded for SMS/WhatsApp/Telegram; requires optional `adb` + `phone_addr:port` pairing to actually deliver. |
| **Wake gate** | Enforced by the Gemini system prompt, so it depends on cloud transcription and costs a reconnect when toggled. It is not a local acoustic detector. |
| **Local VAD model** | Silero VAD is downloaded on first run; if it is missing the backend falls back to "streaming raw mic" (noise-un-gated). |
| **Offline speech** | **None.** Speech needs a Gemini key and a network connection. There is no local fallback — with Gemini down you can only type. |
| **Web deck** | Functional but opt-in (`WebDashboardEnabled`) and still evolving. |
| **`computer_use`** | Working but deliberately step-capped; treat as experimental autonomy — confirm before mutating actions. |
| **Legacy `ZenApiKey`** | Kept only for migration; superseded by `GeminiApiKey`. |

---

## Prerequisites

- **Windows 10/11** + .NET 10 SDK.
- **Python 3.11–3.13** with backend dependencies: `google-genai`, `sounddevice`, `numpy`.
  ULTRON auto-locates `%LOCALAPPDATA%\Programs\Python\Python312\python.exe` (and 311/313); the CPython version is embedded in the output folder name.
- **Gemini Live API key** — paste in Settings, or set `GEMINI_API_KEY`.
- **Optional:** `adb` on `PATH` for phone tools (`phone_addr:port` pairing).

## Model files

ULTRON downloads the Silero-VAD model on first run to
`%LOCALAPPDATA%\Ultron\models`. It gates mic audio so noise and echo never
reach Gemini.

| File | Source |
| ---- | ------ |
| silero-vad.onnx | snakers4/silero-vad (GitHub) |

There are no local speech models. Speech recognition and synthesis are both
performed by Gemini Live, which requires a network connection.

At startup the app runs non-blocking first-run checks and surfaces any missing
prerequisite (Python, backend script, models, adb) in the chat window.

## Build & test

```powershell
dotnet restore
dotnet build --no-restore      # app (single-file WinExe, x64)
dotnet test tools/Tests/Ultron.Tests.csproj --no-restore
```

The test project compiles the real service sources (`MemoryStore`, `LongTermMemory`,
`AppLog`, `GeminiBackend`, `UltronOptions`) and runs unit tests plus an integration
test that spawns the Python backend and verifies the `waiting → start → error →
exit` protocol with an invalid API key (auto-skips when Python/deps are absent).

Roslyn analyzers run in build (see `Directory.Build.props`); the CI workflow
(`.github/workflows/ci.yml`) restores, tests, and builds on `windows-latest`.

## Layout

- `MainWindow.xaml.cs` — composition root + UI; wires all services.
- `Services/` — brain, memory (`LongTermMemory` for facts, `MemoryStore` for transcripts), backend IPC, volume, adb, logging.
- `backend/gemini_backend.py` — Gemini Live: audio streaming, tool dispatch, IPC.
- `tools/Tests/` — unit + backend-protocol integration tests (xunit).
- `tools/Smoke/` — console smoke harness for the same service sources.

## Privacy

- **Memory Logging off means off.** While it is disabled, ULTRON writes no
  conversation transcript, stores no facts, and injects no memory into the model
  prompt. The gate covers the model's `save_memory` / `recall_memory` tools and
  the Memory panel's manual edits, so the setting cannot be bypassed from the UI.
  Switching it off does not erase what was already stored; use the purge button
  for that.
- Long-term facts live in `%LOCALAPPDATA%\Ultron\long_term.json` as plain text
  grouped by category, `{"category": {"key": {"value", "updated"}}}`. The
  conversation transcript lives in `%LOCALAPPDATA%\Ultron\memory.db`. A one-click
  purge erases both.
- API keys and long tokens are redacted (`***REDACTED***`) before any text is
  stored. Set *Redact secrets before store* in Settings (default on).
- Audio always streams to Gemini Live while the microphone is open. The local VAD
  is a signal gate only; it does no recognition and never leaves the machine.