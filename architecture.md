# ULTRON Voice Assistant (Windows Desktop) — System Architecture

This document provides a complete walkthrough of the ULTRON voice assistant system architecture as of September 2026.

---

## 1. ULTRON Voice Assistant (Windows Desktop)

**Core stack**: C# WinUI 3 (app shell) + Python 3.12 backend (Gemini Live API) — two processes connected over stdin/stdout IPC.

### 1.1 C# App Layer (`MainWindow.xaml.cs`, `GeminiBackend.cs`)
- **UI**: WinUI 3 desktop window. Status bar, chat transcript, mic VU bar, DSP indicator. Key UI state machine (`AssistantStateMachine`) with states: `Idle → Listening → Speaking → ToolRunning`.
- **IPC host**: Spawns `python.exe gemini_backend.py`, reads/writes JSON lines over stdin/stdout. Handles reconnect with exponential backoff.
- **Event routing**:
  - `TranscriptReceived` → display user/assistant text in chat.
  - `ToolCallReceived` → route to C# local tools (desktop control, press_key, window_manage, undo stack) OR forward to backend's tool_call for server-handled tools (create_document, task_inbox, guardian, computer_use, set_live_vision).
  - `ComputerActRequested` → execute mouse/keyboard actions (`ExecuteComputerAct`), reply with `SendComputerActResultAsync`.
  - `StatusChanged` → update status bar + mic state (asleep/awake/speaking).
- **Local tools** (C# side, fast Win32 API calls):
  - `press_key` / `type_text` → SendInputBatch (synthetic keyboard input).
  - `window_manage` → Native.FindWindowByTitle / ListWindows + focus/minimize/maximize/close/Alt+Tab/ShowDesktop.
  - `desktop_control` → Media VK codes, volume, mute, brightness, settings shortcuts.
  - `open_app` → Process.Start via native.
  - `undo` → UndoLedger: LIFO stack of (description, undo_func) pairs.
  - `web_search`, `weather_report`, `browser_control`, `file_processor`, `code_helper`, `send_message`, `youtube_video`, `reminder` → delegate to backend via IPC (Python does the work, result flows back).

### 1.2 Python Backend (`gemini_backend.py`)
- **Gemini Live session**: Google genai API, model `gemini-2.5-flash`. Session configured with ~20 tool declarations (server-side + client-side tools merged).
- **Audio pipeline**:
  - `_mic_capture_loop`: VAD-gated mic capture via sounddevice, sent as PCM16 to Gemini Live input.
  - `_audio_play_loop`: Gemini PCM output decoded and played via sounddevice Output stream. DSP layer (`VoiceDsp` from `voice_dsp.py`) applies EQ/flanger/shaping when enabled (currently OFF per user request).
  - `_send_audio_loop`: bridges mic queue to Gemini Live `send_realtime`.
- **Tool dispatch** (inside the Live session loop): Python-side tools are handled directly:
  - `save_memory` / `recall_memory` → `MemoryManager` (JSON store, categories: people/projects/topics/preferences/apps/system).
  - `background_monitor` → `monitor_store` (due topics, intervals, last-checked).
  - `computer_use` → autonomous desktop agent (`computer_use.py`): screenshots screen, asks Gemini to decide next action, calls `_computer_act` (IPC round-trip to C# for actual mouse/keyboard), repeat up to N steps.
  - `set_live_vision` → toggle ambient webcam capture; on `turn_complete`, capture frame and inject as user turn ("Here's what the user sees...").
  - `create_document` → `productivity.py` → python-docx / openpyxl / python-pptx → saves to `~/Documents/Ultron/`.
  - `task_inbox` → `productivity.py` → JSON-backed task list in `%LOCALAPPDATA%\Ultron\tasks.json`. Actions: add/list/complete/clear.
  - `guardian` → `guardian.py` → background watchdog (CPU/memory/battery via psutil, raises alerts when thresholds breached; also reads `task_inbox` for overdue items). Fires audible alerts via `_speak_unsolicited`.
- **IPC bridge**: `_ipc_read_loop` reads JSON lines from C#, dispatches:
  - `set_voice` → update voice model.
  - `voice_dsp` → update DSP enable/effect params.
  - `set_live_vision` → pass-through toggle.
  - `tool_result` → resolve `_pending_tools` future (for tools delegated from C#).
  - `computer_act_result` → resolve `_computer_act_responses` future.

### 1.3 Key Files
| File | Role |
|------|------|
| `MainWindow.xaml.cs` | WinUI UI, C# tool dispatch, keyboard/mouse helpers |
| `Services/GeminiBackend.cs` | C# ↔ Python IPC, event bridge, Win32 native calls |
| `Services/AppSettings.cs` | Persisted settings (DSP on/off, voice model, mic device, key bindings) |
| `backend/gemini_backend.py` | Gemini Live session, audio capture/play, tool dispatch, IPC bridge |
| `backend/voice_dsp.py` | Real-time audio DSP (EQ, flanger, reverb) — vectorized for performance |
| `backend/computer_use.py` | Autonomous desktop agent engine (screenshot → AI decision → action loop) |
| `backend/guardian.py` | System health watchdog (CPU/memory/battery), config persistence |
| `backend/productivity.py` | Document builder (Word/Excel/PPTX) + persistent task inbox |
| `backend/monitor.py` | Background topic scheduler (due timestamps, intervals) |
| `backend/memory_store.json` | Persistent memory (user preferences, facts, people) |

### 1.4 Data Flow Summary
```
User speaks → Mic (VAD gate) → Python audio_queue → Gemini Live API
Gemini Live → function_call (tool) → Python dispatch → result → send_tool_response
Gemini Live → audio chunk → Python audio_play → Speaker
User types in chat → C# → IPC to Python → Gemini text input (non-voice)
C# local tools (press_key, window_manage) → handled directly in C# → result to Gemini
Computer-use actions → backend screenshots → asks Gemini → IPC to C# for mouse/keyboard → result back
Guardian watchdog → psutil stats → if breach → _speak_unsolicited → Gemini says alert aloud
Task inbox overdue → guardian loop → same _speak_unsolicited path
```

---

## 2. Build & Deploy Status
| Component | Status |
|-----------|--------|
| ULTRON C# shell | ✅ WinUI 3, build clean (0 errors) |
| Python backend | ✅ All modules: gemini_backend, voice_dsp, computer_use, guardian, productivity, monitor, tools_extra |
| Voice DSP | ✅ Vectorized, EQ-in-vocoder, no per-sample Python loops. Currently OFF (user preference) |
| Mic robustness | ✅ Fallback to default device on bad device switch (no more PortAudioError crash) |
| Computer-use | ✅ Autonomous engine wired, C# execute + IPC reply |
| Guardian | ✅ CPU/memory/battery + task inbox overdue alerts |
| Productivity | ✅ Word/Excel/PPTX generation + persistent task inbox |
| Live vision | ✅ Ambient webcam capture per user exchange |
| Full keyboard/media | ✅ Media keys + window manage actions |

---

## 3. Next Steps
1. **Test all ULTRON features by voice** (user plans to do when noise is low).
2. **Fix remaining correctness issues** (call path, approval gates, IPC hardening).
3. **Add Python CI** (compileall, pyflakes, requirements.txt).
5. **Hardening** (approval gates, phone tool lockdown, DashboardServer localhost-only).