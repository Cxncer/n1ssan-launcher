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
        public WelcomeWindow() { InitializeComponent(); }

        private async void WelcomeWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_starting) return;
            _starting = true;

            if (AmbientBackground.Fill is LinearGradientBrush gradient)
            {
                gradient.BeginAnimation(LinearGradientBrush.StartPointProperty,
                    new PointAnimation(new Point(0, 0), new Point(1, 0.65),
                        TimeSpan.FromSeconds(2.8))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });

                gradient.BeginAnimation(LinearGradientBrush.EndPointProperty,
                    new PointAnimation(new Point(1, 1), new Point(0.15, 0.25),
                        TimeSpan.FromSeconds(2.8))
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