// MainWindow.Orb.cs - orb animation
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
    /* ===================== ORB ===================== */

    private void StartOrbSpin()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        var angle = 0.0;
        t.Tick += (_, _) =>
        {
            angle = (angle + 1.2) % 360;
            OrbSpin.Angle = angle;
        };
        t.Start();
    }

}
