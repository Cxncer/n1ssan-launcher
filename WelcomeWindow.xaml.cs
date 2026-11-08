using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.IO;
using System.Windows.Media.Imaging;

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

        private void Window_SourceInitialized(object? sender, EventArgs e)
            => WindowGlassEffects.Apply(this);

        private async void WelcomeWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_starting) return;
            _starting = true;
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