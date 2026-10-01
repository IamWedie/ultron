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
                    // Re-apply the call-enabled flag on every connect: a
                    // respawned backend is a fresh process, so it only knows the
                    // spawn-time environment value, not the current toggle.
                    _ = _gemini.SendTelegramCallEnabledAsync(_settings.TelegramCallEnabled);
                    // The backend has no access to config.dat, so re-assert the
                    // privacy switch on every connect: a respawned backend is a
                    // fresh process and would otherwise inject memory by default.
                    _ = _gemini.SetMemoryLoggingAsync(_settings.MemoryLogging);
                    // Apply the persisted Ultron voice profile on every connect.
                    _ = _gemini.SendVoiceDspAsync(
                        _settings.VoiceDspEnabled,
                        _settings.VoiceDspPitch,
                        _settings.VoiceDspChorus,
                        _settings.VoiceDspBass,
                        _settings.VoiceDspDarken);
                    // The wake gate is now a system-prompt instruction inside the
                    // Gemini session, baked in at launch from WakeWordEnabled
                    // (see GeminiBackend.StartAsync). It no longer needs a local
                    // enrolled voice to decide, so there is no "start asleep"
                    // state: the mic opens and the model simply stays quiet
                    // until the user says the wake phrase. If the gate is off,
                    // the model answers speech straight away.
                    //
                    // Two separate things, deliberately not merged:
                    //   _wakeGateOn  - policy. "Answer only after 'Hey Ultron'."
                    //                   Lives in the session's system prompt, so
                    //                   changing it later costs a reconnect.
                    //   _awakeInGemini - runtime. "Is mic audio streaming right
                    //                   now?" Owned by the backend; the UI only
                    //                   remembers what the backend last reported.
                    // The backend opens the mic by default on a fresh session.
                    _wakeGateOn = _settings.WakeWordEnabled;
                    _awakeInGemini = true;
                    if (_wakeGateOn)
                    {
                        TickerText.Text = "CORE STATUS: MIC ON — say \"Hey Ultron\" when ready";
                        AddMessage("system", "Gemini Live connected. Mic is open. Say \"Hey Ultron\" to start, then keep talking as long as you like.");
                    }
                    else
                    {
                        TickerText.Text = "CORE STATUS: GEMINI LIVE CONNECTED";
                        AddMessage("system", "Gemini Live API connected — real-time voice active, no wake phrase needed.");
                    }
                    SetMicVisual(_awakeInGemini);
                    SetMicToggleVisual(_awakeInGemini);
                    // No local capture start here. The C# microphone pipeline is
                    // deleted; the Python backend owns the only audio input.
                    break;
                case "connecting":
                    // The process is up but no Gemini session is attached yet, so
                    // the mic is not streaming even though _awake defaults to true
                    // inside the backend. Grey the toggle so it never claims to be
                    // listening during this window; ToggleAwake also rejects clicks
                    // while we are here.
                    TickerText.Text = "CORE STATUS: CONNECTING TO GEMINI...";
                    SetMicVisual(false);
                    SetMicToggleVisual(false);
                    break;
                case "awake":
                    _awakeInGemini = true;
                    TickerText.Text = "CORE STATUS: LISTENING";
                    SetMicVisual(true);
                    SetMicToggleVisual(true);
                    break;
                case "asleep":
                    // Explicit sleep: the model was told to sleep, the room went
                    // quiet past the idle timeout, or the user flipped the mic off.
                    // The mic is shut, so the wake phrase cannot be heard either —
                    // the honest instruction is to turn the mic back on. Mentioning
                    // "Hey Ultron" here was the confusing part: it tells the user to
                    // speak into a microphone that is closed.
                    _awakeInGemini = false;
                    TickerText.Text = _wakeGateOn
                        ? "CORE STATUS: MIC OFF — turn the mic toggle on, then say \"Hey Ultron\""
                        : "CORE STATUS: MIC OFF — turn the mic toggle on to listen";
                    SetMicVisual(false);
                    SetMicToggleVisual(false);
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
                    SetMicToggleVisual(false);
                    TickerText.Text = "CORE STATUS: GEMINI DISCONNECTED";
                    // No local fallback to switch to: speech is cloud-only. The
                    // backend supervisor reconnects on its own, and typed input
                    // still works in the meantime.
                    AddMessage("system", "Gemini Live disconnected — no local speech fallback exists. Reconnecting; typing still works.");
                    break;
            }
        });
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
