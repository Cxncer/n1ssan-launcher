using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

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
            }
            catch
            {
                // Glass decoration is optional; it must never block startup.
            }
        }
    }
}