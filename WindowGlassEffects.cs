using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace n1ssan_launcher
{
    internal static class WindowGlassEffects
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaSystemBackdropType = 38;
        private const int DwmwaTransientWindowBackdrop = 3;
        private const int DwmwaRoundCorner = 2;
        private const int WcaAccentPolicy = 19;
        private const int AccentEnableAcrylicBlurBehind = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(
            IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int size);

        public static void Apply(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            try
            {
                int darkMode = 1;
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode,
                    ref darkMode, sizeof(int));

                int cornerPreference = DwmwaRoundCorner;
                DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference,
                    ref cornerPreference, sizeof(int));

                int backdrop = DwmwaTransientWindowBackdrop;
                int backdropResult = DwmSetWindowAttribute(hwnd,
                    DwmwaSystemBackdropType, ref backdrop, sizeof(int));

                if (backdropResult != 0)
                    ApplyLegacyAcrylic(hwnd);
            }
            catch
            {
                // Native visual effects are optional and must never block startup.
            }
        }

        private static void ApplyLegacyAcrylic(IntPtr hwnd)
        {
            var accent = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = 2,
                GradientColor = unchecked((int)0xB80F1B12),
                AnimationId = 0
            };

            IntPtr accentPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = accentPtr,
                    SizeOfData = Marshal.SizeOf<AccentPolicy>()
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }
    }
}