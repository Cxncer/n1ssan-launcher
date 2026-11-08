using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace n1ssan_launcher
{
    internal static class WindowGlassEffects
    {
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
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int darkMode = 1;
                DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));

                // Windows acrylic backdrop: blurred desktop/application content
                // beneath the dark green tint, with the animated WPF aurora above it.
                // GradientColor uses AABBGGRR; 0xC80F1B12 is a dark green 78% tint.
                var accent = new AccentPolicy
                {
                    AccentState = AccentEnableAcrylicBlurBehind,
                    AccentFlags = 2,
                    GradientColor = unchecked((int)0xC80F1B12),
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

                // Keep the rounded alpha silhouette independent of the backdrop.
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
                // Optional native glass effects must never block launcher startup.
            }
        }
    }
}