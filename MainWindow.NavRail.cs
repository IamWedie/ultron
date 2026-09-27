// MainWindow.NavRail.cs - left navigation rail
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
    /* ===================== NAV RAIL ===================== */

    private Button? _activeNav;

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (_activeNav is not null)
        {
            _activeNav.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            if (_activeNav.Content is FontIcon prev) prev.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 201, 206, 214));
        }
        _activeNav = btn;
        btn.Background = (Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["UltronCrimsonBrush"];
        if (btn.Content is FontIcon cur) cur.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));

        // Secondary view placeholder text.
        (SecondaryViewTitle.Text, SecondaryViewBody.Text) = btn.Tag switch
        {
            "commandCenter" => ("COMMAND CENTER", "Voice + text interface. Wake word, conversation mode, phone control and approvals all live here."),
            "systemMonitor" => ("SYSTEM MONITOR", "Live process/thread/sensor telemetry charts will render here on GPU (Composition/Win2D)."),
            "automation" => ("AUTOMATION & SCRIPTS", "Phone routines (locate, ring, SMS, screenshot, unlock) and scripted tool actions will be managed here."),
            "directives" => ("LOGS & DIRECTIVES", "Conversation ledger, executed-command log and Ultron's directive/permission rules will land here."),
            _ => ("", ""),
        };

        // Memory panel is its own dedicated central view.
        var isMemory = (btn.Tag as string) == "memory";
        MemoryView.Visibility = isMemory ? Visibility.Visible : Visibility.Collapsed;
        OrbContainer.Visibility = isMemory ? Visibility.Collapsed : Visibility.Visible;
        OrbStateLabel.Visibility = isMemory ? Visibility.Collapsed : Visibility.Visible;
        ConversationList.Visibility = isMemory ? Visibility.Collapsed : Visibility.Visible;
        if (isMemory) RefreshMemory();
    }

}
