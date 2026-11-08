using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace n1ssan_launcher
{
    internal static class WindowGlassEffects
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void Apply(Window window)
        {
            try
            {
                // Layered WPF windows provide transparency themselves. Do not extend
                // the DWM frame over the client area: doing so caused stale opacity
                // until the window was minimized/restored.
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                int darkMode = 1;
                DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));

                // Apply an anti-aliased rounded alpha mask to the entire window,
                // including child content, so no rectangular pixels leak at corners.
                double width = window.Width;
                double height = window.Height;
                var roundedGeometry = new RectangleGeometry(
                    new Rect(0, 0, width, height), 22, 22);
                window.OpacityMask = new DrawingBrush(
                    new GeometryDrawing(Brushes.White, null, roundedGeometry))
                {
                    Viewbox = new Rect(0, 0, width, height),
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, 1, 1),
                    ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                    Stretch = Stretch.Fill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
            }
            catch
            {
                // Glass decoration is optional; it must never block startup.
            }
        }
    }
}