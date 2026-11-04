using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;


namespace n1ssan_launcher
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _statusTimer = new();
        private readonly BridgeProcessManager _bridgeManager = new();
        private readonly WebProcessManager _webManager = new();
        private readonly StartupStateMachine _startupStateMachine = new();
        private readonly System.Threading.SemaphoreSlim _destinationLaunchGate = new(1, 1);

        private bool _startupInProgress;
        private bool _isClosing;
        private bool _cs2Ready;
        private bool _bridgeReady;
        private bool _dllReady;
        private string _lastDllStatusMessage = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
            StartupDiagnostics.Write("========== DOMINATE session started ==========");
            StartupDiagnostics.Write("[DOMINATE] Log file: " + StartupDiagnostics.LogPath);
            StartupDiagnostics.Write("[DOMINATE] Base directory: " + AppContext.BaseDirectory);

            _startupStateMachine.StateChanged += OnStartupStateChanged;

            Background = new SolidColorBrush(
                Color.FromRgb(8, 9, 9));

            _statusTimer.Interval = TimeSpan.FromSeconds(2);

            _statusTimer.Tick += async (_, _) =>
            {
                UpdateCS2Status();
                await MonitorStartupAsync();
                UpdateDllStatusFromSnapshot();
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

        private bool IsCS2Running()
        {
            Process[] processes =
                Process.GetProcessesByName("cs2");

            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }

        private void UpdateCS2Status()
        {
            bool running = IsCS2Running();
            _cs2Ready = running;

            UpdateCs2Status(
                running,
                running ? "CS2 is running" : "Waiting for game");
        }

        // ========================================================
        // AUTOMATIC STARTUP ORCHESTRATION
        // ========================================================

        private async Task MonitorStartupAsync()
        {
            if (_isClosing || _startupInProgress)
                return;

            bool gameRunning = IsCS2Running();

            if (!gameRunning)
            {
                if (_startupStateMachine.Current !=
                    StartupState.WaitingForGame)
                {
                    _startupStateMachine.Reset();
                }

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

                await _webManager.EnsureRunningAsync(
                    _bridgeManager.ProjectRoot);

                // Do not advance if CS2 closed during initialization.
                if (!IsCS2Running())
                {
                    _startupStateMachine.Reset();
                    return;
                }

                _startupStateMachine.TryTransition(
                    StartupState.WebReady);

                StartupDiagnostics.Write("[DOMINATE] Bridge and Obsidian Web are ready.");

                // DLL integration is deliberately not reported as ready.
                // The authorized loader and initialization handshake
                // will be integrated in the next implementation stage.
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

        private void UpdateDllStatusFromSnapshot()
        {
            StartupState state = _startupStateMachine.Current;
            if (state != StartupState.WebReady &&
                state != StartupState.LoaderStarting &&
                state != StartupState.DllInitializing &&
                state != StartupState.Ready)
            {
                return;
            }

            Process[] processes = Process.GetProcessesByName("cs2");
            try
            {
                if (processes.Length == 0)
                    return;

                Process game = processes[0];
                bool found = DllStatusMonitor.TryRead(
                    game.Id,
                    out DllStatusSnapshot? snapshot,
                    out string message,
                    out bool isReady,
                    out bool isFailure);

                _dllReady = found && isReady;
                if (!found && snapshot == null)
                {
                    UpdateDllStatus(false, message);
                }
                else
                {
                    UpdateDllStatus(isReady, message);
                }

                if (!string.Equals(_lastDllStatusMessage, message,
                    StringComparison.Ordinal))
                {
                    _lastDllStatusMessage = message;
                    StartupDiagnostics.Write("[DOMINATE] DLL status: " + message);
                }

                if (isFailure)
                {
                    SetReadiness("DLL initialization failed: " +
                        (snapshot?.Reason ?? "unknown error"), true);
                }
                else if (isReady)
                {
                    SetReadiness(
                        "DLL worker heartbeat confirmed. Loader integration remains separate.",
                        false);
                }
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
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
                        "Starting Obsidian Web at http://127.0.0.1:5173/obsidian.html",
                        false);

                    break;

                case StartupState.WebReady:

                    _bridgeReady = true;
                    UpdateBridgeStatus(true, "Bridge + Web online");
                    UpdateDllStatus(false, "Awaiting integration");

                    SetReadiness(
                        "Obsidian Web ready — http://127.0.0.1:5173/obsidian.html",
                        false);

                    break;

                case StartupState.LoaderStarting:

                    UpdateDllStatus(false, "Preparing...");

                    SetReadiness(
                        "Preparing DLL integration.",
                        false);

                    break;

                case StartupState.DllInitializing:

                    UpdateDllStatus(false, "Initializing...");

                    SetReadiness(
                        "Waiting for DLL initialization.",
                        false);

                    break;

                case StartupState.Ready:

                    UpdateCs2Status(true, "CS2 detected");
                    UpdateBridgeStatus(true, "Bridge connected");
                    UpdateDllStatus(true, "DLL connected");

                    SetReadiness(
                        "n1ssan is connected and ready.",
                        true);

                    break;

                case StartupState.Failed:

                    UpdateBridgeStatus(false, "Initialization failed");
                    UpdateDllStatus(false, "Not connected");

                    SetReadiness(
                        "Startup failed. Check diagnostic output.",
                        true);

                    break;
            }
        }

        // ========================================================
        // STATUS UI HELPERS
        // ========================================================

        private void UpdateCs2Status(
            bool isReady,
            string message)
        {
            SetStatus(
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
                DllStatusCircle,
                DllStatusText,
                isReady,
                message);
        }

        private void SetStatus(
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

            label.Foreground = isReady
                ? new SolidColorBrush(
                    Color.FromRgb(30, 215, 96))
                : new SolidColorBrush(
                    Color.FromRgb(146, 151, 144));
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

        private void Dominate_Click(
            object sender,
            RoutedEventArgs e)
        {
            UpdateCS2Status();
            if (!_cs2Ready || !_bridgeReady || !_dllReady)
            {
                string missing = !_cs2Ready ? "CS2" :
                    !_bridgeReady ? "Bridge" : "DLL READY heartbeat";
                SetReadiness("DOMINATE is locked. Waiting for " + missing + ".", true);
                StartupDiagnostics.Write("[DOMINATE] Workspace selector blocked; prerequisite missing: " + missing);
                return;
            }

            DashboardView.Visibility = Visibility.Collapsed;
            DestinationView.Visibility = Visibility.Visible;
            DominateButton.Visibility = Visibility.Collapsed;
            StartupDiagnostics.Write("[DOMINATE] All three checks are green. Workspace selector opened.");
            SetReadiness("Choose Armory or Loadout.", false);
        }

        private async void Armory_Click(
            object sender,
            RoutedEventArgs e)
        {
            await OpenWorkspaceAsync(
                "Armory",
                "http://127.0.0.1:5173/obsidian.html");
        }

        private async void Loadout_Click(
            object sender,
            RoutedEventArgs e)
        {
            await OpenWorkspaceAsync(
                "Loadout",
                "http://127.0.0.1:5173/?page=loadout");
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
                // Let the automatic startup pipeline finish first so
                // navigation cannot race Bridge or Vite initialization.
                while (_startupInProgress && !_isClosing)
                    await Task.Delay(100);

                if (_isClosing)
                    return;

                _startupInProgress = true;

                try
                {
                    SetReadiness(
                        $"Preparing {workspaceName}...",
                        false);

                    await _bridgeManager.EnsureRunningAsync();
                    await _webManager.EnsureRunningAsync(
                        _bridgeManager.ProjectRoot);

                    if (_isClosing)
                        return;

                    Process? browser = Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });

                    if (browser == null)
                        throw new InvalidOperationException("The default browser could not be started.");
                    browser.Dispose();

                    StartupDiagnostics.Write(
                        $"[DOMINATE] {workspaceName} opened in the default browser: {url}");
                    StartupDiagnostics.Write("[DOMINATE] Closing launcher; browser session now owns Bridge/Web lifetime.");
                    _isClosing = true;
                    _statusTimer.Stop();
                    Close();
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.Write(
                        $"[DOMINATE] Could not open {workspaceName}: {ex}");

                    SetReadiness(
                        $"Could not open {workspaceName}. Check the startup log.",
                        true);
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

        private void Header_MouseLeftButtonDown(
            object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton ==
                System.Windows.Input.MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;
            _statusTimer.Stop();

            base.OnClosed(e);
        }
    }
}