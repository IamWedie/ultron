// MainWindow.Navigation.cs - window controls: minimise, maximise, close
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
    /* ===================== WINDOW CONTROLS ===================== */

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        // Minimize-to-tray: drop out of the taskbar; the tray icon stays alive.
        _appWindow.Hide();
        var wake = HotkeyService.TryParse(_settings.HotkeyWake, out var mods, out var vk)
            ? HotkeyService.ToDisplay(mods, vk)
            : "Win+Alt+L";
        _tray?.ShowBalloon("ULTRON", $"Still running in the tray. {wake} to wake it.");
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            if (p.State == OverlappedPresenterState.Maximized) p.Restore();
            else p.Maximize();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

}
