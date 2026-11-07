using System;
using System.Diagnostics;
using System.Threading;
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
        private int _cs2Pid;
        private int _injectedPid;
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

                // Native initialization is deliberately deferred until the user
                // chooses Loadout or Armory. The selected mode must be written before
                // injection because the DLL now refuses to default to either mode.
                Cs2Instance? game = FindCs2();

                if (game == null)
                {
                    StartupDiagnostics.Write("[DOMINATE] CS2 closed before workspace selection.");
                    _startupStateMachine.Reset();
                    return;
                }

                _cs2Pid = game.Pid;
                StartupDiagnostics.Write(
                    $"[DOMINATE] Bridge/Web ready. Waiting for explicit workspace selection before injection. PID={game.Pid}");
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
                        "Starting Obsidian Web at http://127.0.0.1:5173/obsidian.html",
                        false);

                    break;

                case StartupState.WebReady:

                    _bridgeReady = true;
                    UpdateBridgeStatus(true, "Bridge + Web online");
                    UpdateDllStatus(false, "Awaiting native runtime");

                    SetReadiness(
                        "Bridge and Web are ready. Choose Loadout or Armory to select the runtime mode and inject.",
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

        private async void Dominate_Click(
            object sender,
            RoutedEventArgs e)
        {
            SetReadiness("Verifying CS2, Bridge, and Web...", false);
            UpdateCS2Status();
            _bridgeReady = await _bridgeManager.IsHealthyAsync();
            UpdateBridgeStatus(_bridgeReady,
                _bridgeReady ? "Bridge connected" : "Bridge unavailable");

            if (_startupStateMachine.Current != StartupState.WebReady ||
                !_cs2Ready || !_bridgeReady)
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
            StartupDiagnostics.Write("[DOMINATE] CS2, Bridge, and Web are ready. Workspace selector opened; runtime mode will be selected before injection.");
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
                "http://127.0.0.1:5173/loadout");
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

                    await _bridgeManager.EnsureRunningAsync(resetRuntimeMode: false);
                    await _webManager.EnsureRunningAsync(
                        _bridgeManager.ProjectRoot);

                    if (_isClosing)
                        return;

                    if (!IsCS2Running())
                        throw new InvalidOperationException("CS2 closed before runtime mode selection.");

                    string runtimeMode = workspaceName == "Armory" ? "armory" : "loadout";
                    StartupDiagnostics.Write($"[DOMINATE] Selecting runtime mode '{runtimeMode}' before injection.");
                    await _bridgeManager.SelectRuntimeModeAsync(runtimeMode);

                    _startupStateMachine.TryTransition(StartupState.LoaderStarting);

                    Cs2Instance? game = FindCs2();
                    if (game == null)
                        throw new InvalidOperationException("CS2 is no longer running.");

                    InjectorResult injection;
                    if (_injectedPid == game.Pid)
                    {
                        StartupDiagnostics.Write("[DOMINATE] Injector skipped: this PID was already injected in this session.");
                        injection = new InjectorResult(
                            true, string.Empty, 0, false,
                            "RESULT=OK status=already-loaded",
                            string.Empty, string.Empty);
                    }
                    else
                    {
                        StartupDiagnostics.Write("[DOMINATE] Running n1ssan injector after runtime mode selection.");
                        injection = await InjectorRunner.RunAsync(game.Pid, CancellationToken.None);
                        StartupDiagnostics.Write(
                            $"[DOMINATE] Injector exit={injection.ExitCode}, timedOut={injection.TimedOut}, result={injection.ResultLine}");

                        if (!string.IsNullOrWhiteSpace(injection.Output))
                            StartupDiagnostics.Write("[DOMINATE] Injector stdout: " + Compact(injection.Output));
                        if (!string.IsNullOrWhiteSpace(injection.Error))
                            StartupDiagnostics.Write("[DOMINATE] Injector stderr: " + Compact(injection.Error));
                    }

                    if (_isClosing)
                        return;

                    if (!injection.Ok)
                        throw new InvalidOperationException("Injector failed: " + InjectorRunner.Describe(injection));

                    _injectedPid = game.Pid;
                    _cs2Pid = game.Pid;
                    _startupStateMachine.TryTransition(StartupState.DllInitializing);

                    (bool dllReady, string dllMessage, DllStatusSnapshot? dllSnapshot) =
                        await WaitForDllReadyAsync(game.Pid, TimeSpan.FromSeconds(120));

                    if (_isClosing)
                        return;

                    if (!dllReady)
                    {
                        StartupDiagnostics.Write("[DOMINATE] DLL readiness failed: " + dllMessage);
                        _startupStateMachine.TryTransition(StartupState.Failed);
                        throw new InvalidOperationException(
                            "The native runtime did not become ready: " + dllMessage);
                    }

                    _dllReady = true;
                    StartupDiagnostics.Write(
                        "[DOMINATE] DLL is ready: " + dllMessage +
                        (dllSnapshot != null ? $" (build {dllSnapshot.BuildId})" : string.Empty));
                    _startupStateMachine.TryTransition(StartupState.Ready);

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

                    if (_startupStateMachine.Current != StartupState.Ready)
                        _startupStateMachine.TryTransition(StartupState.Failed);

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