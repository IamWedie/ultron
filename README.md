# ULTRON

A Windows desktop AI assistant: WinUI 3 frontend, a Python Gemini Live backend
for real-time voice, plus local fallback STT/VAD/TTS when no Gemini key is set.

## Prerequisites

- **Windows 10/11** + .NET 10 SDK.
- **Python 3.11–3.13** with the backend dependencies:
  `google-genai`, `sounddevice`, `numpy`.
  Installed from `%LOCALAPPDATA%\Programs\Python\Python312\python.exe` works;
  ULTRON locates it automatically. The version is embedded in the output
  folder name (`CPYTHON-…`), which the app finds at runtime.
- Gemini Live API key (put it in Settings, or set `GEMINI_API_KEY`).
- Optional: `adb` on `PATH` for phone tools (`phone_addr:port` pairing).

## Model files (local mode only)

When no Gemini key is set, ULTRON downloads Whisper, Silero-VAD and Kokoro
models on first run to `%LOCALAPPDATA%\Ultron\models`:

| File | Source |
| ---- | ------ |
| whisper-encoder/decoder/tokenizer | onnx-community/whisper-tiny.en (HuggingFace) |
| silero-vad.onnx | snakers4/silero-vad (GitHub) |
| kokoro-v1.0.int8.onnx + voices | thewh1teagle/kokoro-onnx (GitHub) |

At startup the app runs non-blocking first-run checks and displays any missing
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

## Voice (optional)

- Wake word ("Hey Ultron"), PTT, voice-ID owner enrollment, and the "Ultron"
  voice processor (pitch/chorus/bass processing of Gemini output) are available
  in Settings. They can be toggled on/off; the DSP is off by default.