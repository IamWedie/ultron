// MainWindow.Hotkeys.cs - global hotkeys: press handling and (re)binding
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
    private void OnHotkeyPressed(int id)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (id)
            {
                case HotkeyPtt: TogglePtt(); break;
                case HotkeyMute: ToggleMicMute(); break;
                case HotkeyWake: ToggleAwake(); break;
            }
        });
    }

    /// <summary>(Re)apply the configured global hotkeys. Reports invalid specs and
    /// combos that are already held by another app, keeping prior bindings intact.</summary>
    internal void ApplyHotkeyBindings()
    {
        if (_hotkeys is null) return;
        foreach (var (id, spec) in new[]
                 {
                     (HotkeyPtt, _settings.HotkeyPtt ?? ""),
                     (HotkeyMute, _settings.HotkeyMute ?? ""),
                     (HotkeyWake, _settings.HotkeyWake ?? ""),
                 })
        {
            if (!HotkeyService.TryParse(spec, out var mods, out var vk))
            {
                _hotkeys.Unregister(id);
                Dbg($"hotkey {id}: invalid spec '{spec}' — not registered.");
                continue;
            }
            _hotkeys.Unregister(id);
            if (_hotkeys.Register(id, mods, vk))
                Dbg($"hotkey {id}: registered '{HotkeyService.ToDisplay(mods, vk)}'.");
            else
                Dbg($"hotkey {id}: '{spec}' is already in use by another app — not registered.");
        }
    }

}
