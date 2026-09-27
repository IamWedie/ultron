// MainWindow.QuickActions.cs - quick action chips
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
using QRCoder;

namespace Ultron;

public sealed partial class MainWindow
{
    /* ===================== QUICK ACTIONS ===================== */

    private void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        _lastInteraction = DateTime.UtcNow;
        switch (btn.Tag)
        {
            case "purge":
                _brain.ResetHistory();
                ConversationList.Items.Clear();
                AddMessage("system", "Purge Cache: history cleared.");
                break;
            case "wake":
                if (_voiceId.OwnerEnrolled && _settings.VoiceEnrolled)
                {
                    AddMessage("system", "Wake simulated — verifying owner.");
                    StartWakeVerify();
                }
                else
                {
                    AddMessage("system", "No voice profile yet — opening VOICE ID SETUP.");
                    _ = OpenVoiceSetupAsync();
                }
                break;
            case "lock":
                _sm.ForceSleep();
                AddMessage("system", "System locked — Ultron asleep.");
                break;
            case "focus":
                if (_sm.Current == AssistantState.Engaged) _sm.RestCue();
                else _sm.ForceSleep();
                AddMessage("system", "Focus mode — Ultron resting until next command.");
                break;
            case "settings":
                _ = OpenSettingsAsync();
                break;
            default:
                AddMessage("system", $"Quick action: {btn.Tag} (handler pending).");
                break;
        }
    }

}
