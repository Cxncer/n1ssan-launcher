
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

        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        private Process? _process;

        public string ProjectRoot => ResolveBridgeRoot();

        public async Task EnsureRunningAsync()
        {
            string bridgeRoot = ProjectRoot;
            string dataFile = ResolveDataFile(bridgeRoot);

            if (await IsHealthyAsync())
            {
                EnsureDataFileExists(dataFile);
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

            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Could not start n1ssan Bridge.");

            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    Debug.WriteLine("[Bridge] " + e.Data);
            };

            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    Debug.WriteLine("[Bridge Error] " + e.Data);
            };

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

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
                    EnsureDataFileExists(dataFile);
                    return;
                }

                await Task.Delay(300);
            }

            throw new TimeoutException(
                "n1ssan Bridge did not become healthy within 15 seconds.");
        }

        private static async Task<bool> IsHealthyAsync()
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

        private static void EnsureDataFileExists(string dataFile)
        {
            if (!File.Exists(dataFile))
            {
                throw new FileNotFoundException(
                    "n1ssan Bridge is online, but its data file was not found: " +
                    dataFile,
                    dataFile);
            }

            var info = new FileInfo(dataFile);
            if (info.Length == 0)
            {
                throw new InvalidDataException(
                    "n1ssan Bridge data file is empty: " + dataFile);
            }

            Debug.WriteLine(
                $"[Bridge] Data file verified: {dataFile} ({info.Length} bytes)");
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
