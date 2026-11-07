using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace n1ssan_launcher
{
    public partial class WelcomeWindow : Window
    {
        private bool _starting;

        public WelcomeWindow()
        {
            InitializeComponent();
        }

        private async void WelcomeWindow_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            if (_starting)
                return;

            _starting = true;

            // One complete revolution in 3 seconds.
            var rotation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(3),
                RepeatBehavior = new RepeatBehavior(1),
                FillBehavior = FillBehavior.HoldEnd
            };

            ArcRotation.BeginAnimation(
                RotateTransform.AngleProperty,
                rotation);

            await Task.Delay(3000);

            // Do not launch the dashboard if the welcome
            // window was closed during initialization.
            if (!IsVisible)
                return;

            // MainWindow starts its own single monitoring pipeline
            // from its constructor; do not start a second loop here.
            var dashboard = new MainWindow();
            dashboard.Show();

            Close();
        }

        private void Minimize_Click(
            object sender,
            RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Close_Click(
            object sender,
            RoutedEventArgs e)
        {
            Close();
        }
    }
}
