using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;


namespace n1ssan_launcher
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _statusTimer = new();
        private readonly BridgeProcessManager _bridgeManager = new();
        private readonly WebProcessManager _webManager = new();
        private readonly StartupStateMachine _startupStateMachine = new();
        private readonly System.Threading.SemaphoreSlim _destinationLaunchGate = new(1, 1);

        // Add these Interop definitions inside MainWindow class
        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_BLURBEHIND
        {
            public uint dwFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fEnable;
            public IntPtr hRgnBlur;
            [MarshalAs(UnmanagedType.Bool)] public bool fTransitionOnMaximized;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND blurBehind);

        private const uint DWM_BB_ENABLE = 0x00000001;

        // Replace your current Window_SourceInitialized method with this:
        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            IntPtr windowHandle = new WindowInteropHelper(this).Handle;

            DWM_BLURBEHIND blurBehind = new DWM_BLURBEHIND
            {
                dwFlags = DWM_BB_ENABLE,
                fEnable = true,
                hRgnBlur = IntPtr.Zero,
                fTransitionOnMaximized = false
            };

            DwmEnableBlurBehindWindow(windowHandle, ref blurBehind);
            WindowGlassEffects.Apply(this);
        }

        private bool _startupInProgress;
        private bool _isClosing;
        private bool _cs2Ready;
        private bool _bridgeReady;
        private bool _dllReady;
        private int _cs2Pid;
        private int _injectedPid;
        private string _lastDllStatusMessage = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
            LoadBrandAssets();
            UpdateRoundedClip();
            AnimateAurora();
            StartupDiagnostics.Write("========== DOMINATE session started ==========");
            StartupDiagnostics.Write("[DOMINATE] Log file: " + StartupDiagnostics.LogPath);
            StartupDiagnostics.Write("[DOMINATE] Base directory: " + AppContext.BaseDirectory);

            _startupStateMachine.StateChanged += OnStartupStateChanged;

            Background = Brushes.Transparent;

            _statusTimer.Interval = TimeSpan.FromSeconds(2);

            _statusTimer.Tick += async (_, _) =>
            {
                UpdateCS2Status();
                await MonitorStartupAsync();
                UpdateDllStatusFromSnapshot();
                RefreshDominateButton();
            };

            UpdateCS2Status();

            _statusTimer.Start();

            // Allow the welcome screen to finish before startup.
            _ = BeginStartupAfterWelcomeAsync();
        }

        private async Task BeginStartupAfterWelcomeAsync()
        {
            await Task.Delay(2500);

            if (!_isClosing)
                await MonitorStartupAsync();
        }

        // ========================================================
        // GAME DETECTION
        // ========================================================

        private static Cs2Instance? FindCs2()
            => Cs2Process.SelectPreferred();

        private static bool IsCS2Running()
            => FindCs2() != null;

        private void UpdateCS2Status()
        {
            Cs2Instance? game = FindCs2();
            bool running = game != null;

            if (running)
            {
                if (game!.Pid != _cs2Pid)
                {
                    // A new game process invalidates the previous injection.
                    _cs2Pid = game.Pid;
                    _injectedPid = 0;
                }
            }
            else
            {
                _cs2Pid = 0;
            }

            _cs2Ready = running;

            UpdateCs2Status(
                running,
                running ? $"CS2 is running (PID {_cs2Pid})" : "Waiting for game");
        }

        // ========================================================
        // AUTOMATIC STARTUP ORCHESTRATION
        // ========================================================

        private async Task MonitorStartupAsync()
        {
            if (_isClosing || _startupInProgress)
                return;

            bool gameRunning = FindCs2() != null;

            if (!gameRunning)
            {
                if (_startupStateMachine.Current !=
                    StartupState.WaitingForGame)
                {
                    _startupStateMachine.Reset();
                }

                _cs2Pid = 0;
                _injectedPid = 0;
                _dllReady = false;

                return;
            }

            if (_startupStateMachine.Current !=
                StartupState.WaitingForGame)
            {
                return;
            }

            await RunStartupPipelineAsync();
        }

        private async Task RunStartupPipelineAsync()
        {
            if (_startupInProgress || _isClosing)
                return;

            _startupInProgress = true;

            try
            {
                if (!IsCS2Running())
                    return;

                // STEP 1: Game detected.
                _startupStateMachine.TryTransition(
                    StartupState.GameDetected);

                // STEP 2: Initialize Bridge.
                _startupStateMachine.TryTransition(
                    StartupState.BridgeStarting);

                await _bridgeManager.EnsureRunningAsync();

                // Do not advance if CS2 closed during initialization.
                if (!IsCS2Running())
                {
                    _startupStateMachine.Reset();
                    return;
                }

                _startupStateMachine.TryTransition(
                    StartupState.BridgeReady);

                StartupDiagnostics.Write("[DOMINATE] Bridge is healthy. Starting Obsidian Web.");

                // STEP 3: Start the Obsidian Vite development server.
                _startupStateMachine.TryTransition(
                    StartupState.WebStarting);

                if (!_bridgeManager.IsPackagedRelease)
                    await _webManager.EnsureRunningAsync(_bridgeManager.ProjectRoot);

                // Do not advance if CS2 closed during initialization.
                if (!IsCS2Running())
                {
                    _startupStateMachine.Reset();
                    return;
                }

                _startupStateMachine.TryTransition(
                    StartupState.WebReady);

                StartupDiagnostics.Write("[DOMINATE] Bridge and Obsidian Web are ready.");

                // Inject in neutral mode. The DLL must reach READY before the
                // user opens the DOMINATE destination selector.
                Cs2Instance? game = FindCs2();
                if (game == null)
                {
                    StartupDiagnostics.Write("[DOMINATE] CS2 closed before injection.");
                    _startupStateMachine.Reset();
                    return;
                }

                _cs2Pid = game.Pid;
                _startupStateMachine.TryTransition(StartupState.LoaderStarting);

                StartupDiagnostics.Write($"[DOMINATE] Injecting neutral runtime into CS2 PID={game.Pid}.");
                InjectorResult injection = await InjectorRunner.RunAsync(game.Pid, CancellationToken.None);
                StartupDiagnostics.Write(
                    $"[DOMINATE] Injector exit={injection.ExitCode}, timedOut={injection.TimedOut}, result={injection.ResultLine}");
                if (!string.IsNullOrWhiteSpace(injection.Output))
                    StartupDiagnostics.Write("[DOMINATE] Injector stdout: " + Compact(injection.Output));
                if (!string.IsNullOrWhiteSpace(injection.Error))
                    StartupDiagnostics.Write("[DOMINATE] Injector stderr: " + Compact(injection.Error));

                if (!injection.Ok)
                    throw new InvalidOperationException("Injector failed: " + InjectorRunner.Describe(injection));

                _injectedPid = game.Pid;
                _startupStateMachine.TryTransition(StartupState.DllInitializing);

                (bool dllReady, string dllMessage, DllStatusSnapshot? dllSnapshot) =
                    await WaitForDllReadyAsync(game.Pid, TimeSpan.FromSeconds(120));

                if (!dllReady)
                    throw new InvalidOperationException("The native runtime did not become ready: " + dllMessage);

                _dllReady = true;
                StartupDiagnostics.Write(
                    "[DOMINATE] Neutral DLL is ready: " + dllMessage +
                    (dllSnapshot != null ? $" (build {dllSnapshot.BuildId})" : string.Empty));
                _startupStateMachine.TryTransition(StartupState.Ready);
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Write("[DOMINATE] Startup failed: " + ex);

                _startupStateMachine.TryTransition(
                    StartupState.Failed);
            }
            finally
            {
                _startupInProgress = false;
            }
        }

        // ========================================================
        // READ-ONLY DLL STATUS MONITOR
        // ========================================================

        /// <summary>
        /// Waits for the injected DLL to publish READY or DEGRADED while it initializes.
        /// FAILED reasons and stale/missing heartbeats are returned as actionable text.
        /// </summary>
        private async Task<(bool Ready, string Message, DllStatusSnapshot? Snapshot)>
            WaitForDllReadyAsync(int pid, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            string lastMessage = "No status record from the DLL yet.";

            while (DateTime.UtcNow < deadline)
            {
                if (_isClosing)
                    return (false, "Startup cancelled.", null);

                if (!Cs2Process.IsAlive(pid))
                    return (false, "CS2 closed while the DLL was initializing.", null);

                if (DllStatusMonitor.TryRead(
                        pid,
                        out DllStatusSnapshot? snapshot,
                        out string message,
                        out bool isReady,
                        out bool isFailure))
                {
                    lastMessage = message;

                    if (!string.Equals(
                            _lastDllStatusMessage,
                            message,
                            StringComparison.Ordinal))
                    {
                        _lastDllStatusMessage = message;
                        StartupDiagnostics.Write("[DOMINATE] DLL status: " + message);
                    }

                    UpdateDllStatus(isReady, message);

                    if (isFailure)
                    {
                        return (
                            false,
                            "DLL initialization failed: " +
                                (snapshot?.Reason ?? "unknown error"),
                            snapshot);
                    }

                    if (isReady)
                        return (true, message, snapshot);
                }
                else
                {
                    lastMessage = message;
                    UpdateDllStatus(false, message);
                }

                await Task.Delay(1000);
            }

            return (
                false,
                $"No READY heartbeat within {timeout.TotalSeconds:0} seconds. " +
                    $"Last status: {lastMessage}",
                null);
        }

        private static string Compact(string value)
        {
            string single = value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            return single.Length <= 600
                ? single
                : single.Substring(0, 600) + "…";
        }

        private void UpdateDllStatusFromSnapshot()
        {
            StartupState state = _startupStateMachine.Current;
            if (state != StartupState.DllInitializing &&
                state != StartupState.Ready)
            {
                return;
            }

            if (_cs2Pid <= 0)
                return;

            bool found = DllStatusMonitor.TryRead(
                _cs2Pid,
                out DllStatusSnapshot? snapshot,
                out string message,
                out bool isReady,
                out bool isFailure);

            _dllReady = found && isReady;

            UpdateDllStatus(isReady, message);

            if (!string.Equals(
                    _lastDllStatusMessage,
                    message,
                    StringComparison.Ordinal))
            {
                _lastDllStatusMessage = message;
                StartupDiagnostics.Write("[DOMINATE] DLL status: " + message);
            }

            if (isFailure)
            {
                SetReadiness(
                    "DLL initialization failed: " +
                        (snapshot?.Reason ?? "unknown error"),
                    true);
            }
            else if (state == StartupState.Ready)
            {
                SetReadiness(
                    isReady
                        ? "n1ssan is connected and ready."
                        : "The DLL heartbeat is no longer current.",
                    !isReady);
            }
        }

        // ========================================================
        // STARTUP STATE -> UI
        // ========================================================

        private void OnStartupStateChanged(StartupState state)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() =>
                    OnStartupStateChanged(state));

                return;
            }

            switch (state)
            {
                case StartupState.WaitingForGame:

                    _cs2Ready = false;
                    _bridgeReady = false;
                    _dllReady = false;
                    UpdateCs2Status(false, "Waiting for CS2");
                    UpdateBridgeStatus(false, "Waiting for game");
                    UpdateDllStatus(false, "Waiting for connection");

                    SetReadiness(
                        "Waiting for Counter-Strike 2.",
                        false);

                    break;

                case StartupState.GameDetected:

                    UpdateCs2Status(true, "CS2 detected");
                    UpdateBridgeStatus(false, "Preparing...");
                    UpdateDllStatus(false, "Waiting for Bridge");

                    SetReadiness(
                        "CS2 detected. Preparing n1ssan.",
                        false);

                    break;

                case StartupState.BridgeStarting:

                    UpdateBridgeStatus(false, "Initializing...");

                    SetReadiness(
                        "Initializing n1ssan Bridge.",
                        false);

                    break;

                case StartupState.BridgeReady:

                    _bridgeReady = true;
                    UpdateBridgeStatus(true, "Bridge initialized");

                    SetReadiness(
                        "Bridge online. Preparing Obsidian Web.",
                        false);

                    break;

                case StartupState.WebStarting:

                    UpdateBridgeStatus(true, "Bridge online / Web starting");

                    SetReadiness(
                        $"Starting Obsidian Web at {_bridgeManager.ArmoryUrl}",
                        false);

                    break;

                case StartupState.WebReady:

                    _bridgeReady = true;
                    UpdateBridgeStatus(true, "Bridge + Web online");
                    UpdateDllStatus(false, "Preparing neutral runtime");

                    SetReadiness(
                        "Bridge and Web are ready. Preparing native runtime.",
                        false);

                    break;

                case StartupState.LoaderStarting:

                    UpdateDllStatus(false, "Starting injector...");

                    SetReadiness(
                        "Starting n1ssan injector.",
                        false);

                    break;

                case StartupState.DllInitializing:

                    UpdateDllStatus(false, "Initializing...");

                    SetReadiness(
                        "Waiting for the native runtime to report READY.",
                        false);

                    break;

                case StartupState.Ready:

                    UpdateCs2Status(true, "CS2 detected");
                    UpdateBridgeStatus(true, "Bridge connected");
                    UpdateDllStatus(true, "DLL connected");

                    SetReadiness(
                        "DLL ready. Click DOMINATE to choose Loadout or Armory.",
                        false);

                    break;

                case StartupState.Failed:

                    UpdateBridgeStatus(false, "Initialization failed");
                    UpdateDllStatus(false, "Not connected");

                    SetReadiness(
                        "Startup failed. Check diagnostic output.",
                        true);

                    break;
            }

            RefreshDominateButton();
        }

        // ========================================================
        // STATUS UI HELPERS
        // ========================================================

        private void LoadBrandAssets()
        {
            SetImage(N1ssanLogo, "n1ssan-icon-white.png");
            SetImage(Cs2Icon, "cs2-white.png");
            SetImage(BridgeIcon, "connection-white.png");
            SetImage(DllIcon, "dll-white.png");
        }

        private static void SetImage(System.Windows.Controls.Image image, string fileName)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "assets", "icons", fileName);
            if (File.Exists(path))
                image.Source = new BitmapImage(new Uri(path, UriKind.Absolute));
            else
                StartupDiagnostics.Write("[UI] Missing icon asset: " + path);
        }

        private void AnimateAurora()
        {
            if (AuroraGlow.RenderTransform is not TranslateTransform transform) return;
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-24, 35, TimeSpan.FromSeconds(8))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-10, 28, TimeSpan.FromSeconds(10))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }

        private void GlassContent_SizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateRoundedClip();

        private void UpdateRoundedClip()
        {
            if (GlassContent == null || GlassContent.ActualWidth <= 0 || GlassContent.ActualHeight <= 0) return;
            GlassContent.Clip = new RectangleGeometry(new Rect(0, 0, GlassContent.ActualWidth, GlassContent.ActualHeight), 30, 30);
        }

        private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left && e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                DragMove();
                e.Handled = true;
            }
        }

        private void UpdateCs2Status(
            bool isReady,
            string message)
        {
            SetStatus(
                Cs2Icon,
                Cs2StatusCircle,
                Cs2StatusText,
                isReady,
                message);
        }

        private void UpdateBridgeStatus(
            bool isReady,
            string message)
        {
            SetStatus(
                BridgeIcon,
                BridgeStatusCircle,
                BridgeStatusText,
                isReady,
                message);
        }

        private void UpdateDllStatus(
            bool isReady,
            string message)
        {
            SetStatus(
                DllIcon,
                DllStatusCircle,
                DllStatusText,
                isReady,
                message);
        }

        private void SetStatus(
            System.Windows.Controls.Image icon,
            System.Windows.Shapes.Ellipse circle,
            System.Windows.Controls.TextBlock label,
            bool isReady,
            string message)
        {
            circle.Stroke = isReady
                ? new SolidColorBrush(
                    Color.FromRgb(30, 215, 96))
                : new SolidColorBrush(
                    Color.FromRgb(146, 151, 144));

            circle.Fill = isReady
                ? new SolidColorBrush(
                    Color.FromArgb(70, 30, 215, 96))
                : new SolidColorBrush(
                    Color.FromArgb(51, 23, 24, 25));

            label.Text = message;
            icon.Opacity = isReady ? 1.0 : 0.68;

            label.Foreground = isReady
                ? new SolidColorBrush(
                    Color.FromRgb(30, 215, 96))
                : new SolidColorBrush(
                    Color.FromRgb(146, 151, 144));
        }

        private void RefreshDominateButton()
        {
            DominateButton.IsEnabled =
                _startupStateMachine.Current == StartupState.Ready &&
                _cs2Ready && _bridgeReady && _dllReady;
        }

        private void SetReadiness(
            string message,
            bool isError)
        {
            SystemReadinessText.Text = message;

            SystemReadinessText.Foreground = isError
                ? new SolidColorBrush(
                    Color.FromRgb(240, 100, 100))
                : new SolidColorBrush(
                    Color.FromRgb(146, 151, 144));
        }

        // ========================================================
        // DOMINATE
        // ========================================================

        private async void Dominate_Click(
            object sender,
            RoutedEventArgs e)
        {
            SetReadiness("Verifying CS2, Bridge, and Web...", false);
            UpdateCS2Status();
            _bridgeReady = await _bridgeManager.IsHealthyAsync();
            UpdateBridgeStatus(_bridgeReady,
                _bridgeReady ? "Bridge connected" : "Bridge unavailable");

            if (_startupStateMachine.Current != StartupState.Ready ||
                !_cs2Ready || !_bridgeReady || !_dllReady)
            {
                string missing = !_cs2Ready ? "CS2" :
                    !_bridgeReady ? "Bridge" :
                    "Bridge/Web startup";
                SetReadiness("DOMINATE is locked. Waiting for " + missing + ".", true);
                StartupDiagnostics.Write("[DOMINATE] Workspace selector blocked; prerequisite missing: " + missing);
                return;
            }

            DashboardView.Visibility = Visibility.Collapsed;
            DestinationView.Visibility = Visibility.Visible;
            DominateButton.Visibility = Visibility.Collapsed;
            StartupDiagnostics.Write("[DOMINATE] CS2, Bridge, Web, and neutral DLL are ready. Workspace selector opened; mode will be selected in the running DLL.");
            SetReadiness("Choose Armory or Loadout.", false);
        }

        private System.Windows.Controls.Border? GetSpecularHighlight()
            => DominateButton.Template.FindName("SpecularHighlight", DominateButton)
                as System.Windows.Controls.Border;

        private void DominateButton_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (GetSpecularHighlight()?.Background is not RadialGradientBrush brush)
                return;

            Point position = e.GetPosition(DominateButton);
            double x = Math.Clamp(position.X / Math.Max(1, DominateButton.ActualWidth), 0.0, 1.0);
            double y = Math.Clamp(position.Y / Math.Max(1, DominateButton.ActualHeight), 0.0, 1.0);
            Point lightPosition = new(x, y);
            brush.Center = lightPosition;
            brush.GradientOrigin = lightPosition;
        }

        private void DominateButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            GetSpecularHighlight()?.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0.0, 0.82, TimeSpan.FromMilliseconds(180)));
        }

        private void DominateButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            GetSpecularHighlight()?.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0.82, 0.0, TimeSpan.FromMilliseconds(240)));
        }

        private async void Armory_Click(
            object sender,
            RoutedEventArgs e)
        {
            await OpenWorkspaceAsync(
                "Armory",
                _bridgeManager.ArmoryUrl);
        }

        private async void Loadout_Click(
            object sender,
            RoutedEventArgs e)
        {
            await OpenWorkspaceAsync(
                "Loadout",
                _bridgeManager.LoadoutUrl);
        }

        private async Task OpenWorkspaceAsync(
            string workspaceName,
            string url)
        {
            if (_isClosing)
                return;

            await _destinationLaunchGate.WaitAsync();

            try
            {
                while (_startupInProgress && !_isClosing)
                    await Task.Delay(100);

                if (_isClosing)
                    return;

                _startupInProgress = true;

                try
                {
                    SetReadiness($"Selecting {workspaceName} runtime mode...", false);

                    await _bridgeManager.EnsureRunningAsync(resetRuntimeMode: false);
                if (!_bridgeManager.IsPackagedRelease)
                    await _webManager.EnsureRunningAsync(_bridgeManager.ProjectRoot);

                    Cs2Instance? game = FindCs2();
                    if (game == null)
                        throw new InvalidOperationException("CS2 is no longer running.");

                    if (!_dllReady || _injectedPid != game.Pid)
                        throw new InvalidOperationException("The neutral DLL is not ready for the current CS2 process. Restart DOMINATE.");

                    string runtimeMode = workspaceName == "Armory" ? "armory" : "loadout";
                    StartupDiagnostics.Write($"[DOMINATE] Sending runtime mode '{runtimeMode}' to the already-injected DLL.");
                    await _bridgeManager.SelectRuntimeModeAsync(runtimeMode);

                    Process? browser = Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });

                    if (browser == null)
                        throw new InvalidOperationException("The default browser could not be started.");
                    browser.Dispose();

                    StartupDiagnostics.Write($"[DOMINATE] {workspaceName} opened: {url}");
                    StartupDiagnostics.Write("[DOMINATE] Closing launcher; browser session now owns Bridge/Web lifetime.");
                    _isClosing = true;
                    _statusTimer.Stop();
                    Close();
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.Write($"[DOMINATE] Could not open {workspaceName}: {ex}");
                    SetReadiness($"Could not open {workspaceName}. Check the startup log.", true);
                }
                finally
                {
                    _startupInProgress = false;
                }
            }
            finally
            {
                _destinationLaunchGate.Release();
            }
        }

        // ========================================================
        // WINDOW CONTROLS
        // ========================================================

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


        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;
            _statusTimer.Stop();

            base.OnClosed(e);
        }
    }
}