// MainWindow.CommandInput.cs - typed command input
// Part of MainWindow. Fields and composition root live in MainWindow.xaml.cs.
//
// This file was formerly MainWindow.VoiceId.cs and held the local voice-identity
// stack: AudioCapture microphone capture, cosine-matched owner-passphrase
// verification, and wake-word enrolment. All of that is deleted. Speech
// recognition and synthesis are both cloud-only via Gemini Live, so there is
// nothing left to identify the speaker locally.
//
// SendCommand stays because it is the typed path, and it is the only way to talk
// to the model while the microphone gate is shut.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ultron.Services;
using Windows.System;

namespace Ultron;

public sealed partial class MainWindow
{
    private void CommandInput_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) SendCommand();
    }

    private async void SendCommand()
    {
        var text = CommandInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        CommandInput.Text = "";
        _lastInteraction = DateTime.UtcNow;
        AddMessage("user", text);
        _sm.EngageFromText();

        if (_geminiMode && _gemini.IsConnected)
        {
            // Gemini Live path: send text; the spoken/text reply arrives async
            // via the TranscriptReceived event.
            try { await _gemini.SendTextAsync(text); }
            catch (Exception ex) { AddMessage("system", "Gemini: " + ex.Message); }
            return;
        }

        // No Gemini session. Brain is the local fallback for typed text only;
        // there is no local speech pipeline behind it any more.
        var replyBox = AddMessage("ultron", "");
        try
        {
            var reply = await _brain.AskAsync(text, chunk =>
            {
                if (replyBox.Children[1] is TextBlock tb)
                {
                    DispatcherQueue.TryEnqueue(() => tb.Text += chunk);
                }
            });
            if (replyBox.Children[1] is TextBlock tb2 && string.IsNullOrEmpty(tb2.Text))
            {
                tb2.Text = reply;
            }
            _sm.CommandFinished();
        }
        catch (Exception ex)
        {
            AddMessage("system", ex.Message);
            _sm.CommandFinished();
        }
    }

}