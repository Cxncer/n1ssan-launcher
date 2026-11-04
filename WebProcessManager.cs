using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace n1ssan_launcher
{
    internal sealed class WebProcessManager
    {
        private const string WebUrl =
            "http://127.0.0.1:5173/obsidian.html";

        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        private Process? _process;

        public async Task EnsureRunningAsync(string projectRoot)
        {
            StartupDiagnostics.Write("[Obsidian Web] Startup requested. Root: " + projectRoot);
            StartupDiagnostics.Write("[Obsidian Web] Application base: " + AppContext.BaseDirectory);

            // Reuse an already-running Vite instance instead of creating
            // another server when DOMINATE is restarted.
            if (await IsHealthyAsync())
            {
                StartupDiagnostics.Write("[Obsidian Web] Existing server is healthy.");
                return;
            }

            // A previous startup attempt may still own the process while
            // Vite is finishing initialization. Wait for it rather than
            // launching a duplicate server.
            if (_process == null || _process.HasExited)
            {
                _process?.Dispose();

                string viteEntry = Path.Combine(
                    projectRoot,
                    "node_modules",
                    "vite",
                    "bin",
                    "vite.js");

                StartupDiagnostics.Write("[Obsidian Web] Vite entry: " + viteEntry);
                StartupDiagnostics.Write("[Obsidian Web] Working directory: " + projectRoot);

                if (!File.Exists(viteEntry))
                {
                    StartupDiagnostics.Write("[Obsidian Web] ERROR: Vite entry file missing.");
                    throw new FileNotFoundException(
                        "Vite is not installed in n1ssan-web-bridge. " +
                        "Run npm install in the project directory.",
                        viteEntry);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    WorkingDirectory = projectRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                startInfo.ArgumentList.Add(viteEntry);
                startInfo.ArgumentList.Add("--host");
                startInfo.ArgumentList.Add("127.0.0.1");
                startInfo.ArgumentList.Add("--port");
                startInfo.ArgumentList.Add("5173");
                startInfo.ArgumentList.Add("--strictPort");

                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;

                StartupDiagnostics.Write("[Obsidian Web] Launching node. Arguments: " + string.Join(" ", startInfo.ArgumentList));
                _process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException(
                        "Could not start n1ssan Obsidian Web.");
                StartupDiagnostics.Write("[Obsidian Web] Process started. PID: " + _process.Id);

                _process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                        StartupDiagnostics.Write("[Obsidian Web stdout] " + e.Data);
                };

                _process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                        StartupDiagnostics.Write("[Obsidian Web stderr] " + e.Data);
                };

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            else
            {
                Debug.WriteLine(
                    "[Obsidian Web] Waiting for the existing Vite process.");
            }

            DateTime deadline =
                DateTime.UtcNow.AddSeconds(20);

            while (DateTime.UtcNow < deadline)
            {
                if (_process == null || _process.HasExited)
                {
                    int exitCode = _process?.ExitCode ?? -1;
                    StartupDiagnostics.Write("[Obsidian Web] Process exited before readiness. Exit code: " + exitCode);

                    throw new InvalidOperationException(
                        $"Obsidian Web exited with code {exitCode}. " +
                        "Check that dependencies are installed and port 5173 is free.");
                }

                if (await IsHealthyAsync())
                {
                    StartupDiagnostics.Write("[Obsidian Web] Ready at " + WebUrl);

                    return;
                }

                await Task.Delay(300);
            }

            StartupDiagnostics.Write("[Obsidian Web] ERROR: Readiness timeout after 20 seconds.");
            throw new TimeoutException(
                "n1ssan Obsidian Web did not become ready within 20 seconds.");
        }

        private static async Task<bool> IsHealthyAsync()
        {
            try
            {
                using var response =
                    await Client.GetAsync(WebUrl);

                if (!response.IsSuccessStatusCode)
                    return false;

                string html =
                    await response.Content.ReadAsStringAsync();

                // Vite injects this client script into transformed HTML.
                // This prevents an unrelated service on port 5173 from
                // being mistaken for the n1ssan development server.
                return html.Contains(
                    "/@vite/client",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Write("[Obsidian Web] Health check failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }
    }
}