// MainWindow.Telemetry.cs - telemetry sparklines
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
    /* ===================== TELEMETRY ===================== */

    private void StartTelemetryLoop()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            var s = _telemetry.Snapshot();
            MemText.Text = $"MEM  {s.MemMb,6:F0} MB";
            CpuText.Text = $"CPU  {s.CpuPercent,4:F0}%";
            ThreadText.Text = $"THR  {s.ThreadCount,4}";
            CamText.Text = $"UP   {(int)s.Uptime.TotalHours}h {s.Uptime.Minutes}m";

            _spark.Enqueue(Math.Clamp(s.CpuPercent, 0, 100));
            if (_spark.Count > 30) _spark.Dequeue();
            RenderSparkline(_spark.ToArray());
        };
        t.Start();
    }

    private void RenderSparkline(double[] values)
    {
        var n = values.Length;
        if (n < 2) return;
        var max = 100.0;
        var w = 84.0;
        var h = 30.0;
        var points = new Microsoft.UI.Xaml.Media.PointCollection();
        for (var i = 0; i < n; i++)
        {
            var x = (i / (double)(n - 1)) * w;
            var y = h - (values[i] / max) * (h - 4) - 2;
            points.Add(new Windows.Foundation.Point(x, y));
        }
        SparkLine.Points = points;
    }

}
