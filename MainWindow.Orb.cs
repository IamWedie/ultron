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
    /* ===================== FACE ===================== */

    /// <summary>Open-eye height for the current state. Sleep keeps the lids
    /// nearly shut so "asleep" is readable at a glance; Rest is half-lidded.</summary>
    private double _faceLid = 0.12;

    /// <summary>Builds the brow and cheek geometry in code. WinUI XAML has no
    /// path mini-language for <c>PathFigureCollection</c>, so declaring these
    /// as markup strings fails the XAML compiler.</summary>
    private void BuildFaceGeometry()
    {
        var brow = new PathFigureCollection
        {
            new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(30, 10),
                Segments = new PathSegmentCollection
                {
                    new LineSegment { Point = new Windows.Foundation.Point(54, 2) },
                },
            },
            new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(78, 2),
                Segments = new PathSegmentCollection
                {
                    new LineSegment { Point = new Windows.Foundation.Point(102, 10) },
                },
            },
        };
        FaceBrow.Data = new PathGeometry { Figures = brow, FillRule = Microsoft.UI.Xaml.Media.FillRule.Nonzero };

        // Cheek/jaw lines sweeping down from the outer edges.
        var contour = new PathFigureCollection
        {
            new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(18, 34),
                Segments = new PathSegmentCollection
                {
                    new BezierSegment
                    {
                        Point1 = new Windows.Foundation.Point(6, 74),
                        Point2 = new Windows.Foundation.Point(22, 106),
                        Point3 = new Windows.Foundation.Point(34, 106),
                    },
                    new BezierSegment
                    {
                        Point1 = new Windows.Foundation.Point(66, 124),
                        Point2 = new Windows.Foundation.Point(98, 116),
                        Point3 = new Windows.Foundation.Point(114, 92),
                    },
                },
            },
            new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(114, 34),
                Segments = new PathSegmentCollection
                {
                    new BezierSegment
                    {
                        Point1 = new Windows.Foundation.Point(126, 74),
                        Point2 = new Windows.Foundation.Point(110, 106),
                        Point3 = new Windows.Foundation.Point(98, 106),
                    },
                },
            },
        };
        FaceContour.Data = new PathGeometry
        {
            Figures = contour,
            FillRule = Microsoft.UI.Xaml.Media.FillRule.Nonzero,
        };
    }

    /// <summary>
    /// Drives the face: a slow breathing pulse, plus an occasional blink and a
    /// periodic idle double-blink. Replaces the old continuous 360-degree spin,
    /// which read as generic loading UI rather than a face.
    /// </summary>
    private void StartFaceAnimation()
    {
        BuildFaceGeometry();
        var blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        var tick = 0;
        var phase = 0.0;
        blink.Tick += (_, _) =>
        {
            tick++;
            phase += 0.035;
            var breathe = 1.0 + 0.022 * Math.Sin(phase);
            OrbPulse.ScaleX = breathe;
            OrbPulse.ScaleY = breathe;

            if (tick % 3 != 0) return;

            // Blink pattern: 2 frames closed, 10 open, rare double-blink.
            var cycle = tick / 3 % 260;
            bool lidClosed = cycle < 2 || (cycle is > 18 and < 21) || (cycle > 130 && cycle < 133);
            var lid = lidClosed ? 0.06 : _faceLid;
            EyeLeftScale.ScaleY = lid;
            EyeRightScale.ScaleY = lid;
        };
        blink.Start();
    }

}
