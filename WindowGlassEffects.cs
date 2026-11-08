using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace n1ssan_launcher
{
    internal static class WindowGlassEffects
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Margins { public int Left; public int Right; public int Top; public int Bottom; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void Apply(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                int darkMode = 1, backdrop = 3;
                DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));
                DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));
                var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
                if (HwndSource.FromHwnd(hwnd) is HwndSource source)
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
            catch { /* The optional system glass effect must never block startup. */ }
        }
    }
}