
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace n1ssan_launcher
{
    internal sealed class BridgeProcessManager
    {
        private const string HealthUrl =
            "http://127.0.0.1:3210/health";
        private const string RuntimeModeResetUrl =
            "http://127.0.0.1:3210/runtime-mode/reset";

        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        private Process? _process;

        public string ProjectRoot => ResolveBridgeRoot();

        public async Task EnsureRunningAsync(bool resetRuntimeMode = true)
        {
            string bridgeRoot = ProjectRoot;
            string dataFile = ResolveDataFile(bridgeRoot);

            if (await IsHealthyAsync())
            {
                if (resetRuntimeMode)
                    await ResetRuntimeModeAsync();
                LogDataFileStatus(dataFile);
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "node",
                WorkingDirectory = bridgeRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("--import");
            startInfo.ArgumentList.Add("tsx");
            startInfo.ArgumentList.Add("bridge/server.ts");

            startInfo.RedirectStandardOutput = false;
            startInfo.RedirectStandardError = false;

            StartupDiagnostics.Write(
    "[Bridge] Starting Node. Working directory: " + bridgeRoot);

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Could not start n1ssan Bridge.");

            StartupDiagnostics.Write(
                "[Bridge] Process started. PID: " + _process.Id);

            _process.Exited += (_, _) =>
            {
                try
                {
                    StartupDiagnostics.Write(
                        $"[Bridge] PROCESS EXITED. PID: {_process.Id}, Exit code: {_process.ExitCode}");
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.Write(
                        "[Bridge] Could not read exit details: " + ex.Message);
                }
            };

            _process.EnableRaisingEvents = true;
            DateTime deadline =
                DateTime.UtcNow.AddSeconds(15);

            while (DateTime.UtcNow < deadline)
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Bridge exited with code {_process.ExitCode}.");
                }

                if (await IsHealthyAsync())
                {
                    if (resetRuntimeMode)
                        await ResetRuntimeModeAsync();
                    LogDataFileStatus(dataFile);
                    return;
                }

                await Task.Delay(300);
            }

            throw new TimeoutException(
                "n1ssan Bridge did not become healthy within 15 seconds.");
        }

        public async Task<bool> IsHealthyAsync()
        {
            try
            {
                using var response =
                    await Client.GetAsync(HealthUrl);

                if (!response.IsSuccessStatusCode)
                    return false;

                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());

                return document.RootElement.TryGetProperty(
                    "status", out JsonElement status)
                    && status.GetString() == "online";
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveBridgeRoot()
        {
            string? configured =
                Environment.GetEnvironmentVariable(
                    "N1SSAN_BRIDGE_DIR");

            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (IsValidBridgeRoot(configured))
                    return Path.GetFullPath(configured);

                throw new DirectoryNotFoundException(
                    "N1SSAN_BRIDGE_DIR is invalid: " + configured);
            }

            var current =
                new DirectoryInfo(AppContext.BaseDirectory);

            while (current != null)
            {
                string candidate = Path.Combine(
                    current.FullName,
                    "n1ssan-web-bridge");

                if (IsValidBridgeRoot(candidate))
                    return candidate;

                current = current.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not locate the n1ssan-web-bridge project.");
        }

        private static string ResolveDataFile(string bridgeRoot)
        {
            string? configured =
                Environment.GetEnvironmentVariable("N1SSAN_DATA_FILE");

            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(bridgeRoot, "n1ssan-data.json")
                : Path.GetFullPath(configured);
        }

        private static async Task ResetRuntimeModeAsync()
        {
            using var response = await Client.PostAsync(
                RuntimeModeResetUrl,
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Bridge could not reset the runtime mode (HTTP {(int)response.StatusCode}).");
            }

            Debug.WriteLine("[Bridge] Runtime mode reset to neutral; waiting for workspace selection.");
        }

        public async Task SelectRuntimeModeAsync(string mode)
        {
            if (mode != "loadout" && mode != "armory")
                throw new ArgumentOutOfRangeException(nameof(mode), "Runtime mode must be loadout or armory.");

            using var response = await Client.PostAsync(
                "http://127.0.0.1:3210/runtime-mode",
                new StringContent(
                    JsonSerializer.Serialize(new { mode }),
                    System.Text.Encoding.UTF8,
                    "application/json"));

            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Bridge could not select runtime mode '{mode}' (HTTP {(int)response.StatusCode}): {body}");
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("mode", out JsonElement selected) ||
                !string.Equals(selected.GetString(), mode, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Bridge did not confirm runtime mode '{mode}'. Response: {body}");
            }

            // The Bridge write is only the request. Wait for the injected DLL to
            // acknowledge that it has prepared and activated the selected mode.
            string acknowledgementPath = Path.Combine(ProjectRoot, "runtime-mode-active.json");
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(acknowledgementPath))
                    {
                        using var acknowledgement = JsonDocument.Parse(
                            await File.ReadAllTextAsync(acknowledgementPath));
                        if (acknowledgement.RootElement.TryGetProperty("mode", out JsonElement active) &&
                            string.Equals(active.GetString(), mode, StringComparison.Ordinal))
                        {
                            Debug.WriteLine($"[Bridge] DLL acknowledged active runtime mode: {mode}.");
                            return;
                        }
                    }
                }
                catch (JsonException)
                {
                    // The DLL may be in the middle of replacing the small ack file.
                }

                await Task.Delay(250);
            }

            throw new TimeoutException(
                $"The DLL did not acknowledge activation of runtime mode '{mode}' within 15 seconds.");
        }

        private static void LogDataFileStatus(string dataFile)
        {
            // Loadout data is optional while the user is in Armory mode. DOMINATE
            // must not prevent the Bridge from starting just because no Loadout
            // profile has been projected yet; the native DLL validates the
            // selected runtime mode and its own required data source.
            if (!File.Exists(dataFile))
            {
                Debug.WriteLine(
                    "[Bridge] Loadout data file is not present yet; continuing for Armory mode.");
                return;
            }

            var info = new FileInfo(dataFile);
            Debug.WriteLine(
                $"[Bridge] Loadout data file: {dataFile} ({info.Length} bytes)");
        }

        private static bool IsValidBridgeRoot(string path)
        {
            return
                File.Exists(Path.Combine(path, "package.json")) &&
                File.Exists(Path.Combine(
                    path, "bridge", "server.ts"));
        }
    }
}
