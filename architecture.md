# UltronEvolved — Full System Architecture Walkthrough

This document provides a complete walkthrough of the UltronEvolved system architecture as of September 2026, covering both the ULTRON voice assistant (Windows desktop) and the primavera Canva-like design app (Linux/GTK4 + Wayland).

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

## 2. Primavera — Canva-like Design App (Linux, GTK4 + Wayland)

### 2.1 Target Environment
- **OS**: Debian/Ubuntu on bare metal
- **Compositor**: Wayfire (wlroots-based, Compiz-like 3D effects, XWayland support)
- **Shell**: wf-shell (GNOME-like panel + taskbar) or GNOME Shell over XWayland for settings
- **GTK**: 4.0 (Wayland-native, client-side decorations)
- **XWayland**: for legacy apps (gnome-control-center, etc.); Wayfire spawns Xwayland automatically and exposes it as XServer1

### 2.2 Wayland Integration
- **Xwayland-shell-v1 protocol**: allows Xwayland server to associate X11 windows with wl_surface — handles seamless XWayland↔Wayland window management.
- **Client shell** (Wayfire config): `wf-shell` provides panel, taskbar, app launcher; custom CSS theme for round corners and dark mode. Layer effects for blur/shadows.
- **XWayland gateway** (alpha/primavera config): Wayfire's XWayland instance with XServer1 display `:0`, rootful for X11 apps that need it (e.g. GIMP if needed). Environment: `DISPLAY=:0` for XWayland apps.
- **GNOME Settings via XWayland**: `export DISPLAY=:0 && gnome-control-center` runs the full GNOME control center in an XWayland window — settings accessible through Wayfire panel.

### 2.3 App Architecture (GTK4 + pygobject)
```
primavera/
├── main.py                  # GtkApplication entry point
├── app_window.py            # GtkApplicationWindow — canvas + sidebar
├── canvas.py                # GtkDrawingArea + GskRenderNode tree
├── tool_palette.py          # Sidebar: shapes, text, images, brushes
├── layers_panel.py          # Layer list, reorder, lock, visibility
├── property_inspector.py    # Properties panel for selected element
├── file_operations.py       # Open/save/export (PDF, PNG, SVG)
├── history.py               # Undo/redo stack
├── style.css                # GTK4 CSS for dark theme + round corners
└── wayland/
    ├── __init__.py
    ├── client_shell.py      # wl_surface + xdg_surface integration
    └── xwayland_gateway.py  # Xwayland-shell-v1 protocol bridge
```

**Key design decisions**:
- Canvas uses GTK4's `Gtk.DrawingArea` with `snapshot.render_layout()` for vector rendering.
- Each element (shape, text, image) is a dataclass with bounding box, rotation, style props — serialized to/from JSON project files.
- Wayland client shell integration via `Gtk4WaylandClient` — the app is a native Wayland client, no XWayland needed for the design app itself (pure Wayland). XWayland is only for auxiliary GNOME settings.
- Dark theme via CSS override directory + `prefers-color-scheme: dark`.

### 2.4 Wayfire Configuration for Primavera
```ini
# ~/.config/wayfire/wayfire.ini
[core]
plugins = wf-shell pixdecor
xwayland = true
xwayland_mode = rootless

[desktop]
blur = true
blur_radius = 5
corner_radius = 12

[autostart]
# Start GNOME settings via XWayland for system prefs
gnome-settings = sh -c "sleep 2 && dbus-update-activation-environment WAYLAND_DISPLAY DISPLAY && gnome-settings-daemon"
panel = wf-panel
launcher = wlr-taskbar
```

---

## 3. Cross-System Integration (Windows ↔ Linux)

The ULTRON voice assistant (Windows) can interact with the primavera design app (Linux) via network:
- `waypipe` allows remote Wayland app execution over SSH.
- `SSH tunnel` for file transfer and command execution.
- ULTRON's `computer_use` agent can SSH into the Linux box and drive Wayfire via `wf-message` IPC or direct keyboard simulation.

---

## 4. Build & Deploy Status
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
| Primavera app | 🔨 Scaffolded, to be built on Linux machine |
| Wayland config | 🔨 Documented, ready to deploy on Debian box |

---

## 5. Next Steps
1. **Test all ULTRON features by voice** (user plans to do when noise is low).
2. **Deploy Wayfire config** to Debian box and verify XWayland + GNOME settings.
3. **Build primavera GTK4 app** on Linux with the scaffolded code.
4. **Network bridge**: SSH/waypipe connection from ULTRON Windows → primavera Linux.
5. **Audio environment hooks**: canberra-gtk-play for start/stop/error sounds (Wayfire side).
6. **Live vision integration with primavera**: ULTRON camera frames → primavera as reference image.
