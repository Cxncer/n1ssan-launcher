using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Controls;

namespace n1ssan_launcher
{
    public partial class WelcomeWindow : Window
    {
        private bool _starting;
        public WelcomeWindow()
        {
            InitializeComponent();
            string logoPath = Path.Combine(AppContext.BaseDirectory, "assets", "icons", "n1ssan-icon-white.png");
            if (File.Exists(logoPath))
                WelcomeLogo.Source = new BitmapImage(new Uri(logoPath, UriKind.Absolute));
            else
                StartupDiagnostics.Write("[UI] Missing welcome logo asset: " + logoPath);
        }

        private void GlassContent_SizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateRoundedClip();

        private void UpdateRoundedClip()
        {
            if (GlassContent == null || GlassContent.ActualWidth <= 0 || GlassContent.ActualHeight <= 0) return;
            GlassContent.Clip = new RectangleGeometry(new Rect(0, 0, GlassContent.ActualWidth, GlassContent.ActualHeight), 30, 30);
        }

        private void Window_Drag_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left && e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                DragMove();
                e.Handled = true;
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void Close_Click(object sender, RoutedEventArgs e)
            => Close();


        private async void WelcomeWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_starting) return;
            _starting = true;
            InitializingBar.BeginAnimation(FrameworkElement.WidthProperty,
                new DoubleAnimation(0, 42, TimeSpan.FromSeconds(3))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });

            if (AuroraGlow.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(-24, 30, TimeSpan.FromSeconds(8))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                transform.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(-12, 24, TimeSpan.FromSeconds(10))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            }
            await Task.Delay(3000);
            if (!IsVisible) return;
            var dashboard = new MainWindow();
            dashboard.Show();
            Close();
        }
    }
}