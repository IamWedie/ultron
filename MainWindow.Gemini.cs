// MainWindow.Gemini.cs - Gemini events: status, transcript, tool calls, errors
// Part of MainWindow. Fields and composition root live in MainWindow.xaml.cs.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Ultron.Services;
using Ultron.Services.Apps;
using Ultron.Services.Processes;
using Ultron.Services.Tools.Apps;
using Windows.System;
using Windows.UI;
using Microsoft.UI;
using Xaml = Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Buffers;
using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Ultron;

public sealed partial class MainWindow
{
    /* ===================== GEMINI BACKEND ===================== */

    private void OnGeminiStatusChanged(string state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (state)
            {
                case "connected":
                    _geminiMode = true;
                    // Apply the persisted Ultron voice profile on every connect.
                    _ = _gemini.SendVoiceDspAsync(
                        _settings.VoiceDspEnabled,
                        _settings.VoiceDspPitch,
                        _settings.VoiceDspChorus,
                        _settings.VoiceDspBass,
                        _settings.VoiceDspDarken);
                    // Wake gate: active only when the user enrolled a wake
                    // phrase AND the setting is on. If armed, start asleep and
                    // keep the local mic running purely as the "Hey Ultron"
                    // detector; the Python backend mutes itself while asleep.
                    _wakeGateOn = _settings.WakeWordEnabled && _voiceId.WakeEnrolled;
                    _awakeInGemini = !_wakeGateOn;
                    _ = _gemini.SetWakeEnabledAsync(_wakeGateOn);
                    if (_wakeGateOn)
                    {
                        TickerText.Text = "CORE STATUS: ASLEEP — say \"Hey Ultron\"";
                        AddMessage("system", "Wake gate armed — say \"Hey Ultron\" to talk to me.");
                        SetMicVisual(false);
                        StartMonitor();
                        _ = _gemini.SetAwakeAsync(false);
                    }
                    else
                    {
                        TickerText.Text = "CORE STATUS: GEMINI LIVE CONNECTED";
                        AddMessage("system", "Gemini Live API connected — real-time voice active.");
                        StopLocalMic();
                        _ = _gemini.SetAwakeAsync(true);
                    }
                    break;
                case "connecting":
                    TickerText.Text = "CORE STATUS: CONNECTING TO GEMINI...";
                    break;
                case "awake":
                    _awakeInGemini = true;
                    TickerText.Text = "CORE STATUS: LISTENING";
                    SetMicVisual(true);
                    break;
                case "asleep":
                    _awakeInGemini = false;
                    TickerText.Text = "CORE STATUS: ASLEEP — say \"Hey Ultron\"";
                    SetMicVisual(false);
                    break;
                case "listening":
                    TickerText.Text = "CORE STATUS: LISTENING";
                    break;
                case "speaking":
                    TickerText.Text = "CORE STATUS: SPEAKING";
                    break;
                case "disconnected":
                    _geminiMode = false;
                    _awakeInGemini = true;
                    _wakeGateOn = false;
                    SetMicVisual(false);
                    TickerText.Text = "CORE STATUS: GEMINI DISCONNECTED";
                    // Fall back to local STT/TTS pipeline (loads models lazily if skipped).
                    _ = EnsureLocalModels();
                    ResumeLocalMic();
                    break;
            }
        });
    }

    private void StopLocalMic()
    {
        try
        {
            _audio?.Stop();
            _verifyWatch.Stop();
            lock (_verifyLock) _verifyBuffer = null;
        }
        catch (Exception ex) { AppLog.Write("MainWindow", $"StopLocalMic failed: {ex.Message}", AppLog.Level.Warn); }
    }

    private void ResumeLocalMic()
    {
        try
        {
            if (_voiceId.WakeEnrolled)
                StartMonitor();
        }
        catch (Exception ex) { AppLog.Write("MainWindow", $"ResumeLocalMic failed: {ex.Message}", AppLog.Level.Warn); }
    }

    private void OnGeminiTranscript(string role, string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AddMessage(role == "user" ? "user" : "ultron", text);
            if (role == "user")
            {
                _lastInteraction = DateTime.UtcNow;
                _sm.EngageFromText();
            }
            else
            {
                _sm.CommandFinished();
            }
        });
    }

    private void OnGeminiToolCall(ToolCall call)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            Dbg($"Gemini tool call: {call.Name}");
            // The router owns policy, approval and failure translation, so this
            // method only has to hand the call over and report the outcome.
            var result = await _toolRouter.ExecuteAsync(call);
            try
            {
                await _gemini.SendToolResultAsync(call.Id, call.Name, result);
            }
            catch (Exception ex)
            {
                Dbg($"Could not deliver result for {call.Name}: {ex.Message}");
            }
        });
    }

}
